namespace ReelBolt.Shared.Data.Models.Billing;

/// <summary>What a promotion does. Stored as the member name.</summary>
public enum PromotionKind
{
    /// <summary>A code that grants free Promo credits with an expiry.</summary>
    CreditCode,

    /// <summary>A Paddle discount on a subscription plan.</summary>
    PlanDiscount,

    /// <summary>A Paddle discount on a top-up pack (always once).</summary>
    PackDiscount,

    /// <summary>Extra Promo credits on top of a purchased pack.</summary>
    PackBonus,
}

/// <summary>Lifecycle of a promotion. Stored as the member name.</summary>
public enum PromotionStatus
{
    Active,
    Paused,
    Archived,
}

/// <summary>How a discount is expressed (Paddle's <c>percentage</c> / <c>flat</c>).</summary>
public enum PromotionDiscountType
{
    Percentage,
    Flat,
}

/// <summary>How long a plan discount recurs.</summary>
public enum PromotionDuration
{
    Once,
    Months,
    Forever,
}

/// <summary>
/// A promotion an admin runs (table <c>promotions</c>). Discount kinds are mirrored by a Paddle
/// discount (<see cref="ProviderDiscountId"/>); the code lives only here. The value fields
/// (kind, code, amounts, targets, duration, bonus) are immutable after create; only
/// <see cref="Name"/>, <see cref="PublicHeadline"/>, <see cref="Status"/> and <see cref="EndsAt"/>
/// may be edited. API-owned; the engine maps none of the promotion tables.
/// </summary>
public class Promotion
{
    public Guid Id { get; set; }
    public PromotionKind Kind { get; set; }

    /// <summary>Admin-only label (≤120).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Customer-facing headline (≤120).</summary>
    public string? PublicHeadline { get; set; }

    /// <summary>Uppercase <c>^[A-Z0-9]{4,32}$</c>, unique when present. Null means automatic. Required for <see cref="PromotionKind.CreditCode"/>.</summary>
    public string? Code { get; set; }

    public PromotionStatus Status { get; set; } = PromotionStatus.Active;
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }

    /// <summary>Global cap; null is unlimited.</summary>
    public int? MaxRedemptions { get; set; }

    public int RedemptionCount { get; set; }
    public int MaxRedemptionsPerOrg { get; set; } = 1;

    /// <summary>CreditCode: millicredits granted per redemption.</summary>
    public long? CreditsMillis { get; set; }

    /// <summary>CreditCode: days until the granted credits expire.</summary>
    public int? CreditExpiryDays { get; set; }

    public PromotionDiscountType? DiscountType { get; set; }

    /// <summary>Paddle's string format: a percentage (<c>"10"</c>) or whole minor units for flat.</summary>
    public string? DiscountAmount { get; set; }

    /// <summary>ISO 4217 (≤3), flat discounts only.</summary>
    public string? CurrencyCode { get; set; }

    public PromotionDuration? DurationKind { get; set; }
    public int? DurationMonths { get; set; }

    /// <summary>jsonb <c>string[]</c> of plan keys; empty means every purchasable plan.</summary>
    public string TargetPlanKeysJson { get; set; } = "[]";

    /// <summary>jsonb <c>int[]</c> of pack credit sizes; empty means every pack.</summary>
    public string TargetPackCreditsJson { get; set; } = "[]";

    /// <summary>jsonb <c>string[]</c> of provider price ids, resolved at create.</summary>
    public string TargetPriceIdsJson { get; set; } = "[]";

    /// <summary>PackBonus: percent of the pack's credits added as Promo credits.</summary>
    public int? BonusPercent { get; set; }

    /// <summary>The Paddle discount id (<c>dsc_…</c>) for discount kinds.</summary>
    public string? ProviderDiscountId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid CreatedByUserId { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>What a redemption row records. Stored as the member name.</summary>
public enum PromotionRedemptionKind
{
    CreditCodeRedeemed,
    DiscountApplied,
    BonusGranted,
}

/// <summary>
/// Whether a redemption row is a use (contract C10). Stored as the member name. Only
/// <see cref="Granted"/> rows count against any cap; a <see cref="NotGranted"/> row records a paid
/// checkout that was refused its bonus (or whose discount was not counted), so support can see it.
/// </summary>
public enum PromotionRedemptionOutcome
{
    Granted,
    NotGranted,
}

/// <summary>Why a paid checkout's promotion was not granted or counted (contract C10). Stored as the member name.</summary>
public enum PromotionNotGrantedReason
{
    /// <summary>The checkout's reservation was superseded by a newer checkout (or another payment) and a cap is reached.</summary>
    Superseded,

    /// <summary>The checkout's reservation had expired when it was paid and a cap is reached.</summary>
    Expired,

    /// <summary>The reservation was not within every cap when it was created, and a cap is reached.</summary>
    ReservedOverCap,

    /// <summary>No reservation of this server backs the payment, and a cap is reached.</summary>
    NotReserved,

    /// <summary>The promotion's global overshoot bound is used up.</summary>
    OvershootBoundReached,

    /// <summary>The buyer is at the cap and already had its one overshoot of this promotion.</summary>
    UserCapReached,

    /// <summary>The organization is at the cap and already had its one overshoot of this promotion.</summary>
    OrgCapReached,

    /// <summary>The payment does not match the checkout record (another organization or price).</summary>
    CheckoutMismatch,
}

/// <summary>
/// One use of a promotion by an organization (table <c>promotion_redemptions</c>). A discount
/// application is idempotent per (promotion, provider transaction).
/// </summary>
public class PromotionRedemption
{
    public Guid Id { get; set; }
    public Guid PromotionId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? UserId { get; set; }
    public PromotionRedemptionKind Kind { get; set; }
    public string? ProviderTransactionId { get; set; }
    public Guid? LedgerEntryId { get; set; }
    public long? CreditsMillis { get; set; }

    /// <summary>Minor units, as Paddle sends them.</summary>
    public string? DiscountTotal { get; set; }

    public string? CurrencyCode { get; set; }

    /// <summary>
    /// <see cref="PromotionRedemptionKind.DiscountApplied"/> on a subscription: the provider subscription
    /// (≤64). A plan discount is recorded once per (promotion, subscription), so renewals that still
    /// carry the discount neither add rows nor count again.
    /// </summary>
    public string? ProviderSubscriptionId { get; set; }

    /// <summary>
    /// A paid, reserved checkout honoured although a cap (global, per org or per user) was already
    /// reached when the payment landed (contract C8). Shown in the admin redemption list.
    /// </summary>
    public bool Overshoot { get; set; }

    /// <summary>
    /// <see cref="PromotionRedemptionOutcome.Granted"/> for a use; <see cref="PromotionRedemptionOutcome.NotGranted"/>
    /// records a refused grant or count (contract C10), which never counts against a cap.
    /// </summary>
    public PromotionRedemptionOutcome Outcome { get; set; } = PromotionRedemptionOutcome.Granted;

    /// <summary>Why the row is <see cref="PromotionRedemptionOutcome.NotGranted"/>; null for a granted use.</summary>
    public PromotionNotGrantedReason? NotGrantedReason { get; set; }

    /// <summary>
    /// when a plan change to a price outside the promotion's targets removed this subscription's
    /// discount at the provider (UTC); null while it was never removed this way. Written once.
    /// </summary>
    public DateTime? DiscountRemovedAt { get; set; }

    /// <summary>The provider price id (≤64) of the plan change that removed the discount; null when not removed.</summary>
    public string? DiscountRemovedForPriceId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// What this server attached to one provider checkout (table <c>promotion_checkouts</c>), written
/// right after the provider returned the transaction id and only when a discount or bonus promotion
/// was attached. The webhook grants a pack bonus only from this row, never from the checkout's
/// client-visible custom data; an open row (no <see cref="CompletedAt"/>, not superseded, younger than
/// <c>Billing:Promotions:CheckoutReservationMinutes</c>) also reserves its promotions' global cap.
/// </summary>
public class PromotionCheckout
{
    public Guid Id { get; set; }

    /// <summary>
    /// The provider's transaction id (≤200), unique. Null only while the provider checkout is being
    /// created: the row is reserved first, under the promotion locks, and stamped once the provider answers.
    /// </summary>
    public string? ProviderTransactionId { get; set; }

    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public Guid? DiscountPromotionId { get; set; }
    public Guid? BonusPromotionId { get; set; }

    /// <summary>The provider price id the checkout sells (≤200).</summary>
    public string PriceId { get; set; } = string.Empty;

    /// <summary>The pack size for a top-up; null for a plan.</summary>
    public int? TopUpCredits { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When a paid webhook for the transaction was processed.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// When a newer checkout by the same user or org for the same promotion (or the payment of another
    /// of their checkouts) replaced this one. A superseded row reserves nothing and, if it is paid after
    /// all, is honoured only while the caps still hold.
    /// </summary>
    public DateTime? SupersededAt { get; set; }

    /// <summary>
    /// Whether every cap of the reserved promotions held when this reservation was created (contract
    /// C10). Only such a reservation, neither superseded nor expired, may be honoured past a cap.
    /// </summary>
    public bool WithinCapsAtReservation { get; set; }

    /// <summary>
    /// When the provider transaction of this superseded or expired reservation was cancelled, or found
    /// no longer cancellable (already paid) and left alone. Null while a cancel is still owed.
    /// </summary>
    public DateTime? ProviderCancelledAt { get; set; }

    /// <summary>How many times a cancel of the provider transaction was attempted (the sweeper retries).</summary>
    public int ProviderCancelAttempts { get; set; }

    /// <summary>
    /// After a failed cancel, the earliest time the sweeper tries again (exponential backoff, N-17).
    /// Null while no attempt has failed.
    /// </summary>
    public DateTime? ProviderCancelNextAttemptAt { get; set; }
}

/// <summary>
/// A platform admin's manual Promo credit grant to an organization (table
/// <c>admin_credit_grants</c>). <see cref="RequestId"/> is unique so a replayed request returns the
/// same row.
/// </summary>
public class AdminCreditGrant
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public long CreditsMillis { get; set; }
    public DateTime ExpiresAt { get; set; }

    /// <summary>Required, ≤500.</summary>
    public string Reason { get; set; } = string.Empty;

    public Guid GrantedByUserId { get; set; }
    public Guid LedgerEntryId { get; set; }
    public Guid RequestId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A platform admin's manual plan grant to an organization (table <c>admin_subscription_grants</c>): the
/// audit trail of the <c>subscriptions</c> row it wrote, which carries <c>provider = NULL</c>.
/// That null is the signal that separates a gift from revenue — a gifted plan is never a Paddle
/// subscription, and BI must not count it as one. <see cref="RequestId"/> is unique, so a replayed
/// request returns the same row; <see cref="SubscriptionId"/> is a plain column, not a foreign key,
/// because an audit trail must outlive the row it describes.
/// </summary>
public class AdminSubscriptionGrant
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid PlanId { get; set; }

    /// <summary>The plan's catalog key at grant time (<c>pro</c>, <c>team</c>, ...).</summary>
    public string PlanKey { get; set; } = string.Empty;

    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }

    /// <summary>Required, ≤500. Why the plan was given.</summary>
    public string Reason { get; set; } = string.Empty;

    public Guid GrantedByUserId { get; set; }

    /// <summary>The <c>subscriptions</c> row this grant wrote.</summary>
    public Guid SubscriptionId { get; set; }

    /// <summary>The previous manual subscription this grant ended, when it replaced one.</summary>
    public Guid? SupersededSubscriptionId { get; set; }

    public Guid RequestId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When an admin ended the grant early; null while it runs (or has merely lapsed).</summary>
    public DateTime? RevokedAt { get; set; }

    public Guid? RevokedByUserId { get; set; }

    /// <summary>Optional ≤500 note kept with a revoke.</summary>
    public string? RevokeReason { get; set; }
}
