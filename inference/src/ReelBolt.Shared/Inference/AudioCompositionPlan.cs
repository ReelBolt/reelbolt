using System.Text.Json;

namespace ReelBolt.Shared.Inference;

/// <summary>Which composition-plan schema an endpoint takes.</summary>
public enum AudioPlanShape
{
    /// <summary>No plan field: a plan is refused.</summary>
    None,

    /// <summary>
    /// The v1-style plan (<c>positive_global_styles</c>, <c>negative_global_styles</c>, <c>sections</c>), which
    /// fal's <c>MusicCompositionPlan</c> declares (research/audio-vendors.md A1).
    /// </summary>
    Sections,

    /// <summary>The ElevenLabs <c>music_v2</c>/<c>music_v2_5</c> plan: <c>{chunks: [...]}</c> (B1).</summary>
    Chunks,
}

/// <summary>
/// Validates a caller's composition plan against the endpoint's schema and returns it unchanged, so the
/// vendor receives exactly what the caller wrote and nothing it would silently ignore.
/// </summary>
public static class AudioCompositionPlan
{
    /// <summary>Most sections or chunks a plan may hold (ElevenLabs: <c>sections[&lt;=30]</c>, <c>chunks[&lt;=30]</c>).</summary>
    public const int MaxParts = 30;

    /// <summary>Shortest and longest section/chunk, in milliseconds (both schemas).</summary>
    public const int MinPartMs = 3000, MaxPartMs = 120000;

    private static readonly string[] SectionsRoot = ["positive_global_styles", "negative_global_styles", "sections"];
    private static readonly string[] SectionKeys =
        ["section_name", "positive_local_styles", "negative_local_styles", "duration_ms", "lines"];
    private static readonly string[] ChunkRequired = ["text", "duration_ms"];
    private static readonly string[] ChunkOptional = ["positive_styles", "negative_styles", "context_adherence"];
    private static readonly string[] Adherence = ["low", "medium", "high"];

    /// <summary>
    /// Parses and validates <paramref name="json"/> for <paramref name="shape"/>; returns the parsed plan and its
    /// total length in seconds (the sum of its parts, which is what the vendor generates and bills).
    /// </summary>
    /// <exception cref="ArgumentException">The plan is not valid JSON or breaks the schema; the message names the field.</exception>
    public static (JsonElement Plan, double TotalSec) Validate(string json, AudioPlanShape shape, string endpointId)
    {
        if (shape == AudioPlanShape.None)
        {
            throw Refuse(endpointId, "documents no composition plan field");
        }

        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw Refuse(endpointId, "was given a composition plan that is not valid JSON (" + ex.Message + ")");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Refuse(endpointId, "takes a composition plan that is a JSON object");
        }

        long totalMs = shape == AudioPlanShape.Sections ? ValidateSections(root, endpointId) : ValidateChunks(root, endpointId);
        return (root, totalMs / 1000.0);
    }

    private static long ValidateSections(JsonElement root, string id)
    {
        RequireExactly(root, SectionsRoot, SectionsRoot, id, "the composition plan");
        RequireStrings(root, "positive_global_styles", id, null, null);
        RequireStrings(root, "negative_global_styles", id, null, null);

        JsonElement sections = root.GetProperty("sections");
        IReadOnlyList<JsonElement> parts = RequireParts(sections, "sections", id);
        long total = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            JsonElement section = parts[i];
            string where = "sections[" + i + "]";
            if (section.ValueKind != JsonValueKind.Object) throw Refuse(id, where + " must be an object");
            RequireExactly(section, SectionKeys, SectionKeys, id, where);

            JsonElement name = section.GetProperty("section_name");
            if (name.ValueKind != JsonValueKind.String || name.GetString()!.Length is < 1 or > 100)
            {
                throw Refuse(id, where + ".section_name must be a string of 1-100 characters");
            }

            RequireStrings(section, "positive_local_styles", id, where, null);
            RequireStrings(section, "negative_local_styles", id, where, null);
            RequireStrings(section, "lines", id, where, 200);
            total += RequirePartMs(section, id, where);
        }

        return total;
    }

    private static long ValidateChunks(JsonElement root, string id)
    {
        if (root.TryGetProperty("sections", out _))
        {
            throw Refuse(id, "takes the 'chunks' plan; a 'sections' plan is the deprecated music_v1 shape");
        }

        RequireExactly(root, ["chunks"], ["chunks"], id, "the composition plan");
        IReadOnlyList<JsonElement> parts = RequireParts(root.GetProperty("chunks"), "chunks", id);
        long total = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            JsonElement chunk = parts[i];
            string where = "chunks[" + i + "]";
            if (chunk.ValueKind != JsonValueKind.Object) throw Refuse(id, where + " must be an object");

            // conditioning_ref and condition_strength exist in the vendor schema but their types were not read,
            // so they are refused rather than passed through unvalidated.
            RequireExactly(chunk, ChunkRequired, [.. ChunkRequired, .. ChunkOptional], id, where);

            JsonElement text = chunk.GetProperty("text");
            if (text.ValueKind != JsonValueKind.String || text.GetString()!.Length > 6132)
            {
                throw Refuse(id, where + ".text must be a string of at most 6132 characters");
            }

            if (chunk.TryGetProperty("positive_styles", out _)) RequireStrings(chunk, "positive_styles", id, where, null, 50);
            if (chunk.TryGetProperty("negative_styles", out _)) RequireStrings(chunk, "negative_styles", id, where, null, 50);
            if (chunk.TryGetProperty("context_adherence", out JsonElement adherence)
                && (adherence.ValueKind != JsonValueKind.String || !Adherence.Contains(adherence.GetString(), StringComparer.Ordinal)))
            {
                throw Refuse(id, where + ".context_adherence must be one of: " + string.Join(", ", Adherence));
            }

            total += RequirePartMs(chunk, id, where);
        }

        return total;
    }

    private static IReadOnlyList<JsonElement> RequireParts(JsonElement array, string name, string id)
    {
        if (array.ValueKind != JsonValueKind.Array) throw Refuse(id, "'" + name + "' must be an array");
        int count = array.GetArrayLength();
        if (count is < 1 or > MaxParts)
        {
            throw Refuse(id, "'" + name + "' must hold 1 to " + MaxParts + " entries; it holds " + count);
        }

        return [.. array.EnumerateArray()];
    }

    private static long RequirePartMs(JsonElement part, string id, string where)
    {
        JsonElement ms = part.GetProperty("duration_ms");
        if (ms.ValueKind != JsonValueKind.Number || !ms.TryGetInt32(out int value) || value is < MinPartMs or > MaxPartMs)
        {
            throw Refuse(id, where + ".duration_ms must be an integer from " + MinPartMs + " to " + MaxPartMs);
        }

        return value;
    }

    private static void RequireExactly(JsonElement obj, string[] required, string[] allowed, string id, string where)
    {
        foreach (JsonProperty property in obj.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw Refuse(id, where + " carries '" + property.Name + "', which this endpoint's plan schema does not " +
                                 "declare (allowed: " + string.Join(", ", allowed) + ")");
            }
        }

        foreach (string name in required)
        {
            if (!obj.TryGetProperty(name, out _)) throw Refuse(id, where + " is missing the required '" + name + "'");
        }
    }

    private static void RequireStrings(JsonElement obj, string name, string id, string? where, int? maxLength, int? maxCount = null)
    {
        string field = (where is null ? string.Empty : where + ".") + name;
        JsonElement array = obj.GetProperty(name);
        if (array.ValueKind != JsonValueKind.Array) throw Refuse(id, field + " must be an array of strings");
        if (maxCount is { } max && array.GetArrayLength() > max) throw Refuse(id, field + " holds at most " + max + " entries");
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw Refuse(id, field + " must be an array of strings");
            if (maxLength is { } limit && item.GetString()!.Length > limit)
            {
                throw Refuse(id, field + " entries are at most " + limit + " characters");
            }
        }
    }

    private static ArgumentException Refuse(string id, string why) =>
        new("'" + id + "' " + why + "; the composition plan was not sent.", "request");
}
