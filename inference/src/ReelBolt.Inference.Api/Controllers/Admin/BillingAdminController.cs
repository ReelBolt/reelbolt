using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Reconcile;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;

namespace ReelBolt.Inference.Api.Controllers.Admin;

/// <summary>One cost roll-up row: a key (the enum member name, the model, the template) and its window totals.</summary>
public sealed record BillingCostBucket(
    string Key,
    decimal CostUsd,
    long CreditsMillis,
    long Events);

/// <summary>One organization's window cost, with its display name.</summary>
public sealed record BillingCostOrganization(
    Guid OrganizationId,
    string Name,
    decimal CostUsd,
    long CreditsMillis,
    long Events);

/// <summary>One plan's committed run rate against the cost its organizations actually incurred in the window.</summary>
public sealed record BillingPlanMargin(
    string PlanKey,
    string PlanName,
    int Organizations,
    decimal CommittedMonthlyUsd,
    decimal WindowCommittedUsd,
    decimal CostUsd,
    decimal MarginUsd,
    double? MarginPercent,
    decimal ListPriceMonthlyUsd = 0m);

/// <summary>
/// The money the provider actually took in one currency within the window, from <c>billing_payments</c>
/// and <c>billing_refunds</c>. Currencies are never summed together: one row per currency code.
/// </summary>
public sealed record BillingCollectedCurrency(
    string? CurrencyCode,
    int MinorUnitExponent,
    int Payments,
    int ZeroAmountPayments,
    long GrossMinor,
    long SubscriptionGrossMinor,
    long TopUpGrossMinor,
    long DiscountMinor,
    long TaxMinor,
    long? FeeMinor,
    int PaymentsWithUnknownTax,
    int Refunds,
    long RefundedMinor,
    long NetMinor,
    long RefundedTaxMinor = 0);

/// <summary>
/// What the ledger can actually say about money coming in. <see cref="Collected"/> is the money the
/// provider actually took (every completed transaction is stored in <c>billing_payments</c>, every approved
/// refund in <c>billing_refunds</c>), in integer minor units per currency. <see cref="CommittedMonthlyUsd"/>
/// is the run rate of paying subscriptions after discounts: list price times the charged fraction of the
/// subscription's latest recorded payment. <see cref="ListPriceMonthlyUsd"/> is the old, pre-discount figure.
/// </summary>
public sealed record BillingAdminRevenue(
    decimal CommittedMonthlyUsd,
    decimal WindowCommittedUsd,
    int PayingSubscriptions,
    int TrialingSubscriptions,
    int InternalTrialOrganizations,
    long TopUpCreditsInWindow,
    bool TopUpUsdRecorded,
    bool TopUpCreditsRecorded,
    decimal ListPriceMonthlyUsd = 0m,
    int DiscountedSubscriptions = 0,
    int FullyDiscountedSubscriptions = 0,
    int SubscriptionsWithoutPayment = 0,
    IReadOnlyList<BillingCollectedCurrency>? Collected = null,
    decimal? CollectedNetUsd = null,
    int OtherCurrencyPayments = 0,
    string? ActiveProvider = null,
    IReadOnlyList<BillingExcludedProvider>? Excluded = null);

/// <summary>
/// A billing provider other than the active one: its payments and paying subscriptions are reported here and
/// never counted in any revenue, run-rate or margin figure (they are test or legacy money).
/// </summary>
public sealed record BillingExcludedProvider(
    string Provider,
    int PayingSubscriptions,
    IReadOnlyList<BillingCollectedCurrency> Collected);

/// <summary>What the window cost us, sliced every way the ledger attributes it.</summary>
public sealed record BillingAdminCost(
    decimal TotalUsd,
    long TotalCreditsMillis,
    long Events,
    long UnpricedPlatformEvents,
    long UnattributedEvents,
    IReadOnlyList<BillingCostBucket> ByProviderKind,
    IReadOnlyList<BillingCostBucket> ByModel,
    IReadOnlyList<BillingCostBucket> ByUsageKind,
    IReadOnlyList<BillingCostBucket> BySource,
    IReadOnlyList<BillingCostBucket> ByBillingMode,
    IReadOnlyList<BillingCostBucket> ByTemplate,
    IReadOnlyList<BillingCostOrganization> TopOrganizations,
    bool TopOrganizationsTruncated);

/// <summary>Body of <c>POST organizations/{orgId}/resync</c>: an optional reason for the audit row (at most 500 characters).</summary>
public sealed record BillingResyncRequest(string? Reason);

/// <summary>200 answer of a resync: the reconcile's outcome (a <see cref="ReconcileOutcome"/> name) and what it applied.</summary>
public sealed record BillingResyncResponse(string Outcome, IReadOnlyList<string> Drift, int SubscriptionEventsApplied, int TransactionsApplied);

/// <summary>One organization whose subscription is waiting on the provider (read from the database only).</summary>
/// <param name="Reason"><c>change_pending|renewal_overdue|cancel_overdue|checkout_unconfirmed|reconcile_failing</c>.</param>
/// <param name="WaitingSince">When the change was requested, or when the renewal or cancellation fell due.</param>
public sealed record BillingReconcileItem(
    Guid OrganizationId,
    string OrganizationName,
    string? PlanKey,
    string? Status,
    string Reason,
    DateTime WaitingSince,
    DateTime? LastCheckedAt,
    string? LastOutcome,
    string? LastError);

/// <summary><c>GET reconcile</c>: at most 200 items, oldest <c>WaitingSince</c> first.</summary>
public sealed record BillingReconcileListResponse(int StuckAfterSeconds, IReadOnlyList<BillingReconcileItem> Items);

public sealed record BillingAdminOverviewResponse(
    DateTime From,
    DateTime To,
    int WindowDays,
    BillingAdminRevenue Revenue,
    BillingAdminCost Cost,
    IReadOnlyList<BillingPlanMargin> Plans);

/// <summary>
/// Read-only platform-admin view of the money side of the ledger: what organizations are committed to
/// pay, what the window cost us (`usage_events.cost_usd`), the margin those two imply per plan, and
/// the cost slices the ledger attributes (provider kind, model, usage kind, source, billing mode,
/// template, organization).
/// <para>
/// Writes nothing, and never invents a figure the ledger does not hold. One honest restriction the
/// caller must carry into the UI: <b>`CommittedMonthlyUsd` is the list-price run rate</b> of paying
/// subscriptions (MRR by contract, not by payment) — a different figure from the cash actually
/// received, which is recorded separately in <c>billing_payments</c>; and COGS is whatever the rate
/// cards priced at write time, so <b>PLACEHOLDER</b> rate cards (B13)
/// make every cost and margin figure here a placeholder too.
/// </para>
/// Routed under <c>/api/v1/billing/admin</c>, not <c>/api/v1/admin/*</c>, because nginx sends the latter
/// to the Go API (the same reason <c>RateCardsController</c> lives off that prefix).
/// </summary>
[ApiController]
[Route("api/v1/billing/admin")]
[Authorize]
public class BillingAdminController(
    InferenceApiDbContext db,
    ICurrentUser currentUser,
    IBillingReconciler reconciler,
    IOptions<BillingOptions> billingOptions,
    IOptions<BillingReconcileOptions> reconcileOptions,
    IEnumerable<IBillingProvider>? providers = null,
    IOptions<BillingProviderOptions>? providerSelection = null) : ControllerBase
{
    /// <summary>Most items the reconcile attention list carries.</summary>
    public const int MaxAttentionItems = 200;

    /// <summary>Longest resync reason the audit row keeps.</summary>
    public const int MaxResyncReasonLength = 500;

    /// <summary>Watch kinds recorded after a member's change was accepted by the provider.</summary>
    private static readonly HashSet<string> ChangeKinds = new(StringComparer.Ordinal) { "change_plan", "seats", "cancel", "resume", "keep" };

    private static readonly SubscriptionStatus[] RenewingStatuses =
        [SubscriptionStatus.Active, SubscriptionStatus.Trialing, SubscriptionStatus.PastDue];

    private static readonly JsonSerializerOptions DetailJson = new(JsonSerializerDefaults.Web);

    private bool BillingDisabled => !billingOptions.Value.Enabled;

    /// <summary>
    /// The organizations whose subscription is waiting on the provider: a change, renewal, cancellation or paid
    /// checkout not confirmed within the stuck threshold, or a reconcile that keeps failing. Read from the
    /// database only — listing never calls the provider.
    /// </summary>
    [HttpGet("reconcile")]
    public async Task<ActionResult<BillingReconcileListResponse>> Attention(CancellationToken ct)
    {
        if (BillingDisabled) return NotFound();
        if (!currentUser.IsPlatformAdmin) return Forbid();

        BillingReconcileOptions settings = reconcileOptions.Value;
        DateTime now = DateTime.UtcNow;
        DateTime stuckBefore = now - settings.StuckAfter;

        Func<BillingReconcileWatch, bool> isStuck = BillingReconcileBacklog.IsStuck(stuckBefore).Compile();
        List<BillingReconcileWatch> watches = await db.BillingReconcileWatches.AsNoTracking()
            .Where(BillingReconcileBacklog.IsFailingOrStuck(stuckBefore))
            .OrderBy(w => w.ChangeRequestedAt)
            .Take(MaxAttentionItems)
            .ToListAsync(ct);

        List<Subscription> overdue = await db.Subscriptions.AsNoTracking()
            .Where(s => s.Provider != null && s.Status != SubscriptionStatus.Canceled)
            .Where(s => (RenewingStatuses.Contains(s.Status) && s.PeriodEnd != null && s.PeriodEnd <= stuckBefore)
                || (s.CancelAtPeriodEnd && s.CancelEffectiveAt != null && s.CancelEffectiveAt <= stuckBefore))
            .OrderBy(s => s.PeriodEnd)
            .Take(MaxAttentionItems)
            .ToListAsync(ct);

        List<Guid> orgIds = watches.Select(w => w.OrganizationId).Concat(overdue.Select(s => s.OrganizationId)).Distinct().ToList();
        if (orgIds.Count == 0) return Ok(new BillingReconcileListResponse(settings.StuckAfterSeconds, []));

        // The subscription in force per org (newest non-canceled), the change endpoints' own selection.
        Dictionary<Guid, Subscription> current = (await db.Subscriptions.AsNoTracking()
                .Where(s => orgIds.Contains(s.OrganizationId) && s.Status != SubscriptionStatus.Canceled)
                .ToListAsync(ct))
            .GroupBy(s => s.OrganizationId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.CreatedAt).First());
        Dictionary<Guid, string> planKeys = await db.Plans.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Key, ct);
        Dictionary<Guid, string> names = await db.Organizations.AsNoTracking().IgnoreQueryFilters()
            .Where(o => orgIds.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, o => o.Name, ct);
        Dictionary<Guid, BillingReconcileWatch> watchByOrg = await db.BillingReconcileWatches.AsNoTracking()
            .Where(w => orgIds.Contains(w.OrganizationId))
            .ToDictionaryAsync(w => w.OrganizationId, ct);

        List<BillingReconcileItem> items = [];
        foreach (Guid orgId in orgIds)
        {
            watchByOrg.TryGetValue(orgId, out BillingReconcileWatch? watch);
            current.TryGetValue(orgId, out Subscription? sub);
            if (AttentionReason(watch, sub, stuckBefore, isStuck) is not { } attention) continue;

            items.Add(new BillingReconcileItem(
                orgId,
                names.TryGetValue(orgId, out string? name) ? name : "(deleted organization)",
                sub is not null && planKeys.TryGetValue(sub.PlanId, out string? key) ? key : null,
                sub?.Status.ToString(),
                attention.Reason,
                attention.Since,
                watch?.LastCheckedAt,
                watch?.LastOutcome,
                watch?.LastError));
        }

        return Ok(new BillingReconcileListResponse(
            settings.StuckAfterSeconds,
            items.OrderBy(i => i.WaitingSince).ThenBy(i => i.OrganizationId).Take(MaxAttentionItems).ToList()));
    }

    /// <summary>
    /// Why an organization needs attention, and since when: a failing reconcile first, then a change or checkout
    /// past the threshold, then a cancellation and finally a renewal the provider never confirmed. A watch's change
    /// needs attention only when <paramref name="isStuck"/> (<see cref="BillingReconcileBacklog.IsStuck"/>) says so:
    /// an abandoned checkout is not listed.
    /// </summary>
    private static (string Reason, DateTime Since)? AttentionReason(
        BillingReconcileWatch? watch, Subscription? sub, DateTime stuckBefore, Func<BillingReconcileWatch, bool> isStuck)
    {
        DateTime? cancelDue = sub is { Provider: not null, CancelAtPeriodEnd: true, CancelEffectiveAt: { } effective } && effective <= stuckBefore
            ? effective
            : null;
        DateTime? renewalDue = sub is { Provider: not null, PeriodEnd: { } end } && RenewingStatuses.Contains(sub.Status) && end <= stuckBefore
            ? end
            : null;
        DateTime? requested = watch?.ChangeRequestedAt;
        DateTime? stuckSince = watch is not null && isStuck(watch) ? requested : null;

        if (watch is { ConsecutiveFailures: > 0 })
        {
            return ("reconcile_failing", requested ?? cancelDue ?? renewalDue ?? watch.LastCheckedAt ?? watch.UpdatedAt);
        }

        if (stuckSince is { } since)
        {
            if (watch!.ChangeKind == BillingReconciler.CheckoutKind) return ("checkout_unconfirmed", since);
            if (ChangeKinds.Contains(watch.ChangeKind)) return ("change_pending", since);
        }

        if (cancelDue is { } cancelSince) return ("cancel_overdue", cancelSince);
        if (renewalDue is { } renewalSince) return ("renewal_overdue", renewalSince);

        // An overdue watch the reconciler kept due (too recent or not applied) with no overdue date on the row.
        return stuckSince is { } due ? ("change_pending", due) : null;
    }

    /// <summary>
    /// "Resync from Paddle": reads the organization's subscription from the provider and applies it now
    /// (<c>force</c>), under the org's change claim. Every call that reaches the reconciler is audited in
    /// <c>billing_admin_actions</c>, whatever its outcome — written with <see cref="CancellationToken.None"/>, so a
    /// client abort (Outcome <c>Cancelled</c> when the run itself was cancelled) never loses it.
    /// </summary>
    [HttpPost("organizations/{orgId:guid}/resync")]
    public async Task<IActionResult> Resync(Guid orgId, [FromBody] BillingResyncRequest? body, CancellationToken ct)
    {
        if (BillingDisabled) return NotFound();
        if (!currentUser.IsPlatformAdmin) return Forbid();

        string? reason = string.IsNullOrWhiteSpace(body?.Reason) ? null : body.Reason.Trim();
        if (reason is { Length: > MaxResyncReasonLength })
            return BadRequest(new { message = $"The reason is at most {MaxResyncReasonLength} characters." });

        if (!await db.Organizations.AsNoTracking().AnyAsync(o => o.Id == orgId, ct)) return NotFound();

        ReconcileResult result;
        try
        {
            result = await reconciler.ReconcileAsync(orgId, ReconcileTrigger.Admin, force: true, ct);
        }
        catch (Exception ex)
        {
            // Applying failed and rolled back (the reconciler logged it), or the admin's client went away and the
            // run was cancelled — possibly after something was applied. Either way audited, then rethrown.
            string outcome = ex is OperationCanceledException && ct.IsCancellationRequested ? "Cancelled" : "Error";
            db.ChangeTracker.Clear();
            await AuditResyncAsync(orgId, reason, outcome, new { outcome, error = ex.GetType().Name }, CancellationToken.None);
            throw;
        }

        await AuditResyncAsync(orgId, reason, result.Outcome.ToString(), new
        {
            outcome = result.Outcome.ToString(),
            drift = result.Drift,
            subscriptionEventsApplied = result.SubscriptionEventsApplied,
            transactionsApplied = result.TransactionsApplied,
            error = result.Error
        }, CancellationToken.None); // the reconcile may have applied: its audit row survives a client abort

        return result.Outcome switch
        {
            ReconcileOutcome.Busy => Conflict(new { error = "change_in_progress" }),
            ReconcileOutcome.ProviderUnavailable => StatusCode(StatusCodes.Status502BadGateway, new { error = "billing_provider_error" }),
            _ => Ok(new BillingResyncResponse(result.Outcome.ToString(), result.Drift, result.SubscriptionEventsApplied, result.TransactionsApplied))
        };
    }

    private async Task AuditResyncAsync(Guid orgId, string? reason, string outcome, object detail, CancellationToken ct)
    {
        db.BillingAdminActions.Add(new BillingAdminAction
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            ActorUserId = currentUser.UserId,
            Action = "resync",
            Reason = reason,
            Outcome = outcome,
            DetailJson = JsonSerializer.Serialize(detail, DetailJson),
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Default window when the caller supplies neither bound.</summary>
    public const int DefaultWindowDays = 30;

    /// <summary>Refuses to aggregate a wider window than this; it is an unbounded table scan otherwise.</summary>
    public const int MaxWindowDays = 366;

    /// <summary>How many organizations the "top organizations by cost" list carries.</summary>
    private const int TopOrganizationCount = 20;

    /// <summary>How many rows each cost slice carries (largest first).</summary>
    private const int MaxBucketsPerSlice = 25;

    /// <summary>A 30-day month, the convention every prorated figure below is stated under.</summary>
    private const double DaysPerMonth = 30d;

    [HttpGet("overview")]
    public async Task<ActionResult<BillingAdminOverviewResponse>> Overview(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();

        DateTime end = NormalizeUtc(to ?? DateTime.UtcNow);
        DateTime start = NormalizeUtc(from ?? end.AddDays(-DefaultWindowDays));
        if (start >= end) return BadRequest(new { message = "from must be before to." });

        int windowDays = (int)Math.Ceiling((end - start).TotalDays);
        if (windowDays > MaxWindowDays)
            return BadRequest(new { message = $"The window is at most {MaxWindowDays} days." });

        List<UsageEvent> events = await db.UsageEvents.AsNoTracking()
            .Where(e => e.OccurredAt >= start && e.OccurredAt < end)
            .ToListAsync(ct);

        List<Organization> organizations = await db.Organizations.AsNoTracking().ToListAsync(ct);
        Dictionary<Guid, string> orgNames = organizations.ToDictionary(o => o.Id, o => o.Name);

        List<Subscription> subscriptions = await db.Subscriptions.AsNoTracking()
            .Where(s => s.Status != SubscriptionStatus.Canceled)
            .ToListAsync(ct);
        Dictionary<Guid, Plan> plansById = (await db.Plans.AsNoTracking().ToListAsync(ct)).ToDictionary(p => p.Id);

        BillingAdminCost cost = await BuildCostAsync(events, orgNames, ct);

        // Every organization's window cost, deliberately NOT read back off the truncated "top
        // organizations" list: the per-plan margin rows must account for every organization, or the
        // organizations past the cap would silently reappear as unplanned cost.
        Dictionary<Guid, decimal> costByOrganization = events
            .GroupBy(e => e.OrganizationId)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.CostUsd));

        string? activeProvider = ActiveProviderName();
        Dictionary<Guid, decimal> chargedFraction = await BuildChargedFractionsAsync(subscriptions, ct);
        BillingAdminRevenue revenue = await BuildRevenueAsync(subscriptions, plansById, chargedFraction, activeProvider, windowDays, start, end, ct);
        List<BillingPlanMargin> planMargins = BuildPlanMargins(subscriptions, plansById, chargedFraction, activeProvider, events, costByOrganization, windowDays);

        return Ok(new BillingAdminOverviewResponse(start, end, windowDays, revenue, cost, planMargins));
    }

    private async Task<BillingAdminRevenue> BuildRevenueAsync(
        List<Subscription> subscriptions, Dictionary<Guid, Plan> plansById, Dictionary<Guid, decimal> chargedFraction,
        string? activeProvider, int windowDays, DateTime start, DateTime end, CancellationToken ct)
    {
        // "Paying" means the provider holds a live subscription that bills: raw Trial rows (Provider
        // null, no money ever moved) are counted separately so a big trial population never reads as
        // revenue.
        List<Subscription> paying = subscriptions.Where(s => IsCountedPaying(s, activeProvider)).ToList();

        decimal listMonthly = paying.Sum(s => MonthlyUsd(plansById, s));
        decimal committedMonthly = paying.Sum(s => CommittedUsd(plansById, chargedFraction, s));
        int withoutPayment = paying.Count(s => !chargedFraction.ContainsKey(s.Id));
        int discounted = paying.Count(s => chargedFraction.TryGetValue(s.Id, out decimal f) && f < 1m);
        int fullyDiscounted = paying.Count(s => chargedFraction.TryGetValue(s.Id, out decimal f) && f <= 0m);

        long topUpMillis = await db.CreditLedgerEntries.AsNoTracking()
            .Where(e => e.Kind == CreditLedgerEntryKind.TopUp && e.CreatedAt >= start && e.CreatedAt < end)
            .SumAsync(e => (long?)e.BalanceDeltaMillis, ct) ?? 0L;

        (List<BillingCollectedCurrency> collected, List<BillingExcludedProvider> excluded) =
            await BuildCollectedAsync(subscriptions, activeProvider, start, end, ct);
        BillingCollectedCurrency? usd = collected.FirstOrDefault(c => string.Equals(c.CurrencyCode, "USD", StringComparison.OrdinalIgnoreCase));

        return new BillingAdminRevenue(
            CommittedMonthlyUsd: Round(committedMonthly),
            WindowCommittedUsd: Round(Prorate(committedMonthly, windowDays)),
            PayingSubscriptions: paying.Count,
            TrialingSubscriptions: subscriptions.Count(s => s.Status == SubscriptionStatus.Trialing),
            InternalTrialOrganizations: subscriptions.Count(s => s.Provider is null),
            TopUpCreditsInWindow: topUpMillis / 1000,
            TopUpUsdRecorded: true,
            TopUpCreditsRecorded: true,
            ListPriceMonthlyUsd: Round(listMonthly),
            DiscountedSubscriptions: discounted,
            FullyDiscountedSubscriptions: fullyDiscounted,
            SubscriptionsWithoutPayment: withoutPayment,
            Collected: collected,
            CollectedNetUsd: usd is null ? null : usd.NetMinor / 100m,
            OtherCurrencyPayments: collected
                .Where(c => !string.Equals(c.CurrencyCode, "USD", StringComparison.OrdinalIgnoreCase))
                .Sum(c => c.Payments),
            ActiveProvider: activeProvider,
            Excluded: excluded);
    }

    /// <summary>
    /// The charged fraction of each subscription's latest recorded payment, keyed by subscription id:
    /// (subtotal - discount) / subtotal when the provider stated both, else 0 for an amount of 0, else 1.
    /// A subscription with no recorded payment has no entry (the caller counts it at list price). Only the
    /// latest payment decides, so a once-only discount does not follow the subscription into its renewals.
    /// </summary>
    private async Task<Dictionary<Guid, decimal>> BuildChargedFractionsAsync(List<Subscription> subscriptions, CancellationToken ct)
    {
        List<Subscription> keyed = subscriptions
            .Where(s => s.Provider is not null && s.ProviderSubscriptionId is not null).ToList();
        if (keyed.Count == 0) return [];

        string[] providerSubscriptionIds = keyed.Select(s => s.ProviderSubscriptionId!).Distinct().ToArray();
        var payments = await db.BillingPayments.AsNoTracking()
            .Where(p => p.ProviderSubscriptionId != null && providerSubscriptionIds.Contains(p.ProviderSubscriptionId))
            .Select(p => new { p.Provider, p.ProviderSubscriptionId, p.OccurredAt, p.AmountMinor, p.SubtotalMinor, p.DiscountMinor })
            .ToListAsync(ct);

        var latest = payments
            .GroupBy(p => (p.Provider, p.ProviderSubscriptionId))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.OccurredAt).First());

        Dictionary<Guid, decimal> fractions = [];
        foreach (Subscription subscription in keyed)
        {
            if (!latest.TryGetValue((subscription.Provider!, subscription.ProviderSubscriptionId), out var payment)) continue;

            fractions[subscription.Id] = payment.SubtotalMinor is > 0 && payment.DiscountMinor is { } discount
                ? Math.Clamp((payment.SubtotalMinor.Value - discount) / (decimal)payment.SubtotalMinor.Value, 0m, 1m)
                : payment.AmountMinor == 0 ? 0m : 1m;
        }

        return fractions;
    }

    private async Task<(List<BillingCollectedCurrency> Counted, List<BillingExcludedProvider> Excluded)> BuildCollectedAsync(
        List<Subscription> subscriptions, string? activeProvider, DateTime start, DateTime end, CancellationToken ct)
    {
        var allPayments = await db.BillingPayments.AsNoTracking()
            .Where(p => p.OccurredAt >= start && p.OccurredAt < end)
            .Select(p => new PaymentFact(p.Provider, p.CurrencyCode, p.ProviderSubscriptionId, p.AmountMinor, p.FeeMinor, p.DiscountMinor, p.TaxMinor, p.TotalMinor))
            .ToListAsync(ct);
        var allRefunds = await db.BillingRefunds.AsNoTracking()
            .Where(r => r.OccurredAt >= start && r.OccurredAt < end)
            .Select(r => new RefundFact(r.Provider, r.CurrencyCode, r.AmountMinor, r.TaxMinor))
            .ToListAsync(ct);

        bool Counted(string provider) => activeProvider is null || string.Equals(provider, activeProvider, StringComparison.OrdinalIgnoreCase);
        List<BillingCollectedCurrency> counted = CollectedRows(allPayments.Where(p => Counted(p.Provider)).ToList(), allRefunds.Where(r => Counted(r.Provider)).ToList());

        List<BillingExcludedProvider> excluded = [];
        if (activeProvider is not null)
        {
            foreach (string provider in allPayments.Select(p => p.Provider).Concat(allRefunds.Select(r => r.Provider))
                         .Concat(subscriptions.Where(IsPaying).Select(s => s.Provider!))
                         .Where(p => !Counted(p)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal))
            {
                excluded.Add(new BillingExcludedProvider(
                    provider,
                    subscriptions.Count(s => IsPaying(s) && string.Equals(s.Provider, provider, StringComparison.OrdinalIgnoreCase)),
                    CollectedRows(
                        allPayments.Where(p => string.Equals(p.Provider, provider, StringComparison.OrdinalIgnoreCase)).ToList(),
                        allRefunds.Where(r => string.Equals(r.Provider, provider, StringComparison.OrdinalIgnoreCase)).ToList())));
            }
        }

        return (counted, excluded);
    }

    private sealed record PaymentFact(string Provider, string? CurrencyCode, string? ProviderSubscriptionId, long AmountMinor, long? FeeMinor, long? DiscountMinor, long? TaxMinor, long? TotalMinor);

    private sealed record RefundFact(string Provider, string? CurrencyCode, long AmountMinor, long? TaxMinor);

    private static List<BillingCollectedCurrency> CollectedRows(List<PaymentFact> payments, List<RefundFact> refunds)
    {
        List<BillingCollectedCurrency> rows = [];
        foreach (string? currency in payments.Select(p => p.CurrencyCode).Concat(refunds.Select(r => r.CurrencyCode)).Distinct()
                     .OrderBy(c => c, StringComparer.Ordinal))
        {
            var mine = payments.Where(p => p.CurrencyCode == currency).ToList();
            var mineRefunds = refunds.Where(r => r.CurrencyCode == currency).ToList();

            // Tax is stated on the transaction total; when a provider credit was applied the buyer paid only
            // part of it, so the tax share of what was collected is pro rata.
            long Tax(long? tax, long? total, long amount) =>
                tax is not { } t ? 0
                : total is > 0 && amount < total ? (long)Math.Round(t * (decimal)amount / total.Value, MidpointRounding.AwayFromZero)
                : t;

            long gross = mine.Sum(p => p.AmountMinor);
            long taxShare = mine.Sum(p => Tax(p.TaxMinor, p.TotalMinor, p.AmountMinor));
            long refunded = mineRefunds.Sum(r => r.AmountMinor);
            long refundedTax = mineRefunds.Sum(r => r.TaxMinor ?? 0);
            rows.Add(new BillingCollectedCurrency(
                CurrencyCode: currency,
                MinorUnitExponent: MinorUnitExponent(currency),
                Payments: mine.Count,
                ZeroAmountPayments: mine.Count(p => p.AmountMinor == 0),
                GrossMinor: gross,
                SubscriptionGrossMinor: mine.Where(p => p.ProviderSubscriptionId != null).Sum(p => p.AmountMinor),
                TopUpGrossMinor: mine.Where(p => p.ProviderSubscriptionId == null).Sum(p => p.AmountMinor),
                DiscountMinor: mine.Sum(p => p.DiscountMinor ?? 0),
                TaxMinor: taxShare,
                FeeMinor: mine.Any(p => p.FeeMinor != null) ? mine.Sum(p => p.FeeMinor ?? 0) : null,
                PaymentsWithUnknownTax: mine.Count(p => p.TaxMinor == null),
                Refunds: mineRefunds.Count,
                RefundedMinor: refunded,
                NetMinor: gross - taxShare - (refunded - refundedTax),
                RefundedTaxMinor: refundedTax));
        }

        return rows;
    }

    /// <summary>The provider holds a live subscription that bills (not an internal grant, not a trial).</summary>
    private static bool IsPaying(Subscription s) =>
        s.Provider is not null && s.Status is SubscriptionStatus.Active or SubscriptionStatus.PastDue;

    /// <summary>Paying, and (when a provider is active) on that provider: only the active provider is revenue.</summary>
    private static bool IsCountedPaying(Subscription s, string? activeProvider) =>
        IsPaying(s) && (activeProvider is null || string.Equals(s.Provider, activeProvider, StringComparison.OrdinalIgnoreCase));

    private string? ActiveProviderName() =>
        BillingProviderSelector.Active(providers ?? [], providerSelection?.Value)?.Name;

    private static int MinorUnitExponent(string? currencyCode) => currencyCode?.ToUpperInvariant() switch
    {
        "BIF" or "CLP" or "DJF" or "GNF" or "ISK" or "JPY" or "KMF" or "KRW" or "PYG" or "RWF" or "UGX" or "UYI"
            or "VND" or "VUV" or "XAF" or "XOF" or "XPF" => 0,
        "BHD" or "IQD" or "JOD" or "KWD" or "LYD" or "OMR" or "TND" => 3,
        _ => 2
    };

    /// <summary>List price after the charged fraction of the latest recorded payment; list price when none is recorded.</summary>
    private static decimal CommittedUsd(Dictionary<Guid, Plan> plansById, Dictionary<Guid, decimal> chargedFraction, Subscription subscription) =>
        MonthlyUsd(plansById, subscription) * chargedFraction.GetValueOrDefault(subscription.Id, 1m);

    private static List<BillingPlanMargin> BuildPlanMargins(
        List<Subscription> subscriptions, Dictionary<Guid, Plan> plansById, Dictionary<Guid, decimal> chargedFraction,
        string? activeProvider, List<UsageEvent> events, Dictionary<Guid, decimal> costByOrganization, int windowDays)
    {
        // The newest non-canceled subscription per organization is the one in force, the same rule
        // BillingController.Summary uses.
        Dictionary<Guid, Subscription> current = [];
        foreach (Subscription subscription in subscriptions.OrderBy(s => s.CreatedAt))
            current[subscription.OrganizationId] = subscription;

        Dictionary<Guid, decimal> costByPlan = [];
        decimal attributedCost = 0m;
        foreach ((Guid organizationId, Subscription subscription) in current)
        {
            decimal orgCost = costByOrganization.TryGetValue(organizationId, out decimal known) ? known : 0m;
            attributedCost += orgCost;
            costByPlan[subscription.PlanId] = costByPlan.GetValueOrDefault(subscription.PlanId) + orgCost;
        }

        // Cost on a usage row whose organization has no subscription at all (a platform-attributed
        // or pre-subscription row) must not vanish: it lands in its own row.
        decimal unplannedCost = events.Sum(e => e.CostUsd) - attributedCost;

        List<BillingPlanMargin> rows = [];
        foreach (IGrouping<Guid, Subscription> group in current.Values.GroupBy(s => s.PlanId))
        {
            plansById.TryGetValue(group.Key, out Plan? plan);
            // Committed counts paying provider subscriptions only (as BuildRevenueAsync does): internal grants
            // and trialing rows still count as organizations and carry cost, but add no committed revenue.
            List<Subscription> payingInGroup = group.Where(s => IsCountedPaying(s, activeProvider)).ToList();
            decimal listPrice = payingInGroup.Sum(s => MonthlyUsd(plansById, s));
            decimal committed = payingInGroup.Sum(s => CommittedUsd(plansById, chargedFraction, s));
            decimal planCost = costByPlan.GetValueOrDefault(group.Key);
            decimal windowCommitted = Prorate(committed, windowDays);
            decimal margin = windowCommitted - planCost;
            rows.Add(new BillingPlanMargin(
                PlanKey: plan?.Key ?? group.Key.ToString(),
                PlanName: plan?.Name ?? "(unknown plan)",
                Organizations: group.Count(),
                CommittedMonthlyUsd: Round(committed),
                WindowCommittedUsd: Round(windowCommitted),
                CostUsd: Round(planCost),
                MarginUsd: Round(margin),
                MarginPercent: windowCommitted > 0 ? (double)Math.Round(margin / windowCommitted * 100m, 2) : null,
                ListPriceMonthlyUsd: Round(listPrice)));
        }

        if (unplannedCost != 0m)
        {
            rows.Add(new BillingPlanMargin(
                PlanKey: "(no plan)",
                PlanName: "Usage with no subscription",
                Organizations: 0,
                CommittedMonthlyUsd: 0m,
                WindowCommittedUsd: 0m,
                CostUsd: Round(unplannedCost),
                MarginUsd: Round(-unplannedCost),
                MarginPercent: null));
        }

        return rows.OrderByDescending(r => r.CostUsd).ToList();
    }

    private async Task<BillingAdminCost> BuildCostAsync(
        List<UsageEvent> events, Dictionary<Guid, string> orgNames, CancellationToken ct)
    {
        // usage_events carries the execution id, not the template; resolve the template name through
        // the execution's definition in two small queries rather than one wide join.
        Guid[] executionIds = events.Where(e => e.ExecutionId is not null).Select(e => e.ExecutionId!.Value).Distinct().ToArray();
        Dictionary<Guid, string> templateNameByExecution = [];
        if (executionIds.Length > 0)
        {
            var executions = await db.WorkflowExecutions.AsNoTracking()
                .Where(x => executionIds.Contains(x.Id))
                .Select(x => new { x.Id, x.WorkflowDefinitionId })
                .ToListAsync(ct);
            Guid[] definitionIds = executions.Select(x => x.WorkflowDefinitionId).Distinct().ToArray();
            Dictionary<Guid, string> nameByDefinition = (await db.WorkflowDefinitions.AsNoTracking()
                    .Where(d => definitionIds.Contains(d.Id))
                    .Select(d => new { d.Id, d.Name })
                    .ToListAsync(ct))
                .ToDictionary(d => d.Id, d => d.Name);
            foreach (var execution in executions)
                if (nameByDefinition.TryGetValue(execution.WorkflowDefinitionId, out string? name))
                    templateNameByExecution[execution.Id] = name;
        }

        List<BillingCostBucket> byTemplate = BuildTemplateBuckets(events, templateNameByExecution);

        List<BillingCostOrganization> byOrganization = events
            .GroupBy(e => e.OrganizationId)
            .Select(g => new BillingCostOrganization(
                g.Key,
                orgNames.TryGetValue(g.Key, out string? name) ? name : "(deleted organization)",
                Round(g.Sum(e => e.CostUsd)),
                g.Sum(e => e.CreditsMillis),
                g.LongCount()))
            .OrderByDescending(o => o.CostUsd)
            .ThenByDescending(o => o.CreditsMillis)
            .ToList();

        return new BillingAdminCost(
            TotalUsd: Round(events.Sum(e => e.CostUsd)),
            TotalCreditsMillis: events.Sum(e => e.CreditsMillis),
            Events: events.Count,
            // A Platform row nothing priced was recorded at zero by the price service's fail-closed path:
            // real spend that is invisible in every total above. Never hide the count. An oracle-priced row
            // (Higgsfield) has no price id but real credits, and storage is free by decision, so neither counts.
            UnpricedPlatformEvents: events.LongCount(e => e.PriceId is null && e.CreditsMillis == 0
                && e.BillingMode == UsageBillingMode.Platform && e.Kind != UsageKind.StorageGbDay),
            UnattributedEvents: events.LongCount(e => e.Source == UsageSource.Unattributed),
            ByProviderKind: Slice(events, e => e.ProviderKind.ToString()),
            ByModel: Slice(events, e => string.IsNullOrWhiteSpace(e.ModelName) ? "(no model recorded)" : e.ModelName),
            ByUsageKind: Slice(events, e => e.Kind.ToString()),
            BySource: Slice(events, e => e.Source.ToString()),
            ByBillingMode: Slice(events, e => e.BillingMode.ToString()),
            ByTemplate: byTemplate,
            TopOrganizations: byOrganization.Take(TopOrganizationCount).ToList(),
            TopOrganizationsTruncated: byOrganization.Count > TopOrganizationCount);
    }

    private static List<BillingCostBucket> BuildTemplateBuckets(List<UsageEvent> events, Dictionary<Guid, string> templateNameByExecution)
    {
        // Rows with no execution id are not a template at all (assistant chat, indexing, storage) and
        // say so rather than being folded into one; a run whose definition is gone reads as deleted.
        Dictionary<string, (decimal Cost, long Credits, long Events)> buckets = [];

        foreach (UsageEvent e in events)
        {
            string label = e.ExecutionId is { } id && templateNameByExecution.TryGetValue(id, out string? name)
                ? name
                : e.ExecutionId is null ? "(not a workflow run)" : "(template deleted)";
            (decimal Cost, long Credits, long Events) current = buckets.GetValueOrDefault(label, (0m, 0L, 0L));
            buckets[label] = (current.Cost + e.CostUsd, current.Credits + e.CreditsMillis, current.Events + 1);
        }

        return buckets
            .Select(kv => new BillingCostBucket(kv.Key, Round(kv.Value.Cost), kv.Value.Credits, kv.Value.Events))
            .OrderByDescending(b => b.CostUsd)
            .ThenByDescending(b => b.Events)
            .Take(MaxBucketsPerSlice)
            .ToList();
    }

    private static List<BillingCostBucket> Slice(List<UsageEvent> events, Func<UsageEvent, string> keySelector) =>
        events
            .GroupBy(keySelector)
            .Select(g => new BillingCostBucket(g.Key, Round(g.Sum(e => e.CostUsd)), g.Sum(e => e.CreditsMillis), g.LongCount()))
            .OrderByDescending(b => b.CostUsd)
            .ThenByDescending(b => b.Events)
            .Take(MaxBucketsPerSlice)
            .ToList();

    /// <summary>List price of a subscription per month: per seat for a per-seat plan, flat otherwise.</summary>
    private static decimal MonthlyUsd(Dictionary<Guid, Plan> plansById, Subscription subscription)
    {
        if (!plansById.TryGetValue(subscription.PlanId, out Plan? plan) || plan.PriceMonthlyUsd is not { } price)
            return 0m;
        return plan.Kind == PlanKind.PerSeat ? price * Math.Max(subscription.Seats, 1) : price;
    }

    private static decimal Prorate(decimal monthlyUsd, int windowDays) =>
        monthlyUsd * windowDays / (decimal)DaysPerMonth;

    private static decimal Round(decimal value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);
}
