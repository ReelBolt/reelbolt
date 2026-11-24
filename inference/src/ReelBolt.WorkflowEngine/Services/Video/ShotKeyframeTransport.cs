using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The stable <c>keyframeReason</c> a clip's manifest entry can carry when a frame it asked for was
/// not applied. Each code names a case the executor can actually observe; deliberately absent is any
/// code for "this step cannot fetch frames", because it can.
/// </summary>
public static class ShotKeyframeReasons
{
    /// <summary>
    /// The id the plan named was never in the <c>VideoAnalyze</c> artifact's <c>OfferedIds</c> — the
    /// ids actually shown to the planner — or the artifact cannot place it. Never fatal; the clip is
    /// generated from its prompt alone.
    /// </summary>
    public const string Unresolved = "keyframe_unresolved";

    /// <summary>
    /// The id resolved, but cutting the frame out of its source failed: the media session refused, or
    /// ffmpeg could not produce the frame. Never fatal; the clip is generated from its prompt alone,
    /// and the caller proceeds without the frame rather than failing a step the operator has already
    /// consented to and paid to plan.
    /// </summary>
    public const string ExtractionFailed = "keyframe_extraction_failed";

    /// <summary>
    /// The clip never reached a ledger row at all — it was skipped past <c>MaxClips</c>, or its prompt
    /// sanitized to nothing — so no frame was ever cut for it and none was needed. Recorded rather
    /// than left blank so "requested but not applied" always has a reason.
    /// </summary>
    public const string NotDispatched = "keyframe_not_dispatched";
}

/// <summary>
/// One requested frame, resolved: which stored object holds it and the moment to cut out of that
/// object.
/// </summary>
/// <param name="ShotId">
/// The offered shot id the plan named. A shot id — never a silence gap, a transcript segment, a
/// placement, a music track, an effect clip or an insert region: only a shot has a frame range.
/// </param>
/// <param name="StorageKey">
/// The object to stage and decode — the analyzed source the shot was detected in. For a shot of a
/// still photo this is the short clip the analyzer rendered the image into, so a still is cut the
/// same way footage is.
/// </param>
/// <param name="AtSec">The moment inside that object, in seconds of its own timeline.</param>
public sealed record ShotFrameTarget(string ShotId, string StorageKey, double AtSec);

/// <summary>
/// What one planned shot's <c>firstFrame</c>/<c>lastFrame</c> pair resolved to.
/// </summary>
/// <param name="First">The frame to send as the generated clip's opening image, or null.</param>
/// <param name="Last">The frame to send as the generated clip's closing image, or null.</param>
/// <param name="Reason">
/// Why nothing was resolved, or null when everything the shot asked for resolved. When it is set the
/// pair is ALL-OR-NOTHING: a shot that names two frames and has one unreadable id gets neither, so a
/// generated clip never silently opens on a frame while closing on nothing.
/// </param>
public sealed record ShotKeyframeResolution(ShotFrameTarget? First, ShotFrameTarget? Last, string? Reason)
{
    /// <summary>The shot named no frame: nothing to resolve, nothing to send.</summary>
    public static readonly ShotKeyframeResolution None = new(null, null, null);

    /// <summary>Nothing was resolved, for <paramref name="reason"/>.</summary>
    public static ShotKeyframeResolution NotApplied(string reason) => new(null, null, reason);

    /// <summary>At least one frame was resolved and is ready to be cut.</summary>
    public bool Resolved => Reason is null && (First is not null || Last is not null);

    /// <summary>Whether the shot asked for a frame at all.</summary>
    public bool Requested => Reason is not null || First is not null || Last is not null;
}

/// <summary>
/// Turns the opaque ids a <c>ShotDirector</c> plan emitted into the object-and-moment a frame has to
/// be cut from. Pure: no I/O, no ffmpeg, no provider call — every method here is safe to run in a unit
/// test and costs nothing.
/// </summary>
/// <remarks>
/// The id vocabulary is deliberately unchanged: a frame reference is still a STRING the planner was
/// offered, never a time and never a URL. Everything numeric that reaches ffmpeg is decided here, by
/// first-party code, from the artifact's own measurements.
/// </remarks>
public static class ShotKeyframeResolver
{
    /// <summary>The frame rate assumed for a source whose artifact carries no usable rational fps.</summary>
    private const double FallbackFrameSeconds = 1.0 / 30.0;

    /// <summary>
    /// Resolves one planned shot's frame references against the artifact the planner was shown.
    /// </summary>
    /// <param name="artifact">The <c>VideoAnalyze</c> artifact whose <c>Shots</c>/<c>Sources</c> place an id.</param>
    /// <param name="offeredIds">
    /// The ids actually shown to the planner. Null means "no offer to check against" (an artifact that
    /// could not be loaded), which resolves nothing rather than trusting an id on faith.
    /// </param>
    /// <param name="firstFrameId">The plan's <c>firstFrame</c>; blank counts as absent.</param>
    /// <param name="lastFrameId">The plan's <c>lastFrame</c>; blank counts as absent.</param>
    /// <returns>
    /// Both targets and no reason, <see cref="ShotKeyframeResolution.None"/> when the shot named no
    /// frame, or the pair refused with <see cref="ShotKeyframeReasons.Unresolved"/>.
    /// </returns>
    public static ShotKeyframeResolution Resolve(
        VideoAnalysisArtifact? artifact, IReadOnlySet<string>? offeredIds,
        string? firstFrameId, string? lastFrameId)
    {
        string? first = Clean(firstFrameId);
        string? last = Clean(lastFrameId);
        if (first is null && last is null)
        {
            return ShotKeyframeResolution.None;
        }

        // The offered set is the whole authorization ("not merely in the artifact — in the set actually
        // shown to the model"): an id the planner was never given is a plan defect, not an invitation
        // to guess which shot was meant.
        if (offeredIds is not null)
        {
            if ((first is not null && !offeredIds.Contains(first)) || (last is not null && !offeredIds.Contains(last)))
            {
                return ShotKeyframeResolution.NotApplied(ShotKeyframeReasons.Unresolved);
            }
        }

        ShotFrameTarget? firstTarget = first is null ? null : Target(artifact, first, isLastFrame: false);
        ShotFrameTarget? lastTarget = last is null ? null : Target(artifact, last, isLastFrame: true);

        return (first is not null && firstTarget is null) || (last is not null && lastTarget is null)
            ? ShotKeyframeResolution.NotApplied(ShotKeyframeReasons.Unresolved)
            : new ShotKeyframeResolution(firstTarget, lastTarget, null);
    }

    /// <summary>
    /// The frame a shot's LAST frame should be cut at: one frame-length BEFORE its exclusive end.
    /// </summary>
    /// <remarks>
    /// The artifact's <c>EndSec</c> is exclusive and is the start of the next shot, so seeking exactly
    /// there would hand the provider the first frame of the shot that FOLLOWS — the opposite of what a
    /// closing image is for. The frame length comes from the source's own probed rational rate
    /// (<c>FpsNum</c>/<c>FpsDen</c>) rather than a rounded approximation, and a shot shorter than a
    /// single frame clamps to its start instead of seeking backwards out of it.
    /// </remarks>
    public static double LastFrameSec(double startSec, double endSec, VideoAnalysisMedia? media)
    {
        double frameSeconds = media is { FpsNum: > 0, FpsDen: > 0 }
            ? (double)media.FpsDen / media.FpsNum
            : FallbackFrameSeconds;

        return Math.Max(startSec, endSec - frameSeconds);
    }

    /// <summary>The trimmed id, or null when it is absent or whitespace-only.</summary>
    private static string? Clean(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim();

    private static ShotFrameTarget? Target(VideoAnalysisArtifact? artifact, string shotId, bool isLastFrame)
    {
        if (artifact?.Shots is not { } shots)
        {
            return null;
        }

        VideoAnalysisShot? shot = shots.FirstOrDefault(s => string.Equals(s.Id, shotId, StringComparison.Ordinal));
        if (shot is null)
        {
            return null;
        }

        VideoAnalysisSourceInfo? source = artifact.Sources?.FirstOrDefault(s => s.SourceIndex == shot.SourceIndex);
        if (source is null || string.IsNullOrWhiteSpace(source.StorageKey))
        {
            return null;
        }

        double atSec = isLastFrame
            ? LastFrameSec(shot.StartSec, shot.EndSec, source.Media)
            : Math.Max(0, shot.StartSec);

        return new ShotFrameTarget(shot.Id, source.StorageKey, atSec);
    }
}

/// <summary>
/// The outcome of cutting one shot's frames out of the media session.
/// </summary>
/// <param name="Images">The frames to send, in order: first frame then last frame. Null when none was produced.</param>
/// <param name="ClipReason">
/// Why no frame will ride this clip's request — a <see cref="ShotKeyframeReasons"/> code — or null.
/// </param>
public sealed record ShotKeyframeCaptureResult(IReadOnlyList<VideoGenerationImageInput>? Images, string? ClipReason)
{
    public static ShotKeyframeCaptureResult Captured(IReadOnlyList<VideoGenerationImageInput> images) => new(images, null);

    public static ShotKeyframeCaptureResult Failed(string reason) => new(null, reason);
}

/// <summary>
/// Cuts the resolved frames out of their sources THROUGH THE MEDIA WORKSPACE and answers with the
/// bytes a video provider takes — the producer the <c>VideoGenerate</c> step was missing.
/// </summary>
/// <remarks>
/// <para>
/// Everything goes through <see cref="IMediaWorkspace"/> and <see cref="IKeyframeExtractor"/>, so the
/// decode runs on whichever machine holds the media (a runner session, when the compute fabric is on)
/// and the small encoded frame — never the footage — comes back to the engine, which is the only party
/// that can put it on a provider request. One instance serves one step ATTEMPT: it remembers which
/// object each source storage key was staged as, so two frames of one shot (and two shots of one
/// source) cost one staging.
/// </para>
/// <para>
/// Failure is a value, never an exception: a frame that cannot be cut degrades its own clip with a
/// reason, exactly as an unresolvable id does. The caller owns the session's lifetime, because the
/// session is the expensive object and only it knows when the last frame has been cut.
/// </para>
/// </remarks>
public sealed class ShotKeyframeCapture
{
    /// <summary>
    /// The widest frame this step hands a provider. 1280 px is the provider-facing width every
    /// image-to-video model in the supported set accepts as a start/end image, and the scale is
    /// bounded by ffmpeg's own filter (never upscaling), so a 6K photo does not become a 6K upload.
    /// </summary>
    public const int FrameWidthPixels = 1280;

    /// <summary>
    /// The most one encoded frame may be before it is refused. A JPEG at
    /// <see cref="FrameWidthPixels"/> is a few hundred kilobytes; this bounds what a hostile or
    /// pathological source could push into a provider request body.
    /// </summary>
    public const int MaxFrameBytes = 8 * 1024 * 1024;

    /// <summary>The media type ffmpeg's <c>mjpeg</c> encoder produces for every frame here.</summary>
    public const string FrameContentType = "image/jpeg";

    private readonly IKeyframeExtractor _extractor;
    private readonly ILogger _logger;
    private readonly Dictionary<string, string> _stagedByStorageKey = new(StringComparer.Ordinal);
    private int _next;

    public ShotKeyframeCapture(IKeyframeExtractor extractor, ILogger logger)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Cuts every frame <paramref name="resolution"/> resolved and reads it back as bytes.
    /// </summary>
    /// <param name="workspace">The open media session the sources are staged into and decoded in.</param>
    /// <param name="resolution">A resolution that <see cref="ShotKeyframeResolution.Resolved"/>.</param>
    /// <param name="ct">The step's cancellation token.</param>
    public async Task<ShotKeyframeCaptureResult> CaptureAsync(
        IMediaWorkspace workspace, ShotKeyframeResolution resolution, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(resolution);

        var images = new List<VideoGenerationImageInput>(2);

        try
        {
            foreach (ShotFrameTarget? target in new[] { resolution.First, resolution.Last })
            {
                if (target is null)
                {
                    continue;
                }

                string sourceVirtualPath = await StageSourceAsync(workspace, target.StorageKey, ct).ConfigureAwait(false);
                string frameVirtualPath = $"kf-frame-{_next++}.jpg";

                await _extractor.ExtractKeyframeAsync(
                    workspace, sourceVirtualPath, frameVirtualPath, target.AtSec, FrameWidthPixels, ct).ConfigureAwait(false);

                // The extractor reports ffmpeg's exit code but not its output: a "successful" run that
                // wrote nothing would otherwise become a zero-byte image on a paid request.
                MediaWorkspaceStat stat = await workspace.StatAsync(frameVirtualPath, ct).ConfigureAwait(false);
                if (!stat.Exists || stat.Bytes <= 0)
                {
                    _logger.LogWarning(
                        "Keyframe transport: cutting shot {ShotId} at {AtSec:0.###}s produced no frame; " +
                        "the clip will be generated from its prompt alone.", target.ShotId, target.AtSec);
                    return ShotKeyframeCaptureResult.Failed(ShotKeyframeReasons.ExtractionFailed);
                }

                byte[] bytes = await workspace.ReadAsync(frameVirtualPath, MaxFrameBytes, ct).ConfigureAwait(false);
                images.Add(new VideoGenerationImageInput(FrameContentType, bytes));

                _logger.LogInformation(
                    "Keyframe transport: cut shot {ShotId} at {AtSec:0.###}s ({Bytes} bytes, {ContentType}) for the provider request.",
                    target.ShotId, target.AtSec, bytes.Length, FrameContentType);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A frame the plan asked for and the operator consented to is expensive to lose, so the
            // failure is logged with its cause and then recorded on the clip rather than thrown: the
            // step's own contract is to keep producing the clips it can pay for.
            _logger.LogWarning(ex,
                "Keyframe transport: cutting this shot's frame(s) failed; the clip will be generated from its prompt alone.");
            return ShotKeyframeCaptureResult.Failed(ShotKeyframeReasons.ExtractionFailed);
        }

        return images.Count > 0
            ? ShotKeyframeCaptureResult.Captured(images)
            : ShotKeyframeCaptureResult.Failed(ShotKeyframeReasons.ExtractionFailed);
    }

    /// <summary>
    /// Stages the object a frame is cut from, once per storage key for this attempt. Both frames of one
    /// shot, and two shots of one source, then decode the same staged copy.
    /// </summary>
    private async Task<string> StageSourceAsync(IMediaWorkspace workspace, string storageKey, CancellationToken ct)
    {
        if (_stagedByStorageKey.TryGetValue(storageKey, out string? staged))
        {
            return staged;
        }

        // The extension is deliberately generic (".bin" is this pipeline's own spelling for "probe the
        // content"): the artifact's key names an object store, and ffmpeg probes the container itself,
        // so nothing here guesses a format from a key it was handed.
        string virtualPath = $"kf-source-{_next++}.bin";
        await workspace.StageAsync(storageKey, virtualPath, ct).ConfigureAwait(false);
        _stagedByStorageKey[storageKey] = virtualPath;
        return virtualPath;
    }
}
