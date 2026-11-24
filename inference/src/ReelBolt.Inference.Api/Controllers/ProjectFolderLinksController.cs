using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.FolderSync;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The computer folders a project mirrors, as its dashboard sees them
/// (docs/design/local-folder-sync-and-storage.md, "Dashboard API"). Every action resolves the project
/// through <see cref="IProjectAccessService"/> first — <see cref="ProjectPermission.View"/> to read,
/// <see cref="ProjectPermission.Edit"/> to pause, resume or unlink — and a link is only ever looked up
/// within that project. Links are created only from the desktop app (the folder picker is local).
/// Unlinking keeps the files; the computer stops syncing the folder on its next request (<c>404</c>).
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/folder-links")]
[Authorize]
public class ProjectFolderLinksController : ControllerBase
{
    /// <summary>A computer seen within this window is shown as online.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(2);

    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly LocalFolderSyncService _sync;

    public ProjectFolderLinksController(
        InferenceApiDbContext db,
        IProjectAccessService projectAccess,
        LocalFolderSyncService sync)
    {
        _db = db;
        _projectAccess = projectAccess;
        _sync = sync;
    }

    /// <summary>The clock "online" is judged by. Settable for tests.</summary>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>The project's computer folders, with whether each computer is online.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProjectFolderLinkResponse>>> List(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        List<ProjectLocalFolderLink> links = await _db.ProjectLocalFolderLinks
            .Where(l => l.ProjectId == projectId)
            .OrderBy(l => l.TargetFolder)
            .ToListAsync(ct);
        Dictionary<Guid, RunnerDevice> devices = await DevicesAsync(links, ct);

        return Ok(links.Select(l => ToResponse(l, devices.GetValueOrDefault(l.DeviceId))).ToList());
    }

    /// <summary>Pauses syncing a folder (<c>200</c>): the computer's uploads and deletes are refused until it is resumed.</summary>
    [HttpPost("{id:guid}/pause")]
    public async Task<ActionResult<ProjectFolderLinkResponse>> Pause(Guid projectId, Guid id, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        ProjectLocalFolderLink? link = await FindAsync(projectId, id, ct);
        if (link is null) return LinkNotFound();

        _sync.Pause(link, ProjectLocalFolderLink.PauseReasonUser);
        await _db.SaveChangesAsync(ct);
        return Ok(await ResponseAsync(link, ct));
    }

    /// <summary>
    /// Resumes syncing (<c>200</c>). Resuming a link the mass-delete guard paused only lets the next
    /// manifest be judged again; the large deletion itself is confirmed from the desktop app.
    /// </summary>
    [HttpPost("{id:guid}/resume")]
    public async Task<ActionResult<ProjectFolderLinkResponse>> Resume(Guid projectId, Guid id, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        ProjectLocalFolderLink? link = await FindAsync(projectId, id, ct);
        if (link is null) return LinkNotFound();

        _sync.Resume(link);
        await _db.SaveChangesAsync(ct);
        return Ok(await ResponseAsync(link, ct));
    }

    /// <summary>Unlinks a folder (<c>204</c>). Its files stay in the project as ordinary files.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid id, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        ProjectLocalFolderLink? link = await FindAsync(projectId, id, ct);
        if (link is null) return LinkNotFound();

        await _sync.UnlinkAsync(link, ct);
        return NoContent();
    }

    private Task<ProjectLocalFolderLink?> FindAsync(Guid projectId, Guid id, CancellationToken ct) =>
        _db.ProjectLocalFolderLinks.FirstOrDefaultAsync(l => l.Id == id && l.ProjectId == projectId, ct);

    private async Task<Dictionary<Guid, RunnerDevice>> DevicesAsync(IEnumerable<ProjectLocalFolderLink> links, CancellationToken ct)
    {
        List<Guid> ids = links.Select(l => l.DeviceId).Distinct().ToList();
        return await _db.RunnerDevices.Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
    }

    private async Task<ProjectFolderLinkResponse> ResponseAsync(ProjectLocalFolderLink link, CancellationToken ct) =>
        ToResponse(link, await _db.RunnerDevices.FirstOrDefaultAsync(d => d.Id == link.DeviceId, ct));

    private ProjectFolderLinkResponse ToResponse(ProjectLocalFolderLink link, RunnerDevice? device)
    {
        DateTime now = Clock.GetUtcNow().UtcDateTime;
        bool revoked = device?.RevokedAt is not null;
        bool online = !revoked && device?.LastSeenAt is { } seen && now - seen < OnlineWindow;
        return new ProjectFolderLinkResponse(
            link.Id,
            link.DeviceId,
            device?.Name ?? string.Empty,
            online,
            revoked,
            link.TargetFolder,
            link.DisplayPath,
            link.PausedAt is not null,
            link.PausedAt is null ? null : link.PauseReason ?? ProjectLocalFolderLink.PauseReasonUser,
            link.FileCount,
            link.LastChangeAt,
            link.LastReconciledAt,
            link.LastError);
    }

    private NotFoundObjectResult LinkNotFound() =>
        NotFound(new { error = "link_not_found", message = "That computer folder is no longer linked to this project." });
}
