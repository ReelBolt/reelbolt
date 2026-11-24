using System.Globalization;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// The canvas an outpaint asks for, in the terms the Wan VACE endpoint accepts: four per-side expansion ratios
/// (each 0..<see cref="MaxMargin"/>) and the resulting even-pixel output size, at the source's own frame rate.
/// </summary>
/// <param name="Width">The output width in pixels (even, at least 2).</param>
/// <param name="Height">The output height in pixels (even, at least 2).</param>
/// <param name="Left">The fraction of the source width added on the left.</param>
/// <param name="Right">The fraction of the source width added on the right.</param>
/// <param name="Top">The fraction of the source height added on the top.</param>
/// <param name="Bottom">The fraction of the source height added on the bottom.</param>
public sealed record VideoOutpaintCanvas(int Width, int Height, double Left, double Right, double Top, double Bottom)
{
    /// <summary>The one <c>expand_ratio</c> the vendor model accepts for every enabled side (the largest margin).</summary>
    public double ExpandRatio => Math.Max(Math.Max(Left, Right), Math.Max(Top, Bottom));

    public bool ExpandLeft => Left > 0;

    public bool ExpandRight => Right > 0;

    public bool ExpandTop => Top > 0;

    public bool ExpandBottom => Bottom > 0;
}

/// <summary>
/// Turns the platform's two ways of asking for an extended canvas — a target ASPECT or four per-side MARGINS — into
/// a <see cref="VideoOutpaintCanvas"/>. Pure geometry; no vendor call and no I/O. A request that would need more than
/// <see cref="MaxMargin"/> on a side is unofferable (<c>null</c>), never silently clamped: the vendor's
/// <c>expand_ratio</c> is per side and a margin beyond the platform's bound is a different picture.
/// </summary>
public static class VideoOutpaintGeometry
{
    /// <summary>The largest expansion ratio the platform asks of one side.</summary>
    public const double MaxMargin = 0.5;

    /// <summary>The target aspects the platform offers, in display order. Luma cannot do 4:5; Wan reaches it by margins.</summary>
    public static readonly IReadOnlyList<string> Aspects = ["16:9", "9:16", "1:1", "4:5"];

    /// <summary>True when <paramref name="aspect"/> is one of <see cref="Aspects"/> ("16:9", "9:16", "1:1", "4:5").</summary>
    public static bool IsSupportedAspect(string? aspect) =>
        aspect is not null && Aspects.Contains(aspect.Trim(), StringComparer.Ordinal);

    /// <summary>The numeric width/height ratio of <paramref name="aspect"/> ("4:5" = 0.8), or null when unknown.</summary>
    public static double? AspectRatio(string? aspect)
    {
        if (!IsSupportedAspect(aspect)) return null;
        string[] parts = aspect!.Trim().Split(':');
        return double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// True when the margins that grow the picture are not all the same amount. Wan VACE has ONE <c>expand_ratio</c> for every enabled
    /// side, so the platform only offers "these sides, one amount": anything else would ask the vendor for a different frame than the
    /// one that was priced, plan-gated and conformed to.
    /// </summary>
    public static bool AreUneven(double left, double right, double top, double bottom)
    {
        double[] grown = [left, right, top, bottom];
        grown = [.. grown.Where(m => m > 0)];
        return grown.Length > 1 && grown.Max() - grown.Min() > 1e-9;
    }

    /// <summary>The canvas four explicit per-side margins produce, or null when one is outside 0..<see cref="MaxMargin"/> or they are uneven.</summary>
    public static VideoOutpaintCanvas? ForMargins(int sourceWidth, int sourceHeight, double left, double right, double top, double bottom)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return null;
        double[] margins = [left, right, top, bottom];
        if (margins.Any(m => m < 0 || m > MaxMargin || double.IsNaN(m) || double.IsInfinity(m))) return null;
        if (AreUneven(left, right, top, bottom)) return null;

        return new VideoOutpaintCanvas(
            EvenAtLeast2(sourceWidth * (1 + left + right)),
            EvenAtLeast2(sourceHeight * (1 + top + bottom)),
            left, right, top, bottom);
    }

    /// <summary>
    /// The canvas that gives the picture <paramref name="aspect"/>, expanding symmetrically on one axis, or null when
    /// that needs more than <see cref="MaxMargin"/> on a side (a portrait clip cannot become 16:9 under this bound).
    /// An already-matching aspect expands nothing.
    /// </summary>
    public static VideoOutpaintCanvas? ForAspect(int sourceWidth, int sourceHeight, string? aspect)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || AspectRatio(aspect) is not { } target) return null;

        double current = sourceWidth / (double)sourceHeight;
        if (Math.Abs(target - current) < 1e-9)
            return new VideoOutpaintCanvas(EvenAtLeast2(sourceWidth), EvenAtLeast2(sourceHeight), 0, 0, 0, 0);

        if (target > current)
        {
            double ratio = (target * sourceHeight - sourceWidth) / (2d * sourceWidth);
            return ratio <= MaxMargin + 1e-9
                ? new VideoOutpaintCanvas(EvenAtLeast2(target * sourceHeight), EvenAtLeast2(sourceHeight), ratio, ratio, 0, 0)
                : null;
        }

        double vertical = (sourceWidth / target - sourceHeight) / (2d * sourceHeight);
        return vertical <= MaxMargin + 1e-9
            ? new VideoOutpaintCanvas(EvenAtLeast2(sourceWidth), EvenAtLeast2(sourceWidth / target), 0, 0, vertical, vertical)
            : null;
    }

    /// <summary>True when <paramref name="aspect"/> is in the Luma endpoint's enum (4:5 is not).</summary>
    public static bool LumaSupports(string? aspect) =>
        aspect?.Trim() is "16:9" or "9:16" or "1:1" or "4:3" or "3:4" or "21:9" or "9:21";

    private static int EvenAtLeast2(double value) =>
        Math.Max(2, (int)Math.Round(value / 2d, MidpointRounding.AwayFromZero) * 2);
}
