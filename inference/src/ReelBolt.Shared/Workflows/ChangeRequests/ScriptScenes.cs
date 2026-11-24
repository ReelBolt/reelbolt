using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReelBolt.Shared.Workflows.ChangeRequests;

/// <summary>One scene of a stored scriptwriter output, as a change request sees it.</summary>
/// <param name="Id">The scene's own <c>sceneId</c>, else <c>sc{n}</c> (1-based position).</param>
/// <param name="Index">Position in <c>scenes[]</c>.</param>
/// <param name="Voiceover">The narration of the scene.</param>
/// <param name="Visual">The visual description of the scene.</param>
/// <param name="DurationSec">The scene's <c>duration</c> in seconds, when it has one.</param>
public sealed record ScriptSceneInfo(string Id, int Index, string Voiceover, string Visual, double? DurationSec)
{
    /// <summary>What the scene costs to rewrite: its narration plus visual description, in characters.</summary>
    public int Chars => Voiceover.Length + Visual.Length;
}

/// <summary>
/// Reads the scenes out of a stored scriptwriter output (lenient: the stored text may carry prose around the JSON
/// object, <c>onScreenText</c> may be a string). Pure; unexpected output yields no scenes, never an exception.
/// </summary>
public static class ScriptScenes
{
    /// <summary>The JSON object inside <paramref name="raw"/>: the whole text when it is one, else the span from the first <c>{</c> to the last <c>}</c> that parses.</summary>
    public static JsonObject? ParseObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        string text = raw.Trim();
        if (TryParse(text) is { } whole)
            return whole;
        int first = text.IndexOf('{');
        int last = text.LastIndexOf('}');
        return first >= 0 && last > first ? TryParse(text[first..(last + 1)]) : null;
    }

    private static JsonObject? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text, null, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IReadOnlyList<ScriptSceneInfo> Read(string? outputJson) => Read(ParseObject(outputJson));

    public static IReadOnlyList<ScriptSceneInfo> Read(JsonObject? root)
    {
        if (root is null || !TryGet(root, "scenes", out JsonNode? node) || node is not JsonArray array)
            return [];
        List<ScriptSceneInfo> scenes = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject scene)
                continue;
            string id = Str(scene, "sceneId");
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
            {
                id = StepChangeCapabilities.SceneId(i + 1);
                if (!seen.Add(id))
                    continue;
            }
            double? duration = TryGet(scene, "duration", out JsonNode? d) && d is JsonValue dv && dv.TryGetValue(out double sec) && double.IsFinite(sec) ? sec : null;
            scenes.Add(new ScriptSceneInfo(id, i, Str(scene, "voiceover"), Str(scene, "visualDescription"), duration));
        }
        return scenes;
    }

    /// <summary>Case-insensitive property lookup (the model's own casing varies; the lenient reader accepts any).</summary>
    public static bool TryGet(JsonObject o, string name, out JsonNode? value)
    {
        foreach (KeyValuePair<string, JsonNode?> p in o)
        {
            if (string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    /// <summary>The actual key of <paramref name="name"/> in <paramref name="o"/> (its own casing), or <paramref name="name"/> when absent.</summary>
    public static string KeyOf(JsonObject o, string name) =>
        o.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;

    private static string Str(JsonObject o, string name) =>
        TryGet(o, name, out JsonNode? v) && v is JsonValue jv && jv.TryGetValue(out string? s) ? s ?? string.Empty : string.Empty;
}
