using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.Uploads;

/// <summary>
/// <see cref="IDirectUploadService"/>: the single-PUT logic that used to live in
/// <c>ProjectFilesController</c> (ticket MAC, server-chosen key, HEAD verification, quota before and
/// after), plus S3 multipart for files above <see cref="DirectUploadLimits.SinglePutMaxBytes"/>.
///
/// <para>
/// <b>The key.</b> Always derived here from (project, a server-generated object id, the folder path,
/// the file name) — the same derivation <c>S3FileStorageService.UploadAsync</c> uses. A ticket that
/// replaces a file (<see cref="DirectUploadRequest.ReplacesFileId"/>) gets a NEW object id too, so the
/// new bytes land beside the old ones: the row keeps its id and is pointed at the new object only once
/// the upload is verified, and the old object is deleted after that. A refused or abandoned replacement
/// therefore never destroys the file it was meant to replace.
/// </para>
///
/// <para>
/// <b>The ticket.</b> <see cref="DirectUploadTicket.UploadToken"/> is an HMAC
/// (<see cref="IConfirmationTokenService"/>) over every ticket field, length-prefixed, together with the
/// caller key — so a ticket minted for one user (or one device) does not verify for another, and no field
/// (mode, upload id, part layout, source, replaced file, size, expiry) can be changed by the client.
/// </para>
///
/// <para>
/// <b>Staging (upload-replay-fix).</b> A single-PUT URL names the ticket's key, and that key is only ever a
/// STAGING key: completion pins the object's ETag with a HEAD, moves it with a conditional server-side copy
/// (<see cref="IFileStorageService.MoveIfMatchAsync"/>) to a fresh key that was never presigned, and judges and
/// records that copy. A presigned PUT cannot be revoked, so a replay through the URL after completion only
/// re-creates an unreferenced object at the staging key: it can never change a recorded file's bytes, which
/// therefore only change through a new ticket and a new completion (size, quota, CSAM and moderation again).
/// A multipart upload is not relocated (unless the ticket asks for it): its part URLs are bound to the upload
/// id, which storage forgets once the upload is completed, so they cannot write after completion.
/// </para>
///
/// <para>
/// <b>Lifetimes.</b> A single-PUT ticket lives for the URL's hour plus a 30-minute grace for the
/// completion call. An assistant ticket (<see cref="DirectUploadRequest.RelocateOnComplete"/>, S-05) is
/// shorter: a 15-minute PUT URL, 30-minute part URLs, and its completion moves the object to a fresh key
/// that was never presigned, so the URL (which lands in chat transcripts) is useless once applied. A multipart ticket lives <see cref="MultipartTicketTtl"/> (a very large file can take
/// longer than 4 hours to send), and each batch of part URLs is valid for at most 4 hours, never past the
/// ticket. Abandoned multipart uploads are removed by the bucket's lifecycle rule (1 day).
/// </para>
/// </summary>
public sealed class DirectUploadService : IDirectUploadService
{
    /// <summary>Operation name of the ticket in the confirmation-token MAC.</summary>
    internal const string TicketOperation = "DirectUpload.v2";

    /// <summary>The only category a direct upload writes (browser uploads and the folder sync alike).</summary>
    public const string Category = "userFiles";

    /// <summary>Most part URLs one call may ask for.</summary>
    public const int MaxPartUrlsPerCall = 100;

    /// <summary>The time a ticket outlives its URLs, for the completion call only.</summary>
    public static readonly TimeSpan CompletionGrace = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a single PUT's URL stays valid: one hour, enough for the largest single PUT
    /// (<see cref="DirectUploadLimits.SinglePutMaxBytes"/>) at under 1 Mbit/s. Shorter than
    /// <see cref="PresignedUrlService.MaxBrowserUploadPutTtl"/> because a presigned URL cannot be revoked.
    /// </summary>
    public static readonly TimeSpan SinglePutUrlTtl = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a single-PUT ticket stays usable: the transfer window plus a grace period, so a client that
    /// finished its PUT at 59m is not refused at 1h00m. The URL is dead after <see cref="SinglePutUrlTtl"/>,
    /// so the object cannot change during the grace period.
    /// </summary>
    public static readonly TimeSpan SinglePutTicketTtl = SinglePutUrlTtl + CompletionGrace;

    /// <summary>S-05: a relocating (assistant) ticket's single-PUT URL lifetime.</summary>
    public static readonly TimeSpan RelocatingSinglePutUrlTtl = TimeSpan.FromMinutes(15);

    /// <summary>S-05: a relocating (assistant) ticket's part-URL lifetime.</summary>
    public static readonly TimeSpan RelocatingPartUrlTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The largest object a relocating ticket may hold: the completion moves it with one server-side
    /// <c>CopyObject</c>, whose ceiling is 5 GiB.
    /// </summary>
    public const long RelocateMaxBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>How long a multipart ticket stays usable: one day, the lifecycle rule's abort horizon.</summary>
    public static readonly TimeSpan MultipartTicketTtl = TimeSpan.FromHours(24);

    /// <summary>Longest a batch of part URLs is valid.</summary>
    public static readonly TimeSpan PartUrlTtl = PresignedUrlService.MaxBrowserUploadPutTtl;

    private static readonly Dictionary<string, string> MediaExtensionMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".webm"] = "video/webm",
        [".mkv"] = "video/x-matroska",
        [".avi"] = "video/x-msvideo",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".m4a"] = "audio/mp4",
        [".aac"] = "audio/aac",
        [".flac"] = "audio/flac",
        [".ogg"] = "audio/ogg",
    };

    private readonly InferenceApiDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly IPresignedUrlService _presign;
    private readonly IConfirmationTokenService _tokens;
    private readonly IMultipartObjectStore? _multipart;
    private readonly IPlanGateService? _planGate;
    private readonly TimeProvider _clock;
    private readonly ILogger<DirectUploadService>? _logger;

    public DirectUploadService(
        InferenceApiDbContext db,
        IFileStorageService storage,
        IPresignedUrlService presign,
        IConfirmationTokenService tokens,
        IMultipartObjectStore? multipart = null,
        IPlanGateService? planGate = null,
        TimeProvider? clock = null,
        ILogger<DirectUploadService>? logger = null)
    {
        _db = db;
        _storage = storage;
        _presign = presign;
        _tokens = tokens;
        _multipart = multipart;
        _planGate = planGate;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    // ------------------------------------------------------------------ create

    public async Task<DirectUploadTicket> CreateAsync(DirectUploadRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CallerKey)) throw new ArgumentException("A caller key is required.", nameof(request));

        if (request.SizeBytes < 0)
            throw Refused("INVALID_SIZE", "The file size must not be negative.", StatusCodes.Status400BadRequest);
        if (request.SizeBytes > DirectUploadLimits.MaxObjectBytes)
            throw Refused("FILE_TOO_LARGE", $"Files are limited to {DirectUploadLimits.MaxObjectBytes} bytes (5 TiB), the most object storage can hold in one file.", StatusCodes.Status413PayloadTooLarge);
        if (!string.Equals(request.Category, Category, StringComparison.Ordinal))
            throw Refused("INVALID_PATH", "Direct uploads can only add project files.", StatusCodes.Status400BadRequest);
        if (request.RelocateOnComplete && request.ReplacesFileId is not null)
            throw new ArgumentException("A relocating ticket cannot replace a file.", nameof(request));
        if (request.RelocateOnComplete && request.SizeBytes > RelocateMaxBytes)
            throw Refused("FILE_TOO_LARGE", $"Files uploaded this way are limited to {RelocateMaxBytes} bytes (5 GiB). Upload larger files from the web app.", StatusCodes.Status413PayloadTooLarge);

        ProjectFile? replaced = null;
        if (request.ReplacesFileId is { } replacesId)
        {
            replaced = await Files().FirstOrDefaultAsync(f => f.Id == replacesId && f.ProjectId == request.ProjectId, ct)
                ?? throw Refused("REPLACED_FILE_NOT_FOUND", "The file this upload was meant to replace no longer exists.", StatusCodes.Status404NotFound);
            if (await Outputs.OutputGate.IsFileHeldAsync(_db, replaced, ct))
                throw Refused(OutputHoldResponse.Code, OutputHoldResponse.Message, StatusCodes.Status404NotFound);
            // An avatar's reference keeps the bytes its consent was attested for: replacing it is refused like a delete.
            if (await ReelBolt.Shared.Avatars.AvatarFileUse.FirstUseAsync(_db, request.ProjectId, [replaced.Id], ct) is { } avatarUse)
                throw Refused(ReelBolt.Shared.Avatars.AvatarFileUse.InUseCode, avatarUse.Message, StatusCodes.Status409Conflict);
        }

        // The plan's quota on the CLAIMED size, net of the file being replaced. Checked again against the
        // real size at completion: a presigned PUT cannot carry a maximum length.
        await RequireQuotaAsync(request.OrganizationId, request.SizeBytes - ReelBolt.Shared.Billing.ProjectFileQuota.CountedSize(replaced), ct);

        (string originalPath, string fileName) = NormalizePath(request.FileName, request.RelativePath);
        string? relativePath = string.IsNullOrWhiteSpace(request.RelativePath) ? null : originalPath;
        Guid objectId = Guid.NewGuid();
        string key = DeriveKey(request.ProjectId, objectId, fileName, relativePath)!;
        string contentType = ResolveContentType(request.ContentType, fileName) ?? string.Empty;

        DateTimeOffset now = _clock.GetUtcNow();
        bool relocate = request.RelocateOnComplete;
        DirectUploadTicket ticket;
        if (request.SizeBytes <= DirectUploadLimits.SinglePutMaxBytes)
        {
            TimeSpan urlTtl = relocate ? RelocatingSinglePutUrlTtl : SinglePutUrlTtl;
            long expiresAtUnix = now.Add(urlTtl + CompletionGrace).ToUnixTimeSeconds();
            PresignedPut put = await _presign.CreateBrowserUploadPutAsync(request.ProjectId, key, contentType, urlTtl);
            Dictionary<string, string> headers = new(StringComparer.Ordinal);
            if (contentType.Length > 0) headers["Content-Type"] = contentType;
            ticket = Sign(new DirectUploadTicket(
                string.Empty, DirectUploadMode.SinglePut, request.ProjectId, objectId, key, fileName, relativePath, contentType,
                request.SizeBytes, expiresAtUnix, put.Url, null, 0, 0, headers,
                request.SourceKind, request.SourceRevision, request.SourceLinkId, request.ReplacesFileId)
            { RelocateOnComplete = relocate }, request.CallerKey);
        }
        else
        {
            if (_multipart is null)
            {
                throw Refused(
                    "DIRECT_UPLOAD_DISABLED",
                    "This deployment cannot upload large files in parts. Ask your administrator to enable multipart uploads.",
                    StatusCodes.Status501NotImplemented);
            }

            long partSize = DirectUploadLimits.PartSizeFor(request.SizeBytes);
            int partCount = (int)((request.SizeBytes + partSize - 1) / partSize);
            long expiresAtUnix = now.Add(relocate ? RelocatingPartUrlTtl + CompletionGrace : MultipartTicketTtl).ToUnixTimeSeconds();
            string uploadId = await _multipart.CreateAsync(request.ProjectId, key, contentType, ct);
            ticket = Sign(new DirectUploadTicket(
                string.Empty, DirectUploadMode.Multipart, request.ProjectId, objectId, key, fileName, relativePath, contentType,
                request.SizeBytes, expiresAtUnix, null, uploadId, partSize, partCount, new Dictionary<string, string>(StringComparer.Ordinal),
                request.SourceKind, request.SourceRevision, request.SourceLinkId, request.ReplacesFileId)
            { RelocateOnComplete = relocate }, request.CallerKey);
        }

        return ticket;
    }

    // ------------------------------------------------------------------ parts

    public async Task<IReadOnlyList<DirectUploadPartUrl>> PresignPartsAsync(DirectUploadTicket ticket, IReadOnlyList<int> partNumbers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (ticket.Mode != DirectUploadMode.Multipart || string.IsNullOrEmpty(ticket.UploadId))
            throw Refused("NOT_MULTIPART", "This upload is a single PUT and has no parts.", StatusCodes.Status400BadRequest);
        if (_multipart is null)
            throw Refused("DIRECT_UPLOAD_DISABLED", "This deployment cannot upload large files in parts.", StatusCodes.Status501NotImplemented);
        if (partNumbers is null || partNumbers.Count == 0)
            throw Refused("INVALID_PART_NUMBERS", "Ask for at least one part.", StatusCodes.Status400BadRequest);
        if (partNumbers.Count > MaxPartUrlsPerCall)
            throw Refused("INVALID_PART_NUMBERS", $"Ask for at most {MaxPartUrlsPerCall} parts at a time.", StatusCodes.Status400BadRequest);
        if (partNumbers.Any(n => n < 1 || n > ticket.PartCount) || partNumbers.Distinct().Count() != partNumbers.Count)
            throw Refused("INVALID_PART_NUMBERS", $"Part numbers must be distinct and between 1 and {ticket.PartCount}.", StatusCodes.Status400BadRequest);

        DateTimeOffset now = _clock.GetUtcNow();
        DateTimeOffset ticketExpires = DateTimeOffset.FromUnixTimeSeconds(ticket.ExpiresAtUnix);
        if (ticketExpires <= now)
        {
            await AbortAsync(ticket, ct);
            throw Expired();
        }

        TimeSpan maxTtl = ticket.RelocateOnComplete ? RelocatingPartUrlTtl : PartUrlTtl;
        TimeSpan ttl = ticketExpires - now < maxTtl ? ticketExpires - now : maxTtl;
        long expiresAtUnix = now.Add(ttl).ToUnixTimeSeconds();

        List<DirectUploadPartUrl> urls = new(partNumbers.Count);
        foreach (int partNumber in partNumbers)
        {
            string url = await _multipart.PresignPartAsync(ticket.ProjectId, ticket.Key, ticket.UploadId, partNumber, ttl);
            urls.Add(new DirectUploadPartUrl(partNumber, url, expiresAtUnix));
        }

        return urls;
    }

    // ------------------------------------------------------------------ complete

    public async Task<ProjectFile> CompleteAsync(DirectUploadTicket ticket, DirectUploadCompletion completion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        completion ??= new DirectUploadCompletion([]);

        // Idempotent: a retried completion (a double submit, an answer lost in transit) returns the row
        // the first call wrote. For a new file the row's id is the ticket's object id; for a replacement
        // the replaced row already points at this ticket's key.
        Guid rowId = ticket.ReplacesFileId ?? ticket.FileId;
        ProjectFile? existing = await Files().FirstOrDefaultAsync(f => f.Id == rowId && f.ProjectId == ticket.ProjectId, ct);

        // A replacement must never repoint a quarantined row or delete its quarantine object — a
        // ticket issued before the match can still be completed. Refuse, and remove only the new
        // upload's object when it is not the quarantined one already in place.
        if (existing?.CsamStatus == CsamScanStatus.Quarantined)
        {
            if (!string.Equals(existing.StorageKey, ticket.Key, StringComparison.Ordinal))
                await DiscardAsync(ticket, ct);
            throw Refused("FILE_UNAVAILABLE", "This file cannot be used.", StatusCodes.Status409Conflict);
        }

        // A kept file is neither repointed nor deleted by a later completion; the new upload's object goes.
        if (existing is not null && await Outputs.OutputGate.IsFileHeldAsync(_db, existing, ct))
        {
            if (!string.Equals(existing.StorageKey, ticket.Key, StringComparison.Ordinal))
                await DiscardAsync(ticket, ct);
            throw Refused(OutputHoldResponse.Code, OutputHoldResponse.Message, StatusCodes.Status404NotFound);
        }

        // A ticket issued before the file became an avatar's reference must not replace its bytes at completion either.
        if (existing is not null && ticket.ReplacesFileId is not null && !CompletedBy(existing, ticket)
            && await ReelBolt.Shared.Avatars.AvatarFileUse.FirstUseAsync(_db, ticket.ProjectId, [existing.Id], ct) is { } avatarUse)
        {
            await DiscardAsync(ticket, ct);
            throw Refused(ReelBolt.Shared.Avatars.AvatarFileUse.InUseCode, avatarUse.Message, StatusCodes.Status409Conflict);
        }

        bool relocate = RelocatesOnComplete(ticket);
        if (existing is not null && (ticket.ReplacesFileId is null || CompletedBy(existing, ticket)))
        {
            // A relocated file lives at a key that was never presigned; anything at the ticket's own key
            // now is a re-PUT through a URL that is still alive. It is never recorded, so it goes.
            bool recordedAtPresignedKey = string.Equals(existing.StorageKey, ticket.Key, StringComparison.Ordinal);
            if (!recordedAtPresignedKey)
                await DiscardObjectAsync(ticket.ProjectId, ticket.Key, ct);

            // Only a row recorded AT the presigned key (a multipart upload's key, which no URL can write once
            // completed, or a single PUT completed before staging existed) can have had its bytes replaced
            // through the URL, so only then are the verdicts stale. A relocated row's bytes cannot change, and
            // its verdicts stand.
            bool reset = recordedAtPresignedKey
                && ticket.Mode == DirectUploadMode.SinglePut
                && (existing.ModerationStatus != FileModerationStatus.Unmoderated
                    || existing.CsamStatus != CsamScanStatus.Pending);
            if (reset)
            {
                // A presigned PUT can be replayed with different bytes, so both verdicts describe
                // stale bytes. Reset them; the sweeper re-scans and moderation waits for that verdict.
                existing.ModerationStatus = FileModerationStatus.Unmoderated;
                existing.CsamStatus = CsamScanStatus.Pending;
                existing.CsamScannedAt = null;
                await _db.SaveChangesAsync(ct);
            }

            return existing;
        }

        if (DateTimeOffset.FromUnixTimeSeconds(ticket.ExpiresAtUnix) <= _clock.GetUtcNow())
        {
            await AbortAsync(ticket, ct);
            throw Expired();
        }

        if (ticket.ReplacesFileId is not null && existing is null)
        {
            await DiscardAsync(ticket, ct);
            throw Refused("REPLACED_FILE_NOT_FOUND", "The file this upload was meant to replace no longer exists.", StatusCodes.Status404NotFound);
        }

        Project project = await _db.Projects
            .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            .FirstOrDefaultAsync(p => p.Id == ticket.ProjectId, ct)
            ?? throw Refused("PROJECT_NOT_FOUND", "The project no longer exists.", StatusCodes.Status404NotFound);

        if (ticket.Mode == DirectUploadMode.Multipart)
        {
            if (_multipart is null || string.IsNullOrEmpty(ticket.UploadId))
                throw Refused("DIRECT_UPLOAD_DISABLED", "This deployment cannot upload large files in parts.", StatusCodes.Status501NotImplemented);

            IReadOnlyList<DirectUploadCompletedPart> parts = completion.Parts ?? [];
            bool complete = parts.Count == ticket.PartCount
                && parts.Select(p => p.PartNumber).OrderBy(n => n).SequenceEqual(Enumerable.Range(1, ticket.PartCount))
                && parts.All(p => !string.IsNullOrWhiteSpace(p.ETag));
            if (!complete)
            {
                throw Refused(
                    "UPLOAD_INCOMPLETE",
                    $"The upload has {ticket.PartCount} parts and each needs its number and the ETag storage returned for it.",
                    StatusCodes.Status409Conflict);
            }

            // False: storage no longer knows the upload id (an earlier completion whose answer was lost).
            // The HEAD below decides whether the object is there.
            await _multipart.CompleteAsync(ticket.ProjectId, ticket.Key, ticket.UploadId, parts, ct);
        }

        // S-05 and upload-replay-fix: a relocating ticket's object (every single PUT, and a multipart ticket
        // that asks for it) is moved to a fresh key BEFORE it is measured, and everything below judges (and
        // records) the moved copy, which no URL can write to. The move is conditional on the ETag read here,
        // so bytes replaced through the URL while the completion runs are refused rather than recorded.
        Guid objectId = ticket.FileId;
        string key = ticket.Key;
        if (relocate)
        {
            objectId = Guid.NewGuid();
            key = DeriveKey(ticket.ProjectId, objectId, ticket.FileName, ticket.RelativePath)
                ?? throw Refused("INVALID_PATH", "The file path is not valid.", StatusCodes.Status400BadRequest);
            StoredObjectMetadata? staged;
            bool moved;
            try
            {
                staged = await _storage.GetMetadataAsync(ticket.ProjectId, ticket.Key, ct);
                moved = staged is { ETag: { Length: > 0 } pinnedETag }
                    && await _storage.MoveIfMatchAsync(ticket.ProjectId, ticket.Key, key, pinnedETag, ct);
            }
            catch (OperationCanceledException)
            {
                // S-40: an aborted request can cancel mid-move (copy landed, source not deleted). The same cleanup as
                // a failed move runs on a bounded non-request token, then the cancellation surfaces unchanged.
                await DiscardCancelledRelocationAsync(ticket, key);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A concurrent completion moved it first (its row is the answer), or nothing was PUT.
                if (await RacedCompletionAsync(ticket, rowId, ct) is { } raced)
                    return raced;
                _logger?.LogWarning(ex, "Relocating an upload failed; nothing was recorded.");
                // S-31: the move failed (above the 5 GiB CopyObject ceiling, or the copy landed and the source delete
                // failed), so nothing will be recorded: the ticket-key object and any copy at the fresh key would sit in
                // storage outside the quota. Both go (never one a row points at); the fresh key is this call's alone.
                await DiscardAsync(ticket, ct);
                await DiscardObjectAsync(ticket.ProjectId, key, ct);
                throw Refused("UPLOAD_OBJECT_MISSING", "No object was stored for this upload, so there is nothing to record.", StatusCodes.Status409Conflict);
            }

            if (!moved)
            {
                if (staged is null)
                {
                    // Nothing at the staging key: a concurrent completion moved it first, or nothing was PUT.
                    if (await RacedCompletionAsync(ticket, rowId, ct) is { } raced)
                        return raced;
                    throw Refused("UPLOAD_OBJECT_MISSING", "No object was stored for this upload, so there is nothing to record.", StatusCodes.Status409Conflict);
                }

                // The staged object changed between the HEAD and the copy (bytes re-PUT through the URL while the
                // completion ran), or the store could not condition the copy. Neither is recorded; both copies go.
                await DiscardAsync(ticket, ct);
                await DiscardObjectAsync(ticket.ProjectId, key, ct);
                throw Refused(
                    "UPLOAD_CHANGED",
                    "The uploaded file changed in storage while the upload was being finished, so it was not recorded. Upload it again.",
                    StatusCodes.Status409Conflict);
            }
        }

        // S-80: the object a replacement makes unreachable. Read here, before the row is written, because the
        // cleanup of a cancelled save needs it too — and a replacement never relocates (CreateAsync refuses the
        // combination), so the cancellation catch below is the only place that cleanup can run.
        string? oldKey = existing?.StorageKey;

        // S-46: once a relocation has moved the object, the fresh key is this call's alone until the row is saved. A
        // cancellation in the HEAD, the quota check or the save would otherwise orphan it outside the quota, so it is
        // removed on a non-request token; the no-row check keeps it once a saved row points at it.
        try
        {
            StoredObjectMetadata? stored = await _storage.GetMetadataAsync(ticket.ProjectId, key, ct);
            if (stored is null)
                throw Refused("UPLOAD_OBJECT_MISSING", "No object was stored at that key, so there is nothing to record.", StatusCodes.Status409Conflict);

            if (stored.SizeBytes > DirectUploadLimits.MaxObjectBytes)
            {
                await DiscardAsync(ticket, ct);
                if (relocate) await DiscardObjectAsync(ticket.ProjectId, key, ct);
                throw Refused("FILE_TOO_LARGE", $"Files are limited to {DirectUploadLimits.MaxObjectBytes} bytes.", StatusCodes.Status413PayloadTooLarge);
            }

            if (stored.SizeBytes != ticket.SizeBytes)
            {
                await DiscardAsync(ticket, ct);
                if (relocate) await DiscardObjectAsync(ticket.ProjectId, key, ct);
                throw Refused(
                    "UPLOAD_SIZE_MISMATCH",
                    $"Storage holds {stored.SizeBytes} bytes for this upload and {ticket.SizeBytes} were expected. The upload was not recorded.",
                    StatusCodes.Status409Conflict);
            }

            // The quota, re-checked against what is ACTUALLY there, net of the file being replaced. An upload
            // that overshot is refused and its object deleted, never silently recorded.
            try
            {
                await RequireQuotaAsync(project.OrganizationId, stored.SizeBytes - ReelBolt.Shared.Billing.ProjectFileQuota.CountedSize(existing), ct);
            }
            catch (DirectUploadRefusedException)
            {
                await DiscardAsync(ticket, ct);
                if (relocate) await DiscardObjectAsync(ticket.ProjectId, key, ct);
                throw;
            }

            string originalPath = ticket.RelativePath ?? ticket.FileName;
            string? directoryPath = ProjectFilePath.GetDirectoryPath(originalPath);
            string storageFileName = ProjectFilePath.BuildStorageFileName(objectId, ticket.FileName);

            // A synced file (any source) is not summarized or embedded — the same rule as the GitHub pull —
            // and neither is media (R22). Everything else waits for the summarizer and the indexer; the caller
            // queues them for a row it gets back as Pending.
            // Issue 8: a `.ts` the browser or sync client typed as video is decided by its bytes, now that
            // they are in storage (the declared type was signed into the URL before anything arrived).
            string mimeType = await ResolveStoredMimeTypeAsync(ticket, key, ct);
            bool pending = ticket.SourceKind is null && !IsVideoOrAudio(mimeType);

            ProjectFile row = existing ?? new ProjectFile { Id = ticket.FileId, ProjectId = ticket.ProjectId };
            row.OriginalFileName = ticket.FileName;
            row.OriginalPath = originalPath;
            row.DirectoryPath = directoryPath;
            row.Category = Category;
            row.StorageKey = key;
            row.StorageFileName = storageFileName;
            row.StorageBucket = _storage.BucketName;
            row.StoragePrefix = ProjectFilePath.BuildStoragePrefix(ticket.ProjectId, Category);
            row.StorageMetadataJson = BuildStorageMetadataJson(project, row.Id, originalPath, ticket.FileName, storageFileName, ticket.SourceKind, relocate ? ticket.FileId : null);
            row.MimeType = mimeType;
            row.SizeBytes = stored.SizeBytes;
            row.SourceKind = ticket.SourceKind;
            row.SourceRevision = ticket.SourceRevision;
            row.SourceLinkId = ticket.SourceLinkId;
            row.AgentSummary = null;
            row.SummaryStatus = pending ? SummaryStatus.Pending : SummaryStatus.Done;
            row.IndexingStatus = pending ? FileIndexingStatus.Pending : FileIndexingStatus.NotIndexed;
            row.IndexedAt = null;
            row.IndexingError = null;
            // A re-upload or replacement writes new bytes, so any earlier verdict no longer applies.
            row.ModerationStatus = FileModerationStatus.Unmoderated;
            row.CsamStatus = CsamScanStatus.Pending;
            row.CsamScannedAt = null;
            row.UploadedAt = _clock.GetUtcNow().UtcDateTime;

            if (existing is null) _db.ProjectFiles.Add(row);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (relocate && existing is null)
            {
                // Two completions raced and both moved a copy: the row that won is the file; ours goes.
                _db.Entry(row).State = EntityState.Detached;
                await DiscardObjectAsync(ticket.ProjectId, key, ct);
                return await Files().AsNoTracking().FirstOrDefaultAsync(f => f.Id == rowId && f.ProjectId == ticket.ProjectId, ct)
                    ?? throw Refused("UPLOAD_OBJECT_MISSING", "The upload could not be recorded. Start it again.", StatusCodes.Status409Conflict);
            }

            // The row now points at the new object: the replaced bytes can go — on a token of its own, because a
            // request cancelled between the save and this delete must not abandon it (S-80).
            await RetireReplacedKeyAsync(ticket.ProjectId, oldKey, key);

            // A completed upload's post-commit effects — a media row's derivative set (d2 N6), the
            // summarizer, the activity feed — belong to the caller's one post-add path
            // (IProjectFileAddedEffects), which every route that commits a file row already applies
            // after this returns. Publishing here as well would ask the engine for the same derivative
            // set twice for one upload.
            return row;
        }
        catch (OperationCanceledException)
        {
            // S-46: a relocation's fresh-key copy is this call's alone until the row is saved.
            if (relocate)
                await DiscardCancelledFreshKeyAsync(ticket.ProjectId, key);

            // S-80: a replacement's previous object is unreachable as soon as a row points at the new key, and a
            // cancellation in the save leaves no way to know whether the write landed — the no-row check decides,
            // so the bytes are retired when they are already orphaned and kept when the file still points at them.
            await RetireReplacedKeyAsync(ticket.ProjectId, oldKey, key);
            throw;
        }
        catch (Exception ex) when (ex is not DirectUploadRefusedException && (relocate || oldKey is not null))
        {
            // QA-P0-28: the S-46 window wraps HEAD, the quota check and SaveChanges, but a failure there that is
            // neither a cancellation, a refusal nor a DbUpdateException (a storage outage on the HEAD, a database
            // error on the quota check or the save) orphans the fresh-key copy just as a cancellation does. The same
            // cleanup runs on the same bounded non-request token, with the no-row check, and the original failure
            // surfaces unchanged. The refusal paths above already remove their own copy, so they are not re-run.
            if (relocate)
                await DiscardCancelledFreshKeyAsync(ticket.ProjectId, key);
            await RetireReplacedKeyAsync(ticket.ProjectId, oldKey, key);
            throw;
        }
    }

    // ------------------------------------------------------------------ abort

    public async Task AbortAsync(DirectUploadTicket ticket, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        try
        {
            await DiscardAsync(ticket, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Aborting the upload to {StorageKey} failed; storage's lifecycle rule will remove it.", ticket.Key);
        }
    }

    // ------------------------------------------------------------------ verify

    public DirectUploadTicket? Verify(DirectUploadTicketEcho echo, string callerKey)
    {
        if (echo is null || string.IsNullOrWhiteSpace(callerKey) || string.IsNullOrWhiteSpace(echo.UploadToken)) return null;
        if (echo.ProjectId == Guid.Empty || echo.FileId == Guid.Empty || string.IsNullOrWhiteSpace(echo.Key)) return null;
        if (!Enum.IsDefined(echo.Mode) || echo.SizeBytes < 0) return null;

        // The name and path as the ticket was minted with them (normalized), and the key they derive —
        // which must be the echoed key before the MAC is even consulted.
        string originalPath;
        string fileName;
        try
        {
            (originalPath, fileName) = NormalizePath(echo.FileName, echo.RelativePath);
        }
        catch (DirectUploadRefusedException)
        {
            return null;
        }

        string? relativePath = string.IsNullOrWhiteSpace(echo.RelativePath) ? null : originalPath;
        string? expectedKey = DeriveKey(echo.ProjectId, echo.FileId, fileName, relativePath);
        if (expectedKey is null || !string.Equals(expectedKey, echo.Key, StringComparison.Ordinal)) return null;

        DirectUploadTicket ticket = new(
            echo.UploadToken, echo.Mode, echo.ProjectId, echo.FileId, echo.Key, fileName, relativePath, echo.ContentType ?? string.Empty,
            echo.SizeBytes, echo.ExpiresAtUnix, null, echo.UploadId, echo.PartSizeBytes, echo.PartCount,
            new Dictionary<string, string>(StringComparer.Ordinal),
            echo.SourceKind, echo.SourceRevision, echo.SourceLinkId, echo.ReplacesFileId)
        { RelocateOnComplete = echo.RelocateOnComplete };

        return _tokens.Verify(TicketOperation, Payload(ticket, callerKey), Guid.Empty, null, echo.UploadToken) ? ticket : null;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The key a ticket's fields derive, or null when the name or path is not a valid project path. The
    /// controller uses it to tell a ticket whose key was swapped (<c>UPLOAD_KEY_MISMATCH</c>) from one
    /// that is otherwise invalid.
    /// </summary>
    public static string? DeriveKey(Guid projectId, Guid objectId, string? fileName, string? relativePath)
    {
        try
        {
            (string originalPath, string name) = NormalizePath(fileName, relativePath);
            string storageFileName = ProjectFilePath.BuildStorageFileName(objectId, name);
            return ProjectFilePath.BuildStorageKey(projectId, Category, ProjectFilePath.GetDirectoryPath(originalPath), storageFileName);
        }
        catch (DirectUploadRefusedException)
        {
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The MIME the row records: the ticket's declared type, except for a <c>.ts</c>-family name whose
    /// declaration is a transport stream or generic, where the first bytes of the stored object decide
    /// (<see cref="TransportStreamMimeSniffer"/>). A read failure keeps the declaration: the bytes are
    /// stored and the upload must not fail over a type hint. Cancellation is not swallowed.
    /// </summary>
    private async Task<string> ResolveStoredMimeTypeAsync(DirectUploadTicket ticket, string key, CancellationToken ct)
    {
        if (!TransportStreamMimeSniffer.NeedsSniff(ticket.ContentType, ticket.FileName))
            return ticket.ContentType;

        try
        {
            await using Stream content = await _storage.DownloadAsync(ticket.ProjectId, key, ct);
            byte[] head = await ProjectFileTextPolicy.ReadHeadAsync(content, ct);
            return TransportStreamMimeSniffer.Resolve(ticket.ContentType, ticket.FileName, head) ?? ticket.ContentType;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Reading the head of an uploaded .ts file failed; its declared type was kept.");
            return ticket.ContentType;
        }
    }

    /// <summary>
    /// R22: a generic or missing content type for a well-known media extension is resolved from the
    /// extension, so a video never reaches the text summarizer; a text extension from the shared
    /// <see cref="ProjectFileTextPolicy"/> is resolved to its MIME so a new <c>.scss</c>/<c>.csproj</c>
    /// is not stored as <c>application/octet-stream</c>; any other specific type is trusted.
    /// </summary>
    public static string? ResolveContentType(string? contentType, string? fileName)
    {
        if (!string.IsNullOrEmpty(contentType) &&
            !contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return contentType;
        }

        string ext = Path.GetExtension(fileName ?? string.Empty);
        if (!string.IsNullOrEmpty(ext) && MediaExtensionMimeTypes.TryGetValue(ext, out string? resolved))
            return resolved;

        return ProjectFileTextPolicy.MimeTypeForExtension(fileName) ?? contentType;
    }

    public static bool IsVideoOrAudio(string? mimeType) =>
        !string.IsNullOrEmpty(mimeType) &&
        (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
         mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));

    /// <summary>The stored path and the file name: the folder path when given (it ends with the name), else the bare name.</summary>
    private static (string OriginalPath, string FileName) NormalizePath(string? fileName, string? relativePath)
    {
        string name = (fileName ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > 255 || name.IndexOfAny(['/', '\\']) >= 0 || name is "." or ".." || name.Any(char.IsControl))
            throw Refused("INVALID_PATH", "The file name is not valid.", StatusCodes.Status400BadRequest);

        try
        {
            string? normalizedRelative = string.IsNullOrWhiteSpace(relativePath) ? null : ProjectFilePath.NormalizeRelativePath(relativePath);
            if (normalizedRelative is not null && normalizedRelative.Any(char.IsControl))
                throw Refused("INVALID_PATH", "The file path is not valid.", StatusCodes.Status400BadRequest);
            string originalPath = ProjectFilePath.NormalizeRelativePath(normalizedRelative ?? name);
            return (originalPath, ProjectFilePath.GetFileName(originalPath));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw Refused("INVALID_PATH", "The file path is not valid.", StatusCodes.Status400BadRequest);
        }
    }

    private DirectUploadTicket Sign(DirectUploadTicket unsigned, string callerKey) =>
        unsigned with { UploadToken = _tokens.Compute(TicketOperation, Payload(unsigned, callerKey), Guid.Empty, null) };

    /// <summary>
    /// The message the ticket MAC covers: every field the client echoes, plus the caller key. Every value
    /// is length-prefixed (a null as <c>-1:</c>), so no two distinct tuples frame to the same string.
    /// </summary>
    internal static string Payload(DirectUploadTicket t, string callerKey) =>
        string.Concat(
            Frame(callerKey),
            Frame(t.Mode.ToString()),
            Frame(t.ProjectId.ToString("D")),
            Frame(t.FileId.ToString("D")),
            Frame(t.Key),
            Frame(t.FileName),
            Frame(t.RelativePath),
            Frame(t.ContentType),
            Frame(t.SizeBytes.ToString(CultureInfo.InvariantCulture)),
            Frame(t.ExpiresAtUnix.ToString(CultureInfo.InvariantCulture)),
            Frame(t.UploadId),
            Frame(t.PartSizeBytes.ToString(CultureInfo.InvariantCulture)),
            Frame(t.PartCount.ToString(CultureInfo.InvariantCulture)),
            Frame(t.SourceKind),
            Frame(t.SourceRevision),
            Frame(t.SourceLinkId?.ToString("D")),
            Frame(t.ReplacesFileId?.ToString("D")),
            // Appended only when set, so a browser ticket's MAC is byte-for-byte what it always was.
            t.RelocateOnComplete ? Frame("relocate") : string.Empty);

    private static string Frame(string? value) =>
        value is null ? "-1:" : string.Concat(value.Length.ToString(CultureInfo.InvariantCulture), ":", value);

    /// <summary>
    /// Whether completion moves the object off the ticket's (presigned) key: always for a single PUT, whose URL
    /// would otherwise keep writing to the recorded file, and for a ticket that asks for it (S-05).
    /// </summary>
    internal static bool RelocatesOnComplete(DirectUploadTicket ticket) =>
        ticket.RelocateOnComplete || ticket.Mode == DirectUploadMode.SinglePut;

    /// <summary>The <c>StorageMetadataJson</c> key a relocated row keeps its ticket's object id under.</summary>
    internal const string UploadObjectIdMetadataKey = "upload-object-id";

    /// <summary>
    /// Whether <paramref name="row"/> was written by a completion of <paramref name="ticket"/>: it sits at the
    /// ticket's key (not relocated), or it records the ticket's object id (relocated). This is how a retried
    /// completion of a replacement is told from a first one.
    /// </summary>
    private static bool CompletedBy(ProjectFile row, DirectUploadTicket ticket)
    {
        if (string.Equals(row.StorageKey, ticket.Key, StringComparison.Ordinal)) return true;
        if (string.IsNullOrEmpty(row.StorageMetadataJson)) return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(row.StorageMetadataJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(UploadObjectIdMetadataKey, out JsonElement id)
                && id.ValueKind == JsonValueKind.String
                && Guid.TryParse(id.GetString(), out Guid parsed)
                && parsed == ticket.FileId;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The row a concurrent completion of the same ticket already wrote, or null.</summary>
    private async Task<ProjectFile?> RacedCompletionAsync(DirectUploadTicket ticket, Guid rowId, CancellationToken ct)
    {
        ProjectFile? row = await Files().AsNoTracking().FirstOrDefaultAsync(f => f.Id == rowId && f.ProjectId == ticket.ProjectId, ct);
        return row is not null && (ticket.ReplacesFileId is null || CompletedBy(row, ticket)) ? row : null;
    }

    private IQueryable<ProjectFile> Files() =>
        _db.ProjectFiles.IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName]);

    private async Task RequireQuotaAsync(Guid organizationId, long netGrowth, CancellationToken ct)
    {
        if (_planGate is null || netGrowth <= 0) return;
        if (await _planGate.CheckUploadAsync(organizationId, netGrowth, ct) is { } violation)
        {
            throw new DirectUploadRefusedException(
                PlanGate.StorageCode,
                violation.Message,
                violation.StatusCode,
                usedBytes: violation.Current,
                limitBytes: violation.Limit,
                incomingBytes: violation.Incoming,
                plan: violation.Plan);
        }
    }

    /// <summary>Removes what a refused or abandoned upload left: the multipart upload, or the single object.</summary>
    private async Task DiscardAsync(DirectUploadTicket ticket, CancellationToken ct)
    {
        if (ticket.Mode == DirectUploadMode.Multipart && _multipart is not null && !string.IsNullOrEmpty(ticket.UploadId))
            await _multipart.AbortAsync(ticket.ProjectId, ticket.Key, ticket.UploadId, ct);

        // Never delete an object a row points at (a completed upload aborted afterwards).
        bool referenced = await Files().AnyAsync(f => f.ProjectId == ticket.ProjectId && f.StorageKey == ticket.Key, ct);
        if (!referenced) await DeleteQuietlyAsync(ticket.ProjectId, ticket.Key, ct);
    }

    /// <summary>How long the cleanup of a cancelled relocation may run once the request token is gone.</summary>
    private static readonly TimeSpan CancelledRelocationCleanupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// S-40: removes the ticket-key object and the fresh-key copy of a relocation the request cancelled, on a token
    /// of its own (bounded by <see cref="CancelledRelocationCleanupTimeout"/>), keeping the no-row checks. Never throws:
    /// the caller rethrows the cancellation, and the retention sweep catches what a storage outage leaves.
    /// </summary>
    private async Task DiscardCancelledRelocationAsync(DirectUploadTicket ticket, string freshKey)
    {
        using CancellationTokenSource cleanup = new(CancelledRelocationCleanupTimeout);
        try
        {
            await DiscardAsync(ticket, cleanup.Token);
            await DiscardObjectAsync(ticket.ProjectId, freshKey, cleanup.Token);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Cleaning up a cancelled upload relocation failed.");
        }
    }

    /// <summary>
    /// S-46: removes the fresh-key copy of a relocation the request cancelled after the move, on the same bounded
    /// non-request token as S-40, keeping the no-row check. Never throws: the caller rethrows the cancellation.
    /// </summary>
    private async Task DiscardCancelledFreshKeyAsync(Guid projectId, string freshKey)
    {
        using CancellationTokenSource cleanup = new(CancelledRelocationCleanupTimeout);
        try
        {
            await DiscardObjectAsync(projectId, freshKey, cleanup.Token);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Cleaning up a cancelled upload relocation failed.");
        }
    }

    /// <summary>
    /// S-80: retires the object a replacement left behind, on a token of its own bounded by
    /// <see cref="CancelledRelocationCleanupTimeout"/> rather than the request's — a request cancelled after the row
    /// was written must not abandon the delete and leave the replaced bytes outside the quota. The same no-row check
    /// as every other discard runs first, so an object a row still points at (the save did not land) is kept. Never
    /// throws: the caller either answers with the row or rethrows the cancellation unchanged.
    /// </summary>
    private async Task RetireReplacedKeyAsync(Guid projectId, string? replacedKey, string currentKey)
    {
        if (replacedKey is null || string.Equals(replacedKey, currentKey, StringComparison.Ordinal))
            return;

        using CancellationTokenSource cleanup = new(CancelledRelocationCleanupTimeout);
        try
        {
            await DiscardObjectAsync(projectId, replacedKey, cleanup.Token);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not retire the object a replacement left behind.");
        }
    }

    /// <summary>Deletes one object unless a row points at it (never a recorded file).</summary>
    private async Task DiscardObjectAsync(Guid projectId, string key, CancellationToken ct)
    {
        bool referenced = await Files().AnyAsync(f => f.ProjectId == projectId && f.StorageKey == key, ct);
        if (!referenced) await DeleteQuietlyAsync(projectId, key, ct);
    }

    /// <summary>
    /// Best-effort delete. Never throws into the response: the point of the caller is to answer with the
    /// refusal, and a storage outage must not turn a 413 into a 500. The bucket's lifecycle rules and the
    /// retention sweep catch what is left.
    /// </summary>
    private async Task DeleteQuietlyAsync(Guid projectId, string key, CancellationToken ct)
    {
        try
        {
            await _storage.DeleteAsync(projectId, key, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Could not delete the object at {StorageKey}.", key);
        }
    }

    private static string BuildStorageMetadataJson(
        Project project, Guid fileId, string originalPath, string originalFileName, string storageFileName, string? sourceKind, Guid? uploadObjectId)
    {
        Dictionary<string, string> metadata = new()
        {
            ["project-id"] = project.Id.ToString(),
            ["owner-id"] = project.OwnerId.ToString(),
            ["org-id"] = project.OrganizationId.ToString(),
            ["project-file-id"] = fileId.ToString(),
            ["category"] = Category,
            ["original-path"] = originalPath,
            ["original-file-name"] = originalFileName,
            ["storage-file-name"] = storageFileName
        };
        if (sourceKind is not null) metadata["source-kind"] = sourceKind;
        if (uploadObjectId is { } uploadId) metadata[UploadObjectIdMetadataKey] = uploadId.ToString();
        return JsonSerializer.Serialize(metadata);
    }

    private static DirectUploadRefusedException Refused(string code, string message, int status) => new(code, message, status);

    private static DirectUploadRefusedException Expired() =>
        Refused("UPLOAD_TICKET_EXPIRED", "This upload ticket has expired. Start the upload again.", StatusCodes.Status410Gone);
}
