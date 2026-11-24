using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.FolderSync;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The desktop app's folder sync API (docs/design/local-folder-sync-and-storage.md, "Device API").
///
/// <para>
/// <b>No user token.</b> Every request is signed with the paired computer's Ed25519 key and checked by
/// <see cref="DeviceAuthenticationFilter"/> before anything else runs; it carries no cookie and no bearer
/// token, hence <see cref="AllowAnonymousAttribute"/> on the class (the JWT handler has nothing to do here).
/// Every refusal of the signature is the same <c>401 device_unauthorized</c>.
/// </para>
///
/// <para>
/// <b>Authority.</b> A device acts as its pairing user inside its own organization: every action re-checks
/// through <see cref="DeviceProjectAccess"/> that the pairing user is still a member who may edit the
/// project — <c>403 device_owner_lacks_access</c> otherwise — and a project or link outside the device's
/// organization, or another device's link, is <c>404</c>.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/device")]
[AllowAnonymous]
[ServiceFilter(typeof(DeviceAuthenticationFilter))]
public class DeviceFolderSyncController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly DeviceProjectAccess _deviceAccess;
    private readonly LocalFolderSyncService _sync;
    private readonly FolderSyncManifestBuffer _manifests;
    private readonly IDirectUploadService _uploads;
    private readonly ILogger<DeviceFolderSyncController> _logger;
    private readonly ICsmaHashMatcher? _csamMatcher;
    private readonly IContentModerationEnqueuer? _moderation;
    private readonly Services.Telemetry.IProductEventEmitter? _productEvents;

    public DeviceFolderSyncController(
        InferenceApiDbContext db,
        DeviceProjectAccess deviceAccess,
        LocalFolderSyncService sync,
        FolderSyncManifestBuffer manifests,
        IDirectUploadService uploads,
        ILogger<DeviceFolderSyncController> logger,
        ICsmaHashMatcher? csamMatcher = null,
        IContentModerationEnqueuer? moderation = null,
        Services.Telemetry.IProductEventEmitter? productEvents = null)
    {
        _productEvents = productEvents;
        _db = db;
        _deviceAccess = deviceAccess;
        _sync = sync;
        _manifests = manifests;
        _uploads = uploads;
        _logger = logger;
        _csamMatcher = csamMatcher;
        _moderation = moderation;
    }

    private DeviceContext Device => HttpContext.GetDevice();

    /// <summary>Who this computer is, and whom it acts for.</summary>
    [HttpGet("me")]
    public async Task<ActionResult<DeviceMeResponse>> Me(CancellationToken ct)
    {
        DeviceContext device = Device;
        if (!await _deviceAccess.PairingUserMayEditAsync(device, ct)) return OwnerLacksAccess();

        string organizationName = await _db.Organizations
            .Where(o => o.Id == device.OrganizationId)
            .Select(o => o.Name)
            .FirstOrDefaultAsync(ct) ?? string.Empty;
        string? email = await _db.ApplicationUsers
            .Where(u => u.Id == device.PairingUserId)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(ct);

        return Ok(new DeviceMeResponse(device.DeviceId, device.DeviceName, device.OrganizationId, organizationName, email));
    }

    /// <summary>
    /// This computer removes itself (<c>204</c>): the desktop app's "Unpair". The device is revoked exactly
    /// as the dashboard's removal revokes it (see <see cref="DeviceSelfRemoval"/>), so it leaves the
    /// workspace's device list and every later request it signs is refused. Allowed whatever the pairing
    /// user may still do in the workspace: giving up access never needs access. A repeat after a lost answer
    /// cannot reach here (the revoked device no longer authenticates) and gets <c>401 device_unauthorized</c>,
    /// which the app treats as "already removed".
    /// </summary>
    [HttpDelete("me")]
    public async Task<IActionResult> ForgetMe([FromServices] DeviceSelfRemoval removal, CancellationToken ct)
    {
        await removal.RevokeAsync(Device, ct);
        return NoContent();
    }

    /// <summary>The projects of the device's workspace its pairing user can edit.</summary>
    [HttpGet("projects")]
    public async Task<ActionResult<IReadOnlyList<DeviceProjectResponse>>> Projects(CancellationToken ct)
    {
        DeviceContext device = Device;
        if (!await _deviceAccess.PairingUserMayEditAsync(device, ct)) return OwnerLacksAccess();

        IReadOnlyList<Project> projects = await _deviceAccess.EditableProjectsAsync(device, ct);
        return Ok(projects.Select(p => new DeviceProjectResponse(p.Id, p.Name)).ToList());
    }

    /// <summary>This computer's links.</summary>
    [HttpGet("folder-links")]
    public async Task<ActionResult<IReadOnlyList<DeviceFolderLinkResponse>>> Links(CancellationToken ct)
    {
        DeviceContext device = Device;
        if (!await _deviceAccess.PairingUserMayEditAsync(device, ct)) return OwnerLacksAccess();

        var rows = await _db.ProjectLocalFolderLinks
            .Where(l => l.DeviceId == device.DeviceId && l.OrganizationId == device.OrganizationId)
            .Join(_db.Projects, l => l.ProjectId, p => p.Id, (l, p) => new { Link = l, ProjectName = p.Name })
            .OrderBy(x => x.ProjectName)
            .ToListAsync(ct);

        return Ok(rows.Select(x => ToDeviceResponse(x.Link, x.ProjectName)).ToList());
    }

    /// <summary>Links a folder of this computer to a project (<c>201</c>). <c>409 target_folder_taken</c>, <c>400 invalid_target_folder</c>.</summary>
    [HttpPost("folder-links")]
    public async Task<ActionResult<DeviceFolderLinkResponse>> CreateLink([FromBody] CreateDeviceFolderLinkRequest request, CancellationToken ct)
    {
        DeviceContext device = Device;
        DeviceAccessResult access = await _deviceAccess.CheckProjectAsync(device, request.ProjectId, ct);
        if (Refusal(access) is { } refused) return refused;

        FolderLinkCreateResult result = await _sync.CreateLinkAsync(device, access.Project!, request.TargetFolder, request.DisplayPath, ct);
        if (result.Link is not { } link) return Error(result.StatusCode, result.Error!, result.Message!);

        // product-analytics: the person connected a source (activation.source_connected, once per user).
        // A device acts as its pairing user, and choosing the folder in the desktop app is that person's
        // own action; the uploads the folder then sends are a sync and write nothing. Best effort.
        if (_productEvents is not null)
        {
            await _productEvents.FirstAsync(
                device.PairingUserId, access.Project!.OrganizationId, Services.Telemetry.ProductEventNames.SourceConnected, ct);
        }

        return StatusCode(StatusCodes.Status201Created, ToDeviceResponse(link, access.Project!.Name));
    }

    /// <summary>Removes a link (<c>204</c>). The files it synced stay in the project.</summary>
    [HttpDelete("folder-links/{id:guid}")]
    public async Task<IActionResult> DeleteLink(Guid id, CancellationToken ct)
    {
        (ProjectLocalFolderLink? link, ActionResult? refusal) = await ResolveLinkAsync(id, ct);
        if (refusal is not null) return refusal;

        await _sync.UnlinkAsync(link!, ct);
        return NoContent();
    }

    /// <summary>
    /// One batch of the folder's complete manifest. Non-final batches answer <c>{ accepted }</c>; the final
    /// batch answers what to upload and delete, what was skipped, whether the link is paused, and the
    /// workspace's storage.
    /// </summary>
    [HttpPost("folder-links/{id:guid}/manifest")]
    public async Task<IActionResult> Manifest(Guid id, [FromBody] ManifestBatchRequest request, CancellationToken ct)
    {
        (ProjectLocalFolderLink? link, ActionResult? refusal) = await ResolveLinkAsync(id, ct);
        if (refusal is not null) return refusal;

        IReadOnlyList<ManifestFileEntry> files = request.Files ?? [];
        string manifestId = request.ManifestId ?? string.Empty;
        switch (_manifests.Add(link!.Id, manifestId, request.Batch, files, out int total))
        {
            case ManifestBatchOutcome.Invalid:
                return Error(400, "invalid_manifest",
                    $"A manifest batch needs a manifest id, a batch number from 0, and at most {FolderSyncManifestBuffer.MaxFilesPerBatch} files.");
            case ManifestBatchOutcome.TooLarge:
                return Error(413, "manifest_too_large",
                    $"A synced folder can hold at most {FolderSyncManifestBuffer.MaxFilesPerManifest:N0} files. Choose a smaller folder.");
        }

        if (!request.Final) return Ok(new ManifestAcceptedResponse(total));

        IReadOnlyList<ManifestFileEntry>? complete = _manifests.Take(link.Id, manifestId, request.Batch);
        if (complete is null)
            return Error(409, "manifest_incomplete", "Some parts of the folder's list did not arrive. Send the list again.");

        ManifestResultResponse result = await _sync.ReconcileAsync(link, Device, complete, request.ConfirmLargeDelete, ct);
        return Ok(result);
    }

    /// <summary>
    /// Opens an upload of one file of the folder: a single PUT or a multipart upload. <c>409 unchanged</c>
    /// when the project already holds that content, <c>409 occupied</c> when another source holds the path,
    /// <c>409 link_paused</c>, and the upload machinery's own refusals (<c>413 STORAGE_QUOTA_EXCEEDED</c>, …).
    /// </summary>
    [HttpPost("folder-links/{id:guid}/uploads")]
    public async Task<ActionResult<DeviceUploadTicket>> CreateUpload(Guid id, [FromBody] DeviceUploadRequest request, CancellationToken ct)
    {
        (ProjectLocalFolderLink? link, ActionResult? refusal) = await ResolveLinkAsync(id, ct);
        if (refusal is not null) return refusal;
        if (link!.PausedAt is not null) return LinkPaused();

        if (!FolderSyncPaths.IsValidRelativePath(request.Path))
            return Error(400, "invalid_path", "That file's path cannot be synced (it is empty, too long, or contains characters a project path cannot hold).");
        if (!FolderSyncPaths.IsSha256Hex(request.Sha256))
            return Error(400, "invalid_sha256", "The file's SHA-256 must be 64 hexadecimal characters.");
        if (request.Size < 0)
            return Error(400, "invalid_size", "The file's size cannot be negative.");

        string path = request.Path!;
        string sha256 = request.Sha256!.ToLowerInvariant();

        ProjectFile? current = await _sync.FindLinkRowAsync(link, path, ct);

        // Replacing a quarantined row would repoint it at a normal key and delete the quarantine
        // object. Evidence is never replaced; the desktop app reports this path as unavailable.
        if (current?.CsamStatus == CsamScanStatus.Quarantined)
            return Error(409, "FILE_UNAVAILABLE", "This file cannot be used.");
        if (current is not null && string.Equals(current.SourceRevision, sha256, StringComparison.OrdinalIgnoreCase))
            return Error(409, "unchanged", "The project already has this version of the file.");
        if (current is null && await _sync.IsOccupiedAsync(link, path, ct))
            return Error(409, "occupied", "A file from another source already has this name in the project, so this one is skipped.");

        string stored = FolderSyncPaths.StoredPath(link.TargetFolder, path);
        DirectUploadRequest upload = new(
            link.ProjectId,
            link.OrganizationId,
            CallerKey(),
            FileName: stored[(stored.LastIndexOf('/') + 1)..],
            RelativePath: stored,
            SizeBytes: request.Size,
            ContentType: string.IsNullOrWhiteSpace(request.ContentType) ? null : request.ContentType,
            Category: "userFiles",
            SourceKind: LocalFolderSyncService.SourceKind,
            SourceRevision: sha256,
            SourceLinkId: link.Id,
            ReplacesFileId: current?.Id);

        try
        {
            DirectUploadTicket ticket = await _uploads.CreateAsync(upload, ct);
            return Ok(DeviceUploadTicket.From(ticket));
        }
        catch (DirectUploadRefusedException ex)
        {
            return UploadRefused(ex);
        }
    }

    /// <summary>Presigned URLs for parts of a multipart upload.</summary>
    [HttpPost("folder-links/{id:guid}/uploads/parts")]
    public async Task<ActionResult<IReadOnlyList<DeviceUploadPartUrl>>> UploadParts(Guid id, [FromBody] DeviceUploadPartsRequest request, CancellationToken ct)
    {
        (ProjectLocalFolderLink? link, ActionResult? refusal) = await ResolveLinkAsync(id, ct);
        if (refusal is not null) return refusal;
        if (link!.PausedAt is not null) return LinkPaused();

        DirectUploadTicket? ticket = VerifyTicket(link, request.Ticket);
        if (ticket is null) return InvalidTicket();
        if (request.PartNumbers is not { Count: > 0 } partNumbers)
            return Error(400, "INVALID_PART_NUMBERS", "Ask for at least one part.");

        try
        {
            IReadOnlyList<DirectUploadPartUrl> urls = await _uploads.PresignPartsAsync(ticket, partNumbers, ct);
            return Ok(urls.Select(u => new DeviceUploadPartUrl(u.PartNumber, u.Url, u.ExpiresAtUnix)).ToList());
        }
        catch (DirectUploadRefusedException ex)
        {
            return UploadRefused(ex);
        }
    }

    /// <summary>Finishes an upload: the file is in the project (a changed file keeps its id).</summary>
    [HttpPost("folder-links/{id:guid}/uploads/complete")]
    public async Task<ActionResult<DeviceUploadCompleteResponse>> CompleteUpload(Guid id, [FromBody] DeviceUploadCompleteRequest request, CancellationToken ct)
    {
        (ProjectLocalFolderLink? link, ActionResult? refusal) = await ResolveLinkAsync(id, ct);
        if (refusal is not null) return refusal;
        if (link!.PausedAt is not null) return LinkPaused();

        DirectUploadTicket? ticket = VerifyTicket(link, request.Ticket);
        if (ticket is null) return InvalidTicket();

        List<DirectUploadCompletedPart> parts = (request.Parts ?? [])
            .Select(p => new DirectUploadCompletedPart(p.PartNumber, p.Etag ?? string.Empty))
            .ToList();

        ProjectFile file;
        try
        {
            file = await _uploads.CompleteAsync(ticket, new DirectUploadCompletion(parts), ct);
        }
        catch (DirectUploadRefusedException ex)
        {
            return UploadRefused(ex);
        }

        await _sync.RecordUploadAsync(link, file, ct);

        // The desktop path has no post-add effects: scan here, after the row is recorded. A match
        // quarantines the bytes and answers the same generic refusal as the browser uploads.
        if (_csamMatcher is not null)
        {
            CsamScanOutcome outcome = await _csamMatcher.ScanAsync(file, actorUserId: null, ct);
            if (outcome.Status == CsamScanStatus.Quarantined) return FileUnavailable();
        }

        if (CsamScanGate.AllowsModeration(file))
            _moderation?.TryQueue(file.Id);
        string path = FolderSyncPaths.RelativeTo(link.TargetFolder, file.OriginalPath) ?? file.OriginalPath ?? file.OriginalFileName;
        return Ok(new DeviceUploadCompleteResponse(file.Id, path));
    }

    /// <summary>Abandons an upload (<c>204</c>). Allowed while the link is paused, so the computer can clean up.</summary>
    [HttpPost("folder-links/{id:guid}/uploads/abort")]
    public async Task<IActionResult> AbortUpload(Guid id, [FromBody] DeviceUploadAbortRequest request, CancellationToken ct)
    {
        (ProjectLocalFolderLink? link, ActionResult? refusal) = await ResolveLinkAsync(id, ct);
        if (refusal is not null) return refusal;

        DirectUploadTicket? ticket = VerifyTicket(link!, request.Ticket);
        if (ticket is null) return InvalidTicket();

        await _uploads.AbortAsync(ticket, ct);
        return NoContent();
    }

    /// <summary>Deletes this link's files at the given folder-relative paths (at most 1,000). Other files are never touched.</summary>
    [HttpPost("folder-links/{id:guid}/delete")]
    public async Task<ActionResult<DeviceDeleteResponse>> DeleteFiles(Guid id, [FromBody] DeviceDeleteRequest request, CancellationToken ct)
    {
        (ProjectLocalFolderLink? link, ActionResult? refusal) = await ResolveLinkAsync(id, ct);
        if (refusal is not null) return refusal;
        if (link!.PausedAt is not null) return LinkPaused();

        IReadOnlyList<string> paths = request.Paths ?? [];
        if (paths.Count > LocalFolderSyncService.MaxDeletePaths)
            return Error(400, "too_many_paths", $"Delete at most {LocalFolderSyncService.MaxDeletePaths} files per request.");

        FolderDeleteOutcome outcome = await _sync.DeleteFilesWithSkippedAsync(link, paths, ct);
        return Ok(new DeviceDeleteResponse(outcome.Deleted, outcome.Skipped));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// This device's link and the pairing user's current authority over its project. Another device's link
    /// is answered exactly like a missing one.
    /// </summary>
    private async Task<(ProjectLocalFolderLink?, ActionResult?)> ResolveLinkAsync(Guid id, CancellationToken ct)
    {
        DeviceContext device = Device;
        ProjectLocalFolderLink? link = await _sync.FindDeviceLinkAsync(device, id, ct);
        if (link is null) return (null, LinkNotFound());

        DeviceAccessResult access = await _deviceAccess.CheckProjectAsync(device, link.ProjectId, ct);
        if (access.Outcome == DeviceAccessOutcome.NotFound) return (null, LinkNotFound());
        if (access.Outcome == DeviceAccessOutcome.OwnerLacksAccess) return (null, OwnerLacksAccess());
        return (link, null);
    }

    private DirectUploadTicket? VerifyTicket(ProjectLocalFolderLink link, DeviceUploadTicket? echo)
    {
        if (echo?.ToEcho() is not { } parsed) return null;
        DirectUploadTicket? ticket = _uploads.Verify(parsed, CallerKey());
        // A ticket this server issued to this device, but for another link or project, is refused too.
        return ticket is not null && ticket.SourceLinkId == link.Id && ticket.ProjectId == link.ProjectId ? ticket : null;
    }

    private string CallerKey() => $"device:{Device.DeviceId}";

    private ActionResult? Refusal(DeviceAccessResult access) => access.Outcome switch
    {
        DeviceAccessOutcome.NotFound => Error(404, "project_not_found", "That project could not be found."),
        DeviceAccessOutcome.OwnerLacksAccess => OwnerLacksAccess(),
        _ => null
    };

    private ObjectResult UploadRefused(DirectUploadRefusedException ex)
    {
        _logger.LogInformation("A folder-sync upload was refused: {Code}", ex.Code);
        return StatusCode(ex.StatusCode, new { error = ex.Code, message = ex.Message, usedBytes = ex.UsedBytes, limitBytes = ex.LimitBytes });
    }

    private ObjectResult OwnerLacksAccess() => Error(403, "device_owner_lacks_access",
        "The person who connected this computer can no longer change projects in this workspace. Connect the computer again with an account that can.");

    private ObjectResult LinkNotFound() => Error(404, "link_not_found", "This folder is no longer linked to a ReelBolt project.");

    private ObjectResult LinkPaused() => Error(409, "link_paused", "Syncing this folder is paused. Resume it to sync again.");

    private ObjectResult InvalidTicket() => Error(400, "invalid_ticket", "That upload is not valid for this folder. Start the upload again.");

    private ObjectResult Error(int status, string error, string message) => StatusCode(status, new { error, message });

    /// <summary>Generic, by design: nothing about why beyond "cannot be used".</summary>
    private ObjectResult FileUnavailable() =>
        StatusCode(StatusCodes.Status422UnprocessableEntity,
            new { code = "FILE_UNAVAILABLE", message = "This file cannot be used." });

    private static DeviceFolderLinkResponse ToDeviceResponse(ProjectLocalFolderLink link, string projectName) => new(
        link.Id,
        link.ProjectId,
        projectName,
        link.TargetFolder,
        link.DisplayPath,
        link.PausedAt is not null,
        link.PausedAt is null ? null : link.PauseReason ?? ProjectLocalFolderLink.PauseReasonUser,
        link.FileCount,
        link.LastReconciledAt,
        link.LastError);
}
