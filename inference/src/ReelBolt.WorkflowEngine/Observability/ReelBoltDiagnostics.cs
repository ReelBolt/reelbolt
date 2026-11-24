using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ReelBolt.WorkflowEngine.Observability;

/// <summary>
/// OpenTelemetry instrumentation for the Workflow Engine.
/// </summary>
public static class ReelBoltDiagnostics
{
    public const string ServiceName = "ReelBolt.WorkflowEngine";

    public static readonly ActivitySource ActivitySource = new(ServiceName);

    private static readonly Meter Meter = new(ServiceName);

    public static readonly UpDownCounter<long> ActiveWorkflows =
        Meter.CreateUpDownCounter<long>("reelbolt.workflows.active", "workflows", "Number of currently executing workflows");

    public static readonly Counter<long> CompletedWorkflows =
        Meter.CreateCounter<long>("reelbolt.workflows.completed", "workflows", "Total completed workflows");

    public static readonly Counter<long> NeedsAttention =
        Meter.CreateCounter<long>("reelbolt.workflow.needs_attention", "workflows", "Runs that finished Passed but need attention");

    public static readonly Histogram<long> StepDuration =
        Meter.CreateHistogram<long>("reelbolt.step.duration_ms", "ms", "Duration of individual step executions");

    /// <summary>Wall-clock duration of one <see cref="Execution.Decision.IDecisionGate.RunShadowAsync"/> call.</summary>
    public static readonly Histogram<long> DecisionLatency =
        Meter.CreateHistogram<long>("reelbolt.decision.latency", "ms", "Duration of a Decision gate shadow run");

    /// <summary>Confidence (0-1) of each individual Decision gate answer, one recorded value per question.</summary>
    public static readonly Histogram<double> DecisionConfidence =
        Meter.CreateHistogram<double>("reelbolt.decision.confidence", "1", "Confidence of a Decision gate answer");

    /// <summary>Incremented once per shadow run in which at least one question came back escalated.</summary>
    public static readonly Counter<long> DecisionEscalations =
        Meter.CreateCounter<long>("reelbolt.decision.escalations", "runs", "Decision gate shadow runs with at least one escalated question");

    /// <summary>Review iterations whose score was capped because the film's measured length missed the requested length.</summary>
    public static readonly Counter<long> ReviewLengthCapped =
        Meter.CreateCounter<long>("reelbolt.review.length_capped", "reviews", "Review scores capped because the film was more than 15 percent off the requested length");

    /// <summary>
    /// Executions that were admitted nowhere, tagged with the reason (F11) — the counter an operator
    /// watches after flipping a switch: it should be zero while every switch is off, and it is the only
    /// signal that distinguishes "the pause is working" from "the pause is silently rejecting
    /// everything", which look identical from the outside.
    /// </summary>
    public static readonly Counter<long> ExecutionsDeferred =
        Meter.CreateCounter<long>("reelbolt.platform.executions_deferred", "executions", "Executions not admitted, tagged with the reason (compute_paused, organization_disabled, ...)");

    /// <summary>
    /// The platform settings store could not be read, so the kill switches are unknown and are treated
    /// as paused (F11). Non-zero means no work is starting and the settings table needs attention.
    /// </summary>
    public static readonly Counter<long> SwitchStoreUnreadable =
        Meter.CreateCounter<long>("reelbolt.platform.switches_unreadable", "reads", "Reads of platform_settings that failed; while any occur, compute is treated as paused");

    /// <summary>
    /// Composition workspace snapshots, tagged <c>outcome</c> = <c>captured</c> | <c>deduplicated</c> |
    /// <c>unsupported</c> | <c>failed</c> and <c>source</c> (the <c>CompositionVersionSource</c>). A
    /// capture never fails the render it follows, so a rising <c>failed</c> count is the ONLY signal
    /// that films are being delivered without a revisable source.
    /// </summary>
    public static readonly Counter<long> CompositionWorkspaceCaptures =
        Meter.CreateCounter<long>("reelbolt.composition_workspace.captures", "snapshots", "Composition workspace snapshots, tagged with outcome and source");

    /// <summary>
    /// S-3: credit holds left open after a close attempt gave up, tagged <c>phase</c> = <c>step</c>
    /// (the step's three in-process attempts all failed; the metered amount was recorded on the step
    /// result) | <c>sweep</c> (a sweep pass could not close a hold; the next pass retries it) | <c>orphan</c>
    /// (a sweep closed a hold whose step result still said Running or Pending: the execution lease had
    /// lapsed and the hold was older than the running-step ceiling). Each one is money the platform is
    /// owed (or owes) until a later pass closes it; it should be zero.
    /// </summary>
    public static readonly Counter<long> UnsettledHolds =
        Meter.CreateCounter<long>("reelbolt.billing.unsettled_holds", "holds", "Credit holds left open after a settle/release attempt gave up, tagged with phase (step, sweep, orphan)");

    /// <summary>Composition workspace restores into a fresh sandbox, tagged <c>outcome</c> = <c>restored</c> | <c>failed</c>.</summary>
    public static readonly Counter<long> CompositionWorkspaceRestores =
        Meter.CreateCounter<long>("reelbolt.composition_workspace.restores", "restores", "Composition workspace restores, tagged with outcome");

    /// <summary>
    /// Engine error tracking (alpha-launch T-018): one per error the engine records, tagged
    /// <c>kind</c>. It shares this meter, whose name is already exported, so a failure reaches the
    /// watched OTLP destination with the rest of the engine's metrics (docs/observability.md).
    /// </summary>
    public static readonly Counter<long> Errors =
        Meter.CreateCounter<long>("reelbolt.errors", "errors", "Errors observed by the engine, tagged with kind");

    /// <summary>
    /// The closed set of <c>kind</c> values; anything else collapses to <c>other</c>, so no caller can
    /// mint a metric series. A metric dimension must be bounded.
    /// </summary>
    private static readonly string[] ErrorKinds = ["unhandled", "handled", "upstream", "validation"];

    /// <summary>
    /// Counts one error and stamps it on the active span. Reporting only: it never refuses, rewrites
    /// or swallows anything, and never throws into the caller. Only the exception's TYPE is recorded,
    /// never its message, which can carry user prose.
    /// </summary>
    public static void RecordError(Exception exception, string kind, string? environment = null)
    {
        try
        {
            string normalized = kind?.Trim().ToLowerInvariant() ?? "";
            if (Array.IndexOf(ErrorKinds, normalized) < 0) normalized = "other";

            var tags = new TagList { { "kind", normalized } };
            if (!string.IsNullOrWhiteSpace(environment)) tags.Add("environment", environment);
            Errors.Add(1, tags);

            Activity? activity = Activity.Current;
            if (activity is null) return;
            activity.SetTag("error.kind", normalized);
            activity.SetTag("error.type", exception.GetType().FullName);
            activity.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
        }
        catch
        {
            // Telemetry must never break the work it observes.
        }
    }
}
