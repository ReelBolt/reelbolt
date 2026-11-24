using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Styles;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Editing styles (see docs/editing-styles.md): the built-in presets, and the caller's own saved
/// styles. Owner-scoped: another user's style — or a project-pinned style whose project is not the
/// caller's — is a 404, never a 403, so a guessed id is not confirmed to a stranger.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/styles")]
public class StylesController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;

    public StylesController(InferenceApiDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess)
    {
        _projectAccess = projectAccess;
        _db = db;
        _currentUser = currentUser;
    }

    /// <summary>The built-in presets everyone can pick.</summary>
    [HttpGet("presets")]
    public ActionResult<List<StylePresetResponse>> ListPresets() =>
        Ok(StylePresetCatalog.All.Select(StylePresetResponse.From).ToList());

    /// <summary>
    /// The caller's saved styles, newest first. With <paramref name="projectId"/>: the styles usable
    /// in that project (global ones plus the ones pinned to it); without: every style the caller owns.
    /// </summary>
    [HttpGet]
    [Filters.ConditionalGet]
    public async Task<ActionResult<List<EditingStyleResponse>>> List([FromQuery] Guid? projectId, CancellationToken ct)
    {
        IQueryable<EditingStyle> query = _db.EditingStyles.AsNoTracking().Where(s => s.OwnerId == _currentUser.UserId);
        if (projectId is { } pid)
        {
            if (!await OwnsProjectAsync(pid, ct))
                return NotFound();
            query = query.Where(s => s.ProjectId == null || s.ProjectId == pid);
        }

        List<EditingStyle> styles = await query.OrderByDescending(s => s.UpdatedAt).ToListAsync(ct);
        return Ok(styles.Select(EditingStyleResponse.From).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EditingStyleResponse>> Get(Guid id, CancellationToken ct)
    {
        EditingStyle? style = await FindOwnedAsync(id, ct);
        return style is null ? NotFound() : Ok(EditingStyleResponse.From(style));
    }

    [HttpPost]
    public async Task<ActionResult<EditingStyleResponse>> Create([FromBody] SaveEditingStyleRequest request, CancellationToken ct)
    {
        (string name, string? description, StyleConfig config, string? error) =
            EditingStyleRules.Normalize(request.Name, request.Description, request.Config);
        if (error is not null)
            return BadRequest(new { message = error });

        if (request.ProjectId is { } pid && !await OwnsProjectAsync(pid, ct))
            return NotFound(new { message = "Project not found." });

        int count = await _db.EditingStyles.CountAsync(s => s.OwnerId == _currentUser.UserId, ct);
        if (count >= EditingStyleRules.MaxStylesPerUser)
            return BadRequest(new { message = $"You can keep at most {EditingStyleRules.MaxStylesPerUser} styles. Delete one first." });

        DateTime now = DateTime.UtcNow;
        var style = new EditingStyle
        {
            Id = Guid.NewGuid(),
            OwnerId = _currentUser.UserId,
            ProjectId = request.ProjectId,
            Name = name,
            Description = description,
            ConfigJson = StyleJson.Serialize(config),
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.EditingStyles.Add(style);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = style.Id }, EditingStyleResponse.From(style));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<EditingStyleResponse>> Update(Guid id, [FromBody] SaveEditingStyleRequest request, CancellationToken ct)
    {
        EditingStyle? style = await FindOwnedAsync(id, ct, tracked: true);
        if (style is null)
            return NotFound();

        (string name, string? description, StyleConfig config, string? error) =
            EditingStyleRules.Normalize(request.Name, request.Description, request.Config);
        if (error is not null)
            return BadRequest(new { message = error });

        if (request.ProjectId is { } pid && !await OwnsProjectAsync(pid, ct))
            return NotFound(new { message = "Project not found." });

        style.Name = name;
        style.Description = description;
        style.ProjectId = request.ProjectId;
        style.ConfigJson = StyleJson.Serialize(config);
        style.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(EditingStyleResponse.From(style));
    }

    /// <summary>Deletes a saved style. Past runs keep their own snapshot of it.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        EditingStyle? style = await FindOwnedAsync(id, ct, tracked: true);
        if (style is null)
            return NotFound();

        _db.EditingStyles.Remove(style);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<bool> OwnsProjectAsync(Guid projectId, CancellationToken ct) =>
        (await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct)).IsFound;

    private Task<EditingStyle?> FindOwnedAsync(Guid id, CancellationToken ct, bool tracked = false)
    {
        IQueryable<EditingStyle> query = tracked ? _db.EditingStyles : _db.EditingStyles.AsNoTracking();
        return query.FirstOrDefaultAsync(s => s.Id == id && s.OwnerId == _currentUser.UserId, ct);
    }
}
