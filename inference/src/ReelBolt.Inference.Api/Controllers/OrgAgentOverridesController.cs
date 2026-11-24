using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// An organization's provider choice for a BUILT-IN agent type (built-in <c>agent_definitions</c> rows are
/// shared by every org, so the choice lives in <c>organization_agent_provider_overrides</c>, keyed
/// (org, agent type)). Owner/Admin only; the provider must be one the org can actually resolve — an enabled
/// platform-managed row, or the org's own — and a Chat one, since the override feeds chat resolution only.
/// An id outside that set (another org's row, a disabled or missing one) is <c>404</c>.
/// </summary>
[ApiController]
[Route("api/v1/org/agent-overrides")]
[Authorize]
public class OrgAgentOverridesController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IInferenceProviderResolver? _resolver;
    private readonly IOrganizationEntitlements? _entitlements;

    public OrgAgentOverridesController(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IInferenceProviderResolver? resolver = null,
        IOrganizationEntitlements? entitlements = null)
    {
        _db = db;
        _currentUser = currentUser;
        _resolver = resolver;
        _entitlements = entitlements;
    }

    [HttpGet]
    [Filters.ConditionalGet]
    public async Task<ActionResult<List<OrgAgentOverrideResponse>>> List(CancellationToken ct)
    {
        if (OrgAdminGate.Check(this, _currentUser, out ProviderScope scope) is { } denied) return denied;

        List<OrganizationAgentProviderOverride> rows = await _db.OrganizationAgentProviderOverrides
            .AsNoTracking()
            .Where(o => o.OrganizationId == scope.OrganizationId)
            .ToListAsync(ct);
        return Ok(rows.OrderBy(o => o.AgentType.ToString()).Select(MapToResponse).ToList());
    }

    [HttpPut("{agentType}")]
    public async Task<ActionResult<OrgAgentOverrideResponse>> Set(
        string agentType, [FromBody] SetOrgAgentOverrideRequest request, CancellationToken ct)
    {
        if (OrgAdminGate.Check(this, _currentUser, out ProviderScope scope) is { } denied) return denied;
        Guid orgId = scope.OrganizationId!.Value;

        if (!TryParseAgentType(agentType, out AgentType parsed))
            return BadRequest(new { error = $"'{agentType}' is not a built-in agent type that can be overridden." });

        if (request.InferenceProviderId is not { } providerId)
            return BadRequest(new { error = "inferenceProviderId is required; use DELETE to clear the override." });

        bool byo = _entitlements is null || await _entitlements.AllowsByoProvidersAsync(orgId, ct);
        // The id names a model capability (docs/design/model-catalog.md §6); usable = capability, model
        // and provider account all enabled.
        ModelCapability? provider = await _db.ModelCapabilities
            .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == providerId && c.IsEnabled && c.Model.IsEnabled && c.Model.Provider.IsEnabled
                                      && (c.OrganizationId == null || (byo && c.OrganizationId == orgId)), ct);
        // An unpriced platform model is not offered by the picker and the resolver would refuse to run it, so it
        // answers with the same single 404 as any other id the caller cannot use (D5).
        if (provider is not null && provider.OrganizationId is null && provider.PriceSource == ModelPriceSource.Table
            && UnpricedCapabilityGate.Applies(_resolver)
            && !await UnpricedCapabilityGate.IsFullyPricedAsync(_db, provider.Id, provider.Capability, DateTime.UtcNow, ct))
            provider = null;
        if (provider is null) return NotFound(new { error = "Referenced inference provider not found." });

        if (provider.Capability != InferenceProviderCapability.Chat)
            return BadRequest(new { error = "Only a Chat-capability provider can be assigned as an agent's inference provider override." });

        OrganizationAgentProviderOverride? row = await _db.OrganizationAgentProviderOverrides
            .FirstOrDefaultAsync(o => o.OrganizationId == orgId && o.AgentType == parsed, ct);
        if (row is null)
        {
            row = new OrganizationAgentProviderOverride { OrganizationId = orgId, AgentType = parsed };
            _db.OrganizationAgentProviderOverrides.Add(row);
        }
        row.InferenceProviderId = provider.Id;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _resolver?.Invalidate(orgId);
        return Ok(MapToResponse(row));
    }

    [HttpDelete("{agentType}")]
    public async Task<IActionResult> Clear(string agentType, CancellationToken ct)
    {
        if (OrgAdminGate.Check(this, _currentUser, out ProviderScope scope) is { } denied) return denied;
        Guid orgId = scope.OrganizationId!.Value;

        if (!TryParseAgentType(agentType, out AgentType parsed))
            return BadRequest(new { error = $"'{agentType}' is not a built-in agent type that can be overridden." });

        OrganizationAgentProviderOverride? row = await _db.OrganizationAgentProviderOverrides
            .FirstOrDefaultAsync(o => o.OrganizationId == orgId && o.AgentType == parsed, ct);
        if (row is not null)
        {
            _db.OrganizationAgentProviderOverrides.Remove(row);
            await _db.SaveChangesAsync(ct);
        }

        // Idempotent: clearing an override that is not set is still success. Invalidated either way.
        _resolver?.Invalidate(orgId);
        return NoContent();
    }

    /// <summary>
    /// <c>Custom</c> is excluded: a custom agent's provider lives on its own row, set through the agent.
    /// A numeric string is not an agent type name (Enum.TryParse would accept "3").
    /// </summary>
    private static bool TryParseAgentType(string value, out AgentType agentType) =>
        Enum.TryParse(value, ignoreCase: true, out agentType)
        && Enum.IsDefined(agentType)
        && agentType != AgentType.Custom
        && !int.TryParse(value, out _);

    private static OrgAgentOverrideResponse MapToResponse(OrganizationAgentProviderOverride o) =>
        new(o.AgentType.ToString(), o.InferenceProviderId, o.UpdatedAt);
}
