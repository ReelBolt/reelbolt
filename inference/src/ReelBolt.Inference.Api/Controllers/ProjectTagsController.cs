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

/// <summary>Free-form project tags of the active organization. Reads need View, writes need org Edit.</summary>
[ApiController]
[Route("api/v1/project-tags")]
[Authorize]
public class ProjectTagsController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;

    public ProjectTagsController(InferenceApiDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess)
    {
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
    }

    [HttpGet]
    public async Task<ActionResult<List<ProjectTagResponse>>> List(CancellationToken ct)
    {
        if (_projectAccess.AuthorizeOrganization(ProjectPermission.View).ToFailure() is { } denied) return denied;
        Guid orgId = _currentUser.OrganizationId!.Value;
        List<ProjectTagResponse> tags = await _db.ProjectTags
            .Where(t => t.OrganizationId == orgId)
            .OrderBy(t => t.NameKey)
            .Select(t => new ProjectTagResponse(t.Id, t.Name, t.Color, t.Links.Count, t.CreatedAt))
            .ToListAsync(ct);
        return Ok(tags);
    }

    [HttpPost]
    public async Task<ActionResult<ProjectTagResponse>> Create([FromBody] ProjectTagRequest request, CancellationToken ct)
    {
        if (_projectAccess.AuthorizeOrganization(ProjectPermission.Edit).ToFailure() is { } denied) return denied;
        Guid orgId = _currentUser.OrganizationId!.Value;
        if (Validate(request, requireName: true, out string? name, out string? color) is { } invalid) return invalid;

        // Cap check and insert share one transaction under a per-org advisory lock so two creates cannot both pass it.
        return await ProjectOrganizationRules.InTransactionAsync<ActionResult<ProjectTagResponse>>(_db, "project-tags", orgId, async () =>
        {
            if (await _db.ProjectTags.CountAsync(t => t.OrganizationId == orgId, ct) >= ProjectOrganizationRules.MaxTagsPerOrganization)
                return Conflict(new { code = "TAG_LIMIT_REACHED", message = "This workspace has reached its tag limit." });

            ProjectTag tag = new()
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                Name = name!,
                NameKey = ProjectOrganizationRules.NameKey(name!),
                Color = color!,
                CreatedAt = DateTime.UtcNow
            };
            _db.ProjectTags.Add(tag);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ProjectOrganizationRules.IsUniqueViolation(ex))
            {
                return NameTaken();
            }
            return Created($"api/v1/project-tags/{tag.Id}", new ProjectTagResponse(tag.Id, tag.Name, tag.Color, 0, tag.CreatedAt));
        }, ct);
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<ProjectTagResponse>> Update(Guid id, [FromBody] ProjectTagRequest request, CancellationToken ct)
    {
        if (_projectAccess.AuthorizeOrganization(ProjectPermission.Edit).ToFailure() is { } denied) return denied;
        Guid orgId = _currentUser.OrganizationId!.Value;
        ProjectTag? tag = await _db.ProjectTags.FirstOrDefaultAsync(t => t.Id == id && t.OrganizationId == orgId, ct);
        if (tag is null) return NotFound();
        if (Validate(request, requireName: false, out string? name, out string? color) is { } invalid) return invalid;

        if (name is not null)
        {
            tag.Name = name;
            tag.NameKey = ProjectOrganizationRules.NameKey(name);
        }
        if (request.Color is not null) tag.Color = color!;

        // Tag name/colour are part of every linked project's response: move their validators in the same transaction,
        // so a 409 leaves no UpdatedAt changed.
        bool taken = await ProjectOrganizationRules.InTransactionAsync(_db, "project-tags", orgId, async () =>
        {
            await ProjectOrganizationRules.BumpAsync(
                _db, _db.Projects.Where(p => p.TagLinks.Any(l => l.TagId == id)), DateTime.UtcNow, clearFolder: false, ct);
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
        int count = await _db.ProjectTagLinks.CountAsync(l => l.TagId == id, ct);
        return Ok(new ProjectTagResponse(tag.Id, tag.Name, tag.Color, count, tag.CreatedAt));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (_projectAccess.AuthorizeOrganization(ProjectPermission.Edit).ToFailure() is { } denied) return denied;
        Guid orgId = _currentUser.OrganizationId!.Value;
        ProjectTag? tag = await _db.ProjectTags.FirstOrDefaultAsync(t => t.Id == id && t.OrganizationId == orgId, ct);
        if (tag is null) return NotFound();

        await ProjectOrganizationRules.InTransactionAsync(_db, "project-tags", orgId, async () =>
        {
            await ProjectOrganizationRules.BumpAsync(
                _db, _db.Projects.Where(p => p.TagLinks.Any(l => l.TagId == id)), DateTime.UtcNow, clearFolder: false, ct);
            _db.ProjectTags.Remove(tag);
            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);
        return NoContent();
    }

    private BadRequestObjectResult? Validate(ProjectTagRequest request, bool requireName, out string? name, out string? color)
    {
        name = null;
        color = null;
        if (request.Name is not null || requireName)
        {
            name = ProjectOrganizationRules.NormalizeName(request.Name, ProjectOrganizationRules.MaxTagNameLength);
            if (name is null)
                return BadRequest(new { code = "TAG_NAME_INVALID", message = $"A tag name is 1 to {ProjectOrganizationRules.MaxTagNameLength} characters with no control characters." });
        }
        if (request.Color is not null || requireName)
        {
            color = ProjectOrganizationRules.NormalizeColor(request.Color);
            if (color is null)
                return BadRequest(new { code = "TAG_COLOR_INVALID", message = $"The colour must be one of: {string.Join(", ", ProjectOrganizationRules.Palette)}." });
        }
        return null;
    }

    private ConflictObjectResult NameTaken() =>
        Conflict(new { code = "TAG_NAME_TAKEN", message = "A tag with that name already exists." });
}
