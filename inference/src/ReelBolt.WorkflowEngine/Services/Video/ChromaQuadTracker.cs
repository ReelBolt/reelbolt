using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video.Tracking;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Pure, unit-testable, deterministic chroma-plate quad tracker — the tracking engine behind
/// tracked screen inserts (see docs/video-editing.md "Tracked screen inserts (Phase 5)"). Given a
/// raw RGB24 frame grid (the exact <see cref="FrameGridResult"/> shape
/// <see cref="IFrameGridSampler"/> already produces, sampled at a higher resolution/fps than the
/// Phase 1 pass), it finds — per frame — the largest connected region of a target chroma color
/// (green/blue/magenta), fits a quadrilateral to it via the extreme-point method, and assembles
/// per-frame quads into contiguous, smoothed <see cref="VideoInsertRegionTrack"/>s with a
/// confidence score.
///
/// <para>
/// <b>Why marker/chroma-based, not general feature tracking:</b> this codebase's whole video
/// pipeline is built on "deterministic first-party C# computes every number; the model only picks
/// offered ids". A chroma plate is the one tracking target that is reliably detectable with pure
/// pixel statistics — no OpenCV, no native binding, no new dependency, fully testable against
/// synthetic in-memory frames. General markerless planar tracking (KLT/homography estimation over
/// feature correspondences) was considered and rejected for v1: it requires either a native CV
/// library (OpenCvSharp ships no musl/Alpine binaries the WorkflowEngine image could use without
/// building OpenCV from source, growing the image by hundreds of MB and adding a large native
/// attack surface parsing untrusted media — the exact class of cost the "why ffmpeg is not in the
/// sandbox" analysis weighs) or a from-scratch feature tracker whose robustness could not honestly
/// be validated in this environment. The constraint this buys is stated plainly: the source
/// footage must actually contain a uniform-color plate (the standard practice for exactly this
/// kind of commercial shot). See docs/video-editing.md for the full decision record.
/// </para>
/// </summary>
public static class ChromaQuadTracker
{
    /// <summary>Tracker knobs — every numeric here is workflow-author config or a compiled default, never model output.</summary>
    public sealed record Options(
        /// <summary>"green" / "blue" / "magenta" — matched in C# channel-ratio space only; never reaches ffmpeg.</summary>
        string ColorName = "green",
        /// <summary>Minimum fraction of frame area the matched component must cover to count as a plate.</summary>
        double MinAreaRatio = 0.004,
        /// <summary>Minimum (component area) / (fitted quad area) — rejects scattered noise and strongly non-quad blobs.</summary>
        double MinFillRatio = 0.65,
        /// <summary>Tracks shorter than this are dropped.</summary>
        double MinTrackSeconds = 1.0,
        /// <summary>Detection dropouts up to this many consecutive frames are bridged by linear interpolation instead of splitting the track.</summary>
        int MaxGapFrames = 3,
        /// <summary>Cap on emitted tracks (longest kept, then re-sorted chronologically).</summary>
        int MaxRegions = 8,
        /// <summary>Maximum normalized centroid jump between consecutive detected frames for them to belong to the same track.</summary>
        double MaxCentroidJump = 0.2,
        /// <summary>
        /// How many chroma plates may be detected SIMULTANEOUSLY in one frame — the multi-plate
        /// ("multipoint") capability. <c>1</c> (the default) keeps the original behaviour exactly:
        /// only the largest core component per frame is a plate, so two phones held up at once
        /// become one flickering track. Raise it and each component clearing the area/fill gates
        /// becomes its own candidate, associated across frames into independent concurrent tracks
        /// — which the compile step already composites independently, one insert per region id.
        /// See docs/video-editing.md "Non-planar surfaces and multi-plate compositing".
        /// </summary>
        int MaxPlatesPerFrame = 1,
        /// <summary>
        /// Measure each detected plate's silhouette — refined corners plus edge curvature
        /// (<see cref="ChromaPlateEdgeProfiler"/>) — and carry both on every keyframe. Curvature
        /// lets a bent plate be composited with a mesh warp instead of one flat quad; the refined
        /// corners improve BOTH paths, since the extreme-point fit lands inside the true corner on
        /// any rounded-corner screen. <c>false</c> leaves every keyframe's bows and refined
        /// corners unset, which the compile step reads as "planar, corners as fitted" — the exact
        /// pre-existing behaviour.
        /// </summary>
        bool MeasureCurvature = false,
        /// <summary>
        /// Grid-search each emitted track's own <c>colorkey</c> parameters
        /// (<see cref="PlateChromaKeySolver"/>) so the compile step can clip the composited insert
        /// to the plate's ACTUAL per-frame silhouette — rounded bezel corners, a camera notch, a
        /// hand crossing the screen — instead of a filled quadrilateral. <c>false</c> (the
        /// default) leaves <c>VideoInsertRegionTrack.ChromaKey</c> unset, which the compile step
        /// reads as "no matte" and composites exactly as before. See docs/video-editing.md
        /// "Silhouette matting".
        /// </summary>
        bool SolveChromaKey = false,
        /// <summary>
        /// How many robust deviations below its OWN recent behaviour a detection's strict core
        /// area (see <c>FrameQuad.CoreAreaRatio</c>) must fall before it is read as OCCLUSION —
        /// something passed in front of the plate — rather than as a genuinely smaller plate, and
        /// is discarded instead of being fitted.
        ///
        /// <para>
        /// Deliberately a SENSITIVITY and not a fixed ratio. What counts as an abnormal dip is a
        /// property of the footage: a locked-off shot of a bright plate holds its core area to a
        /// fraction of a percent, while a handheld one against foliage wanders by a couple of
        /// percent every frame, and any single ratio that suits one is either deaf or hysterical
        /// on the other. Each track therefore measures its own frame-to-frame spread and derives
        /// its own threshold from it, bounded by <see cref="MinOcclusionDropMargin"/> and
        /// <see cref="MaxOcclusionDropMargin"/> so that neither a pathologically steady nor a
        /// pathologically noisy track can drive it somewhere absurd.
        /// </para>
        /// <para>
        /// Occlusion only ever removes plate area, so the test is one-sided: a plate moving toward
        /// the camera grows and is never suspected, and one moving away shrinks gradually, which
        /// the rolling median follows. See docs/video-editing.md "Surviving occlusion".
        /// </para>
        /// </summary>
        double OcclusionSensitivity = 6.0,
        /// <summary>Smallest core-area dip that may ever count as occlusion, whatever a track's measured steadiness suggests — below this is sensor and compression noise.</summary>
        double MinOcclusionDropMargin = 0.03,
        /// <summary>Largest dip a track may demand before believing an occlusion, whatever its measured noise — past this the test would be too deaf to be worth running.</summary>
        double MaxOcclusionDropMargin = 0.30,
        /// <summary>
        /// How many consecutive frames a track may be occluded before its new, smaller geometry
        /// is accepted as the truth after all. Generous compared with
        /// <see cref="MaxGapFrames"/> — that one governs a plate that has genuinely VANISHED,
        /// whereas an occluded plate is still being detected every frame, just partially. Past
        /// this the plate has probably really changed and holding the old size would be the
        /// bigger error.
        /// </summary>
        int MaxOccludedFrames = 45,
        /// <summary>
        /// Pins hysteresis growth's purity bar (a fraction of the seed's own median chroma purity —
        /// see <see cref="DefaultGrowExcessFraction"/>). <c>null</c>, the default, means "tune it
        /// for this footage": <see cref="ChromaTrackerTuner"/> grid-searches it per source against
        /// the frames themselves before tracking.
        /// </summary>
        double? GrowExcessFraction = null,
        /// <summary>
        /// Floor of the per-frame implausibility tolerance, as a fraction of the plate's OWN
        /// diagonal in that frame — see <see cref="RejectImplausibleSamples"/>. A detection whose
        /// corners sit further than this from where its neighbours say they should be, AND
        /// further than <see cref="OutlierSpeedFactor"/> times the plate's own local motion, is
        /// a corrupted measurement (a reflection streak the edge fit latched onto, a fragment)
        /// and is replaced by interpolation. Relative to the plate, never pixels, so it means
        /// the same thing for a tablet filling the frame and a phone in the distance. 0 disables.
        /// </summary>
        double OutlierFloorFraction = 0.02,
        /// <summary>
        /// The other arm of the tolerance: how many frames' worth of the plate's own local motion
        /// a detection may deviate from a straight-line prediction before it is implausible.
        /// Needed because real motion is not linear and generated footage holds and jumps —
        /// a frame-hold followed by a double step deviates by about one frame's travel, which
        /// must pass; the reflection-corrupted frames this exists for deviated by 3-4x the
        /// plate's speed.
        /// </summary>
        double OutlierSpeedFactor = 1.5,
        /// <summary>Most of a track's detections that may be rejected as implausible, so a track that is noise throughout is left alone rather than rewritten.</summary>
        double MaxOutlierFraction = 0.3,
        /// <summary>
        /// Re-locate each frame's edges from the neighbouring frame's and keep whichever line
        /// looks more like the plate's own boundary — what keeps a finger over a corner or a
        /// reflection on the glass from dragging an edge inward. See
        /// <see cref="ChromaEdgeTemporalRefiner"/>.
        /// </summary>
        bool RefineEdgesFromNeighbours = true,
        /// <summary>
        /// Solve each track's plate as one rigid surface and repair the frames where something covers
        /// a corner or an edge (see <see cref="ChromaPlatePoseTracker"/>). A track with nothing to
        /// repair is left exactly as it was.
        /// </summary>
        bool SolveRigidPose = true);

    /// <summary>
    /// Absolute green/blue/magenta channel floor for the HIGH-confidence "core" chroma mask — the
    /// seed a plate's detection grows from. Deliberately strict: a pixel this bright in the target
    /// channel, with the core margin below, is unambiguously the plate and not scenery.
    /// </summary>
    private const int ChromaCoreFloor = 60;

    /// <summary>
    /// Absolute channel floor for the permissive "grow" mask (see <see cref="DetectQuad"/>'s
    /// hysteresis step). 24/255 is where 8-bit channel RATIOS stop being meaningful — below it,
    /// quantization and sensor noise dominate and a "green-dominant" test would fire on near-black
    /// noise. This is the floor's actual job; the old single-threshold mask instead used the CORE
    /// floor of 60 for everything, which on real footage sits in the MIDDLE of a dim plate's own
    /// brightness distribution (measured on a real 4096x2160 laptop-screen plate: the plate's green
    /// channel spans 28..88, so a flat g&gt;=60 cut captured only 66% of it — see the class remarks).
    /// </summary>
    private const int ChromaGrowFloor = 24;

    /// <summary>One frame's detection result — internal exchange type, normalized coordinates.</summary>
    /// <param name="FillScore">
    /// Two-sided quad-fit quality in 0..1: <c>fill</c> when the component under-fills its fitted
    /// quad, <c>1/fill</c> when it OVER-fills it. The old code stored <c>Math.Min(fill, 1.0)</c>,
    /// which silently rewrote the single loudest symptom of a broken extreme-point fit — a
    /// component LARGER than the quad that supposedly bounds it — into a perfect score (a 45°
    /// plate fits a degenerate triangle with fill ≈ 2.03 and used to report 1.0).
    /// </param>
    /// <param name="ChromaMargin">
    /// <c>|core component| / |grown component|</c> in 0..1 — how much of this plate was unambiguous
    /// chroma versus recovered by the tolerant grow pass. 1.0 means the strict core alone found the
    /// whole plate; a low value means the plate is dim/unevenly lit. It used to be a confidence
    /// term; it no longer is (see <see cref="EdgeContrast"/>), and survives to derive
    /// <see cref="CoreAreaRatio"/> for the occlusion test.
    /// </param>
    internal sealed record FrameQuad(
        int FrameIndex,
        double X0, double Y0, double X1, double Y1, double X2, double Y2, double X3, double Y3,
        double AreaRatio, double FillScore, double ChromaMargin,
        InsertPlateBows Bows = default,
        /// <summary>Corners refined against the silhouette (normalized), or <c>default</c> when curvature was not measured.</summary>
        (double X0, double Y0, double X1, double Y1, double X2, double Y2, double X3, double Y3) Surface = default,
        /// <summary>
        /// The grown component's own pixel indices, retained ONLY on the handful of frames the
        /// chroma-key solver learns from (see <see cref="KeySolveSampleTarget"/>) and dropped from
        /// every other frame — this is the tracker's ground truth for what the plate is, and
        /// keeping it for all frames would hold the whole clip's segmentation in memory for no
        /// reason. <c>null</c> everywhere when key solving is off.
        /// </summary>
        int[]? Component = null,
        /// <summary>The frame's strict and permissive chroma masks, retained alongside <see cref="Component"/> on sampled frames only. Their difference inside the quad is the blend band the key solver's spill term scores.</summary>
        bool[]? CoreMask = null,
        bool[]? GrowMask = null,
        /// <summary>
        /// Does the fitted quad sit on a real boundary? 0..1, from
        /// <see cref="ChromaTrackerTuner.EdgeContrast"/>: chroma purity just inside each edge minus
        /// just outside. The confidence term that can see a WRONG fit, in both directions — a fit
        /// that leaked into green scenery and one that stopped short inside a dim plate both have
        /// the same colour on either side of their edge. Defaults to 1 for synthetic frames built
        /// directly in tests.
        /// </summary>
        double EdgeContrast = 1.0,
        /// <summary>
        /// <c>true</c> for a sample synthesized by bridging a gap or an occlusion rather than
        /// detected — see <see cref="AssembleTracks"/>. Lets <see cref="TrimWeakEnds"/> tell a
        /// fragment cut off by a gap from the track's continuous body.
        /// </summary>
        bool Interpolated = false,
        /// <summary>Median target-minus-rival dominance over this detection's core component (0..255) — the plate's own level, which the edge fit and the compile step's matte both threshold against.</summary>
        double PlateDominance = 0,
        /// <summary>
        /// On a sample BRIDGED across an occlusion, the detection that frame actually produced and
        /// the occlusion test set aside. Its geometry was not trusted as a whole — something
        /// covered part of the plate — but the edges the occluder did not touch are still the
        /// plate's real edges, and <see cref="ChromaEdgeTemporalRefiner"/> offers them as
        /// candidates against the interpolated ones. <c>null</c> everywhere else.
        /// </summary>
        FrameQuad? SetAside = null)
    {
        /// <summary>
        /// Area of the STRICT core component — the grown area scaled back by the fraction of it
        /// that was unambiguous chroma. This, not <see cref="AreaRatio"/>, is the stable measure
        /// of how much plate is visible: hysteresis growth intermittently bridges a plate into
        /// green scenery behind it, and on the project's own phone clip that makes the grown area
        /// flip between 0.19 and 0.35 frame to frame while the core drifts smoothly by under 2%.
        /// </summary>
        public double CoreAreaRatio => AreaRatio * ChromaMargin;

        public double CentroidX => (X0 + X1 + X2 + X3) / 4.0;
        public double CentroidY => (Y0 + Y1 + Y2 + Y3) / 4.0;
    }

    /// <summary>
    /// Derives the detection grid's dimensions so grid space preserves the SOURCE's aspect ratio.
    /// Pure and internal so it can be unit-tested directly.
    ///
    /// <para>
    /// A fixed 320x180 grid squashes a 1080x1920 portrait source vertically by 10.7x, which breaks
    /// two things at once: vertical corner precision drops to ±10.7 source px (against ±3.4
    /// horizontal, and well past <c>InsertOverscan</c>'s 0.02 default, leaving a visible green
    /// fringe), and every physical distance measured in grid space — notably
    /// <see cref="QuadAspect"/> — is wrong by the grid/source aspect mismatch. Matching the grid to
    /// the source makes grid-space and source-space distances agree by construction.
    /// </para>
    ///
    /// <para>
    /// <paramref name="configuredWidth"/>/<paramref name="configuredHeight"/> of 0 or less mean
    /// "auto"; any positive pair is an explicit workflow-author override and is passed through
    /// (clamped) untouched. Auto starts from <paramref name="baseWidth"/> and derives the height,
    /// falling back to deriving the width from the height cap when the source is tall enough to
    /// exceed it — which is what turns a 1080x1920 source into 202x360, the aspect-matched grid
    /// measured to cut residual green fringe from 0.773% to 0.090%.
    /// </para>
    /// </summary>
    internal static (int Width, int Height) DeriveGridSize(
        int sourceWidth, int sourceHeight, int configuredWidth, int configuredHeight, int baseWidth = AutoGridBaseWidth)
    {
        if (configuredWidth > 0 && configuredHeight > 0)
            return (Math.Clamp(configuredWidth, MinGridWidth, MaxGridWidth), Math.Clamp(configuredHeight, MinGridHeight, MaxGridHeight));

        // No usable probe (a source whose dimensions could not be read) — keep the historical
        // 16:9 default rather than inventing an aspect.
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return (Math.Clamp(baseWidth, MinGridWidth, MaxGridWidth), Math.Clamp((int)Math.Round(baseWidth * 9.0 / 16.0), MinGridHeight, MaxGridHeight));

        double aspect = sourceWidth / (double)sourceHeight;
        int w = Math.Clamp(baseWidth, MinGridWidth, MaxGridWidth);
        int h = (int)Math.Round(w / aspect);

        if (h > MaxGridHeight)
        {
            h = MaxGridHeight;
            w = (int)Math.Round(h * aspect);
        }
        else if (h < MinGridHeight)
        {
            h = MinGridHeight;
            w = (int)Math.Round(h * aspect);
        }

        return (Math.Clamp(w, MinGridWidth, MaxGridWidth), Math.Clamp(h, MinGridHeight, MaxGridHeight));
    }

    /// <summary>
    /// Long-axis width of the AUTO tracking grid. Was 320; at 320 one grid pixel spans ~4 source
    /// pixels of a 1344-wide clip, and even sub-pixel edge fitting (see
    /// <see cref="ChromaEdgeLineFitter"/>) was measured to wobble 1.9 px RMS there against 1.4 px
    /// at 480 and 1.2 px at 626 — 480 is where most of the gain is for 2.25x the pixels.
    /// </summary>
    internal const int AutoGridBaseWidth = 480;

    /// <summary>
    /// Ceiling on the raw RGB grid buffer the insert pass holds in memory at once (it is read whole
    /// — the tuner and track assembly both walk it repeatedly). About what the previous fixed
    /// 3000-frame cap cost at 320x180, so a higher resolution and frame rate buy precision on
    /// short clips without raising the worst-case footprint on long ones.
    /// </summary>
    internal const long InsertGridByteBudget = 640L * 1024 * 1024;

    /// <summary>
    /// The sample rate every frame-count knob in <see cref="Options"/> and the assembly constants
    /// below was tuned at. <see cref="Track"/> rescales them to the actual rate, so a gap of
    /// "3 frames" keeps meaning 0.3 s whatever cadence the grid was sampled at.
    /// </summary>
    internal const double ReferenceSampleFps = 10.0;

    /// <summary>Highest automatic insert sample rate. A 60 fps source is sampled at 30.</summary>
    internal const double MaxAutoSampleFps = 30.0;

    /// <summary>
    /// The insert pass's sample rate. A positive <paramref name="configuredFps"/> is an explicit
    /// author choice and is honoured (clamped 0.5..30). Otherwise it is the SOURCE's own frame
    /// rate, or the smallest exact divisor of it that is at most <see cref="MaxAutoSampleFps"/>
    /// and keeps the clip within <paramref name="maxFrames"/> and <see cref="InsertGridByteBudget"/>.
    ///
    /// <para>
    /// Exact divisors matter as much as density. The previous fixed 10 fps on a 24 fps source
    /// made ffmpeg's <c>fps</c> filter pick the NEAREST source frame for each sample, so a
    /// keyframe stamped <c>i/10</c> actually showed the plate up to 21 ms earlier or later — a
    /// timing error that reads exactly like corner jitter once interpolated back to 24 fps.
    /// Measured on the phone clip: 12 fps (an exact divisor) wobbled 1.5 px where 10 fps on the
    /// same grid wobbled 2.2 px, and the full 24 fps 1.2 px.
    /// </para>
    /// </summary>
    internal static double ResolveSampleFps(
        double configuredFps, double sourceFps, double durationSec, int gridWidth, int gridHeight, int maxFrames)
    {
        if (configuredFps > 0)
            return Math.Clamp(configuredFps, 0.5, MaxAutoSampleFps);

        if (!(sourceFps > 0) || double.IsInfinity(sourceFps))
            return ReferenceSampleFps;

        long frameBytes = Math.Max(1L, (long)gridWidth * gridHeight * 3);
        double frameCap = Math.Min(Math.Max(1, maxFrames), InsertGridByteBudget / frameBytes);

        for (int divisor = 1; divisor <= 240; divisor++)
        {
            double fps = sourceFps / divisor;
            if (fps > MaxAutoSampleFps + 1e-9)
                continue;
            if (fps < 2)
                break;
            if (!(durationSec > 0) || fps * durationSec <= frameCap)
                return fps;
        }

        // A clip so long that even 2 fps would not fit: give it the most the budget allows and
        // accept the nearest-frame timing error, as every long clip did before.
        return durationSec > 0 ? Math.Clamp(frameCap / durationSec, 0.01, MaxAutoSampleFps) : ReferenceSampleFps;
    }

    /// <summary>The frame cap the sampler must enforce for a grid of this size: the configured cap or the byte budget, whichever is lower.</summary>
    internal static int SampleFrameCap(int gridWidth, int gridHeight, int maxFrames) =>
        (int)Math.Min(Math.Max(1, maxFrames), InsertGridByteBudget / Math.Max(1L, (long)gridWidth * gridHeight * 3));

    internal const int MinGridWidth = 64;
    internal const int MaxGridWidth = 640;
    internal const int MinGridHeight = 36;
    internal const int MaxGridHeight = 360;

    /// <summary>
    /// Is <paramref name="run"/> a detection of a plate already solved? True when, on most of the
    /// frames it detected anything, its centre lies inside that plate's quad.
    /// </summary>
    private static bool IsSamePlate(List<FrameQuad> run, Dictionary<int, FrameQuad> plate)
    {
        int detected = 0, inside = 0;
        foreach (FrameQuad q in run)
        {
            if (q.Interpolated)
                continue;
            detected++;
            if (!plate.TryGetValue(q.FrameIndex, out FrameQuad? p))
                continue;
            ReadOnlySpan<(double X, double Y)> ring = [(p.X0, p.Y0), (p.X1, p.Y1), (p.X3, p.Y3), (p.X2, p.Y2)];
            bool positive = false, negative = false;
            for (int k = 0; k < 4; k++)
            {
                (double X, double Y) a = ring[k], b = ring[(k + 1) % 4];
                double cross = (b.X - a.X) * (q.CentroidY - a.Y) - (b.Y - a.Y) * (q.CentroidX - a.X);
                positive |= cross > 0;
                negative |= cross < 0;
            }

            if (!(positive && negative))
                inside++;
        }

        return detected > 0 && inside >= 0.6 * detected;
    }

    /// <summary>Test hook: receives the rigid-pose solve's one-line notes (model found, growth, repair decision).</summary>
    internal static Action<string>? PoseLog { get; set; }

    /// <summary>With <see cref="PoseLog"/> set: also one note per solved frame (the measurement harness switches it on).</summary>
    internal static bool PoseLogEveryFrame { get; set; }

    /// <summary>
    /// Runs the full per-frame detection + track assembly over one raw RGB24 grid buffer. Frame
    /// <c>i</c>'s sample time is exactly <c>i / fps</c> (the <c>fps</c> filter emits CFR from t=0
    /// — same contract as <see cref="FrameGridResult"/>). Local ids are assigned <c>r0</c>,
    /// <c>r1</c>, … in chronological order; the caller remaps them into the artifact's global id
    /// space exactly like every other id kind.
    /// </summary>
    /// <param name="sourceWidth">
    /// The SOURCE video's real pixel width, used only for physical-distance measurements
    /// (<see cref="QuadAspect"/>). <c>width</c>/<c>height</c> above are the GRID's sample
    /// dimensions and are what index <paramref name="pixelData"/>; they are NOT interchangeable
    /// with the source's. Defense in depth alongside <see cref="DeriveGridSize"/>: even if a
    /// workflow author pins a grid that does not match the source's aspect, the emitted
    /// <see cref="VideoInsertRegionTrack.MeanAspectRatio"/> stays correct. 0 (the default) falls
    /// back to the grid dimensions — the pre-existing behavior, which every synthetic test that
    /// builds its own square-pixel grid relies on.
    /// </param>
    /// <param name="sourceHeight">The SOURCE video's real pixel height — see <paramref name="sourceWidth"/>.</param>
    public static IReadOnlyList<VideoInsertRegionTrack> Track(
        byte[] pixelData, int frameCount, int width, int height, double fps, Options options,
        int sourceWidth = 0, int sourceHeight = 0)
    {
        if (frameCount <= 0 || width <= 0 || height <= 0 || fps <= 0)
            return Array.Empty<VideoInsertRegionTrack>();

        int frameSize = width * height * 3;

        // Frame-count knobs are expressed at ReferenceSampleFps; rescale them so they keep their
        // meaning in SECONDS at whatever rate this grid was sampled. Never scaled down: below the
        // reference rate a frame already spans more time than these were tuned for. The smoother's
        // half-widths (InsertTrackSmoother) are deliberately left in grid frames — it exists to average
        // out per-frame measurement noise, and at a higher rate the same frames lag motion LESS.
        double frameScale = Math.Max(1.0, fps / ReferenceSampleFps);
        options = options with
        {
            MaxGapFrames = ScaleFrames(options.MaxGapFrames, frameScale),
            MaxOccludedFrames = ScaleFrames(options.MaxOccludedFrames, frameScale)
        };

        // Self-tuning: unless the author pinned it, pick the growth bar that fits THIS footage
        // before running the full pass. See ChromaTrackerTuner.
        VideoInsertTrackTuning? tuning = null;
        if (options.GrowExcessFraction is null)
        {
            tuning = ChromaTrackerTuner.Tune(pixelData, frameCount, width, height, options);
            options = options with { GrowExcessFraction = tuning.Parameters[ChromaTrackerTuner.GrowExcessFractionKey] };
        }

        // Frames whose segmentation is kept for the chroma-key grid search — evenly spaced across
        // the clip so a track of any length and position gets evidence, and capped so retention
        // stays a fixed small cost regardless of clip length.
        int retainStride = options.SolveChromaKey
            ? Math.Max(1, frameCount / KeySolveSampleTarget)
            : int.MaxValue;

        var perFrame = new List<FrameQuad>[frameCount];
        for (int f = 0; f < frameCount; f++)
        {
            int offset = f * frameSize;
            perFrame[f] = offset + frameSize > pixelData.Length
                ? []
                : DetectQuads(pixelData, offset, width, height, f, options, f % retainStride == 0);
        }

        PoseLog?.Invoke("stage: detections done, assembling");
        var poseFrames = new ChromaPlatePoseTracker.FrameSource(pixelData, width, height, NormalizeColorName(options.ColorName));
        List<List<FrameQuad>> assembled = AssembleTracks(perFrame, options, frameScale)
            .Select(TrimWeakEnds)
            .Where(r => r.Count > 0)
            .ToList();

        // Longest first: a run the rigid-pose solve repairs is followed over every frame its plate is
        // in view, which is often where assembly had started ANOTHER run of the same plate (it ends a
        // track that stays occluded and opens a new one afterwards). Those are the same plate seen
        // again, not a second one, and are dropped.
        var refined = new List<FrameQuad>?[assembled.Count];
        var solvedPlates = new List<Dictionary<int, FrameQuad>>();
        foreach (int i in Enumerable.Range(0, assembled.Count)
            .OrderByDescending(i => assembled[i].Count(q => !q.Interpolated))
            .ThenBy(i => i))
        {
            List<FrameQuad> r = assembled[i];
            if (solvedPlates.Any(plate => IsSamePlate(r, plate)))
                continue;

            if (options.SolveRigidPose)
            {
                long before = poseFrames.FramesSolved;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                (List<FrameQuad> solved, bool repaired) = ChromaPlatePoseTracker.Refine(r, poseFrames, options.MeasureCurvature, PoseLog);
                PoseLog?.Invoke(FormattableString.Invariant(
                    $"run {r[0].FrameIndex}-{r[^1].FrameIndex} ({r.Count(q => !q.Interpolated)} detections): {(repaired ? "repaired" : "left as it was")}, {poseFrames.FramesSolved - before} frame solves, {clock.ElapsedMilliseconds} ms"));
                if (repaired)
                {
                    // Every edge is already where the plate's own evidence puts it; the
                    // neighbour-seeded refit would only second-guess the edges held through an
                    // occlusion.
                    refined[i] = solved;
                    solvedPlates.Add(solved.ToDictionary(q => q.FrameIndex));
                    continue;
                }
            }

            refined[i] = options.RefineEdgesFromNeighbours
                ? ChromaEdgeTemporalRefiner.Refine(r, pixelData, width, height, NormalizeColorName(options.ColorName))
                : r;
        }

        PoseLog?.Invoke($"stage: {assembled.Count} runs assembled and refined");
        List<List<FrameQuad>> runs = refined
            .Where(r => r is not null)
            .Select(r => r!)
            .Select(r => RejectImplausibleSamples(r, fps, width, height, options).Run)
            .Where(r => r.Count > 0)
            .Select(r => UprightTrackLabels(r, width, height))
            .ToList();

        PoseLog?.Invoke($"stage: {runs.Count} runs after the outlier pass");
        var tracks = new List<VideoInsertRegionTrack>();
        double sourcePxPerGridPx = sourceWidth > 0 ? sourceWidth / (double)width : 1.0;
        foreach (List<FrameQuad> run in runs)
        {
            double startSec = run[0].FrameIndex / fps;
            double endSec = (run[^1].FrameIndex + 1) / fps;
            if (endSec - startSec < options.MinTrackSeconds)
                continue;

            // Robust, motion-adaptive local quadratic, aware of held (repeated) source frames; corners, surface
            // corners and bows are smoothed together. See InsertTrackSmoother.
            List<FrameQuad> smoothed = InsertTrackSmoother.Smooth(
                run, InsertTrackSmoother.DetectHeld(pixelData, frameCount, width, height, run), sourcePxPerGridPx, width, height);

            // Confidence is a product of four INDEPENDENT axes, deliberately measuring different
            // failure modes (see docs/video-editing.md "Tracked screen inserts (Phase 5)"):
            //
            //   coverage      — temporal: how many frames of the run actually detected anything.
            //   meanFillScore — geometric: does the fitted quad actually bound its own component?
            //                   Two-sided, so a degenerate/under-covering fit scores low instead of
            //                   being clamped up to a perfect 1.0 (see FrameQuad.FillScore).
            //   meanEdgeContrast — photometric: does each fitted edge sit on a real plate boundary
            //                   (see FrameQuad.EdgeContrast)? This replaced the old chroma-margin
            //                   term (core/grown pixel ratio), which docked a CORRECT fit of a dim
            //                   plate for needing growth — the laptop fixtures read ~0.64 however
            //                   right they were — while being unable to tell a leaked fit from a
            //                   good one, since a leak into scenery is also "grown".
            //   consistency   — temporal: do this frame's corners agree with their neighbours'?
            //                   Every other term is computed one frame at a time, so a track whose
            //                   corners leapt onto scenery on half its frames — each frame a tidy,
            //                   well-filled quad — still scored 0.71 and was composited. See
            //                   TemporalConsistency.
            //
            // The old formula was coverage * meanFill with meanFill clamped to 1.0 — which made the
            // second term a constant 1.0 for essentially every real detection (a discretized quad
            // fit normally over-fills slightly, ~1.02-1.07) and left NO term at all that could see
            // under-detection. That is why a visibly wrong, 66%-of-the-plate fit on real footage
            // still reported 0.9996 and sailed past MinInsertConfidence.
            int spanFrames = run[^1].FrameIndex - run[0].FrameIndex + 1;
            double coverage = Math.Clamp(run.Count / (double)Math.Max(1, spanFrames), 0, 1);
            double meanFillScore = run.Average(q => q.FillScore);
            double meanEdgeContrast = run.Average(q => q.EdgeContrast);
            double consistency = TemporalConsistency(run, ScaleFrames(ConsistencyHalfWindow, frameScale));
            double confidence = Math.Clamp(coverage * meanFillScore * meanEdgeContrast * consistency, 0, 1);

            double meanArea = run.Average(q => q.AreaRatio);
            int aspectW = sourceWidth > 0 ? sourceWidth : width;
            int aspectH = sourceHeight > 0 ? sourceHeight : height;
            double meanAspect = run.Average(q => QuadAspect(q, aspectW, aspectH));

            // Median rather than mean: the bow of a frame where a hand clipped the plate edge is
            // an outlier, and one such frame must not tilt the whole track's surface class.
            double medianCurvature = Median(smoothed.Select(q => q.Bows.MaxMagnitude).ToList());

            // The key is solved per TRACK, from that track's own retained frames — two plates in
            // one shot can be different colours and different brightnesses, so one key for the
            // whole clip would be the very clip-wide compromise this search exists to avoid.
            PlateChromaKey chromaKey = options.SolveChromaKey
                ? PlateChromaKeySolver.Solve(run
                    .Where(q => q.Component is { Length: > 0 })
                    .Select(q => new PlateChromaKeySolver.Sample(
                        pixelData, q.FrameIndex * frameSize, width, height, q.Component!,
                        q.CoreMask, q.GrowMask,
                        [
                            (q.X0 * width - 0.5, q.Y0 * height - 0.5), (q.X1 * width - 0.5, q.Y1 * height - 0.5),
                            (q.X2 * width - 0.5, q.Y2 * height - 0.5), (q.X3 * width - 0.5, q.Y3 * height - 0.5)
                        ]))
                    .ToList())
                : PlateChromaKey.None;

            tracks.Add(new VideoInsertRegionTrack(
                Id: string.Empty, // assigned below, after the MaxRegions cap, so ids stay chronological+contiguous
                ShotId: null,     // resolved by the caller against its own shot list
                StartSec: startSec,
                EndSec: endSec,
                Keyframes: smoothed
                    .Select(q => new VideoInsertQuadKeyframe(
                        q.FrameIndex / fps, q.X0, q.Y0, q.X1, q.Y1, q.X2, q.Y2, q.X3, q.Y3,
                        ToDto(q.Bows), SurfaceDto(q)))
                    .ToList(),
                Confidence: Math.Round(confidence, 4),
                MeanAreaRatio: Math.Round(meanArea, 5),
                MeanAspectRatio: Math.Round(meanAspect, 3),
                MotionClass: ClassifyMotion(smoothed, fps),
                ColorName: NormalizeColorName(options.ColorName),
                MeanCurvature: Math.Round(medianCurvature, 5),
                ChromaKey: KeyDto(chromaKey, Median(run.Where(q => !q.Interpolated).Select(q => q.PlateDominance).ToList())),
                Tuning: tuning));
        }

        int maxRegions = Math.Max(0, options.MaxRegions);
        if (tracks.Count > maxRegions)
        {
            tracks = tracks
                .OrderByDescending(t => t.EndSec - t.StartSec)
                .Take(maxRegions)
                .OrderBy(t => t.StartSec)
                .ToList();
        }

        for (int i = 0; i < tracks.Count; i++)
            tracks[i] = tracks[i] with { Id = $"r{i}" };

        return AttachUncoveredPlateSpans(tracks, pixelData, frameCount, width, height, fps, options);
    }

    /// <summary>
    /// Fraction of <see cref="Options.MinAreaRatio"/> a frame's strict plate pixels must cover for
    /// the plate to count as VISIBLE there. A quarter: well below what a track needs, because the
    /// question is no longer "is this a trustworthy plate" but "would raw green show" — the whip
    /// clip's first frames show a 0.2%-of-frame sliver that fails every tracking gate and still
    /// reads as a green flash.
    /// </summary>
    internal const double VisiblePlateAreaFraction = 0.25;

    /// <summary>
    /// Finds every sampled frame where the plate is visible (<see cref="VisiblePlateAreaFraction"/>)
    /// but no emitted track covers it, groups them into runs, and hands each run to the track
    /// nearest to it in time as <see cref="VideoInsertRegionTrack.UncoveredPlateSpans"/>. With no
    /// track at all there is nothing to attach to — no geometry and no key to neutralise with —
    /// and the tracks are returned unchanged.
    /// </summary>
    internal static IReadOnlyList<VideoInsertRegionTrack> AttachUncoveredPlateSpans(
        List<VideoInsertRegionTrack> tracks, byte[] pixelData, int frameCount, int width, int height, double fps, Options options)
    {
        if (tracks.Count == 0)
            return tracks;

        string color = NormalizeColorName(options.ColorName);
        int pixels = width * height;
        int frameSize = pixels * 3;
        double minVisible = Math.Max(0, options.MinAreaRatio) * VisiblePlateAreaFraction * pixels;
        var runs = new List<(int Start, int End, int X0, int Y0, int X1, int Y1)>(); // [Start, End] inclusive frames, plate box
        int runStart = -1, bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = -1, by1 = -1;
        for (int f = 0; f <= frameCount; f++)
        {
            bool uncovered = false;
            if (f < frameCount && (long)(f + 1) * frameSize <= pixelData.Length)
            {
                double t = f / fps;
                if (!tracks.Any(tr => t >= tr.StartSec - 1e-6 && t < tr.EndSec - 1e-6))
                {
                    int core = 0, offset = f * frameSize;
                    int fx0 = int.MaxValue, fy0 = int.MaxValue, fx1 = -1, fy1 = -1;
                    for (int p = 0; p < pixels; p++)
                    {
                        int b0 = offset + p * 3;
                        if (!IsCorePlate(color, pixelData[b0], pixelData[b0 + 1], pixelData[b0 + 2]))
                            continue;
                        core++;
                        int x = p % width, y = p / width;
                        fx0 = Math.Min(fx0, x);
                        fy0 = Math.Min(fy0, y);
                        fx1 = Math.Max(fx1, x);
                        fy1 = Math.Max(fy1, y);
                    }

                    uncovered = core >= minVisible && minVisible > 0;
                    if (uncovered)
                    {
                        bx0 = Math.Min(bx0, fx0);
                        by0 = Math.Min(by0, fy0);
                        bx1 = Math.Max(bx1, fx1);
                        by1 = Math.Max(by1, fy1);
                    }
                }
            }

            if (uncovered && runStart < 0)
                runStart = f;
            else if (!uncovered && runStart >= 0)
            {
                runs.Add((runStart, f - 1, bx0, by0, bx1, by1));
                runStart = -1;
                bx0 = by0 = int.MaxValue;
                bx1 = by1 = -1;
            }
        }

        if (runs.Count == 0)
            return tracks;

        var spans = new List<VideoInsertPlateSpan>[tracks.Count];
        foreach ((int start, int end, int x0, int y0, int x1, int y1) in runs)
        {
            double s = start / fps, e = (end + 1) / fps;
            int nearest = Enumerable.Range(0, tracks.Count)
                .OrderBy(i => Math.Max(0, Math.Max(tracks[i].StartSec - e, s - tracks[i].EndSec)))
                .ThenBy(i => i)
                .First();
            (spans[nearest] ??= []).Add(new VideoInsertPlateSpan(
                Math.Round(s, 6), Math.Round(e, 6),
                Math.Round(x0 / (double)width, 5), Math.Round(y0 / (double)height, 5),
                Math.Round((x1 + 1 - x0) / (double)width, 5), Math.Round((y1 + 1 - y0) / (double)height, 5)));
        }

        return tracks
            .Select((t, i) => spans[i] is null ? t : t with { UncoveredPlateSpans = spans[i] })
            .ToList();
    }

    /// <summary>The strict "core" chroma test of <see cref="DetectQuads"/> for one pixel.</summary>
    private static bool IsCorePlate(string color, byte r, byte g, byte b) => color switch
    {
        "blue" => b >= ChromaCoreFloor && b * 10 > r * 13 + 100 && b * 10 > g * 13 + 100,
        "magenta" => r >= ChromaCoreFloor && b >= ChromaCoreFloor && r * 10 > g * 13 + 100 && b * 10 > g * 13 + 100,
        _ => g >= ChromaCoreFloor && g * 10 > r * 13 + 100 && g * 10 > b * 13 + 100
    };

    /// <summary>"green"/"blue"/"magenta", defaulting unknown values to "green" — C#-side only, never an ffmpeg value.</summary>
    internal static string NormalizeColorName(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "blue" => "blue",
            "magenta" => "magenta",
            _ => "green"
        };

    /// <summary>
    /// Per-frame detection: dual-threshold chroma mask → 4-connected CORE components → hysteresis
    /// growth through the permissive mask → extreme-point quad fit → area, degeneracy and fill
    /// gates, keeping the <see cref="Options.MaxPlatesPerFrame"/> largest survivors in descending
    /// core-area order. With the default cap of 1 this is exactly the largest-component-only
    /// detector. Internal so tests can drive a single synthetic frame directly.
    /// </summary>
    internal static List<FrameQuad> DetectQuads(
        byte[] pixelData, int frameOffset, int width, int height, int frameIndex, Options options,
        bool retainComponents = false)
    {
        string color = NormalizeColorName(options.ColorName);
        int pixelCount = width * height;

        // 1. Chroma masks — TWO levels (hysteresis), not one.
        //
        //    The relative channel-dominance test is already brightness-robust; the ABSOLUTE floor
        //    next to it is not, and a single floor of 60 is what broke real footage: on a dim,
        //    unevenly-lit laptop-screen plate the green channel spans 28..88 across the plate
        //    itself, so g>=60 sliced the plate in half and the extreme-point fit below then fitted
        //    a quad to the brighter fragment — confidently, and wrongly.
        //
        //    Fix: classic dual-threshold hysteresis. The CORE mask (unchanged from before, floor
        //    60) seeds detection with pixels that are unambiguously the plate; the GROW mask (floor
        //    24, a proportionally relaxed dominance margin) then extends that seed — and ONLY that
        //    seed, by 4-connectivity — to recover the plate's own dim regions. Growth cannot jump
        //    to a different object: an unrelated green-ish blob is admitted only if it is
        //    physically contiguous with the high-confidence core, and if growth ever did merge
        //    scenery into the region the existing MinFillRatio gate below rejects the resulting
        //    non-quadrilateral blob exactly as it always has. How much growth was needed is
        //    recorded as ChromaMargin (the occlusion test's core-area measure).
        var coreMask = new bool[pixelCount];
        var growMask = new bool[pixelCount];
        var excess = new float[pixelCount];
        var dominance = new float[pixelCount];
        for (int p = 0; p < pixelCount; p++)
        {
            int b0 = frameOffset + p * 3;
            byte r = pixelData[b0], g = pixelData[b0 + 1], b = pixelData[b0 + 2];
            excess[p] = ChromaExcess(color, r, g, b);
            dominance[p] = ChromaEdgeLineFitter.Dominance(color, r, g, b);
            switch (color)
            {
                case "blue":
                    coreMask[p] = b >= ChromaCoreFloor && b * 10 > r * 13 + 100 && b * 10 > g * 13 + 100;
                    growMask[p] = b >= ChromaGrowFloor && b * 10 > r * 11 + 20 && b * 10 > g * 11 + 20;
                    break;
                case "magenta":
                    coreMask[p] = r >= ChromaCoreFloor && b >= ChromaCoreFloor && r * 10 > g * 13 + 100 && b * 10 > g * 13 + 100;
                    growMask[p] = r >= ChromaGrowFloor && b >= ChromaGrowFloor && r * 10 > g * 11 + 20 && b * 10 > g * 11 + 20;
                    break;
                default:
                    coreMask[p] = g >= ChromaCoreFloor && g * 10 > r * 13 + 100 && g * 10 > b * 13 + 100;
                    growMask[p] = g >= ChromaGrowFloor && g * 10 > r * 11 + 20 && g * 10 > b * 11 + 20;
                    break;
            }
        }

        // 2. 4-connected CORE components (iterative BFS — the grid is small, <= 640x360), largest
        //    first. Only the first MaxPlatesPerFrame that survive every gate below are kept; the
        //    default cap of 1 makes this the original largest-component-only path.
        int maxPlates = Math.Clamp(options.MaxPlatesPerFrame, 1, 16);
        var quads = new List<FrameQuad>(maxPlates);
        bool[]? componentMask = null;

        // Pixels already absorbed by an accepted plate's growth. Hysteresis growth is what makes
        // this necessary: the permissive mask routinely bridges what the strict mask left as
        // SEVERAL core fragments of one physical plate, so growing each core independently would
        // return the same region two or three times over. On the real two-plate fixture that is
        // exactly what happened — three byte-identical detections of the same phone screen, which
        // the track assembler then split into several competing low-confidence tracks. A core
        // already claimed by a larger plate's growth is part of that plate, not a new one.
        var claimed = new bool[pixelCount];

        foreach (List<int> coreComponent in RankedComponents(coreMask, width, height))
        {
            if (quads.Count >= maxPlates)
                break;

            if (claimed[coreComponent[0]] || coreComponent.Any(p => claimed[p]))
                continue;

            // 2b. Hysteresis growth: extend THIS core seed through the permissive mask — but only
            //     into pixels whose chroma purity is comparable to the seed's OWN. See
            //     DefaultGrowExcessFraction for why the permissive mask alone floods into foliage.
            float minExcess = (float)(MedianExcess(coreComponent, excess) * (options.GrowExcessFraction ?? DefaultGrowExcessFraction));
            List<int> grown = GrowComponent(coreComponent, growMask, excess, minExcess, width, height);
            FrameQuad? quad = FitQuad(
                coreComponent.Count, grown, ref componentMask, width, height, frameIndex, pixelCount, options,
                dominance, MedianOf(coreComponent, dominance));
            if (quad is null)
                continue;

            quad = quad with
            {
                EdgeContrast = ChromaTrackerTuner.EdgeContrast(pixelData, frameOffset, width, height, color, quad),
                PlateDominance = MedianOf(coreComponent, dominance)
            };

            if (retainComponents && options.SolveChromaKey)
                quad = quad with { Component = grown.ToArray(), CoreMask = coreMask, GrowMask = growMask };

            foreach (int p in grown)
                claimed[p] = true;

            quads.Add(quad);
        }

        return quads;
    }

    /// <summary>
    /// Back-compat single-plate entry point: the largest component's quad, or <c>null</c>.
    /// Preserved because it is the shape the original detector exposed and the shape the
    /// synthetic per-frame tests drive.
    /// </summary>
    internal static FrameQuad? DetectQuad(
        byte[] pixelData, int frameOffset, int width, int height, int frameIndex, Options options) =>
        DetectQuads(pixelData, frameOffset, width, height, frameIndex, options with { MaxPlatesPerFrame = 1 })
            .FirstOrDefault();

    /// <summary>
    /// Extreme-point quad fit plus the area, degeneracy and fill gates, for one already-grown
    /// component. <paramref name="componentMask"/> is a scratch buffer reused across the
    /// components of one frame — populated only when curvature is being measured, and only with
    /// THIS component's pixels, so a second plate elsewhere in frame can never be probed into
    /// this one's edge profile.
    /// </summary>
    private static FrameQuad? FitQuad(
        int coreComponentSize, List<int> bestComponent, ref bool[]? componentMask,
        int width, int height, int frameIndex, int pixelCount, Options options,
        float[] dominance, double plateDominance)
    {
        int bestComponentSize = bestComponent.Count;
        double chromaMargin = Math.Clamp(coreComponentSize / (double)Math.Max(1, bestComponentSize), 0, 1);

        double areaRatio = bestComponentSize / (double)pixelCount;
        if (areaRatio < options.MinAreaRatio)
            return null;

        // 3. Extreme-point quad fit IN THE PLATE'S OWN FRAME: TL=min(x'+y'), BR=max(x'+y'),
        //    TR=max(x'-y'), BL=min(x'-y'), where x',y' are the pixel coordinates rotated by minus
        //    the component's measured orientation (see ChromaQuadOrientation). In image axes the
        //    rule only works for a roughly upright plate: past ~30 degrees two of the four
        //    extremes converge on the same physical corner, and at 45 degrees the "quad" is a
        //    triangle. Real footage passes through that angle — a tablet turned from landscape to
        //    portrait, a phone whipped up toward the lens — and every frame of the turn used to
        //    fit a kite. Measuring the orientation first makes the fit angle-independent; for an
        //    upright plate the angle is ~0 and this is the classic fit unchanged.
        double orientation = ChromaQuadOrientation.DominantAngle(bestComponent, width, height);
        ((double x, double y) tl, (double x, double y) tr, (double x, double y) br, (double x, double y) bl) =
            ChromaQuadOrientation.ExtremeCorners(bestComponent, width, orientation);

        // 3b. Degenerate-fit guard, kept as a backstop: two extremes on the SAME pixel is not a
        //     quad fit at all, and a perspective warp onto it is meaningless — reject rather than
        //     composite garbage. With the orientation measured above this now only fires on a
        //     component too thin or too scattered to have four corners.
        if (SamePoint(tl, tr) || SamePoint(tl, bl) || SamePoint(br, tr) || SamePoint(br, bl))
            return null;

        // 4. Fill-ratio gate: component area vs. the fitted quad's own (shoelace) area — rejects
        //    strongly non-quadrilateral blobs (L-shapes, scattered noise the mask happened to join).
        double quadArea = Math.Abs(
            Shoelace(tl, tr, br) + Shoelace(tl, br, bl));
        if (quadArea < 1)
            return null;

        double fillRatio = Math.Clamp(bestComponentSize / quadArea, 0, 1.5);
        if (fillRatio < options.MinFillRatio)
            return null;

        // Two-sided fit quality. A discretized quad normally over-fills a little (pixel-center
        // coordinates lose a half-pixel border all round, ~1.02-1.07 in practice), so mild
        // over-unity is expected and barely penalized — but a fit that misses a large part of its
        // own component (a rotated plate's collapsing extremes reach ~2.0) is scored for what it
        // is. The previous Math.Min(fillRatio, 1.0) made exactly that case look perfect.
        double fillScore = Math.Clamp(fillRatio <= 1.0 ? fillRatio : 1.0 / fillRatio, 0, 1);

        // 4b. Sub-pixel corners: fit a line to each edge from the intensity ramp and intersect
        //     them (see ChromaEdgeLineFitter). The extreme-point corners above stay the gating
        //     geometry — fill and degeneracy are properties of the segmentation — but the TRACKED
        //     corners are the refined ones whenever the fit succeeds, since the extreme pixel is
        //     both quantized to the grid and pulled inside any rounded corner.
        ChromaEdgeLineFitter.Result? refined = ChromaEdgeLineFitter.Fit(
            dominance, width, height, plateDominance, tl, tr, br, bl);
        if (refined is { } r)
        {
            tl = r.Corners[0];
            tr = r.Corners[1];
            br = r.Corners[2];
            bl = r.Corners[3];
        }

        // Straight edges mean a flat plate, and a flat plate is described completely by its
        // line-fitted corners — so the binary-mask profiler is not consulted at all. It used to
        // be, and on the project's flat phone clip it read grid-quantization ripple as a bow on
        // most frames and moved the corners the compile step actually uses, adding ~1.5 px of
        // wobble on top of the tracker's own.
        bool lineFitFlat = refined is { } flatCheck && flatCheck.MaxBowFraction < FlatEdgeBowFraction;

        // Normalize to 0..1 of frame dimensions, at pixel centers, so the coordinates are
        // resolution-independent (the compile step multiplies by the canonical encode dimensions).
        double NX(double x) => (x + 0.5) / width;
        double NY(double y) => (y + 0.5) / height;

        // Silhouette measurement (opt-in) — against THIS component alone, in grid pixel space,
        // stored as dimensionless fractions so it replays unchanged at encode resolution. Left at
        // `default` when off, which is what makes the persisted payloads null and keeps an old
        // artifact indistinguishable from a new unmeasured one. See ChromaPlateEdgeProfiler.
        InsertPlateBows bows = InsertPlateBows.Flat;
        (double, double, double, double, double, double, double, double) surface = default;

        if (options.MeasureCurvature && lineFitFlat)
        {
            surface = (NX(tl.x), NY(tl.y), NX(tr.x), NY(tr.y), NX(bl.x), NY(bl.y), NX(br.x), NY(br.y));
        }
        else if (options.MeasureCurvature)
        {
            if (componentMask is null)
                componentMask = new bool[pixelCount];
            else
                Array.Clear(componentMask);

            foreach (int p in bestComponent)
                componentMask[p] = true;

            PlateSurface measured = ChromaPlateEdgeProfiler.Measure(componentMask, width, height, tl, tr, bl, br);
            bows = measured.Bows;

            surface = (
                NX(measured.Tl.X), NY(measured.Tl.Y), NX(measured.Tr.X), NY(measured.Tr.Y),
                NX(measured.Bl.X), NY(measured.Bl.Y), NX(measured.Br.X), NY(measured.Br.Y));
        }

        return new FrameQuad(
            frameIndex,
            NX(tl.x), NY(tl.y), NX(tr.x), NY(tr.y), NX(bl.x), NY(bl.y), NX(br.x), NY(br.y),
            areaRatio, fillScore, chromaMargin, bows, surface);

        static double Shoelace((double x, double y) a, (double x, double y) b, (double x, double y) c) =>
            0.5 * ((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y));

        static bool SamePoint((double x, double y) a, (double x, double y) b) =>
            Math.Abs(a.x - b.x) < 1e-9 && Math.Abs(a.y - b.y) < 1e-9;
    }

    /// <summary>
    /// Every 4-connected component of <paramref name="mask"/>, in descending size order. The
    /// caller takes as many as it can use rather than this method truncating, because hysteresis
    /// growth may make a later candidate redundant — see the claimed-pixel pass in
    /// <see cref="DetectQuads"/>. Ties are broken by the component's first (raster-order) pixel so
    /// the ordering is total and deterministic: two equally-sized plates must not swap places
    /// between runs, since their <c>r{n}</c> ids follow from it.
    /// </summary>
    private static List<List<int>> RankedComponents(bool[] mask, int width, int height)
    {
        int pixelCount = width * height;
        var componentOf = new int[pixelCount];
        Array.Fill(componentOf, -1);
        var components = new List<List<int>>();
        var queue = new Queue<int>();

        for (int seed = 0; seed < pixelCount; seed++)
        {
            if (!mask[seed] || componentOf[seed] >= 0)
                continue;

            var component = new List<int>();
            componentOf[seed] = seed;
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                int p = queue.Dequeue();
                component.Add(p);
                int x = p % width, y = p / width;
                Consider(x - 1, y);
                Consider(x + 1, y);
                Consider(x, y - 1);
                Consider(x, y + 1);

                void Consider(int nx, int ny)
                {
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                        return;
                    int np = ny * width + nx;
                    if (mask[np] && componentOf[np] < 0)
                    {
                        componentOf[np] = seed;
                        queue.Enqueue(np);
                    }
                }
            }

            components.Add(component);
        }

        return components
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c[0])
            .ToList();
    }

    /// <summary>
    /// Hysteresis growth: flood-fills outward from <paramref name="seed"/> through
    /// <paramref name="growMask"/>, returning the seed plus everything 4-connected to it. Pixels
    /// that satisfy the permissive mask but are NOT contiguous with the high-confidence seed are
    /// deliberately never admitted. Connectivity alone is NOT enough on a scene that is itself
    /// green-ish, which is why <paramref name="minExcess"/> exists — see
    /// <see cref="DefaultGrowExcessFraction"/>.
    /// </summary>
    private static List<int> GrowComponent(
        List<int> seed, bool[] growMask, float[] excess, float minExcess, int width, int height)
    {
        var included = new bool[width * height];
        var queue = new Queue<int>();
        var result = new List<int>(seed.Count);
        foreach (int p in seed)
        {
            included[p] = true;
            result.Add(p);
            queue.Enqueue(p);
        }

        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            int x = p % width, y = p / width;
            Consider(x - 1, y);
            Consider(x + 1, y);
            Consider(x, y - 1);
            Consider(x, y + 1);

            void Consider(int nx, int ny)
            {
                if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                    return;
                int np = ny * width + nx;
                if (growMask[np] && !included[np] && excess[np] >= minExcess)
                {
                    included[np] = true;
                    result.Add(np);
                    queue.Enqueue(np);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Fraction of the seed's own median chroma purity (<see cref="ChromaExcess"/>) a pixel must
    /// reach to be grown into. The permissive grow mask is a fixed "target channel dominates by
    /// ~10%" test, and that is satisfied by ordinary foliage: on the project's handheld phone clip
    /// lawn reads about (70, 93, 26) — purity 0.1-0.3 — against a plate of (0, 230, 0), purity
    /// 1.0, and wherever the coarse grid thinned the bezel to nothing the plate flooded into the
    /// grass. The extreme-point corners then jumped onto the lawn on roughly half the frames,
    /// the tracked quad swung far outside a phone that was barely moving, and the chroma-key
    /// solver — scoring against that leaked segmentation — found no key that agreed and dropped
    /// the silhouette matte entirely.
    ///
    /// <para>
    /// Relative, not absolute, because the plate is what sets the bar. The dim laptop plate this
    /// growth was introduced for has a core purity of ~0.63 and genuinely dim regions at 0.5-0.6,
    /// all of which a half-of-median bar keeps; the phone's bar is 0.5, which every lawn pixel
    /// fails. Measured on all four real fixtures before/after — see docs/video-editing.md
    /// "Keeping growth on the plate".
    /// </para>
    /// </summary>
    internal const double DefaultGrowExcessFraction = 0.5;

    /// <summary>
    /// Largest per-edge bow (<see cref="ChromaEdgeLineFitter.Result.MaxBowFraction"/>) at which a
    /// line-fitted plate still counts as FLAT. Measured on the project's flat phone clip at the
    /// 480-wide auto grid: median 0.007, 90th percentile 0.011 — grid ripple and the last of the
    /// corner rounding, not a bend. The synthetic barrel plate in the tests reads 0.14, and the
    /// profiler's own notes put a real barrel-curved phone at 0.055. A frame above this still
    /// goes through <see cref="ChromaPlateEdgeProfiler"/> exactly as before, so a genuinely
    /// curved plate keeps its mesh.
    /// </summary>
    internal const double FlatEdgeBowFraction = 0.025;

    /// <summary>
    /// Chroma purity of one pixel in 0..1: how far the target channel(s) rise above the strongest
    /// competing channel, relative to the target itself. 1.0 is a perfectly saturated plate, 0 or
    /// below is not the target colour at all. Brightness-independent, so a dim region of a plate
    /// scores like its bright regions while a dull green leaf scores far lower.
    /// </summary>
    internal static float ChromaExcess(string color, byte r, byte g, byte b)
    {
        int target, rival;
        switch (color)
        {
            case "blue":
                target = b;
                rival = Math.Max(r, g);
                break;
            case "magenta":
                target = Math.Min(r, b);
                rival = g;
                break;
            default:
                target = g;
                rival = Math.Max(r, b);
                break;
        }

        return target <= 0 ? 0f : (target - rival) / (float)target;
    }

    private static double MedianExcess(List<int> component, float[] excess) => MedianOf(component, excess);

    private static double MedianOf(List<int> component, float[] signal)
    {
        var values = new float[component.Count];
        for (int i = 0; i < values.Length; i++)
            values[i] = signal[component[i]];
        Array.Sort(values);
        return values.Length == 0 ? 0 : values[values.Length / 2];
    }

    /// <summary>One track still accepting detections, plus the state that lets it survive an occlusion.</summary>
    private sealed class OpenTrack
    {
        public required List<FrameQuad> Samples { get; init; }

        /// <summary>Last frame that produced a detection for this track AT ALL, occluded or not — governs the "plate has vanished" expiry.</summary>
        public int LastSeenFrame { get; set; }

        /// <summary>Last frame whose detection was actually trusted and appended — governs the "occluded too long" expiry.</summary>
        public int LastAcceptedFrame { get; set; }

        public FrameQuad Last => Samples[^1];

        /// <summary>Detections the occlusion test set aside since the last accepted sample, by frame — attached to the bridged samples so their unoccluded edges can still be used.</summary>
        public Dictionary<int, FrameQuad> SetAside { get; } = new();

        /// <summary>
        /// Where this track's centroid is expected to be at <paramref name="frame"/>, carrying its
        /// last observed velocity forward. Matching against this rather than against the last
        /// accepted sample is what lets a track survive being occluded WHILE MOVING: every held
        /// frame leaves the last sample staler, and a plate that kept moving meanwhile would
        /// otherwise fall outside the centroid-jump gate and be adopted by a brand-new track. That
        /// is exactly how the first occlusion fix cut a handheld phone's single track in two.
        /// </summary>
        public (double X, double Y) PredictedCentroid(int frame)
        {
            FrameQuad last = Last;
            if (Samples.Count < 2)
                return (last.CentroidX, last.CentroidY);

            FrameQuad prev = Samples[^2];
            int span = Math.Max(1, last.FrameIndex - prev.FrameIndex);
            double vx = (last.CentroidX - prev.CentroidX) / span;
            double vy = (last.CentroidY - prev.CentroidY) / span;
            int ahead = Math.Max(0, frame - last.FrameIndex);
            return (last.CentroidX + vx * ahead, last.CentroidY + vy * ahead);
        }

        /// <summary>
        /// Median area over the track's recent samples. Median, and recent, so that a plate
        /// legitimately moving away shrinks the baseline with it while a single frame bitten into
        /// by a hand does not.
        /// </summary>
        public double RecentMedianCoreArea(int window)
        {
            int from = Math.Max(0, Samples.Count - window);
            return Median(Samples.Skip(from).Select(q => q.CoreAreaRatio).ToList());
        }

        /// <summary>
        /// A robust, scale-free measure of how much this plate's core area moves from frame to
        /// frame: the median absolute relative step over the recent window. Median absolute
        /// rather than standard deviation so that the very dips the caller is hunting for cannot
        /// inflate the yardstick used to find them.
        /// </summary>
        public double CoreAreaSpread(int window)
        {
            int from = Math.Max(1, Samples.Count - window);
            var steps = new List<double>();
            for (int i = from; i < Samples.Count; i++)
            {
                double previous = Samples[i - 1].CoreAreaRatio;
                if (previous > 0)
                    steps.Add(Math.Abs(Samples[i].CoreAreaRatio - previous) / previous);
            }

            return Median(steps);
        }

    }

    /// <summary>Samples a track needs before the occlusion test applies at all: with less history there is no baseline, and rejecting on a guess would stop tracks from ever starting.</summary>
    private const int OcclusionMinHistory = 4;

    /// <summary>
    /// How many recent samples the occlusion BASELINE is taken over. Deliberately short: the
    /// baseline is "how big was this plate a moment ago", and a long window lags behind a plate
    /// genuinely moving away from camera until the lag itself looks like a collapse — a steadily
    /// shrinking plate had its track cut short before this was narrowed. Three is enough to
    /// shrug off a single bad frame, which is all the baseline needs; the SPREAD that sets the
    /// threshold is estimated over a longer window, where more samples genuinely help.
    /// </summary>
    private const int OcclusionBaselineSamples = 3;

    /// <summary>How many recent samples the occlusion SPREAD is taken over — about a second at the default insert sample rate.</summary>
    private const int OcclusionSpreadWindow = 10;

    /// <summary>
    /// Associates per-frame detections into independent, possibly CONCURRENT tracks. A detection
    /// joins an open track when it is at most <see cref="Options.MaxGapFrames"/>+1 frames after
    /// that track's last sample AND its centroid did not jump implausibly far; bridged gap frames
    /// are filled by linear interpolation so downstream keyframes stay temporally dense. Tracks
    /// that go unmatched for longer than the gap window close; unmatched detections open new ones.
    ///
    /// <para>
    /// Matching is greedy over all (open track, detection) pairs in ascending centroid distance,
    /// with ties broken by track then detection index — a total order, so the assembly is
    /// bit-for-bit reproducible. With <see cref="Options.MaxPlatesPerFrame"/> at its default of 1
    /// there is at most one detection per frame and this reduces to the original single-run walk,
    /// with one deliberate improvement: a track stays open across the gap window, so a plate that
    /// jumps away and returns within it is re-associated instead of being split.
    /// </para>
    /// <para>
    /// <b>Occlusion is missing data, not a smaller plate.</b> When a hand crosses a screen the
    /// detector still succeeds — it just finds the REMAINING visible fragment — and fitting a quad
    /// to that fragment collapses the tracked geometry onto it, so the composited insert shrinks
    /// and snaps back as the hand passes. On the project's own laptop footage that left a wedge of
    /// raw plate exposed for the whole pass. A matched detection is therefore checked against the
    /// track's own recent median area, and one that has collapsed
    /// (<see cref="Options.OcclusionAreaDropRatio"/>) is DISCARDED rather than fitted: the track
    /// holds its geometry, stays open, and the frames are bridged by the same interpolation that
    /// already covers a detection dropout. Two separate expiries keep the two situations apart —
    /// <see cref="Options.MaxGapFrames"/> for a plate that has genuinely vanished, and the far
    /// more generous <see cref="Options.MaxOccludedFrames"/> for one that is still being seen
    /// every frame, just partially. See docs/video-editing.md "Surviving occlusion".
    /// </para>
    /// </summary>
    internal static List<List<FrameQuad>> AssembleTracks(
        IReadOnlyList<IReadOnlyList<FrameQuad>> perFrame, Options options, double frameScale = 1.0)
    {
        int occlusionMinHistory = ScaleFrames(OcclusionMinHistory, frameScale);
        int occlusionSpreadWindow = ScaleFrames(OcclusionSpreadWindow, frameScale);
        var open = new List<OpenTrack>();
        var finished = new List<List<FrameQuad>>();
        double sensitivity = Math.Max(0, options.OcclusionSensitivity);
        double minMargin = Math.Clamp(options.MinOcclusionDropMargin, 0, 1);
        double maxMargin = Math.Clamp(options.MaxOcclusionDropMargin, minMargin, 1);
        int maxOccluded = Math.Max(0, options.MaxOccludedFrames);

        for (int f = 0; f < perFrame.Count; f++)
        {
            // Retire tracks whose plate has vanished outright, and tracks that have been occluded
            // so long the smaller reading is more likely to be the truth. Checked before matching,
            // so an expired track can never capture this frame's detection.
            for (int i = open.Count - 1; i >= 0; i--)
            {
                bool vanished = f - open[i].LastSeenFrame - 1 > options.MaxGapFrames;
                bool occludedTooLong = f - open[i].LastAcceptedFrame - 1 > maxOccluded;
                if (vanished || occludedTooLong)
                {
                    finished.Add(open[i].Samples);
                    open.RemoveAt(i);
                }
            }

            IReadOnlyList<FrameQuad> detections = perFrame[f];
            if (detections.Count == 0)
                continue;

            var candidates = new List<(double Distance, int Track, int Detection)>();
            for (int ti = 0; ti < open.Count; ti++)
            {
                (double px, double py) = open[ti].PredictedCentroid(f);

                // The jump allowance grows with how long the track has gone unaccepted: a plate
                // held through several occluded frames has had that much longer to move, and
                // holding it to a single frame's allowance would strand it.
                int heldFor = Math.Max(0, f - open[ti].Last.FrameIndex);
                double allowedJump = options.MaxCentroidJump * (1 + heldFor);

                for (int di = 0; di < detections.Count; di++)
                {
                    FrameQuad d = detections[di];
                    double jump = Math.Sqrt(Math.Pow(d.CentroidX - px, 2) + Math.Pow(d.CentroidY - py, 2));
                    if (jump <= allowedJump)
                        candidates.Add((jump, ti, di));
                }
            }

            var trackTaken = new bool[open.Count];
            var detectionTaken = new bool[detections.Count];
            foreach ((double _, int ti, int di) in candidates
                .OrderBy(c => c.Distance)
                .ThenBy(c => c.Track)
                .ThenBy(c => c.Detection))
            {
                if (trackTaken[ti] || detectionTaken[di])
                    continue;

                trackTaken[ti] = true;
                detectionTaken[di] = true;

                OpenTrack track = open[ti];

                // Corner labels follow the PHYSICAL corners, not the image axes. The per-frame fit
                // names corners in the plate's own frame, whose orientation is only defined modulo
                // a quarter turn (ChromaQuadOrientation), so a plate turning through 45 degrees has
                // its "top-left" renamed mid-turn. Re-labelling each detection against the track's
                // previous sample keeps the content pinned to the same four corners across the
                // turn instead of snapping a quarter turn. A no-op for an upright plate.
                FrameQuad d = AlignCornerLabels(detections[di], track.Last);

                // The plate is still being seen this frame either way — that is what distinguishes
                // an occlusion from a disappearance, and what keeps the vanished-expiry at bay.
                track.LastSeenFrame = f;

                // Occlusion test, two independent symptoms. Area catches something covering a
                // large part of the plate. Corner SHIFT catches the case area alone misses and
                // that real footage actually produced: a hand biting one corner costs only ~15%
                // of the area, but drags that corner — and with it the whole fitted edge — a long
                // way, which is what exposed a wedge of raw plate as the hand passed.
                // Occlusion test. Measured against the STRICT CORE area, not the grown one: on
                // real footage the grown area is too noisy to threshold (see FrameQuad.CoreAreaRatio),
                // while the core moves smoothly with the plate and drops sharply the moment
                // anything covers it. Measured on the project's own clips: an unoccluded handheld
                // phone's core changes under 2% frame to frame, and a hand crossing the laptop's
                // screen takes its core to 0.81 of baseline.
                bool hasHistory = track.Samples.Count >= occlusionMinHistory;
                double baseline = track.RecentMedianCoreArea(OcclusionBaselineSamples);
                if (hasHistory && baseline > 0)
                {
                    // The threshold is this track's own measured steadiness, not a constant: a
                    // dip only counts as occlusion once it is several robust deviations outside
                    // what this plate's core area does from frame to frame anyway.
                    double margin = Math.Clamp(
                        sensitivity * track.CoreAreaSpread(occlusionSpreadWindow), minMargin, maxMargin);
                    if (d.CoreAreaRatio < baseline * (1 - margin))
                    {
                        // Occluded: hold the geometry rather than collapsing onto the fragment —
                        // but keep the detection, whose uncovered edges are still real.
                        track.SetAside[f] = d;
                        continue;
                    }
                }

                FrameQuad last = track.Last;
                int gap = d.FrameIndex - last.FrameIndex - 1;
                for (int g = 1; g <= gap; g++)
                {
                    FrameQuad bridged = Lerp(last, d, last.FrameIndex + g, g / (double)(gap + 1));
                    if (track.SetAside.TryGetValue(bridged.FrameIndex, out FrameQuad? aside))
                        bridged = bridged with { SetAside = aside };
                    track.Samples.Add(bridged);
                }

                track.SetAside.Clear();

                track.Samples.Add(d);
                track.LastAcceptedFrame = f;
            }

            for (int di = 0; di < detections.Count; di++)
            {
                if (!detectionTaken[di])
                {
                    open.Add(new OpenTrack
                    {
                        Samples = [detections[di]],
                        LastSeenFrame = f,
                        LastAcceptedFrame = f
                    });
                }
            }
        }

        finished.AddRange(open.Select(t => t.Samples));

        // Chronological by first sample, then by starting centroid — again a total order, so two
        // plates that appear on the same frame always get the same r{n} ids.
        return finished
            .OrderBy(t => t[0].FrameIndex)
            .ThenBy(t => t[0].CentroidX)
            .ThenBy(t => t[0].CentroidY)
            .ToList();
    }

    /// <summary>
    /// A fragment at either end of a track, cut off from the body by a bridged gap, is dropped when
    /// its median core area is below this fraction of the body's. See <see cref="TrimWeakEnds"/>.
    /// </summary>
    internal const double WeakEndAreaRatio = 0.5;

    /// <summary>
    /// Drops a weak FRAGMENT from either end of a track: the samples before the first bridged gap
    /// (or after the last), when what they detected is much smaller than the rest of the track —
    /// together with the bridge that joined them on.
    ///
    /// <para>
    /// The case that needed it: on the project's generated phone clip the phone is turned toward
    /// the camera through a flash of glare. Before the turn, two frames see a thin, dim sliver of
    /// the screen edge-on (~4% of its eventual area); during it the screen is a pale reflection
    /// with no green at all; after it the plate is clean. Gap bridging joined the sliver to the
    /// body, so the insert opened on the sliver's geometry and then INTERPOLATED across the glare
    /// while the phone rotated — a quad sliding visibly off the phone for a quarter of a second.
    /// Feature tracking was tried for those frames and loses the phone too (fast rotation, motion
    /// blur, a screen whose appearance flips from green to white). With no trustworthy geometry,
    /// the right composite is none: the insert starts on the first clean frame and the glare
    /// frames show the source as filmed.
    /// </para>
    /// <para>
    /// Only ends separated by a bridge are candidates, so a plate that genuinely starts small and
    /// grows — walking toward the camera — keeps every frame.
    /// </para>
    /// </summary>
    internal static List<FrameQuad> TrimWeakEnds(List<FrameQuad> run)
    {
        int first = run.FindIndex(q => q.Interpolated);
        if (first < 0)
            return run;
        int last = run.FindLastIndex(q => q.Interpolated);

        int start = 0, end = run.Count; // [start, end)
        double body = Median(run.Where(q => !q.Interpolated).Select(q => q.CoreAreaRatio).ToList());

        // Leading fragment: [0, first), then the bridge up to the next detected sample.
        double lead = Median(run.Take(first).Where(q => !q.Interpolated).Select(q => q.CoreAreaRatio).ToList());
        if (first > 0 && lead < WeakEndAreaRatio * body)
        {
            start = first;
            while (start < run.Count && run[start].Interpolated)
                start++;
        }

        // Trailing fragment: (last, end), and the bridge back to the previous detected sample.
        double tail = Median(run.Skip(last + 1).Where(q => !q.Interpolated).Select(q => q.CoreAreaRatio).ToList());
        if (last < run.Count - 1 && tail < WeakEndAreaRatio * body)
        {
            end = last + 1;
            while (end > start && run[end - 1].Interpolated)
                end--;
        }

        return start == 0 && end == run.Count ? run : run.GetRange(start, Math.Max(0, end - start));
    }

    /// <summary>Half-width (frames) of the window <see cref="TemporalConsistency"/> takes each corner's median over.</summary>
    private const int ConsistencyHalfWindow = 6;

    /// <summary>
    /// How far (as a fraction of the quad's own diagonal) a frame's corner may sit from the
    /// temporal median of its neighbours and still count as agreeing. 4% is several grid pixels on
    /// any plate worth inserting into — well above quantization jitter, well below a corner that
    /// has jumped onto the background.
    /// </summary>
    private const double ConsistencyTolerance = 0.04;

    /// <summary>
    /// Fraction of a run's frames whose four corners all lie within
    /// <see cref="ConsistencyTolerance"/> of the per-corner temporal median over a
    /// ±<see cref="ConsistencyHalfWindow"/> window. A physical plate moves continuously; a fit
    /// that flickers between the plate and something else does not, and this is the only
    /// confidence term that compares frames with each other at all. Runs too short to have a
    /// neighbourhood score 1.
    /// </summary>
    internal static double TemporalConsistency(IReadOnlyList<FrameQuad> run, int halfWindow = ConsistencyHalfWindow)
    {
        if (run.Count < 2 * halfWindow + 1)
            return 1.0;

        int agreeing = 0;
        var window = new List<double>(2 * halfWindow + 1);
        for (int i = 0; i < run.Count; i++)
        {
            int from = Math.Max(0, i - halfWindow);
            int to = Math.Min(run.Count - 1, i + halfWindow);

            double MedianOf(Func<FrameQuad, double> pick)
            {
                window.Clear();
                for (int j = from; j <= to; j++)
                    window.Add(pick(run[j]));
                return Median(window);
            }

            double mx0 = MedianOf(q => q.X0), my0 = MedianOf(q => q.Y0);
            double mx1 = MedianOf(q => q.X1), my1 = MedianOf(q => q.Y1);
            double mx2 = MedianOf(q => q.X2), my2 = MedianOf(q => q.Y2);
            double mx3 = MedianOf(q => q.X3), my3 = MedianOf(q => q.Y3);

            double diagonal = Math.Max(
                Math.Sqrt(Math.Pow(mx3 - mx0, 2) + Math.Pow(my3 - my0, 2)),
                Math.Sqrt(Math.Pow(mx2 - mx1, 2) + Math.Pow(my2 - my1, 2)));
            if (diagonal <= 1e-9)
                continue;

            FrameQuad q = run[i];
            double worst = Math.Max(
                Math.Max(Math.Sqrt(Math.Pow(q.X0 - mx0, 2) + Math.Pow(q.Y0 - my0, 2)),
                         Math.Sqrt(Math.Pow(q.X1 - mx1, 2) + Math.Pow(q.Y1 - my1, 2))),
                Math.Max(Math.Sqrt(Math.Pow(q.X2 - mx2, 2) + Math.Pow(q.Y2 - my2, 2)),
                         Math.Sqrt(Math.Pow(q.X3 - mx3, 2) + Math.Pow(q.Y3 - my3, 2))));

            if (worst <= ConsistencyTolerance * diagonal)
                agreeing++;
        }

        return agreeing / (double)run.Count;
    }

    /// <summary>A frame count tuned at <see cref="ReferenceSampleFps"/>, expressed at <paramref name="frameScale"/> times that rate.</summary>
    internal static int ScaleFrames(int frames, double frameScale) =>
        frames <= 0 ? frames : (int)Math.Round(frames * Math.Max(1.0, frameScale));

    /// <summary>Median of an unsorted list; 0 for an empty one. Used for the curvature summary, where a mean would be pulled by a single clipped frame.</summary>
    internal static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return 0;

        List<double> sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>How many frames of a clip are kept for the chroma-key grid search. A handful is plenty — the key describes the plate's colour, which barely moves over a shot.</summary>
    private const int KeySolveSampleTarget = 8;

    /// <summary>The persisted DTO for the chosen chroma key — <c>null</c> when key solving was off or nothing scored well enough to trust.</summary>
    internal static VideoInsertChromaKey? KeyDto(PlateChromaKey key, double plateDominance = 0) =>
        key.Score < PlateChromaKeySolver.MinUsableScore
            ? null
            : new VideoInsertChromaKey(
                key.R, key.G, key.B, key.Similarity, key.Blend, key.Score,
                plateDominance >= 1 ? (int)Math.Round(plateDominance) : null);

    /// <summary>The persisted DTO for the refined corners — <c>null</c> (omitted) whenever curvature was not measured, so the compile step falls back to the fitted corners.</summary>
    internal static VideoInsertSurfaceQuad? SurfaceDto(FrameQuad q) =>
        q.Surface == default
            ? null
            : new VideoInsertSurfaceQuad(
                Math.Round(q.Surface.X0, 6), Math.Round(q.Surface.Y0, 6),
                Math.Round(q.Surface.X1, 6), Math.Round(q.Surface.Y1, 6),
                Math.Round(q.Surface.X2, 6), Math.Round(q.Surface.Y2, 6),
                Math.Round(q.Surface.X3, 6), Math.Round(q.Surface.Y3, 6));

    /// <summary>The persisted DTO for one frame's measured bows — <c>null</c> (omitted from the artifact entirely) when the plate read as flat.</summary>
    internal static VideoInsertEdgeBows? ToDto(InsertPlateBows bows) =>
        bows.IsFlat
            ? null
            : new VideoInsertEdgeBows(
                Math.Round(bows.Top.Bow, 6), Math.Round(bows.Top.Skew, 6),
                Math.Round(bows.Right.Bow, 6), Math.Round(bows.Right.Skew, 6),
                Math.Round(bows.Bottom.Bow, 6), Math.Round(bows.Bottom.Skew, 6),
                Math.Round(bows.Left.Bow, 6), Math.Round(bows.Left.Skew, 6));

    private static FrameQuad Lerp(FrameQuad a, FrameQuad b, int frameIndex, double t) => new(
        frameIndex,
        a.X0 + (b.X0 - a.X0) * t, a.Y0 + (b.Y0 - a.Y0) * t,
        a.X1 + (b.X1 - a.X1) * t, a.Y1 + (b.Y1 - a.Y1) * t,
        a.X2 + (b.X2 - a.X2) * t, a.Y2 + (b.Y2 - a.Y2) * t,
        a.X3 + (b.X3 - a.X3) * t, a.Y3 + (b.Y3 - a.Y3) * t,
        a.AreaRatio + (b.AreaRatio - a.AreaRatio) * t,
        a.FillScore + (b.FillScore - a.FillScore) * t,
        a.ChromaMargin + (b.ChromaMargin - a.ChromaMargin) * t,
        a.Bows.Lerp(b.Bows, t),
        (a.Surface.X0 + (b.Surface.X0 - a.Surface.X0) * t, a.Surface.Y0 + (b.Surface.Y0 - a.Surface.Y0) * t,
         a.Surface.X1 + (b.Surface.X1 - a.Surface.X1) * t, a.Surface.Y1 + (b.Surface.Y1 - a.Surface.Y1) * t,
         a.Surface.X2 + (b.Surface.X2 - a.Surface.X2) * t, a.Surface.Y2 + (b.Surface.Y2 - a.Surface.Y2) * t,
         a.Surface.X3 + (b.Surface.X3 - a.Surface.X3) * t, a.Surface.Y3 + (b.Surface.Y3 - a.Surface.Y3) * t),
        EdgeContrast: a.EdgeContrast + (b.EdgeContrast - a.EdgeContrast) * t,
        Interpolated: true);

    /// <summary>
    /// Re-labels <paramref name="d"/>'s corners by the cyclic shift (TL→TR→BR→BL→TL) that brings
    /// them closest to <paramref name="reference"/>'s — the shift that is right for a rigid plate
    /// whose per-frame labelling flipped as it passed 45 degrees (see
    /// <see cref="ChromaQuadOrientation"/>). Only cyclic shifts are tried: a rigid motion
    /// preserves the corners' winding, so a reversal could only ever be a worse fit. The refined
    /// surface corners and the per-edge bows are shifted with them. Shift 0 returns the input
    /// unchanged, which is the case for every upright plate.
    /// </summary>
    internal static FrameQuad AlignCornerLabels(FrameQuad d, FrameQuad reference)
    {
        // Cycle order TL, TR, BR, BL — note FrameQuad stores BL in slot 2 and BR in slot 3.
        (double X, double Y)[] cycle = [(d.X0, d.Y0), (d.X1, d.Y1), (d.X3, d.Y3), (d.X2, d.Y2)];
        (double X, double Y)[] target = [(reference.X0, reference.Y0), (reference.X1, reference.Y1), (reference.X3, reference.Y3), (reference.X2, reference.Y2)];

        int bestShift = 0;
        double bestCost = double.MaxValue;
        for (int k = 0; k < 4; k++)
        {
            double cost = 0;
            for (int i = 0; i < 4; i++)
            {
                var p = cycle[(i + k) % 4];
                cost += Math.Sqrt(Math.Pow(p.X - target[i].X, 2) + Math.Pow(p.Y - target[i].Y, 2));
            }

            if (cost < bestCost - 1e-12)
            {
                bestCost = cost;
                bestShift = k;
            }
        }

        if (bestShift == 0)
            return d;

        (double X, double Y) C(int i) => cycle[(i + bestShift) % 4];
        (double X, double Y)[] surfaceCycle = [(d.Surface.X0, d.Surface.Y0), (d.Surface.X1, d.Surface.Y1), (d.Surface.X3, d.Surface.Y3), (d.Surface.X2, d.Surface.Y2)];
        (double X, double Y) S(int i) => surfaceCycle[(i + bestShift) % 4];

        // Edge i runs from cycle corner i to i+1 (top, right, bottom, left), so edges shift by the same k.
        InsertEdgeBow[] edges = [d.Bows.Top, d.Bows.Right, d.Bows.Bottom, d.Bows.Left];
        InsertEdgeBow E(int i) => edges[(i + bestShift) % 4];

        return d with
        {
            X0 = C(0).X,
            Y0 = C(0).Y,
            X1 = C(1).X,
            Y1 = C(1).Y,
            X3 = C(2).X,
            Y3 = C(2).Y,
            X2 = C(3).X,
            Y2 = C(3).Y,
            Surface = d.Surface == default
                ? default
                : (S(0).X, S(0).Y, S(1).X, S(1).Y, S(3).X, S(3).Y, S(2).X, S(2).Y),
            Bows = new InsertPlateBows(E(0), E(1), E(2), E(3))
        };
    }

    /// <summary>
    /// Applies ONE cyclic relabelling to every sample of a run so that the edge called "top" is,
    /// over the track as a whole, the most nearly horizontal of the four — the labelling a viewer
    /// would call upright for most of the shot. <see cref="AlignCornerLabels"/> keeps labels
    /// continuous frame to frame but inherits whichever labelling the track's FIRST detection
    /// happened to get, and when that frame sits near 45 degrees the choice is a coin toss: on a
    /// generated clip of a phone whipped up from a tilt into an upright pose, the first frame
    /// chose the sideways labelling and the content stayed sideways on an upright phone for the
    /// remaining four seconds. The median angle over the run is what makes the majority pose win;
    /// a plate that spends the shot turning from landscape to portrait gets whichever it held
    /// longer, and keeps it continuously through the turn either way.
    /// </summary>
    internal static List<FrameQuad> UprightTrackLabels(List<FrameQuad> run, int width, int height)
    {
        if (run.Count == 0)
            return run;

        int bestShift = 0;
        double bestTilt = double.MaxValue;
        var tilts = new List<double>(run.Count);
        for (int k = 0; k < 4; k++)
        {
            tilts.Clear();
            foreach (FrameQuad q in run)
            {
                (double X, double Y)[] cycle = [(q.X0 * width, q.Y0 * height), (q.X1 * width, q.Y1 * height), (q.X3 * width, q.Y3 * height), (q.X2 * width, q.Y2 * height)];
                var a = cycle[k % 4];
                var b = cycle[(k + 1) % 4];
                tilts.Add(Math.Abs(Math.Atan2(b.Y - a.Y, b.X - a.X)));
            }

            double tilt = Median(tilts);
            if (tilt < bestTilt - 1e-9)
            {
                bestTilt = tilt;
                bestShift = k;
            }
        }

        if (bestShift == 0)
            return run;

        // Shift every sample by the same k, via AlignCornerLabels against a rotated copy of itself
        // — the shift that maps corner k onto slot 0.
        var shifted = new List<FrameQuad>(run.Count);
        foreach (FrameQuad q in run)
        {
            (double X, double Y)[] cycle = [(q.X0, q.Y0), (q.X1, q.Y1), (q.X3, q.Y3), (q.X2, q.Y2)];
            FrameQuad reference = q with
            {
                X0 = cycle[bestShift].X,
                Y0 = cycle[bestShift].Y,
                X1 = cycle[(bestShift + 1) % 4].X,
                Y1 = cycle[(bestShift + 1) % 4].Y,
                X3 = cycle[(bestShift + 2) % 4].X,
                Y3 = cycle[(bestShift + 2) % 4].Y,
                X2 = cycle[(bestShift + 3) % 4].X,
                Y2 = cycle[(bestShift + 3) % 4].Y
            };
            shifted.Add(AlignCornerLabels(q, reference));
        }

        return shifted;
    }

    /// <summary>Half-width, in SECONDS, of the neighbourhood a detection is judged against in <see cref="RejectImplausibleSamples"/>: 125 ms either side, i.e. ±3 frames at 24 fps, never fewer than ±2 samples.</summary>
    internal const double OutlierHalfWindowSeconds = 0.125;

    /// <summary>
    /// Replaces detections that cannot be the plate with interpolation between the ones that can,
    /// and drops them outright at a track's ends. Returns the cleaned run plus the track's own
    /// measurement noise — the median two-sided line residual of the surviving detections against
    /// their neighbours (a diagnostic; <see cref="InsertTrackSmoother"/> measures its own noise).
    ///
    /// <para>
    /// <b>Why.</b> On the project's phone clip the per-frame corners were within ~1 px of a
    /// full-resolution reference on EVERY frame of the turn except three: two where a reflection
    /// streak crossed the top and left edges and the edge fit followed the streak (63 and 41 px
    /// off), and the first frame of the track, where half the screen was glare and the fit
    /// bounded only the green half. Smoothing cannot repair that; it spreads it — the 3-frame
    /// average put a frame whose own detection was 0.9 px off 27 px off, because its neighbour
    /// was the corrupted one. Interpolating ACROSS a corrupted frame from its clean neighbours
    /// is a few pixels off at worst on a fast turn; the corrupted frame itself was tens.
    /// </para>
    /// <para>
    /// <b>How.</b> Every detected (non-interpolated) sample is predicted from a least-squares
    /// line through its detected neighbours within <see cref="OutlierHalfWindowSeconds"/>, and
    /// its residual — the furthest any corner sits from that prediction — is compared with a
    /// tolerance that is the larger of <see cref="Options.OutlierFloorFraction"/> of the plate's
    /// own diagonal and <see cref="Options.OutlierSpeedFactor"/> times the plate's local speed
    /// (the median per-frame travel of its neighbours). Both arms are relative to the footage:
    /// the floor scales with the plate, the speed arm with its motion, so a fast whip and a
    /// locked-off shot are judged against their own behaviour. A sample with at least five neighbours is judged by the SMALLER of that
    /// line residual and its distance from the best continuous single-kink path through the neighbours (<see cref="HingeResidual"/>):
    /// a whip's start or stop is one velocity kink, which one straight line cannot follow, while a spike or a short excursion needs
    /// two kinks and stays an outlier. Rejection is greedy, worst ratio
    /// first, re-predicting after each removal — a gross outlier corrupts the predictions of its
    /// neighbours, so judging all frames against predictions that still contain it (one pass)
    /// flagged every frame of the turn and then none. Capped at
    /// <see cref="Options.MaxOutlierFraction"/> of the detections.
    /// </para>
    /// <para>Measured on the phone clip's turn (frames 26-45 against the bezel-based reference): mean corner error 4.46 → 1.47 px, frame-to-frame slip 7.0 → 2.7 px RMS, worst frame 35 → 9.5 px; the steady part unchanged (1.20 → 1.17 px). Rejects exactly the three frames above across a wide range of both knobs (floor 0.01-0.02, speed factor 1.5-2.0).</para>
    /// </summary>
    internal static (List<FrameQuad> Run, double Noise) RejectImplausibleSamples(
        List<FrameQuad> run, double fps, int width, int height, Options options)
    {
        double floor = Math.Max(0, options.OutlierFloorFraction);
        double speedFactor = Math.Max(0, options.OutlierSpeedFactor);
        if (run.Count < 4 || width <= 0 || height <= 0)
            return (run, 0);
        bool rejectionEnabled = floor > 0 || speedFactor > 0;

        int half = Math.Max(2, (int)Math.Round(OutlierHalfWindowSeconds * Math.Max(fps, 1)));
        var detected = new List<int>();
        for (int i = 0; i < run.Count; i++)
        {
            if (!run[i].Interpolated)
                detected.Add(i);
        }

        if (detected.Count < 4)
            return (run, 0);

        var rejected = new bool[run.Count];
        int maxRejections = (int)Math.Floor(Math.Clamp(options.MaxOutlierFraction, 0, 1) * detected.Count);
        var residuals = new double[run.Count];

        for (int pass = 0; ; pass++)
        {
            int worst = -1;
            double worstRatio = 1;
            foreach (int i in detected)
            {
                residuals[i] = 0;
                if (rejected[i])
                    continue;

                (double residual, double speed, List<int> neighbours) = ResidualAgainstNeighbours(run, detected, rejected, i, half, width, height);
                if (double.IsNaN(residual))
                    continue;

                // The reported noise stays the two-sided line residual's median; only the decision is made on the smaller of the
                // line and the best single-kink path. The hinge can only lower the judged value, so it is evaluated only when the
                // line residual is already over the tolerance.
                residuals[i] = residual;
                double tolerance = Math.Max(floor * Diagonal(run[i], width, height), speedFactor * speed);
                if (tolerance <= 0)
                    continue;

                double judged = residual;
                if (judged > tolerance && neighbours.Count >= HingeMinNeighbours)
                    judged = Math.Min(judged, HingeResidual(run, neighbours, i, width, height));

                double ratio = judged / tolerance;
                if (ratio > worstRatio)
                {
                    worstRatio = ratio;
                    worst = i;
                }
            }

            if (worst < 0 || pass >= maxRejections || !rejectionEnabled)
                break;
            rejected[worst] = true;
        }

        var accepted = detected.Where(i => !rejected[i]).ToList();
        if (accepted.Count == 0)
            return (run, 0);

        double noise = Median(accepted.Select(i => residuals[i]).ToList());
        if (accepted.Count == detected.Count)
            return (run, noise);

        // Rebuild: the span between the first and last accepted detection, every non-accepted
        // sample inside it interpolated between its enclosing accepted ones (an existing bridge
        // was linear already, so it reproduces itself), and anything outside it dropped.
        var cleaned = new List<FrameQuad>(run.Count);
        for (int a = 0; a + 1 < accepted.Count; a++)
        {
            int from = accepted[a], to = accepted[a + 1];
            cleaned.Add(run[from]);
            int span = run[to].FrameIndex - run[from].FrameIndex;
            for (int j = from + 1; j < to; j++)
                cleaned.Add(Lerp(run[from], run[to], run[j].FrameIndex, (run[j].FrameIndex - run[from].FrameIndex) / (double)span));
        }

        cleaned.Add(run[accepted[^1]]);
        return (cleaned, noise);
    }

    /// <summary>
    /// The furthest any corner of <c>run[i]</c> sits from a straight-line prediction through its
    /// accepted detected neighbours within ±<paramref name="half"/> frames, in grid pixels, with
    /// the neighbours' median per-frame travel (same units). NaN when fewer than three neighbours
    /// are available to predict from.
    /// </summary>
    private static (double Residual, double Speed, List<int> Neighbours) ResidualAgainstNeighbours(
        List<FrameQuad> run, List<int> detected, bool[] rejected, int i, int half, int width, int height)
    {
        int frame = run[i].FrameIndex;
        var idx = new List<int>();
        foreach (int j in detected)
        {
            if (j == i || rejected[j])
                continue;
            int dt = run[j].FrameIndex - frame;
            if (dt < -half || dt > half)
                continue;
            idx.Add(j);
        }

        if (idx.Count < 3)
            return (double.NaN, 0, idx);

        // Least squares line per coordinate; the intercept is the prediction at dt = 0.
        double n = idx.Count, st = 0, stt = 0;
        foreach (int j in idx)
        {
            double t = run[j].FrameIndex - frame;
            st += t;
            stt += t * t;
        }

        double det = n * stt - st * st;
        double residual = 0;
        Span<double> coords = stackalloc double[8];
        for (int c = 0; c < 8; c++)
        {
            double sy = 0, sty = 0;
            foreach (int j in idx)
            {
                double t = run[j].FrameIndex - frame;
                double y = Coordinate(run[j], c, width, height);
                sy += y;
                sty += t * y;
            }

            coords[c] = Math.Abs(det) < 1e-12 ? sy / n : (stt * sy - st * sty) / det;
        }

        for (int k = 0; k < 4; k++)
        {
            double dx = Coordinate(run[i], 2 * k, width, height) - coords[2 * k];
            double dy = Coordinate(run[i], 2 * k + 1, width, height) - coords[2 * k + 1];
            residual = Math.Max(residual, Math.Sqrt(dx * dx + dy * dy));
        }

        var steps = new List<double>(idx.Count);
        for (int k = 0; k + 1 < idx.Count; k++)
        {
            int a = idx[k], b = idx[k + 1];
            int dt = run[b].FrameIndex - run[a].FrameIndex;
            if (dt <= 0)
                continue;
            steps.Add(MaxCornerDistance(run[a], run[b], width, height) / dt);
        }

        return (residual, steps.Count == 0 ? 0 : Median(steps), idx);
    }

    /// <summary>Fewest accepted neighbours a sample needs before the single-kink path is allowed to explain it: two kink-free halves of at least two samples around the knot.</summary>
    private const int HingeMinNeighbours = 5;

    /// <summary>
    /// The furthest any corner of <c>run[i]</c> sits from the best CONTINUOUS TWO-SEGMENT (single-kink) path through its accepted
    /// neighbours <paramref name="idx"/> (the sample itself excluded), in grid pixels. Per coordinate x(t) = a + b t + c max(0, t - knot) is
    /// fitted by least squares for every candidate knot (the neighbours' own frames and the sample's frame); the knot with the smallest
    /// total squared residual over the eight coordinates wins, and the path is evaluated at the sample's frame (t = 0).
    /// <para>
    /// A whip's start or stop is exactly one velocity kink: one side of the sample still predicts it perfectly, which a single straight line
    /// through BOTH sides cannot. A spike or a short excursion (a reflection streak, an occluder) needs two or more kinks inside the window and
    /// stays far from every single-kink path, so it is still judged an outlier. <see cref="double.PositiveInfinity"/> when no knot is admissible.
    /// </para>
    /// </summary>
    private static double HingeResidual(List<FrameQuad> run, List<int> idx, int i, int width, int height)
    {
        int frame = run[i].FrameIndex;
        var knots = new SortedSet<double> { 0 };
        foreach (int j in idx)
            knots.Add(run[j].FrameIndex - frame);
        double minT = run[idx[0]].FrameIndex - frame, maxT = run[idx[^1]].FrameIndex - frame;
        double bestSse = double.PositiveInfinity;
        Span<double> bestPred = stackalloc double[8];
        Span<double> pred = stackalloc double[8];
        foreach (double knot in knots)
        {
            if (knot <= minT || knot >= maxT)
                continue;
            double s11 = 0, s12 = 0, s13 = 0, s22 = 0, s23 = 0, s33 = 0;
            foreach (int j in idx)
            {
                double t = run[j].FrameIndex - frame, h = Math.Max(0, t - knot);
                s11 += 1; s12 += t; s13 += h; s22 += t * t; s23 += t * h; s33 += h * h;
            }

            double det = s11 * (s22 * s33 - s23 * s23) - s12 * (s12 * s33 - s23 * s13) + s13 * (s12 * s23 - s22 * s13);
            if (Math.Abs(det) < 1e-9)
                continue;
            double sse = 0;
            for (int c = 0; c < 8; c++)
            {
                double b1 = 0, b2 = 0, b3 = 0;
                foreach (int j in idx)
                {
                    double t = run[j].FrameIndex - frame, h = Math.Max(0, t - knot), y = Coordinate(run[j], c, width, height);
                    b1 += y; b2 += t * y; b3 += h * y;
                }

                // Cramer's rule for (a, b, cc).
                double a = (b1 * (s22 * s33 - s23 * s23) - s12 * (b2 * s33 - s23 * b3) + s13 * (b2 * s23 - s22 * b3)) / det;
                double bb = (s11 * (b2 * s33 - s23 * b3) - b1 * (s12 * s33 - s23 * s13) + s13 * (s12 * b3 - b2 * s13)) / det;
                double cc = (s11 * (s22 * b3 - b2 * s23) - s12 * (s12 * b3 - b2 * s13) + b1 * (s12 * s23 - s22 * s13)) / det;
                foreach (int j in idx)
                {
                    double t = run[j].FrameIndex - frame, h = Math.Max(0, t - knot);
                    double r = Coordinate(run[j], c, width, height) - (a + bb * t + cc * h);
                    sse += r * r;
                }

                pred[c] = a + cc * Math.Max(0, -knot); // the path's value at t = 0
            }

            if (sse < bestSse)
            {
                bestSse = sse;
                pred.CopyTo(bestPred);
            }
        }

        if (double.IsPositiveInfinity(bestSse))
            return double.PositiveInfinity;
        double worst = 0;
        for (int k = 0; k < 4; k++)
        {
            double dx = Coordinate(run[i], 2 * k, width, height) - bestPred[2 * k];
            double dy = Coordinate(run[i], 2 * k + 1, width, height) - bestPred[2 * k + 1];
            worst = Math.Max(worst, Math.Sqrt(dx * dx + dy * dy));
        }

        return worst;
    }

    /// <summary>Coordinate <paramref name="c"/> (x0,y0,x1,y1,x2,y2,x3,y3) of a quad in grid pixels.</summary>
    private static double Coordinate(FrameQuad q, int c, int width, int height) => c switch
    {
        0 => q.X0 * width,
        1 => q.Y0 * height,
        2 => q.X1 * width,
        3 => q.Y1 * height,
        4 => q.X2 * width,
        5 => q.Y2 * height,
        6 => q.X3 * width,
        _ => q.Y3 * height
    };

    private static double MaxCornerDistance(FrameQuad a, FrameQuad b, int width, int height)
    {
        double worst = 0;
        for (int k = 0; k < 4; k++)
        {
            double dx = Coordinate(a, 2 * k, width, height) - Coordinate(b, 2 * k, width, height);
            double dy = Coordinate(a, 2 * k + 1, width, height) - Coordinate(b, 2 * k + 1, width, height);
            worst = Math.Max(worst, Math.Sqrt(dx * dx + dy * dy));
        }

        return worst;
    }

    /// <summary>The longer diagonal of a quad in grid pixels — the plate's own size, which every relative tolerance here is a fraction of.</summary>
    private static double Diagonal(FrameQuad q, int width, int height) => Math.Max(
        Math.Sqrt(Math.Pow((q.X3 - q.X0) * width, 2) + Math.Pow((q.Y3 - q.Y0) * height, 2)),
        Math.Sqrt(Math.Pow((q.X2 - q.X1) * width, 2) + Math.Pow((q.Y2 - q.Y1) * height, 2)));

    private static double QuadAspect(FrameQuad q, int width, int height)
    {
        double topW = Dist(q.X0, q.Y0, q.X1, q.Y1, width, height);
        double bottomW = Dist(q.X2, q.Y2, q.X3, q.Y3, width, height);
        double leftH = Dist(q.X0, q.Y0, q.X2, q.Y2, width, height);
        double rightH = Dist(q.X1, q.Y1, q.X3, q.Y3, width, height);
        double h = (leftH + rightH) / 2;
        return h > 1e-6 ? (topW + bottomW) / 2 / h : 1.0;

        static double Dist(double ax, double ay, double bx, double by, int w, int hh) =>
            Math.Sqrt(Math.Pow((bx - ax) * w, 2) + Math.Pow((by - ay) * hh, 2));
    }

    /// <summary>The motion word of a run: its centroids are already normalized. See <see cref="TrackMotion.Classify"/>.</summary>
    internal static string ClassifyMotion(List<FrameQuad> run, double fps) =>
        TrackMotion.Classify(run.Count, fps, i => (run[i].CentroidX, run[i].CentroidY));
}
