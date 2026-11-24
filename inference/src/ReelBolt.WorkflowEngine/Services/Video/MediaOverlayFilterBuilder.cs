using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// A single fully-resolved picture/video overlay, ready to be rendered into ffmpeg filter syntax by
/// <see cref="MediaOverlayFilterBuilder"/>. Every field is already a safe, first-party value: the
/// caller has resolved the input to a local scratch file, mapped the window onto the OUTPUT clock,
/// computed the inset box with <see cref="MediaOverlayFilterBuilder.ComputeInsetBox"/> and turned
/// the opacity percentage into 0..1. No field here ever comes from a model.
/// </summary>
/// <param name="Token">A diagnostics-only name for the overlay (never interpolated into the graph); the filtergraph labels are generated from the overlay's index.</param>
/// <param name="Form">A full-frame card or an anchored inset.</param>
/// <param name="InputIndex">The overlay's ffmpeg input position, recorded by the caller for diagnostics.</param>
/// <param name="BoxX">The inset content box's left edge in canvas pixels (ignored for a card).</param>
/// <param name="BoxY">The inset content box's top edge in canvas pixels (ignored for a card).</param>
/// <param name="BoxW">The inset content box's width in canvas pixels (ignored for a card).</param>
/// <param name="BoxH">The inset content box's height in canvas pixels (ignored for a card).</param>
/// <param name="Opacity">The overlay's alpha, 0..1.</param>
/// <param name="Border">An inset's hairline border; ignored by a card.</param>
/// <param name="OutputStartSec">Output-timeline start in seconds.</param>
/// <param name="OutputEndSec">Output-timeline end in seconds.</param>
/// <param name="EndsEarly">Whether the content is shorter than its window and stops on <c>eof_action=pass</c> (reported only).</param>
/// <param name="ContentName">The content's display name for diagnostics (never used in the filter).</param>
public sealed record ResolvedMediaOverlay(
    string Token,
    MediaOverlayForm Form,
    int InputIndex,
    int BoxX, int BoxY, int BoxW, int BoxH,
    double Opacity,
    MediaOverlayBorder Border,
    double OutputStartSec, double OutputEndSec,
    bool EndsEarly,
    string? ContentName);

/// <summary>
/// Builds the ffmpeg <c>overlay</c> filter-chain segments for pictures and videos over the film
/// (see docs/design/picture-overlay.md §6.1). Pure — no I/O, no async, no DI, no ffprobe: the caller
/// has already downloaded each overlay to a local scratch file, added it as its own extra <c>-i</c>
/// input, and supplies that input's position through <c>inputIndexForIndex</c>. All numbers reach
/// the filtergraph through <see cref="FfmpegArgvFormat.Number"/> (invariant culture).
/// </summary>
/// <remarks>
/// <para>
/// A <b>card</b> fills the frame: scaled with <c>force_original_aspect_ratio=decrease</c>, centred on
/// black and composited at <c>0:0</c>. An <b>inset</b> is scaled to its resolved content box, a
/// hairline border is padded around it (drawn into the overlay input so it scales with the content),
/// and it is composited at the anchor position the caller resolved.
/// </para>
/// <para>
/// <c>format=rgba</c> always precedes <c>scale</c>. Pinning the pixel format before libavfilter's
/// negotiation runs is the same alpha-negotiation fix <see cref="OverlayAssetFilterBuilder"/> and
/// <see cref="OutputPolicyFilterBuilder"/> document: the decoded input may carry alpha in any of
/// several layouts, and a scale/overlay pair can otherwise silently negotiate a non-alpha common
/// format and composite as an opaque block.
/// </para>
/// <para>
/// The chain consumes <c>baseLabel</c>, emits two segments per overlay (the input preparation then
/// the composite) in the overlays' own order, and ends at <c>finalLabel</c>. Every label is generated
/// from the overlay's index (<c>[mosrc{i}]</c>, <c>[mo{i}]</c>); <see cref="ResolvedMediaOverlay.Token"/>
/// is diagnostics only and never reaches the graph. The two caller-supplied program labels are
/// validated against <c>[A-Za-z][A-Za-z0-9_]*</c>. An empty overlay list throws — the caller never
/// calls the builder with nothing to draw.
/// </para>
/// </remarks>
public static class MediaOverlayFilterBuilder
{
    /// <summary>
    /// The program frame rate an overlay input is normalized to before the composite when the caller
    /// names none. The compile passes the program's real rate; this is only the builder's default.
    /// </summary>
    public const int DefaultFpsNum = 30;

    /// <summary>The denominator of <see cref="DefaultFpsNum"/>.</summary>
    public const int DefaultFpsDen = 1;

    /// <summary>The sample rate every mixed overlay's audio is normalized to (§5.5), matching the program mix.</summary>
    public const int ProgramAudioSampleRate = 48000;

    /// <summary>The channel layout every mixed overlay's audio is normalized to (§5.5), matching the program mix.</summary>
    public const string ProgramAudioChannelLayout = "stereo";

    /// <summary>The lower bound of a mixed overlay's level, in dB (§5.5): quieter than this is clamped.</summary>
    public const double MinVolumeDb = -18;

    /// <summary>The upper bound of a mixed overlay's level, in dB (§5.5): louder than this is clamped.</summary>
    public const double MaxVolumeDb = 0;

    /// <summary>An inset's width as a fraction of the safe width, per <see cref="MediaOverlaySize"/>.</summary>
    public const double SmallWidthFraction = 0.25;

    /// <summary>An inset's width as a fraction of the safe width, per <see cref="MediaOverlaySize"/>.</summary>
    public const double MediumWidthFraction = 0.36;

    /// <summary>An inset's width as a fraction of the safe width, per <see cref="MediaOverlaySize"/>.</summary>
    public const double LargeWidthFraction = 0.50;

    /// <summary>The largest safe margin the config accepts (0..0.15 in §5.1).</summary>
    public const double MaxSafeMargin = 0.15;

    /// <summary>
    /// The hairline border's width in pixels: <c>max(2, round(canvasHeight * 0.002))</c> (2 px at
    /// 1080p), per §5.4.
    /// </summary>
    public static int BorderPixels(int canvasHeight) =>
        Math.Max(2, (int)Math.Round(canvasHeight * 0.002, MidpointRounding.AwayFromZero));

    /// <summary>
    /// Builds the filter-chain segments for <paramref name="overlays"/>. See the type remarks for the
    /// contract. The returned segments are joined with <c>;</c> by the caller.
    /// </summary>
    /// <param name="baseLabel">The label the program picture currently ends at, e.g. <c>[vcut]</c>.</param>
    /// <param name="overlays">The resolved overlays, in draw order (later ones sit on top).</param>
    /// <param name="canvasWidth">The program canvas width in pixels.</param>
    /// <param name="canvasHeight">The program canvas height in pixels.</param>
    /// <param name="inputIndexForIndex">Maps an overlay's position in <paramref name="overlays"/> to its ffmpeg input index.</param>
    /// <param name="finalLabel">The label the chain ends at; defaults to <c>[vout]</c>.</param>
    /// <param name="fpsNum">The program's frame-rate numerator; the overlay is normalized to it before the composite.</param>
    /// <param name="fpsDen">The program's frame-rate denominator.</param>
    public static IReadOnlyList<string> BuildFilterChain(
        string baseLabel,
        IReadOnlyList<ResolvedMediaOverlay> overlays,
        int canvasWidth,
        int canvasHeight,
        Func<int, int> inputIndexForIndex,
        string finalLabel = "[vout]",
        int fpsNum = DefaultFpsNum,
        int fpsDen = DefaultFpsDen)
    {
        if (baseLabel is null)
            throw new ArgumentNullException(nameof(baseLabel));
        if (overlays is null)
            throw new ArgumentNullException(nameof(overlays));
        if (overlays.Count == 0)
            throw new ArgumentException("BuildFilterChain requires at least one overlay.", nameof(overlays));
        if (inputIndexForIndex is null)
            throw new ArgumentNullException(nameof(inputIndexForIndex));
        if (canvasWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(canvasWidth), "canvasWidth must be positive.");
        if (canvasHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(canvasHeight), "canvasHeight must be positive.");
        if (fpsNum <= 0)
            throw new ArgumentOutOfRangeException(nameof(fpsNum), "fpsNum must be positive.");
        if (fpsDen <= 0)
            throw new ArgumentOutOfRangeException(nameof(fpsDen), "fpsDen must be positive.");
        ValidateLabel(baseLabel, nameof(baseLabel));
        ValidateLabel(finalLabel, nameof(finalLabel));

        (_, _, int cardW, int cardH) = ComputeCardBox(canvasWidth, canvasHeight);
        string fps = $"{FfmpegArgvFormat.Number(fpsNum)}/{FfmpegArgvFormat.Number(fpsDen)}";

        var segments = new List<string>(overlays.Count * 2);
        string currentLabel = baseLabel;

        for (int i = 0; i < overlays.Count; i++)
        {
            ResolvedMediaOverlay overlay = overlays[i];
            int inputIndex = inputIndexForIndex(i);

            // Every filtergraph label is generated from the overlay's position. No caller- or
            // model-supplied string (including ResolvedMediaOverlay.Token) ever reaches the graph:
            // a token is diagnostics only, so ']', ';' or whitespace in it cannot inject a filter.
            string sourceLabel = $"[mosrc{i}]";
            string overlayLabel = $"[mo{i}]";
            string opacity = FfmpegArgvFormat.Number(overlay.Opacity);
            string start = FfmpegArgvFormat.Number(overlay.OutputStartSec);
            string end = FfmpegArgvFormat.Number(overlay.OutputEndSec);
            // The window's own duration: caps a looping input and trims content longer than the
            // span. A PlayOnce input stops at its own end first and then eof_action=pass lets the
            // film show through (§5.5).
            string span = FfmpegArgvFormat.Number(Math.Max(0, overlay.OutputEndSec - overlay.OutputStartSec));
            string outLabel = i == overlays.Count - 1 ? finalLabel : overlayLabel;

            string source;
            int boxX;
            int boxY;
            if (overlay.Form == MediaOverlayForm.Card)
            {
                // Full frame: fit by aspect, centre on black, composite at 0:0.
                source =
                    $"[{inputIndex}:v]format=rgba,fps={fps},trim=duration={span}," +
                    $"scale={FfmpegArgvFormat.Number(cardW)}:{FfmpegArgvFormat.Number(cardH)}:force_original_aspect_ratio=decrease," +
                    $"pad={FfmpegArgvFormat.Number(cardW)}:{FfmpegArgvFormat.Number(cardH)}:(ow-iw)/2:(oh-ih)/2:black," +
                    $"colorchannelmixer=aa={opacity},setpts=PTS+{start}/TB{sourceLabel}";
                boxX = 0;
                boxY = 0;
            }
            else
            {
                // Inset: scale to the resolved content box, pad the hairline border around it, then
                // shift the padded input up/left by the border so the CONTENT lands at BoxX/BoxY.
                int border = overlay.Border == MediaOverlayBorder.None ? 0 : BorderPixels(canvasHeight);
                int padW = overlay.BoxW + 2 * border;
                int padH = overlay.BoxH + 2 * border;
                string borderColour = overlay.Border == MediaOverlayBorder.Light ? "white" : "black";
                source =
                    $"[{inputIndex}:v]format=rgba,fps={fps},trim=duration={span}," +
                    $"scale={FfmpegArgvFormat.Number(overlay.BoxW)}:{FfmpegArgvFormat.Number(overlay.BoxH)}:force_original_aspect_ratio=decrease," +
                    $"pad={FfmpegArgvFormat.Number(padW)}:{FfmpegArgvFormat.Number(padH)}:" +
                    $"{FfmpegArgvFormat.Number(border)}:{FfmpegArgvFormat.Number(border)}:{borderColour}," +
                    $"colorchannelmixer=aa={opacity},setpts=PTS+{start}/TB{sourceLabel}";
                boxX = overlay.BoxX - border;
                boxY = overlay.BoxY - border;
            }

            segments.Add(source);
            segments.Add(
                $"{currentLabel}{sourceLabel}overlay=x={FfmpegArgvFormat.Number(boxX)}:" +
                $"y={FfmpegArgvFormat.Number(boxY)}:eof_action=pass:" +
                $"enable='between(t,{start},{end})'{outLabel}");
            currentLabel = outLabel;
        }

        return segments;
    }

    /// <summary>
    /// The bounded level, in dB, a mixed overlay's audio is summed at (§5.5): Quiet −12,
    /// Balanced −6, Prominent 0, always inside <see cref="MinVolumeDb"/>..<see cref="MaxVolumeDb"/>.
    /// </summary>
    public static double VolumeDb(MediaOverlayVolume volume) => ClampVolumeDb(volume switch
    {
        MediaOverlayVolume.Quiet => -12,
        MediaOverlayVolume.Prominent => 0,
        _ => -6, // Balanced
    });

    /// <summary>Clamps a mixed overlay's level to the documented −18..0 dB bound.</summary>
    public static double ClampVolumeDb(double db) => Math.Clamp(db, MinVolumeDb, MaxVolumeDb);

    /// <summary>
    /// The audio branch for one <c>Mixed</c> video overlay (§5.5): the overlay's own audio trimmed
    /// to the window, timestamp-reset, resampled to the program's 48 kHz stereo mix, gained to
    /// <paramref name="volume"/>'s bounded level and delayed to the window's start on the output
    /// clock. The caller feeds the returned label to <see cref="BuildAudioMixStage"/>.
    /// </summary>
    /// <param name="overlay">The resolved overlay (its window supplies the trim and the delay).</param>
    /// <param name="inputIndex">The overlay's ffmpeg input position (its own audio stream).</param>
    /// <param name="volume">The bounded level to mix at.</param>
    /// <param name="outputLabel">The label the branch ends at, e.g. <c>[moa0]</c>.</param>
    public static string BuildAudioBranch(
        ResolvedMediaOverlay overlay,
        int inputIndex,
        MediaOverlayVolume volume,
        string outputLabel)
    {
        if (overlay is null)
            throw new ArgumentNullException(nameof(overlay));
        if (inputIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(inputIndex), "inputIndex must not be negative.");
        ValidateLabel(outputLabel, nameof(outputLabel));

        double start = Math.Max(0, overlay.OutputStartSec);
        double span = Math.Max(0, overlay.OutputEndSec - start);
        if (span <= 0)
            throw new ArgumentException("The overlay's output window must have a positive duration.", nameof(overlay));

        string gain = FfmpegArgvFormat.Number(VolumeDb(volume));
        string delayMs = FfmpegArgvFormat.Number((long)Math.Round(start * 1000, MidpointRounding.AwayFromZero));
        return $"[{inputIndex}:a]atrim=start=0:end={FfmpegArgvFormat.Number(span)}," +
               "asetpts=PTS-STARTPTS," +
               $"aformat=sample_rates={ProgramAudioSampleRate}:channel_layouts={ProgramAudioChannelLayout}," +
               $"volume={gain}dB,adelay={delayMs}|{delayMs}{outputLabel}";
    }

    /// <summary>
    /// The final <c>amix</c> stage that sums every mixed overlay branch onto the program mix
    /// (§5.5). <c>duration=first</c> pins the output to the program, <c>dropout_transition=0</c>
    /// keeps an ended overlay from dipping the program, and <c>normalize=0</c> preserves the
    /// program's level (the same requirement music/voiceover/SFX mix stages document).
    /// </summary>
    /// <param name="baseLabel">The program mix the overlays are summed onto.</param>
    /// <param name="overlayLabels">One label per <see cref="BuildAudioBranch"/> output.</param>
    /// <param name="finalLabel">The label the mix ends at, e.g. <c>[aovl]</c>.</param>
    public static string BuildAudioMixStage(
        string baseLabel,
        IReadOnlyList<string> overlayLabels,
        string finalLabel)
    {
        if (baseLabel is null)
            throw new ArgumentNullException(nameof(baseLabel));
        if (overlayLabels is null)
            throw new ArgumentNullException(nameof(overlayLabels));
        if (overlayLabels.Count == 0)
            throw new ArgumentException("BuildAudioMixStage requires at least one overlay label.", nameof(overlayLabels));
        ValidateLabel(baseLabel, nameof(baseLabel));
        ValidateLabel(finalLabel, nameof(finalLabel));
        foreach (string label in overlayLabels)
            ValidateLabel(label, nameof(overlayLabels));

        return $"{baseLabel}{string.Concat(overlayLabels)}" +
               $"amix=inputs={overlayLabels.Count + 1}:duration=first:dropout_transition=0:normalize=0{finalLabel}";
    }

    /// <summary>
    /// The box a card is scaled and padded to: the canvas, rounded down to even (H.264-family
    /// encoders require even dimensions).
    /// </summary>
    public static (int X, int Y, int W, int H) ComputeCardBox(int canvasWidth, int canvasHeight)
    {
        if (canvasWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(canvasWidth), "canvasWidth must be positive.");
        if (canvasHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(canvasHeight), "canvasHeight must be positive.");

        return (0, 0, EvenAtMost(canvasWidth), EvenAtMost(canvasHeight));
    }

    /// <summary>
    /// The content box an inset is scaled into: its width is the <see cref="MediaOverlaySize"/>
    /// fraction of the safe width, its height follows the content's own aspect ratio, and the pair is
    /// clamped to the safe height (with the width following) so the box ALWAYS matches the content's
    /// aspect — no letterbox is drawn inside the inset. Every coordinate and dimension is even.
    /// </summary>
    /// <param name="anchor">Where the box sits inside the safe rectangle.</param>
    /// <param name="size">The width fraction of the safe width.</param>
    /// <param name="safeMargin">Fraction of the canvas kept clear around the inset (clamped 0..0.15).</param>
    /// <param name="contentWidth">The overlay content's intrinsic width in pixels.</param>
    /// <param name="contentHeight">The overlay content's intrinsic height in pixels.</param>
    /// <param name="canvasWidth">The program canvas width in pixels.</param>
    /// <param name="canvasHeight">The program canvas height in pixels.</param>
    public static (int X, int Y, int W, int H) ComputeInsetBox(
        MediaOverlayAnchor anchor,
        MediaOverlaySize size,
        double safeMargin,
        int contentWidth,
        int contentHeight,
        int canvasWidth,
        int canvasHeight)
    {
        if (contentWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(contentWidth), "contentWidth must be positive.");
        if (contentHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(contentHeight), "contentHeight must be positive.");
        if (canvasWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(canvasWidth), "canvasWidth must be positive.");
        if (canvasHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(canvasHeight), "canvasHeight must be positive.");

        double margin = Math.Clamp(double.IsFinite(safeMargin) ? safeMargin : MaxSafeMargin, 0, MaxSafeMargin);
        (int safeW, int safeH) = ReframeFilterBuilder.SafeBox(canvasWidth, canvasHeight, margin);
        int safeX = EvenAtMost(Math.Max(0, (canvasWidth - safeW) / 2));
        int safeY = EvenAtMost(Math.Max(0, (canvasHeight - safeH) / 2));

        double fraction = size switch
        {
            MediaOverlaySize.Small => SmallWidthFraction,
            MediaOverlaySize.Large => LargeWidthFraction,
            _ => MediumWidthFraction, // Medium
        };

        double targetW = safeW * fraction;
        double targetH = targetW * contentHeight / contentWidth;
        if (targetH > safeH)
        {
            targetH = safeH;
            targetW = targetH * contentWidth / contentHeight;
        }

        int w = Math.Min(safeW, EvenNear(targetW));
        int h = Math.Min(safeH, EvenNear(targetH));

        int x = anchor switch
        {
            MediaOverlayAnchor.TopLeft or MediaOverlayAnchor.Left or MediaOverlayAnchor.BottomLeft => safeX,
            MediaOverlayAnchor.Top or MediaOverlayAnchor.Center or MediaOverlayAnchor.Bottom =>
                safeX + (safeW - w) / 2,
            _ => safeX + safeW - w, // TopRight, Right, BottomRight
        };
        int y = anchor switch
        {
            MediaOverlayAnchor.TopLeft or MediaOverlayAnchor.Top or MediaOverlayAnchor.TopRight => safeY,
            MediaOverlayAnchor.Left or MediaOverlayAnchor.Center or MediaOverlayAnchor.Right =>
                safeY + (safeH - h) / 2,
            _ => safeY + safeH - h, // BottomLeft, Bottom, BottomRight
        };

        return (x, y, w, h);
    }

    /// <summary>Rounds to the nearest even integer, preferring the upper neighbour on a tie/odd whole.</summary>
    private static int EvenNear(double value)
    {
        int rounded = Math.Max(2, (int)Math.Round(value, MidpointRounding.AwayFromZero));
        return rounded + (rounded & 1);
    }

    /// <summary>Rounds down to the nearest even integer, never below 2.</summary>
    private static int EvenAtMost(int value)
    {
        int even = value & ~1;
        return even < 2 ? 2 : even;
    }

    /// <summary>
    /// A program filtergraph label must be exactly <c>[A-Za-z][A-Za-z0-9_]*</c> wrapped in square
    /// brackets. Anything else could close the label and inject a filter, so it is refused before
    /// the string is built. The builder's own labels are generated from the overlay index and never
    /// need this check; only the two caller-supplied program labels do.
    /// </summary>
    private static void ValidateLabel(string label, string paramName)
    {
        if (label is null || label.Length < 3 || label[0] != '[' || label[^1] != ']')
            throw new ArgumentException($"'{label}' is not a filtergraph label ([A-Za-z][A-Za-z0-9_]*).", paramName);

        if (!char.IsAsciiLetter(label[1]))
            throw new ArgumentException($"'{label}' is not a filtergraph label ([A-Za-z][A-Za-z0-9_]*).", paramName);

        for (int i = 2; i < label.Length - 1; i++)
        {
            if (!char.IsAsciiLetterOrDigit(label[i]) && label[i] != '_')
                throw new ArgumentException($"'{label}' is not a filtergraph label ([A-Za-z][A-Za-z0-9_]*).", paramName);
        }
    }
}
