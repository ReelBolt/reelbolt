namespace ReelBolt.Shared.Data.Models.Billing;

/// <summary>
/// One free trial per normalized email (table <c>trial_claims</c>); only the SHA-256 is stored, so
/// deleting an account does not let the same address claim again. Used by signup (C5).
/// </summary>
public class TrialClaim
{
    public Guid Id { get; set; }

    /// <summary>Lowercase hex SHA-256 of the normalized email. Unique.</summary>
    public string EmailSha256 { get; set; } = string.Empty;

    public Guid? OrganizationId { get; set; }
    public DateTime ClaimedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Why the first-video grant was refused for this claim (<c>disposable_email</c>, <c>tor_exit</c> or <c>vpn_proxy_hosting</c>);
    /// null when it was granted or never applied. Never the raw sign-up IP.
    /// </summary>
    public string? FirstVideoDeniedReason { get; set; }
}

/// <summary>Maps an organization to its customer record at a billing provider (table <c>billing_customers</c>).</summary>
public class BillingCustomer
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderCustomerId { get; set; } = string.Empty;
    public string? Email { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// An inbound provider webhook, stored before processing so a redelivery is detected by the unique
/// (<see cref="Provider"/>, <see cref="EventId"/>) pair (table <c>billing_webhook_events</c>).
/// </summary>
public class BillingWebhookEvent
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;

    /// <summary>jsonb raw payload.</summary>
    public string PayloadJson { get; set; } = "{}";

    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// How processing ended (<c>Applied|Duplicate|Stale|Unresolved</c>), written with <see cref="ProcessedAt"/>;
    /// null on a row from before the column existed or one still being processed.
    /// </summary>
    public string? Outcome { get; set; }
}

/// <summary>
/// One completed payment: the money the provider actually took for one transaction, promoted out of
/// the retained webhook payload so revenue is queryable with plain SQL. One row per
/// (provider, provider_transaction_id), so a webhook replay can never double-count.
/// <para>
/// Amounts are integer minor units exactly as the provider sent them ("1450" is $14.50); the
/// conversion to a display currency stays at the edge, never here. The row carries no credential and
/// no personal data — no card details, no customer id, no address — and no foreign key to
/// <c>organizations</c>, so the revenue trail outlives an account purge, for the same reason
/// <see cref="BillingWebhookEvent"/> has none.
/// </para>
/// </summary>
public class BillingPayment
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = string.Empty;

    /// <summary>The provider's transaction id: the revenue key. Unique per provider.</summary>
    public string ProviderTransactionId { get; set; } = string.Empty;

    /// <summary>The subscription the transaction paid for; null for a stand-alone top-up.</summary>
    public string? ProviderSubscriptionId { get; set; }

    /// <summary>The organization the payload itself named; null when it named none (never inferred here).</summary>
    public Guid? OrganizationId { get; set; }

    /// <summary>What the provider charged, in integer minor units of <see cref="CurrencyCode"/>.</summary>
    public long AmountMinor { get; set; }

    /// <summary>ISO-4217 code the amount is in, as the provider sent it; null when it sent none.</summary>
    public string? CurrencyCode { get; set; }

    /// <summary>The provider's own fee, in minor units; null when it reported none. Never 0 for "unknown".</summary>
    public long? FeeMinor { get; set; }

    /// <summary>Paddle <c>details.totals.subtotal</c>: the list amount before discount and tax. Null = not sent, never 0 for unknown.</summary>
    public long? SubtotalMinor { get; set; }

    /// <summary>Paddle <c>details.totals.discount</c>: what the discount took off. Null = not sent; 0 = sent as no discount.</summary>
    public long? DiscountMinor { get; set; }

    /// <summary>Paddle <c>details.totals.tax</c>. Null = not sent.</summary>
    public long? TaxMinor { get; set; }

    /// <summary><c>details.totals.total</c>: subtotal - discount + tax, before any Paddle credit applied (<see cref="AmountMinor"/> is the grand total after it). Null = not sent.</summary>
    public long? TotalMinor { get; set; }

    /// <summary>Paddle <c>details.totals.earnings</c>: what the seller keeps after fee and tax. Null = not sent.</summary>
    public long? EarningsMinor { get; set; }

    /// <summary>When the provider says the payment happened (not when the webhook was delivered).</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>When this row was written.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// An approved refund or chargeback the provider returned (table <c>billing_refunds</c>, Inference API
/// owned): one row per provider adjustment, unique per provider + adjustment id so a replay never counts
/// twice. Like <see cref="BillingPayment"/> it carries no personal data and no FK to organizations.
/// </summary>
public class BillingRefund
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = string.Empty;

    /// <summary>The provider's adjustment (Stripe: refund or dispute) id: the idempotency key. Unique per provider.</summary>
    public string ProviderAdjustmentId { get; set; } = string.Empty;

    /// <summary>The transaction the adjustment returned money for; null when the provider named none.</summary>
    public string? ProviderTransactionId { get; set; }

    /// <summary>What was returned to the buyer, tax included, in minor units of <see cref="CurrencyCode"/>.</summary>
    public long AmountMinor { get; set; }

    /// <summary>The tax share of <see cref="AmountMinor"/>; null when the provider did not state it.</summary>
    public long? TaxMinor { get; set; }

    public string? CurrencyCode { get; set; }

    /// <summary>When the provider says the adjustment happened.</summary>
    public DateTime OccurredAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// "A subscription change was sent to the provider and its webhook is due" (table
/// <c>billing_reconcile_watches</c>, Inference API owned, one row per organization). The reconciler reads
/// it to tell a lost webhook from one still on its way, and records each check's result on it.
/// </summary>
public class BillingReconcileWatch
{
    /// <summary>Primary key: one watch per organization.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>What was asked last: <c>cancel|resume|change_plan|seats|keep|checkout|overdue</c>.</summary>
    public string ChangeKind { get; set; } = string.Empty;

    /// <summary>When the change was accepted by the provider; null = nothing due.</summary>
    public DateTime? ChangeRequestedAt { get; set; }

    /// <summary>The member who asked for it (audit only); null for a watch the platform opened itself.</summary>
    public Guid? ChangeRequestedBy { get; set; }

    public DateTime? LastCheckedAt { get; set; }

    /// <summary>The last reconcile's outcome (<c>Healed</c>, <c>InSync</c>, <c>ProviderUnavailable</c>, …).</summary>
    public string? LastOutcome { get; set; }

    /// <summary>A short, secret-free reason for the last failure; null when the last check succeeded.</summary>
    public string? LastError { get; set; }

    /// <summary>Checks in a row that could not read the provider or apply its state; reset by a success.</summary>
    public int ConsecutiveFailures { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// An audited platform-admin billing action, e.g. a forced "Resync from Paddle" (table
/// <c>billing_admin_actions</c>, Inference API owned).
/// </summary>
public class BillingAdminAction
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ActorUserId { get; set; }

    /// <summary>What was done: <c>resync</c>.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>The admin's free-text reason; may be null.</summary>
    public string? Reason { get; set; }

    /// <summary>The action's result (for a resync, the reconcile outcome).</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>jsonb detail (drift fields, events applied, error).</summary>
    public string DetailJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One same-period upgrade and its proration payment, matched up whichever arrives first (table
/// <c>billing_pending_upgrade_grants</c>, Inference API owned). The subscription side
/// (<see cref="NewEntitlementMillis"/> set) records the upgrade; the payment side
/// (<see cref="TransactionId"/> set) records the paid proration transaction. The credit difference is
/// granted once both halves are on the row (<see cref="GrantedAt"/>), and clawed back at most once on a
/// refund or chargeback of that transaction (<see cref="ClawedBackAt"/>).
/// </summary>
public class BillingPendingUpgradeGrant
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderSubscriptionId { get; set; } = string.Empty;

    /// <summary>The billing period the upgrade happened in; null on a payment not yet matched to its upgrade.</summary>
    public DateTime? PeriodStart { get; set; }

    public DateTime? PeriodEnd { get; set; }

    /// <summary>Monthly entitlement before the upgrade; null until the subscription side arrives.</summary>
    public long? OldEntitlementMillis { get; set; }

    /// <summary>Monthly entitlement after the upgrade; null until the subscription side arrives.</summary>
    public long? NewEntitlementMillis { get; set; }

    /// <summary>When the upgrade took effect (the subscription event's <c>occurred_at</c>).</summary>
    public DateTime? EffectiveAt { get; set; }

    /// <summary>The paid proration transaction; null while the upgrade is unpaid. Unique per provider.</summary>
    public string? TransactionId { get; set; }

    /// <summary>When the provider says the transaction completed.</summary>
    public DateTime? PaidAt { get; set; }

    /// <summary>The instant the provider prorated the charge from, when the transaction carried it.</summary>
    public DateTime? ProrationStartsAt { get; set; }

    /// <summary>
    /// Security R-2: the upgrade's target items, canonical (<c>priceId:quantity</c> sorted ordinally, comma
    /// separated); set by the subscription side. A payment pairs only with an upgrade whose target equals its
    /// <see cref="ChargedItems"/> exactly. Null (a row from before T-011) never pairs.
    /// </summary>
    public string? TargetItems { get; set; }

    /// <summary>
    /// Security R-2: the items the proration transaction charged positively, in the same canonical form as
    /// <see cref="TargetItems"/>; set by the payment side. Null (no usable items on the transaction) never pairs.
    /// </summary>
    public string? ChargedItems { get; set; }

    public long? GrantedMillis { get; set; }
    public DateTime? GrantedAt { get; set; }
    public long? ClawedBackMillis { get; set; }
    public DateTime? ClawedBackAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
