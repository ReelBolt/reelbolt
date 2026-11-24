using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Shared.Workflows.ChangeRequests;

/// <summary>
/// Which scenes of a script a change request touches, and how: scenes with structured overrides are edited directly
/// (no model call), scenes named with an instruction only are rewritten by one model call.
/// </summary>
/// <param name="Direct">Merged overrides (<c>*</c> beneath the scene's own) by scene id, for scenes that carry any.</param>
/// <param name="ModelSceneIds">Scene ids named by <c>itemIds</c> with an instruction and no override, in script order.</param>
/// <param name="RedoneSceneIds">Every touched scene id in script order.</param>
public sealed record ScriptChangePlan(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> Direct,
    IReadOnlyList<string> ModelSceneIds,
    IReadOnlyList<string> RedoneSceneIds)
{
    public int Matched => RedoneSceneIds.Count;

    /// <summary>True when no model call is needed: every touched scene is changed by structured overrides.</summary>
    public bool DirectOnly => ModelSceneIds.Count == 0;
}

/// <summary>
/// Pure logic of a change request on a script (docs/change-requests.md "Script (by scene)"): the plan, the direct
/// application of overrides, and the enforcement that a model rewrite changed only the scenes that were named.
/// </summary>
public static class ScriptChange
{
    /// <summary>The share of the script step charged when no model call is made (the step costs nothing, but the hold is never zero).</summary>
    public const double NoModelCallFraction = 1e-9;

    public static ScriptChangePlan Plan(ChangeRequestPayload payload, IReadOnlyList<ScriptSceneInfo> scenes)
    {
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> raw =
            payload.Overrides ?? new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>();
        raw.TryGetValue(ChangeRequestValidator.AllItems, out IReadOnlyDictionary<string, JsonElement>? star);

        Dictionary<string, IReadOnlyDictionary<string, JsonElement>> direct = new(StringComparer.Ordinal);
        foreach (ScriptSceneInfo scene in scenes)
        {
            raw.TryGetValue(scene.Id, out IReadOnlyDictionary<string, JsonElement>? own);
            Dictionary<string, JsonElement> merged = new(StringComparer.Ordinal);
            foreach ((string k, JsonElement v) in star ?? new Dictionary<string, JsonElement>())
                merged[k] = v;
            foreach ((string k, JsonElement v) in own ?? new Dictionary<string, JsonElement>())
                merged[k] = v;
            if (merged.Count > 0)
                direct[scene.Id] = merged;
        }

        HashSet<string> named = new(payload.ItemIds ?? [], StringComparer.Ordinal);
        List<string> model = [];
        if (!string.IsNullOrWhiteSpace(payload.Instruction))
            model.AddRange(scenes.Where(s => named.Contains(s.Id) && !direct.ContainsKey(s.Id)).Select(s => s.Id));

        List<string> redone = [.. scenes.Where(s => direct.ContainsKey(s.Id) || model.Contains(s.Id)).Select(s => s.Id)];
        return new ScriptChangePlan(direct, model, redone);
    }

    /// <summary>
    /// The scenes' cost share the request re-makes, in (0, 1]: <see cref="NoModelCallFraction"/> when no model call is
    /// made; otherwise 1 (the model reads and returns the full script).
    /// </summary>
    public static double CostFraction(ChangeRequestPayload payload, string? outputJson)
    {
        IReadOnlyList<ScriptSceneInfo> scenes = ScriptScenes.Read(outputJson);
        if (scenes.Count == 0)
            return 1d;
        ScriptChangePlan plan = Plan(payload, scenes);
        if (plan.Matched == 0)
            return 1d;
        if (plan.DirectOnly)
            return NoModelCallFraction;
        // A model call sends and receives the WHOLE script, whichever scenes it was asked to change.
        return 1d;
    }

    /// <summary>
    /// A copy of the previous script with the structured overrides of <paramref name="plan"/> applied to their scenes.
    /// Text goes through the sanitisers the rest of the pipeline uses; a value that sanitises to nothing leaves the
    /// field as it was. Returns the scene ids actually changed in <paramref name="applied"/>.
    /// </summary>
    public static JsonObject ApplyDirect(JsonObject previous, ScriptChangePlan plan, out List<string> applied)
    {
        JsonObject root = (JsonObject)previous.DeepClone();
        applied = [];
        if (plan.Direct.Count == 0 || !ScriptScenes.TryGet(root, "scenes", out JsonNode? node) || node is not JsonArray array)
            return root;
        IReadOnlyList<ScriptSceneInfo> infos = ScriptScenes.Read(root);
        foreach (ScriptSceneInfo info in infos)
        {
            if (!plan.Direct.TryGetValue(info.Id, out IReadOnlyDictionary<string, JsonElement>? ov) || array[info.Index] is not JsonObject scene)
                continue;
            bool changed = false;
            if (ov.TryGetValue("voiceover", out JsonElement vo) && vo.ValueKind == JsonValueKind.String
                && VoiceoverTextSanitizer.Sanitize(vo.GetString(), StepChangeCapabilities.MaxTextLength) is { Length: > 0 } voText)
            {
                scene[ScriptScenes.KeyOf(scene, "voiceover")] = voText;
                changed = true;
            }

            if (ov.TryGetValue("onScreenText", out JsonElement os) && OnScreenLines(os) is { Count: > 0 } lines)
            {
                scene[ScriptScenes.KeyOf(scene, "onScreenText")] = new JsonArray([.. lines.Select(l => (JsonNode?)JsonValue.Create(l))]);
                changed = true;
            }

            if (ov.TryGetValue("visualDescription", out JsonElement vd) && vd.ValueKind == JsonValueKind.String
                && GeneratedPromptSanitizer.StripControlCharacters(vd.GetString()).Trim() is { Length: > 0 } visual)
            {
                scene[ScriptScenes.KeyOf(scene, "visualDescription")] = visual;
                changed = true;
            }

            if (changed)
                applied.Add(info.Id);
        }
        return root;
    }

    /// <summary>The on-screen lines of an override value (a string with line breaks, or a list of strings): sanitised, trimmed, non-empty, at most the cap.</summary>
    public static List<string> OnScreenLines(JsonElement value)
    {
        IEnumerable<string> raw = value.ValueKind switch
        {
            JsonValueKind.String => (value.GetString() ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
            JsonValueKind.Array => value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? string.Empty),
            _ => []
        };
        return [.. raw
            .Select(l => OnScreenCopySanitizer.Strip(GeneratedPromptSanitizer.StripControlCharacters(l)).Trim())
            .Where(l => l.Length > 0)
            .Take(StepChangeCapabilities.MaxOnScreenTextLines)];
    }

    /// <summary>
    /// Folds a model's rewritten script into the previous one, in code: ONLY the scenes of <paramref name="plan"/>'s
    /// <see cref="ScriptChangePlan.ModelSceneIds"/> take the model's voiceover / on-screen text / visual description;
    /// every other scene, every id, start time and duration, and the title, length and narrative stay the previous
    /// script's, verbatim, whatever the model returned. Null with an <paramref name="error"/> when the model's answer is not a
    /// script or changed the number of scenes.
    /// </summary>
    public static JsonObject? Enforce(JsonObject previous, JsonObject? modelScript, ScriptChangePlan plan, out string? error)
    {
        error = null;
        if (modelScript is null || !ScriptScenes.TryGet(modelScript, "scenes", out JsonNode? mn) || mn is not JsonArray modelScenes)
        {
            error = "the rewritten script has no scenes";
            return null;
        }

        JsonObject root = (JsonObject)previous.DeepClone();
        if (!ScriptScenes.TryGet(root, "scenes", out JsonNode? pn) || pn is not JsonArray previousScenes)
        {
            error = "the previous script has no scenes";
            return null;
        }

        if (modelScenes.Count != previousScenes.Count)
        {
            error = $"the rewrite changed the number of scenes ({previousScenes.Count} before, {modelScenes.Count} after)";
            return null;
        }

        foreach (ScriptSceneInfo info in ScriptScenes.Read(root).Where(i => plan.ModelSceneIds.Contains(i.Id)))
        {
            // The model may renumber ids: match its scene by id first, else by position (the count is unchanged).
            JsonObject? rewritten = modelScenes.OfType<JsonObject>().FirstOrDefault(m => ScriptScenes.TryGet(m, "sceneId", out JsonNode? id)
                    && id is JsonValue iv && iv.TryGetValue(out string? s) && string.Equals(s, info.Id, StringComparison.OrdinalIgnoreCase))
                ?? modelScenes[info.Index] as JsonObject;
            if (rewritten is null || previousScenes[info.Index] is not JsonObject target)
                continue;

            if (ScriptScenes.TryGet(rewritten, "voiceover", out JsonNode? vo) && vo is JsonValue vv && vv.TryGetValue(out string? voText)
                && VoiceoverTextSanitizer.Sanitize(voText, StepChangeCapabilities.MaxTextLength) is { Length: > 0 } voClean)
                target[ScriptScenes.KeyOf(target, "voiceover")] = voClean;
            if (ScriptScenes.TryGet(rewritten, "onScreenText", out JsonNode? os) && ToElement(os) is { } osEl && OnScreenLines(osEl) is { Count: > 0 } lines)
                target[ScriptScenes.KeyOf(target, "onScreenText")] = new JsonArray([.. lines.Select(l => (JsonNode?)JsonValue.Create(l))]);
            if (ScriptScenes.TryGet(rewritten, "visualDescription", out JsonNode? vd) && vd is JsonValue dv && dv.TryGetValue(out string? vdText)
                && GeneratedPromptSanitizer.StripControlCharacters(vdText).Trim() is { Length: > 0 } vdClean)
                target[ScriptScenes.KeyOf(target, "visualDescription")] = vdClean;
        }
        return root;
    }

    /// <summary>The previous script with a <c>changeRequest</c> record added (additive: readers of the script ignore it).</summary>
    public static string WithChangeRequest(JsonObject script, Guid requestId, IReadOnlyList<string> redoneSceneIds, int sceneCount)
    {
        script["changeRequest"] = new JsonObject
        {
            ["requestId"] = requestId.ToString("D"),
            ["redoneSceneIds"] = new JsonArray([.. redoneSceneIds.Select(id => (JsonNode?)JsonValue.Create(id))]),
            ["reusedSceneCount"] = Math.Max(0, sceneCount - redoneSceneIds.Count)
        };
        return script.ToJsonString();
    }

    /// <summary>True when <paramref name="script"/> reads as a <see cref="ScriptwriterOutput"/> with <paramref name="sceneCount"/> scenes (what Voiceover and the author read).</summary>
    public static bool IsValidScript(string scriptJson, int sceneCount)
    {
        try
        {
            ScriptwriterOutput? output = JsonSerializer.Deserialize<ScriptwriterOutput>(scriptJson, ScriptwriterOutputJson.Options);
            return output is not null && output.Scenes.Count == sceneCount;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonElement? ToElement(JsonNode? node)
    {
        if (node is null)
            return null;
        using JsonDocument doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }
}
