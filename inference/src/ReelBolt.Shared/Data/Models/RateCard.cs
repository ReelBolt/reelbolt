namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// A price for one usage kind of one provider kind and model: what we charge in credits and what it
/// costs us. Table <c>rate_cards</c>, owned by the Inference API, read-only in the engine. A row is
/// effective over <c>[EffectiveFrom, EffectiveTo)</c>; a later card supersedes by date, never by edit.
/// </summary>
public class RateCard
{
    public Guid Id { get; set; }
    public InferenceProviderKind ProviderKind { get; set; }

    /// <summary>An exact model name, or <c>*</c> for any model of the provider kind (exact wins).</summary>
    public string ModelPattern { get; set; } = "*";

    public UsageKind UsageKind { get; set; }

    /// <summary>Millicredits charged per one unit of the usage kind (fractions allowed, rounded when priced).</summary>
    public decimal CreditsMillisPerUnit { get; set; }

    /// <summary>Provider cost in USD per one unit.</summary>
    public decimal CostUsdPerUnit { get; set; }

    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
    public DateTime? EffectiveTo { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Scales the credits of a priced event by billing mode (Platform 1.0, Byo 0 for LLM kinds, Runner 0
/// or a fraction for compute kinds). <see cref="UsageKind"/> null is the default for the mode; a
/// kind-specific row wins over it. Table <c>rate_multipliers</c>, owned by the Inference API.
/// </summary>
public class RateMultiplier
{
    public Guid Id { get; set; }
    public UsageBillingMode BillingMode { get; set; }
    public UsageKind? UsageKind { get; set; }
    public decimal Multiplier { get; set; } = 1m;
    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
    public DateTime? EffectiveTo { get; set; }
}
