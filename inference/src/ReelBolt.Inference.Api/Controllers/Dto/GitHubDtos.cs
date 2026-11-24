namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>
/// Requests a repository be linked to a project. <see cref="Repository"/> is <c>owner/name</c> or any
/// github.com URL for it; <see cref="Branch"/> empty means the repository's default branch, and
/// <see cref="PathPrefix"/> empty means the whole repository. <see cref="IncludePaths"/> null or empty
/// means every file; otherwise the repository-relative files and folders to sync (a folder includes
/// everything under it). At most <c>GitHubIncludePaths.MaxPaths</c> entries, each a plain relative path.
/// </summary>
public sealed record LinkGitHubRepositoryRequest(
    string Repository,
    string? Branch = null,
    string? PathPrefix = null,
    IReadOnlyList<string>? IncludePaths = null);

/// <summary>
/// Changes what a link follows, then re-syncs. <see cref="Branch"/> null leaves the branch alone and
/// empty means the default branch; <see cref="IncludePaths"/> null leaves the selection alone and an
/// empty list means every file.
/// </summary>
public sealed record UpdateGitHubLinkRequest(string? Branch = null, IReadOnlyList<string>? IncludePaths = null);

/// <summary>
/// The link, as the dashboard sees it. Never carries a token or a secret. <c>includePaths</c> null
/// means every file is synced. <c>syncing</c> is true while a sync of this project is queued or running
/// in the background (after "Sync now", a new link or a changed selection); the dashboard re-reads the
/// link until it is false, and then the outcome is on the link: <c>lastPulledCommit</c>/<c>lastPulledFileCount</c>
/// for a success, <c>lastError</c> in plain words for a failure.
/// </summary>
public sealed record ProjectGitHubLinkResponse(
    Guid ProjectId,
    string Repository,
    long RepositoryId,
    long InstallationId,
    string Branch,
    string? PathPrefix,
    string? LastPulledCommit,
    DateTime? LastPulledAt,
    int LastPulledFileCount,
    string? LastError,
    string DefaultBranch = "main",
    IReadOnlyList<string>? IncludePaths = null,
    DateTime? LinkedAt = null,
    bool Syncing = false);

/// <summary>
/// The answer to "Sync now" (<c>202 Accepted</c>): <c>queued</c> when a sync was queued, or
/// <c>already_syncing</c> when one of this project was already queued or running. The outcome is read
/// from the link afterwards.
/// </summary>
public sealed record GitHubSyncQueuedResponse(string Status)
{
    public const string Queued = "queued";
    public const string AlreadySyncing = "already_syncing";
}

/// <summary>One file or folder of the repository, for the file selection tree.</summary>
public sealed record GitHubFileOption(string Path, bool IsDirectory, long Size);

/// <summary>
/// Every file and folder of one branch, flattened, plus the limits one sync works within so the
/// dashboard can warn before a selection is saved rather than after a sync refuses it. <c>commit</c> is
/// null for an empty repository; <c>truncated</c> means GitHub could not list it all, which also means
/// it cannot be synced.
/// </summary>
public sealed record GitHubFileListResponse(
    string Repository,
    string Branch,
    string? Commit,
    IReadOnlyList<GitHubFileOption> Entries,
    bool Truncated,
    int MaxFiles,
    long MaxTotalBytes,
    long MaxFileBytes);

/// <summary>The repository's branches, default first. <c>truncated</c> when GitHub had more than one page.</summary>
public sealed record GitHubBranchListResponse(string DefaultBranch, IReadOnlyList<string> Branches, bool Truncated);

/// <summary>
/// Asks for the URL to send the browser to in order to connect a GitHub account.
/// <see cref="ProjectId"/> is where the browser is returned to; it is optional, and it is echoed back
/// from our own single-use state row rather than from the callback's query string.
/// </summary>
public sealed record StartGitHubConnectRequest(Guid? ProjectId = null);

/// <summary>The authorization URL. Absolute, on github.com, and short-lived.</summary>
public sealed record StartGitHubConnectResponse(string AuthorizeUrl);

/// <summary>
/// The workspace's GitHub account connection. <c>canConnect</c> is false when this deployment has no
/// client id, client secret or callback URL, which is what tells the dashboard to explain rather than
/// to offer a button that cannot work. <b>No field here is, or ever may be, a token.</b>
/// </summary>
public sealed record GitHubAccountResponse(
    bool Connected,
    string? Login,
    string? AvatarUrl,
    bool CanConnect,
    string? RevokedReason,
    DateTime? ConnectedAt,
    string? InstallUrl);

/// <summary>
/// One repository the person may pick. <c>linkedProjectId</c> is set when this workspace already
/// follows it; <c>manageUrl</c> points at GitHub's page for the installation, which is where a
/// repository is added when the installation was granted only selected repositories.
/// </summary>
public sealed record GitHubRepositoryOption(
    long Id,
    string FullName,
    string DefaultBranch,
    bool IsPrivate,
    long InstallationId,
    string AccountLogin,
    string AccountType,
    string? RepositorySelection,
    string? LinkedProjectId,
    string ManageUrl);

/// <summary>The picker's list. <c>installUrl</c> is where an account that is missing an installation starts.</summary>
public sealed record GitHubRepositoryListResponse(
    IReadOnlyList<GitHubRepositoryOption> Repositories,
    bool Truncated,
    string? InstallUrl,
    string? AccountLogin);

/// <summary>One entry of a folder listing. Only a directory may be chosen as the folder to pull.</summary>
public sealed record GitHubFolderOption(string Name, string Path, string Sha, bool IsDirectory);

/// <summary>
/// The children of one directory of the chosen repository. <c>path</c> is empty at the repository root,
/// which is the "pull the whole repository" choice.
/// </summary>
public sealed record GitHubFolderListResponse(
    string Path,
    string? ParentPath,
    IReadOnlyList<GitHubFolderOption> Entries,
    bool Truncated);
