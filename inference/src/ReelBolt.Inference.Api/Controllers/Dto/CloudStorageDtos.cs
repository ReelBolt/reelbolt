namespace ReelBolt.Inference.Api.Controllers.Dto;

// Cloud drive sync (docs/design/cloud-drive-sync.md "HTTP API"). JSON is camelCase; `provider` is
// always the URL slug ("google-drive", "onedrive", "dropbox"). No response ever carries a token.

/// <summary>One provider of <c>GET /api/v1/cloud-storage/providers</c>.</summary>
public sealed record CloudStorageProviderResponse(string Provider, string DisplayName, bool Configured);

/// <summary>One of the caller's connected accounts in the active workspace.</summary>
public sealed record CloudStorageConnectionResponse(
    Guid Id,
    string Provider,
    string? AccountEmail,
    string? AccountDisplayName,
    DateTime CreatedAt,
    string? LastError);

/// <summary>Body of <c>POST {provider}/oauth/start</c>: where to come back to (a <c>/app/…</c> path; anything else is ignored).</summary>
public sealed record CloudOAuthStartRequest(string? ReturnTo = null);

public sealed record CloudOAuthStartResponse(string AuthorizeUrl);

/// <summary>
/// A space to pick a folder from. <see cref="DriveId"/> null is the account's own space. <see cref="Kind"/>:
/// <c>personal</c>, <c>sharedDrive</c>, <c>sharePointLibrary</c>, <c>teamSpace</c>.
/// </summary>
public sealed record CloudDriveResponse(string? DriveId, string Name, string Kind);

/// <summary>A folder: <see cref="FolderId"/> null is the drive's root.</summary>
public sealed record CloudFolderResponse(string? FolderId, string Name, string DisplayPath);

public sealed record CloudItemResponse(string Id, string Name, bool IsFolder, long? Size, DateTime? ModifiedAt);

/// <summary>One level of a folder, folders first; at most 500 items, then <see cref="Truncated"/>.</summary>
public sealed record CloudFolderListResponse(CloudFolderResponse Folder, IReadOnlyList<CloudItemResponse> Items, bool Truncated);

/// <summary>
/// Links a folder to a project. <see cref="DriveId"/> null is the account's own space,
/// <see cref="FolderId"/> null that drive's root. <see cref="IncludePaths"/> null or empty means every
/// file of the folder; otherwise folder-relative files and folders (the GitHub selection's format and rules).
/// </summary>
public sealed record CreateProjectCloudLinkRequest(
    Guid ConnectionId,
    string? DriveId = null,
    string? FolderId = null,
    IReadOnlyList<string>? IncludePaths = null);

/// <summary>Changes the selection. <see cref="IncludePaths"/> null leaves it alone; an empty list means every file.</summary>
public sealed record UpdateProjectCloudLinkRequest(IReadOnlyList<string>? IncludePaths = null);

/// <summary>
/// A project's link to a cloud folder. <see cref="IncludePaths"/> null means every file.
/// <see cref="Syncing"/> is true while a sync of it is queued or running; the outcome is then on the
/// link (<see cref="LastSyncedAt"/>/<see cref="LastSyncedFileCount"/>, or <see cref="LastError"/> in plain words).
/// </summary>
public sealed record ProjectCloudLinkResponse(
    Guid Id,
    string Provider,
    Guid? ConnectionId,
    string? AccountEmail,
    string? DriveId,
    string? FolderId,
    string FolderDisplayPath,
    IReadOnlyList<string>? IncludePaths,
    DateTime? LastSyncedAt,
    int LastSyncedFileCount,
    string? LastError,
    bool Syncing);

/// <summary>Answer of <c>POST {linkId}/sync</c>: <c>queued</c> or <c>already_syncing</c>.</summary>
public sealed record CloudSyncQueuedResponse(string Status);
