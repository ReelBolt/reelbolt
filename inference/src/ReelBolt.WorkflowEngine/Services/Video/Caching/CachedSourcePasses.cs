using System.Globalization;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Services.Video.Events;

namespace ReelBolt.WorkflowEngine.Services.Video.Caching;

/// <summary>
/// Which pixels a pass reads: the staged source, or the small analysis copy of it (<see cref="AnalysisProxy"/>).
/// The choice is made by policy before any pass runs, and the copy itself is made only when a pass that reads
/// it has no cached answer. A copy that could not be made turns the choice back to the source for every later
/// pass, and that is the variant their results are stored under.
/// </summary>
public sealed class AnalysisPixels
{
    /// <summary>The variant of a pass that read the source itself.</summary>
    public const string SourceVariant = "source";

    private readonly Func<CancellationToken, Task<string?>>? _makeCopy;
    private bool _copyTried;

    private AnalysisPixels(string sourceVirtualPath, string variant, bool thinned, Func<CancellationToken, Task<string?>>? makeCopy)
    {
        VirtualPath = sourceVirtualPath;
        Variant = variant;
        Thinned = thinned;
        _makeCopy = makeCopy;
    }

    /// <summary>Passes read the source.</summary>
    public static AnalysisPixels OfSource(string sourceVirtualPath) =>
        new(sourceVirtualPath, SourceVariant, thinned: false, makeCopy: null);

    /// <summary>
    /// Passes read the analysis copy. <paramref name="makeCopy"/> is called at most once, by the first pass that
    /// needs the pixels: it answers the copy's session path, or null when the copy could not be made.
    /// </summary>
    public static AnalysisPixels OfCopy(
        string sourceVirtualPath, double sourceFps, string? cameraProfileId, Func<CancellationToken, Task<string?>> makeCopy)
    {
        ArgumentNullException.ThrowIfNull(makeCopy);
        bool thinned = sourceFps > AnalysisProxy.MaxFps + 0.5;
        string variant = string.Create(
            CultureInfo.InvariantCulture,
            $"copy:fps={(thinned ? AnalysisProxy.MaxFps : 0)}:profile={cameraProfileId ?? "-"}");
        return new AnalysisPixels(sourceVirtualPath, variant, thinned, makeCopy);
    }

    /// <summary>The file passes read now: the source until the copy is made.</summary>
    public string VirtualPath { get; private set; }

    /// <summary>The pixels variant that goes into every parameter string: <see cref="SourceVariant"/>, or the copy with its frame rate and camera profile.</summary>
    public string Variant { get; private set; }

    /// <summary>True while the passes read (or would read) the copy.</summary>
    public bool ReadsCopy => !string.Equals(Variant, SourceVariant, StringComparison.Ordinal);

    /// <summary>True when the copy was thinned to <see cref="AnalysisProxy.MaxFps"/>.</summary>
    public bool Thinned { get; private set; }

    /// <summary>True once the copy was made in this attempt.</summary>
    public bool CopyMade { get; private set; }

    /// <summary>True once a pass was answered from the cache with a result that was read from the copy.</summary>
    public bool AnsweredFromCopy { get; private set; }

    /// <summary>
    /// True when the passes' answers come from the copy: it was made, or an answer was read from the cache as an
    /// earlier copy gave it, and no later attempt to make the copy failed. After such a failure the later passes
    /// read the source as it is, so the copy (and a camera profile it carries) no longer counts as applied.
    /// </summary>
    public bool AnalysedTheCopy => ReadsCopy && (CopyMade || AnsweredFromCopy);

    /// <summary>
    /// Called by a pass that has to read pixels. Makes the copy the first time when the policy wants one, and
    /// answers the file to read. An exception from making the copy (a deadline on a large source) is the caller's.
    /// </summary>
    public async Task<string> ReadyAsync(CancellationToken ct)
    {
        if (_makeCopy is null || _copyTried || !ReadsCopy)
            return VirtualPath;

        _copyTried = true;
        string? copy;
        try
        {
            copy = await _makeCopy(ct).ConfigureAwait(false);
        }
        catch
        {
            // No copy: whatever a caller does with this exception, a later pass reads the source, and that is
            // the variant its result is stored under.
            Variant = SourceVariant;
            Thinned = false;
            throw;
        }

        if (copy is null)
        {
            Variant = SourceVariant;
            Thinned = false;
        }
        else
        {
            VirtualPath = copy;
            CopyMade = true;
        }

        return VirtualPath;
    }

    internal void MarkAnswered()
    {
        if (ReadsCopy)
            AnsweredFromCopy = true;
    }
}

/// <summary>
/// The silence, shot, frame grid and event passes of VideoAnalyze, each answered from the per-source pass cache
/// when it holds the answer and run (then stored) when it does not. With no cache, or no identity for the
/// source, every call is exactly the call to the detector it wraps.
/// </summary>
/// <remarks>
/// The parameter string of a pass holds every argument that changes its result, and the pixels variant. A read
/// or write that fails is logged and ignored: the pass then simply runs. Before a result is stored the source is
/// identified once more, and a source that is gone or has another version by then gets no entry.
/// </remarks>
public sealed class CachedSourcePasses
{
    public const string SilencePass = "silence";
    public const string ShotsPass = "shots";
    public const string GridPass = "grid";
    public const string EventsPass = "events";

    /// <summary>Bumped when the stored form or the meaning of a pass changes. The VideoAnalyze revision tag is hashed too.</summary>
    public const string PassRevision = "1";

    private readonly ISilenceDetector _silence;
    private readonly IShotDetector _shots;
    private readonly IFrameGridSampler _grids;
    private readonly IVideoEventDetector? _events;
    private readonly ISourcePassCache? _cache;
    private readonly VideoEditingOptions _options;
    private readonly ILogger _logger;

    public CachedSourcePasses(
        ISilenceDetector silence,
        IShotDetector shots,
        IFrameGridSampler grids,
        IVideoEventDetector? events,
        ISourcePassCache? cache,
        VideoEditingOptions options,
        ILogger logger)
    {
        _silence = silence;
        _shots = shots;
        _grids = grids;
        _events = events;
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    /// <summary>The cache identity of a stored source, or null when nothing is cached for it.</summary>
    public async Task<SourcePassIdentity?> IdentifyAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        if (_cache is null)
            return null;
        try
        {
            return await _cache.IdentifyAsync(projectId, storageKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Pass cache: could not identify {StorageKey}; nothing is cached for it", storageKey);
            return null;
        }
    }

    // ---- What a VideoAnalyze step config asks of each pass. The step executor and the upload warm-up
    // (SourcePassWarmUp) both go through these, so the two can never ask a different question of the same file. ----

    /// <summary>True when a step with <paramref name="config"/> runs the silence pass on this source: it is on and the source has sound.</summary>
    public static bool RunsSilence(VideoAnalyzeStepConfig config, MediaProbeResult probe) =>
        config.DetectSilence && probe.AudioCodec is not null;

    /// <summary>The size of the visual frame grid of <paramref name="config"/>, clamped so a config cannot ask for an enormous one.</summary>
    public static (int Width, int Height) VisualGridSize(VideoAnalyzeStepConfig config) =>
        (Math.Clamp(config.VisualGridWidth, 8, 256), Math.Clamp(config.VisualGridHeight, 8, 256));

    /// <summary>The event detector's options for a step config.</summary>
    public static VideoEventOptions EventOptionsFor(VideoAnalyzeStepConfig config) =>
        config.MaxEvents is { } max ? new VideoEventOptions { MaxEvents = Math.Clamp(max, 10, 2000) } : new VideoEventOptions();

    /// <summary>True when a step with <paramref name="config"/> has a pass that must read the source's own pixels, so it never takes the analysis copy.</summary>
    public static bool NeedsSourcePixels(VideoAnalyzeStepConfig config) =>
        config.DetectInsertRegions || config.DetectSharpness || config.TrackObjects is { Count: > 0 };

    /// <summary>
    /// The policy of the analysis copy (<see cref="AnalysisProxy"/>): true when the pixel passes of a step with
    /// <paramref name="config"/> read the small copy of this source and not the source. Never for a source whose
    /// scenes its author described, never for a still, never when a pass needs the source's own pixels; otherwise
    /// for a long, large source, or when a camera profile was chosen for it.
    /// </summary>
    public static bool WantsAnalysisCopy(
        VideoAnalyzeStepConfig config, MediaProbeResult probe, VideoEditingOptions options,
        bool cameraProfileChosen, bool scenesAuthored, bool isStill) =>
        !scenesAuthored && !isStill && !NeedsSourcePixels(config) &&
        (config.DetectShots || config.AnalyzeVisuals || config.AnalyzeEvents) &&
        (AnalysisProxy.Eligible(probe, options.AnalysisProxyMinDurationSeconds) || cameraProfileChosen);

    /// <summary>The silence pass as a step with <paramref name="config"/> runs it.</summary>
    public Task<IReadOnlyList<(double StartSec, double EndSec)>> SilenceAsync(
        SourcePassIdentity? source, IMediaWorkspace workspace, string virtualPath,
        VideoAnalyzeStepConfig config, MediaProbeResult probe, CancellationToken ct) =>
        SilenceAsync(source, workspace, virtualPath, config.SilenceThresholdDb, config.MinSilenceMs / 1000.0, probe.DurationSec, ct);

    /// <summary>The shot pass as a step with <paramref name="config"/> runs it (raw spans, before any flash frame is absorbed).</summary>
    public Task<IReadOnlyList<(double StartSec, double EndSec)>> ShotsAsync(
        SourcePassIdentity? source, IMediaWorkspace workspace, AnalysisPixels pixels,
        VideoAnalyzeStepConfig config, MediaProbeResult probe, CancellationToken ct) =>
        ShotsAsync(
            source, workspace, pixels, config.SceneDetection == SceneDetectionMode.Sensitive,
            config.SceneThreshold, probe.DurationSec, Math.Max(0, config.MinShotMs) / 1000.0, ct);

    /// <summary>
    /// <see cref="SceneDetectionMode.Soft"/> shots: the standard spans plus the soft-transition boundaries. Uncached, because
    /// the cached shot pass carries spans only (and its key does not name the Soft mode), not the boundary list.
    /// </summary>
    public async Task<SceneDetectionResult> SoftShotsAsync(
        IMediaWorkspace workspace, AnalysisPixels pixels, VideoAnalyzeStepConfig config, MediaProbeResult probe, CancellationToken ct)
    {
        string path = await pixels.ReadyAsync(ct).ConfigureAwait(false);
        return await SceneCutFinder.DetectWithMethodsAsync(
            _shots, workspace, path, SceneDetectionMode.Soft,
            config.SceneThreshold, probe.DurationSec, Math.Max(0, config.MinShotMs) / 1000.0, _options, ct,
            probe.FpsDen > 0 && probe.FpsNum > 0 ? ((int)probe.FpsNum, (int)probe.FpsDen) : null).ConfigureAwait(false);
    }

    /// <summary>The frame grid the visual analysis of a step with <paramref name="config"/> reads.</summary>
    public Task<FrameGridResult> VisualGridAsync(
        SourcePassIdentity? source, IMediaWorkspace workspace, AnalysisPixels pixels,
        VideoAnalyzeStepConfig config, MediaProbeResult probe, CancellationToken ct)
    {
        (int gridWidth, int gridHeight) = VisualGridSize(config);
        return GridAsync(
            source, workspace, pixels, config.VisualSampleFps, gridWidth, gridHeight,
            probe.DurationSec, config.MaxVisualSampleFrames, chroma: null, ct);
    }

    public static string SilenceParameters(double thresholdDb, double minSilenceSec, double durationSec) =>
        $"thresholdDb={N(thresholdDb)};minSilenceSec={N(minSilenceSec)};durationSec={N(durationSec)};pixels={AnalysisPixels.SourceVariant}";

    public static string ShotsParameters(bool sensitive, double sceneThreshold, double durationSec, double minShotSec, string pixelsVariant) =>
        $"mode={(sensitive ? nameof(SceneDetectionMode.Sensitive) : nameof(SceneDetectionMode.Standard))};sceneThreshold={N(sceneThreshold)};" +
        $"durationSec={N(durationSec)};minShotSec={(sensitive ? N(minShotSec) : "-")};pixels={pixelsVariant}";

    public static string GridParameters(
        double sampleFps, int gridWidth, int gridHeight, double durationSec, int maxSampleFrames, GridChroma chroma, string pixelsVariant) =>
        $"sampleFps={N(sampleFps)};grid={N(gridWidth)}x{N(gridHeight)};durationSec={N(durationSec)};maxFrames={N(maxSampleFrames)};" +
        $"chroma={chroma};pixels={pixelsVariant}";

    public static string EventsParameters(MediaProbeResult probe, VideoEventOptions options, string pixelsVariant) =>
        $"maxEvents={N(options.MaxEvents)};speech={options.DetectSpeech};beats={options.DetectBeats};" +
        $"silenceDb={N(options.SilenceThresholdDb)};minSilenceMs={N(options.MinSilenceMs)};durationSec={N(probe.DurationSec)};" +
        $"fps={N(probe.FpsNum)}/{N(probe.FpsDen)};size={N(probe.Width)}x{N(probe.Height)};audio={probe.AudioCodec is not null};" +
        $"pixels={pixelsVariant}";

    /// <summary><see cref="ISilenceDetector.DetectAsync(IMediaWorkspace,string,double,double,double?,CancellationToken)"/> on the source's own sound.</summary>
    public async Task<IReadOnlyList<(double StartSec, double EndSec)>> SilenceAsync(
        SourcePassIdentity? source, IMediaWorkspace workspace, string virtualPath,
        double thresholdDb, double minSilenceSec, double durationSec, CancellationToken ct)
    {
        string parameters = SilenceParameters(thresholdDb, minSilenceSec, durationSec);
        if (SourcePassPayloads.TryDecodeSpans(await ReadAsync(source, SilencePass, parameters, ct).ConfigureAwait(false), out var cached))
            return cached;

        IReadOnlyList<(double StartSec, double EndSec)> spans = await _silence
            .DetectAsync(workspace, virtualPath, thresholdDb, minSilenceSec, durationSec, ct).ConfigureAwait(false);
        await WriteAsync(source, SilencePass, parameters, () => SourcePassPayloads.EncodeSpans(spans), ct).ConfigureAwait(false);
        return spans;
    }

    /// <summary>
    /// The raw shot spans, before any flash frame is absorbed: the scene-score detector, or with
    /// <paramref name="sensitive"/> the scene score merged with the luma-jump pass (<see cref="SceneCutFinder"/>).
    /// </summary>
    public async Task<IReadOnlyList<(double StartSec, double EndSec)>> ShotsAsync(
        SourcePassIdentity? source, IMediaWorkspace workspace, AnalysisPixels pixels, bool sensitive,
        double sceneThreshold, double durationSec, double minShotSec, CancellationToken ct)
    {
        string wanted = ShotsParameters(sensitive, sceneThreshold, durationSec, minShotSec, pixels.Variant);
        if (SourcePassPayloads.TryDecodeSpans(await ReadAsync(source, ShotsPass, wanted, ct).ConfigureAwait(false), out var cached))
        {
            pixels.MarkAnswered();
            return cached;
        }

        string path = await pixels.ReadyAsync(ct).ConfigureAwait(false);
        IReadOnlyList<(double StartSec, double EndSec)> spans = sensitive
            ? await SceneCutFinder.DetectAsync(
                _shots, workspace, path, SceneDetectionMode.Sensitive,
                sceneThreshold, durationSec, minShotSec, _options, ct).ConfigureAwait(false)
            : await _shots.DetectShotsAsync(workspace, path, sceneThreshold, durationSec, ct).ConfigureAwait(false);
        await WriteAsync(
            source, ShotsPass, ShotsParameters(sensitive, sceneThreshold, durationSec, minShotSec, pixels.Variant),
            () => SourcePassPayloads.EncodeSpans(spans), ct).ConfigureAwait(false);
        return spans;
    }

    /// <summary>
    /// One colour frame grid. <paramref name="chroma"/> null is the sampler's plain overload; a value is the
    /// overload that takes it.
    /// </summary>
    public async Task<FrameGridResult> GridAsync(
        SourcePassIdentity? source, IMediaWorkspace workspace, AnalysisPixels pixels, double sampleFps,
        int gridWidth, int gridHeight, double durationSec, int maxSampleFrames, GridChroma? chroma, CancellationToken ct)
    {
        GridChroma keyed = chroma ?? GridChroma.Default;
        string wanted = GridParameters(sampleFps, gridWidth, gridHeight, durationSec, maxSampleFrames, keyed, pixels.Variant);
        if (SourcePassPayloads.TryDecodeGrid(await ReadAsync(source, GridPass, wanted, ct).ConfigureAwait(false), out FrameGridResult? cached))
        {
            pixels.MarkAnswered();
            return cached!;
        }

        string path = await pixels.ReadyAsync(ct).ConfigureAwait(false);
        FrameGridResult grid = chroma is { } explicitChroma
            ? await _grids.SampleAsync(workspace, path, sampleFps, gridWidth, gridHeight, durationSec, maxSampleFrames, explicitChroma, ct).ConfigureAwait(false)
            : await _grids.SampleAsync(workspace, path, sampleFps, gridWidth, gridHeight, durationSec, maxSampleFrames, ct).ConfigureAwait(false);
        if (grid is not null)
        {
            await WriteAsync(
                source, GridPass, GridParameters(sampleFps, gridWidth, gridHeight, durationSec, maxSampleFrames, keyed, pixels.Variant),
                () => SourcePassPayloads.EncodeGrid(grid), ct).ConfigureAwait(false);
        }

        return grid!;
    }

    /// <summary>True when a detector is there to run <see cref="EventsAsync"/>.</summary>
    public bool CanDetectEvents => _events is not null;

    /// <summary>
    /// The local event detector: pixels from <paramref name="pixels"/>, sound from <paramref name="audioVirtualPath"/>
    /// (the source). A thinned copy is read at its own frame rate.
    /// </summary>
    public async Task<VideoEventSet> EventsAsync(
        SourcePassIdentity? source, IMediaWorkspace workspace, AnalysisPixels pixels, MediaProbeResult probe,
        VideoEventOptions options, string audioVirtualPath, CancellationToken ct)
    {
        IVideoEventDetector detector = _events ?? throw new InvalidOperationException("No event detector is available.");
        string wanted = EventsParameters(probe, options, pixels.Variant);
        if (SourcePassPayloads.TryDecodeEvents(await ReadAsync(source, EventsPass, wanted, ct).ConfigureAwait(false), out VideoEventSet? cached))
        {
            pixels.MarkAnswered();
            return cached!;
        }

        string path = await pixels.ReadyAsync(ct).ConfigureAwait(false);
        bool thinned = pixels.CopyMade && pixels.Thinned;
        VideoEventSet set = await detector.DetectAsync(
            workspace, path, probe, options, audioVirtualPath,
            thinned ? AnalysisProxy.MaxFps : 0, thinned ? 1 : 0, ct).ConfigureAwait(false);
        if (set is not null)
        {
            await WriteAsync(
                source, EventsPass, EventsParameters(probe, options, pixels.Variant),
                () => SourcePassPayloads.EncodeEvents(set), ct).ConfigureAwait(false);
        }

        return set!;
    }

    /// <summary>True when the cache holds the answer <see cref="EventsAsync"/> would give for these arguments.</summary>
    public async Task<bool> HasEventsAsync(
        SourcePassIdentity? source, AnalysisPixels pixels, MediaProbeResult probe, VideoEventOptions options, CancellationToken ct) =>
        SourcePassPayloads.TryDecodeEvents(
            await ReadAsync(source, EventsPass, EventsParameters(probe, options, pixels.Variant), ct).ConfigureAwait(false), out _);

    private async Task<byte[]?> ReadAsync(SourcePassIdentity? source, string pass, string parameters, CancellationToken ct)
    {
        if (_cache is null || source is null)
            return null;
        try
        {
            return await _cache
                .TryReadAsync(source, SourcePassCache.EntryKey(source, pass, PassRevision, parameters), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Pass cache: reading the {Pass} pass of {StorageKey} failed; running it", pass, source.StorageKey);
            return null;
        }
    }

    private async Task WriteAsync(SourcePassIdentity? source, string pass, string parameters, Func<byte[]?> encode, CancellationToken ct)
    {
        if (_cache is null || source is null)
            return;
        try
        {
            if (encode() is not { } payload)
                return;

            // The pass ran for a while. The stored object must still be the one that was read: a file that was
            // replaced, deleted or moved away since gets no entry.
            SourcePassIdentity? now = await _cache.IdentifyAsync(source.ProjectId, source.StorageKey, ct).ConfigureAwait(false);
            if (now is null || !string.Equals(now.Version, source.Version, StringComparison.Ordinal))
            {
                _logger.LogDebug("Pass cache: {StorageKey} changed while the {Pass} pass ran; its result is not stored", source.StorageKey, pass);
                return;
            }

            await _cache.WriteAsync(source, SourcePassCache.EntryKey(source, pass, PassRevision, parameters), payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Pass cache: storing the {Pass} pass of {StorageKey} failed; continuing without it", pass, source.StorageKey);
        }
    }

    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
