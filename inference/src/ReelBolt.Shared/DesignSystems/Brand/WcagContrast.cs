namespace ReelBolt.Shared.DesignSystems.Brand;

/// <summary>WCAG 2.x relative luminance and contrast ratio helpers.</summary>
public static class WcagContrast
{
    /// <summary>Minimum contrast ratio for a logo to count as readable on a background.</summary>
    public const double ReadableThreshold = 3.0;

    /// <summary>Relative luminance of an sRGB colour given as 0..255 channels.</summary>
    public static double RelativeLuminance(byte r, byte g, byte b) =>
        FromLinear(Linearize(r / 255.0), Linearize(g / 255.0), Linearize(b / 255.0));

    /// <summary>Relative luminance of a colour whose channels are already linear (0..1).</summary>
    public static double FromLinear(double r, double g, double b) =>
        (0.2126 * r) + (0.7152 * g) + (0.0722 * b);

    /// <summary>sRGB channel (0..1) to linear light.</summary>
    public static double Linearize(double c) =>
        c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    /// <summary>Contrast ratio (1..21) between two relative luminances.</summary>
    public static double Ratio(double luminanceA, double luminanceB)
    {
        double hi = Math.Max(luminanceA, luminanceB);
        double lo = Math.Min(luminanceA, luminanceB);
        return (hi + 0.05) / (lo + 0.05);
    }
}
