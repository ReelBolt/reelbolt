using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReelBolt.Shared.DesignSystems;

/// <summary>
/// Pure renderers that turn a canonical <see cref="DesignDocument"/> into the exchange dialects the
/// platform supports: DTCG 2025.10, CSS custom properties (plain <c>:root</c> and Tailwind v4
/// <c>@theme</c>), <c>DESIGN.md</c> and the canonical JSON document. Nothing here performs IO or
/// mutates its input; a document the exporter cannot express simply drops that piece.
///
/// <para>
/// The round-trip behaviour is pinned by <c>RoundTripTests</c>. <see cref="CssLossyTokenTypes"/>
/// and <see cref="DesignMdLossyFields"/> are the lossy lists those tests assert; see the test file
/// for the same lists expressed as <see cref="JsonObject"/> paths.
/// </para>
/// </summary>
public static class DesignExporter
{
    /// <summary>Token kinds a CSS round trip cannot reproduce (<c>shadow</c> only best-effort).</summary>
    public static readonly string[] CssLossyTokenTypes = ["shadow", "typography"];

    /// <summary>
    /// Token kinds a DTCG round trip cannot reproduce. None any more: <see cref="Importers.DtcgImporter"/> reads the
    /// 2025.10 <c>{value, unit}</c> object form of <c>dimension</c> and <c>duration</c>, which also makes the
    /// composite <c>shadow</c> and <c>typography</c> values (built from them) round-trip.
    /// </summary>
    public static readonly string[] DtcgLossyTokenTypes = [];

    /// <summary>DESIGN.md fields a round trip cannot reproduce.</summary>
    public static readonly string[] DesignMdLossyFields =
        ["modes", "assets", "fonts", "shadow", "motion", "component"];

    private static readonly string[] NonTokenRootKeys = ["modes", "voice", "assets", "fonts"];

    private static readonly string[] TypographySubKeys =
        ["fontFamily", "fontSize", "fontWeight", "lineHeight", "letterSpacing"];

    /// <summary>The canonical document itself (the <c>json</c> format); an independent clone.</summary>
    public static JsonObject ToJson(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return (JsonObject)document.DeepClone();
    }

    /// <summary>
    /// DTCG 2025.10: tokens keep their position, colour values gain the
    /// <c>{colorSpace,components,alpha,hex}</c> object form, and the non-token sections
    /// (<c>modes</c>, <c>voice</c>, <c>assets</c>, <c>fonts</c>) move under the root
    /// <c>$extensions.com.reelbolt</c> namespace. Per-token <c>$extensions.com.reelbolt</c>
    /// (provenance and locks) and <c>$description</c> are carried through unchanged.
    /// </summary>
    public static JsonObject ToDtcg(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var output = (JsonObject)document.DeepClone();
        var extension = new JsonObject();
        foreach (string key in NonTokenRootKeys)
        {
            if (!output.TryGetPropertyValue(key, out JsonNode? node) || node is null)
                continue;

            extension[key] = node.DeepClone();
            output.Remove(key);
        }

        foreach (DesignTokenRef token in DesignDocument.Tokens(output))
        {
            if (string.Equals(token.Type, "color", StringComparison.Ordinal))
            {
                if (IsAlias(token.Value, out _))
                    continue;

                JsonNode? expanded = ExpandColor(token.Value);
                if (expanded is not null)
                    token.Token["$value"] = expanded;
            }
            else if (string.Equals(token.Type, "shadow", StringComparison.Ordinal))
            {
                token.Token["$value"] = ExpandShadowColors(token.Value) ?? token.Value.DeepClone();
            }
        }

        if (extension.Count > 0)
        {
            if (output["$extensions"] is not JsonObject extensions)
            {
                extensions = new JsonObject();
                output["$extensions"] = extensions;
            }

            extensions["com.reelbolt"] = extension;
        }

        // Source file ids and repository paths describe the owner's project, not the design: leave them out
        // of the exchange file (the canonical json export keeps them, as it is the document itself).
        StripSourceProvenance(output);
        return output;
    }

    private static void StripSourceProvenance(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["prov"] is JsonObject prov)
                {
                    prov.Remove("sourceId");
                    prov.Remove("loc");
                }

                foreach (KeyValuePair<string, JsonNode?> property in obj)
                    StripSourceProvenance(property.Value);
                break;

            case JsonArray array:
                foreach (JsonNode? item in array)
                    StripSourceProvenance(item);
                break;
        }
    }

    /// <summary>
    /// CSS custom properties. The base block is <c>:root</c> or, when
    /// <paramref name="tailwindV4"/> is set, a Tailwind v4 <c>@theme</c> block; a <c>.dark</c>
    /// block follows whenever <c>modes.dark</c> carries overrides. Every emitted value is
    /// defensively escaped so a hostile token can never close a declaration or a <c>&lt;/style&gt;</c>.
    /// </summary>
    public static string ToCss(JsonObject document, bool tailwindV4)
    {
        ArgumentNullException.ThrowIfNull(document);

        var builder = new StringBuilder();
        builder.Append(tailwindV4 ? "@theme {\n" : ":root {\n");
        foreach (DesignTokenRef token in DesignDocument.Tokens(document))
        {
            if (IsCssSkipped(token.Type))
                continue;

            AppendCssDeclaration(builder, CssVariableName(token.Path, tailwindV4), token.Value, token.Type, document, tailwindV4);
        }

        builder.Append("}\n");

        if (document["modes"] is JsonObject modes
            && modes["dark"] is JsonObject dark
            && dark.Count > 0)
        {
            builder.Append("\n.dark {\n");
            foreach (KeyValuePair<string, JsonNode?> entry in dark)
            {
                string type = TypeOf(DesignDocument.FindToken(document, entry.Key));
                if (IsCssSkipped(type))
                    continue;

                AppendCssDeclaration(builder, CssVariableName(entry.Key, tailwindV4), entry.Value, type, document, tailwindV4);
            }

            builder.Append("}\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// A <c>DESIGN.md</c>: YAML front matter (<c>name</c>, <c>colors</c>, <c>typography</c>,
    /// <c>rounded</c>, <c>spacing</c>) followed by prose sections carrying the voice. The shape is
    /// the one <see cref="Importers.DesignMdImporter"/> reads back.
    /// </summary>
    public static string ToDesignMd(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        string name = TryString(document["name"], out string? parsedName) && !string.IsNullOrEmpty(parsedName)
            ? parsedName!
            : "Design System";

        List<KeyValuePair<string, string>> colors = ColorEntries(document);
        List<KeyValuePair<string, List<KeyValuePair<string, string>>>> typography = TypographyEntries(document);
        List<KeyValuePair<string, string>> rounded = DimensionEntries(document, "radius.");
        List<KeyValuePair<string, string>> spacing = DimensionEntries(document, "spacing.");

        var builder = new StringBuilder();
        builder.Append("---\n");
        builder.Append("name: ").Append(YamlScalar(name)).Append('\n');

        AppendYamlMap(builder, "colors", colors, quoteValues: true);
        AppendTypography(builder, typography);
        AppendYamlMap(builder, "rounded", rounded, quoteValues: false);
        AppendYamlMap(builder, "spacing", spacing, quoteValues: false);
        builder.Append("---\n");

        builder.Append('\n').Append("# ").Append(MdEscape(name)).Append('\n');

        JsonObject? voice = document["voice"] as JsonObject;
        if (TryString(voice?["summary"], out string? summary) && !string.IsNullOrEmpty(summary))
            builder.Append("\n## Overview\n\n").Append(MdEscape(summary!)).Append('\n');

        if (colors.Count > 0)
        {
            builder.Append("\n## Colors\n\n");
            foreach (KeyValuePair<string, string> color in colors)
                builder.Append("- ").Append(MdEscape(color.Key)).Append(": ").Append(MdEscape(color.Value)).Append('\n');
        }

        if (typography.Count > 0)
        {
            builder.Append("\n## Typography\n\n");
            foreach (KeyValuePair<string, List<KeyValuePair<string, string>>> entry in typography)
            {
                builder.Append("- ").Append(MdEscape(entry.Key)).Append(':');
                foreach (KeyValuePair<string, string> sub in entry.Value)
                    builder.Append(' ').Append(MdEscape(sub.Key)).Append('=').Append(MdEscape(sub.Value));
                builder.Append('\n');
            }
        }

        string[] doItems = VoiceList(voice, "do");
        string[] dontItems = VoiceList(voice, "dont");
        if (doItems.Length > 0 || dontItems.Length > 0)
        {
            builder.Append("\n## Do's and Don'ts\n");
            AppendBullets(builder, "Do's", doItems);
            AppendBullets(builder, "Don'ts", dontItems);
        }

        AppendBullets(builder, "Tone", VoiceList(voice, "tone"));

        return builder.ToString();
    }

    /// <summary>
    /// Escapes a value so it cannot terminate the declaration it lives in or a surrounding HTML
    /// <c>&lt;style&gt;</c>. The validator already forbids these characters, so this is a defensive
    /// second line of defence, not the first.
    /// </summary>
    internal static string EscapeCssValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            switch (c)
            {
                case ';':
                case '{':
                case '}':
                case '<':
                case '>':
                case '\\':
                    builder.Append('\\').Append(c);
                    break;
                default:
                    builder.Append(char.IsControl(c) ? ' ' : c);
                    break;
            }
        }

        return builder.ToString();
    }

    private static JsonNode? ExpandColor(JsonNode? value)
    {
        if (value is not JsonObject color)
            return null;

        if (!TryString(color["hex"], out string? hex) || hex is null || !TryParseHex(hex, out int red, out int green, out int blue, out double alpha))
            return null;

        return new JsonObject
        {
            ["colorSpace"] = "srgb",
            ["components"] = new JsonArray
            {
                JsonValue.Create(red / 255.0),
                JsonValue.Create(green / 255.0),
                JsonValue.Create(blue / 255.0),
            },
            ["alpha"] = JsonValue.Create(alpha),
            ["hex"] = hex,
        };
    }

    private static JsonNode? ExpandShadowColors(JsonNode? value)
    {
        if (value is JsonObject entry)
        {
            var clone = (JsonObject)entry.DeepClone();
            if (clone["color"] is JsonObject color && ExpandColor(color) is JsonNode expanded)
                clone["color"] = expanded;

            return clone;
        }

        if (value is JsonArray array)
        {
            var result = new JsonArray();
            foreach (JsonNode? item in array)
                result.Add(ExpandShadowColors(item));

            return result;
        }

        return value?.DeepClone();
    }

    private static bool TryParseHex(string hex, out int red, out int green, out int blue, out double alpha)
    {
        red = 0;
        green = 0;
        blue = 0;
        alpha = 1;

        if (hex.Length is not (7 or 9) || hex[0] != '#')
            return false;

        if (!TryHex(hex.AsSpan(1, 2), out red)
            || !TryHex(hex.AsSpan(3, 2), out green)
            || !TryHex(hex.AsSpan(5, 2), out blue))
        {
            return false;
        }

        if (hex.Length == 9 && TryHex(hex.AsSpan(7, 2), out int parsedAlpha))
            alpha = parsedAlpha / 255.0;

        return true;
    }

    private static bool TryHex(ReadOnlySpan<char> text, out int value) =>
        int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

    private static void AppendCssDeclaration(StringBuilder builder, string name, JsonNode? value, string type, JsonObject document, bool tailwindV4)
    {
        string text = EscapeCssValue(SerializeCssValue(value, type, document, tailwindV4));
        builder.Append("  ").Append(name).Append(": ").Append(text).Append(";\n");
    }

    private static string SerializeCssValue(JsonNode? value, string type, JsonObject document, bool tailwindV4)
    {
        if (value is null)
            return string.Empty;

        if (IsAlias(value, out string? target) && target is not null)
            return "var(" + CssVariableName(target, tailwindV4) + ")";

        return type switch
        {
            "color" => ColorText(value),
            "dimension" => DimensionText(value),
            "duration" => DimensionText(value),
            "fontFamily" => FontFamilyText(value),
            "fontWeight" => NumberText(Number(value)),
            "number" => NumberText(Number(value)),
            "cubicBezier" => CubicBezierText(value),
            "shadow" => ShadowText(value),
            _ => value.ToJsonString(),
        };
    }

    private static bool IsCssSkipped(string type) =>
        string.Equals(type, "typography", StringComparison.Ordinal);

    private static string CssVariableName(string path, bool tailwindV4)
    {
        // Plain CSS joins the canonical token path; Tailwind v4 uses its own namespaces.
        if (!tailwindV4)
            return "--" + Dash(path);

        if (TryRelative(path, "color.", out string? color))
            return "--color-" + Dash(color!);
        if (TryRelative(path, "typography.family.", out string? family))
            return "--font-" + Dash(family!);
        if (TryRelative(path, "typography.size.", out string? size))
            return "--text-" + Dash(size!);
        if (TryRelative(path, "spacing.", out string? spacing))
            return "--spacing-" + Dash(spacing!);
        if (TryRelative(path, "radius.", out string? radius))
            return "--radius-" + Dash(radius!);
        if (TryRelative(path, "shadow.", out string? shadow))
            return "--shadow-" + Dash(shadow!);
        if (TryRelative(path, "motion.easing.", out string? easing))
            return "--ease-" + Dash(easing!);

        return "--" + Dash(path);
    }

    private static bool TryRelative(string path, string prefix, out string? relative)
    {
        if (path.StartsWith(prefix, StringComparison.Ordinal))
        {
            relative = path[prefix.Length..];
            return true;
        }

        relative = null;
        return false;
    }

    private static string Dash(string path) => path.Replace('.', '-');

    private static string ColorText(JsonNode? value) =>
        value is JsonObject color && TryString(color["hex"], out string? hex) && hex is not null
            ? hex
            : value?.ToJsonString() ?? string.Empty;

    private static string DimensionText(JsonNode? value)
    {
        if (value is JsonObject dimension && TryString(dimension["unit"], out string? unit) && unit is not null)
            return NumberText(Number(dimension["value"])) + unit;

        if (value is JsonValue jsonValue && jsonValue.GetValueKind() == JsonValueKind.Number)
            return NumberText(Number(value));

        return value?.ToJsonString() ?? string.Empty;
    }

    private static string FontFamilyText(JsonNode? value)
    {
        if (value is JsonArray families)
        {
            var names = new List<string>();
            foreach (JsonNode? family in families)
            {
                if (TryString(family, out string? name) && name is not null)
                    names.Add(name);
            }

            return string.Join(", ", names);
        }

        return TryString(value, out string? single) && single is not null ? single : string.Empty;
    }

    private static string CubicBezierText(JsonNode? value)
    {
        if (value is not JsonArray array || array.Count != 4)
            return value?.ToJsonString() ?? string.Empty;

        var points = new List<string>(4);
        foreach (JsonNode? point in array)
            points.Add(NumberText(Number(point)));

        return "cubic-bezier(" + string.Join(", ", points) + ")";
    }

    private static string ShadowText(JsonNode? value)
    {
        if (value is JsonArray array)
        {
            var entries = new List<string>();
            foreach (JsonNode? entry in array)
                entries.Add(ShadowEntryText(entry));

            return string.Join(", ", entries);
        }

        return ShadowEntryText(value);
    }

    private static string ShadowEntryText(JsonNode? value)
    {
        if (value is not JsonObject entry)
            return value?.ToJsonString() ?? string.Empty;

        var parts = new List<string>();
        if (entry["inset"] is JsonValue inset && inset.GetValueKind() == JsonValueKind.True)
            parts.Add("inset");

        parts.Add(DimensionOrZero(entry["offsetX"]));
        parts.Add(DimensionOrZero(entry["offsetY"]));
        parts.Add(DimensionOrZero(entry["blur"]));
        parts.Add(DimensionOrZero(entry["spread"]));
        parts.Add(ColorText(entry["color"]));
        return string.Join(" ", parts);
    }

    private static string DimensionOrZero(JsonNode? value) =>
        value is null ? "0px" : DimensionText(value);

    private static double Number(JsonNode? value) =>
        CanonicalJson.TryGetNumber(value, out double number) ? number : 0;

    private static string NumberText(double value)
    {
        if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
            return ((long)value).ToString(CultureInfo.InvariantCulture);

        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string TypeOf(JsonObject? token) =>
        TryString(token?["$type"], out string? type) && type is not null ? type : string.Empty;

    private static bool IsAlias(JsonNode? value, out string? target)
    {
        target = null;
        if (!TryString(value, out string? text) || text is null)
            return false;

        return DesignDocument.TryParseAlias(text, out target);
    }

    private static List<KeyValuePair<string, string>> ColorEntries(JsonObject document)
    {
        var entries = new List<KeyValuePair<string, string>>();
        foreach (DesignTokenRef token in DesignDocument.Tokens(document))
        {
            if (!string.Equals(token.Type, "color", StringComparison.Ordinal) || IsAlias(token.Value, out _))
                continue;

            if (!TryString((token.Value as JsonObject)?["hex"], out string? hex) || hex is null)
                continue;

            entries.Add(new KeyValuePair<string, string>(LeafKey(token.Path, "color."), hex));
        }

        return entries;
    }

    private static List<KeyValuePair<string, List<KeyValuePair<string, string>>>> TypographyEntries(JsonObject document)
    {
        var entries = new List<KeyValuePair<string, List<KeyValuePair<string, string>>>>();
        foreach (DesignTokenRef token in DesignDocument.Tokens(document))
        {
            string path = token.Path;
            if (!path.StartsWith("typography.", StringComparison.Ordinal))
                continue;

            if (string.Equals(token.Type, "fontFamily", StringComparison.Ordinal))
            {
                entries.Add(Entry("family-" + LeafKey(path, "typography.family."), "fontFamily", FontFamilyText(token.Value)));
            }
            else if (string.Equals(token.Type, "dimension", StringComparison.Ordinal)
                && path.StartsWith("typography.size.", StringComparison.Ordinal))
            {
                entries.Add(Entry("size-" + LeafKey(path, "typography.size."), "fontSize", DimensionText(token.Value)));
            }
            else if (string.Equals(token.Type, "fontWeight", StringComparison.Ordinal)
                && path.StartsWith("typography.weight.", StringComparison.Ordinal))
            {
                entries.Add(Entry("weight-" + LeafKey(path, "typography.weight."), "fontWeight", NumberText(Number(token.Value))));
            }
            else if (path.StartsWith("typography.line-height.", StringComparison.Ordinal))
            {
                entries.Add(Entry("line-height-" + LeafKey(path, "typography.line-height."), "lineHeight", LineHeightText(token.Value)));
            }
            else if (string.Equals(token.Type, "dimension", StringComparison.Ordinal)
                && path.StartsWith("typography.letter-spacing.", StringComparison.Ordinal))
            {
                entries.Add(Entry("letter-spacing-" + LeafKey(path, "typography.letter-spacing."), "letterSpacing", DimensionText(token.Value)));
            }
            else if (string.Equals(token.Type, "typography", StringComparison.Ordinal))
            {
                entries.Add(new KeyValuePair<string, List<KeyValuePair<string, string>>>(
                    LeafKey(path, "typography."),
                    CompositeTypography(token.Value)));
            }
        }

        return entries;
    }

    private static List<KeyValuePair<string, string>> CompositeTypography(JsonNode? value)
    {
        var subs = new List<KeyValuePair<string, string>>();
        if (value is not JsonObject typography)
            return subs;

        foreach (string key in TypographySubKeys)
        {
            if (!typography.TryGetPropertyValue(key, out JsonNode? sub) || sub is null)
                continue;

            subs.Add(new KeyValuePair<string, string>(key, key switch
            {
                "fontFamily" => FontFamilyText(sub),
                "fontSize" => DimensionText(sub),
                "fontWeight" => NumberText(Number(sub)),
                "lineHeight" => LineHeightText(sub),
                _ => DimensionText(sub),
            }));
        }

        return subs;
    }

    private static string LineHeightText(JsonNode? value) =>
        value is JsonValue jsonValue && jsonValue.GetValueKind() == JsonValueKind.Number
            ? NumberText(Number(value))
            : DimensionText(value);

    private static List<KeyValuePair<string, string>> DimensionEntries(JsonObject document, string prefix)
    {
        var entries = new List<KeyValuePair<string, string>>();
        foreach (DesignTokenRef token in DesignDocument.Tokens(document))
        {
            if (!string.Equals(token.Type, "dimension", StringComparison.Ordinal))
                continue;

            if (!token.Path.StartsWith(prefix, StringComparison.Ordinal) || IsAlias(token.Value, out _))
                continue;

            entries.Add(new KeyValuePair<string, string>(LeafKey(token.Path, prefix), DimensionText(token.Value)));
        }

        return entries;
    }

    private static KeyValuePair<string, List<KeyValuePair<string, string>>> Entry(string key, string name, string value) =>
        new(key, [new KeyValuePair<string, string>(name, value)]);

    private static string LeafKey(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.Ordinal)
            ? Dash(path[prefix.Length..])
            : Dash(path);

    private static void AppendYamlMap(StringBuilder builder, string key, List<KeyValuePair<string, string>> entries, bool quoteValues)
    {
        if (entries.Count == 0)
            return;

        builder.Append(key).Append(":\n");
        foreach (KeyValuePair<string, string> entry in entries)
        {
            builder.Append("  ").Append(entry.Key).Append(": ");
            if (quoteValues)
                builder.Append('"').Append(entry.Value).Append('"');
            else
                builder.Append(YamlScalar(entry.Value));
            builder.Append('\n');
        }
    }

    private static void AppendTypography(StringBuilder builder, List<KeyValuePair<string, List<KeyValuePair<string, string>>>> entries)
    {
        if (entries.Count == 0)
            return;

        builder.Append("typography:\n");
        foreach (KeyValuePair<string, List<KeyValuePair<string, string>>> entry in entries)
        {
            builder.Append("  ").Append(entry.Key).Append(":\n");
            foreach (KeyValuePair<string, string> sub in entry.Value)
                builder.Append("    ").Append(sub.Key).Append(": ").Append(YamlScalar(sub.Value)).Append('\n');
        }
    }

    private static void AppendBullets(StringBuilder builder, string heading, string[] items)
    {
        if (items.Length == 0)
            return;

        builder.Append("\n### ").Append(heading).Append("\n\n");
        foreach (string item in items)
            builder.Append("- ").Append(MdEscape(item)).Append('\n');
    }

    /// <summary>
    /// Neutralises Markdown link/image/code syntax and raw HTML in text written into the DESIGN.md prose:
    /// <c>&lt;</c>/<c>&gt;</c> become entities and <c>[</c>, <c>]</c> and the backtick are backslash-escaped.
    /// Without an unescaped <c>]</c> a following <c>(</c> cannot open a link target, so parentheses stay as written.
    /// </summary>
    internal static string MdEscape(string text)
    {
        if (text.IndexOfAny(['<', '>', '[', ']', '`']) < 0)
            return text;

        var builder = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            switch (c)
            {
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '[' or ']' or '`':
                    builder.Append('\\').Append(c);
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    private static string[] VoiceList(JsonObject? voice, string key)
    {
        if (voice?[key] is not JsonArray array)
            return [];

        var items = new List<string>();
        foreach (JsonNode? node in array)
        {
            if (TryString(node, out string? text) && !string.IsNullOrEmpty(text))
                items.Add(text!);
        }

        return [.. items];
    }

    private static string YamlScalar(string value)
    {
        if (value.Length == 0)
            return "\"\"";

        bool quote = value != value.Trim()
            || value.IndexOfAny([':', '#', '[', ']', '{', '}', ',', '&', '*', '!', '|', '>', '@', '`', '\'', '"']) >= 0
            || value.StartsWith('-')
            || value.StartsWith('?');

        return quote
            ? "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : value;
    }

    private static bool TryString(JsonNode? node, out string? value)
    {
        if (node is JsonValue jsonValue && jsonValue.GetValueKind() == JsonValueKind.String)
        {
            value = jsonValue.GetValue<string>();
            return true;
        }

        value = null;
        return false;
    }
}
