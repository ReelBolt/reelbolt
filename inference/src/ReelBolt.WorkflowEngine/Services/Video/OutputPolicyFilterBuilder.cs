namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The corner of the output frame the ReelBolt watermark is anchored to. The watermark's NEAR
/// corner (the corner closest to this one) is pinned to the frame corner by a fixed margin, so
/// <c>BottomRight</c> means "the watermark's bottom-right corner sits <see cref="
/// ReelBolt.WorkflowEngine.Services.Video.OutputPolicyFilterBuilder.DefaultWatermarkMargin"/> pixels
/// from the frame's bottom-right". Only the corner is parameterised here; the actual x/y are
/// expressed with the <c>overlay</c> filter's own variables at runtime, so this builder never needs
/// the watermark image's pixel size (it is pure — no I/O).
/// </summary>
public enum WatermarkCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// Builds the ffmpeg filter-chain fragment for the B8b "trial output policy" (see
/// docs/billing.md): it caps the OUTPUT height so a watermarked/trial render never exceeds
/// <paramref name="maxHeight"/> (default 720p), and — when a watermark is requested — composites the
/// ReelBolt mark on top of that capped frame. Pure — no I/O, no file paths, no storage keys, no
/// ffprobe — and it splices into an existing filtergraph exactly the way
/// <see cref="OverlayAssetFilterBuilder"/> does: it consumes <paramref name="baseFilterChainEndLabel"/>
/// and, when it has anything to emit, ends at <paramref name="finalLabel"/> (default
/// <c>"[vout]"</c>). The watermark image is supplied by the CALLER as its own extra <c>-i</c> input
/// (already downloaded to a local scratch file, the way the caller prepares every motion-graphics
/// overlay asset); this builder only references that input through the ffmpeg input index the
/// caller resolves with <paramref name="inputIndexForIndex"/> and never touches the file itself.
/// </summary>
/// <remarks>
/// The cap applies to the frame's SHORT side (a "p" resolution; a portrait frame caps its width with
/// <c>scale=min(iw,maxHeight):-2</c>). For landscape and square the height cap is <c>scale=-2:min(ih,maxHeight)</c>: <c>-2</c> recomputes the width to preserve the
/// source aspect ratio (rounded even, as <c>scale</c> requires of H.264-family outputs) and
/// <c>min(ih,maxHeight)</c> guarantees we never UPSCALE — a source already at or below the cap is a
/// no-op the builder does not emit at all. When nothing is to scale and no watermark is requested the
/// fragment is the input label unchanged, so the caller can continue the chain from it untouched;
/// that is the only case in which the returned value is not terminated by <paramref name="finalLabel"/>.
///
/// <para>
/// The watermark is scaled to a fixed percentage of the (already-resolved) OUTPUT width and pinned to
/// <paramref name="watermarkCorner"/> by <paramref name="watermarkMargin"/> using the <c>overlay</c>
/// filter's own <c>main_w</c>/<c>main_h</c>/<c>overlay_w</c>/<c>overlay_h</c> variables, so the
/// placement stays correct at runtime regardless of the watermark's actual pixel size.
/// </para>
/// </remarks>
public static class OutputPolicyFilterBuilder
{
    /// <summary>The default maximum output height in pixels: trial (watermarked) renders are capped at 720p.</summary>
    public const int DefaultMaxHeight = 720;

    /// <summary>The default watermark corner: the ReelBolt mark is painted bottom-right.</summary>
    public static readonly WatermarkCorner DefaultCorner = WatermarkCorner.BottomRight;

    /// <summary>The default distance, in OUTPUT-frame pixels, from the nearest frame corner to the watermark's near corner.</summary>
    public const int DefaultWatermarkMargin = 24;

    /// <summary>The default watermark width as a percentage of the OUTPUT frame width.</summary>
    public const int DefaultWatermarkWidthPct = 15;

    /// <summary>
    /// Builds the filter-chain fragment for the given output policy. See the type remarks for the
    /// full contract. Returns the <paramref name="baseFilterChainEndLabel"/> unchanged only when the
    /// cap is a no-op AND no watermark is requested; otherwise returns a <c>;</c>-joined chain that
    /// consumes the base label and ends at <paramref name="finalLabel"/>.
    /// </summary>
    public static string BuildFilterChain(
        string baseFilterChainEndLabel,
        int probedWidth,
        int probedHeight,
        int maxHeight = DefaultMaxHeight,
        bool applyWatermark = false,
        WatermarkCorner watermarkCorner = WatermarkCorner.BottomRight,
        int watermarkMargin = DefaultWatermarkMargin,
        int watermarkWidthPct = DefaultWatermarkWidthPct,
        Func<int, int>? inputIndexForIndex = null,
        string finalLabel = "[vout]")
    {
        if (baseFilterChainEndLabel is null)
            throw new ArgumentNullException(nameof(baseFilterChainEndLabel));
        if (probedWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(probedWidth), "probedWidth must be positive.");
        if (probedHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(probedHeight), "probedHeight must be positive.");
        if (maxHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxHeight), "maxHeight must be positive.");
        if (watermarkWidthPct is <= 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(watermarkWidthPct), "watermarkWidthPct must be in the open-closed range (0, 100].");
        if (watermarkMargin < 0)
            throw new ArgumentOutOfRangeException(nameof(watermarkMargin), "watermarkMargin must be >= 0.");
        if (applyWatermark && inputIndexForIndex is null)
            throw new ArgumentNullException(nameof(inputIndexForIndex), "inputIndexForIndex is required when applyWatermark is true.");

        // The cap is a "p" resolution (720p, 1080p), which names the SHORT side of the frame: a 9:16 reel
        // at 1080p is 1080x1920. Capping the height regardless of orientation turned a full-quality
        // 1080x1920 reel into 608x1080 (staging run 98e57ffd).
        bool portrait = probedHeight > probedWidth;
        bool capIsNoOp = !CapApplies(probedWidth, probedHeight, maxHeight);

        // The one case the caller short-circuits on: the frame already meets the cap and there is
        // nothing to composite. Hand the label straight back so the caller continues the chain from
        // it unchanged — the only path that does not terminate at finalLabel.
        if (capIsNoOp && !applyWatermark)
            return baseFilterChainEndLabel;

        var segments = new List<string>(3);
        string currentLabel = baseFilterChainEndLabel;

        // (a) Cap the OUTPUT height. -2 keeps the width aspect-correct and even; min(ih,maxHeight)
        // never upscales. Only emitted when the cap actually does something (capIsNoOp is false).
        if (!capIsNoOp)
        {
            string cappedLabel = applyWatermark ? "[vcap]" : finalLabel;
            segments.Add(portrait
                ? $"{currentLabel}scale='min(iw,{FfmpegArgvFormat.Number(maxHeight)})':-2:flags=lanczos{cappedLabel}"
                : $"{currentLabel}scale=-2:'min(ih,{FfmpegArgvFormat.Number(maxHeight)})':flags=lanczos{cappedLabel}");
            currentLabel = cappedLabel;
        }

        // (b) Optionally composite the watermark on top of the (capped) frame. The caller has
        // already added the watermark image as its own extra -i input; we reference it by the
        // input index it resolves for us.
        if (applyWatermark)
        {
            int inputIndex = inputIndexForIndex!(0);
            int outputWidth = OutputWidth(probedWidth, probedHeight, maxHeight);
            int wmWidth = WatermarkWidth(outputWidth, watermarkWidthPct);

            // Pin alpha BEFORE scaling — the same negotiation gotcha OverlayAssetFilterBuilder
            // documents: the watermark is expected to be a transparent-background image, and
            // forcing rgba before scale stops libavfilter from silently negotiating a non-alpha
            // format that would composite as an opaque block. width is the fixed fraction of the
            // OUTPUT width; -1 derives the height to preserve the watermark's own aspect ratio.
            string wmLabel = "[wm]";
            segments.Add(
                $"[{inputIndex}:v]format=rgba,scale={FfmpegArgvFormat.Number(wmWidth)}:-1:flags=bilinear{wmLabel}");

            (string x, string y) = OverlayExpression(watermarkCorner, watermarkMargin);
            segments.Add($"{currentLabel}{wmLabel}overlay=x='{x}':y='{y}'{finalLabel}");
        }

        return string.Join(";", segments);
    }

    /// <summary>
    /// Whether the cap does anything to a frame of this size: the cap is a "p" resolution, which names
    /// the frame's SHORT side. The one rule every caller deciding whether to invoke
    /// <see cref="BuildFilterChain"/> must share with it (see <see cref="ResolvedOutputPolicy.CapsAt"/>).
    /// </summary>
    public static bool CapApplies(int width, int height, int maxHeight) => Math.Min(width, height) > maxHeight;

    /// <summary>The height the emitted scale stage produces — the cap for a landscape frame, the aspect-scaled (even) height for a portrait one.</summary>
    internal static int OutputHeight(int probedWidth, int probedHeight, int maxHeight)
    {
        if (!CapApplies(probedWidth, probedHeight, maxHeight))
            return probedHeight;
        if (probedHeight <= probedWidth)
            return maxHeight;

        double raw = (double)probedHeight * maxHeight / probedWidth;
        int h = (int)Math.Round(raw, MidpointRounding.AwayFromZero);
        if (h % 2 != 0)
            h += 1; // scale=...:-2 rounds the auto height to an even value
        return Math.Max(2, h);
    }

    /// <summary>
    /// The width (in pixels) the (already capped) output frame will have, mirroring the
    /// <c>scale=-2:min(ih,maxHeight)</c> computation: aspect-preserved and even when a cap applies,
    /// otherwise the source width verbatim. Used only to size the watermark as a percentage of the
    /// frame; it never appears in the emitted filtergraph directly.
    /// </summary>
    internal static int OutputWidth(int probedWidth, int probedHeight, int maxHeight)
    {
        if (!CapApplies(probedWidth, probedHeight, maxHeight))
            return probedWidth;

        // Portrait: the short side is the width, capped directly.
        if (probedHeight > probedWidth)
            return Math.Min(probedWidth, maxHeight);

        double raw = (double)probedWidth * maxHeight / probedHeight;
        int w = (int)Math.Round(raw, MidpointRounding.AwayFromZero);
        if (w % 2 != 0)
            w += (w > 0 ? 1 : -1); // scale=-2 rounds the auto width to an even value
        return Math.Max(2, w);
    }

    /// <summary>
    /// The watermark's scale target width: <paramref name="watermarkWidthPct"/> percent of the
    /// output width, never below one pixel.
    /// </summary>
    internal static int WatermarkWidth(int outputWidth, int watermarkWidthPct) =>
        Math.Max(1, (int)Math.Round(outputWidth * (double)watermarkWidthPct / 100.0));

    /// <summary>
    /// The <c>overlay</c> x/y expressions that pin the watermark's NEAR corner to
    /// <paramref name="corner"/> by <paramref name="margin"/>. Both use the overlay filter's own
    /// <c>main_w</c>/<c>main_h</c>/<c>overlay_w</c>/<c>overlay_h</c> variables, so the placement is
    /// correct at runtime regardless of the watermark's actual pixel size — this builder stays pure.
    /// </summary>
    internal static (string X, string Y) OverlayExpression(WatermarkCorner corner, int margin)
    {
        string m = FfmpegArgvFormat.Number(margin);
        return corner switch
        {
            WatermarkCorner.TopLeft => (m, m),
            WatermarkCorner.TopRight => ($"main_w-overlay_w-{m}", m),
            WatermarkCorner.BottomLeft => (m, $"main_h-overlay_h-{m}"),
            _ => ($"main_w-overlay_w-{m}", $"main_h-overlay_h-{m}"), // BottomRight (default)
        };
    }
}
