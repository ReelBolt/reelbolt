using System.Diagnostics;
using MassTransit;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.WorkflowEngine.Observability;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The stage a media-derivative run is reporting. Deliberately a <b>second</b>, engine-owned
/// vocabulary rather than a reuse of <see cref="MediaDerivativeStage"/>: that enum belongs to
/// <see cref="MediaDerivativeService"/>, which is pure production, and it has no
/// <see cref="Failed"/> member because production itself either returns or throws. The dispatcher
/// (which owns the cache decision, the upload and the persistence) is the layer that can fail a
/// derivative <i>without</i> failing anything else, so the terminal stage it reports lives here.
/// The eight shared member names are identical on purpose, so the mapping below is a rename-free
/// pass-through and a reader never has to translate between the two.
/// </summary>
public enum MediaDerivativeProgressStage
{
    Queued,
    Probing,
    Proxy,
    Filmstrip,
    Peaks,
    KeyframeIndex,
    Done,

    /// <summary>No production was needed or possible: a cache hit, or a source whose MIME type has no derivatives.</summary>
    Skipped,

    /// <summary>Terminal failure of one derivative (or of the whole run when every derivative failed).</summary>
    Failed,

    /// <summary>Extracting full-size stills (T-101). Appended.</summary>
    Stills,

    /// <summary>R09.1: an analysis kind is running (<c>Detail</c> = the kind, percent 0) or done (percent 100). Appended.</summary>
    Analysis,

    /// <summary>R09.1: an analysis kind failed (<c>Detail</c> = the plain reason). Appended.</summary>
    AnalysisFailed,

    /// <summary>The stabilised preview proxy is being encoded (percent is the encode's). Appended.</summary>
    Stabilize
}

/// <summary>
/// One progress transition of a media-derivative run.
/// </summary>
/// <param name="ProjectId">Owning project.</param>
/// <param name="SourceProjectFileId">The <c>ProjectFile</c> the run is for, when the source is one
/// (null for an unregistered render, exactly as on the event and the row).</param>
/// <param name="SourceStorageKey">The source object's storage key — the half of the cache identity
/// that says <i>which</i> source this is.</param>
/// <param name="CacheKey">The derivative's canonical cache key, or null for a run-level transition
/// that is not yet attributable to one derivative (a MIME-type skip, a stale message).</param>
/// <param name="Stage">Where the run is.</param>
/// <param name="PercentComplete">Optional coarse percentage within the derivative, when the producer reports one.</param>
/// <param name="Detail">Free-form, human-readable context (why a derivative was skipped, what failed).</param>
public sealed record MediaDerivativeProgressUpdate(
    Guid ProjectId,
    Guid? SourceProjectFileId,
    string SourceStorageKey,
    string? CacheKey,
    MediaDerivativeProgressStage Stage,
    int? PercentComplete = null,
    string? Detail = null)
{
    /// <summary>
    /// Widens the producer's own stage vocabulary into this one. The eight shared names map
    /// one-for-one; <see cref="MediaDerivativeStage.Skipped"/> is the only member production never
    /// reports (it does no caching), and it maps to a stage the dispatcher reports itself.
    /// </summary>
    public static MediaDerivativeProgressStage From(MediaDerivativeStage stage) => stage switch
    {
        MediaDerivativeStage.Queued => MediaDerivativeProgressStage.Queued,
        MediaDerivativeStage.Probing => MediaDerivativeProgressStage.Probing,
        MediaDerivativeStage.Proxy => MediaDerivativeProgressStage.Proxy,
        MediaDerivativeStage.Filmstrip => MediaDerivativeProgressStage.Filmstrip,
        MediaDerivativeStage.Peaks => MediaDerivativeProgressStage.Peaks,
        MediaDerivativeStage.KeyframeIndex => MediaDerivativeProgressStage.KeyframeIndex,
        MediaDerivativeStage.Skipped => MediaDerivativeProgressStage.Skipped,
        MediaDerivativeStage.Stills => MediaDerivativeProgressStage.Stills,
        MediaDerivativeStage.Stabilize => MediaDerivativeProgressStage.Stabilize,
        _ => MediaDerivativeProgressStage.Done
    };
}

/// <summary>
/// The one seam a derivative run reports through. Engine-owned and deliberately narrow: it has no
/// execution id, no step id and no step-result id, because <b>a derivative is not a workflow
/// step</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this seam exists rather than a reuse of <c>WorkflowStepProgress</c>.</b> That event is
/// routed by its three ids — the Go API relays it as <c>step.progress</c> into the per-execution
/// workflow monitor — so publishing it from a derivative run would mean inventing an execution id,
/// a step id and a step-result id. Every consumer of that event would then render a phantom step
/// card for work no step ever did. A derivative has no such identity and must not pretend to.
/// </para>
/// <para>
/// <b>The default implementation reports three ways</b>: a structured log line per transition, an
/// OpenTelemetry <see cref="ActivityEvent"/> on the ambient activity, and — since the shared wire
/// event landed — one <c>MediaDerivativeProgress</c> message on the bus, which is what lets the GUI
/// render "Generating proxy 42 %". The dispatcher opens that activity for the whole run
/// (<c>media.derivatives</c>, on <see cref="ReelBoltDiagnostics.ActivitySource"/>, which the
/// engine's OTel tracing already subscribes to), so events land on a real span rather than on
/// nothing.
/// </para>
/// <para>
/// <b>Publication is a courtesy and can never cost a derivative.</b> It is fire-and-forget (the
/// caller's work is already durable when it reports), never awaited, and every failure — an
/// unstarted bus, a broker outage, a serializer error — is caught and logged at Debug.
/// <see cref="IMediaDerivativeProgressSink.Report"/> must never throw.
/// </para>
/// </remarks>
public interface IMediaDerivativeProgressSink
{
    /// <summary>Reports one transition. Implementations must not throw and must not block.</summary>
    void Report(MediaDerivativeProgressUpdate update);
}

/// <summary>
/// The default <see cref="IMediaDerivativeProgressSink"/>: one structured log line per transition,
/// an <see cref="ActivityEvent"/> on the current activity, and one <c>MediaDerivativeProgress</c>
/// message on the bus for the GUI.
/// </summary>
/// <remarks>
/// <para>
/// <b>It publishes <c>MediaDerivativeProgress</c>, never <c>WorkflowStepProgress</c>.</b> That event
/// is routed by an execution id, a step id and a step-result id — none of which a derivative has —
/// and the Go API relays it as <c>step.progress</c> into the per-execution workflow monitor, so
/// publishing it here would render a phantom step card for work no step ever did. Refusing to
/// publish it was the original call and it still stands; the shared event is the honest alternative.
/// </para>
/// <para>
/// <b>Why the bus is injected as <see cref="IBus"/>.</b> This sink is a singleton, and a derivative
/// run executes on a process-lifetime task with no consume scope, so the scoped
/// <c>IPublishEndpoint</c> is the wrong tool (and would be an invalid root-scope resolution under
/// <c>ValidateScopes</c>). <see cref="IBus"/> is the singleton the host registers and is exactly what
/// publishing outside a consume context is for. The dependency is optional so a harness that builds
/// the engine's registrations without a broker still resolves this type — in that case the sink is
/// the structured log and the span alone.
/// </para>
/// </remarks>
public sealed class LoggingMediaDerivativeProgressSink : IMediaDerivativeProgressSink
{
    /// <summary>Name of the activity event raised for every transition. Pinned — a dashboard or a test filters on it.</summary>
    public const string ActivityEventName = "media.derivative.progress";

    private readonly ILogger<LoggingMediaDerivativeProgressSink> _logger;
    private readonly IBus? _bus;

    public LoggingMediaDerivativeProgressSink(
        ILogger<LoggingMediaDerivativeProgressSink> logger,
        IBus? bus = null)
    {
        _logger = logger;
        _bus = bus;
    }

    /// <inheritdoc />
    public void Report(MediaDerivativeProgressUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        _logger.LogInformation(
            "Media derivative progress: Stage={Stage}, ProjectId={ProjectId}, SourceProjectFileId={SourceProjectFileId}, " +
            "SourceStorageKey={SourceStorageKey}, CacheKey={CacheKey}, PercentComplete={PercentComplete}, Detail={Detail}",
            update.Stage,
            update.ProjectId,
            update.SourceProjectFileId,
            update.SourceStorageKey,
            update.CacheKey,
            update.PercentComplete,
            update.Detail);

        AddActivityEvent(update);

        // Fire and forget, by contract: Report() is called from the derivative's own commit path,
        // whose success is already durable (the row is inserted, the object is uploaded), and the
        // caller is synchronous. Awaiting a broker here could fail an upload that already succeeded.
        // The task observes its own failures, so nothing is left unobserved.
        _ = PublishSafelyAsync(update);
    }

    /// <summary>
    /// Publishes one transition as <c>MediaDerivativeProgress</c> and <b>never throws</b> — the
    /// whole reason progress reporting cannot cost a derivative. Exposed to tests rather than only
    /// reached through <see cref="Report"/>'s fire-and-forget task, so the mapping and the
    /// swallowing can both be asserted deterministically.
    /// </summary>
    /// <remarks>
    /// A null bus is a legitimate case — a harness that builds the engine's registrations without a
    /// broker — and is not an error. Every other failure (a bus that has not started, a broker
    /// outage, a serializer error) is logged at Debug and dropped: the derivative it describes is
    /// unaffected either way.
    /// </remarks>
    internal async Task PublishSafelyAsync(MediaDerivativeProgressUpdate update)
    {
        if (_bus is null)
            return;

        try
        {
            await _bus.Publish(ToEvent(update)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Media derivative progress for project {ProjectId} (stage {Stage}) could not be published; " +
                "the derivative is unaffected",
                update.ProjectId,
                update.Stage);
        }
    }

    /// <summary>
    /// Maps the engine-owned update onto the shared wire event. The stage is carried as a NAME from
    /// the closed vocabulary rather than as an enum, because the producer's vocabulary
    /// (<see cref="MediaDerivativeProgressStage"/>) is engine-owned and the consumer must not have to
    /// reference it; the member names are identical on purpose.
    /// </summary>
    internal static MediaDerivativeProgress ToEvent(MediaDerivativeProgressUpdate update) => new()
    {
        ProjectId = update.ProjectId,
        SourceProjectFileId = update.SourceProjectFileId,
        SourceStorageKey = update.SourceStorageKey,
        CacheKey = update.CacheKey,
        Stage = update.Stage.ToString(),
        PercentComplete = update.PercentComplete,
        Detail = update.Detail,
        OccurredAt = DateTime.UtcNow
    };

    private static void AddActivityEvent(MediaDerivativeProgressUpdate update)
    {
        // Null is a legitimate case (a caller may report outside the dispatcher's activity, and
        // tracing may be off) and is never an error: progress reporting must not be able to fail a
        // derivative.
        Activity? activity = Activity.Current;
        if (activity is null)
            return;

        var tags = new ActivityTagsCollection
        {
            ["reelforge.project_id"] = update.ProjectId,
            ["reelforge.media.source_storage_key"] = update.SourceStorageKey,
            ["reelforge.media.derivative.stage"] = update.Stage.ToString()
        };

        if (update.SourceProjectFileId is { } fileId)
            tags["reelforge.media.source_project_file_id"] = fileId;

        if (!string.IsNullOrEmpty(update.CacheKey))
            tags["reelforge.media.derivative.cache_key"] = update.CacheKey;

        if (update.PercentComplete is { } percent)
            tags["reelforge.media.derivative.percent"] = percent;

        if (!string.IsNullOrEmpty(update.Detail))
            tags["reelforge.media.derivative.detail"] = update.Detail;

        activity.AddEvent(new ActivityEvent(ActivityEventName, tags: tags));
    }
}
