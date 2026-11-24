using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;

namespace ReelBolt.WorkflowEngine.Services.Video.Events;

/// <summary>
/// Turns detected events into the offer an analysis makes: the artifact's merged <c>e{n}</c> list (frames and seconds
/// stay there) and the agent-facing <c>view.events</c> (id, type word, strength word, region word, never a number).
/// Pure; no I/O. Declared events (a render's own sidecar, <see cref="RenderEventAnchors"/>) and detected ones share
/// ONE id space ordered by analysed source, then by time within the source.
/// </summary>
public static class VideoEventOffers
{
    /// <summary>Warning code: some detected events were left out of <c>view.events</c> for the view's size budget.</summary>
    public const string EventsSuppressedWarning = "events_suppressed";

    /// <summary>Warning code: some events were left out of the outline's per-section lists for the planner's token budget.</summary>
    public const string OutlineSuppressedWarning = "outline_suppressed";

    /// <summary>Joins the events digest with the outline digest, so a change of either changes the cache key.</summary>
    public static string JoinDigest(string eventsDigest, string outlineDigest) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(eventsDigest + "+" + outlineDigest))).ToLowerInvariant();

    /// <summary>Warning code: events were asked for and a source could not be read for them.</summary>
    public const string EventsUnavailableWarning = "events_unavailable";

    /// <summary>Rough characters one <c>view.events</c> entry costs; the budget pass uses it before measuring.</summary>
    private const int EntryCharsEstimate = 130;

    private const string DeclaredOrigin = "Declared";

    /// <summary>The detected events of one analysed source, on that source's own timeline.</summary>
    public sealed record DetectedSource(int SourceIndex, VideoEventSet Set);

    /// <summary>
    /// One merged list: every declared event and every detected event, ordered by source (declared insert-content events
    /// last) and time, numbered <c>e0..</c> contiguously. A declared event keeps <c>Origin = Declared</c>; a detected one
    /// carries its type, strength, region and origin. With no detected source the result is the declared list, renumbered
    /// identically (so the flag-off path is unchanged).
    /// </summary>
    public static List<VideoAnalysisRenderEvent> Merge(
        IReadOnlyList<VideoAnalysisRenderEvent> declared, IReadOnlyList<DetectedSource> detected)
    {
        List<(int Source, double Sec, int Kind, int Order, VideoAnalysisRenderEvent Event)> items = [];
        int order = 0;
        foreach (VideoAnalysisRenderEvent e in declared)
            items.Add((e.SourceIndex ?? int.MaxValue, e.ContentSec, 0, order++, e with { Origin = DeclaredOrigin }));

        foreach (DetectedSource source in detected)
        {
            double fps = source.Set.Fps > 0 ? source.Set.Fps : 30.0;
            foreach (VideoEvent e in source.Set.Events)
            {
                string typeWord = TypeWord(e.Type);
                items.Add((source.SourceIndex, e.Frame / fps, 1, order++, new VideoAnalysisRenderEvent(
                    Id: "",
                    Name: typeWord,
                    Label: typeWord,
                    Frame: e.Frame,
                    ContentSec: e.Frame / fps,
                    SourceIndex: source.SourceIndex,
                    Type: e.Type.ToString(),
                    Origin: e.Origin.ToString(),
                    Strength: e.Strength,
                    Region: e.Region,
                    EndFrame: e.EndFrame,
                    Direction: e.Direction,
                    SpeedWord: e.Speed,
                    Kind: VideoEventKinds.Parse(e.Kind))));
            }
        }

        List<VideoAnalysisRenderEvent> merged = [];
        foreach (var item in items.OrderBy(i => i.Source).ThenBy(i => i.Sec).ThenBy(i => i.Kind).ThenBy(i => i.Order))
            merged.Add(item.Event with { Id = "e" + merged.Count.ToString(CultureInfo.InvariantCulture) });
        return merged;
    }

    /// <summary>The declared events of a merged list (the render-declared subset, with their merged ids).</summary>
    public static List<VideoAnalysisRenderEvent> DeclaredOf(IReadOnlyList<VideoAnalysisRenderEvent> merged) =>
        [.. merged.Where(e => e.Type is null)];

    /// <summary>The detected events of a merged list.</summary>
    public static List<VideoAnalysisRenderEvent> DetectedOf(IReadOnlyList<VideoAnalysisRenderEvent> merged) =>
        [.. merged.Where(e => e.Type is not null)];

    /// <summary>The event with id <paramref name="id"/> in an artifact: the merged list first, then the declared one.</summary>
    public static VideoAnalysisRenderEvent? Find(VideoAnalysisArtifact artifact, string id) =>
        artifact.Events?.FirstOrDefault(e => e.Id == id) ?? artifact.RenderEvents?.FirstOrDefault(e => e.Id == id);

    /// <summary>
    /// The detected events the view can show inside <paramref name="remainingChars"/>: the strongest first, then back in id
    /// order. Never fewer than zero; <c>Dropped</c> counts what did not fit.
    /// </summary>
    public static (List<VideoAnalysisRenderEvent> Shown, int Dropped) FitToBudget(
        IReadOnlyList<VideoAnalysisRenderEvent> detected, int remainingChars)
    {
        // A described event carries its "seen" words too.
        int longestSeen = detected.Count == 0 ? 0 : detected.Max(e => e.Seen?.Length ?? 0);
        int perEntry = EntryCharsEstimate + (longestSeen > 0 ? longestSeen + 10 : 0);
        int fits = Math.Clamp(remainingChars / perEntry, 0, detected.Count);
        if (fits >= detected.Count)
            return ([.. detected], 0);

        HashSet<string> keep = Execution.StepExecutors.VideoViewCoverage.SelectSpread(detected, fits);
        return ([.. detected.Where(e => keep.Contains(e.Id))], detected.Count - fits);
    }

    /// <summary>
    /// <c>view.events</c>: <c>{id, type, strength, region?, what?, seen?, kind?, look?}</c>; words only: no frame, no second,
    /// no number. <c>look: true</c> marks an event of <paramref name="pictureIds"/>: the analysis kept a picture of it that
    /// <c>LookAtMoment</c> can ask about. With no such ids (every run that does not describe events) no event carries it.
    /// </summary>
    public static JsonArray ViewNode(IReadOnlyList<VideoAnalysisRenderEvent> events, IReadOnlyCollection<string>? pictureIds = null) =>
        new([.. events.Select(e =>
        {
            JsonObject node = new()
            {
                ["id"] = e.Id,
                ["type"] = e.Type is not null && Enum.TryParse(e.Type, out VideoEventType t) ? TypeWord(t) : e.Name,
                ["strength"] = StrengthWord(e.Strength ?? 0)
            };
            if (e.Region is { } region)
                node["region"] = RegionWord(region);
            if (VideoEventWords.What(e) is { } what)
                node["what"] = what;
            if (e.Seen is { Length: > 0 } seen)
                node["seen"] = seen;
            if (VideoEventKinds.Parse(e.Kind) is { } kind)
                node["kind"] = kind;
            if (pictureIds is { Count: > 0 } && pictureIds.Contains(e.Id))
                node[VideoEventStrips.LookMark] = true;
            return (JsonNode)node;
        })]);

    /// <summary><c>meta.events</c>: <c>{count, byType, digest, suppressed}</c> over the SHOWN detected events.</summary>
    public static JsonObject MetaNode(IReadOnlyList<VideoAnalysisRenderEvent> shown, IReadOnlyList<VideoAnalysisRenderEvent> all, int suppressed)
    {
        JsonObject byType = [];
        foreach (IGrouping<string, VideoAnalysisRenderEvent> group in shown.GroupBy(e => e.Name).OrderBy(g => g.Key, StringComparer.Ordinal))
            byType[group.Key] = group.Count();
        return new JsonObject
        {
            ["count"] = shown.Count,
            ["byType"] = byType,
            ["digest"] = Digest(all),
            ["suppressed"] = suppressed
        };
    }

    /// <summary>SHA-256 over every merged event (what, where, when, how strong): covers the frames the view never shows.</summary>
    public static string Digest(IReadOnlyList<VideoAnalysisRenderEvent> events)
    {
        StringBuilder sb = new();
        foreach (VideoAnalysisRenderEvent e in events)
        {
            sb.Append(e.Id).Append('|').Append(e.Type ?? e.Name).Append('|').Append(e.Origin ?? "-").Append('|')
              .Append(e.Frame.ToString(CultureInfo.InvariantCulture)).Append('|')
              .Append(e.EndFrame?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('|')
              .Append(e.ContentSec.ToString("R", CultureInfo.InvariantCulture)).Append('|')
              .Append(e.Strength?.ToString("R", CultureInfo.InvariantCulture) ?? "-").Append('|')
              .Append(e.Region is { } r ? string.Create(CultureInfo.InvariantCulture, $"{r.X:R},{r.Y:R},{r.W:R},{r.H:R}") : "-").Append('|')
              .Append(e.SourceIndex?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('|')
              .Append(e.InsertContentStorageKey ?? "-");
            // Only when described, so every digest of an analysis without descriptions is unchanged.
            if (e.Seen is { Length: > 0 } seen)
                sb.Append('|').Append(seen);
            if (VideoEventKinds.Parse(e.Kind) is { } kind)
                sb.Append("|k=").Append(kind);
            sb.Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>The snake_case word of a type: <c>MoveStart</c> becomes <c>move_start</c>.</summary>
    public static string TypeWord(VideoEventType type) => VideoEventWords.TypeWord(type);

    /// <summary><c>weak</c> below 0.35, <c>strong</c> from 0.7, else <c>clear</c>.</summary>
    public static string StrengthWord(double strength) => VideoEventWords.StrengthWord(strength);

    /// <summary>The cell of a 3x3 grid the region's centre falls in: <c>top-left</c> .. <c>bottom-right</c>, <c>center</c> in the middle.</summary>
    public static string RegionWord(VideoEventRegion region) => VideoEventWords.RegionWord(region);
}
