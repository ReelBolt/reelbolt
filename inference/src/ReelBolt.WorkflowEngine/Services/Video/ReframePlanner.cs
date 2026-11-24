using System.Text.Json.Nodes;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The time-varying part of one source clip's canvas fit (docs/video-editing.md "Keyframed
/// reframing"): the stretches the subject safe area pads, and the clip's reframes. Every piece of
/// geometry that lands on the picture — tracked inserts, plate guards, censor masks, overlays —
/// asks <see cref="VideoCompileStepExecutor.SourceCanvasFit.At"/> for the fit at its own source
/// moment, which comes here.
/// </summary>
internal sealed class ReframeFitTimeline(
    int sourceWidth,
    int sourceHeight,
    IReadOnlyList<VideoReframe> reframes,
    IReadOnlyList<(double Start, double End, VideoCompileStepExecutor.SourceCanvasFit Fit)> padWindows,
    int canvasWidth = 0,
    int canvasHeight = 0)
{
    /// <summary>
    /// <paramref name="baseFit"/> (the clip's static fit) at <paramref name="sourceSec"/>: swapped for
    /// the safe-area pad inside a padded span, then re-expressed for the reframe window the
    /// <c>zoompan</c> stage crops at that moment (<see cref="ReframeGeometry.PixelWindow"/>).
    /// </summary>
    public VideoCompileStepExecutor.SourceCanvasFit Apply(VideoCompileStepExecutor.SourceCanvasFit baseFit, double sourceSec)
    {
        VideoCompileStepExecutor.SourceCanvasFit fit = baseFit with { Timeline = null };
        foreach ((double start, double end, VideoCompileStepExecutor.SourceCanvasFit pad) in padWindows)
        {
            if (sourceSec >= start && sourceSec < end)
            {
                fit = pad;
                break;
            }
        }

        ReframeWindow window = ReframeGeometry.WindowAt(reframes, sourceSec);
        if (ReframeGeometry.AnchorAt(reframes, sourceSec) == ReframeAnchor.Visible && sourceWidth > 0 && sourceHeight > 0 &&
            canvasWidth > 0 && canvasHeight > 0)
        {
            // Visible anchor: the window is zoompan's, and the cover crop pans inside the zoomed frame.
            double vx = fit.Width > canvasWidth + 1e-6 ? canvasWidth / fit.Width : 1;
            double vy = fit.Height > canvasHeight + 1e-6 ? canvasHeight / fit.Height : 1;
            ReframeStrip strip = ReframeGeometry.Decompose(window, vx, vy);
            fit = ReframeGeometry.MoveCrop(fit, strip.StripX, strip.StripY, canvasWidth, canvasHeight);
            window = strip.Window;
        }

        return window == ReframeWindow.Identity || sourceWidth <= 0 || sourceHeight <= 0
            ? fit
            : ReframeGeometry.Apply(fit, ReframeGeometry.PixelWindow(window, sourceWidth, sourceHeight), sourceWidth, sourceHeight);
    }
}

/// <summary>
/// What reframing and the subject safe area do to one compile: which spans get a
/// <c>zoompan</c> stage, which are padded inside the safe area, the geometry timeline per clip,
/// and the <c>reframes</c> report. <see cref="None"/> changes nothing.
/// </summary>
internal sealed class ReframePlan
{
    private readonly IReadOnlyDictionary<int, IReadOnlyList<VideoReframe>> _bySource;
    private readonly HashSet<VideoCompileStepExecutor.ResolvedSpan> _safePadded;
    private readonly IReadOnlyDictionary<int, ReframeFitTimeline> _timelines;
    private readonly Func<int, VideoAnalysisMedia> _sourceMedia;
    private readonly JsonObject? _node;

    internal ReframePlan(
        IReadOnlyDictionary<int, IReadOnlyList<VideoReframe>> bySource,
        HashSet<VideoCompileStepExecutor.ResolvedSpan> safePadded,
        double safeMargin,
        bool blurSafePad,
        IReadOnlyDictionary<int, ReframeFitTimeline> timelines,
        Func<int, VideoAnalysisMedia> sourceMedia,
        bool anyStage,
        JsonObject? node,
        string? errorCode = null,
        string? errorMessage = null)
    {
        _bySource = bySource;
        _safePadded = safePadded;
        SafeMargin = safeMargin;
        BlurSafePad = blurSafePad;
        _timelines = timelines;
        _sourceMedia = sourceMedia;
        RequiresSegmented = anyStage || safePadded.Count > 0;
        _node = node;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public static ReframePlan None { get; } = new(
        new Dictionary<int, IReadOnlyList<VideoReframe>>(), [], 0, false, new Dictionary<int, ReframeFitTimeline>(),
        _ => new VideoAnalysisMedia(0, 0, 0, 0, 0), false, null);

    internal static ReframePlan Error(string code, string message) => new(
        new Dictionary<int, IReadOnlyList<VideoReframe>>(), [], 0, false, new Dictionary<int, ReframeFitTimeline>(),
        _ => new VideoAnalysisMedia(0, 0, 0, 0, 0), false, null, code, message);

    /// <summary>A config error (<c>REFRAME_UNKNOWN_SOURCE</c>); null when the plan is usable.</summary>
    public string? ErrorCode { get; }

    public string? ErrorMessage { get; }

    /// <summary>The safe-area margin a padded span is fitted inside (fraction of the canvas).</summary>
    public double SafeMargin { get; }

    /// <summary>Whether a safe-area pad is filled with the blurred picture (<c>CanvasFill = Blur</c>).</summary>
    public bool BlurSafePad { get; }

    /// <summary>
    /// Whether any span is reframed or safe-padded — only the segmented encoder has a per-span picture
    /// stage on the span's own clock, so the single-source select path routes there.
    /// </summary>
    public bool RequiresSegmented { get; }

    /// <summary>Whether <paramref name="span"/> is padded inside the safe area.</summary>
    public bool SafePads(VideoCompileStepExecutor.ResolvedSpan span) => _safePadded.Contains(span);

    /// <summary>The span's <c>fps,zoompan</c> stage at the canvas rate, or null.</summary>
    public string? StageFor(VideoCompileStepExecutor.ResolvedSpan span, int fpsNum, int fpsDen)
    {
        if (!_bySource.TryGetValue(span.SourceIndex, out IReadOnlyList<VideoReframe>? reframes))
            return null;
        VideoAnalysisMedia media = _sourceMedia(span.SourceIndex);
        return ReframeFilterBuilder.ZoompanStage(reframes, span.SnappedStart, span.SnappedEnd, media.Width, media.Height, fpsNum, fpsDen);
    }

    /// <summary>
    /// The cover crop of a span with a <see cref="ReframeAnchor.Visible"/> reframe (its x/y follow the reframe
    /// in time), or null when the span has none — the caller keeps its own crop.
    /// </summary>
    public string? VisibleCoverCrop(VideoCompileStepExecutor.ResolvedSpan span, int canvasWidth, int canvasHeight, CropFocus? focus)
    {
        if (!_bySource.TryGetValue(span.SourceIndex, out IReadOnlyList<VideoReframe>? reframes))
            return null;
        VideoAnalysisMedia media = _sourceMedia(span.SourceIndex);
        return ReframeFilterBuilder.VisibleCoverCrop(
            reframes, span.SnappedStart, span.SnappedEnd, media.Width, media.Height, canvasWidth, canvasHeight,
            focus is null || focus.IsCentre ? null : focus.X, focus is null || focus.IsCentre ? null : focus.Y);
    }

    /// <summary>
    /// The geometry a segmentation MASK of <paramref name="span"/> must take so it lands on the picture: the
    /// span's <c>zoompan</c> stage and its per-span canvas fit (the safe-area pad on black, or the cover crop
    /// a visible-anchor reframe pans), exactly as <see cref="ReframeFilterBuilder.SpanVideoChain"/> builds the
    /// picture; a blurred fill is the same geometry as a black pad. Null when the span has neither a stage
    /// nor a pad, so the mask keeps the clip's static conform. No leading comma; empty when the stage is
    /// the only geometry and the clip is not fitted to a canvas.
    /// </summary>
    /// <param name="normalized">Whether the clip is fitted to the canvas at all (multi-clip or fixed format).</param>
    /// <param name="cover">Whether the clip is cover-fitted (a crop) rather than contained (a pad).</param>
    public string? MaskChainFor(
        VideoCompileStepExecutor.ResolvedSpan span, int canvasWidth, int canvasHeight, int fpsNum, int fpsDen,
        bool normalized, bool cover, CropFocus? focus)
    {
        string? stage = StageFor(span, fpsNum, fpsDen);
        bool safe = SafePads(span);
        if (stage is null && !safe)
            return null;

        string? fit = safe
            ? ReframeFilterBuilder.SafePadSuffix(canvasWidth, canvasHeight, SafeMargin, fpsNum, fpsDen)[1..]
            : !normalized
                ? null
                : cover && focus is not { Pad: true }
                    ? $"scale={canvasWidth}:{canvasHeight}:force_original_aspect_ratio=increase," +
                      $"{VisibleCoverCrop(span, canvasWidth, canvasHeight, focus) ?? VideoCompileStepExecutor.CoverCropFilter(canvasWidth, canvasHeight, focus)},setsar=1"
                    : $"scale={canvasWidth}:{canvasHeight}:force_original_aspect_ratio=decrease,pad={canvasWidth}:{canvasHeight}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1";
        return string.Join(",", new[] { stage, fit }.Where(part => !string.IsNullOrEmpty(part)));
    }

    /// <summary>The clip's geometry timeline, or null when nothing about its fit varies over time.</summary>
    public ReframeFitTimeline? TimelineFor(int sourceIndex) => _timelines.GetValueOrDefault(sourceIndex);

    /// <summary>
    /// The <c>reframes</c> report (output summary and EDL): each reframe — applied or why not, the
    /// spans it ran on, the centres clamped into the frame — and each shot the safe area padded.
    /// Null when nothing was configured or padded, so the output is byte-identical.
    /// </summary>
    public JsonObject? ToNode() => _node?.DeepClone().AsObject();
}

/// <summary>Builds the <see cref="ReframePlan"/> for a compile once its spans and fits are known.</summary>
internal static class ReframePlanner
{
    public static ReframePlan Build(
        VideoCompileStepConfig config,
        IReadOnlyList<VideoReframe> reframes,
        IReadOnlyList<ReframeClamp> clamps,
        VideoAnalysisArtifact artifact,
        IReadOnlyList<VideoCompileStepExecutor.ResolvedSpan> spans,
        Func<int, VideoAnalysisMedia> sourceMedia,
        VideoAnalysisMedia canvas,
        SourceFitPlan fits,
        Func<int, bool> isBridge)
    {
        HashSet<int> known = (artifact.Sources ?? []).Select(s => s.SourceIndex).Where(i => !isBridge(i)).ToHashSet();
        if (artifact.Sources is not { Count: > 0 })
            known.Add(0);
        if (reframes.FirstOrDefault(r => !known.Contains(r.SourceIndex)) is { } stray)
            return ReframePlan.Error(VideoReframeCodes.UnknownSource, $"A reframe names source {stray.SourceIndex}, which the analysis does not have.");

        Dictionary<int, IReadOnlyList<VideoReframe>> bySource = reframes
            .GroupBy(r => r.SourceIndex)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<VideoReframe>)g.OrderBy(r => r.StartSec).ToList());

        // ---- the subject safe area: a span over a text-bearing shot is padded, never cropped ----
        // A span a reframe covers (hand-written, or a followSubject expansion) is the reframe's: its author
        // chose the framing, so the guard reports the shot under safeAreaReframed and pads nothing there.
        double margin = config.SubjectSafeArea ?? VideoReframe.DefaultSafeArea;
        var safePadded = new HashSet<VideoCompileStepExecutor.ResolvedSpan>();
        var shotsNode = new JsonArray();
        var yieldedNode = new JsonArray();
        var padWindows = new Dictionary<int, List<(double, double, VideoCompileStepExecutor.SourceCanvasFit)>>();
        if (fits.CoverFit && margin > 0)
        {
            var reported = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
            var reportedYielded = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
            for (int i = 0; i < spans.Count; i++)
            {
                VideoCompileStepExecutor.ResolvedSpan span = spans[i];
                int idx = span.SourceIndex;
                if (isBridge(idx) || fits.FitOf(idx) != VideoOutputFit.Subject || fits.CropFocus.GetValueOrDefault(idx) is { Pad: true })
                    continue;
                VideoAnalysisMedia media = sourceMedia(idx);
                if (!DifferentAspect(media, canvas))
                    continue;

                bool composition = artifact.Sources?.FirstOrDefault(s => s.SourceIndex == idx)?.IsComposition == true;
                List<(VideoAnalysisShot Shot, string Reason)> textShots = artifact.Shots
                    .Where(s => s.SourceIndex == idx && s.StartSec < span.SnappedEnd && s.EndSec > span.SnappedStart)
                    .Select(s => (Shot: s, Reason: composition ? "composition" : HasOnScreenText(s) ? "on_screen_text" : TextUnknownNoSubject(s) ? "text_unknown" : null))
                    .Where(t => t.Reason is not null)
                    .Select(t => (t.Shot, t.Reason!))
                    .ToList();
                if (composition && textShots.Count == 0)
                    textShots.Add((new VideoAnalysisShot($"source-{idx}", span.SnappedStart, span.SnappedEnd, SourceIndex: idx), "composition"));
                if (textShots.Count == 0)
                    continue;

                if (bySource.TryGetValue(idx, out IReadOnlyList<VideoReframe>? owning) &&
                    owning.Any(r => ReframeFilterBuilder.Overlaps(r, span.SnappedStart, span.SnappedEnd)))
                {
                    ReportShots(yieldedNode, reportedYielded, textShots, idx, i, fit: null);
                    continue;
                }

                safePadded.Add(span);
                if (!padWindows.TryGetValue(idx, out var windows))
                    padWindows[idx] = windows = [];
                windows.Add((span.SnappedStart, span.SnappedEnd, SafePadFit(media, canvas, margin)));
                ReportShots(shotsNode, reported, textShots, idx, i, fit: "Pad");
            }
        }

        // ---- the reframes ----
        bool anyStage = false;
        var reframesNode = new JsonArray();
        for (int r = 0; r < reframes.Count; r++)
        {
            VideoReframe reframe = reframes[r];
            VideoAnalysisMedia media = sourceMedia(reframe.SourceIndex);
            var spanIndices = new JsonArray();
            for (int i = 0; i < spans.Count; i++)
            {
                if (spans[i].SourceIndex == reframe.SourceIndex &&
                    ReframeFilterBuilder.Overlaps(reframe, spans[i].SnappedStart, spans[i].SnappedEnd))
                    spanIndices.Add(i);
            }

            bool sized = media.Width > 0 && media.Height > 0;
            bool applied = sized && spanIndices.Count > 0;
            anyStage |= applied;
            var entry = new JsonObject
            {
                ["source"] = reframe.SourceIndex,
                ["fromSec"] = reframe.StartSec,
                ["toSec"] = reframe.ToSec,
                ["keyframes"] = reframe.Keyframes.Count,
                ["applied"] = applied
            };
            if (!applied)
                entry["reason"] = !sized ? "source_size_unknown" : "not_in_edit";
            entry["spans"] = spanIndices;
            var clamped = new JsonArray();
            foreach (ReframeClamp c in clamps.Where(c => c.Reframe == r))
                clamped.Add(new JsonObject { ["keyframe"] = c.Keyframe, ["axis"] = c.Axis, ["from"] = c.From, ["to"] = c.To });
            if (clamped.Count > 0)
                entry["clamped"] = clamped;
            reframesNode.Add(entry);
        }

        var timelines = new Dictionary<int, ReframeFitTimeline>();
        foreach (int idx in bySource.Keys.Concat(padWindows.Keys).Distinct())
        {
            VideoAnalysisMedia media = sourceMedia(idx);
            timelines[idx] = new ReframeFitTimeline(
                media.Width, media.Height,
                bySource.GetValueOrDefault(idx) ?? [],
                padWindows.TryGetValue(idx, out var w) ? w : [],
                canvas.Width, canvas.Height);
        }

        JsonObject? node = null;
        if (reframes.Count > 0 || shotsNode.Count > 0)
        {
            node = new JsonObject();
            if (reframes.Count > 0)
                node["reframes"] = reframesNode;
            if (shotsNode.Count > 0 || yieldedNode.Count > 0)
                node["subjectSafeArea"] = margin;
            if (shotsNode.Count > 0)
                node["safeAreaShots"] = shotsNode;
            if (yieldedNode.Count > 0)
                node["safeAreaReframed"] = yieldedNode;
        }

        return new ReframePlan(bySource, safePadded, margin, config.CanvasFill == VideoCanvasFill.Blur,
            timelines, sourceMedia, anyStage, node);
    }

    /// <summary>One report entry per shot (<c>fit</c> only on padded shots), each listing the span indices it applies to.</summary>
    private static void ReportShots(
        JsonArray target, Dictionary<string, JsonArray> seen, List<(VideoAnalysisShot Shot, string Reason)> shots,
        int sourceIndex, int spanIndex, string? fit)
    {
        foreach ((VideoAnalysisShot shot, string reason) in shots)
        {
            if (!seen.TryGetValue(shot.Id, out JsonArray? spanList))
            {
                spanList = [];
                seen[shot.Id] = spanList;
                var entry = new JsonObject { ["shot"] = shot.Id, ["source"] = sourceIndex };
                if (fit is not null)
                    entry["fit"] = fit;
                entry["reason"] = reason;
                entry["spans"] = spanList;
                target.Add(entry);
            }

            spanList.Add(spanIndex);
        }
    }

    /// <summary>
    /// Where a clip padded inside the safe area lands: fitted into <see cref="ReframeFilterBuilder.SafeBox"/>
    /// and centred on the canvas — the geometry mirror of <see cref="ReframeFilterBuilder.SafePadSuffix"/>.
    /// </summary>
    internal static VideoCompileStepExecutor.SourceCanvasFit SafePadFit(VideoAnalysisMedia source, VideoAnalysisMedia canvas, double margin)
    {
        int cw = Math.Max(2, canvas.Width) - Math.Max(2, canvas.Width) % 2;
        int ch = Math.Max(2, canvas.Height) - Math.Max(2, canvas.Height) % 2;
        if (source.Width <= 0 || source.Height <= 0)
            return VideoCompileStepExecutor.SourceCanvasFit.Identity(canvas);
        (int bw, int bh) = ReframeFilterBuilder.SafeBox(cw, ch, margin);
        double scale = Math.Min(bw / (double)source.Width, bh / (double)source.Height);
        double w = source.Width * scale, h = source.Height * scale;
        return new VideoCompileStepExecutor.SourceCanvasFit((cw - w) / 2.0, (ch - h) / 2.0, w, h);
    }

    /// <summary>
    /// The analysis never read this shot's text (no caption) and located no subject to crop toward
    /// (no <c>SubjectLabel</c> / <c>LocateSubjects</c> result): a blind crop could cut text, so the shot is padded.
    /// </summary>
    private static bool TextUnknownNoSubject(VideoAnalysisShot shot) => shot.Caption is null && shot.Subject is null;

    private static bool HasOnScreenText(VideoAnalysisShot shot) =>
        shot.Caption?.OnScreenText?.Any(t => !string.IsNullOrWhiteSpace(t)) == true;

    private static bool DifferentAspect(VideoAnalysisMedia media, VideoAnalysisMedia canvas) =>
        media.Width > 0 && media.Height > 0 && canvas.Width > 0 && canvas.Height > 0 &&
        Math.Abs(media.Width / (double)media.Height - canvas.Width / (double)canvas.Height) > 0.01;
}
