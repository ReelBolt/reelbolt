using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ReelBolt.Shared.Workflows.VideoEvents;

/// <summary>One section <c>c{n}</c> of the outline. The internal times never reach an agent-facing view.</summary>
public sealed record OutlineSection(
    string Id,
    double StartSec,
    double EndSec,
    string Position,
    string Descriptor,
    IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<string> TopEventIds,
    IReadOnlyList<VideoAnalysisRenderEvent> Events,
    string? Summary = null);

/// <summary>The L0 outline of an analysis: its sections and the agent-facing JSON.</summary>
public sealed record VideoEventOutlineResult(
    IReadOnlyList<OutlineSection> Sections,
    JsonObject Outline,
    int TokensEstimate,
    string Digest,
    int TokenBudget);

/// <summary>The L1 view: the events of each section, trimmed to the budget, and how many were left out.</summary>
public sealed record VideoEventSectionView(JsonObject Sections, int TokensEstimate, int Suppressed, int TotalEvents);

/// <summary>
/// The deterministic outline of a video's events (docs/design/video-events-analysis.md section 6, levels L0 and L1).
/// Sections <c>c{n}</c> (3 to 12) are cut at shot bounds, speech paragraphs, long idles and big transitions; each has a
/// one-line descriptor in words, counts by type and its three strongest events. Ids and words only: no time, no frame.
/// Pure and deterministic: the same artifact always gives the same bytes. A model summary may be passed in
/// (<c>summaries</c>, at most 25 words each); it only fills a text field and never adds an event.
/// </summary>
public static class VideoEventOutline
{
    public const int MinSections = 3;
    public const int MaxSections = 12;
    public const int MaxEventsPerSection = 25;
    public const int SectionTokenBudget = 1800;
    public const int InitialPromptTokenBudget = 8000;
    public const int MaxSummaryWords = 25;

    private const double ParagraphGapSec = 1.5;
    private const double LongIdleSec = 2.0;
    private const double SectionTargetSec = 30.0;
    private const double BigTransition = 0.7;

    /// <summary>L0 budget: 1,500 tokens up to 10 minutes, plus 120 per further 5 minutes, never above 3,000.</summary>
    public static int OutlineTokenBudget(double durationSec) =>
        durationSec <= 600 ? 1500 : Math.Min(3000, 1500 + 120 * (int)Math.Ceiling((durationSec - 600) / 300.0));

    /// <summary>The detected events of an artifact that sit on an analysed source's timeline, in id order.</summary>
    public static List<VideoAnalysisRenderEvent> DetectedEvents(VideoAnalysisArtifact artifact)
    {
        List<VideoAnalysisRenderEvent> events =
        [
            .. (artifact.Events ?? [])
                .Where(e => e.Type is not null && e.SourceIndex is not null && VideoEventWords.ParseType(e.Type) is not null)
                .OrderBy(e => IdNumber(e.Id))
        ];
        // A run that feeds a sound designer is shown moments a sound belongs to, never every beat.
        if (artifact.SfxEventsOnly == true)
        {
            Dictionary<int, double> durations = (artifact.Sources ?? []).ToDictionary(s => s.SourceIndex, s => s.Media.DurationSec);
            events = SfxWorthyEvents.Select(events, i => durations.TryGetValue(i, out double d) ? (double?)d : null, artifact.SfxOnFootage == true);
        }

        return events;
    }

    public static int IdNumber(string id) =>
        id.Length > 1 && int.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : int.MaxValue;

    /// <summary>The outline of the artifact, or null when it has no detected event to outline.</summary>
    public static VideoEventOutlineResult? Build(VideoAnalysisArtifact artifact, IReadOnlyDictionary<string, string>? summaries = null)
    {
        List<VideoAnalysisRenderEvent> events = DetectedEvents(artifact);
        if (events.Count == 0)
            return null;

        Dictionary<int, double> offsets = SourceOffsets(artifact, events, out double total);
        if (total <= 0)
            return null;

        double T(VideoAnalysisRenderEvent e) => offsets[e.SourceIndex!.Value] + e.ContentSec;

        double[] cuts = ChooseCuts(artifact, events, offsets, total);
        List<OutlineSection> sections = [];
        for (int i = 0; i <= cuts.Length; i++)
        {
            double start = i == 0 ? 0 : cuts[i - 1];
            double end = i == cuts.Length ? total : cuts[i];
            bool last = i == cuts.Length;
            List<VideoAnalysisRenderEvent> inside = [.. events.Where(e => T(e) >= start && (last ? T(e) <= end : T(e) < end))];
            sections.Add(Describe(artifact, offsets, "c" + i.ToString(CultureInfo.InvariantCulture), start, end, i, cuts.Length + 1, inside, summaries));
        }

        return Assemble(artifact, sections, events, total);
    }

    private static VideoEventOutlineResult Assemble(
        VideoAnalysisArtifact artifact, List<OutlineSection> sections, List<VideoAnalysisRenderEvent> events, double total)
    {
        int budget = OutlineTokenBudget(total);
        JsonObject global = GlobalFacts(artifact, sections, events, total);
        int topKeep = 3;
        bool dropSummaries = false;
        bool compactCounts = false;
        JsonObject outline;
        string json;
        while (true)
        {
            outline = new JsonObject { ["global"] = global.DeepClone(), ["sections"] = SectionNodes(sections, topKeep, dropSummaries, compactCounts) };
            json = outline.ToJsonString();
            if (VideoEventWords.EstimateTokens(json) <= budget || (topKeep == 0 && dropSummaries && compactCounts))
                break;
            if (topKeep > 1) topKeep--;
            else if (!dropSummaries) dropSummaries = true;
            else if (topKeep > 0) topKeep--;
            else compactCounts = true;
        }

        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        return new VideoEventOutlineResult(sections, outline, VideoEventWords.EstimateTokens(json), digest, budget);
    }

    private static JsonArray SectionNodes(IReadOnlyList<OutlineSection> sections, int topKeep, bool dropSummaries, bool compactCounts) =>
        new([.. sections.Select(s =>
        {
            JsonObject counts = [];
            IEnumerable<KeyValuePair<string, int>> ordered = s.Counts.OrderBy(k => k.Key, StringComparer.Ordinal);
            if (compactCounts)
                ordered = ordered.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Take(3).OrderBy(k => k.Key, StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> kv in ordered)
                counts[kv.Key] = kv.Value;
            JsonObject node = new()
            {
                ["id"] = s.Id,
                ["position"] = s.Position,
                ["descriptor"] = s.Descriptor,
                ["counts"] = counts,
                ["top"] = new JsonArray([.. s.TopEventIds.Take(topKeep).Select(id => (JsonNode)JsonValue.Create(id)!)])
            };
            if (!dropSummaries && s.Summary is not null)
                node["summary"] = s.Summary;
            return (JsonNode)node;
        })]);

    private static JsonObject GlobalFacts(VideoAnalysisArtifact artifact, List<OutlineSection> sections, List<VideoAnalysisRenderEvent> events, double total)
    {
        VideoAnalysisMedia media = artifact.Sources is { Count: > 0 } ? artifact.Sources[0].Media : artifact.Media;
        double fps = media.FpsDen > 0 ? (double)media.FpsNum / media.FpsDen : 0;
        bool hasAudio = (artifact.Sources ?? []).Any(s => !string.IsNullOrWhiteSpace(s.Media.AudioCodec)) || !string.IsNullOrWhiteSpace(artifact.Media.AudioCodec);
        bool hasSpeech = artifact.Segments.Count > 0 || events.Any(e => e.Type is nameof(VideoEventType.SpeechStart));
        int visual = events.Count(e => IsVisual(e));
        int camera = events.Count(e => e.Type is nameof(VideoEventType.CameraMoveStart));
        return new JsonObject
        {
            ["length"] = total <= 30 ? "short" : total <= 600 ? "medium" : total <= 1800 ? "long" : "very_long",
            ["fpsClass"] = fps <= 0 ? "unknown" : fps <= 26 ? "film" : fps <= 31 ? "standard" : "high",
            ["hasAudio"] = hasAudio,
            ["hasSpeech"] = hasSpeech,
            ["cameraMoves"] = camera == 0 ? "none" : visual > 0 && camera * 4 >= visual ? "much" : "some",
            ["sources"] = Math.Max(1, artifact.Sources?.Count ?? 1),
            ["sectionCount"] = sections.Count
        };
    }

    private static OutlineSection Describe(
        VideoAnalysisArtifact artifact, Dictionary<int, double> offsets, string id, double start, double end, int index, int count,
        List<VideoAnalysisRenderEvent> inside, IReadOnlyDictionary<string, string>? summaries)
    {
        double span = Math.Max(1.0, end - start);
        int visual = inside.Count(IsVisual);
        double ratePer10 = visual / span * 10.0;
        string motion = ratePer10 < 1 ? "still" : ratePer10 < 4 ? "calm" : ratePer10 < 10 ? "busy" : "frantic";

        double spoken = 0;
        foreach (VideoAnalysisSegment seg in artifact.Segments)
        {
            double off = offsets.GetValueOrDefault(seg.SourceIndex);
            spoken += Math.Max(0, Math.Min(end, off + seg.EndSec) - Math.Max(start, off + seg.StartSec));
        }

        double coverage = spoken / span;
        string speech = coverage < 0.03 ? "no speech" : coverage < 0.35 ? "sparse speech" : coverage < 0.7 ? "steady speech" : "dense speech";
        string text = inside.Any(e => e.Type is nameof(VideoEventType.TextIn) or nameof(VideoEventType.TextOut)) ? "text on screen"
            : inside.Any(e => e.Type is nameof(VideoEventType.CaptionTick)) ? "running captions" : "no text";

        Dictionary<string, int> counts = inside
            .GroupBy(e => VideoEventWords.TypeWord(VideoEventWords.ParseType(e.Type)!.Value))
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        string[] dominant = [.. counts.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Take(2).Select(k => k.Key.Replace('_', ' '))];
        string mostly = dominant.Length == 0 ? "no events" : "mostly " + string.Join(" and ", dominant);

        List<VideoAnalysisRenderEvent> ranked = Ranked(inside);
        string? summary = null;
        if (summaries is not null && summaries.TryGetValue(id, out string? raw))
            summary = ClampWords(raw);

        return new OutlineSection(
            id, start, end,
            count == 1 || index == 0 ? "opening" : index == count - 1 ? "closing" : "middle",
            $"{motion} motion, {speech}, {text}, {mostly}",
            counts, [.. ranked.Take(3).Select(e => e.Id)], ranked, summary);
    }

    /// <summary>Events strongest first, equal strength in id order.</summary>
    public static List<VideoAnalysisRenderEvent> Ranked(IEnumerable<VideoAnalysisRenderEvent> events) =>
        [.. events.OrderByDescending(e => e.Strength ?? 0).ThenBy(e => IdNumber(e.Id))];

    /// <summary>Something happening in the picture; a caption word changing is the rhythm of the captions, not motion.</summary>
    private static bool IsVisual(VideoAnalysisRenderEvent e) =>
        e.Type is not (nameof(VideoEventType.Beat) or nameof(VideoEventType.SpeechStart) or nameof(VideoEventType.SpeechEnd) or nameof(VideoEventType.CaptionTick));

    /// <summary>At most <see cref="MaxSummaryWords"/> words, digits and control characters removed.</summary>
    private static string? ClampWords(string raw)
    {
        string[] words = [.. raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(MaxSummaryWords)];
        string joined = new([.. string.Join(' ', words).Where(c => !char.IsControl(c))]);
        return joined.Length == 0 ? null : joined;
    }

    private static Dictionary<int, double> SourceOffsets(VideoAnalysisArtifact artifact, List<VideoAnalysisRenderEvent> events, out double total)
    {
        Dictionary<int, double> lengths = [];
        foreach (VideoAnalysisSourceInfo s in artifact.Sources ?? [])
            lengths[s.SourceIndex] = Math.Max(0, s.Media.DurationSec);
        if (lengths.Count == 0)
            lengths[0] = Math.Max(0, artifact.Media.DurationSec);
        foreach (VideoAnalysisRenderEvent e in events)
        {
            int src = e.SourceIndex!.Value;
            lengths[src] = Math.Max(lengths.GetValueOrDefault(src), e.ContentSec);
        }

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

    private static double[] ChooseCuts(
        VideoAnalysisArtifact artifact, List<VideoAnalysisRenderEvent> events, Dictionary<int, double> offsets, double total)
    {
        int target = Math.Clamp((int)Math.Ceiling(total / SectionTargetSec), MinSections, MaxSections);
        List<(double Time, int Weight)> candidates = [];

        foreach (KeyValuePair<int, double> kv in offsets.OrderBy(k => k.Key).Skip(1))
            candidates.Add((kv.Value, 10));
        foreach (VideoAnalysisShot shot in artifact.Shots.Where(s => s.StartSec > 0))
            candidates.Add((offsets.GetValueOrDefault(shot.SourceIndex) + shot.StartSec, 1));
        foreach (IGrouping<int, VideoAnalysisSegment> group in artifact.Segments.GroupBy(s => s.SourceIndex))
        {
            VideoAnalysisSegment[] ordered = [.. group.OrderBy(s => s.StartSec)];
            for (int i = 1; i < ordered.Length; i++)
                if (ordered[i].StartSec - ordered[i - 1].EndSec >= ParagraphGapSec)
                    candidates.Add((offsets.GetValueOrDefault(group.Key) + ordered[i].StartSec, 2));
        }

        foreach (VideoAnalysisSilenceSpan idle in artifact.SilenceSpans.Where(s => s.EndSec - s.StartSec >= LongIdleSec))
            candidates.Add((offsets.GetValueOrDefault(idle.SourceIndex) + idle.StartSec, 2));
        foreach (VideoAnalysisRenderEvent e in events.Where(e => e.Type is nameof(VideoEventType.Transition) && (e.Strength ?? 0) >= BigTransition))
            candidates.Add((offsets[e.SourceIndex!.Value] + e.ContentSec, 3));

        double minGap = Math.Max(1.0, total / (target * 3.0));
        List<double> accepted = [];
        foreach ((double time, int weight) in candidates.OrderByDescending(c => c.Weight).ThenBy(c => c.Time))
        {
            if (accepted.Count + 1 >= target)
                break;
            if (time <= 0 || time >= total)
                continue;
            if (weight < 10 && (time < minGap || total - time < minGap || accepted.Any(a => Math.Abs(a - time) < minGap)))
                continue;
            if (weight >= 10 && accepted.Contains(time))
                continue;
            accepted.Add(time);
        }

        // Too few natural cuts for the minimum: split the longest stretch in two until there are three sections.
        while (accepted.Count + 1 < MinSections)
        {
            List<double> bounds = [0, .. accepted.OrderBy(a => a), total];
            int widest = 0;
            for (int i = 1; i < bounds.Count - 1 + 1 && i < bounds.Count; i++)
                if (bounds[i] - bounds[i - 1] > bounds[widest + 1] - bounds[widest])
                    widest = i - 1;
            accepted.Add((bounds[widest] + bounds[widest + 1]) / 2);
        }

        return [.. accepted.OrderBy(a => a)];
    }

    /// <summary>
    /// The L1 view: the events of every section (strongest first, at most 25 and 1,800 tokens each), trimmed lowest strength
    /// first so the whole fits <paramref name="totalTokenBudget"/> beside the outline.
    /// </summary>
    public static VideoEventSectionView BuildSectionView(
        VideoAnalysisArtifact artifact, VideoEventOutlineResult outline, int totalTokenBudget = InitialPromptTokenBudget)
    {
        int room = Math.Max(0, totalTokenBudget - outline.TokensEstimate);
        List<(string Section, VideoAnalysisRenderEvent Event, JsonObject Node, int Tokens)> all = [];
        int total = 0;
        int suppressed = 0;
        foreach (OutlineSection section in outline.Sections)
        {
            total += section.Events.Count;
            int used = 0;
            int kept = 0;
            foreach (VideoAnalysisRenderEvent e in section.Events)
            {
                JsonObject node = EventNode(artifact, e);
                int tokens = VideoEventWords.EstimateTokens(node.ToJsonString()) + 1;
                if (kept >= MaxEventsPerSection || used + tokens > SectionTokenBudget)
                {
                    suppressed++;
                    continue;
                }

                used += tokens;
                kept++;
                all.Add((section.Id, e, node, tokens));
            }
        }

        // Whole-prompt trim: drop the weakest event first (latest id first among equals) until the view fits.
        int sum = all.Sum(a => a.Tokens) + outline.Sections.Count * 8;
        if (sum > room)
        {
            HashSet<string> dropped = [];
            foreach (var item in all.OrderBy(a => a.Event.Strength ?? 0).ThenByDescending(a => IdNumber(a.Event.Id)))
            {
                if (sum <= room)
                    break;
                dropped.Add(item.Event.Id);
                sum -= item.Tokens;
                suppressed++;
            }

            all = [.. all.Where(a => !dropped.Contains(a.Event.Id))];
        }

        JsonObject sections = [];
        foreach (OutlineSection section in outline.Sections)
            sections[section.Id] = new JsonArray([.. all.Where(a => a.Section == section.Id).Select(a => (JsonNode)a.Node)]);
        return new VideoEventSectionView(sections, Math.Max(0, sum), suppressed, total);
    }

    /// <summary>One event as an agent sees it: id, type, strength word, region word, nearest speech line and shot.</summary>
    public static JsonObject EventNode(VideoAnalysisArtifact artifact, VideoAnalysisRenderEvent e)
    {
        JsonObject node = new()
        {
            ["id"] = e.Id,
            ["type"] = VideoEventWords.TypeWord(VideoEventWords.ParseType(e.Type)!.Value),
            ["strength"] = VideoEventWords.StrengthWord(e.Strength ?? 0)
        };
        if (e.Region is { } region)
            node["region"] = VideoEventWords.RegionWord(region);
        if (VideoEventWords.What(e) is { } what)
            node["what"] = what;
        if (e.Seen is { Length: > 0 } seen)
            node["seen"] = seen;
        if (VideoEventKinds.Parse(e.Kind) is { } kind)
            node["kind"] = kind;
        // The analysis kept a picture of this moment that LookAtMoment can ask about.
        if (artifact.EventStripIds is { Count: > 0 } pictures && pictures.Contains(e.Id, StringComparer.Ordinal))
            node[VideoEventStrips.LookMark] = true;
        int src = e.SourceIndex ?? 0;
        VideoAnalysisSegment? line = artifact.Segments
            .Where(s => s.SourceIndex == src)
            .Select(s => (Seg: s, Dist: e.ContentSec < s.StartSec ? s.StartSec - e.ContentSec : e.ContentSec > s.EndSec ? e.ContentSec - s.EndSec : 0))
            .Where(t => t.Dist <= 3.0)
            .OrderBy(t => t.Dist).ThenBy(t => IdNumber(t.Seg.Id))
            .Select(t => t.Seg)
            .FirstOrDefault();
        if (line is not null)
            node["speech"] = line.Id;
        VideoAnalysisShot? shot = artifact.Shots.FirstOrDefault(s => s.SourceIndex == src && e.ContentSec >= s.StartSec && e.ContentSec < s.EndSec);
        if (shot is not null)
            node["shot"] = shot.Id;
        return node;
    }
}
