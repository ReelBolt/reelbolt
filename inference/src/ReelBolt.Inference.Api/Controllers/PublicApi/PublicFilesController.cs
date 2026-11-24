using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.PublicApi.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Projects;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers.PublicApi;

/// <summary>
/// <c>/api/public/v1/projects/{projectId}/files</c>. Thin over the services the dashboard's
/// <c>ProjectFilesController</c> calls, with the same gate order: project access first, then the email gate
/// (import), then the service, which owns the quota and size rules.
/// </summary>
[Route("api/public/v1/projects/{projectId:guid}/files")]
public sealed class PublicFilesController : PublicResourceControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly IDirectUploadService? _directUploads;
    private readonly IDirectUploadFinalizer? _finalizer;
    private readonly IUrlImportService? _urlImport;
    private readonly IOutputLinkService? _links;
    private readonly IProjectFileDeletionService _deletion;
    private readonly PublishModeration? _publishModeration;

    public PublicFilesController(
        InferenceApiDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess,
        IProjectFileDeletionService deletion,
        IDirectUploadService? directUploads = null, IDirectUploadFinalizer? finalizer = null,
        IUrlImportService? urlImport = null, IOutputLinkService? links = null,
        PublishModeration? publishModeration = null)
    {
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _deletion = deletion;
        _directUploads = directUploads;
        _finalizer = finalizer;
        _urlImport = urlImport;
        _links = links;
        _publishModeration = publishModeration;
    }

    private string CallerKey => _currentUser.UserId.ToString("D");

    [RequireScope(ApiScopes.FilesRead)]
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> List(
        Guid projectId, [FromQuery] int? limit, [FromQuery] string? cursor,
        [FromQuery] string? pathPrefix, [FromQuery] string? mimePrefix, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return PublicRefusal(denied);

        int take = PublicApiPagination.NormalizeLimit(limit);
        IQueryable<ProjectFile> query = _db.ProjectFiles.AsNoTracking()
            .Where(f => f.ProjectId == projectId
                        && f.CsamStatus != CsamScanStatus.Quarantined
                        && f.ModerationStatus != FileModerationStatus.Quarantined);
        if (!string.IsNullOrEmpty(pathPrefix))
            query = query.Where(f => f.OriginalPath != null && f.OriginalPath.StartsWith(pathPrefix));
        if (!string.IsNullOrEmpty(mimePrefix))
            query = query.Where(f => f.MimeType.StartsWith(mimePrefix));
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!PublicApiPagination.TryDecodeCursor(cursor, out DateTime at, out Guid after))
                return InvalidRequest("The cursor is not valid.");
            query = query.Where(f => f.UploadedAt < at || (f.UploadedAt == at && f.Id.CompareTo(after) < 0));
        }

        List<ProjectFile> rows = await query.OrderByDescending(f => f.UploadedAt).ThenByDescending(f => f.Id)
            .Take(take + 1).ToListAsync(ct);
        bool more = rows.Count > take;
        if (more) rows.RemoveAt(rows.Count - 1);
        string? next = more ? PublicApiPagination.EncodeCursor(rows[^1].UploadedAt, rows[^1].Id) : null;

        // A file whose bytes are not served is left out of the list like a missing one.
        HashSet<Guid> held = await OutputGate.HeldFileIdsAsync(_db, projectId, rows, ct);
        return Ok(new PublicApiPage<PublicFileView>([.. rows.Where(f => !held.Contains(f.Id)).Select(ToView)], next));
    }

    [RequireScope(ApiScopes.FilesRead)]
    [HttpGet("{fileId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Get(Guid projectId, Guid fileId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return PublicRefusal(denied);

        ProjectFile? file = await FindVisibleAsync(projectId, fileId, ct);
        return file is null ? NotFoundError("The file was not found.") : Ok(ToView(file));
    }

    [RequireScope(ApiScopes.FilesWrite)]
    [Idempotent]
    [HttpPost("uploads")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async Task<ActionResult> CreateUpload(Guid projectId, [FromBody] PublicCreateUploadRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return PublicRefusal(denied);
        Project project = access.Project!;

        if (_directUploads is null) return DirectUploadUnavailable();
        if (request?.SizeBytes is not long size || size < 0)
            return InvalidRequest("sizeBytes is required and must not be negative.");

        string? relativePath = string.IsNullOrWhiteSpace(request.DirectoryPath)
            ? null
            : request.DirectoryPath.Trim().Trim('/', '\\') + "/" + (request.FileName ?? string.Empty).Trim();
        try
        {
            DirectUploadTicket ticket = await _directUploads.CreateAsync(
                new DirectUploadRequest(projectId, project.OrganizationId, CallerKey, request.FileName ?? string.Empty,
                    relativePath, size, request.MimeType, DirectUploadService.Category), ct);

            List<PublicUploadPartView> parts = [];
            if (ticket.Mode == DirectUploadMode.SinglePut)
            {
                parts.Add(new PublicUploadPartView(1, ticket.Url ?? string.Empty,
                    DateTimeOffset.FromUnixTimeSeconds(ticket.ExpiresAtUnix).UtcDateTime));
            }
            else
            {
                for (int first = 1; first <= ticket.PartCount; first += DirectUploadService.MaxPartUrlsPerCall)
                {
                    int[] numbers = [.. Enumerable.Range(first, Math.Min(DirectUploadService.MaxPartUrlsPerCall, ticket.PartCount - first + 1))];
                    foreach (DirectUploadPartUrl part in await _directUploads.PresignPartsAsync(ticket, numbers, ct))
                        parts.Add(new PublicUploadPartView(part.PartNumber, part.Url,
                            DateTimeOffset.FromUnixTimeSeconds(part.ExpiresAtUnix).UtcDateTime));
                }
            }

            return StatusCode(StatusCodes.Status201Created, new PublicUploadView(
                PublicUploadId.Encode(ticket),
                ticket.Mode == DirectUploadMode.SinglePut ? "single" : "multipart",
                parts,
                ticket.Mode == DirectUploadMode.SinglePut ? ticket.SizeBytes : ticket.PartSizeBytes,
                ticket.Headers));
        }
        catch (DirectUploadRefusedException refused)
        {
            return Refusal(refused);
        }
    }

    [RequireScope(ApiScopes.FilesWrite)]
    [Idempotent]
    [HttpPost("uploads/{uploadId}/complete")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async Task<ActionResult> CompleteUpload(
        Guid projectId, string uploadId, [FromBody] PublicCompleteUploadRequest? request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return PublicRefusal(denied);

        if (_directUploads is null || _finalizer is null) return DirectUploadUnavailable();

        DirectUploadTicketEcho? echo = PublicUploadId.TryDecode(uploadId, projectId);
        if (echo is null || string.IsNullOrWhiteSpace(echo.UploadToken) || string.IsNullOrWhiteSpace(echo.Key) || echo.FileId == Guid.Empty)
            return InvalidUpload();
        string? expectedKey = DirectUploadService.DeriveKey(projectId, echo.FileId, echo.FileName, echo.RelativePath);
        if (expectedKey is null || !string.Equals(echo.Key, expectedKey, StringComparison.Ordinal))
            return InvalidUpload();
        DirectUploadTicket? ticket = _directUploads.Verify(echo, CallerKey);
        if (ticket is null) return InvalidUpload();

        IReadOnlyList<DirectUploadCompletedPart> parts = [.. (request?.Parts ?? [])
            .Select(p => new DirectUploadCompletedPart(p.PartNumber, p.Etag ?? string.Empty))];
        DirectUploadFinalizeResult finished = await _finalizer.FinalizeAsync(
            projectId, _currentUser.UserId, ticket, new DirectUploadCompletion(parts), ct);

        return finished.Outcome switch
        {
            DirectUploadFinalizeOutcome.Held => PublicRefusal(OutputGate.Unavailable()),
            DirectUploadFinalizeOutcome.Refused => Refusal(finished.Refused!),
            DirectUploadFinalizeOutcome.Quarantined => FileUnavailable(),
            DirectUploadFinalizeOutcome.Replayed => Ok(ToView(finished.File!)),
            _ => StatusCode(StatusCodes.Status201Created, ToView(finished.File!))
        };
    }

    [RequireScope(ApiScopes.FilesWrite)]
    [Idempotent]
    [HttpPost("import")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async Task<ActionResult> Import(Guid projectId, [FromBody] PublicImportFileRequest? request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return PublicRefusal(denied);
        if (EmailVerificationGate.Check(_currentUser) is { } unverified) return PublicRefusal(unverified);

        if (_urlImport is null)
            return PublicError(StatusCodes.Status501NotImplemented, "IMPORT_DISABLED",
                "This deployment does not import files from URLs.");

        UrlImportResult result = await _urlImport.ImportAsync(
            access.Project!, _currentUser.UserId, request?.Url ?? string.Empty, request?.FileName, request?.DirectoryPath, ct);
        if (result.Refusal is { } refusal)
        {
            return PublicError(refusal.StatusCode, refusal.Code, refusal.Message, new
            {
                upstreamStatus = refusal.UpstreamStatus,
                limit = refusal.LimitBytes,
                current = refusal.UsedBytes
            });
        }

        // The import's post-add path (inside UrlImportService) already scanned it.
        if (result.File!.CsamStatus == CsamScanStatus.Quarantined) return FileUnavailable();
        return StatusCode(StatusCodes.Status201Created, ToView(result.File!));
    }

    [RequireScope(ApiScopes.FilesRead)]
    [HttpGet("{fileId:guid}/download-link")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async Task<ActionResult> DownloadLink(Guid projectId, Guid fileId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return PublicRefusal(denied);

        // The same gates, in the same order, as the dashboard download: missing or quarantined 404, held,
        // then the stored publish verdict. Only then is a link minted.
        ProjectFile? file = await _db.ProjectFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId && f.ProjectId == projectId, ct);
        if (file is null) return NotFoundError("The file was not found.");
        if (file.CsamStatus == CsamScanStatus.Quarantined
            || file.ModerationStatus == FileModerationStatus.Quarantined) return NotFoundError("The file was not found.");
        if (await OutputGate.IsFileHeldAsync(_db, file, ct)) return PublicRefusal(OutputGate.Unavailable());
        if (_publishModeration is not null
            && await _publishModeration.GateStoredFileAsync(projectId, file.StorageKey, ct) is { } refused)
            return PublicRefusal(refused.ToRefusal(this));

        if (_links is null)
            return PublicError(StatusCodes.Status501NotImplemented, "LINKS_DISABLED", "This deployment does not mint download links.");

        // OutputLink.v1 (15 minutes by default). The link re-checks View at fetch time against this user.
        OutputLink link = _links.Create(new OutputLinkTarget(projectId, OutputLinkKind.ProjectFile, file.Id, null), _currentUser.UserId, null);
        return Ok(new PublicDownloadLinkView(link.Url, link.ExpiresAt));
    }

    [RequireScope(ApiScopes.FilesWrite)]
    [HttpDelete("{fileId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Delete(Guid projectId, Guid fileId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return PublicRefusal(denied);
        Project project = access.Project!;

        ProjectFile? file = await _db.ProjectFiles.FirstOrDefaultAsync(f => f.Id == fileId && f.ProjectId == projectId, ct);
        if (file is null || file.CsamStatus == CsamScanStatus.Quarantined) return NotFoundError("The file was not found.");
        if (await OutputGate.IsFileHeldAsync(_db, file, ct)) return PublicRefusal(OutputGate.Unavailable());

        // D10 picture copies and the rest of the deletion are the one path the dashboard also takes.
        if (await _deletion.DeleteAsync(project, file, ct) is { } failure) return PublicRefusal(failure);
        return NoContent();
    }

    private async Task<ProjectFile?> FindVisibleAsync(Guid projectId, Guid fileId, CancellationToken ct)
    {
        ProjectFile? file = await _db.ProjectFiles.AsNoTracking().FirstOrDefaultAsync(
            f => f.Id == fileId && f.ProjectId == projectId
                 && f.CsamStatus != CsamScanStatus.Quarantined
                 && f.ModerationStatus != FileModerationStatus.Quarantined, ct);
        if (file is null || await OutputGate.IsFileHeldAsync(_db, file, ct)) return null;
        return file;
    }

    private PublicFileView ToView(ProjectFile f) => new(
        Id(f.Id), f.OriginalFileName, f.OriginalPath ?? f.OriginalFileName, f.MimeType, f.SizeBytes, Utc(f.UploadedAt), StatusOf(f));

    /// <summary>Plain words: <c>checking</c> until the safety scan has a verdict, else <c>ready</c>.</summary>
    private static string StatusOf(ProjectFile f) =>
        f.CsamStatus is CsamScanStatus.Pending or CsamScanStatus.Scanning || f.ModerationStatus == FileModerationStatus.Scanning
            ? "checking"
            : "ready";

    private ObjectResult InvalidUpload() =>
        PublicError(StatusCodes.Status400BadRequest, "invalid_upload", "This upload id is not one this server issued to you for this project.");

    /// <summary>The one answer for a file whose scan matched: generic by design.</summary>
    private ObjectResult FileUnavailable() =>
        PublicError(StatusCodes.Status422UnprocessableEntity, "FILE_UNAVAILABLE", "This file cannot be used.");

    private ObjectResult DirectUploadUnavailable() =>
        PublicError(StatusCodes.Status501NotImplemented, "DIRECT_UPLOAD_DISABLED",
            "This deployment does not support uploading directly to storage.");

    private ObjectResult Refusal(DirectUploadRefusedException refused) =>
        refused.Code == ReelBolt.Shared.Billing.PlanGate.StorageCode
            ? PublicError(refused.StatusCode, refused.Code, refused.Message, new
            {
                feature = "storage",
                limit = refused.LimitBytes,
                current = refused.UsedBytes,
                incoming = refused.IncomingBytes,
                plan = refused.Plan
            })
            : PublicError(refused.StatusCode, refused.Code, refused.Message);
}
