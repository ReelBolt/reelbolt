using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Skills;
using System.Text.Json;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Read-only skill catalog listing plus the organization-scoped per-agent skill assignment
/// endpoint (an org Owner/Admin action — A8).
///
/// Skill assignment is editable ONLY for a custom (non-built-in) AgentDefinition — a built-in
/// agent's skills come exclusively from SkillCatalog.DefaultsFor(AgentType) in code and the
/// seeded prompt, never from a runtime override. This is the OPPOSITE of how the per-agent
/// inference-provider override works (AgentsController.SetInferenceProvider, deliberately
/// allowed for built-ins) — SetSkills below is modeled on AgentsController.Update's
/// built-in-forbid pattern instead.
///
/// Routed under /api/v1/skills (and /api/v1/agents/{id}/skills for the PUT), not
/// /api/v1/admin/*, for the same reason as /api/v1/inference-providers: nginx routes
/// /api/v1/admin/* to the Go API, so an Inference-API-owned admin endpoint must live elsewhere.
/// </summary>
[ApiController]
[Route("api/v1/skills")]
[Authorize]
public class SkillsController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IWebHostEnvironment _env;

    public SkillsController(InferenceApiDbContext db, ICurrentUser currentUser, IWebHostEnvironment env)
    {
        _db = db;
        _currentUser = currentUser;
        _env = env;
    }

    [HttpGet]
    [Filters.ConditionalGet]
    public async Task<ActionResult<List<SkillSummaryResponse>>> List(CancellationToken ct)
    {
        Dictionary<string, int> assignedCounts = await ComputeAssignedCountsAsync(ct);

        List<SkillSummaryResponse> result = SkillCatalog.All
            .Select(s => new SkillSummaryResponse(
                s.Name,
                s.DisplayName,
                s.Description,
                s.Category.ToString(),
                s.Version,
                DefaultForAgentTypes(s.Name),
                assignedCounts.GetValueOrDefault(s.Name),
                s.SourceUrl,
                s.SourceCommit))
            .ToList();

        return Ok(result);
    }

    [HttpGet("{name:regex(^[[a-z0-9-]]+$)}")]
    public async Task<ActionResult<SkillDetailResponse>> Get(string name, CancellationToken ct)
    {
        SkillDescriptor? descriptor = SkillCatalog.Find(name);
        if (descriptor == null) return NotFound();

        Dictionary<string, int> assignedCounts = await ComputeAssignedCountsAsync(ct);
        string? body = await TryReadSkillBodyAsync(descriptor, ct);

        return Ok(new SkillDetailResponse(
            descriptor.Name,
            descriptor.DisplayName,
            descriptor.Description,
            descriptor.Category.ToString(),
            descriptor.Version,
            DefaultForAgentTypes(descriptor.Name),
            assignedCounts.GetValueOrDefault(descriptor.Name),
            body,
            descriptor.SourceUrl,
            descriptor.SourceCommit));
    }

    /// <summary>
    /// Sets (or clears) a custom agent's skill assignment. Organization-scoped (A8): another
    /// organization's custom agent is 404, and inside the caller's own organization the action
    /// needs Owner or Admin (docs/design/tenancy.md, "Assign skills to org custom agents"). Unlike
    /// AgentsController.SetInferenceProvider, this is explicitly NOT allowed for built-in
    /// agents — a built-in's skills come from code, full stop. See the class-level doc comment.
    /// </summary>
    [HttpPut("/api/v1/agents/{id:guid}/skills")]
    public async Task<ActionResult<AgentDefinitionResponse>> SetSkills(
        Guid id, [FromBody] SetAgentSkillsRequest request, CancellationToken ct)
    {
        AgentDefinition? agent = await _db.AgentDefinitions
            .Include(a => a.InferenceProvider)
            .ThenInclude(c => c!.Model)
            .FirstOrDefaultAsync(a => a.Id == id, ct);

        // Another organization's custom agent is 404, never 403 (decision D5).
        if (agent == null || !OrgAuthority.IsVisible(agent, _currentUser)) return NotFound();

        // The one thing to get right: skill assignment is editable ONLY for custom agents.
        // Mirrors AgentsController.Update's Forbid() (not SetInferenceProvider's allow-built-ins
        // pattern), since a built-in's skills come exclusively from
        // SkillCatalog.DefaultsFor(AgentType) in code — never from this column.
        if (agent.IsBuiltIn) return Forbid();

        // Inside the agent's own organization this is an Owner/Admin action (docs/design/tenancy.md,
        // "Assign skills to org custom agents"): a Member gets 403, never 404.
        if (OrgAuthority.RequireOrgAdmin(this, _currentUser) is { } denied) return denied;

        string[] skills = request.Skills ?? [];
        string[] unknown = skills
            .Where(s => SkillCatalog.Find(s) == null)
            .ToArray();
        if (unknown.Length > 0)
        {
            return BadRequest(new { error = "Unknown skill name(s).", skills = unknown });
        }

        // Store the catalog's canonical names (deduplicated), not the caller's casing — every
        // later consumer then matches by simple equality instead of depending on each read path
        // remembering to compare case-insensitively.
        string[] canonical = skills
            .Select(s => SkillCatalog.Find(s)!.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        agent.AssignedSkillsJson = JsonSerializer.Serialize(canonical);
        await _db.SaveChangesAsync(ct);

        return Ok(AgentsController.MapToResponse(agent));
    }

    /// <summary>Reverse-indexes SkillCatalog.DefaultsFor across every AgentType for one skill name.</summary>
    private static string[] DefaultForAgentTypes(string skillName) =>
        Enum.GetValues<AgentType>()
            .Where(t => SkillCatalog.DefaultsFor(t).Contains(skillName, StringComparer.OrdinalIgnoreCase))
            .Select(t => t.ToString())
            .ToArray();

    /// <summary>How many custom AgentDefinition rows currently have this skill assigned.</summary>
    private async Task<Dictionary<string, int>> ComputeAssignedCountsAsync(CancellationToken ct)
    {
        List<string?> jsonValues = await _db.AgentDefinitions
            .Where(a => !a.IsBuiltIn && a.AssignedSkillsJson != null)
            .Select(a => a.AssignedSkillsJson)
            .ToListAsync(ct);

        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? json in jsonValues)
        {
            if (string.IsNullOrEmpty(json)) continue;

            string[]? names;
            try
            {
                names = JsonSerializer.Deserialize<string[]>(json);
            }
            catch (JsonException)
            {
                continue;
            }

            if (names == null) continue;

            foreach (string name in names)
            {
                counts[name] = counts.GetValueOrDefault(name) + 1;
            }
        }

        return counts;
    }

    /// <summary>
    /// Reads a skill's SKILL.md body from the vendored corpus, if reachable. Returns null (never
    /// throws) when the corpus isn't present — e.g. this worktree's Dockerfile doesn't yet COPY
    /// inference/skills/ into the image, since that vendored corpus doesn't exist here yet
    /// (a sibling effort's responsibility — see SkillCatalog.cs's stub note).
    /// </summary>
    private async Task<string?> TryReadSkillBodyAsync(SkillDescriptor descriptor, CancellationToken ct)
    {
        string path = Path.Combine(_env.ContentRootPath, "skills", descriptor.RelativePath, "SKILL.md");
        try
        {
            if (!System.IO.File.Exists(path)) return null;
            string raw = await System.IO.File.ReadAllTextAsync(path, ct);

            // The admin view shows what the agent is given: the ReelBolt-authored preface (when
            // the skill has one) in front of the upstream text.
            string prefacePath = Path.Combine(
                _env.ContentRootPath, "skills", descriptor.RelativePath, SkillPreface.FileName);
            string? preface = System.IO.File.Exists(prefacePath)
                ? await System.IO.File.ReadAllTextAsync(prefacePath, ct)
                : null;

            return SkillPreface.Compose(preface, raw);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
