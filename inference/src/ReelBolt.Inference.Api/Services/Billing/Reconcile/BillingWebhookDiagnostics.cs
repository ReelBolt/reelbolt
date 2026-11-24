using System.Diagnostics.Metrics;

namespace ReelBolt.Inference.Api.Services.Billing.Reconcile;

/// <summary>
/// Webhook and reconcile telemetry. Tags are low-cardinality words only — never an organization,
/// subscription or event id (docs/observability.md). Registered through <c>AddReelBoltTelemetry</c>.
/// </summary>
public static class BillingWebhookDiagnostics
{
    /// <summary>Meter name; the Inference API's telemetry registration adds it with <c>AddMeter</c>.</summary>
    public const string MeterName = "ReelBolt.Inference.Billing.Webhooks";

    private static readonly Meter Meter = new(MeterName);

    private static long _stuckWatches;
    private static long _unresolved24h;
    private static long _driftFindings;
    private static long _unattributedUsage;

    /// <summary>Webhooks rejected by signature verification. Tag: <c>provider</c>.</summary>
    public static readonly Counter<long> SignatureFailures =
        Meter.CreateCounter<long>("reelbolt.billing.webhook.signature_failures", "webhooks", "Webhooks rejected by signature verification, tagged with provider");

    /// <summary>Webhooks whose processing threw (answered 500, the provider retries). Tags: <c>provider</c>, <c>kind</c>.</summary>
    public static readonly Counter<long> ProcessingFailures =
        Meter.CreateCounter<long>("reelbolt.billing.webhook.processing_failures", "webhooks", "Webhooks whose processing failed, tagged with provider and event kind");

    /// <summary>Every processed webhook. Tags: <c>provider</c>, <c>outcome</c> (<c>applied|duplicate|stale|unresolved</c>).</summary>
    public static readonly Counter<long> Outcomes =
        Meter.CreateCounter<long>("reelbolt.billing.webhook.outcomes", "webhooks", "Processed webhooks, tagged with provider and outcome");

    /// <summary>Every reconcile run. Tags: <c>trigger</c> (<c>lazy|sweep|admin</c>), <c>outcome</c> (<see cref="ReconcileOutcome"/>, lower case).</summary>
    public static readonly Counter<long> ReconcileRuns =
        Meter.CreateCounter<long>("reelbolt.billing.reconcile.runs", "runs", "Reconcile runs, tagged with trigger and outcome");

    /// <summary>Watches whose change is older than the stuck threshold, as last reported by <see cref="ReportBacklog"/>.</summary>
    public static readonly ObservableGauge<long> StuckWatches =
        Meter.CreateObservableGauge("reelbolt.billing.reconcile.stuck_watches", () => Interlocked.Read(ref _stuckWatches), "watches", "Subscription changes still waiting on their webhook past the stuck threshold");

    /// <summary>Webhook rows of the last 24 hours that ended <c>Unresolved</c>, as last reported by <see cref="ReportBacklog"/>.</summary>
    public static readonly ObservableGauge<long> Unresolved24h =
        Meter.CreateObservableGauge("reelbolt.billing.webhook.unresolved_24h", () => Interlocked.Read(ref _unresolved24h), "webhooks", "Webhooks of the last 24 hours that could not be resolved to an organization or plan");

    /// <summary>Residual Paddle-vs-ledger drift findings the last sweep left, as reported by <see cref="ReportDrift"/>.</summary>
    public static readonly ObservableGauge<long> DriftFindings =
        Meter.CreateObservableGauge("reelbolt.billing.reconcile.drift_findings", () => Interlocked.Read(ref _driftFindings), "findings", "Residual Paddle-vs-ledger drift findings from the last sweep (B12)");

    /// <summary><c>usage_events</c> of the last 24 hours with no attributable organization or user, as reported by <see cref="ReportDrift"/>.</summary>
    public static readonly ObservableGauge<long> UnattributedUsage =
        Meter.CreateObservableGauge("reelbolt.billing.usage.unattributed", () => Interlocked.Read(ref _unattributedUsage), "events", "usage_events of the last 24 hours with no attributable organization or user (B12)");

    /// <summary>Log event: a reconcile applied the provider's state (a webhook was lost).</summary>
    public static readonly EventId ReconcileHealed = new(7101, "billing.reconcile_healed");

    /// <summary>Log event: a reconcile could not read the provider or could not apply its state.</summary>
    public static readonly EventId ReconcileFailed = new(7102, "billing.reconcile_failed");

    /// <summary>Log event: a change's webhook is overdue (the provider changed longer ago than the stuck threshold).</summary>
    public static readonly EventId ChangeStuck = new(7103, "billing.change_stuck");

    /// <summary>Log event: a webhook failed signature verification.</summary>
    public static readonly EventId WebhookSignatureFailed = new(7104, "billing.webhook_signature_failed");

    /// <summary>Log event: a webhook's processing threw.</summary>
    public static readonly EventId WebhookProcessingFailed = new(7105, "billing.webhook_processing_failed");

    /// <summary>Log event: a webhook was recorded but could not be resolved.</summary>
    public static readonly EventId WebhookUnresolved = new(7106, "billing.webhook_unresolved");

    /// <summary>Log event: a correctly signed webhook was refused before processing (a reserved event id).</summary>
    public static readonly EventId WebhookRejected = new(7107, "billing.webhook_rejected");

    /// <summary>
    /// Log event (Warning): the sweep pushed an alert because webhook signature or processing failures grew since its
    /// previous alert (at most one per hour per process).
    /// </summary>
    public static readonly EventId WebhookFailuresAlert = new(7108, "billing.webhook_failures_alert");

    /// <summary>
    /// Log event (Warning): the sweep found residual Paddle-vs-ledger drift it did not resolve (B12). The message
    /// names the drift classes.
    /// </summary>
    public static readonly EventId DriftDetected = new(7109, "billing.drift_detected");

    /// <summary>
    /// Log event (Warning): the ledger holds <c>usage_events</c> with no attributable organization or user (B12).
    /// </summary>
    public static readonly EventId UsageUnattributed = new(7110, "billing.usage_unattributed");

    /// <summary>
    /// This process's running totals of signature and processing failures (Interlocked), fed by
    /// <see cref="RecordSignatureFailure"/> and <see cref="RecordProcessingFailure"/>.
    /// </summary>
    public static BillingWebhookFailureTotals Totals { get; } = new();

    /// <summary>A webhook failed signature verification: the counter (tag <c>provider</c>) and the process total.</summary>
    public static void RecordSignatureFailure(string provider)
    {
        SignatureFailures.Add(1, new KeyValuePair<string, object?>("provider", provider));
        Totals.RecordSignatureFailure();
    }

    /// <summary>A webhook's processing threw: the counter (tags <c>provider</c>, <c>kind</c>) and the process total.</summary>
    public static void RecordProcessingFailure(string provider, string kind)
    {
        ProcessingFailures.Add(1,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("kind", kind));
        Totals.RecordProcessingFailure();
    }

    /// <summary>Sets the two backlog gauges (the sweep and the daily report call this after counting).</summary>
    public static void ReportBacklog(long stuck, long unresolved)
    {
        Interlocked.Exchange(ref _stuckWatches, stuck);
        Interlocked.Exchange(ref _unresolved24h, unresolved);
    }

    /// <summary>Sets B12's two gauges (the sweep calls this after building its drift report).</summary>
    public static void ReportDrift(long driftFindings, long unattributedUsage)
    {
        Interlocked.Exchange(ref _driftFindings, driftFindings);
        Interlocked.Exchange(ref _unattributedUsage, unattributedUsage);
    }

    /// <summary>Counts one reconcile run.</summary>
    public static void RecordRun(ReconcileTrigger trigger, ReconcileOutcome outcome)
        => ReconcileRuns.Add(1,
            new KeyValuePair<string, object?>("trigger", trigger.ToString().ToLowerInvariant()),
            new KeyValuePair<string, object?>("outcome", outcome.ToString().ToLowerInvariant()));
}
