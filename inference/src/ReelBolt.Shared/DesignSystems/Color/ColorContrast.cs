using System.Globalization;

namespace ReelBolt.Shared.DesignSystems.Color;

/// <summary>
/// WCAG 2.x contrast ratio between two sRGB colours (see SPEC 14.2). Pure and total: a malformed
/// hex answers the "no contrast" ratio 1.0 rather than throwing, so a caller is never failed by a
/// colour it does not understand.
/// </summary>
public static class ColorContrast
{
    /// <summary>
    /// The WCAG 2.x contrast ratio between <paramref name="hexA"/> and <paramref name="hexB"/>
    /// (1.0 .. 21.0). Input is <c>#RRGGBB</c>; an optional <c>#RRGGBBAA</c> alpha is ignored, and
    /// anything that is not a parseable hex answers 1.0.
    /// </summary>
    public static double Ratio(string? hexA, string? hexB)
    {
        if (!TryLuminance(hexA, out double lumA) || !TryLuminance(hexB, out double lumB))
            return 1.0;

        double lighter = Math.Max(lumA, lumB);
        double darker = Math.Min(lumA, lumB);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// The WCAG 2.x relative luminance of <paramref name="hex"/>, or false when it is not at least
    /// six hex digits (with or without a leading <c>#</c>).
    /// </summary>
    public static bool TryLuminance(string? hex, out double luminance)
    {
        luminance = 0;
        if (hex is null)
            return false;

        ReadOnlySpan<char> span = hex.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#')
            span = span[1..];
        if (span.Length < 6)
            return false;

        span = span[..6];
        if (!TryByte(span[..2], out int r) || !TryByte(span[2..4], out int g) || !TryByte(span[4..6], out int b))
            return false;

        luminance = 0.2126 * Linear(r / 255.0) + 0.7152 * Linear(g / 255.0) + 0.0722 * Linear(b / 255.0);
        return true;
    }

    private static bool TryByte(ReadOnlySpan<char> value, out int parsed) =>
        int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed);

    private static double Linear(double channel) =>
        channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
}
