using System.Globalization;
using System.Text.Json.Nodes;

namespace ReelBolt.Shared.Workflows.VideoEvents;

/// <summary>
/// The read tools of the speech outline over a stored analysis: a section's phrases, the phrases between two ids, the sound
/// effects and the music tracks on offer. Pure over the artifact. A call returns at most <see cref="MaxTokens"/> tokens,
/// cut at an entry boundary (never mid-JSON) with <c>truncated</c> and a <c>next</c> cursor to ask for the rest. Ids and
/// words only: a phrase is <c>{ids: "t12-t30", text}</c> and either end is a valid keep id; no time is ever returned.
/// </summary>
public static class VideoFootageReader
{
    public const int MaxTokens = 2000;
    private const int MaxPhraseChars = 400;

    private static string Error(string message) => new JsonObject { ["error"] = message }.ToJsonString();

    public static string ReadSection(VideoAnalysisArtifact artifact, string? sectionId, string? cursor)
    {
        List<SpeechSection> sections = VideoSpeechOutline.SectionsFor(artifact);
        int index = sections.FindIndex(s => s.Id == sectionId?.Trim());
        if (index < 0)
            return Error("unknown section; pass a section id from the outline, like c2");
        Dictionary<int, double> offsets = VideoSpeechOutline.Offsets(artifact, out _);
        List<SpeechPhrase> phrases = VideoSpeechOutline.PhrasesIn(VideoSpeechOutline.Phrases(artifact), offsets, sections[index], index == sections.Count - 1);
        return Page(sectionId!.Trim(), phrases, cursor);
    }

    public static string ReadTranscript(VideoAnalysisArtifact artifact, string? fromId, string? toId, string? cursor)
    {
        List<SpeechPhrase> all = VideoSpeechOutline.Phrases(artifact);
        Dictionary<int, double> offsets = VideoSpeechOutline.Offsets(artifact, out _);
        List<SpeechSection> sections = VideoSpeechOutline.SectionsFor(artifact);

        int Position(string? id, bool end)
        {
            string key = id?.Trim() ?? "";
            int s = sections.FindIndex(x => x.Id == key);
            if (s >= 0)
            {
                List<SpeechPhrase> inside = VideoSpeechOutline.PhrasesIn(all, offsets, sections[s], s == sections.Count - 1);
                return inside.Count == 0 ? -1 : all.IndexOf(end ? inside[^1] : inside[0]);
            }

            return all.FindIndex(p => IsInside(artifact, p, key));
        }

        int a = Position(fromId, false), b = Position(toId, true);
        if (a < 0 || b < 0)
            return Error("unknown id; pass transcript ids like t12 or section ids like c2");
        if (a > b)
            (a, b) = (b, a);
        return Page($"{fromId?.Trim()}..{toId?.Trim()}", all.GetRange(a, b - a + 1), cursor);
    }

    private static bool IsInside(VideoAnalysisArtifact artifact, SpeechPhrase p, string id)
    {
        int n = VideoEventOutline.IdNumber(id);
        return id.StartsWith('t') && n != int.MaxValue
            && n >= VideoEventOutline.IdNumber(p.FromId) && n <= VideoEventOutline.IdNumber(p.ToId)
            && artifact.Segments.Any(s => s.Id == id && s.SourceIndex == p.SourceIndex);
    }

    private static string Page(string scope, List<SpeechPhrase> phrases, string? cursor)
    {
        int start = int.TryParse(cursor?.TrimStart('p'), NumberStyles.None, CultureInfo.InvariantCulture, out int c) ? Math.Min(c, phrases.Count) : 0;
        if (!string.IsNullOrWhiteSpace(cursor) && start >= phrases.Count)
            return new JsonObject
            {
                ["scope"] = scope,
                ["complete"] = true,
                ["message"] = "This range is fully returned; do not call again."
            }.ToJsonString();
        JsonObject head = new()
        {
            ["scope"] = scope,
            ["total"] = phrases.Count,
            ["note"] = VideoEventReader.UntrustedNote,
            ["hint"] = "Each entry is a run of speech; ids \"tA-tB\" mean a keep may start at tA and end at tB (or at any id inside)."
        };
        JsonArray items = [];
        int tokens = VideoEventWords.EstimateTokens(head.ToJsonString()) + 60;
        int i = start;
        for (; i < phrases.Count; i++)
        {
            SpeechPhrase p = phrases[i];
            JsonObject node = new()
            {
                ["ids"] = p.FromId == p.ToId ? p.FromId : p.FromId + "-" + p.ToId,
                ["text"] = Clean(p.Text)
            };
            if (p.Speaker is not null)
                node["speaker"] = p.Speaker;
            int cost = VideoEventWords.EstimateTokens(node.ToJsonString()) + 1;
            if (tokens + cost > MaxTokens && items.Count > 0)
                break;
            tokens += cost;
            items.Add(node);
        }

        head["phrases"] = items;
        head["truncated"] = i < phrases.Count;
        if (i < phrases.Count)
            head["next"] = "p" + i.ToString(CultureInfo.InvariantCulture);
        else
        {
            head["complete"] = true;
            head["message"] = "This range is fully returned; do not call again.";
        }

        return head.ToJsonString();
    }

    public static string ListSfxClips(VideoAnalysisArtifact artifact, string? cursor) =>
        PageList("sfxClips", artifact.SfxCandidates is { Count: > 0 } sfx
            ? [.. sfx.Select(s => (s.Id, s.FileName, s.SizeBytes, s.Category, s.Tags))]
            : [], cursor);

    public static string ListMusic(VideoAnalysisArtifact artifact, string? cursor) =>
        PageList("musicTracks", artifact.MusicCandidates is { Count: > 0 } music
            ? [.. music.Select(m => (m.Id, m.FileName, m.SizeBytes, (string?)null, (IReadOnlyList<string>?)null))]
            : [], cursor);

    private static string PageList(string name, List<(string Id, string FileName, long SizeBytes, string? Category, IReadOnlyList<string>? Tags)> entries, string? cursor)
    {
        int start = int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out int c) ? Math.Min(c, entries.Count) : 0;
        JsonObject head = new() { ["total"] = entries.Count };
        JsonArray items = [];
        int tokens = 60;
        int i = start;
        for (; i < entries.Count; i++)
        {
            JsonObject node = new() { ["id"] = entries[i].Id, ["name"] = Clean(entries[i].FileName), ["sizeKb"] = entries[i].SizeBytes / 1024 };
            // A built-in library sound also carries its category and tag words (absent for a project file).
            if (entries[i].Category is { Length: > 0 } category)
                node["category"] = Clean(category);
            if (entries[i].Tags is { Count: > 0 } tags)
                node["tags"] = new JsonArray([.. tags.Select(t => (JsonNode)Clean(t))]);
            int cost = VideoEventWords.EstimateTokens(node.ToJsonString()) + 1;
            if (tokens + cost > MaxTokens && items.Count > 0)
                break;
            tokens += cost;
            items.Add(node);
        }

        head[name] = items;
        head["truncated"] = i < entries.Count;
        if (i < entries.Count)
            head["next"] = i.ToString(CultureInfo.InvariantCulture);
        return head.ToJsonString();
    }

    private static string Clean(string text)
    {
        string flat = string.Join(' ', new string([.. text.Select(c => char.IsControl(c) ? ' ' : c)]).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= MaxPhraseChars ? flat : flat[..MaxPhraseChars] + "...";
    }
}
