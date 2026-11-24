namespace ReelBolt.Shared.Data.Models.Billing;

/// <summary>What a plan row sells. Append-only, stored as the member name.</summary>
public enum PlanKind
{
    Trial,
    Subscription,
    PerSeat,
    Contact
}

/// <summary>
/// A sellable plan. Table <c>plans</c>, owned by the Inference API and upserted at startup from the
/// one catalog file <c>Billing/plans.catalog.json</c> (R5), never edited by hand. The engine maps it
/// read-only. Credit RATES live in rate cards, not here; this row holds allowances and entitlements.
/// </summary>
public class Plan
{
    public Guid Id { get; set; }

    /// <summary>Catalog id (<c>trial</c>, <c>creator</c>, ...); also <c>OrganizationEntitlementSet.Tier</c>.</summary>
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public PlanKind Kind { get; set; }

    /// <summary>Indicative list price in USD per month (per seat for <see cref="PlanKind.PerSeat"/>); null when not sold.</summary>
    public decimal? PriceMonthlyUsd { get; set; }

    public int? MinSeats { get; set; }

    /// <summary>Credits granted each billing period, in millicredits (D8); per seat for <see cref="PlanKind.PerSeat"/>.</summary>
    public long MonthlyCreditsMillis { get; set; }

    /// <summary>One-time credits granted when the plan starts (Trial), in millicredits.</summary>
    public long OneTimeCreditsMillis { get; set; }

    /// <summary>Whether the org may buy top-ups on this plan (D9: paid plans only).</summary>
    public bool TopUpAllowed { get; set; }

    /// <summary>
    /// jsonb. camelCase fields of <c>OrganizationEntitlementSet</c> (without <c>tier</c>); see
    /// <c>PlanEntitlements</c>. An omitted field means "unlimited / allowed".
    /// </summary>
    public string EntitlementsJson { get; set; } = "{}";

    /// <summary>jsonb object <c>{ "paddle": { "monthly": "pri_..." } }</c>. Environment specific, never in the catalog; the upsert preserves it.</summary>
    public string ProviderPriceIdsJson { get; set; } = "{}";

    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
