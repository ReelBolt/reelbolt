using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing.Promotions;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>How a webhook ended. Every outcome except an exception is a 200 to the provider.</summary>
public enum BillingEventOutcome
{
    /// <summary>State changed (or the event is one the platform deliberately ignores).</summary>
    Applied,

    /// <summary>The (provider, event id) pair was already stored; nothing ran.</summary>
    Duplicate,

    /// <summary>Older than the newest event already applied to that subscription; recorded, not applied.</summary>
    Stale,

    /// <summary>Recorded but not actionable (unknown organization or price); see the row's <c>error</c>.</summary>
    Unresolved
}

/// <summary>
/// Applies a verified <see cref="BillingEvent"/> with exactly-once effects. The webhook row, the
/// subscription change and every ledger entry commit in ONE transaction, so a redelivery finds the row
/// and is a no-op, a crash leaves nothing half done, and a failure rolls the row back so the provider's
/// retry runs it again. Credit effects carry their own idempotency keys as a second line of defence
/// (<c>plan:</c> per subscription period, <c>plan-upgrade:</c> and <c>topup:</c> per transaction,
/// <c>refund:</c> per adjustment, <c>clawback:</c> per refunded top-up transaction), so even a
/// differently-numbered event describing the same payment cannot grant (or claw back) twice.
/// <para>
/// Webhooks are authoritative: a checkout redirect or client call never changes a subscription or a
/// balance. Events can arrive out of order, so every subscription change is gated by
/// <see cref="Subscription.LastEventAt"/>.
/// </para>
/// </summary>
public sealed class BillingEventProcessor(
    InferenceApiDbContext db,
    ILogger<BillingEventProcessor> logger,
    TimeProvider? time = null,
    Microsoft.Extensions.Options.IOptions<PromotionOptions>? promotionOptions = null,
    IProductEventLog? productEvents = null,
    IServiceScopeFactory? scopeFactory = null)
{
    /// <summary>The most uses a promotion is ever honoured past its global cap (contract C10).</summary>
    public const int MaxOvershoot = 5;

    /// <summary>
    /// The global overshoot bound of a promotion (contract C10): min(5, max(1, MaxRedemptions / 100)) in
    /// integer division, or 5 when the promotion has no global cap (its per-user and per-org caps can still
    /// be overshot). Beyond it nothing more is granted or counted.
    /// </summary>
    public static int OvershootBound(int? maxRedemptions)
        => maxRedemptions is { } max ? Math.Min(MaxOvershoot, Math.Max(1, max / 100)) : MaxOvershoot;

    /// <summary>The Warning logged once for every refused grant or count (a NotGranted redemption row).</summary>
    public static readonly EventId PromotionNotGranted = new(41_701, "PromotionNotGranted");

    /// <summary>Meter name; the Inference API's telemetry registration adds it with <c>AddMeter</c>.</summary>
    public const string MeterName = "ReelBolt.Inference.Billing";

    private static readonly System.Diagnostics.Metrics.Meter Meter = new(MeterName);

    /// <summary>
    /// N-20: one per reserved payment refused because the promotion's global overshoot bound is
    /// used up, tagged <c>kind</c> (the promotion kind) and <c>promotion_id</c>. Alert on any increase: the
    /// buyer paid for a promotion support has to make good.
    /// </summary>
    public static readonly System.Diagnostics.Metrics.Counter<long> OvershootBoundHit =
        Meter.CreateCounter<long>("reelbolt.billing.promotion_overshoot_bound_hit", "payments", "Reserved promotion payments refused because the overshoot bound was used up");

    /// <summary>plan changes that removed a recurring promo discount because the new price is outside the promotion's targets.</summary>
    public static readonly System.Diagnostics.Metrics.Counter<long> PromotionDiscountRemovedOnPlanChange =
        Meter.CreateCounter<long>("reelbolt.billing.promotion_discount_removed_on_plan_change", "changes", "Plan changes that removed a recurring promo discount outside its target prices");

    /// <summary>How long a top-up lives (the catalog's <c>topUp.expiresAfterMonths</c>).</summary>
    public const int TopUpExpiryMonths = 12;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>How long a reservation lives (<c>Billing:Promotions:CheckoutReservationMinutes</c>); a later payment finds it expired.</summary>
    private readonly TimeSpan _reservationLifetime = TimeSpan.FromMinutes(Math.Max(1, (promotionOptions?.Value ?? new PromotionOptions()).CheckoutReservationMinutes));

    /// <summary>
    /// The <c>product_events</c> writer for the payment step (product-events-funnel T-004). Production
    /// resolves the registered <see cref="IProductEventLog"/> from a fresh scope after the money commits;
    /// this instance is the fallback for a processor built without a scope factory (the tests).
    /// </summary>
    private readonly IProductEventLog _productEvents = productEvents ?? new ProductEventLog(db);

    /// <summary>
    /// Opens the fresh scope the payment step's funnel row is written on, <b>after</b> the money
    /// transaction has committed, so a failed funnel write cannot roll the payment back.
    /// </summary>
    private readonly IServiceScopeFactory? _scopeFactory = scopeFactory;

    /// <summary>
    /// The payment this call committed and owes a funnel row to, captured inside the transaction and
    /// emitted after it commits. Null when the payment named no resolvable organization (nothing to
    /// attribute the step to) or when this call did not record a new payment.
    /// </summary>
    private (Guid OrganizationId, string? TransactionId)? _pendingFirstPayment;

    /// <summary>
    /// The event name the payment step writes (docs/product-events.md): the first paid transaction recorded
    /// for an organization, observed server-side.
    /// </summary>
    public const string FirstPaymentEvent = ProductEventLog.FirstPaymentEvent;

    public static string PlanRefillKey(string provider, string subscriptionId, DateTime periodStart)
        // A period read back from the database has Kind Unspecified: it is UTC, so it must not be shifted as local time.
        => $"plan:{provider}:{subscriptionId}:{(periodStart.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(periodStart, DateTimeKind.Utc) : periodStart.ToUniversalTime()):yyyyMMddTHHmmssZ}";

    /// <summary>
    /// How far apart a proration payment and an upgrade may be and still belong together: the payment's
    /// proration start (else its completion time) against the upgrade event's <c>occurred_at</c>. The
    /// provider prorates from the instant it applies the change and stamps the update event at that same
    /// change, so a real pair is seconds apart; anything further is a different upgrade's payment and stays
    /// unmatched (F-1 re-review).
    /// </summary>
    public static readonly TimeSpan UpgradePairingTolerance = TimeSpan.FromMinutes(10);

    private const string SubscriptionLockPrefix = "billing-subscription:";

    /// <summary>
    /// The transaction-scoped advisory lock key that serialises every webhook of one subscription: the
    /// first 8 bytes of SHA-256 of <c>"billing-subscription:{provider}:{subscriptionId}"</c>, stable across
    /// processes and replicas.
    /// </summary>
    public static long SubscriptionLockKey(string provider, string subscriptionId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{SubscriptionLockPrefix}{provider}:{subscriptionId}"));
        return BitConverter.ToInt64(hash, 0);
    }

    private const string TransactionLockPrefix = "billing-transaction:";

    /// <summary>
    /// The transaction-scoped advisory lock key that serialises a top-up payment and its refund (N-3): the
    /// first 8 bytes of SHA-256 of <c>"billing-transaction:{provider}:{transactionId}"</c>. Always taken
    /// before <see cref="SubscriptionLockKey"/>, so the two locks never form a cycle.
    /// </summary>
    public static long TransactionLockKey(string provider, string transactionId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{TransactionLockPrefix}{provider}:{transactionId}"));
        return BitConverter.ToInt64(hash, 0);
    }

    /// <summary>The credit difference of a same-period upgrade, once per paid proration transaction.</summary>
    public static string PlanUpgradeKey(string provider, string transactionId) => $"plan-upgrade:{provider}:{transactionId}";

    /// <summary>Monthly credits of a plan at a seat count (per-seat plans multiply).</summary>
    public static long EntitlementMillis(Plan plan, int seats)
        => plan.MonthlyCreditsMillis * (plan.Kind == PlanKind.PerSeat ? seats : 1);

    /// <summary>Monthly price of a plan at a seat count (per-seat plans multiply); a plan without a price counts as 0.</summary>
    public static decimal MonthlyPriceUsd(Plan plan, int seats)
        => (plan.PriceMonthlyUsd ?? 0m) * (plan.Kind == PlanKind.PerSeat ? seats : 1);

    /// <summary>
    /// Plan decision 1: a change is a downgrade when the monthly price (× seats) goes down; the same or
    /// a higher total is an upgrade. The change-plan endpoint picks the provider timing with this, and the
    /// webhook path classifies the provider's resulting update with the same rule, so they cannot disagree.
    /// </summary>
    public static bool IsDowngrade(Plan from, int fromSeats, Plan to, int toSeats)
        => IsDowngrade(from, fromSeats, to, toSeats, BillingInterval.Monthly, BillingInterval.Monthly);

    /// <summary>
    /// Interval-aware direction. A change that also moves the interval (monthly to annual) is never a
    /// downgrade: it is immediate and the provider prorates. Within one interval the per-month-equivalent
    /// prices (annual price / 12 on an annual subscription) are compared.
    /// </summary>
    public static bool IsDowngrade(Plan from, int fromSeats, Plan to, int toSeats, BillingInterval fromInterval, BillingInterval toInterval)
        => fromInterval == toInterval
            && MonthlyEquivalentUsd(to, toSeats, toInterval) < MonthlyEquivalentUsd(from, fromSeats, fromInterval);

    /// <summary>Price per month at a seat count on <paramref name="interval"/>: the monthly price, or the annual price / 12.</summary>
    public static decimal MonthlyEquivalentUsd(Plan plan, int seats, BillingInterval interval)
    {
        if (interval == BillingInterval.Monthly)
        {
            return MonthlyPriceUsd(plan, seats);
        }

        decimal annual = ReelBolt.Inference.Api.Billing.PlanPricing.AnnualPriceUsd(plan) ?? (plan.PriceMonthlyUsd ?? 0m) * 10m;
        return annual / 12m * (plan.Kind == PlanKind.PerSeat ? seats : 1);
    }

    /// <summary>
    /// The credits a same-period upgrade grants: the monthly-entitlement difference scaled by the share of
    /// the period still to run, matching what the provider charges for a <c>prorated_immediately</c>
    /// change. <c>remaining = clamp(periodEnd − at, 0, periodLength)</c>; rounded down to whole
    /// millicredits; 0 when the entitlement does not rise or either period bound is unknown. The webhook
    /// grants with this once the proration is paid (at the instant the provider prorated from) and the
    /// change-plan endpoint announces it (at now), so the estimate and the grant cannot disagree.
    /// </summary>
    public static long ProratedUpgradeGrantMillis(long oldEntitlementMillis, long newEntitlementMillis, DateTime? periodStart, DateTime? periodEnd, DateTime at)
    {
        long difference = newEntitlementMillis - oldEntitlementMillis;
        if (difference <= 0 || periodStart is not { } start || periodEnd is not { } end)
        {
            return 0;
        }

        if (SubscriptionInterval.Of(start, end) == BillingInterval.Annual)
        {
            // An annual term grants and prorates by month: only the current month's difference, never the year's.
            (start, end) = AnnualCreditSchedule.MonthWindow(start, end, at);
        }

        long length = (end - start).Ticks;
        if (length <= 0)
        {
            return 0;
        }

        long remaining = Math.Clamp((end - at).Ticks, 0, length);
        // Int128: a large seat difference times a month of ticks overflows a long.
        return (long)((Int128)difference * remaining / length);
    }

    public static string TopUpKey(string provider, string transactionId) => $"topup:{provider}:{transactionId}";

    public static string RefundKey(string provider, string adjustmentId) => $"refund:{provider}:{adjustmentId}";

    /// <summary>
    /// The clawback of a fully refunded top-up, keyed per TRANSACTION (N-02): a refund and a later
    /// chargeback of the same payment are two adjustments but claw back once between them.
    /// </summary>
    public static string ClawbackKey(string provider, string transactionId) => $"clawback:{provider}:{transactionId}";

    /// <summary>The Promo grant a pack bonus adds on top of a top-up, one per transaction.</summary>
    public static string TopUpBonusKey(string provider, string transactionId) => $"topup-bonus:{provider}:{transactionId}";

    /// <summary>The clawback of a top-up's bonus when the top-up is fully refunded, once per transaction.</summary>
    public static string BonusClawbackKey(string provider, string transactionId) => $"clawback-bonus:{provider}:{transactionId}";

    public async Task<BillingEventOutcome> ProcessAsync(BillingEvent evt, string rawBody, CancellationToken ct = default)
    {
        if (await db.BillingWebhookEvents.AsNoTracking().AnyAsync(e => e.Provider == evt.Provider && e.EventId == evt.EventId, ct).ConfigureAwait(false))
        {
            return BillingEventOutcome.Duplicate;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        // N-3: a top-up payment and its refund write different tables (ledger vs. tombstone), so only a
        // common lock stops both committing. A completed transaction takes it too, whatever its kind, so two
        // deliveries of the same payment (a replay carrying a fresh event id) serialise and the payment row
        // is written once. Taken first, before the subscription lock, in every path.
        if (evt.IsPaidTransaction || evt.Kind is BillingEventKind.PaymentRefunded)
        {
            await LockTransactionAsync(evt.Provider, evt.TransactionId, ct).ConfigureAwait(false);
        }

        // Before any read of the subscription or its pending upgrade rows: a subscription.updated and the
        // transaction.completed of the same upgrade must see each other's rows, not both insert unpaired ones.
        if (evt.Kind is BillingEventKind.SubscriptionChanged or BillingEventKind.UpgradePaid or BillingEventKind.PaymentRefunded)
        {
            await LockSubscriptionAsync(evt.Provider, evt.SubscriptionId, ct).ConfigureAwait(false);
        }

        BillingWebhookEvent row = new()
        {
            Id = Guid.NewGuid(),
            Provider = evt.Provider,
            EventId = evt.EventId,
            EventType = evt.EventType,
            PayloadJson = rawBody,
            ReceivedAt = _time.GetUtcNow().UtcDateTime,
            Attempts = 1
        };
        db.BillingWebhookEvents.Add(row);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A concurrent delivery of the same event won the unique index: it owns the work.
            db.Entry(row).State = EntityState.Detached;
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return BillingEventOutcome.Duplicate;
        }

        BillingEventOutcome outcome;
        try
        {
            string? note;
            (outcome, note) = await ApplyAsync(evt, ct).ConfigureAwait(false);
            row.ProcessedAt = _time.GetUtcNow().UtcDateTime;
            row.Error = note;
            row.Outcome = outcome.ToString();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // The transaction is rolled back, so the payment this call stashed is not owed a funnel row:
            // drop it before rethrowing, or a later event on this instance would emit a stale activation.
            _pendingFirstPayment = null;
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            throw;
        }

        // After the money is committed, on a fresh scope: the funnel row is derived from billing_payments
        // and must never be able to roll the payment back, so it is written once the transaction is gone.
        await EmitPendingFirstPaymentAsync(ct).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>
    /// Serialises the webhooks of one subscription across replicas with <c>pg_advisory_xact_lock</c>, held
    /// until the per-event transaction commits or rolls back. A no-op without a subscription id and on
    /// non-Postgres providers (Sqlite/InMemory tests, where a single process applies events one at a time).
    /// </summary>
    private async Task LockSubscriptionAsync(string provider, string? subscriptionId, CancellationToken ct)
    {
        if (subscriptionId is null || !db.Database.IsNpgsql())
        {
            return;
        }

        long key = SubscriptionLockKey(provider, subscriptionId);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Serialises the events of one transaction (a top-up payment and its refund) with
    /// <c>pg_advisory_xact_lock</c> on <see cref="TransactionLockKey"/>, like <see cref="LockSubscriptionAsync"/>:
    /// a no-op without a transaction id and off Postgres.
    /// </summary>
    private async Task LockTransactionAsync(string provider, string? transactionId, CancellationToken ct)
    {
        if (transactionId is null || !db.Database.IsNpgsql())
        {
            return;
        }

        long key = TransactionLockKey(provider, transactionId);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", ct).ConfigureAwait(false);
    }

    private async Task<(BillingEventOutcome, string?)> ApplyAsync(BillingEvent evt, CancellationToken ct)
    {
        await RecordPaymentAsync(evt, ct).ConfigureAwait(false);
        await RecordRefundAsync(evt, ct).ConfigureAwait(false);
        return evt.Kind switch
        {
            BillingEventKind.SubscriptionChanged => await ApplySubscriptionAsync(evt, ct).ConfigureAwait(false),
            BillingEventKind.TopUpPaid => await ApplyTopUpAsync(evt, ct).ConfigureAwait(false),
            BillingEventKind.PaymentRefunded => await ApplyRefundAsync(evt, ct).ConfigureAwait(false),
            BillingEventKind.UpgradePaid => await ApplyUpgradePaidAsync(evt, ct).ConfigureAwait(false),
            BillingEventKind.TransactionCompleted => await ApplyDiscountedTransactionAsync(evt, ct).ConfigureAwait(false),
            _ => (BillingEventOutcome.Applied, Ignored(evt))
        };
    }

    /// <summary>
    /// Records the money a completed transaction actually took, one <see cref="BillingPayment"/> row per
    /// provider transaction, before anything else touches credits: a payment whose organization cannot be
    /// resolved, or whose credits are not granted, is still money received. Idempotent twice over — the
    /// caller holds the transaction's advisory lock, and the row's unique index is the second line of
    /// defence — so a replay, or a differently-numbered event for the same payment, can never double-count
    /// revenue.
    /// <para>
    /// The organization is the one our own records corroborate, resolved in the same order the credit
    /// handlers use (<c>subscriptions</c> row, else the payload's own claim when it names an organization
    /// that exists, else the <c>billing_customers</c> row) — the same rule the backfill applies, so the two
    /// paths cannot attribute one payment to two organizations. Never a non-existent organization, and
    /// never a dropped row: money with no resolvable owner is recorded unattributed.
    /// </para>
    /// </summary>
    private async Task RecordPaymentAsync(BillingEvent evt, CancellationToken ct)
    {
        if (!evt.IsPaidTransaction || evt.TransactionId is null)
        {
            return;
        }

        bool recorded = await db.BillingPayments.AsNoTracking()
            .AnyAsync(p => p.Provider == evt.Provider && p.ProviderTransactionId == evt.TransactionId, ct)
            .ConfigureAwait(false);
        if (recorded)
        {
            return;
        }

        Guid? orgId = evt.OrganizationId is { } claimed && await db.Organizations.AnyAsync(o => o.Id == claimed, ct).ConfigureAwait(false)
            ? claimed
            : null;
        if (evt.SubscriptionId is { } subscriptionId)
        {
            Guid? subOrg = await db.Subscriptions.AsNoTracking()
                .Where(s => s.Provider == evt.Provider && s.ProviderSubscriptionId == subscriptionId)
                .Select(s => (Guid?)s.OrganizationId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            orgId = subOrg ?? orgId;
        }

        orgId ??= await OrganizationByCustomerAsync(evt, ct).ConfigureAwait(false);

        db.BillingPayments.Add(new BillingPayment
        {
            Id = Guid.NewGuid(),
            Provider = evt.Provider,
            ProviderTransactionId = evt.TransactionId,
            ProviderSubscriptionId = evt.SubscriptionId,
            OrganizationId = orgId,
            AmountMinor = evt.AmountMinor,
            CurrencyCode = evt.CurrencyCode,
            FeeMinor = evt.FeeMinor,
            SubtotalMinor = evt.SubtotalMinor,
            DiscountMinor = evt.DiscountMinor,
            TaxMinor = evt.TaxMinor,
            TotalMinor = evt.TotalMinor,
            EarningsMinor = evt.EarningsMinor,
            OccurredAt = evt.OccurredAt,
            CreatedAt = _time.GetUtcNow().UtcDateTime
        });

        if (evt.SubscriptionId is { } pairedSubscription && evt.Kind != BillingEventKind.UpgradePaid)
        {
            // An upgrade's payment pairs only through its own upgrade row (ApplyUpgradePaidAsync): an unpaired seat
            // payment must never be taken for the term payment.
            Subscription? termSub = await db.Subscriptions
                .FirstOrDefaultAsync(s => s.Provider == evt.Provider && s.ProviderSubscriptionId == pairedSubscription, ct).ConfigureAwait(false);
            if (termSub is not null)
            {
                // Stripe states the term itself (invoice billing_reason subscription_create / subscription_cycle,
                // carried as TransactionCompleted with the invoice's billing period).
                PairTermPayment(termSub, evt.TransactionId, evt.AmountMinor, evt.BillingPeriodStart ?? (evt.Kind == BillingEventKind.TransactionCompleted ? evt.PeriodStart : null));
            }
        }

        if (evt.AmountMinor <= 0)
        {
            // A 100%-off (zero-amount) payment is recorded as the revenue fact it is, but it is not the
            // activation "first payment": the funnel row is claimed once per organization, so a zero
            // would use it up before any money moved.
        }
        else if (orgId is { } resolvedOrganization)
        {
            // Owed once this transaction commits; emitted on a fresh scope after it (see ProcessAsync).
            _pendingFirstPayment = (resolvedOrganization, evt.TransactionId);
        }
        else
        {
            logger.LogWarning(
                "Paid transaction {Transaction} names no resolvable organization; no {Event} funnel row is written (billing_payments remains the authoritative record).",
                evt.TransactionId, FirstPaymentEvent);
        }
    }

    /// <summary>
    /// Records the money an approved refund or chargeback returned, one <see cref="BillingRefund"/> row per
    /// provider adjustment (unique per provider + adjustment id, so a replay never counts twice). Written
    /// whatever the refund does to credits: a refund of a subscription payment claws nothing back, but the
    /// money still left. No row when the provider stated no amount.
    /// </summary>
    private async Task RecordRefundAsync(BillingEvent evt, CancellationToken ct)
    {
        if (evt.Kind != BillingEventKind.PaymentRefunded || evt.AdjustmentId is null || evt.RefundAmountMinor is not { } amount)
        {
            return;
        }

        bool recorded = await db.BillingRefunds.AsNoTracking()
            .AnyAsync(r => r.Provider == evt.Provider && r.ProviderAdjustmentId == evt.AdjustmentId, ct)
            .ConfigureAwait(false);
        if (recorded)
        {
            return;
        }

        db.BillingRefunds.Add(new BillingRefund
        {
            Id = Guid.NewGuid(),
            Provider = evt.Provider,
            ProviderAdjustmentId = evt.AdjustmentId,
            ProviderTransactionId = evt.TransactionId,
            AmountMinor = amount,
            TaxMinor = evt.RefundTaxMinor,
            CurrencyCode = evt.CurrencyCode,
            OccurredAt = evt.OccurredAt,
            CreatedAt = _time.GetUtcNow().UtcDateTime
        });
    }

    /// <summary>
    /// Writes the funnel row a committed payment owes (initiative product-events-funnel T-004), on a fresh
    /// scope so the write happens <b>after</b> the money transaction has committed and can never roll it
    /// back: a connection drop, a deadlock, a statement timeout, a constraint violation or a missing table
    /// here loses a metric, never a payment. The row is <em>derived</em> from <c>billing_payments</c>, which
    /// stays authoritative for the money. Best effort throughout: the whole write is wrapped so a failure is
    /// logged and swallowed.
    /// </summary>
    private async Task EmitPendingFirstPaymentAsync(CancellationToken ct)
    {
        if (_pendingFirstPayment is not { } pending)
        {
            return;
        }

        _pendingFirstPayment = null;
        try
        {
            if (_scopeFactory is { } scopes)
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                await WriteFirstPaymentAsync(
                    scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>(),
                    scope.ServiceProvider.GetRequiredService<IProductEventLog>(),
                    pending,
                    ct).ConfigureAwait(false);
                return;
            }

            await WriteFirstPaymentAsync(db, _productEvents, pending, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to write the {Event} funnel row for organization {Organization} (transaction {Transaction}); the committed payment record is unaffected.",
                FirstPaymentEvent, pending.OrganizationId, pending.TransactionId);
        }
    }

    /// <summary>
    /// The funnel row itself: the organization's Owner (lowest <c>created_at</c>) as the person, best
    /// effort — a payment may have no resolvable person and inventing one would be a lie. The write is the
    /// claim: the partial unique index makes it at most one <c>alpha.first_payment</c> row per organization,
    /// so a second paid transaction (or a concurrent writer) appends nothing.
    /// </summary>
    private async Task WriteFirstPaymentAsync(
        InferenceApiDbContext context,
        IProductEventLog writer,
        (Guid OrganizationId, string? TransactionId) pending,
        CancellationToken ct)
    {
        Guid? ownerId = await context.OrganizationMemberships.AsNoTracking()
            .Where(m => m.OrganizationId == pending.OrganizationId && m.Role == OrganizationRole.Owner)
            .OrderBy(m => m.CreatedAt)
            .Select(m => (Guid?)m.UserId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        await writer.ClaimAndAppendFirstPaymentAsync(pending.OrganizationId, ownerId, ct).ConfigureAwait(false);
    }

    /// <summary>An event the platform deliberately does not act on; a stated reason is logged and kept on the webhook row.</summary>
    private string? Ignored(BillingEvent evt)
    {
        if (evt.IgnoredReason is { } reason)
        {
            logger.LogWarning("Billing event {EventId} ({EventType}) ignored: {Reason}", evt.EventId, evt.EventType, reason);
        }

        return evt.IgnoredReason;
    }

    // ------------------------------------------------------------------ subscriptions

    private async Task<(BillingEventOutcome, string?)> ApplySubscriptionAsync(BillingEvent evt, CancellationToken ct)
    {
        if (evt.SubscriptionId is null)
        {
            return (BillingEventOutcome.Unresolved, "Subscription event without a subscription id.");
        }

        Subscription? sub = await db.Subscriptions
            .FirstOrDefaultAsync(s => s.Provider == evt.Provider && s.ProviderSubscriptionId == evt.SubscriptionId, ct).ConfigureAwait(false);

        if (sub is not null && sub.LastEventAt is { } last && evt.OccurredAt <= last)
        {
            return (BillingEventOutcome.Stale, $"Stale: event at {evt.OccurredAt:O} is not newer than the applied {last:O}.");
        }

        Guid? orgId = sub?.OrganizationId ?? evt.OrganizationId ?? await OrganizationByCustomerAsync(evt, ct).ConfigureAwait(false);
        if (orgId is null)
        {
            logger.LogWarning("Billing event {EventId} names no known organization (subscription {Sub}).", evt.EventId, evt.SubscriptionId);
            return (BillingEventOutcome.Unresolved, "No organization could be resolved for this subscription.");
        }

        if (!await db.Organizations.AnyAsync(o => o.Id == orgId, ct).ConfigureAwait(false))
        {
            return (BillingEventOutcome.Unresolved, "The organization no longer exists.");
        }

        (Plan? plan, int seats) = await ResolvePlanAsync(evt, ct).ConfigureAwait(false);
        if (plan is null && sub is null)
        {
            return (BillingEventOutcome.Unresolved, "No plan carries any of this subscription's provider price ids.");
        }

        DateTime now = _time.GetUtcNow().UtcDateTime;
        bool isNew = sub is null;
        if (sub is null)
        {
            sub = new Subscription
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId.Value,
                PlanId = plan!.Id,
                Provider = evt.Provider,
                ProviderSubscriptionId = evt.SubscriptionId,
                CreatedAt = now
            };
            db.Subscriptions.Add(sub);

            // A new paid subscription replaces whatever the org had (the internal Trial row, an old canceled-and-resubscribed one).
            List<Subscription> others = await db.Subscriptions
                .Where(s => s.OrganizationId == orgId && s.Id != sub.Id && s.Status != SubscriptionStatus.Canceled)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (Subscription other in others)
            {
                other.Status = SubscriptionStatus.Canceled;
                other.UpdatedAt = now;
            }
        }

        Plan effectivePlan = plan ?? await db.Plans.AsNoTracking().FirstAsync(p => p.Id == sub.PlanId, ct).ConfigureAwait(false);
        Upgrade? upgrade = null;
        if (plan is not null)
        {
            int eventSeats = plan.Kind == PlanKind.PerSeat ? Math.Max(seats, plan.MinSeats ?? 1) : 1;
            (effectivePlan, upgrade) = await ApplyPlanChangeAsync(sub, isNew, plan, eventSeats, evt, ct).ConfigureAwait(false);
        }

        DateTime? previousPeriodStart = sub.PeriodStart;
        sub.Status = evt.Status;
        sub.PeriodStart = evt.PeriodStart ?? sub.PeriodStart;
        sub.PeriodEnd = evt.PeriodEnd ?? sub.PeriodEnd;
        OpenTerm(sub, previousPeriodStart);
        sub.CancelAtPeriodEnd = evt.CancelAtPeriodEnd;
        sub.CancelEffectiveAt = evt.CancelAtPeriodEnd ? evt.CancelEffectiveAt ?? sub.PeriodEnd : null;
        sub.LastEventAt = evt.OccurredAt;
        sub.UpdatedAt = now;

        await EnsureCustomerAsync(orgId.Value, evt.Provider, evt.CustomerId, now, ct).ConfigureAwait(false);

        // The period refill is keyed by (subscription, period start), so the created, updated and
        // renewal events that all describe the same period grant exactly once between them. It is the
        // entitlement the period started with (R-3): when this event is itself an upgrade, or an upgrade
        // was already recorded in a period not refilled yet, that is the plan before the upgrade, and the
        // refill lands before the upgrade's grant so the upgrade is measured from it.
        if (evt.Status is SubscriptionStatus.Active or SubscriptionStatus.Trialing && sub.PeriodStart is { } start)
        {
            long amount = await PeriodBaseMillisAsync(evt.Provider, evt.SubscriptionId, start, upgrade?.OldEntitlementMillis ?? EntitlementMillis(effectivePlan, sub.Seats), ct).ConfigureAwait(false);
            // An annual term refills monthly: month 0 lives one month (not the whole year) and, right after a
            // monthly-to-annual switch, is reduced by the refill that month already received.
            DateTime? refillExpiresAt = sub.PeriodEnd;
            if (sub.PeriodEnd is { } termEnd && SubscriptionInterval.Of(start, termEnd) == BillingInterval.Annual)
            {
                refillExpiresAt = AnnualCreditSchedule.MonthWindow(start, termEnd, start).End;
                amount = await ReduceBySwitchRefillAsync(evt.Provider, evt.SubscriptionId, start, amount, ct).ConfigureAwait(false);
            }

            if (amount > 0)
            {
                CreditLedgerResult grant = await new CreditLedger(db, _time).GrantAsync(
                    orgId.Value,
                    CreditGrantSource.Plan,
                    amount,
                    refillExpiresAt,
                    PlanRefillKey(evt.Provider, evt.SubscriptionId, start),
                    context: new CreditLedgerContext(Note: $"{effectivePlan.Key} refill"),
                    ct: ct).ConfigureAwait(false);
                if (!grant.Ok)
                {
                    throw new InvalidOperationException($"Plan refill refused: {grant.Error}");
                }
            }
        }

        // G-1: a waiting upgrade whose period is over (the subscription moved on) or whose subscription is
        // canceled can no longer be granted: closed with 0 now, so no later event naming its period grants it.
        await CloseLapsedWaitingUpgradesAsync(evt.Provider, evt.SubscriptionId, sub.Status, sub.PeriodStart, ct).ConfigureAwait(false);

        // F-3: an upgrade paid while the subscription was not Active (past due, paused) waits on its row;
        // back to Active in that same period, it is granted now — after the refill, before this event's
        // own upgrade, so the period cap sees the rows in the order they happened.
        if (sub.Status == SubscriptionStatus.Active && sub.PeriodStart is { } activePeriod)
        {
            await GrantWaitingUpgradesAsync(evt.Provider, evt.SubscriptionId, activePeriod, ct).ConfigureAwait(false);
        }

        if (upgrade is not null)
        {
            await RecordUpgradeAsync(sub, upgrade.OldEntitlementMillis, upgrade.NewEntitlementMillis, evt, ct).ConfigureAwait(false);
        }

        await AttributeUnattributedPaymentsAsync(sub, ct).ConfigureAwait(false);
        return (BillingEventOutcome.Applied, null);
    }

    /// <summary>
    /// Records the payment that opened the subscription's current annual term
    /// (<see cref="Subscription.TermOpeningTransactionId"/>), once per term, and ONLY on an explicit provider signal:
    /// a positive-amount invoice whose billing period (Stripe <c>subscription_create</c> / <c>subscription_cycle</c>)
    /// starts at the term start (Paddle <c>billing_period.starts_at</c>), within <see cref="PlainSignalTolerance"/> because the
    /// provider stamps the period a moment before the payment. An upgrade payment (<paramref name="exact"/>) pairs only when
    /// it is billed from exactly the term start, to the second. No time or amount guessing: without the signal the id
    /// stays null and <see cref="AnnualTermPayments"/> evaluates every payment of the term.
    /// </summary>
    private static readonly TimeSpan PlainSignalTolerance = TimeSpan.FromMinutes(5);

    private static void PairTermPayment(Subscription sub, string? transactionId, long amountMinor, DateTime? paymentPeriodStart, bool exact = false)
    {
        if (transactionId is null || amountMinor <= 0 || paymentPeriodStart is not { } period || sub.TermOpeningTransactionId is not null
            || sub is not { PeriodStart: { } start, PeriodEnd: { } end } || SubscriptionInterval.Of(start, end) != BillingInterval.Annual)
        {
            return;
        }

        if ((period - start).Duration() <= (exact ? TimeSpan.Zero : PlainSignalTolerance))
        {
            sub.TermOpeningTransactionId = transactionId;
        }
    }

    /// <summary>A new term just opened (the period start moved): forget the previous term's opening payment.</summary>
    private static void OpenTerm(Subscription sub, DateTime? previousStart)
    {
        if (sub.PeriodStart != previousStart)
        {
            sub.TermOpeningTransactionId = null;
        }
    }

    /// <summary>
    /// Stripe sends <c>invoice.paid</c> and <c>customer.subscription.created</c> in either order. A payment that
    /// arrived first and could not name its organization (no metadata, no customer row yet) is recorded
    /// unattributed; once the subscription row names the organization, the payment is attributed to it and,
    /// when it moved money, the first-payment funnel row it owed is queued (the claim is once per organization).
    /// </summary>
    private async Task AttributeUnattributedPaymentsAsync(Subscription sub, CancellationToken ct)
    {
        if (sub.Provider is null || sub.ProviderSubscriptionId is null)
        {
            return;
        }

        List<BillingPayment> orphans = await db.BillingPayments
            .Where(p => p.Provider == sub.Provider && p.ProviderSubscriptionId == sub.ProviderSubscriptionId && p.OrganizationId == null)
            .OrderBy(p => p.OccurredAt)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (BillingPayment payment in orphans)
        {
            payment.OrganizationId = sub.OrganizationId;
        }

        if (orphans.FirstOrDefault(p => p.AmountMinor > 0) is { } first && _pendingFirstPayment is null)
        {
            _pendingFirstPayment = (sub.OrganizationId, first.ProviderTransactionId);
        }
    }

    /// <summary>The entitlement before and after an upgrade this event applied; recorded once the period refill is in.</summary>
    private sealed record Upgrade(long OldEntitlementMillis, long NewEntitlementMillis);

    /// <summary>
    /// What a period's refill grants: the entitlement before the period's first recorded upgrade (an upgrade
    /// that landed while the period had no refill yet, e.g. past due), else <paramref name="fallbackMillis"/>.
    /// </summary>
    private async Task<long> PeriodBaseMillisAsync(string provider, string subscriptionId, DateTime periodStart, long fallbackMillis, CancellationToken ct)
    {
        long? first = await db.BillingPendingUpgradeGrants.AsNoTracking()
            .Where(r => r.Provider == provider && r.ProviderSubscriptionId == subscriptionId && r.PeriodStart == periodStart && r.NewEntitlementMillis != null)
            .OrderBy(r => r.EffectiveAt).ThenBy(r => r.CreatedAt)
            .Select(r => r.OldEntitlementMillis).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return first ?? fallbackMillis;
    }

    /// <summary>
    /// A monthly-to-annual switch opens a new (annual) period right away. The month the subscriber was
    /// already paying for got its plan refill, so the annual term's first refill is reduced by the plain
    /// (monthly) refills granted for this subscription in the month before the term start: never granted
    /// twice, never negative. Renewals of an annual term find no such refill (the previous term's grants are a
    /// year old or month-keyed), so they are untouched.
    /// </summary>
    private async Task<long> ReduceBySwitchRefillAsync(string provider, string subscriptionId, DateTime termStart, long amount, CancellationToken ct)
    {
        string prefix = $"plan:{provider}:{subscriptionId}:";
        var refills = await db.CreditLedgerEntries.AsNoTracking()
            .Where(e => e.IdempotencyKey.StartsWith(prefix) && e.BalanceDeltaMillis > 0)
            .Select(e => new { e.IdempotencyKey, e.BalanceDeltaMillis })
            .ToListAsync(ct).ConfigureAwait(false);
        DateTime windowStart = termStart.AddMonths(-1);
        long already = refills
            .Select(e => (Start: AnnualCreditSchedule.RefillKeyPeriodStart(e.IdempotencyKey, provider, subscriptionId), Delta: e.BalanceDeltaMillis))
            .Where(e => e.Start is { } start && start >= windowStart && start < termStart)
            .Sum(e => e.Delta);
        return Math.Max(0, amount - already);
    }

    /// <summary>
    /// Applies the event's plan/seats to the row (plan decisions 2–4) and answers the plan whose
    /// entitlements the row now carries.
    /// <list type="bullet">
    /// <item>New row, or a new period (or no period to compare): the event's plan applies and any held
    /// downgrade is cleared — on a renewal the provider's items already are the downgraded ones. A new
    /// period whose first event is already an upgrade over the row (its renewal webhook lagged, R-3) is an
    /// upgrade of that period: the refill is the row's plan and the difference waits for its payment.</item>
    /// <item>Same period, back on the row's own plan/seats: "keep my plan", the held downgrade is cleared.</item>
    /// <item>Same period, an upgrade (price × seats not lower): applies now and answers the
    /// <see cref="Upgrade"/> the caller records after the period refill. The credit difference is granted
    /// only when the proration transaction is paid (<see cref="ApplyUpgradePaidAsync"/>); a payment that
    /// already arrived is matched at once.</item>
    /// <item>Same period, a downgrade: the row keeps what was paid for and holds the lower plan in
    /// <c>Scheduled*</c> until the next period.</item>
    /// </list>
    /// Duplicates and out-of-order deliveries never reach here twice: the caller already dropped events
    /// not newer than <see cref="Subscription.LastEventAt"/>, and the grant key is idempotent.
    /// </summary>
    private async Task<(Plan Effective, Upgrade? Upgrade)> ApplyPlanChangeAsync(Subscription sub, bool isNew, Plan plan, int seats, BillingEvent evt, CancellationToken ct)
    {
        bool samePeriod = !isNew && sub.PeriodStart is { } rowStart && evt.PeriodStart is { } evtStart && rowStart == evtStart;
        bool newPeriod = !isNew && sub.PeriodStart is not null && evt.PeriodStart is not null && !samePeriod;
        if (plan.Id == sub.PlanId && seats == sub.Seats)
        {
            ClearScheduled(sub);
            return (plan, null);
        }

        if (!samePeriod && !newPeriod)
        {
            SetPlan(sub, plan, seats);
            return (plan, null);
        }

        Plan current = await db.Plans.AsNoTracking().FirstAsync(p => p.Id == sub.PlanId, ct).ConfigureAwait(false);
        if (IsDowngrade(current, sub.Seats, plan, seats, SubscriptionInterval.Of(sub.PeriodStart, sub.PeriodEnd), SubscriptionInterval.Of(evt.PeriodStart, evt.PeriodEnd)))
        {
            if (newPeriod)
            {
                SetPlan(sub, plan, seats);
                return (plan, null);
            }

            sub.ScheduledPlanId = plan.Id;
            sub.ScheduledSeats = seats;
            sub.ScheduledChangeAt = evt.PeriodEnd ?? sub.PeriodEnd;
            return (current, null);
        }

        long oldEntitlement = EntitlementMillis(current, sub.Seats);
        long newEntitlement = EntitlementMillis(plan, seats);
        SetPlan(sub, plan, seats);
        if (newEntitlement > oldEntitlement && evt.PeriodEnd is null)
        {
            logger.LogWarning(
                "Billing event {EventId} upgrades subscription {Sub} without a period end; no upgrade credits will be granted.",
                evt.EventId, evt.SubscriptionId);
        }

        return (plan, new Upgrade(oldEntitlement, newEntitlement));
    }

    // ------------------------------------------------------------------ paid upgrades (F-1)

    /// <summary>
    /// Records the subscription half of an upgrade, with its target items (<see cref="ItemsKey"/>). Every
    /// upgrade by price is recorded, even one that lowers the credits, so its own proration payment matches it
    /// and not a later upgrade. R-2: a payment already here pairs only if it charged EXACTLY these items; time
    /// (inside this period and within <see cref="UpgradePairingTolerance"/>) only narrows those exact matches
    /// and never pairs on its own. It must be unambiguous both ways: exactly one such payment, and no other
    /// unpaid upgrade to the same items within the tolerance of that payment. Several left: nothing pairs and a
    /// warning names every transaction and row for manual review. Payments in the window whose items differ:
    /// nothing pairs, with a warning too. Either way the upgrade waits unpaid (an under-grant, never a payment
    /// taken from a neighbouring upgrade).
    /// </summary>
    private async Task RecordUpgradeAsync(Subscription sub, long oldEntitlement, long newEntitlement, BillingEvent evt, CancellationToken ct)
    {
        string? target = ItemsKey(evt.Items);
        List<BillingPendingUpgradeGrant> inWindow = (await PaidUnmatchedAsync(evt.Provider, evt.SubscriptionId!, ct).ConfigureAwait(false))
            .Where(r => Within(PaidInstant(r), evt.PeriodStart, evt.PeriodEnd) && Correlates(PaidInstant(r), evt.ChangedAt ?? evt.OccurredAt))
            .ToList();
        List<BillingPendingUpgradeGrant> payments = inWindow.Where(r => SameItems(target, r.ChargedItems)).ToList();
        List<BillingPendingUpgradeGrant> rivals = payments.Count == 1
            ? (await UnpaidUpgradesAsync(evt.Provider, evt.SubscriptionId!, ct).ConfigureAwait(false))
                .Where(r => IsCandidate(PaidInstant(payments[0]), r) && SameItems(r.TargetItems, payments[0].ChargedItems)).ToList()
            : [];
        BillingPendingUpgradeGrant? match = payments.Count == 1 && rivals.Count == 0 ? payments[0] : null;
        // N-1: the one payment was refunded, so this upgrade is retired on its own row, not paired.
        BillingPendingUpgradeGrant? refunded = match?.ClawedBackAt is not null ? match : null;
        BillingPendingUpgradeGrant? row = refunded is null ? match : null;
        if (row is null)
        {
            row = new BillingPendingUpgradeGrant
            {
                Id = Guid.NewGuid(),
                OrganizationId = sub.OrganizationId,
                Provider = evt.Provider,
                ProviderSubscriptionId = evt.SubscriptionId!,
                CreatedAt = _time.GetUtcNow().UtcDateTime
            };
            db.BillingPendingUpgradeGrants.Add(row);
            if (payments.Count > 1 || rivals.Count > 0)
            {
                logger.LogWarning(
                    "Upgrade {EventId} of subscription {Sub} (row {Row}, items {Items}) was not paired: payments {Transactions} and unpaid upgrade rows {Rows} all charge or target exactly these items within the pairing tolerance. No credits granted; review manually.",
                    evt.EventId, evt.SubscriptionId, row.Id, target,
                    string.Join(", ", payments.Select(p => $"{p.TransactionId} (row {p.Id})")),
                    string.Join(", ", rivals.Select(r => r.Id).Prepend(row.Id)));
            }
            else if (payments.Count == 0 && inWindow.Count > 0)
            {
                logger.LogWarning(
                    "Upgrade {EventId} of subscription {Sub} (row {Row}, items {Items}) was not paired: payments {Transactions} lie within the pairing tolerance but none charged exactly these items. No credits granted; review manually.",
                    evt.EventId, evt.SubscriptionId, row.Id, target ?? "(none)",
                    string.Join(", ", inWindow.Select(p => $"{p.TransactionId} (row {p.Id}, items {p.ChargedItems ?? "(none)"})")));
            }
        }

        row.TargetItems = target;
        row.OrganizationId = sub.OrganizationId;
        row.PeriodStart = evt.PeriodStart;
        row.PeriodEnd = evt.PeriodEnd;
        row.OldEntitlementMillis = oldEntitlement;
        row.NewEntitlementMillis = newEntitlement;
        // DEC-5: the time the change really happened (a late-processed Stripe event keeps its webhook's created time).
        row.EffectiveAt = evt.ChangedAt ?? evt.OccurredAt;
        if (refunded is not null)
        {
            Retire(row, refunded);
        }
        else if (row.TransactionId is not null)
        {
            await SettlePaidUpgradeAsync(row, sub.Status, sub.PeriodStart, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// N-1: an upgrade whose proration payment was refunded is closed as paid-and-clawed-back with 0
    /// granted, so it can never absorb a neighbouring payment; its entitlement still counts as reached for
    /// the period's cap (as for a payment refunded after its grant). The refunded payment is marked matched
    /// (it takes the upgrade's period), so it no longer competes for later upgrades. N-5: every retire logs a
    /// warning naming the row and the transaction, so an under-grant (a refund retiring the wrong upgrade) is
    /// reviewable.
    /// </summary>
    private void Retire(BillingPendingUpgradeGrant upgrade, BillingPendingUpgradeGrant refundedPayment)
    {
        logger.LogWarning(
            "Upgrade row {Row} of subscription {Sub} (items {Items}) is retired with 0 granted: its proration payment {Transaction} (row {PaymentRow}) was refunded. Review manually if that payment belonged to another upgrade.",
            upgrade.Id, upgrade.ProviderSubscriptionId, upgrade.TargetItems, refundedPayment.TransactionId, refundedPayment.Id);
        DateTime now = _time.GetUtcNow().UtcDateTime;
        upgrade.GrantedMillis = 0;
        upgrade.GrantedAt = now;
        upgrade.ClawedBackMillis = 0;
        upgrade.ClawedBackAt = now;
        refundedPayment.PeriodStart = upgrade.PeriodStart;
        refundedPayment.PeriodEnd = upgrade.PeriodEnd;
    }

    /// <summary>
    /// N-1: the late <c>transaction.completed</c> of a payment refunded before it arrived (a tombstone). The
    /// tombstone learns the payment's instant and charged items; when exactly one unpaid upgrade to exactly
    /// those items lies within the tolerance of it (and that upgrade has no other unmatched payment for the
    /// same items), that upgrade is retired (R-2). With none, the tombstone waits as a refunded payment for its
    /// upgrade event; with several, a warning asks for review.
    /// </summary>
    private async Task RetireRefundedUpgradeAsync(BillingPendingUpgradeGrant tombstone, BillingEvent evt, CancellationToken ct)
    {
        tombstone.PaidAt = evt.OccurredAt;
        tombstone.ProrationStartsAt = evt.ProrationStartsAt;
        tombstone.ChargedItems = ItemsKey(evt.Items);
        if (tombstone.ProviderSubscriptionId.Length == 0)
        {
            tombstone.ProviderSubscriptionId = evt.SubscriptionId!;
        }

        DateTime instant = PaidInstant(tombstone);
        List<BillingPendingUpgradeGrant> upgrades = (await UnpaidUpgradesAsync(evt.Provider, evt.SubscriptionId!, ct).ConfigureAwait(false))
            .Where(r => IsCandidate(instant, r) && SameItems(r.TargetItems, tombstone.ChargedItems))
            .ToList();
        List<BillingPendingUpgradeGrant> rivals = upgrades.Count == 1
            ? (await PaidUnmatchedAsync(evt.Provider, evt.SubscriptionId!, ct).ConfigureAwait(false))
                .Where(p => p.Id != tombstone.Id && IsCandidate(PaidInstant(p), upgrades[0]) && SameItems(upgrades[0].TargetItems, p.ChargedItems)).ToList()
            : [];
        if (upgrades.Count == 1 && rivals.Count == 0)
        {
            Retire(upgrades[0], tombstone);
        }
        else if (upgrades.Count > 0)
        {
            logger.LogWarning(
                "Refunded upgrade payment {Transaction} of subscription {Sub} arrived late; unpaid upgrade rows {Rows} and payments {Transactions} all lie within the pairing tolerance, so none was retired. Review manually.",
                evt.TransactionId, evt.SubscriptionId,
                string.Join(", ", upgrades.Select(r => r.Id)),
                string.Join(", ", rivals.Select(p => $"{p.TransactionId} (row {p.Id})").Prepend(evt.TransactionId)));
        }
    }

    /// <summary>
    /// The payment half. R-2: the one unpaid upgrade of the subscription whose target items equal EXACTLY the
    /// items this transaction charged (<see cref="BillingEvent.Items"/>), narrowed by time (its period contains
    /// the proration instant, its own instant within <see cref="UpgradePairingTolerance"/>), takes this
    /// transaction and is settled through the F-3 gates (<see cref="SettlePaidUpgradeAsync"/>: amount &gt; 0
    /// and the subscription Active in that period). Several exact matches, or a rival payment for the same
    /// items: nothing pairs, with a warning. No exact match: the payment is remembered (paid, unmatched) for
    /// the upgrade event still on its way, and never pays for a stranger's upgrade; a warning when it cannot
    /// pair at all (no items) or when upgrades in the window target other items. Never touches the
    /// subscription row or its <see cref="Subscription.LastEventAt"/> — this is not a subscription event.
    /// </summary>
    private async Task<(BillingEventOutcome, string?)> ApplyUpgradePaidAsync(BillingEvent evt, CancellationToken ct)
    {
        if (evt.SubscriptionId is null || evt.TransactionId is null)
        {
            return (BillingEventOutcome.Unresolved, "Upgrade payment without a subscription or transaction id.");
        }

        BillingPendingUpgradeGrant? seen = await db.BillingPendingUpgradeGrants
            .FirstOrDefaultAsync(r => r.Provider == evt.Provider && r.TransactionId == evt.TransactionId, ct).ConfigureAwait(false);
        if (seen is not null)
        {
            if (seen.ClawedBackAt is null || seen.GrantedAt is not null)
            {
                return (BillingEventOutcome.Applied, "Upgrade payment already recorded; nothing changed.");
            }

            // A tombstone without PaidAt: this is its payment's first arrival (later ones change nothing).
            if (seen.PaidAt is null)
            {
                await RetireRefundedUpgradeAsync(seen, evt, ct).ConfigureAwait(false);
            }

            return (BillingEventOutcome.Applied, "Upgrade payment was refunded before it arrived; nothing granted.");
        }

        Subscription? sub = await db.Subscriptions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Provider == evt.Provider && s.ProviderSubscriptionId == evt.SubscriptionId, ct).ConfigureAwait(false);
        Guid? orgId = sub?.OrganizationId ?? evt.OrganizationId ?? await OrganizationByCustomerAsync(evt, ct).ConfigureAwait(false);
        if (orgId is null || !await db.Organizations.AnyAsync(o => o.Id == orgId, ct).ConfigureAwait(false))
        {
            logger.LogWarning("Upgrade payment {Transaction} for subscription {Sub} names no known organization.", evt.TransactionId, evt.SubscriptionId);
            return (BillingEventOutcome.Unresolved, "No organization could be resolved for this upgrade payment.");
        }

        DateTime instant = evt.ProrationStartsAt ?? evt.OccurredAt;
        string? charged = ItemsKey(evt.Items);
        List<BillingPendingUpgradeGrant> inWindow = (await UnpaidUpgradesAsync(evt.Provider, evt.SubscriptionId, ct).ConfigureAwait(false))
            .Where(r => IsCandidate(instant, r))
            .ToList();
        List<BillingPendingUpgradeGrant> upgrades = inWindow.Where(r => SameItems(r.TargetItems, charged)).ToList();
        // R-2: the one candidate upgrade must not have another unmatched payment for the same items within its tolerance either.
        List<BillingPendingUpgradeGrant> rivals = upgrades.Count == 1
            ? (await PaidUnmatchedAsync(evt.Provider, evt.SubscriptionId, ct).ConfigureAwait(false))
                .Where(p => IsCandidate(PaidInstant(p), upgrades[0]) && SameItems(upgrades[0].TargetItems, p.ChargedItems)).ToList()
            : [];
        BillingPendingUpgradeGrant? row = upgrades.Count == 1 && rivals.Count == 0 ? upgrades[0] : null;

        if (row is null)
        {
            BillingPendingUpgradeGrant payment = new()
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId.Value,
                Provider = evt.Provider,
                ProviderSubscriptionId = evt.SubscriptionId,
                TransactionId = evt.TransactionId,
                PaidAt = evt.OccurredAt,
                ProrationStartsAt = evt.ProrationStartsAt,
                ChargedItems = charged,
                CreatedAt = _time.GetUtcNow().UtcDateTime
            };
            if (evt.AmountMinor <= 0)
            {
                // F-3: a $0 (or unknown-amount) payment still pairs with its upgrade, but arrives closed with 0.
                CloseWithZero(payment);
            }

            db.BillingPendingUpgradeGrants.Add(payment);
            if (upgrades.Count == 0)
            {
                return (BillingEventOutcome.Applied, await UnmatchedPaymentNoteAsync(payment, inWindow, evt, ct).ConfigureAwait(false));
            }

            logger.LogWarning(
                "Upgrade payment {Transaction} of subscription {Sub} (row {Row}, items {Items}) was not paired: unpaid upgrade rows {Rows} and payments {Transactions} all target or charge exactly these items within the pairing tolerance. No credits granted; review manually.",
                evt.TransactionId, evt.SubscriptionId, payment.Id, charged,
                string.Join(", ", upgrades.Select(r => r.Id)),
                string.Join(", ", rivals.Select(p => $"{p.TransactionId} (row {p.Id})").Prepend(evt.TransactionId)));
            return (BillingEventOutcome.Applied, "Upgrade payment matches more than one upgrade or competes with another payment; nothing paired (review manually).");
        }

        row.TransactionId = evt.TransactionId;
        row.PaidAt = evt.OccurredAt;
        row.ProrationStartsAt = evt.ProrationStartsAt;
        row.ChargedItems = charged;
        if (sub is not null && evt.ProrationStartsAt is { } billedFrom)
        {
            // The interval switch (monthly to annual) bills from exactly the new term's start (no tolerance); a seat or
            // plan change inside a term is prorated from a later instant, so it never carries this signal.
            Subscription? termSub = await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == sub.Id, ct).ConfigureAwait(false);
            if (termSub is not null)
            {
                PairTermPayment(termSub, evt.TransactionId, evt.AmountMinor, billedFrom, exact: true);
            }
        }

        if (evt.AmountMinor <= 0)
        {
            CloseWithZero(row);
            return (BillingEventOutcome.Applied, "Upgrade payment charged nothing; its upgrade is closed with 0 granted.");
        }

        return (BillingEventOutcome.Applied, await SettlePaidUpgradeAsync(row, sub?.Status, sub?.PeriodStart, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// The note (and, when the payment cannot simply be waiting for its upgrade event, the warning) of a payment
    /// that matched no upgrade (R-2): it charged no usable items (it can never pair); upgrades in the window
    /// target other items; or its only exact match was already retired by a refunded payment (N-5).
    /// </summary>
    private async Task<string> UnmatchedPaymentNoteAsync(BillingPendingUpgradeGrant payment, List<BillingPendingUpgradeGrant> inWindow, BillingEvent evt, CancellationToken ct)
    {
        if (payment.ChargedItems is null)
        {
            logger.LogWarning(
                "Upgrade payment {Transaction} of subscription {Sub} (row {Row}) carries no positively charged items, so it can never pair with an upgrade. No credits granted; review manually.",
                evt.TransactionId, evt.SubscriptionId, payment.Id);
            return "Upgrade payment carries no charged items; it never pairs (review manually).";
        }

        DateTime instant = PaidInstant(payment);
        List<BillingPendingUpgradeGrant> retiredRows = await db.BillingPendingUpgradeGrants.AsNoTracking()
            .Where(r => r.Provider == evt.Provider && r.ProviderSubscriptionId == evt.SubscriptionId
                && r.TransactionId == null && r.NewEntitlementMillis != null && r.ClawedBackAt != null && r.TargetItems == payment.ChargedItems)
            .ToListAsync(ct).ConfigureAwait(false);
        List<Guid> retired = retiredRows.Where(r => IsCandidate(instant, r)).Select(r => r.Id).ToList();
        if (retired.Count > 0)
        {
            logger.LogWarning(
                "Upgrade payment {Transaction} of subscription {Sub} (row {Row}, items {Items}) matches only upgrade rows {Rows}, already retired by a refunded payment. Not paired, no credits granted; review manually (the refund may have retired this payment's upgrade).",
                evt.TransactionId, evt.SubscriptionId, payment.Id, payment.ChargedItems, string.Join(", ", retired));
            return "Upgrade payment matches only an upgrade already retired by a refunded payment; nothing paired (review manually).";
        }

        if (inWindow.Count > 0)
        {
            logger.LogWarning(
                "Upgrade payment {Transaction} of subscription {Sub} (row {Row}, items {Items}) was not paired: unpaid upgrade rows {Rows} lie within the pairing tolerance but none targets exactly these items. No credits granted; review manually.",
                evt.TransactionId, evt.SubscriptionId, payment.Id, payment.ChargedItems,
                string.Join(", ", inWindow.Select(r => $"{r.Id} (items {r.TargetItems ?? "(none)"})")));
            return "Upgrade payment matches no upgrade's items; nothing paired (review manually).";
        }

        return "Upgrade payment arrived before its upgrade; credits are granted when the upgrade event lands.";
    }

    /// <summary>
    /// F-3: the two gates of a paid-upgrade grant, applied once an upgrade and its payment (amount &gt; 0)
    /// are on one row. The subscription must be <see cref="SubscriptionStatus.Active"/> in the upgrade's own
    /// period at grant time: then the difference is granted. Past due or paused in that period, the row waits
    /// (paired, <see cref="BillingPendingUpgradeGrant.GrantedAt"/> null) and is granted by the subscription
    /// event that makes it Active again in the same period (<see cref="GrantWaitingUpgradesAsync"/>). Trialing
    /// (a trial's upgrade never grants: the trial ends with a new period), canceled, unknown, or already in a
    /// later period: closed with 0. Answers the webhook note.
    /// </summary>
    private async Task<string?> SettlePaidUpgradeAsync(BillingPendingUpgradeGrant row, SubscriptionStatus? status, DateTime? subscriptionPeriodStart, CancellationToken ct)
    {
        if (row.GrantedAt is not null)
        {
            return "Upgrade payment charged nothing; its upgrade is closed with 0 granted.";
        }

        bool samePeriod = subscriptionPeriodStart is not null && subscriptionPeriodStart == row.PeriodStart;
        if (samePeriod && status == SubscriptionStatus.Active)
        {
            await GrantPaidUpgradeAsync(row, ct).ConfigureAwait(false);
            return null;
        }

        if (samePeriod && status is SubscriptionStatus.PastDue or SubscriptionStatus.Paused)
        {
            return $"Upgrade paid while the subscription is {status}; granted if it is Active again in this period.";
        }

        CloseWithZero(row);
        return $"Upgrade paid while the subscription is {status?.ToString() ?? "unknown"} (or in another period); closed with 0 granted.";
    }

    /// <summary>
    /// Grants, oldest first, the upgrades of this period that were paid while the subscription was not
    /// Active (paired, not granted, not refunded). Each grant is saved before the next is measured, so the
    /// period's high-water cap sees it.
    /// </summary>
    private async Task GrantWaitingUpgradesAsync(string provider, string subscriptionId, DateTime periodStart, CancellationToken ct)
    {
        List<BillingPendingUpgradeGrant> waiting = await db.BillingPendingUpgradeGrants
            .Where(r => r.Provider == provider && r.ProviderSubscriptionId == subscriptionId && r.PeriodStart == periodStart
                && r.TransactionId != null && r.NewEntitlementMillis != null && r.GrantedAt == null && r.ClawedBackAt == null)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (BillingPendingUpgradeGrant row in waiting.OrderBy(r => r.EffectiveAt).ThenBy(r => r.CreatedAt))
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await GrantPaidUpgradeAsync(row, ct).ConfigureAwait(false);
        }

        if (waiting.Count > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// G-1: closes with 0 the waiting upgrades (paired, not granted, not refunded) of a canceled subscription,
    /// or of a period older than the one the subscription is in now, each with a warning for review.
    /// </summary>
    private async Task CloseLapsedWaitingUpgradesAsync(string provider, string subscriptionId, SubscriptionStatus status, DateTime? periodStart, CancellationToken ct)
    {
        bool canceled = status == SubscriptionStatus.Canceled;
        if (!canceled && periodStart is null)
        {
            return;
        }

        List<BillingPendingUpgradeGrant> lapsed = await db.BillingPendingUpgradeGrants
            .Where(r => r.Provider == provider && r.ProviderSubscriptionId == subscriptionId
                && r.TransactionId != null && r.NewEntitlementMillis != null && r.GrantedAt == null && r.ClawedBackAt == null
                && (canceled || r.PeriodStart < periodStart))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (BillingPendingUpgradeGrant row in lapsed)
        {
            CloseWithZero(row);
            logger.LogWarning(
                "Waiting upgrade row {Row} (payment {Transaction}) of subscription {Sub} is closed with 0 granted: the subscription is {Status} in period {Period}, so its period {RowPeriod} can no longer turn Active. Review manually.",
                row.Id, row.TransactionId, subscriptionId, status, periodStart, row.PeriodStart);
        }
    }

    /// <summary>
    /// F-3: closes a row with 0 granted (<see cref="BillingPendingUpgradeGrant.GrantedMillis"/> 0,
    /// <see cref="BillingPendingUpgradeGrant.GrantedAt"/> set). On a payment not matched yet (a $0 charge) it
    /// still pairs with its upgrade, which then arrives closed; a closed upgrade already holds its
    /// transaction, so it never takes another payment. Its entitlement counts as reached for the period cap,
    /// as for a retired upgrade.
    /// </summary>
    private void CloseWithZero(BillingPendingUpgradeGrant row)
    {
        row.GrantedMillis = 0;
        row.GrantedAt = _time.GetUtcNow().UtcDateTime;
    }

    /// <summary>
    /// Grants a paid upgrade: <c>ProratedUpgradeGrantMillis(highWater, new, period, instant)</c>, where
    /// the instant is <c>max(provider's proration start, upgrade's own time)</c> — never earlier than the
    /// upgrade itself, whatever the payment claims — and
    /// <c>highWater = max(entitlement the period started with, highest entitlement already granted for an
    /// upgrade this period)</c>. Upgrade/downgrade cycles inside one period therefore never add up to
    /// more than the highest plan's prorated difference. Plan credits, expiring with the period, keyed per
    /// transaction.
    /// </summary>
    private async Task GrantPaidUpgradeAsync(BillingPendingUpgradeGrant row, CancellationToken ct)
    {
        List<BillingPendingUpgradeGrant> samePeriod = await db.BillingPendingUpgradeGrants.AsNoTracking()
            .Where(r => r.Provider == row.Provider && r.ProviderSubscriptionId == row.ProviderSubscriptionId
                && r.PeriodStart == row.PeriodStart && r.Id != row.Id && r.NewEntitlementMillis != null)
            .ToListAsync(ct).ConfigureAwait(false);

        string? refillKey = row.PeriodStart is { } start ? PlanRefillKey(row.Provider, row.ProviderSubscriptionId, start) : null;
        long? refill = refillKey is null
            ? null
            : await db.CreditLedgerEntries.AsNoTracking()
                .Where(e => e.IdempotencyKey == refillKey)
                .Select(e => (long?)e.BalanceDeltaMillis).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        long periodBase = refill
            ?? samePeriod.Append(row).OrderBy(r => r.EffectiveAt).ThenBy(r => r.CreatedAt).First().OldEntitlementMillis!.Value;
        long highWater = samePeriod.Where(r => r.GrantedAt is not null).Select(r => r.NewEntitlementMillis!.Value).Append(periodBase).Max();

        DateTime effectiveAt = row.EffectiveAt!.Value;
        DateTime instant = row.ProrationStartsAt is { } prorated && prorated > effectiveAt ? prorated : effectiveAt;
        long amount = ProratedUpgradeGrantMillis(highWater, row.NewEntitlementMillis!.Value, row.PeriodStart, row.PeriodEnd, instant);
        row.GrantedMillis = amount;
        row.GrantedAt = _time.GetUtcNow().UtcDateTime;
        if (amount <= 0)
        {
            return;
        }

        CreditLedgerResult grant = await new CreditLedger(db, _time).GrantAsync(
            row.OrganizationId,
            CreditGrantSource.Plan,
            amount,
            row.PeriodStart is { } termStart && row.PeriodEnd is { } termEnd && SubscriptionInterval.Of(termStart, termEnd) == BillingInterval.Annual
                ? AnnualCreditSchedule.MonthWindow(termStart, termEnd, instant).End
                : row.PeriodEnd,
            PlanUpgradeKey(row.Provider, row.TransactionId!),
            context: new CreditLedgerContext(Note: "plan upgrade difference"),
            ct: ct).ConfigureAwait(false);
        if (!grant.Ok)
        {
            throw new InvalidOperationException($"Plan upgrade grant refused: {grant.Error}");
        }
    }

    private static DateTime PaidInstant(BillingPendingUpgradeGrant row) => row.ProrationStartsAt ?? row.PaidAt!.Value;

    /// <summary>
    /// Proration transactions of the subscription that arrived (PaidAt known) and are not matched to an
    /// upgrade. A refunded one is included (N-1): it never grants, but it retires the upgrade it pairs
    /// with and competes as a rival; once it has retired one it carries that period and drops out.
    /// </summary>
    private Task<List<BillingPendingUpgradeGrant>> PaidUnmatchedAsync(string provider, string subscriptionId, CancellationToken ct)
        => db.BillingPendingUpgradeGrants
            .Where(r => r.Provider == provider && r.ProviderSubscriptionId == subscriptionId
                && r.TransactionId != null && r.NewEntitlementMillis == null && r.PeriodStart == null
                && (r.ClawedBackAt == null || r.PaidAt != null))
            .ToListAsync(ct);

    /// <summary>Recorded upgrades of the subscription whose proration payment has not been matched yet (retired ones excluded).</summary>
    private Task<List<BillingPendingUpgradeGrant>> UnpaidUpgradesAsync(string provider, string subscriptionId, CancellationToken ct)
        => db.BillingPendingUpgradeGrants
            .Where(r => r.Provider == provider && r.ProviderSubscriptionId == subscriptionId
                && r.TransactionId == null && r.NewEntitlementMillis != null && r.ClawedBackAt == null)
            .ToListAsync(ct);

    /// <summary>True when a payment at <paramref name="paymentInstant"/> could be the proration of <paramref name="upgrade"/>: inside its period and within the tolerance.</summary>
    private static bool IsCandidate(DateTime paymentInstant, BillingPendingUpgradeGrant upgrade)
        => upgrade.PeriodStart is not null && upgrade.EffectiveAt is { } at
            && Within(paymentInstant, upgrade.PeriodStart, upgrade.PeriodEnd) && Correlates(paymentInstant, at);

    /// <summary>
    /// R-2: the canonical form of an item list, stored on both halves of an upgrade row: <c>priceId:quantity</c>
    /// per item, sorted ordinally, comma separated. Null for no items, which never pairs.
    /// </summary>
    public static string? ItemsKey(IReadOnlyList<BillingLineItem> items)
        => items.Count == 0
            ? null
            : string.Join(",", items.Select(i => $"{i.PriceId}:{i.Quantity.ToString(CultureInfo.InvariantCulture)}").Order(StringComparer.Ordinal));

    /// <summary>R-2: an upgrade's target and a payment's charged items pair only when both are known and exactly equal.</summary>
    private static bool SameItems(string? target, string? charged)
        => target is not null && charged is not null && string.Equals(target, charged, StringComparison.Ordinal);

    /// <summary>True when a payment instant and an upgrade instant are close enough to be the same upgrade.</summary>
    private static bool Correlates(DateTime paymentInstant, DateTime upgradeAt)
        => (paymentInstant - upgradeAt).Duration() <= UpgradePairingTolerance;

    private static bool Within(DateTime at, DateTime? start, DateTime? end)
        => (start is null || at >= start) && (end is null || at < end);

    private static void SetPlan(Subscription sub, Plan plan, int seats)
    {
        sub.PlanId = plan.Id;
        sub.Seats = seats;
        ClearScheduled(sub);
    }

    private static void ClearScheduled(Subscription sub)
    {
        sub.ScheduledPlanId = null;
        sub.ScheduledSeats = null;
        sub.ScheduledChangeAt = null;
    }

    private async Task<(Plan? Plan, int Seats)> ResolvePlanAsync(BillingEvent evt, CancellationToken ct)
    {
        if (evt.Items.Count == 0)
        {
            return (null, 1);
        }

        List<Plan> plans = await db.Plans.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return ResolveItems(evt.Items, plans, evt.Provider);
    }

    /// <summary>
    /// The plan and seat count a provider item list stands for: the first item whose price id a plan
    /// carries. Seats are 1 on a flat plan and at least the minimum on a per-seat plan. (null, 1) when no
    /// plan carries any of the price ids. The webhook path and the change endpoints' live check share it.
    /// </summary>
    public static (Plan? Plan, int Seats) ResolveItems(IReadOnlyList<BillingLineItem> items, IReadOnlyList<Plan> plans, string provider)
    {
        (Plan? plan, int seats, _) = ResolveItemsWithInterval(items, plans, provider);
        return (plan, seats);
    }

    /// <summary>
    /// <see cref="ResolveItems"/> plus the billing interval of the matched price id (the plan's
    /// <c>annual</c> id is Annual, any other id Monthly). The interval is null when nothing matched.
    /// </summary>
    public static (Plan? Plan, int Seats, BillingInterval? Interval) ResolveItemsWithInterval(IReadOnlyList<BillingLineItem> items, IReadOnlyList<Plan> plans, string provider)
    {
        foreach (BillingLineItem item in items)
        {
            foreach (Plan p in plans)
            {
                if (PriceIdsOf(p, provider).Contains(item.PriceId, StringComparer.Ordinal))
                {
                    BillingInterval interval = string.Equals(AnnualPriceIdOf(p, provider), item.PriceId, StringComparison.Ordinal)
                        ? BillingInterval.Annual
                        : BillingInterval.Monthly;
                    return (p, p.Kind == PlanKind.PerSeat ? Math.Max(Math.Max(1, item.Quantity), p.MinSeats ?? 1) : 1, interval);
                }
            }
        }

        return (null, 1, null);
    }

    /// <summary>
    /// The provider price id of (plan, interval). Monthly is the <c>monthly</c> id, falling back to the
    /// first non-annual id so a map spelled any other way keeps resolving as before; Annual is the
    /// <c>annual</c> id only (null when the plan has none for the provider).
    /// </summary>
    public static string? PriceIdFor(Plan plan, string provider, BillingInterval interval)
    {
        if (interval == BillingInterval.Annual)
        {
            return AnnualPriceIdOf(plan, provider);
        }

        return MonthlyPriceIdOf(plan, provider) ?? NamedPriceIds(plan, provider).Where(x => x.Key != "annual").Select(x => x.Value).FirstOrDefault();
    }

    /// <summary>The plan's <c>annual</c> provider price id, or null when it has none.</summary>
    public static string? AnnualPriceIdOf(Plan plan, string provider)
        => NamedPriceIds(plan, provider).Where(x => x.Key == "annual" && !string.IsNullOrEmpty(x.Value)).Select(x => x.Value).FirstOrDefault();

    private static List<KeyValuePair<string, string>> NamedPriceIds(Plan plan, string provider)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(plan.ProviderPriceIdsJson) ? "{}" : plan.ProviderPriceIdsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(provider, out JsonElement p)
                && p.ValueKind == JsonValueKind.Object)
            {
                return p.EnumerateObject().Where(x => x.Value.ValueKind == JsonValueKind.String)
                    .Select(x => new KeyValuePair<string, string>(x.Name, x.Value.GetString()!)).ToList();
            }
        }
        catch (JsonException)
        {
            // A malformed price map is "no price ids".
        }

        return [];
    }

    /// <summary>The provider price ids stored on a plan (<c>{ "paddle": { "monthly": "pri_…" } }</c>).</summary>
    public static IReadOnlyList<string> PriceIdsOf(Plan plan, string provider)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(plan.ProviderPriceIdsJson) ? "{}" : plan.ProviderPriceIdsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(provider, out JsonElement p)
                && p.ValueKind == JsonValueKind.Object)
            {
                return p.EnumerateObject().Where(x => x.Value.ValueKind == JsonValueKind.String).Select(x => x.Value.GetString()!).ToList();
            }
        }
        catch (JsonException)
        {
            // A malformed price map is "no price ids", never a crash of the webhook path.
        }

        return [];
    }

    /// <summary>The plan's <c>monthly</c> provider price id, or null when it has none.</summary>
    public static string? MonthlyPriceIdOf(Plan plan, string provider)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(plan.ProviderPriceIdsJson) ? "{}" : plan.ProviderPriceIdsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(provider, out JsonElement p)
                && p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty("monthly", out JsonElement monthly)
                && monthly.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(monthly.GetString())
                    ? monthly.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<Guid?> OrganizationByCustomerAsync(BillingEvent evt, CancellationToken ct)
    {
        if (evt.CustomerId is null)
        {
            return null;
        }

        return await db.BillingCustomers.AsNoTracking()
            .Where(c => c.Provider == evt.Provider && c.ProviderCustomerId == evt.CustomerId)
            .Select(c => (Guid?)c.OrganizationId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores the provider customer a checkout was created for (plan decision 5), so the portal works and
    /// a retried checkout reuses it before any webhook arrives. Insert-only, like the webhook path: an
    /// org that already has a customer at this provider, or a customer id already stored, is left alone,
    /// and losing a race to a concurrent insert is the same outcome.
    /// </summary>
    public async Task RememberCustomerAsync(Guid orgId, string provider, string customerId, CancellationToken ct = default)
    {
        BillingCustomer? added = await EnsureCustomerAsync(orgId, provider, customerId, _time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        if (added is null)
        {
            return;
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            db.Entry(added).State = EntityState.Detached;
            logger.LogInformation("Billing customer {Customer} for organization {Org} was stored concurrently.", customerId, orgId);
        }
    }

    private async Task<BillingCustomer?> EnsureCustomerAsync(Guid orgId, string provider, string? customerId, DateTime now, CancellationToken ct)
    {
        if (customerId is null)
        {
            return null;
        }

        bool known = await db.BillingCustomers.AnyAsync(c => c.OrganizationId == orgId && c.Provider == provider, ct).ConfigureAwait(false)
            || await db.BillingCustomers.AnyAsync(c => c.Provider == provider && c.ProviderCustomerId == customerId, ct).ConfigureAwait(false)
            || db.BillingCustomers.Local.Any(c => (c.OrganizationId == orgId && c.Provider == provider) || (c.Provider == provider && c.ProviderCustomerId == customerId));
        if (known)
        {
            return null;
        }

        BillingCustomer customer = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Provider = provider,
            ProviderCustomerId = customerId,
            CreatedAt = now
        };
        db.BillingCustomers.Add(customer);
        return customer;
    }

    // ------------------------------------------------------------------ top-ups and refunds

    private async Task<(BillingEventOutcome, string?)> ApplyTopUpAsync(BillingEvent evt, CancellationToken ct)
    {
        if (evt.TransactionId is null || evt.TopUpCredits is not > 0)
        {
            return (BillingEventOutcome.Unresolved, "Top-up payment without a transaction id or a recognised pack.");
        }

        // R-1: a refund (or chargeback) processed before this payment left a tombstone; it never grants.
        if (await db.BillingPendingUpgradeGrants.AnyAsync(r => r.Provider == evt.Provider && r.TransactionId == evt.TransactionId && r.ClawedBackAt != null, ct).ConfigureAwait(false))
        {
            return (BillingEventOutcome.Applied, "Top-up was refunded before its payment arrived; nothing granted.");
        }

        Guid? orgId = evt.OrganizationId ?? await OrganizationByCustomerAsync(evt, ct).ConfigureAwait(false);
        if (orgId is null || !await db.Organizations.AnyAsync(o => o.Id == orgId, ct).ConfigureAwait(false))
        {
            logger.LogWarning("Top-up {Transaction} paid but no organization could be resolved.", evt.TransactionId);
            return (BillingEventOutcome.Unresolved, "No organization could be resolved for this top-up.");
        }

        DateTime now = _time.GetUtcNow().UtcDateTime;
        CreditLedgerResult grant = await new CreditLedger(db, _time).GrantAsync(
            orgId.Value,
            CreditGrantSource.TopUp,
            evt.TopUpCredits.Value * 1000L,
            now.AddMonths(TopUpExpiryMonths),
            TopUpKey(evt.Provider, evt.TransactionId),
            context: new CreditLedgerContext(Note: $"top-up {evt.TopUpCredits} credits"),
            ct: ct).ConfigureAwait(false);
        if (!grant.Ok)
        {
            throw new InvalidOperationException($"Top-up grant refused: {grant.Error}");
        }

        PromotionCheckout? checkout = await CompleteCheckoutAsync(evt, now, ct).ConfigureAwait(false);
        Promotion? discount = await DiscountPromotionAsync(evt, ct).ConfigureAwait(false);
        // Both promotions' locks up front, in the one ordinal order every path uses (no lock cycles).
        await PromotionLocks.LockPromotionsAsync(db, [checkout?.BonusPromotionId, discount?.Id], ct).ConfigureAwait(false);
        await GrantTopUpBonusAsync(orgId.Value, evt, checkout, now, ct).ConfigureAwait(false);
        await RecordDiscountAsync(orgId.Value, evt, discount, checkout, now, ct).ConfigureAwait(false);
        await EnsureCustomerAsync(orgId.Value, evt.Provider, evt.CustomerId, now, ct).ConfigureAwait(false);
        return (BillingEventOutcome.Applied, null);
    }

    // ------------------------------------------------------------------ promotions

    /// <summary>
    /// The <see cref="PromotionCheckout"/> this server wrote for the paid transaction, marked completed so
    /// it stops reserving its promotions' caps. Null when the checkout carried no promotion.
    /// </summary>
    private async Task<PromotionCheckout?> CompleteCheckoutAsync(BillingEvent evt, DateTime now, CancellationToken ct)
    {
        // The reservation was stored under the checkout reference the provider returned at checkout: its
        // checkout id when it has one (a hosted-checkout session), else the transaction id.
        string? reservationId = evt.CheckoutId ?? evt.TransactionId;
        if (reservationId is null)
        {
            return null;
        }

        PromotionCheckout? checkout = await db.PromotionCheckouts
            .FirstOrDefaultAsync(c => c.ProviderTransactionId == reservationId, ct).ConfigureAwait(false);
        if (checkout is not null)
        {
            checkout.CompletedAt ??= now;
        }

        return checkout;
    }

    /// <summary>
    /// The pack bonus promised at checkout: Promo credits on top of the pack, keyed per transaction so a
    /// replay or a second event for the same payment grants once.
    /// <para>
    /// Granted ONLY from the <see cref="PromotionCheckout"/> this server stored when it created the
    /// transaction. The checkout's custom data (<c>bonus_promotion_id</c>) is client-visible and is not
    /// trusted at all: a payment without a server-recorded reservation grants nothing. The record must
    /// belong to the paying org, the purchased price must be the one checked out and the promotion must
    /// still exist as a pack bonus.
    /// </para>
    /// <para>
    /// Caps (contract C10, under the promotion's advisory lock): see <see cref="DecideUseAsync"/>. A use
    /// within every cap is granted; past a cap only a live reservation that was within every cap when it
    /// was created is honoured, as <see cref="PromotionRedemption.Overshoot"/>, once per user and org and
    /// within <see cref="OvershootBound"/>. Every refusal is a NotGranted row with its reason, shown in the
    /// admin redemption list, plus one Warning. A promotion paused, archived or ended after the checkout is
    /// still honoured: only its existence and caps are checked here, not its status or window.
    /// </para>
    /// </summary>
    private async Task GrantTopUpBonusAsync(Guid orgId, BillingEvent evt, PromotionCheckout? checkout, DateTime now, CancellationToken ct)
    {
        if (evt.TransactionId is null || evt.TopUpCredits is not > 0)
        {
            return;
        }

        if (checkout?.BonusPromotionId is not { } bonusId)
        {
            if (evt.BonusPromotionId is { } claimed)
            {
                logger.LogWarning(
                    "Top-up {Transaction} names bonus promotion {Promotion} in its custom data, but this server attached no bonus to it; no bonus granted.",
                    evt.TransactionId, claimed);
            }

            return;
        }

        Promotion? bonus = await db.Promotions.AsNoTracking().FirstOrDefaultAsync(p => p.Id == bonusId, ct).ConfigureAwait(false);
        if (bonus is not { Kind: PromotionKind.PackBonus, BonusPercent: > 0 })
        {
            logger.LogWarning("Top-up {Transaction} was checked out with bonus promotion {Promotion}, which is no longer a pack bonus; no bonus granted.", evt.TransactionId, bonusId);
            return;
        }

        long amount = evt.TopUpCredits.Value * 1000L * bonus.BonusPercent.Value / 100;
        if (amount <= 0)
        {
            return;
        }

        // Everything below runs under the promotion's lock (taken by the caller, re-entrant here), on
        // rows read after the lock: concurrent webhooks for the last use see each other's grants.
        await PromotionLocks.LockAsync(db, PromotionLocks.PromotionKey(bonus.Id), ct).ConfigureAwait(false);
        if (await RedemptionExistsAsync(bonus.Id, evt.TransactionId, ct).ConfigureAwait(false))
        {
            return; // already granted (or refused) by an earlier event for this payment
        }

        if (checkout.OrganizationId != orgId || !evt.Items.Any(i => string.Equals(i.PriceId, checkout.PriceId, StringComparison.Ordinal)))
        {
            // Paid for another org, or another price, than the one checked out.
            await RecordNotGrantedAsync(bonus.Id, orgId, checkout.UserId, PromotionRedemptionKind.BonusGranted, evt, PromotionNotGrantedReason.CheckoutMismatch, amount, now, ct).ConfigureAwait(false);
            return;
        }

        UseDecision decision = await DecideUseAsync(bonus.Id, orgId, checkout.UserId, PromotionRedemptionKind.BonusGranted, checkout.Id, PaidAt(evt, now), ct).ConfigureAwait(false);
        if (decision.Reason is { } refused)
        {
            await RecordNotGrantedAsync(bonus.Id, orgId, checkout.UserId, PromotionRedemptionKind.BonusGranted, evt, refused, amount, now, ct).ConfigureAwait(false);
            return;
        }

        if (decision.Overshoot)
        {
            logger.LogWarning(
                "Top-up {Transaction}: bonus promotion {Promotion} is at a cap, but this checkout's live reservation was within every cap; granted and recorded as overshoot.",
                evt.TransactionId, bonus.Id);
        }

        await CountRedemptionAsync(bonus.Id, now, ct).ConfigureAwait(false);
        CreditLedgerResult grant = await new CreditLedger(db, _time).GrantAsync(
            orgId,
            CreditGrantSource.Promo,
            amount,
            now.AddMonths(TopUpExpiryMonths),
            TopUpBonusKey(evt.Provider, evt.TransactionId),
            context: new CreditLedgerContext(Note: $"top-up bonus {bonus.BonusPercent}%"),
            ct: ct).ConfigureAwait(false);
        if (!grant.Ok)
        {
            throw new InvalidOperationException($"Top-up bonus refused: {grant.Error}");
        }

        db.PromotionRedemptions.Add(new PromotionRedemption
        {
            Id = Guid.NewGuid(),
            PromotionId = bonus.Id,
            OrganizationId = orgId,
            UserId = checkout.UserId,
            Kind = PromotionRedemptionKind.BonusGranted,
            ProviderTransactionId = evt.TransactionId,
            LedgerEntryId = grant.Entry?.Id,
            CreditsMillis = amount,
            Overshoot = decision.Overshoot,
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await SupersedeSiblingsAsync(checkout, now, ct).ConfigureAwait(false);
    }

    /// <summary>When the payment happened: the provider's event time, never later than now.</summary>
    private static DateTime PaidAt(BillingEvent evt, DateTime now)
    {
        DateTime occurred = evt.OccurredAt.Kind switch
        {
            DateTimeKind.Unspecified => DateTime.SpecifyKind(evt.OccurredAt, DateTimeKind.Utc),
            DateTimeKind.Local => evt.OccurredAt.ToUniversalTime(),
            _ => evt.OccurredAt
        };
        return occurred != default && occurred < now ? occurred : now;
    }

    /// <summary>What one paid use of a promotion gets: granted (maybe as an overshoot), or refused with a reason.</summary>
    private readonly record struct UseDecision(bool Overshoot, PromotionNotGrantedReason? Reason);

    /// <summary>
    /// The C10 rule for one paid use, under the promotion's lock:
    /// <list type="number">
    /// <item>within every cap (global, per org, per user; granted uses only): granted;</item>
    /// <item>otherwise refused unless a reservation of this server backs the payment
    /// (<see cref="PromotionNotGrantedReason.NotReserved"/>) that is neither superseded
    /// (<see cref="PromotionNotGrantedReason.Superseded"/>) nor expired at payment time
    /// (<see cref="PromotionNotGrantedReason.Expired"/>) and was within every cap when created
    /// (<see cref="PromotionNotGrantedReason.ReservedOverCap"/>);</item>
    /// <item>and unless the user (<see cref="PromotionNotGrantedReason.UserCapReached"/>) or org
    /// (<see cref="PromotionNotGrantedReason.OrgCapReached"/>) already had its one overshoot of this
    /// promotion, or the promotion's <see cref="OvershootBound"/> is used up
    /// (<see cref="PromotionNotGrantedReason.OvershootBoundReached"/>);</item>
    /// <item>otherwise granted as an overshoot.</item>
    /// </list>
    /// </summary>
    private async Task<UseDecision> DecideUseAsync(
        Guid promotionId, Guid orgId, Guid? userId, PromotionRedemptionKind kind, Guid? checkoutId, DateTime paidAt, CancellationToken ct)
    {
        if (await WithinCapsAsync(promotionId, orgId, userId, kind, ct).ConfigureAwait(false))
        {
            return new UseDecision(false, null);
        }

        if (checkoutId is not { } id)
        {
            return new UseDecision(false, PromotionNotGrantedReason.NotReserved);
        }

        // Re-read under the lock: a concurrent payment of a sibling may have superseded it just now.
        var reservation = await db.PromotionCheckouts.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { c.SupersededAt, c.CreatedAt, c.WithinCapsAtReservation })
            .FirstAsync(ct).ConfigureAwait(false);
        if (reservation.SupersededAt is not null)
        {
            return new UseDecision(false, PromotionNotGrantedReason.Superseded);
        }

        if (reservation.CreatedAt <= paidAt - _reservationLifetime)
        {
            return new UseDecision(false, PromotionNotGrantedReason.Expired);
        }

        if (!reservation.WithinCapsAtReservation)
        {
            return new UseDecision(false, PromotionNotGrantedReason.ReservedOverCap);
        }

        IQueryable<PromotionRedemption> overshoots = db.PromotionRedemptions
            .Where(r => r.PromotionId == promotionId && r.Kind == kind && r.Outcome == PromotionRedemptionOutcome.Granted && r.Overshoot);
        if (userId is { } user && await overshoots.AnyAsync(r => r.UserId == user, ct).ConfigureAwait(false))
        {
            return new UseDecision(false, PromotionNotGrantedReason.UserCapReached);
        }

        if (await overshoots.AnyAsync(r => r.OrganizationId == orgId, ct).ConfigureAwait(false))
        {
            return new UseDecision(false, PromotionNotGrantedReason.OrgCapReached);
        }

        var promotion = await db.Promotions.AsNoTracking().Where(p => p.Id == promotionId).Select(p => new { p.MaxRedemptions, p.Kind }).FirstAsync(ct).ConfigureAwait(false);
        if (await overshoots.CountAsync(ct).ConfigureAwait(false) >= OvershootBound(promotion.MaxRedemptions))
        {
            // The NotGranted row's Warning (EventId 41701) still follows; the counter is what an alert watches.
            OvershootBoundHit.Add(1,
                new KeyValuePair<string, object?>("kind", promotion.Kind.ToString()),
                new KeyValuePair<string, object?>("promotion_id", promotionId.ToString("D")));
            return new UseDecision(false, PromotionNotGrantedReason.OvershootBoundReached);
        }

        return new UseDecision(true, null);
    }

    /// <summary>
    /// Records a refused grant or count (contract C10, N-11): a NotGranted row with its reason, which
    /// never counts against a cap and is shown in the admin redemption list so support can make the
    /// buyer whole, plus one Warning (ids and the reason only, no personal data).
    /// </summary>
    private async Task RecordNotGrantedAsync(
        Guid promotionId, Guid orgId, Guid? userId, PromotionRedemptionKind kind, BillingEvent evt,
        PromotionNotGrantedReason reason, long? creditsMillis, DateTime now, CancellationToken ct)
    {
        bool discount = kind == PromotionRedemptionKind.DiscountApplied;
        db.PromotionRedemptions.Add(new PromotionRedemption
        {
            Id = Guid.NewGuid(),
            PromotionId = promotionId,
            OrganizationId = orgId,
            UserId = userId,
            Kind = kind,
            ProviderTransactionId = evt.TransactionId,
            ProviderSubscriptionId = discount ? evt.SubscriptionId : null,
            CreditsMillis = discount ? null : creditsMillis,
            DiscountTotal = discount ? evt.DiscountTotal : null,
            CurrencyCode = discount ? evt.CurrencyCode : null,
            Outcome = PromotionRedemptionOutcome.NotGranted,
            NotGrantedReason = reason,
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogWarning(
            PromotionNotGranted,
            "Transaction {Transaction}: promotion {Promotion} was not {Action} ({Reason}); recorded as a NotGranted redemption for support.",
            evt.TransactionId, promotionId, discount ? "counted" : "granted", reason);
    }

    /// <summary>
    /// Whether one more use of the promotion stays within every cap: the global count (re-read under the
    /// lock), and the granted uses of this kind by the org and by the user against the per-org limit.
    /// NotGranted rows are never uses (C10).
    /// </summary>
    private async Task<bool> WithinCapsAsync(Guid promotionId, Guid orgId, Guid? userId, PromotionRedemptionKind kind, CancellationToken ct)
    {
        var caps = await db.Promotions.AsNoTracking()
            .Where(p => p.Id == promotionId)
            .Select(p => new { p.RedemptionCount, p.MaxRedemptions, p.MaxRedemptionsPerOrg })
            .FirstAsync(ct).ConfigureAwait(false);
        if (caps.MaxRedemptions is { } max && caps.RedemptionCount >= max)
        {
            return false;
        }

        IQueryable<PromotionRedemption> uses = db.PromotionRedemptions
            .Where(r => r.PromotionId == promotionId && r.Kind == kind && r.Outcome == PromotionRedemptionOutcome.Granted);
        int byOrg = await uses.CountAsync(r => r.OrganizationId == orgId, ct).ConfigureAwait(false);
        if (byOrg >= caps.MaxRedemptionsPerOrg)
        {
            return false;
        }

        return userId is not { } user
            || await uses.CountAsync(r => r.UserId == user, ct).ConfigureAwait(false) < caps.MaxRedemptionsPerOrg;
    }

    /// <summary>
    /// A paid checkout ends the buyer's other open reservations of the same promotions (same user or same
    /// org): they would exceed the caps it just used, so if one of them is paid after all it is granted
    /// only while the caps still hold.
    /// </summary>
    private Task<int> SupersedeSiblingsAsync(PromotionCheckout checkout, DateTime now, CancellationToken ct)
    {
        Guid? discountId = checkout.DiscountPromotionId;
        Guid? bonusId = checkout.BonusPromotionId;
        return db.PromotionCheckouts
            .Where(c => c.Id != checkout.Id && c.CompletedAt == null && c.SupersededAt == null)
            .Where(c => c.UserId == checkout.UserId || c.OrganizationId == checkout.OrganizationId)
            .Where(c => (discountId != null && c.DiscountPromotionId == discountId) || (bonusId != null && c.BonusPromotionId == bonusId))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.SupersededAt, (DateTime?)now), ct);
    }

    /// <summary>A subscription payment that carried a provider discount: record which promotion it was.</summary>
    private async Task<(BillingEventOutcome, string?)> ApplyDiscountedTransactionAsync(BillingEvent evt, CancellationToken ct)
    {
        Guid? orgId = evt.OrganizationId
            ?? await db.Subscriptions.AsNoTracking()
                .Where(s => s.Provider == evt.Provider && s.ProviderSubscriptionId == evt.SubscriptionId)
                .Select(s => (Guid?)s.OrganizationId).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? await OrganizationByCustomerAsync(evt, ct).ConfigureAwait(false);
        if (orgId is null || !await db.Organizations.AnyAsync(o => o.Id == orgId, ct).ConfigureAwait(false))
        {
            return (BillingEventOutcome.Unresolved, "No organization could be resolved for this discounted transaction.");
        }

        DateTime now = _time.GetUtcNow().UtcDateTime;
        PromotionCheckout? checkout = await CompleteCheckoutAsync(evt, now, ct).ConfigureAwait(false);
        Promotion? discount = await DiscountPromotionAsync(evt, ct).ConfigureAwait(false);
        await RecordDiscountAsync(orgId.Value, evt, discount, checkout, now, ct).ConfigureAwait(false);
        return (BillingEventOutcome.Applied, null);
    }

    /// <summary>The promotion that owns the transaction's provider discount, if any.</summary>
    private async Task<Promotion?> DiscountPromotionAsync(BillingEvent evt, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(evt.DiscountId) || evt.TransactionId is null)
        {
            return null;
        }

        Promotion? promotion = await db.Promotions.AsNoTracking()
            .FirstOrDefaultAsync(p => p.ProviderDiscountId == evt.DiscountId, ct).ConfigureAwait(false);
        if (promotion is null)
        {
            logger.LogInformation("Transaction {Transaction} carries discount {Discount}, which no promotion owns.", evt.TransactionId, evt.DiscountId);
        }

        return promotion;
    }

    /// <summary>
    /// A transaction whose provider discount belongs to one of our promotions gets one redemption row:
    /// once per (promotion, subscription) for a subscription payment (also a unique index, S-11), so the
    /// renewals a recurring discount also reduces neither add rows nor count against the caps again; once
    /// per (promotion, transaction) for a top-up.
    /// <para>
    /// The provider already charged the discounted price, so the row is always written; whether it counts
    /// as a use follows the same C10 rule as a bonus (<see cref="DecideUseAsync"/>, under the promotion's
    /// lock): within every cap it counts; past a cap it counts as an <see cref="PromotionRedemption.Overshoot"/>
    /// only for a live reservation that was within every cap, once per user and org and within the bound;
    /// otherwise it is NotGranted with its reason and counts nothing. The buyer comes from the checkout
    /// record, so the per-user cap sees discounts too.
    /// </para>
    /// </summary>
    private async Task RecordDiscountAsync(Guid orgId, BillingEvent evt, Promotion? promotion, PromotionCheckout? checkout, DateTime now, CancellationToken ct)
    {
        if (promotion is null || evt.TransactionId is null)
        {
            return;
        }

        await PromotionLocks.LockAsync(db, PromotionLocks.PromotionKey(promotion.Id), ct).ConfigureAwait(false);
        string? subscriptionId = evt.SubscriptionId;
        bool recorded = subscriptionId is not null
            ? await db.PromotionRedemptions.AnyAsync(
                r => r.PromotionId == promotion.Id && r.Kind == PromotionRedemptionKind.DiscountApplied && r.ProviderSubscriptionId == subscriptionId, ct).ConfigureAwait(false)
            : await RedemptionExistsAsync(promotion.Id, evt.TransactionId, ct).ConfigureAwait(false);
        if (recorded)
        {
            return;
        }

        PromotionCheckout? reservation = checkout is not null && checkout.OrganizationId == orgId && checkout.DiscountPromotionId == promotion.Id ? checkout : null;
        Guid? userId = reservation?.UserId;
        UseDecision decision = await DecideUseAsync(promotion.Id, orgId, userId, PromotionRedemptionKind.DiscountApplied, reservation?.Id, PaidAt(evt, now), ct).ConfigureAwait(false);
        if (decision.Reason is { } refused)
        {
            await RecordNotGrantedAsync(promotion.Id, orgId, userId, PromotionRedemptionKind.DiscountApplied, evt, refused, null, now, ct).ConfigureAwait(false);
            return;
        }

        if (decision.Overshoot)
        {
            logger.LogWarning(
                "Transaction {Transaction} used discount promotion {Promotion} past a cap on a live reservation that was within every cap; recorded as overshoot.",
                evt.TransactionId, promotion.Id);
        }

        db.PromotionRedemptions.Add(new PromotionRedemption
        {
            Id = Guid.NewGuid(),
            PromotionId = promotion.Id,
            OrganizationId = orgId,
            UserId = userId,
            Kind = PromotionRedemptionKind.DiscountApplied,
            ProviderTransactionId = evt.TransactionId,
            ProviderSubscriptionId = subscriptionId,
            DiscountTotal = evt.DiscountTotal,
            CurrencyCode = evt.CurrencyCode,
            Overshoot = decision.Overshoot,
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await CountRedemptionAsync(promotion.Id, now, ct).ConfigureAwait(false);
        if (reservation is not null)
        {
            await SupersedeSiblingsAsync(reservation, now, ct).ConfigureAwait(false);
        }
    }

    private Task<bool> RedemptionExistsAsync(Guid promotionId, string transactionId, CancellationToken ct)
        => db.PromotionRedemptions.AnyAsync(r => r.PromotionId == promotionId && r.ProviderTransactionId == transactionId, ct);

    /// <summary>
    /// Counts a granted use (an overshoot included) against the promotion's global cap. A NotGranted row
    /// is never counted (C10).
    /// </summary>
    private Task<int> CountRedemptionAsync(Guid promotionId, DateTime now, CancellationToken ct)
        => db.Promotions.Where(p => p.Id == promotionId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.RedemptionCount, p => p.RedemptionCount + 1).SetProperty(p => p.UpdatedAt, now), ct);

    private async Task<(BillingEventOutcome, string?)> ApplyRefundAsync(BillingEvent evt, CancellationToken ct)
    {
        if (evt.TransactionId is null || evt.AdjustmentId is null)
        {
            return (BillingEventOutcome.Unresolved, "Refund without a transaction or adjustment id.");
        }

        // Only top-up payments are clawed back automatically: the grant is found by the transaction it was
        // keyed on. A refunded subscription payment, or a partial refund, needs a human decision.
        string key = TopUpKey(evt.Provider, evt.TransactionId);
        CreditLedgerEntry? original = await db.Set<CreditLedgerEntry>().AsNoTracking()
            .FirstOrDefaultAsync(e => e.IdempotencyKey == key, ct).ConfigureAwait(false);
        if (original is null)
        {
            BillingPendingUpgradeGrant? upgrade = await db.BillingPendingUpgradeGrants
                .FirstOrDefaultAsync(r => r.Provider == evt.Provider && r.TransactionId == evt.TransactionId, ct).ConfigureAwait(false);
            if (upgrade is not null && evt.SubscriptionId is null)
            {
                // The refund did not name its subscription, so no lock was taken up front: take it now and
                // re-read the row under it, so a concurrent grant of the same payment is seen.
                await LockSubscriptionAsync(upgrade.Provider, upgrade.ProviderSubscriptionId, ct).ConfigureAwait(false);
                await db.Entry(upgrade).ReloadAsync(ct).ConfigureAwait(false);
            }

            if (upgrade is not null)
            {
                (BillingEventOutcome, string?) upgraded = await ClawBackUpgradeAsync(upgrade, evt, ct).ConfigureAwait(false);
                // An upgrade payment that is the term's stored opening payment (the interval switch) also takes the term's credits back.
                await ClawBackAnnualTermAsync(evt, ct).ConfigureAwait(false);
                return upgraded;
            }

            return await ClawBackAnnualTermAsync(evt, ct).ConfigureAwait(false)
                ?? await RememberRefundAsync(evt, ct).ConfigureAwait(false);
        }

        if (!evt.FullRefund)
        {
            return (BillingEventOutcome.Applied, "Partial refund of a top-up; no credits changed (review manually).");
        }

        CreditLedgerResult adjust = await new CreditLedger(db, _time).AdjustAsync(
            original.OrganizationId,
            -original.BalanceDeltaMillis,
            ClawbackKey(evt.Provider, evt.TransactionId),
            new CreditLedgerContext(Note: $"refund of top-up {evt.TransactionId}"),
            ct).ConfigureAwait(false);
        if (!adjust.Ok)
        {
            throw new InvalidOperationException($"Refund clawback refused: {adjust.Error}");
        }

        if (adjust.Replayed)
        {
            // A refund then a chargeback (two adjustments) of one payment: clawed back once already.
            return (BillingEventOutcome.Applied, "This top-up was already clawed back; no credits changed.");
        }

        // A fully refunded top-up takes its pack bonus with it.
        string bonusKey = TopUpBonusKey(evt.Provider, evt.TransactionId);
        CreditLedgerEntry? bonus = await db.Set<CreditLedgerEntry>().AsNoTracking()
            .FirstOrDefaultAsync(e => e.IdempotencyKey == bonusKey, ct).ConfigureAwait(false);
        if (bonus is not null)
        {
            CreditLedgerResult clawback = await new CreditLedger(db, _time).AdjustAsync(
                bonus.OrganizationId,
                -bonus.BalanceDeltaMillis,
                BonusClawbackKey(evt.Provider, evt.TransactionId),
                new CreditLedgerContext(Note: $"refund of top-up bonus {evt.TransactionId}"),
                ct).ConfigureAwait(false);
            if (!clawback.Ok)
            {
                throw new InvalidOperationException($"Bonus clawback refused: {clawback.Error}");
            }
        }

        return (BillingEventOutcome.Applied, null);
    }

    /// <summary>
    /// A full refund of an annual subscription's term payment, made within
    /// <see cref="BillingRefundPolicy.MoneyBackDays"/> days of that payment, takes back the plan credits of
    /// the term still unspent (month 0 and every monthly grant, <c>min(remaining, granted)</c> each) under one
    /// <c>clawback:</c> key per payment, so a refund then a chargeback claws back once. The sweeper stops
    /// granting once the payment is fully refunded (the refund row this event wrote). Answers null when the
    /// refund is not that case (not full, not an annual term's payment, or later than the window), leaving it
    /// to <see cref="RememberRefundAsync"/> and a human decision.
    /// </summary>
    private async Task<(BillingEventOutcome, string?)?> ClawBackAnnualTermAsync(BillingEvent evt, CancellationToken ct)
    {
        if (!evt.FullRefund || evt.TransactionId is null)
        {
            return null;
        }

        BillingPayment? payment = await db.BillingPayments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Provider == evt.Provider && p.ProviderTransactionId == evt.TransactionId, ct).ConfigureAwait(false);
        if (payment?.ProviderSubscriptionId is not { } subscriptionId)
        {
            return null;
        }

        await LockSubscriptionAsync(evt.Provider, subscriptionId, ct).ConfigureAwait(false);
        Subscription? sub = await db.Subscriptions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Provider == evt.Provider && s.ProviderSubscriptionId == subscriptionId, ct).ConfigureAwait(false);
        if (sub is not { PeriodStart: { } start, PeriodEnd: { } end }
            || SubscriptionInterval.Of(start, end) != BillingInterval.Annual
            || evt.OccurredAt - payment.OccurredAt > BillingRefundPolicy.MoneyBackWindow)
        {
            return null;
        }

        // The same decision the sweeper uses: a candidate payment of the term (a plain one, or the stored opening
        // payment even when an upgrade row owns it) claws the term's credits back, ambiguous or not.
        AnnualTermPayments term = await AnnualTermPayments.LoadAsync(db, evt.Provider, subscriptionId, start, _time.GetUtcNow().UtcDateTime, sub.TermOpeningTransactionId, ct).ConfigureAwait(false);
        if (!term.IsTermPayment(payment.ProviderTransactionId))
        {
            return null;
        }

        // The org lock before the unspent amounts are read: a concurrent spend (or grant) cannot change them
        // between the read and the adjustment, which takes the same lock.
        await LockOrganizationAsync(sub.OrganizationId, ct).ConfigureAwait(false);

        List<string> keys = AnnualCreditSchedule.TermKeys(evt.Provider, subscriptionId, start);
        var granted = await db.CreditLedgerEntries.AsNoTracking()
            .Where(e => keys.Contains(e.IdempotencyKey) && e.GrantId != null)
            .Select(e => new { e.GrantId, e.BalanceDeltaMillis })
            .ToListAsync(ct).ConfigureAwait(false);
        long take = 0;
        foreach (var entry in granted)
        {
            long remaining = await db.CreditGrants.AsNoTracking()
                .Where(g => g.Id == entry.GrantId).Select(g => g.RemainingMillis).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            take += Math.Min(remaining, entry.BalanceDeltaMillis);
        }

        // Bounded by the term's own grants: what other candidates' clawbacks already took is deducted, so two refunded
        // candidates can never take the term's credits twice (a first debit may have landed on non-term grants).
        List<string> otherClawKeys = term.TermPayments.Where(p => p.TransactionId != evt.TransactionId)
            .Select(p => ClawbackKey(evt.Provider, p.TransactionId)).ToList();
        long alreadyClawed = otherClawKeys.Count == 0
            ? 0
            : -await db.CreditLedgerEntries.AsNoTracking().Where(e => otherClawKeys.Contains(e.IdempotencyKey))
                .SumAsync(e => e.BalanceDeltaMillis, ct).ConfigureAwait(false);
        take = Math.Max(0, take - Math.Max(0, alreadyClawed));

        if (take > 0)
        {
            CreditLedgerResult adjust = await new CreditLedger(db, _time).AdjustAsync(
                sub.OrganizationId,
                -take,
                ClawbackKey(evt.Provider, evt.TransactionId),
                new CreditLedgerContext(Note: $"refund of annual plan payment {evt.TransactionId}"),
                ct).ConfigureAwait(false);
            if (!adjust.Ok)
            {
                throw new InvalidOperationException($"Annual refund clawback refused: {adjust.Error}");
            }

            if (adjust.Replayed)
            {
                return (BillingEventOutcome.Applied, "This annual payment was already clawed back; no credits changed.");
            }
        }

        return (BillingEventOutcome.Applied, take > 0 ? null : "Annual plan payment refunded; nothing of its credits was left to claw back.");
    }

    /// <summary>The credit ledger's own per-organization lock (same key as <see cref="CreditLedger"/>); reentrant in a transaction.</summary>
    private async Task LockOrganizationAsync(Guid organizationId, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
        {
            return;
        }

        string lockKey = organizationId.ToString();
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey}))", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// R-1: a full refund or chargeback of a transaction this processor has not seen (no top-up grant, no
    /// upgrade payment row) — its <c>transaction.completed</c> may simply not have arrived yet. It is
    /// remembered as a tombstone: a <see cref="BillingPendingUpgradeGrant"/> carrying only the transaction,
    /// already clawed back (0), so a later upgrade payment or top-up for it grants nothing. No credits
    /// change now; another adjustment for the same transaction finds the row and is a no-op. A partial
    /// refund, or one whose organization cannot be resolved (the row needs one), only asks for a review.
    /// The tombstone also lands for refunds of renewal payments, which never grant by transaction; harmless.
    /// When the refund names a known subscription it may be such a payment, so the row's note asks for a
    /// manual review and a warning is logged (N-2).
    /// </summary>
    private async Task<(BillingEventOutcome, string?)> RememberRefundAsync(BillingEvent evt, CancellationToken ct)
    {
        if (!evt.FullRefund)
        {
            return (BillingEventOutcome.Applied, "Partial refund of a transaction not seen yet; no credits changed (review manually).");
        }

        Guid? subOrg = evt.SubscriptionId is null
            ? null
            : await db.Subscriptions.AsNoTracking()
                .Where(s => s.Provider == evt.Provider && s.ProviderSubscriptionId == evt.SubscriptionId)
                .Select(s => (Guid?)s.OrganizationId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        Guid? orgId = subOrg ?? evt.OrganizationId ?? await OrganizationByCustomerAsync(evt, ct).ConfigureAwait(false);
        if (orgId is null || !await db.Organizations.AnyAsync(o => o.Id == orgId, ct).ConfigureAwait(false))
        {
            logger.LogWarning(
                "Refund {Adjustment} of transaction {Transaction} matches nothing seen yet and names no known organization; a later payment for it is not blocked. Review manually.",
                evt.AdjustmentId, evt.TransactionId);
            return (BillingEventOutcome.Unresolved, "Refund of a transaction not seen yet, for no known organization; nothing remembered (review manually).");
        }

        DateTime now = _time.GetUtcNow().UtcDateTime;
        db.BillingPendingUpgradeGrants.Add(new BillingPendingUpgradeGrant
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId.Value,
            Provider = evt.Provider,
            ProviderSubscriptionId = evt.SubscriptionId ?? string.Empty,
            TransactionId = evt.TransactionId,
            ClawedBackMillis = 0,
            ClawedBackAt = now,
            CreatedAt = now
        });
        if (subOrg is null)
        {
            return (BillingEventOutcome.Applied, "Refund of a transaction not seen yet; remembered, so its payment will grant nothing.");
        }

        // N-2: a refund naming a known subscription may be of a renewal or initial payment, which is never
        // clawed back automatically; keep the manual-review cue the docs promise.
        logger.LogWarning(
            "Refund {Adjustment} of transaction {Transaction} (subscription {Sub}) matches nothing seen yet; remembered so a late upgrade payment grants nothing. If it refunds a subscription payment, review manually.",
            evt.AdjustmentId, evt.TransactionId, evt.SubscriptionId);
        return (BillingEventOutcome.Applied, "Refund of a transaction not seen yet; remembered, so its payment will grant nothing. Credits unchanged: review manually (subscription payment?).");
    }

    /// <summary>
    /// A full refund or chargeback of an upgrade's proration payment takes back what is left of that
    /// grant: <c>min(grant remaining, granted)</c>, once per payment (the row remembers it) and under the
    /// adjustment's <c>refund:</c> key. A refunded payment not yet matched is closed, so its upgrade event
    /// can no longer grant. A partial refund needs a human, as for top-ups; G-2: if the payment has not granted
    /// yet (waiting for Active, or not paired yet), it is closed with 0 so it never grants the full difference.
    /// </summary>
    private async Task<(BillingEventOutcome, string?)> ClawBackUpgradeAsync(BillingPendingUpgradeGrant row, BillingEvent evt, CancellationToken ct)
    {
        if (!evt.FullRefund)
        {
            if (row.GrantedAt is null && row.ClawedBackAt is null)
            {
                CloseWithZero(row);
                logger.LogWarning(
                    "Partial refund {Adjustment} of upgrade payment {Transaction} (row {Row}) before it granted: closed with 0 granted. Review manually.",
                    evt.AdjustmentId, evt.TransactionId, row.Id);
                return (BillingEventOutcome.Applied, "Partial refund of an upgrade payment not granted yet; closed with 0 granted (review manually).");
            }

            return (BillingEventOutcome.Applied, "Partial refund of an upgrade payment; no credits changed (review manually).");
        }

        if (row.ClawedBackAt is not null)
        {
            return (BillingEventOutcome.Applied, "Upgrade payment already clawed back; no credits changed.");
        }

        long take = 0;
        if (row.GrantedMillis is > 0)
        {
            string key = PlanUpgradeKey(row.Provider, row.TransactionId!);
            Guid? grantId = await db.CreditLedgerEntries.AsNoTracking()
                .Where(e => e.IdempotencyKey == key).Select(e => e.GrantId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            long remaining = grantId is null
                ? 0
                : await db.CreditGrants.AsNoTracking().Where(g => g.Id == grantId).Select(g => g.RemainingMillis).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            take = Math.Min(remaining, row.GrantedMillis.Value);
        }

        if (take > 0)
        {
            CreditLedgerResult adjust = await new CreditLedger(db, _time).AdjustAsync(
                row.OrganizationId,
                -take,
                RefundKey(evt.Provider, evt.AdjustmentId!),
                new CreditLedgerContext(Note: $"refund of plan upgrade {evt.TransactionId}"),
                ct).ConfigureAwait(false);
            if (!adjust.Ok)
            {
                throw new InvalidOperationException($"Upgrade refund clawback refused: {adjust.Error}");
            }
        }

        // F-3: a waiting upgrade (paired, not granted) is retired with 0, so a later Active grants nothing.
        if (row.NewEntitlementMillis is not null && row.GrantedAt is null)
        {
            CloseWithZero(row);
        }

        row.ClawedBackMillis = take;
        row.ClawedBackAt = _time.GetUtcNow().UtcDateTime;
        return (BillingEventOutcome.Applied, take > 0 ? null : "Upgrade payment refunded; nothing of its grant was left to claw back.");
    }
}
