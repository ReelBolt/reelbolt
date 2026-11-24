using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Reconcile;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Inference.Api.Services.Background;

/// <summary>
/// Tunables of the daily spend report (config section <c>Billing:SpendReport</c>). Off unless
/// <see cref="Enabled"/>: self-host has no platform budget to watch, and a report nobody asked for is
/// noise in the log.
/// </summary>
public sealed record SpendReportOptions
{
    public const string SectionName = "Billing:SpendReport";

    /// <summary>Run the report at all. Default false (self-host untouched).</summary>
    public bool Enabled { get; init; }

    /// <summary>How often to report, in hours. The plan says daily; the interval is configurable so a test is not a day long.</summary>
    public int IntervalHours { get; init; } = 24;

    /// <summary>Wait before the first report, so a restarting service does not report every time it boots.</summary>
    public int InitialDelaySeconds { get; init; } = 300;

    /// <summary>Platform-wide alert threshold over the window, in USD. Zero means no platform alert.</summary>
    public decimal DailyThresholdUsd { get; init; }

    /// <summary>Per-organisation alert threshold over the window, in USD. Zero means no per-organisation alert.</summary>
    public decimal OrganizationThresholdUsd { get; init; }

    /// <summary>How many organisations the report names, largest spend first.</summary>
    public int TopOrganizations { get; init; } = 10;

    /// <summary>
    /// Where an alert is POSTed, if anywhere. Empty means "log only", which is the honest default: this
    /// platform has no pager integration, and a webhook URL is deployment-specific config.
    /// </summary>
    public string? AlertWebhookUrl { get; init; }
}

/// <summary>One organization's spend in the reported window.</summary>
public sealed record SpendReportOrganization(Guid OrganizationId, decimal CostUsd, long CreditsMillis, long Events);

/// <summary>
/// What the ledger says about one window. <see cref="UnattributedEvents"/> is a separate count rather
/// than folded into a total: an event with no organization is a metering bug (B3b's own invariant), and
/// hiding it inside a number would be exactly how it stops being noticed.
/// </summary>
public sealed record SpendReportWindow(
    DateTime From,
    DateTime To,
    decimal TotalCostUsd,
    long TotalCreditsMillis,
    long TotalEvents,
    long UnattributedEvents,
    IReadOnlyList<SpendReportOrganization> TopOrganizations,
    BillingWebhookReport? BillingWebhooks = null)
{
    /// <summary>
    /// every enabled PLATFORM capability with no effective price for a unit it meters
    /// (<see cref="PriceCoverage.FindAsync"/>), as of the end of the window. Reported as an error section: a gap is a
    /// model nobody may run (the resolver refuses it) or, before that rule, usage metered at zero credits.
    /// </summary>
    public IReadOnlyList<PriceCoverageGap> PriceGaps { get; init; } = [];

    /// <summary>Platform usage events in the window recorded without a price (<c>usage_events.unpriced</c>).</summary>
    public long UnpricedEvents { get; init; }
}

/// <summary>
/// The billing-webhook health of one report window. <see cref="Received"/> and <see cref="Unresolved"/> count the
/// provider's own webhooks received in the window; <see cref="Reconciled"/> the reconciler's synthetic events
/// (<c>reconcile:*</c>) that ended Applied in it, i.e. lost webhooks healed. <see cref="Stuck"/> (changes waiting
/// past the stuck threshold, <see cref="BillingReconcileBacklog.IsStuck"/>: an abandoned checkout is not one) and <see cref="Failing"/> (watches whose reconcile keeps failing) are the current backlog.
/// <see cref="SignatureFailures"/> and <see cref="ProcessingFailures"/> are this instance's webhooks rejected by signature
/// verification and whose processing failed since its previous report (process-local, <see cref="BillingWebhookFailureTotals"/>).
/// </summary>
public sealed record BillingWebhookReport(long Received, long Unresolved, long Reconciled, long Stuck, int StuckAfterMinutes, long Failing, long SignatureFailures = 0, long ProcessingFailures = 0);

/// <summary>An alert the report raised, in the shape the webhook receives.</summary>
/// <param name="Detail">What a non-spend alert (scope <c>billing_webhooks</c>) is about; null on a spend alert.</param>
public sealed record SpendReportAlert(string Scope, Guid? OrganizationId, decimal CostUsd, decimal ThresholdUsd, DateTime From, DateTime To, string? Detail = null);

/// <summary>
/// POSTs alerts to <see cref="SpendReportOptions.AlertWebhookUrl"/> — the one sender the daily report and the billing
/// reconcile sweep share. Best effort: no URL or no <see cref="IHttpClientFactory"/> is a no-op, and a failing endpoint
/// is logged, never thrown (only cancellation is).
/// </summary>
public static class SpendAlertSender
{
    /// <summary>The named <see cref="HttpClient"/> the alerts are sent with.</summary>
    public const string HttpClientName = nameof(DailySpendReportService);

    public static async Task PostAsync(IHttpClientFactory? httpClientFactory, string? alertWebhookUrl, IReadOnlyList<SpendReportAlert> alerts, ILogger logger, CancellationToken ct)
    {
        if (alerts.Count == 0 || string.IsNullOrWhiteSpace(alertWebhookUrl) || httpClientFactory is null)
        {
            return;
        }

        try
        {
            using HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(10);
            using HttpResponseMessage response = await client.PostAsJsonAsync(alertWebhookUrl, alerts, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("The alert webhook answered {Status}", (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Posting the alert to the configured webhook failed");
        }
    }
}

/// <summary>Metrics for the spend report and its alerts (F11).</summary>
public static class SpendReportDiagnostics
{
    /// <summary>Meter name; the Inference API's telemetry registration adds it with <c>AddMeter</c>.</summary>
    public const string MeterName = "ReelBolt.Inference.Billing";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>One per threshold crossing, tagged with its scope. Zero is what a healthy day looks like.</summary>
    public static readonly Counter<long> AlertsRaised =
        Meter.CreateCounter<long>("reelbolt.billing.spend_alerts", "alerts", "Spend thresholds crossed by the daily report, tagged with scope");
}

/// <summary>
/// F11 step 6: a periodic report of what the platform actually spent, off the B ledger, with an alert
/// when a threshold is crossed.
///
/// <para>
/// <b>Why the ledger and not the provider.</b> <c>usage_events</c> is priced at write time (B4/B6), so
/// it answers "what did we spend, on what, for whom" without a provider API — and it is the only source
/// that answers it per organisation. It is NOT a substitute for the provider-side budget alerts
/// (go-live §5.1/5.2), which are the ones that fire when the ledger is itself broken or lagging; the
/// two are complements, and this one is the number an operator reads.
/// </para>
///
/// <para>
/// <b>What it does when a threshold is crossed.</b> Logs at Warning, increments
/// <see cref="SpendReportDiagnostics.AlertsRaised"/> and POSTs <see cref="SpendReportAlert"/> to
/// <see cref="SpendReportOptions.AlertWebhookUrl"/> when one is configured. All three are best effort:
/// a webhook that is down must never take the report — or the service — down with it.
/// </para>
///
/// <para>
/// <b>What it deliberately does not do.</b> It does not pause anything. F11's automatic brake is the
/// credit gate (B6/B7) and the operator's is <c>compute_paused</c>; a report that silently killed work
/// on its own estimate would be an outage with no human in it.
/// </para>
/// </summary>
public sealed class DailySpendReportService : BackgroundService
{
    /// <summary>Alert scope of the billing-webhook health check (lost, unresolved or unreconcilable webhooks).</summary>
    public const string BillingWebhooksScope = "billing_webhooks";

    /// <summary>Event-id prefix of the reconciler's synthetic webhook events.</summary>
    private const string SyntheticEventPrefix = "reconcile:";
    private const string ConfirmEventPrefix = "confirm:";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<SpendReportOptions> _options;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILogger<DailySpendReportService> _logger;
    private readonly TimeProvider _clock;
    private readonly BillingWebhookFailureTotals _failureTotals;
    private long _reportedSignatureFailures;
    private long _reportedProcessingFailures;

    public DailySpendReportService(
        IServiceScopeFactory scopeFactory,
        IOptions<SpendReportOptions> options,
        ILogger<DailySpendReportService> logger,
        IHttpClientFactory? httpClientFactory = null,
        TimeProvider? clock = null,
        BillingWebhookFailureTotals? failureTotals = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _clock = clock ?? TimeProvider.System;
        _failureTotals = failureTotals ?? BillingWebhookDiagnostics.Totals;
        // The first report counts from this instance's start.
        _reportedSignatureFailures = _failureTotals.SignatureFailures;
        _reportedProcessingFailures = _failureTotals.ProcessingFailures;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SpendReportOptions options = _options.Value;
        if (!options.Enabled)
        {
            _logger.LogDebug("The daily spend report is disabled (Billing:SpendReport:Enabled)");
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.InitialDelaySeconds)), stoppingToken);

            using PeriodicTimer timer = new(TimeSpan.FromHours(Math.Clamp(options.IntervalHours, 1, 24 * 7)));
            do
            {
                try
                {
                    await ReportOnceAsync(_clock.GetUtcNow(), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A report is an observation, never a dependency: a ledger that is briefly
                    // unreachable must not stop the service, and the next window covers it.
                    _logger.LogError(ex, "The spend report could not be produced");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>
    /// Produces (and alerts on) one window ending at <paramref name="now"/>. Public and clock-injected
    /// so a test runs a day in milliseconds; returns the window it reported.
    /// </summary>
    public async Task<SpendReportWindow> ReportOnceAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        SpendReportOptions options = _options.Value;
        DateTime to = now.UtcDateTime;
        DateTime from = to.AddHours(-Math.Clamp(options.IntervalHours, 1, 24 * 7));

        SpendReportWindow window = await BuildWindowAsync(from, to, options, ct);
        // Signature and processing failures never reach the database: this instance's totals since its last report.
        long signatureTotal = _failureTotals.SignatureFailures;
        long processingTotal = _failureTotals.ProcessingFailures;
        window = window with
        {
            BillingWebhooks = window.BillingWebhooks! with
            {
                SignatureFailures = signatureTotal - _reportedSignatureFailures,
                ProcessingFailures = processingTotal - _reportedProcessingFailures
            }
        };
        _reportedSignatureFailures = signatureTotal;
        _reportedProcessingFailures = processingTotal;
        BillingWebhookReport webhooks = window.BillingWebhooks!;

        _logger.LogInformation(
            "Spend report {From:o}..{To:o}: {TotalUsd} USD, {Credits} millicredits, {Events} usage events, {Unattributed} unattributed; top {TopCount} of {OrgCount} organizations: {TopOrgs}",
            window.From,
            window.To,
            window.TotalCostUsd.ToString("0.####", CultureInfo.InvariantCulture),
            window.TotalCreditsMillis,
            window.TotalEvents,
            window.UnattributedEvents,
            window.TopOrganizations.Count,
            window.TopOrganizations.Count,
            string.Join(", ", window.TopOrganizations.Select(o => o.OrganizationId + "=" + o.CostUsd.ToString("0.####", CultureInfo.InvariantCulture))));

        if (window.PriceGaps.Count > 0 || window.UnpricedEvents > 0)
        {
            // the error section. Each gap is an enabled platform model with no price in force: the resolver
            // will not run it, and any unpriced usage that still reached the ledger was charged its hold, not its cost.
            _logger.LogError(
                "Price coverage: {GapCount} enabled platform capabilities have no price in force and {UnpricedEvents} usage events in {From:o}..{To:o} were recorded unpriced: {Gaps}",
                window.PriceGaps.Count,
                window.UnpricedEvents,
                window.From,
                window.To,
                string.Join("; ", window.PriceGaps.Select(g =>
                    $"{g.ProviderName} / {g.ModelDisplayName} ({g.Kind} {g.Capability}, {g.CapabilityId}) missing {string.Join(", ", g.MissingUnits)}")));
        }

        int hours = (int)Math.Round((window.To - window.From).TotalHours);
        _logger.LogInformation(
            "Billing webhooks over {Hours}h: {Received} received, {Unresolved} unresolved, {Reconciled} reconciled, {Stuck} waiting past {StuckAfterMinutes} min, {Failing} failing to reconcile; {SignatureFailures} signature failures, {ProcessingFailures} processing failures since the last report (this instance)",
            hours, webhooks.Received, webhooks.Unresolved, webhooks.Reconciled, webhooks.Stuck, webhooks.StuckAfterMinutes, webhooks.Failing, webhooks.SignatureFailures, webhooks.ProcessingFailures);

        List<SpendReportAlert> alerts = [];
        if (options.DailyThresholdUsd > 0 && window.TotalCostUsd > options.DailyThresholdUsd)
        {
            alerts.Add(new SpendReportAlert("platform", null, window.TotalCostUsd, options.DailyThresholdUsd, window.From, window.To));
        }

        if (options.OrganizationThresholdUsd > 0)
        {
            foreach (SpendReportOrganization org in window.TopOrganizations.Where(o => o.CostUsd > options.OrganizationThresholdUsd))
            {
                alerts.Add(new SpendReportAlert("organization", org.OrganizationId, org.CostUsd, options.OrganizationThresholdUsd, window.From, window.To));
            }
        }

        // Lost, rejected or unprocessable provider webhooks: a customer's plan may not match what they pay for.
        if (webhooks.Stuck > 0 || webhooks.Unresolved > 0 || webhooks.Failing > 0 || webhooks.SignatureFailures > 0 || webhooks.ProcessingFailures > 0)
        {
            alerts.Add(new SpendReportAlert(BillingWebhooksScope, null, 0m, 0m, window.From, window.To,
                string.Create(CultureInfo.InvariantCulture,
                    $"Billing webhooks over {hours}h: {webhooks.Received} received, {webhooks.Unresolved} unresolved, {webhooks.Reconciled} reconciled, {webhooks.Stuck} waiting past {webhooks.StuckAfterMinutes} min, {webhooks.Failing} failing to reconcile; {webhooks.SignatureFailures} signature failures, {webhooks.ProcessingFailures} processing failures since the last report (this instance)")));
        }

        foreach (SpendReportAlert alert in alerts)
        {
            SpendReportDiagnostics.AlertsRaised.Add(1, new KeyValuePair<string, object?>("scope", alert.Scope));
            if (alert.Detail is { } detail)
            {
                _logger.LogWarning("Billing alert ({Scope}) over {From:o}..{To:o}: {Detail}", alert.Scope, alert.From, alert.To, detail);
            }
            else
            {
                _logger.LogWarning(
                    "Spend alert ({Scope}): {CostUsd} USD over {From:o}..{To:o} exceeds the {ThresholdUsd} USD threshold",
                    alert.Scope, alert.CostUsd, alert.From, alert.To, alert.ThresholdUsd);
            }
        }

        await SpendAlertSender.PostAsync(_httpClientFactory, options.AlertWebhookUrl, alerts, _logger, ct);

        return window;
    }

    /// <summary>One pass over the ledger. Read-only and <c>IgnoreQueryFilters</c>: the report is platform-wide by definition.</summary>
    private async Task<SpendReportWindow> BuildWindowAsync(DateTime from, DateTime to, SpendReportOptions options, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();

        IQueryable<UsageEvent> window = db.UsageEvents.AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.OccurredAt >= from && e.OccurredAt < to);

        // Aggregated in the database: a day of usage events is not something to load into memory to
        // add up. The aggregates over a nullable projection are what make an empty window a zero
        // rather than a throw, on both providers.
        decimal totalCostUsd = await window.Select(e => (decimal?)e.CostUsd).SumAsync(ct) ?? 0m;
        long totalCreditsMillis = await window.Select(e => (long?)e.CreditsMillis).SumAsync(ct) ?? 0L;
        long totalEvents = await window.LongCountAsync(ct);
        long unattributed = await window.LongCountAsync(e => e.Source == UsageSource.Unattributed, ct);

        // Grouped and summed in the database; ranked and cut here. One row per active organisation is
        // a small result set, and ordering a projection built from a GroupBy is the one shape neither
        // provider's translator accepts, so the ranking is deliberately the client's job.
        var byOrganization = await window
            .GroupBy(e => e.OrganizationId)
            .Select(g => new
            {
                OrganizationId = g.Key,
                CostUsd = g.Sum(e => e.CostUsd),
                CreditsMillis = g.Sum(e => e.CreditsMillis),
                Events = g.Count()
            })
            .ToListAsync(ct);

        List<SpendReportOrganization> top = byOrganization
            .Select(o => new SpendReportOrganization(o.OrganizationId, o.CostUsd, o.CreditsMillis, o.Events))
            .OrderByDescending(o => o.CostUsd)
            .ThenBy(o => o.OrganizationId)
            .Take(Math.Clamp(options.TopOrganizations, 1, 100))
            .ToList();

        long unpriced = await window.LongCountAsync(e => e.Unpriced, ct);
        IReadOnlyList<PriceCoverageGap> gaps = await PriceCoverage.FindAsync(db, to, ct);
        BillingWebhookReport webhooks = await BuildWebhookReportAsync(db, scope.ServiceProvider, from, to, ct);

        return new SpendReportWindow(from, to, totalCostUsd, totalCreditsMillis, totalEvents, unattributed, top, webhooks)
        {
            PriceGaps = gaps,
            UnpricedEvents = unpriced
        };
    }

    /// <summary>
    /// The window's webhook traffic (the provider's own events vs the reconciler's <c>reconcile:*</c> ones) and the
    /// current reconcile backlog. Read-only, platform-wide.
    /// </summary>
    private static async Task<BillingWebhookReport> BuildWebhookReportAsync(
        InferenceApiDbContext db, IServiceProvider services, DateTime from, DateTime to, CancellationToken ct)
    {
        BillingReconcileOptions reconcile = services.GetService<IOptions<BillingReconcileOptions>>()?.Value ?? new BillingReconcileOptions();
        string unresolved = nameof(BillingEventOutcome.Unresolved);
        string applied = nameof(BillingEventOutcome.Applied);

        IQueryable<ReelBolt.Shared.Data.Models.Billing.BillingWebhookEvent> window = db.BillingWebhookEvents.AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.ReceivedAt >= from && e.ReceivedAt < to);
        IQueryable<ReelBolt.Shared.Data.Models.Billing.BillingWebhookEvent> real = window.Where(e => !e.EventId.StartsWith(SyntheticEventPrefix) && !e.EventId.StartsWith(ConfirmEventPrefix));

        long received = await real.LongCountAsync(ct);
        long unresolvedCount = await real.LongCountAsync(e => e.Outcome == unresolved, ct);
        long reconciled = await window.LongCountAsync(e => e.EventId.StartsWith(SyntheticEventPrefix) && e.Outcome == applied, ct);

        DateTime stuckBefore = to - reconcile.StuckAfter;
        IQueryable<ReelBolt.Shared.Data.Models.Billing.BillingReconcileWatch> watches = db.BillingReconcileWatches.AsNoTracking().IgnoreQueryFilters();
        long stuck = await watches.LongCountAsync(BillingReconcileBacklog.IsStuck(stuckBefore), ct);
        long failing = await watches.LongCountAsync(w => w.ConsecutiveFailures > 0, ct);

        return new BillingWebhookReport(received, unresolvedCount, reconciled, stuck, (int)reconcile.StuckAfter.TotalMinutes, failing);
    }
}
