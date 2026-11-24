using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReelBolt.Shared.DesignSystems.Merge;

/// <summary>
/// Combines design documents. <see cref="Merge"/> layers candidates onto an optional locked base
/// (SPEC §4.7); <see cref="Inherit"/> layers a child theme onto a parent theme (SPEC §16.1). Both
/// are pure, never touch IO and never throw on malformed input — a wrong-shaped node is simply
/// skipped.
/// </summary>
public static class DesignDocumentMerger
{
    private static readonly string[] PlainSections = ["voice", "assets", "fonts", "modes"];

    /// <summary>
    /// Layers <paramref name="candidates"/> onto <paramref name="current"/> (which may be null).
    /// Per token path a locked base token wins, then an imported, static or LLM candidate (higher
    /// confidence, then earlier candidate index), and finally an unlocked base token when no
    /// candidate defines the path. Non-token sections merge key-wise with earlier candidates first.
    /// The output always declares <see cref="DesignDocument.Schema"/>.
    /// </summary>
    public static JsonObject Merge(
        JsonObject? current, IReadOnlyList<JsonObject> candidates, CancellationToken ct = default)
    {
        IReadOnlyList<JsonObject> layers = candidates ?? Array.Empty<JsonObject>();
        var result = new JsonObject { ["schema"] = DesignDocument.Schema };

        JsonNode? name = NameOf(current);
        if (name is null)
        {
            foreach (JsonObject? candidate in layers)
            {
                name = NameOf(candidate);
                if (name is not null)
                    break;
            }
        }

        if (name is not null)
            result["name"] = name.DeepClone();

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ct.ThrowIfCancellationRequested();
        AddTokenPaths(current, paths, seen);
        foreach (JsonObject? candidate in layers)
        {
            ct.ThrowIfCancellationRequested();
            AddTokenPaths(candidate, paths, seen);
        }

        // Locked base tokens are written first and always win: a candidate leaf at a locked token's
        // parent path (which would replace the group holding it) or below a locked leaf (which would
        // graft children onto it) is dropped, so a path collision can never erase a lock.
        var lockedPaths = new List<string>();
        int batch = 0;
        foreach (string path in paths)
        {
            if ((++batch & 255) == 0)
                ct.ThrowIfCancellationRequested();
            JsonObject? baseToken = TokenAt(current, path);
            if (baseToken is not null && LockedOf(baseToken))
            {
                lockedPaths.Add(path);
                SetTokenAt(result, path, baseToken);
            }
        }

        foreach (string path in paths)
        {
            if ((++batch & 255) == 0)
                ct.ThrowIfCancellationRequested();
            if (lockedPaths.Contains(path, StringComparer.Ordinal) || CollidesWithLock(path, lockedPaths))
                continue;

            JsonObject? winner = ChooseToken(path, current, layers);
            if (winner is not null)
                SetTokenAt(result, path, winner);
        }

        foreach (string section in PlainSections)
        {
            ct.ThrowIfCancellationRequested();
            JsonObject? merged = MergeKeywise(section, current, layers);
            if (merged is not null)
                result[section] = merged;
        }

        ReapplyLockedModes(result, current, lockedPaths);

        return result;
    }

    /// <summary>
    /// A locked token keeps its whole base mode set: for every locked path the merged modes carry exactly
    /// the base overrides, so a candidate can neither replace, drop nor add a mode override for it.
    /// </summary>
    private static void ReapplyLockedModes(JsonObject result, JsonObject? current, List<string> lockedPaths)
    {
        if (lockedPaths.Count == 0 || result["modes"] is not JsonObject merged)
            return;

        JsonObject? baseModes = current?["modes"] as JsonObject;
        foreach (string path in lockedPaths)
        {
            foreach (KeyValuePair<string, JsonNode?> mode in merged.ToList())
            {
                if (mode.Value is JsonObject overrides && (baseModes?[mode.Key] as JsonObject)?.ContainsKey(path) != true)
                    overrides.Remove(path);
            }

            if (baseModes is null)
                continue;

            foreach (KeyValuePair<string, JsonNode?> baseMode in baseModes)
            {
                if (baseMode.Value is not JsonObject baseOverrides || !baseOverrides.TryGetPropertyValue(path, out JsonNode? value))
                    continue;

                if (merged[baseMode.Key] is not JsonObject target)
                {
                    target = new JsonObject();
                    merged[baseMode.Key] = target;
                }

                target[path] = value?.DeepClone();
            }
        }
    }

    /// <summary>
    /// Layers <paramref name="child"/> onto <paramref name="parent"/>: a child token wins, other
    /// child keys win over the parent's, and every parent key the child omits is kept — there are no
    /// tombstones. <c>modes</c> merge per mode name and per override path.
    /// </summary>
    public static JsonObject Inherit(JsonObject parent, JsonObject child)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(child);

        var result = (JsonObject)parent.DeepClone();
        result["schema"] = DesignDocument.Schema;

        JsonNode? childName = NameOf(child);
        if (childName is not null)
            result["name"] = childName.DeepClone();

        foreach (DesignTokenRef token in DesignDocument.Tokens(child))
            SetTokenAt(result, token.Path, token.Token);

        foreach (string section in new[] { "voice", "assets", "fonts" })
        {
            if (!child.TryGetPropertyValue(section, out JsonNode? node) || node is not JsonObject childSection)
                continue;

            if (result[section] is not JsonObject targetSection)
            {
                targetSection = new JsonObject();
                result[section] = targetSection;
            }

            foreach (KeyValuePair<string, JsonNode?> entry in childSection)
                targetSection[entry.Key] = entry.Value?.DeepClone();
        }

        if (child.TryGetPropertyValue("modes", out JsonNode? modesNode) && modesNode is JsonObject childModes)
        {
            if (result["modes"] is not JsonObject targetModes)
            {
                targetModes = new JsonObject();
                result["modes"] = targetModes;
            }

            foreach (KeyValuePair<string, JsonNode?> mode in childModes)
            {
                if (mode.Value is not JsonObject childOverrides)
                {
                    targetModes[mode.Key] = mode.Value?.DeepClone();
                    continue;
                }

                if (targetModes[mode.Key] is not JsonObject targetOverrides)
                {
                    targetOverrides = new JsonObject();
                    targetModes[mode.Key] = targetOverrides;
                }

                foreach (KeyValuePair<string, JsonNode?> over in childOverrides)
                    targetOverrides[over.Key] = over.Value?.DeepClone();
            }
        }

        return result;
    }

    private static bool CollidesWithLock(string path, List<string> lockedPaths)
    {
        foreach (string locked in lockedPaths)
        {
            if (locked.StartsWith(path + ".", StringComparison.Ordinal)
                || path.StartsWith(locked + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonObject? ChooseToken(string path, JsonObject? current, IReadOnlyList<JsonObject> layers)
    {
        JsonObject? baseToken = TokenAt(current, path);
        if (baseToken is not null && LockedOf(baseToken))
            return baseToken;

        JsonObject? best = null;
        int bestLevel = int.MaxValue;
        double bestConf = double.MinValue;
        int bestIndex = int.MaxValue;

        for (int i = 0; i < layers.Count; i++)
        {
            JsonObject? token = TokenAt(layers[i], path);
            if (token is null)
                continue;

            int level = LevelOf(token);
            double conf = ConfOf(token);

            if (best is null
                || level < bestLevel
                || (level == bestLevel && conf > bestConf)
                || (level == bestLevel && conf == bestConf && i < bestIndex))
            {
                best = token;
                bestLevel = level;
                bestConf = conf;
                bestIndex = i;
            }
        }

        // An unlocked base token only fills a path no candidate defines.
        return best ?? baseToken;
    }

    private static int LevelOf(JsonObject token) => MethodOf(token) switch
    {
        "imported" => 2,
        "static" => 3,
        "llm" => 4,
        _ => 5,
    };

    private static string? MethodOf(JsonObject token) =>
        ExtensionOf(token)?["prov"] is JsonObject prov && prov["method"] is JsonValue value
            && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    private static double ConfOf(JsonObject token) =>
        ExtensionOf(token) is JsonObject reelbolt && CanonicalJson.TryGetNumber(reelbolt["conf"], out double conf)
            ? conf
            : 0;

    private static bool LockedOf(JsonObject token) =>
        ExtensionOf(token)?["locked"] is JsonValue value
        && value.GetValueKind() == JsonValueKind.True;

    private static JsonObject? ExtensionOf(JsonObject token) =>
        token["$extensions"] is JsonObject extensions && extensions["com.reelbolt"] is JsonObject reelbolt
            ? reelbolt
            : null;

    private static JsonObject? TokenAt(JsonObject? doc, string path)
    {
        if (doc is null)
            return null;

        JsonObject? node = DesignDocument.FindToken(doc, path);
        return node is not null && node.ContainsKey("$value") ? node : null;
    }

    private static void AddTokenPaths(JsonObject? doc, List<string> paths, HashSet<string> seen)
    {
        if (doc is null)
            return;

        foreach (DesignTokenRef token in DesignDocument.Tokens(doc))
        {
            if (seen.Add(token.Path))
                paths.Add(token.Path);
        }
    }

    private static void SetTokenAt(JsonObject root, string dotPath, JsonObject token)
    {
        string[] segments = dotPath.Split('.');
        JsonObject current = root;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (current[segments[i]] is JsonObject next)
            {
                current = next;
            }
            else
            {
                var created = new JsonObject();
                current[segments[i]] = created;
                current = created;
            }
        }

        current[segments[^1]] = token.DeepClone();
    }

    private static JsonObject? MergeKeywise(string section, JsonObject? current, IReadOnlyList<JsonObject> layers)
    {
        JsonObject? result = null;

        foreach (JsonObject? layer in layers)
        {
            if (layer is null
                || !layer.TryGetPropertyValue(section, out JsonNode? node)
                || node is not JsonObject layerSection)
            {
                continue;
            }

            result ??= new JsonObject();
            foreach (KeyValuePair<string, JsonNode?> entry in layerSection)
            {
                if (!result.ContainsKey(entry.Key))
                    result[entry.Key] = entry.Value?.DeepClone();
            }
        }

        if (current is not null
            && current.TryGetPropertyValue(section, out JsonNode? baseNode)
            && baseNode is JsonObject baseSection)
        {
            result ??= new JsonObject();
            foreach (KeyValuePair<string, JsonNode?> entry in baseSection)
            {
                if (!result.ContainsKey(entry.Key))
                    result[entry.Key] = entry.Value?.DeepClone();
            }
        }

        return result;
    }

    private static JsonNode? NameOf(JsonObject? doc) =>
        doc is not null && doc.TryGetPropertyValue("name", out JsonNode? name) && name is not null ? name : null;
}
