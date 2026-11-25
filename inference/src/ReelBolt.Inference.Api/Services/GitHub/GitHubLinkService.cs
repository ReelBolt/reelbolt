using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Observability;

namespace ReelBolt.Inference.Api.Services.GitHub;

/// <summary>The result of trying to create a link: either a link, or the HTTP refusal to answer with.</summary>
public sealed record GitHubLinkResult(ProjectGitHubLink? Link, int StatusCode, string? Error, bool Changed = true)
{
    public static GitHubLinkResult Ok(ProjectGitHubLink link) => new(link, 200, null);
    public static GitHubLinkResult Unchanged(ProjectGitHubLink link) => new(link, 200, null, Changed: false);
    public static GitHubLinkResult Refused(int statusCode, string error) => new(null, statusCode, error);
}

/// <summary>
/// A planned change to a link: the branch to store (null = the default branch) and the selection to
/// store (null = every file), each with whether it differs from what is stored now.
/// </summary>
public sealed record GitHubLinkChange(bool BranchChanged, string? Branch, bool SelectionChanged, string? IncludePathsJson);

/// <summary>
/// A repository as the caller's own connected GitHub account sees it, or the refusal to answer with:
/// <c>409 github_account_not_connected</c>, <c>404 repository_not_found</c> or <c>424 github_error</c>.
/// </summary>
public sealed record GitHubReachResult(GitHubRepositoryChoice? Repository, int StatusCode, string? Error)
{
    public static GitHubReachResult Ok(GitHubRepositoryChoice repository) => new(repository, 200, null);
    public static GitHubReachResult Refused(int statusCode, string error) => new(null, statusCode, error);
}

/// <summary>
/// Creates and removes repository links. Two things have to be true before a link exists, and this is
/// the only place that decides them:
///
/// <list type="number">
/// <item><b>The caller may edit the project.</b> Checked by the controller through
/// <c>IProjectAccessService</c> before this service is called, so a Member of another organization — or
/// of no organization — never reaches here with a project id.</item>
/// <item><b>The app is really installed on that repository.</b> Proven by asking GitHub, as the app,
/// which installation covers the repository. A caller cannot assert it, and a repository the app cannot
/// see has no installation, so it is refused.</item>
/// </list>
///
/// <para>
/// And one more, which is the cross-tenant one: <b>an installation belongs to exactly one
/// organization</b>. Without that, any user who may edit any project could link a colleague's private
/// repository — GitHub would happily deliver its pushes, because the app is installed on it — and read
/// its contents out of their own project. The first organization to link a repository under an
/// installation claims it; every other organization is answered <b>404</b>, the same non-oracle answer
/// the rest of the tenancy boundary gives, never a 403 that would confirm the repository exists.
/// </para>
/// </summary>
public interface IGitHubLinkService
{
    /// <summary>
    /// Creates the link. <paramref name="includePaths"/> null or empty means the whole repository (or the
    /// whole of <paramref name="pathPrefix"/>); otherwise the repository-relative files and folders to
    /// mirror, validated by <see cref="GitHubIncludePaths.TryNormalize"/>.
    /// </summary>
    Task<GitHubLinkResult> LinkAsync(
        Guid projectId,
        Guid organizationId,
        Guid userId,
        string repository,
        string? branch,
        string? pathPrefix,
        IReadOnlyList<string>? includePaths,
        CancellationToken ct);

    /// <summary>
    /// Changes what an existing link follows: its branch (<paramref name="branch"/> null = unchanged,
    /// empty = the repository's default branch) and its selection (<paramref name="includePaths"/> null =
    /// unchanged, empty = everything). The caller's own connected account must still see the
    /// repository, through the installation the link was made with — the same person check a new link
    /// gets, never the installation alone.
    /// </summary>
    Task<GitHubLinkResult> UpdateAsync(
        ProjectGitHubLink link,
        Guid organizationId,
        Guid userId,
        string? branch,
        IReadOnlyList<string>? includePaths,
        CancellationToken ct);

    /// <summary>
    /// The person check on its own: the repository with GitHub's numeric id
    /// <paramref name="repositoryId"/>, as the caller's own connected account lists it. Every read that
    /// reaches a repository's files on somebody's behalf goes through this first.
    /// </summary>
    Task<GitHubReachResult> ResolveReachableAsync(Guid userId, Guid organizationId, long repositoryId, CancellationToken ct);

    /// <summary>
    /// <see cref="ResolveReachableAsync"/> for an existing link, which additionally requires the account
    /// to reach the repository through the installation the link pulls with.
    /// </summary>
    Task<GitHubReachResult> ResolveLinkedAsync(ProjectGitHubLink link, Guid organizationId, Guid userId, CancellationToken ct);
}

/// <inheritdoc />
public sealed class GitHubLinkService : IGitHubLinkService
{
    /// <summary>
    /// The HTTP status of <c>github_error</c>: <c>424 Failed Dependency</c>, never 502 — Cloudflare replaces the
    /// body of an origin 502 with its own page, so the dashboard never saw the code or the sentence.
    /// </summary>
    public const int FailureStatus = 424;

    private readonly InferenceApiDbContext _db;
    private readonly IGitHubAppClient _gitHub;
    private readonly IGitHubAccountService _accounts;
    private readonly GitHubOptions _options;
    private readonly ILogger<GitHubLinkService> _logger;

    public GitHubLinkService(
        InferenceApiDbContext db,
        IGitHubAppClient gitHub,
        IGitHubAccountService accounts,
        IOptions<GitHubOptions> options,
        ILogger<GitHubLinkService> logger)
    {
        _db = db;
        _gitHub = gitHub;
        _accounts = accounts;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The link with no file selection (the whole repository, or the whole of the prefix).</summary>
    public Task<GitHubLinkResult> LinkAsync(
        Guid projectId,
        Guid organizationId,
        Guid userId,
        string repository,
        string? branch,
        string? pathPrefix,
        CancellationToken ct) =>
        LinkAsync(projectId, organizationId, userId, repository, branch, pathPrefix, includePaths: null, ct);

    public async Task<GitHubLinkResult> LinkAsync(
        Guid projectId,
        Guid organizationId,
        Guid userId,
        string repository,
        string? branch,
        string? pathPrefix,
        IReadOnlyList<string>? includePaths,
        CancellationToken ct)
    {
        if (!_gitHub.IsConfigured)
            return GitHubLinkResult.Refused(503, "github_app_not_configured");

        if (!TryParseRepository(repository, out string owner, out string name))
            return GitHubLinkResult.Refused(400, "invalid_repository");

        // Before anything reaches GitHub: the branch name ends up in a REST path.
        if (!string.IsNullOrWhiteSpace(branch) && !GitHubBranchName.IsValid(branch.Trim()))
            return GitHubLinkResult.Refused(400, "invalid_branch");

        // The person check comes FIRST and is mandatory. An installation token can read everything the
        // app was installed on, so "may the app see this repository?" is far weaker than "may this
        // person?" — and with linking falling back to the installation claim alone, anyone in any
        // workspace could link (and pull) a repository from an installation nobody had claimed yet.
        // The repository must be in the caller's own connected account's list (the picker's set),
        // checked before any app-credentialed call, so nothing about a repository outside that set is
        // ever looked up or revealed.
        (GitHubRepositoryChoice? choice, GitHubLinkResult? refusal) =
            await ReachableRepositoryAsync(userId, organizationId, owner, name, ct);
        if (refusal is not null) return refusal;

        GitHubRepositoryInfo? info = await _gitHub.GetRepositoryAsync(owner, name, ct);
        if (info is null || info.Id != choice!.Id) return GitHubLinkResult.Refused(404, "repository_not_found");

        GitHubInstallationInfo? installation = await _gitHub.GetInstallationForRepositoryAsync(owner, name, ct);
        if (installation is null || installation.Id != choice.InstallationId)
            return GitHubLinkResult.Refused(404, "github_app_not_installed");

        // The installation claim. Another organization already owns this installation, so this
        // repository is not ours to link — answered exactly like "the app is not installed".
        GitHubAppInstallation? claim = await _db.GitHubAppInstallations
            .FirstOrDefaultAsync(i => i.InstallationId == installation.Id, ct);

        if (claim is not null && claim.OrganizationId != organizationId)
        {
            _logger.LogWarning(
                "Refused a link of {Repository} for organization {OrganizationId}: installation {InstallationId} belongs to another organization",
                info.FullName.ForLog(), organizationId, installation.Id);
            return GitHubLinkResult.Refused(404, "github_app_not_installed");
        }

        ProjectGitHubLink? existingForProject = await _db.ProjectGitHubLinks
            .FirstOrDefaultAsync(l => l.ProjectId == projectId, ct);
        if (existingForProject is not null)
            return GitHubLinkResult.Refused(409, "project_already_linked");

        ProjectGitHubLink? existingForRepository = await _db.ProjectGitHubLinks
            .FirstOrDefaultAsync(l => l.RepositoryId == info.Id, ct);
        if (existingForRepository is not null)
        {
            return existingForRepository.OrganizationId == organizationId
                ? GitHubLinkResult.Refused(409, "repository_already_linked")
                : GitHubLinkResult.Refused(404, "repository_not_found");
        }

        string targetBranch = string.IsNullOrWhiteSpace(branch) ? info.DefaultBranch : branch.Trim();
        if (string.IsNullOrWhiteSpace(targetBranch))
            return GitHubLinkResult.Refused(400, "invalid_branch");

        if (!string.IsNullOrWhiteSpace(branch))
        {
            // A typo in a branch name would otherwise be discovered by a push that never arrives.
            try
            {
                await _gitHub.GetBranchHeadAsync(installation.Id, owner, name, targetBranch, ct);
            }
            catch (GitHubApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return GitHubLinkResult.Refused(404, "branch_not_found");
            }
        }

        string? normalizedPrefix = null;
        if (!string.IsNullOrWhiteSpace(pathPrefix))
        {
            try
            {
                normalizedPrefix = ReelBolt.Shared.ProjectFilePath.NormalizeDirectoryPath(pathPrefix);
            }
            catch (Exception)
            {
                return GitHubLinkResult.Refused(400, "invalid_path_prefix");
            }
        }

        if (!TryNormalizeSelection(includePaths, normalizedPrefix, out List<string> selection))
            return GitHubLinkResult.Refused(400, GitHubIncludePaths.InvalidError);

        if (claim is null)
        {
            claim = new GitHubAppInstallation
            {
                Id = Guid.NewGuid(),
                InstallationId = installation.Id,
                OrganizationId = organizationId,
                AccountLogin = installation.AccountLogin,
                RepositorySelection = installation.RepositorySelection,
                ClaimedByUserId = userId,
                ClaimedAt = DateTime.UtcNow
            };
            _db.GitHubAppInstallations.Add(claim);
        }

        DateTime now = DateTime.UtcNow;
        ProjectGitHubLink link = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OrganizationId = organizationId,
            InstallationId = installation.Id,
            RepositoryId = info.Id,
            RepositoryFullName = info.FullName,
            RepositoryDefaultBranch = info.DefaultBranch,
            Branch = string.Equals(targetBranch, info.DefaultBranch, StringComparison.Ordinal) ? null : targetBranch,
            PathPrefix = normalizedPrefix,
            IncludePathsJson = GitHubIncludePaths.Serialize(selection),
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.ProjectGitHubLinks.Add(link);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Linked {Repository} (installation {InstallationId}) to project {ProjectId} in organization {OrganizationId}",
            link.RepositoryFullName.ForLog(), link.InstallationId, projectId, organizationId);

        return GitHubLinkResult.Ok(link);
    }

    public async Task<GitHubLinkResult> UpdateAsync(
        ProjectGitHubLink link,
        Guid organizationId,
        Guid userId,
        string? branch,
        IReadOnlyList<string>? includePaths,
        CancellationToken ct)
    {
        if (!_gitHub.IsConfigured)
            return GitHubLinkResult.Refused(503, "github_app_not_configured");

        // Validated before anything reaches GitHub: a malformed request needs no network to refuse,
        // and one that changes nothing needs no person check, no write and — above all — no sync.
        GitHubLinkChange? change = PlanChange(link, branch, includePaths, out string? invalid);
        if (invalid is not null) return GitHubLinkResult.Refused(400, invalid);
        if (change is null) return GitHubLinkResult.Unchanged(link);

        GitHubReachResult reach = await ResolveLinkedAsync(link, organizationId, userId, ct);
        if (reach.Repository is null) return GitHubLinkResult.Refused(reach.StatusCode, reach.Error!);

        if (change.BranchChanged && change.Branch is { } target)
        {
            (string owner, string name) = GitHubRepositoryPullService.SplitFullName(link.RepositoryFullName);
            try
            {
                await _gitHub.GetBranchHeadAsync(link.InstallationId, owner, name, target, ct);
            }
            catch (GitHubApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return GitHubLinkResult.Refused(404, "branch_not_found");
            }
        }

        if (change.BranchChanged) link.Branch = change.Branch;
        if (change.SelectionChanged) link.IncludePathsJson = change.IncludePathsJson;

        link.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Updated the link of project {ProjectId} to {Repository}: branch {Branch}, selection changed {SelectionChanged}",
            link.ProjectId, link.RepositoryFullName.ForLog(), (link.Branch ?? "(default)").ForLog(), change.SelectionChanged);

        return GitHubLinkResult.Ok(link);
    }

    /// <summary>
    /// What a change request would do to <paramref name="link"/>, without doing it: null when it changes
    /// nothing (the normalized selection and the resolved branch equal what is stored), or
    /// <paramref name="invalid"/> set to the <c>400</c> code when the request is malformed. Used by the
    /// controller before it takes a sync slot, and by <see cref="UpdateAsync"/> itself.
    /// </summary>
    public static GitHubLinkChange? PlanChange(
        ProjectGitHubLink link,
        string? branch,
        IReadOnlyList<string>? includePaths,
        out string? invalid)
    {
        invalid = null;

        bool selectionChanged = false;
        string? includePathsJson = link.IncludePathsJson;
        if (includePaths is not null)
        {
            if (!TryNormalizeSelection(includePaths, link.PathPrefix, out List<string> normalized))
            {
                invalid = GitHubIncludePaths.InvalidError;
                return null;
            }

            includePathsJson = GitHubIncludePaths.Serialize(normalized);
            selectionChanged = !string.Equals(includePathsJson, link.IncludePathsJson, StringComparison.Ordinal);
        }

        bool branchChanged = false;
        string? storedBranch = link.Branch;
        if (branch is not null)
        {
            string trimmed = branch.Trim();
            if (trimmed.Length > 0 && !GitHubBranchName.IsValid(trimmed))
            {
                invalid = "invalid_branch";
                return null;
            }

            string defaultBranch = string.IsNullOrWhiteSpace(link.RepositoryDefaultBranch) ? "main" : link.RepositoryDefaultBranch;
            string target = trimmed.Length == 0 ? defaultBranch : trimmed;
            storedBranch = string.Equals(target, defaultBranch, StringComparison.Ordinal) ? null : target;
            branchChanged = !string.Equals(storedBranch, link.Branch, StringComparison.Ordinal);
        }

        return selectionChanged || branchChanged
            ? new GitHubLinkChange(branchChanged, storedBranch, selectionChanged, includePathsJson)
            : null;
    }

    public async Task<GitHubReachResult> ResolveReachableAsync(Guid userId, Guid organizationId, long repositoryId, CancellationToken ct)
    {
        try
        {
            GitHubAccountView view = await _accounts.GetViewAsync(userId, organizationId, ct);
            if (!view.Connected) return GitHubReachResult.Refused(409, "github_account_not_connected");

            IReadOnlyList<GitHubRepositoryChoice> reachable = await _accounts.ListRepositoriesAsync(userId, organizationId, ct);
            GitHubRepositoryChoice? choice = reachable.FirstOrDefault(r => r.Id == repositoryId);
            return choice is null
                ? GitHubReachResult.Refused(404, "repository_not_found")
                : GitHubReachResult.Ok(choice);
        }
        catch (GitHubAccountUnavailableException)
        {
            return GitHubReachResult.Refused(409, "github_account_not_connected");
        }
        catch (GitHubApiException ex)
        {
            _logger.LogWarning("Could not check a repository against the connected GitHub account: {Message}", ex.Message);
            return GitHubReachResult.Refused(FailureStatus, "github_error");
        }
    }

    public async Task<GitHubReachResult> ResolveLinkedAsync(ProjectGitHubLink link, Guid organizationId, Guid userId, CancellationToken ct)
    {
        GitHubReachResult reach = await ResolveReachableAsync(userId, organizationId, link.RepositoryId, ct);
        if (reach.Repository is null) return reach;

        // Reached, but through a different installation than the one the link pulls with: the person's
        // access does not vouch for the installation this link spends, so it is refused exactly like a
        // repository they cannot see.
        if (reach.Repository.InstallationId != link.InstallationId)
        {
            _logger.LogWarning(
                "Refused access to the link of project {ProjectId}: the caller reaches {Repository} through installation {Reached}, not {Linked}",
                link.ProjectId, link.RepositoryFullName, reach.Repository.InstallationId, link.InstallationId);
            return GitHubReachResult.Refused(404, "repository_not_found");
        }

        return reach;
    }

    /// <summary>
    /// Normalizes a selection and checks it fits the link's legacy path prefix: with a prefix, every
    /// chosen path must lie inside it, since a pulled file is stored relative to the prefix and a path
    /// outside it could never be pulled.
    /// </summary>
    private static bool TryNormalizeSelection(IReadOnlyList<string>? includePaths, string? pathPrefix, out List<string> selection)
    {
        if (!GitHubIncludePaths.TryNormalize(includePaths, out selection, out _)) return false;
        if (string.IsNullOrWhiteSpace(pathPrefix) || selection.Count == 0) return true;

        string prefix = pathPrefix.Trim('/');
        return selection.All(path => GitHubIncludePaths.IsWithin(path, prefix));
    }

    /// <summary>
    /// The repository as the caller's OWN connected GitHub account sees it, or the refusal. No
    /// connection (or one GitHub no longer honours) is <c>409 github_account_not_connected</c> — about
    /// the caller's own account, so it reveals nothing about the repository; a repository outside their
    /// list is the tenancy rules' <c>404</c>.
    /// </summary>
    private async Task<(GitHubRepositoryChoice? Choice, GitHubLinkResult? Refusal)> ReachableRepositoryAsync(
        Guid userId,
        Guid organizationId,
        string owner,
        string name,
        CancellationToken ct)
    {
        try
        {
            GitHubAccountView view = await _accounts.GetViewAsync(userId, organizationId, ct);
            if (!view.Connected)
                return (null, GitHubLinkResult.Refused(409, "github_account_not_connected"));

            IReadOnlyList<GitHubRepositoryChoice> reachable = await _accounts.ListRepositoriesAsync(userId, organizationId, ct);
            GitHubRepositoryChoice? choice = reachable.FirstOrDefault(r =>
                string.Equals(r.FullName, owner + "/" + name, StringComparison.OrdinalIgnoreCase));
            if (choice is not null) return (choice, null);

            _logger.LogWarning(
                "Refused a link of {Owner}/{Name} for user {UserId}: their connected GitHub account cannot use it",
                owner.ForLog(), name.ForLog(), userId);
            return (null, GitHubLinkResult.Refused(404, "repository_not_found"));
        }
        catch (GitHubAccountUnavailableException)
        {
            return (null, GitHubLinkResult.Refused(409, "github_account_not_connected"));
        }
        catch (GitHubApiException ex)
        {
            _logger.LogWarning("Could not check a repository against the connected GitHub account: {Message}", ex.Message);
            return (null, GitHubLinkResult.Refused(FailureStatus, "github_error"));
        }
    }

    /// <summary>
    /// Accepts <c>owner/name</c>, <c>owner/name.git</c>, and any github.com URL for the repository.
    /// Nothing else — a URL on another host is not a GitHub repository, whatever it looks like.
    /// </summary>
    internal static bool TryParseRepository(string? raw, out string owner, out string name)
    {
        owner = string.Empty;
        name = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        string value = raw.Trim();

        if (value.Contains("://", StringComparison.Ordinal) ||
            value.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(value.Contains("://", StringComparison.Ordinal) ? value : "https://" + value, UriKind.Absolute, out Uri? uri))
                return false;

            if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Host, "www.github.com", StringComparison.OrdinalIgnoreCase))
                return false;

            value = uri.AbsolutePath.Trim('/');
        }

        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            value = value[..^4];

        string[] parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;

        owner = parts[0];
        name = parts[1];

        return owner.Length > 0 && name.Length > 0 &&
               !owner.Contains(' ') && !name.Contains(' ') &&
               !owner.Contains("..", StringComparison.Ordinal) && !name.Contains("..", StringComparison.Ordinal);
    }
}
