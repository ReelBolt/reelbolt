namespace ReelBolt.Shared.Billing;

/// <summary>Binding for the <c>Billing</c> configuration section (entitlement-relevant keys only).</summary>
public sealed class BillingOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Billing";

    /// <summary>
    /// Whether plans gate anything. <c>false</c> (the default, i.e. self-host) makes every org
    /// all-allowed with tier <c>selfhost</c>, regardless of plan rows.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Plan key an org without a usable subscription falls back to.</summary>
    public string TrialPlanKey { get; set; } = "trial";

    /// <summary>
    /// TTL of the per-org entitlement cache. Within one process an explicit
    /// <see cref="IEntitlementCache.Invalidate"/> (B9a's webhook processor) takes effect at once; the
    /// other service learns of a change within this many seconds. 0 disables caching.
    /// </summary>
    public int EntitlementCacheSeconds { get; set; } = 30;
}
