namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// The single pricing settings row (id 1, docs/design/model-catalog.md §2). Only <see cref="Markup"/> is
/// editable from admin; <see cref="CreditUsdValue"/> is the published credit COGS and changes only by
/// migration.
/// </summary>
public class PricingSettings
{
    /// <summary>The one row's id.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public decimal Markup { get; set; } = 1.2m;

    public decimal CreditUsdValue { get; set; } = 0.006m;

    /// <summary>Set once the catalog upgrader has ported prices; null before.</summary>
    public DateTime? CatalogUpgradedAt { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
