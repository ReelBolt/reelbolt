using System.Globalization;

namespace ReelBolt.WorkflowEngine.Services.Video.Quality;

/// <summary>An sRGB colour with 0..255 channels and 0..1 alpha.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, double A);

/// <summary>
/// Reusable analysis helper: WCAG 2.x contrast maths (relative luminance of gamma-decoded sRGB,
/// contrast ratio (L1+0.05)/(L2+0.05)), ffmpeg colour parsing and alpha blending. Pure, no I/O.
/// </summary>
public static class ContrastMath
{
    private static readonly Dictionary<string, (byte R, byte G, byte B)> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = (255, 255, 255),
        ["black"] = (0, 0, 0),
        ["red"] = (255, 0, 0),
        ["green"] = (0, 128, 0),
        ["blue"] = (0, 0, 255),
        ["yellow"] = (255, 255, 0),
        ["gray"] = (128, 128, 128),
        ["grey"] = (128, 128, 128),
        ["cyan"] = (0, 255, 255),
        ["magenta"] = (255, 0, 255),
        ["orange"] = (255, 165, 0),
    };

    /// <summary>Gamma-decodes one sRGB channel (0..255) to linear light 0..1.</summary>
    public static double Linearise(double channel)
    {
        if (double.IsNaN(channel)) return 0;
        var c = Math.Clamp(channel, 0, 255) / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>Relative luminance (0..1) of an sRGB colour, BT.709 weights on linear light.</summary>
    public static double RelativeLuminance(double r, double g, double b) =>
        (0.2126 * Linearise(r)) + (0.7152 * Linearise(g)) + (0.0722 * Linearise(b));

    /// <summary>WCAG contrast ratio (1..21) between two relative luminances, order independent.</summary>
    public static double Ratio(double luminanceA, double luminanceB)
    {
        var hi = Math.Max(luminanceA, luminanceB);
        var lo = Math.Min(luminanceA, luminanceB);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>WCAG contrast ratio between two opaque colours.</summary>
    public static double Ratio(Rgba a, Rgba b) =>
        Ratio(RelativeLuminance(a.R, a.G, a.B), RelativeLuminance(b.R, b.G, b.B));

    /// <summary>Composites <paramref name="fg"/> over an opaque <paramref name="bg"/>; result is opaque.</summary>
    public static Rgba Blend(Rgba fg, Rgba bg)
    {
        var a = double.IsNaN(fg.A) ? 1 : Math.Clamp(fg.A, 0, 1);
        byte Mix(byte f, byte b) => (byte)Math.Clamp(Math.Round((f * a) + (b * (1 - a))), 0, 255);
        return new Rgba(Mix(fg.R, bg.R), Mix(fg.G, bg.G), Mix(fg.B, bg.B), 1);
    }

    /// <summary>
    /// Parses an ffmpeg colour: a name (<c>white</c>), <c>#RRGGBB</c>, <c>0xRRGGBB</c>, each with an
    /// optional <c>@alpha</c> suffix (0..1). Returns false for anything else; never throws.
    /// </summary>
    public static bool TryParseColor(string? text, out Rgba colour)
    {
        colour = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        var alpha = 1.0;
        var at = s.IndexOf('@');
        if (at >= 0)
        {
            if (!double.TryParse(s[(at + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out alpha)
                || double.IsNaN(alpha) || double.IsInfinity(alpha))
                return false;
            alpha = Math.Clamp(alpha, 0, 1);
            s = s[..at];
        }

        string? hex = null;
        if (s.StartsWith('#')) hex = s[1..];
        else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = s[2..];

        if (hex is not null)
        {
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v))
                return false;
            colour = new Rgba((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF), alpha);
            return true;
        }

        if (Named.TryGetValue(s, out var n))
        {
            colour = new Rgba(n.R, n.G, n.B, alpha);
            return true;
        }

        return false;
    }
}
