using System.Globalization;
using System.Text.Json.Nodes;

namespace ReelBolt.Shared.Workflows.VideoEvents;

/// <summary>
/// The ids a keep may name that an outline-mode view lists as RANGES instead of rows: <c>view.outline.phrases[].ids</c>
/// ("t12-t30", every id in between is valid) and <c>view.outline.shots</c> (shot id ranges). Consumers that validate ids against
/// the view's rows (the rooms, the interleave guard) add these, or they would drop a keep that starts anywhere past the first section.
/// </summary>
public static class OutlineViewIds
{
    /// <summary>Builds <c>outline.shots</c>: one <c>{ids: "s0-s88", src?}</c> per run of consecutive shot ids of a source.</summary>
    public static JsonArray ShotRanges(VideoAnalysisArtifact artifact)
    {
        bool multi = (artifact.Sources?.Count ?? 0) > 1;
        JsonArray ranges = [];
        foreach (IGrouping<int, VideoAnalysisShot> source in artifact.Shots.GroupBy(s => s.SourceIndex).OrderBy(g => g.Key))
        {
            List<(int N, string Id)> ids = [.. source.Select(s => (VideoEventOutline.IdNumber(s.Id), s.Id)).Where(x => x.Item1 != int.MaxValue).OrderBy(x => x.Item1)];
            int from = 0;
            for (int i = 1; i <= ids.Count; i++)
            {
                if (i < ids.Count && ids[i].N == ids[i - 1].N + 1)
                    continue;
                JsonObject node = new() { ["ids"] = ids[from].Id == ids[i - 1].Id ? ids[from].Id : ids[from].Id + "-" + ids[i - 1].Id };
                if (multi)
                    node["src"] = source.Key;
                ranges.Add(node);
                from = i;
            }
        }

        return ranges;
    }

    /// <summary>
    /// <c>meta.offeredIdRanges</c>: every offered id (shots, silences, transcript) as ranges of consecutive ids per prefix and
    /// source. Tiny, and always kept, so a room can accept an id from any section even when the phrase list was left out.
    /// </summary>
    public static JsonArray AllIdRanges(VideoAnalysisArtifact artifact)
    {
        bool multi = (artifact.Sources?.Count ?? 0) > 1;
        IEnumerable<(string Id, int Src)> all = artifact.Shots.Select(x => (x.Id, x.SourceIndex))
            .Concat(artifact.SilenceSpans.Select(x => (x.Id, x.SourceIndex)))
            .Concat(artifact.Segments.Select(x => (x.Id, x.SourceIndex)));
        JsonArray ranges = [];
        foreach (var group in all.Where(x => x.Id.Length > 1 && VideoEventOutline.IdNumber(x.Id) != int.MaxValue)
                     .GroupBy(x => (Prefix: x.Id[0], x.Src)).OrderBy(g => g.Key.Prefix).ThenBy(g => g.Key.Src))
        {
            List<(int N, string Id)> ids = [.. group.Select(x => (VideoEventOutline.IdNumber(x.Id), x.Id)).OrderBy(x => x.Item1)];
            int from = 0;
            for (int i = 1; i <= ids.Count; i++)
            {
                if (i < ids.Count && ids[i].N == ids[i - 1].N + 1)
                    continue;
                JsonObject node = new() { ["ids"] = ids[from].Id == ids[i - 1].Id ? ids[from].Id : ids[from].Id + "-" + ids[i - 1].Id };
                if (multi)
                    node["src"] = group.Key.Src;
                ranges.Add(node);
                from = i;
            }
        }

        return ranges;
    }

    /// <summary>The most ids one view may expand to (a hostile or broken view cannot make a room build an unbounded set).</summary>
    public const int MaxExpandedIds = 200_000;

    /// <summary>
    /// Every (id, source index, isShot) the outline's ranges (and <c>meta.offeredIdRanges</c> when <paramref name="meta"/> is given)
    /// name, expanded, at most <see cref="MaxExpandedIds"/>. Empty for a view without an outline.
    /// </summary>
    public static IEnumerable<(string Id, int Src, bool IsShot)> Expand(JsonNode? view, JsonNode? meta = null)
    {
        int budget = MaxExpandedIds;
        foreach ((JsonNode? items, bool isShot) in new[]
                 {
                     (view?["outline"]?["phrases"], false), (view?["outline"]?["shots"], true), (meta?["offeredIdRanges"], false)
                 })
        {
            if (items is not JsonArray array)
                continue;
            foreach (JsonNode? item in array)
            {
                if (item?["ids"] is not JsonValue idsValue || !idsValue.TryGetValue(out string? ids) || string.IsNullOrEmpty(ids))
                    continue;
                int src = item["src"] is JsonValue srcValue && srcValue.TryGetValue(out int parsed) ? parsed : 0;
                string[] ends = ids.Split('-');
                if (ends.Length != 2)
                {
                    if (budget-- <= 0) yield break;
                    yield return (ids, src, isShot || ids[0] == 's');
                    continue;
                }

                int a = VideoEventOutline.IdNumber(ends[0]), b = VideoEventOutline.IdNumber(ends[1]);
                if (a == int.MaxValue || b == int.MaxValue || b < a || b - a > MaxExpandedIds || ends[0].Length == 0 || ends[0][0] != ends[1][0])
                {
                    if (budget-- <= 0) yield break;
                    yield return (ends[0], src, isShot || ends[0][0] == 's');
                    if (budget-- <= 0) yield break;
                    yield return (ends[1], src, isShot || ends[1][0] == 's');
                    continue;
                }

                char prefix = ends[0][0];
                for (int n = a; n <= b; n++)
                {
                    if (budget-- <= 0) yield break;
                    yield return (prefix + n.ToString(CultureInfo.InvariantCulture), src, isShot || prefix == 's');
                }
            }
        }
    }
}
