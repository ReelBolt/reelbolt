using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Linq.Expressions;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Moderation;

/// <summary>
/// The two timing knobs the output-moderation lane reads from configuration, with the shipped defaults. Kept in
/// this assembly rather than in <see cref="ReelBolt.Shared.Moderation.ContentModerationOptions"/> because the lane,
/// its sweep and the output view are the only readers, and a missing/typoed value must fall back to the default
/// rather than disable the fast path.
/// </summary>
public static class OutputModerationTiming
{
    /// <summary>Content produced this recently is "fresh": the sweep keeps its short interval while it exists.</summary>
    public const int DefaultRecentRowThresholdSeconds = 300;

    /// <summary>The view's Retry-After for an output whose verdict is still pending.</summary>
    public const int DefaultPendingRetryAfterSeconds = 5;

    /// <summary>The longest the lane may sleep when it finds nothing: short enough that a fresh burst is never minutes late.</summary>
    public const int DefaultMaxIdleSweepSeconds = 60;

    /// <summary>
    /// The effective ceiling for the idle sleep, clamped to 30 s..<see cref="OutputModerationWorker.MaxIdleSweepInterval"/>.
    /// The hard maximum stays 10 min for an operator who deliberately wants the old behaviour; the shipped value is 60 s
    /// because the sweep is a cheap existence query and a cap-refused burst must not wait minutes for its verdict.
    /// </summary>
    public static TimeSpan IdleSweepCeiling(IConfiguration? configuration) =>
        TimeSpan.FromSeconds(Math.Clamp(
            configuration?.GetValue("ContentModeration:MaxIdleSweepSeconds", DefaultMaxIdleSweepSeconds)
                ?? DefaultMaxIdleSweepSeconds,
            (int)OutputModerationWorker.SweepInterval.TotalSeconds,
            (int)OutputModerationWorker.MaxIdleSweepInterval.TotalSeconds));

    /// <summary>
    /// How recent a produced row must be for the sweep to stay on <see cref="OutputModerationWorker.SweepInterval"/>
    /// instead of the idle backoff. Clamped to 30 s..1 h: anything below a sweep interval is meaningless, and a
    /// day-long window would keep the idle query running at full rate forever.
    /// </summary>
    public static TimeSpan RecentRowThreshold(IConfiguration? configuration) =>
        TimeSpan.FromSeconds(Math.Clamp(
            configuration?.GetValue("ContentModeration:RecentRowThresholdSeconds", DefaultRecentRowThresholdSeconds)
                ?? DefaultRecentRowThresholdSeconds,
            30, 3600));

    /// <summary>The seconds a pending view asks the caller to wait before asking again, clamped to 1..60.</summary>
    public static int PendingRetryAfterSeconds(IConfiguration? configuration) =>
        Math.Clamp(
            configuration?.GetValue("ContentModeration:PendingRetryAfterSeconds", DefaultPendingRetryAfterSeconds)
                ?? DefaultPendingRetryAfterSeconds,
            1, 60);

    /// <summary>The header value for a pending answer.</summary>
    public static string PendingRetryAfterHeader(IConfiguration? configuration) =>
        PendingRetryAfterSeconds(configuration).ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The one non-blocking way to ask for an output to be judged. An offer never waits and a full queue
/// is fine: the sweep finds the output again.
/// </summary>
public interface IOutputModerationEnqueuer
{
    /// <summary>Offers the output. False when the queue is full or the same offer is already queued or running.</summary>
    bool TryQueue(Guid projectId, string storageKey);

    /// <summary>
    /// As above, for an offer made on behalf of an organization: at most a small number of its offers
    /// wait in the lane at once, so one tenant cannot hold the others in pending. The rest are left to
    /// the sweep, which takes one output per organization in turn.
    /// </summary>
    bool TryQueue(Guid projectId, string storageKey, Guid organizationId);

    /// <summary>The lane finished (or dropped) this task; its organization may offer another.</summary>
    void Released(OutputModerationTask task);
}

/// <inheritdoc />
public sealed class OutputModerationEnqueuer(IBackgroundTaskQueue<OutputModerationTask> queue)
    : IOutputModerationEnqueuer
{
    /// <summary>How many offers one organization may have waiting in the lane at once.</summary>
    public const int MaxQueuedPerOrganization = 5;

    private readonly object _gate = new();
    private readonly Dictionary<OutputModerationTask, Guid> _organizationOf = [];
    private readonly Dictionary<Guid, int> _queued = [];

    public bool TryQueue(Guid projectId, string storageKey) =>
        queue.TryQueue(new OutputModerationTask(projectId, storageKey));

    public bool TryQueue(Guid projectId, string storageKey, Guid organizationId)
    {
        OutputModerationTask task = new(projectId, storageKey);
        lock (_gate)
        {
            if (_queued.GetValueOrDefault(organizationId) >= MaxQueuedPerOrganization) return false;
            if (!queue.TryQueue(task)) return false;

            _organizationOf[task] = organizationId;
            _queued[organizationId] = _queued.GetValueOrDefault(organizationId) + 1;
            return true;
        }
    }

    public void Released(OutputModerationTask task)
    {
        lock (_gate)
        {
            if (_organizationOf.Remove(task, out Guid organizationId))
                _queued[organizationId] = Math.Max(0, _queued.GetValueOrDefault(organizationId) - 1);
        }
    }
}

/// <summary>
/// Ends the output-moderation sweep's sleep early. Pulsed when something the sweep is waiting on changed (a CSAM scan
/// finished, so held outputs can be judged now). Wakes collapse: one waiting wake is enough, and none is lost.
/// </summary>
public interface IOutputModerationWake
{
    /// <summary>Asks the sweep to run now. Never blocks.</summary>
    void Wake();

    /// <summary>Waits for a wake or the timeout. True when woken (and takes the wake).</summary>
    Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct);
}

/// <inheritdoc />
public sealed class OutputModerationWake : IOutputModerationWake
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Wake()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake is already waiting; one is enough.
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

/// <summary>The one status mapping for a refused publish outcome, shared by every route that serves output bytes.</summary>
public static class PublishModerationResults
{
    /// <summary>Blocked is 422, anything else 409; a pending answer also asks the caller to retry in 5 seconds.</summary>
    public static ObjectResult ToRefusal(this PublishModerationOutcome outcome, ControllerBase controller)
    {
        if (outcome.Code == PublishModerationOutcome.PendingCode)
        {
            if (controller.HttpContext?.Response is { } response) response.Headers["Retry-After"] = "5";
        }
        return controller.StatusCode(
            outcome.Code == "CONTENT_BLOCKED" ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status409Conflict,
            new { message = outcome.Message, code = outcome.Code, @final = outcome.Final });
    }
}

/// <summary>Reason codes a stored verdict carries beyond the policy's own.</summary>
public static class OutputModerationReasonCodes
{
    /// <summary>The bytes were read fine but hold nothing to classify (or are of an unsupported type). Final: never retried.</summary>
    public const string NothingToClassify = "nothing_to_classify";

    /// <summary>
    /// Recorded on an Allow for wordless audio our own Music or Sound step made from a checked prompt (see
    /// <see cref="GeneratedAudioClearance"/>). No vendor call was made for it: there is nothing in it to send.
    /// </summary>
    public const string GeneratedWordlessAudio = "generated_wordless_audio";

    /// <summary>
    /// Recorded on an Allow whose speech was not heard: the file has sound, no platform transcription provider answered, and
    /// <c>ContentModeration:AllowFramesOnlyWithoutTranscription</c> let it through on its frames alone.
    /// </summary>
    public const string FramesOnly = "frames_only";
}

/// <summary>When the background lane tries an outage again, and when it stops trying.</summary>
public static class OutputModerationBackoff
{
    /// <summary>After this many outages in a row the sweep stops; a view may still ask again.</summary>
    public const int MaxAttempts = 10;

    /// <summary>The longest gap between two tries, and the least gap between two view-driven tries once the sweep has stopped.</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromHours(6);

    private static readonly TimeSpan[] Gaps =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        MaxGap
    ];

    /// <summary>
    /// The "never" retry time of a refusal that is certain for these bytes (a file that is not a playable video, two audio
    /// tracks, a false length): the same bytes will always give the same answer, so nothing offers them again by time.
    /// A far-future time, not null, because null already means "the sweep gave up on an outage; a view may ask every 6 hours".
    /// </summary>
    public static readonly DateTime Never = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The next try after the <paramref name="attempts"/>-th outage, or null once the attempts are used up.</summary>
    public static DateTime? NextAttemptAt(int attempts, DateTime now)
    {
        if (attempts >= MaxAttempts) return null;
        return now + Gaps[Math.Clamp(attempts - 1, 0, Gaps.Length - 1)];
    }
}

/// <summary>Metrics of the output-moderation lane. Closed tag sets only: never ids or content.</summary>
public static class OutputModerationDiagnostics
{
    /// <summary>Meter name; the Inference API's telemetry registration adds it with <c>AddMeter</c>.</summary>
    public const string MeterName = "ReelBolt.Inference.Moderation";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Seconds from the step that made an output completing to its first verdict. Backfilled outputs older than one hour are not recorded.</summary>
    public static readonly Histogram<double> VerdictSeconds = Meter.CreateHistogram<double>(
        "reelbolt.moderation.output_verdict_seconds", "s",
        "Seconds from an output being produced to its first stored moderation verdict");

    /// <summary>How many offers wait in, or run on, the lane (set by the worker); read by the depth gauge.</summary>
    public static Func<int>? DepthProbe { get; set; }

    /// <summary>Offers waiting in or running on the moderation lane. A sustained high value means users see "still being checked".</summary>
    public static readonly ObservableGauge<int> QueueDepth = Meter.CreateObservableGauge(
        "reelbolt.moderation.output_queue_depth", () => DepthProbe?.Invoke() ?? 0, "offers",
        "Output moderation offers waiting in or running on the lane");

    /// <summary>Older outputs are backfill, not the latency a user waits for.</summary>
    public static readonly TimeSpan MaxRecordedAge = TimeSpan.FromHours(1);
}

/// <summary>The suspected-CSAM rule, as one predicate both the processor and the sweep use.</summary>
public static class OutputModerationHold
{
    /// <summary>
    /// A project file the CSAM control holds (<c>CsamScanGate.IsHeld</c>), or, when that control is on,
    /// one whose verdict is not in yet (sampled frames could include it). Suspected CSAM never goes to
    /// the moderation vendor (docs/csam.md).
    /// </summary>
    public static Expression<Func<ProjectFile, bool>> FilePredicate(bool csamEnabled) =>
        csamEnabled
            ? f => f.CsamStatus == CsamScanStatus.Quarantined || f.CsamQuarantineKey != null
                   || f.CsamStatus == CsamScanStatus.Pending || f.CsamStatus == CsamScanStatus.Scanning
            : f => f.CsamStatus == CsamScanStatus.Quarantined || f.CsamQuarantineKey != null;

    /// <summary>
    /// The project-level output marker (<c>Project.CsamOutputHoldAt</c>, see <c>ProjectOutputHold.ReadAsync</c>), additive
    /// to <see cref="FilePredicate"/>: a project with the marker set holds all moderation work, whatever its files say.
    /// </summary>
    public static Expression<Func<Project, bool>> ProjectMarkerPredicate() => p => p.CsamOutputHoldAt != null;
}

/// <summary>The content type the sampler needs, read from a stored output's key.</summary>
internal static class OutputMimeTypes
{
    public const string Unknown = "application/octet-stream";

    /// <summary>The kind a content type is processed as: text, video, audio, image or other.</summary>
    public static string KindOf(string? mime)
    {
        string m = (mime ?? string.Empty).Trim().ToLowerInvariant();
        if (m.StartsWith("text/", StringComparison.Ordinal)
            || m is "application/json" or "application/xml" or "application/x-ndjson" or "application/yaml" or "application/x-yaml")
            return "text";
        if (m.StartsWith("video/", StringComparison.Ordinal)) return "video";
        if (m.StartsWith("audio/", StringComparison.Ordinal)) return "audio";
        if (m.StartsWith("image/", StringComparison.Ordinal)) return "image";
        return "other";
    }

    public static string FromKey(string storageKey) =>
        Path.GetExtension(storageKey).ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".avi" => "video/x-msvideo",
            ".mpg" or ".mpeg" => "video/mpeg",
            ".ts" => "video/mp2t",
            ".mxf" => "video/mxf",
            ".aac" => "audio/aac",
            ".flac" => "audio/flac",
            ".opus" => "audio/ogg",
            ".aiff" or ".aif" => "audio/aiff",
            ".bmp" => "image/bmp",
            ".avif" => "image/avif",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".m4a" => "audio/mp4",
            ".ogg" => "audio/ogg",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".txt" or ".srt" or ".vtt" or ".md" or ".csv" or ".tsv" or ".edl" or ".ass" or ".ssa" or ".sbv" or ".log"
                or ".otio" or ".fcpxml" or ".yaml" or ".yml" => "text/plain",
            ".xml" => "application/xml",
            ".json" => "application/json",
            _ => Unknown
        };
}
