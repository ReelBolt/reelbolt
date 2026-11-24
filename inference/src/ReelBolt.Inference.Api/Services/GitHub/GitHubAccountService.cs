using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Services.GitHub;

/// <summary>
/// Raised when the caller's GitHub connection cannot be used. <see cref="Reason"/> is a short machine
/// token the controller turns into a status code; the dashboard turns it into a sentence.
/// </summary>
public sealed class GitHubAccountUnavailableException : Exception
{
    public GitHubAccountUnavailableException(string reason) : base(reason) => Reason = reason;

    /// <summary><c>not_connected</c>, <c>revoked</c>, <c>expired</c> or <c>unreadable</c>.</summary>
    public string Reason { get; }

    /// <summary>Reconnecting is the fix for all of these, so all four are a conflict the person can act on.</summary>
    public int StatusCode => 409;
}

/// <summary>
/// Raised when a folder the picker asked for is not a folder. Distinct from a repository that is not
/// reachable at all: both are 404 at the edge, but they are different sentences and only one of them
/// is about the repository the person chose.
/// </summary>
public sealed class GitHubFolderNotFoundException : Exception
{
    public GitHubFolderNotFoundException(string message) : base(message)
    {
    }
}

/// <summary>The connected GitHub account as the dashboard sees it. Carries no token, ever.</summary>
public sealed record GitHubAccountView(
    bool Connected,
    string? Login,
    string? AvatarUrl,
    bool CanConnect,
    string? RevokedReason,
    DateTime? ConnectedAt,
    string? InstallUrl);

/// <summary>
/// One repository the connected person may pick, with the installation that covers it.
/// <see cref="LinkedProjectId"/> is set when this workspace has already linked it — the picker shows it
/// as taken rather than letting the person walk into a <c>409</c>.
/// </summary>
public sealed record GitHubRepositoryChoice(
    long Id,
    string FullName,
    string DefaultBranch,
    bool IsPrivate,
    long InstallationId,
    string AccountLogin,
    string AccountType,
    string? RepositorySelection,
    string? LinkedProjectId);

/// <summary>One entry of one directory. Only directories may be chosen as the pull's folder.</summary>
public sealed record GitHubFolderEntry(string Name, string Path, string Sha, bool IsDirectory);

/// <summary>One directory's children, with the path it was listed for.</summary>
public sealed record GitHubFolderListing(string Path, string? ParentPath, IReadOnlyList<GitHubFolderEntry> Entries, bool Truncated);

/// <summary>
/// The person's GitHub account: reading it, spending its token, revoking it, and the two read-only
/// queries the dashboard's pickers run.
///
/// <para>
/// <b>Nothing here trusts the caller's input for identity.</b> Every method takes the person and the
/// workspace as arguments supplied from the session's own claims, and every repository question is
/// answered by asking GitHub — with the user token — which repositories that person may use. There is
/// no code path in which a repository id, an installation id or an <c>owner/name</c> from a request body
/// becomes an authorization decision.
/// </para>
/// </summary>
public interface IGitHubAccountService
{
    /// <summary>The connection's state, for the "Connect GitHub" control. Null when nothing is stored.</summary>
    Task<GitHubAccountView> GetViewAsync(Guid userId, Guid organizationId, CancellationToken ct);

    /// <summary>Removes the connection at the person's request.</summary>
    Task<bool> DisconnectAsync(Guid userId, Guid organizationId, CancellationToken ct);

    /// <summary>
    /// Marks every connection of one GitHub account as revoked. Called from the
    /// <c>github_app_authorization</c> webhook, which has no tenant and arrives because the person
    /// revoked ReelBolt in their GitHub settings — the one signal GitHub gives.
    /// </summary>
    Task<int> RevokeByGitHubUserIdAsync(long gitHubUserId, CancellationToken ct);

    /// <summary>Every repository the connected person may link, across every installation they can use.</summary>
    Task<IReadOnlyList<GitHubRepositoryChoice>> ListRepositoriesAsync(Guid userId, Guid organizationId, CancellationToken ct);

    /// <summary>One directory of one repository, resolved server-side from the repository's root.</summary>
    Task<GitHubFolderListing> ListFolderAsync(Guid userId, Guid organizationId, long repositoryId, string? path, CancellationToken ct);
}

/// <inheritdoc />
public sealed class GitHubAccountService : IGitHubAccountService
{
    /// <summary>Longest path we will walk from the root. Deeper than any real Remotion project.</summary>
    internal const int MaxPathSegments = 32;

    /// <summary>Most entries one directory listing returns, so a directory with 50k files cannot flood the dashboard.</summary>
    internal const int MaxEntriesPerLevel = 200;

    private readonly InferenceApiDbContext _db;
    private readonly IGitHubAppClient _gitHub;
    private readonly GitHubOptions _options;
    private readonly ISecretProtector _secrets;
    private readonly TimeProvider _clock;
    private readonly ILogger<GitHubAccountService> _logger;

    public GitHubAccountService(
        InferenceApiDbContext db,
        IGitHubAppClient gitHub,
        IOptions<GitHubOptions> options,
        ISecretProtector secrets,
        ILogger<GitHubAccountService> logger,
        TimeProvider? clock = null)
    {
        _db = db;
        _gitHub = gitHub;
        _options = options.Value;
        _secrets = secrets;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<GitHubAccountView> GetViewAsync(Guid userId, Guid organizationId, CancellationToken ct)
    {
        GitHubAccountLink? link = await FindAsync(userId, organizationId, ct);
        string? installUrl = InstallUrl();

        return link is null
            ? new GitHubAccountView(false, null, null, _gitHub.IsUserOAuthConfigured, null, null, installUrl)
            : new GitHubAccountView(
                link.IsActive,
                link.Login,
                link.AvatarUrl,
                _gitHub.IsUserOAuthConfigured,
                link.RevokedReason,
                link.ConnectedAt,
                installUrl);
    }

    public async Task<bool> DisconnectAsync(Guid userId, Guid organizationId, CancellationToken ct)
    {
        GitHubAccountLink? link = await FindAsync(userId, organizationId, ct);
        if (link is null) return false;

        Revoke(link, "disconnected");
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Disconnected the GitHub account of user {UserId} in organization {OrganizationId}", userId, organizationId);
        return true;
    }

    public async Task<int> RevokeByGitHubUserIdAsync(long gitHubUserId, CancellationToken ct)
    {
        List<GitHubAccountLink> links = await _db.GitHubAccountLinks
            .Where(l => l.GitHubUserId == gitHubUserId && l.RevokedAt == null)
            .ToListAsync(ct);

        foreach (GitHubAccountLink link in links) Revoke(link, "user_revoked");
        if (links.Count > 0) await _db.SaveChangesAsync(ct);

        return links.Count;
    }

    public async Task<IReadOnlyList<GitHubRepositoryChoice>> ListRepositoriesAsync(Guid userId, Guid organizationId, CancellationToken ct)
    {
        string token = await GetUserTokenAsync(userId, organizationId, ct);

        IReadOnlyList<GitHubUserInstallation> installations = await _gitHub.ListUserInstallationsAsync(token, ct);

        List<GitHubRepositoryChoice> choices = [];
        foreach (GitHubUserInstallation installation in installations)
        {
            ct.ThrowIfCancellationRequested();
            if (choices.Count >= _options.MaxRepositoriesListed) break;

            IReadOnlyList<GitHubUserRepository> repositories =
                await _gitHub.ListInstallationRepositoriesAsync(token, installation.Id, installation.AccountLogin, ct);

            foreach (GitHubUserRepository repository in repositories)
            {
                choices.Add(new GitHubRepositoryChoice(
                    repository.Id,
                    repository.FullName,
                    repository.DefaultBranch,
                    repository.IsPrivate,
                    installation.Id,
                    installation.AccountLogin,
                    installation.AccountType,
                    installation.RepositorySelection,
                    LinkedProjectId: null));
            }
        }

        return await AnnotateLinksAsync(choices, organizationId, ct);
    }

    /// <summary>
    /// Marks the repositories this workspace already follows, and <b>drops</b> the ones another
    /// workspace follows.
    ///
    /// <para>
    /// The second half is the same non-oracle rule the link endpoint already applies: a repository
    /// linked under a different organization's installation is answered <c>404</c> there, and listing it
    /// here with a reason would say out loud what that <c>404</c> exists to hide. It is simply absent,
    /// which is what "this workspace cannot link it" should look like.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<GitHubRepositoryChoice>> AnnotateLinksAsync(
        List<GitHubRepositoryChoice> choices,
        Guid organizationId,
        CancellationToken ct)
    {
        if (choices.Count == 0) return choices;

        List<long> ids = choices.Select(c => c.Id).ToList();
        List<ProjectGitHubLink> links = await _db.ProjectGitHubLinks
            .Where(l => ids.Contains(l.RepositoryId))
            .ToListAsync(ct);

        Dictionary<long, ProjectGitHubLink> mine = links
            .Where(l => l.OrganizationId == organizationId)
            .ToDictionary(l => l.RepositoryId);

        HashSet<long> foreign = links
            .Where(l => l.OrganizationId != organizationId)
            .Select(l => l.RepositoryId)
            .ToHashSet();

        return choices
            .Where(c => !foreign.Contains(c.Id))
            .Select(c => mine.TryGetValue(c.Id, out ProjectGitHubLink? link)
                ? c with { LinkedProjectId = link.ProjectId.ToString() }
                : c)
            .ToList();
    }

    public async Task<GitHubFolderListing> ListFolderAsync(
        Guid userId,
        Guid organizationId,
        long repositoryId,
        string? path,
        CancellationToken ct)
    {
        string token = await GetUserTokenAsync(userId, organizationId, ct);

        // The repository must be one GitHub says this person may use. Resolved from the repository id
        // rather than from a caller-supplied installation id: an installation id is a capability, and a
        // capability that arrives in a request body is not an authorization.
        GitHubUserRepository? repository = await FindReachableRepositoryAsync(token, repositoryId, ct);
        if (repository is null)
            throw new GitHubApiException($"Repository {repositoryId} is not one this account can use.", System.Net.HttpStatusCode.NotFound);

        (string owner, string repo) = GitHubRepositoryPullService.SplitFullName(repository.FullName);

        string normalizedPath = NormalizeFolderPath(path);
        string[] segments = normalizedPath.Length == 0
            ? []
            : normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length > MaxPathSegments)
            throw new GitHubApiException("That folder path is too deep to browse.", System.Net.HttpStatusCode.BadRequest);

        // Walk from the branch to the requested directory, one level per HTTP request, so the SHA we
        // descend into is always one GitHub returned for this repository. Accepting a SHA from the
        // client would let a caller who may pick from repository A enumerate repository B's tree,
        // because the installation token can read everything the app was installed on.
        string reference = repository.DefaultBranch;
        string resolvedPath = string.Empty;

        foreach (string segment in segments)
        {
            GitHubTreeEntry? child;
            try
            {
                GitHubTreeLevel level = await _gitHub.GetTreeLevelAsync(repository.InstallationId, owner, repo, reference, ct);
                child = level.Entries.FirstOrDefault(e =>
                    string.Equals(e.Type, "tree", StringComparison.Ordinal) &&
                    string.Equals(e.Path, segment, StringComparison.Ordinal));
            }
            catch (GitHubApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // The branch moved between two clicks of the picker, so the subtree we were walking into
                // is gone. That is a stale folder, not a repository that cannot be reached.
                throw new GitHubFolderNotFoundException($"'{resolvedPath}{segment}' is no longer a folder of {repository.FullName}.");
            }

            if (child is null)
                throw new GitHubFolderNotFoundException($"'{resolvedPath}{segment}' is not a folder of {repository.FullName}.");

            reference = child.Sha;
            resolvedPath = resolvedPath.Length == 0 ? segment : resolvedPath + "/" + segment;
        }

        GitHubTreeLevel target;
        try
        {
            target = await _gitHub.GetTreeLevelAsync(repository.InstallationId, owner, repo, reference, ct);
        }
        catch (GitHubApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound && resolvedPath.Length == 0)
        {
            // A repository with no commits has no tree at all, which GitHub answers 404 for. Showing an
            // empty folder is the truth and better than an error: there is genuinely nothing to pull.
            return new GitHubFolderListing(string.Empty, null, [], Truncated: false);
        }

        List<GitHubFolderEntry> entries = target.Entries
            .Where(e => e.Path is { Length: > 0 })
            .OrderByDescending(e => string.Equals(e.Type, "tree", StringComparison.Ordinal))
            .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .Take(MaxEntriesPerLevel)
            .Select(e => new GitHubFolderEntry(
                e.Path,
                resolvedPath.Length == 0 ? e.Path : resolvedPath + "/" + e.Path,
                e.Sha,
                string.Equals(e.Type, "tree", StringComparison.Ordinal)))
            .ToList();

        bool truncated = target.Truncated || target.Entries.Count > MaxEntriesPerLevel;

        return new GitHubFolderListing(
            resolvedPath,
            ParentOf(resolvedPath),
            entries,
            truncated);
    }

    // ------------------------------------------------------------------ tokens

    /// <summary>
    /// The user access token for one (person, workspace), decrypted and — when it has expired and a
    /// refresh token was issued — refreshed first, persisting the replacement so the next call reuses it.
    ///
    /// <para>
    /// This is the only method that returns plaintext, and it is <c>private</c> on purpose: the token
    /// reaches GitHub and nothing else.
    /// </para>
    /// </summary>
    private async Task<string> GetUserTokenAsync(Guid userId, Guid organizationId, CancellationToken ct)
    {
        GitHubAccountLink? link = await FindAsync(userId, organizationId, ct);

        if (link is null) throw new GitHubAccountUnavailableException("not_connected");
        if (!link.IsActive) throw new GitHubAccountUnavailableException("revoked");

        if (!_secrets.TryUnprotect(link.ProtectedAccessToken, out string accessToken) || string.IsNullOrWhiteSpace(accessToken))
        {
            // The key ring cannot read what we wrote (a rotated or missing dpkeys volume). Fail closed:
            // asking the person to reconnect is recoverable, and guessing is not.
            Revoke(link, "decryption_failed");
            await _db.SaveChangesAsync(ct);
            throw new GitHubAccountUnavailableException("unreadable");
        }

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        bool expiring = link.AccessTokenExpiresAt is { } expiry && expiry <= now.AddMinutes(1);
        if (!expiring) return accessToken;

        if (link.ProtectedRefreshToken is null ||
            !_secrets.TryUnprotect(link.ProtectedRefreshToken, out string refreshToken) ||
            string.IsNullOrWhiteSpace(refreshToken) ||
            (link.RefreshTokenExpiresAt is { } refreshExpiry && refreshExpiry <= now))
        {
            Revoke(link, "expired");
            await _db.SaveChangesAsync(ct);
            throw new GitHubAccountUnavailableException("expired");
        }

        GitHubUserTokens? refreshed = await _gitHub.RefreshUserTokenAsync(refreshToken, ct);
        if (refreshed is null)
        {
            // GitHub refused the refresh: the authorization was revoked, or the six-month refresh
            // token lapsed. Either way the person has to connect again, so say so now rather than
            // issuing a request GitHub will answer 401.
            Revoke(link, "invalid_grant");
            await _db.SaveChangesAsync(ct);
            throw new GitHubAccountUnavailableException("revoked");
        }

        link.ProtectedAccessToken = _secrets.Protect(refreshed.AccessToken);
        link.ProtectedRefreshToken = refreshed.RefreshToken is { Length: > 0 } next ? _secrets.Protect(next) : null;
        link.AccessTokenExpiresAt = refreshed.AccessTokenExpiresAt;
        link.RefreshTokenExpiresAt = refreshed.RefreshTokenExpiresAt;
        link.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        return refreshed.AccessToken;
    }

    /// <summary>
    /// Overwrites the stored credential and marks the row revoked. Overwriting rather than leaving the
    /// ciphertext behind is the point: "revoked" has to mean the credential is gone, not merely ignored.
    /// </summary>
    private static void Revoke(GitHubAccountLink link, string reason)
    {
        link.RevokedAt = DateTime.UtcNow;
        link.RevokedReason = reason;
        link.ProtectedAccessToken = string.Empty;
        link.ProtectedRefreshToken = null;
        link.AccessTokenExpiresAt = null;
        link.RefreshTokenExpiresAt = null;
        link.UpdatedAt = DateTime.UtcNow;
    }

    private Task<GitHubAccountLink?> FindAsync(Guid userId, Guid organizationId, CancellationToken ct) =>
        _db.GitHubAccountLinks.FirstOrDefaultAsync(l => l.UserId == userId && l.OrganizationId == organizationId, ct);

    private async Task<GitHubUserRepository?> FindReachableRepositoryAsync(string token, long repositoryId, CancellationToken ct)
    {
        IReadOnlyList<GitHubUserInstallation> installations = await _gitHub.ListUserInstallationsAsync(token, ct);

        foreach (GitHubUserInstallation installation in installations)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<GitHubUserRepository> repositories =
                await _gitHub.ListInstallationRepositoriesAsync(token, installation.Id, installation.AccountLogin, ct);

            GitHubUserRepository? match = repositories.FirstOrDefault(r => r.Id == repositoryId);
            if (match is not null) return match;
        }

        return null;
    }

    /// <summary>The App's install page, and where an account's own installation can be edited.</summary>
    private string? InstallUrl() =>
        string.IsNullOrWhiteSpace(_options.AppSlug) ? null : $"https://github.com/apps/{_options.AppSlug}/installations/new";

    /// <summary>
    /// A repository-relative folder path, or "" for the root. Deliberately stricter than the link's own
    /// <c>PathPrefix</c> normalization: the picker reports the exact names GitHub returned, so nothing
    /// here should be silently rewritten.
    /// </summary>
    internal static string NormalizeFolderPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string value = raw.Replace('\\', '/').Trim().Trim('/');
        if (value.Length == 0) return string.Empty;

        string[] segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s is "." or ".."))
            throw new GitHubApiException("That folder path is not valid.", System.Net.HttpStatusCode.BadRequest);

        return string.Join('/', segments);
    }

    private static string? ParentOf(string path)
    {
        if (path.Length == 0) return null;
        int slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }
}
