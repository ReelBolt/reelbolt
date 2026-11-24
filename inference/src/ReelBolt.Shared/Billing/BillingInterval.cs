namespace ReelBolt.Shared.Billing;

/// <summary>The billing cadence of a subscription: one charge a month or one a year.</summary>
public enum BillingInterval
{
    Monthly = 0,
    Annual = 1
}

/// <summary>
/// The interval of a subscription is derived, never stored: a period longer than
/// <see cref="AnnualThresholdDays"/> days is annual (a month is at most 31 days).
/// </summary>
public static class SubscriptionInterval
{
    /// <summary>A period spanning more than this many days is annual.</summary>
    public const int AnnualThresholdDays = 40;

    /// <summary>Annual when <c>periodEnd - periodStart</c> exceeds 40 days; monthly otherwise or when a bound is unknown.</summary>
    public static BillingInterval Of(DateTime? periodStart, DateTime? periodEnd)
        => periodStart is { } start && periodEnd is { } end && (end - start).TotalDays > AnnualThresholdDays
            ? BillingInterval.Annual
            : BillingInterval.Monthly;

    /// <summary>The wire spelling: <c>monthly</c> or <c>annual</c>.</summary>
    public static string ToWire(BillingInterval interval) => interval == BillingInterval.Annual ? "annual" : "monthly";

    /// <summary>Parses the wire spelling (case-insensitive); null or blank is monthly; anything else is not parsed.</summary>
    public static bool TryParse(string? value, out BillingInterval interval)
    {
        interval = BillingInterval.Monthly;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "monthly":
                return true;
            case "annual":
                interval = BillingInterval.Annual;
                return true;
            default:
                return false;
        }
    }
}
