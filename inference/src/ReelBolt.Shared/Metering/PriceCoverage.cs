using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Metering;

/// <summary>An enabled platform capability that has no effective price for one or more of the units it meters.</summary>
public sealed record PriceCoverageGap(
    Guid CapabilityId,
    string ProviderName,
    string ModelDisplayName,
    InferenceProviderKind Kind,
    InferenceProviderCapability Capability,
    IReadOnlyList<UsageKind> MissingUnits);

/// <summary>A platform capability as the coverage check sees it.</summary>
public sealed record CoverageCapability(
    Guid Id,
    string ProviderName,
    string ModelDisplayName,
    InferenceProviderKind Kind,
    InferenceProviderCapability Capability,
    ModelPriceSource PriceSource);

/// <summary>
/// What the platform can bill but does not price (docs/design/model-catalog.md §5.5; replaces the rate-card coverage
/// check). A gap is money: a Platform event through it is recorded at zero credits with no <c>price_id</c>.
/// </summary>
/// <remarks>
/// Checked: every <b>enabled platform</b> capability (capability, model and provider enabled, no organization),
/// including the system compute capability, against the units its capability meters
/// (<see cref="ProviderCapabilityUsage.ByCapability"/>; Compute needs the three compute units). Exempt: oracle-priced
/// capabilities (Higgsfield), which are billed from the vendor's own quote. BYO capabilities are never priced (BYO is
/// ×0), so they are never gaps.
/// </remarks>
public static class PriceCoverage
{
    /// <summary>The gaps among <paramref name="capabilities"/> given <paramref name="prices"/>, at <paramref name="at"/>.</summary>
    public static IReadOnlyList<PriceCoverageGap> Find(
        IEnumerable<CoverageCapability> capabilities, IEnumerable<ModelCapabilityPrice> prices, DateTime at)
    {
        ILookup<Guid, UsageKind> priced = prices
            .Where(p => ModelPriceService.IsEffective(p, at))
            .ToLookup(p => p.ModelCapabilityId, p => p.Unit);

        List<PriceCoverageGap> gaps = [];
        foreach (CoverageCapability capability in capabilities)
        {
            if (capability.PriceSource == ModelPriceSource.Oracle) continue;
            if (!ProviderCapabilityUsage.ByCapability.TryGetValue(capability.Capability, out UsageKind[]? units)) continue;

            HashSet<UsageKind> have = [.. priced[capability.Id]];
            List<UsageKind> missing = [.. units.Where(u => !have.Contains(u))];
            if (missing.Count > 0)
            {
                gaps.Add(new PriceCoverageGap(capability.Id, capability.ProviderName, capability.ModelDisplayName,
                    capability.Kind, capability.Capability, missing));
            }
        }

        return gaps
            .OrderBy(g => g.ProviderName, StringComparer.Ordinal)
            .ThenBy(g => g.ModelDisplayName, StringComparer.Ordinal)
            .ThenBy(g => g.Capability)
            .ToList();
    }

    /// <summary>The same check over a context that maps the catalog and its prices (tenant filters bypassed).</summary>
    public static async Task<IReadOnlyList<PriceCoverageGap>> FindAsync(DbContext db, DateTime at, CancellationToken ct = default)
    {
        List<CoverageCapability> capabilities = await db.Set<ModelCapability>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.OrganizationId == null && c.IsEnabled && c.Model.IsEnabled && c.Model.Provider.IsEnabled)
            .Select(c => new CoverageCapability(
                c.Id, c.Model.Provider.Name, c.Model.DisplayName, c.Model.Provider.Kind, c.Capability, c.PriceSource))
            .ToListAsync(ct);
        List<Guid> ids = [.. capabilities.Select(c => c.Id)];
        List<ModelCapabilityPrice> prices = await db.Set<ModelCapabilityPrice>()
            .AsNoTracking()
            .Where(p => ids.Contains(p.ModelCapabilityId))
            .ToListAsync(ct);
        return Find(capabilities, prices, at);
    }
}
