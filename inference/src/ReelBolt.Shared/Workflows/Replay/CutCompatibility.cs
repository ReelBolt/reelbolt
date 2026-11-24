using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ReelBolt.Shared.Workflows.Replay;

/// <summary>The answer of <see cref="CutCompatibility.CompareAnalysisViews"/>: whether a kept decision still means the same thing.</summary>
public sealed record CutCompatibilityResult(bool Compatible, string Reason)
{
    public static CutCompatibilityResult Yes(string reason) => new(true, reason);
    public static CutCompatibilityResult No(string reason) => new(false, reason);
}

/// <summary>
/// "Keep the same cut" (docs/design/composition-revisions.md): an editorial decision names only the
/// opaque ids a <c>VideoAnalyze</c> step OFFERED (<c>s3</c>, <c>g1</c>, <c>t7</c>, <c>p2</c> …), never a
/// time. When a revised film is analysed again, the decision can be reused exactly when the new analysis
/// offers the SAME ids, from the same sources, at (nearly) the same places — then every id the decision
/// names resolves to the same scene of the new film, and the compile cuts the new frames at the new
/// boundaries. When anything about the offered set changed (a scene added or removed, which renumbers
/// every later id; a scene re-timed beyond tolerance; a transcript segment that appeared), the decision
/// no longer means what the owner approved and the step is re-made.
///
/// <para>
/// Deliberately STRICTER than "the ids the decision names still exist": a scene added to the film
/// leaves every old id resolvable while the kept cut silently drops the new scene, which is not the
/// cut the owner approved either. The whole offered set is compared.
/// </para>
/// </summary>
public static partial class CutCompatibility
{
    /// <summary>A boundary may move this much in seconds without changing what an id means (about 7 frames at 30 fps).</summary>
    public const double AbsoluteToleranceSec = 0.25;

    /// <summary>…or this fraction of the item's own length, whichever is larger.</summary>
    public const double RelativeTolerance = 0.10;

    [GeneratedRegex("^[a-z][0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex OfferedId();

    /// <summary>
    /// Compares the bounded <c>{view, meta}</c> envelope a <c>VideoAnalyze</c> step produced in the source
    /// run with the one it produced in the replay.
    /// </summary>
    public static CutCompatibilityResult CompareAnalysisViews(string? sourceOutputJson, string? replayOutputJson)
    {
        Dictionary<string, OfferedItem>? before = OfferedItems(sourceOutputJson);
        Dictionary<string, OfferedItem>? after = OfferedItems(replayOutputJson);
        if (before is null || after is null)
            return CutCompatibilityResult.No("the analysis could not be read");
        if (before.Count == 0)
            return CutCompatibilityResult.No("the original analysis offered nothing to compare");

        List<string> missing = [.. before.Keys.Where(id => !after.ContainsKey(id)).Order(StringComparer.Ordinal)];
        if (missing.Count > 0)
            return CutCompatibilityResult.No($"the new film no longer offers {Describe(missing)}");

        List<string> added = [.. after.Keys.Where(id => !before.ContainsKey(id)).Order(StringComparer.Ordinal)];
        if (added.Count > 0)
            return CutCompatibilityResult.No($"the new film offers {Describe(added)} that the cut never saw");

        foreach ((string id, OfferedItem old) in before.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            OfferedItem now = after[id];
            if (old.Source != now.Source)
                return CutCompatibilityResult.No($"{id} now comes from a different source");

            double tolerance = Math.Max(AbsoluteToleranceSec, RelativeTolerance * Math.Max(0, (old.End ?? 0) - (old.Start ?? 0)));
            if (Moved(old.Start, now.Start, tolerance) || Moved(old.End, now.End, tolerance))
            {
                return CutCompatibilityResult.No(string.Create(CultureInfo.InvariantCulture,
                    $"{id} moved from {Format(old.Start)}–{Format(old.End)} s to {Format(now.Start)}–{Format(now.End)} s"));
            }
        }

        return CutCompatibilityResult.Yes($"the new film offers the same {before.Count} ids at the same places");
    }

    /// <summary>True when two JSON documents are the same value (property order and whitespace ignored).</summary>
    public static bool SameJson(string? a, string? b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
            return true;
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        try
        {
            return JsonNode.DeepEquals(Normalize(JsonNode.Parse(a)), Normalize(JsonNode.Parse(b)));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonNode? Normalize(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Normalize(p.Value?.DeepClone())))),
        JsonArray array => new JsonArray([.. array.Select(n => Normalize(n?.DeepClone()))]),
        _ => node?.DeepClone()
    };

    private sealed record OfferedItem(int? Source, double? Start, double? End);

    /// <summary>Every object under <c>view</c> carrying an offered id, keyed by id. Null when unreadable.</summary>
    private static Dictionary<string, OfferedItem>? OfferedItems(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson))
            return null;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(outputJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root?["view"] is not JsonObject view)
            return null;

        Dictionary<string, OfferedItem> items = new(StringComparer.Ordinal);
        Collect(view, items);
        return items;
    }

    private static void Collect(JsonNode? node, Dictionary<string, OfferedItem> items)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["id"] is JsonValue idValue && idValue.TryGetValue(out string? id) && id is not null && OfferedId().IsMatch(id))
                {
                    items.TryAdd(id, new OfferedItem(
                        Integer(obj["src"]),
                        Number(obj["startSec"]) ?? Number(obj["start"]),
                        Number(obj["endSec"]) ?? Number(obj["end"])));
                }

                foreach (KeyValuePair<string, JsonNode?> child in obj)
                    Collect(child.Value, items);
                break;

            case JsonArray array:
                foreach (JsonNode? child in array)
                    Collect(child, items);
                break;
        }
    }

    private static bool Moved(double? before, double? after, double tolerance) =>
        (before is null) != (after is null)
        || (before is { } b && after is { } a && Math.Abs(a - b) > tolerance);

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out double number) ? number : null;

    private static int? Integer(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out int number) ? number : null;

    private static string Format(double? seconds) =>
        seconds is { } s ? s.ToString("0.##", CultureInfo.InvariantCulture) : "?";

    private static string Describe(List<string> ids) =>
        ids.Count <= 4 ? string.Join(", ", ids) : $"{string.Join(", ", ids.Take(4))} and {ids.Count - 4} more";
}
