using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Billing.Reconcile;

/// <summary>One sweep's tally.</summary>
/// <param name="Candidates">Organizations handed to the reconciler.</param>
/// <param name="Failed">Of those, how many threw (logged; the sweep went on).</param>
/// <param name="Stuck">Watches stuck after the pass (<see cref="BillingReconcileBacklog.IsStuck"/>).</param>
/// <param name="Unresolved24h">Webhook rows of the last 24 hours that ended <c>Unresolved</c>.</param>
public sealed record BillingSweepResult(int Candidates, int Failed, long Stuck, long Unresolved24h)
{
    public static readonly BillingSweepResult None = new(0, 0, 0, 0);
}

/// <summary>
/// Periodic lost-webhook recovery (<c>Billing:Reconcile</c>). Every <see cref="BillingReconcileOptions.SweepInterval"/>
/// (after <see cref="BillingReconcileOptions.StartupDelay"/>) it picks at most <see cref="BillingReconcileOptions.MaxPerSweep"/>,
/// those without a failing check first, then oldest first:
/// <list type="bullet">
/// <item>a watch whose change is older than the stuck threshold (an <see cref="BillingReconciler.OverdueKind"/>
/// one is already past it) and was not checked since the change (a change recorded after the last check — its
/// <c>UpdatedAt</c> is later, as an older due time is kept), or not within the last
/// <see cref="BillingReconcileOptions.SweepInterval"/> — a check that left it due (TooRecent) is retried on the next
/// sweep, a failing one (ProviderUnavailable, Stale) backs off: <c>min(SweepInterval * 2^(failures - 1), 1 h)</c>
/// (<see cref="RetryDelay"/>);</item>
/// <item>a <see cref="BillingReconciler.CheckoutKind"/> watch, by its own query so abandoned checkouts can never fill
/// the window of the others (Security S-2), at most a quarter of the pass: every
/// <see cref="BillingReconcileOptions.CheckoutRecheck"/> after a check that found nothing live, else on the change-watch
/// cadence above (TooRecent, ProviderUnavailable, Stale: QA-4);</item>
/// <item>a provider subscription (Active, Trialing or PastDue) whose period ended longer than the stuck threshold
/// ago — a renewal webhook that never came;</item>
/// <item>a cancel-at-period-end subscription past its effective date by the threshold that is still not Canceled.</item>
/// </list>
/// The last two back off through the org's watch row (the reconciler opens one with kind
/// <see cref="BillingReconciler.OverdueKind"/> and records <c>LastCheckedAt</c>), so an org found in order is not
/// re-read within <see cref="BillingReconcileOptions.RecheckAfter"/>; an org whose watch still has a change due
/// (one whose check failed included) is left to the first rule.
/// <para>
/// After the pass it also builds B12's <see cref="BillingDriftReport"/> from the results: the residual drift
/// the reconcile left (<see cref="BillingDriftReport.Classify"/>) and the ledger's unattributed
/// <c>usage_events</c>, logged as <see cref="BillingWebhookDiagnostics.DriftDetected"/> /
/// <see cref="BillingWebhookDiagnostics.UsageUnattributed"/> and alerted once an hour under the
/// <see cref="BillingDriftScope"/> scope.
/// </para>
/// <para>
/// The sweeper only selects: each candidate gets its own DI scope and <see cref="IBillingReconciler.ReconcileAsync"/>
/// (the org's change claim, so a second replica gets <see cref="ReconcileOutcome.Busy"/> and applies nothing), and
/// the reconciler keeps the watch. One organization's exception is logged and never stops the sweep. After the
/// pass the backlog gauges are reported and every still-stuck watch (<see cref="BillingReconcileBacklog.IsStuck"/>: an
/// abandoned checkout is not one) logs <see cref="BillingWebhookDiagnostics.ChangeStuck"/>, and when this process's webhook
/// signature or processing failures (<see cref="BillingWebhookFailureTotals"/>) grew since the previous alert, at most once
/// an hour, one <c>billing_webhooks</c> alert goes to <see cref="SpendReportOptions.AlertWebhookUrl"/> (the daily report's
/// sender) and logs <see cref="BillingWebhookDiagnostics.WebhookFailuresAlert"/> (QA-2).
/// A no-op while <c>Billing:Enabled</c> or <c>Billing:Reconcile:Enabled</c> is false.
/// </para>
/// </summary>
public sealed class BillingReconcileSweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<BillingOptions> billingOptions,
    IOptions<BillingReconcileOptions> options,
    ILogger<BillingReconcileSweeper> logger,
    TimeProvider? clock = null,
    BillingWebhookFailureTotals? failureTotals = null) : BackgroundService
{
    private static readonly SubscriptionStatus[] RenewingStatuses =
        [SubscriptionStatus.Active, SubscriptionStatus.Trialing, SubscriptionStatus.PastDue];

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly BillingWebhookFailureTotals _failureTotals = failureTotals ?? BillingWebhookDiagnostics.Totals;

    /// <summary>The most often a webhook-failure alert is pushed (per process).</summary>
    public static readonly TimeSpan FailureAlertInterval = TimeSpan.FromHours(1);

    /// <summary>Alert scope of B12's reconciliation drift report.</summary>
    public const string BillingDriftScope = "billing_drift";

    /// <summary>The window the unattributed-<c>usage_events</c> count covers.</summary>
    public static readonly TimeSpan UsageAttributionWindow = TimeSpan.FromHours(24);

    private DateTime? _lastFailureAlertAt;
    private DateTime? _lastDriftAlertAt;
    private DateTime? _failureCountsSince;
    private long _alertedSignatureFailures = -1;
    private long _alertedProcessingFailures = -1;

    private bool Disabled => !billingOptions.Value.Enabled || !options.Value.Enabled;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Disabled)
        {
            logger.LogDebug("The billing reconcile sweep is disabled (Billing:Enabled / Billing:Reconcile:Enabled)");
            return;
        }

        try
        {
            await Task.Delay(options.Value.StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SweepOnceAsync(_clock.GetUtcNow().UtcDateTime, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The billing reconcile sweep failed; retrying after the interval");
                }

                await Task.Delay(options.Value.SweepInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>One sweep at <paramref name="nowUtc"/>. Public for tests.</summary>
    public async Task<BillingSweepResult> SweepOnceAsync(DateTime nowUtc, CancellationToken ct)
    {
        if (Disabled) return BillingSweepResult.None;

        BillingReconcileOptions settings = options.Value;
        DateTime stuckBefore = nowUtc - settings.StuckAfter;
        DateTime recheckBefore = nowUtc - settings.RecheckAfter;

        List<Guid> candidates = await SelectCandidatesAsync(nowUtc, stuckBefore, recheckBefore, settings, ct);

        int failed = 0;
        List<BillingDriftFinding> findings = [];
        foreach (Guid organizationId in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                using IDisposable system = scope.ServiceProvider.GetService<ITenantContext>().System();
                IBillingReconciler reconciler = scope.ServiceProvider.GetRequiredService<IBillingReconciler>();
                ReconcileResult result = await reconciler.ReconcileAsync(organizationId, ReconcileTrigger.Sweep, force: false, ct);
                findings.AddRange(BillingDriftReport.Classify(organizationId, result));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failed++;
                logger.LogError(ex, "The billing reconcile sweep could not reconcile organization {Org}; continuing with the next one", organizationId);
            }
        }

        (long stuck, long unresolved) = await ReportBacklogAsync(nowUtc, stuckBefore, ct);
        if (candidates.Count > 0 || stuck > 0)
        {
            logger.LogInformation(
                "The billing reconcile sweep checked {Count} organization(s), {Failed} failed; {Stuck} change(s) still waiting past the threshold, {Unresolved} unresolved webhook(s) in 24h",
                candidates.Count, failed, stuck, unresolved);
        }

        // B12: the residual drift the pass left, and the ledger's unattributed rows, are reported and
        // (throttled) alerted — an operator sees them rather than having to read per-organization debug logs.
        long unattributed = await CountUnattributedUsageAsync(nowUtc, ct);
        BillingDriftReport report = new(candidates.Count, findings, unattributed);
        BillingWebhookDiagnostics.ReportDrift(report.Findings.Count, unattributed);
        await ReportDriftAsync(nowUtc, report, ct);

        await AlertOnWebhookFailuresAsync(nowUtc, ct);
        return new BillingSweepResult(candidates.Count, failed, stuck, unresolved);
    }

    /// <summary>The unattributed <c>usage_events</c> of the last <see cref="UsageAttributionWindow"/> (B12).</summary>
    private async Task<long> CountUnattributedUsageAsync(DateTime nowUtc, CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        using IDisposable system = scope.ServiceProvider.GetService<ITenantContext>().System();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        UsageAttributionReport report = await BillingUsageAttribution
            .QueryAsync(db, nowUtc - UsageAttributionWindow, nowUtc, ct).ConfigureAwait(false);
        return report.UnattributedEvents;
    }

    /// <summary>
    /// B12: logs the pass's named drift findings and unattributed-<c>usage_events</c> count, and pushes one
    /// <c>billing_drift</c> alert when there is either — at most once per <see cref="FailureAlertInterval"/> per
    /// process, through the same sender the webhook-failure alert uses. Best effort; never throws.
    /// </summary>
    private async Task ReportDriftAsync(DateTime nowUtc, BillingDriftReport report, CancellationToken ct)
    {
        if (report.HasDrift)
        {
            logger.LogWarning(BillingWebhookDiagnostics.DriftDetected,
                "Billing drift ({Candidates} organization(s) checked): {Findings} finding(s) [{Classes}] not resolved by the reconcile",
                report.Candidates, report.Findings.Count, report.DescribeClasses());
        }

        if (report.UnattributedUsageEvents > 0)
        {
            logger.LogWarning(BillingWebhookDiagnostics.UsageUnattributed,
                "Billing ledger: {Unattributed} usage_event(s) in the last {Hours}h have no attributable organization or user",
                report.UnattributedUsageEvents, (int)UsageAttributionWindow.TotalHours);
        }

        if (!report.HasDrift && report.UnattributedUsageEvents == 0) return;
        if (_lastDriftAlertAt is { } last && nowUtc - last < FailureAlertInterval) return;

        _lastDriftAlertAt = nowUtc;
        string classes = report.HasDrift ? $" [{report.DescribeClasses()}]" : string.Empty;
        string detail = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Billing reconciliation over {report.Candidates} organization(s): {report.Findings.Count} drift finding(s){classes}; {report.UnattributedUsageEvents} usage_event(s) in the last {(int)UsageAttributionWindow.TotalHours}h have no attributable organization or user");

        logger.LogWarning(BillingWebhookDiagnostics.DriftDetected,
            "Billing alert ({Scope}): {Detail}", BillingDriftScope, detail);
        SpendReportDiagnostics.AlertsRaised.Add(1, new KeyValuePair<string, object?>("scope", BillingDriftScope));

        using IServiceScope scope = scopeFactory.CreateScope();
        SpendReportOptions? alerting = scope.ServiceProvider.GetService<IOptions<SpendReportOptions>>()?.Value;
        await SpendAlertSender.PostAsync(
            scope.ServiceProvider.GetService<IHttpClientFactory>(),
            alerting?.AlertWebhookUrl,
            [new SpendReportAlert(BillingDriftScope, null, 0m, 0m, nowUtc - UsageAttributionWindow, nowUtc, detail)],
            logger,
            ct);
    }

    /// <summary>
    /// QA-2: signature and processing failures are pushed, not only metered. One <c>billing_webhooks</c> alert when
    /// either process total grew since the previous alert (since this sweeper started, for the first), at most once per
    /// <see cref="FailureAlertInterval"/>; counts that grow meanwhile go into the next alert. Best effort.
    /// </summary>
    private async Task AlertOnWebhookFailuresAsync(DateTime nowUtc, CancellationToken ct)
    {
        if (_alertedSignatureFailures < 0)
        {
            // The first pass: count from here (the process has just started; its totals are what they are).
            _alertedSignatureFailures = 0;
            _alertedProcessingFailures = 0;
            _failureCountsSince = nowUtc;
        }

        long signature = _failureTotals.SignatureFailures;
        long processing = _failureTotals.ProcessingFailures;
        long newSignature = signature - _alertedSignatureFailures;
        long newProcessing = processing - _alertedProcessingFailures;
        if (newSignature <= 0 && newProcessing <= 0) return;
        if (_lastFailureAlertAt is { } last && nowUtc - last < FailureAlertInterval) return;

        DateTime since = _failureCountsSince ?? nowUtc;
        string detail = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Billing webhooks since {since:O}: {newSignature} signature failure(s), {newProcessing} processing failure(s) since the previous alert (this instance)");
        logger.LogWarning(BillingWebhookDiagnostics.WebhookFailuresAlert,
            "Billing alert ({Scope}): {SignatureFailures} webhook signature failure(s) and {ProcessingFailures} processing failure(s) since {Since:O} on this instance",
            DailySpendReportService.BillingWebhooksScope, newSignature, newProcessing, since);
        SpendReportDiagnostics.AlertsRaised.Add(1, new KeyValuePair<string, object?>("scope", DailySpendReportService.BillingWebhooksScope));

        _alertedSignatureFailures = signature;
        _alertedProcessingFailures = processing;
        _lastFailureAlertAt = nowUtc;
        _failureCountsSince = nowUtc;

        using IServiceScope scope = scopeFactory.CreateScope();
        SpendReportOptions? alerting = scope.ServiceProvider.GetService<IOptions<SpendReportOptions>>()?.Value;
        await SpendAlertSender.PostAsync(
            scope.ServiceProvider.GetService<IHttpClientFactory>(),
            alerting?.AlertWebhookUrl,
            [new SpendReportAlert(DailySpendReportService.BillingWebhooksScope, null, 0m, 0m, since, nowUtc, detail)],
            logger,
            ct);
    }

    /// <summary>The most a failing watch waits between retries (<see cref="RetryDelay"/>).</summary>
    public static readonly TimeSpan MaxFailureBackoff = TimeSpan.FromHours(1);

    /// <summary>
    /// How long after its last check a watch that is still due is retried: <paramref name="baseDelay"/> (the
    /// sweep interval; a checkout that found nothing live: its recheck) after a check that did not fail, else
    /// <c>min(SweepInterval * 2^(failures - 1), 1 h)</c>, never below <paramref name="baseDelay"/>.
    /// </summary>
    public static TimeSpan RetryDelay(int consecutiveFailures, TimeSpan sweepInterval, TimeSpan baseDelay)
    {
        if (consecutiveFailures <= 0) return baseDelay;
        // 2^12 sweep intervals is already far past the cap for any sane interval; it keeps the shift in range.
        int exponent = Math.Min(consecutiveFailures - 1, 12);
        double seconds = Math.Min(sweepInterval.TotalSeconds * (1L << exponent), MaxFailureBackoff.TotalSeconds);
        TimeSpan backoff = TimeSpan.FromSeconds(seconds);
        return backoff > baseDelay ? backoff : baseDelay;
    }

    private async Task<List<Guid>> SelectCandidatesAsync(
        DateTime nowUtc, DateTime stuckBefore, DateTime recheckBefore, BillingReconcileOptions settings, CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        using IDisposable system = scope.ServiceProvider.GetService<ITenantContext>().System();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        int max = settings.MaxPerSweep;
        DateTime retryBefore = nowUtc - settings.SweepInterval;
        DateTime checkoutRecheckBefore = nowUtc - settings.CheckoutRecheck;
        string nothing = BillingReconcileBacklog.AwaitingNothingOutcome;

        // (a) A change whose webhook is overdue (an overdue renewal/cancellation already is), not checked since the
        // change or not within the last sweep. The SQL filter is coarse (Npgsql and Sqlite both translate it):
        // watches without failures first, oldest first, in a window of 4 * MaxPerSweep. The exact rules run below.
        // Checkouts are not in it: they have their own query, so abandoned ones cannot fill this window (S-2).
        var window = await db.BillingReconcileWatches.AsNoTracking()
            .Where(w => w.ChangeRequestedAt != null
                && w.ChangeKind != BillingReconciler.CheckoutKind
                && (w.ChangeRequestedAt <= stuckBefore || w.ChangeKind == BillingReconciler.OverdueKind))
            .Where(w => w.LastCheckedAt == null
                || w.LastCheckedAt < w.ChangeRequestedAt
                || w.LastCheckedAt < w.UpdatedAt
                || w.LastCheckedAt <= retryBefore)
            .OrderBy(w => w.ConsecutiveFailures == 0 ? 0 : 1)
            .ThenBy(w => w.ChangeRequestedAt)
            .ThenBy(w => w.OrganizationId)
            .Take(max * 4)
            .Select(w => new { w.OrganizationId, w.ChangeKind, w.ChangeRequestedAt, w.LastCheckedAt, w.UpdatedAt, w.LastOutcome, w.ConsecutiveFailures })
            .ToListAsync(ct);

        // (a') A started checkout whose subscription.created may be lost: rechecked every CheckoutRecheck after a check
        // that found nothing live, else on the change-watch cadence (QA-4). At most a quarter of the pass.
        int checkoutQuota = Math.Max(1, max / 4);
        var checkoutWindow = await db.BillingReconcileWatches.AsNoTracking()
            .Where(w => w.ChangeRequestedAt != null
                && w.ChangeKind == BillingReconciler.CheckoutKind
                && w.ChangeRequestedAt <= stuckBefore)
            .Where(w => w.LastCheckedAt == null
                || w.LastCheckedAt < w.ChangeRequestedAt
                || w.LastCheckedAt < w.UpdatedAt
                || (w.LastOutcome == nothing && w.LastCheckedAt <= checkoutRecheckBefore)
                || (w.LastOutcome != nothing && w.LastCheckedAt <= retryBefore))
            .OrderBy(w => w.ConsecutiveFailures == 0 ? 0 : 1)
            .ThenBy(w => w.ChangeRequestedAt)
            .ThenBy(w => w.OrganizationId)
            .Take(checkoutQuota * 4)
            .Select(w => new { w.OrganizationId, w.ChangeKind, w.ChangeRequestedAt, w.LastCheckedAt, w.UpdatedAt, w.LastOutcome, w.ConsecutiveFailures })
            .ToListAsync(ct);

        // A watch checked since its change waits its retry delay: a checkout that found nothing live its
        // CheckoutRecheck; a failing watch backs off exponentially (up to 1 h) so a few broken orgs cannot take every slot.
        List<(Guid Org, DateTime Since, bool Failing)> due = [];
        int checkouts = 0;
        foreach (var w in window.Concat(checkoutWindow))
        {
            bool checkout = w.ChangeKind == BillingReconciler.CheckoutKind;
            bool checkedSinceChange = w.LastCheckedAt is { } last && last >= w.ChangeRequestedAt && last >= w.UpdatedAt;
            if (checkedSinceChange)
            {
                TimeSpan baseDelay = checkout && w.LastOutcome == nothing ? settings.CheckoutRecheck : settings.SweepInterval;
                TimeSpan delay = RetryDelay(w.ConsecutiveFailures, settings.SweepInterval, baseDelay);
                if (w.LastCheckedAt > nowUtc - delay) continue;
            }

            if (checkout && ++checkouts > checkoutQuota) continue;
            due.Add((w.OrganizationId, w.ChangeRequestedAt!.Value, w.ConsecutiveFailures > 0));
        }

        // (b)/(c) back off through the watch: none with a change still due (rule (a) owns those) and none
        // checked within RecheckAfter.
        IQueryable<Subscription> unwatched = db.Subscriptions.AsNoTracking()
            .Where(s => !db.BillingReconcileWatches.Any(w => w.OrganizationId == s.OrganizationId
                && (w.ChangeRequestedAt != null || (w.LastCheckedAt != null && w.LastCheckedAt > recheckBefore))));

        // (b) A renewal the provider never confirmed.
        var renewals = await unwatched
            .Where(s => s.Provider != null && RenewingStatuses.Contains(s.Status) && s.PeriodEnd != null && s.PeriodEnd <= stuckBefore)
            .OrderBy(s => s.PeriodEnd)
            .Take(max)
            .Select(s => new { s.OrganizationId, s.PeriodEnd })
            .ToListAsync(ct);
        due.AddRange(renewals.Select(s => (s.OrganizationId, s.PeriodEnd!.Value, false)));

        // (c) A cancellation past its effective date that never arrived.
        var cancels = await unwatched
            .Where(s => s.Provider != null && s.CancelAtPeriodEnd && s.Status != SubscriptionStatus.Canceled
                && s.CancelEffectiveAt != null && s.CancelEffectiveAt <= stuckBefore)
            .OrderBy(s => s.CancelEffectiveAt)
            .Take(max)
            .Select(s => new { s.OrganizationId, s.CancelEffectiveAt })
            .ToListAsync(ct);
        due.AddRange(cancels.Select(s => (s.OrganizationId, s.CancelEffectiveAt!.Value, false)));

        // Not failing before failing, then oldest first.
        return due
            .GroupBy(d => d.Org)
            .Select(g => (Org: g.Key, Since: g.Min(d => d.Since), Failing: g.All(d => d.Failing)))
            .OrderBy(d => d.Failing)
            .ThenBy(d => d.Since)
            .ThenBy(d => d.Org)
            .Take(max)
            .Select(d => d.Org)
            .ToList();
    }

    /// <summary>Counts the backlog for the gauges and logs each still-stuck watch (org id only, no customer data).</summary>
    private async Task<(long Stuck, long Unresolved)> ReportBacklogAsync(DateTime nowUtc, DateTime stuckBefore, CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        using IDisposable system = scope.ServiceProvider.GetService<ITenantContext>().System();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();

        var stuck = await db.BillingReconcileWatches.AsNoTracking()
            .Where(BillingReconcileBacklog.IsStuck(stuckBefore))
            .OrderBy(w => w.ChangeRequestedAt)
            .Select(w => new { w.OrganizationId, w.ChangeKind, w.ChangeRequestedAt, w.LastOutcome, w.ConsecutiveFailures })
            .ToListAsync(ct);

        DateTime since24h = nowUtc.AddHours(-24);
        long unresolved = await db.BillingWebhookEvents.AsNoTracking()
            .LongCountAsync(e => e.Outcome == nameof(BillingEventOutcome.Unresolved) && e.ReceivedAt >= since24h
                && !e.EventId.StartsWith(BillingReconciler.ConfirmEventIdPrefix), ct);

        BillingWebhookDiagnostics.ReportBacklog(stuck.Count, unresolved);

        foreach (var watch in stuck)
        {
            logger.LogWarning(BillingWebhookDiagnostics.ChangeStuck,
                "Organization {Org}'s {Kind} change requested at {Since:O} is still waiting on its webhook (last check: {Outcome}, {Failures} failure(s) in a row)",
                watch.OrganizationId, watch.ChangeKind, watch.ChangeRequestedAt, watch.LastOutcome ?? "never", watch.ConsecutiveFailures);
        }

        return (stuck.Count, unresolved);
    }
}
