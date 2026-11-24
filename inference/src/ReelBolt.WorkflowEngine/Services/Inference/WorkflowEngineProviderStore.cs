using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Data;

namespace ReelBolt.WorkflowEngine.Services.Inference;

/// <summary>
/// Reads inference providers and per-agent overrides through the WorkflowEngine's read-only,
/// excluded-from-migrations mapping of the tables the Inference API owns.
/// </summary>
public sealed class WorkflowEngineProviderStore : IInferenceProviderStore
{
    private readonly WorkflowEngineDbContext _db;

    public WorkflowEngineProviderStore(WorkflowEngineDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ModelCapabilityView>> LoadEnabledAsync(Guid? organizationId, CancellationToken ct)
    {
        // Scope applied explicitly: platform rows plus this organization's own.
        return await ModelCapabilityView.EnabledFor(_db.ModelCapabilities.AsNoTracking(), organizationId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlySet<Guid>> LoadUnpricedCapabilityIdsAsync(DateTime at, CancellationToken ct)
    {
        return await ModelCapabilityView.UnpricedPlatformIdsAsync(_db.ModelCapabilities.AsNoTracking(), at, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, Guid?>> LoadAgentOverridesAsync(Guid? organizationId, CancellationToken ct)
    {
        if (organizationId is null)
            return new Dictionary<Guid, Guid?>();

        return await _db.AgentDefinitions
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
        // Materialized before grouping: the dictionary is keyed by an enum the provider stores as
        // text, and the id tie-break below is a pure in-memory ordering decision, not something
        // worth pushing into SQL. There is normally exactly one built-in row per type (the seeder
        // guarantees it); ordering by Id makes a duplicate deterministic rather than arbitrary.
        var rows = await _db.AgentDefinitions
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
