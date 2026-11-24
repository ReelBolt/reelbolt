using ReelBolt.Inference.Api.Services.Uploads;

namespace ReelBolt.Inference.Api.Services.FolderSync;

// Computer folder sync wire contract (docs/design/local-folder-sync-and-storage.md, "Device API" and
// "Dashboard API"). JSON camelCase. Errors are { error: code, message: plain words }.

public sealed record DeviceMeResponse(Guid DeviceId, string DeviceName, Guid OrganizationId, string OrganizationName, string? PairedByEmail);

public sealed record DeviceProjectResponse(Guid Id, string Name);

/// <summary>A link as the desktop app sees it. <see cref="PausedReason"/>: <c>guard</c>, <c>user</c> or null.</summary>
public sealed record DeviceFolderLinkResponse(
    Guid Id,
    Guid ProjectId,
    string ProjectName,
    string TargetFolder,
    string DisplayPath,
    bool Paused,
    string? PausedReason,
    int FileCount,
    DateTime? LastReconciledAt,
    string? LastError);

public sealed record CreateDeviceFolderLinkRequest(Guid ProjectId, string? TargetFolder, string? DisplayPath);

public sealed record ManifestFileEntry(string? Path, long Size, string? Sha256);

/// <summary>
/// One batch of a manifest: 0-based <see cref="Batch"/>, at most 2,000 files, <see cref="Final"/> on the
/// highest batch. <see cref="ConfirmLargeDelete"/> (read from the final batch) lifts a mass-delete guard pause.
/// </summary>
public sealed record ManifestBatchRequest(string? ManifestId, int Batch, bool Final, IReadOnlyList<ManifestFileEntry>? Files, bool ConfirmLargeDelete = false);

public sealed record ManifestAcceptedResponse(int Accepted);

public sealed record ManifestSkippedPath(string Path, string Reason);

public sealed record StorageUsageResponse(long UsedBytes, long? LimitBytes);

/// <summary>
/// The answer to a final manifest batch: the paths the desktop app must upload and delete (it sends the
/// deletes to <c>…/delete</c>; nothing is deleted during the manifest), what was skipped and why, and
/// whether the link is paused (and why).
/// </summary>
public sealed record ManifestResultResponse(
    IReadOnlyList<string> Upload,
    IReadOnlyList<string> Delete,
    IReadOnlyList<ManifestSkippedPath> Skipped,
    bool Paused,
    string? PausedReason,
    string? LastError,
    StorageUsageResponse Storage);

public sealed record DeviceUploadRequest(string? Path, long Size, string? Sha256, string? ContentType);

/// <summary>
/// An open upload, as the device receives and echoes it. <see cref="Mode"/> is <c>singlePut</c> (PUT the
/// bytes to <see cref="Url"/> with every <see cref="Headers"/> entry) or <c>multipart</c> (ask for part URLs).
/// </summary>
public sealed record DeviceUploadTicket(
    string UploadToken,
    string Mode,
    Guid ProjectId,
    Guid FileId,
    string Key,
    string FileName,
    string? RelativePath,
    string ContentType,
    long SizeBytes,
    long ExpiresAtUnix,
    string? Url,
    string? UploadId,
    long PartSizeBytes,
    int PartCount,
    IReadOnlyDictionary<string, string>? Headers,
    string? SourceKind,
    string? SourceRevision,
    Guid? SourceLinkId,
    Guid? ReplacesFileId)
{
    public const string SinglePut = "singlePut";
    public const string Multipart = "multipart";

    public static DeviceUploadTicket From(DirectUploadTicket t) => new(
        t.UploadToken, t.Mode == DirectUploadMode.Multipart ? Multipart : SinglePut, t.ProjectId, t.FileId, t.Key,
        t.FileName, t.RelativePath, t.ContentType, t.SizeBytes, t.ExpiresAtUnix, t.Url, t.UploadId,
        t.PartSizeBytes, t.PartCount, t.Headers, t.SourceKind, t.SourceRevision, t.SourceLinkId, t.ReplacesFileId);

    /// <summary>The echo the upload service verifies. Null when <see cref="Mode"/> is not recognized.</summary>
    public DirectUploadTicketEcho? ToEcho()
    {
        DirectUploadMode? mode = Mode switch
        {
            SinglePut or "0" => DirectUploadMode.SinglePut,
            Multipart or "1" => DirectUploadMode.Multipart,
            _ => null
        };

        return mode is { } m
            ? new DirectUploadTicketEcho(UploadToken, m, ProjectId, FileId, Key, FileName, RelativePath, ContentType,
                SizeBytes, ExpiresAtUnix, UploadId, PartSizeBytes, PartCount, SourceKind, SourceRevision, SourceLinkId, ReplacesFileId)
            : null;
    }
}

public sealed record DeviceUploadPartsRequest(DeviceUploadTicket? Ticket, IReadOnlyList<int>? PartNumbers);

public sealed record DeviceUploadPartUrl(int PartNumber, string Url, long ExpiresAtUnix);

public sealed record DeviceUploadCompletedPart(int PartNumber, string? Etag);

public sealed record DeviceUploadCompleteRequest(DeviceUploadTicket? Ticket, IReadOnlyList<DeviceUploadCompletedPart>? Parts);

public sealed record DeviceUploadCompleteResponse(Guid FileId, string Path);

public sealed record DeviceUploadAbortRequest(DeviceUploadTicket? Ticket);

public sealed record DeviceDeleteRequest(IReadOnlyList<string>? Paths);

public sealed record DeviceDeleteResponse(int Deleted, IReadOnlyList<string>? Skipped = null);

/// <summary>What a device delete removed, and the quarantined paths it deliberately left alone.</summary>
public sealed record FolderDeleteOutcome(int Deleted, IReadOnlyList<string> Skipped);

/// <summary>A link as the project's dashboard sees it.</summary>
public sealed record ProjectFolderLinkResponse(
    Guid Id,
    Guid DeviceId,
    string DeviceName,
    bool DeviceOnline,
    bool DeviceRevoked,
    string TargetFolder,
    string DisplayPath,
    bool Paused,
    string? PausedReason,
    int FileCount,
    DateTime? LastChangeAt,
    DateTime? LastReconciledAt,
    string? LastError);
