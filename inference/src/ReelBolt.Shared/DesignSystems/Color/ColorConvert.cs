using System.Globalization;
using System.Text;

namespace ReelBolt.Shared.DesignSystems.Color;

/// <summary>
/// Converts a CSS colour string to a canonical lowercase sRGB hex string.
///
/// <para>
/// Hand-written, total and pure: it never throws, never touches IO or the network, and answers
/// <c>false</c> for anything it does not understand. Out-of-gamut colours are clipped to the sRGB
/// gamut rather than rejected.
/// </para>
/// </summary>
public static class ColorConvert
{
    /// <summary>The longest input accepted, in characters.</summary>
    public const int MaxInputLength = 200;

    /// <summary>
    /// Converts <paramref name="css"/> to lowercase <c>#rrggbb</c> (or <c>#rrggbbaa</c> when the
    /// alpha is below 1). <paramref name="original"/> is the trimmed input cut to 64 characters
    /// when the input was a function (<c>rgb(</c>, <c>hsl(</c>, <c>oklch(</c> …), else null.
    /// On false both outputs are empty/null.
    /// </summary>
    public static bool TryToSrgbHex(string css, out string hex, out string? original)
    {
        hex = "";
        original = null;

        try
        {
            if (string.IsNullOrEmpty(css) || css.Length > MaxInputLength)
                return false;

            string s = css.Trim();
            if (s.Length == 0)
                return false;

            if (s[0] == '#')
            {
                if (!TryHex(s, out string fromHex))
                    return false;
                hex = fromHex;
                return true;
            }

            int paren = s.IndexOf('(');
            if (paren < 0)
                return TryKeyword(s, out hex);

            if (s[^1] != ')' || paren == 0)
                return false;

            string name = s[..paren].Trim().ToLowerInvariant();
            string body = s[(paren + 1)..^1];

            if (!TryResolve(name, body, out double r, out double g, out double b, out double a))
                return false;

            hex = ToHex(r, g, b, a);
            original = s.Length > 64 ? s[..64] : s;
            return true;
        }
        catch
        {
            hex = "";
            original = null;
            return false;
        }
    }

    private static bool TryHex(string s, out string hex)
    {
        hex = "";
        string digits = s[1..];
        if (digits.Length is not (3 or 4 or 6 or 8))
            return false;

        foreach (char c in digits)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }

        if (digits.Length is 3 or 4)
        {
            var expanded = new StringBuilder(digits.Length * 2);
            foreach (char c in digits)
            {
                expanded.Append(c);
                expanded.Append(c);
            }
            digits = expanded.ToString();
        }

        hex = "#" + digits.ToLowerInvariant();
        return true;
    }

    private static bool TryKeyword(string s, out string hex)
    {
        hex = "";
        if (s.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            hex = "#00000000";
            return true;
        }

        if (NamedColors.TryGetValue(s, out int rgb))
        {
            hex = "#" + rgb.ToString("x6", CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    /// <summary>The 148 CSS Color Module Level 4 named colours (case-insensitive), as 0xRRGGBB.</summary>
    private static readonly Dictionary<string, int> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["aliceblue"] = 0xf0f8ff,
        ["antiquewhite"] = 0xfaebd7,
        ["aqua"] = 0x00ffff,
        ["aquamarine"] = 0x7fffd4,
        ["azure"] = 0xf0ffff,
        ["beige"] = 0xf5f5dc,
        ["bisque"] = 0xffe4c4,
        ["black"] = 0x000000,
        ["blanchedalmond"] = 0xffebcd,
        ["blue"] = 0x0000ff,
        ["blueviolet"] = 0x8a2be2,
        ["brown"] = 0xa52a2a,
        ["burlywood"] = 0xdeb887,
        ["cadetblue"] = 0x5f9ea0,
        ["chartreuse"] = 0x7fff00,
        ["chocolate"] = 0xd2691e,
        ["coral"] = 0xff7f50,
        ["cornflowerblue"] = 0x6495ed,
        ["cornsilk"] = 0xfff8dc,
        ["crimson"] = 0xdc143c,
        ["cyan"] = 0x00ffff,
        ["darkblue"] = 0x00008b,
        ["darkcyan"] = 0x008b8b,
        ["darkgoldenrod"] = 0xb8860b,
        ["darkgray"] = 0xa9a9a9,
        ["darkgreen"] = 0x006400,
        ["darkgrey"] = 0xa9a9a9,
        ["darkkhaki"] = 0xbdb76b,
        ["darkmagenta"] = 0x8b008b,
        ["darkolivegreen"] = 0x556b2f,
        ["darkorange"] = 0xff8c00,
        ["darkorchid"] = 0x9932cc,
        ["darkred"] = 0x8b0000,
        ["darksalmon"] = 0xe9967a,
        ["darkseagreen"] = 0x8fbc8f,
        ["darkslateblue"] = 0x483d8b,
        ["darkslategray"] = 0x2f4f4f,
        ["darkslategrey"] = 0x2f4f4f,
        ["darkturquoise"] = 0x00ced1,
        ["darkviolet"] = 0x9400d3,
        ["deeppink"] = 0xff1493,
        ["deepskyblue"] = 0x00bfff,
        ["dimgray"] = 0x696969,
        ["dimgrey"] = 0x696969,
        ["dodgerblue"] = 0x1e90ff,
        ["firebrick"] = 0xb22222,
        ["floralwhite"] = 0xfffaf0,
        ["forestgreen"] = 0x228b22,
        ["fuchsia"] = 0xff00ff,
        ["gainsboro"] = 0xdcdcdc,
        ["ghostwhite"] = 0xf8f8ff,
        ["gold"] = 0xffd700,
        ["goldenrod"] = 0xdaa520,
        ["gray"] = 0x808080,
        ["green"] = 0x008000,
        ["greenyellow"] = 0xadff2f,
        ["grey"] = 0x808080,
        ["honeydew"] = 0xf0fff0,
        ["hotpink"] = 0xff69b4,
        ["indianred"] = 0xcd5c5c,
        ["indigo"] = 0x4b0082,
        ["ivory"] = 0xfffff0,
        ["khaki"] = 0xf0e68c,
        ["lavender"] = 0xe6e6fa,
        ["lavenderblush"] = 0xfff0f5,
        ["lawngreen"] = 0x7cfc00,
        ["lemonchiffon"] = 0xfffacd,
        ["lightblue"] = 0xadd8e6,
        ["lightcoral"] = 0xf08080,
        ["lightcyan"] = 0xe0ffff,
        ["lightgoldenrodyellow"] = 0xfafad2,
        ["lightgray"] = 0xd3d3d3,
        ["lightgreen"] = 0x90ee90,
        ["lightgrey"] = 0xd3d3d3,
        ["lightpink"] = 0xffb6c1,
        ["lightsalmon"] = 0xffa07a,
        ["lightseagreen"] = 0x20b2aa,
        ["lightskyblue"] = 0x87cefa,
        ["lightslategray"] = 0x778899,
        ["lightslategrey"] = 0x778899,
        ["lightsteelblue"] = 0xb0c4de,
        ["lightyellow"] = 0xffffe0,
        ["lime"] = 0x00ff00,
        ["limegreen"] = 0x32cd32,
        ["linen"] = 0xfaf0e6,
        ["magenta"] = 0xff00ff,
        ["maroon"] = 0x800000,
        ["mediumaquamarine"] = 0x66cdaa,
        ["mediumblue"] = 0x0000cd,
        ["mediumorchid"] = 0xba55d3,
        ["mediumpurple"] = 0x9370db,
        ["mediumseagreen"] = 0x3cb371,
        ["mediumslateblue"] = 0x7b68ee,
        ["mediumspringgreen"] = 0x00fa9a,
        ["mediumturquoise"] = 0x48d1cc,
        ["mediumvioletred"] = 0xc71585,
        ["midnightblue"] = 0x191970,
        ["mintcream"] = 0xf5fffa,
        ["mistyrose"] = 0xffe4e1,
        ["moccasin"] = 0xffe4b5,
        ["navajowhite"] = 0xffdead,
        ["navy"] = 0x000080,
        ["oldlace"] = 0xfdf5e6,
        ["olive"] = 0x808000,
        ["olivedrab"] = 0x6b8e23,
        ["orange"] = 0xffa500,
        ["orangered"] = 0xff4500,
        ["orchid"] = 0xda70d6,
        ["palegoldenrod"] = 0xeee8aa,
        ["palegreen"] = 0x98fb98,
        ["paleturquoise"] = 0xafeeee,
        ["palevioletred"] = 0xdb7093,
        ["papayawhip"] = 0xffefd5,
        ["peachpuff"] = 0xffdab9,
        ["peru"] = 0xcd853f,
        ["pink"] = 0xffc0cb,
        ["plum"] = 0xdda0dd,
        ["powderblue"] = 0xb0e0e6,
        ["purple"] = 0x800080,
        ["rebeccapurple"] = 0x663399,
        ["red"] = 0xff0000,
        ["rosybrown"] = 0xbc8f8f,
        ["royalblue"] = 0x4169e1,
        ["saddlebrown"] = 0x8b4513,
        ["salmon"] = 0xfa8072,
        ["sandybrown"] = 0xf4a460,
        ["seagreen"] = 0x2e8b57,
        ["seashell"] = 0xfff5ee,
        ["sienna"] = 0xa0522d,
        ["silver"] = 0xc0c0c0,
        ["skyblue"] = 0x87ceeb,
        ["slateblue"] = 0x6a5acd,
        ["slategray"] = 0x708090,
        ["slategrey"] = 0x708090,
        ["snow"] = 0xfffafa,
        ["springgreen"] = 0x00ff7f,
        ["steelblue"] = 0x4682b4,
        ["tan"] = 0xd2b48c,
        ["teal"] = 0x008080,
        ["thistle"] = 0xd8bfd8,
        ["tomato"] = 0xff6347,
        ["turquoise"] = 0x40e0d0,
        ["violet"] = 0xee82ee,
        ["wheat"] = 0xf5deb3,
        ["white"] = 0xffffff,
        ["whitesmoke"] = 0xf5f5f5,
        ["yellow"] = 0xffff00,
        ["yellowgreen"] = 0x9acd32,
    };

    private static bool TryResolve(string name, string body, out double r, out double g, out double b, out double a)
    {
        r = 0;
        g = 0;
        b = 0;
        a = 1;

        List<string> tokens = Tokenize(body, out int slashIndex);

        switch (name)
        {
            case "rgb" or "rgba":
                {
                    if (!ExtractChannels(tokens, slashIndex, 0, 3, allowLegacyAlpha: true, out string[] channels, out a))
                        return false;

                    if (!TryParseChannel(channels[0], out double c0, out bool p0)
                        || !TryParseChannel(channels[1], out double c1, out bool p1)
                        || !TryParseChannel(channels[2], out double c2, out bool p2))
                        return false;

                    r = RgbChannel(c0, p0) / 255.0;
                    g = RgbChannel(c1, p1) / 255.0;
                    b = RgbChannel(c2, p2) / 255.0;
                    return true;
                }

            case "hsl" or "hsla":
                {
                    if (!ExtractChannels(tokens, slashIndex, 0, 3, allowLegacyAlpha: true, out string[] channels, out a))
                        return false;

                    if (!TryParseToken(channels[0], allowAngle: true, out double hv, out Unit hu)
                        || !TryParseChannel(channels[1], out double sv, out _)
                        || !TryParseChannel(channels[2], out double lv, out _))
                        return false;

                    HslToRgb(ToHueDegrees(hv, hu), sv / 100.0, lv / 100.0, out r, out g, out b);
                    return true;
                }

            case "hwb":
                {
                    if (!ExtractChannels(tokens, slashIndex, 0, 3, allowLegacyAlpha: false, out string[] channels, out a))
                        return false;

                    if (!TryParseToken(channels[0], allowAngle: true, out double hv, out Unit hu)
                        || !TryParseChannel(channels[1], out double wv, out _)
                        || !TryParseChannel(channels[2], out double bv, out _))
                        return false;

                    double h = ToHueDegrees(hv, hu);
                    double w = wv / 100.0;
                    double black = bv / 100.0;

                    if (w + black >= 1.0)
                    {
                        double gray = w / (w + black);
                        r = gray;
                        g = gray;
                        b = gray;
                    }
                    else
                    {
                        HslToRgb(h, 1.0, 0.5, out double hr, out double hg, out double hb);
                        r = hr * (1.0 - w - black) + w;
                        g = hg * (1.0 - w - black) + w;
                        b = hb * (1.0 - w - black) + w;
                    }
                    return true;
                }

            case "oklab":
                {
                    if (!ExtractChannels(tokens, slashIndex, 0, 3, allowLegacyAlpha: false, out string[] channels, out a))
                        return false;

                    if (!TryParseChannel(channels[0], out double lv, out bool lp)
                        || !TryParseComponent(channels[1], 0.4, out double av)
                        || !TryParseComponent(channels[2], 0.4, out double bv))
                        return false;

                    OklabToSrgb(lp ? lv / 100.0 : lv, av, bv, out r, out g, out b);
                    return true;
                }

            case "oklch":
                {
                    if (!ExtractChannels(tokens, slashIndex, 0, 3, allowLegacyAlpha: false, out string[] channels, out a))
                        return false;

                    if (!TryParseChannel(channels[0], out double lv, out bool lp)
                        || !TryParseComponent(channels[1], 0.4, out double cv)
                        || !TryParseToken(channels[2], allowAngle: true, out double hv, out Unit hu))
                        return false;

                    double h = ToHueDegrees(hv, hu) * Math.PI / 180.0;
                    OklabToSrgb(lp ? lv / 100.0 : lv, cv * Math.Cos(h), cv * Math.Sin(h), out r, out g, out b);
                    return true;
                }

            case "lab":
                {
                    if (!ExtractChannels(tokens, slashIndex, 0, 3, allowLegacyAlpha: false, out string[] channels, out a))
                        return false;

                    if (!TryParseToken(channels[0], allowAngle: false, out double lv, out _)
                        || !TryParseComponent(channels[1], 125.0, out double av)
                        || !TryParseComponent(channels[2], 125.0, out double bv))
                        return false;

                    LabToSrgb(lv, av, bv, out r, out g, out b);
                    return true;
                }

            case "lch":
                {
                    if (!ExtractChannels(tokens, slashIndex, 0, 3, allowLegacyAlpha: false, out string[] channels, out a))
                        return false;

                    if (!TryParseToken(channels[0], allowAngle: false, out double lv, out _)
                        || !TryParseComponent(channels[1], 150.0, out double cv)
                        || !TryParseToken(channels[2], allowAngle: true, out double hv, out Unit hu))
                        return false;

                    double h = ToHueDegrees(hv, hu) * Math.PI / 180.0;
                    LabToSrgb(lv, cv * Math.Cos(h), cv * Math.Sin(h), out r, out g, out b);
                    return true;
                }

            case "color":
                {
                    if (tokens.Count == 0)
                        return false;

                    string space = tokens[0];
                    bool srgb = space.Equals("srgb", StringComparison.OrdinalIgnoreCase);
                    bool displayP3 = space.Equals("display-p3", StringComparison.OrdinalIgnoreCase);
                    if (!srgb && !displayP3)
                        return false;

                    if (!ExtractChannels(tokens, slashIndex, 1, 3, allowLegacyAlpha: false, out string[] channels, out a))
                        return false;

                    if (!TryParseChannel(channels[0], out double c0, out bool p0)
                        || !TryParseChannel(channels[1], out double c1, out bool p1)
                        || !TryParseChannel(channels[2], out double c2, out bool p2))
                        return false;

                    double v0 = p0 ? c0 / 100.0 : c0;
                    double v1 = p1 ? c1 / 100.0 : c1;
                    double v2 = p2 ? c2 / 100.0 : c2;

                    if (srgb)
                    {
                        r = v0;
                        g = v1;
                        b = v2;
                    }
                    else
                    {
                        double lr = SrgbToLinear(v0);
                        double lg = SrgbToLinear(v1);
                        double lb = SrgbToLinear(v2);
                        double mr = 1.2249402 * lr - 0.2249402 * lg;
                        double mg = -0.0420570 * lr + 1.0420570 * lg;
                        double mb = -0.0196376 * lr - 0.0786360 * lg + 1.0982736 * lb;
                        r = LinearToSrgb(mr);
                        g = LinearToSrgb(mg);
                        b = LinearToSrgb(mb);
                    }
                    return true;
                }

            default:
                return false;
        }
    }

    private static List<string> Tokenize(string body, out int slashIndex)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        bool pending = false;
        slashIndex = -1;

        foreach (char c in body)
        {
            if (c == '/')
            {
                if (pending)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                    pending = false;
                }
                slashIndex = tokens.Count;
                continue;
            }

            if (char.IsWhiteSpace(c) || c == ',')
            {
                if (pending)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                    pending = false;
                }
                continue;
            }

            sb.Append(c);
            pending = true;
        }

        if (pending)
            tokens.Add(sb.ToString());

        return tokens;
    }

    private static bool ExtractChannels(
        List<string> tokens,
        int slashIndex,
        int firstColorIndex,
        int expected,
        bool allowLegacyAlpha,
        out string[] channels,
        out double alpha)
    {
        channels = [];
        alpha = 1;

        if (slashIndex >= 0)
        {
            if (slashIndex - firstColorIndex != expected || tokens.Count - slashIndex != 1)
                return false;

            channels = tokens.GetRange(firstColorIndex, expected).ToArray();
            return TryParseAlpha(tokens[^1], out alpha);
        }

        int count = tokens.Count - firstColorIndex;
        if (count == expected)
        {
            channels = tokens.GetRange(firstColorIndex, expected).ToArray();
            return true;
        }

        if (allowLegacyAlpha && count == expected + 1)
        {
            channels = tokens.GetRange(firstColorIndex, expected).ToArray();
            return TryParseAlpha(tokens[^1], out alpha);
        }

        return false;
    }

    private static bool TryParseToken(string token, bool allowAngle, out double value, out Unit unit)
    {
        value = 0;
        unit = Unit.None;

        if (token.Length == 0)
            return false;

        if (token == "none")
            return true;

        string num = token;
        if (num.EndsWith("%", StringComparison.Ordinal))
        {
            unit = Unit.Percent;
            num = num[..^1];
        }
        else if (allowAngle)
        {
            if (num.EndsWith("grad", StringComparison.Ordinal))
            {
                unit = Unit.Grad;
                num = num[..^4];
            }
            else if (num.EndsWith("turn", StringComparison.Ordinal))
            {
                unit = Unit.Turn;
                num = num[..^4];
            }
            else if (num.EndsWith("deg", StringComparison.Ordinal))
            {
                unit = Unit.Deg;
                num = num[..^3];
            }
            else if (num.EndsWith("rad", StringComparison.Ordinal))
            {
                unit = Unit.Rad;
                num = num[..^3];
            }
        }

        if (num.Length == 0)
            return false;

        if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;

        return double.IsFinite(value);
    }

    private static bool TryParseChannel(string token, out double value, out bool percent)
    {
        percent = false;
        if (!TryParseToken(token, allowAngle: false, out value, out Unit unit))
            return false;

        percent = unit == Unit.Percent;
        return true;
    }

    private static bool TryParseComponent(string token, double percentScale, out double value)
    {
        value = 0;
        if (!TryParseToken(token, allowAngle: false, out double parsed, out Unit unit))
            return false;

        value = unit == Unit.Percent ? parsed / 100.0 * percentScale : parsed;
        return true;
    }

    private static bool TryParseAlpha(string token, out double alpha)
    {
        alpha = 1;
        if (!TryParseToken(token, allowAngle: false, out double parsed, out Unit unit))
            return false;

        double value = unit == Unit.Percent ? parsed / 100.0 : parsed;
        alpha = Math.Clamp(value, 0.0, 1.0);
        return true;
    }

    private static double RgbChannel(double value, bool percent)
    {
        double channel = percent ? value * 2.55 : value;
        return Math.Clamp(channel, 0.0, 255.0);
    }

    private static double ToHueDegrees(double value, Unit unit) => unit switch
    {
        Unit.Percent => value / 100.0 * 360.0,
        Unit.Grad => value * 360.0 / 400.0,
        Unit.Rad => value * 180.0 / Math.PI,
        Unit.Turn => value * 360.0,
        _ => value,
    };

    private static void HslToRgb(double hueDegrees, double s, double l, out double r, out double g, out double b)
    {
        double h = ((hueDegrees % 360.0) + 360.0) % 360.0;
        double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
        double hp = h / 60.0;
        double x = c * (1.0 - Math.Abs(hp % 2.0 - 1.0));

        double r1;
        double g1;
        double b1;
        if (hp < 1.0)
        {
            (r1, g1, b1) = (c, x, 0.0);
        }
        else if (hp < 2.0)
        {
            (r1, g1, b1) = (x, c, 0.0);
        }
        else if (hp < 3.0)
        {
            (r1, g1, b1) = (0.0, c, x);
        }
        else if (hp < 4.0)
        {
            (r1, g1, b1) = (0.0, x, c);
        }
        else if (hp < 5.0)
        {
            (r1, g1, b1) = (x, 0.0, c);
        }
        else
        {
            (r1, g1, b1) = (c, 0.0, x);
        }

        double m = l - c / 2.0;
        r = r1 + m;
        g = g1 + m;
        b = b1 + m;
    }

    private static void OklabToSrgb(double l, double a, double b, out double r, out double g, out double bl)
    {
        double l_ = l + 0.3963377774 * a + 0.2158037573 * b;
        double m_ = l - 0.1055613458 * a - 0.0638541728 * b;
        double s_ = l - 0.0894841775 * a - 1.2914855480 * b;

        double lc = l_ * l_ * l_;
        double mc = m_ * m_ * m_;
        double sc = s_ * s_ * s_;

        double lr = 4.0767416621 * lc - 3.3077115913 * mc + 0.2309699292 * sc;
        double lg = -1.2684380046 * lc + 2.6097574011 * mc - 0.3413193965 * sc;
        double lb = -0.0041960863 * lc - 0.7034186147 * mc + 1.7076147010 * sc;

        r = LinearToSrgb(lr);
        g = LinearToSrgb(lg);
        bl = LinearToSrgb(lb);
    }

    private static void LabToSrgb(double l, double a, double b, out double r, out double g, out double bl)
    {
        const double epsilon = 216.0 / 24389.0;
        const double kappa = 24389.0 / 27.0;

        double fy = (l + 16.0) / 116.0;
        double fx = fy + a / 500.0;
        double fz = fy - b / 200.0;

        double fx3 = fx * fx * fx;
        double fz3 = fz * fz * fz;
        double x = fx3 > epsilon ? fx3 : (116.0 * fx - 16.0) / kappa;
        double y = l > kappa * epsilon ? fy * fy * fy : l / kappa;
        double z = fz3 > epsilon ? fz3 : (116.0 * fz - 16.0) / kappa;

        double xD50 = x * 0.96422;
        double yD50 = y;
        double zD50 = z * 0.82521;

        // Bradford D50 -> D65.
        double xD65 = 0.9555766 * xD50 - 0.0230393 * yD50 + 0.0631636 * zD50;
        double yD65 = -0.0282895 * xD50 + 1.0099416 * yD50 + 0.0210077 * zD50;
        double zD65 = 0.0122982 * xD50 - 0.0204830 * yD50 + 1.3299098 * zD50;

        double lr = 3.2409699419 * xD65 - 1.5373831776 * yD65 - 0.4986107603 * zD65;
        double lg = -0.9692436363 * xD65 + 1.8759675015 * yD65 + 0.0415550574 * zD65;
        double lb = 0.0556300797 * xD65 - 0.2039769589 * yD65 + 1.0569715142 * zD65;

        r = LinearToSrgb(lr);
        g = LinearToSrgb(lg);
        bl = LinearToSrgb(lb);
    }

    private static double LinearToSrgb(double value)
    {
        double v = Math.Clamp(value, 0.0, 1.0);
        return v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055;
    }

    private static double SrgbToLinear(double value) =>
        value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

    private static string ToHex(double r, double g, double b, double a)
    {
        byte rb = ToByte(r);
        byte gb = ToByte(g);
        byte bb = ToByte(b);

        if (a >= 1.0)
            return "#" + Convert.ToHexStringLower(new[] { rb, gb, bb });

        int alphaByte = Math.Clamp(
            (int)Math.Round(a * 255.0, MidpointRounding.AwayFromZero),
            0,
            255);

        return "#" + Convert.ToHexStringLower(new[] { rb, gb, bb, (byte)alphaByte });
    }

    private static byte ToByte(double value)
    {
        double clamped = Math.Clamp(value, 0.0, 1.0);
        return (byte)Math.Clamp(
            (int)Math.Round(clamped * 255.0, MidpointRounding.AwayFromZero),
            0,
            255);
    }

    private enum Unit
    {
        None,
        Percent,
        Deg,
        Grad,
        Rad,
        Turn,
    }
}
