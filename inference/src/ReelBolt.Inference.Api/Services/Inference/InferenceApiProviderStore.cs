using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Services.Inference;

/// <summary>
/// Reads inference providers and per-agent overrides from the tables the Inference API owns.
/// </summary>
public sealed class InferenceApiProviderStore : IInferenceProviderStore
{
    private readonly InferenceApiDbContext _db;

    public InferenceApiProviderStore(InferenceApiDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ModelCapabilityView>> LoadEnabledAsync(Guid? organizationId, CancellationToken ct)
    {
        // The caller's organization is NOT necessarily the ambient one (the resolver is a singleton
        // refreshing under whatever flow happened to miss the cache), so the tenant filter is
        // bypassed (for the capability, its model and its provider alike) and the scope is applied
        // explicitly: platform rows plus this organization's own.
        return await ModelCapabilityView.EnabledFor(
                _db.ModelCapabilities.IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName]).AsNoTracking(),
                organizationId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlySet<Guid>> LoadUnpricedCapabilityIdsAsync(DateTime at, CancellationToken ct)
    {
        // Platform rows only, so the tenant filter (which would hide nothing relevant) is bypassed like above.
        return await ModelCapabilityView.UnpricedPlatformIdsAsync(
            _db.ModelCapabilities.IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName]).AsNoTracking(), at, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, Guid?>> LoadAgentOverridesAsync(Guid? organizationId, CancellationToken ct)
    {
        if (organizationId is null)
            return new Dictionary<Guid, Guid?>();

        return await _db.AgentDefinitions
            .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            .AsNoTracking()
            .Where(a => !a.IsBuiltIn && a.OrganizationId == organizationId)
            .Select(a => new { a.Id, a.InferenceProviderId })
            .ToDictionaryAsync(a => a.Id, a => a.InferenceProviderId, ct);
    }

    public async Task<IReadOnlyDictionary<AgentType, Guid?>> LoadOrgAgentOverridesAsync(Guid? organizationId, CancellationToken ct)
    {
        if (organizationId is null)
            return new Dictionary<AgentType, Guid?>();

        return await _db.OrganizationAgentProviderOverrides
            .AsNoTracking()
            .Where(o => o.OrganizationId == organizationId)
            .Select(o => new { o.AgentType, o.InferenceProviderId })
            .ToDictionaryAsync(o => o.AgentType, o => o.InferenceProviderId, ct);
    }

    public async Task<IReadOnlyDictionary<AgentType, Guid?>> LoadBuiltInAgentOverridesAsync(CancellationToken ct)
    {
        // Mirrors WorkflowEngineProviderStore.LoadBuiltInAgentOverridesAsync exactly — see its
        // comment for why the grouping happens in memory and why ties break on Id.
        var rows = await _db.AgentDefinitions
            .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            .AsNoTracking()
            .Where(a => a.IsBuiltIn && a.AgentType != AgentType.Custom)
            .Select(a => new { a.Id, a.AgentType, a.InferenceProviderId })
            .ToListAsync(ct);

        return rows
            .GroupBy(a => a.AgentType)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.Id).First().InferenceProviderId);
    }

    public async Task<AgentReasoningEfforts> LoadAgentReasoningEffortsAsync(Guid? organizationId, CancellationToken ct)
    {
        // Built-ins (shared by every org) plus this organization's own custom agents; another
        // organization's custom agents are never loaded.
        var rows = await _db.AgentDefinitions
            .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            .AsNoTracking()
            .Where(a => a.IsBuiltIn || (organizationId != null && a.OrganizationId == organizationId))
            .Select(a => new { a.Id, a.AgentType, a.IsBuiltIn, a.ReasoningEffort })
            .ToListAsync(ct);

        Dictionary<Guid, string?> byId = rows.ToDictionary(a => a.Id, a => a.ReasoningEffort);
        Dictionary<AgentType, string?> byType = rows
            .Where(a => a.IsBuiltIn && a.AgentType != AgentType.Custom)
            .GroupBy(a => a.AgentType)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.Id).First().ReasoningEffort);

        return new AgentReasoningEfforts(byId, byType);
    }
}
