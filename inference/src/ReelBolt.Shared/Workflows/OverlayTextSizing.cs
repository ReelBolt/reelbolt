using System.Globalization;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// The one table that turns the motion-graphics plan's size and placement WORDS into numbers
/// (docs/design/graphics-see-and-size.md). The model chooses <c>textSize</c>, <c>anchor</c> and
/// <c>assetCanvas</c> as words on <c>MotionGraphicsOverlay</c>; every number here is the server's.
/// Shared by the graphics step (which tells the agent what each word means on the output canvas and
/// measures the rendered type) and the compile (which draws drawtext at that size and enforces the
/// floor on rendered graphics).
/// </summary>
public static class OverlayTextSizing
{
    /// <summary>Default legibility floor: capital letters at least 2.5% of the output height.</summary>
    public const double DefaultMinCapHeightPct = 2.5;

    /// <summary>Lowest floor a config may set.</summary>
    public const double MinAllowedCapHeightPct = 1.0;

    /// <summary>Highest floor a config may set, and the largest cap height any word maps to.</summary>
    public const double MaxAllowedCapHeightPct = 12.0;

    /// <summary>
    /// Cap height as a fraction of the font size (em). Real faces sit between 0.70 and 0.73 (Inter,
    /// DejaVu Sans, Montserrat) and higher for condensed display faces (Anton about 0.86), so 0.7 is
    /// the conservative choice: the font size it asks for never yields caps below the floor.
    /// </summary>
    public const double CapToEm = 0.7;

    /// <summary>The <c>assetCanvas</c> word for an asset rendered at the full output canvas size.</summary>
    public const string OutputCanvas = "Output";

    /// <summary>The <c>textSize</c> words, smallest first.</summary>
    public static readonly IReadOnlyList<string> TextSizeWords = ["Small", "Medium", "Large", "XLarge"];

    /// <summary>The <c>anchor</c> words.</summary>
    public static readonly IReadOnlyList<string> AnchorWords = ["Top", "Center", "Bottom"];

    /// <summary>The cap height a <c>textSize</c> word stands for, in percent of the output height; null for an unknown or empty word.</summary>
    public static double? CapHeightPct(string? textSize) => textSize switch
    {
        "Small" => 3.0,
        "Medium" => 4.0,
        "Large" => 5.5,
        "XLarge" => 7.5,
        _ => null
    };

    /// <summary>A recognised anchor word, or "" for anything else.</summary>
    public static string NormalizeAnchor(string? anchor) =>
        anchor is "Top" or "Center" or "Bottom" ? anchor : string.Empty;

    /// <summary>Whether <paramref name="assetCanvas"/> asks for the full-canvas composite.</summary>
    public static bool IsOutputCanvas(string? assetCanvas) =>
        string.Equals(assetCanvas, OutputCanvas, StringComparison.Ordinal);

    /// <summary>A configured floor clamped to <see cref="MinAllowedCapHeightPct"/>..<see cref="MaxAllowedCapHeightPct"/>; NaN or infinity reads as the default.</summary>
    public static double ClampMinCap(double pct) =>
        double.IsFinite(pct) ? Math.Clamp(pct, MinAllowedCapHeightPct, MaxAllowedCapHeightPct) : DefaultMinCapHeightPct;

    /// <summary>The cap height a word yields once the floor is applied, in percent; null for no word.</summary>
    public static double? EffectiveCapHeightPct(string? textSize, double minCapPct)
    {
        double? word = CapHeightPct(textSize);
        return word is { } pct ? Math.Clamp(Math.Max(pct, ClampMinCap(minCapPct)), MinAllowedCapHeightPct, MaxAllowedCapHeightPct) : null;
    }

    /// <summary>Cap height in pixels for a percentage of <paramref name="canvasHeight"/>, rounded up.</summary>
    public static int CapPx(int canvasHeight, double capPct) =>
        (int)Math.Ceiling(Math.Max(0, canvasHeight) * capPct / 100.0 - 1e-9);

    /// <summary>The font size (em, pixels) whose capitals are <paramref name="capPct"/> of <paramref name="canvasHeight"/>, rounded up.</summary>
    public static int FontPxForCap(int canvasHeight, double capPct) =>
        (int)Math.Ceiling(Math.Max(0, canvasHeight) * capPct / 100.0 / CapToEm - 1e-9);

    /// <summary>The cap height, in percent of <paramref name="canvasHeight"/>, of type set at <paramref name="fontPx"/>.</summary>
    public static double CapPctForFontPx(double fontPx, int canvasHeight) =>
        canvasHeight <= 0 ? 0 : Math.Round(fontPx * CapToEm / canvasHeight * 100.0, 2);

    /// <summary>"2.5" style invariant formatting for messages.</summary>
    public static string Format(double pct) => pct.ToString("0.##", CultureInfo.InvariantCulture);
}
