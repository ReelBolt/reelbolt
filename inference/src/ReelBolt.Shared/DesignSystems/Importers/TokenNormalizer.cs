using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.DesignSystems.Color;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// Turns the raw values an importer finds into the canonical <c>$value</c> forms
/// <see cref="DesignDocumentValidator"/> requires. Pure and total: every method answers null rather
/// than throwing, and reports why through an <see cref="ImportDiagnostic"/>.
/// </summary>
public static class TokenNormalizer
{
    /// <summary>The largest absolute dimension accepted.</summary>
    public const double MaxDimension = 10000;

    /// <summary>The largest duration accepted, expressed in milliseconds.</summary>
    public const double MaxDurationMs = 60000;

    /// <summary>
    /// Normalizes <paramref name="raw"/> according to <paramref name="type"/>. Null is returned when
    /// the value cannot be understood; <paramref name="diagnostic"/> then explains why.
    /// </summary>
    public static JsonNode? Normalize(string type, JsonNode? raw, out ImportDiagnostic? diagnostic)
    {
        switch (type)
        {
            case "color":
                return NormalizeColor(raw, out diagnostic);
            case "dimension":
                return NormalizeDimension(raw, out diagnostic);
            case "fontFamily":
                return NormalizeFontFamily(raw, out diagnostic);
            case "fontWeight":
                return NormalizeFontWeight(raw, out diagnostic);
            case "duration":
                return NormalizeDuration(raw, out diagnostic);
            case "cubicBezier":
                return NormalizeCubicBezier(raw, out diagnostic);
            case "number":
                return NormalizeNumber(raw, out diagnostic);
            case "shadow":
                return NormalizeShadow(raw, out diagnostic);
            case "keyword":
                return NormalizeKeyword(raw, out diagnostic);
            case "typography":
                return NormalizeTypography(raw, out diagnostic);
            default:
                diagnostic = Warn("UNKNOWN_TYPE", $"cannot normalize '{type}'");
                return null;
        }
    }

    /// <summary><c>"16px"</c> -> <c>{"value":16,"unit":"px"}</c>; a bare number defaults to pixels.</summary>
    public static JsonNode? NormalizeDimension(JsonNode? raw, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;

        if (raw is JsonValue value)
        {
            if (value.GetValueKind() == JsonValueKind.Number)
            {
                if (!CanonicalJson.TryGetNumber(raw, out double number) || !double.IsFinite(number) || Math.Abs(number) > MaxDimension)
                {
                    diagnostic = Warn("DIMENSION_INVALID", "dimension must be a finite number within +/-10000");
                    return null;
                }

                return Dimension(number, "px");
            }

            if (value.GetValueKind() == JsonValueKind.String)
                return TryParseDimensionString(value.GetValue<string>(), out JsonNode? parsed, out diagnostic) ? parsed : null;
        }

        // DTCG 2025.10: { "value": 16, "unit": "px" }.
        if (raw is JsonObject objectForm)
        {
            if (!CanonicalJson.TryGetNumber(objectForm["value"], out double objectNumber)
                || !double.IsFinite(objectNumber)
                || Math.Abs(objectNumber) > MaxDimension)
            {
                diagnostic = Warn("DIMENSION_INVALID", "dimension must be a finite number within +/-10000");
                return null;
            }

            string? objectUnit = StringOf(objectForm["unit"]);
            if (objectUnit is not ("px" or "rem" or "em" or "%"))
            {
                diagnostic = Warn("DIMENSION_INVALID", "dimension unit must be px, rem, em or %");
                return null;
            }

            return Dimension(objectNumber, objectUnit);
        }

        diagnostic = Warn("DIMENSION_INVALID", "dimension must be a number, a string or a {value, unit} object");
        return null;
    }

    /// <summary>A string becomes a one-element array; a comma-separated string is split; an array must hold 1..5 names.</summary>
    public static JsonNode? NormalizeFontFamily(JsonNode? raw, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;
        var families = new List<string>();

        switch (raw)
        {
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                foreach (string part in value.GetValue<string>().Split(','))
                    AddFamily(part, families);
                break;

            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    if (item is not JsonValue entry || entry.GetValueKind() != JsonValueKind.String)
                    {
                        diagnostic = Warn("FONT_FAMILY_INVALID", "family array entries must be strings");
                        return null;
                    }

                    AddFamily(entry.GetValue<string>(), families);
                }

                break;

            default:
                diagnostic = Warn("FONT_FAMILY_INVALID", "font family must be a string or an array");
                return null;
        }

        if (families.Count is < 1 or > 5)
        {
            diagnostic = Warn("FONT_FAMILY_INVALID", "font family must hold between 1 and 5 names");
            return null;
        }

        var result = new JsonArray();
        foreach (string family in families)
            result.Add(family);
        return result;
    }

    /// <summary><c>normal</c>=400, <c>bold</c>=700, <c>lighter</c>=300, <c>bolder</c>=800; a whole number in 1..1000 otherwise.</summary>
    public static JsonNode? NormalizeFontWeight(JsonNode? raw, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;

        if (raw is JsonValue value)
        {
            if (value.GetValueKind() == JsonValueKind.String)
            {
                string text = value.GetValue<string>().Trim();
                int? keyword = text.ToLowerInvariant() switch
                {
                    "normal" => 400,
                    "bold" => 700,
                    "lighter" => 300,
                    "bolder" => 800,
                    _ => null,
                };

                if (keyword is not null)
                    return JsonValue.Create(keyword.Value);

                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                {
                    diagnostic = Warn("FONT_WEIGHT_INVALID", "font weight must be a keyword or a number");
                    return null;
                }

                return Weight(parsed, out diagnostic);
            }

            if (value.GetValueKind() == JsonValueKind.Number)
            {
                if (!CanonicalJson.TryGetNumber(raw, out double number))
                {
                    diagnostic = Warn("FONT_WEIGHT_INVALID", "font weight must be finite");
                    return null;
                }

                return Weight(number, out diagnostic);
            }
        }

        diagnostic = Warn("FONT_WEIGHT_INVALID", "font weight must be a keyword or a number");
        return null;
    }

    /// <summary><c>"200ms"</c> -> <c>{"value":200,"unit":"ms"}</c>; <c>"0.2s"</c> -> <c>{"value":0.2,"unit":"s"}</c>.</summary>
    public static JsonNode? NormalizeDuration(JsonNode? raw, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;

        if (raw is JsonValue value)
        {
            if (value.GetValueKind() == JsonValueKind.Number)
            {
                if (!CanonicalJson.TryGetNumber(raw, out double number) || !double.IsFinite(number) || number < 0 || number > MaxDurationMs)
                {
                    diagnostic = Warn("DURATION_INVALID", "duration must be between 0 and 60000 ms");
                    return null;
                }

                return Duration(number, "ms");
            }

            if (value.GetValueKind() == JsonValueKind.String)
            {
                string text = value.GetValue<string>().Trim();
                string unit = "ms";

                if (text.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
                    text = text[..^2];
                else if (text.EndsWith("s", StringComparison.OrdinalIgnoreCase))
                {
                    unit = "s";
                    text = text[..^1];
                }

                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) || !double.IsFinite(parsed) || parsed < 0)
                {
                    diagnostic = Warn("DURATION_INVALID", "duration must be a finite, non-negative number");
                    return null;
                }

                double milliseconds = unit == "s" ? parsed * 1000.0 : parsed;
                if (milliseconds > MaxDurationMs)
                {
                    diagnostic = Warn("DURATION_INVALID", "duration exceeds 60000 ms");
                    return null;
                }

                return Duration(parsed, unit);
            }
        }

        // DTCG 2025.10: { "value": 200, "unit": "ms" }.
        if (raw is JsonObject objectForm)
        {
            string? objectUnit = StringOf(objectForm["unit"]);
            if (objectUnit is not ("ms" or "s"))
            {
                diagnostic = Warn("DURATION_INVALID", "duration unit must be ms or s");
                return null;
            }

            if (!CanonicalJson.TryGetNumber(objectForm["value"], out double objectNumber) || !double.IsFinite(objectNumber) || objectNumber < 0)
            {
                diagnostic = Warn("DURATION_INVALID", "duration must be a finite, non-negative number");
                return null;
            }

            double objectMilliseconds = objectUnit == "s" ? objectNumber * 1000.0 : objectNumber;
            if (objectMilliseconds > MaxDurationMs)
            {
                diagnostic = Warn("DURATION_INVALID", "duration exceeds 60000 ms");
                return null;
            }

            return Duration(objectNumber, objectUnit);
        }

        diagnostic = Warn("DURATION_INVALID", "duration must be a number, a string or a {value, unit} object");
        return null;
    }

    /// <summary>Exactly four finite numbers, with x1 and x2 inside [0,1].</summary>
    public static JsonNode? NormalizeCubicBezier(JsonNode? raw, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;
        if (raw is not JsonArray array || array.Count != 4)
        {
            diagnostic = Warn("CUBIC_BEZIER_INVALID", "cubic bezier must hold exactly four numbers");
            return null;
        }

        var numbers = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (!CanonicalJson.TryGetNumber(array[i], out numbers[i]) || !double.IsFinite(numbers[i]))
            {
                diagnostic = Warn("CUBIC_BEZIER_INVALID", "cubic bezier entries must be finite numbers");
                return null;
            }
        }

        if (numbers[0] < 0 || numbers[0] > 1 || numbers[2] < 0 || numbers[2] > 1)
        {
            diagnostic = Warn("CUBIC_BEZIER_INVALID", "cubic bezier x1 and x2 must be inside [0,1]");
            return null;
        }

        var result = new JsonArray();
        foreach (double number in numbers)
            result.Add(JsonValue.Create(number));
        return result;
    }

    /// <summary>A finite number within +/-1e6.</summary>
    public static JsonNode? NormalizeNumber(JsonNode? raw, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;
        if (!CanonicalJson.TryGetNumber(raw, out double number) || !double.IsFinite(number) || Math.Abs(number) > 1e6)
        {
            diagnostic = Warn("NUMBER_INVALID", "number must be finite and within +/-1000000");
            return null;
        }

        return JsonValue.Create(number);
    }

    /// <summary>A lowercase keyword: <c>^[a-z][a-z0-9-]{0,39}$</c>.</summary>
    public static JsonNode? NormalizeKeyword(JsonNode? raw, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;

        if (raw is JsonValue value
            && value.GetValueKind() == JsonValueKind.String
            && IsKeyword(value.GetValue<string>()))
        {
            return JsonValue.Create(value.GetValue<string>());
        }

        diagnostic = Warn("KEYWORD_INVALID", "keyword must match ^[a-z][a-z0-9-]{0,39}$");
        return null;
    }

    private static bool IsKeyword(string text)
    {
        if (text.Length is < 1 or > 40 || text[0] < 'a' || text[0] > 'z')
            return false;

        for (int i = 1; i < text.Length; i++)
        {
            char c = text[i];
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-'))
                return false;
        }

        return true;
    }

    /// <summary>
    /// A shadow entry (object), an array of at most four entries, the literal <c>none</c> (an empty
    /// shadow), or a best-effort CSS string. Anything that cannot yield a colour and measurable
    /// offsets answers null.
    /// </summary>
    public static JsonNode? NormalizeShadow(JsonNode? raw, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;

        switch (raw)
        {
            case JsonObject entry:
                return ShadowEntry(entry, out diagnostic);

            case JsonArray array:
                if (array.Count > 4)
                {
                    diagnostic = Warn("SHADOW_INVALID", "a shadow array must hold between 0 and 4 entries");
                    return null;
                }

                var shadows = new JsonArray();
                foreach (JsonNode? item in array)
                {
                    if (item is not JsonObject itemObject)
                    {
                        diagnostic = Warn("SHADOW_INVALID", "shadow array entries must be objects");
                        return null;
                    }

                    JsonNode? normalized = ShadowEntry(itemObject, out ImportDiagnostic? entryDiagnostic);
                    if (normalized is null)
                    {
                        diagnostic = entryDiagnostic;
                        return null;
                    }

                    shadows.Add(normalized);
                }

                return shadows;

            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                string css = value.GetValue<string>().Trim();
                if (string.Equals(css, "none", StringComparison.OrdinalIgnoreCase))
                    return new JsonArray();

                return CssShadow(css, out diagnostic);

            default:
                diagnostic = Warn("SHADOW_INVALID", "shadow must be an object, an array or a CSS string");
                return null;
        }
    }

    /// <summary>
    /// Normalizes a colour in either canonical form: a CSS string, or a DTCG 2025.10 colour object
    /// with <c>hex</c> and/or <c>components</c>.
    /// </summary>
    public static JsonNode? NormalizeColor(JsonNode? raw, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;

        if (raw is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            if (ColorConvert.TryToSrgbHex(value.GetValue<string>(), out string hex, out _))
                return Color(hex);

            diagnostic = Warn("COLOR_INVALID", "colour string could not be converted to sRGB");
            return null;
        }

        if (raw is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("hex", out JsonNode? hexNode)
                && hexNode is JsonValue hexValue
                && hexValue.GetValueKind() == JsonValueKind.String
                && ColorConvert.TryToSrgbHex(hexValue.GetValue<string>(), out string fromHex, out _))
            {
                return Color(fromHex);
            }

            string? colorSpace = StringOf(obj["colorSpace"]);
            if ((colorSpace is null || string.Equals(colorSpace, "srgb", StringComparison.OrdinalIgnoreCase))
                && obj.TryGetPropertyValue("components", out JsonNode? components)
                && components is JsonArray array
                && TryComponentsToHex(array, out string fromComponents))
            {
                return Color(fromComponents);
            }

            diagnostic = Warn("COLOR_INVALID", "colour object is not a supported sRGB value");
            return null;
        }

        diagnostic = Warn("COLOR_INVALID", "colour must be a string or an object");
        return null;
    }

    /// <summary>Normalizes the sub-values of a composite typography token.</summary>
    public static JsonNode? NormalizeTypography(JsonNode? raw, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;
        if (raw is not JsonObject obj)
        {
            diagnostic = Warn("TYPOGRAPHY_INVALID", "typography must be an object");
            return null;
        }

        var result = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> property in obj)
        {
            JsonNode? normalized;
            switch (property.Key)
            {
                case "fontFamily":
                    normalized = NormalizeFontFamily(property.Value, out diagnostic);
                    break;
                case "fontSize":
                case "letterSpacing":
                    normalized = NormalizeDimension(property.Value, out diagnostic);
                    break;
                case "fontWeight":
                    normalized = NormalizeFontWeight(property.Value, out diagnostic);
                    break;
                case "lineHeight":
                    normalized = NormalizeLineHeight(property.Value, out diagnostic);
                    break;
                default:
                    continue;
            }

            if (normalized is null)
                return null;

            result[property.Key] = normalized;
        }

        if (result.Count == 0)
        {
            diagnostic = Warn("TYPOGRAPHY_INVALID", "typography carries no recognizable sub-values");
            return null;
        }

        return result;
    }

    private static JsonNode? NormalizeLineHeight(JsonNode? raw, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;
        if (raw is JsonValue value && value.GetValueKind() == JsonValueKind.Number)
            return NormalizeNumber(raw, out diagnostic);

        return NormalizeDimension(raw, out diagnostic);
    }

    private static JsonNode? ShadowEntry(JsonObject entry, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;

        if (!entry.TryGetPropertyValue("color", out JsonNode? colorNode))
        {
            diagnostic = Warn("SHADOW_INVALID", "shadow entry has no colour");
            return null;
        }

        JsonNode? color = NormalizeColor(colorNode, out ImportDiagnostic? colorDiagnostic);
        if (color is null)
        {
            diagnostic = colorDiagnostic;
            return null;
        }

        var result = new JsonObject { ["color"] = color };
        foreach (string key in ShadowDimensionKeys)
        {
            if (!entry.TryGetPropertyValue(key, out JsonNode? dimensionNode))
                continue;

            JsonNode? dimension = NormalizeDimension(dimensionNode, out ImportDiagnostic? dimensionDiagnostic);
            if (dimension is null)
            {
                diagnostic = dimensionDiagnostic;
                return null;
            }

            result[key] = dimension;
        }

        if (entry.TryGetPropertyValue("inset", out JsonNode? inset)
            && inset is JsonValue insetValue
            && (insetValue.GetValueKind() == JsonValueKind.True || insetValue.GetValueKind() == JsonValueKind.False))
        {
            result["inset"] = JsonValue.Create(insetValue.GetValue<bool>());
        }

        return result;
    }

    private static readonly string[] ShadowDimensionKeys = ["offsetX", "offsetY", "blur", "spread"];

    private static JsonNode? CssShadow(string css, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;
        var tokens = TokenizeCss(css);
        bool inset = false;
        JsonNode? color = null;
        var dimensions = new List<JsonNode>();

        foreach (string token in tokens)
        {
            if (token.Equals("inset", StringComparison.OrdinalIgnoreCase))
            {
                inset = true;
                continue;
            }

            if (color is null && (token.Length > 0 && (token[0] == '#' || token.Contains('('))))
            {
                JsonNode? parsedColor = NormalizeColor(JsonValue.Create(token), out _);
                if (parsedColor is not null)
                {
                    color = parsedColor;
                    continue;
                }
            }

            if (TryParseDimensionString(token, out JsonNode? dimension, out _) && dimension is not null)
                dimensions.Add(dimension);
        }

        if (color is null)
        {
            diagnostic = Warn("SHADOW_INVALID", "CSS shadow has no recognizable colour");
            return null;
        }

        var result = new JsonObject { ["color"] = color };
        for (int i = 0; i < dimensions.Count && i < ShadowDimensionKeys.Length; i++)
            result[ShadowDimensionKeys[i]] = dimensions[i];

        if (inset)
            result["inset"] = true;

        return result;
    }

    private static List<string> TokenizeCss(string css)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        int depth = 0;

        foreach (char c in css)
        {
            if (c == '(')
                depth++;
            else if (c == ')')
                depth = Math.Max(0, depth - 1);

            if (char.IsWhiteSpace(c) && depth == 0)
            {
                if (sb.Length > 0)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }

                continue;
            }

            sb.Append(c);
        }

        if (sb.Length > 0)
            tokens.Add(sb.ToString());

        return tokens;
    }

    private static bool TryParseDimensionString(string? text, out JsonNode? value, out ImportDiagnostic? diagnostic)
    {
        value = null;
        diagnostic = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            diagnostic = Warn("DIMENSION_INVALID", "dimension is empty");
            return false;
        }

        string trimmed = text.Trim();
        string unit = "px";

        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^2];
        else if (trimmed.EndsWith("rem", StringComparison.OrdinalIgnoreCase))
        {
            unit = "rem";
            trimmed = trimmed[..^3];
        }
        else if (trimmed.EndsWith("em", StringComparison.OrdinalIgnoreCase))
        {
            unit = "em";
            trimmed = trimmed[..^2];
        }
        else if (trimmed.EndsWith("%", StringComparison.Ordinal))
        {
            unit = "%";
            trimmed = trimmed[..^1];
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            || !double.IsFinite(number)
            || Math.Abs(number) > MaxDimension)
        {
            diagnostic = Warn("DIMENSION_INVALID", "dimension must be a finite number within +/-10000");
            return false;
        }

        value = Dimension(number, unit);
        return true;
    }

    private static JsonNode? Weight(double number, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;
        if (number < 1 || number > 1000 || number != Math.Floor(number) || !double.IsFinite(number))
        {
            diagnostic = Warn("FONT_WEIGHT_INVALID", "font weight must be a whole number between 1 and 1000");
            return null;
        }

        return JsonValue.Create((int)number);
    }

    private static bool TryComponentsToHex(JsonArray components, out string hex)
    {
        hex = string.Empty;
        if (components.Count < 3)
            return false;

        var channels = new byte[3];
        for (int i = 0; i < 3; i++)
        {
            JsonNode? node = components[i];
            double value;

            if (node is JsonValue valueNode && valueNode.GetValueKind() == JsonValueKind.String)
            {
                string text = valueNode.GetValue<string>().Trim();
                if (!text.EndsWith("%", StringComparison.Ordinal)
                    || !double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
                {
                    return false;
                }

                value = percent / 100.0;
            }
            else if (!CanonicalJson.TryGetNumber(node, out value))
            {
                return false;
            }

            if (!double.IsFinite(value))
                return false;

            channels[i] = (byte)Math.Clamp((int)Math.Round(Math.Clamp(value, 0.0, 1.0) * 255.0, MidpointRounding.AwayFromZero), 0, 255);
        }

        hex = "#" + Convert.ToHexStringLower(channels);
        return true;
    }

    private static void AddFamily(string? raw, List<string> families)
    {
        string? clean = DesignText.Sanitize(raw, 80);
        if (clean is not null && DesignText.IsSafeFontFamily(clean))
            families.Add(clean);
    }

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static JsonObject Dimension(double value, string unit) =>
        new() { ["value"] = JsonValue.Create(value), ["unit"] = unit };

    private static JsonObject Duration(double value, string unit) =>
        new() { ["value"] = JsonValue.Create(value), ["unit"] = unit };

    private static JsonObject Color(string hex) =>
        new() { ["hex"] = hex, ["colorSpace"] = "srgb" };

    private static ImportDiagnostic Warn(string code, string message) =>
        new(ImportDiagnosticSeverity.Warn, code, string.Empty, message);
}
