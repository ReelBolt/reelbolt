using System.Globalization;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// A single fully-resolved motion-graphics overlay, ready to be rendered into ffmpeg filter
/// syntax by <see cref="DrawtextFilterBuilder"/>. Every field here is already a safe, concrete
/// value — sanitized text (<see cref="OverlayTextSanitizer"/>), server-resolved output-timeline
/// seconds (<c>VideoCompileStepExecutor.MapSourceWindowToOutput</c>), and a fixed enum word for
/// duration/emphasis already mapped to milliseconds/style by the caller. The model never supplied
/// any of the geometry/timing here directly.
/// </summary>
public sealed record ResolvedOverlay(
    string PlacementId,
    string Kind,
    string SanitizedText,
    string SanitizedSubtext,
    int DurationMs,
    string Emphasis,
    double OutputStartSec,
    double OutputEndSec,
    VideoAnalysisRect Rect,
    string TextColor,
    // Phase 3 rendered-asset overlay path (docs/video-editing.md "Motion graphics (Phase 3)"):
    // non-null only for an overlay whose MotionGraphicsOverlay.RenderedAssetStorageKey was
    // validated (execution-scoped storage-key prefix) and successfully downloaded+probed by
    // VideoCompileStepExecutor.ResolveGraphicsAsync — the local scratch-space path of that
    // downloaded asset. When set, this overlay is composited by OverlayAssetFilterBuilder (the
    // ffmpeg `overlay` filter) instead of DrawtextFilterBuilder; SanitizedText/SanitizedSubtext
    // are always empty for an asset overlay (see IsAssetOverlay).
    string? RenderedAssetLocalPath = null,
    /// <summary>
    /// The owning placement's named region ("LowerThird" / "UpperThird" / "CenterBand" — see
    /// VideoAnalysisPlacement.Region), used only by <see cref="DrawtextFilterBuilder.ComputeAccentBoxPixels"/>
    /// to decide which edge of the named band to anchor the (deliberately smaller) drawn box
    /// against. Distinct from <see cref="Kind"/> (the model's own descriptive-only
    /// LowerThird/Title/Callout/Tag choice, which happens to share some of the same words but is
    /// never read for geometry — see MotionGraphicsOverlay.Kind's doc comment). Defaults to ""
    /// (treated as CenterBand-style vertical centering) so existing callers that never set it keep
    /// compiling and behaving predictably.
    /// </summary>
    string PlacementRegion = "",
    /// <summary>
    /// The probed <c>VideoCodec</c> of <see cref="RenderedAssetLocalPath"/> (e.g. <c>"vp9"</c>),
    /// null when there is no asset. VP9-in-WebM alpha is a side channel ffmpeg's default decoder
    /// selection doesn't always honor — the caller uses this to force <c>-c:v libvpx-vp9</c> on
    /// this specific input before adding it, rather than trusting stream-copy-style auto-detection
    /// to preserve a transparency that ffprobe itself only reports via
    /// <see cref="MediaProbeResult.AlphaModeTag"/>, not <c>pix_fmt</c>. See
    /// <see cref="AlphaPixelFormats"/>'s remarks for the full story of why pix_fmt alone is not
    /// trustworthy for this codec/container pairing.
    /// </summary>
    string? RenderedAssetVideoCodec = null,
    /// <summary>
    /// The probed pixel size of <see cref="RenderedAssetLocalPath"/> (0 when there is no asset, or
    /// on a caller that never set it). <c>OverlayAssetFilterBuilder</c> places an asset at its own
    /// aspect ratio from these — see <see cref="DrawtextFilterBuilder.ComputeAssetBoxPixels"/>.
    /// </summary>
    int RenderedAssetWidth = 0,
    int RenderedAssetHeight = 0,
    /// <summary>
    /// The plan's <c>assetCanvas</c> word (docs/design/graphics-see-and-size.md): "Output" composites
    /// the asset full frame at its designed size (<see cref="DrawtextFilterBuilder.ComputeCanvasAssetBoxPixels"/>);
    /// anything else keeps the band fit.
    /// </summary>
    string AssetCanvas = "",
    /// <summary>The cap height the plan's <c>textSize</c> word stands for (floor applied), in percent of the frame; null keeps the configured size.</summary>
    double? TextCapHeightPct = null,
    /// <summary>The plan's <c>anchor</c> word for a drawn overlay ("Top"/"Center"/"Bottom"); "" keeps the region's own edge.</summary>
    string Anchor = "")
{
    /// <summary>True when this overlay carries a resolved, locally-downloaded rendered asset and must be composited via <c>OverlayAssetFilterBuilder</c> rather than <c>DrawtextFilterBuilder</c>.</summary>
    public bool IsAssetOverlay => !string.IsNullOrEmpty(RenderedAssetLocalPath);
}

/// <summary>
/// Builds the drawbox/drawtext filter-chain fragment appended after the existing select/setpts
/// cut stage when <c>VideoCompileStepConfig.EnableGraphics</c> is on (see docs/video-editing.md
/// "Motion graphics (Phase 3)"). Pure — no I/O, no ffmpeg — the caller writes each overlay's
/// sanitized text to its own scratch file (see <see cref="MainTextSlot"/>/<see cref="SubtextSlot"/>)
/// BEFORE invoking this, and only ever passes this builder a <c>textFilePathForIndex</c> callback
/// to resolve those paths into the filter string.
/// </summary>
/// <remarks>
/// <b>Text never appears in the returned filter string.</b> Each drawtext filter references its
/// text via <c>textfile=</c> (a scratch file path — a path is not the text itself), never via an
/// inline <c>text=</c> value, and every drawtext filter also carries <c>expansion=none</c> to
/// disable drawtext's own <c>%{...}</c> expansion syntax as defense-in-depth. All geometry/timing
/// numbers are computed here in C# from already-validated inputs (a probed frame size, a resolved
/// output-timeline window, a normalized safe-zone rect) and formatted via
/// <see cref="FfmpegArgvFormat.Number(double)"/> — culture-invariant, exactly like
/// <c>VideoCompileStepExecutor.EncodeReencodeAsync</c>'s own <c>BetweenTerms()</c> helper.
/// </remarks>
public static class DrawtextFilterBuilder
{
    /// <summary>The scratch-file "slot" index for overlay <paramref name="overlayIndex"/>'s main text file — the single source of truth both this builder and its caller must use.</summary>
    public static int MainTextSlot(int overlayIndex) => overlayIndex * 2;

    /// <summary>The scratch-file "slot" index for overlay <paramref name="overlayIndex"/>'s subtext file (only used when <see cref="ResolvedOverlay.SanitizedSubtext"/> is non-empty).</summary>
    public static int SubtextSlot(int overlayIndex) => overlayIndex * 2 + 1;

    /// <summary>
    /// Builds the filter-chain fragment for <paramref name="overlays"/> (must be non-empty — the
    /// caller keeps the existing zero-overlay label plumbing unchanged when there is nothing to
    /// draw). Starts from <paramref name="baseFilterChainEndLabel"/> (the cut stage's own output
    /// label, e.g. <c>"[vcut]"</c>) and ends at <paramref name="finalLabel"/> (default
    /// <c>"[vout]"</c>, which the caller's existing <c>-map "[vout]"</c> continues to reference
    /// unchanged when there is no further stage). Phase 3's rendered-asset overlay path
    /// (<see cref="ResolvedOverlay.RenderedAssetLocalPath"/>, applied by a separate
    /// <c>OverlayAssetFilterBuilder</c> stage) passes a non-default <paramref name="finalLabel"/>
    /// (e.g. <c>"[vtxt]"</c>) when BOTH text and asset overlays are present in the same compile,
    /// so the asset stage can chain after this one and become the actual <c>[vout]</c> itself —
    /// see <c>VideoCompileStepExecutor.EncodeReencodeAsync</c>.
    /// </summary>
    public static string BuildFilterChain(
        string baseFilterChainEndLabel,
        IReadOnlyList<ResolvedOverlay> overlays,
        int probedWidth,
        int probedHeight,
        int fontSizePct,
        int fadeMs,
        string fontColor,
        string boxColor,
        string? fontFilePath,
        Func<int, string> textFilePathForIndex,
        string finalLabel = "[vout]",
        int boxHeightPct = 16,
        int boxWidthPct = 82,
        double minCapHeightPct = 0)
    {
        if (overlays.Count == 0)
            throw new ArgumentException("BuildFilterChain requires at least one overlay.", nameof(overlays));

        double fadeSec = Math.Max(0.01, fadeMs) / 1000.0;

        List<string> segments = new();
        string currentLabel = baseFilterChainEndLabel;

        for (int i = 0; i < overlays.Count; i++)
        {
            ResolvedOverlay overlay = overlays[i];
            bool isLast = i == overlays.Count - 1;
            bool hasSubtext = overlay.SanitizedSubtext.Length > 0;

            // Emphasis (Subtle/Normal/Strong) is the one MotionGraphicsOverlay field the model
            // controls that previously did nothing downstream — see docs/video-editing.md "Motion
            // graphics (Phase 3)". A small, deliberately simple effect: it nudges the per-overlay
            // font size, computed fresh per overlay (not once for the whole chain) since different
            // overlays in the same compile can carry different emphasis.
            int fontSize = ComputeFontSize(probedHeight, fontSizePct, overlay.Emphasis, overlay.TextCapHeightPct, minCapHeightPct);
            int subFontSize = Math.Max(10, (int)Math.Round(fontSize * 0.7));

            (int boxX, int boxY, int boxW, int boxH) = ComputeAccentBoxPixels(
                overlay.Rect, overlay.PlacementRegion, probedWidth, probedHeight, boxHeightPct, boxWidthPct, overlay.Anchor);
            string start = FfmpegArgvFormat.Number(overlay.OutputStartSec);
            string end = FfmpegArgvFormat.Number(overlay.OutputEndSec);
            string enableExpr = $"between(t,{start},{end})";
            string alphaExpr = BuildAlphaExpression(start, end, fadeSec);

            // "none" means skip the drawbox entirely — chain drawtext directly off the previous
            // stage's label instead of introducing a [gfx{i}b] box label (see docs/video-editing.md
            // "Motion graphics (Phase 3)").
            bool skipBox = string.Equals(boxColor, "none", StringComparison.OrdinalIgnoreCase);
            string boxLabel = currentLabel;
            if (!skipBox)
            {
                boxLabel = $"[gfx{i}b]";
                segments.Add(
                    $"{currentLabel}drawbox=x={boxX}:y={boxY}:w={boxW}:h={boxH}:color={boxColor}:t=fill:" +
                    $"enable='{enableExpr}'{boxLabel}");
            }

            string mainTextPath = textFilePathForIndex(MainTextSlot(i));
            string mainLabel = !hasSubtext && isLast ? finalLabel : hasSubtext ? $"[gfx{i}t]" : $"[gfx{i}]";

            string mainY = hasSubtext
                ? $"{boxY}+({boxH}/2-text_h)/2"
                : $"{boxY}+({boxH}-text_h)/2";

            segments.Add(
                $"{boxLabel}drawtext={FontFileOption(fontFilePath)}" +
                $"textfile='{EscapeFilterPath(mainTextPath)}':expansion=none:fontsize={fontSize}:" +
                $"fontcolor={fontColor}:x={boxX}+({boxW}-text_w)/2:y={mainY}:" +
                $"alpha='{alphaExpr}':enable='{enableExpr}'{mainLabel}");

            currentLabel = mainLabel;

            if (hasSubtext)
            {
                string subTextPath = textFilePathForIndex(SubtextSlot(i));
                string subLabel = isLast ? finalLabel : $"[gfx{i}]";
                string subY = $"{boxY}+{boxH}/2+({boxH}/2-text_h)/2";

                segments.Add(
                    $"{currentLabel}drawtext={FontFileOption(fontFilePath)}" +
                    $"textfile='{EscapeFilterPath(subTextPath)}':expansion=none:fontsize={subFontSize}:" +
                    $"fontcolor={fontColor}:x={boxX}+({boxW}-text_w)/2:y={subY}:" +
                    $"alpha='{alphaExpr}':enable='{enableExpr}'{subLabel}");

                currentLabel = subLabel;
            }
        }

        return string.Join(";", segments);
    }

    /// <summary>
    /// <c>max(12, round(probedHeight * effectivePct / 100))</c> — never below 12px regardless of a
    /// tiny/misconfigured percentage. <paramref name="emphasis"/> ("Subtle"/"Normal"/"Strong")
    /// scales <paramref name="fontSizePct"/> by 0.8x/1x/1.25x respectively before clamping the
    /// result to the same valid percentage range (<c>[2, 12]</c>) the unscaled value is clamped
    /// to — so Strong/Subtle nudge the size within the existing bounds rather than escaping them.
    /// Any value other than the three recognized words is treated as "Normal" (no scaling).
    /// </summary>
    internal static int ComputeFontSize(
        int probedHeight, int fontSizePct, string emphasis, double? textCapHeightPct, double minCapHeightPct)
    {
        // A textSize word (docs/design/graphics-see-and-size.md) replaces the configured size; the
        // floor then raises anything whose capitals would come out smaller than minCapHeightPct.
        int size = textCapHeightPct is { } cap && cap > 0
            ? Math.Max(12, OverlayTextSizing.FontPxForCap(probedHeight, cap))
            : ComputeFontSize(probedHeight, fontSizePct, emphasis);
        return minCapHeightPct > 0
            ? Math.Max(size, OverlayTextSizing.FontPxForCap(probedHeight, OverlayTextSizing.ClampMinCap(minCapHeightPct)))
            : size;
    }

    internal static int ComputeFontSize(int probedHeight, int fontSizePct, string emphasis = "Normal")
    {
        double multiplier = emphasis switch
        {
            "Strong" => 1.25,
            "Subtle" => 0.8,
            _ => 1.0
        };
        double effectivePct = Math.Clamp(fontSizePct * multiplier, 2, 12);
        return Math.Max(12, (int)Math.Round(probedHeight * effectivePct / 100.0));
    }

    /// <summary>
    /// The actual on-screen box a motion-graphics overlay (drawbox/drawtext OR a stretch-scaled
    /// rendered asset — both DrawtextFilterBuilder and OverlayAssetFilterBuilder call this) is
    /// drawn into. A named overlay-safe-zone band (<c>rect</c> — see
    /// <c>OverlayPlacementBuilder</c>/<c>FrameGridAnalyzer</c>) is a SAFE ZONE for placement, not a
    /// target size to fill: <c>LowerThird</c>/<c>UpperThird</c> are literally the frame's outer
    /// third (full width, ~33% of frame height), and naively stretch-filling that whole band is
    /// what made a rendered overlay visibly "block the whole screen" in practice (root-caused against a real
    /// 1440x2558 output; see docs/video-editing.md "Motion graphics (Phase 3)"). This method instead
    /// computes a compact ACCENT box: capped to <paramref name="boxHeightPct"/> percent of the
    /// FULL FRAME height (never exceeding the named band's own height, so it still sits inside the
    /// safe zone it was offered), and inset horizontally to <paramref name="boxWidthPct"/> percent
    /// of the band's own width so it is never full-bleed edge-to-edge either. The box is anchored
    /// to whichever edge of the band reads as a natural lower-third/upper-third accent strip
    /// (<paramref name="region"/> — "LowerThird" hugs the band's bottom edge, "UpperThird" hugs its
    /// top edge; any other/unrecognized region, including "CenterBand", is vertically centered
    /// within the band) and horizontally centered.
    /// </summary>
    internal static (int X, int Y, int W, int H) ComputeAccentBoxPixels(
        VideoAnalysisRect rect, string region, int probedWidth, int probedHeight,
        int boxHeightPct = 16, int boxWidthPct = 82, string anchor = "")
    {
        int bandX = (int)Math.Round(rect.X * probedWidth);
        int bandY = (int)Math.Round(rect.Y * probedHeight);
        int bandW = (int)Math.Round(rect.W * probedWidth);
        int bandH = (int)Math.Round(rect.H * probedHeight);

        double heightFraction = Math.Clamp(boxHeightPct, 6, 40) / 100.0;
        double widthFraction = Math.Clamp(boxWidthPct, 30, 100) / 100.0;

        int accentH = Math.Max(1, Math.Min(Math.Max(1, bandH), (int)Math.Round(probedHeight * heightFraction)));
        int accentW = Math.Max(1, (int)Math.Round(bandW * widthFraction));

        int x = bandX + (bandW - accentW) / 2;
        // The plan's anchor word, when given, decides the edge; else the region's own edge.
        string edge = anchor switch
        {
            "Top" => "UpperThird",
            "Bottom" => "LowerThird",
            "Center" => "CenterBand",
            _ => region
        };
        int y = edge switch
        {
            "LowerThird" => bandY + bandH - accentH,
            "UpperThird" => bandY,
            _ => bandY + (bandH - accentH) / 2
        };

        return (x, y, accentW, accentH);
    }

    /// <summary>
    /// A fixed fade-in/hold/fade-out template: 0 before <c>start</c>, linear ramp 0→1 over
    /// <paramref name="fadeSec"/>, held at 1, linear ramp 1→0 over the last <paramref name="fadeSec"/>
    /// before <c>end</c>, 0 after. All literals are pre-formatted, culture-invariant strings.
    /// </summary>
    /// <summary>
    /// The width rendered overlay assets are authored against: a Remotion graphic sized for a
    /// 1920x1080 frame is placed at that same size on a 1920-wide output (and proportionally on
    /// any other width).
    /// </summary>
    internal const int AssetReferenceCanvasWidth = 1920;

    /// <summary>
    /// The pixel box a RENDERED ASSET overlay is composited into. Unlike
    /// <see cref="ComputeAccentBoxPixels"/>, which sizes a drawn text box, this never changes the
    /// asset's aspect ratio: the asset keeps the size it was designed at (relative to
    /// <see cref="AssetReferenceCanvasWidth"/>) and is only ever scaled DOWN, uniformly, to fit its
    /// placement's named band. It then sits centred horizontally, near the bottom of a LowerThird,
    /// near the top of an UpperThird, centred in a CenterBand — always inside a title-safe margin.
    /// </summary>
    /// <remarks>
    /// Assets used to be stretched into the accent box, a fixed ~9:1 strip, whatever their own
    /// shape. A CenterBand card rendered at the 4:1 its prompt asked for came out at half its
    /// height and full width — text visibly squashed. Returns null when the asset size is
    /// unknown, so a caller can fall back to the accent box.
    /// </remarks>
    internal static (int X, int Y, int W, int H)? ComputeAssetBoxPixels(
        VideoAnalysisRect rect, string region, int probedWidth, int probedHeight, int assetWidth, int assetHeight)
    {
        if (assetWidth <= 0 || assetHeight <= 0 || probedWidth <= 0 || probedHeight <= 0)
            return null;

        int bandX = (int)Math.Round(rect.X * probedWidth);
        int bandY = (int)Math.Round(rect.Y * probedHeight);
        int bandW = Math.Max(1, (int)Math.Round(rect.W * probedWidth));
        int bandH = Math.Max(1, (int)Math.Round(rect.H * probedHeight));

        // Title-safe: 5% of the frame on each side, never more than the band itself can give up.
        int safeX = (int)Math.Round(probedWidth * 0.05);
        int safeY = (int)Math.Round(probedHeight * 0.05);
        int availW = Math.Max(1, Math.Min(bandW, probedWidth - 2 * safeX));
        int availH = Math.Max(1, bandH - (region is "LowerThird" or "UpperThird" ? safeY : 0));

        double scale = probedWidth / (double)AssetReferenceCanvasWidth;
        scale = Math.Min(scale, availW / (double)assetWidth);
        scale = Math.Min(scale, availH / (double)assetHeight);

        int w = Math.Max(2, (int)Math.Round(assetWidth * scale));
        int h = Math.Max(2, (int)Math.Round(assetHeight * scale));
        w -= w % 2;
        h -= h % 2;

        int x = bandX + (bandW - w) / 2;
        int y = region switch
        {
            "LowerThird" => bandY + bandH - safeY - h,
            "UpperThird" => bandY + safeY,
            _ => bandY + (bandH - h) / 2
        };
        y = Math.Clamp(y, bandY, Math.Max(bandY, bandY + bandH - h));

        return (x, y, w, h);
    }

    /// <summary>
    /// The pixel box an <c>assetCanvas: "Output"</c> asset is composited into
    /// (docs/design/graphics-see-and-size.md): the asset was rendered on the output canvas, so it is
    /// drawn full frame: exactly its own size when it matches the frame, else fitted uniformly
    /// (never stretched) and centred. The compile records the scale this implies, never silently.
    /// Null when either size is unknown.
    /// </summary>
    internal static (int X, int Y, int W, int H)? ComputeCanvasAssetBoxPixels(
        int probedWidth, int probedHeight, int assetWidth, int assetHeight)
    {
        if (assetWidth <= 0 || assetHeight <= 0 || probedWidth <= 0 || probedHeight <= 0)
            return null;
        if (assetWidth == probedWidth && assetHeight == probedHeight)
            return (0, 0, probedWidth, probedHeight);

        double scale = Math.Min(probedWidth / (double)assetWidth, probedHeight / (double)assetHeight);
        int w = Math.Max(2, (int)Math.Round(assetWidth * scale));
        int h = Math.Max(2, (int)Math.Round(assetHeight * scale));
        w -= w % 2;
        h -= h % 2;
        return ((probedWidth - w) / 2, (probedHeight - h) / 2, w, h);
    }

    internal static string BuildAlphaExpression(string start, string end, double fadeSec)
    {
        string fade = fadeSec.ToString(CultureInfo.InvariantCulture);
        return
            $"if(lt(t,{start}),0," +
            $"if(lt(t,{start}+{fade}),(t-{start})/{fade}," +
            $"if(lt(t,{end}-{fade}),1," +
            $"if(lt(t,{end}),({end}-t)/{fade},0))))";
    }

    /// <summary>
    /// The <c>fontfile='...':</c> option of a drawtext filter, or nothing when <paramref name="fontFilePath"/> is
    /// empty: drawtext then falls back to fontconfig's default font. Used for a remote (runner) session, where an
    /// engine-host font path is not a file the session can read.
    /// </summary>
    public static string FontFileOption(string? fontFilePath) =>
        string.IsNullOrEmpty(fontFilePath) ? string.Empty : $"fontfile='{EscapeFilterPath(fontFilePath)}':";

    /// <summary>
    /// The engine's font path for a drawtext filter run in <paramref name="workspace"/>: the path itself locally, null
    /// in a remote (runner) session, whose host does not have the engine's files (security T-326).
    /// </summary>
    public static string? EngineFontFor(IMediaWorkspace workspace, string fontFilePath) =>
        workspace.IsRemoteSession ? null : fontFilePath;

    /// <summary>
    /// Escapes a PATH for embedding inside a SINGLE-QUOTED ffmpeg filter option value (every call
    /// site here wraps the result in <c>'...'</c>, e.g. <c>fontfile='{EscapeFilterPath(path)}'</c>)
    /// — never used for overlay text, which never appears here at all. Paths originate from our
    /// own scratch-space naming, never from model output, but are escaped anyway since a project
    /// name or execution id could in principle contain one of these characters on some platform.
    /// </summary>
    /// <remarks>
    /// ffmpeg's filtergraph parser does NOT process backslash escapes inside a single-quoted
    /// value — everything between the quotes is literal except the closing quote itself. That
    /// means, for a value already wrapped in single quotes:
    /// <list type="bullet">
    /// <item><c>:</c> needs no escaping — the surrounding quotes already protect it from being
    /// read as the option separator.</item>
    /// <item><c>\</c> needs no escaping either — it has no special meaning inside single quotes,
    /// so a literal backslash in the path passes through unchanged.</item>
    /// <item><c>'</c> is the one character that DOES need handling: since a single-quoted value
    /// cannot contain a literal quote directly, the standard (shell-like) idiom is used — close
    /// the quote, insert an escaped literal quote via a backslash OUTSIDE any quotes
    /// (<c>\'</c>), then reopen a new quoted segment: <c>'</c> becomes <c>'\''</c>. ffmpeg
    /// concatenates adjacent quoted/escaped segments, so this reconstructs the literal quote
    /// correctly (see <c>DrawtextFilterBuilderTests</c> for a worked example).</item>
    /// </list>
    /// A previous version of this method escaped <c>:</c>/<c>\</c> with a bare backslash and
    /// <c>'</c> with <c>\'</c> — none of which ffmpeg's single-quoted-value parser actually
    /// processes, so a path containing a literal quote would have broken out of the quoted value
    /// instead of being escaped. Harmless in practice (real scratch paths are
    /// <c>{ScratchPath}/{guid}/{guid}/ov-N.txt</c>, which never contain any of these characters),
    /// but incorrect in isolation — fixed here regardless.
    /// </remarks>
    internal static string EscapeFilterPath(string path) =>
        path.Replace("'", "'\\''");
}
