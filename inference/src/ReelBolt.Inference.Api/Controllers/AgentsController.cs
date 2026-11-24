using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class AgentsController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;

    private readonly IInferenceProviderResolver? _providerResolver;

    public AgentsController(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IProjectAccessService projectAccess,
        IInferenceProviderResolver? providerResolver = null)
    {
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _providerResolver = providerResolver;
    }

    [HttpGet]
    [Filters.ConditionalGet]
    public async Task<ActionResult<List<AgentDefinitionResponse>>> List(CancellationToken ct)
    {
        // No active organization = nothing to list (404), and Billing has no agent access at all
        // (403). Every Owner/Admin/Member sees the whole organization's custom agents plus every
        // built-in one — the ADR's "Custom agents: create, edit, delete | yes | yes | yes | no".
        if (_projectAccess.AuthorizeOrganization(ProjectPermission.View).ToFailure() is { } denied) return denied;

        // The EF tenant filter already scopes this; the explicit OrganizationId comparison is the
        // second, independent enforcement the same way ProjectAccessService does it.
        List<AgentDefinition> agents = await _db.AgentDefinitions
            .Include(a => a.InferenceProvider)
            .ThenInclude(c => c!.Model)
            .Where(a => a.IsBuiltIn || a.OrganizationId == _currentUser.OrganizationId)
            .OrderBy(a => a.Name)
            .ToListAsync(ct);
        return Ok(agents.Select(MapToResponse));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AgentDefinitionResponse>> Get(Guid id, CancellationToken ct)
    {
        if (_projectAccess.AuthorizeOrganization(ProjectPermission.View).ToFailure() is { } denied) return denied;

        AgentDefinition? agent = await _db.AgentDefinitions
            .Include(a => a.InferenceProvider)
            .ThenInclude(c => c!.Model)
            .FirstOrDefaultAsync(a => a.Id == id, ct);

        // A built-in agent is visible everywhere; a custom agent only inside its own organization —
        // and one in another organization is 404, never 403 (decision D5).
        if (agent == null || !OrgAuthority.IsVisible(agent, _currentUser)) return NotFound();
        return Ok(MapToResponse(agent));
    }

    [HttpPost]
    public async Task<ActionResult<AgentDefinitionResponse>> Create([FromBody] CreateAgentRequest request, CancellationToken ct)
    {
        if (_projectAccess.AuthorizeOrganization(ProjectPermission.Edit).ToFailure() is { } denied) return denied;

        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Description = request.Description,
            SystemPrompt = request.SystemPrompt,
            AgentType = AgentType.Custom,
            IsBuiltIn = false,
            OwnerId = _currentUser.UserId,
            OrganizationId = ActiveOrganization.Require(_currentUser),
            ConfigJson = request.ConfigJson,
            Color = request.Color,
            CreatedAt = DateTime.UtcNow
        };

        _db.AgentDefinitions.Add(agent);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = agent.Id }, MapToResponse(agent));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<AgentDefinitionResponse>> Update(Guid id, [FromBody] UpdateAgentRequest request, CancellationToken ct)
    {
        if (_projectAccess.AuthorizeOrganization(ProjectPermission.Edit).ToFailure() is { } denied) return denied;

        AgentDefinition? agent = await _db.AgentDefinitions.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (agent == null || !OrgAuthority.IsVisible(agent, _currentUser)) return NotFound();
        // A built-in is visible to everyone and editable by no one. Any Owner/Admin/Member of the
        // agent's own organization may edit it — ownership is the organization's, not the creator's.
        if (agent.IsBuiltIn) return Forbid();

        agent.Name = request.Name;
        agent.Description = request.Description;
        agent.SystemPrompt = request.SystemPrompt;
        agent.ConfigJson = request.ConfigJson;
        agent.Color = request.Color;
        await _db.SaveChangesAsync(ct);

        return Ok(MapToResponse(agent));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (_projectAccess.AuthorizeOrganization(ProjectPermission.Edit).ToFailure() is { } denied) return denied;

        AgentDefinition? agent = await _db.AgentDefinitions.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (agent == null || !OrgAuthority.IsVisible(agent, _currentUser)) return NotFound();
        if (agent.IsBuiltIn) return Forbid();

        // A past run keeps the steps it executed: its ExecutionSnapshot (or a legacy archive) is a
        // kind-5 copy whose steps still name this agent, and workflow_steps.agent_definition_id is
        // ON DELETE RESTRICT. Refuse plainly, with how many runs keep it, instead of failing inside the
        // database. Steps of the user's own (Standard) workflows are left to the RESTRICT exactly as
        // before.
        List<Guid> runStepIds = await _db.WorkflowSteps
            .Where(s => s.AgentDefinitionId == id && s.WorkflowDefinition.Kind == WorkflowDefinitionKind.ExecutionSnapshot)
            .Select(s => s.Id)
            .ToListAsync(ct);
        if (runStepIds.Count > 0)
        {
            List<Guid> snapshotIds = await _db.WorkflowSteps
                .Where(s => runStepIds.Contains(s.Id))
                .Select(s => s.WorkflowDefinitionId)
                .Distinct()
                .ToListAsync(ct);
            int runCount = (await _db.WorkflowExecutions
                    .Where(e => e.SnapshotDefinitionId != null && snapshotIds.Contains(e.SnapshotDefinitionId.Value))
                    .Select(e => e.Id)
                    .ToListAsync(ct))
                .Union(await _db.WorkflowStepResults
                    .Where(r => runStepIds.Contains(r.WorkflowStepId))
                    .Select(r => r.WorkflowExecutionId)
                    .Distinct()
                    .ToListAsync(ct))
                .Count();
            return Conflict(new
            {
                code = "AGENT_IN_USE_BY_RUNS",
                runCount,
                message = $"This agent can't be deleted: {runCount} past run(s) keep the steps they ran with it."
            });
        }

        _db.AgentDefinitions.Remove(agent);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Sets (or clears) the per-agent inference provider override. For a <b>built-in</b> agent that
    /// row is the platform-wide default every organization inherits, so it stays platform-admin
    /// only — and, unlike <see cref="Update"/>, it is explicitly allowed for built-ins, since
    /// routing a chatty analysis agent to a cheaper local model is the primary use case. For a
    /// <b>custom</b> agent the override is the owning organization's own resource, so it is that
    /// organization's Owner/Admin action (A8).
    /// </summary>
    [HttpPut("{id:guid}/inference-provider")]
    public async Task<ActionResult<AgentDefinitionResponse>> SetInferenceProvider(
        Guid id, [FromBody] SetAgentInferenceProviderRequest request, CancellationToken ct)
    {
        AgentDefinition? agent = await _db.AgentDefinitions
            .Include(a => a.InferenceProvider)
            .ThenInclude(c => c!.Model)
            .FirstOrDefaultAsync(a => a.Id == id, ct);

        // Another organization's custom agent is 404, never 403 (D5) — checked before either
        // authority rule, so the answer never depends on which authority the caller holds.
        if (agent == null || !OrgAuthority.IsVisible(agent, _currentUser)) return NotFound();

        if (agent.IsBuiltIn)
        {
            if (!_currentUser.IsPlatformAdmin) return Forbid();
        }
        else if (OrgAuthority.RequireOrgAdmin(this, _currentUser) is { } denied)
        {
            return denied;
        }

        if (request.InferenceProviderId.HasValue)
        {
            // This override feeds chat-completion resolution only (InferenceProviderResolver.
            // ResolveAsync) — a Transcription-capability provider would build an IChatClient
            // against an ASR endpoint. Reject it here rather than relying on the resolver's own
            // defense-in-depth check or the UI's picker filter, neither of which is a server
            // boundary (found by Copilot review).
            // The id names a model capability (docs/design/model-catalog.md §6).
            // A built-in row is the platform default for every organization, so only a PLATFORM capability may be
            // set on it: the platform admin's own organization's capability is invisible to everyone else.
            bool platformOnly = agent.IsBuiltIn;
            InferenceProviderCapability? providerCapability = await _db.ModelCapabilities
                .Where(c => c.Id == request.InferenceProviderId.Value && (!platformOnly || c.OrganizationId == null))
                .Select(c => (InferenceProviderCapability?)c.Capability)
                .FirstOrDefaultAsync(ct);
            if (providerCapability == null)
            {
                return BadRequest(new { error = "Referenced inference provider not found." });
            }
            if (providerCapability != InferenceProviderCapability.Chat)
            {
                return BadRequest(new { error = "Only a Chat-capability provider can be assigned as an agent's inference provider override." });
            }
        }

        agent.InferenceProviderId = request.InferenceProviderId;
        await _db.SaveChangesAsync(ct);
        // A built-in row is the PLATFORM override every org falls back to, so every snapshot is stale.
        _providerResolver?.InvalidateAll();

        // Reload the navigation property so the response reflects the new (or cleared) provider.
        await _db.Entry(agent).Reference(a => a.InferenceProvider).LoadAsync(ct);
        if (agent.InferenceProvider is { } capability)
            await _db.Entry(capability).Reference(c => c.Model).LoadAsync(ct);

        return Ok(MapToResponse(agent));
    }

    /// <summary>
    /// Sets (or clears) how hard the agent's model is asked to think. Same authority as
    /// <see cref="SetInferenceProvider"/>: for a <b>built-in</b> agent the row is the platform-wide
    /// default every organization inherits, so it is platform-admin only (and, unlike
    /// <see cref="Update"/>, explicitly allowed for built-ins); for a <b>custom</b> agent it is the owning
    /// organization's Owner/Admin action (A8). The value must be one of
    /// <see cref="AgentReasoningEffort.Vocabulary"/>; <c>null</c> clears it back to the code default.
    /// </summary>
    [HttpPut("{id:guid}/reasoning-effort")]
    public async Task<ActionResult<AgentDefinitionResponse>> SetReasoningEffort(
        Guid id, [FromBody] SetAgentReasoningEffortRequest request, CancellationToken ct)
    {
        AgentDefinition? agent = await _db.AgentDefinitions
            .Include(a => a.InferenceProvider)
            .ThenInclude(c => c!.Model)
            .FirstOrDefaultAsync(a => a.Id == id, ct);

        // Another organization's custom agent is 404, never 403 (D5) - before either authority rule.
        if (agent == null || !OrgAuthority.IsVisible(agent, _currentUser)) return NotFound();

        if (agent.IsBuiltIn)
        {
            if (!_currentUser.IsPlatformAdmin) return Forbid();
        }
        else if (OrgAuthority.RequireOrgAdmin(this, _currentUser) is { } denied)
        {
            return denied;
        }

        string? effort = null;
        if (request.ReasoningEffort is not null)
        {
            effort = AgentReasoningEffort.Normalize(request.ReasoningEffort);
            if (effort is null)
            {
                return BadRequest(new
                {
                    error = $"ReasoningEffort must be one of: {string.Join(", ", AgentReasoningEffort.Vocabulary)} (or null to use the default)."
                });
            }
        }

        agent.ReasoningEffort = effort;
        await _db.SaveChangesAsync(ct);
        // A built-in row is the PLATFORM value every org falls back to, so every snapshot is stale.
        _providerResolver?.InvalidateAll();

        return Ok(MapToResponse(agent));
    }

    /// <summary>
    /// Public (not private) so SkillsController.SetSkills can reuse it after updating
    /// AssignedSkillsJson, keeping the response shape identical to every other agent mutation
    /// endpoint.
    /// </summary>
    public static AgentDefinitionResponse MapToResponse(AgentDefinition a)
    {
        string[]? tools = a.AvailableToolsJson != null
            ? System.Text.Json.JsonSerializer.Deserialize<string[]>(a.AvailableToolsJson)
            : null;

        // AssignedSkills is only meaningful for a custom agent — a built-in row's
        // AssignedSkillsJson is never set (see AgentDefinition.AssignedSkillsJson doc comment),
        // so this naturally comes back null for built-ins.
        string[]? assignedSkills = a.AssignedSkillsJson != null
            ? System.Text.Json.JsonSerializer.Deserialize<string[]>(a.AssignedSkillsJson)
            : null;

        // EffectiveSkills resolves the "where do this agent's skills actually come from" rule
        // server-side: SkillCatalog.DefaultsFor(agentType) for a built-in, else whatever was
        // explicitly assigned (empty, not null, when nothing was).
        string[] effectiveSkills = a.IsBuiltIn
            ? [.. ReelBolt.Shared.Skills.SkillCatalog.DefaultsFor(a.AgentType)]
            : assignedSkills ?? [];

        return new AgentDefinitionResponse(
            a.Id, a.Name, a.Description, a.SystemPrompt,
            a.AgentType.ToString(), a.IsBuiltIn, a.OwnerId, a.ConfigJson,
            a.CreatedAt, a.Color, a.OutputSchemaJson,
            tools, a.GeneratesOutput, a.OutputSchemaName,
            a.InferenceProviderId, a.InferenceProvider?.Model?.DisplayName,
            assignedSkills, effectiveSkills,
            a.OrganizationId,
            a.ReasoningEffort);
    }
}
