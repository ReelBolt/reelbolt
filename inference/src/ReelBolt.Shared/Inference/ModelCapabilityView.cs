using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// One model capability flattened with its model and provider account (docs/design/model-catalog.md §5.1):
/// what the resolver, <see cref="ProviderHealthSelector"/> and the interim flat admin contract work over.
/// The members carry the names of the old one-table <c>InferenceProvider</c> entity, so the resolution
/// logic reads exactly as it did: <see cref="Id"/> is the CAPABILITY id (the old provider row id for a
/// migrated row), <see cref="Name"/> the model's display name and <see cref="ModelName"/> its model code.
/// </summary>
public sealed class ModelCapabilityView
{
    /// <summary>The model capability id (what every "provider id" reference holds).</summary>
    public Guid Id { get; set; }

    public Guid? OrganizationId { get; set; }

    /// <summary>The model's display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The provider account's kind.</summary>
    public InferenceProviderKind Kind { get; set; }

    public InferenceProviderCapability Capability { get; set; } = InferenceProviderCapability.Chat;

    /// <summary>The provider account's endpoint.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The model code sent to the vendor.</summary>
    public string ModelName { get; set; } = string.Empty;

    public string? ApiKeyEncrypted { get; set; }

    public string? ApiKeyLastFour { get; set; }

    public bool IsDefault { get; set; }

    /// <summary>True only when the capability, its model and its provider are all enabled.</summary>
    public bool IsEnabled { get; set; } = true;

    public bool PersonalOAuthMode { get; set; }

    public int? TimeoutSeconds { get; set; }

    public string? ExtraHeadersJson { get; set; }

    public DateTime? LastTestAt { get; set; }

    public bool? LastTestOk { get; set; }

    public string? LastTestError { get; set; }

    /// <summary>The provider account id.</summary>
    public Guid ProviderId { get; set; }

    /// <summary>The provider account's name.</summary>
    public string ProviderName { get; set; } = string.Empty;

    public Guid ModelId { get; set; }

    public ModelPriceSource PriceSource { get; set; } = ModelPriceSource.Table;

    /// <summary>True for the platform compute provider's capability.</summary>
    public bool IsSystem { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// The flattening, as an expression both DbContexts translate (a join through the two required
    /// navigations). Callers apply their own scope and enabled filters.
    /// </summary>
    public static readonly Expression<Func<ModelCapability, ModelCapabilityView>> Projection = c => new ModelCapabilityView
    {
        Id = c.Id,
        OrganizationId = c.OrganizationId,
        Name = c.Model.DisplayName,
        Kind = c.Model.Provider.Kind,
        Capability = c.Capability,
        Endpoint = c.Model.Provider.Endpoint,
        ModelName = c.Model.ModelCode,
        ApiKeyEncrypted = c.Model.Provider.ApiKeyEncrypted,
        ApiKeyLastFour = c.Model.Provider.ApiKeyLastFour,
        IsDefault = c.IsDefault,
        IsEnabled = c.IsEnabled && c.Model.IsEnabled && c.Model.Provider.IsEnabled,
        PersonalOAuthMode = c.Model.Provider.PersonalOAuthMode,
        TimeoutSeconds = c.Model.Provider.TimeoutSeconds,
        ExtraHeadersJson = c.Model.Provider.ExtraHeadersJson,
        LastTestAt = c.LastTestAt,
        LastTestOk = c.LastTestOk,
        LastTestError = c.LastTestError,
        ProviderId = c.Model.ProviderId,
        ProviderName = c.Model.Provider.Name,
        ModelId = c.ModelId,
        PriceSource = c.PriceSource,
        IsSystem = c.Model.Provider.IsSystem,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt
    };

    private static readonly Func<ModelCapability, ModelCapabilityView> Compiled = Projection.Compile();

    /// <summary>The same flattening over a loaded capability (its <c>Model</c> and <c>Model.Provider</c> must be loaded).</summary>
    public static ModelCapabilityView From(ModelCapability capability) => Compiled(capability);

    /// <summary>
    /// Enabled capabilities whose model and provider are enabled, visible to <paramref name="organizationId"/>:
    /// every platform row plus that organization's own (a null id = platform rows only).
    /// </summary>
    public static IQueryable<ModelCapabilityView> EnabledFor(IQueryable<ModelCapability> capabilities, Guid? organizationId) =>
        capabilities
            .Where(c => c.IsEnabled && c.Model.IsEnabled && c.Model.Provider.IsEnabled &&
                        (c.OrganizationId == null || (organizationId != null && c.OrganizationId == organizationId)))
            .Select(Projection);

    /// <summary>
    /// The ids of the enabled platform capabilities priced from their table that are not fully priced at
    /// <paramref name="at"/> (<see cref="IInferenceProviderStore.LoadUnpricedCapabilityIdsAsync"/>): no price row in force
    /// at all, or (T-032) no price in force for some unit the capability meters
    /// (<see cref="Metering.ProviderCapabilityUsage.ByCapability"/>, the same rule as <see cref="Metering.PriceCoverage.Find"/>).
    /// One query both DbContexts translate, then the per-unit rule in memory. Organization (BYO) rows and oracle-priced rows
    /// are never in it.
    /// </summary>
    public static async Task<IReadOnlySet<Guid>> UnpricedPlatformIdsAsync(IQueryable<ModelCapability> capabilities, DateTime at, CancellationToken ct)
    {
        var rows = await capabilities
            .Where(c => c.OrganizationId == null && c.PriceSource == ModelPriceSource.Table
                        && c.IsEnabled && c.Model.IsEnabled && c.Model.Provider.IsEnabled)
            .Select(c => new
            {
                c.Id,
                c.Capability,
                Units = c.Prices
                    .Where(p => p.EffectiveFrom <= at && (p.EffectiveTo == null || p.EffectiveTo > at))
                    .Select(p => p.Unit)
                    .ToList()
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        HashSet<Guid> unpriced = [];
        foreach (var row in rows)
        {
            if (!IsFullyPriced(row.Capability, row.Units)) unpriced.Add(row.Id);
        }

        return unpriced;
    }

    /// <summary>
    /// Whether a capability whose prices in force cover <paramref name="pricedUnits"/> is priced: at least one price, and
    /// one for every unit its capability meters (<see cref="Metering.PriceCoverage.Find"/>'s rule).
    /// </summary>
    public static bool IsFullyPriced(InferenceProviderCapability capability, IReadOnlyCollection<UsageKind> pricedUnits)
    {
        if (pricedUnits.Count == 0) return false;
        return !Metering.ProviderCapabilityUsage.ByCapability.TryGetValue(capability, out UsageKind[]? units)
               || units.All(pricedUnits.Contains);
    }
}
