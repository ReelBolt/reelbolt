namespace ReelBolt.Shared.DesignSystems.Brand;

/// <summary>
/// The measured ink tone of a logo. <see cref="Luminance"/> is the WCAG relative luminance (0..1) of the
/// alpha-weighted mean colour of the mark's pixels; the two readable flags say whether that ink reaches a
/// WCAG contrast of at least 3.0 against a plain white (light) or black (dark) background.
/// </summary>
public sealed record LogoTone(string Tone, double Luminance, bool ReadableOnLight, bool ReadableOnDark, string Source)
{
    public const string Dark = "dark";
    public const string Light = "light";
    public const string Mid = "mid";

    public const string SourcePixels = "pixels";
    public const string SourceSvgFill = "svg-fill";
    public const string SourceSvgDefaultBlack = "svg-default-black";
    public const string SourceName = "name";
    public const string SourceUnknown = "unknown";

    /// <summary>The tone for a mean-colour luminance.</summary>
    public static LogoTone FromLuminance(double luminance, string source)
    {
        double l = double.IsFinite(luminance) ? Math.Clamp(luminance, 0, 1) : 0;
        bool onLight = WcagContrast.Ratio(l, 1.0) >= WcagContrast.ReadableThreshold;
        bool onDark = WcagContrast.Ratio(l, 0.0) >= WcagContrast.ReadableThreshold;
        string tone = onLight && onDark ? Mid
            : onLight ? Dark
            : onDark ? Light
            : l < 0.18 ? Dark : Light;
        return new LogoTone(tone, Math.Round(l, 3), onLight, onDark, source);
    }
}
