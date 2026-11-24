namespace ReelBolt.Shared.Data.Models.Billing;

/// <summary>Provider-neutral subscription state. Append-only, stored as the member name.</summary>
public enum SubscriptionStatus
{
    Trialing,
    Active,
    PastDue,
    Paused,
    Canceled
}

/// <summary>An organization's subscription to a plan at a billing provider. Table <c>subscriptions</c>.</summary>
public class Subscription
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid PlanId { get; set; }

    /// <summary>Billing provider name (<c>paddle</c>); null for the internal Trial plan.</summary>
    public string? Provider { get; set; }

    public string? ProviderSubscriptionId { get; set; }
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }
    public int Seats { get; set; } = 1;
    public bool CancelAtPeriodEnd { get; set; }

    /// <summary>When a scheduled cancellation takes effect (the provider's date); null when none is scheduled.</summary>
    public DateTime? CancelEffectiveAt { get; set; }

    /// <summary>
    /// A downgrade waiting for the period to end. The provider has already switched the items, but the
    /// entitlements of <see cref="PlanId"/>/<see cref="Seats"/> were paid for until <see cref="PeriodEnd"/>,
    /// so the lower plan is held here and applied by the first webhook of the next period.
    /// </summary>
    public Guid? ScheduledPlanId { get; set; }

    /// <summary>Seat count of <see cref="ScheduledPlanId"/>; null when no change is scheduled.</summary>
    public int? ScheduledSeats { get; set; }

    /// <summary>When <see cref="ScheduledPlanId"/> takes effect (the current period end).</summary>
    public DateTime? ScheduledChangeAt { get; set; }

    /// <summary>Provider transaction id that opened the current billing term (annual credit grants key off it). Null on old rows.</summary>
    public string? TermOpeningTransactionId { get; set; }

    /// <summary>
    /// <c>occurred_at</c> of the newest provider event applied to this row. A webhook older than this is
    /// stale (providers do not deliver in order) and is recorded but not applied.
    /// </summary>
    public DateTime? LastEventAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
