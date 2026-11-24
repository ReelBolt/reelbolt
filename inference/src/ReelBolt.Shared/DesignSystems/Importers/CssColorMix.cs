using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// Resolves the narrow slice of <c>color-mix()</c> a design export actually uses: exactly two
/// operands in the sRGB space, each a hex literal or a <c>var(--name)</c> alias into the supplied
/// declaration map. Anything else — another colour space, a nested colour function, a missing
/// alias — answers false rather than guessing. Pure and total: it never throws, reads no IO and
/// has no notion of the network.
/// </summary>
public static class CssColorMix
{
    /// <summary>The deepest alias chain followed before the value is rejected.</summary>
    public const int MaxDepth = 4;

    /// <summary>
    /// Resolves <c>color-mix(in srgb, &lt;color&gt; &lt;p&gt;%, &lt;color&gt; [&lt;q&gt;%])</c>
    /// over hex literals and <c>var(--name)</c> aliases in <paramref name="declarations"/>.
    /// <paramref name="declarations"/> is keyed by the bare custom-property name (without the
    /// leading dashes). The output is <c>#rrggbb</c>, or <c>#rrggbbaa</c> when the mix is
    /// translucent.
    /// </summary>
    public static bool TryResolve(
        string value,
        IReadOnlyDictionary<string, string> declarations,
        out string? hex)
    {
        hex = null;
        declarations ??= Empty;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (!TryResolveValue(value.Trim(), declarations, 0, out Rgba color))
            return false;

        hex = color.ToHex();
        return true;
    }

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static bool TryResolveValue(
        string raw,
        IReadOnlyDictionary<string, string> declarations,
        int depth,
        out Rgba color)
    {
        color = default;
        if (depth > MaxDepth)
            return false;

        string value = raw.Trim();
        if (value.Length == 0)
            return false;

        if (value[0] == '#')
            return TryParseHex(value, out color);

        if (StartsWithFunction(value, "color-mix", out string inner))
            return TryParseMix(inner, declarations, depth, out color);

        if (StartsWithFunction(value, "var", out string reference))
            return TryResolveAlias(reference, declarations, depth + 1, out color);

        return false;
    }

    private static bool TryResolveAlias(
        string reference,
        IReadOnlyDictionary<string, string> declarations,
        int depth,
        out Rgba color)
    {
        color = default;
        if (depth > MaxDepth)
            return false;

        // A fallback would be a second value the grammar does not cover; refuse it rather than pick one.
        if (reference.Contains(',', StringComparison.Ordinal))
            return false;

        string name = reference.Trim();
        if (name.Length <= 2 || !name.StartsWith("--", StringComparison.Ordinal))
            return false;

        name = name[2..].Trim();
        if (name.Length == 0 || !declarations.TryGetValue(name, out string? declared))
            return false;

        return TryResolveValue(declared, declarations, depth, out color);
    }

    private static bool TryParseMix(
        string inner,
        IReadOnlyDictionary<string, string> declarations,
        int depth,
        out Rgba color)
    {
        color = default;

        List<string> parts = SplitTopLevel(inner, ',');
        if (parts.Count != 3)
            return false;

        string space = CollapseWhitespace(parts[0]);
        if (!string.Equals(space, "in srgb", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!TryParseOperand(parts[1], out string firstColor, out double? firstPercent)
            || !TryParseOperand(parts[2], out string secondColor, out double? secondPercent))
        {
            return false;
        }

        // The grammar names each operand as a hex literal or a var alias, never another function: a nested
        // color-mix(...) (or any other function) is rejected rather than recursively resolved.
        if (!IsOperandValue(firstColor) || !IsOperandValue(secondColor))
            return false;

        if (!TryResolveValue(firstColor, declarations, depth, out Rgba left)
            || !TryResolveValue(secondColor, declarations, depth, out Rgba right))
        {
            return false;
        }

        double weightLeft;
        double weightRight;

        if (firstPercent is null && secondPercent is null)
        {
            weightLeft = 0.5;
            weightRight = 0.5;
        }
        else
        {
            double p = firstPercent ?? 100.0 - secondPercent!.Value;
            double q = secondPercent ?? 100.0 - p;
            double total = p + q;
            if (total <= 0 || !double.IsFinite(total))
                return false;

            weightLeft = p / total;
            weightRight = q / total;
        }

        // Premultiplied sRGB interpolation: mix in straight alpha only when both operands are opaque.
        double alpha = weightLeft * left.Alpha + weightRight * right.Alpha;
        if (alpha <= 0)
        {
            color = new Rgba(0, 0, 0, 0);
            return true;
        }

        double red = (weightLeft * left.Red * left.Alpha + weightRight * right.Red * right.Alpha) / alpha;
        double green = (weightLeft * left.Green * left.Alpha + weightRight * right.Green * right.Alpha) / alpha;
        double blue = (weightLeft * left.Blue * left.Alpha + weightRight * right.Blue * right.Alpha) / alpha;

        color = new Rgba(
            (byte)Math.Clamp(Math.Round(red, MidpointRounding.AwayFromZero), 0.0, 255.0),
            (byte)Math.Clamp(Math.Round(green, MidpointRounding.AwayFromZero), 0.0, 255.0),
            (byte)Math.Clamp(Math.Round(blue, MidpointRounding.AwayFromZero), 0.0, 255.0),
            (byte)Math.Clamp(Math.Round(alpha * 255.0, MidpointRounding.AwayFromZero), 0.0, 255.0));
        return true;
    }

    /// <summary>True for a hex literal or a <c>var(--name)</c> alias: the only operand forms the grammar accepts.</summary>
    private static bool IsOperandValue(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Length > 0
            && (trimmed[0] == '#' || StartsWithFunction(trimmed, "var", out _));
    }

    private static bool TryParseOperand(string raw, out string color, out double? percent)
    {
        color = string.Empty;
        percent = null;

        List<string> tokens = SplitTopLevel(raw.Trim(), ' ');
        tokens.RemoveAll(static token => token.Length == 0);
        if (tokens.Count is < 1 or > 2)
            return false;

        color = tokens[0].Trim();
        if (color.Length == 0)
            return false;

        if (tokens.Count == 1)
            return true;

        string measurement = tokens[1].Trim();
        if (!measurement.EndsWith("%", StringComparison.Ordinal))
            return false;

        string number = measurement[..^1].Trim();
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            || !double.IsFinite(parsed)
            || parsed < 0 || parsed > 100)
        {
            return false;
        }

        percent = parsed;
        return true;
    }

    private static bool TryParseHex(string value, out Rgba color)
    {
        color = default;

        string digits = value[1..].ToLowerInvariant();
        if (digits.Length is not (3 or 4 or 6 or 8))
            return false;

        foreach (char c in digits)
        {
            if (!IsHex(c))
                return false;
        }

        if (digits.Length <= 4)
        {
            var expanded = new StringBuilder(digits.Length * 2);
            foreach (char c in digits)
                expanded.Append(c).Append(c);
            digits = expanded.ToString();
        }

        byte red = (byte)(HexValue(digits[0]) * 16 + HexValue(digits[1]));
        byte green = (byte)(HexValue(digits[2]) * 16 + HexValue(digits[3]));
        byte blue = (byte)(HexValue(digits[4]) * 16 + HexValue(digits[5]));
        byte alpha = digits.Length == 8 ? (byte)(HexValue(digits[6]) * 16 + HexValue(digits[7])) : (byte)255;

        color = new Rgba(red, green, blue, alpha);
        return true;
    }

    private static bool StartsWithFunction(string value, string name, out string inner)
    {
        inner = string.Empty;
        int open = value.IndexOf('(');
        if (open <= 0 || value[^1] != ')')
            return false;

        if (!string.Equals(value[..open].Trim(), name, StringComparison.OrdinalIgnoreCase))
            return false;

        inner = value[(open + 1)..^1];
        return true;
    }

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        int depth = 0;

        foreach (char c in text)
        {
            if (c == '(')
                depth++;
            else if (c == ')' && depth > 0)
                depth--;

            if (depth == 0 && c == separator)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        parts.Add(current.ToString());
        return parts;
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool pendingSpace = false;

        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');

    private static int HexValue(char c) => c <= '9' ? c - '0' : c - 'a' + 10;

    private readonly record struct Rgba(byte Red, byte Green, byte Blue, byte Alpha)
    {
        public string ToHex() =>
            Alpha == 255
                ? $"#{Red:x2}{Green:x2}{Blue:x2}"
                : $"#{Red:x2}{Green:x2}{Blue:x2}{Alpha:x2}";
    }
}
