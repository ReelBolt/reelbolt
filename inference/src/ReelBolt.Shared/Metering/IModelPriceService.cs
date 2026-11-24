using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Metering;

/// <summary>What a quantity of one unit costs: credits charged, our cost, and the price row it was priced at.</summary>
/// <param name="PriceId">The <c>model_capability_prices</c> row; null when nothing priced it or the price was an oracle quote.</param>
/// <param name="Variant">The variant of that row (null when variant-less or unpriced).</param>
public readonly record struct PriceQuote(long CreditsMillis, decimal CostUsd, Guid? PriceId, string? Variant = null);

/// <summary>
/// One unit of usage to price (docs/design/model-catalog.md §5.3).
/// </summary>
/// <param name="ModelCapabilityId">The model capability that served the call (what every "provider id" holds); null for a config fallback.</param>
/// <param name="AccountId">The provider account; null to take it from <paramref name="ModelCapabilityId"/>.</param>
/// <param name="Kind">The provider kind.</param>
/// <param name="Capability">The capability the call was made for (Chat, Vision, ImageErase, Compute…).</param>
/// <param name="ModelCode">The model that actually ran; null or empty when the caller did not report one.</param>
/// <param name="Unit">The metered unit.</param>
/// <param name="Variant">The price variant (video resolution, fal image tier, compute tier); null = none.</param>
/// <param name="BillingMode">Platform, Byo or Runner.</param>
/// <param name="Quantity">How many units.</param>
/// <param name="At">When the usage happened (selects the effective price and multiplier); null = now.</param>
/// <param name="PromptTokens">
/// The call's whole prompt length in tokens, cached input included; when positive and no <paramref name="Variant"/> was
/// requested, a <c>gt{N}k</c> prompt-length tier row of the unit is preferred (N × 1000 below it, the largest N).
/// Null or 0 = unknown, the short-context row.
/// </param>
public sealed record PriceRequest(
    Guid? ModelCapabilityId,
    Guid? AccountId,
    InferenceProviderKind Kind,
    InferenceProviderCapability Capability,
    string? ModelCode,
    UsageKind Unit,
    string? Variant,
    UsageBillingMode BillingMode,
    decimal Quantity,
    DateTime? At = null,
    long? PromptTokens = null);

/// <summary>
/// A platform-billed unit has no price row. Pricing fails closed: a silent zero would give the work away for free,
/// so callers must decide what to do (the recorder writes the row at zero and logs an error).
/// </summary>
public sealed class UnpricedUsageException(UsageKind kind, InferenceProviderKind providerKind, string modelName)
    : InvalidOperationException($"No price row prices {kind} for {providerKind}/{modelName}.")
{
    public UsageKind Kind { get; } = kind;
    public InferenceProviderKind ProviderKind { get; } = providerKind;
    public string ModelName { get; } = modelName;
}

/// <summary>
/// Prices usage against <c>model_capability_prices</c>, <c>rate_multipliers</c> and <c>pricing_settings</c>
/// (docs/design/model-catalog.md §5.3). Replaces the rate-card service.
/// </summary>
public interface IModelPriceService
{
    /// <summary>
    /// Prices <see cref="PriceRequest.Quantity"/> units. Credits = round(qty × perUnit × multiplier), floored at
    /// one millicredit on Platform when positive, where perUnit = override ?? cost × markup × 1000 ÷ credit value.
    /// Cost USD is ours, so it is zero for Byo and Runner.
    /// </summary>
    /// <exception cref="UnpricedUsageException">Billing mode Platform and no price row applies.</exception>
    ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default);

    /// <summary>
    /// Prices an oracle-quoted USD amount (Higgsfield) at the same markup, credit value and billing-mode
    /// multiplier a price row would be priced at. There is no price row, so <see cref="PriceQuote.PriceId"/> is null.
    /// </summary>
    ValueTask<PriceQuote> PriceQuotedUsdAsync(
        decimal costUsd, UsageKind unit, UsageBillingMode billingMode, DateTime? at = null, CancellationToken ct = default);

    /// <summary>Drops the cached snapshot so the next call reloads (after an admin write or the upgrader).</summary>
    void Invalidate();
}

/// <summary>A model capability as pricing sees it: its account, kind, capability and model code.</summary>
public sealed record PricedCapability(
    Guid Id,
    Guid ProviderId,
    Guid? OrganizationId,
    InferenceProviderKind Kind,
    InferenceProviderCapability Capability,
    string ModelCode);

/// <summary>Everything pricing needs, loaded in one read.</summary>
public sealed record ModelPriceSnapshot(
    IReadOnlyList<PricedCapability> Capabilities,
    IReadOnlyList<ModelCapabilityPrice> Prices,
    IReadOnlyList<RateMultiplier> Multipliers,
    decimal Markup = PricingDefaults.DefaultMarkup,
    decimal CreditUsdValue = PricingDefaults.CreditUsdValue);

/// <summary>Where <see cref="ModelPriceService"/> reads its tables from (each service supplies its own context).</summary>
public interface IModelPriceSource
{
    Task<ModelPriceSnapshot> LoadAsync(CancellationToken ct);
}
