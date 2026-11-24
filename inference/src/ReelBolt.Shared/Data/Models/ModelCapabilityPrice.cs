namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// The price of one unit of usage on one <see cref="ModelCapability"/> (docs/design/model-catalog.md §2).
/// A new price for the same (unit, variant) closes its open predecessor. Platform-admin only; carries no
/// organization and is reached only through its capability.
/// </summary>
public class ModelCapabilityPrice
{
    public Guid Id { get; set; }

    public Guid ModelCapabilityId { get; set; }

    /// <summary>The metered unit, by <see cref="UsageKind"/> name.</summary>
    public UsageKind Unit { get; set; }

    /// <summary>Distinguishes prices of one unit (video resolution, fal image tier, compute tier); null = any.</summary>
    public string? Variant { get; set; }

    public decimal CostUsdPerUnit { get; set; }

    /// <summary>When set, the credits charged per unit instead of the cost × markup derivation.</summary>
    public decimal? CreditsMillisPerUnitOverride { get; set; }

    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;

    public DateTime? EffectiveTo { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ModelCapability ModelCapability { get; set; } = null!;
}
