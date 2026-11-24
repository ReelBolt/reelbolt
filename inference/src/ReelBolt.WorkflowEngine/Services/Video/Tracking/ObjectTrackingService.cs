using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video.Tracking;

/// <summary>What one source's object-tracking pass produced, plus what it cost and what degraded.</summary>
public sealed record ObjectTrackingResult(
    IReadOnlyList<VideoInsertRegionTrack> Tracks,
    int SeedCount,
    int VisionCalls,
    bool Degraded,
    string? DegradedReason);

/// <summary>Runs <see cref="VideoAnalyzeStepConfig.TrackObjects"/> for one source.</summary>
public interface IObjectTrackingService
{
    Task<ObjectTrackingResult> TrackAsync(
        string localVideoPath,
        VideoScratchSpace scratch,
        int sourceWidth,
        int sourceHeight,
        double durationSec,
        VideoAnalyzeStepConfig config,
        Func<string, Task> reportProgress,
        CancellationToken ct);
}

/// <summary>
/// Finds and follows the configured objects through one source: samples grayscale frames, turns
/// every target into SEEDS (a region the author placed, or boxes a vision model found on frames
/// sampled across the clip, each refined on a zoomed crop), and hands each seed not already
/// covered by an earlier track to <see cref="PlanarTracker"/>, tuned per label by
/// <see cref="PlanarTrackerTuner"/>. See docs/video-editing.md "Tracking objects".
/// </summary>
public sealed class ObjectTrackingService : IObjectTrackingService
{
    /// <summary>Overlap (IoU of bounding boxes) above which a new seed is the same object an existing track already follows.</summary>
    internal const double SameObjectIou = 0.3;

    private const int MaxSampleFrames = 3000;
    private const long MaxSampleBytes = 600L * 1024 * 1024;
    private const int LocatorImageWidth = 1280;
    private const int RefineImageWidth = 768;

    private readonly IFrameGridSampler _sampler;
    private readonly IKeyframeExtractor _keyframes;
    private readonly IObjectLocator _locator;
    private readonly IInferenceProviderResolver _providers;
    private readonly ILogger<ObjectTrackingService> _logger;

    public ObjectTrackingService(
        IFrameGridSampler sampler,
        IKeyframeExtractor keyframes,
        IObjectLocator locator,
        IInferenceProviderResolver providers,
        ILogger<ObjectTrackingService> logger)
    {
        _sampler = sampler;
        _keyframes = keyframes;
        _locator = locator;
        _providers = providers;
        _logger = logger;
    }

    private sealed record Seed(string Label, int Frame, (double X, double Y)[] Corners, string Origin);

    public async Task<ObjectTrackingResult> TrackAsync(
        string localVideoPath,
        VideoScratchSpace scratch,
        int sourceWidth,
        int sourceHeight,
        double durationSec,
        VideoAnalyzeStepConfig config,
        Func<string, Task> reportProgress,
        CancellationToken ct)
    {
        // Only Feature targets belong to this pass. A segmentation target is a hosted model call with its
        // own mask artifact (SegmentationTrackingService): following it with the planar tracker would
        // publish an insert region where the author asked for a prompted mask.
        IReadOnlyList<ObjectTrackTarget> targets = (config.TrackObjects ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t.Label) && t.Method == ObjectTrackMethod.Feature)
            .ToList();
        if (targets.Count == 0 || sourceWidth <= 0 || sourceHeight <= 0)
            return new ObjectTrackingResult([], 0, 0, false, null);

        (int w, int h) = TrackingSize(sourceWidth, sourceHeight, config.ObjectTrackMaxSide);
        double fps = Math.Clamp(config.ObjectTrackSampleFps, 2, 30);

        await reportProgress("Sampling frames for object tracking");
        // Frames are held in one buffer: cap them by bytes, not just count, so a long clip at a
        // high tracking resolution lowers the sample rate instead of overflowing the 2 GB array
        // limit (3000 frames at 1280x720 is 2.8 GB) — which would degrade, and then fail every
        // censor compile behind it.
        int maxFrames = (int)Math.Clamp(MaxSampleBytes / ((long)w * h), 30, MaxSampleFrames);
        FrameGridResult grid = await _sampler.SampleGrayAsync(localVideoPath, scratch, fps, w, h, durationSec, maxFrames, ct);
        var frames = new GrayFrames(grid.PixelData, grid.GridWidth, grid.GridHeight, grid.FrameCount, grid.EffectiveFps);
        if (frames.Count == 0)
            return new ObjectTrackingResult([], 0, 0, true, "no frames could be sampled");

        var seeds = new List<Seed>();
        int visionCalls = 0;
        // The frame pass stops at a fixed number of frames whatever the file says about its own length. A file with
        // no length, or one longer than its header says, fills that cap before its end: every track then stops there
        // while the video plays on. That is not a clean run for a censor pass, so it is said.
        bool degraded = SamplingStoppedEarly(frames.Count, maxFrames);
        string? degradedReason = degraded ? SamplingStoppedEarlyReason : null;
        if (degraded)
        {
            _logger.LogWarning(
                "Object tracking read {Frames} frames, the most it reads (declared duration {DurationSec}s); tracks may stop before the video ends.",
                frames.Count, durationSec);
        }

        // Placed targets: exactly where the author put them.
        foreach (ObjectTrackTarget t in targets.Where(t => t.Region is not null))
        {
            int frame = Math.Clamp((int)Math.Round((t.AtSec ?? 0) * frames.Fps), 0, frames.Count - 1);
            VideoInsertSurfaceQuad r = t.Region!;
            seeds.Add(new Seed(t.Label.Trim(), frame,
                [(r.X0 * w, r.Y0 * h), (r.X1 * w, r.Y1 * h), (r.X3 * w, r.Y3 * h), (r.X2 * w, r.Y2 * h)],
                "placed"));
        }

        // Found targets: the vision provider looks on frames spread across the clip.
        List<ObjectTrackTarget> found = targets.Where(t => t.Region is null).ToList();
        if (found.Count > 0)
        {
            ResolvedInferenceProvider? vision = await _providers.ResolveVisionAsync(config.ObjectVisionProviderId, ct);
            if (vision is null)
            {
                degraded = true;
                degradedReason ??= NoVisionProviderReason;
            }
            else
            {
                IReadOnlyList<double> times = DetectionTimes(frames.Count / frames.Fps,
                    Math.Clamp(config.ObjectDetectEverySec, 0.25, 30), Math.Clamp(config.MaxObjectDetectFrames, 1, 60));
                foreach (ObjectTrackTarget target in found)
                {
                    string label = target.Label.Trim();
                    foreach (double at in times)
                    {
                        ct.ThrowIfCancellationRequested();
                        await reportProgress($"Looking for \"{VisionObjectLocator.Sanitize(label)}\" at {at:0.0}s");
                        try
                        {
                            (IReadOnlyList<LocatedBox> boxes, int calls) = await LocateRefinedAsync(
                                vision, localVideoPath, scratch, at, label, ct);
                            visionCalls += calls;
                            int frame = Math.Clamp((int)Math.Round(at * frames.Fps), 0, frames.Count - 1);
                            foreach (LocatedBox b in boxes)
                            {
                                seeds.Add(new Seed(label, frame,
                                    [(b.X0 * w, b.Y0 * h), (b.X1 * w, b.Y0 * h), (b.X1 * w, b.Y1 * h), (b.X0 * w, b.Y1 * h)],
                                    "found"));
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Object locator failed for '{Label}' at {At}s; continuing.", label, at);
                            degraded = true;
                            degradedReason ??= VisionLookupFailedReason;
                        }
                    }
                }
            }
        }

        // Track every seed no earlier track already covers.
        var runs = new List<(string Label, List<TrackedQuad> Quads, VideoInsertTrackTuning Tuning)>();
        var tunedByLabel = new Dictionary<string, (PlanarTrackerOptions Options, VideoInsertTrackTuning Tuning)>(StringComparer.OrdinalIgnoreCase);
        int maxTracks = Math.Clamp(config.MaxObjectTracks, 0, 64);
        int untracked = 0;
        foreach (Seed seed in seeds.OrderBy(s => s.Origin == "placed" ? 0 : 1).ThenBy(s => s.Frame))
        {
            if (runs.Any(r => string.Equals(r.Label, seed.Label, StringComparison.OrdinalIgnoreCase) && Covers(r.Quads, seed)))
                continue;
            if (runs.Count >= maxTracks)
            {
                // A sighting nothing follows is not a detail for a censor pass: report it, so the
                // compile refuses to publish rather than leave that object visible.
                untracked++;
                continue;
            }

            ct.ThrowIfCancellationRequested();
            await reportProgress($"Tracking \"{VisionObjectLocator.Sanitize(seed.Label)}\" from {seed.Frame / frames.Fps:0.0}s");
            if (!tunedByLabel.TryGetValue(seed.Label, out var tuned))
            {
                tuned = PlanarTrackerTuner.Tune(frames, seed.Frame, seed.Corners, new PlanarTrackerOptions());
                tunedByLabel[seed.Label] = tuned;
            }

            List<TrackedQuad> quads = PlanarTracker.Track(frames, seed.Frame, seed.Corners, tuned.Options);
            runs.Add((seed.Label, quads, tuned.Tuning));
        }

        var tracks = runs
            .Select(r => ToTrack(r.Label, r.Quads, r.Tuning, frames, sourceWidth, sourceHeight))
            .Where(t => t is not null)
            .Select(t => t!)
            .OrderBy(t => t.StartSec)
            .ToList();

        if (untracked > 0)
        {
            degraded = true;
            degradedReason ??= TrackLimitReason(untracked, maxTracks);
        }

        return new ObjectTrackingResult(tracks, seeds.Count, visionCalls, degraded, degradedReason);
    }

    internal const string SamplingStoppedEarlyReason =
        "the video is longer than its file says, so the objects were followed only up to the point the frame reading stopped";

    internal const string NoVisionProviderReason = "no Vision provider is configured to find labelled objects";

    internal const string VisionLookupFailedReason = "a vision lookup failed";

    /// <summary>The fixed part of <see cref="TrackLimitReason"/>: how a recorded reason is known to be that one.</summary>
    private const string TrackLimitReasonMark = " sighting(s) were not tracked because MaxObjectTracks (";

    internal static string TrackLimitReason(int untracked, int maxTracks) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{untracked}{TrackLimitReasonMark}{maxTracks}) was reached");

    /// <summary>
    /// What to do about a recorded reason, as the last sentence of a refusal that names it. A reason a new run of the
    /// same analysis cannot change (the track limit, a file whose length is wrong) says its own next step; a reason
    /// about the Vision provider (or none recorded) points there; anything else may pass on its own.
    /// </summary>
    internal static string NextStepFor(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return FixVisionNextStep;
        return reason switch
        {
            NoVisionProviderReason or VisionLookupFailedReason => FixVisionNextStep,
            SamplingStoppedEarlyReason =>
                "save the video again as a new file so its length is right, upload that file, and run the analysis on it. Running the same file again gives the same result.",
            _ when reason.Contains(TrackLimitReasonMark, StringComparison.Ordinal) =>
                "raise MaxObjectTracks on the analyze step, or track fewer objects, and run again. Running it unchanged gives the same result.",
            _ => "run the analysis again once that is settled."
        };
    }

    private const string FixVisionNextStep = "fix the Vision provider and run again.";

    /// <summary>
    /// True when the gray pass returned as many frames as it may write (<see cref="FfmpegFrameGridSampler.PlanMaxGrayFrames"/>):
    /// an honest file asked for <paramref name="maxSampleFrames"/> stays below that, so a full buffer means frames were left unread.
    /// </summary>
    internal static bool SamplingStoppedEarly(int frameCount, int maxSampleFrames) =>
        frameCount >= FfmpegFrameGridSampler.PlanMaxGrayFrames(maxSampleFrames);

    /// <summary>
    /// Coarse boxes on the whole frame, then each refined on a zoomed crop around it — vision
    /// models place boxes loosely on a full frame and much more tightly when the object fills the
    /// image. A refinement that finds nothing near the crop's centre keeps the coarse box.
    /// </summary>
    private async Task<(IReadOnlyList<LocatedBox> Boxes, int Calls)> LocateRefinedAsync(
        ResolvedInferenceProvider vision, string video, VideoScratchSpace scratch, double at, string label, CancellationToken ct)
    {
        string full = scratch.GetPath($"locate-{Guid.NewGuid():N}.jpg");
        await _keyframes.ExtractKeyframeAsync(video, full, at, LocatorImageWidth, ct);
        IReadOnlyList<LocatedBox> coarse = await _locator.LocateAsync(vision, full, label, ct);
        int calls = 1;

        var refined = new List<LocatedBox>(coarse.Count);
        foreach (LocatedBox box in coarse)
        {
            LocatedBox crop = CropAround(box);
            string zoom = scratch.GetPath($"locate-zoom-{Guid.NewGuid():N}.jpg");
            try
            {
                await _keyframes.ExtractCroppedKeyframeAsync(video, zoom, at, crop.X0, crop.Y0, crop.Width, crop.Height, RefineImageWidth, ct);
                IReadOnlyList<LocatedBox> inner = await _locator.LocateAsync(vision, zoom, label, ct);
                calls++;
                LocatedBox? best = inner
                    .OrderBy(b => Math.Pow((b.X0 + b.X1) / 2 - 0.5, 2) + Math.Pow((b.Y0 + b.Y1) / 2 - 0.5, 2))
                    .Cast<LocatedBox?>()
                    .FirstOrDefault();
                refined.Add(best is { } b2
                    ? new LocatedBox(
                        crop.X0 + b2.X0 * crop.Width, crop.Y0 + b2.Y0 * crop.Height,
                        crop.X0 + b2.X1 * crop.Width, crop.Y0 + b2.Y1 * crop.Height)
                    : box);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Zoomed refinement failed; keeping the coarse box.");
                refined.Add(box);
            }
        }

        return (refined, calls);
    }

    /// <summary>The box grown to 2.5x (at least 12% of the frame each way), centred, clamped into the frame.</summary>
    internal static LocatedBox CropAround(LocatedBox box)
    {
        double cw = Math.Min(1, Math.Max(0.12, box.Width * 2.5));
        double ch = Math.Min(1, Math.Max(0.12, box.Height * 2.5));
        double cx = Math.Clamp((box.X0 + box.X1) / 2 - cw / 2, 0, 1 - cw);
        double cy = Math.Clamp((box.Y0 + box.Y1) / 2 - ch / 2, 0, 1 - ch);
        return new LocatedBox(cx, cy, cx + cw, cy + ch);
    }

    /// <summary>0, then every <paramref name="everySec"/>, capped at <paramref name="max"/> lookups spread over the whole clip.</summary>
    internal static IReadOnlyList<double> DetectionTimes(double durationSec, double everySec, int max)
    {
        if (durationSec <= 0)
            return [0];

        // Every everySec while that fits under the cap; past it, the cap spread evenly instead,
        // so a long clip is still covered end to end rather than only at its start.
        int wanted = (int)Math.Floor(durationSec / everySec) + 1;
        double step = wanted <= max ? everySec : durationSec / max;
        int count = Math.Min(wanted, max);
        return Enumerable.Range(0, count).Select(i => Math.Round(i * step, 3)).ToList();
    }

    /// <summary>
    /// Does an existing track already follow this seed's object at the seed's frame? Overlap, OR
    /// the seed's centre inside the track's quad grown by half: a vision box is often looser or a
    /// little offset compared with the tracked quad of the same plate, and IoU alone let the same
    /// plate start a second and third track — on a real clip those duplicates used up the track
    /// cap and a plate that reappeared later went untracked.
    /// </summary>
    private static bool Covers(List<TrackedQuad> quads, Seed seed)
    {
        TrackedQuad? at = quads.FirstOrDefault(q => q.FrameIndex == seed.Frame);
        if (at is null)
            return false;
        if (Iou(at.Corners, seed.Corners) > SameObjectIou)
            return true;
        double cx = seed.Corners.Average(c => c.X), cy = seed.Corners.Average(c => c.Y);
        return Geometry.PointInPolygon(PlanarTracker.Grow(at.Corners, 0.5), cx, cy);
    }

    internal static double Iou((double X, double Y)[] a, (double X, double Y)[] b)
    {
        double ax0 = a.Min(p => p.X), ay0 = a.Min(p => p.Y), ax1 = a.Max(p => p.X), ay1 = a.Max(p => p.Y);
        double bx0 = b.Min(p => p.X), by0 = b.Min(p => p.Y), bx1 = b.Max(p => p.X), by1 = b.Max(p => p.Y);
        double iw = Math.Max(0, Math.Min(ax1, bx1) - Math.Max(ax0, bx0));
        double ih = Math.Max(0, Math.Min(ay1, by1) - Math.Max(ay0, by0));
        double inter = iw * ih;
        double union = (ax1 - ax0) * (ay1 - ay0) + (bx1 - bx0) * (by1 - by0) - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>Tracking resolution: the source scaled so its longest side is at most <paramref name="maxSide"/>, even dimensions.</summary>
    internal static (int Width, int Height) TrackingSize(int sourceWidth, int sourceHeight, int maxSide)
    {
        int side = Math.Clamp(maxSide, 320, 1920);
        double scale = Math.Min(1.0, side / (double)Math.Max(sourceWidth, sourceHeight));
        int w = Math.Max(16, (int)Math.Round(sourceWidth * scale / 2) * 2);
        int h = Math.Max(16, (int)Math.Round(sourceHeight * scale / 2) * 2);
        return (w, h);
    }

    /// <summary>One tracker run as an insert-region track, coordinates normalized to the frame.</summary>
    internal static VideoInsertRegionTrack? ToTrack(
        string label, List<TrackedQuad> quads, VideoInsertTrackTuning tuning, GrayFrames frames, int sourceWidth, int sourceHeight)
    {
        if (quads.Count == 0)
            return null;

        double w = frames.Width, h = frames.Height, fps = frames.Fps;
        List<TrackedQuad> smoothed = SmoothCorners(quads);
        var keyframes = smoothed
            .Select(q => new VideoInsertQuadKeyframe(
                Math.Round(q.FrameIndex / fps, 4),
                Math.Round(q.Corners[0].X / w, 5), Math.Round(q.Corners[0].Y / h, 5),
                Math.Round(q.Corners[1].X / w, 5), Math.Round(q.Corners[1].Y / h, 5),
                Math.Round(q.Corners[3].X / w, 5), Math.Round(q.Corners[3].Y / h, 5),
                Math.Round(q.Corners[2].X / w, 5), Math.Round(q.Corners[2].Y / h, 5)))
            .ToList();

        double meanArea = quads.Average(q => Math.Abs(Geometry.Area(q.Corners)) / (w * h));
        double sx = sourceWidth / w, sy = sourceHeight / h;
        double meanAspect = quads.Average(q =>
        {
            double top = Dist(q.Corners[0], q.Corners[1], sx, sy), bottom = Dist(q.Corners[3], q.Corners[2], sx, sy);
            double left = Dist(q.Corners[0], q.Corners[3], sx, sy), right = Dist(q.Corners[1], q.Corners[2], sx, sy);
            double hh = (left + right) / 2;
            return hh > 1e-6 ? (top + bottom) / 2 / hh : 1.0;
        });

        int span = quads[^1].FrameIndex - quads[0].FrameIndex + 1;
        double coverage = quads.Count / (double)Math.Max(1, span);
        double confidence = Math.Clamp(coverage * quads.Average(q => q.Confidence), 0, 1);

        return new VideoInsertRegionTrack(
            Id: string.Empty,
            ShotId: null,
            StartSec: Math.Round(quads[0].FrameIndex / fps, 4),
            EndSec: Math.Round((quads[^1].FrameIndex + 1) / fps, 4),
            Keyframes: keyframes,
            Confidence: Math.Round(confidence, 4),
            MeanAreaRatio: Math.Round(meanArea, 5),
            MeanAspectRatio: Math.Round(meanAspect, 3),
            MotionClass: ClassifyMotion(quads, fps, w, h),
            ColorName: "none",
            Tuning: tuning,
            Method: "feature",
            Label: label);

        static double Dist((double X, double Y) a, (double X, double Y) b, double sx, double sy) =>
            Math.Sqrt(Math.Pow((a.X - b.X) * sx, 2) + Math.Pow((a.Y - b.Y) * sy, 2));
    }

    /// <summary>3-frame centred average of each corner — the tracker's per-frame estimates carry sub-pixel jitter a corner pin would show as shimmer.</summary>
    internal static List<TrackedQuad> SmoothCorners(List<TrackedQuad> quads)
    {
        if (quads.Count < 3)
            return quads;
        var output = new List<TrackedQuad>(quads.Count);
        for (int i = 0; i < quads.Count; i++)
        {
            int from = Math.Max(0, i - 1), to = Math.Min(quads.Count - 1, i + 1);
            var corners = new (double X, double Y)[4];
            for (int c = 0; c < 4; c++)
            {
                double x = 0, y = 0;
                for (int j = from; j <= to; j++)
                {
                    x += quads[j].Corners[c].X;
                    y += quads[j].Corners[c].Y;
                }

                corners[c] = (x / (to - from + 1), y / (to - from + 1));
            }

            output.Add(quads[i] with { Corners = corners });
        }

        return output;
    }

    /// <summary>The motion word of a track whose corners are in pixels of a <paramref name="w"/> by <paramref name="h"/> frame.</summary>
    private static string ClassifyMotion(List<TrackedQuad> quads, double fps, double w, double h) =>
        TrackMotion.Classify(quads.Count, fps, i => (quads[i].Corners.Average(c => c.X), quads[i].Corners.Average(c => c.Y)), w, h);
}
