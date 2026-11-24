using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReelBolt.Shared.DesignSystems;

/// <summary>
/// Turns a JSON tree into a single canonical byte string so two structurally equal
/// documents hash the same whatever order their keys were written in.
///
/// <para>
/// Object keys are sorted with <see cref="StringComparer.Ordinal"/> at every level, arrays keep
/// their order, numbers are normalised through their own text (never through
/// <c>TryGetValue&lt;double&gt;</c>, which is false for an int-backed node), and strings and keys are
/// escaped with the relaxed encoder so a value survives unchanged. The recursion is bounded:
/// anything deeper than <see cref="MaxDepth"/> is written as <c>null</c>.
/// </para>
/// </summary>
public static class CanonicalJson
{
    /// <summary>The deepest object/array nesting serialized; deeper nodes become <c>null</c>.</summary>
    public const int MaxDepth = 128;

    /// <summary>The canonical JSON text for <paramref name="node"/>; a null node is <c>null</c>.</summary>
    public static string Serialize(JsonNode? node)
    {
        var sb = new StringBuilder();
        WriteNode(sb, node, 0);
        return sb.ToString();
    }

    /// <summary>The lowercase hex SHA-256 of <paramref name="canonical"/> encoded as UTF-8.</summary>
    public static string Sha256Hex(string canonical) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    /// <summary>The lowercase hex SHA-256 of the canonical form of <paramref name="node"/>.</summary>
    public static string Digest(JsonNode node) => Sha256Hex(Serialize(node));

    /// <summary>
    /// Reads a JSON number as a <see cref="double"/>. False for a non-number node, a null node,
    /// an unparsable value or a non-finite one (<c>1e999</c> parses to infinity).
    /// </summary>
    public static bool TryGetNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue jsonValue || jsonValue.GetValueKind() != JsonValueKind.Number)
            return false;

        if (!double.TryParse(
                jsonValue.ToJsonString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsed))
            return false;

        if (!double.IsFinite(parsed))
            return false;

        value = parsed;
        return true;
    }

    private static void WriteNode(StringBuilder sb, JsonNode? node, int depth)
    {
        if (node is null || depth > MaxDepth)
        {
            sb.Append("null");
            return;
        }

        switch (node)
        {
            case JsonObject obj:
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, JsonNode?> property in
                         obj.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    if (!first)
                        sb.Append(',');
                    first = false;
                    WriteString(sb, property.Key);
                    sb.Append(':');
                    WriteNode(sb, property.Value, depth + 1);
                }
                sb.Append('}');
                break;

            case JsonArray array:
                sb.Append('[');
                for (int i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    WriteNode(sb, array[i], depth + 1);
                }
                sb.Append(']');
                break;

            case JsonValue value:
                WriteValue(sb, value);
                break;

            default:
                sb.Append("null");
                break;
        }
    }

    private static void WriteValue(StringBuilder sb, JsonValue value)
    {
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                WriteString(sb, value.GetValue<string>());
                break;

            case JsonValueKind.Number:
                sb.Append(FormatNumber(value));
                break;

            case JsonValueKind.True:
                sb.Append("true");
                break;

            case JsonValueKind.False:
                sb.Append("false");
                break;

            default:
                sb.Append("null");
                break;
        }
    }

    private static void WriteString(StringBuilder sb, string value)
    {
        sb.Append('"');
        sb.Append(JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping));
        sb.Append('"');
    }

    private static string FormatNumber(JsonValue value)
    {
        string text = value.ToJsonString();

        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer))
            return integer.ToString(CultureInfo.InvariantCulture);

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            return "null";

        if (!double.IsFinite(number))
            return "null";

        if (number == Math.Floor(number) && Math.Abs(number) < 1e15)
            return ((long)number).ToString(CultureInfo.InvariantCulture);

        return number.ToString("R", CultureInfo.InvariantCulture);
    }
}
