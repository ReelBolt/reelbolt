using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ReelBolt.Shared.Workflows.VideoEvents;

/// <summary>One run of transcript tokens with no pause longer than <see cref="VideoSpeechOutline.PhraseGapSec"/> inside it.</summary>
public sealed record SpeechPhrase(string FromId, string ToId, int SourceIndex, double StartSec, double EndSec, double GapBeforeSec, string Text, string? Speaker = null);

/// <summary>A section <c>c{n}</c> of the whole timeline. Times are on the concatenated timeline of all sources and never reach an agent.</summary>
public sealed record SpeechSection(string Id, double StartSec, double EndSec);

public sealed record VideoSpeechOutlineResult(
    IReadOnlyList<SpeechSection> Sections, JsonArray Nodes, int TokensEstimate, string Digest, double FirstSec, double LastSec, double DurationSec,
    JsonArray? Sources = null, bool Summarised = false);

/// <summary>
/// The deterministic speech outline of a video: sections that cover the ENTIRE timeline (never a prefix), each with a short
/// gist from its own words, cut points as transcript ids (<c>t{n}</c>, the first token after a pause, so a cut never lands
/// inside a word run) and, when events were analysed, one tempo line instead of every beat. Ids and words only: no time.
/// When the events outline exists its sections are reused, so there is ONE section id space. No model is called.
/// </summary>
public static class VideoSpeechOutline
{
    public const double PhraseGapSec = 0.25;
    public const int MaxPhraseTokens = 24;
    private const int MaxCutsPerSection = 6;
    private const int GistLeadWords = 8;
    private const int SourceGistWords = 4;
    private const int GistKeyTerms = 4;
    private const double SectionTargetSec = 30.0;

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "again", "being", "because", "could", "every", "going", "have", "just", "like", "little", "might",
        "really", "should", "since", "still", "that", "their", "there", "these", "they", "thing", "things", "this", "those",
        "through", "very", "want", "were", "what", "when", "where", "which", "while", "with", "would", "your", "gonna", "okay"
    };

    /// <summary>Source start offsets on the concatenated timeline and the total length.</summary>
    public static Dictionary<int, double> Offsets(VideoAnalysisArtifact artifact, out double total)
    {
        Dictionary<int, double> lengths = [];
        foreach (VideoAnalysisSourceInfo s in artifact.Sources ?? [])
            lengths[s.SourceIndex] = Math.Max(0, s.Media.DurationSec);
        if (lengths.Count == 0)
            lengths[0] = Math.Max(0, artifact.Media.DurationSec);
        foreach (VideoAnalysisSegment s in artifact.Segments)
            lengths[s.SourceIndex] = Math.Max(lengths.GetValueOrDefault(s.SourceIndex), s.EndSec);

        Dictionary<int, double> offsets = [];
        double running = 0;
        foreach (KeyValuePair<int, double> kv in lengths.OrderBy(k => k.Key))
        {
            offsets[kv.Key] = running;
            running += kv.Value;
        }

        total = running;
        return offsets;
    }

    /// <summary>The transcript as phrases, source by source in time order.</summary>
    public static List<SpeechPhrase> Phrases(VideoAnalysisArtifact artifact)
    {
        List<SpeechPhrase> phrases = [];
        foreach (IGrouping<int, VideoAnalysisSegment> source in artifact.Segments.GroupBy(s => s.SourceIndex).OrderBy(g => g.Key))
        {
            List<VideoAnalysisSegment> tokens = [.. source.OrderBy(s => s.StartSec).ThenBy(s => VideoEventOutline.IdNumber(s.Id))];
            int from = 0;
            double previousEnd = 0;
            for (int i = 1; i <= tokens.Count; i++)
            {
                bool close = i == tokens.Count
                    || tokens[i].StartSec - tokens[i - 1].EndSec >= PhraseGapSec
                    || tokens[i].Speaker != tokens[i - 1].Speaker
                    || i - from >= MaxPhraseTokens;
                if (!close)
                    continue;
                VideoAnalysisSegment first = tokens[from], last = tokens[i - 1];
                phrases.Add(new SpeechPhrase(
                    first.Id, last.Id, source.Key, first.StartSec, last.EndSec,
                    from == 0 ? double.PositiveInfinity : first.StartSec - previousEnd,
                    string.Join(' ', tokens.Skip(from).Take(i - from).Select(t => t.Text.Trim())),
                    first.Speaker));
                previousEnd = last.EndSec;
                from = i;
            }
        }

        return phrases;
    }

    public const int PhraseTextChars = 80;

    /// <summary>
    /// The whole film as phrases: <c>{ids: "t12-t30", text, speaker?}</c> with the text cut to <paramref name="textChars"/>.
    /// Every phrase is listed; the text shrinks (never the list) when the view must stay small. Ids and words only.
    /// </summary>
    public static JsonArray PhraseList(VideoAnalysisArtifact artifact, int textChars = PhraseTextChars)
    {
        JsonArray list = [];
        foreach (SpeechPhrase p in Phrases(artifact))
        {
            JsonObject n = new() { ["ids"] = p.FromId == p.ToId ? p.FromId : p.FromId + "-" + p.ToId };
            if (textChars > 0)
            {
                string t = Clean(p.Text);
                n["text"] = t.Length <= textChars ? t : t[..textChars].TrimEnd() + "...";
            }

            if (p.Speaker is not null)
                n["speaker"] = p.Speaker;
            if (p.SourceIndex != 0 || (artifact.Sources?.Count ?? 0) > 1)
                n["src"] = p.SourceIndex;
            list.Add(n);
        }

        return list;
    }

    /// <summary>The sections of an artifact: the events outline's own when it has one, else even sections cut at long pauses.</summary>
    public static List<SpeechSection> SectionsFor(VideoAnalysisArtifact artifact)
    {
        if (VideoEventOutline.Build(artifact) is { } events)
            return [.. events.Sections.Select(s => new SpeechSection(s.Id, s.StartSec, s.EndSec))];

        Dictionary<int, double> offsets = Offsets(artifact, out double total);
        if (total <= 0)
            return [];
        List<SpeechPhrase> phrases = Phrases(artifact);
        int count = Math.Clamp((int)Math.Round(total / SectionTargetSec), VideoEventOutline.MinSections, VideoEventOutline.MaxSections);
        List<double> cuts = [];
        for (int k = 1; k < count; k++)
        {
            double target = total * k / count;
            SpeechPhrase? best = phrases
                .Where(p => !double.IsPositiveInfinity(p.GapBeforeSec))
                .OrderBy(p => Math.Abs(offsets[p.SourceIndex] + p.StartSec - target)).ThenBy(p => p.FromId, StringComparer.Ordinal)
                .FirstOrDefault();
            double at = best is null ? target : offsets[best.SourceIndex] + best.StartSec;
            if (at > (cuts.Count == 0 ? 0 : cuts[^1]) && at < total)
                cuts.Add(at);
        }

        List<SpeechSection> sections = [];
        for (int i = 0; i <= cuts.Count; i++)
            sections.Add(new SpeechSection("c" + i.ToString(CultureInfo.InvariantCulture), i == 0 ? 0 : cuts[i - 1], i == cuts.Count ? total : cuts[i]));
        return sections;
    }

    /// <summary>The phrases that start inside <paramref name="section"/> (global timeline).</summary>
    public static List<SpeechPhrase> PhrasesIn(
        IReadOnlyList<SpeechPhrase> phrases, IReadOnlyDictionary<int, double> offsets, SpeechSection section, bool last) =>
        [.. phrases.Where(p =>
        {
            double at = offsets[p.SourceIndex] + p.StartSec;
            return at >= section.StartSec && (last ? at <= section.EndSec : at < section.EndSec);
        })];

    /// <summary>
    /// The outline: one node per section. <paramref name="events"/> adds the tempo line (beats omitted). Budgeted like the
    /// events outline; the cut points and gist shrink before a section is ever dropped.
    /// </summary>
    public static VideoSpeechOutlineResult? Build(VideoAnalysisArtifact artifact)
    {
        List<SpeechSection> sections = SectionsFor(artifact);
        Dictionary<int, double> offsets = Offsets(artifact, out double total);
        if (sections.Count == 0 || total <= 0)
            return null;

        List<SpeechPhrase> phrases = Phrases(artifact);
        List<VideoAnalysisRenderEvent> beats = [.. VideoEventOutline.DetectedEvents(artifact).Where(e => e.Type == nameof(VideoEventType.Beat))];
        int budget = VideoEventOutline.OutlineTokenBudget(total);
        int cutsPer = MaxCutsPerSection;
        int leadWords = GistLeadWords;
        int terms = GistKeyTerms;
        int srcWords = SourceGistWords;
        bool multi = (artifact.Sources?.Count ?? 0) > 1 || phrases.Select(p => p.SourceIndex).Distinct().Count() > 1;
        bool summarised = false;
        JsonArray nodes;
        JsonArray? sources;
        string json;
        while (true)
        {
            nodes = new([.. sections.Select((s, i) => Node(s, i == sections.Count - 1, phrases, offsets, beats, cutsPer, leadWords, terms, multi))]);
            sources = multi ? SourceLines(artifact, sections, phrases, offsets, srcWords) : null;
            json = nodes.ToJsonString() + (sources?.ToJsonString() ?? "");
            if (VideoEventWords.EstimateTokens(json) <= budget || (cutsPer <= 1 && leadWords <= 3 && terms == 0 && srcWords == 0))
                break;
            summarised = true;
            if (cutsPer > 2) cutsPer--;
            else if (terms > 0) terms--;
            else if (leadWords > 3) leadWords -= 1;
            else if (srcWords > 0) srcWords = 0;
            else cutsPer = 1;
        }

        double first = phrases.Count == 0 ? 0 : phrases.Min(p => offsets[p.SourceIndex] + p.StartSec);
        // Where the listed content ends: the last phrase inside a section whose node actually carries a gist. A section
        // that lost its node would lower this, so the coverage figure can fail; only a speechless film falls back to its length.
        double lastSec = 0;
        for (int i = 0; i < sections.Count; i++)
        {
            if (nodes[i] is JsonObject n && n["gist"] is not null)
                foreach (SpeechPhrase p in PhrasesIn(phrases, offsets, sections[i], i == sections.Count - 1))
                    lastSec = Math.Max(lastSec, offsets[p.SourceIndex] + p.EndSec);
        }

        if (phrases.Count == 0)
            lastSec = sections[^1].EndSec;
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        return new VideoSpeechOutlineResult(sections, nodes, VideoEventWords.EstimateTokens(json), digest, 0, lastSec, total, sources, summarised && multi);
    }

    /// <summary>The section node an agent sees. Public so the executor can lay it over the events outline's section of the same id.</summary>
    private static JsonNode Node(
        SpeechSection section, bool last, List<SpeechPhrase> phrases, Dictionary<int, double> offsets,
        List<VideoAnalysisRenderEvent> beats, int cutsPer, int leadWords, int terms, bool multi)
    {
        List<SpeechPhrase> inside = PhrasesIn(phrases, offsets, section, last);
        JsonObject node = new() { ["id"] = section.Id };
        if (multi && inside.Count > 0)
        {
            List<int> from = [.. inside.Select(p => p.SourceIndex).Distinct().Order()];
            if (from.Count == 1) node["source"] = from[0];
            else node["sources"] = new JsonArray([.. from.Select(i => (JsonNode)JsonValue.Create(i)!)]);
        }

        if (inside.Count > 0)
        {
            node["gist"] = Gist(inside, leadWords, terms);
            node["cuts"] = new JsonArray([.. CutPoints(inside, cutsPer).Select(id => (JsonNode)JsonValue.Create(id)!)]);
        }

        if (inside.Any(p => p.Speaker is not null))
        {
            AddSpeakers(node, inside, phrases);
        }

        string? tempo = Tempo(beats, offsets, section, last);
        if (tempo is not null)
            node["tempo"] = tempo;
        return node;
    }

    private static List<string> CutPoints(List<SpeechPhrase> inside, int max)
    {
        HashSet<string> keep = new(StringComparer.Ordinal) { inside[0].FromId };
        foreach (SpeechPhrase p in inside.Skip(1).Where(p => !double.IsPositiveInfinity(p.GapBeforeSec))
                     .OrderByDescending(p => p.GapBeforeSec).ThenBy(p => VideoEventOutline.IdNumber(p.FromId)))
        {
            if (keep.Count >= max)
                break;
            keep.Add(p.FromId);
        }

        return [.. keep.OrderBy(VideoEventOutline.IdNumber)];
    }

    /// <summary>
    /// One line per source clip so none is ever dropped when the sections are summarised: its section ids, how many speakers
    /// (when labeled), the first and last transcript id it holds (both are valid keep ids), and a few lead words. Ids and words only.
    /// </summary>
    private static JsonArray SourceLines(
        VideoAnalysisArtifact artifact, List<SpeechSection> sections, List<SpeechPhrase> phrases,
        Dictionary<int, double> offsets, int gistWords)
    {
        JsonArray lines = [];
        List<int> indexes = [.. offsets.Keys.Order()];
        foreach (int source in indexes)
        {
            List<SpeechPhrase> own = [.. phrases.Where(p => p.SourceIndex == source)];
            JsonObject line = new() { ["source"] = source };
            if (own.Count == 0)
            {
                line["speech"] = "none";
                lines.Add(line);
                continue;
            }

            List<string> inSections = [.. sections.Select((sec, i) => (sec, i))
                .Where(x => PhrasesIn(own, offsets, x.sec, x.i == sections.Count - 1).Count > 0).Select(x => x.sec.Id)];
            line["sections"] = inSections.Count <= 1 ? string.Join("", inSections) : inSections[0] + "-" + inSections[^1];
            line["ids"] = own[0].FromId + "-" + own[^1].ToId;
            int speakers = own.Where(p => p.Speaker is not null).Select(p => p.Speaker).Distinct().Count();
            if (speakers > 0)
                line["speakers"] = speakers;
            if (gistWords > 0)
                line["gist"] = Clean(string.Join(' ', Words(own[0].Text).Take(gistWords)));
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>
    /// Adds <c>speakers</c> (talk-time share, e.g. "S1 70%, S2 30%"), <c>speaker</c> (the dominant one) and
    /// <c>turns</c> (ids of phrases where the speaker differs from the previous phrase) to a section node.
    /// Only called when the transcript carries labels, so an unlabeled outline is unchanged.
    /// </summary>
    private static void AddSpeakers(JsonObject node, List<SpeechPhrase> inside, List<SpeechPhrase> all)
    {
        Dictionary<string, double> talk = [];
        foreach (SpeechPhrase p in inside.Where(p => p.Speaker is not null))
            talk[p.Speaker!] = talk.GetValueOrDefault(p.Speaker!) + Math.Max(0, p.EndSec - p.StartSec);
        double sum = talk.Values.Sum();
        List<KeyValuePair<string, double>> ranked = [.. talk.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal)];
        node["speakers"] = string.Join(", ", ranked.Select(k =>
            k.Key + " " + (sum <= 0 ? 0 : (int)Math.Round(100 * k.Value / sum)).ToString(CultureInfo.InvariantCulture) + "%"));
        node["speaker"] = ranked[0].Key;

        Dictionary<string, SpeechPhrase> before = [];
        for (int i = 1; i < all.Count; i++)
            before[all[i].FromId] = all[i - 1];
        List<string> turns = [.. inside
            .Where(p => p.Speaker is not null && before.TryGetValue(p.FromId, out SpeechPhrase? prev)
                && prev.SourceIndex == p.SourceIndex && prev.Speaker is not null && prev.Speaker != p.Speaker)
            .Select(p => p.FromId).Take(MaxCutsPerSection)];
        if (turns.Count > 0)
            node["turns"] = new JsonArray([.. turns.Select(id => (JsonNode)JsonValue.Create(id)!)]);
    }

    private static string Gist(List<SpeechPhrase> inside, int leadWords, int terms)
    {
        string lead = string.Join(' ', Words(inside[0].Text).Take(leadWords));
        if (terms <= 0)
            return Clean(lead);
        string keyTerms = string.Join(' ', inside.SelectMany(p => Words(p.Text))
            .Select(w => w.ToLowerInvariant().Trim('.', ',', '!', '?', ';', ':', '"', '\''))
            .Where(w => w.Length >= 5 && !Stop.Contains(w) && w.All(char.IsLetter))
            .GroupBy(w => w).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(terms).Select(g => g.Key));
        return keyTerms.Length == 0 ? Clean(lead) : Clean($"{lead} | {keyTerms}");
    }

    private static IEnumerable<string> Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string Clean(string text) =>
        string.Join(' ', new string([.. text.Select(c => char.IsControl(c) ? ' ' : c)]).Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>"~N bpm" from the median gap of the section's beats, or null with fewer than four.</summary>
    private static string? Tempo(List<VideoAnalysisRenderEvent> beats, Dictionary<int, double> offsets, SpeechSection section, bool last)
    {
        List<double> at = [.. beats.Select(b => offsets.GetValueOrDefault(b.SourceIndex ?? 0) + b.ContentSec)
            .Where(t => t >= section.StartSec && (last ? t <= section.EndSec : t < section.EndSec)).Order()];
        if (at.Count < 4)
            return null;
        List<double> gaps = [.. at.Zip(at.Skip(1), (a, b) => b - a).Where(g => g > 0.05).Order()];
        if (gaps.Count == 0)
            return null;
        return "~" + ((int)Math.Round(60.0 / gaps[gaps.Count / 2])).ToString(CultureInfo.InvariantCulture) + " bpm, beats omitted";
    }
}
