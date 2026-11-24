using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.GitHub;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The repository link for one project: what the dashboard's "connect a repository" control talks to.
///
/// <para>
/// Every action here goes through <see cref="IProjectAccessService"/> first, with
/// <see cref="ProjectPermission.View"/> for a read and <see cref="ProjectPermission.Edit"/> for
/// everything that changes the link. That is the authorization half of the feature: linking a
/// repository decides where a customer's pushes land, so it needs the same permission as editing the
/// project, and a project in another organization is <b>404</b> — never 403 — exactly like every other
/// project route. The other half is <see cref="IGitHubLinkService"/>, which proves with GitHub that the
/// app is actually installed on the repository.
/// </para>
///
/// <para>
/// A project has at most one link. Unlinking removes the link and nothing else: the files a previous
/// pull wrote stay in the project, because silently deleting a customer's source tree because they
/// detached a repository would be the more surprising of the two behaviours. Re-linking replaces the
/// link and the next pull reconciles the file set against the new repository.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/github")]
[Authorize]
public class ProjectGitHubController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly IGitHubLinkService _links;
    private readonly GitHubSyncQueue _syncQueue;
    private readonly IGitHubAccountService _accounts;
    private readonly IGitHubAppClient _gitHub;
    private readonly ICurrentUser _currentUser;
    private readonly GitHubOptions _options;
    private readonly GitHubSyncThrottle _throttle;
    private readonly ILogger<ProjectGitHubController> _logger;
    private readonly Services.Telemetry.IProductEventEmitter? _productEvents;

    /// <summary>
    /// The status of a GitHub failure (<c>github_error</c>): <c>424 Failed Dependency</c>, never 502.
    /// Cloudflare, in front of every cloud deployment, replaces the body of an origin 502 with its own HTML
    /// page, so the dashboard saw an opaque "Bad Gateway" instead of the code and the sentence. The dashboard
    /// branches on <c>error</c>, never on the status; the operator detail stays in the log.
    /// </summary>
    internal const int GitHubFailureStatus = GitHubLinkService.FailureStatus;

    /// <summary>The plain sentence of a <c>github_error</c> whose detail is only fit for the log.</summary>
    internal const string GitHubFailureMessage = "GitHub did not answer as expected. Wait a moment and try again.";

    public ProjectGitHubController(
        InferenceApiDbContext db,
        IProjectAccessService projectAccess,
        IGitHubLinkService links,
        GitHubSyncQueue syncQueue,
        IGitHubAccountService accounts,
        IGitHubAppClient gitHub,
        ICurrentUser currentUser,
        IOptions<GitHubOptions> options,
        GitHubSyncThrottle throttle,
        ILogger<ProjectGitHubController> logger,
        Services.Telemetry.IProductEventEmitter? productEvents = null)
    {
        _productEvents = productEvents;
        _throttle = throttle;
        _db = db;
        _projectAccess = projectAccess;
        _links = links;
        _syncQueue = syncQueue;
        _accounts = accounts;
        _gitHub = gitHub;
        _currentUser = currentUser;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The project's linked repository, or 404 when it has none.</summary>
    [HttpGet]
    public async Task<ActionResult<ProjectGitHubLinkResponse>> Get(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        ProjectGitHubLink? link = await _db.ProjectGitHubLinks.FirstOrDefaultAsync(l => l.ProjectId == projectId, ct);
        if (link is null) return NotFound(new { error = "not_linked" });

        return Ok(ToResponse(link));
    }

    /// <summary>
    /// Links a repository (<c>owner/name</c> or its github.com URL) to this project, then queues its
    /// first sync so the chosen files arrive straight away. The repository must be one the caller's own
    /// connected GitHub account can use and the GitHub App is installed on, which is what makes the link
    /// trustworthy rather than merely well-formed.
    ///
    /// <para>
    /// The first sync is never part of the answer: the link is saved and answered <c>200</c> with
    /// <c>syncing: true</c>, and <see cref="GitHubSyncWorker"/> pulls in the background. A sync that
    /// fails (too many files chosen, GitHub unavailable) shows up later as <c>lastError</c> on the link,
    /// so the dashboard shows the linked repository with the reason, rather than a failed link that in
    /// fact exists.
    /// </para>
    /// </summary>
    [HttpPut]
    public async Task<ActionResult<ProjectGitHubLinkResponse>> Link(
        Guid projectId,
        [FromBody] LinkGitHubRepositoryRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        GitHubLinkResult result;
        try
        {
            result = await _links.LinkAsync(
                projectId,
                project.OrganizationId,
                _currentUser.UserId,
                request.Repository,
                request.Branch,
                request.PathPrefix,
                request.IncludePaths,
                ct);
        }
        catch (GitHubApiException ex)
        {
            // Previously unhandled, so any GitHub refusal surfaced as a bare 500.
            _logger.LogWarning("Linking a GitHub repository failed: {Message}", ex.Message);
            return StatusCode(GitHubFailureStatus, new { error = "github_error", message = GitHubFailureMessage });
        }

        if (result.Link is null)
            return StatusCode(result.StatusCode, new { error = result.Error });

        // The first sync of a new link: the concurrency rule only, no cooldown. When somebody is already
        // syncing this project (a second tab), that sync brings the files.
        GitHubSyncThrottle.Lease? lease = _throttle.TryBegin(result.Link.ProjectId, enforceCooldown: false, out _);
        if (lease is not null) _syncQueue.Enqueue(result.Link.ProjectId, force: false, lease);

        // product-analytics: the person connected a source (activation.source_connected, once per user).
        // Here, on their own action, and not in the sync: a background pull is nobody's step. Best effort.
        if (_productEvents is not null)
        {
            await _productEvents.FirstAsync(
                _currentUser.UserId, project.OrganizationId, Services.Telemetry.ProductEventNames.SourceConnected, ct);
        }

        return Ok(ToResponse(result.Link));
    }

    /// <summary>
    /// Changes what the link follows — its branch, its chosen files, or both — and queues a re-sync at
    /// once, so files that were dropped from the choice leave the project and new ones arrive. Same
    /// person check as linking: the caller's own connected GitHub account must still see the repository.
    /// A change that changes nothing queues no sync.
    /// </summary>
    [HttpPatch]
    public async Task<ActionResult<ProjectGitHubLinkResponse>> Update(
        Guid projectId,
        [FromBody] UpdateGitHubLinkRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        ProjectGitHubLink? link = await _db.ProjectGitHubLinks.FirstOrDefaultAsync(l => l.ProjectId == projectId, ct);
        if (link is null) return NotFound(new { error = "not_linked" });

        // A malformed request is refused, and one that changes nothing is answered with the link as it
        // is — before a sync slot is taken, so re-sending the same selection can never force a sync.
        if (GitHubLinkService.PlanChange(link, request.Branch, request.IncludePaths, out string? invalid) is null)
        {
            return invalid is not null
                ? BadRequest(new { error = invalid })
                : Ok(ToResponse(link));
        }

        // The same cooldown "Sync now" has: every accepted change forces a full re-sync, so without it
        // a loop of alternating selections would be an unthrottled way to make the server pull.
        GitHubSyncThrottle.Lease? lease = _throttle.TryBegin(projectId, enforceCooldown: true, out TimeSpan retryAfter);
        if (lease is null) return TooSoon("sync_too_soon", retryAfter);

        // The lease goes to the queue when a sync is queued; on every other way out it is released here.
        bool queued = false;
        try
        {
            GitHubLinkResult result;
            try
            {
                result = await _links.UpdateAsync(link, access.Project!.OrganizationId, _currentUser.UserId, request.Branch, request.IncludePaths, ct);
            }
            catch (GitHubApiException ex)
            {
                lease.NoSyncRan();
                _logger.LogWarning("Changing a GitHub link failed: {Message}", ex.Message);
                return StatusCode(GitHubFailureStatus, new { error = "github_error", message = GitHubFailureMessage });
            }

            if (result.Link is null || !result.Changed)
            {
                // Refused (a missing branch, a lost connection) or nothing to do: no sync ran, so the
                // cooldown is withdrawn and the corrected request can follow at once.
                lease.NoSyncRan();
                return result.Link is null
                    ? StatusCode(result.StatusCode, new { error = result.Error })
                    : Ok(ToResponse(result.Link));
            }

            // Forced: the commit may be the same, but the set of files is not.
            _syncQueue.Enqueue(projectId, force: true, lease);
            queued = true;
            return Ok(ToResponse(result.Link));
        }
        finally
        {
            if (!queued) lease.Dispose();
        }
    }

    /// <summary>
    /// Every file and folder of a repository's branch, for the file selection tree. With
    /// <paramref name="repositoryId"/> it is a repository from the picker; without it, the repository
    /// already linked to this project (and <paramref name="branch"/> defaults to the linked branch).
    ///
    /// <para>
    /// Either way the caller's own connected GitHub account must list the repository first — never the
    /// installation alone — and the commit is resolved from the branch name on the server, never taken
    /// from the request.
    /// </para>
    /// </summary>
    [HttpGet("files")]
    public async Task<ActionResult<GitHubFileListResponse>> Files(
        Guid projectId,
        [FromQuery] long? repositoryId,
        [FromQuery] string? branch,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        // The branch name ends up in a GitHub REST path; refuse anything git would not accept, before
        // anything at all is asked of GitHub.
        if (!string.IsNullOrWhiteSpace(branch) && !GitHubBranchName.IsValid(branch.Trim()))
            return BadRequest(new { error = "invalid_branch" });

        (GitHubRepositoryChoice? repository, string? linkedBranch, ActionResult? refusal) =
            await ResolveForBrowsingAsync(projectId, access.Project!.OrganizationId, repositoryId, ct);
        if (refusal is not null) return refusal;

        try
        {
            GitHubFileListing listing = await GitHubFileTree.ListAsync(
                _gitHub,
                repository!.InstallationId,
                repository.FullName,
                repository.DefaultBranch,
                string.IsNullOrWhiteSpace(branch) ? linkedBranch : branch,
                ct);

            return Ok(new GitHubFileListResponse(
                listing.Repository,
                listing.Branch,
                listing.CommitSha,
                listing.Entries.Select(e => new GitHubFileOption(e.Path, e.IsDirectory, e.Size)).ToList(),
                listing.Truncated,
                _options.MaxFilesPerPull,
                _options.MaxTotalBytes,
                _options.MaxFileBytes));
        }
        catch (GitHubBranchNotFoundException)
        {
            return NotFound(new { error = "branch_not_found" });
        }
        catch (GitHubApiException ex)
        {
            return BrowsingFailure(ex);
        }
    }

    /// <summary>The branches of a repository (from the picker, or the linked one), default branch first.</summary>
    [HttpGet("branches")]
    public async Task<ActionResult<GitHubBranchListResponse>> Branches(
        Guid projectId,
        [FromQuery] long? repositoryId,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        (GitHubRepositoryChoice? repository, _, ActionResult? refusal) =
            await ResolveForBrowsingAsync(projectId, access.Project!.OrganizationId, repositoryId, ct);
        if (refusal is not null) return refusal;

        try
        {
            GitHubBranchList list = await GitHubFileTree.BranchesAsync(
                _gitHub, repository!.InstallationId, repository.FullName, repository.DefaultBranch, ct);
            return Ok(new GitHubBranchListResponse(repository.DefaultBranch, list.Names, list.Truncated));
        }
        catch (GitHubApiException ex)
        {
            return BrowsingFailure(ex);
        }
    }

    /// <summary>
    /// The repository a browse request is about, after the person check, plus the linked branch when it
    /// is the linked repository. The refusals are the same as the picker's.
    /// </summary>
    private async Task<(GitHubRepositoryChoice? Repository, string? LinkedBranch, ActionResult? Refusal)> ResolveForBrowsingAsync(
        Guid projectId,
        Guid organizationId,
        long? repositoryId,
        CancellationToken ct)
    {
        if (!_gitHub.IsUserOAuthConfigured)
            return (null, null, StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "github_oauth_not_configured" }));
        if (!_gitHub.IsConfigured)
            return (null, null, StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "github_app_not_configured" }));

        GitHubReachResult reach;
        string? linkedBranch = null;
        if (repositoryId is { } id)
        {
            reach = await _links.ResolveReachableAsync(_currentUser.UserId, organizationId, id, ct);
        }
        else
        {
            ProjectGitHubLink? link = await _db.ProjectGitHubLinks.FirstOrDefaultAsync(l => l.ProjectId == projectId, ct);
            if (link is null) return (null, null, NotFound(new { error = "not_linked" }));
            reach = await _links.ResolveLinkedAsync(link, organizationId, _currentUser.UserId, ct);
            linkedBranch = link.Branch;
        }

        return reach.Repository is null
            ? (null, null, StatusCode(reach.StatusCode, new { error = reach.Error }))
            : (reach.Repository, linkedBranch, null);
    }

    private ActionResult BrowsingFailure(GitHubApiException ex)
    {
        // A repository the person cannot use is answered exactly like one that does not exist.
        if (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            return NotFound(new { error = "repository_not_found" });
        _logger.LogWarning("Browsing a GitHub repository failed: {Message}", ex.Message);
        return ex.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
            ? StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "github_app_not_configured" })
            : StatusCode(GitHubFailureStatus, new { error = "github_error", message = GitHubFailureMessage });
    }

    /// <summary>
    /// The repositories the caller may link to this project, for the picker.
    ///
    /// <para>
    /// The list is GitHub's own answer, through <i>this person's</i> user access token: the
    /// installations they can use, and the repositories under each. It therefore cannot contain a
    /// repository the person cannot access — not by a filter applied here, but because that token is
    /// the intersection of the app's permissions and their own, and GitHub is what enumerates it. A
    /// repository another workspace has already linked is absent rather than annotated, for the same
    /// non-oracle reason the link endpoint answers <c>404</c> there.
    /// </para>
    ///
    /// <para>
    /// <c>409 github_account_not_connected</c> when the caller has not connected GitHub in this
    /// workspace, <c>503 github_oauth_not_configured</c> when the deployment cannot offer the flow at
    /// all.
    /// </para>
    /// </summary>
    [HttpGet("repositories")]
    public async Task<ActionResult<GitHubRepositoryListResponse>> Repositories(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        if (!_gitHub.IsUserOAuthConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "github_oauth_not_configured" });

        IReadOnlyList<GitHubRepositoryChoice> choices;
        try
        {
            choices = await _accounts.ListRepositoriesAsync(_currentUser.UserId, access.Project!.OrganizationId, ct);
        }
        catch (GitHubAccountUnavailableException ex)
        {
            return StatusCode(ex.StatusCode, new { error = $"github_account_{ex.Reason}" });
        }
        catch (GitHubApiException ex)
        {
            _logger.LogWarning("Listing GitHub repositories failed: {Message}", ex.Message);
            return StatusCode(GitHubFailureStatus, new { error = "github_error", message = GitHubFailureMessage });
        }

        List<GitHubRepositoryOption> options = choices.Select(c => new GitHubRepositoryOption(
            c.Id,
            c.FullName,
            c.DefaultBranch,
            c.IsPrivate,
            c.InstallationId,
            c.AccountLogin,
            c.AccountType,
            c.RepositorySelection,
            c.LinkedProjectId,
            ManageInstallationUrl(c.AccountType, c.AccountLogin, c.InstallationId))).ToList();

        return Ok(new GitHubRepositoryListResponse(
            options,
            Truncated: choices.Count >= _options.MaxRepositoriesListed,
            InstallUrl: InstallUrl(),
            AccountLogin: options.Select(o => o.AccountLogin).Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault()));
    }

    /// <summary>
    /// One directory of one repository, for the folder picker. <paramref name="path"/> absent or empty
    /// means the repository root, which is also the "pull the whole repository" choice.
    ///
    /// <para>
    /// The repository is named by GitHub's numeric id and is resolved against the caller's own
    /// installations before anything is read, and the directory is resolved by walking down from the
    /// branch head — never by taking a tree SHA from the request. Both matter: the installation token
    /// can read every repository the app was installed on, so a caller-supplied tree SHA would let
    /// somebody who may choose from repository A enumerate repository B's file names.
    /// </para>
    /// </summary>
    [HttpGet("tree")]
    public async Task<ActionResult<GitHubFolderListResponse>> Tree(
        Guid projectId,
        [FromQuery] long repositoryId,
        [FromQuery] string? path,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        if (!_gitHub.IsUserOAuthConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "github_oauth_not_configured" });

        // Browsing reads the tree with an INSTALLATION token, which needs the App id and private key —
        // a different credential set from the OAuth client that lists the repositories. A deployment with
        // only the OAuth half configured used to answer 502 here, blaming GitHub for a local config gap.
        if (!_gitHub.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "github_app_not_configured" });

        GitHubFolderListing listing;
        try
        {
            listing = await _accounts.ListFolderAsync(
                _currentUser.UserId,
                access.Project!.OrganizationId,
                repositoryId,
                path,
                ct);
        }
        catch (GitHubAccountUnavailableException ex)
        {
            return StatusCode(ex.StatusCode, new { error = $"github_account_{ex.Reason}" });
        }
        catch (GitHubFolderNotFoundException)
        {
            return NotFound(new { error = "folder_not_found" });
        }
        catch (GitHubApiException ex)
        {
            // A repository the person cannot use is answered exactly like one that does not exist, so
            // the endpoint is not an existence oracle for somebody else's repository.
            if (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                return NotFound(new { error = "repository_not_found" });
            _logger.LogWarning("Browsing a GitHub repository failed: {Message}", ex.Message);
            return ex.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                ? StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "github_app_not_configured" })
                : StatusCode(GitHubFailureStatus, new { error = "github_error", message = ex.Message });
        }

        return Ok(new GitHubFolderListResponse(
            listing.Path,
            listing.ParentPath,
            listing.Entries.Select(e => new GitHubFolderOption(e.Name, e.Path, e.Sha, e.IsDirectory)).ToList(),
            listing.Truncated));
    }

    /// <summary>
    /// GitHub's page for an installation. This is where a repository is added when the app was granted
    /// only selected repositories — the case the picker cannot list, because the person's token cannot
    /// see a repository the app was never given.
    /// </summary>
    private static string ManageInstallationUrl(string accountType, string accountLogin, long installationId) =>
        string.Equals(accountType, "Organization", StringComparison.OrdinalIgnoreCase)
            ? $"https://github.com/organizations/{Uri.EscapeDataString(accountLogin)}/settings/installations/{installationId}"
            : $"https://github.com/settings/installations/{installationId}";

    private string? InstallUrl() =>
        string.IsNullOrWhiteSpace(_options.AppSlug) ? null : $"https://github.com/apps/{_options.AppSlug}/installations/new";

    /// <summary>Removes the link. The files already in the project stay.</summary>
    [HttpDelete]
    public async Task<IActionResult> Unlink(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        ProjectGitHubLink? link = await _db.ProjectGitHubLinks.FirstOrDefaultAsync(l => l.ProjectId == projectId, ct);
        if (link is null) return NotFound(new { error = "not_linked" });

        _db.ProjectGitHubLinks.Remove(link);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Unlinked the repository of project {ProjectId}", projectId);
        return NoContent();
    }

    /// <summary>
    /// "Sync now": queues a pull of the linked branch as it stands right now, without waiting for a
    /// push, and answers <c>202 Accepted</c> at once — <c>{"status":"queued"}</c>, or
    /// <c>{"status":"already_syncing"}</c> when a sync of this project is already queued or running.
    /// <see cref="GitHubSyncWorker"/> runs the pull in the background (a pull of a few hundred files
    /// outlives nginx, Cloudflare and the browser), and its outcome lands on the link: the dashboard
    /// re-reads <c>GET .../github</c> while <c>syncing</c> is true. Same reconciliation as the webhook
    /// path, so running it twice at the same commit does nothing the second time.
    ///
    /// <para>
    /// Everything that can be decided before the pull still is, synchronously: the caller's own
    /// connected GitHub account must still see the repository — the same person check linking makes —
    /// so a member who could never have linked it cannot spend the installation on it either; and one
    /// "Sync now" per <see cref="GitHubSyncThrottle.Cooldown"/> (<c>429 sync_too_soon</c> with
    /// <c>Retry-After</c>). A sync refused for a reason the owner can fix (too many files chosen, too
    /// much data) or failed by GitHub is no longer an error status of this request: it is the link's
    /// <c>lastError</c>, in plain words, once the sync has run.
    /// </para>
    /// </summary>
    [HttpPost("pull")]
    public async Task<ActionResult<GitHubSyncQueuedResponse>> Pull(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        ProjectGitHubLink? link = await _db.ProjectGitHubLinks.FirstOrDefaultAsync(l => l.ProjectId == projectId, ct);
        if (link is null) return NotFound(new { error = "not_linked" });

        if (!_gitHub.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "github_app_not_configured" });

        GitHubReachResult reach = await _links.ResolveLinkedAsync(link, access.Project!.OrganizationId, _currentUser.UserId, ct);
        if (reach.Repository is null)
            return StatusCode(reach.StatusCode, new { error = reach.Error });

        // A sync already queued or running is the sync the person asked for: say so, not "too soon".
        if (_throttle.IsRunning(projectId))
            return Accepted(new GitHubSyncQueuedResponse(GitHubSyncQueuedResponse.AlreadySyncing));

        GitHubSyncThrottle.Lease? lease = _throttle.TryBegin(projectId, enforceCooldown: true, out TimeSpan retryAfter);
        if (lease is null)
        {
            return _throttle.IsRunning(projectId)
                ? Accepted(new GitHubSyncQueuedResponse(GitHubSyncQueuedResponse.AlreadySyncing))
                : TooSoon("sync_too_soon", retryAfter);
        }

        // The queue owns the lease from here; the worker releases it when the pull ends.
        _syncQueue.Enqueue(projectId, force: false, lease);
        return Accepted(new GitHubSyncQueuedResponse(GitHubSyncQueuedResponse.Queued));
    }

    private ObjectResult TooSoon(string code, TimeSpan retryAfter)
    {
        int seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return StatusCode(StatusCodes.Status429TooManyRequests, new { error = code, retryAfterSeconds = seconds });
    }

    private ProjectGitHubLinkResponse ToResponse(ProjectGitHubLink link)
    {
        IReadOnlyList<string>? includePaths;
        try
        {
            IReadOnlyList<string> parsed = GitHubIncludePaths.Parse(link.IncludePathsJson);
            includePaths = parsed.Count == 0 ? null : parsed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
        {
            // Shown as "everything" would be a lie; an empty choice makes the dashboard ask again.
            _logger.LogError(ex, "The stored file selection of project {ProjectId} is unreadable", link.ProjectId);
            includePaths = [];
        }

        string defaultBranch = string.IsNullOrWhiteSpace(link.RepositoryDefaultBranch) ? "main" : link.RepositoryDefaultBranch;
        return new ProjectGitHubLinkResponse(
            link.ProjectId,
            link.RepositoryFullName,
            link.RepositoryId,
            link.InstallationId,
            link.Branch ?? defaultBranch,
            link.PathPrefix,
            link.LastPulledCommit,
            link.LastPulledAt,
            link.LastPulledFileCount,
            link.LastError,
            defaultBranch,
            includePaths,
            link.CreatedAt,
            Syncing: _throttle.IsRunning(link.ProjectId));
    }
}
