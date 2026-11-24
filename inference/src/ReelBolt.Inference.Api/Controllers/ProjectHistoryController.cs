using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.History;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// A project's version history: layer 1 of contract section 5 (list, save a named version, read one
/// with its items, rename/un-name, delete a version, and "Delete forever" for one file).
///
/// <para>
/// Every action resolves the project through <see cref="IProjectAccessService"/> exactly once: View to
/// read, Edit to save/rename, Delete to delete a version or erase a file. Restore and its preview
/// validate the body first (a bad body is a 400, never a permission answer) and then make their single
/// call, with Edit for item scope and Delete for project scope. A project in another organization (or a
/// missing one) is the same 404; a role too low is 403. A version id that is not this project's is 404,
/// never 403.
/// </para>
/// <para>
/// Responses never carry storage keys, ETags, content identities, digests or bucket names; the
/// restorability verdict is computed by <see cref="IProjectHistoryService"/>, not here.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/history")]
[Authorize]
public class ProjectHistoryController : ControllerBase
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly IProjectAccessService _projectAccess;
    private readonly IProjectHistoryService _history;
    private readonly IProjectHistoryEraser _eraser;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectHistoryRestoreService _restore;

    public ProjectHistoryController(
        IProjectAccessService projectAccess,
        IProjectHistoryService history,
        IProjectHistoryEraser eraser,
        ICurrentUser currentUser,
        IProjectHistoryRestoreService restore)
    {
        _restore = restore;
        _projectAccess = projectAccess;
        _history = history;
        _eraser = eraser;
        _currentUser = currentUser;
    }

    /// <summary>Lists the project's versions newest first, with the retained history bytes and the window.</summary>
    [HttpGet("versions")]
    public async Task<ActionResult<ProjectHistoryVersionsResponse>> ListVersions(
        Guid projectId,
        [FromQuery] int? limit,
        [FromQuery] int? before,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        ProjectHistoryVersionsPage? page = await _history.ListVersionsAsync(
            projectId, Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit), before, ct);
        if (page is null) return NotFound();

        // The project is already loaded: answer the two write permissions from the same rule the
        // write routes enforce, without a second access lookup.
        Project project = access.Project!;
        return Ok(new ProjectHistoryVersionsResponse(
            page.Versions.Select(ToResponse).ToList(),
            page.HistoryBytes,
            page.RetentionDays,
            _projectAccess.Allows(project, ProjectPermission.Edit),
            _projectAccess.Allows(project, ProjectPermission.Delete)));
    }

    /// <summary>Saves the project's current state as a named version. 201 with the version.</summary>
    [HttpPost("versions")]
    public async Task<ActionResult<ProjectHistoryVersionResponse>> CreateVersion(
        Guid projectId,
        [FromBody] CreateProjectHistoryVersionRequest? request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        string? name = request?.Name;
        if (!ProjectVersionName.IsValid(name))
            return InvalidName();

        ProjectVersion? version;
        try
        {
            version = await _history.CaptureAsync(
                projectId, ProjectVersionCause.Named, name, CallerId(), ct);
        }
        catch (ProjectHistoryTooLargeException ex)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new
            {
                code = ex.Code,
                message = "This project has too many files to save as one version."
            });
        }
        catch (ProjectHistoryNamedVersionLimitException)
        {
            return NamedVersionLimit();
        }

        if (version is null) return NotFound();

        ProjectHistoryVersionInfo? info = await _history.GetVersionInfoAsync(projectId, version.Id, ct);
        return info is null
            ? NotFound()
            : StatusCode(StatusCodes.Status201Created, ToResponse(info));
    }

    /// <summary>One version and its items, each with its layer-1 restorability verdict.</summary>
    [HttpGet("versions/{versionId:guid}")]
    public async Task<ActionResult<ProjectHistoryVersionDetailResponse>> GetVersion(
        Guid projectId,
        Guid versionId,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        ProjectHistoryVersionDetail? detail = await _history.GetVersionAsync(projectId, versionId, ct);
        return detail is null ? NotFound() : Ok(ToResponse(detail));
    }

    /// <summary>
    /// Names or renames a version. A null/blank name makes it automatic again (the cause is kept and
    /// it expires after the retention window). 409 when the named-version cap is reached.
    /// </summary>
    [HttpPut("versions/{versionId:guid}/name")]
    public async Task<ActionResult<ProjectHistoryVersionResponse>> RenameVersion(
        Guid projectId,
        Guid versionId,
        [FromBody] RenameProjectHistoryVersionRequest? request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        string? name = request?.Name;
        if (!string.IsNullOrWhiteSpace(name) && !ProjectVersionName.IsValid(name))
            return InvalidName();

        ProjectHistoryVersionInfo? info;
        try
        {
            info = await _history.RenameVersionAsync(projectId, versionId, name, ct);
        }
        catch (ProjectHistoryNamedVersionLimitException)
        {
            return NamedVersionLimit();
        }

        return info is null ? NotFound() : Ok(ToResponse(info));
    }

    /// <summary>Deletes a version; the retention GC frees its now-unreferenced bytes.</summary>
    [HttpDelete("versions/{versionId:guid}")]
    public async Task<IActionResult> DeleteVersion(Guid projectId, Guid versionId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Delete, ct);
        if (access.Failure is { } denied) return denied;

        if (await _history.IsVersionDeletionHeldAsync(projectId, versionId, ct))
            return OutputHeld();

        return await _history.DeleteVersionAsync(projectId, versionId, ct) ? NoContent() : NotFound();
    }

    /// <summary>"Delete forever": erases the file's bytes from every version of the project.</summary>
    [HttpDelete("files/{fileId:guid}")]
    public async Task<IActionResult> EraseFile(Guid projectId, Guid fileId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Delete, ct);
        if (access.Failure is { } denied) return denied;

        if (await _history.IsFileDeletionHeldAsync(projectId, fileId, ct))
            return OutputHeld();

        await _eraser.EraseFileAsync(projectId, fileId, ct);
        return NoContent();
    }

    // ── layer 2: diff and content (contract section 7, section 5 last row) ──────────────────────

    /// <summary>The items that changed between the version and another version, or the live project.</summary>
    [HttpGet("versions/{versionId:guid}/changes")]
    public async Task<ActionResult<ProjectHistoryChangesResponse>> GetChanges(
        Guid projectId,
        Guid versionId,
        [FromQuery] string? to,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        if (!TryParseTarget(to, out Guid? toVersionId)) return InvalidTarget();

        ProjectHistoryChanges? changes = await _history.ComputeChangesAsync(
            projectId, versionId, toVersionId, ct);
        return changes is null ? NotFound() : Ok(ToResponse(changes));
    }

    /// <summary>One item's diff: text hunks, a binary size change, or a kind-specific document diff.</summary>
    [HttpGet("versions/{versionId:guid}/items/{kind}/{key}/diff")]
    public async Task<ActionResult<ProjectHistoryItemDiffResponse>> GetItemDiff(
        Guid projectId,
        Guid versionId,
        string kind,
        string key,
        [FromQuery] string? to,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        if (!TryParseKind(kind, out ProjectVersionItemKind itemKind)) return InvalidKind();
        if (!TryParseTarget(to, out Guid? toVersionId)) return InvalidTarget();

        ProjectHistoryItemDiff? diff = await _history.ComputeItemDiffAsync(
            projectId, versionId, itemKind, key, toVersionId, ct);
        return diff is null ? NotFound() : Ok(ToResponse(diff));
    }

    /// <summary>A file item's bytes at the version, served under the safe-content policy.</summary>
    [HttpGet("versions/{versionId:guid}/items/{kind}/{key}/content")]
    public async Task<IActionResult> GetItemContent(
        Guid projectId,
        Guid versionId,
        string kind,
        string key,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        if (!TryParseKind(kind, out ProjectVersionItemKind itemKind)) return InvalidKind();

        ProjectHistoryItemContent? content = await _history.GetFileContentAsync(
            projectId, versionId, itemKind, key, ct);
        if (content is null) return NotFound();

        ServedContent served = SafeContentPolicy.Decide(content.MimeType, content.FileName ?? "file");
        SafeContentPolicy.ApplySecurityHeaders(Response);
        Response.Headers["Content-Disposition"] =
            SafeContentPolicy.BuildContentDisposition(inline: false, content.FileName);
        return File(content.Content, served.ContentType);
    }

    private Guid? CallerId() => _currentUser.UserId == Guid.Empty ? null : _currentUser.UserId;

    private BadRequestObjectResult InvalidName() => BadRequest(new
    {
        code = "INVALID_NAME",
        message = $"A version name must be 1 to {ProjectVersionName.MaxLength} characters after "
                  + "trimming and contain no control or format characters."
    });

    private ConflictObjectResult NamedVersionLimit() => Conflict(new
    {
        code = ProjectHistoryErrorCodes.NamedVersionLimit,
        message = "This project already has the maximum number of named versions. Delete one first."
    });

    /// <summary>
    /// A project output hold protects a platform-made item; destroying its history copy is refused
    /// with the same neutral wording the output paths use.
    /// </summary>
    private ConflictObjectResult OutputHeld() => Conflict(new
    {
        code = ProjectHistoryErrorCodes.OutputHeld,
        message = "This item is not available right now."
    });

    /// <summary>
    /// <c>to</c> is a version id or the literal <c>current</c>; absent means current. Anything else is
    /// a bad request rather than a silently different comparison.
    /// </summary>
    private static bool TryParseTarget(string? to, out Guid? toVersionId)
    {
        toVersionId = null;
        if (string.IsNullOrWhiteSpace(to)
            || string.Equals(to.Trim(), "current", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Guid.TryParse(to.Trim(), out Guid parsed))
        {
            toVersionId = parsed;
            return true;
        }

        return false;
    }

    private const int MaxRestoreItems = 50;

    /// <summary>What restoring the version would change; writes nothing.</summary>
    [HttpPost("versions/{versionId:guid}/restore/preview")]
    public async Task<ActionResult<ProjectHistoryRestorePreviewResponse>> PreviewRestore(
        Guid projectId,
        Guid versionId,
        [FromBody] ProjectHistoryRestoreRequest? request,
        CancellationToken ct)
    {
        // The body is validated before any permission decision: a malformed request is the caller's
        // 400, not a 403/404 that would leak whether the project exists. One access call follows it.
        if (!TryParseRestore(request, out ProjectHistoryRestoreScope scope,
                out List<ProjectHistoryRestoreItemRef> items, out IActionResult? invalid))
            return (ActionResult)invalid!;

        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, PermissionFor(scope), ct);
        if (access.Failure is { } denied) return denied;

        ProjectHistoryRestorePreview? preview = await _restore.PreviewAsync(projectId, versionId, scope, items, ct);
        if (preview is null) return NotFound();
        return Ok(new ProjectHistoryRestorePreviewResponse(
            preview.Scope == ProjectHistoryRestoreScope.Project ? "project" : "item",
            preview.Changes
                .Select(c => new ProjectHistoryItemChangeResponse(
                    c.Kind.ToString(), c.Key, c.Name, c.PreviousName, c.Change))
                .ToList(),
            preview.Blocked
                .Select(b => new ProjectHistoryRestoreBlockedResponse(b.Kind.ToString(), b.Key, b.Name, b.Reason))
                .ToList(),
            preview.EstimateBytes));
    }

    /// <summary>Restores the version (or listed items) after taking a safety version.</summary>
    [HttpPost("versions/{versionId:guid}/restore")]
    public async Task<ActionResult<ProjectHistoryRestoreResponse>> Restore(
        Guid projectId,
        Guid versionId,
        [FromBody] ProjectHistoryRestoreRequest? request,
        CancellationToken ct)
    {
        // Body first, then one access call: the permission is chosen from the parsed scope
        // (item -> Edit, project -> Delete), so an invalid body never becomes a permission decision.
        if (!TryParseRestore(request, out ProjectHistoryRestoreScope scope,
                out List<ProjectHistoryRestoreItemRef> items, out IActionResult? invalid))
            return (ActionResult)invalid!;

        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, PermissionFor(scope), ct);
        if (access.Failure is { } denied) return denied;

        ProjectHistoryRestoreResult? result;
        try
        {
            result = await _restore.RestoreAsync(projectId, versionId, scope, items, _currentUser.UserId, ct);
        }
        catch (ProjectHistoryRestoreConflictException ex)
        {
            return Conflict(new { code = ex.Code, message = ex.Message });
        }
        catch (ProjectHistoryTooLargeException ex)
        {
            // The safety version the restore takes first has the same file cap as a saved version.
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new
            {
                code = ex.Code,
                message = "This project has too many files to restore safely; no changes were made."
            });
        }
        catch (ProjectHistoryNothingToRestoreException ex)
        {
            return BadRequest(new { code = ProjectHistoryNothingToRestoreException.ErrorCode, message = ex.Message });
        }

        if (result is null) return NotFound();
        return Ok(new ProjectHistoryRestoreResponse(
            result.SafetyVersionId,
            result.Restored.Select(e => new ProjectHistoryRestoreEntryResponse(e.Kind.ToString(), e.Key, e.Name)).ToList(),
            result.Removed.Select(e => new ProjectHistoryRestoreEntryResponse(e.Kind.ToString(), e.Key, e.Name)).ToList(),
            result.Skipped.Select(e => new ProjectHistoryRestoreSkippedResponse(e.Kind.ToString(), e.Key, e.Name, e.Reason)).ToList(),
            result.Notices.Select(e => new ProjectHistoryRestoreNoticeResponse(e.Kind.ToString(), e.Key, e.Code)).ToList()));
    }

    private static ProjectPermission PermissionFor(ProjectHistoryRestoreScope scope) =>
        scope == ProjectHistoryRestoreScope.Project
            ? ProjectPermission.Delete
            : ProjectPermission.Edit;

    private bool TryParseRestore(
        ProjectHistoryRestoreRequest? request,
        out ProjectHistoryRestoreScope scope,
        out List<ProjectHistoryRestoreItemRef> items,
        out IActionResult? invalid)
    {
        scope = ProjectHistoryRestoreScope.Item;
        items = new();
        invalid = null;
        if (request is null || string.IsNullOrWhiteSpace(request.Scope))
        {
            invalid = BadRequest(new { code = "INVALID_SCOPE", message = "scope must be 'item' or 'project'." });
            return false;
        }

        if (string.Equals(request.Scope, "project", StringComparison.OrdinalIgnoreCase))
        {
            scope = ProjectHistoryRestoreScope.Project;
            return true;
        }

        if (!string.Equals(request.Scope, "item", StringComparison.OrdinalIgnoreCase))
        {
            invalid = BadRequest(new { code = "INVALID_SCOPE", message = "scope must be 'item' or 'project'." });
            return false;
        }

        if (request.Items is not { Count: > 0 and <= MaxRestoreItems })
        {
            invalid = BadRequest(new
            {
                code = "INVALID_ITEMS",
                message = $"Scope item needs 1 to {MaxRestoreItems} items."
            });
            return false;
        }

        foreach (ProjectHistoryRestoreItemRequest item in request.Items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Key) || !TryParseKind(item.Kind ?? "", out ProjectVersionItemKind kind))
            {
                invalid = InvalidKind();
                return false;
            }

            items.Add(new ProjectHistoryRestoreItemRef(kind, item.Key));
        }

        return true;
    }

    private static bool TryParseKind(string kind, out ProjectVersionItemKind parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(kind)
            || !Enum.TryParse(kind, ignoreCase: true, out parsed))
        {
            return false;
        }

        return Enum.IsDefined(parsed);
    }

    private BadRequestObjectResult InvalidTarget() => BadRequest(new
    {
        code = "INVALID_TARGET",
        message = "'to' must be a version id or 'current'."
    });

    private BadRequestObjectResult InvalidKind() => BadRequest(new
    {
        code = "INVALID_ITEM_KIND",
        message = "The item kind is not one this project records."
    });

    private static ProjectHistoryChangesResponse ToResponse(ProjectHistoryChanges changes) =>
        new(
            changes.Changes
                .Select(c => new ProjectHistoryItemChangeResponse(
                    c.Kind.ToString(), c.Key, c.Name, c.PreviousName, c.Change))
                .ToList(),
            changes.UnchangedCount,
            changes.Truncated);

    private static ProjectHistoryItemDiffResponse ToResponse(ProjectHistoryItemDiff diff) =>
        new(
            diff.Kind.ToString(),
            diff.Key,
            diff.Name,
            diff.Mode,
            diff.Hunks?.Select(hunk => ToResponse(hunk)).ToList(),
            diff.FromSize,
            diff.ToSize,
            diff.Workflow is null
                ? null
                : new ProjectHistoryWorkflowDiffResponse(
                    diff.Workflow.Added
                        .Select(s => new ProjectHistoryWorkflowStepResponse(s.Order, s.Type, s.AgentName))
                        .ToList(),
                    diff.Workflow.Removed
                        .Select(s => new ProjectHistoryWorkflowStepResponse(s.Order, s.Type, s.AgentName))
                        .ToList(),
                    diff.Workflow.Changed
                        .Select(s => new ProjectHistoryWorkflowStepChangeResponse(
                            s.Order, s.Type, s.AgentName, s.ChangedFields))
                        .ToList()),
            diff.Timeline is null
                ? null
                : new ProjectHistoryTimelineDiffResponse(
                    diff.Timeline.FromRevision,
                    diff.Timeline.ToRevision,
                    new ProjectHistoryTimelineSummaryResponse(
                        diff.Timeline.FromSummary.Tracks,
                        diff.Timeline.FromSummary.Clips,
                        diff.Timeline.FromSummary.DurationFrames),
                    new ProjectHistoryTimelineSummaryResponse(
                        diff.Timeline.ToSummary.Tracks,
                        diff.Timeline.ToSummary.Clips,
                        diff.Timeline.ToSummary.DurationFrames)),
            diff.DesignSystem,
            diff.Pronunciation is null
                ? null
                : new ProjectHistoryPronunciationDiffResponse(
                    diff.Pronunciation.Added,
                    diff.Pronunciation.Removed,
                    diff.Pronunciation.Changed),
            diff.Project is null
                ? null
                : new ProjectHistoryProjectDiffResponse(
                    diff.Project.Name is null
                        ? null
                        : new ProjectHistoryFieldChangeResponse(
                            diff.Project.Name.From, diff.Project.Name.To),
                    diff.Project.Description is null
                        ? null
                        : new ProjectHistoryFieldChangeResponse(
                            diff.Project.Description.From, diff.Project.Description.To)));

    private static ProjectHistoryHunkResponse ToResponse(LineDiffHunk hunk) =>
        new(
            hunk.FromStart,
            hunk.FromCount,
            hunk.ToStart,
            hunk.ToCount,
            hunk.Lines
                .Select(line => new ProjectHistoryDiffLineResponse(line.Op, line.Text))
                .ToList());

    private static ProjectHistoryVersionResponse ToResponse(ProjectHistoryVersionInfo info) =>
        new(
            info.Id,
            info.Number,
            info.Name,
            info.Cause,
            info.CreatedAt,
            info.ExpiresAt,
            info.CreatedByName,
            info.ItemCount,
            info.TotalBytes);

    private static ProjectHistoryVersionDetailResponse ToResponse(ProjectHistoryVersionDetail detail) =>
        new(
            detail.Version.Id,
            detail.Version.Number,
            detail.Version.Name,
            detail.Version.Cause,
            detail.Version.CreatedAt,
            detail.Version.ExpiresAt,
            detail.Version.CreatedByName,
            detail.Version.ItemCount,
            detail.Version.TotalBytes,
            detail.Items
                .Select(i => new ProjectHistoryItemResponse(
                    i.Kind.ToString(),
                    i.Key,
                    i.Name,
                    i.Category,
                    i.MimeType,
                    i.SizeBytes,
                    i.Restorable,
                    i.NotRestorableReason))
                .ToList());
}
