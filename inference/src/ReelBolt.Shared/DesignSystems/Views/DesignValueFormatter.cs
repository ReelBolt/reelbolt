using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReelBolt.Shared.DesignSystems.Views;

/// <summary>
/// Formats one design-token value as the short string an agent block shows. Every member is pure
/// and total: an unrecognised type, a wrong-shaped value or a missing member answers null so the
/// caller can skip the line. Alias values must already be resolved by the caller.
/// </summary>
public static class DesignValueFormatter
{
    /// <summary>
    /// The value string for <paramref name="type"/> (<c>color</c>, <c>dimension</c>,
    /// <c>fontFamily</c>, <c>fontWeight</c>, <c>duration</c>, <c>cubicBezier</c> or <c>number</c>),
    /// or null. <c>shadow</c> and <c>typography</c> deliberately answer null so callers skip them.
    /// </summary>
    public static string? Format(string type, JsonNode? value)
    {
        if (value is null || string.IsNullOrEmpty(type))
            return null;

        return type switch
        {
            "color" => FormatColor(value),
            "dimension" => FormatDimension(value),
            "fontFamily" => FormatFontFamily(value),
            "fontWeight" => FormatNumber(value),
            "duration" => FormatDuration(value),
            "cubicBezier" => FormatCubicBezier(value),
            "number" => FormatNumber(value),
            _ => null,
        };
    }

    private static string? FormatColor(JsonNode value)
    {
        if (value is not JsonObject color)
            return null;
        if (color["hex"] is not JsonValue hex || hex.GetValueKind() != JsonValueKind.String)
            return null;

        string text = hex.GetValue<string>();
        if (text.Length is not (7 or 9) || text[0] != '#')
            return null;

        return text.ToUpperInvariant();
    }

    private static string? FormatDimension(JsonNode value)
    {
        if (value is not JsonObject dimension)
            return null;
        if (!CanonicalJson.TryGetNumber(dimension["value"], out double number)
            || !TryGetString(dimension["unit"], out string? unit))
            return null;

        return Rounded(number) + unit;
    }

    private static string? FormatFontFamily(JsonNode value)
    {
        if (value is not JsonArray families || families.Count == 0)
            return null;

        var names = new List<string>();
        foreach (JsonNode? entry in families)
        {
            if (!TryGetString(entry, out string? family))
                return null;

            string? safe = DesignText.Sanitize(family, 80);
            if (safe is not null)
                names.Add(safe);
        }

        return names.Count == 0 ? null : string.Join(", ", names);
    }

    private static string? FormatDuration(JsonNode value)
    {
        if (value is not JsonObject duration)
            return null;
        if (!CanonicalJson.TryGetNumber(duration["value"], out double number)
            || !TryGetString(duration["unit"], out string? unit))
            return null;

        double milliseconds = unit switch
        {
            "ms" => number,
            "s" => number * 1000,
            _ => double.NaN,
        };

        return double.IsNaN(milliseconds) ? null : Rounded(milliseconds) + "ms";
    }

    private static string? FormatCubicBezier(JsonNode value)
    {
        if (value is not JsonArray points || points.Count != 4)
            return null;

        var parts = new string[4];
        for (int i = 0; i < 4; i++)
        {
            if (!CanonicalJson.TryGetNumber(points[i], out double number))
                return null;
            parts[i] = Rounded(number);
        }

        return "cubic-bezier(" + string.Join(",", parts) + ")";
    }

    private static string? FormatNumber(JsonNode value) =>
        CanonicalJson.TryGetNumber(value, out double number) ? Rounded(number) : null;

    private static string Rounded(double number) =>
        Math.Round(number, 2).ToString("0.##", CultureInfo.InvariantCulture);

    private static bool TryGetString(JsonNode? node, out string? value)
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
