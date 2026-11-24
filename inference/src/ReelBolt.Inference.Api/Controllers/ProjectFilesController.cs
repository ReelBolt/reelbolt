using System.Globalization;
using System.Text.Json;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MassTransit;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Notifications;
using ReelBolt.Inference.Api.Services.Graphics;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Projects;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Editing.Color;
using ReelBolt.Shared;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Storage;
using DirectUploadTicket = ReelBolt.Inference.Api.Controllers.Dto.DirectUploadTicket;

namespace ReelBolt.Inference.Api.Controllers;

[ApiController]
[Route("api/v1/projects/{projectId:guid}/files")]
[Authorize]
public class ProjectFilesController : ControllerBase
{
    private const int ProjectReindexDefaultMaxFiles = 25;
    private const int ProjectReindexAbsoluteMaxFiles = 200;
    private static readonly TimeSpan ProjectReindexPublishInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The largest file a direct upload may claim: S3's own 5 TiB object ceiling
    /// (<see cref="DirectUploadLimits.MaxObjectBytes"/>). It is not a product limit — the plan's storage
    /// quota is. Files above <see cref="DirectUploadLimits.SinglePutMaxBytes"/> go up in parts. (The
    /// multipart FORM route below keeps its own, smaller cap: in practice the proxy in front of it.)
    /// </summary>
    internal const long MaxDirectUploadBytes = DirectUploadLimits.MaxObjectBytes;

    /// <summary>
    /// Answer for a deployment with no presigning wired (a composition that never registered
    /// <see cref="IPresignedUrlService"/>, e.g. a hand-built container). 501 is deliberate: it tells
    /// the client this server does not implement direct upload, which is the one answer the web
    /// client turns into the multipart fallback — as opposed to, say, a 500, which it reports.
    /// </summary>
    internal const string DirectUploadDisabledCode = "DIRECT_UPLOAD_DISABLED";

    /// <summary>How long a single PUT's presigned URL stays valid (see <see cref="DirectUploadService.SinglePutUrlTtl"/>).</summary>
    internal static readonly TimeSpan UploadUrlTtl = DirectUploadService.SinglePutUrlTtl;

    /// <summary>How long a single-PUT ticket stays usable: the URL's window plus a grace period for the completion call.</summary>
    internal static readonly TimeSpan UploadTicketTtl = DirectUploadService.SinglePutTicketTtl;

    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IPlanGateService? _planGate;
    private readonly IProjectAccessService _projectAccess;
    private readonly IFileStorageService _fileStorage;
    private readonly IBackgroundTaskQueue<FileSummarizationTask> _summarizationQueue;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly VectorSearchQueryService _vectorSearchQueryService;
    private readonly ReelBolt.Inference.Api.Services.Moderation.PublishModeration? _publishModeration;
    private readonly IDirectUploadService? _directUploads;
    private readonly ILogger<ProjectFilesController>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IUrlImportService? _urlImport;
    private readonly IProjectFileAddedEffects _fileAddedEffects;
    /// <summary>T-004: a retried direct-upload completion re-scans (the bytes may have changed).</summary>
    private readonly ReelBolt.Inference.Api.Services.Moderation.IContentModerationEnqueuer? _moderationEnqueuer;
    private readonly OrganizationStorageLock _storageLock;
    private readonly IProjectFileDeletionService? _fileDeletion;
    private readonly IDirectUploadFinalizer? _uploadFinalizer;
    private readonly Services.Telemetry.IProductEventEmitter? _productEvents;

    public ProjectFilesController(
        InferenceApiDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess,
        IFileStorageService fileStorage,
        IBackgroundTaskQueue<FileSummarizationTask> summarizationQueue,
        IPublishEndpoint publishEndpoint,
        VectorSearchQueryService vectorSearchQueryService,
        IPlanGateService? planGate = null,
        IPresignedUrlService? presignedUrls = null,
        IConfirmationTokenService? confirmationTokens = null,
        IUserActivityNotifier? activityNotifier = null,
        ILogger<ProjectFilesController>? logger = null,
        TimeProvider? timeProvider = null,
        IDirectUploadService? directUploads = null,
        IMultipartObjectStore? multipartStore = null,
        IUrlImportService? urlImport = null,
        IProjectFileAddedEffects? fileAddedEffects = null,
        OrganizationStorageLock? storageLock = null,
        ReelBolt.Inference.Api.Services.Moderation.IContentModerationEnqueuer? moderationEnqueuer = null,
        ReelBolt.Inference.Api.Services.Moderation.PublishModeration? publishModeration = null,
        IProjectFileDeletionService? fileDeletion = null,
        IDirectUploadFinalizer? uploadFinalizer = null,
        Services.Telemetry.IProductEventEmitter? productEvents = null)
    {
        _productEvents = productEvents;
        _uploadFinalizer = uploadFinalizer;
        _fileDeletion = fileDeletion;
        _publishModeration = publishModeration;
        _logger = logger;
        // Only the direct-upload ticket's expiry reads the clock; TimeProvider is the house seam for
        // that (see ExecutionLeaseService, CreditGate, …), so the expiry path is testable.
        _timeProvider = timeProvider ?? TimeProvider.System;
        _planGate = planGate;
        // Optional so a composition (and every existing test) that does not wire presigning still
        // builds the controller; the direct-upload endpoints then answer 501 and the multipart form
        // action is unaffected. Every shipped composition registers IDirectUploadService
        // (InferenceApiServiceCollectionExtensions); a caller that wires only the presigner and the
        // token service (the tests) gets the same service built over them.
        _directUploads = directUploads
            ?? (presignedUrls is not null && confirmationTokens is not null
                ? new DirectUploadService(db, fileStorage, presignedUrls, confirmationTokens, multipartStore, planGate, _timeProvider)
                : null);
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _fileStorage = fileStorage;
        _summarizationQueue = summarizationQueue;
        _publishEndpoint = publishEndpoint;
        _vectorSearchQueryService = vectorSearchQueryService;
        _logger = logger;
        _urlImport = urlImport;
        // One post-add path for every way a file arrives (form, direct upload, URL import, assistant).
        _fileAddedEffects = fileAddedEffects ?? new ProjectFileAddedEffects(publishEndpoint, summarizationQueue, fileStorage, activityNotifier);
        _moderationEnqueuer = moderationEnqueuer;
        // Optional so every existing test keeps compiling; DI injects the singleton. The guard's
        // cross-replica guarantee is the advisory lock, and the semaphore is its per-replica fast path.
        _storageLock = storageLock ?? new OrganizationStorageLock();
    }

    [HttpGet]
    [Filters.ConditionalGet]
    public async Task<ActionResult<List<ProjectFileResponse>>> List(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        List<ProjectFileResponse> files = await _db.ProjectFiles
            .Where(f => f.ProjectId == projectId && f.CsamStatus != CsamScanStatus.Quarantined)
            .OrderByDescending(f => f.UploadedAt)
            .Select(f => new ProjectFileResponse(
                f.Id,
                f.OriginalFileName,
                f.OriginalPath,
                f.DirectoryPath,
                f.Category,
                f.StorageKey,
                f.StorageFileName,
                f.MimeType,
                f.SizeBytes,
                f.AgentSummary,
                f.SummaryStatus.ToString(),
                f.IndexingStatus.ToString(),
                f.IndexedAt,
                f.IndexingError,
                f.UploadedAt,
                f.ModerationStatus.ToString()))
            .ToListAsync(ct);
        // A file whose bytes are not served is left out of the list like a missing one.
        var producedAt = (await _db.ProjectFiles
                .Where(f => f.ProjectId == projectId && f.OutputProducedAt != null)
                .Select(f => new { f.Id, f.OutputProducedAt })
                .ToListAsync(ct))
            .ToDictionary(f => f.Id, f => f.OutputProducedAt);
        HashSet<Guid> unavailable = await Services.Outputs.OutputGate.HeldFileIdsAsync(
            _db, projectId, files.Select(f => (f.Id, f.StorageKey, f.UploadedAt, producedAt.GetValueOrDefault(f.Id))), ct);
        if (unavailable.Count > 0) files = files.Where(f => !unavailable.Contains(f.Id)).ToList();
        return Ok(files);
    }

    // Kestrel's default MaxRequestBodySize (~30MB) and MVC's default MultipartBodyLengthLimit (128MB)
    // both reject raw source-video uploads long before either matters, so they are raised here, scoped
    // to this one endpoint, rather than globally for every controller action. This is the FORM fallback
    // route: it cannot raise the real ceiling, because nginx's `client_max_body_size` (100M by default)
    // and Cloudflare's 100 MB request-body cap sit in front of it. A file bigger than that goes through
    // the browser's presigned route (upload-url + upload-parts), which never passes a proxy — see
    // docs/direct-upload.md. The attribute therefore carries the same object ceiling as everything else,
    // so there is exactly one number in the codebase; the proxies remain the binding constraint here.
    [HttpPost]
    [RequestSizeLimit(MaxDirectUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxDirectUploadBytes)]
    public async Task<ActionResult<ProjectFileResponse>> Upload(Guid projectId, IFormFile file, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        // Plan storage quota (B8a): 413 with a machine-readable STORAGE_QUOTA_EXCEEDED body.
        if (_planGate is not null && await _planGate.CheckUploadAsync(project.OrganizationId, file.Length, ct) is { } overQuota)
            return overQuota.ToResult();

        // catch optional relative path that the client may send when uploading a folder
        string? normalizedRelativePath = null;
        if (Request.Form.TryGetValue("relativePath", out var values))
        {
            string? rawPath = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(rawPath))
            {
                try
                {
                    normalizedRelativePath = ProjectFilePath.NormalizeRelativePath(rawPath);
                }
                catch
                {
                    return BadRequest(new { error = "Invalid file path." });
                }
            }
        }

        Guid fileId = Guid.NewGuid();
        Dictionary<string, string> storageMetadata = new()
        {
            ["project-id"] = projectId.ToString(),
            ["owner-id"] = project.OwnerId.ToString(),
            ["org-id"] = project.OrganizationId.ToString(),
            ["uploaded-by"] = _currentUser.UserId.ToString(),
            ["project-file-id"] = fileId.ToString()
        };

        using Stream stream = file.OpenReadStream();
        StoredFileObject storedObject = await _fileStorage.UploadAsync(
            projectId,
            fileId,
            stream,
            Path.GetFileName(file.FileName),
            file.ContentType,
            storageMetadata,
            ct,
            category: "userFiles",
            originalPath: normalizedRelativePath ?? file.FileName);

        // R22: video/audio uploads (raw source footage for the video-editing workflow) must never
        // be handed to the text summarizer or the vector/embedding chunker — both assume text
        // content, and a large mp4/wav would otherwise be queued into them regardless of size.
        // The stored MimeType is resolved (not the raw client Content-Type) so a client that sends
        // a generic "application/octet-stream" for a known media extension doesn't defeat R22.
        string? resolvedMimeType = ResolveUploadMimeType(file.ContentType, file.FileName);
        // Issue 8: a .ts typed as a transport stream (every Windows browser does) is decided by its bytes.
        if (TransportStreamMimeSniffer.NeedsSniff(resolvedMimeType, file.FileName))
        {
            await using Stream head = file.OpenReadStream();
            resolvedMimeType = await TransportStreamMimeSniffer.ResolveAsync(resolvedMimeType, file.FileName, head, ct);
        }
        bool isMediaUpload = IsVideoOrAudioMimeType(resolvedMimeType);

        ProjectFile projectFile = new()
        {
            Id = fileId,
            ProjectId = projectId,
            OriginalFileName = storedObject.OriginalFileName,
            OriginalPath = storedObject.OriginalPath,
            DirectoryPath = storedObject.DirectoryPath,
            Category = "userFiles",
            StorageKey = storedObject.StorageKey,
            StorageFileName = storedObject.StorageFileName,
            StorageBucket = storedObject.BucketName,
            StoragePrefix = storedObject.StoragePrefix,
            StorageMetadataJson = storedObject.StorageMetadataJson,
            MimeType = resolvedMimeType ?? "application/octet-stream",
            SizeBytes = file.Length,
            SummaryStatus = isMediaUpload ? SummaryStatus.Done : SummaryStatus.Pending,
            IndexingStatus = isMediaUpload ? FileIndexingStatus.NotIndexed : FileIndexingStatus.Pending,
            UploadedAt = DateTime.UtcNow
        };

        _db.ProjectFiles.Add(projectFile);
        await _db.SaveChangesAsync(ct);

        // R22 (text is indexed and summarized; video/audio requests its derivative set instead, best
        // effort, because the row is already committed) and the activity feed: the one post-add path
        // every way of adding a file shares (ProjectFileAddedEffects). That path publishes a media
        // row's derivative request through UploadDerivativeRequest, the same helper the direct-upload
        // completion and the cloud sync use, so every upload path asks for the same thing.
        await _fileAddedEffects.RunAsync(projectFile, _currentUser.UserId, ct);

        // A scan that matched quarantined the file: the upload itself succeeded (the row and bytes
        // exist as evidence) but this file is not usable, and no detail beyond that is returned.
        if (projectFile.CsamStatus == CsamScanStatus.Quarantined) return FileUnavailable();

        await RecordFirstUploadAsync(project, ct);

        return StatusCode(201, new ProjectFileResponse(
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
            projectFile.ModerationStatus.ToString()));
    }

    /// <summary>
    /// Step 1 of a direct upload (docs/direct-upload.md): the browser asks for permission and a place
    /// to PUT, and the bytes then never cross this service. The caller must hold
    /// <see cref="ProjectPermission.Edit"/> on the project — a presigned URL is a capability to write,
    /// so this check is never relaxed — and everything else (the claimed size against
    /// <see cref="DirectUploadLimits.MaxObjectBytes"/>, the plan's quota on the claim, the name and folder
    /// path, the server-chosen key, single PUT or multipart) is <see cref="IDirectUploadService.CreateAsync"/>.
    /// The KEY is generated there and nowhere else: the client receives it and can never choose it.
    /// </summary>
    [HttpPost("upload-url")]
    public async Task<ActionResult<DirectUploadTicket>> CreateUploadUrl(
        Guid projectId,
        [FromBody] CreateDirectUploadRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        if (_directUploads is null) return DirectUploadUnavailable();

        if (request is null || request.SizeBytes < 0)
            return BadRequest(new { message = "sizeBytes is required and must not be negative." });

        try
        {
            Services.Uploads.DirectUploadTicket ticket = await _directUploads.CreateAsync(
                new DirectUploadRequest(
                    projectId,
                    project.OrganizationId,
                    CallerKey,
                    request.FileName,
                    string.IsNullOrWhiteSpace(request.RelativePath) ? null : request.RelativePath,
                    request.SizeBytes,
                    request.ContentType,
                    DirectUploadService.Category),
                ct);

            // The URL is a bearer capability to write that one key: it is returned to the caller and
            // never logged.
            return Ok(ToBrowserTicket(ticket));
        }
        catch (DirectUploadRefusedException refused)
        {
            return Refusal(refused);
        }
    }

    /// <summary>
    /// For a multipart ticket: presigned PUT URLs for up to 100 parts at a time (the client asks in
    /// batches as it goes, so no URL outlives the part it is for by hours). Each URL is valid for at most
    /// four hours and never past the ticket.
    /// </summary>
    [HttpPost("upload-parts")]
    public async Task<ActionResult<IReadOnlyList<DirectUploadPartUrl>>> CreateUploadPartUrls(
        Guid projectId,
        [FromBody] DirectUploadPartsRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        if (_directUploads is null) return DirectUploadUnavailable();
        if (request is null) return InvalidTicket("The upload ticket is required.");

        (Services.Uploads.DirectUploadTicket? ticket, ObjectResult? invalid) = VerifyEcho(projectId, new DirectUploadTicketEcho(
            request.UploadToken, ParseMode(request.Mode), projectId, request.FileId, request.Key, request.FileName, request.RelativePath,
            request.ContentType ?? string.Empty, request.SizeBytes, request.ExpiresAtUnix, request.UploadId, request.PartSizeBytes,
            request.PartCount, null, null, null, null));
        if (invalid is not null) return invalid;

        try
        {
            return Ok(await _directUploads.PresignPartsAsync(ticket!, request.PartNumbers ?? [], ct));
        }
        catch (DirectUploadRefusedException refused)
        {
            return Refusal(refused);
        }
    }

    /// <summary>
    /// Step 3 of a direct upload: the browser says the bytes are in storage, and this verifies it and
    /// records the file. Nothing here is taken on trust: the caller holds Edit (404/403); the ticket's
    /// key is exactly the key its own file id, name and folder derive (<c>UPLOAD_KEY_MISMATCH</c>), so a
    /// client cannot point finalisation at another object; the ticket's MAC verifies, bound to this
    /// caller (<c>UPLOAD_TICKET_INVALID</c>); then <see cref="IDirectUploadService.CompleteAsync"/>
    /// completes a multipart upload, HEADs the object and checks its REAL size and the quota against it
    /// — discarding the object on any refusal. The client's claimed size and content type are hints; the
    /// HEAD is the fact. Idempotent: a repeated completion answers 200 with the row the first one wrote.
    /// </summary>
    [HttpPost("upload-complete")]
    public async Task<ActionResult<ProjectFileResponse>> CompleteUpload(
        Guid projectId,
        [FromBody] CompleteDirectUploadRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        if (_directUploads is null) return DirectUploadUnavailable();

        if (request is null
            || string.IsNullOrWhiteSpace(request.UploadToken)
            || string.IsNullOrWhiteSpace(request.Key)
            || string.IsNullOrWhiteSpace(request.FileName)
            || request.FileId == Guid.Empty)
        {
            return InvalidTicket("An upload token, key, file id and file name are required.");
        }

        if (request.SizeBytes < 0) return InvalidTicket("sizeBytes must not be negative.");

        (Services.Uploads.DirectUploadTicket? ticket, ObjectResult? invalid) = VerifyEcho(projectId, new DirectUploadTicketEcho(
            request.UploadToken, ParseMode(request.Mode), projectId, request.FileId, request.Key, request.FileName, request.RelativePath,
            request.ContentType ?? string.Empty, request.SizeBytes, request.ExpiresAtUnix, request.UploadId, request.PartSizeBytes,
            request.PartCount, null, null, null, null));
        if (invalid is not null) return invalid;

        IDirectUploadFinalizer finalizer = _uploadFinalizer
            ?? new DirectUploadFinalizer(_db, _directUploads, _fileAddedEffects, _moderationEnqueuer);
        IReadOnlyList<DirectUploadCompletedPart> parts = (request.Parts ?? [])
            .Select(p => new DirectUploadCompletedPart(p.PartNumber, p.ETag))
            .ToList();
        DirectUploadFinalizeResult finished = await finalizer.FinalizeAsync(
            projectId, _currentUser.UserId, ticket!, new DirectUploadCompletion(parts), ct);

        // Only the completion that wrote the row: a replay is the same upload again.
        if (finished.Outcome == DirectUploadFinalizeOutcome.Created) await RecordFirstUploadAsync(access.Project!, ct);

        return finished.Outcome switch
        {
            DirectUploadFinalizeOutcome.Held => Services.Outputs.OutputGate.Unavailable(),
            DirectUploadFinalizeOutcome.Refused => Refusal(finished.Refused!),
            DirectUploadFinalizeOutcome.Quarantined => FileUnavailable(),
            DirectUploadFinalizeOutcome.Replayed => Ok(ToResponse(finished.File!)),
            _ => StatusCode(201, ToResponse(finished.File!))
        };
    }

    /// <summary>
    /// Imports one file from a public https URL (<see cref="IUrlImportService"/>: SSRF-guarded, capped by
    /// <c>FileImport:MaxBytes</c>/<c>FileImport:TimeoutSeconds</c>, quota-checked before and after the
    /// download). Edit and a verified email are required. 201 with the new file; a refusal is
    /// <c>{ message, code }</c> with 400 (URL/path), 413 (too large, quota) or 422 (upstream failure,
    /// timeout) — never 502/504, which the CDN would swap for its own error page.
    /// </summary>
    [HttpPost("import-url")]
    public async Task<ActionResult<ProjectFileResponse>> ImportFromUrl(
        Guid projectId,
        [FromBody] ImportProjectFileFromUrlRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        if (EmailVerificationGate.Check(_currentUser) is { } unverified) return unverified;

        if (_urlImport is null)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new
            {
                code = "IMPORT_DISABLED",
                message = "This deployment does not import files from URLs."
            });
        }

        UrlImportResult result = await _urlImport.ImportAsync(
            access.Project!, _currentUser.UserId, request?.Url ?? string.Empty, request?.FileName, request?.DirectoryPath, ct);

        if (result.Refusal is { } refusal)
        {
            return StatusCode(refusal.StatusCode, new
            {
                message = refusal.Message,
                code = refusal.Code,
                upstreamStatus = refusal.UpstreamStatus,
                limit = refusal.LimitBytes,
                current = refusal.UsedBytes
            });
        }

        // The import's post-add path (inside UrlImportService) already scanned it.
        if (result.File!.CsamStatus == CsamScanStatus.Quarantined) return FileUnavailable();

        await RecordFirstUploadAsync(access.Project!, ct);

        return StatusCode(201, ToResponse(result.File!));
    }

    /// <summary>The bundled first-party sample clips, by key (embedded resources, see Assets/samples/README.md).</summary>
    private static readonly IReadOnlyDictionary<string, string> SampleClips =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["first-film"] = "Samples.first-film.mp4" };

    internal const string SampleClipFileName = "Sample clip.mp4";

    /// <summary>
    /// Copies a bundled, generated sample clip into the project so a new user can run a workflow without
    /// uploading anything. Edit access and a verified email, like import-url; the plan's storage quota applies.
    /// 201 with the new file; idempotent: a repeat answers 200 with the existing file and stores nothing.
    /// An unknown key is 400. The row goes through the same post-add path as every upload.
    /// </summary>
    [HttpPost("sample")]
    public async Task<ActionResult<ProjectFileResponse>> AddSample(
        Guid projectId,
        [FromBody] AddSampleFileRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        if (EmailVerificationGate.Check(_currentUser) is { } unverified) return unverified;
        Project project = access.Project!;

        if (request?.SampleKey is not { } key || !SampleClips.TryGetValue(key, out string? resourceName))
            return BadRequest(new { code = "SAMPLE_UNKNOWN", message = "Unknown sample key." });

        ProjectFile? existing = await _db.ProjectFiles
            .Where(f => f.ProjectId == projectId && f.OriginalFileName == SampleClipFileName
                && (f.DirectoryPath == null || f.DirectoryPath == ""))
            .OrderBy(f => f.UploadedAt)
            .FirstOrDefaultAsync(ct);
        if (existing is not null && existing.CsamStatus != CsamScanStatus.Quarantined)
            return Ok(ToResponse(existing));

        await using Stream? resource = typeof(ProjectFilesController).Assembly.GetManifestResourceStream(resourceName);
        if (resource is null)
            return StatusCode(StatusCodes.Status500InternalServerError, new { code = "SAMPLE_MISSING", message = "The sample is not available." });
        using MemoryStream bytes = new();
        await resource.CopyToAsync(bytes, ct);
        bytes.Position = 0;
        // Read the size before the upload: the storage client disposes the stream it is given.
        long sizeBytes = bytes.Length;

        if (_planGate is not null && await _planGate.CheckUploadAsync(project.OrganizationId, sizeBytes, ct) is { } overQuota)
            return overQuota.ToResult();

        Guid fileId = Guid.NewGuid();
        Dictionary<string, string> storageMetadata = new()
        {
            ["project-id"] = projectId.ToString(),
            ["owner-id"] = project.OwnerId.ToString(),
            ["org-id"] = project.OrganizationId.ToString(),
            ["uploaded-by"] = _currentUser.UserId.ToString(),
            ["project-file-id"] = fileId.ToString()
        };
        StoredFileObject stored = await _fileStorage.UploadAsync(
            projectId, fileId, bytes, SampleClipFileName, "video/mp4", storageMetadata, ct,
            category: "userFiles", originalPath: SampleClipFileName);

        ProjectFile projectFile = new()
        {
            Id = fileId,
            ProjectId = projectId,
            OriginalFileName = stored.OriginalFileName,
            OriginalPath = stored.OriginalPath,
            DirectoryPath = stored.DirectoryPath,
            Category = "userFiles",
            StorageKey = stored.StorageKey,
            StorageFileName = stored.StorageFileName,
            StorageBucket = stored.BucketName,
            StoragePrefix = stored.StoragePrefix,
            StorageMetadataJson = stored.StorageMetadataJson,
            MimeType = "video/mp4",
            SizeBytes = sizeBytes,
            SummaryStatus = SummaryStatus.Done,
            IndexingStatus = FileIndexingStatus.NotIndexed,
            UploadedAt = DateTime.UtcNow
        };
        _db.ProjectFiles.Add(projectFile);
        await _db.SaveChangesAsync(ct);

        await _fileAddedEffects.RunAsync(projectFile, _currentUser.UserId, ct);
        if (projectFile.CsamStatus == CsamScanStatus.Quarantined) return FileUnavailable();

        return StatusCode(201, ToResponse(projectFile));
    }

    /// <summary>
    /// product-analytics: the caller's first upload (<c>activation.file_uploaded</c>, once per user), written
    /// by the three routes a person adds a file through: the form upload, a direct upload's completion and
    /// an import from a link. It is an event, not a read of <c>project_files</c>, because that table cannot
    /// say it: a row names no uploader, its <c>uploaded_at</c> is rewritten by a move, and deleting the
    /// file deletes the row. Called only after the file is usable (never for a quarantined one), and best
    /// effort: the emitter swallows its own failures.
    /// </summary>
    private Task RecordFirstUploadAsync(Project project, CancellationToken ct) =>
        _productEvents is null
            ? Task.CompletedTask
            : _productEvents.FirstAsync(
                _currentUser.UserId, project.OrganizationId, Services.Telemetry.ProductEventNames.FileUploaded, ct);

    /// <summary>
    /// The one answer for a file whose scan matched: generic by design. It says nothing about why
    /// beyond "cannot be used", and the bytes stay quarantined as evidence.
    /// </summary>
    internal static ObjectResult FileUnavailable() =>
        new(new { code = "FILE_UNAVAILABLE", message = "This file cannot be used." })
        {
            StatusCode = StatusCodes.Status422UnprocessableEntity
        };

    /// <summary>
    /// True when a move or rename of <paramref name="files"/> must be refused: one of them is an engine output (it carries
    /// <see cref="ProjectFile.OutputProducedAt"/>) that may predate the project's quarantine marker, or the marker cannot
    /// be read. The stamp survives a move, so a move cannot make an old output look new; refusing the request keeps the
    /// engine's view of the row what it was. No marker: false. Cancellation propagates.
    /// </summary>
    private async Task<bool> MovesOldOutputAsync(Guid projectId, IEnumerable<ProjectFile> files, CancellationToken ct)
    {
        try
        {
            DateTime? marker = await _db.Projects.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.Id == projectId)
                .Select(p => p.ContentQuarantinedAt)
                .FirstOrDefaultAsync(ct);
            return marker is not null
                && files.Any(f => f.OutputProducedAt is not null && ContentQuarantineRule.Predates(marker, f.OutputProducedAt));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>The same generic body for a folder operation refused because it holds quarantined evidence.</summary>
    internal static ObjectResult FileUnavailableConflict() =>
        new(new { code = "FILE_UNAVAILABLE", message = "This file cannot be used." })
        {
            StatusCode = StatusCodes.Status409Conflict
        };

    /// <summary>
    /// Cancels a direct upload: aborts its multipart upload (freeing the stored parts) or deletes the
    /// object a single PUT left. Best effort and idempotent; never deletes an object a file already uses.
    /// </summary>
    [HttpPost("upload-abort")]
    public async Task<IActionResult> AbortUpload(
        Guid projectId,
        [FromBody] AbortDirectUploadRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        if (_directUploads is null) return DirectUploadUnavailable();
        if (request is null) return InvalidTicket("The upload ticket is required.");

        (Services.Uploads.DirectUploadTicket? ticket, ObjectResult? invalid) = VerifyEcho(projectId, new DirectUploadTicketEcho(
            request.UploadToken, ParseMode(request.Mode), projectId, request.FileId, request.Key, request.FileName, request.RelativePath,
            request.ContentType ?? string.Empty, request.SizeBytes, request.ExpiresAtUnix, request.UploadId, request.PartSizeBytes,
            request.PartCount, null, null, null, null));
        if (invalid is not null) return invalid;

        await _directUploads.AbortAsync(ticket!, ct);
        return NoContent();
    }

    /// <summary>
    /// Moves one object. When the object has an ETag, the copy is conditional on it: only then is the version
    /// read before the move trusted for the new key (the bytes copied are the bytes that were judged). If
    /// the object changed meanwhile, or the store cannot condition the copy, the object is still moved but
    /// nothing is vouched for and the verdict is dropped, so the new key reads pending.
    /// </summary>
    private async Task<(string? Version, bool Verified)> MoveObjectAsync(
        Guid projectId, string oldKey, string newKey, CancellationToken ct)
    {
        string? version = _publishModeration is null
            ? null
            : await _publishModeration.ReadObjectVersionAsync(projectId, oldKey, ct);
        int split = version?.LastIndexOf(':') ?? -1;
        string? etag = version is not null && split > 0 && !version.StartsWith("len:", StringComparison.Ordinal)
            ? version[..split]
            : null;

        if (etag is not null && await _fileStorage.MoveIfMatchAsync(projectId, oldKey, newKey, etag, ct))
            return (version, true);

        await _fileStorage.MoveAsync(projectId, oldKey, newKey, ct);
        return (version, false);
    }

    /// <summary>Names the file that stopped a move and says plainly that nothing moved and the rest can go once it is checked.</summary>
    private ObjectResult NamedRefusal(PublishModerationOutcome refused, ProjectFile file, int total)
    {
        string name = string.IsNullOrWhiteSpace(file.OriginalFileName) ? "A file" : $"\"{file.OriginalFileName}\"";
        string message = refused.Code == "CONTENT_BLOCKED"
            ? $"{name} cannot be moved: {refused.Message}"
            : total > 1
                ? $"{name} has not been checked yet, so nothing was moved. The rest can be moved once it has been checked."
                : $"{name} has not been checked yet, so it was not moved. It can be moved once it has been checked.";
        return (refused with { Message = message }).ToRefusal(this);
    }

    /// <summary>
    /// A moved object keeps its verdict when the bytes are the ones that were judged: the row follows
    /// the key and takes the version read after the server's own move (it made that copy). Anything else
    /// (no released verdict, or a verdict for other bytes) drops the row.
    /// </summary>
    private async Task CarryVerdictAsync(Guid projectId, string oldKey, string newKey, string? versionBefore, CancellationToken ct)
    {
        if (_publishModeration is null)
        {
            await ForgetVerdictsAsync(projectId, [oldKey], ct);
            return;
        }

        await _publishModeration.MoveVerdictAsync(projectId, oldKey, newKey, versionBefore, ct);
    }

    /// <summary>The stored moderation verdict belongs to the object at its key: it goes when the object moves or is deleted.</summary>
    private async Task ForgetVerdictsAsync(Guid projectId, IReadOnlyCollection<string> keys, CancellationToken ct) =>
        _db.OutputModerationVerdicts.RemoveRange(await _db.OutputModerationVerdicts
            .Where(v => v.ProjectId == projectId && keys.Contains(v.StorageKey)).ToListAsync(ct));

    [HttpGet("{fileId:guid}/download")]
    public async Task<IActionResult> Download(Guid projectId, Guid fileId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        ProjectFile? file = await _db.ProjectFiles.FirstOrDefaultAsync(f => f.Id == fileId && f.ProjectId == projectId, ct);
        if (file == null) return NotFound();
        if (file.CsamStatus == CsamScanStatus.Quarantined
            || file.ModerationStatus == FileModerationStatus.Quarantined) return NotFound();
        if (await Services.Outputs.OutputGate.IsFileHeldAsync(_db, file, ct)) return Services.Outputs.OutputGate.Unavailable();

        // The file may share the key of a step output: then the stored verdict decides, as for download.
        if (_publishModeration is not null
            && await _publishModeration.GateStoredFileAsync(projectId, file.StorageKey, ct) is { } refused)
            return refused.ToRefusal(this);

        Stream stream = await _fileStorage.DownloadAsync(projectId, file.StorageKey, ct);
        ServedContent served = SafeContentPolicy.Decide(file.MimeType, file.OriginalFileName);
        SafeContentPolicy.ApplySecurityHeaders(Response);
        Response.Headers["Content-Disposition"] = SafeContentPolicy.BuildContentDisposition(false, file.OriginalFileName);
        return File(stream, served.ContentType);
    }

    [HttpGet("{fileId:guid}/content")]
    public async Task<ActionResult<ProjectFileContentResponse>> GetContent(Guid projectId, Guid fileId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        ProjectFile? file = await _db.ProjectFiles.FirstOrDefaultAsync(f => f.Id == fileId && f.ProjectId == projectId, ct);
        if (file == null) return NotFound();
        if (file.CsamStatus == CsamScanStatus.Quarantined
            || file.ModerationStatus == FileModerationStatus.Quarantined) return NotFound();
        if (await Services.Outputs.OutputGate.IsFileHeldAsync(_db, file, ct)) return Services.Outputs.OutputGate.Unavailable();

        // A .cube LUT is read here by the editor (viewing LUT, grade LUT check) whatever MIME it was uploaded with
        // (browsers send application/octet-stream for it). Read-only: it is NOT in ProjectFileTextPolicy, so the PUT
        // below, the assistant, the brief resolver and the engine workspace still treat it as not text.
        bool isLut = IsCubeLut(file.OriginalFileName);
        if (!isLut && !IsLikelyText(file.MimeType, file.OriginalFileName))
            return Conflict(new { message = "File is not a text-editable asset." });
        if (isLut && file.SizeBytes > CubeLut.MaxBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { code = "LUT_TOO_LARGE", message = $"A LUT file can be at most {CubeLut.MaxBytes} bytes." });

        // The file may share the key of a step output: then the stored verdict decides, as for download.
        if (_publishModeration is not null
            && await _publishModeration.GateStoredFileAsync(projectId, file.StorageKey, ct) is { } refused)
            return refused.ToRefusal(this);

        await using Stream stream = await _fileStorage.DownloadAsync(projectId, file.StorageKey, ct);
        using StreamReader reader = new(stream);
        string content = await reader.ReadToEndAsync(ct);

        return Ok(new ProjectFileContentResponse(
            file.Id,
            file.OriginalFileName,
            file.OriginalPath,
            file.MimeType,
            content,
            file.UploadedAt));
    }

    [HttpPut("{fileId:guid}/content")]
    public async Task<ActionResult<ProjectFileResponse>> UpdateContent(
        Guid projectId,
        Guid fileId,
        [FromBody] UpdateProjectFileContentRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        ProjectFile? file = await _db.ProjectFiles.FirstOrDefaultAsync(f => f.Id == fileId && f.ProjectId == projectId, ct);
        if (file == null) return NotFound();
        if (file.CsamStatus == CsamScanStatus.Quarantined
            || file.ModerationStatus == FileModerationStatus.Quarantined) return NotFound();
        if (await Services.Outputs.OutputGate.IsFileHeldAsync(_db, file, ct)) return Services.Outputs.OutputGate.Unavailable();

        if (!IsLikelyText(file.MimeType, file.OriginalFileName))
            return Conflict(new { message = "File is not a text-editable asset." });

        // An output's type is fixed: changing it would route the same bytes down another moderation
        // pipeline. New bytes at the same key read as a new version and are judged again.
        if (ProjectOutputKeys.IsAddressableOutput(projectId, file.StorageKey)
            && !string.IsNullOrWhiteSpace(request.ContentType)
            && !string.Equals(request.ContentType.Trim(), file.MimeType, StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(new
            {
                message = "The content type of a generated output cannot be changed.",
                code = "OUTPUT_CONTENT_TYPE_LOCKED"
            });
        }

        // An avatar's reference keeps the bytes its consent was attested for: a rewrite in place is refused like a delete.
        if (await ReelBolt.Shared.Avatars.AvatarFileUse.FirstUseAsync(_db, projectId, [file.Id], ct) is { } avatarUse)
            return Conflict(avatarUse.ToBody());

        // Plan storage quota on the NET growth: a rewrite that does not grow the file always passes.
        long newSizeBytes = System.Text.Encoding.UTF8.GetByteCount(request.Content ?? string.Empty);
        long growthBytes = newSizeBytes - ReelBolt.Shared.Billing.ProjectFileQuota.CountedSize(file);
        if (growthBytes > 0 && _planGate is not null && await _planGate.CheckUploadAsync(project.OrganizationId, growthBytes, ct) is { } overQuota)
            return overQuota.ToResult();

        string normalizedPath = ProjectFilePath.NormalizeRelativePath(file.OriginalPath ?? file.OriginalFileName);
        string contentType = string.IsNullOrWhiteSpace(request.ContentType) ? file.MimeType : request.ContentType;
        // Issue 8: a text edit of a .ts never stores a transport-stream type.
        contentType = TransportStreamMimeSniffer.Resolve(
            contentType, file.OriginalFileName, System.Text.Encoding.UTF8.GetBytes(request.Content ?? string.Empty)) ?? contentType;

        Dictionary<string, string> metadata = new()
        {
            ["project-id"] = projectId.ToString(),
            ["owner-id"] = project.OwnerId.ToString(),
            ["org-id"] = project.OrganizationId.ToString(),
            ["uploaded-by"] = _currentUser.UserId.ToString(),
            ["project-file-id"] = fileId.ToString(),
            ["updated-by"] = _currentUser.UserId.ToString()
        };

        await using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(request.Content));
        StoredFileObject storedObject = await _fileStorage.UploadAsync(
            projectId,
            fileId,
            stream,
            file.OriginalFileName,
            contentType,
            metadata,
            ct,
            category: file.Category,
            originalPath: normalizedPath);

        if (!string.Equals(file.StorageKey, storedObject.StorageKey, StringComparison.Ordinal))
            await _fileStorage.DeleteAsync(projectId, file.StorageKey, ct);

        file.OriginalFileName = storedObject.OriginalFileName;
        file.OriginalPath = storedObject.OriginalPath;
        file.DirectoryPath = storedObject.DirectoryPath;
        file.StorageKey = storedObject.StorageKey;
        file.StorageFileName = storedObject.StorageFileName;
        file.StorageBucket = storedObject.BucketName;
        file.StoragePrefix = storedObject.StoragePrefix;
        file.StorageMetadataJson = storedObject.StorageMetadataJson;
        file.MimeType = contentType;
        file.SizeBytes = System.Text.Encoding.UTF8.GetByteCount(request.Content);
        // Rewritten bytes are not the quota-exempt library sound any more.
        if (file.SourceKind == ReelBolt.Shared.Billing.ProjectFileQuota.SfxLibrarySourceKind) file.SourceKind = null;
        file.SummaryStatus = SummaryStatus.Pending;
        file.IndexingStatus = FileIndexingStatus.Pending;
        file.IndexedAt = null;
        file.IndexingError = null;
        // A rewrite replaces the bytes, so the earlier verdict no longer describes this row. The CSAM
        // verdict is reset too and the sweeper re-scans before general moderation may read the bytes.
        file.ModerationStatus = FileModerationStatus.Unmoderated;
        file.CsamStatus = CsamScanStatus.Pending;
        file.CsamScannedAt = null;
        file.UploadedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        if (CsamScanGate.AllowsModeration(file))
            _moderationEnqueuer?.TryQueue(file.Id);

        await _publishEndpoint.Publish(new ProjectFileIndexingRequested
        {
            ProjectId = projectId,
            FileId = file.Id,
            Operation = "Upsert",
            RequestedAt = DateTime.UtcNow
        }, ct);

        // Non-blocking, like Upload: the row is Pending, and the sweeper covers a full queue.
        _summarizationQueue.TryQueue(new FileSummarizationTask(file.Id));

        return Ok(new ProjectFileResponse(
            file.Id,
            file.OriginalFileName,
            file.OriginalPath,
            file.DirectoryPath,
            file.Category,
            file.StorageKey,
            file.StorageFileName,
            file.MimeType,
            file.SizeBytes,
            file.AgentSummary,
            file.SummaryStatus.ToString(),
                file.IndexingStatus.ToString(),
                file.IndexedAt,
                file.IndexingError,
            file.UploadedAt,
            file.ModerationStatus.ToString()));
    }

    /// <summary>Longest file name a rename accepts, in characters (the common file-system limit).</summary>
    internal const int MaxFileNameLength = 255;

    /// <summary>
    /// Renames one file in place: only its display name (and the display path derived from it)
    /// changes. The storage object is NOT moved — its key is id-based, so every step result, render
    /// and workflow config that already points at it keeps working. Before this existed a mislabelled
    /// clip had to be deleted and re-uploaded. 400 for an empty name, a name with a path separator or
    /// control character, "." / "..", or one longer than <see cref="MaxFileNameLength"/>; 409 when
    /// another file in the same folder already has that name (case-insensitively, so two files that
    /// look the same in the list cannot coexist); 404/403 like every owner-scoped file endpoint.
    /// </summary>
    [HttpPatch("{fileId:guid}")]
    public async Task<ActionResult<ProjectFileResponse>> Rename(
        Guid projectId,
        Guid fileId,
        [FromBody] RenameProjectFileRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        ProjectFile? file = await _db.ProjectFiles.FirstOrDefaultAsync(f => f.Id == fileId && f.ProjectId == projectId, ct);
        if (file == null || file.CsamStatus == CsamScanStatus.Quarantined) return NotFound();

        string? invalid = ValidateFileName(request?.FileName, out string fileName);
        if (invalid is not null)
            return BadRequest(new { message = invalid });

        if (await MovesOldOutputAsync(projectId, [file], ct))
            return FileUnavailableConflict();

        // A file that is not served is not renamed, and nothing about it (summary or key) is returned.
        if (await Services.Outputs.OutputGate.IsFileHeldAsync(_db, file, ct))
            return Services.Outputs.OutputGate.Unavailable();

        if (!string.Equals(file.OriginalFileName, fileName, StringComparison.Ordinal))
        {
            string lowered = fileName.ToLower();
            string? directory = file.DirectoryPath;
            bool clash = await _db.ProjectFiles.AnyAsync(f =>
                f.ProjectId == projectId &&
                f.Id != fileId &&
                f.Category == file.Category &&
                f.DirectoryPath == directory &&
                f.OriginalFileName.ToLower() == lowered, ct);
            if (clash)
                return Conflict(new { message = $"Another file in this folder is already called \"{fileName}\"." });

            file.OriginalFileName = fileName;
            file.OriginalPath = ProjectFilePath.CombineDirectoryAndFileName(file.DirectoryPath, fileName);
            await _db.SaveChangesAsync(ct);

            // The search index stores each chunk's file name and path; refresh it for text files
            // (R22: never for video/audio, which are not indexed at all).
            if (!IsVideoOrAudioMimeType(file.MimeType))
            {
                await _publishEndpoint.Publish(new ProjectFileIndexingRequested
                {
                    ProjectId = projectId,
                    FileId = file.Id,
                    Operation = "Upsert",
                    RequestedAt = DateTime.UtcNow
                }, ct);
            }
        }

        return Ok(new ProjectFileResponse(
            file.Id,
            file.OriginalFileName,
            file.OriginalPath,
            file.DirectoryPath,
            file.Category,
            file.StorageKey,
            file.StorageFileName,
            file.MimeType,
            file.SizeBytes,
            file.AgentSummary,
            file.SummaryStatus.ToString(),
            file.IndexingStatus.ToString(),
            file.IndexedAt,
            file.IndexingError,
            file.UploadedAt,
            file.ModerationStatus.ToString()));
    }

    /// <summary>Returns why <paramref name="raw"/> is not a usable file name, or null (and the trimmed name) when it is.</summary>
    internal static string? ValidateFileName(string? raw, out string fileName)
    {
        fileName = (raw ?? string.Empty).Trim();
        if (fileName.Length == 0)
            return "File name is required.";
        if (fileName.Length > MaxFileNameLength)
            return $"File name must be at most {MaxFileNameLength} characters.";
        if (fileName.IndexOfAny(['/', '\\']) >= 0)
            return "File name cannot contain / or \\. Use the move action to put a file in another folder.";
        if (fileName is "." or "..")
            return "File name cannot be \".\" or \"..\".";
        if (fileName.Any(char.IsControl))
            return "File name cannot contain control characters.";
        return null;
    }

    [HttpPost("move")]
    public async Task<ActionResult<List<ProjectFileResponse>>> Move(
        Guid projectId,
        [FromBody] MoveProjectFilesRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        if (request.FileIds == null || request.FileIds.Count == 0)
            return BadRequest(new { message = "At least one fileId is required." });

        string? targetDirectoryPath = ProjectFilePath.NormalizeDirectoryPath(request.TargetDirectoryPath);
        List<ProjectFile> files = await _db.ProjectFiles
            .Where(f => f.ProjectId == projectId && request.FileIds.Contains(f.Id))
            .ToListAsync(ct);

        if (files.Count != request.FileIds.Count)
            return NotFound(new { message = "One or more files were not found in project scope." });

        // Moving a quarantined row rewrites StorageKey, divorcing it from CsamQuarantineKey and
        // letting a later purge delete evidence. Refuse the whole request.
        if (files.Any(f => f.CsamStatus == CsamScanStatus.Quarantined || f.CsamQuarantineKey is not null))
            return FileUnavailableConflict();
        if ((await Services.Outputs.OutputGate.HeldFileIdsAsync(_db, projectId, files, ct)).Count > 0)
            return Services.Outputs.OutputGate.Unavailable();

        // An old engine output keeps its place: a move rewrites the row's time, category and key, and is refused.
        if (await MovesOldOutputAsync(projectId, files, ct))
            return FileUnavailableConflict();

        // An output leaves its key only when it has a current Allow or Review verdict; checked for every
        // file first, so a refusal moves nothing.
        if (_publishModeration is not null)
        {
            foreach (ProjectFile file in files)
            {
                string category = string.IsNullOrWhiteSpace(request.TargetCategory) ? file.Category : request.TargetCategory;
                string fileName = string.IsNullOrWhiteSpace(file.StorageFileName)
                    ? ProjectFilePath.BuildStorageFileName(file.Id, file.OriginalFileName)
                    : file.StorageFileName;
                if (ProjectFilePath.BuildStorageKey(projectId, category, targetDirectoryPath, fileName) == file.StorageKey) continue;
                if (await _publishModeration.GateStoredCopyAsync(projectId, file.StorageKey, ct) is { } refused)
                    return NamedRefusal(refused, file, files.Count);
            }
        }

        List<ProjectFile> moved = [];
        try
        {
            foreach (ProjectFile file in files)
            {
                string targetCategory = string.IsNullOrWhiteSpace(request.TargetCategory) ? file.Category : request.TargetCategory;
                if (targetCategory != "userFiles" && targetCategory != "agentFiles" && targetCategory != "outputFiles")
                    return BadRequest(new { message = "TargetCategory must be userFiles, agentFiles, or outputFiles." });

                string storageFileName = string.IsNullOrWhiteSpace(file.StorageFileName)
                    ? ProjectFilePath.BuildStorageFileName(file.Id, file.OriginalFileName)
                    : file.StorageFileName;

                string nextOriginalPath = ProjectFilePath.CombineDirectoryAndFileName(targetDirectoryPath, file.OriginalFileName);
                string nextStorageKey = ProjectFilePath.BuildStorageKey(projectId, targetCategory, targetDirectoryPath, storageFileName);

                (string? versionBefore, bool verified) = await MoveObjectAsync(projectId, file.StorageKey, nextStorageKey, ct);
                await CarryVerdictAsync(projectId, file.StorageKey, nextStorageKey, verified ? versionBefore : null, ct);
                await PictureSourceCopies.RepointMovedAsync(_db, projectId, file.StorageKey, nextStorageKey, ct);

                file.Category = targetCategory;
                file.DirectoryPath = targetDirectoryPath;
                file.OriginalPath = nextOriginalPath;
                file.StorageFileName = storageFileName;
                file.StorageKey = nextStorageKey;
                file.StoragePrefix = ProjectFilePath.BuildStoragePrefix(projectId, targetCategory);
                file.IndexingStatus = FileIndexingStatus.Pending;
                file.IndexedAt = null;
                file.IndexingError = null;
                file.UploadedAt = DateTime.UtcNow;
                // Saved as each object moves: a storage failure on a later file leaves the earlier rows true, so a retry continues from the real state.
                await _db.SaveChangesAsync(ct);
                moved.Add(file);
            }

        }
        catch
        {
            // A storage failure part-way: the files already moved are real, so they are still indexed under their new keys.
            await PublishMovedIndexingAsync(projectId, moved, skipMedia: false, swallow: true, ct);
            throw;
        }

        await PublishMovedIndexingAsync(projectId, moved, skipMedia: false, swallow: false, ct);

        return Ok(files.Select(f => new ProjectFileResponse(
            f.Id,
            f.OriginalFileName,
            f.OriginalPath,
            f.DirectoryPath,
            f.Category,
            f.StorageKey,
            f.StorageFileName,
            f.MimeType,
            f.SizeBytes,
            f.AgentSummary,
            f.SummaryStatus.ToString(),
                f.IndexingStatus.ToString(),
                f.IndexedAt,
                f.IndexingError,
            f.UploadedAt,
            f.ModerationStatus.ToString())).ToList());
    }

    /// <summary>Asks for the indexing of files whose objects are now at their new keys. When a later move failed, a failed publish must not hide that failure.</summary>
    private async Task PublishMovedIndexingAsync(
        Guid projectId, IReadOnlyList<ProjectFile> moved, bool skipMedia, bool swallow, CancellationToken ct)
    {
        foreach (ProjectFile file in moved)
        {
            if (skipMedia && IsVideoOrAudioMimeType(file.MimeType)) continue;

            try
            {
                await _publishEndpoint.Publish(new ProjectFileIndexingRequested
                {
                    ProjectId = projectId,
                    FileId = file.Id,
                    Operation = "Upsert",
                    RequestedAt = DateTime.UtcNow
                }, ct);
            }
            catch (Exception ex) when (swallow && ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Could not ask for the indexing of a file that was already moved");
            }
        }
    }

    [HttpPost("folders")]
    public async Task<IActionResult> CreateFolder(Guid projectId, [FromBody] FolderPathRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        string normalizedPath = ProjectFilePath.NormalizeRelativePath(request.Path);
        return StatusCode(201, new
        {
            path = normalizedPath,
            materialized = false,
            message = "Folder paths are logical and become materialized when files are uploaded or moved into them."
        });
    }

    [HttpPatch("folders")]
    public async Task<IActionResult> RenameFolder(Guid projectId, [FromBody] RenameFolderRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        string sourcePath = ProjectFilePath.NormalizeRelativePath(request.SourcePath);
        string targetPath = ProjectFilePath.NormalizeRelativePath(request.TargetPath);

        List<ProjectFile> files = await _db.ProjectFiles
            .Where(f => f.ProjectId == projectId &&
                        f.DirectoryPath != null &&
                        (f.DirectoryPath == sourcePath || f.DirectoryPath.StartsWith(sourcePath + "/")))
            .ToListAsync(ct);

        if (files.Count == 0)
            return NotFound(new { message = "No files found in source folder." });

        // Moving a quarantined object would break its evidence key; refuse the rename rather than
        // move bytes that are under legal hold.
        if (files.Any(f => f.CsamStatus == CsamScanStatus.Quarantined))
            return FileUnavailableConflict();
        if ((await Services.Outputs.OutputGate.HeldFileIdsAsync(_db, projectId, files, ct)).Count > 0)
            return Services.Outputs.OutputGate.Unavailable();

        if (await MovesOldOutputAsync(projectId, files, ct))
            return FileUnavailableConflict();

        if (_publishModeration is not null && !string.Equals(sourcePath, targetPath, StringComparison.Ordinal))
        {
            foreach (ProjectFile file in files)
            {
                if (await _publishModeration.GateStoredCopyAsync(projectId, file.StorageKey, ct) is { } refused)
                    return NamedRefusal(refused, file, files.Count);
            }
        }

        List<ProjectFile> moved = [];
        try
        {
            foreach (ProjectFile file in files)
            {
                string oldDirectory = file.DirectoryPath!;
                string suffix = oldDirectory.Length == sourcePath.Length ? string.Empty : oldDirectory[sourcePath.Length..];
                string nextDirectory = string.IsNullOrEmpty(suffix)
                    ? targetPath
                    : $"{targetPath}{suffix}";

                string storageFileName = string.IsNullOrWhiteSpace(file.StorageFileName)
                    ? ProjectFilePath.BuildStorageFileName(file.Id, file.OriginalFileName)
                    : file.StorageFileName;

                string nextStorageKey = ProjectFilePath.BuildStorageKey(projectId, file.Category, nextDirectory, storageFileName);
                (string? versionBefore, bool verified) = await MoveObjectAsync(projectId, file.StorageKey, nextStorageKey, ct);
                await CarryVerdictAsync(projectId, file.StorageKey, nextStorageKey, verified ? versionBefore : null, ct);
                await PictureSourceCopies.RepointMovedAsync(_db, projectId, file.StorageKey, nextStorageKey, ct);

                file.DirectoryPath = nextDirectory;
                file.OriginalPath = ProjectFilePath.CombineDirectoryAndFileName(nextDirectory, file.OriginalFileName);
                file.StorageFileName = storageFileName;
                file.StorageKey = nextStorageKey;
                file.StoragePrefix = ProjectFilePath.BuildStoragePrefix(projectId, file.Category);
                // R22: a moved video/audio file must not be re-queued for vector indexing —
                // ProjectFileIndexingConsumer reads the object as UTF8 text before chunking it,
                // which is exactly the "binary handed to the text pipeline" failure mode R22 exists
                // to prevent, just reached via Move instead of Upload.
                if (!IsVideoOrAudioMimeType(file.MimeType))
                {
                    file.IndexingStatus = FileIndexingStatus.Pending;
                    file.IndexedAt = null;
                    file.IndexingError = null;
                }
                file.UploadedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                moved.Add(file);
            }

        }
        catch
        {
            await PublishMovedIndexingAsync(projectId, moved, skipMedia: true, swallow: true, ct);
            throw;
        }

        await PublishMovedIndexingAsync(projectId, moved, skipMedia: true, swallow: false, ct);

        return Ok(new { sourcePath, targetPath, movedFiles = files.Count });
    }

    [HttpDelete("folders")]
    public async Task<IActionResult> DeleteFolder(Guid projectId, [FromBody] FolderDeleteRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        string normalizedPath = ProjectFilePath.NormalizeRelativePath(request.Path);

        List<ProjectFile> files = await _db.ProjectFiles
            .Where(f => f.ProjectId == projectId &&
                        f.DirectoryPath != null &&
                        (f.DirectoryPath == normalizedPath || f.DirectoryPath.StartsWith(normalizedPath + "/")))
            .ToListAsync(ct);

        if (files.Count == 0)
            return NotFound(new { message = "No files found in folder." });

        // Quarantined evidence is never destroyed by a folder operation, and a cascade through the
        // file row would take its audit with it. Refuse the whole folder.
        if (files.Any(f => f.CsamStatus == CsamScanStatus.Quarantined))
            return FileUnavailableConflict();
        if ((await Services.Outputs.OutputGate.HeldFileIdsAsync(_db, projectId, files, ct)).Count > 0)
            return Services.Outputs.OutputGate.Unavailable();
        // A reference of an avatar cannot disappear from under it (the link is ON DELETE NO ACTION): the whole folder
        // is refused, naming the avatar, until the file is removed from the avatar or the avatar is deleted.
        if (await ReelBolt.Shared.Avatars.AvatarFileUse.FirstUseAsync(_db, projectId, [.. files.Select(f => f.Id)], ct) is { } avatarUse)
            return Conflict(avatarUse.ToBody());

        if (!request.Recursive && files.Any(f => !string.Equals(f.DirectoryPath, normalizedPath, StringComparison.Ordinal)))
            return Conflict(new { message = "Folder is not empty. Retry with recursive=true." });

        List<Guid> deletedFileIds = files.Select(f => f.Id).ToList();

        // D10: pictures using any of these photos get their own copies before the objects go; a user's delete
        // whose copies would not fit the plan's storage is refused before anything is copied (409). S-09: the
        // preflight, the copy and the record are one critical section under the organization's storage guard,
        // whose cross-replica guarantee is the advisory lock, so parallel deletes cannot each pass a check only
        // one of them fits. A refusal returns before the commit and the guard rolls back.
        await using OrganizationStorageGuard storageGuard = await _storageLock.AcquireAsync(_db, project.OrganizationId, ct);
        if (await PictureSourceCopies.CheckCopiesFitAsync(_db, _planGate, project.OrganizationId, projectId, files, folder: true, ct) is { } overQuota)
            return Conflict(overQuota.ToBody());
        await PictureSourceCopies.CopyBeforeDeleteAsync(
            _db, _fileStorage, projectId, files, _logger, ct,
            async (key, token) => _publishModeration is null || await _publishModeration.GateStoredCopyAsync(projectId, key, token) is null);

        // The rows and a durable deletion mark per object go in ONE transaction, then the objects go. A commit
        // failure rolls the layer repointing back with the original still in place, so the delete is retryable
        // (QA-SQC-1); and if an object delete then fails, its mark is already durable, so the retention sweep
        // finishes the job instead of the success answer hiding surviving bytes.
        List<StorageRetentionMark> marks = MarkForDeletion(files, project.OrganizationId, DateTime.UtcNow);
        _db.StorageRetentionMarks.AddRange(marks);
        _db.ProjectFiles.RemoveRange(files);
        await ForgetVerdictsAsync(projectId, files.Select(f => f.StorageKey).ToList(), ct);
        await _db.SaveChangesAsync(CancellationToken.None);
        await storageGuard.CommitAsync(CancellationToken.None);

        try
        {
            await DeleteMarkedObjectsAsync(projectId, marks, files, CancellationToken.None);
        }
        catch
        {
            // The rows are already gone, so the files' vector chunks must still go: queue the deletes
            // before surfacing the object-store failure, or they are left behind with no row to name them.
            await PublishIndexDeletesAsync(projectId, deletedFileIds);
            throw;
        }

        await PublishIndexDeletesAsync(projectId, deletedFileIds);

        return Ok(new { deletedFiles = files.Count, path = normalizedPath });
    }

    /// <summary>
    /// How many pictures use this file's photo, for the delete dialog ("N pictures use this photo and keep
    /// their own copy"). View role; a file outside the project is 404.
    /// </summary>
    [HttpGet("{fileId:guid}/picture-usage")]
    public async Task<ActionResult<PictureUsageResponse>> GetPictureUsage(Guid projectId, Guid fileId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        ProjectFile? file = await _db.ProjectFiles.FirstOrDefaultAsync(f => f.Id == fileId && f.ProjectId == projectId, ct);
        if (file is null || file.CsamStatus == CsamScanStatus.Quarantined) return NotFound();
        if (await Services.Outputs.OutputGate.IsFileHeldAsync(_db, file, ct)) return Services.Outputs.OutputGate.Unavailable();

        int pictures = await PictureSourceCopies.CountPicturesUsingAsync(_db, projectId, file.StorageKey, ct);
        return Ok(new PictureUsageResponse(pictures));
    }

    [HttpDelete("{fileId:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid fileId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        ProjectFile? file = await _db.ProjectFiles.FirstOrDefaultAsync(f => f.Id == fileId && f.ProjectId == projectId, ct);
        if (file == null || file.CsamStatus == CsamScanStatus.Quarantined) return NotFound();
        if (await Services.Outputs.OutputGate.IsFileHeldAsync(_db, file, ct)) return Services.Outputs.OutputGate.Unavailable();
        // A reference of an avatar cannot disappear from under it (the link is ON DELETE NO ACTION): 409 naming the avatar.
        if (await ReelBolt.Shared.Avatars.AvatarFileUse.FirstUseAsync(_db, projectId, [file.Id], ct) is { } avatarUse)
            return Conflict(avatarUse.ToBody());

        // D10 picture copies, the plan quota preflight, the storage guard, the durable deletion mark and
        // the vector-index delete are the one shared path both this route and the public API call.
        IProjectFileDeletionService deletion = _fileDeletion
            ?? new ProjectFileDeletionService(_db, _fileStorage, _planGate, _publishModeration, _publishEndpoint, _storageLock, _logger);
        if (await deletion.DeleteAsync(project, file, ct) is { } failure) return failure;

        return NoContent();
    }

    [HttpPost("search")]
    public async Task<ActionResult<SearchProjectFilesResponse>> Search(
        Guid projectId,
        [FromBody] SearchProjectFilesRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        int limit = request.Limit <= 0 ? 5 : request.Limit;
        if (string.IsNullOrWhiteSpace(request.Query))
            return Ok(new SearchProjectFilesResponse([], false));

        try
        {
            IReadOnlyList<VectorSearchChunkResult> results = await _vectorSearchQueryService
                .SearchProjectFilesAsync(projectId, request.Query, limit, ct);

            // A quarantined file's bytes were moved and its row is never indexed from here on; this
            // also hides a row indexed before the control existed.
            List<Guid> resultFileIds = results.Select(r => r.FileId).Distinct().ToList();
            HashSet<Guid> quarantined = resultFileIds.Count == 0
                ? []
                : (await _db.ProjectFiles
                    .Where(f => f.ProjectId == projectId
                                && f.CsamStatus == CsamScanStatus.Quarantined
                                && resultFileIds.Contains(f.Id))
                    .Select(f => f.Id)
                    .ToListAsync(ct)).ToHashSet();

            // A file whose bytes are not served never surfaces through its text either.
            HashSet<Guid> unavailable = [];
            if (resultFileIds.Count > 0)
            {
                List<ProjectFile> found = await _db.ProjectFiles.IgnoreQueryFilters().AsNoTracking()
                    .Where(f => f.ProjectId == projectId && resultFileIds.Contains(f.Id)).ToListAsync(ct);
                unavailable = await Services.Outputs.OutputGate.HeldFileIdsAsync(_db, projectId, found, ct);
            }

            List<SearchProjectFileChunkResult> mapped = results
                .Where(r => !quarantined.Contains(r.FileId) && !unavailable.Contains(r.FileId))
                .Select(r => new SearchProjectFileChunkResult(
                    r.FileId,
                    r.FilePath,
                    r.FileName,
                    r.ChunkIndex,
                    r.TotalChunks,
                    r.Language,
                    r.Content,
                    r.Score))
                .ToList();

            return Ok(new SearchProjectFilesResponse(mapped, false));
        }
        catch (VectorSearchException)
        {
            // IndexNotReady, or no usable embedding provider: report "index not ready" instead of a 500.
            return Ok(new SearchProjectFilesResponse([], true));
        }
    }

    [HttpPost("{fileId:guid}/reindex")]
    public async Task<IActionResult> ReindexFile(Guid projectId, Guid fileId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        ProjectFile? file = await _db.ProjectFiles.FirstOrDefaultAsync(f => f.Id == fileId && f.ProjectId == projectId, ct);
        if (file == null || file.CsamStatus == CsamScanStatus.Quarantined) return NotFound();
        if (await Services.Outputs.OutputGate.IsFileHeldAsync(_db, file, ct)) return Services.Outputs.OutputGate.Unavailable();

        file.IndexingStatus = FileIndexingStatus.Pending;
        file.IndexedAt = null;
        file.IndexingError = null;
        await _db.SaveChangesAsync(ct);

        await _publishEndpoint.Publish(new ProjectFileIndexingRequested
        {
            ProjectId = projectId,
            FileId = file.Id,
            Operation = "Upsert",
            RequestedAt = DateTime.UtcNow
        }, ct);

        return Accepted(new { fileId = file.Id, status = file.IndexingStatus.ToString() });
    }

    [HttpPost("reindex")]
    public async Task<ActionResult<ReindexProjectFilesResponse>> ReindexProjectFiles(
        Guid projectId,
        [FromBody] ReindexProjectFilesRequest? request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        bool includeIndexed = request?.IncludeIndexed ?? false;
        int maxFiles = Math.Clamp(
            request?.MaxFiles ?? ProjectReindexDefaultMaxFiles,
            1,
            ProjectReindexAbsoluteMaxFiles);

        IQueryable<ProjectFile> eligibleQuery = _db.ProjectFiles
            .Where(f => f.ProjectId == projectId && f.CsamStatus != CsamScanStatus.Quarantined)
            .Where(f => f.IndexingStatus != FileIndexingStatus.Pending && f.IndexingStatus != FileIndexingStatus.Processing)
            // R22: video/audio uploads sit at IndexingStatus.NotIndexed by design (see Upload) —
            // without this filter a bulk reindex would sweep them right back into the text
            // chunker/embedder, the exact failure mode R22 exists to prevent.
            .Where(f => !(f.MimeType.StartsWith("video/") || f.MimeType.StartsWith("audio/")));

        if (!includeIndexed)
            eligibleQuery = eligibleQuery.Where(f => f.IndexingStatus != FileIndexingStatus.Indexed);

        int eligibleFiles;
        List<ProjectFile> selectedFiles;
        if ((await Services.Outputs.OutputGate.ReadAsync(_db, projectId, ct)).IsActive)
        {
            // Files whose bytes are not served are left out of the count and the selection alike, so they neither
            // inflate the total nor take a slot from a file that can be queued.
            List<ProjectFile> candidates = await eligibleQuery.OrderByDescending(f => f.UploadedAt).ToListAsync(ct);
            HashSet<Guid> unavailableFiles = await Services.Outputs.OutputGate.HeldFileIdsAsync(_db, projectId, candidates, ct);
            candidates = candidates.Where(f => !unavailableFiles.Contains(f.Id)).ToList();
            eligibleFiles = candidates.Count;
            selectedFiles = candidates.Take(maxFiles).ToList();
        }
        else
        {
            eligibleFiles = await eligibleQuery.CountAsync(ct);
            selectedFiles = await eligibleQuery
                .OrderByDescending(f => f.UploadedAt)
                .Take(maxFiles)
                .ToListAsync(ct);
        }

        foreach (ProjectFile file in selectedFiles)
        {
            file.IndexingStatus = FileIndexingStatus.Pending;
            file.IndexedAt = null;
            file.IndexingError = null;
        }

        await _db.SaveChangesAsync(ct);

        for (int index = 0; index < selectedFiles.Count; index++)
        {
            ProjectFile file = selectedFiles[index];
            await _publishEndpoint.Publish(new ProjectFileIndexingRequested
            {
                ProjectId = projectId,
                FileId = file.Id,
                Operation = "Upsert",
                RequestedAt = DateTime.UtcNow
            }, ct);

            if (index < selectedFiles.Count - 1)
                await Task.Delay(ProjectReindexPublishInterval, ct);
        }

        return Ok(new ReindexProjectFilesResponse(
            selectedFiles.Count,
            eligibleFiles,
            eligibleFiles > selectedFiles.Count));
    }

    /// <summary>The caller a browser ticket is bound to: the user id (the desktop app's is <c>device:{id}</c>).</summary>
    private string CallerKey => _currentUser.UserId.ToString("D");

    private static DirectUploadMode ParseMode(string? mode) =>
        string.Equals(mode, "multipart", StringComparison.OrdinalIgnoreCase) ? DirectUploadMode.Multipart : DirectUploadMode.SinglePut;

    /// <summary>The browser's JSON shape of a ticket: the original single-PUT fields first, unchanged, then the multipart ones.</summary>
    private static DirectUploadTicket ToBrowserTicket(Services.Uploads.DirectUploadTicket t) => new(
        t.UploadToken,
        t.Key,
        t.FileId,
        t.Url,
        t.ExpiresAtUnix,
        t.ContentType,
        MaxDirectUploadBytes,
        t.Mode == DirectUploadMode.Multipart ? "multipart" : "singlePut",
        t.UploadId,
        t.PartSizeBytes,
        t.PartCount,
        t.FileName,
        t.RelativePath,
        t.SizeBytes);

    /// <summary>
    /// The echoed ticket, verified for this caller — or the refusal to answer with: the ticket's name or
    /// path is not a project path (<c>UPLOAD_TICKET_INVALID</c>), its key is not the one its own fields
    /// derive (<c>UPLOAD_KEY_MISMATCH</c>), or its MAC does not verify for this caller.
    /// </summary>
    private (Services.Uploads.DirectUploadTicket? Ticket, ObjectResult? Refusal) VerifyEcho(Guid projectId, DirectUploadTicketEcho echo)
    {
        if (string.IsNullOrWhiteSpace(echo.UploadToken) || string.IsNullOrWhiteSpace(echo.Key) || echo.FileId == Guid.Empty)
            return (null, InvalidTicket("An upload token, key, file id and file name are required."));

        string? expectedKey = DirectUploadService.DeriveKey(projectId, echo.FileId, echo.FileName, echo.RelativePath);
        if (expectedKey is null)
            return (null, InvalidTicket("The upload ticket's file path is not a valid project file path."));

        if (!string.Equals(echo.Key, expectedKey, StringComparison.Ordinal))
        {
            return (null, StatusCode(StatusCodes.Status400BadRequest, new
            {
                code = "UPLOAD_KEY_MISMATCH",
                message = "The key is not the one this upload was issued for."
            }));
        }

        Services.Uploads.DirectUploadTicket? ticket = _directUploads!.Verify(echo, CallerKey);
        return ticket is null
            ? (null, InvalidTicket("This upload ticket is not one this server issued to you for this project."))
            : (ticket, null);
    }

    /// <summary>
    /// A refusal from the upload service, as the browser routes have always answered: a quota refusal
    /// carries the plan gate's own body (<c>{ code, feature, message, limit, current }</c>), every other
    /// one <c>{ code, message }</c>.
    /// </summary>
    private ObjectResult Refusal(DirectUploadRefusedException refused) =>
        refused.Code == PlanGate.StorageCode
            ? StatusCode(refused.StatusCode, new
            {
                code = refused.Code,
                feature = "storage",
                message = refused.Message,
                limit = refused.LimitBytes,
                current = refused.UsedBytes,
                incoming = refused.IncomingBytes,
                plan = refused.Plan
            })
            : StatusCode(refused.StatusCode, new { code = refused.Code, message = refused.Message });

    /// <summary>501 for a composition with no presigning wired; the web client's one signal to fall back to multipart.</summary>
    private ObjectResult DirectUploadUnavailable() =>
        StatusCode(StatusCodes.Status501NotImplemented, new
        {
            code = DirectUploadDisabledCode,
            message = "This deployment does not support uploading directly to storage. Use the multipart upload."
        });

    /// <summary>400 for a completion whose ticket is missing a field, malformed, or not this caller's.</summary>
    private ObjectResult InvalidTicket(string message) =>
        StatusCode(StatusCodes.Status400BadRequest, new { code = "UPLOAD_TICKET_INVALID", message });

    private static ProjectFileResponse ToResponse(ProjectFile file) => new(
        file.Id,
        file.OriginalFileName,
        file.OriginalPath,
        file.DirectoryPath,
        file.Category,
        file.StorageKey,
        file.StorageFileName,
        file.MimeType,
        file.SizeBytes,
        file.AgentSummary,
        file.SummaryStatus.ToString(),
        file.IndexingStatus.ToString(),
        file.IndexedAt,
        file.IndexingError,
        file.UploadedAt,
        file.ModerationStatus.ToString());

    private static bool IsVideoOrAudioMimeType(string? mimeType) => DirectUploadService.IsVideoOrAudio(mimeType);

    /// R22 relies on the client-supplied Content-Type to detect video/audio uploads, but a real
    /// client (especially a non-browser one) can send a generic "application/octet-stream" for a
    /// media file. One rule for the form route and the direct upload: <see cref="DirectUploadService.ResolveContentType"/>.
    private static string? ResolveUploadMimeType(string? contentType, string? fileName) =>
        DirectUploadService.ResolveContentType(contentType, fileName);

    /// <summary>
    /// The text-asset predicate this surface uses to decide what it will show and accept as editable
    /// content, delegated to the shared <see cref="ProjectFileTextPolicy"/> so it cannot drift from
    /// the engine workspace, the assistant file tools or the brief resolver.
    /// </summary>
    private static bool IsLikelyText(string mimeType, string fileName) =>
        ProjectFileTextPolicy.IsLikelyText(mimeType, fileName);

    /// <summary>A <c>.cube</c> LUT, readable (never writable) through <c>GET …/content</c> regardless of its stored MIME.</summary>
    internal static bool IsCubeLut(string? fileName) =>
        string.Equals(Path.GetExtension(fileName), ".cube", StringComparison.OrdinalIgnoreCase);
    /// <summary>
    /// One durable deletion mark per file object, written in the same transaction that removes the rows, so a
    /// failed object delete is retried by the retention sweep instead of being reported as gone.
    /// </summary>
    private static List<StorageRetentionMark> MarkForDeletion(IEnumerable<ProjectFile> files, Guid organizationId, DateTime now) =>
        [.. files
            .Where(f => !string.IsNullOrEmpty(f.StorageKey))
            .Select(f => new StorageRetentionMark
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                ProjectId = f.ProjectId,
                StorageKey = f.StorageKey,
                Category = f.Category,
                MarkedAt = now
            })];

    /// <summary>
    /// Queues the vector-chunk removal for rows that are already gone, one event per file. Uncancellable:
    /// a client that disconnected must not drop it - dropping it leaves indexed content behind for a file
    /// that no longer exists.
    /// </summary>
    private async Task PublishIndexDeletesAsync(Guid projectId, IReadOnlyCollection<Guid> fileIds)
    {
        foreach (Guid fileId in fileIds)
        {
            await _publishEndpoint.Publish(new ProjectFileIndexingRequested
            {
                ProjectId = projectId,
                FileId = fileId,
                Operation = "Delete",
                RequestedAt = DateTime.UtcNow
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Deletes the objects of rows that are already committed and drops each mark whose object is gone. A
    /// transient provider failure keeps its mark, so the sweep retries it; anything else is rethrown — a scope
    /// error means the call was wrong, and swallowing it would report a delete whose bytes survive with no
    /// durable record.
    /// </summary>
    private async Task DeleteMarkedObjectsAsync(
        Guid projectId, IReadOnlyList<StorageRetentionMark> marks, IReadOnlyList<ProjectFile> files, CancellationToken ct)
    {
        Dictionary<string, StorageRetentionMark> byKey = [];
        foreach (StorageRetentionMark mark in marks) byKey.TryAdd(mark.StorageKey, mark);

        List<StorageRetentionMark> done = [];
        foreach (ProjectFile file in files)
        {
            if (!byKey.TryGetValue(file.StorageKey, out StorageRetentionMark? mark)) continue;
            try
            {
                await _fileStorage.DeleteAsync(projectId, file.StorageKey, ct);
                done.Add(mark);
            }
            catch (Exception ex) when (StorageFailure.IsTransient(ex))
            {
                _logger?.LogWarning(
                    ex, "Could not remove the object of deleted project file {FileId}; its retention mark makes the sweep retry it", file.Id);
            }
        }

        if (done.Count > 0)
        {
            _db.StorageRetentionMarks.RemoveRange(done);
            await _db.SaveChangesAsync(CancellationToken.None);
        }
    }
}
