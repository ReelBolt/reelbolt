using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Storage;
using static ReelBolt.Inference.Api.Controllers.EditImageDocumentSupport;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The image editor's byte-moving routes: presigned uploads for masks and exports, and the export
/// registry (docs/design/thumbnail-editor.md sections 1 and 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Uploads go to the object store directly.</b> nginx caps <c>/api/v1/*</c> bodies at 1-2 MB and the
/// cloud store (Cloudflare R2) is reached through presigned PUTs, so a brush mask or a flattened export
/// is: <c>assets/upload-url</c> -> the browser PUTs the PNG -> the API is told the key. The key is the
/// SERVER's: <c>projects/{p}/agentFiles/image-editor/{documentId}/{purpose}-{guid}{ext}</c>, and every
/// later route that accepts a key checks it is exactly that shape for this document
/// (<see cref="EditImageDocumentSupport.IsIssuedEditorKey"/>), so a client can neither write to nor name
/// another tenant's object. See <c>docs/direct-upload.md</c> for the general scheme this narrows.
/// </para>
/// <para>
/// <b>Nothing is ever recorded on trust.</b> The export route HEADs the object, so the bytes that arrived,
/// not the client's claim, decide whether and how big the file is.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/image-documents")]
[Authorize]
public class EditImageAssetsController : ControllerBase
{
    /// <summary>How long a mask or export PUT stays valid: a single brush stroke or one render, not a phone upload.</summary>
    public static readonly TimeSpan UploadUrlTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long an uploaded mask or export may sit unused before the retention sweep may delete it.
    /// </summary>
    public static readonly TimeSpan UnusedUploadHorizon = TimeSpan.FromHours(24);

    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly IFileStorageService _storage;
    private readonly IPresignedUrlService? _presignedUrls;
    private readonly IPlanGateService? _planGate;
    private readonly IConfiguration? _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly IContentModerationEnqueuer? _moderation;
    /// <summary>T-004: publish/egress gate for a thumbnail export; null in tests that do not exercise publication.</summary>
    private readonly PublishModeration? _publishModeration;
    private readonly ReelBolt.Inference.Api.Services.Uploads.OrganizationStorageLock _storageLock;

    public EditImageAssetsController(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IProjectAccessService projectAccess,
        IFileStorageService storage,
        IPresignedUrlService? presignedUrls = null,
        IPlanGateService? planGate = null,
        IConfiguration? configuration = null,
        TimeProvider? timeProvider = null,
        IContentModerationEnqueuer? moderation = null,
        PublishModeration? publishModeration = null,
        ReelBolt.Inference.Api.Services.Uploads.OrganizationStorageLock? storageLock = null)
    {
        _storageLock = storageLock ?? new ReelBolt.Inference.Api.Services.Uploads.OrganizationStorageLock();
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _storage = storage;
        _presignedUrls = presignedUrls;
        _planGate = planGate;
        _configuration = configuration;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _moderation = moderation;
        _publishModeration = publishModeration;
    }

    // ------------------------------------------------------------------ upload-url

    /// <summary>
    /// Issues a presigned PUT for a <c>mask</c> (a PNG of at most 12 MiB), an <c>export</c> (any of
    /// <see cref="EditImageDocumentSupport.ExportFormats"/>, at most 200 MiB) or a <c>source</c> (a PNG or JPEG
    /// of at most 48 MiB: a smaller copy of a layer, rendered by the browser at the size the layer appears in
    /// the design, for an AI service whose documented limits the original is over; the operation answers
    /// <c>422 SOURCE_TOO_LARGE</c> with the size it needs).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both purposes get a <see cref="StorageRetentionMark"/> with a 24 hour horizon. A mask is
    /// consumed by one operation and then worthless, and an export key is promoted (MOVED) to a real
    /// project file by <c>POST exports</c> — after which the mark names a key that no longer exists,
    /// which the sweep's delete tolerates. The mark is what stops an upload that is never used from
    /// sitting in the bucket forever with nothing naming it.
    /// </para>
    /// <para>
    /// The sweep deletes a mark once it is <c>GraceDays</c> old, so the mark is written with
    /// <c>MarkedAt</c> set so that it ripens <see cref="UnusedUploadHorizon"/> after issue: now, plus the
    /// horizon, minus <c>Billing:StorageRetention:GraceDays</c>. The sweep itself is opt-in
    /// (<c>Billing:StorageRetention:Enabled</c>), exactly like every other mark.
    /// </para>
    /// </remarks>
    [HttpPost("{documentId:guid}/assets/upload-url")]
    public async Task<ActionResult<EditImageUploadUrlResponse>> CreateUploadUrl(
        Guid projectId, Guid documentId, [FromBody] EditImageUploadUrlRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        bool exists = await _db.EditImageDocuments.AnyAsync(d => d.Id == documentId && d.ProjectId == projectId, ct);
        if (!exists) return NotFound();

        if (_presignedUrls is null)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new
            {
                code = "DIRECT_UPLOAD_DISABLED",
                message = "This deployment does not support uploading directly to storage, so the editor cannot send masks or exports."
            });
        }

        string? purpose = request.Purpose?.Trim().ToLowerInvariant();
        if (purpose is not ("mask" or "export" or "source"))
            return BadRequest(new { message = "purpose must be 'mask', 'export' or 'source'." });

        // A mask is always a PNG. An export is any of the editor's export formats; the canonical
        // spelling of the type is what gets signed, so the browser's PUT must send exactly that.
        string requestedType = request.ContentType?.Trim() ?? "";
        string? contentType = null;
        string? extension = null;
        if (purpose == "mask")
        {
            if (string.Equals(requestedType, "image/png", StringComparison.OrdinalIgnoreCase))
            {
                contentType = "image/png";
                extension = ".png";
            }
        }
        else if (purpose == "source")
        {
            if (string.Equals(requestedType, "image/png", StringComparison.OrdinalIgnoreCase))
            {
                contentType = "image/png";
                extension = ".png";
            }
            else if (string.Equals(requestedType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
            {
                contentType = "image/jpeg";
                extension = ".jpg";
            }
        }
        else if (ExportFormats.TryGetValue(requestedType, out string? exportExtension))
        {
            contentType = requestedType.ToLowerInvariant();
            extension = exportExtension;
        }

        if (contentType is null || extension is null)
        {
            return BadRequest(new
            {
                message = purpose switch
                {
                    "mask" => "contentType must be image/png for a mask.",
                    "source" => "contentType must be image/png or image/jpeg for a source copy.",
                    _ => $"contentType must be one of: {string.Join(", ", ExportFormats.Keys)}."
                }
            });
        }

        long limit = purpose switch
        {
            "mask" => MaxEditorUploadBytes,
            "source" => MaxSourceCopyBytes,
            _ => MaxExportUploadBytes
        };
        // Empty is a bad request, not a too-large one (audit d6 N2): nothing was sent to upload.
        if (request.ContentLength < 1)
        {
            return BadRequest(new
            {
                code = "UPLOAD_EMPTY",
                message = purpose switch
                {
                    "mask" => "The mask is empty. Paint the area to change, then try again.",
                    "source" => "The photo copy is empty. Choose the photo again.",
                    _ => "The export is empty. Try exporting again."
                }
            });
        }
        if (request.ContentLength > limit)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new
            {
                code = "FILE_TOO_LARGE",
                message = $"A {purpose} is limited to {limit} bytes ({limit / (1024 * 1024)} MiB)."
            });
        }

        string key = $"{EditorPrefix(projectId, documentId)}{purpose}-{Guid.NewGuid()}{extension}";
        PresignedPut put = await _presignedUrls.CreateBrowserUploadPutAsync(projectId, key, contentType, UploadUrlTtl);

        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        int graceDays = Math.Clamp(
            _configuration?.GetValue("Billing:StorageRetention:GraceDays", StorageRetentionService.DefaultGraceDays)
                ?? StorageRetentionService.DefaultGraceDays,
            0, 365);

        _db.StorageRetentionMarks.Add(new StorageRetentionMark
        {
            Id = Guid.NewGuid(),
            OrganizationId = project.OrganizationId,
            ProjectId = projectId,
            StorageKey = key,
            Category = "agentFiles",
            MarkedAt = now.Add(UnusedUploadHorizon).AddDays(-graceDays)
        });
        await _db.SaveChangesAsync(ct);

        // The URL is a bearer capability to write that one key: returned to the caller, never logged.
        return Ok(new EditImageUploadUrlResponse(
            key,
            put.Url,
            new Dictionary<string, string> { ["Content-Type"] = contentType },
            now.Add(UploadUrlTtl)));
    }

    // ------------------------------------------------------------------ exports

    /// <summary>
    /// Registers a picture the browser rendered and uploaded (PNG, JPEG, WebP, AVIF, or a layered
    /// Photoshop or OpenRaster file) as an export of the document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In order, each failing closed: the caller may edit the project (404/403); the document is in this
    /// project (404); the key is exactly one an <c>export</c> upload-url issued for this document
    /// (<c>400 EXPORT_KEY_INVALID</c>, storage untouched); the object exists (<c>409 UPLOAD_OBJECT_MISSING</c>);
    /// its REAL size, from a HEAD, is inside the 200 MiB ceiling (<c>413</c>, object deleted) and the plan's
    /// storage quota (object deleted). The object is MOVED to
    /// <c>projects/{p}/userFiles/thumbnails/{fileId}{ext}</c> BEFORE it is judged, with a copy conditional on the
    /// ETag the HEAD read (<c>409 UPLOAD_CHANGED</c> when it changed), and size, quota and publish moderation
    /// judge that moved copy, which no presigned URL can write: a re-PUT through the upload URL while the export
    /// is being finished can never put unjudged bytes into the project (upload-replay-fix). A refusal deletes the
    /// moved copy. The type and extension are the ones the key was issued for, and a file name missing that
    /// extension gets it — so an export is an ordinary user file the retention sweep never touches, and the
    /// project-file and export rows are written in one save.
    /// </para>
    /// <para>
    /// <b>A retried POST is not idempotent</b>: the first call moved the object, so the second finds the
    /// upload key empty and answers <c>409 UPLOAD_OBJECT_MISSING</c>. The first call's file is listed by
    /// <c>GET exports</c>.
    /// </para>
    /// <para>
    /// <b><c>setAsProjectThumbnail</c> is a documented no-op.</b> The project model has no thumbnail
    /// field to set; the flag is accepted so the client contract is stable, and the response states
    /// <c>projectThumbnailApplied: false</c> rather than pretending.
    /// </para>
    /// </remarks>
    [HttpPost("{documentId:guid}/exports")]
    public async Task<ActionResult<EditImageExportResponse>> CreateExport(
        Guid projectId, Guid documentId, [FromBody] CreateEditImageExportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        EditImageDocument? document = await _db.EditImageDocuments
            .FirstOrDefaultAsync(d => d.Id == documentId && d.ProjectId == projectId, ct);
        if (document is null) return NotFound();

        if (!IsIssuedEditorKey(request.Key, projectId, documentId, "export"))
        {
            return BadRequest(new
            {
                code = ExportKeyInvalidCode,
                message = "key must be one returned by assets/upload-url with purpose 'export' for this document."
            });
        }

        if (request.Width is < 1 or > MaxExportSide || request.Height is < 1 or > MaxExportSide)
            return BadRequest(new { message = $"width and height must each be between 1 and {MaxExportSide}." });

        // The key says what was uploaded (upload-url chose its extension from the content type), so the
        // file's type and extension follow the key, never the caller's file name.
        string mimeType = ExportContentTypeOfKey(request.Key!) ?? "image/png";
        string extension = ExportFormats[mimeType];

        string? invalidName = ProjectFilesController.ValidateFileName(request.FileName, out string fileName);
        if (invalidName is not null) return BadRequest(new { message = invalidName });
        if (!string.Equals(ExportContentTypeForExtension(Path.GetExtension(fileName)), mimeType, StringComparison.OrdinalIgnoreCase))
            fileName += extension;

        StoredObjectMetadata? staged = await _storage.GetMetadataAsync(projectId, request.Key!, ct);
        if (staged is null)
        {
            return Conflict(new
            {
                code = "UPLOAD_OBJECT_MISSING",
                message = "No object was stored at that key, so there is nothing to record."
            });
        }
        if (staged.SizeBytes < 1 || staged.SizeBytes > MaxExportUploadBytes)
        {
            await DiscardAsync(projectId, request.Key!);
            return ExportTooLarge();
        }
        Guid fileId = Guid.NewGuid();
        const string directory = "thumbnails";
        string originalPath = ProjectFilePath.NormalizeRelativePath($"{directory}/{fileName}");
        string storageFileName = ProjectFilePath.BuildStorageFileName(fileId, fileName);
        string finalKey = ProjectFilePath.BuildStorageKey(projectId, "userFiles", directory, storageFileName);

        // upload-replay-fix: the upload URL stays alive after this call, so the bytes are moved off its key
        // FIRST — conditionally on the ETag just read — and everything below judges the moved copy. A store
        // that cannot condition the copy, or bytes re-PUT since the HEAD, are refused; nothing is recorded.
        // The move is inside the cleanup block: a move that throws (a request cancelled after the copy landed, a
        // failed source delete, a concurrent export of the same key) must not leave an unrecorded copy behind.
        bool recorded = false;
        try
        {
            bool moved = staged.ETag is { Length: > 0 } pinnedETag
                && await _storage.MoveIfMatchAsync(projectId, request.Key!, finalKey, pinnedETag, ct);
            if (!moved)
            {
                await DiscardAsync(projectId, request.Key!);
                return Conflict(new
                {
                    code = "UPLOAD_CHANGED",
                    message = "The export changed in storage while it was being saved, so it was not recorded. Try exporting again."
                });
            }

            StoredObjectMetadata? stored = await _storage.GetMetadataAsync(projectId, finalKey, ct);
            if (stored is null)
            {
                return Conflict(new
                {
                    code = "UPLOAD_OBJECT_MISSING",
                    message = "No object was stored at that key, so there is nothing to record."
                });
            }
            if (stored.SizeBytes < 1 || stored.SizeBytes > MaxExportUploadBytes)
                return ExportTooLarge();
            // The quota, against what is ACTUALLY there: a presigned PUT cannot carry a maximum length.
            // The organization's storage guard is held from the check through the row write, so two
            // concurrent exports cannot both pass it; disposed on every exit path.
            await using ReelBolt.Inference.Api.Services.Uploads.OrganizationStorageGuard guard =
                await _storageLock.AcquireAsync(_db, project.OrganizationId, ct);
            if (_planGate is not null && await _planGate.CheckUploadAsync(project.OrganizationId, stored.SizeBytes, ct) is { } overQuota)
                return overQuota.ToResult();
            // T-004: exporting is publication. Refuse a blocked/unclassifiable export BEFORE it becomes
            // a project file; no row is created on a refusal and the moved copy is deleted.
            if (_publishModeration is not null)
            {
                PublishModerationOutcome moderation = await _publishModeration.JudgeInlineAsync(
                    projectId, finalKey, mimeType,
                    new ModerationAttribution(
                        project.OrganizationId, projectId, _currentUser.UserId, null, null,
                        ModerationSurface.Publish, ModerationContentKind.Image),
                    ct);
                if (!moderation.Allowed)
                {
                    return moderation.Code == "CONTENT_BLOCKED"
                        ? StatusCode(StatusCodes.Status422UnprocessableEntity, new { message = moderation.Message, code = moderation.Code })
                        : StatusCode(StatusCodes.Status409Conflict, new { message = moderation.Message, code = moderation.Code });
                }
            }
            ActionResult<EditImageExportResponse> result = await RecordExportAsync(
                projectId, documentId, request, project, document, fileId, fileName, originalPath, directory,
                storageFileName, finalKey, mimeType, stored.SizeBytes, guard, ct);
            recorded = true;
            return result;
        }
        finally
        {
            // The moved copy is this call's alone until its row is saved. Every refusal above, and an exception
            // (a storage or database failure, a cancelled request), must not leave it in userFiles outside the
            // quota; the no-row check keeps it when the save landed before the failure surfaced.
            if (!recorded) await DiscardUnrecordedAsync(projectId, finalKey);
        }
    }

    /// <summary>
    /// Deletes <paramref name="key"/> unless a project file row points at it, on a bounded token of its own (the
    /// request's may be cancelled). Never throws; a failed check keeps the object for the retention sweep.
    /// </summary>
    private async Task DiscardUnrecordedAsync(Guid projectId, string key)
    {
        using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(30));
        try
        {
            bool referenced = await _db.ProjectFiles.IgnoreQueryFilters()
                .AnyAsync(f => f.ProjectId == projectId && f.StorageKey == key, cleanup.Token);
            if (!referenced) await _storage.DeleteAsync(projectId, key, cleanup.Token);
        }
        catch (Exception)
        {
            // The answer stands; the retention sweep's orphan pass finds an object no row names.
        }
    }

    private ObjectResult ExportTooLarge() => StatusCode(StatusCodes.Status413PayloadTooLarge, new
    {
        code = "FILE_TOO_LARGE",
        message = $"An export is limited to {MaxExportUploadBytes} bytes (200 MiB) and must not be empty."
    });

    /// <summary>Writes the project-file and export rows for a judged, moved export (one save), then queues moderation.</summary>
    private async Task<ActionResult<EditImageExportResponse>> RecordExportAsync(
        Guid projectId, Guid documentId, CreateEditImageExportRequest request, Project project, EditImageDocument document,
        Guid fileId, string fileName, string originalPath, string directory, string storageFileName, string finalKey,
        string mimeType, long sizeBytes, ReelBolt.Inference.Api.Services.Uploads.OrganizationStorageGuard guard, CancellationToken ct)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        ProjectFile projectFile = new()
        {
            Id = fileId,
            ProjectId = projectId,
            OriginalFileName = fileName,
            OriginalPath = originalPath,
            DirectoryPath = directory,
            Category = "userFiles",
            StorageKey = finalKey,
            StorageFileName = storageFileName,
            StorageBucket = _storage.BucketName,
            StoragePrefix = ProjectFilePath.BuildStoragePrefix(projectId, "userFiles"),
            StorageMetadataJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["project-id"] = projectId.ToString(),
                ["owner-id"] = project.OwnerId.ToString(),
                ["org-id"] = project.OrganizationId.ToString(),
                ["uploaded-by"] = _currentUser.UserId.ToString(),
                ["project-file-id"] = fileId.ToString(),
                ["category"] = "userFiles",
                ["original-path"] = originalPath,
                ["original-file-name"] = fileName,
                ["storage-file-name"] = storageFileName,
                ["image-document-id"] = documentId.ToString()
            }),
            MimeType = mimeType,
            SizeBytes = sizeBytes,
            // A rendered PNG has no text to summarise or embed: it is recorded finished, so it is
            // neither queued into the summarizer nor the vector indexer (the same reasoning R22 applies
            // to video and audio).
            SummaryStatus = SummaryStatus.Done,
            IndexingStatus = FileIndexingStatus.NotIndexed,
            UploadedAt = now
        };

        EditImageExport export = new()
        {
            Id = Guid.NewGuid(),
            DocumentId = documentId,
            ProjectFileId = fileId,
            Width = request.Width,
            Height = request.Height,
            Revision = document.Revision,
            CreatedAt = now
        };

        _db.ProjectFiles.Add(projectFile);
        _db.EditImageExports.Add(export);
        await _db.SaveChangesAsync(ct);
        await guard.CommitAsync(ct);
        if (CsamScanGate.AllowsModeration(projectFile))
            _moderation?.TryQueue(projectFile.Id);

        return StatusCode(StatusCodes.Status201Created, new EditImageExportResponse(
            ToExportDto(export, projectId),
            new ProjectFileResponse(
                projectFile.Id,
                projectFile.OriginalFileName,
                projectFile.OriginalPath,
                projectFile.DirectoryPath,
                projectFile.Category,
                projectFile.StorageKey,
                projectFile.StorageFileName,
                projectFile.MimeType,
                projectFile.SizeBytes,
                projectFile.AgentSummary,
                projectFile.SummaryStatus.ToString(),
                projectFile.IndexingStatus.ToString(),
                projectFile.IndexedAt,
                projectFile.IndexingError,
                projectFile.UploadedAt,
                projectFile.ModerationStatus.ToString()),
            // No project thumbnail exists to set; see the remarks.
            ProjectThumbnailApplied: false));
    }

    /// <summary>Every export of the document, newest first.</summary>
    [HttpGet("{documentId:guid}/exports")]
    public async Task<ActionResult<IReadOnlyList<EditImageExportDto>>> ListExports(
        Guid projectId, Guid documentId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        bool exists = await _db.EditImageDocuments.AnyAsync(d => d.Id == documentId && d.ProjectId == projectId, ct);
        if (!exists) return NotFound();

        List<EditImageExport> exports = await _db.EditImageExports
            .Where(e => e.DocumentId == documentId)
            .OrderByDescending(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        return Ok(exports.Select(e => ToExportDto(e, projectId)).ToList());
    }

    // ------------------------------------------------------------------ internals

    private static EditImageExportDto ToExportDto(EditImageExport export, Guid projectId) => new(
        export.Id,
        export.DocumentId,
        export.ProjectFileId,
        export.ProjectFileId is { } fileId ? $"/api/v1/projects/{projectId}/files/{fileId}/download" : null,
        export.Width,
        export.Height,
        export.Revision,
        export.CreatedAt);

    /// <summary>Best-effort delete of a refused upload; never throws into the response.</summary>
    private async Task DiscardAsync(Guid projectId, string key)
    {
        try
        {
            await _storage.DeleteAsync(projectId, key, CancellationToken.None);
        }
        catch (Exception)
        {
            // The refusal is the answer; a failed delete leaves an object the 24 hour retention mark
            // written at upload-url time still names.
        }
    }
}
