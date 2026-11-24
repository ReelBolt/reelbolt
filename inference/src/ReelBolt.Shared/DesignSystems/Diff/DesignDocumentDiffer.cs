using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReelBolt.Shared.DesignSystems.Diff;

/// <summary>One token path the design document gained, lost or changed.</summary>
/// <param name="Path">The dot path, e.g. <c>color.brand.primary</c>.</param>
/// <param name="Before">The base <c>$value</c>, absent for an addition.</param>
/// <param name="After">The candidate <c>$value</c>, absent for a removal.</param>
/// <param name="Locked">True when the base token was locked (locks always come from the base).</param>
/// <param name="Conflict">True when the base token was locked and the candidate contradicts it.</param>
/// <param name="Prov">The candidate token's provenance (the base's for a removal).</param>
public sealed record DesignDiffEntry(
    string Path,
    JsonNode? Before,
    JsonNode? After,
    bool Locked,
    bool Conflict,
    JsonObject? Prov);

/// <summary>
/// The design half of a drift diff (SPEC 13.3): token paths added/removed/changed and the
/// <c>voice</c>/<c>assets</c>/<c>fonts</c>/<c>modes</c> section summary.
/// </summary>
public sealed record DesignDiff(
    IReadOnlyList<DesignDiffEntry> Added,
    IReadOnlyList<DesignDiffEntry> Removed,
    IReadOnlyList<DesignDiffEntry> Changed,
    IReadOnlyDictionary<string, string> Sections)
{
    /// <summary>The design portion of the SPEC 13.3 <c>diff_json</c> object.</summary>
    public JsonObject ToJson()
    {
        var root = new JsonObject
        {
            ["counts"] = new JsonObject
            {
                ["added"] = Added.Count,
                ["removed"] = Removed.Count,
                ["changed"] = Changed.Count,
            },
            ["added"] = Entries(Added),
            ["removed"] = Entries(Removed),
            ["changed"] = Entries(Changed),
            ["sections"] = new JsonObject
            {
                ["voice"] = Section("voice"),
                ["assets"] = Section("assets"),
                ["fonts"] = Section("fonts"),
                ["modes"] = Section("modes"),
            },
        };

        return root;

        string Section(string name) =>
            Sections.TryGetValue(name, out string? state) ? state : "unchanged";

        JsonArray Entries(IReadOnlyList<DesignDiffEntry> entries)
        {
            var array = new JsonArray();
            foreach (DesignDiffEntry entry in entries)
            {
                var node = new JsonObject { ["path"] = entry.Path };
                if (entry.Before is not null)
                    node["before"] = entry.Before.DeepClone();
                if (entry.After is not null)
                    node["after"] = entry.After.DeepClone();
                if (entry.Locked || entry.Conflict)
                {
                    node["locked"] = entry.Locked;
                    node["conflict"] = entry.Conflict;
                }

                if (entry.Prov is not null)
                    node["prov"] = entry.Prov.DeepClone();
                array.Add(node);
            }

            return array;
        }
    }
}

/// <summary>
/// Walks two design documents and reports which token paths were added, removed or changed, plus a
/// per-section summary. Pure and total: malformed or missing nodes are treated as absent, never as
/// an exception. <see cref="DesignDocumentMerger.Merge"/> already keeps a locked base value, so a
/// contradiction is reported here (for the UI) while the proposed document keeps the base token.
/// </summary>
public static class DesignDocumentDiffer
{
    /// <summary>The non-token sections the summary compares.</summary>
    public static readonly string[] SectionNames = ["voice", "assets", "fonts", "modes"];

    public static DesignDiff Diff(JsonObject? before, JsonObject after, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(after);

        Dictionary<string, JsonObject> beforeTokens = Tokens(before);
        Dictionary<string, JsonObject> afterTokens = Tokens(after);

        var added = new List<DesignDiffEntry>();
        var removed = new List<DesignDiffEntry>();
        var changed = new List<DesignDiffEntry>();

        int batch = 0;
        ct.ThrowIfCancellationRequested();
        foreach (KeyValuePair<string, JsonObject> entry in afterTokens)
        {
            if ((++batch & 255) == 0)
                ct.ThrowIfCancellationRequested();
            if (!beforeTokens.ContainsKey(entry.Key))
                added.Add(new DesignDiffEntry(entry.Key, null, Value(entry.Value), false, false, Prov(entry.Value)));
        }

        foreach (KeyValuePair<string, JsonObject> entry in beforeTokens)
        {
            if ((++batch & 255) == 0)
                ct.ThrowIfCancellationRequested();
            bool locked = Locked(entry.Value);
            if (!afterTokens.TryGetValue(entry.Key, out JsonObject? candidate))
            {
                removed.Add(new DesignDiffEntry(entry.Key, Value(entry.Value), null, locked, locked, Prov(entry.Value)));
                continue;
            }

            if (!SameNode(entry.Value["$value"], candidate["$value"]))
                changed.Add(new DesignDiffEntry(entry.Key, Value(entry.Value), Value(candidate), locked, locked, Prov(candidate)));
        }

        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in SectionNames)
            sections[name] = SameNode(before?[name], after[name]) ? "unchanged" : "changed";

        return new DesignDiff(added, removed, changed, sections);
    }

    /// <summary>
    /// The diff a human approves (DSM-07): <paramref name="baseDoc"/> against the PROPOSED (merged)
    /// document, so every entry is something approval really applies. A locked base token the raw
    /// import contradicts is the one exception — it is appended to <c>Changed</c> with
    /// <c>Conflict = true</c> and shows the base value kept and the import value refused.
    /// </summary>
    public static DesignDiff DiffProposed(
        JsonObject? baseDoc, JsonObject proposed, JsonObject? rawImport, CancellationToken ct = default)
    {
        DesignDiff diff = Diff(baseDoc, proposed, ct);
        if (baseDoc is null || rawImport is null)
            return diff;

        Dictionary<string, JsonObject> baseTokens = Tokens(baseDoc);
        Dictionary<string, JsonObject> importTokens = Tokens(rawImport);
        var changed = new List<DesignDiffEntry>(diff.Changed);

        int checkpoint = 0;
        foreach (KeyValuePair<string, JsonObject> entry in baseTokens)
        {
            if ((++checkpoint & 255) == 0)
                ct.ThrowIfCancellationRequested();
            if (!Locked(entry.Value)
                || !importTokens.TryGetValue(entry.Key, out JsonObject? candidate)
                || SameNode(entry.Value["$value"], candidate["$value"]))
            {
                continue;
            }

            changed.Add(new DesignDiffEntry(entry.Key, Value(entry.Value), Value(candidate), true, true, Prov(candidate)));
        }

        return new DesignDiff(diff.Added, diff.Removed, changed, diff.Sections);
    }

    /// <summary>
    /// The digest of <paramref name="doc"/> with every token's <c>prov</c> block removed, so a change
    /// that moves only provenance (a source location, a source id) compares equal.
    /// </summary>
    public static string SemanticDigest(JsonObject doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        JsonNode copy = doc.DeepClone();
        StripProv(copy);
        return CanonicalJson.Digest(copy);
    }

    private static void StripProv(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["$extensions"] is JsonObject extensions && extensions["com.reelbolt"] is JsonObject reelbolt)
                    reelbolt.Remove("prov");
                foreach (KeyValuePair<string, JsonNode?> child in obj.ToList())
                    StripProv(child.Value);
                break;
            case JsonArray array:
                foreach (JsonNode? child in array)
                    StripProv(child);
                break;
        }
    }

    private static Dictionary<string, JsonObject> Tokens(JsonObject? doc)
    {
        var tokens = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (doc is null)
            return tokens;

        foreach (DesignTokenRef token in DesignDocument.Tokens(doc))
            tokens[token.Path] = token.Token;
        return tokens;
    }

    private static JsonNode? Value(JsonObject token) =>
        token.TryGetPropertyValue("$value", out JsonNode? value) ? value?.DeepClone() : null;

    private static bool Locked(JsonObject token) =>
        token["$extensions"] is JsonObject extensions
        && extensions["com.reelbolt"] is JsonObject reelbolt
        && reelbolt["locked"] is JsonValue value
        && value.GetValueKind() == JsonValueKind.True;

    private static JsonObject? Prov(JsonObject token) =>
        token["$extensions"] is JsonObject extensions
        && extensions["com.reelbolt"] is JsonObject reelbolt
        && reelbolt["prov"] is JsonObject prov
            ? (JsonObject)prov.DeepClone()
            : null;

    private static bool SameNode(JsonNode? left, JsonNode? right) =>
        string.Equals(CanonicalJson.Serialize(left), CanonicalJson.Serialize(right), StringComparison.Ordinal);
}

/// <summary>
/// Builds the capped SPEC 13.3 <c>diff_json</c> object from an optional design diff and an optional
/// product-map diff. When the encoded object exceeds <paramref name="maxBytes"/> the lists are
/// trimmed from the end, <c>"truncated":true</c> is recorded and <c>shown</c> carries the kept list sizes
/// next to the untouched <c>counts</c>, rather than throwing.
/// </summary>
public static class DiffJson
{
    /// <summary>The SPEC 13.3 cap.</summary>
    public const int MaxBytes = 256 * 1024;

    public static JsonObject Build(
        DesignDiff? design, double driftScore, ProductMapDiff? product, int maxBytes = MaxBytes)
    {
        var root = new JsonObject
        {
            ["counts"] = new JsonObject
            {
                ["added"] = design?.Added.Count ?? 0,
                ["removed"] = design?.Removed.Count ?? 0,
                ["changed"] = design?.Changed.Count ?? 0,
            },
            ["driftScore"] = driftScore,
        };

        if (design is not null)
        {
            JsonObject designJson = design.ToJson();
            foreach (string key in new[] { "added", "removed", "changed", "sections" })
                root[key] = designJson[key]?.DeepClone();
        }
        else
        {
            root["added"] = new JsonArray();
            root["removed"] = new JsonArray();
            root["changed"] = new JsonArray();
            root["sections"] = new JsonObject
            {
                ["voice"] = "unchanged",
                ["assets"] = "unchanged",
                ["fonts"] = "unchanged",
                ["modes"] = "unchanged",
            };
        }

        if (product is not null)
            root["product"] = product.ToJson();

        bool truncated = Trim(root, maxBytes);
        if (truncated)
        {
            // The counts above stay the true totals; "shown" says how many entries each list kept, so a
            // reviewer can tell exactly how much of the change is not listed.
            root["truncated"] = true;
            root["shown"] = new JsonObject
            {
                ["added"] = (root["added"] as JsonArray)?.Count ?? 0,
                ["removed"] = (root["removed"] as JsonArray)?.Count ?? 0,
                ["changed"] = (root["changed"] as JsonArray)?.Count ?? 0,
            };
        }

        return root;
    }

    private static bool Trim(JsonObject root, int maxBytes)
    {
        bool truncated = false;
        foreach (string list in new[] { "changed", "removed", "added" })
        {
            if (root[list] is not JsonArray array)
                continue;

            while (array.Count > 0 && Size(root) > maxBytes)
            {
                array.RemoveAt(array.Count - 1);
                truncated = true;
            }
        }

        // The product lists are dropped only once the design lists are exhausted.
        if (root["product"] is JsonObject product)
        {
            foreach (string collection in new[] { "dependencies", "components", "routes" })
            {
                if (product[collection] is not JsonObject group)
                    continue;

                foreach (string list in new[] { "changed", "removed", "added" })
                {
                    if (group[list] is not JsonArray array)
                        continue;

                    while (array.Count > 0 && Size(root) > maxBytes)
                    {
                        array.RemoveAt(array.Count - 1);
                        truncated = true;
                    }
                }
            }
        }

        return truncated;
    }

    private static int Size(JsonObject root) =>
        Encoding.UTF8.GetByteCount(root.ToJsonString());
}
