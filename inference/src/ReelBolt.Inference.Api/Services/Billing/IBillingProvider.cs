using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models.Billing;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>What a checkout buys. Exactly one of <c>Plan</c> / <c>TopUpCredits</c> is set.</summary>
/// <param name="OrganizationId">The paying organization; carried to the provider as custom data so webhooks can find it again.</param>
/// <param name="UserId">The member who started the checkout (audit only).</param>
/// <param name="Email">Prefills the checkout; may be null.</param>
/// <param name="Plan">The plan to subscribe to.</param>
/// <param name="Seats">Seat count for a per-seat plan (already validated against the plan minimum).</param>
/// <param name="TopUpCredits">A top-up pack size in credits (must be one of the configured packs).</param>
/// <param name="ExistingCustomerId">The org's provider customer id, when it already has one.</param>
/// <param name="DiscountId">A provider discount to apply to the transaction (a valid code, or an automatic promotion).</param>
/// <param name="PromotionId">The promotion behind <paramref name="DiscountId"/>; carried as custom data back to the webhook.</param>
/// <param name="BonusPromotionId">A top-up bonus promotion promised at checkout; carried as custom data back to the webhook.</param>
/// <param name="Interval">The billing interval of a plan checkout (the plan's annual price id when Annual); ignored for a top-up.</param>
public sealed record CheckoutRequest(
    Guid OrganizationId,
    Guid UserId,
    string? Email,
    Plan? Plan,
    int Seats,
    int? TopUpCredits,
    string? ExistingCustomerId,
    string? DiscountId = null,
    Guid? PromotionId = null,
    Guid? BonusPromotionId = null,
    BillingInterval Interval = BillingInterval.Monthly);

/// <summary>A hosted checkout the browser is sent to (or hands to the provider's JS overlay). Grants nothing by itself.</summary>
/// <param name="CustomerId">The provider customer the checkout belongs to (given or just created); null when it has none yet.</param>
public sealed record CheckoutSession(string Url, string TransactionId, string? CustomerId = null);

/// <summary>
/// An authenticated, short-lived customer-portal session. <paramref name="OverviewUrl"/> is always set;
/// the per-subscription deep links are null when the provider did not return them.
/// </summary>
public sealed record PortalSession(string OverviewUrl, string? CancelUrl, string? UpdatePaymentMethodUrl);

/// <summary>When a plan change takes effect at the provider (and how it is billed).</summary>
public enum PlanChangeTiming
{
    /// <summary>Switch now and charge the prorated difference now (an upgrade).</summary>
    Immediately,

    /// <summary>Switch the items now but bill the new price from the next period (a downgrade; entitlements change at period end).</summary>
    NextBillingPeriod
}

/// <summary>
/// What a plan change does with the subscription's discount. <see cref="Unchanged"/> leaves it as
/// the provider holds it (the request does not mention it); <see cref="Remove"/> removes it in the same request.
/// </summary>
public enum PlanChangeDiscount
{
    Unchanged,
    Remove
}

/// <summary>
/// The provider refused a request with a typed error (Paddle's <c>error.code</c>). It is an
/// <see cref="HttpRequestException"/> so callers that only know "the provider failed" keep working.
/// </summary>
public sealed class BillingProviderException(string code, int status, string detail)
    : HttpRequestException($"Billing provider {status}: {code}: {detail}", null, (System.Net.HttpStatusCode)status)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public string Detail { get; } = detail;
}

/// <summary>The neutral meaning of a provider webhook. Anything the platform does not act on is <see cref="Ignored"/>.</summary>
public enum BillingEventKind
{
    Ignored,
    SubscriptionChanged,
    TopUpPaid,
    PaymentRefunded,

    /// <summary>The proration transaction of a same-period subscription upgrade was paid.</summary>
    UpgradePaid,

    /// <summary>A subscription payment that carried a provider discount (plan promotions). In-memory only: the kind is never persisted.</summary>
    TransactionCompleted
}

/// <summary>
/// A discount to create at the provider for one promotion. Never carries a code: codes are validated by
/// this platform, which passes the discount id itself.
/// </summary>
/// <param name="PromotionId">Our promotion; sent as custom data.</param>
/// <param name="Description">Shown to the buyer by the provider.</param>
/// <param name="Type">Percentage or flat.</param>
/// <param name="Amount">Percentage (<c>"20"</c> = 20%) or, for flat, minor units of <paramref name="CurrencyCode"/>.</param>
/// <param name="CurrencyCode">Required for a flat discount; ignored for a percentage.</param>
/// <param name="Recur">Whether the discount carries over to subscription renewals.</param>
/// <param name="MaximumRecurringIntervals">How many renewals it recurs for; null is forever.</param>
/// <param name="UsageLimit">Global redemption cap at the provider; null is unlimited.</param>
/// <param name="RestrictToPriceIds">Provider price ids it applies to; empty means every price.</param>
/// <param name="ExpiresAt">When the provider stops accepting it (UTC); null never.</param>
public sealed record ProviderDiscountSpec(
    Guid PromotionId,
    string Description,
    PromotionDiscountType Type,
    string Amount,
    string? CurrencyCode,
    bool Recur,
    int? MaximumRecurringIntervals,
    int? UsageLimit,
    IReadOnlyList<string> RestrictToPriceIds,
    DateTime? ExpiresAt);

/// <summary>
/// The provider's own formatted prices for one line, shown verbatim (no hand-written prices).
/// <paramref name="RawTax"/> and <paramref name="RawDiscount"/> are the line's unformatted
/// <c>totals.tax</c> and <c>totals.discount</c> (minor units as a string), for callers that must
/// know whether tax or a discount applies without parsing locale text. Null means unknown.
/// </summary>
/// <para><c>UnitSubtotal</c>/<c>UnitTotal</c> are the line's <c>formatted_unit_totals</c> (one unit of a
/// multi-unit line, e.g. one seat), verbatim; null when the provider did not send them (for example Paddle's
/// <c>formatted_unit_totals</c>).</para>
/// <para>The trailing <c>*Minor</c> values and <c>MinorUnitExponent</c> are the raw integer totals (minor units of
/// <c>CurrencyCode</c>) for providers that return numbers rather than formatted text; null when the provider
/// supplies only formatted strings (Paddle).</para>
public sealed record PricePreview(string CurrencyCode, string Subtotal, string Discount, string Total, string? RawTax = null, string? RawDiscount = null, string? UnitSubtotal = null, string? UnitTotal = null,
    long? SubtotalMinor = null, long? DiscountMinor = null, long? TotalMinor = null, long? UnitSubtotalMinor = null, long? UnitTotalMinor = null, int? MinorUnitExponent = null);

/// <summary>One priced line of a subscription or transaction.</summary>
public sealed record BillingLineItem(string PriceId, int Quantity);

/// <summary>
/// The provider's live view of a subscription, read on demand (not from a webhook). Its items move the
/// moment a change is accepted, while the local row only follows when the webhook lands.
/// </summary>
/// <param name="Status">The provider's own status word (Paddle: <c>active</c>, <c>trialing</c>, <c>past_due</c>, …).</param>
/// <param name="Items">The subscription's current price ids and quantities.</param>
/// <param name="CancelScheduled">True when a cancellation is scheduled.</param>
/// <param name="PeriodEnd">The end of the current billing period, when the provider reports one.</param>
/// <param name="PeriodStart">The start of the current billing period, when the provider reports one.</param>
/// <param name="Discount">The discount the subscription carries, when it carries one.</param>
public sealed record ProviderSubscriptionState(string Status, IReadOnlyList<BillingLineItem> Items, bool CancelScheduled, DateTime? PeriodEnd, DateTime? PeriodStart = null, ProviderSubscriptionDiscount? Discount = null);

/// <summary>The discount on a live subscription: the provider's discount id and when it stops applying (null: while it recurs).</summary>
public sealed record ProviderSubscriptionDiscount(string Id, DateTime? EndsAt);

/// <summary>
/// A provider webhook after signature verification and parsing. Providers produce this; the
/// <see cref="BillingEventProcessor"/> never sees a provider payload shape.
/// </summary>
public sealed record BillingEvent
{
    public required string Provider { get; init; }

    /// <summary>The provider's unique id for this notification (the idempotency key).</summary>
    public required string EventId { get; init; }

    public required string EventType { get; init; }

    /// <summary>When the provider says it happened (not when it was delivered); orders out-of-order events.</summary>
    public required DateTime OccurredAt { get; init; }

    /// <summary>
    /// When the change really happened, for a provider whose <see cref="OccurredAt"/> is the (later) time the event was
    /// completed from a live read (Stripe: the webhook's <c>created</c>). The processor pairs an upgrade payment with its
    /// subscription change on <c>ChangedAt ?? OccurredAt</c>; the stale guard stays on <see cref="OccurredAt"/>. Null for Paddle.
    /// </summary>
    public DateTime? ChangedAt { get; init; }

    public BillingEventKind Kind { get; init; } = BillingEventKind.Ignored;

    /// <summary>From the custom data this platform attached at checkout. Null when absent.</summary>
    public Guid? OrganizationId { get; init; }

    public string? CustomerId { get; init; }
    public string? SubscriptionId { get; init; }

    /// <summary>The transaction (top-up or upgrade payment, or the one a refund adjusts).</summary>
    public string? TransactionId { get; init; }

    /// <summary>
    /// The provider's checkout reference (for example a hosted-checkout session id), when it differs from
    /// <see cref="TransactionId"/>. Used to find the promotion reservation made at checkout. Null when the
    /// provider has no separate checkout id (Paddle).
    /// </summary>
    public string? CheckoutId { get; init; }

    /// <summary>True for the payment that started a subscription (Stripe invoice <c>billing_reason=subscription_create</c>); the provider then resolves <see cref="CheckoutId"/> (DEC-20).</summary>
    public bool FirstSubscriptionInvoice { get; init; }

    /// <summary>
    /// The billing period start the provider states on a plain (non-upgrade) paid subscription transaction (Paddle:
    /// <c>billing_period.starts_at</c>): an explicit signal of which term the payment opens. Null when not stated.
    /// </summary>
    public DateTime? BillingPeriodStart { get; init; }

    /// <summary>The provider's id for a refund adjustment (idempotency of the clawback).</summary>
    public string? AdjustmentId { get; init; }

    public SubscriptionStatus Status { get; init; } = SubscriptionStatus.Active;
    public DateTime? PeriodStart { get; init; }
    public DateTime? PeriodEnd { get; init; }
    public bool CancelAtPeriodEnd { get; init; }

    /// <summary>When the scheduled cancellation takes effect; null when no cancellation is scheduled.</summary>
    public DateTime? CancelEffectiveAt { get; init; }

    /// <summary>
    /// Provider price ids on the subscription, with their quantities. For <see cref="BillingEventKind.UpgradePaid"/>:
    /// only the lines the transaction charged above 0 (the upgrade's new price(s) and quantity, never the
    /// credits for removed items); empty when the payload carries none, so the payment never pairs (R-2).
    /// </summary>
    public IReadOnlyList<BillingLineItem> Items { get; init; } = [];

    /// <summary>For <see cref="BillingEventKind.TopUpPaid"/>: the pack size in credits, resolved from the configured price ids.</summary>
    public int? TopUpCredits { get; init; }

    /// <summary>For <see cref="BillingEventKind.PaymentRefunded"/>: true when the whole payment was returned.</summary>
    public bool FullRefund { get; init; }

    /// <summary>
    /// For <see cref="BillingEventKind.UpgradePaid"/>: the instant the provider prorated the charge from,
    /// when the transaction carries it. The credit grant is prorated from the same instant.
    /// </summary>
    public DateTime? ProrationStartsAt { get; init; }

    /// <summary>
    /// What the provider charged, in minor units of the transaction's currency (Paddle:
    /// <c>details.totals.grand_total</c>), for <b>every</b> completed transaction — a top-up, an upgrade or
    /// a subscription payment, not only <see cref="BillingEventKind.UpgradePaid"/>. 0 when absent or
    /// unparseable, so a payment whose amount is unknown never grants (fail closed);
    /// <see cref="IsPaidTransaction"/> tells "the provider stated an amount" from "it did not".
    /// </summary>
    public long AmountMinor { get; init; }

    /// <summary>
    /// True when this event is a completed transaction whose charged amount the provider stated: the money
    /// side of a top-up, an upgrade or a subscription payment. A payment row is recorded for it whatever
    /// the event's <see cref="Kind"/> does to credits. False for a completed transaction whose payload
    /// carries no parseable amount, so a row is never written claiming an amount the provider never gave.
    /// </summary>
    public bool IsPaidTransaction { get; init; }

    /// <summary>
    /// The provider's own fee for the transaction, in minor units of <see cref="CurrencyCode"/> (Paddle:
    /// <c>details.totals.fee</c>). Null when it reported none — never 0, which would read as "no fee".
    /// </summary>
    public long? FeeMinor { get; init; }

    /// <summary>Paddle <c>details.totals.subtotal</c> (list amount before discount and tax). Null when the provider did not state it; never 0 for unknown.</summary>
    public long? SubtotalMinor { get; init; }

    /// <summary>Paddle <c>details.totals.discount</c>. Null when not stated.</summary>
    public long? DiscountMinor { get; init; }

    /// <summary>Paddle <c>details.totals.tax</c>. Null when not stated.</summary>
    public long? TaxMinor { get; init; }

    /// <summary>Paddle <c>details.totals.total</c> (before any provider credit applied). Null when not stated.</summary>
    public long? TotalMinor { get; init; }

    /// <summary>Paddle <c>details.totals.earnings</c>. Null when not stated.</summary>
    public long? EarningsMinor { get; init; }

    /// <summary>
    /// For <see cref="BillingEventKind.PaymentRefunded"/>: what the buyer got back, tax included, in minor
    /// units of <see cref="CurrencyCode"/>. Null when the provider stated no amount (no refund row is written).
    /// </summary>
    public long? RefundAmountMinor { get; init; }

    /// <summary>For <see cref="BillingEventKind.PaymentRefunded"/>: the tax share of <see cref="RefundAmountMinor"/>, when stated.</summary>
    public long? RefundTaxMinor { get; init; }

    /// <summary>The provider discount applied to the transaction, when any.</summary>
    public string? DiscountId { get; init; }

    /// <summary>The discounted amount in minor units, exactly as the provider sends it.</summary>
    public string? DiscountTotal { get; init; }

    public string? CurrencyCode { get; init; }

    /// <summary>From custom data: the promotion behind <see cref="DiscountId"/>. Null when absent or unparsable.</summary>
    public Guid? PromotionId { get; init; }

    /// <summary>
    /// From custom data: the top-up bonus promotion named at checkout. Informational only: the bonus is
    /// granted from the server's own <see cref="ReelBolt.Shared.Data.Models.Billing.PromotionCheckout"/>
    /// record, never from this client-visible value.
    /// </summary>
    public Guid? BonusPromotionId { get; init; }

    /// <summary>For an <see cref="BillingEventKind.Ignored"/> event the platform deliberately does not act on: why (logged and kept on the webhook row).</summary>
    public string? IgnoredReason { get; init; }
}

/// <summary>
/// A provider entity read on demand (not delivered), turned into the <see cref="BillingEvent"/> its webhook
/// would have produced, so it can be fed through the same <see cref="BillingEventProcessor.ProcessAsync"/>.
/// </summary>
/// <param name="Event">The synthetic event (provider-parsed exactly like the webhook; only its id, type and time are synthetic).</param>
/// <param name="RawJson">The synthetic webhook envelope that was parsed, for the webhook-event audit row.</param>
/// <param name="ProviderUpdatedAt">The provider's own last-modified instant of the entity; null when it reports none.</param>
public sealed record BillingSnapshot(BillingEvent Event, string RawJson, DateTime? ProviderUpdatedAt);

/// <summary>Why a webhook was rejected before it touched any state.</summary>
public sealed class WebhookVerificationException(string message) : Exception(message);

/// <summary>
/// The provider boundary (D11). Everything provider-specific (payload shapes, signature scheme, URLs)
/// lives in an implementation; the rest of the platform sees only these neutral shapes, so changing
/// provider is one new class.
/// </summary>
public interface IBillingProvider
{
    /// <summary>Stable lowercase name stored on subscriptions and webhook rows (<c>paddle</c>).</summary>
    string Name { get; }

    /// <summary>True when the provider has the credentials it needs; checkout and webhooks refuse otherwise.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// True when the provider's subscription snapshot carries no <c>updated_at</c> and the reconciler may still use it
    /// (its synthetic event is stamped with the fetch time, its ids are content hashes). Paddle: false (fails closed).
    /// </summary>
    bool ReconcilesWithoutUpdatedAt => false;

    /// <summary>True when <paramref name="providerTransactionId"/> is one of this provider's transaction ids (Paddle <c>txn_</c>; Stripe <c>cs_</c>, <c>pi_</c>, <c>in_</c>).</summary>
    bool OwnsTransaction(string providerTransactionId) => false;

    /// <summary>Creates a hosted checkout for a plan or a top-up. Never grants credits.</summary>
    Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct);

    /// <summary>
    /// True when the provider already holds a live (active, trialing, past-due or paused) subscription for
    /// this customer that belongs to <paramref name="organizationId"/> — or names no organization at all.
    /// Closes the gap between payment and the first webhook, when no local subscription row exists yet.
    /// Throws <see cref="HttpRequestException"/> when the provider cannot answer.
    /// </summary>
    Task<bool> HasLiveSubscriptionAsync(string providerCustomerId, Guid organizationId, CancellationToken ct);

    /// <summary>An authenticated, short-lived customer-portal session (invoices, payment method, cancel), with per-subscription deep links.</summary>
    Task<PortalSession> CreatePortalSessionAsync(string providerCustomerId, string? providerSubscriptionId, CancellationToken ct);

    /// <summary>
    /// Verifies the signature over the exact raw body and parses it.
    /// Throws <see cref="WebhookVerificationException"/> on a bad or missing signature, a stale
    /// timestamp or a malformed body; nothing is read from an unverified payload.
    /// </summary>
    BillingEvent VerifyAndParseWebhook(string rawBody, IReadOnlyDictionary<string, string> headers);

    /// <summary>
    /// Completes a verified event before it is processed: a provider may read its own API for facts a
    /// webhook cannot carry. The default returns the event unchanged. Throws <see cref="HttpRequestException"/>
    /// when the provider cannot answer, so the webhook answers 500 and the provider retries.
    /// </summary>
    Task<BillingEvent> ResolveAsync(BillingEvent evt, CancellationToken ct) => Task.FromResult(evt);

    /// <summary>The most seats a self-serve plan change may carry; larger teams contact sales.</summary>
    int MaxSelfServeSeats => 200;

    /// <summary>The provider's name as shown to customers (for example in "Manage in ...").</summary>
    string DisplayName => Name;

    /// <summary>Schedules the subscription to cancel at the end of the current billing period. Undone by <see cref="ResumeSubscriptionAsync"/>.</summary>
    Task CancelSubscriptionAsync(string providerSubscriptionId, CancellationToken ct);

    /// <summary>Removes a scheduled change (a pending cancellation) so the subscription renews.</summary>
    Task ResumeSubscriptionAsync(string providerSubscriptionId, CancellationToken ct);

    /// <summary>
    /// Replaces the subscription's items — a plan change and a seat change alike (one code path, so a
    /// seat decrease cannot be billed differently from a plan downgrade). The local row only changes when
    /// the provider's webhook arrives. <paramref name="discount"/> <see cref="PlanChangeDiscount.Remove"/>
    /// removes the subscription's discount in the same request; <see cref="PlanChangeDiscount.Unchanged"/>
    /// does not mention it.
    /// </summary>
    Task ChangePlanAsync(string providerSubscriptionId, IReadOnlyList<BillingLineItem> items, PlanChangeTiming timing, PlanChangeDiscount discount, CancellationToken ct);

    /// <summary>
    /// The subscription as the provider holds it right now. A change request compares it with the local
    /// row to tell an unconfirmed change (webhook not landed) from a settled one.
    /// Throws <see cref="HttpRequestException"/> when the provider cannot answer.
    /// </summary>
    Task<ProviderSubscriptionState> GetSubscriptionAsync(string providerSubscriptionId, CancellationToken ct);

    /// <summary>
    /// The subscription as the provider holds it right now, as the event its <c>subscription.updated</c> webhook
    /// would carry: <c>EventId = "reconcile:sub:{id}:{updated_at UTC ticks}"</c>, <c>EventType = "subscription.reconciled"</c>,
    /// <c>OccurredAt</c> = the provider's <c>updated_at</c> (now when it reports none).
    /// Throws <see cref="BillingProviderException"/> on a typed refusal (404 included), <see cref="HttpRequestException"/> otherwise.
    /// </summary>
    Task<BillingSnapshot> SnapshotSubscriptionAsync(string providerSubscriptionId, CancellationToken ct);

    /// <summary>
    /// The subscription's completed upgrade (proration) payments billed at or after <paramref name="since"/>, each as
    /// the event its <c>transaction.completed</c> webhook would carry: <c>EventId = "reconcile:txn:{id}"</c>,
    /// <c>EventType = "transaction.reconciled"</c>, <c>OccurredAt</c> = <c>billed_at ?? updated_at</c>. Only
    /// <see cref="BillingEventKind.UpgradePaid"/> events are returned. Throws like <see cref="SnapshotSubscriptionAsync"/>.
    /// </summary>
    Task<IReadOnlyList<BillingSnapshot>> CompletedUpgradeTransactionsAsync(string providerSubscriptionId, DateTime since, CancellationToken ct);

    /// <summary>
    /// The id of the customer's live subscription that <see cref="HasLiveSubscriptionAsync"/> would count for
    /// <paramref name="organizationId"/> (one naming this organization first, else one naming none); null when none.
    /// Throws <see cref="HttpRequestException"/> when the provider cannot answer.
    /// </summary>
    Task<string?> FindLiveSubscriptionIdAsync(string providerCustomerId, Guid organizationId, CancellationToken ct);

    /// <summary>The configured top-up price id for a pack size, or null when that pack is not sold.</summary>
    string? TopUpPriceId(int credits);

    /// <summary>The configured top-up pack sizes in credits, ascending.</summary>
    IReadOnlyList<int> TopUpPacks { get; }

    /// <summary>Creates a provider discount for a promotion and returns its id (<c>dsc_…</c>).</summary>
    Task<string> CreateDiscountAsync(ProviderDiscountSpec spec, CancellationToken ct);

    /// <summary>
    /// Updates only the given fields: <paramref name="active"/> sets the status, <paramref name="expiresAt"/>
    /// the expiry, and <paramref name="clearExpiry"/> removes it. Providers have no delete.
    /// </summary>
    Task UpdateDiscountAsync(string discountId, bool? active, DateTime? expiresAt, bool clearExpiry, CancellationToken ct);

    /// <summary>The provider's formatted totals for the first line of <paramref name="items"/>, with an optional discount.</summary>
    Task<PricePreview> PreviewPriceAsync(IReadOnlyList<BillingLineItem> items, string? discountId, CancellationToken ct);

    /// <summary>
    /// Cancels an unpaid checkout transaction (contract C10: a superseded or expired promotion
    /// reservation must not stay payable). Only a transaction that can still be paid (Paddle: draft or
    /// ready) is cancelled; one the provider refuses to cancel (already paid, billed, completed or gone)
    /// is <see cref="ProviderTransactionCancel.NotCancellable"/> and is left alone. Throws
    /// <see cref="HttpRequestException"/> on a failure worth retrying (network, 5xx, auth, rate limit).
    /// </summary>
    Task<ProviderTransactionCancel> CancelTransactionAsync(string providerTransactionId, CancellationToken ct);

    /// <summary>
    /// Refunds a paid transaction (the money-back promise, <see cref="BillingRefundPolicy.MoneyBackDays"/> days, built by the launch-alpha-offer initiative on top of this
    /// member). <paramref name="amountMinor"/> null refunds the whole payment. The credit claw-back is NOT done here: it follows
    /// from the provider's refund webhook through the usual processor path. The default is unsupported (Paddle keeps it);
    /// Stripe implements it. <paramref name="idempotencyKey"/> makes a retried call refund once.
    /// </summary>
    Task<ProviderRefund> RefundTransactionAsync(string providerTransactionId, long? amountMinor, string reason, string idempotencyKey, CancellationToken ct)
        => throw new NotSupportedException($"{Name} does not support refunds from the platform.");

    /// <summary>
    /// Reads a finished hosted checkout back from the provider so the buyer sees the result on return without waiting for the
    /// webhook. <paramref name="organizationId"/> is the caller's active organization: a checkout of another organization is
    /// <see cref="CheckoutConfirmationStatus.NotFound"/>, indistinguishable from an unknown one. The events are the ones the
    /// webhooks would carry (already resolved), in processing order. The default is unsupported (Paddle keeps it).
    /// Throws <see cref="HttpRequestException"/> when the provider cannot answer.
    /// </summary>
    Task<CheckoutConfirmation> ConfirmCheckoutAsync(string checkoutId, Guid organizationId, CancellationToken ct)
        => throw new NotSupportedException($"{Name} does not support confirming a checkout.");
}

/// <summary>What <see cref="IBillingProvider.ConfirmCheckoutAsync"/> found.</summary>
public enum CheckoutConfirmationStatus
{
    /// <summary>Paid (or nothing to pay): the events are ready to process.</summary>
    Confirmed,

    /// <summary>Still open, or payment is async/unpaid: the webhook will land.</summary>
    Pending,

    /// <summary>The session expired unpaid.</summary>
    Abandoned,

    /// <summary>Unknown, or it belongs to another organization (never revealed which).</summary>
    NotFound
}

/// <summary>The result of confirming a checkout.</summary>
/// <param name="Kind"><c>subscription</c> or <c>topUp</c>; empty when not confirmed.</param>
/// <param name="Events">The events to process, in order (subscription event first, then the invoice event).</param>
/// <param name="RawBodies">The synthetic envelope behind each event (parallel to <paramref name="Events"/>), for the webhook-event audit row.</param>
public sealed record CheckoutConfirmation(CheckoutConfirmationStatus Status, string Kind, IReadOnlyList<BillingEvent> Events, IReadOnlyList<string>? RawBodies = null);

/// <summary>A refund created at the provider.</summary>
/// <param name="Id">The provider's refund id.</param>
/// <param name="AmountMinor">The refunded amount in minor units of <paramref name="CurrencyCode"/>.</param>
/// <param name="CurrencyCode">Upper-case ISO currency code.</param>
/// <param name="Status">The provider's status word (Stripe: <c>pending</c>, <c>succeeded</c>, <c>failed</c>, ...).</param>
public sealed record ProviderRefund(string Id, long AmountMinor, string CurrencyCode, string Status);

/// <summary>What <see cref="IBillingProvider.CancelTransactionAsync"/> did.</summary>
public enum ProviderTransactionCancel
{
    /// <summary>The transaction is cancelled and can no longer be paid.</summary>
    Cancelled,

    /// <summary>The provider refused: the transaction is past the point of cancelling (or unknown). Left alone.</summary>
    NotCancellable,
}
