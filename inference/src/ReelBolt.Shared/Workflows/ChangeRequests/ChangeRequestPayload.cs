using System.Text.Json;

namespace ReelBolt.Shared.Workflows.ChangeRequests;

/// <summary>
/// What a change request carries (docs/design/change-requests.md): an instruction, the items it is scoped
/// to and typed overrides keyed by item id or <c>"*"</c>. <see cref="ToCanonicalJson"/> is stable (sorted
/// keys, sorted de-duplicated item ids, trimmed instruction): the confirmation token and equality use it.
/// </summary>
public sealed record ChangeRequestPayload(
    string? Instruction,
    IReadOnlyList<string>? ItemIds,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? Overrides)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Parses a payload; null for null/blank/garbage JSON.</summary>
    public static ChangeRequestPayload? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ChangeRequestPayload>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    /// <summary>Stable canonical JSON: same request, same string, whatever the input ordering.</summary>
    public string ToCanonicalJson()
    {
        using MemoryStream ms = new();
        using (Utf8JsonWriter w = new(ms))
        {
            w.WriteStartObject();
            w.WriteString("instruction", (Instruction ?? string.Empty).Trim());
            w.WriteStartArray("itemIds");
            foreach (string id in (ItemIds ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                w.WriteStringValue(id);
            w.WriteEndArray();
            w.WriteStartObject("overrides");
            foreach ((string item, IReadOnlyDictionary<string, JsonElement> keys) in (Overrides ?? new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>()).OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                w.WriteStartObject(item);
                foreach ((string key, JsonElement value) in (keys ?? new Dictionary<string, JsonElement>()).OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    w.WritePropertyName(key);
                    WriteCanonical(w, value);
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>True when both payloads ask for exactly the same thing.</summary>
    public bool CanonicalEquals(ChangeRequestPayload? other) =>
        other is not null && string.Equals(ToCanonicalJson(), other.ToCanonicalJson(), StringComparison.Ordinal);

    private static void WriteCanonical(Utf8JsonWriter w, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (JsonProperty p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    w.WritePropertyName(p.Name);
                    WriteCanonical(w, p.Value);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (JsonElement item in e.EnumerateArray())
                    WriteCanonical(w, item);
                w.WriteEndArray();
                break;
            default:
                e.WriteTo(w);
                break;
        }
    }
}
