using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>Project folders of the active organization (one level in v1). Reads need View, writes need org Edit.</summary>
[ApiController]
[Route("api/v1/project-folders")]
[Authorize]
public class ProjectFoldersController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;

    public ProjectFoldersController(InferenceApiDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess)
    {
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
    }

    [HttpGet]
    public async Task<ActionResult<List<ProjectFolderResponse>>> List(CancellationToken ct)
    {
        if (_projectAccess.AuthorizeOrganization(ProjectPermission.View).ToFailure() is { } denied) return denied;
        Guid orgId = _currentUser.OrganizationId!.Value;
        List<ProjectFolder> folders = await _db.ProjectFolders
            .Where(f => f.OrganizationId == orgId)
            .OrderBy(f => f.NameKey)
            .ToListAsync(ct);
        Dictionary<Guid, int> counts = await _db.Projects
            .Where(p => p.FolderId != null)
            .GroupBy(p => p.FolderId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        return Ok(folders.Select(f => new ProjectFolderResponse(f.Id, f.Name, counts.GetValueOrDefault(f.Id), f.CreatedAt)).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<ProjectFolderResponse>> Create([FromBody] ProjectFolderRequest request, CancellationToken ct)
    {
        if (_projectAccess.AuthorizeOrganization(ProjectPermission.Edit).ToFailure() is { } denied) return denied;
        Guid orgId = _currentUser.OrganizationId!.Value;
        if (Validate(request) is not { } name) return InvalidName();

        return await ProjectOrganizationRules.InTransactionAsync<ActionResult<ProjectFolderResponse>>(_db, "project-folders", orgId, async () =>
        {
            if (await _db.ProjectFolders.CountAsync(f => f.OrganizationId == orgId, ct) >= ProjectOrganizationRules.MaxFoldersPerOrganization)
                return Conflict(new { code = "FOLDER_LIMIT_REACHED", message = "This workspace has reached its folder limit." });

            ProjectFolder folder = new()
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                Name = name,
                NameKey = ProjectOrganizationRules.NameKey(name),
                CreatedAt = DateTime.UtcNow
            };
            _db.ProjectFolders.Add(folder);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ProjectOrganizationRules.IsUniqueViolation(ex))
            {
                return NameTaken();
            }
            return Created($"api/v1/project-folders/{folder.Id}", new ProjectFolderResponse(folder.Id, folder.Name, 0, folder.CreatedAt));
        }, ct);
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<ProjectFolderResponse>> Rename(Guid id, [FromBody] ProjectFolderRequest request, CancellationToken ct)
    {
        if (_projectAccess.AuthorizeOrganization(ProjectPermission.Edit).ToFailure() is { } denied) return denied;
        Guid orgId = _currentUser.OrganizationId!.Value;
        ProjectFolder? folder = await _db.ProjectFolders.FirstOrDefaultAsync(f => f.Id == id && f.OrganizationId == orgId, ct);
        if (folder is null) return NotFound();
        if (Validate(request) is not { } name) return InvalidName();

        folder.Name = name;
        folder.NameKey = ProjectOrganizationRules.NameKey(name);
        // The folder id is in each project's response, the name is not; still bump so a client that keys on updatedAt refreshes.
        bool taken = await ProjectOrganizationRules.InTransactionAsync(_db, "project-folders", orgId, async () =>
        {
            await ProjectOrganizationRules.BumpAsync(
                _db, _db.Projects.Where(p => p.FolderId == id), DateTime.UtcNow, clearFolder: false, ct);
            try
            {
                await _db.SaveChangesAsync(ct);
                return false;
            }
            catch (DbUpdateException ex) when (ProjectOrganizationRules.IsUniqueViolation(ex))
            {
                return true;
            }
        }, ct, commitWhen: isTaken => !isTaken);
        if (taken) return NameTaken();
        int count = await _db.Projects.CountAsync(p => p.FolderId == id, ct);
        return Ok(new ProjectFolderResponse(folder.Id, folder.Name, count, folder.CreatedAt));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (_projectAccess.AuthorizeOrganization(ProjectPermission.Edit).ToFailure() is { } denied) return denied;
        Guid orgId = _currentUser.OrganizationId!.Value;
        ProjectFolder? folder = await _db.ProjectFolders.FirstOrDefaultAsync(f => f.Id == id && f.OrganizationId == orgId, ct);
        if (folder is null) return NotFound();

        // The FK is ON DELETE SET NULL; clear it here too (and move the projects' validators) so tracked state and ETags agree.
        await ProjectOrganizationRules.InTransactionAsync(_db, "project-folders", orgId, async () =>
        {
            await ProjectOrganizationRules.BumpAsync(
                _db, _db.Projects.Where(p => p.FolderId == id), DateTime.UtcNow, clearFolder: true, ct);
            _db.ProjectFolders.Remove(folder);
            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);
        return NoContent();
    }

    private static string? Validate(ProjectFolderRequest request) =>
        ProjectOrganizationRules.NormalizeName(request.Name, ProjectOrganizationRules.MaxFolderNameLength);

    private BadRequestObjectResult InvalidName() =>
        BadRequest(new { code = "FOLDER_NAME_INVALID", message = $"A folder name is 1 to {ProjectOrganizationRules.MaxFolderNameLength} characters with no control characters." });

    private ConflictObjectResult NameTaken() =>
        Conflict(new { code = "FOLDER_NAME_TAKEN", message = "A folder with that name already exists." });
}
