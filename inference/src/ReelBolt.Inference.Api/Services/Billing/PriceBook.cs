using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>
/// The price book (docs/design/model-catalog.md §1, §4.2): what a platform capability's price rows are pre-filled
/// with when it is created or upgraded. It wraps <see cref="ModelPriceCatalog"/>, which stays as data, and is never
/// read while metering — metering reads only <c>model_capability_prices</c>.
/// </summary>
public sealed class PriceBook(InferenceApiDbContext db, IModelPriceService? prices = null, TimeProvider? time = null)
{
    /// <summary>
    /// The prices the book proposes for one capability of one model:
    /// <list type="bullet">
    ///   <item><description>every <see cref="ModelPriceCatalog.Priced"/> entry of the same kind whose model is
    ///   <paramref name="modelCode"/>, for each unit the capability meters (a Chat entry also prices Vision, a fal
    ///   ImageEdit entry also prices ImageErase: they meter the same unit);</description></item>
    ///   <item><description>its <c>code#tier</c> entries, one variant per tier;</description></item>
    ///   <item><description>for MiniMax and fal.ai video generation, one variant per resolution of the client's own
    ///   static table (<c>Describe(model).PricePerSecondByResolution</c>, no network);</description></item>
    ///   <item><description>for the system compute capability, <see cref="PricingDefaults.ComputePrices"/>.</description></item>
    /// </list>
    /// A model the catalog does not price gets nothing: it stays a coverage gap rather than an invented figure.
    /// </summary>
    public static IReadOnlyList<SuggestedPrice> SuggestFor(
        InferenceProviderKind kind, InferenceProviderCapability capability, string? modelCode)
    {
        if (capability == InferenceProviderCapability.Compute) return PricingDefaults.ComputePrices();
        if (!ProviderCapabilityUsage.ByCapability.TryGetValue(capability, out UsageKind[]? units)) return [];

        string code = modelCode?.Trim() ?? string.Empty;
        List<SuggestedPrice> suggestions = [];
        HashSet<(UsageKind, string)> seen = [];

        void Add(UsageKind unit, string? variant, decimal usd, string notes)
        {
            if (seen.Add((unit, variant?.ToUpperInvariant() ?? string.Empty)))
                suggestions.Add(new SuggestedPrice(unit, variant, usd, notes));
        }

        // An entry for this very capability is preferred over a same-unit entry for a sibling capability.
        foreach (ModelPrice entry in ModelPriceCatalog.Priced
                     .Where(e => e.ProviderKind == kind)
                     .OrderBy(e => e.Capability == capability ? 0 : 1))
        {
            (string entryCode, string? tier) = MeteringImageClient.SplitBillingKey(entry.Model);
            if (!string.Equals(entryCode, code, StringComparison.OrdinalIgnoreCase)) continue;

            foreach ((UsageKind unit, decimal usd) in entry.UsdPerUnit)
            {
                if (units.Contains(unit)) Add(unit, tier, usd, PublishedNote(entry.Basis));
            }
        }

        // A lip-sync model is a fal.ai video model, so it carries the same per-resolution table.
        if (capability is InferenceProviderCapability.VideoGeneration or InferenceProviderCapability.LipSync)
        {
            foreach ((string resolution, decimal usd) in VideoResolutionPrices(kind, code))
            {
                Add(UsageKind.VideoGenSeconds, resolution, usd, PublishedNote(
                    $"{kind} video table (Describe(\"{code}\").PricePerSecondByResolution), {resolution}"));
            }
        }

        return suggestions;
    }

    /// <summary>
    /// The per-resolution USD-per-second table of a MiniMax or fal.ai video model, from the client's own static
    /// table. Empty for any other kind (Higgsfield is oracle-priced, ComfyUI budgets GPU-seconds, not USD) and for a
    /// model the client cannot describe.
    /// </summary>
    public static IReadOnlyList<(string Resolution, decimal UsdPerSecond)> VideoResolutionPrices(
        InferenceProviderKind kind, string? modelCode)
    {
        string code = modelCode?.Trim() ?? string.Empty;
        try
        {
            IReadOnlyDictionary<string, decimal>? table = kind switch
            {
                InferenceProviderKind.MiniMax => MiniMaxTables.Value.Describe(code).PricePerSecondByResolution,
                InferenceProviderKind.FalAi => (code.Length == 0
                    ? FalVideoCatalog.Instance.DefaultFor(FalVideoJob.ImageToVideo)
                    : FalVideoCatalog.Instance.Find(code))?.PricePerSecondByResolution,
                _ => null
            };
            return table is null ? [] : [.. table.Select(p => (p.Key, p.Value))];
        }
        catch (NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>
    /// The note a pre-filled price carries. It keeps the <c>PLACEHOLDER</c> marker word (load bearing for
    /// <c>infra/loadtest/x1-cost.mjs</c>): a published list price is better than an estimate and worse than an invoice.
    /// </summary>
    public static string PublishedNote(string basis) =>
        $"{PricingDefaults.PlaceholderMarker} (published vendor list price, not reconciled against a provider invoice) - Basis: {basis}.";

    /// <summary>
    /// Pre-fills the prices of a newly created PLATFORM capability from <see cref="SuggestFor"/>. Does nothing for a
    /// BYO capability (BYO is never priced), for one that already has any price row, or when the book has nothing for
    /// it. Returns how many rows it added and invalidates the price cache when it added any.
    /// </summary>
    public async Task<int> AutoPriceAsync(Guid capabilityId, CancellationToken ct = default)
    {
        ModelCapability? capability = await db.ModelCapabilities
            .IgnoreQueryFilters()
            .Include(c => c.Model).ThenInclude(m => m.Provider)
            .FirstOrDefaultAsync(c => c.Id == capabilityId, ct);
        if (capability is null || capability.OrganizationId is not null) return 0;
        if (await db.ModelCapabilityPrices.AnyAsync(p => p.ModelCapabilityId == capabilityId, ct)) return 0;

        DateTime now = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        IReadOnlyList<SuggestedPrice> suggestions =
            SuggestFor(capability.Model.Provider.Kind, capability.Capability, capability.Model.ModelCode);
        foreach (SuggestedPrice suggestion in suggestions)
        {
            db.ModelCapabilityPrices.Add(ToRow(capabilityId, suggestion, now));
        }

        if (suggestions.Count == 0) return 0;
        await db.SaveChangesAsync(ct);
        prices?.Invalidate();
        return suggestions.Count;
    }

    /// <summary>A price row for <paramref name="capabilityId"/> from a suggestion, effective from <paramref name="from"/>.</summary>
    public static ModelCapabilityPrice ToRow(Guid capabilityId, SuggestedPrice suggestion, DateTime from) => new()
    {
        Id = Guid.NewGuid(),
        ModelCapabilityId = capabilityId,
        Unit = suggestion.Unit,
        Variant = suggestion.Variant,
        CostUsdPerUnit = suggestion.CostUsdPerUnit,
        EffectiveFrom = from,
        Notes = suggestion.Notes,
        CreatedAt = from
    };

    // Describe is a pure lookup on the client's static table; the instance is never used to send anything.
    private static readonly Lazy<MiniMaxVideoGenerationClient> MiniMaxTables =
        new(() => new MiniMaxVideoGenerationClient(new HttpClient(), string.Empty));
}
