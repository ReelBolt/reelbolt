using System.Globalization;
using System.Text.RegularExpressions;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Automatic caption placement that keeps burned captions off the picture's own text. A bottom
/// caption over a screen recording sat on top of the app's title ("Timeline editor") because the
/// automatic position is always the lower third. For each kept shot that carries captions, one frame
/// — fitted to the canvas exactly as the encode fits it — is measured for edge density (ffmpeg
/// <c>edgedetect</c> at a fixed 640 px width, mean of the edge map) in the band the caption would
/// cover and in the matching band at the top. Text, UI chrome and dense detail read high; a clean
/// wall, sky or blurred background reads near zero.
/// </summary>
/// <remarks>
/// Measured on the launch reel's sources (bottom vs top): a UI timeline 12.9 vs 3.1 and a monitor
/// over a keyboard 16.8 vs 6.1 (moved up); a desk shot 0.2 vs 8.3 and a phone held in front of a
/// face 8.0 vs 6.6 (kept down). Hence "at least <see cref="MinBusyBottom"/> and at least
/// <see cref="BusyRatio"/> times the top".
/// </remarks>
public static class CaptionPlacementProbe
{
    /// <summary>Edge density (0..255 mean of the edge map at 640 px) from which a bottom band counts as busy.</summary>
    public const double MinBusyBottom = 8.0;

    /// <summary>How much busier than the top band the bottom band must be before a caption moves up.</summary>
    public const double BusyRatio = 2.0;

    private static readonly Regex YAvg = new(@"lavfi\.signalstats\.YAVG=([0-9.]+)", RegexOptions.Compiled);

    /// <summary>
    /// Whether both bands carry text, so a caption has nowhere clean to go. Used only over a
    /// designed composition (a Remotion render with its own typography), where the cue is then not
    /// drawn: busy natural footage keeps its captions.
    /// </summary>
    public static bool BothBusy(double bottom, double top) => bottom >= MinBusyBottom && top >= MinBusyBottom;

    /// <summary>Whether a caption should move from the bottom band to the top.</summary>
    public static bool ShouldMoveUp(double bottom, double top) =>
        bottom >= MinBusyBottom && bottom >= BusyRatio * top;

    /// <summary>
    /// One frame at <paramref name="atSec"/>, fitted to the canvas by <paramref name="canvasFitFilter"/>
    /// (null when the source already is the canvas), edge-mapped at 640 px and cropped to the band
    /// <c>[top, top + height)</c> of the frame (fractions), its mean printed to stderr.
    /// </summary>
    public static IReadOnlyList<string> BandArgs(string videoPath, double atSec, string? canvasFitFilter, double bandTop, double bandHeight)
    {
        string fit = string.IsNullOrEmpty(canvasFitFilter) ? string.Empty : canvasFitFilter + ",";
        string top = Num(Math.Clamp(bandTop, 0, 0.98));
        string height = Num(Math.Clamp(bandHeight, 0.02, 1 - Math.Clamp(bandTop, 0, 0.98)));
        return
        [
            "-hide_banner", "-nostdin", .. UserMediaInputFormats.InputOptions(),
            "-ss", Num(Math.Max(0, atSec)), "-i", videoPath, "-frames:v", "1", "-an",
            "-vf", $"{fit}scale=640:-2,format=gray,edgedetect=low=0.1:high=0.3," +
                   $"crop=iw*0.8:ih*{height}:iw*0.1:ih*{top},signalstats,metadata=print:key=lavfi.signalstats.YAVG",
            "-f", "null", "-"
        ];
    }

    /// <summary>The band's measured edge density, or null when ffmpeg printed none.</summary>
    public static double? ParseDensity(string stderr)
    {
        Match m = YAvg.Match(stderr ?? string.Empty);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
    }

    /// <summary>
    /// The caption's bottom band and the mirrored top band as (top, height) fractions of the frame,
    /// from the layout: two lines of text at the layout's size above the bottom margin, and the same
    /// height below the top margin.
    /// </summary>
    public static ((double Top, double Height) Bottom, (double Top, double Height) Upper) Bands(CaptionLayout layout)
    {
        double h = Math.Max(16, layout.PlayResY);
        double band = Math.Clamp(2.6 * layout.FontSizePx / h, 0.06, 0.35);
        double bottomEdge = 1 - layout.MarginV / h;
        double topEdge = (layout.TopMarginV > 0 ? layout.TopMarginV : layout.MarginV) / h;
        return ((Math.Max(0, bottomEdge - band), band), (topEdge, band));
    }

    private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
