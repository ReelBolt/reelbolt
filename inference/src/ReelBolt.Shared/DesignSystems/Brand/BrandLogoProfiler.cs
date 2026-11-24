using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Shared.DesignSystems.Brand;

/// <summary>
/// Measures the ink tone of a logo file. PNG is decoded with <see cref="PngAlpha"/>; SVG is parsed
/// defensively (no DTD, bounded size). Other formats are not decoded and yield <c>null</c>. Never throws.
/// </summary>
public static class BrandLogoProfiler
{
    public const int MaxSvgBytes = 512 * 1024;

    private static readonly HashSet<string> Drawable = new(StringComparer.Ordinal)
    {
        "path", "circle", "ellipse", "rect", "polygon", "polyline", "line",
    };

    public static LogoTone? Profile(ReadOnlySpan<byte> bytes, string mimeType)
    {
        try
        {
            string mime = (mimeType ?? string.Empty).Trim().ToLowerInvariant();
            if (mime == "image/png") return ProfilePng(bytes);
            if (mime == "image/svg+xml" || mime == "image/svg") return ProfileSvg(bytes);
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static LogoTone? ProfilePng(ReadOnlySpan<byte> bytes)
    {
        if (!PngAlpha.IsPng(bytes)) return null;
        var mean = PngAlpha.MeanLogoLinearColor(bytes.ToArray());
        if (mean is null) return null;
        return LogoTone.FromLuminance(WcagContrast.FromLinear(mean.Value.R, mean.Value.G, mean.Value.B), LogoTone.SourcePixels);
    }

    private static LogoTone? ProfileSvg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxSvgBytes) return null;

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = MaxSvgBytes * 2L,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        double r = 0, g = 0, b = 0, weight = 0;
        int drawables = 0, noFill = 0;
        var inherited = new Stack<(string? Fill, bool Skip)>();
        var css = ReadStyleRules(bytes);

        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (inherited.Count > 0) inherited.Pop();
                    continue;
                }

                if (reader.NodeType != XmlNodeType.Element) continue;

                string name = reader.LocalName;
                (string? parentFill, bool parentSkip) = inherited.Count > 0 ? inherited.Peek() : (null, false);
                string? own = Property(reader, "fill", css);
                string? effective = own ?? parentFill;
                bool skip = parentSkip || SkipContainers.Contains(name)
                    || IsFaint(Property(reader, "opacity", css)) || IsFaint(Property(reader, "fill-opacity", css));
                bool empty = reader.IsEmptyElement;
                string? d = Drawable.Contains(name) ? reader.GetAttribute("d") : null;
                if (!empty) inherited.Push((effective, skip));

                if (!Drawable.Contains(name) || skip) continue;
                if (name == "path" && string.IsNullOrWhiteSpace(d)) continue;

                drawables++;
                double w = name == "path" ? Math.Max(1, d!.Length) : 1;
                if (effective is null)
                {
                    noFill++;
                    Add(0, 0, 0, 1);
                    continue;
                }

                var rgb = ParseColor(effective);
                if (rgb is not { } c) continue;
                Add(WcagContrast.Linearize(c.R / 255.0), WcagContrast.Linearize(c.G / 255.0), WcagContrast.Linearize(c.B / 255.0), c.A);

                void Add(double lr, double lg, double lb, double alpha)
                {
                    double ww = w * alpha;
                    r += lr * ww;
                    g += lg * ww;
                    b += lb * ww;
                    weight += ww;
                }
            }
        }
        catch (Exception)
        {
            return null;
        }

        if (drawables == 0 || weight <= 0) return null;
        string source = noFill == drawables ? LogoTone.SourceSvgDefaultBlack : LogoTone.SourceSvgFill;
        return LogoTone.FromLuminance(WcagContrast.FromLinear(r / weight, g / weight, b / weight), source);
    }

    private static readonly HashSet<string> SkipContainers = new(StringComparer.Ordinal)
    {
        "defs", "clipPath", "mask", "symbol", "pattern", "marker",
    };

    // Budgets that keep a hostile <style> block from multiplying selectors by declarations.
    private const int MaxSelectorsPerRule = 16;
    private const int MaxStyleEntries = 2_000;

    private static readonly HashSet<string> RelevantProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "fill", "fill-opacity", "opacity", "display", "visibility", "stroke",
    };

    private sealed record CssRules(
        Dictionary<string, Dictionary<string, string>> ByClass,
        Dictionary<string, Dictionary<string, string>> ByTag);

    private static readonly Regex StyleBlock = new(
        @"<style\b[^>]*>(.*?)</style>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    private static readonly Regex CssRule = new(
        @"([^{}]+)\{([^{}]*)\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>Simple <c>.cls{...}</c> / <c>tag{...}</c> rules of the first style blocks; bounded, never throws.</summary>
    private static CssRules ReadStyleRules(ReadOnlySpan<byte> bytes)
    {
        var byClass = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var byTag = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        try
        {
            string text = System.Text.Encoding.UTF8.GetString(bytes);
            int blocks = 0;
            int entries = 0;
            foreach (Match block in StyleBlock.Matches(text))
            {
                if (++blocks > 4) break;
                string css = block.Groups[1].Value;
                if (css.Length > 32_768) css = css[..32_768];
                int rules = 0;
                foreach (Match rule in CssRule.Matches(css))
                {
                    if (++rules > 200) break;
                    var decls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string part in rule.Groups[2].Value.Split(';'))
                    {
                        int colon = part.IndexOf(':');
                        if (colon <= 0) continue;
                        string name = part[..colon].Trim();
                        // Only the presentation properties the profiler reads are kept.
                        if (!RelevantProperties.Contains(name)) continue;
                        decls[name] = part[(colon + 1)..].Trim();
                    }

                    if (decls.Count == 0) continue;

                    int selectors = 0;
                    foreach (string rawSelector in rule.Groups[1].Value.Split(','))
                    {
                        if (++selectors > MaxSelectorsPerRule) break;
                        if (++entries > MaxStyleEntries) break;
                        string sel = rawSelector.Trim();
                        if (sel.Length == 0 || sel.Length > 64) continue;
                        bool isClass = sel[0] == '.';
                        string key = isClass ? sel[1..] : sel;
                        if (key.Length == 0 || !key.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')) continue;
                        var target = isClass ? byClass : byTag;
                        if (!target.TryGetValue(key, out var existing)) target[key] = existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var kv in decls) existing[kv.Key] = kv.Value;
                    }
                }
            }
        }
        catch (Exception)
        {
        }

        return new CssRules(byClass, byTag);
    }

    /// <summary>
    /// A presentation property of this element: its style attribute, else a class rule, else a tag rule,
    /// else the plain attribute. Null/blank means it declares none.
    /// </summary>
    private static string? Property(XmlReader reader, string property, CssRules css)
    {
        string? value = null;
        string? style = reader.GetAttribute("style");
        if (!string.IsNullOrWhiteSpace(style))
        {
            foreach (string part in style.Split(';'))
            {
                int colon = part.IndexOf(':');
                if (colon > 0 && part[..colon].Trim().Equals(property, StringComparison.OrdinalIgnoreCase))
                    value = part[(colon + 1)..].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            string? classes = reader.GetAttribute("class");
            if (!string.IsNullOrWhiteSpace(classes))
            {
                foreach (string cls in classes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (css.ByClass.TryGetValue(cls, out var decls) && decls.TryGetValue(property, out string? v) && !string.IsNullOrWhiteSpace(v))
                        value = v;
            }
        }

        if (string.IsNullOrWhiteSpace(value) && css.ByTag.TryGetValue(reader.LocalName, out var tagDecls)
            && tagDecls.TryGetValue(property, out string? tv))
            value = tv;

        if (string.IsNullOrWhiteSpace(value)) value = reader.GetAttribute(property)?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool IsFaint(string? opacity)
    {
        if (string.IsNullOrWhiteSpace(opacity)) return false;
        string t = opacity.Trim();
        bool percent = t.EndsWith('%');
        if (!TryNumber(t, out double n)) return false;
        if (percent) n /= 100;
        return n < 0.5;
    }

    private static (byte R, byte G, byte B, double A)? ParseColor(string value)
    {
        string v = value.Trim().ToLowerInvariant();
        if (v is "none" or "transparent" or "currentcolor" or "inherit" || v.StartsWith("url(", StringComparison.Ordinal))
            return null;

        if (v.StartsWith('#'))
        {
            string hex = v[1..];
            if (!hex.All(Uri.IsHexDigit)) return null;
            switch (hex.Length)
            {
                case 3:
                case 4:
                    return (Hex(hex[0], hex[0]), Hex(hex[1], hex[1]), Hex(hex[2], hex[2]), hex.Length == 4 ? Hex(hex[3], hex[3]) / 255.0 : 1);
                case 6:
                case 8:
                    return (Hex(hex[0], hex[1]), Hex(hex[2], hex[3]), Hex(hex[4], hex[5]), hex.Length == 8 ? Hex(hex[6], hex[7]) / 255.0 : 1);
                default:
                    return null;
            }
        }

        if (v.StartsWith("rgb", StringComparison.Ordinal))
        {
            int open = v.IndexOf('(');
            int close = v.LastIndexOf(')');
            if (open < 0 || close < open) return null;
            string[] parts = v[(open + 1)..close].Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is < 3 or > 4) return null;
            var ch = new byte[3];
            for (int i = 0; i < 3; i++)
            {
                if (!TryNumber(parts[i], out double n)) return null;
                if (parts[i].EndsWith('%')) n = n * 255 / 100;
                ch[i] = (byte)Math.Clamp(Math.Round(n), 0, 255);
            }

            double a = 1;
            if (parts.Length == 4)
            {
                if (!TryNumber(parts[3], out a)) return null;
                if (parts[3].EndsWith('%')) a /= 100;
                a = Math.Clamp(a, 0, 1);
            }

            return a <= 0 ? null : (ch[0], ch[1], ch[2], a);
        }

        return v switch
        {
            "black" => (0, 0, 0, 1),
            "white" => (255, 255, 255, 1),
            "gray" or "grey" => (128, 128, 128, 1),
            "red" => (255, 0, 0, 1),
            "blue" => (0, 0, 255, 1),
            "green" => (0, 128, 0, 1),
            "yellow" => (255, 255, 0, 1),
            "orange" => (255, 165, 0, 1),
            "purple" => (128, 0, 128, 1),
            _ => null,
        };
    }

    private static bool TryNumber(string s, out double n) =>
        double.TryParse(s.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out n) && double.IsFinite(n);

    private static byte Hex(char hi, char lo) =>
        (byte)((Convert.ToInt32(hi.ToString(), 16) * 16) + Convert.ToInt32(lo.ToString(), 16));
}
