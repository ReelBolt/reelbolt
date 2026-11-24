using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Uploads;

/// <summary>
/// The one implementation of "put a file into a project's storage without its bytes passing through
/// this API" (docs/design/local-folder-sync-and-storage.md "Uploads without a size cap"). Shared by the
/// browser routes in <c>ProjectFilesController</c> and the desktop app's folder sync
/// (<c>DeviceFolderSyncController</c>), so both get the same ticket MAC, the same server-chosen key,
/// the same quota checks and the same multipart machinery.
///
/// <para>
/// <b>Single PUT or multipart.</b> A file up to <see cref="DirectUploadLimits.SinglePutMaxBytes"/> gets
/// one presigned PUT. A larger one gets an S3 multipart upload: the server creates it, the client asks
/// for presigned part URLs in batches and PUTs parts (reading each part's <c>ETag</c>), and completion
/// sends the part list back. Create/Complete/Abort are server-side calls (bucket CORS allows only
/// GET/HEAD/PUT). There is no per-file size limit below <see cref="DirectUploadLimits.MaxObjectBytes"/>
/// (S3's own 5 TiB object ceiling); the plan's storage quota is the real limit.
/// </para>
///
/// <para>
/// <b>Quota.</b> <see cref="CreateAsync"/> checks the claimed size (net of the bytes of the file it
/// replaces, when <see cref="DirectUploadRequest.ReplacesFileId"/> is set) and
/// <see cref="CompleteAsync"/> re-checks the size storage actually holds, discarding the object on
/// refusal — exactly as the single-PUT route did before.
/// </para>
/// </summary>
public interface IDirectUploadService
{
    /// <summary>
    /// Validates and opens an upload. Never trusts a client-chosen key: the key is derived from a new
    /// object id (<see cref="DirectUploadTicket.FileId"/>, a fresh one even when replacing, so the
    /// replaced bytes survive until the new ones are verified) and the request's folder path. Throws
    /// <see cref="DirectUploadRefusedException"/> for every refusal (quota, size, bad path, unsupported
    /// storage) so each caller maps it to its own response.
    /// </summary>
    Task<DirectUploadTicket> CreateAsync(DirectUploadRequest request, CancellationToken ct);

    // Presigned URLs for parts of a multipart ticket (1-based part numbers, at most 100 per call).
    Task<IReadOnlyList<DirectUploadPartUrl>> PresignPartsAsync(DirectUploadTicket ticket, IReadOnlyList<int> partNumbers, CancellationToken ct);

    /// <summary>
    /// Finishes the upload: completes the multipart upload when there is one, HEADs the object, checks
    /// size and quota against what is actually stored, and inserts or rewrites the <see cref="ProjectFile"/>
    /// row (same id when replacing). Idempotent for a repeated completion of the same ticket.
    /// </summary>
    Task<ProjectFile> CompleteAsync(DirectUploadTicket ticket, DirectUploadCompletion completion, CancellationToken ct);

    // Best effort: aborts a multipart upload / deletes a partial object. Never throws for "already gone".
    Task AbortAsync(DirectUploadTicket ticket, CancellationToken ct);

    /// <summary>
    /// Rebuilds a ticket the client echoed back and verifies its MAC in fixed time, bound to the caller
    /// (<paramref name="callerKey"/> = the user id for the browser, <c>device:{deviceId}</c> for the
    /// desktop app). Returns null when the ticket is forged, altered or for another caller. Expiry is NOT
    /// judged here: <see cref="PresignPartsAsync"/> and <see cref="CompleteAsync"/> refuse an expired
    /// ticket with <c>UPLOAD_TICKET_EXPIRED</c> (410) and discard what it uploaded, which a null here
    /// could not do.
    /// </summary>
    DirectUploadTicket? Verify(DirectUploadTicketEcho echo, string callerKey);
}

public static class DirectUploadLimits
{
    // Above this a ticket is multipart. Well under S3/R2's 5 GiB single-PUT limit.
    public const long SinglePutMaxBytes = 256L * 1024 * 1024;

    // Smallest part size used (S3 minimum is 5 MiB for every part but the last).
    public const long MinPartBytes = 64L * 1024 * 1024;

    // S3's maximum part count.
    public const int MaxParts = 10_000;

    // S3's maximum object size (5 TiB). Not a product limit; the storage quota is.
    public const long MaxObjectBytes = 5L * 1024 * 1024 * 1024 * 1024;

    // Part size for a file: 64 MiB, or larger when needed to stay within 10,000 parts (rounded up to 1 MiB).
    public static long PartSizeFor(long sizeBytes)
    {
        long needed = (sizeBytes + MaxParts - 1) / MaxParts;
        long size = Math.Max(MinPartBytes, needed);
        const long MiB = 1024 * 1024;
        return (size + MiB - 1) / MiB * MiB;
    }
}

/// <summary>Who is uploading and what. <see cref="CallerKey"/> binds the ticket MAC (see <see cref="IDirectUploadService.Verify"/>).</summary>
public sealed record DirectUploadRequest(
    Guid ProjectId,
    Guid OrganizationId,
    string CallerKey,
    string FileName,
    string? RelativePath,
    long SizeBytes,
    string? ContentType,
    // "userFiles" for browser uploads; the folder sync also writes "userFiles".
    string Category,
    // project_files.source_kind for the row (null for an upload; "local_folder" for folder sync).
    string? SourceKind = null,
    // project_files.source_revision (the folder sync's SHA-256 hex).
    string? SourceRevision = null,
    // project_files.source_link_id (the folder link).
    Guid? SourceLinkId = null,
    // When set, the upload rewrites this existing row in place (same id) instead of adding one.
    Guid? ReplacesFileId = null,
    // S-05: an assistant/MCP ticket. Its URLs are short-lived (single PUT 15 min, parts 30 min) and its
    // completion MOVES the object to a fresh, never-presigned key before the row is written, so a later
    // re-PUT to the URL cannot change the recorded file. Not combinable with ReplacesFileId.
    bool RelocateOnComplete = false);

public enum DirectUploadMode
{
    SinglePut = 0,
    Multipart = 1
}

/// <summary>
/// An open upload. Everything in it is covered by <see cref="UploadToken"/> (the MAC); the client echoes
/// the fields back and <see cref="IDirectUploadService.Verify"/> rebuilds and checks them.
/// <see cref="Url"/> is set for <see cref="DirectUploadMode.SinglePut"/>; <see cref="UploadId"/> and
/// <see cref="PartSizeBytes"/>/<see cref="PartCount"/> for <see cref="DirectUploadMode.Multipart"/>.
/// </summary>
public sealed record DirectUploadTicket(
    string UploadToken,
    DirectUploadMode Mode,
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
    IReadOnlyDictionary<string, string> Headers,
    string? SourceKind,
    string? SourceRevision,
    Guid? SourceLinkId,
    Guid? ReplacesFileId)
{
    /// <summary>See <see cref="DirectUploadRequest.RelocateOnComplete"/>. Covered by the MAC.</summary>
    public bool RelocateOnComplete { get; init; }
}

/// <summary>The fields a client sends back to continue or finish an upload.</summary>
public sealed record DirectUploadTicketEcho(
    string UploadToken,
    DirectUploadMode Mode,
    Guid ProjectId,
    Guid FileId,
    string Key,
    string FileName,
    string? RelativePath,
    string ContentType,
    long SizeBytes,
    long ExpiresAtUnix,
    string? UploadId,
    long PartSizeBytes,
    int PartCount,
    string? SourceKind,
    string? SourceRevision,
    Guid? SourceLinkId,
    Guid? ReplacesFileId)
{
    /// <summary>The ticket's <see cref="DirectUploadTicket.RelocateOnComplete"/>, as the client echoes it.</summary>
    public bool RelocateOnComplete { get; init; }
}

public sealed record DirectUploadPartUrl(int PartNumber, string Url, long ExpiresAtUnix);

/// <summary>For a multipart ticket, every part's number and the ETag storage returned for it; empty for a single PUT.</summary>
public sealed record DirectUploadCompletion(IReadOnlyList<DirectUploadCompletedPart> Parts);

public sealed record DirectUploadCompletedPart(int PartNumber, string ETag);

/// <summary>
/// A refusal with a stable machine code and a plain-language message for the person:
/// <c>STORAGE_QUOTA_EXCEEDED</c> (413, carries used/limit bytes), <c>FILE_TOO_LARGE</c> (413, above
/// <see cref="DirectUploadLimits.MaxObjectBytes"/>), <c>INVALID_SIZE</c>/<c>INVALID_PATH</c> (400),
/// <c>UPLOAD_SIZE_MISMATCH</c> (409, storage holds a different size than claimed),
/// <c>UPLOAD_OBJECT_MISSING</c> (409, nothing was stored), <c>UPLOAD_INCOMPLETE</c> (409, parts missing
/// or ETags wrong), <c>UPLOAD_TICKET_EXPIRED</c> (410), <c>NOT_MULTIPART</c>/<c>INVALID_PART_NUMBERS</c>
/// (400), <c>REPLACED_FILE_NOT_FOUND</c>/<c>PROJECT_NOT_FOUND</c> (404), <c>DIRECT_UPLOAD_DISABLED</c>
/// (501, this deployment cannot presign or cannot do multipart). The <c>UPLOAD_*</c> codes are the ones
/// the browser routes have always answered with.
/// </summary>
public sealed class DirectUploadRefusedException : Exception
{
    public DirectUploadRefusedException(string code, string message, int statusCode, long? usedBytes = null, long? limitBytes = null, long? incomingBytes = null, string? plan = null)
        : base(message)
    {
        IncomingBytes = incomingBytes;
        Plan = plan;
        Code = code;
        StatusCode = statusCode;
        UsedBytes = usedBytes;
        LimitBytes = limitBytes;
    }

    public string Code { get; }

    /// <summary>The size of the file that is bigger than the whole plan (null for an ordinary full workspace).</summary>
    public long? IncomingBytes { get; }

    /// <summary>The plan's name, with <see cref="IncomingBytes"/>.</summary>
    public string? Plan { get; }

    public int StatusCode { get; }

    public long? UsedBytes { get; }

    public long? LimitBytes { get; }
}
