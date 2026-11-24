using System.Diagnostics;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video.Events;

namespace ReelBolt.WorkflowEngine.Services.Video.Caching;

/// <summary>How one warm-up ended. Only <see cref="Done"/> and <see cref="Partial"/> ran a pass.</summary>
public enum SourcePassWarmUpOutcome
{
    /// <summary>The pass cache or the warm-up is switched off.</summary>
    Off,

    /// <summary>The source has no moving picture a VideoAnalyze step would analyse (no video stream, a still, no length, or longer than a step accepts).</summary>
    NotAnalysable,

    /// <summary>A step would analyse this source through the small analysis copy. The warm-up does not make that copy.</summary>
    TakesAnalysisCopy,

    /// <summary>The store gave no version for the object, so nothing can be cached for it.</summary>
    NoIdentity,

    /// <summary>The stored object is not the one the local file was downloaded from any more.</summary>
    SourceChanged,

    /// <summary>Every pass is in the cache now.</summary>
    Done,

    /// <summary>At least one pass failed, ran out of time or was cancelled. The others are in the cache.</summary>
    Partial,

    /// <summary>Other work was waiting for the slot this warm-up held, so it stopped. The passes that finished are in the cache.</summary>
    Yielded
}

/// <summary>
/// Fills the per-source pass cache for one stored video while a derivative run still has it on disk, so the first
/// VideoAnalyze step on that file does not decode it again for the silence, shot, frame grid and event passes.
/// </summary>
/// <remarks>
/// <para>
/// It asks exactly what a VideoAnalyze step with default settings asks: the settings are the defaults of
/// <see cref="VideoAnalyzeStepConfig"/> itself, and each pass goes through the same <see cref="CachedSourcePasses"/>
/// functions the step executor calls. A pass whose answer is already there is not run again.
/// </para>
/// <para>
/// A source a step would analyse through the analysis copy (<see cref="CachedSourcePasses.WantsAnalysisCopy"/>) is
/// skipped: the copy is one more full decode, and this run does not make it.
/// </para>
/// <para>
/// It is background work. Every ffmpeg call runs at <see cref="VideoToolPriority.Low"/>, the whole warm-up has one
/// deadline that follows the source's length, and <see cref="RunAsync"/> never throws: a failed, late or cancelled
/// pass is logged and the passes that did finish stay in the cache. The caller can ask it to give way: it then
/// starts no further pass.
/// </para>
/// </remarks>
public sealed class SourcePassWarmUp
{
    /// <summary>The whole warm-up gets this many times the deadline one analysis pass gets for the same source.</summary>
    public const int DeadlinePasses = 2;

    /// <summary>The settings of a VideoAnalyze step that sets nothing. Only its defaults are read, never the source reference.</summary>
    private static readonly VideoAnalyzeStepConfig DefaultConfig = new(Version: 1, Source: new VideoSourceRef(VideoSourceKind.ProjectFile));

    private readonly CachedSourcePasses _passes;
    private readonly IVideoToolRunner _runner;
    private readonly VideoEditingOptions _options;
    private readonly VideoSourceLimits _limits;
    private readonly ILogger<SourcePassWarmUp> _logger;

    public SourcePassWarmUp(
        ISilenceDetector silence,
        IShotDetector shots,
        IFrameGridSampler grids,
        ISourcePassCache cache,
        IVideoToolRunner runner,
        IOptions<VideoEditingOptions> options,
        ILogger<SourcePassWarmUp> logger,
        IVideoEventDetector? events = null,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _runner = runner;
        _logger = logger;
        _limits = configuration is null ? VideoSourceLimits.Default : VideoSourceLimits.From(configuration);
        _passes = new CachedSourcePasses(silence, shots, grids, events, cache, _options, logger);
    }

    /// <summary>True when both the pass cache and its warm-up are switched on.</summary>
    public bool Enabled => _options.SourcePassCache.Enabled && _options.SourcePassCache.WarmOnUpload;

    /// <summary>
    /// Runs the passes that are not cached yet for the stored object <paramref name="storageKey"/>, reading the copy
    /// of it at <paramref name="localSourcePath"/>. <paramref name="publishedVersion"/> is the version the caller
    /// downloaded: when it is unknown (empty), or the store reports another one now, nothing is cached, because
    /// the local bytes then have no known version. <paramref name="shouldYield"/> is asked before each pass; true
    /// stops the warm-up there (<see cref="SourcePassWarmUpOutcome.Yielded"/>).
    /// Never throws, also not when <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task<SourcePassWarmUpOutcome> RunAsync(
        Guid projectId, string storageKey, string? publishedVersion, string localSourcePath, MediaProbeResult probe, CancellationToken ct,
        Func<bool>? shouldYield = null)
    {
        try
        {
            return await WarmAsync(projectId, storageKey, publishedVersion, localSourcePath, probe, shouldYield, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Pass cache warm-up of {StorageKey} was cancelled", storageKey);
            return shouldYield?.Invoke() == true ? SourcePassWarmUpOutcome.Yielded : SourcePassWarmUpOutcome.Partial;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pass cache warm-up of {StorageKey} failed; a workflow will run the passes itself", storageKey);
            return SourcePassWarmUpOutcome.Partial;
        }
    }

    /// <summary>
    /// How a warm-up of this source would end before it reads anything, or null when it would run its passes. Pure:
    /// it reads the switches, the probe and the published version only, so a caller that holds the probe can ask
    /// before it keeps a warm-up for later. A null answer is no promise: the store may still report another version.
    /// </summary>
    public SourcePassWarmUpOutcome? RefusalFor(string storageKey, string? publishedVersion, MediaProbeResult probe)
    {
        if (!Enabled)
            return SourcePassWarmUpOutcome.Off;

        ArgumentNullException.ThrowIfNull(probe);
        if (probe.VideoCodec is null || probe.Width <= 0 || probe.Height <= 0 || probe.DurationSec <= 0 ||
            probe.DurationSec > _limits.MaxSeconds ||
            StillImageClip.IsImagePath(storageKey) || StillImageClip.IsImageProbe(probe))
        {
            return SourcePassWarmUpOutcome.NotAnalysable;
        }

        // The default settings name no camera profile, an upload has no authored scenes, and a still was refused above.
        if (CachedSourcePasses.WantsAnalysisCopy(DefaultConfig, probe, _options, cameraProfileChosen: false, scenesAuthored: false, isStill: false))
            return SourcePassWarmUpOutcome.TakesAnalysisCopy;

        // The local file is downloaded as the published version. With no published version there is nothing to
        // compare the stored object with, so the local bytes have no known version and nothing is cached for them.
        return string.IsNullOrWhiteSpace(publishedVersion) ? SourcePassWarmUpOutcome.NoIdentity : null;
    }

    private async Task<SourcePassWarmUpOutcome> WarmAsync(
        Guid projectId, string storageKey, string? publishedVersion, string localSourcePath, MediaProbeResult probe,
        Func<bool>? shouldYield, CancellationToken ct)
    {
        if (RefusalFor(storageKey, publishedVersion, probe) is { } refusal)
        {
            if (refusal == SourcePassWarmUpOutcome.TakesAnalysisCopy)
            {
                _logger.LogInformation(
                    "Pass cache warm-up of {StorageKey} skipped: a source this long and large is analysed through a smaller copy, " +
                    "and the warm-up does not make that copy",
                    storageKey);
            }

            return refusal;
        }

        VideoAnalyzeStepConfig config = DefaultConfig;
        SourcePassIdentity? identity = await _passes.IdentifyAsync(projectId, storageKey, ct).ConfigureAwait(false);
        if (identity is null)
            return SourcePassWarmUpOutcome.NoIdentity;

        // Results of the downloaded bytes must not be stored under the version of an object that replaced it since.
        if (!SameVersion(publishedVersion ?? "", identity.Version))
        {
            _logger.LogInformation("Pass cache warm-up of {StorageKey} skipped: the stored file changed after it was downloaded", storageKey);
            return SourcePassWarmUpOutcome.SourceChanged;
        }

        string fullPath = Path.GetFullPath(localSourcePath);
        string directory = Path.GetDirectoryName(fullPath)
                           ?? throw new ArgumentException($"Cannot determine a directory for '{localSourcePath}'.", nameof(localSourcePath));
        string virtualPath = Path.GetFileName(fullPath);

        // Every pass below runs for as long as this source is long: its deadlines follow it, as in a step.
        using IDisposable timeoutScope = SourceTimeoutPolicy.Begin(
            probe.DurationSec, probe.Width, probe.Height, SourceTimeoutPolicy.SafeLength(fullPath), _limits.MaxSeconds,
            SourceTimeoutPolicy.FpsOf(probe.FpsNum, probe.FpsDen));
        TimeSpan deadline = TimeSpan.FromSeconds(
            Math.Min(SourceTimeoutPolicy.MaxSeconds, (long)DeadlinePasses * SourceTimeoutPolicy.AnalyzeSeconds(_options)));
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(deadline);

        // Low priority: the passes take the derivative lane of the tool runner, never the lane a workflow waits on.
        IMediaWorkspace workspace = LocalMediaWorkspace.CreateAt(directory, _runner, VideoToolPriority.Low, _logger);
        AnalysisPixels pixels = AnalysisPixels.OfSource(virtualPath);

        Stopwatch watch = Stopwatch.StartNew();
        int tried = 0;
        int ready = 0;
        bool stopped = false;
        bool yielded = false;

        async Task PassAsync(string pass, Func<CancellationToken, Task> run)
        {
            if (stopped || bounded.IsCancellationRequested)
            {
                stopped = true;
                return;
            }

            // Other work waits for what this warm-up holds: no further pass is started.
            if (shouldYield?.Invoke() == true)
            {
                stopped = true;
                yielded = true;
                _logger.LogInformation("Pass cache warm-up of {StorageKey} gave way to waiting work before the {Pass} pass", storageKey, pass);
                return;
            }

            tried++;
            try
            {
                await run(bounded.Token).ConfigureAwait(false);
                ready++;
            }
            catch (OperationCanceledException) when (bounded.IsCancellationRequested)
            {
                stopped = true;
                yielded = shouldYield?.Invoke() == true;
                _logger.LogInformation(
                    "Pass cache warm-up of {StorageKey}: the {Pass} pass was stopped ({Why})",
                    storageKey, pass, yielded ? "other work was waiting" : ct.IsCancellationRequested ? "the run was cancelled" : "out of time");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pass cache warm-up of {StorageKey}: the {Pass} pass failed; a workflow will run it itself", storageKey, pass);
            }
        }

        if (CachedSourcePasses.RunsSilence(config, probe))
        {
            await PassAsync(CachedSourcePasses.SilencePass,
                token => _passes.SilenceAsync(identity, workspace, virtualPath, config, probe, token)).ConfigureAwait(false);
        }

        if (config.DetectShots)
        {
            await PassAsync(CachedSourcePasses.ShotsPass,
                token => _passes.ShotsAsync(identity, workspace, pixels, config, probe, token)).ConfigureAwait(false);
        }

        if (config.AnalyzeVisuals)
        {
            await PassAsync(CachedSourcePasses.GridPass,
                token => _passes.VisualGridAsync(identity, workspace, pixels, config, probe, token)).ConfigureAwait(false);
        }

        // Events are off in a step's default settings. They are warmed all the same, with the options a step that
        // only switches them on would use, because they are the most expensive pass to wait for.
        if (_passes.CanDetectEvents)
        {
            await PassAsync(CachedSourcePasses.EventsPass,
                token => _passes.EventsAsync(
                    identity, workspace, pixels, probe, CachedSourcePasses.EventOptionsFor(config), virtualPath, token)).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Pass cache warm-up of {StorageKey}: {Ready} of {Tried} passes ready in {ElapsedMs} ms",
            storageKey, ready, tried, watch.ElapsedMilliseconds);
        if (yielded)
            return SourcePassWarmUpOutcome.Yielded;
        return ready == tried && !stopped ? SourcePassWarmUpOutcome.Done : SourcePassWarmUpOutcome.Partial;
    }

    /// <summary>Two spellings of one object version: the store's ETag with or without its quotes.</summary>
    private static bool SameVersion(string published, string current) =>
        string.Equals(published.Trim().Trim('"'), current.Trim().Trim('"'), StringComparison.Ordinal);
}
