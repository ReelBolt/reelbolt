using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReelBolt.Shared.DesignSystems;

/// <summary>
/// Read/write helpers over the dynamic design document — a plain <see cref="JsonObject"/> whose
/// token groups (<c>color</c>, <c>typography</c>, <c>spacing</c>, <c>radius</c>, <c>shadow</c>,
/// <c>motion</c>, <c>component</c>) hold nested token objects. Nothing here throws on malformed
/// input; a missing or wrong-shaped node simply answers "not found".
/// </summary>
public static class DesignDocument
{
    /// <summary>The schema every design document must declare.</summary>
    public const string Schema = "reelbolt.design-system/1";

    /// <summary>The top-level token groups, in canonical order.</summary>
    public static readonly string[] TokenGroupNames =
    [
        "color",
        "typography",
        "spacing",
        "radius",
        "shadow",
        "motion",
        "component",
        "z",
    ];

    /// <summary>The deepest alias chain that may be followed.</summary>
    public const int MaxAliasDepth = 8;

    /// <summary>A fresh document with the schema and, optionally, a name.</summary>
    public static JsonObject New(string? name = null)
    {
        var root = new JsonObject { ["schema"] = Schema };
        if (name is not null)
            root["name"] = name;
        return root;
    }

    /// <summary>
    /// Every token below every token group, in document order. A node is a token exactly when it is
    /// a <see cref="JsonObject"/> carrying <c>$value</c>; a node without <c>$value</c> is a group and
    /// is walked. Non-object nodes and <c>$</c>-prefixed keys are skipped rather than throwing.
    /// </summary>
    public static IEnumerable<DesignTokenRef> Tokens(JsonObject root)
    {
        if (root is null)
            yield break;

        foreach (string group in TokenGroupNames)
        {
            if (!root.TryGetPropertyValue(group, out JsonNode? node) || node is not JsonObject obj)
                continue;

            foreach (DesignTokenRef token in WalkTokens(obj, group))
                yield return token;
        }
    }

    /// <summary>
    /// The object reached by walking <paramref name="dotPath"/> from the root, or null when any
    /// segment is missing or an intermediate node is not an object.
    /// </summary>
    public static JsonObject? FindToken(JsonObject root, string dotPath)
    {
        if (root is null || string.IsNullOrEmpty(dotPath))
            return null;

        JsonNode? current = root;
        foreach (string segment in dotPath.Split('.'))
        {
            if (string.IsNullOrEmpty(segment) || current is not JsonObject obj)
                return null;

            if (!obj.TryGetPropertyValue(segment, out JsonNode? next))
                return null;

            current = next;
        }

        return current as JsonObject;
    }

    /// <summary>
    /// Creates any missing group objects along <paramref name="dotPath"/> and stores a token at the
    /// leaf with its <c>$type</c>, <c>$value</c> and <c>$extensions.com.reelbolt</c> provenance.
    /// <c>orig</c> is written only when <paramref name="original"/> is non-null.
    /// </summary>
    public static void SetToken(
        JsonObject root,
        string dotPath,
        string type,
        JsonNode? value,
        TokenProvenance prov,
        double conf,
        bool locked = false,
        string? original = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(dotPath);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(prov);

        string[] segments = dotPath.Split('.');
        JsonObject current = root;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (current.TryGetPropertyValue(segments[i], out JsonNode? next) && next is JsonObject nextObject)
            {
                current = nextObject;
            }
            else
            {
                var created = new JsonObject();
                current[segments[i]] = created;
                current = created;
            }
        }

        var provObject = new JsonObject { ["method"] = prov.Method };
        if (prov.Importer is not null)
            provObject["importer"] = prov.Importer;
        if (prov.SourceId is not null)
            provObject["sourceId"] = prov.SourceId.Value.ToString("D");
        if (prov.Loc is not null)
            provObject["loc"] = prov.Loc;

        var reelbolt = new JsonObject
        {
            ["prov"] = provObject,
            ["conf"] = conf,
            ["locked"] = locked,
        };
        if (original is not null)
            reelbolt["orig"] = original;

        var token = new JsonObject
        {
            ["$type"] = type,
            ["$value"] = value?.DeepClone(),
            ["$extensions"] = new JsonObject { ["com.reelbolt"] = reelbolt },
        };

        current[segments[^1]] = token;
    }

    /// <summary>
    /// Follows a <c>"{a.b}"</c> alias at <paramref name="dotPath"/> until a non-alias value is
    /// reached. False when the path is missing, the value is not an alias, the chain cycles or it
    /// would exceed <see cref="MaxAliasDepth"/> hops.
    /// </summary>
    public static bool ResolveAlias(JsonObject root, string dotPath, out JsonNode? value)
    {
        value = null;
        if (root is null || string.IsNullOrEmpty(dotPath))
            return false;

        var visited = new HashSet<string>(StringComparer.Ordinal);
        string current = dotPath;

        for (int depth = 0; ; depth++)
        {
            JsonObject? token = FindToken(root, current);
            if (token is null || !token.TryGetPropertyValue("$value", out JsonNode? node) || node is null)
                return false;

            if (TryGetString(node, out string? text) && TryParseAlias(text, out string? target))
            {
                if (depth >= MaxAliasDepth || !visited.Add(current))
                    return false;

                current = target!;
                continue;
            }

            value = node;
            return true;
        }
    }

    /// <summary>
    /// True when <paramref name="text"/> is exactly <c>{path}</c> with a non-empty path.
    /// </summary>
    internal static bool TryParseAlias(string? text, out string? path)
    {
        path = null;
        if (text is null || text.Length < 3 || text[0] != '{' || text[^1] != '}')
            return false;

        string inner = text[1..^1];
        if (inner.Length == 0)
            return false;

        path = inner;
        return true;
    }

    private static IEnumerable<DesignTokenRef> WalkTokens(JsonObject obj, string path)
    {
        foreach (KeyValuePair<string, JsonNode?> property in obj)
        {
            if (property.Key.StartsWith('$') || property.Value is not JsonObject child)
                continue;

            string childPath = path + "." + property.Key;
            if (child.ContainsKey("$value"))
            {
                string type = TryGetString(child["$type"], out string? parsed) ? parsed! : string.Empty;
                yield return new DesignTokenRef(childPath, type, child["$value"]!, child);
            }
            else
            {
                foreach (DesignTokenRef token in WalkTokens(child, childPath))
                    yield return token;
            }
        }
    }

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

/// <summary>One token found in a design document: its dot path, declared type, raw value and node.</summary>
public sealed record DesignTokenRef(string Path, string Type, JsonNode Value, JsonObject Token);

/// <summary>The provenance recorded on a token by an importer, a static extractor or a model.</summary>
public sealed record TokenProvenance(string Method, string? Importer, Guid? SourceId, string? Loc);
