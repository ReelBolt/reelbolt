using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Billing;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The model picture for every built-in agent in the active workspace: what the platform recommends (and why), what the
/// workspace chose (<c>organization_agent_provider_overrides</c>, written through <see cref="OrgAgentOverridesController"/>),
/// and what the agent really resolves to. Owner/Admin only, with the same 403/404 as its sibling. Read-only.
/// </summary>
/// <remarks>
/// A model is only reported when the workspace can use it: an enabled Chat capability of an enabled model and account,
/// either a PLATFORM one (fully priced while unpriced models fail closed) or the workspace's own while its plan still
/// allows its own providers. The recommendation is a platform model only. Everything else reads as absent, so an id or a
/// name from another workspace cannot appear here.
/// </remarks>
[ApiController]
[Route("api/v1/org/agent-models")]
[Authorize]
public class OrgAgentModelsController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IInferenceProviderResolver _resolver;
    private readonly IOrganizationEntitlements? _entitlements;

    public OrgAgentModelsController(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IInferenceProviderResolver resolver,
        IOrganizationEntitlements? entitlements = null)
    {
        _db = db;
        _currentUser = currentUser;
        _resolver = resolver;
        _entitlements = entitlements;
    }

    [HttpGet]
    [Filters.ConditionalGet]
    public async Task<ActionResult<List<OrgAgentModelResponse>>> List(CancellationToken ct)
    {
        if (OrgAdminGate.Check(this, _currentUser, out ProviderScope scope) is { } denied) return denied;
        Guid orgId = scope.OrganizationId!.Value;

        bool byo = _entitlements is null || await _entitlements.AllowsByoProvidersAsync(orgId, ct);
        Dictionary<Guid, ModelCapabilityView> usable = await UsableChatModelsAsync(orgId, byo, ct);

        List<AgentDefinition> builtIns = await _db.AgentDefinitions
            .AsNoTracking()
            .Where(a => a.IsBuiltIn && a.AgentType != AgentType.Custom)
            .ToListAsync(ct);
        Dictionary<AgentType, Guid?> choices = (await _db.OrganizationAgentProviderOverrides
                .AsNoTracking()
                .Where(o => o.OrganizationId == orgId)
                .ToListAsync(ct))
            .ToDictionary(o => o.AgentType, o => (Guid?)o.InferenceProviderId);
        AgentRecommendationSeed seed = AgentRecommendationSeed.LoadDefault();

        List<OrgAgentModelResponse> rows = [];
        foreach (AgentDefinition agent in builtIns.OrderBy(a => a.AgentType.ToString(), StringComparer.Ordinal))
        {
            ModelCapabilityView? recommended = agent.InferenceProviderId is { } rid
                && usable.TryGetValue(rid, out ModelCapabilityView? r) && r.OrganizationId is null ? r : null;
            AgentRecommendation? seeded = seed.For(agent.AgentType);
            string? why = recommended is not null && seeded is not null
                && recommended.Kind == seeded.Kind && string.Equals(recommended.ModelName, seeded.ModelCode, StringComparison.Ordinal)
                ? seeded.Why
                : null;

            ModelCapabilityView? choice = choices.GetValueOrDefault(agent.AgentType) is { } cid
                && usable.TryGetValue(cid, out ModelCapabilityView? c) ? c : null;

            ModelCapabilityView? effective = await EffectiveAsync(agent.AgentType, usable, ct);

            string source = choice is not null && effective?.Id == choice.Id ? "workspace"
                : recommended is not null && effective?.Id == recommended.Id ? "recommended"
                : "default";

            rows.Add(new OrgAgentModelResponse(
                agent.AgentType.ToString(),
                recommended?.Id, recommended?.Name, why,
                choice?.Id,
                effective?.Id, effective?.Name,
                source));
        }

        return Ok(rows);
    }

    /// <summary>
    /// What the resolver answers for this workspace and agent type, as a usable model; null when nothing resolves (the
    /// unpriced refusal), or it answers with something that is not a model this workspace can use (the config fallback).
    /// </summary>
    private async Task<ModelCapabilityView?> EffectiveAsync(AgentType type, Dictionary<Guid, ModelCapabilityView> usable, CancellationToken ct)
    {
        try
        {
            ResolvedInferenceProvider resolved = await _resolver.ResolveAsync(type, null, ct);
            return resolved.ProviderId is { } id && usable.TryGetValue(id, out ModelCapabilityView? view) ? view : null;
        }
        catch (ModelUnpricedException)
        {
            return null;
        }
    }

    /// <summary>The Chat models this workspace may use, by capability id (the resolver's visibility rules).</summary>
    private async Task<Dictionary<Guid, ModelCapabilityView>> UsableChatModelsAsync(Guid orgId, bool byo, CancellationToken ct)
    {
        IQueryable<ModelCapability> all = _db.ModelCapabilities.IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName]);
        List<ModelCapabilityView> rows = await all
            .AsNoTracking()
            .Where(c => c.Capability == InferenceProviderCapability.Chat && c.IsEnabled && c.Model.IsEnabled && c.Model.Provider.IsEnabled
                        && (c.OrganizationId == null || (byo && c.OrganizationId == orgId)))
            .Select(ModelCapabilityView.Projection)
            .ToListAsync(ct);

        IReadOnlySet<Guid> unpriced = UnpricedCapabilityGate.Applies(_resolver)
            ? await ModelCapabilityView.UnpricedPlatformIdsAsync(all.Where(c => c.Capability == InferenceProviderCapability.Chat), DateTime.UtcNow, ct)
            : new HashSet<Guid>();
        return rows.Where(r => !unpriced.Contains(r.Id)).ToDictionary(r => r.Id);
    }
}
