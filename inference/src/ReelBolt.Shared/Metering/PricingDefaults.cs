using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// The platform's pricing constants (docs/design/model-catalog.md §1, §5.3-5.5). The live values of markup and
/// credit value are the <c>pricing_settings</c> row; these are its defaults and the figures code that cannot
/// read the database (estimates, caps, the price book) is built on.
/// </summary>
public static class PricingDefaults
{
    /// <summary>D9 (LOCKED): one credit is this much platform COGS (<c>pricing_settings.credit_usd_value</c>).</summary>
    public const decimal CreditUsdValue = 0.006m;

    /// <summary>Decision 7: credits charged = cost × this markup ÷ <see cref="CreditUsdValue"/> (<c>pricing_settings.markup</c>).</summary>
    public const decimal DefaultMarkup = 1.2m;

    /// <summary>Millicredits per USD of COGS at 1 credit = <see cref="CreditUsdValue"/> (before markup).</summary>
    public const decimal CreditsMillisPerUsd = 1000m / CreditUsdValue;

    /// <summary>
    /// The word every pre-filled price note starts with: these are indicative figures (published list prices or
    /// stated estimates), not provider-measured costs. <c>infra/loadtest/x1-cost.mjs</c> keys on it.
    /// </summary>
    public const string PlaceholderMarker = "PLACEHOLDER";

    /// <summary>
    /// USD per generated video second used by the step-credit heuristics (<see cref="StepCreditDefaults"/>):
    /// MiniMax-H3-Max at its 768P list price. Not a price row; a heuristic input.
    /// </summary>
    public const decimal VideoGenUsdPerUnit = 0.08m;

    /// <summary>
    /// USD of compute per 720p media second: the catalog seed's 720p render price ($0.0000181, from an 8 vCPU VM at
    /// about $0.26/hour read at one output second per run-second) doubled to cover idle pool-server hours. Estimated
    /// conversion, not measured. Keep equal to the seed's compute block in model-catalog.seed.json.
    /// </summary>
    public const decimal ComputeRenderUsdPerSecond = 0.0000362m;

    /// <summary>An encode pass costs this share of the render pass (not measured).</summary>
    public const decimal ComputeEncodeShare = 0.5m;

    /// <summary>An analyse pass costs this share of the render pass (not measured).</summary>
    public const decimal ComputeAnalyzeShare = 0.25m;

    /// <summary>The compute units, priced on the system compute capability one variant per tier.</summary>
    public static readonly UsageKind[] ComputeUnits =
        [UsageKind.RenderOutputSeconds, UsageKind.EncodeOutputSeconds, UsageKind.AnalyzeMediaSeconds];

    /// <summary>The short side of each compute tier: a tier costs its pixel-count ratio to 720p.</summary>
    private static readonly (string Tier, int ShortSide)[] ComputeTiers =
    [
        (ComputeMetering.Tier720, 720),
        (ComputeMetering.Tier1080, 1080),
        (ComputeMetering.TierUhd, 2160)
    ];

    /// <summary>
    /// The default compute prices, one per (unit, tier): render scaled by the tier's pixel count, encode and analyse
    /// fixed shares of render. Equal, figure for figure, to the compute rate cards the old seed wrote.
    /// </summary>
    public static IReadOnlyList<SuggestedPrice> ComputePrices()
    {
        List<SuggestedPrice> prices = [];
        int baseShortSide = ComputeTiers[0].ShortSide;
        foreach ((string tier, int shortSide) in ComputeTiers)
        {
            decimal tierFactor = (decimal)shortSide * shortSide / ((decimal)baseShortSide * baseShortSide);
            decimal render = ComputeRenderUsdPerSecond * tierFactor;
            string basis = $"{PlaceholderMarker} (indicative, not measured) - Basis: B0's $0.25/hour, 2 vCPU effective anchor " +
                $"({ComputeRenderUsdPerSecond:0.#######}/720p-second) x pixel ratio {tierFactor:0.##}; estimated conversion, not measured";

            prices.Add(new SuggestedPrice(UsageKind.RenderOutputSeconds, tier, render, basis + "."));
            prices.Add(new SuggestedPrice(UsageKind.EncodeOutputSeconds, tier, render * ComputeEncodeShare,
                basis + $"; encode = {ComputeEncodeShare:0.##} of render."));
            prices.Add(new SuggestedPrice(UsageKind.AnalyzeMediaSeconds, tier, render * ComputeAnalyzeShare,
                basis + $"; analyse = {ComputeAnalyzeShare:0.##} of render."));
        }

        return prices;
    }

    /// <summary>Credits per unit before the billing-mode multiplier: <c>override ?? cost × markup × 1000 ÷ creditUsdValue</c>.</summary>
    public static decimal CreditsMillisPerUnit(decimal costUsdPerUnit, decimal? overrideMillis, decimal markup, decimal creditUsdValue) =>
        overrideMillis ?? costUsdPerUnit * markup * 1000m / creditUsdValue;
}

/// <summary>A price the price book proposes for one unit (and variant) of a capability.</summary>
public sealed record SuggestedPrice(UsageKind Unit, string? Variant, decimal CostUsdPerUnit, string Notes);
