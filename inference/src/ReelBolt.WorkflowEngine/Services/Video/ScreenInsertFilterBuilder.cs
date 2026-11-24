using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// One tracked-quad corner keyframe in PIXEL space on the canonical encode frame, keyed by the
/// insert branch's own frame index (frame 0 = the insert's on-screen start). Every number here was
/// computed by deterministic C# (<c>ChromaQuadTracker</c> corners, mapped through
/// <c>OutputTimeline</c> and scaled by the probed frame size in
/// <c>VideoCompileStepExecutor.BuildInsertKeyframes</c>) — never model-supplied.
/// </summary>
public sealed record InsertQuadFrame(
    long FrameIndex,
    double X0, double Y0, double X1, double Y1, double X2, double Y2, double X3, double Y3);

/// <summary>
/// A fully-resolved tracked screen insert, ready to be rendered into ffmpeg filter syntax by
/// <see cref="ScreenInsertFilterBuilder"/>. The caller (<c>VideoCompileStepExecutor.ResolveInsertsAsync</c>)
/// has already validated the region id against <c>OfferedInsertRegionIds</c>, validated + downloaded
/// + probed the rendered asset to a local scratch file, mapped the track's window onto the output
/// timeline, and converted the tracked corners to pixel keyframes.
/// </summary>
public sealed record ResolvedScreenInsert(
    string RegionId,
    string LocalAssetPath,
    double OutputStartSec,
    double OutputEndSec,
    IReadOnlyList<InsertQuadFrame> Keyframes,
    int FpsNum,
    int FpsDen,
    /// <summary>
    /// Non-planar addition (docs/video-editing.md "Non-planar surfaces and multi-plate
    /// compositing"): when set, this insert's plate measured CURVED and is rendered as a tiling
    /// of <c>perspective</c> cells by <see cref="ScreenInsertMeshFilterBuilder"/> instead of one
    /// corner pin. <c>null</c> — the default and the only value on a flat plate — takes the
    /// original single-<c>perspective</c> recipe below, unchanged.
    /// </summary>
    InsertMeshPlan? Mesh = null,
    /// <summary>
    /// Silhouette matting (docs/video-editing.md "Silhouette matting"): when set, the insert's
    /// alpha is the warped quad/mesh mask AND a per-frame chroma matte keyed off the base picture
    /// with these parameters, so the composite is clipped to the plate's real shape — rounded
    /// bezel corners, a camera notch, a hand crossing the screen — rather than filling a
    /// quadrilateral over them. <c>null</c> composites exactly as before matting existed.
    /// </summary>
    VideoInsertChromaKey? Matte = null,
    /// <summary>
    /// S3/MinIO storage key for the rendered screen insert asset.
    /// </summary>
    string? AssetStorageKey = null,
    /// <summary>
    /// First-party filter chain applied to the content before it is warped — the aspect fit
    /// (<see cref="InsertContentFitFilter"/>). <c>null</c> warps the content's full frame exactly
    /// as before. Built from probed integers only; never contains anything model-authored.
    /// </summary>
    string? ContentPrefilter = null,
    /// <summary>
    /// The tracked plate's own corners on the same clock and canvas as <see cref="Keyframes"/>
    /// but WITHOUT overscan — not used by the encode, carried for the timeline manifest so the
    /// editor shows what was tracked rather than the expanded warp target. <c>null</c> falls back
    /// to <see cref="Keyframes"/>.
    /// </summary>
    IReadOnlyList<InsertQuadFrame>? PlateKeyframes = null,
    /// <summary>
    /// Carry the plate's reflections onto the insert: wherever the matte keeps the insert, the
    /// base picture with its plate colour removed is screen-blended over the content, so glare
    /// and streaks the source had on the glass are on the glass of the composite too. Needs
    /// <see cref="Matte"/>; ignored without one. <c>false</c> composites exactly as before. See
    /// docs/video-editing.md "Reflections".
    /// </summary>
    bool TransferReflections = false,
    /// <summary>
    /// Where the content's OWN frame is pinned, when that differs from <see cref="Keyframes"/>
    /// (the warp target, expanded by overscan): the tracked plate itself. The content is then
    /// warped onto these corners WITHOUT the 1px black border, and <c>perspective</c>'s edge
    /// clamping smears its outermost pixels across the overscan band, where the matte keeps them
    /// to the plate's silhouette. So the content's whole frame lands on the plate — nothing
    /// cropped off the edges — and the band between the tracked edge and the true plate edge
    /// shows the content's own edge colour instead of black or raw plate. <c>null</c> pins the
    /// content to <see cref="Keyframes"/> exactly as before. Flat inserts only. See
    /// docs/video-editing.md "Tracking quality on the customer clips".
    /// </summary>
    IReadOnlyList<InsertQuadFrame>? ContentKeyframes = null,
    /// <summary>
    /// A PLATE GUARD rather than an insert: the plate is visible here but nothing is being
    /// composited into it (the tracker could not trust these frames, the track fell under the
    /// confidence floor or past <c>MaxInserts</c>, or the window lies outside the insert's own).
    /// The "content" is this solid colour (an <c>0xRRGGBB</c> literal chosen by first-party code,
    /// supplied as an in-graph <c>lavfi</c> colour input), matted to the plate exactly like a real
    /// insert — a dark screen instead of raw green. See <c>InsertPlateGuardPlanner</c>.
    /// </summary>
    string? GuardFillColor = null,
    /// <summary>
    /// Local scratch path of this insert's content AS STAGED, present only when that content really
    /// carries an audio stream — the sound <c>MixInsertAudio</c> mixes in (docs/video-editing.md
    /// "Sound from an inserted asset"). Deliberately the staged file, never <see cref="LocalAssetPath"/>
    /// after insert-content dip bridging, whose re-encode is video only. <c>null</c> (the default, and
    /// every still image and plate guard) means there is no sound to mix.
    /// </summary>
    string? ContentAudioLocalPath = null,
    /// <summary>The content's own length in seconds, so its sound plays once at its length and is trimmed to the insert's window.</summary>
    double ContentAudioDurationSec = 0,
    /// <summary>The content file's own name, for the timeline manifest's sound item; null when unknown.</summary>
    string? ContentName = null,
    /// <summary>
    /// The size the content and the white mask are prescaled to before they are warped, measured by
    /// the caller over EVERY keyframe of the insert (before <c>MaxInsertExprKeyframes</c> thinned the
    /// series the filter graph carries), so a track whose largest quad falls between two kept
    /// keyframes is still never minified. <c>null</c> sizes the prescale from <see cref="Keyframes"/>
    /// (and <see cref="ContentKeyframes"/>) as given. See <see cref="ScreenInsertFilterBuilder.PrescaleSize"/>.
    /// </summary>
    InsertPrescale? Prescale = null,
    /// <summary>
    /// QA-P1-19 item 3: the near-black stretches REMOVED from this insert's content under
    /// <see cref="InsertContentDipMode.Cut"/> (<c>null</c> for <c>Freeze</c>/<c>Keep</c>, and whenever
    /// nothing was removed). The encode plays the shortened content, so an event declared at content
    /// second <c>t</c> actually lands at <c>t</c> minus every removed second before it — a sound cue
    /// anchored to it must follow. See <see cref="RenderEventAnchors.ResolveOutputSec"/>.
    /// </summary>
    IReadOnlyList<(double StartSec, double EndSec)>? RemovedContentDips = null);

/// <summary>
/// The size, in canvas pixels, that a tracked insert's content (<see cref="ContentWidth"/> x
/// <see cref="ContentHeight"/>) and its white mask (<see cref="MaskWidth"/> x <see cref="MaskHeight"/>)
/// are scaled to before <c>perspective</c> warps them. They differ only for a matted insert whose
/// content is pinned to the plate while the mask follows the (larger) overscanned quad.
/// <see cref="Bounds"/> is the union bounding box of every quad the insert ever occupies (the warp
/// target and, when the content is pinned to the plate, the plate itself), measured by the caller
/// over every keyframe before the series was thinned: <c>perspective</c> then runs on just that
/// region of the canvas instead of on a full frame (see <see cref="ScreenInsertFilterBuilder.WarpRegionFor"/>).
/// </summary>
public sealed record InsertPrescale(int MaskWidth, int MaskHeight, int ContentWidth, int ContentHeight, InsertBounds? Bounds = null);

/// <summary>An axis-aligned pixel box on the canvas: <c>[X0, X1) x [Y0, Y1)</c>.</summary>
public readonly record struct InsertBounds(int X0, int Y0, int X1, int Y1);

/// <summary>
/// Builds the content-shaping prefix for a screen insert so content of a different aspect than its
/// plate is not distorted by the corner pin, which maps the content's whole frame onto the plate.
/// The content is padded (<see cref="InsertContentFit.Contain"/>) or cropped
/// (<see cref="InsertContentFit.Cover"/>) to the plate's physical aspect; the warp then maps that
/// already-correctly-shaped frame on without distortion.
/// </summary>
public static class InsertContentFitFilter
{
    /// <summary>
    /// Pads content by <c>1 + overscan</c> in each dimension, centred, so that warping it onto a
    /// quad expanded by the same factor about its centroid lands the content's own frame back on
    /// the plate instead of cropping it.
    /// </summary>
    public static string OverscanPad(double overscan)
    {
        string k = FfmpegArgvFormat.Number(1 + Math.Clamp(overscan, 0, 1));
        return $"pad=trunc(iw*{k}/2)*2:trunc(ih*{k}/2)*2:(ow-iw)/2:(oh-ih)/2:black";
    }

    /// <summary>Joins two optional filter chains with a comma.</summary>
    public static string? Append(string? first, string? second) =>
        string.IsNullOrEmpty(first) ? second : string.IsNullOrEmpty(second) ? first : first + "," + second;

    /// <summary>Within this relative aspect difference, Auto leaves content alone.</summary>
    internal const double AutoStretchTolerance = 0.15;

    /// <summary>
    /// The prefilter for one insert, or <c>null</c> when the content should be warped whole (an
    /// explicit Stretch, Auto within tolerance, or unusable dimensions).
    /// </summary>
    /// <param name="authoredForPlate">
    /// The content was rendered for this plate (an agent render, prompted to match its aspect).
    /// <see cref="InsertContentFit.Auto"/> then always stretches — exactly the pre-fit behaviour —
    /// because any remaining mismatch was the author's choice. Only content nobody shaped for the
    /// plate, a user's own file, is contained when its aspect is off.
    /// </param>
    public static string? Build(InsertContentFit fit, int contentWidth, int contentHeight, double plateAspect, bool authoredForPlate = false)
    {
        if (contentWidth <= 0 || contentHeight <= 0 || plateAspect <= 0 || double.IsNaN(plateAspect) || double.IsInfinity(plateAspect))
            return null;

        double contentAspect = contentWidth / (double)contentHeight;
        InsertContentFit effective = fit switch
        {
            InsertContentFit.Auto => authoredForPlate || Math.Abs(contentAspect / plateAspect - 1) <= AutoStretchTolerance
                ? InsertContentFit.Stretch
                : InsertContentFit.Contain,
            _ => fit
        };

        if (effective == InsertContentFit.Stretch)
            return null;

        // Target box in the content's own pixel scale: same area order of magnitude, plate aspect,
        // even dimensions for the yuv formats downstream.
        int targetW, targetH;
        if (effective == InsertContentFit.Contain)
        {
            // Grow one side so the whole content fits.
            if (contentAspect > plateAspect)
            {
                targetW = contentWidth;
                targetH = (int)Math.Round(contentWidth / plateAspect);
            }
            else
            {
                targetH = contentHeight;
                targetW = (int)Math.Round(contentHeight * plateAspect);
            }

            targetW = Even(targetW);
            targetH = Even(targetH);
            return $"pad={FfmpegArgvFormat.Number(targetW)}:{FfmpegArgvFormat.Number(targetH)}:(ow-iw)/2:(oh-ih)/2:black";
        }

        // Cover: shrink one side so the content fills the plate.
        if (contentAspect > plateAspect)
        {
            targetH = contentHeight;
            targetW = (int)Math.Round(contentHeight * plateAspect);
        }
        else
        {
            targetW = contentWidth;
            targetH = (int)Math.Round(contentWidth / plateAspect);
        }

        targetW = Math.Max(2, Even(targetW));
        targetH = Math.Max(2, Even(targetH));
        return $"crop={FfmpegArgvFormat.Number(targetW)}:{FfmpegArgvFormat.Number(targetH)}:(iw-ow)/2:(ih-oh)/2";

        static int Even(int v) => Math.Max(2, v + (v & 1));
    }
}

/// <summary>
/// Builds the ffmpeg filter-chain fragment that corner-pins a rendered scene into a tracked,
/// deforming quadrilateral region of the compiled video (see docs/video-editing.md "Tracked screen
/// inserts (Phase 5)"). Pure — no I/O, no ffmpeg. The recipe, per insert (validated end-to-end
/// against a real ffmpeg run during design):
///
/// <code>
/// [N:v] scale=cwxch (the largest quad of the insert + 1px), fps=FPS, pad to the warp window RWxRH (centred),
///       fillborders smear (the edge pixels fill the pad, no black rim),
///       perspective sense=destination eval=frame interpolation=cubic with per-frame corner
///       expressions, setpts (+outputStart delay)                        -> warped content
/// color=white cwxch, pad to RWxRH with black, format=gray,
///       the SAME corner expressions (bilinear), the same setpts delay     -> warped mask
/// alphamerge(content, mask)                                              -> content w/ alpha
/// overlay onto the base at the window's origin OX:OY, enable='between(t,start,end)'
/// </code>
///
/// <c>perspective</c> runs only on the window of the canvas that holds the union of the insert's quads over
/// every keyframe (<see cref="PlanPrescale"/>), at an even integer origin, and the warped window is overlaid
/// at that origin (a matted insert's whole-frame matte is cropped to the same window first); a window that
/// covers the canvas is the full-frame recipe exactly.
///
/// The prescale is the point of this recipe (insert-tracking-smoothness, F3). Scaling content
/// and mask to the whole canvas and letting <c>perspective</c> point-sample them ~2:1 into the plate
/// aliased the mask edge, and multiplying the content's 1px black border into the alpha edge made
/// the edge a nonlinear function of the sub-pixel position; both read as the insert wobbling on its
/// plate. Content and mask are now scaled to the size the quad actually occupies, pinned through the
/// quad's own projective map (<see cref="PinThroughRegion"/>), and the content edge is smeared
/// rather than blackened. The all-white mask is still padded with black: outside the warped quad it
/// must be zero, which <c>alphamerge</c> turns into full transparency (<c>perspective</c> clamps
/// out-of-range source coordinates to the edge pixel, so an unpadded mask would smear white across
/// the frame). A quad whose projective map has its horizon inside the canvas extrapolation keeps the
/// original full-canvas recipe (scale to (W-2)x(H-2), 1px black border), see
/// <see cref="AppendLegacyBorderedBranches"/>. <c>perspective</c> has no alpha-capable pixel format of
/// its own, which is why the mask branch exists at all.
///
/// Corner coordinates are piecewise-linear expressions in <c>perspective</c>'s per-frame <c>in</c>
/// variable (input frame number — the filter sits BEFORE <c>setpts</c>, so frame 0 is the insert's
/// own first frame), built from keyframes by <see cref="BuildPiecewiseExpr"/>. Every number is
/// formatted via <see cref="FfmpegArgvFormat"/> — culture-invariant, same as every other filter
/// builder in this feature. Not one model-originated character reaches this string: the model
/// contributed only an offered region id, and every number below came from
/// <c>ChromaQuadTracker</c>/<c>OutputTimeline</c>/ffprobe.
/// </summary>
public static class ScreenInsertFilterBuilder
{
    /// <summary>The content prefilter followed by a comma, or nothing — see <see cref="ResolvedScreenInsert.ContentPrefilter"/>.</summary>
    internal static string Prefix(string? prefilter) =>
        string.IsNullOrEmpty(prefilter) ? string.Empty : prefilter + ",";

    /// <summary>Extra seconds of synthesized mask beyond the on-screen window, so the mask never hits EOF before the window ends (the content branch may — framesync then repeats its last frame, which for screen content is the right behavior: better a held UI frame than the plate showing through).</summary>
    private const double MaskDurationMarginSec = 1.0;

    /// <summary>
    /// Builds the filter-chain fragment for <paramref name="inserts"/> (must be non-empty — the
    /// caller keeps the existing label plumbing untouched when there is nothing to composite).
    /// Starts from <paramref name="baseFilterChainEndLabel"/> and ends at
    /// <paramref name="finalLabel"/>. <paramref name="inputIndexForIndex"/> maps insert index to
    /// the ffmpeg input index its asset was added at (the caller adds one extra <c>-i</c> per
    /// insert, after every asset-overlay input and before the music input).
    /// </summary>
    public static string BuildFilterChain(
        string baseFilterChainEndLabel,
        IReadOnlyList<ResolvedScreenInsert> inserts,
        int frameWidth,
        int frameHeight,
        Func<int, int> inputIndexForIndex,
        string finalLabel = "[vout]")
    {
        if (inserts.Count == 0)
            throw new ArgumentException("BuildFilterChain requires at least one insert.", nameof(inserts));

        int innerW = Math.Max(2, frameWidth - 2);
        int innerH = Math.Max(2, frameHeight - 2);

        var segments = new List<string>();
        string currentLabel = baseFilterChainEndLabel;

        // Matting needs a copy of the base picture to key, and the base is a single labelled
        // stream — so it is split once, up front, into the compositing branch plus one branch per
        // matted insert. Done here rather than per insert because a label can only be consumed
        // once, and because a plate's key is its own: two plates in one shot are keyed separately.
        int matteCount = inserts.Count(x => x.Matte is not null);
        int sourceCount = inserts.Sum(x => x.Matte is null ? 0 : x.TransferReflections ? 3 : 2);
        var matteSources = new Queue<string>();
        if (matteCount > 0)
        {
            // Two extra branches per matted insert: one to key the matte from, one to despill —
            // and a third when its reflections are carried over, to build the reflection layer.
            currentLabel = "[mtbase]";
            segments.Add(
                $"{baseFilterChainEndLabel}format=rgb24,split={FfmpegArgvFormat.Number(sourceCount + 1)}[mtbase]" +
                string.Concat(Enumerable.Range(0, sourceCount).Select(k => $"[mtsrc{k}]")));
            for (int k = 0; k < sourceCount; k++)
                matteSources.Enqueue($"[mtsrc{k}]");
        }

        for (int i = 0; i < inserts.Count; i++)
        {
            ResolvedScreenInsert insert = inserts[i];
            bool isLast = i == inserts.Count - 1;
            int inputIndex = inputIndexForIndex(i);
            string insertOutLabel = isLast ? finalLabel : $"[si{i}]";

            // A curved plate hands this insert off to the mesh builder, which emits its own
            // cells and ends at the same label the flat branch would have — so the two flavours
            // interleave freely within one chain and every caller stays unaware of the split.
            string? matteLabel = null;
            string? reflectionLabel = null;
            string? reflectionMaskLabel = null;
            string compositeLabel = insertOutLabel;
            if (insert.Matte is not null)
            {
                // The matte is wanted twice — once to bound spill suppression on the base, once
                // as the insert's own alpha — and a filtergraph label may be consumed exactly
                // once, so it is split immediately rather than referenced twice. A third copy,
                // with a second footprint, bounds the reflection transfer.
                bool reflect = insert.TransferReflections;
                string matte = AppendPlateMatte(segments, matteSources.Dequeue(), insert.Matte, $"mt{i}");
                segments.Add(reflect
                    ? $"{matte}split=3[mtd{i}][mta{i}][mtr{i}]"
                    : $"{matte}split=2[mtd{i}][mta{i}]");
                string footprint = AppendInsertFootprint(segments, insert, frameWidth, frameHeight, $"fp{i}");
                if (reflect)
                {
                    segments.Add($"{footprint}split=2[fp{i}s][fp{i}r]");
                    footprint = $"[fp{i}s]";
                }

                currentLabel = AppendSpillSuppression(
                    segments, currentLabel, matteSources.Dequeue(), $"[mtd{i}]", footprint, $"ds{i}");
                matteLabel = $"[mta{i}]";

                if (reflect)
                {
                    reflectionLabel = AppendReflectionLayer(segments, matteSources.Dequeue(), $"rl{i}");
                    segments.Add($"[mtr{i}][fp{i}r]blend=all_mode=multiply[rm{i}]");
                    reflectionMaskLabel = $"[rm{i}]";
                    compositeLabel = $"[sic{i}o]";
                }
            }

            if (insert.Mesh is { } mesh)
            {
                ScreenInsertMeshFilterBuilder.AppendInsert(
                    segments, currentLabel, insert, mesh, i, inputIndex, compositeLabel, matteLabel);
                AppendReflectionTransfer(segments, compositeLabel, reflectionLabel, reflectionMaskLabel, insertOutLabel, i);
                currentLabel = insertOutLabel;
                continue;
            }

            string fps = $"{FfmpegArgvFormat.Number(insert.FpsNum)}/{FfmpegArgvFormat.Number(insert.FpsDen)}";
            string start = FfmpegArgvFormat.Number(insert.OutputStartSec);
            string end = FfmpegArgvFormat.Number(insert.OutputEndSec);
            double maskDuration = Math.Max(0.1, insert.OutputEndSec - insert.OutputStartSec) + MaskDurationMarginSec;

            string delay = DelayToOutputStart(insert);
            string inputLabel = $"[{inputIndex}:v]{Prefix(insert.ContentPrefilter)}";

            // Content and mask are scaled to the largest quad the insert ever occupies and pinned to
            // that region (PlanPrescale), so `perspective` runs near 1:1 instead of point-sampling a
            // full-canvas picture ~2:1 into the plate, and the content's edge is smeared rather than
            // multiplied by a black rim. `perspective` itself runs only on the union bounding box of
            // the insert's quads (plan.Warp), in region coordinates, and the result is overlaid at that
            // region's integer origin, so the per-pixel sampling work scales with the region and not
            // with the frame. A quad whose canvas extrapolation is not well-defined (strong
            // foreshortening) keeps the original full-canvas recipe below.
            int overlayX = 0, overlayY = 0;
            PrescaleRegion? cropMatteTo = null;
            if (PlanPrescale(insert, frameWidth, frameHeight) is { } plan)
            {
                string cw = FfmpegArgvFormat.Number(plan.Content.Width), ch = FfmpegArgvFormat.Number(plan.Content.Height);
                string cx = FfmpegArgvFormat.Number(plan.Content.OffsetX), cy = FfmpegArgvFormat.Number(plan.Content.OffsetY);
                string regionSize = $"{FfmpegArgvFormat.Number(plan.Warp.Width)}:{FfmpegArgvFormat.Number(plan.Warp.Height)}";
                segments.Add(
                    $"{inputLabel}scale={cw}:{ch}:flags=bilinear," +
                    $"fps={fps},format=yuv444p,pad={regionSize}:{cx}:{cy}:black," +
                    $"{SmearBorders(plan.Content, plan.Warp.Width, plan.Warp.Height)}," +
                    $"{BuildPerspectiveFilter(plan.ContentKeys, cubic: true)},{delay}[sic{i}]");

                segments.Add(
                    $"color=c=white:size={FfmpegArgvFormat.Number(plan.Mask.Width)}x{FfmpegArgvFormat.Number(plan.Mask.Height)}:rate={fps}:duration={FfmpegArgvFormat.Number(maskDuration)}," +
                    $"format=gray,pad={regionSize}:{FfmpegArgvFormat.Number(plan.Mask.OffsetX)}:{FfmpegArgvFormat.Number(plan.Mask.OffsetY)}:black," +
                    $"{BuildPerspectiveFilter(plan.MaskKeys)},{delay}[sim{i}]");

                overlayX = plan.Warp.OffsetX;
                overlayY = plan.Warp.OffsetY;
                if (plan.Warp.Width != frameWidth || plan.Warp.Height != frameHeight)
                    cropMatteTo = plan.Warp;
            }
            else
            {
                AppendLegacyBorderedBranches(segments, insert, i, inputLabel, fps, maskDuration, delay, frameWidth, frameHeight, innerW, innerH);
            }

            // The plate matte multiplies into the quad mask, so content survives only where the
            // warp puts it AND the base pixel is actually plate. That conjunction is the whole
            // mechanism: the quad mask keeps foliage elsewhere in frame out, the plate matte
            // keeps the bezel, the notch and any occluding hand in. The mask now covers only the
            // warp region, so the whole-frame matte is cropped to the same window first (exactly
            // as each mesh cell crops its own).
            string maskLabel = $"[sim{i}]";
            if (matteLabel is not null)
            {
                string matteForMask = matteLabel;
                if (cropMatteTo is { } window)
                {
                    segments.Add(
                        $"{matteLabel}crop=w={FfmpegArgvFormat.Number(window.Width)}:h={FfmpegArgvFormat.Number(window.Height)}:" +
                        $"x={FfmpegArgvFormat.Number(window.OffsetX)}:y={FfmpegArgvFormat.Number(window.OffsetY)}:exact=1[mtw{i}]");
                    matteForMask = $"[mtw{i}]";
                }

                segments.Add($"{maskLabel}{matteForMask}blend=all_mode=multiply[simm{i}]");
                maskLabel = $"[simm{i}]";
            }

            segments.Add($"[sic{i}]{maskLabel}alphamerge[sia{i}]");

            segments.Add(
                $"{currentLabel}[sia{i}]overlay=x={FfmpegArgvFormat.Number(overlayX)}:y={FfmpegArgvFormat.Number(overlayY)}:eof_action=pass:enable='between(t,{start},{end})'{compositeLabel}");
            AppendReflectionTransfer(segments, compositeLabel, reflectionLabel, reflectionMaskLabel, insertOutLabel, i);

            currentLabel = insertOutLabel;
        }

        return string.Join(";", segments);
    }

    /// <summary>
    /// The original full-canvas recipe: content and mask scaled to the canvas's inner size, a 1px
    /// black border, warped. Kept for a quad whose prescale cannot be pinned (see
    /// <see cref="PlanPrescale"/>), so such an insert renders exactly as it did before prescaling.
    /// </summary>
    private static void AppendLegacyBorderedBranches(
        List<string> segments, ResolvedScreenInsert insert, int i, string inputLabel, string fps, double maskDuration,
        string delay, int frameWidth, int frameHeight, int innerW, int innerH)
    {
        string perspective = BuildPerspectiveFilter(insert.Keyframes);
        if (insert.ContentKeyframes is { Count: > 0 } contentKeys)
        {
            // Smeared overscan: the content's full frame onto the plate itself, no border, so
            // perspective's edge clamp extends its edge pixels outward under the matte.
            segments.Add(
                $"{inputLabel}scale={FfmpegArgvFormat.Number(frameWidth)}:{FfmpegArgvFormat.Number(frameHeight)}:flags=bilinear," +
                $"fps={fps},format=yuv444p," +
                $"{BuildPerspectiveFilter(contentKeys)},{delay}[sic{i}]");
        }
        else
        {
            segments.Add(
                $"{inputLabel}scale={FfmpegArgvFormat.Number(innerW)}:{FfmpegArgvFormat.Number(innerH)}:flags=bilinear," +
                $"fps={fps},format=yuv444p,pad={FfmpegArgvFormat.Number(frameWidth)}:{FfmpegArgvFormat.Number(frameHeight)}:1:1:black," +
                $"{perspective},{delay}[sic{i}]");
        }

        segments.Add(
            $"color=c=white:size={FfmpegArgvFormat.Number(innerW)}x{FfmpegArgvFormat.Number(innerH)}:rate={fps}:duration={FfmpegArgvFormat.Number(maskDuration)}," +
            $"format=gray,pad={FfmpegArgvFormat.Number(frameWidth)}:{FfmpegArgvFormat.Number(frameHeight)}:1:1:black," +
            $"{perspective},{delay}[sim{i}]");
    }

    /// <summary>The rectangle of the canvas that holds the prescaled content (or mask) before it is warped.</summary>
    internal readonly record struct PrescaleRegion(int Width, int Height, int OffsetX, int OffsetY);

    /// <summary>
    /// One insert's prescale. <see cref="Warp"/> is the canvas window <c>perspective</c> runs on
    /// (its <c>OffsetX/Y</c> are the window's integer origin on the canvas); <see cref="Content"/> and
    /// <see cref="Mask"/> sit inside that window (offsets relative to it); the keyframes pin each of
    /// them, in window coordinates, to the tracked quad.
    /// </summary>
    internal sealed record PrescalePlan(
        PrescaleRegion Warp, PrescaleRegion Content, PrescaleRegion Mask,
        IReadOnlyList<InsertQuadFrame> ContentKeys, IReadOnlyList<InsertQuadFrame> MaskKeys);

    /// <summary>Smallest prescale edge worth building, so a tiny plate still has a mask edge to resolve.</summary>
    internal const int MinPrescalePx = 8;

    /// <summary>
    /// How near the horizon of a quad's projective map the extrapolated canvas corners may fall
    /// before the prescale is refused (the map's denominator <c>w</c> at the unit square's corners
    /// is 1, and it must stay positive and not collapse out to the canvas corners).
    /// </summary>
    private const double MinProjectiveDenominator = 0.05;

    /// <summary>
    /// The size content and mask are prescaled to: the largest quad edge over every keyframe, plus
    /// one pixel and rounded down to even (so never below the edge itself), clamped to the canvas's inner size (the canvas minus the
    /// 1px black border the mask needs, since `perspective` clamps coordinates to the edge pixel and
    /// an unbordered mask would smear white across the frame). Never larger than the canvas, so a
    /// huge quad is no worse than before and a tiny one is never inflated past
    /// <see cref="MinPrescalePx"/> + 1.
    /// </summary>
    internal static (int W, int H) PrescaleSize(IReadOnlyList<InsertQuadFrame> keys, int canvasW, int canvasH)
    {
        int innerW = Math.Max(2, canvasW - 2);
        int innerH = Math.Max(2, canvasH - 2);
        if (keys.Count == 0)
            return (innerW, innerH);

        double maxW = 0, maxH = 0;
        foreach (InsertQuadFrame k in keys)
        {
            maxW = Math.Max(maxW, Math.Max(Dist(k.X0, k.Y0, k.X1, k.Y1), Dist(k.X2, k.Y2, k.X3, k.Y3)));
            maxH = Math.Max(maxH, Math.Max(Dist(k.X0, k.Y0, k.X2, k.Y2), Dist(k.X1, k.Y1, k.X3, k.Y3)));
        }

        return (PrescaleEdge(maxW, innerW), PrescaleEdge(maxH, innerH));

        static double Dist(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

        static int PrescaleEdge(double extent, int inner)
        {
            if (!double.IsFinite(extent))
                return inner;
            // Whole pixels covering the extent, plus one, rounded DOWN to even: never below the extent
            // itself (ceil, or ceil + 1 when that is odd), so the warp is never asked to minify.
            double edge = Math.Ceiling(extent) + 1;
            int even = edge >= inner ? inner : (int)edge & ~1;
            return Math.Clamp(even, Math.Min(MinPrescalePx, inner), inner);
        }
    }

    /// <summary>The canvas rectangle a prescaled picture of <paramref name="size"/> is centred in.</summary>
    internal static PrescaleRegion RegionFor((int W, int H) size, int canvasW, int canvasH)
    {
        int w = Math.Clamp(size.W, 2, Math.Max(2, canvasW - 2));
        int h = Math.Clamp(size.H, 2, Math.Max(2, canvasH - 2));
        return new PrescaleRegion(w, h, (canvasW - w) / 2, (canvasH - h) / 2);
    }

    /// <summary>
    /// Re-expresses each keyframe — where the tracked quad is — as where the four CANVAS corners must
    /// land so that the prescaled <paramref name="region"/> of the canvas lands on that quad
    /// (`perspective sense=destination` sends the input frame's corners to the given points). It is
    /// the quad's own projective map extrapolated out to the canvas corners, exactly as
    /// <see cref="ScreenInsertMeshFilterBuilder.ExtrapolateToCanvas"/> does for a mesh cell. Returns
    /// <c>null</c> when any keyframe is degenerate or its map's horizon falls between the region and
    /// a canvas corner (the extrapolation would fold over), so the caller keeps the original recipe.
    /// </summary>
    internal static IReadOnlyList<InsertQuadFrame>? PinThroughRegion(
        IReadOnlyList<InsertQuadFrame> keys, PrescaleRegion region, int canvasW, int canvasH)
    {
        var result = new List<InsertQuadFrame>(keys.Count);
        double x0 = region.OffsetX, y0 = region.OffsetY;
        foreach (InsertQuadFrame k in keys)
        {
            InsertSurfaceMesh.UnitSquareHomography? h = InsertSurfaceMesh.UnitSquareHomography.Solve((k.X0, k.Y0), (k.X1, k.Y1), (k.X2, k.Y2), (k.X3, k.Y3));
            if (h is null)
                return null;

            (double G, double H)? g = HorizonGradient(k);
            if (g is null)
                return null;

            (double X, double Y)[] corners = new (double, double)[4];
            int n = 0;
            foreach ((double fx, double fy) in new[] { (0.0, 0.0), (canvasW, 0.0), (0.0, canvasH), ((double)canvasW, (double)canvasH) })
            {
                double u = (fx - x0) / region.Width, v = (fy - y0) / region.Height;
                if (1 + g.Value.G * u + g.Value.H * v < MinProjectiveDenominator)
                    return null;
                corners[n++] = h.Map(u, v);
            }

            if (corners.Any(c => !double.IsFinite(c.X) || !double.IsFinite(c.Y)))
                return null;
            result.Add(new InsertQuadFrame(k.FrameIndex, corners[0].X, corners[0].Y, corners[1].X, corners[1].Y, corners[2].X, corners[2].Y, corners[3].X, corners[3].Y));
        }

        return result;
    }

    /// <summary>
    /// <c>(g, h)</c> of the unit-square-to-quad homography's denominator <c>w(u,v) = 1 + g u + h v</c>
    /// (Heckbert's closed form), or <c>null</c> for a degenerate quad.
    /// </summary>
    private static (double G, double H)? HorizonGradient(InsertQuadFrame k)
    {
        // Heckbert's order is TL, TR, BR, BL; InsertQuadFrame is TL, TR, BL, BR.
        double dx1 = k.X1 - k.X3, dx2 = k.X2 - k.X3, dx3 = k.X0 - k.X1 + k.X3 - k.X2;
        double dy1 = k.Y1 - k.Y3, dy2 = k.Y2 - k.Y3, dy3 = k.Y0 - k.Y1 + k.Y3 - k.Y2;
        double det = dx1 * dy2 - dy1 * dx2;
        if (Math.Abs(det) < 1e-12)
            return null;
        return ((dx3 * dy2 - dy3 * dx2) / det, (dx1 * dy3 - dy1 * dx3) / det);
    }

    /// <summary>Pixels kept between the union of the quads and the edge of the warp window: the cubic kernel's reach (2) plus the mask's black border and some slack.</summary>
    internal const int WarpMarginPx = 4;

    /// <summary>Content needs this many pixels of smeared border on every side inside the warp window, so the cubic kernel never reads past the window's edge.</summary>
    private const int ContentMarginPx = 2;

    /// <summary>The mask needs this many pixels of black border on every side inside the warp window (<c>perspective</c> clamps coordinates to the edge pixel).</summary>
    private const int MaskMarginPx = 1;

    /// <summary>
    /// The integer pixel box that contains every quad in <paramref name="series"/> (all their
    /// keyframes, before the series is thinned), or <c>null</c> when a corner is not finite.
    /// </summary>
    public static InsertBounds? QuadBounds(params IReadOnlyList<InsertQuadFrame>?[] series)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (IReadOnlyList<InsertQuadFrame>? keys in series)
        {
            if (keys is null)
                continue;
            foreach (InsertQuadFrame k in keys)
            {
                foreach (double v in new[] { k.X0, k.X1, k.X2, k.X3 })
                {
                    if (!double.IsFinite(v))
                        return null;
                    minX = Math.Min(minX, v);
                    maxX = Math.Max(maxX, v);
                }

                foreach (double v in new[] { k.Y0, k.Y1, k.Y2, k.Y3 })
                {
                    if (!double.IsFinite(v))
                        return null;
                    minY = Math.Min(minY, v);
                    maxY = Math.Max(maxY, v);
                }
            }
        }

        if (minX > maxX || minY > maxY)
            return null;
        const double Limit = 1e7;
        return new InsertBounds(
            (int)Math.Floor(Math.Clamp(minX, -Limit, Limit)), (int)Math.Floor(Math.Clamp(minY, -Limit, Limit)),
            (int)Math.Ceiling(Math.Clamp(maxX, -Limit, Limit)), (int)Math.Ceiling(Math.Clamp(maxY, -Limit, Limit)));
    }

    /// <summary>
    /// The window of the canvas <c>perspective</c> runs on: the union bounding box of the insert's
    /// quads grown by <see cref="WarpMarginPx"/>, at least <paramref name="needW"/> x
    /// <paramref name="needH"/> (the prescaled pictures and their borders), clamped to the canvas, and
    /// placed on an EVEN integer origin — an even origin keeps the 4:2:0 chroma grid of the overlay
    /// aligned with the base picture's, exactly as an overlay at 0:0 is. The origin is an integer
    /// so the corner keyframes shift by whole pixels and sub-pixel positions stay exact. Returns the
    /// full canvas when the bounds are unknown or cover most of it anyway.
    /// </summary>
    internal static PrescaleRegion WarpRegionFor(InsertBounds? bounds, int needW, int needH, int canvasW, int canvasH)
    {
        if (bounds is not { } b)
            return new PrescaleRegion(canvasW, canvasH, 0, 0);

        int x0 = Math.Max(0, b.X0 - WarpMarginPx), y0 = Math.Max(0, b.Y0 - WarpMarginPx);
        int x1 = Math.Min(canvasW, b.X1 + WarpMarginPx), y1 = Math.Min(canvasH, b.Y1 + WarpMarginPx);
        if (x1 <= x0 || y1 <= y0)
            return new PrescaleRegion(canvasW, canvasH, 0, 0);

        int w0 = Math.Min(canvasW, Math.Max(x1 - x0, needW));
        int h0 = Math.Min(canvasH, Math.Max(y1 - y0, needH));
        int ox = Math.Clamp((x0 + x1) / 2 - w0 / 2, 0, canvasW - w0) & ~1;
        int oy = Math.Clamp((y0 + y1) / 2 - h0 / 2, 0, canvasH - h0) & ~1;
        // Snapping the origin down to even may have shaved a pixel off the far side: widen to cover it.
        int w = Math.Min(canvasW - ox, Even(Math.Max(Math.Max(w0, x1 - ox), needW)));
        int h = Math.Min(canvasH - oy, Even(Math.Max(Math.Max(h0, y1 - oy), needH)));
        return new PrescaleRegion(w, h, ox, oy);

        static int Even(int v) => v + (v & 1);
    }

    /// <summary>
    /// Sizes and pins the prescaled content and mask for one flat insert, or <c>null</c> to keep the
    /// full-canvas recipe. The mask follows <see cref="ResolvedScreenInsert.Keyframes"/> (the warp
    /// target, overscan included); the content follows <see cref="ResolvedScreenInsert.ContentKeyframes"/>
    /// when the insert pins its content to the plate, else the same quad as the mask. Both live in
    /// one warp window (<see cref="WarpRegionFor"/>) — the union bounding box of every quad — and
    /// the keyframes are re-expressed relative to that window's origin.
    /// </summary>
    internal static PrescalePlan? PlanPrescale(ResolvedScreenInsert insert, int canvasW, int canvasH)
    {
        if (insert.Keyframes.Count == 0)
            return null;

        IReadOnlyList<InsertQuadFrame>? plate = insert.ContentKeyframes is { Count: > 0 } p ? p : null;
        (int W, int H) maskSize = insert.Prescale is { } mp ? (mp.MaskWidth, mp.MaskHeight) : PrescaleSize(insert.Keyframes, canvasW, canvasH);
        (int W, int H) contentSize = plate is null
            ? maskSize
            : insert.Prescale is { } cp ? (cp.ContentWidth, cp.ContentHeight) : PrescaleSize(plate, canvasW, canvasH);

        // Bounds measured by the caller over every keyframe (before the series was thinned), else
        // over the keyframes as given.
        InsertBounds? bounds = insert.Prescale?.Bounds ?? QuadBounds(insert.Keyframes, plate);
        int needW = Math.Max(maskSize.W + 2 * MaskMarginPx, contentSize.W + 2 * ContentMarginPx);
        int needH = Math.Max(maskSize.H + 2 * MaskMarginPx, contentSize.H + 2 * ContentMarginPx);
        PrescaleRegion warp = WarpRegionFor(bounds, needW, needH, canvasW, canvasH);

        PrescaleRegion mask = CenteredIn(maskSize, warp.Width, warp.Height, MaskMarginPx);
        PrescaleRegion content = plate is null ? mask : CenteredIn(contentSize, warp.Width, warp.Height, 1);

        IReadOnlyList<InsertQuadFrame>? maskKeys = PinThroughRegion(Shifted(insert.Keyframes, warp), mask, warp.Width, warp.Height);
        if (maskKeys is null)
            return null;
        if (plate is null)
            return new PrescalePlan(warp, mask, mask, maskKeys, maskKeys);

        IReadOnlyList<InsertQuadFrame>? contentKeys = PinThroughRegion(Shifted(plate, warp), content, warp.Width, warp.Height);
        return contentKeys is null ? null : new PrescalePlan(warp, content, mask, contentKeys, maskKeys);
    }

    /// <summary>A picture of <paramref name="size"/> centred in a window, kept at least <paramref name="margin"/> pixels from every edge.</summary>
    private static PrescaleRegion CenteredIn((int W, int H) size, int windowW, int windowH, int margin)
    {
        int w = Math.Clamp(size.W, 2, Math.Max(2, windowW - 2 * margin));
        int h = Math.Clamp(size.H, 2, Math.Max(2, windowH - 2 * margin));
        return new PrescaleRegion(w, h, (windowW - w) / 2, (windowH - h) / 2);
    }

    /// <summary>The keyframes expressed relative to the warp window's integer origin.</summary>
    private static IReadOnlyList<InsertQuadFrame> Shifted(IReadOnlyList<InsertQuadFrame> keys, PrescaleRegion window)
    {
        if (window.OffsetX == 0 && window.OffsetY == 0)
            return keys;
        double dx = window.OffsetX, dy = window.OffsetY;
        return keys.Select(k => new InsertQuadFrame(
            k.FrameIndex, k.X0 - dx, k.Y0 - dy, k.X1 - dx, k.Y1 - dy, k.X2 - dx, k.Y2 - dy, k.X3 - dx, k.Y3 - dy)).ToList();
    }

    /// <summary>
    /// The filter that smears a prescaled picture's edge pixels out over the rest of the canvas it
    /// was padded into, so the border beside the warped quad carries the content's own colour
    /// instead of a black rim that the mask would multiply into the edge.
    /// </summary>
    internal static string SmearBorders(PrescaleRegion region, int canvasW, int canvasH) =>
        $"fillborders=left={FfmpegArgvFormat.Number(region.OffsetX)}:right={FfmpegArgvFormat.Number(canvasW - region.OffsetX - region.Width)}" +
        $":top={FfmpegArgvFormat.Number(region.OffsetY)}:bottom={FfmpegArgvFormat.Number(canvasH - region.OffsetY - region.Height)}:mode=smear";

    /// <summary>
    /// Moves an insert branch (content, quad mask, mesh cell) from its own clock — frame 0 = the
    /// insert's first on-screen frame — onto the output clock.
    ///
    /// <para>
    /// <b>Unmatted</b>: a timestamp shift, <c>setpts=PTS-STARTPTS+start/TB</c>, exactly as
    /// always — the branch's first frame exists immediately and nothing waits on it.
    /// </para>
    /// <para>
    /// <b>Matted</b>: the shift is replaced by <c>start</c>'s worth of BLACK frames padded in front
    /// (<c>tpad</c>, which generates them without pulling its input), so the branch carries a frame
    /// for every output frame from t=0. This is what keeps the compile's memory bounded. A matted
    /// insert's alpha is keyed off the BASE picture, so its first composited frame at <c>start</c>
    /// needs the base decoded up to <c>start</c>; every framesync filter (<c>overlay</c>,
    /// <c>alphamerge</c>, <c>blend</c>) must see each input's first frame before it can emit
    /// anything, and the base is one <c>split</c> whose every output keeps receiving frames whether
    /// or not it is consumed. With a shifted branch the whole programme before the insert's start
    /// was therefore queued, at full resolution in RGB, once per split branch: on a four-clip
    /// landscape composite ffmpeg passed 20 GB and was killed, and a two-clip one peaked at
    /// 14.7 GB. Padded from zero, every branch advances in lockstep with the base. The pad is
    /// black: on the mask that is zero coverage, so nothing composites before <c>start</c> — and
    /// the overlay's <c>enable</c> window gates it as well. The frame count is the shift's own
    /// (<c>start × fps</c>, rounded), so frame k of the branch lands on the output frame it did
    /// before. <c>perspective</c> runs BEFORE the pad, so its per-frame <c>in</c> still counts from
    /// the insert's first frame. See docs/video-editing.md "Memory: every branch starts at zero".
    /// </para>
    /// </summary>
    internal static string DelayToOutputStart(ResolvedScreenInsert insert)
    {
        if (insert.Matte is null)
            return $"setpts=PTS-STARTPTS+{FfmpegArgvFormat.Number(insert.OutputStartSec)}/TB";

        long frames = PadFrames(insert);
        return frames > 0
            ? $"setpts=PTS-STARTPTS,tpad=start={FfmpegArgvFormat.Number(frames)}:start_mode=add:color=black"
            : "setpts=PTS-STARTPTS";
    }

    /// <summary>Whole output frames before the insert's start — the pad <see cref="DelayToOutputStart"/> puts in front of a matted branch.</summary>
    internal static long PadFrames(ResolvedScreenInsert insert) =>
        insert.FpsNum > 0 && insert.FpsDen > 0 && insert.OutputStartSec > 0
            ? (long)Math.Round(insert.OutputStartSec * insert.FpsNum / insert.FpsDen, MidpointRounding.AwayFromZero)
            : 0;

    /// <summary>
    /// Emits the per-frame plate matte for one insert: key the base picture with this plate's own
    /// grid-searched <c>colorkey</c> parameters, then invert, giving WHITE exactly where the
    /// plate is visible this frame. <c>colorkey</c> makes matching pixels transparent, so the
    /// plate lands in the alpha channel as zero and <c>negate</c> after <c>alphaextract</c> turns
    /// it into coverage.
    ///
    /// <para>
    /// Every number in the filter comes from <c>PlateChromaKeySolver</c>, which scored it against
    /// <c>ChromaQuadTracker</c>'s own segmentation of the same plate — the key is measured from
    /// the footage, never configured and never model-supplied. It is keyed off the BASE at this
    /// point in the chain rather than the original input because only the base is time-aligned
    /// with the output after the cut; the consequence, worth knowing, is that a colour grade
    /// applied earlier in the chain shifts the plate's colour out from under a key solved on
    /// ungraded pixels.
    /// </para>
    /// </summary>
    internal static string AppendPlateMatte(
        List<string> segments, string baseLabel, VideoInsertChromaKey key, string slot)
    {
        if (key.PlateDominance is int level && level >= MinMatteDominance)
            return AppendDominanceMatte(segments, baseLabel, key, level, slot);

        string colour = $"0x{Math.Clamp(key.R, 0, 255):X2}{Math.Clamp(key.G, 0, 255):X2}{Math.Clamp(key.B, 0, 255):X2}";
        segments.Add(
            $"{baseLabel}colorkey={colour}:{FfmpegArgvFormat.Number(key.Similarity)}:{FfmpegArgvFormat.Number(key.Blend)}," +
            $"format=yuva444p,alphaextract,negate[{slot}]");
        return $"[{slot}]";
    }

    /// <summary>Below this plate dominance (0..255) there is too little colour to key relative to and the <c>colorkey</c> matte is kept.</summary>
    internal const int MinMatteDominance = 24;

    /// <summary>Dominance, as fractions of the plate's own level, over which the plate term of the matte ramps 0 -> 1. Centred on 50% — the same point the tracker's edge-line fit puts the plate's edge at, so matte and geometry agree on where the plate ends.</summary>
    internal const double PlateRampLow = 0.35;
    internal const double PlateRampHigh = 0.65;

    /// <summary>
    /// Chroma PURITY (dominance over the target channel itself, 0..1) over which the dark-plate
    /// term ramps 0 -> 1. A plate pixel dimmed by the bezel's shadow or by the anti-aliased edge
    /// keeps its purity while its dominance falls: on the phone clip the 1-2 px transition into the
    /// bezel reads (0, 25, 0) — purity 1.0, dominance a tenth of the plate's. Without this term the
    /// matte left that rim un-keyed and it showed as a green outline round the insert. Dull scenery
    /// green is far less pure (the plant: 0.25).
    /// </summary>
    internal const double PurityRampLow = 0.5;
    internal const double PurityRampHigh = 0.75;

    /// <summary>Absolute target-channel floor (0..255) the dark-plate term ramps in over, so near-black noise — where purity divides small numbers by small numbers — never counts as plate.</summary>
    internal const int DarkPlateLevelLow = 10;
    internal const int DarkPlateLevelHigh = 24;

    /// <summary>Absolute dominance (0..255) over which the REFLECTION term ramps 0 -> 1: a reflection whitens the plate but leaves it a little greener than anything neutral.</summary>
    internal const int ReflectionDominanceLow = 8;
    internal const int ReflectionDominanceHigh = 24;

    /// <summary>
    /// Target-channel level, as fractions of the plate's own, over which the reflection term ramps
    /// 0 -> 1. Light ADDED to the plate keeps its target channel near the plate's level; dull
    /// scenery of the same hue sits far below it. Measured on the phone clip against a plate green
    /// of 202: the dimmest reflection streak pixels read ~121, the potted plant behind the subject
    /// ~93 — so the ramp sits between them.
    /// </summary>
    internal const double ReflectionLevelLow = 0.45;
    internal const double ReflectionLevelHigh = 0.65;

    /// <summary>
    /// The plate matte keyed on chroma DOMINANCE relative to the plate's own level — WHITE where
    /// the base pixel is plate this frame. Three terms, combined with <c>max</c>, each a function
    /// of just two numbers per pixel — dominance <c>x</c> (target minus strongest rival) and the
    /// target channel's level <c>y</c> — so the whole matte is ONE <c>lut2</c> table lookup:
    ///
    /// <list type="bullet">
    /// <item><b>Plate</b> — dominance from <see cref="PlateRampLow"/> to
    /// <see cref="PlateRampHigh"/> of the plate's level.</item>
    /// <item><b>Dark plate</b> — purity <c>x/y</c> from <see cref="PurityRampLow"/> to
    /// <see cref="PurityRampHigh"/>, above an absolute floor: shadowed plate and its
    /// anti-aliased edge.</item>
    /// <item><b>Reflection on the plate</b> — a little dominance left
    /// (<see cref="ReflectionDominanceLow"/>..<see cref="ReflectionDominanceHigh"/>) AND a target
    /// channel still near the plate's own level (<see cref="ReflectionLevelLow"/>..
    /// <see cref="ReflectionLevelHigh"/>).</item>
    /// </list>
    ///
    /// <para>
    /// <b>Why not <c>colorkey</c>.</b> Its single RGB-distance radius is the wrong shape on both
    /// sides. A reflection streak across a green screen is the plate plus white light —
    /// (150, 215, 160) against a plate of (0, 202, 0) — far outside any radius that excludes the
    /// room, so the streak stayed un-keyed and showed through the insert as a pale green blob.
    /// And dull scenery green is CLOSE in RGB distance to a dark plate: on the project's phone
    /// clip a potted plant behind the subject keyed as plate, and wherever the quad's overscan
    /// reached it the insert was painted onto the plant. Dominance and purity separate the plant
    /// (its green channel is half the plate's, its purity a quarter) from a reflection (the
    /// plate's green with the other two raised), and from a white wall or a pale blue shirt (no
    /// dominance at all).
    /// </para>
    /// <para>
    /// Built from channel mixing, blends and one <c>lut2</c>; every number in it is derived from
    /// the plate's measured level, never configured and never model-supplied.
    /// </para>
    /// </summary>
    internal static string AppendDominanceMatte(
        List<string> segments, string baseLabel, VideoInsertChromaKey key, int level, string slot)
    {
        (string target, string rival) = AppendTargetAndRival(segments, baseLabel, key, slot);

        int targetLevel = Math.Max(1, PlateTargetLevel(key));
        segments.Add($"{target}split=2[{slot}t1][{slot}t2]");
        segments.Add($"[{slot}t1]{rival}blend=all_mode=subtract,format=gray[{slot}d]");
        segments.Add($"[{slot}t2]format=gray[{slot}l]");

        string plate = Ramp("x", PlateRampLow * level, PlateRampHigh * level);
        string dark = $"{Ramp("x/max(y,1)", PurityRampLow, PurityRampHigh)}*{Ramp("y", DarkPlateLevelLow, DarkPlateLevelHigh)}";
        string reflection = $"{Ramp("x", ReflectionDominanceLow, ReflectionDominanceHigh)}*{Ramp("y", ReflectionLevelLow * targetLevel, ReflectionLevelHigh * targetLevel)}";
        segments.Add($"[{slot}d][{slot}l]lut2=c0='255*max({plate},max({dark},{reflection}))'[{slot}]");
        return $"[{slot}]";

        // clip((v - low) / (high - low), 0, 1), with every literal formatted invariantly.
        static string Ramp(string v, double low, double high)
        {
            double lo = Math.Round(low, 3);
            double span = Math.Max(1e-3, Math.Round(high - low, 3));
            return $"clip(({v}-{FfmpegArgvFormat.Number(lo)})/{FfmpegArgvFormat.Number(span)},0,1)";
        }
    }

    /// <summary>
    /// Gray copies (every channel equal) of the plate's TARGET channel and of its strongest RIVAL,
    /// for the plate's own colour family as read off its key colour.
    /// </summary>
    private static (string Target, string Rival) AppendTargetAndRival(
        List<string> segments, string baseLabel, VideoInsertChromaKey key, string slot)
    {
        // Planar RGB for the same reason as the spill suppression's despill: blend must never be
        // negotiated into YUV.
        segments.Add($"{baseLabel}format=gbrp,split=3[{slot}cr][{slot}cg][{slot}cb]");
        segments.Add($"[{slot}cr]{Broadcast('r')}[{slot}r]");
        segments.Add($"[{slot}cg]{Broadcast('g')}[{slot}g]");
        segments.Add($"[{slot}cb]{Broadcast('b')}[{slot}b]");

        switch (PlateFamily(key))
        {
            case 'b':
                segments.Add($"[{slot}r][{slot}g]blend=all_mode=lighten[{slot}v]");
                return ($"[{slot}b]", $"[{slot}v]");
            case 'm':
                segments.Add($"[{slot}r][{slot}b]blend=all_mode=darken[{slot}tm]");
                return ($"[{slot}tm]", $"[{slot}g]");
            default:
                segments.Add($"[{slot}r][{slot}b]blend=all_mode=lighten[{slot}v]");
                return ($"[{slot}g]", $"[{slot}v]");
        }

        static string Broadcast(char c)
        {
            string w(char ch) => ch == c ? "1" : "0";
            return $"colorchannelmixer=rr={w('r')}:rg={w('g')}:rb={w('b')}:gr={w('r')}:gg={w('g')}:gb={w('b')}:br={w('r')}:bg={w('g')}:bb={w('b')}";
        }
    }

    /// <summary><c>g</c>reen, <c>b</c>lue or <c>m</c>agenta — the plate family its key colour belongs to.</summary>
    internal static char PlateFamily(VideoInsertChromaKey key) =>
        key.G >= key.R && key.G >= key.B ? 'g'
        : key.B >= key.R ? (Math.Min(key.R, key.B) > key.G && key.R * 2 > key.B ? 'm' : 'b')
        : 'm';

    private static int PlateTargetLevel(VideoInsertChromaKey key) => PlateFamily(key) switch
    {
        'b' => key.B,
        'm' => Math.Min(key.R, key.B),
        _ => key.G
    };

    /// <summary>Compression noise on a clean plate's darkest channel, removed from the reflection layer so a plate with no reflection contributes exactly nothing.</summary>
    internal const int ReflectionNoiseFloor = 8;

    /// <summary>
    /// The plate's reflection layer: the ACHROMATIC part of the base picture — <c>min(R, G, B)</c>
    /// in every channel, less <see cref="ReflectionNoiseFloor"/>. A clean plate of any hue has a
    /// near-zero darkest channel and contributes black, a no-op under a screen blend; glare is
    /// white light added on top, which raises all three channels together and survives intact.
    ///
    /// <para>
    /// The plate's colour cannot simply be despilled away instead. Real plates are not pure: the
    /// phone clip's reads (0, 171, 30) where the glass turns from the light, and despilling keeps
    /// that blue as "reflection" — screen-blended over the insert's black content it tinted the
    /// whole screen dark teal. The cost of taking only the neutral part is that a COLOURED
    /// reflection (a red shirt mirrored in the glass) carries over as grey, which reads as glare
    /// rather than as a wrongly tinted screen.
    /// </para>
    /// </summary>
    internal static string AppendReflectionLayer(List<string> segments, string baseLabel, string slot)
    {
        segments.Add($"{baseLabel}format=gbrp,split=3[{slot}cr][{slot}cg][{slot}cb]");
        segments.Add($"[{slot}cr]colorchannelmixer=rr=1:rg=0:rb=0:gr=1:gg=0:gb=0:br=1:bg=0:bb=0[{slot}r]");
        segments.Add($"[{slot}cg]colorchannelmixer=rr=0:rg=1:rb=0:gr=0:gg=1:gb=0:br=0:bg=1:bb=0[{slot}g]");
        segments.Add($"[{slot}cb]colorchannelmixer=rr=0:rg=0:rb=1:gr=0:gg=0:gb=1:br=0:bg=0:bb=1[{slot}b]");
        segments.Add($"[{slot}r][{slot}g]blend=all_mode=darken[{slot}rg]");
        segments.Add($"[{slot}rg][{slot}b]blend=all_mode=darken," +
            $"lutrgb=r='max(val-{ReflectionNoiseFloor},0)':g='max(val-{ReflectionNoiseFloor},0)':b='max(val-{ReflectionNoiseFloor},0)'[{slot}]");
        return $"[{slot}]";
    }

    /// <summary>
    /// Screen-blends the reflection layer over the composited insert, inside the matte AND the
    /// insert's footprint only, ending at <paramref name="outLabel"/>. A no-op passthrough label
    /// rename is never emitted: with no reflection layer the caller composited straight to
    /// <paramref name="outLabel"/> and nothing is appended.
    ///
    /// <para>
    /// Physically this is what glass does: what the screen shows and what it reflects ADD, and
    /// screen is the clipped form of that addition. A reflection streak across the plate becomes
    /// the same streak across the new content; a clean plate contributes black, which screen
    /// leaves untouched — so a plate with no reflections composites exactly as before.
    /// </para>
    /// </summary>
    internal static void AppendReflectionTransfer(
        List<string> segments, string compositeLabel, string? reflectionLabel, string? maskLabel, string outLabel, int i)
    {
        if (reflectionLabel is null || maskLabel is null)
            return;

        // Alpha + overlay, not maskedmerge: the screened copy differs from the composite over the
        // WHOLE frame (the reflection layer is the entire despilled picture), and maskedmerge,
        // handed it with this mask, returned that copy everywhere — the finished frame came out
        // tinted magenta edge to edge. Spill suppression gets away with maskedmerge only because
        // its corrected copy equals the base everywhere outside green.
        // In RGB throughout: screen is a per-channel operation, and run on YUV planes it blends
        // the chroma planes too, which shifts every colour of the insert (red read as pink).
        segments.Add($"{compositeLabel}format=gbrp,split=2[rfa{i}][rfb{i}]");
        segments.Add($"[rfb{i}]{reflectionLabel}blend=all_mode=screen[rfs{i}]");
        segments.Add($"[rfs{i}]{maskLabel}alphamerge[rfm{i}]");
        segments.Add($"[rfa{i}][rfm{i}]overlay=x=0:y=0:eof_action=pass:format=rgb{outLabel}");
    }

    /// <summary>
    /// Neutralises the plate's colour where it has bled onto whatever is in FRONT of the plate,
    /// returning the label of a corrected base to composite over.
    ///
    /// <para>
    /// <b>Why a matte alone cannot fix this.</b> The pixels along an occluder's edge are a
    /// physical blend of that occluder and the plate behind it — a hand crossing a green screen
    /// genuinely has green light in its edge pixels, more so when motion blur widens that edge to
    /// several pixels. No cutout can remove colour from a pixel it is correctly keeping, so after
    /// matting the composite still showed a green rim tracing the hand. The key solver's spill
    /// term widens the key as far as it can, but past a point widening it eats the occluder
    /// instead, and what is left has to be corrected photometrically rather than geometrically.
    /// </para>
    /// <para>
    /// <b>The correction</b> is the classic despill, <c>G' = min(G, max(R, B))</c> — a green
    /// channel is pulled down to whatever the other two can justify. Expressed here without any
    /// plane surgery, as <c>max(min(G,R), min(G,B))</c>, which is the same identity built from
    /// two channel-mixed copies and a darken/lighten pair; R and B pass through untouched by
    /// construction. It is also, by construction, a NO-OP on any pixel where green is not
    /// dominant, so it cannot discolour skin, a bezel or a wall.
    /// </para>
    /// <para>
    /// <b>Where it is applied</b> is still bounded, because the one thing it would wrongly touch
    /// is genuinely green scenery. The band is the dilated matte MINUS the matte — a few pixels
    /// just outside the plate, which is exactly where an occluder's contaminated edge lies — so
    /// foliage across the room is never reached, and the plate's own interior is left alone since
    /// the insert covers it anyway.
    /// </para>
    /// </summary>
    internal static string AppendSpillSuppression(
        List<string> segments, string baseLabel, string despillSourceLabel, string matteLabel,
        string footprintLabel, string slot)
    {
        // Band = inside the insert's own footprint, but NOT plate. That is precisely the occluder
        // where it crosses the plate, at whatever width its motion blur happens to be — no radius
        // to pick and nothing to measure. Bounding by a DILATION of the matte was tried first and
        // is the wrong shape twice over: a fixed radius is too narrow for a fast-moving edge and
        // too wide for a locked-off one, and deriving the radius from the plate's own blend
        // statistics fails because the region those statistics describe is the plate's dim
        // interior, not its edge.
        segments.Add($"{matteLabel}negate[{slot}mi]");
        segments.Add($"{footprintLabel}[{slot}mi]blend=all_mode=multiply[{slot}band]");

        // G' = max(min(G,R), min(G,B)) == min(G, max(R,B)). Pinned to planar RGB: blend has no
        // packed-RGB support, and inside a full compile graph ffmpeg's negotiation was converting
        // these branches to YUV, where darken/lighten are not per-RGB-channel min/max at all —
        // measured on the phone clip, (33, 73, 32) "despilled" to (6, 50, 5) instead of
        // (33, 33, 32), so suppression left the green rim it exists to remove. Standalone the same
        // chain happened to negotiate gbrp, which is why it looked right in isolation.
        segments.Add($"{despillSourceLabel}format=gbrp,split=4[{slot}a][{slot}b][{slot}c][{slot}d]");
        segments.Add($"[{slot}c]colorchannelmixer=gr=1:gg=0:gb=0[{slot}gr]");
        segments.Add($"[{slot}d]colorchannelmixer=gr=0:gg=0:gb=1[{slot}gb]");
        segments.Add($"[{slot}a][{slot}gr]blend=all_mode=darken[{slot}t1]");
        segments.Add($"[{slot}b][{slot}gb]blend=all_mode=darken[{slot}t2]");
        segments.Add($"[{slot}t1][{slot}t2]blend=all_mode=lighten[{slot}dsp]");

        // The corrected picture is laid over the base with the band as its alpha. Not maskedmerge,
        // which this used to be: the band is synthesized a margin PAST the insert's window
        // (MaskDurationMarginSec), and maskedmerge's framesync repeats every input's last frame
        // until the LONGEST one ends (it has no `shortest` option to stop it) — so the finished
        // film ran a second past its source on the held final frame, with the insert's enable
        // window already closed: every video with an insert near its end finished on a raw green
        // screen. An overlay always ends with its MAIN input, the base picture.
        segments.Add($"[{slot}dsp][{slot}band]alphamerge[{slot}dsa]");
        segments.Add($"{baseLabel}[{slot}dsa]overlay=x=0:y=0:eof_action=pass:format=gbrp[{slot}out]");
        return $"[{slot}out]";
    }

    /// <summary>
    /// A white plate warped onto this insert's tracked quad — where the insert lands, regardless
    /// of whether it will be rendered flat or as a mesh. Used only to bound spill suppression to
    /// the insert's own area, so the correction can never reach real green scenery elsewhere in
    /// frame. Built from the FLAT corner keyframes even for a curved plate: as a bounding region
    /// the difference is a pixel or two of bow, and reusing them keeps this one branch rather
    /// than one per mesh cell.
    /// </summary>
    internal static string AppendInsertFootprint(
        List<string> segments, ResolvedScreenInsert insert, int frameWidth, int frameHeight, string slot)
    {
        int innerW = Math.Max(2, frameWidth - 2);
        int innerH = Math.Max(2, frameHeight - 2);
        string fps = $"{FfmpegArgvFormat.Number(insert.FpsNum)}/{FfmpegArgvFormat.Number(insert.FpsDen)}";
        double duration = Math.Max(0.1, insert.OutputEndSec - insert.OutputStartSec) + MaskDurationMarginSec;

        // The band MUST carry frames for the whole programme, not just the insert's own window.
        // It is the third input of AppendSpillSuppression's `maskedmerge`, and maskedmerge is a
        // framesync filter: handed one input whose first frame is at t=start, it DISCARDS every
        // frame of the other two — the base picture among them — until all three have delivered.
        // A delay alone therefore silently truncated the finished film to the part at or after the
        // first matted insert. Measured on the alpha launch film: the video stream began at
        // 20.433s (the monitor insert's start) and carried 166 frames instead of 749, so a player
        // held one frame for the first twenty seconds while the audio, which never passes through
        // this filter, played correctly from zero.
        //
        // Padding from zero with BLACK is the exact no-op the band wants: maskedmerge takes the
        // base wherever the mask is black, so every frame before the insert appears is the
        // untouched base. Applied AFTER perspective/setpts so the warp's own `in` frame indices —
        // which count from the insert's first frame, see BuildPerspectiveFilter — are unaffected.
        string start = FfmpegArgvFormat.Number(insert.OutputStartSec);
        string bandDelay = insert.OutputStartSec > 0
            ? $"setpts=PTS-STARTPTS,tpad=start_duration={start}:start_mode=add:color=black"
            : "setpts=PTS-STARTPTS";

        segments.Add(
            $"color=c=white:size={FfmpegArgvFormat.Number(innerW)}x{FfmpegArgvFormat.Number(innerH)}:rate={fps}:duration={FfmpegArgvFormat.Number(duration)}," +
            $"format=gray,pad={FfmpegArgvFormat.Number(frameWidth)}:{FfmpegArgvFormat.Number(frameHeight)}:1:1:black," +
            $"{BuildPerspectiveFilter(insert.Keyframes)},{bandDelay}[{slot}]");
        return $"[{slot}]";
    }

    /// <summary>
    /// The value <c>perspective</c>'s <c>in</c> variable takes on the FIRST frame it filters. It
    /// is 1, not 0: ffmpeg evaluates it as the link's output frame count plus one. Keyframes are
    /// indexed from 0 (frame 0 = the insert's first on-screen frame), so every corner series is
    /// shifted by this much before it is written as an expression. Until it was, every insert ran
    /// one frame AHEAD of its plate — invisible on a still plate, and on a moving one exactly one
    /// frame's travel of misregistration, which reads as the content sliding on the glass and
    /// opens a strip of raw plate along the trailing edge (measured on the customer's whip clip:
    /// 4% of the plate showing green on every fast frame). Verified with a real ffmpeg run, see
    /// <c>InsertTrackingQualityTests.Perspective_in_variable_is_one_based</c>.
    /// </summary>
    internal const long PerspectiveFirstFrame = 1;

    /// <summary>
    /// The <c>perspective</c> filter for one insert's keyframes — <c>sense=destination</c> (the
    /// bordered content's corners are sent TO the tracked quad) with <c>eval=frame</c> so the
    /// corner expressions re-evaluate per frame against <c>in</c>.
    /// </summary>
    internal static string BuildPerspectiveFilter(IReadOnlyList<InsertQuadFrame> keyframes, bool balanced = false, bool cubic = false)
    {
        string X(Func<InsertQuadFrame, double> pick)
        {
            var keys = keyframes.Select(k => (k.FrameIndex + PerspectiveFirstFrame, pick(k))).ToList();
            return balanced ? BuildBalancedPiecewiseExpr(keys) : BuildInterpolationExpr(keys);
        }

        return
            $"perspective=x0='{X(k => k.X0)}':y0='{X(k => k.Y0)}'" +
            $":x1='{X(k => k.X1)}':y1='{X(k => k.Y1)}'" +
            $":x2='{X(k => k.X2)}':y2='{X(k => k.Y2)}'" +
            $":x3='{X(k => k.X3)}':y3='{X(k => k.Y3)}'" +
            ":sense=destination:eval=frame" + (cubic ? ContentInterpolation : string.Empty);
    }

    /// <summary>
    /// `perspective`'s sampling kernel for the prescaled CONTENT: cubic (product decision, 2026-10-17)
    /// instead of the filter's bilinear default, which recovers most of the sharpness the sub-pixel resample
    /// costs (measured half-pixel-offset sharpness ratio 0.883 bilinear, 0.922 cubic). The CPU premium the maintainers
    /// accepted for this (+60 % on a full-frame push-in, +20 % on a slow push) is the one waived exception to the
    /// +10 % compile-cost ceiling; it applies to the kernel only. The white MASK stays bilinear on
    /// purpose: its edge decides where the quad is, and a cubic kernel there measured as wobble (the
    /// perfect-track render slip on slow-push went 0.093 -> 0.148 px, microshake 0.003 -> 0.074) for no
    /// sharpness at all, since the mask carries no detail; the kernel changes the sampling, never the
    /// geometry, so content and mask still land on the same quad. COST (T-010b, interleaved ffmpeg CPU, flat base,
    /// no matte): <c>perspective</c> over the bounding window of the insert's quads, cubic content vs the pre-F3
    /// chain is +20 % on the synthetic slow-push (window 1226x544 of 1344x768) and +60 % on Film A's geometry
    /// (window 1868x1060 of 1920x1080: the push-in ends near full frame, so the union of its quads is the frame);
    /// bilinear content in the same window is -19 % and +0.1 %. Setting this to an empty string is the one-line
    /// switch to bilinear (half-pixel sharpness 0.883 instead of 0.922).
    /// </summary>
    internal const string ContentInterpolation = ":interpolation=cubic";

    /// <summary>
    /// Piecewise-linear interpolation over <c>in</c> (the filter's per-frame input frame number):
    /// a single keyframe becomes a constant; N keyframes become nested
    /// <c>if(lt(in,f_{k+1}), a_k + (in - f_k)*s_k, ...)</c> terms holding the last value beyond
    /// the final keyframe. Keys must be strictly increasing (duplicates are collapsed, keeping the
    /// last). Every literal goes through <see cref="FfmpegArgvFormat.Number(double)"/>.
    /// </summary>
    /// <summary>
    /// The same piecewise-linear function as <see cref="BuildPiecewiseExpr"/>, written as a
    /// BALANCED tree of <c>if(lt(in,f), lower, upper)</c> splits instead of one linear chain.
    /// ffmpeg's expression parser rejects deeply nested expressions ("Missing ')' or too many
    /// args"), and a linear chain nests once per keyframe — which is why inserts cap their
    /// keyframes at <c>MaxInsertExprKeyframes</c> (96). A balanced tree nests log2(n) deep, so a
    /// censor mask can follow a fast-moving object at full keyframe density.
    /// </summary>
    internal static string BuildBalancedPiecewiseExpr(IReadOnlyList<(long Frame, double Value)> keys) =>
        BuildBalancedPiecewiseExpr(keys.Select(k => ((double)k.Frame, k.Value)).ToList(), "in");

    /// <summary>
    /// <see cref="BuildBalancedPiecewiseExpr(IReadOnlyList{ValueTuple{long, double}})"/> over any
    /// expression variable — <c>in</c> (frame number) for <c>perspective</c>, <c>t</c> (seconds) for
    /// an <c>overlay</c> position.
    /// </summary>
    internal static string BuildBalancedPiecewiseExpr(IReadOnlyList<(double Key, double Value)> keys, string variable)
    {
        var cleaned = new List<(double Key, double Value)>();
        foreach ((double key, double value) in keys.OrderBy(k => k.Key))
        {
            if (cleaned.Count > 0 && Math.Abs(cleaned[^1].Key - key) < 1e-9)
                cleaned[^1] = (key, value);
            else
                cleaned.Add((key, value));
        }

        if (cleaned.Count == 0)
            return "0";
        if (cleaned.Count == 1 || cleaned.All(k => k.Value == cleaned[0].Value))
            return FfmpegArgvFormat.Number(cleaned[0].Value);

        // Piece j covers [k_j, k_{j+1}); the first also covers everything before k_0 (extrapolated,
        // exactly like the linear chain) and the last, j = n-1, holds the final value.
        string Piece(int j)
        {
            if (j == cleaned.Count - 1)
                return FfmpegArgvFormat.Number(cleaned[^1].Value);
            (double k0, double v0) = cleaned[j];
            (double k1, double v1) = cleaned[j + 1];
            double slope = (v1 - v0) / (k1 - k0);
            return $"{FfmpegArgvFormat.Number(v0)}+({variable}-{FfmpegArgvFormat.Number(k0)})*{FfmpegArgvFormat.Number(slope)}";
        }

        string Tree(int lo, int hi)
        {
            if (lo == hi)
                return Piece(lo);
            int mid = (lo + hi) / 2;
            return $"if(lt({variable},{FfmpegArgvFormat.Number(cleaned[mid + 1].Key)}),{Tree(lo, mid)},{Tree(mid + 1, hi)})";
        }

        return Tree(0, cleaned.Count - 1);
    }

    /// <summary>
    /// Keyframe count up to which an insert's corner series is written as the original linear
    /// <c>if</c> chain (kept so every existing filtergraph stays byte-identical). Past it the
    /// chain would nest deeper than ffmpeg's expression parser accepts, so the same function is
    /// written as a balanced tree instead — which is what lets a track sampled at the source's
    /// full frame rate reach the encode without being thinned back down.
    /// </summary>
    internal const int LinearChainMaxKeyframes = 96;

    /// <summary>The piecewise-linear corner series in <c>in</c>: the linear chain while it is safe, the balanced tree beyond <see cref="LinearChainMaxKeyframes"/>.</summary>
    internal static string BuildInterpolationExpr(IReadOnlyList<(long Frame, double Value)> keys) =>
        keys.Count > LinearChainMaxKeyframes ? BuildBalancedPiecewiseExpr(keys) : BuildPiecewiseExpr(keys);

    internal static string BuildPiecewiseExpr(IReadOnlyList<(long Frame, double Value)> keys)
    {
        if (keys.Count == 0)
            return "0";

        // Collapse duplicate frame indices (keep last) and ensure ascending order.
        var cleaned = new List<(long Frame, double Value)>();
        foreach ((long frame, double value) in keys.OrderBy(k => k.Frame))
        {
            if (cleaned.Count > 0 && cleaned[^1].Frame == frame)
                cleaned[^1] = (frame, value);
            else
                cleaned.Add((frame, value));
        }

        // A series whose value never changes (one keyframe, or a perfectly static coordinate)
        // collapses to a plain constant — cheaper to evaluate per frame and much easier to read
        // in a captured filtergraph.
        if (cleaned.Count == 1 || cleaned.All(k => k.Value == cleaned[0].Value))
            return FfmpegArgvFormat.Number(cleaned[0].Value);

        // Innermost term: hold the final keyframe's value.
        string expr = FfmpegArgvFormat.Number(cleaned[^1].Value);

        for (int i = cleaned.Count - 2; i >= 0; i--)
        {
            (long f0, double v0) = cleaned[i];
            (long f1, double v1) = cleaned[i + 1];
            double slope = (v1 - v0) / (f1 - f0);
            string segment =
                $"{FfmpegArgvFormat.Number(v0)}+(in-{FfmpegArgvFormat.Number(f0)})*{FfmpegArgvFormat.Number(slope)}";
            expr = $"if(lt(in,{FfmpegArgvFormat.Number(f1)}),{segment},{expr})";
        }

        return expr;
    }
}
