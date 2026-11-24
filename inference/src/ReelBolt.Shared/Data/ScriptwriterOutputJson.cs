using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReelBolt.Shared.Data.OutputSchemas;

/// <summary>
/// The one reader of <see cref="ScriptwriterOutput"/> JSON. Models sometimes answer
/// <c>onScreenText</c> as a single string with line breaks instead of an array; this accepts both.
/// The converter is registered on these OPTIONS only, never as an attribute on the property: a
/// custom converter makes the schema generated for the strict-schema response format unconstrained.
/// </summary>
public static class ScriptwriterOutputJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(), new LenientStringListConverter() }
    };
}

/// <summary>Reads any JSON shape (strings, numbers, objects, nested arrays, null) as a list of trimmed non-empty lines; never throws on shape.</summary>
public sealed class LenientStringListConverter : JsonConverter<List<string>>
{
    public override bool HandleNull => true;

    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Model shape drift never fails the run: whatever the model sent is kept as text lines.
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        var result = new List<string>();
        Collect(doc.RootElement, result, 0);
        return result;
    }

    private static readonly string[] TextKeys = ["text", "line", "value", "content", "title", "label", "name"];

    private static void Collect(JsonElement e, List<string> result, int depth)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                foreach (string line in (e.GetString() ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    string t = line.Trim();
                    if (t.Length > 0) result.Add(t);
                }
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                result.Add(e.GetRawText());
                break;
            case JsonValueKind.Array when depth < 4:
                foreach (JsonElement item in e.EnumerateArray())
                    Collect(item, result, depth + 1);
                break;
            case JsonValueKind.Object when depth < 4:
                foreach (string key in TextKeys)
                {
                    foreach (JsonProperty prop in e.EnumerateObject())
                    {
                        if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase)
                            && prop.Value.ValueKind != JsonValueKind.Null)
                        {
                            Collect(prop.Value, result, depth + 1);
                            return;
                        }
                    }
                }
                foreach (JsonProperty prop in e.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        Collect(prop.Value, result, depth + 1);
                }
                break;
        }
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (string s in value) writer.WriteStringValue(s);
        writer.WriteEndArray();
    }
}
