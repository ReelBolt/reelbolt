namespace ReelBolt.Shared.Data.Models.Billing;

/// <summary>Where a grant's credits came from. Append-only, stored as the member name.</summary>
public enum CreditGrantSource
{
    Trial,
    Plan,
    TopUp,
    Promo
}

/// <summary>
/// A bucket of credits with its own expiry (table <c>credit_grants</c>). Settlements consume
/// buckets in a fixed order: non-top-up before top-up, then earliest expiry first (never-expiring
/// last), then oldest. The invariant is <c>sum(RemainingMillis) == max(account balance, 0)</c>.
/// </summary>
public class CreditGrant
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid OrganizationId { get; set; }
    public CreditGrantSource Source { get; set; }

    /// <summary>Millicredits originally granted.</summary>
    public long AmountMillis { get; set; }

    public long RemainingMillis { get; set; }

    /// <summary>Null never expires.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Set when the expiry sweep zeroed the remainder.</summary>
    public DateTime? ExpiredAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
