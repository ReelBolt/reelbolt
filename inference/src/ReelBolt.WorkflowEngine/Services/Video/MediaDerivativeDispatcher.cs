using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Editing.Color;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.WorkflowEngine.Consumers;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Drain;
using ReelBolt.WorkflowEngine.Observability;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video.Caching;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Which family of derivatives a source's MIME type has. Resolved from the MIME type <b>alone</b>:
/// the message's own <c>Kinds</c> can only narrow this set, never widen it.
/// </summary>
public enum MediaDerivativeSourceClass
{
    /// <summary><c>video/*</c> — the full set: proxy, filmstrip, waveform peaks, keyframe index.</summary>
    Video,

    /// <summary><c>audio/*</c> — waveform peaks only. A filmstrip of an audio file is a blank grid.</summary>
    Audio,

    /// <summary><c>image/*</c> — nothing. A still has no timeline to proxy or scrub.</summary>
    Image,

    /// <summary>Anything else, including an empty or absent MIME type. Treated exactly like <see cref="Image"/>: skip and say so.</summary>
    Unknown
}

/// <summary>
/// A counting gate with two FIFO classes: an interactive waiter is always granted a freed slot before any background
/// (sweep) waiter. A holder is never pre-empted. A queued background waiter can be promoted to the interactive class.
/// </summary>
public sealed class DerivativeSourceGate
{
    private sealed class Waiter(string key, bool interactive)
    {
        public string Key { get; } = key;
        public bool Interactive { get; set; } = interactive;
        public TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _lock = new();
    private readonly LinkedList<Waiter> _interactive = new();
    private readonly LinkedList<Waiter> _background = new();
    private readonly int _slots;
    private int _free;

    /// <summary>Creates a gate with <paramref name="slots"/> free slots.</summary>
    public DerivativeSourceGate(int slots)
    {
        _slots = slots;
        _free = slots;
    }

    /// <summary>Waits for a slot. <paramref name="key"/> identifies the waiter for <see cref="Promote"/>.</summary>
    public Task WaitAsync(string key, bool interactive, CancellationToken ct)
    {
        Waiter waiter;
        lock (_lock)
        {
            if (_free > 0 && _interactive.Count == 0 && _background.Count == 0)
            {
                _free--;
                return Task.CompletedTask;
            }

            waiter = new Waiter(key, interactive);
            (interactive ? _interactive : _background).AddLast(waiter);
        }

        if (ct.CanBeCanceled)
        {
            ct.Register(() =>
            {
                lock (_lock)
                {
                    _interactive.Remove(waiter);
                    _background.Remove(waiter);
                }

                waiter.Signal.TrySetCanceled(ct);
            });
        }

        return waiter.Signal.Task;
    }

    /// <summary>True while no run holds a slot and none waits for one.</summary>
    public bool IsIdle
    {
        get
        {
            lock (_lock)
            {
                return _free >= _slots && _interactive.Count == 0 && _background.Count == 0;
            }
        }
    }

    /// <summary>True while at least one run waits for a slot.</summary>
    public bool HasWaiters
    {
        get
        {
            lock (_lock)
            {
                return _interactive.Count > 0 || _background.Count > 0;
            }
        }
    }

    /// <summary>Moves a still-queued background waiter to the end of the interactive queue. False when it is not waiting as background.</summary>
    public bool Promote(string key)
    {
        lock (_lock)
        {
            for (LinkedListNode<Waiter>? n = _background.First; n is not null; n = n.Next)
            {
                if (n.Value.Key != key)
                    continue;
                _background.Remove(n);
                n.Value.Interactive = true;
                _interactive.AddLast(n.Value);
                return true;
            }

            // Already interactive and queued, or running: the repeat is a plain duplicate, but it is already at the front class.
            return _interactive.Any(w => w.Key == key);
        }
    }

    /// <summary>Returns a slot, handing it to the next interactive waiter, else the next background waiter.</summary>
    public void Release()
    {
        lock (_lock)
        {
            while (true)
            {
                LinkedList<Waiter> q = _interactive.Count > 0 ? _interactive : _background;
                if (q.Count == 0)
                {
                    _free++;
                    return;
                }

                Waiter next = q.First!.Value;
                q.RemoveFirst();
                if (next.Signal.TrySetResult())
                    return;
            }
        }
    }
}

/// <summary>
/// The writer of <c>media_derivatives</c>: downloads a source once, decides per derivative whether
/// its canonical cache identity is already satisfied, produces the misses, uploads their objects and
/// inserts exactly one row each.
/// </summary>
/// <remarks>
/// <para>
/// <b>It dispatches — it does not execute inline.</b> <see cref="DispatchAsync"/> starts the work on
/// a task bound to the process lifetime and returns, exactly like
/// <c>WorkflowExecutionRunner</c> does for a workflow execution, so the caller's RabbitMQ delivery is
/// acknowledged in milliseconds. A derivative encode runs for seconds to minutes; awaiting one inside
/// a consumer body would put the delivery back under the broker's <c>consumer_timeout</c> — the wall
/// that decision exists to remove (see <c>CLAUDE.md</c>, "Workflow Execution Liveness").
/// </para>
/// <para>
/// <b>Nothing here may fail a workflow.</b> Every failure path is caught, logged, reported as a
/// terminal <see cref="MediaDerivativeProgressStage.Failed"/> stage and left with <b>no row</b>, so a
/// later request simply tries again. A derivative pipeline that can fail a workflow is worse than a
/// missing proxy.
/// </para>
/// <para>
/// <b>A derivative the source cannot produce is not a failure.</b>
/// <see cref="MediaDerivativeUnsupportedException"/> — thrown before any work is attempted — is
/// reported as its own <see cref="MediaDerivativeProgressStage.Skipped"/> transition and counted
/// separately, so a source too short for a filmstrip density neither logs a fault nor drags the
/// run's terminal stage to <see cref="MediaDerivativeProgressStage.Failed"/>.
/// </para>
/// <para>
/// <b>Idempotency.</b> Two layers. Within the process, concurrent deliveries for one cache key share
/// a single production through <see cref="_inFlight"/> (an in-memory
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>, correct for one process and <b>only</b> for one
/// process — a second WorkflowEngine replica would run its own copy and lose the database race
/// below). Across processes and restarts, the
/// <c>ux_media_derivatives_cache_key</c> partial unique index is the backstop: a duplicate insert
/// fails loudly at the database rather than silently double-writing, and that failure is handled as
/// "someone else produced it" rather than as an error.
/// </para>
/// <para>
/// <b>Trust boundary (plan §3.7 / Global Constraint #12).</b> The derivative parameter catalogue is
/// server-chosen and closed (<see cref="MediaDerivativeSpec"/>); the only thing this layer selects is
/// a MIME class and a variant index, and the only thing a caller can influence is a member of the
/// closed <see cref="MediaDerivativeKind"/> enum. No client value reaches argv.
/// </para>
/// </remarks>
public sealed class MediaDerivativeDispatcher : IHostedService
{
    /// <summary>The subdirectory of <see cref="VideoEditingOptions.ScratchPath"/> derivative work lives in.</summary>
    public const string ScratchSubdirectory = MediaDerivativeService.ScratchSubdirectory;

    /// <summary>The <c>RequestedBy</c> tag of the periodic backfill sweep (the only background class); everything else is interactive.</summary>
    public const string SweepRequester = "sweep";

    private readonly IDbContextFactory<WorkflowEngineDbContext> _dbFactory;
    private readonly IProjectFileWorkspace _workspace;
    private readonly MediaDerivativeService _derivatives;
    private readonly IVideoToolRunner _runner;
    private readonly IMediaDerivativeProgressSink _progress;
    private readonly VideoEditingOptions _options;
    private readonly IScratchDiskGuard? _scratchGuard;
    private readonly string _bucketName;
    private readonly ILogger<MediaDerivativeDispatcher> _logger;

    /// <summary>
    /// Derivative runs that are in flight, keyed by the canonical cache key — see the class remark on
    /// idempotency. <see cref="Lazy{T}"/> rather than a bare <c>Task</c> deliberately: a bare factory
    /// would have to <i>start</i> its task before <c>GetOrAdd</c> could discard it, so two concurrent
    /// callers would both run a full encode and only one row would survive.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<DerivativeOutcome>>> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Whole requests being worked on, keyed by project + source + requested kinds. A repeat request (the
    /// backfill sweep re-publishes every ten minutes while a long proxy has no row yet) is dropped BEFORE it
    /// downloads a multi-gigabyte source, takes a source slot or probes. The value is 1 when that run was asked to
    /// warm the pass cache and has not reached that point yet (it gives its key up there), else 0.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _runsInFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Warm-ups that gave way to another run, waiting for a quiet moment (see <see cref="DeferWarmUp"/>). In memory
    /// only: an engine restart forgets them, and a step on such a file then runs its own passes as before.
    /// </summary>
    private readonly Queue<MediaDerivativesRequested> _deferredWarmUps = new();

    /// <summary>Guards <see cref="_deferredWarmUps"/>, so both caps are exact.</summary>
    private readonly object _deferredLock = new();

    /// <summary>How often the warm-up of one run key gave way so far.</summary>
    private readonly ConcurrentDictionary<string, int> _warmUpDeferrals = new(StringComparer.Ordinal);

    private int _deferredWarmUpsRunning;

    private int _deferredRecheckPending;

    /// <summary>Most warm-ups that may wait for a quiet moment at once. One over this is dropped (logged), never queued.</summary>
    internal const int MaxDeferredWarmUps = 200;

    /// <summary>
    /// Most of those that may belong to one project, so one project's burst of uploads cannot fill the whole list.
    /// One over this is dropped (logged), never queued.
    /// </summary>
    internal const int MaxDeferredWarmUpsPerProject = 20;

    /// <summary>How often one file's warm-up may give way and be tried again before it is given up.</summary>
    internal const int MaxWarmUpDeferrals = 4;

    /// <summary>How long a waiting warm-up is left alone while a workflow step waits for scratch space, before the engine looks again.</summary>
    internal TimeSpan DeferredRecheckInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Warm-ups waiting for a quiet moment. Test/diagnostic hook.</summary>
    internal int DeferredWarmUpCount
    {
        get
        {
            lock (_deferredLock)
            {
                return _deferredWarmUps.Count;
            }
        }
    }

    /// <summary>
    /// The derivative concurrency cap, in front of T-301's own low-priority admission gate. That gate
    /// protects a compile that has <i>already</i> been queued; this one keeps a burst of derivatives
    /// from occupying the whole ffmpeg budget in the first place.
    /// </summary>
    private readonly SemaphoreSlim _derivativeSlots;

    /// <summary>
    /// Optional: the camera profile the source's own probe says it uses with HIGH confidence (a detector hint), or null.
    /// A source it names gets the converted proxy next to the plain one with no request flag. Null (the default) means
    /// only an explicit <see cref="MediaDerivativesRequested.ProxyVariant"/> asks for one.
    /// </summary>
    private readonly Func<MediaProbeResult, string?>? _autoProfile;

    /// <summary>
    /// Optional: fills the per-source pass cache of VideoAnalyze for a video whose request asks for it
    /// (<see cref="MediaDerivativesRequested.AnalyzeForWorkflows"/>), after the derivatives. Null means no run ever does.
    /// </summary>
    private readonly SourcePassWarmUp? _warmUp;

    /// <summary>
    /// Optional: tells when this engine is draining (a newer one took over and this one only finishes its work).
    /// A draining engine starts no warm-up and keeps none for later. Null means it never drains.
    /// </summary>
    private readonly EngineDrainTarget? _drain;

    /// <summary>
    /// The source-run cap: a run takes one of these BEFORE it downloads and probes its source, and
    /// holds it until its scratch copy is deleted. Without it every queued upload pulled its whole
    /// source to scratch at once and only then waited for a derivative slot (security MI-1), so
    /// scratch disk and bandwidth are now bounded by the same <c>DerivativeMaxConcurrentJobs</c>.
    /// A separate semaphore from <see cref="_derivativeSlots"/>, which each derivative still takes
    /// inside its run: one shared semaphore would deadlock at a cap of 1.
    /// </summary>
    private readonly DerivativeSourceGate _sourceSlots;

    /// <summary>Process-lifetime token for the background runs this dispatcher owns. Never a caller's token.</summary>
    private readonly CancellationTokenSource _lifetimeCts = new();

    public MediaDerivativeDispatcher(
        IDbContextFactory<WorkflowEngineDbContext> dbFactory,
        IProjectFileWorkspace workspace,
        MediaDerivativeService derivatives,
        IVideoToolRunner runner,
        IMediaDerivativeProgressSink progress,
        IOptions<VideoEditingOptions> options,
        IConfiguration configuration,
        ILogger<MediaDerivativeDispatcher> logger,
        IScratchDiskGuard? scratchGuard = null,
        Func<MediaProbeResult, string?>? autoProfile = null,
        SourcePassWarmUp? warmUp = null,
        EngineDrainTarget? drain = null)
    {
        _scratchGuard = scratchGuard;
        _autoProfile = autoProfile;
        _warmUp = warmUp;
        _drain = drain;
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);

        _dbFactory = dbFactory;
        _workspace = workspace;
        _derivatives = derivatives;
        _runner = runner;
        _progress = progress;
        _options = options.Value;
        // Same key, same fallback, as ProjectFileWorkspace: a row whose storage_bucket disagreed with
        // the bucket the object actually went into would be a lookup that silently finds nothing.
        _bucketName = configuration["MinIO:BucketName"] ?? "reelforge";
        _logger = logger;
        _derivativeSlots = new SemaphoreSlim(
            Math.Max(1, _options.DerivativeMaxConcurrentJobs),
            Math.Max(1, _options.DerivativeMaxConcurrentJobs));
        _sourceSlots = new DerivativeSourceGate(Math.Max(1, _options.DerivativeMaxConcurrentJobs));
    }

    /// <summary>Number of derivative productions currently in flight. Test/diagnostic hook.</summary>
    public int InFlightCount => _inFlight.Count;

    private int _active;

    /// <summary>Runs accepted by <see cref="DispatchAsync"/> and not yet finished (drain activeWork).</summary>
    public int ActiveCount => Volatile.Read(ref _active);

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Cancels the in-flight runs. Deliberately does not wait for them: a derivative is best-effort
    /// background work whose absence is repaired by the next request, so blocking shutdown on a
    /// half-finished encode would buy nothing and cost the shutdown window.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_lifetimeCts.IsCancellationRequested)
            _lifetimeCts.Cancel();

        return Task.CompletedTask;
    }

    /// <summary>
    /// The handoff: starts the run on a process-lifetime task and returns immediately. The caller must
    /// <b>not</b> await the work itself — see the class remark on why the delivery’s acknowledgement
    /// must not be tied to the encode.
    /// </summary>
    public Task DispatchAsync(MediaDerivativesRequested message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Fire and forget on purpose. RunAsync is contractually non-throwing (it swallows every
        // failure path itself), and the try/catch is the belt to that braces: an unobserved task
        // exception here would be lost rather than crash the process, which is still worse than a
        // log line.
        Interlocked.Increment(ref _active);
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await RunAsync(message, _lifetimeCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Media derivative run for project {ProjectId} source {SourceStorageKey} ended unexpectedly",
                        message.ProjectId,
                        message.SourceStorageKey);
                }
                finally
                {
                    Interlocked.Decrement(ref _active);
                }
            },
            CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <summary>
    /// The run itself, awaitable. <see cref="DispatchAsync"/> is the production entry point; this is
    /// the same work without the fire-and-forget, so a test can assert the whole pipeline's effects
    /// deterministically. Never throws.
    /// </summary>
    public async Task RunAsync(MediaDerivativesRequested message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        // The run's own span. Both the sink's ActivityEvents and every log line below land on it, and
        // ReelBoltDiagnostics.ActivitySource is already subscribed by the engine's OTel tracing, so
        // this is observable in a real deployment rather than only in a test.
        using Activity? activity = ReelBoltDiagnostics.ActivitySource.StartActivity("media.derivatives");
        activity?.SetTag("reelforge.project_id", message.ProjectId);
        activity?.SetTag("reelforge.media.source_storage_key", message.SourceStorageKey);
        activity?.SetTag("reelforge.media.source_kind", message.SourceRef?.Kind.ToString());
        activity?.SetTag("reelforge.media.mime_type", message.MimeType);
        activity?.SetTag("reelforge.media.requested_by", message.RequestedBy);

        string runKey = RunKey(message);
        bool interactive = !string.Equals(message.RequestedBy, SweepRequester, StringComparison.Ordinal);
        if (!_runsInFlight.TryAdd(runKey, WarmUpWanted(message) ? (byte)1 : (byte)0))
        {
            // The run in flight may not have been asked to warm the pass cache (the flag is not part of the run key):
            // the repeat's warm-up then waits for a quiet moment instead of being lost. When that run was asked
            // too, it will warm the cache itself, and keeping the repeat would only download the file once more.
            bool deferRepeat = WarmUpWanted(message) && !(_runsInFlight.TryGetValue(runKey, out byte warms) && warms == 1);

            // An interactive repeat of a request that still waits behind the backfill moves it to the front class
            // instead of being dropped while the original sits at the back of the queue.
            if (interactive && _sourceSlots.Promote(runKey))
            {
                if (deferRepeat)
                    DeferWarmUp(message);
                Report(
                    message,
                    cacheKey: null,
                    MediaDerivativeProgressStage.Skipped,
                    detail: "A queued background run for this source was promoted to interactive priority; this repeat request was dropped.");
                return;
            }

            if (deferRepeat)
                DeferWarmUp(message);

            Report(
                message,
                cacheKey: null,
                MediaDerivativeProgressStage.Skipped,
                detail: "A run for this source and kind set is already queued or running; this repeat request was dropped.");
            return;
        }

        string? downloadDirectory = null;
        ProxyHandoff handoff = new();
        bool holdsSourceSlot = false;
        bool holdsRunKey = true;
        IDisposable? scratchLease = null;
        int produced = 0;
        int failed = 0;
        int unsupported = 0;
        int cached = 0;
        int attempted = 0;

        try
        {
            IReadOnlyList<(MediaDerivativeKindSlug Slug, int Variant)> plan =
                ResolveSpecPlan(message.MimeType, message.Kinds);

            if (plan.Count == 0)
            {
                Report(
                    message,
                    cacheKey: null,
                    MediaDerivativeProgressStage.Skipped,
                    detail: $"No derivatives are defined for MIME type '{message.MimeType}' " +
                            $"({ClassifyMimeType(message.MimeType)}) with the requested kind set " +
                            $"[{string.Join(",", message.Kinds ?? Array.Empty<MediaDerivativeKind>())}].");
                return;
            }

            // The slot comes before the download: N uploads must never mean N full scratch copies.
            await _sourceSlots.WaitAsync(runKey, interactive, ct).ConfigureAwait(false);
            holdsSourceSlot = true;

            // The output hold may have started while this run waited for the slot: read it again before anything is
            // downloaded. Held or unreadable = no run (fail closed); the finally below releases the slot.
            if (await MediaDerivativesConsumer.IsWithheldAsync(_dbFactory, message, ct).ConfigureAwait(false))
            {
                _logger.LogWarning("Skipping derivatives for {SourceStorageKey}: withheld", message.SourceStorageKey);
                Report(message, cacheKey: null, MediaDerivativeProgressStage.Failed, detail: "The derivatives could not be produced.");
                return;
            }

            // Derivatives are background convenience work: when the scratch volume cannot take this source
            // right now, skip quietly. The next request for the same source repairs it; nothing fails.
            if (_scratchGuard is not null)
            {
                long sourceBytes = await LookupSourceBytesAsync(message, ct).ConfigureAwait(false);
                if (sourceBytes > _scratchGuard.MaxSourceBytes)
                {
                    _logger.LogInformation("Skipping derivatives for {SourceStorageKey}: over the source size limit", message.SourceStorageKey);
                    return;
                }

                if (!_scratchGuard.TryReserve(sourceBytes, out scratchLease))
                {
                    _logger.LogInformation(
                        "Skipping derivatives for {SourceStorageKey}: not enough scratch space right now",
                        message.SourceStorageKey);
                    Report(message, cacheKey: null, MediaDerivativeProgressStage.Skipped,
                        detail: "Skipped for now: not enough working space; it will be retried.");
                    return;
                }
            }

            downloadDirectory = CreateDownloadDirectory(message.ProjectId);

            // ONCE per request, and reused by every spec below. Four derivatives must not mean four
            // copies of a multi-gigabyte source.
            handoff.Directory = downloadDirectory;
            string localSourcePath = Path.Combine(downloadDirectory, "source" + SafeExtension(message.SourceStorageKey));
            await _workspace.DownloadStorageKeyToFileAsync(
                message.ProjectId, message.SourceStorageKey, localSourcePath, ct).ConfigureAwait(false);

            // The published ETag may legitimately be empty (a failed best-effort HEAD must never fail
            // an upload), and an empty string must never become the etag component of a persisted row:
            // every row of one source would then share a key and a re-uploaded different file would
            // satisfy an old derivative. Fall back to the content's own SHA-256 instead of rejecting
            // the message or parking a row under a guessable key.
            string effectiveEtag = ResolveEffectiveEtag(message.SourceEtag, localSourcePath);
            if (string.IsNullOrWhiteSpace(message.SourceEtag))
            {
                _logger.LogInformation(
                    "Media derivative request for {SourceStorageKey} carried no ETag; using the content hash " +
                    "{EffectiveEtag} as the content-identity component",
                    message.SourceStorageKey,
                    effectiveEtag);
            }

            activity?.SetTag("reelforge.media.source_etag", effectiveEtag);

            // The source is probed ONCE per run, at Low priority, before any derivative: the proxy's
            // parameters are functions of it (its height, real frame rate, whether it has audio), and
            // the probe itself is stored on every row of this source (probe at ingest, R05.1) so the
            // media pool knows a file's duration, rate, size and codecs without an analysis run.
            MediaProbeResult? source = await TryProbeForRunAsync(localSourcePath, plan, message, ct).ConfigureAwait(false);
            string? sourceProbeJson = source is null ? null : ToStoredProbeJson(source);
            using IDisposable timeoutScope = source is null
                ? new NoScope()
                : SourceTimeoutPolicy.Begin(
                    source.DurationSec, source.Width, source.Height, SourceTimeoutPolicy.SafeLength(localSourcePath),
                    fps: SourceTimeoutPolicy.FpsOf(source.FpsNum, source.FpsDen));

            if (source is not null)
                plan = WithConvertedProxy(plan, source, message.ProxyVariant, _autoProfile);

            foreach ((MediaDerivativeKindSlug slug, int variant) in plan)
            {
                ct.ThrowIfCancellationRequested();
                if (attempted > 0 && await MediaDerivativesConsumer.IsWithheldAsync(_dbFactory, message, ct).ConfigureAwait(false))
                {
                    Report(message, cacheKey: null, MediaDerivativeProgressStage.Failed, detail: "The derivatives could not be produced.");
                    return;
                }

                attempted++;

                // The probe was taken ONCE for the whole run, above: only the proxy's parameters are
                // functions of the source itself (its height, its real frame rate, whether it has
                // audio), and the same probe is stored on every row of this source.
                MediaDerivativeSpec spec = slug switch
                {
                    MediaDerivativeKindSlug.Proxy => MediaDerivativeSpec.ForProxy(
                        source!.Height,
                        source.FpsNum,
                        source.FpsDen,
                        hasAudio: !string.IsNullOrWhiteSpace(source.AudioCodec),
                        threads: _options.FfmpegThreads,
                        sourceWidth: source.Width,
                        cameraProfileVariant: variant),

                    // The one spec a message parameterizes: its requested times, normalized by the
                    // same rule the publisher matches with, so (source, times) is the cache identity.
                    MediaDerivativeKindSlug.Stills => MediaDerivativeSpec.ForStills(
                        MediaDerivativeStillTimes.NormalizeToMs(message.StillTimesSec)),

                    MediaDerivativeKindSlug.StabilizedProxy => MediaDerivativeSpec.ForStabilizedProxy(
                        source!.Height, source.FpsNum, source.FpsDen, _options.FfmpegThreads, source.Width),

                    _ => ResolveStaticSpec(slug, variant)
                };

                DerivativeOutcome outcome = await ProduceOneDerivativeAsync(
                    message, spec, localSourcePath, effectiveEtag, sourceProbeJson, handoff, ct).ConfigureAwait(false);

                switch (outcome.Status)
                {
                    case DerivativeOutcomeStatus.Produced:
                        produced++;
                        break;
                    case DerivativeOutcomeStatus.Failed:
                        failed++;
                        break;
                    case DerivativeOutcomeStatus.Unsupported:
                        unsupported++;
                        break;
                    case DerivativeOutcomeStatus.CacheHit:
                        cached++;
                        break;
                    default:
                        break;
                }
            }

            // Rows that predate stored probes (or that another run inserted without one) get this
            // run's probe, so a cache-hit request still leaves the source probed.
            if (sourceProbeJson is not null)
                await BackfillSourceProbeAsync(message, effectiveEtag, sourceProbeJson, ct).ConfigureAwait(false);

            // A skipped-as-unsupported derivative is not a failure: a short source must not turn a
            // run that produced everything it could into a Failed report. And a run whose derivatives
            // all already exist is DONE — the source has its set — not skipped (RD-14).
            MediaDerivativeProgressStage terminal = failed > 0
                ? MediaDerivativeProgressStage.Failed
                : produced > 0 || cached > 0
                    ? MediaDerivativeProgressStage.Done
                    : MediaDerivativeProgressStage.Skipped;

            Report(
                message,
                cacheKey: null,
                terminal,
                detail: $"{produced} produced, {failed} failed, {unsupported} unsupported by this source, " +
                        $"{cached} already cached of {attempted} requested " +
                        $"({ClassifyMimeType(message.MimeType)} source).");

            activity?.SetTag("reelforge.media.derivatives.produced", produced);
            activity?.SetTag("reelforge.media.derivatives.failed", failed);
            activity?.SetTag("reelforge.media.derivatives.unsupported", unsupported);

            // Last, after the run reported its derivatives: nothing below can change or delay them. The run's
            // derivatives are done, so a repeat request for this source is a new run from here on, not a
            // duplicate of this one: it queues for the slot, and the warm-up gives way to it.
            if (source is not null && WarmUpWanted(message))
            {
                _runsInFlight.TryRemove(runKey, out _);
                holdsRunKey = false;
                await WarmPassCacheAsync(message, localSourcePath, source, activity, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown, not a fault. The derivatives simply stay absent and the next request retries.
            _logger.LogInformation(
                "Media derivative run for project {ProjectId} source {SourceStorageKey} was cancelled",
                message.ProjectId,
                message.SourceStorageKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Media derivative run for project {ProjectId} source {SourceStorageKey} failed",
                message.ProjectId,
                message.SourceStorageKey);

            Report(message, cacheKey: null, MediaDerivativeProgressStage.Failed, detail: ex.Message);
        }
        finally
        {
            // Only this run's own entry: once the key was given up, the entry (if any) is a later run's.
            if (holdsRunKey)
                _runsInFlight.TryRemove(runKey, out _);
            TryDeleteDirectory(downloadDirectory);
            scratchLease?.Dispose();
            if (holdsSourceSlot)
                _sourceSlots.Release();
            StartDeferredWarmUpIfQuiet();
        }
    }

    /// <summary>
    /// Remembers a warm-up that gave way, to try it again once no run waits. The retry is an ordinary background run
    /// for the same source: its derivatives are already there, so it only downloads the file and warms the cache, and
    /// it gives way like the first try did. Bounded three times: at most <see cref="MaxWarmUpDeferrals"/> tries per
    /// file, <see cref="MaxDeferredWarmUps"/> waiting at once and <see cref="MaxDeferredWarmUpsPerProject"/> of those
    /// for one project; one over any of them is dropped and logged. A draining engine keeps none.
    /// True when the warm-up now waits.
    /// </summary>
    internal bool DeferWarmUp(MediaDerivativesRequested message)
    {
        string runKey = RunKey(message);
        if (Draining)
        {
            _warmUpDeferrals.TryRemove(runKey, out _);
            _logger.LogInformation(
                "Pass cache warm-up for project {ProjectId} source {SourceStorageKey} is dropped: this engine is draining",
                message.ProjectId, message.SourceStorageKey);
            return false;
        }

        int deferrals = _warmUpDeferrals.AddOrUpdate(runKey, 1, (_, n) => n + 1);
        if (deferrals > MaxWarmUpDeferrals)
        {
            _warmUpDeferrals.TryRemove(runKey, out _);
            _logger.LogInformation(
                "Pass cache warm-up for project {ProjectId} source {SourceStorageKey} gave way {Deferrals} times and is given up",
                message.ProjectId, message.SourceStorageKey, deferrals - 1);
            return false;
        }

        int waiting;
        int waitingOfProject;
        lock (_deferredLock)
        {
            waiting = _deferredWarmUps.Count;
            waitingOfProject = _deferredWarmUps.Count(m => m.ProjectId == message.ProjectId);
            if (waiting < MaxDeferredWarmUps && waitingOfProject < MaxDeferredWarmUpsPerProject)
            {
                _deferredWarmUps.Enqueue(message with { RequestedBy = SweepRequester });
                return true;
            }
        }

        _warmUpDeferrals.TryRemove(runKey, out _);
        _logger.LogInformation(
            "Pass cache warm-up for project {ProjectId} source {SourceStorageKey} is dropped: {Waiting} are already waiting, " +
            "{WaitingOfProject} of them for this project",
            message.ProjectId, message.SourceStorageKey, waiting, waitingOfProject);
        return false;
    }

    /// <summary>True once this engine was told to drain. It stays true.</summary>
    private bool Draining => _drain?.DrainToken.IsCancellationRequested == true;

    /// <summary>
    /// True while a warm-up must give way: a run waits for a source slot, a workflow step waits for scratch space
    /// (the run that warms still holds its own reservation), or this engine is draining.
    /// </summary>
    private bool OtherWorkWaits() => _sourceSlots.HasWaiters || _scratchGuard?.HasWaiters == true || Draining;

    /// <summary>
    /// Starts one waiting warm-up when nothing else wants the engine: no run holds or waits for a source slot, no
    /// workflow step waits for scratch space and no other deferred warm-up is running. Called at the end of every
    /// run, so a burst of uploads is warmed one file at a time once its derivatives are done. A draining engine
    /// starts none and forgets the ones that wait: the engine that took over warms on its own requests.
    /// </summary>
    internal void StartDeferredWarmUpIfQuiet()
    {
        if (_lifetimeCts.IsCancellationRequested)
            return;
        if (Draining)
        {
            DropDeferredWarmUps();
            return;
        }

        if (DeferredWarmUpCount == 0 || !_sourceSlots.IsIdle)
            return;

        // No run ends while a step waits for space, so nothing would call this again: look again in a while.
        if (_scratchGuard?.HasWaiters == true)
        {
            RecheckDeferredWarmUpsLater();
            return;
        }

        if (Interlocked.CompareExchange(ref _deferredWarmUpsRunning, 1, 0) != 0)
            return;

        MediaDerivativesRequested? next;
        lock (_deferredLock)
        {
            _deferredWarmUps.TryDequeue(out next);
        }

        if (next is null)
        {
            Volatile.Write(ref _deferredWarmUpsRunning, 0);
            return;
        }

        Interlocked.Increment(ref _active);
        _ = Task.Run(
            async () =>
            {
                try
                {
                    // The request was kept in memory, possibly for a long time. The file may have been moved,
                    // replaced or deleted since, or its project deleted: such a request is dropped, as the consumer
                    // would drop it, before anything is downloaded.
                    if (await IsStillCurrentAsync(next, _lifetimeCts.Token).ConfigureAwait(false))
                    {
                        await RunAsync(next, _lifetimeCts.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Deferred pass cache warm-up for project {ProjectId} source {SourceStorageKey} is dropped: the file changed or is gone",
                            next.ProjectId, next.SourceStorageKey);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex, "Deferred pass cache warm-up for project {ProjectId} source {SourceStorageKey} ended unexpectedly",
                        next.ProjectId, next.SourceStorageKey);
                }
                finally
                {
                    ForgetDeferralsUnlessWaiting(RunKey(next));
                    Interlocked.Decrement(ref _active);
                    Volatile.Write(ref _deferredWarmUpsRunning, 0);

                    // That run's own end saw this one still marked as running, so look again now that it is not.
                    StartDeferredWarmUpIfQuiet();
                }
            },
            CancellationToken.None);
    }

    /// <summary>The consumer's own check, for a request this dispatcher kept. A failed read counts as not current. Never throws.</summary>
    private async Task<bool> IsStillCurrentAsync(MediaDerivativesRequested message, CancellationToken ct)
    {
        try
        {
            return await MediaDerivativesConsumer.IsStillCurrentAsync(_dbFactory, message, _logger, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(
                ex, "Could not confirm the file of a deferred pass cache warm-up for project {ProjectId}; it is dropped", message.ProjectId);
            return false;
        }
    }

    /// <summary>
    /// A replay that ended without waiting again (it finished, or it stopped early: withheld, no scratch space, a
    /// failed download) leaves no count behind. The count is kept while the same request still waits.
    /// </summary>
    private void ForgetDeferralsUnlessWaiting(string runKey)
    {
        lock (_deferredLock)
        {
            if (_deferredWarmUps.Any(m => string.Equals(RunKey(m), runKey, StringComparison.Ordinal)))
                return;
        }

        _warmUpDeferrals.TryRemove(runKey, out _);
    }

    /// <summary>Forgets every waiting warm-up (drain).</summary>
    private void DropDeferredWarmUps()
    {
        int dropped;
        lock (_deferredLock)
        {
            dropped = _deferredWarmUps.Count;
            _deferredWarmUps.Clear();
        }

        _warmUpDeferrals.Clear();
        if (dropped > 0)
            _logger.LogInformation("Dropped {Dropped} waiting pass cache warm-ups: this engine is draining", dropped);
    }

    /// <summary>Calls <see cref="StartDeferredWarmUpIfQuiet"/> once more after <see cref="DeferredRecheckInterval"/>. At most one such call is pending.</summary>
    private void RecheckDeferredWarmUpsLater()
    {
        if (Interlocked.CompareExchange(ref _deferredRecheckPending, 1, 0) != 0)
            return;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(DeferredRecheckInterval, _lifetimeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                finally
                {
                    Volatile.Write(ref _deferredRecheckPending, 0);
                }

                StartDeferredWarmUpIfQuiet();
            },
            CancellationToken.None);
    }

    /// <summary>For tests: completes once no deferred warm-up waits or runs.</summary>
    internal async Task DeferredWarmUpsSettledAsync(CancellationToken ct = default)
    {
        while (DeferredWarmUpCount != 0 || Volatile.Read(ref _deferredWarmUpsRunning) != 0)
            await Task.Delay(20, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The pass cache warm-up of a run whose request asked for it (<see cref="MediaDerivativesRequested.AnalyzeForWorkflows"/>),
    /// for a video source only. It runs inside the run that the consumer and the checks above already let through
    /// (the file's scan is settled, the project's outputs are not held), while the source is still on disk, and it
    /// takes a derivative slot like one more derivative. It never throws and never changes what the run reported.
    /// It is the only part of a run that gives way: it does not start while other work waits (a run for a source
    /// slot, a workflow step for scratch space, or a drain: <see cref="OtherWorkWaits"/>), starts no further pass
    /// once some does, and stops the pass it is in within <see cref="WarmUpYieldPollInterval"/>. The passes that
    /// finished stay in the cache. A warm-up that gave way is tried again when the engine is quiet
    /// (<see cref="DeferWarmUp"/>), but only when it could run at all: a source the warm-up leaves alone is never kept.
    /// </summary>
    private async Task WarmPassCacheAsync(
        MediaDerivativesRequested message, string localSourcePath, MediaProbeResult source, Activity? activity, CancellationToken ct)
    {
        if (!WarmUpWanted(message) || _warmUp is not { } warmUp)
            return;

        bool holdsSlot = false;
        try
        {
            // This run still holds its source slot and its scratch reservation, and other work waits behind them.
            // The warm-up is extra work for one project: it does not start while such work waits.
            if (OtherWorkWaits())
            {
                // The probe is at hand: a source the warm-up would leave alone (not a video a step analyses, one
                // that takes the analysis copy, no known version) is not kept for later. Its retry would download
                // the whole file again and then do nothing.
                if (warmUp.RefusalFor(message.SourceStorageKey, message.SourceEtag, source) is { } refusal)
                {
                    activity?.SetTag("reelforge.media.pass_cache_warm_up", refusal.ToString());
                    _warmUpDeferrals.TryRemove(RunKey(message), out _);
                    return;
                }

                activity?.SetTag("reelforge.media.pass_cache_warm_up", SourcePassWarmUpOutcome.Yielded.ToString());
                DeferWarmUp(message);
                return;
            }

            // The hold may have started while the derivatives were made. Held or unreadable = nothing is analysed.
            if (await MediaDerivativesConsumer.IsWithheldAsync(_dbFactory, message, ct).ConfigureAwait(false))
                return;

            await _derivativeSlots.WaitAsync(ct).ConfigureAwait(false);
            holdsSlot = true;

            // Other work that starts to wait while a pass is running stops that pass: the watcher cancels it, and
            // records that it did. The waiting run can have its slot (another one) before the stopped pass has
            // ended, so "does something still wait" is the wrong question by then: the record decides.
            using CancellationTokenSource giveWay = CancellationTokenSource.CreateLinkedTokenSource(ct);
            int gaveWay = 0;
            Task watcher = CancelWhenOtherWorkWaitsAsync(giveWay, () => Volatile.Write(ref gaveWay, 1));
            SourcePassWarmUpOutcome outcome;
            try
            {
                // The passes reserve scratch space themselves. Their own wait for it is not other work.
                using IDisposable quiet = ScratchDiskGuard.QuietWaits();
                outcome = await warmUp
                    .RunAsync(
                        message.ProjectId, message.SourceStorageKey, message.SourceEtag, localSourcePath, source, giveWay.Token,
                        shouldYield: () => Volatile.Read(ref gaveWay) == 1 || OtherWorkWaits())
                    .ConfigureAwait(false);
            }
            finally
            {
                await giveWay.CancelAsync().ConfigureAwait(false);
                await watcher.ConfigureAwait(false);
            }

            activity?.SetTag("reelforge.media.pass_cache_warm_up", outcome.ToString());
            if (outcome == SourcePassWarmUpOutcome.Yielded && !ct.IsCancellationRequested)
                DeferWarmUp(message);
            else
                _warmUpDeferrals.TryRemove(RunKey(message), out _);
        }
        catch (Exception ex)
        {
            // Shutdown while it waited for its slot, or anything unforeseen: the derivatives are done, so this only logs.
            _logger.LogInformation(
                ex, "Pass cache warm-up for project {ProjectId} source {SourceStorageKey} did not run", message.ProjectId, message.SourceStorageKey);
        }
        finally
        {
            if (holdsSlot)
                _derivativeSlots.Release();
        }
    }

    /// <summary>True when a run for <paramref name="message"/> ends with the pass cache warm-up: the request asked for it, it is switched on, and the source is a video.</summary>
    private bool WarmUpWanted(MediaDerivativesRequested message) =>
        message.AnalyzeForWorkflows && _warmUp is { Enabled: true } &&
        ClassifyMimeType(message.MimeType) == MediaDerivativeSourceClass.Video;

    /// <summary>How often a running warm-up looks for other work that waits (<see cref="OtherWorkWaits"/>).</summary>
    internal static readonly TimeSpan WarmUpYieldPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Cancels <paramref name="giveWay"/> as soon as other work waits, after it called <paramref name="onGaveWay"/>.
    /// Ends when it is cancelled, by this or by the caller. Never throws.
    /// </summary>
    private async Task CancelWhenOtherWorkWaitsAsync(CancellationTokenSource giveWay, Action onGaveWay)
    {
        try
        {
            while (!giveWay.IsCancellationRequested)
            {
                if (OtherWorkWaits())
                {
                    onGaveWay();
                    await giveWay.CancelAsync().ConfigureAwait(false);
                    return;
                }

                await Task.Delay(WarmUpYieldPollInterval, giveWay.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The warm-up ended, or the run was cancelled.
        }
    }

    // ---------------------------------------------------------------------------------------------
    // One derivative: cache decision -> produce -> upload -> insert
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The finished proxy of this run, kept in the run's own download directory (deleted at the end of the
    /// run) so the filmstrip and stills passes read a small file instead of decoding the big source again.
    /// </summary>
    internal sealed class ProxyHandoff
    {
        public string? Directory { get; set; }

        public string? Path { get; private set; }

        public void TryKeep(string producedProxyPath, ILogger logger)
        {
            if (Directory is null)
                return;
            try
            {
                string target = System.IO.Path.Combine(Directory, "proxy-read.mp4");
                File.Copy(producedProxyPath, target, overwrite: true);
                Path = target;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not keep the finished proxy for later passes; they read the source");
            }
        }
    }

    internal static string RunKey(MediaDerivativesRequested message) =>
        string.Join(
            '|',
            message.ProjectId.ToString("D"),
            message.SourceStorageKey,
            message.SourceEtag ?? "",
            string.Join(',', (message.Kinds ?? Array.Empty<MediaDerivativeKind>()).OrderBy(k => (int)k)),
            string.Join(',', message.StillTimesSec ?? []),
            message.ProxyVariant.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private async Task<DerivativeOutcome> ProduceOneDerivativeAsync(
        MediaDerivativesRequested message,
        MediaDerivativeSpec spec,
        string localSourcePath,
        string effectiveEtag,
        string? sourceProbeJson,
        ProxyHandoff handoff,
        CancellationToken ct)
    {
        string cacheKey = MediaDerivativeCacheKey.ComputeCacheKey(message.SourceStorageKey, effectiveEtag, spec);

        // Cheap pre-check outside the shared-production slot: a repeat request for an unchanged
        // source is the common case and must not even queue.
        if (await IsAlreadyProducedAsync(message.ProjectId, cacheKey, ct).ConfigureAwait(false))
        {
            Report(message, cacheKey, MediaDerivativeProgressStage.Skipped, detail: "Cache hit — a live media_derivatives row already owns this cache key.");
            return DerivativeOutcome.CacheHit();
        }

        return await RunOnceAsync(
            cacheKey,
            token => ProduceUploadAndInsertAsync(message, spec, localSourcePath, effectiveEtag, cacheKey, sourceProbeJson, handoff, token),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Shares one production between concurrent deliveries for the same cache key — see the class
    /// remark on idempotency.
    /// </summary>
    private async Task<DerivativeOutcome> RunOnceAsync(
        string cacheKey,
        Func<CancellationToken, Task<DerivativeOutcome>> produce,
        CancellationToken ct)
    {
        Lazy<Task<DerivativeOutcome>> lazy = _inFlight.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<DerivativeOutcome>>(
                () => produce(ct),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // Only the entry this caller owns is removed, so a later request that has already read the
            // same Lazy keeps the task it was given. A redundant production is not a correctness
            // problem anyway: the database's unique index is the real arbiter.
            _inFlight.TryRemove(
                new KeyValuePair<string, Lazy<Task<DerivativeOutcome>>>(cacheKey, lazy));
        }
    }

    private async Task<DerivativeOutcome> ProduceUploadAndInsertAsync(
        MediaDerivativesRequested message,
        MediaDerivativeSpec spec,
        string localSourcePath,
        string effectiveEtag,
        string cacheKey,
        string? sourceProbeJson,
        ProxyHandoff handoff,
        CancellationToken ct)
    {
        // In front of T-301's own low-priority gate, which is what makes the priority rule hold
        // across concurrent requests rather than only within one.
        await _derivativeSlots.WaitAsync(ct).ConfigureAwait(false);

        string? workDirectory = null;
        try
        {
            // Re-check inside the slot: a competing run may have committed its row while we queued.
            if (await IsAlreadyProducedAsync(message.ProjectId, cacheKey, ct).ConfigureAwait(false))
            {
                Report(message, cacheKey, MediaDerivativeProgressStage.Skipped, detail: "Cache hit — another run committed this cache key while this one queued.");
                return DerivativeOutcome.CacheHit();
            }

            var progress = new SynchronousProgress<MediaDerivativeStage>(
                stage => Report(message, cacheKey, MediaDerivativeProgressUpdate.From(stage)));

            MediaDerivativeResult result = await _derivatives.ProduceAsync(
                message.ProjectId,
                message.SourceStorageKey,
                effectiveEtag,
                spec,
                localSourcePath,
                progress,
                ct,
                handoff.Path).ConfigureAwait(false);

            // Filmstrip and stills keep reading the PLAIN proxy: a converted one is never handed on.
            if (spec.Slug == MediaDerivativeKindSlug.Proxy && spec.Variant == 0 && result.Artifacts.Count > 0)
                handoff.TryKeep(result.Artifacts[0].LocalPath, _logger);

            // The service computes the key from the same three inputs this layer did. They can only
            // disagree if one of them changed under us, and a row whose cache_key did not describe its
            // own bytes is the single failure this whole design exists to prevent.
            if (!string.Equals(result.CacheKey, cacheKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Produced derivative reported cache key '{result.CacheKey}' but was requested under " +
                    $"'{cacheKey}'. Refusing to persist a row whose identity does not describe its bytes.");
            }

            if (result.Artifacts.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Produced derivative '{spec.Slug}' variant {spec.Variant} returned no artifacts to upload.");
            }

            workDirectory = Path.GetDirectoryName(Path.GetFullPath(result.Artifacts[0].LocalPath));

            // Upload EVERY artifact — the sheets and the index, the three peaks levels and their
            // sidecar — so nothing produced is stranded on the scratch volume. The file name is the
            // caller-supplied relative layout UploadArtifactAsync preserves verbatim under the
            // category prefix, which is what puts the objects under
            // projects/{projectId}/agentFiles/media-derivatives/{sourceHash}/.
            string sourceHash = MediaDerivativeCacheKey.ComputeSourceHash(message.SourceStorageKey, effectiveEtag);
            var uploadedKeys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (MediaDerivativeArtifact artifact in result.Artifacts)
            {
                string uploadedKey = await _workspace.UploadArtifactAsync(
                    message.ProjectId,
                    artifact.LocalPath,
                    $"{ScratchSubdirectory}/{sourceHash}/{artifact.FileName}",
                    artifact.ContentType,
                    ct).ConfigureAwait(false);

                uploadedKeys[artifact.FileName] = uploadedKey;
            }

            MediaDerivativeArtifact primary = SelectPrimaryArtifact(result);
            string primaryStorageKey = uploadedKeys[primary.FileName];

            // A filmstrip's sprite sheets are not self-describing: which thumbnail index a given sheet
            // starts at, and at what source time, lives in the JSON sidecar uploaded just above. Its
            // object key is recorded on the sheet row so a client fetches that document by a key the
            // row owns, instead of re-deriving the parameter hash itself — a derivation the plan's
            // trust boundary keeps server-side and that a builder-version change would break.
            string? indexStorageKey = ResolveIndexStorageKey(spec, result, uploadedKeys, out string? indexNote);

            MediaDerivative row = BuildRow(
                message,
                spec,
                result,
                effectiveEtag,
                primaryStorageKey,
                primary.ContentType,
                PrimarySizeBytes(primary),
                _bucketName,
                indexStorageKey,
                sourceProbeJson);

            bool inserted = await TryInsertAsync(row, ct).ConfigureAwait(false);
            if (!inserted)
            {
                // The unique index rejected this insert: another writer won the race. That is the
                // designed outcome, not an error — the object is uploaded, the row exists, and this
                // run simply does not own it.
                Report(message, cacheKey, MediaDerivativeProgressStage.Skipped, detail: "Another writer committed this cache key first; this run's row was discarded.");
                return DerivativeOutcome.CacheHit();
            }

            _logger.LogInformation(
                "Media derivative produced: kind={Kind}, variant={Variant}, cacheKey={CacheKey}, " +
                "storageKey={StorageKey}, size={SizeBytes} bytes",
                row.Kind, spec.Variant, cacheKey, primaryStorageKey, row.SizeBytes);

            Report(
                message,
                cacheKey,
                MediaDerivativeProgressStage.Done,
                detail: indexNote is null
                    ? $"Produced {row.Kind} variant {spec.Variant}"
                    : $"Produced {row.Kind} variant {spec.Variant}; {indexNote}",
                percentComplete: 100);

            return DerivativeOutcome.Produced();
        }
        catch (MediaDerivativeUnsupportedException ex)
        {
            // Not a failure. The source's own measured properties make this derivative impossible and
            // NOTHING was run — reporting it as Failed is what made a short clip log a failed stage
            // for the 30 s filmstrip density on every single request, forever, for a workload that
            // could never have run. Information, because the outcome is the correct one.
            _logger.LogInformation(
                "Media derivative {Slug} variant {Variant} skipped as unsupported by the source for " +
                "project {ProjectId} source {SourceStorageKey}: {Reason}",
                spec.Slug, spec.Variant, message.ProjectId, message.SourceStorageKey, ex.Message);

            Report(message, cacheKey, MediaDerivativeProgressStage.Skipped, detail: ex.Message);
            return DerivativeOutcome.Unsupported();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown. Leave the row absent; the next request retries. Not reported as a failure,
            // because nothing is wrong with the derivative.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Media derivative {Slug} variant {Variant} failed for project {ProjectId} source {SourceStorageKey}",
                spec.Slug, spec.Variant, message.ProjectId, message.SourceStorageKey);

            Report(message, cacheKey, MediaDerivativeProgressStage.Failed, detail: ex.Message);
            return DerivativeOutcome.Failed();
        }
        finally
        {
            // The production service deliberately does NOT clean up after itself — its files have to
            // outlive the call, because uploading happens here. The parent of the first artifact is
            // that run's work directory, so it is derived rather than guessed at with a glob that
            // could delete a concurrent run's in-progress files.
            TryDeleteDirectory(workDirectory);
            _derivativeSlots.Release();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Persistence
    // ---------------------------------------------------------------------------------------------

    private async Task<bool> IsAlreadyProducedAsync(Guid projectId, string cacheKey, CancellationToken ct)
    {
        await using WorkflowEngineDbContext db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        return await db.MediaDerivatives
            .AsNoTracking()
            .AnyAsync(d => d.ProjectId == projectId && d.CacheKey == cacheKey, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts one row, returning false when the database's cache-key index rejected it.
    /// </summary>
    private async Task<bool> TryInsertAsync(MediaDerivative row, CancellationToken ct)
    {
        await using WorkflowEngineDbContext db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        try
        {
            db.MediaDerivatives.Add(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex)
        {
            _logger.LogInformation(
                ex,
                "Insert of media derivative cache key {CacheKey} was rejected; treating the existing row as the owner",
                row.CacheKey);
            return false;
        }
    }

    /// <summary>
    /// Resolves the filmstrip index sidecar's storage key for a produced derivative, or null when it
    /// has none — which is every kind but <c>Filmstrip</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The key comes from the upload receipt, not from the binding's prediction.</b>
    /// <see cref="MediaDerivativeIndexBinding"/> computes where the sidecar <i>should</i> be; what is
    /// persisted is the key <c>UploadArtifactAsync</c> actually returned for that artifact, so a row
    /// can never name an object that was not written. The two are compared, and a divergence is logged
    /// rather than silently preferred either way — it would mean the object store laid the artifact out
    /// somewhere other than the prefix the producer computed.
    /// </para>
    /// <para>
    /// <b>A leaf mismatch is a finding and produces NO pointer.</b> It means the JSON artifact the run
    /// produced is not the leaf <see cref="FilmstripIndex.IndexLeaf"/> predicts for this spec, so
    /// pointing at it would name an object the builder never wrote <i>as the index</i>. The row is
    /// still written, with a null pointer, which the column's contract defines as "not available yet".
    /// </para>
    /// </remarks>
    private string? ResolveIndexStorageKey(
        MediaDerivativeSpec spec,
        MediaDerivativeResult result,
        IReadOnlyDictionary<string, string> uploadedKeys,
        out string? note)
    {
        note = null;

        MediaDerivativeIndexResolution resolution = MediaDerivativeIndexBinding.Resolve(spec, result);

        if (resolution.Status == MediaDerivativeIndexBindingStatus.LeafMismatch)
        {
            _logger.LogError(
                "Filmstrip index leaf mismatch for {Slug} variant {Variant}: the produced JSON artifact is " +
                "'{ActualLeaf}' but FilmstripIndex.IndexLeaf predicts '{PredictedLeaf}' for this spec. No " +
                "index_storage_key was persisted; the filmstrip row is unaffected.",
                spec.Slug,
                spec.Variant,
                resolution.ActualIndexLeaf,
                MediaDerivativeIndexBinding.PredictIndexLeaf(spec));

            note = $"filmstrip index NOT bound: produced leaf '{resolution.ActualIndexLeaf}' is not the predicted one";
            return null;
        }

        if (resolution.Reference is not { } reference)
            return null;

        if (!uploadedKeys.TryGetValue(reference.IndexLeaf, out string? uploadedKey))
        {
            // The binding found an index artifact the upload pass did not record. Unreachable while
            // both read the same artifact list, and worth a loud line rather than a silent null if it
            // ever stops being true.
            _logger.LogWarning(
                "Filmstrip index artifact '{IndexLeaf}' was not among the uploaded keys for {Slug} variant {Variant}; " +
                "no index_storage_key was persisted",
                reference.IndexLeaf,
                spec.Slug,
                spec.Variant);
            return null;
        }

        if (!string.Equals(uploadedKey, reference.IndexStorageKey, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Filmstrip index uploaded to '{UploadedKey}' but the producer predicted '{PredictedKey}'; " +
                "persisting the uploaded key, which is the one that addresses a real object",
                uploadedKey,
                reference.IndexStorageKey);
        }

        return uploadedKey;
    }

    /// <summary>
    /// Builds the row. Pure, and <c>internal</c> so a test can pin every persisted field without a
    /// database — the identity fields especially, since one wrong value here is exactly the cache
    /// collision this wave is reviewed for.
    /// </summary>
    /// <remarks>
    /// <b>The T-305 seam, now used.</b> This is the one place a derivative becomes a row, so it is
    /// where the filmstrip index's storage key is recorded — as
    /// <paramref name="indexStorageKey"/>, which every other kind leaves null. The index object was
    /// already uploaded (every artifact is), so no second writer was needed.
    /// </remarks>
    internal static MediaDerivative BuildRow(
        MediaDerivativesRequested message,
        MediaDerivativeSpec spec,
        MediaDerivativeResult result,
        string effectiveEtag,
        string primaryStorageKey,
        string primaryContentType,
        long primarySizeBytes,
        string bucketName,
        string? indexStorageKey = null,
        string? sourceProbeJson = null)
    {
        MediaSourceKind sourceKind = message.SourceRef?.Kind ?? MediaSourceKind.ProjectFile;
        bool isProjectFile = sourceKind == MediaSourceKind.ProjectFile;

        return new MediaDerivative
        {
            Id = Guid.NewGuid(),
            ProjectId = message.ProjectId,

            // Exactly one of the two source pointers is set, and which one is not a guess: SourceKind
            // says so. In particular an unregistered render has no ProjectFile row to point at.
            SourceProjectFileId = isProjectFile ? message.SourceProjectFileId : null,
            SourceStepResultId = isProjectFile ? null : message.SourceRef?.SourceId,

            SourceKind = sourceKind,

            // The EFFECTIVE etag, never the published one — see MediaDerivativeDispatcher.ResolveEffectiveEtag.
            SourceStorageKey = message.SourceStorageKey,
            SourceETag = effectiveEtag,

            ParamsHash = MediaDerivativeCacheKey.ComputeParamsHash(spec),
            ParamsJson = spec.ParamsJson,
            BuilderVersion = spec.BuilderVersion,
            CacheKey = result.CacheKey,

            Kind = ToPersistedKind(spec.Slug),
            StorageKey = primaryStorageKey,
            // Null for every kind but a filmstrip — see ResolveIndexStorageKey.
            IndexStorageKey = indexStorageKey,
            StorageBucket = bucketName,
            MimeType = primaryContentType,
            SizeBytes = primarySizeBytes,
            DurationSec = result.DurationSec,
            Width = result.Width,
            Height = result.Height,
            SourceProbeJson = sourceProbeJson,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Revision of the stored probe's meaning. 2 = width/height are the frames ffmpeg DECODES (a Display
    /// Matrix quarter turn swaps them, #772); a row without this marker holds coded sizes. The marker lives
    /// inside the <c>source_probe_json</c> text (API-owned column, engine-written), so no migration.
    /// </summary>
    internal const int ProbeRevision = 2;

    internal static string ToStoredProbeJson(MediaProbeResult source)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(ToStoredProbe(source).ToJson())!.AsObject();
        node["probeRev"] = ProbeRevision;
        return node.ToJsonString();
    }

    /// <summary>
    /// Overwrite a stored probe when it is missing, unreadable, carries no <c>probeRev</c> at the current
    /// revision (written before the rotation rule: its size may be the coded one), or differs from the
    /// fresh probe in width/height. A current-revision row with the same size is left alone, so an
    /// unrotated upload is written at most once more (to gain the marker) and never churns after that.
    /// </summary>
    internal static bool ShouldReplaceStoredProbe(string? stored, string fresh)
    {
        if (string.IsNullOrWhiteSpace(stored)) return true;
        try
        {
            var old = System.Text.Json.Nodes.JsonNode.Parse(stored)?.AsObject();
            var now = System.Text.Json.Nodes.JsonNode.Parse(fresh)!.AsObject();
            if (old is null) return true;
            int rev = old["probeRev"] is { } r && r.GetValueKind() == System.Text.Json.JsonValueKind.Number ? r.GetValue<int>() : 0;
            if (rev < ProbeRevision) return true;
            // log-footage-color: a stored VIDEO probe written before the colour facts (make, model, bit depth, pixel
            // format) has no such keys, so its camera-profile detection is "unknown". Treat it as replaceable so the next
            // derivative run for that source refreshes it. A fresh probe always writes the keys (null when ffprobe has no
            // value), so this never churns. Limit: an old upload only gains detection once its derivatives run again.
            if (old["videoCodec"] is { } vc && vc.GetValueKind() == System.Text.Json.JsonValueKind.String &&
                (!old.ContainsKey("bitDepth") || !old.ContainsKey("make") || !old.ContainsKey("model") || !old.ContainsKey("pixFmt")))
                return true;
            return !Equals(old["width"]?.ToJsonString(), now["width"]?.ToJsonString()) ||
                   !Equals(old["height"]?.ToJsonString(), now["height"]?.ToJsonString());
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return true;
        }
    }

    /// <summary>
    /// The stored form of a source probe. Pure. <see cref="MediaSourceProbe.HasAudio"/> follows the
    /// one rule the proxy and the peaks derivative already use: an audio codec was reported.
    /// </summary>
    internal static MediaSourceProbe ToStoredProbe(MediaProbeResult source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new MediaSourceProbe(
            source.DurationSec,
            source.FpsNum,
            source.FpsDen,
            source.Width,
            source.Height,
            string.IsNullOrWhiteSpace(source.VideoCodec) ? null : source.VideoCodec,
            string.IsNullOrWhiteSpace(source.AudioCodec) ? null : source.AudioCodec,
            string.IsNullOrWhiteSpace(source.Container) ? null : source.Container,
            source.AudioChannels,
            source.AudioSampleRate,
            source.BitrateBps,
            HasAudio: !string.IsNullOrWhiteSpace(source.AudioCodec),
            ColorTransfer: source.ColorTransfer,
            ColorPrimaries: source.ColorPrimaries,
            Hdr: ReelBolt.WorkflowEngine.Services.Video.TimelineRender.HdrToneMap.KindOf(source.ColorTransfer),
            AltersPixelsOnDecode: source.AltersPixelsOnDecode,
            ColorSpace: source.ColorSpace,
            ColorRange: source.ColorRange,
            PixFmt: source.PixFmt,
            BitDepth: source.BitDepth,
            Make: source.Make,
            Model: source.Model);
    }

    // ---------------------------------------------------------------------------------------------
    // Pure helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The derivative set to produce, as <c>(slug, variant)</c> pairs: the source's MIME class
    /// decides the base set and the message's <c>Kinds</c> can only narrow it.
    /// </summary>
    internal static IReadOnlyList<(MediaDerivativeKindSlug Slug, int Variant)> ResolveSpecPlan(
        string? mimeType,
        IReadOnlyList<MediaDerivativeKind>? kinds)
    {
        var basePlan = new List<(MediaDerivativeKindSlug Slug, int Variant)>();

        switch (ClassifyMimeType(mimeType))
        {
            case MediaDerivativeSourceClass.Video:
                // The seconds-long kinds go FIRST: the source probe is stored with the first row written, and the
                // proxy of a long 4K clip takes many minutes, so a file's length must not wait behind it (the Run
                // gate reads it from the stored probe). Filmstrip and stills read the proxy/source after it.
                for (int variant = 0; variant < MediaDerivativeSpec.PeaksLodMs.Length; variant++)
                    basePlan.Add((MediaDerivativeKindSlug.WaveformPeaks, variant));
                basePlan.Add((MediaDerivativeKindSlug.KeyframeIndex, 0));
                basePlan.Add((MediaDerivativeKindSlug.Proxy, 0));
                for (int variant = 0; variant < MediaDerivativeSpec.FilmstripDensitySeconds.Length; variant++)
                    basePlan.Add((MediaDerivativeKindSlug.Filmstrip, variant));
                basePlan.Add((MediaDerivativeKindSlug.Stills, 0));
                basePlan.Add((MediaDerivativeKindSlug.StabilizedProxy, 0));
                break;

            case MediaDerivativeSourceClass.Audio:
                for (int variant = 0; variant < MediaDerivativeSpec.PeaksLodMs.Length; variant++)
                    basePlan.Add((MediaDerivativeKindSlug.WaveformPeaks, variant));
                break;

            default:
                // Image and unknown MIME types have no derivatives. Callers log and skip rather than
                // guessing at a set from the file's contents.
                break;
        }

        // Stills and the stabilised preview proxy are on demand only: a request that names nothing (the
        // default set) never gets them.
        if (kinds is null || kinds.Count == 0)
            return basePlan.Where(entry => entry.Slug is not (MediaDerivativeKindSlug.Stills or MediaDerivativeKindSlug.StabilizedProxy)).ToList();

        var requested = new HashSet<MediaDerivativeKind>(kinds);
        return basePlan.Where(entry => requested.Contains(ToPersistedKind(entry.Slug))).ToList();
    }

    /// <summary>
    /// Adds the converted proxy (<c>(Proxy, 1..N)</c>) right after the plain one: the variant the request named, else the
    /// profile <paramref name="autoProfile"/> reports for this probe. Never for an HDR source (it keeps its own tone-map),
    /// never when the plan has no plain proxy, never for an unknown variant. Pure.
    /// </summary>
    internal static IReadOnlyList<(MediaDerivativeKindSlug Slug, int Variant)> WithConvertedProxy(
        IReadOnlyList<(MediaDerivativeKindSlug Slug, int Variant)> plan,
        MediaProbeResult source,
        int requestedVariant,
        Func<MediaProbeResult, string?>? autoProfile)
    {
        int plain = -1;
        for (int i = 0; i < plan.Count; i++)
            if (plan[i] == (MediaDerivativeKindSlug.Proxy, 0)) plain = i;
        if (plain < 0 || HdrToneMap.KindOf(source) is not null) return plan;

        int variant = requestedVariant;
        if (variant == 0 && autoProfile?.Invoke(source) is { } id)
            variant = CameraProfileVariants.VariantOf(id);
        if (!CameraProfileVariants.TryProfileOf(variant, out _)) return plan;

        var list = plan.ToList();
        list.Insert(plain + 1, (MediaDerivativeKindSlug.Proxy, variant));
        return list;
    }

    /// <summary>
    /// The production auto-profile selector: the profile the camera detector names with <see cref="DetectionConfidence.High"/>,
    /// else null. High is the only confidence safe to apply without asking — <see cref="DetectionConfidence.Medium"/> is a
    /// suggestion the viewer surfaces and the user accepts, so it must not spend a proxy encode on its own. An HDR source is
    /// refused earlier by <see cref="WithConvertedProxy"/> (and the detector itself never matches an HDR transfer).
    /// </summary>
    public static string? AutoProfileFromProbe(MediaProbeResult source)
    {
        CameraProfileHint hint = CameraProfileDetector.Detect(new CameraFacts(
            Transfer: source.ColorTransfer,
            Primaries: source.ColorPrimaries,
            Matrix: source.ColorSpace,
            Range: source.ColorRange,
            PixFmt: source.PixFmt,
            BitDepth: source.BitDepth,
            VideoCodec: source.VideoCodec,
            Container: source.Container,
            Make: source.Make,
            Model: source.Model,
            Software: source.Software));
        return hint.Confidence == DetectionConfidence.High ? hint.ProfileId : null;
    }

    /// <summary>
    /// The three kinds whose parameters are fully static — everything except the proxy, whose target
    /// height, GOP length and audio expectation are functions of the source's own measurements and so
    /// cannot be known before it is probed.
    /// </summary>
    internal static MediaDerivativeSpec ResolveStaticSpec(MediaDerivativeKindSlug slug, int variant) => slug switch
    {
        MediaDerivativeKindSlug.Filmstrip => MediaDerivativeSpec.ForFilmstrip(variant),
        MediaDerivativeKindSlug.WaveformPeaks => MediaDerivativeSpec.ForWaveformPeaks(variant),
        MediaDerivativeKindSlug.KeyframeIndex => MediaDerivativeSpec.ForKeyframeIndex(),
        MediaDerivativeKindSlug.Stills => MediaDerivativeSpec.ForStills([]),
        _ => throw new ArgumentOutOfRangeException(
            nameof(slug), slug, "This derivative kind's parameters depend on the source; it must be probed, not resolved statically.")
    };

    /// <summary>Maps a MIME type onto the family of derivatives it can have. Pure.</summary>
    internal static MediaDerivativeSourceClass ClassifyMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
            return MediaDerivativeSourceClass.Unknown;

        if (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return MediaDerivativeSourceClass.Video;

        if (mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return MediaDerivativeSourceClass.Audio;

        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return MediaDerivativeSourceClass.Image;

        return MediaDerivativeSourceClass.Unknown;
    }

    /// <summary>
    /// The media-owned slug as the shared enum the table persists. The two are the same closed
    /// catalogue spelled the same way; <c>MediaDerivativeIdentityTests</c> pins that naming, so a
    /// rename on either side fails there instead of silently sharing no cache.
    /// </summary>
    internal static MediaDerivativeKind ToPersistedKind(MediaDerivativeKindSlug slug) => slug switch
    {
        MediaDerivativeKindSlug.Proxy => MediaDerivativeKind.Proxy,
        MediaDerivativeKindSlug.Filmstrip => MediaDerivativeKind.Filmstrip,
        MediaDerivativeKindSlug.WaveformPeaks => MediaDerivativeKind.WaveformPeaks,
        MediaDerivativeKindSlug.KeyframeIndex => MediaDerivativeKind.KeyframeIndex,
        MediaDerivativeKindSlug.Stills => MediaDerivativeKind.Stills,
        MediaDerivativeKindSlug.StabilizedProxy => MediaDerivativeKind.StabilizedProxy,
        _ => throw new ArgumentOutOfRangeException(nameof(slug), slug, "Unknown derivative slug.")
    };

    /// <summary>
    /// The artifact the row's single <c>storage_key</c> points at. One object is addressable per row,
    /// so the choice is part of the contract, and it is the object a client actually consumes to
    /// render that derivative — which is also what keeps each variant's row pointing at its own
    /// object rather than at a document shared with its siblings:
    /// <list type="bullet">
    /// <item><description><b>proxy</b> — the encoded mp4 (its only artifact).</description></item>
    /// <item><description><b>filmstrip</b> — the first sprite sheet, the <c>image/jpeg</c> artifact,
    /// because that is what an editor draws. The JSON index is uploaded alongside it and becomes its
    /// own reference in T-305.</description></item>
    /// <item><description><b>waveform peaks</b> — this variant's own LOD raw file, not the sidecar:
    /// the sidecar is identical for all three levels of detail, so pointing every peaks row at it
    /// would give three rows one shared object and none of them the peaks they name.</description></item>
    /// <item><description><b>keyframe index</b> — the JSON index (its only artifact).</description></item>
    /// </list>
    /// </summary>
    internal static MediaDerivativeArtifact SelectPrimaryArtifact(MediaDerivativeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Artifacts.Count == 0)
            throw new ArgumentException("A produced derivative with no artifacts has no primary object.", nameof(result));

        if (result.Slug is MediaDerivativeKindSlug.Filmstrip or MediaDerivativeKindSlug.Stills)
        {
            MediaDerivativeArtifact? sheet = result.Artifacts
                .FirstOrDefault(a => a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));
            return sheet ?? result.Artifacts[0];
        }

        if (result.Slug == MediaDerivativeKindSlug.WaveformPeaks)
        {
            int variant = Math.Clamp(result.Variant, 0, MediaDerivativeSpec.PeaksLodMs.Length - 1);
            string suffix = $"-lod{MediaDerivativeSpec.PeaksLodMs[variant]}ms.raw";

            MediaDerivativeArtifact? lod = result.Artifacts
                .FirstOrDefault(a => a.FileName.EndsWith(suffix, StringComparison.Ordinal));
            return lod ?? result.Artifacts[0];
        }

        return result.Artifacts[0];
    }

    /// <summary>
    /// The content-identity component actually persisted: the published ETag when there is one,
    /// otherwise a SHA-256 of the downloaded bytes.
    /// </summary>
    /// <remarks>
    /// <b>An empty ETag must never reach a row.</b> It would make every derivative of every source
    /// whose HEAD failed share the empty component, so a re-uploaded <i>different</i> file would
    /// satisfy an old row — the precise collision this wave is reviewed for. Hashing the downloaded
    /// object is the honest substitute: it is content identity, which is what the column means, and it
    /// is stable across requests because it depends only on the bytes. It streams, so a large source is
    /// never buffered.
    /// </remarks>
    internal static string ResolveEffectiveEtag(string? publishedEtag, string localSourcePath)
    {
        if (!string.IsNullOrWhiteSpace(publishedEtag))
            return publishedEtag;

        ArgumentException.ThrowIfNullOrWhiteSpace(localSourcePath);

        using FileStream stream = File.OpenRead(localSourcePath);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    // ---------------------------------------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Probes the source at <see cref="VideoToolPriority.Low"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> <c>IMediaProbe</c>: that seam has a single, parameterless-priority
    /// overload and therefore schedules at <see cref="VideoToolPriority.Normal"/>, which would let a
    /// derivative's spec probe take precedence over a compile. The argv and the parser are T-301's own
    /// first-party ones, so this is the same probe <c>FfprobeMediaProbe</c> performs — and
    /// <c>The_dispatchers_proxy_spec_matches_the_services_own_resolution</c> pins that it produces the
    /// same spec, so the two can never drift apart.
    /// </remarks>
    /// <summary>
    /// The run's one probe. A plan with a proxy cannot proceed without it (the proxy spec is a
    /// function of the source), so its failure still fails the run exactly as before; any other plan
    /// only loses the stored probe, and its derivatives are attempted regardless.
    /// </summary>
    private sealed class NoScope : IDisposable
    {
        public void Dispose()
        {
        }
    }

    /// <summary>The stored size of the source's project-file row; 0 when unknown (the guard still reserves its headroom).</summary>
    private async Task<long> LookupSourceBytesAsync(MediaDerivativesRequested message, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<ProjectWorkspaceFile> files = await _workspace.ListFilesAsync(message.ProjectId, ct).ConfigureAwait(false);
            return files.FirstOrDefault(f => string.Equals(f.StorageKey, message.SourceStorageKey, StringComparison.Ordinal))?.SizeBytes ?? 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
    }

    private async Task<MediaProbeResult?> TryProbeForRunAsync(
        string localSourcePath,
        IReadOnlyList<(MediaDerivativeKindSlug Slug, int Variant)> plan,
        MediaDerivativesRequested message,
        CancellationToken ct)
    {
        try
        {
            return await ProbeSourceAsync(localSourcePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException &&
                                   !plan.Any(entry => entry.Slug == MediaDerivativeKindSlug.Proxy))
        {
            _logger.LogWarning(
                ex,
                "Could not probe derivative source {SourceStorageKey} for project {ProjectId}; its rows are written without a stored probe",
                message.SourceStorageKey,
                message.ProjectId);
            return null;
        }
    }

    /// <summary>
    /// Writes <paramref name="sourceProbeJson"/> onto every live row of this exact source content
    /// (project, storage key, effective etag) whose stored probe is missing, predates the rotation rule
    /// (no <c>probeRev</c> at <see cref="ProbeRevision"/> or above) or reports a different size
    /// (<see cref="ShouldReplaceStoredProbe"/>). Tracked update rather than a bulk
    /// <c>ExecuteUpdate</c>: a handful of rows per source, and best-effort — a failure here costs the
    /// pool its probe, never the derivatives this run produced.
    /// </summary>
    private async Task BackfillSourceProbeAsync(
        MediaDerivativesRequested message,
        string effectiveEtag,
        string sourceProbeJson,
        CancellationToken ct)
    {
        try
        {
            await using WorkflowEngineDbContext db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            List<MediaDerivative> unprobed = await db.MediaDerivatives
                .Where(d => d.ProjectId == message.ProjectId &&
                            d.SourceStorageKey == message.SourceStorageKey &&
                            d.SourceETag == effectiveEtag)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            unprobed = [.. unprobed.Where(d => ShouldReplaceStoredProbe(d.SourceProbeJson, sourceProbeJson))];
            if (unprobed.Count == 0) return;

            foreach (MediaDerivative row in unprobed)
                row.SourceProbeJson = sourceProbeJson;

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Could not store the probe on existing derivative rows of {SourceStorageKey} in project {ProjectId}",
                message.SourceStorageKey,
                message.ProjectId);
        }
    }

    private async Task<MediaProbeResult> ProbeSourceAsync(string localSourcePath, CancellationToken ct)
    {
        VideoToolResult result = await _runner.RunFfprobeAsync(
            MediaDerivativeInputFormats.BuildSourceProbeArgs(localSourcePath),
            SourceTimeoutPolicy.Analyze(_options),
            ct,
            VideoToolPriority.Low).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"ffprobe failed for the derivative source '{localSourcePath}' " +
                $"(exitCode={result.ExitCode}, timedOut={result.TimedOut}). {Tail(result.StdErr)}");
        }

        return FfprobeOutputParser.Parse(result.StdOut);
    }

    private string CreateDownloadDirectory(Guid projectId)
    {
        string root = Path.GetFullPath(_options.ScratchPath);
        string directory = Path.GetFullPath(Path.Combine(
            root, ScratchSubdirectory, projectId.ToString("D", CultureInfo.InvariantCulture), $"source-{Guid.NewGuid():N}"));

        // Same containment assertion the rest of the feature uses: nothing this layer writes may land
        // outside the scratch root, whatever a caller put in the storage key.
        VideoScratchSpace.ValidateScratchScope(root, directory);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private void Report(
        MediaDerivativesRequested message,
        string? cacheKey,
        MediaDerivativeProgressStage stage,
        string? detail = null,
        int? percentComplete = null)
    {
        try
        {
            _progress.Report(new MediaDerivativeProgressUpdate(
                message.ProjectId,
                message.SourceProjectFileId,
                message.SourceStorageKey,
                cacheKey,
                stage,
                percentComplete,
                detail));
        }
        catch (Exception ex)
        {
            // Progress is a courtesy. A sink that throws must never cost a derivative.
            _logger.LogDebug(ex, "Media derivative progress sink threw; the run continues");
        }
    }

    private void TryDeleteDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return;

        string root = Path.GetFullPath(_options.ScratchPath);
        string full = Path.GetFullPath(directory);

        // The containment check runs before the delete, not after the create: this method is handed a
        // path DERIVED from a produced artifact's location, and a recursive delete is the one
        // operation here that could destroy something outside this feature's own scratch space if
        // that derivation were ever wrong. The scratch root itself is refused too, so a derivation
        // that collapsed to the root cannot wipe every concurrent run's work.
        try
        {
            VideoScratchSpace.ValidateScratchScope(root, full);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Refusing to remove '{Directory}': it is outside the video scratch root", full);
            return;
        }

        if (string.Equals(full, root, StringComparison.Ordinal))
            return;

        try
        {
            if (Directory.Exists(full))
                Directory.Delete(full, recursive: true);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not remove derivative scratch directory {Directory}", full);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Could not remove derivative scratch directory {Directory}", full);
        }
    }

    private static long PrimarySizeBytes(MediaDerivativeArtifact artifact)
    {
        FileInfo info = new(artifact.LocalPath);
        return info.Exists ? info.Length : 0;
    }

    /// <summary>
    /// The scratch copy's extension: the storage key's own when it is a known media container (so
    /// ffmpeg's demuxer selection has a hint), the neutral <c>.bin</c> for any other extension, and
    /// none when the key has none. Never a path fragment, and never a playlist or list extension
    /// (<c>.m3u8</c>, <c>.ffconcat</c>, <c>.txt</c>, ...): those name the demuxers that open other
    /// files (security MI-2). ffmpeg identifies the real container from the content regardless, and
    /// the demuxer whitelist refuses anything that is not a media container.
    /// </summary>
    internal static string SafeExtension(string? storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
            return string.Empty;

        string extension = Path.GetExtension(storageKey);
        if (extension.Length == 0)
            return string.Empty;

        return MediaExtensions.Contains(extension) ? extension : NeutralExtension;
    }

    private const string NeutralExtension = ".bin";

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".m4a", ".3gp", ".3g2", ".mkv", ".webm", ".mp3", ".wav", ".aac",
        ".flac", ".ogg", ".oga", ".opus", ".avi", ".ts", ".mts", ".m2ts", ".mpg", ".mpeg", ".flv",
        ".wmv", ".wma", ".asf", ".aif", ".aiff", ".caf", ".mxf"
    };

    private static string Tail(string value, int max = 2000) =>
        value.Length <= max ? value : value[^max..];

    /// <summary>
    /// An <see cref="IProgress{T}"/> that invokes its callback inline. <see cref="Progress{T}"/>
    /// posts to the captured synchronization context, which on a background task means the thread
    /// pool — reordering (and dropping, on shutdown) stage transitions on exactly the path that is
    /// supposed to be observable.
    /// </summary>
    private sealed class SynchronousProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    private enum DerivativeOutcomeStatus
    {
        Produced,
        CacheHit,
        Failed,

        /// <summary>
        /// The source's own measured properties make this derivative impossible, so no work was
        /// attempted. Distinct from <see cref="Failed"/> on purpose: it is neither a fault nor a
        /// cache hit, and folding it into either would misreport the run.
        /// </summary>
        Unsupported
    }

    private sealed record DerivativeOutcome(DerivativeOutcomeStatus Status)
    {
        public static DerivativeOutcome Produced() => new(DerivativeOutcomeStatus.Produced);

        public static DerivativeOutcome CacheHit() => new(DerivativeOutcomeStatus.CacheHit);

        public static DerivativeOutcome Failed() => new(DerivativeOutcomeStatus.Failed);

        public static DerivativeOutcome Unsupported() => new(DerivativeOutcomeStatus.Unsupported);
    }
}
