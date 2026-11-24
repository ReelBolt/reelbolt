using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// One placement of a segmentation mask: the stretch of the SOURCE clip a track covers
/// (<c>[SourceStartSec, SourceEndSec)</c>) and where that same stretch lands on the OUTPUT
/// timeline (<c>[OutputStartSec, OutputEndSec)</c>). One track produces one window per kept span
/// it survives into — a cut that keeps two pieces of the same object's window produces two.
/// </summary>
/// <param name="SpanStartSec">
/// The source second the window's kept span starts at, when the span has its own picture chain
/// (<paramref name="SpanChain"/>); null otherwise.
/// </param>
/// <param name="SpanChain">
/// The span's own geometry after <c>trim,setpts</c> (reframe <c>zoompan</c> stage and/or the per-span canvas
/// fit: the safe-area pad, a visible-anchor cover crop), exactly as the picture branch applies it on the
/// span's clock; null when the span uses the clip's static conform.
/// </param>
public sealed record RegionMaskWindow(
    double SourceStartSec, double SourceEndSec, double OutputStartSec, double OutputEndSec,
    double? SpanStartSec = null, string? SpanChain = null);

/// <summary>The mask-helper knobs of ONE <see cref="VideoRegionEffect"/> entry (see <see cref="RegionEffectFilterBuilder.BuildMaskHelperStages"/>).</summary>
public sealed record RegionMaskHelper(bool Invert, double FeatherPx, double Opacity);

/// <summary>
/// Builds the first-party ffmpeg graph for "apply effect E to object P" (plan §5): a
/// program-length grey mask per segmentation track, the mask helper chain (invert, then feather,
/// then opacity), and one compositing stage per <see cref="VideoRegionEffect"/>.
///
/// <para>
/// <b>What this class is, and is not.</b> It is a pure string builder: no I/O, no clock, no
/// culture-sensitive formatting, no user text. An <see cref="RegionEffect"/> only ever selects a
/// constant graph; the numeric knobs are clamped here and formatted through
/// <see cref="FfmpegArgvFormat"/>; <see cref="VideoRegionEffect.Target"/> never reaches this class
/// at all — it is resolved to a track id before any of this runs.
/// </para>
///
/// <para>
/// <b>Compositing is alpha, not <c>maskedmerge</c>.</b> The plan's sketch composites with
/// <c>[base][effect][mask]maskedmerge</c>, but <c>maskedmerge</c> blends EVERY plane by the mask
/// (<c>ffmpeg -filters</c>: "Merge first stream with second stream using third stream as mask"),
/// and a mask is a grey image whose chroma is neutral everywhere. Outside the object the result
/// would therefore still be a 50/50 chroma mix of the effect and the picture — the isolation bar
/// (plan §7: mean |Δ| outside the mask ≤ 1/255) could not be met, and <c>ColorPop</c>, a chroma-only
/// operation, could not be expressed at all. Instead the effect is applied to the whole picture and
/// composited back through the mask's own ALPHA (<c>alphamerge</c> + <c>overlay</c>), the mechanism
/// <see cref="CensorFilterBuilder"/> already uses: outside the mask the output is the input picture,
/// inside it the effect applies at mask opacity, and a feathered edge becomes a partly transparent
/// composite of exactly that edge.
/// </para>
/// </summary>
public static class RegionEffectFilterBuilder
{
    /// <summary>Blur sigma as a fraction of the frame's SHORT side at <c>Strength = 1</c>.</summary>
    internal const double BlurSigmaFraction = 0.015;

    /// <summary>
    /// Blur sigma as a fraction of the OBJECT's short side (on the output canvas) at <c>Strength = 1</c>.
    /// A frame-relative sigma alone left a near face readable (production run 3bc05f61: a 1280x720 face
    /// filling a 1080x1920 frame kept eyes, nose and mouth at Strength 2); a tenth of the face's short
    /// side at 1.0 removes its features whatever its size. The larger of this and the frame-relative sigma wins.
    /// </summary>
    internal const double ObjectBlurSigmaFraction = 0.10;

    /// <summary>Mosaic block as a fraction of the OBJECT's short side at <c>Strength = 1</c> (about eight blocks across it).</summary>
    internal const double ObjectPixelateBlockFraction = 0.125;

    /// <summary>Mosaic block size as a fraction of the frame's SHORT side at <c>Strength = 1</c>.</summary>
    internal const double PixelateBlockFraction = 0.02;

    /// <summary>Highlight: how much the object is brightened at <c>Strength = 1</c> (eq brightness).</summary>
    internal const double HighlightGain = 0.10;

    /// <summary>Highlight: how much the object's contrast rises at <c>Strength = 1</c> (eq contrast is 1 + this).</summary>
    internal const double HighlightContrast = 0.10;

    /// <summary>Highlight: how much the rest of the picture is dimmed at <c>Strength = 1</c> (eq brightness).</summary>
    internal const double HighlightDim = 0.15;

    /// <summary>Spotlight: how much the rest of the picture is dimmed at <c>Strength = 1</c> (eq brightness).</summary>
    internal const double SpotlightDim = 0.60;

    /// <summary>Outline: band width as a fraction of the frame's SHORT side at <c>Strength = 1</c>.</summary>
    internal const double OutlineBandFraction = 0.004;

    /// <summary>
    /// Widest outline band, in output pixels. The band is built by one <c>dilation</c> pass per band
    /// pixel (ffmpeg 8's <c>dilation</c> grows exactly one pixel a pass — measured: a 20x16 box at
    /// 320 white pixels became 396, i.e. a one-pixel ring), so this is also the pass count cap.
    /// </summary>
    internal const int MaxOutlineBandPx = 16;

    /// <summary>Smallest mosaic block; below this the effect would be a no-op the customer paid for.</summary>
    internal const int MinPixelateBlockPx = 2;

    /// <summary>Smallest blur sigma, for the same reason.</summary>
    internal const double MinBlurSigma = 0.1;

    /// <summary>Widest feather, in output pixels — the same cap <see cref="ReelBolt.Shared.Workflows.RegionEffectRules"/> validates against.</summary>
    internal const double MaxFeatherPx = 64;

    /// <summary>The outline's solid colour: the one outline colour this wave ships.</summary>
    internal const string OutlineColour = "white";

    /// <summary>
    /// The program-length mask stream for ONE segmentation track: the mask video conformed with its
    /// source's own picture geometry (<paramref name="conformFilter"/> is
    /// <see cref="VideoCompileStepExecutor.BuildCanvasFitFilter"/>'s own output — reused, never
    /// re-derived), resampled to the output frame rate, then placed on every output window it
    /// survives into over a black canvas as long as the whole program. Black everywhere else means
    /// "the object is not here", which means "no effect".
    /// </summary>
    /// <param name="inputIndex">ffmpeg input index of the downloaded mask video.</param>
    /// <param name="conformFilter">The picture's own scale/crop/pad stage, or null when the source already IS the canvas.</param>
    /// <param name="fps">The output frame rate as <c>num/den</c>.</param>
    /// <param name="totalDurationSec">Program length — the black canvas must cover every frame of it.</param>
    /// <param name="windows">One entry per kept span the track survives into; never empty (a track with none is refused before this runs).</param>
    /// <param name="outputLabel">The label the finished, program-length grey mask is emitted on.</param>
    /// <param name="slot">
    /// Which track this stream belongs to. Only used to keep the stream's internal labels
    /// (<c>[rm{slot}raw]</c>, <c>[rm{slot}win{n}]</c>…) unique: ffmpeg rejects a label defined
    /// twice, and a compile with two tracked objects builds two of these in one graph.
    /// </param>
    public static string BuildMaskStream(
        int inputIndex,
        string? conformFilter,
        string fps,
        double totalDurationSec,
        int width,
        int height,
        IReadOnlyList<RegionMaskWindow> windows,
        string outputLabel,
        int slot = 0)
    {
        if (windows.Count == 0)
            throw new ArgumentException("A mask stream needs at least one output window.", nameof(windows));

        double duration = Math.Round(Math.Max(0.04, totalDurationSec), 3);
        if (windows.Any(w => w.SpanChain is not null))
            return BuildPerSpanMaskStream(inputIndex, conformFilter, fps, duration, width, height, windows, outputLabel, slot);

        var stages = new List<string>
        {
            $"[{FfmpegArgvFormat.Number(inputIndex)}:v]format=yuv420p" +
            (string.IsNullOrEmpty(conformFilter) ? string.Empty : "," + conformFilter) +
            $",fps={fps},setsar=1,setpts=PTS-STARTPTS[rm{slot}raw]",
            $"color=c=black:s={FfmpegArgvFormat.Number(width)}x{FfmpegArgvFormat.Number(height)}:r={fps}:" +
            $"d={FfmpegArgvFormat.Number(duration)},format=yuv420p[rm{slot}win0]"
        };

        for (int i = 0; i < windows.Count; i++)
        {
            RegionMaskWindow window = windows[i];
            double sourceStart = Math.Max(0, window.SourceStartSec);
            double sourceEnd = Math.Max(sourceStart + 0.001, window.SourceEndSec);
            double outputStart = Math.Max(0, window.OutputStartSec);
            stages.Add(
                $"[rm{slot}raw]trim=start={FfmpegArgvFormat.Number(Math.Round(sourceStart, 3))}:" +
                $"end={FfmpegArgvFormat.Number(Math.Round(sourceEnd, 3))}," +
                $"setpts=PTS-STARTPTS+{FfmpegArgvFormat.Number(Math.Round(outputStart, 3))}/TB[rm{slot}seg{i}]");
            // eof_action=pass: past its window the window stream ends and the black canvas keeps
            // flowing, so the frames after it read "no mask" instead of repeating the window's last one.
            stages.Add(
                $"[rm{slot}win{i}][rm{slot}seg{i}]overlay=x=0:y=0:eof_action=pass:repeatlast=0:format=yuv420[rm{slot}win{i + 1}]");
        }

        stages.Add($"[rm{slot}win{windows.Count}]format=gray{outputLabel}");
        return string.Join(";", stages);
    }

    /// <summary>
    /// <see cref="BuildMaskStream"/> when at least one window's span carries its own picture chain (a reframe
    /// or the safe-area pad): the mask is split once per window, and each window is conformed the way ITS
    /// span's picture is. A chained window is trimmed from its span's first frame, so the <c>zoompan</c> /
    /// cover-crop expressions run on the same clock as the picture, then cut to the window; an unchained one
    /// takes the clip's static conform, exactly as the single-stream graph does.
    /// </summary>
    private static string BuildPerSpanMaskStream(
        int inputIndex, string? conformFilter, string fps, double duration, int width, int height,
        IReadOnlyList<RegionMaskWindow> windows, string outputLabel, int slot)
    {
        string split = windows.Count == 1
            ? $"null[rm{slot}in0]"
            : $"split={FfmpegArgvFormat.Number(windows.Count)}" + string.Concat(Enumerable.Range(0, windows.Count).Select(i => $"[rm{slot}in{i}]"));
        var stages = new List<string>
        {
            $"[{FfmpegArgvFormat.Number(inputIndex)}:v]format=yuv420p,{split}",
            $"color=c=black:s={FfmpegArgvFormat.Number(width)}x{FfmpegArgvFormat.Number(height)}:r={fps}:" +
            $"d={FfmpegArgvFormat.Number(duration)},format=yuv420p[rm{slot}win0]"
        };

        for (int i = 0; i < windows.Count; i++)
        {
            RegionMaskWindow window = windows[i];
            double sourceStart = Math.Max(0, window.SourceStartSec);
            double sourceEnd = Math.Max(sourceStart + 0.001, window.SourceEndSec);
            double outputStart = Math.Round(Math.Max(0, window.OutputStartSec), 3);
            if (window.SpanChain is { } chain)
            {
                double spanStart = Math.Min(sourceStart, Math.Max(0, window.SpanStartSec ?? sourceStart));
                stages.Add(
                    $"[rm{slot}in{i}]trim=start={FfmpegArgvFormat.Number(Math.Round(spanStart, 3))}:" +
                    $"end={FfmpegArgvFormat.Number(Math.Round(sourceEnd, 3))},setpts=PTS-STARTPTS," +
                    (chain.Length == 0 ? string.Empty : chain + ",") +
                    $"fps={fps},setsar=1," +
                    $"trim=start={FfmpegArgvFormat.Number(Math.Round(sourceStart - spanStart, 3))}," +
                    $"setpts=PTS-STARTPTS+{FfmpegArgvFormat.Number(outputStart)}/TB[rm{slot}seg{i}]");
            }
            else
            {
                stages.Add(
                    $"[rm{slot}in{i}]" + (string.IsNullOrEmpty(conformFilter) ? string.Empty : conformFilter + ",") +
                    $"fps={fps},setsar=1,setpts=PTS-STARTPTS," +
                    $"trim=start={FfmpegArgvFormat.Number(Math.Round(sourceStart, 3))}:end={FfmpegArgvFormat.Number(Math.Round(sourceEnd, 3))}," +
                    $"setpts=PTS-STARTPTS+{FfmpegArgvFormat.Number(outputStart)}/TB[rm{slot}seg{i}]");
            }

            stages.Add(
                $"[rm{slot}win{i}][rm{slot}seg{i}]overlay=x=0:y=0:eof_action=pass:repeatlast=0:format=yuv420[rm{slot}win{i + 1}]");
        }

        stages.Add($"[rm{slot}win{windows.Count}]format=gray{outputLabel}");
        return string.Join(";", stages);
    }

    /// <summary>
    /// The mask helper chain of one effect entry, in the plan's fixed order — invert, then feather,
    /// then opacity — so the complement of a feathered mask is never approximated by a second feather.
    /// All knobs at their no-op value emit a pass-through stage instead of nothing, because a label a
    /// later stage consumes must always exist.
    /// </summary>
    /// <param name="inputLabel">The program-length grey mask (<see cref="BuildMaskStream"/>'s output).</param>
    /// <param name="outputLabel">Label the processed mask is emitted on.</param>
    /// <param name="invert">Negate the mask first: "everything EXCEPT the object".</param>
    /// <param name="featherPx">Gaussian sigma applied to the MASK, never to the picture, in output pixels.</param>
    /// <param name="opacity">Mask alpha multiplier 0..1; 1 is a no-op.</param>
    /// <param name="coverObject">
    /// Grow the mask by <see cref="CoverGrowSigmas"/> feather sigmas BEFORE feathering, so the soft edge lies
    /// OUTSIDE the object and every object pixel is painted at (nearly) full opacity. Used by the anonymising
    /// effects (<see cref="RegionEffect.Blur"/>, <see cref="RegionEffect.Pixelate"/>) when not inverted: a
    /// feather centred on the object's edge leaves the outer band of the object partly un-effected, and on a
    /// face that band is the profile, the nose and the eyes (production run 5d420a0f, Strength 2, FeatherPx 24:
    /// alpha 0.5 at the face's own edge, under 0.98 for 48 px inside it). Ignored with no feather or with invert.
    /// </param>
    public static string BuildMaskHelperChain(
        string inputLabel, string outputLabel, bool invert, double featherPx, double opacity, bool coverObject = false)
    {
        var filters = new List<string>();
        if (invert)
            filters.Add("negate");

        double feather = double.IsFinite(featherPx) ? Math.Clamp(featherPx, 0, MaxFeatherPx) : 0;
        if (feather > 0 && coverObject && !invert)
            filters.Add(CoverGrowStage(feather));
        if (feather > 0)
            filters.Add($"gblur=sigma={FfmpegArgvFormat.Number(Math.Round(feather, 3))}");

        double alpha = double.IsFinite(opacity) ? Math.Clamp(opacity, 0, 1) : 1;
        if (alpha < 1)
            filters.Add($"lut=y='val*{FfmpegArgvFormat.Number(Math.Round(alpha, 4))}'");

        return $"{inputLabel}{(filters.Count == 0 ? "null" : string.Join(",", filters))}{outputLabel}";
    }

    /// <summary>
    /// How far, in feather sigmas, <see cref="BuildMaskHelperChain"/>'s <c>coverObject</c> grows the mask before
    /// feathering it. Two sigmas puts the feathered alpha at 0.977 on the object's own edge (the normal CDF at 2).
    /// </summary>
    internal const double CoverGrowSigmas = 2.0;

    /// <summary>
    /// The grey level a once-blurred mask is thresholded at to grow it by <see cref="CoverGrowSigmas"/> sigmas:
    /// a straight edge blurred with sigma s reads Phi(-d/s) x 255 at d pixels outside it, and Phi(-2) x 255 is 5.8.
    /// </summary>
    internal const int CoverGrowThreshold = 6;

    /// <summary>
    /// The grow stage: one Gaussian of the feather's own sigma, then a hard threshold at
    /// <see cref="CoverGrowThreshold"/> — a dilation by about <see cref="CoverGrowSigmas"/> sigmas in two cheap
    /// passes (ffmpeg's <c>dilation</c> grows one pixel a pass, so 48 px would be 48 passes).
    /// </summary>
    internal static string CoverGrowStage(double feather) =>
        $"gblur=sigma={FfmpegArgvFormat.Number(Math.Round(feather, 3))}," +
        $"lut=y='if(gte(val,{FfmpegArgvFormat.Number(CoverGrowThreshold)}),255,0)'";

    /// <summary>True for the effects whose job is to make the object unreadable, so the whole object must be covered.</summary>
    public static bool Anonymises(RegionEffect effect) => effect is RegionEffect.Blur or RegionEffect.Pixelate;

    /// <summary>
    /// The mask helper chains for every effect entry that targets ONE track. A track's mask is
    /// produced once (one download, one conform, one window pass) and fanned out here, because an
    /// ffmpeg pad may be consumed exactly once and two entries on one object may want different
    /// invert/feather/opacity.
    /// </summary>
    /// <param name="trackMaskLabel">The track's program-length mask label.</param>
    /// <param name="helpers">One entry per effect entry on this track, in entry order.</param>
    /// <param name="track">The track's ordinal, used only to keep the fan-out labels unique.</param>
    /// <returns>The filter stages to append, and the per-entry mask label in entry order.</returns>
    public static (string Filter, IReadOnlyList<string> MaskLabels) BuildMaskHelperStages(
        string trackMaskLabel, IReadOnlyList<RegionMaskHelper> helpers, int track)
    {
        if (helpers.Count == 0)
            throw new ArgumentException("At least one effect entry is required.", nameof(helpers));

        var stages = new List<string>();
        var labels = new List<string>(helpers.Count);
        for (int i = 0; i < helpers.Count; i++)
            labels.Add($"[rmt{track}e{i}]");

        if (helpers.Count == 1)
        {
            RegionMaskHelper only = helpers[0];
            stages.Add(BuildMaskHelperChain(trackMaskLabel, labels[0], only.Invert, only.FeatherPx, only.Opacity));
            return (string.Join(";", stages), labels);
        }

        stages.Add($"{trackMaskLabel}split={FfmpegArgvFormat.Number(helpers.Count)}" +
                   string.Concat(labels.Select((_, i) => $"[rmt{track}s{i}]")));
        for (int i = 0; i < helpers.Count; i++)
        {
            RegionMaskHelper helper = helpers[i];
            stages.Add(BuildMaskHelperChain($"[rmt{track}s{i}]", labels[i], helper.Invert, helper.FeatherPx, helper.Opacity));
        }

        return (string.Join(";", stages), labels);
    }

    /// <summary>
    /// One effect, composited onto the picture through the alpha of its own mask. Consumes
    /// <paramref name="inputLabel"/> and <paramref name="maskLabel"/> exactly once each and emits
    /// <paramref name="outputLabel"/>. <paramref name="slot"/> only keeps this stage's internal
    /// labels unique when several effects chain.
    /// </summary>
    public static string BuildEffectChain(
        string inputLabel,
        string maskLabel,
        string outputLabel,
        int slot,
        RegionEffect effect,
        int width,
        int height,
        double strength,
        string fps,
        double totalDurationSec,
        double? objectSizePx = null)
    {
        int shortSide = Math.Max(1, Math.Min(width, height));
        double s = double.IsFinite(strength) ? Math.Clamp(strength, 0, 2) : 1.0;

        return effect switch
        {
            RegionEffect.Blur => Blur(inputLabel, maskLabel, outputLabel, slot, BlurSigma(shortSide, s, objectSizePx)),
            RegionEffect.Pixelate => Pixelate(inputLabel, maskLabel, outputLabel, slot, PixelBlock(shortSide, s, objectSizePx)),
            RegionEffect.Highlight => Highlight(inputLabel, maskLabel, outputLabel, slot, s),
            RegionEffect.Spotlight => Spotlight(inputLabel, maskLabel, outputLabel, slot, s),
            RegionEffect.ColorPop => ColorPop(inputLabel, maskLabel, outputLabel, slot),
            RegionEffect.Outline => Outline(inputLabel, maskLabel, outputLabel, slot, width, height, fps, totalDurationSec, OutlineBand(shortSide, s)),
            RegionEffect.Flash => RegionEffectFlashGlow.Flash(inputLabel, maskLabel, outputLabel, slot, s),
            RegionEffect.Glow => RegionEffectFlashGlow.Glow(inputLabel, maskLabel, outputLabel, slot, shortSide, s),
            _ => throw new ArgumentOutOfRangeException(nameof(effect), effect, "Unknown region effect.")
        };
    }

    /// <summary>
    /// Blur sigma in output pixels for a strength: the larger of 1.5% of the frame's short side and 10% of the
    /// object's short side (<paramref name="objectSizePx"/>, when known) at 1.0, times the strength, never below
    /// <see cref="MinBlurSigma"/>. Without an object size it is exactly the frame-relative sigma.
    /// </summary>
    internal static double BlurSigma(int shortSide, double strength, double? objectSizePx = null) =>
        Math.Round(Math.Max(MinBlurSigma, Math.Max(shortSide * BlurSigmaFraction, ObjectPart(objectSizePx, ObjectBlurSigmaFraction)) * strength), 2);

    /// <summary>
    /// Mosaic block edge in output pixels: the larger of 2% of the frame's short side and an eighth of the object's
    /// short side at 1.0, times the strength, clamped to a usable block.
    /// </summary>
    internal static int PixelBlock(int shortSide, double strength, double? objectSizePx = null) =>
        Math.Clamp((int)Math.Round(Math.Max(shortSide * PixelateBlockFraction, ObjectPart(objectSizePx, ObjectPixelateBlockFraction)) * strength),
            MinPixelateBlockPx, Math.Max(MinPixelateBlockPx, shortSide / 2));

    private static double ObjectPart(double? objectSizePx, double fraction) =>
        objectSizePx is { } size && double.IsFinite(size) && size > 0 ? size * fraction : 0;

    /// <summary>Outline band width in output pixels — 0.4% of the short side at 1.0, capped at <see cref="MaxOutlineBandPx"/>.</summary>
    internal static int OutlineBand(int shortSide, double strength) =>
        Math.Clamp((int)Math.Round(shortSide * OutlineBandFraction * strength), 1, MaxOutlineBandPx);

    private static string Blur(string input, string mask, string output, int slot, double sigma) =>
        $"{input}split=2[ree{slot}b][ree{slot}e];" +
        $"[ree{slot}e]gblur=sigma={FfmpegArgvFormat.Number(sigma)},format=yuva420p[ree{slot}a];" +
        $"[ree{slot}a]{mask}alphamerge[ree{slot}f];" +
        $"[ree{slot}b][ree{slot}f]overlay=x=0:y=0:format=yuv420{output}";

    private static string Pixelate(string input, string mask, string output, int slot, int block) =>
        $"{input}split=2[ree{slot}b][ree{slot}e];" +
        $"[ree{slot}e]pixelize=w={FfmpegArgvFormat.Number(block)}:h={FfmpegArgvFormat.Number(block)}:mode=avg," +
        $"format=yuva420p[ree{slot}a];" +
        $"[ree{slot}a]{mask}alphamerge[ree{slot}f];" +
        $"[ree{slot}b][ree{slot}f]overlay=x=0:y=0:format=yuv420{output}";

    private static string Highlight(string input, string mask, string output, int slot, double strength)
    {
        double gain = Math.Round(HighlightGain * strength, 4);
        double contrast = Math.Round(1 + HighlightContrast * strength, 4);
        double dim = Math.Round(HighlightDim * strength, 4);
        return $"{mask}split=2[ree{slot}mi][ree{slot}mo];" +
               $"[ree{slot}mo]negate[ree{slot}mX];" +
               $"{input}split=2[ree{slot}b][ree{slot}h1];" +
               $"[ree{slot}h1]eq=brightness={FfmpegArgvFormat.Number(gain)}:contrast={FfmpegArgvFormat.Number(contrast)}," +
               $"format=yuva420p[ree{slot}h2];" +
               $"[ree{slot}h2][ree{slot}mi]alphamerge[ree{slot}h3];" +
               $"[ree{slot}b][ree{slot}h3]overlay=x=0:y=0:format=yuv420[ree{slot}h4];" +
               $"[ree{slot}h4]split=2[ree{slot}h5][ree{slot}h6];" +
               $"[ree{slot}h6]eq=brightness={FfmpegArgvFormat.Number(Math.Round(-dim, 4))},format=yuva420p[ree{slot}h7];" +
               $"[ree{slot}h7][ree{slot}mX]alphamerge[ree{slot}h8];" +
               $"[ree{slot}h5][ree{slot}h8]overlay=x=0:y=0:format=yuv420{output}";
    }

    private static string Spotlight(string input, string mask, string output, int slot, double strength)
    {
        double dim = Math.Round(-SpotlightDim * strength, 4);
        return $"{mask}negate[ree{slot}mX];" +
               $"{input}split=2[ree{slot}b][ree{slot}s1];" +
               $"[ree{slot}s1]eq=brightness={FfmpegArgvFormat.Number(dim)},format=yuva420p[ree{slot}s2];" +
               $"[ree{slot}s2][ree{slot}mX]alphamerge[ree{slot}s3];" +
               $"[ree{slot}b][ree{slot}s3]overlay=x=0:y=0:format=yuv420{output}";
    }

    private static string ColorPop(string input, string mask, string output, int slot) =>
        $"{mask}negate[ree{slot}mX];" +
        $"{input}split=2[ree{slot}b][ree{slot}c1];" +
        $"[ree{slot}c1]hue=s=0,format=yuva420p[ree{slot}c2];" +
        $"[ree{slot}c2][ree{slot}mX]alphamerge[ree{slot}c3];" +
        $"[ree{slot}b][ree{slot}c3]overlay=x=0:y=0:format=yuv420{output}";

    private static string Outline(
        string input, string mask, string output, int slot, int width, int height, string fps, double totalDurationSec, int band)
    {
        double duration = Math.Round(Math.Max(0.04, totalDurationSec), 3);
        // band = dilation(mask) ∧ ¬mask: the ring `band` pixels wide that lies just outside the
        // object's silhouette. One dilation pass grows exactly one pixel, so the
        // ring width is the pass count; the multiply (not subtract) keeps the arithmetic exact on a
        // binary mask (255*255 = 255 on the ring, 255*0 and 0*255 = 0 either side of it).
        int passes = Math.Clamp(band, 1, MaxOutlineBandPx);
        var dilations = new System.Text.StringBuilder();
        string previous = $"[ree{slot}o1]";
        for (int pass = 0; pass < passes; pass++)
        {
            string next = pass == passes - 1 ? $"[ree{slot}od]" : $"[ree{slot}d{pass}]";
            dilations.Append($"{previous}dilation=coordinates=255{next};");
            previous = next;
        }

        return $"{mask}split=2[ree{slot}o0][ree{slot}o1];" +
               dilations +
               $"[ree{slot}o0]negate[ree{slot}oi];" +
               $"[ree{slot}od][ree{slot}oi]blend=all_mode=multiply[ree{slot}ob];" +
               $"color=c={OutlineColour}:s={FfmpegArgvFormat.Number(width)}x{FfmpegArgvFormat.Number(height)}:r={fps}:" +
               $"d={FfmpegArgvFormat.Number(duration)},format=yuva420p[ree{slot}oc];" +
               $"[ree{slot}oc][ree{slot}ob]alphamerge[ree{slot}oa];" +
               $"{input}[ree{slot}oa]overlay=x=0:y=0:eof_action=pass:repeatlast=0:format=yuv420{output}";
    }
}
