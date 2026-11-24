using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;

namespace ReelBolt.Shared.Editing.Proposals;

/// <summary>R11.2: one spoken line of a source file (seconds of the SOURCE file). Text is untrusted until <see cref="ProposalFactText.Clean"/>.</summary>
public sealed record FactLine(double StartSec, double EndSec, string Text);

/// <summary>One transcript word of a source file with its timing (seconds of the SOURCE file). Used only by the compiler to cut by word; never sent to the model as a number.</summary>
public sealed record FactWord(double StartSec, double EndSec, string Text);

/// <summary>R11.2: one shot of a source file, every descriptor already a plain WORD (never a number): motion, brightness, colour words and an optional caption.</summary>
public sealed record FactShot(double StartSec, double EndSec, string? Motion, string? Brightness, string? Colour, string? Caption);

/// <summary>R11.2: one quiet gap of a source file.</summary>
public sealed record FactGap(double StartSec, double EndSec);

/// <summary>
/// cut-to-music: the stored beat grid of a music file (seconds of the SOURCE file). The words of the view come from
/// <see cref="Steady"/>, <see cref="Confidence"/>, <see cref="TempoWord"/>, <see cref="BeatsPerBar"/> and <see cref="HasDrop"/>;
/// the times are only ever read by the compiler, never shown to the model.
/// </summary>
public sealed record BeatFacts(IReadOnlyList<double> BeatTimesSec, double BeatSec, int BeatsPerBar, int FirstDownbeatIndex, double Confidence, bool Steady, string TempoWord, bool HasDrop)
{
    public const double UnclearBelow = 0.3;

    private static readonly string[] BarWords = ["", "one", "two", "three", "four", "five", "six", "seven", "eight"];

    /// <summary>Tempo as a word from the stored BPM (same thresholds as the beat tracker).</summary>
    public static string TempoWordOf(double bpm) => bpm < 90 ? "slow" : bpm < 120 ? "medium" : bpm < 150 ? "fast" : "very fast";

    /// <summary>The beat words of one music clip, never a digit: "music has a steady beat, fast tempo, four beats to a bar, and a drop".</summary>
    public string Words()
    {
        if (Confidence < UnclearBelow) return "music: the beat is unclear";
        List<string> parts = [Steady ? "a steady beat" : "a beat, the tempo drifts", $"{TempoWord} tempo"];
        if (BeatsPerBar is >= 2 and <= 8) parts.Add($"{BarWords[BeatsPerBar]} beats to a bar");
        if (HasDrop) parts.Add("a drop");
        string joined = parts.Count == 1 ? parts[0] : parts.Count == 2 ? $"{parts[0]} and {parts[1]}" : string.Join(", ", parts.Take(parts.Count - 1)) + ", and " + parts[^1];
        return "music has " + joined;
    }

    /// <summary>Whether the beat with this index (into <see cref="BeatTimesSec"/>) is a bar line.</summary>
    public bool IsBar(int index) => BeatsPerBar >= 1 && (((index - FirstDownbeatIndex) % BeatsPerBar) + BeatsPerBar) % BeatsPerBar == 0;
}

/// <summary>
/// One moment the events analysis found in the picture of a source file (seconds of the SOURCE file), already narrowed to
/// the moments a sound belongs on. Every descriptor is a plain word from a closed table: the type word
/// (<c>appear</c>, <c>hit</c>...), the strength word, what happens, and the kind of picture when the code could tell.
/// <see cref="Strength"/> only ranks moments against each other; it is never shown.
/// </summary>
public sealed record FactMoment(double TimeSec, string TypeWord, string StrengthWord, string? What, string? Kind, double Strength);

/// <summary>R11.2: everything stored analysis says about one source file. Empty lists mean "not analysed", never "nothing there".</summary>
public sealed record SourceFacts(IReadOnlyList<FactLine> Lines, IReadOnlyList<FactShot> Shots, IReadOnlyList<FactGap> Gaps, string? InputProfile = null, BeatFacts? Beats = null)
{
    /// <summary>log-footage-color: the camera profile id of a source whose stored probe the detector is High on (a clip's own inputProfile wins at render time); never counts as a fact.</summary>
    public static readonly SourceFacts Empty = new([], [], []);

    /// <summary>Word timings of the transcript analysis (empty when unknown, e.g. only segments exist). Lines are derived from them.</summary>
    public IReadOnlyList<FactWord> Words { get; init; } = [];

    /// <summary>The sound-worthy moments of the stored events analysis, in time order (empty when the file has none stored).</summary>
    public IReadOnlyList<FactMoment> Moments { get; init; } = [];

    /// <summary>Beats and moments are not transcript or shots: they never count as a fact and never change the grounding level.</summary>
    public bool IsEmpty => Lines.Count == 0 && Shots.Count == 0 && Gaps.Count == 0;

    public bool HasBeats => Beats is { BeatTimesSec.Count: > 0 };

    public bool HasMoments => Moments.Count > 0;

    /// <summary>A shot descriptor with colour or brightness words: what the Grade agent needs.</summary>
    public bool HasColourFacts => Shots.Any(s => !string.IsNullOrEmpty(s.Colour) || !string.IsNullOrEmpty(s.Brightness));
}

/// <summary>R11.2: how much of the range the model's facts cover; shown to the editor and stated to the model.</summary>
public sealed record ProposalGrounding(string Level, int ClipsWithFacts, int ClipsTotal, string Note, bool HasColourFacts, bool HasTranscriptOrShotFacts, int TranscriptChars = 0, double TranscriptSeconds = 0)
{
    public const string TranscriptAndShots = "transcriptAndShots";
    public const string TranscriptOnly = "transcriptOnly";
    public const string ShotsOnly = "shotsOnly";
    public const string NamesOnly = "namesOnly";

    public const string NamesOnlyNote = "Cutting by length only: analyse this footage first for a smarter cut.";

    public static ProposalGrounding None(int clipsTotal) => new(NamesOnly, 0, clipsTotal, NamesOnlyNote, false, false);

    public static ProposalGrounding Compute(int withFacts, int total, bool lines, bool gaps, bool shots, bool colour, int transcriptChars, double transcriptSeconds)
    {
        bool spoken = lines || gaps;
        string level = spoken && shots ? TranscriptAndShots : spoken ? TranscriptOnly : shots ? ShotsOnly : NamesOnly;
        if (withFacts == 0 || level == NamesOnly) return new ProposalGrounding(NamesOnly, 0, total, NamesOnlyNote, false, false);
        string basis = level switch
        {
            TranscriptAndShots => "what is said and shown",
            TranscriptOnly => "what is said",
            _ => "what is shown"
        };
        string note = withFacts >= total
            ? $"Cutting from {basis} in your footage."
            : $"Cutting from {basis} in {withFacts} of {total} clips; the others are cut by length only.";
        return new ProposalGrounding(level, withFacts, total, note, colour, lines || shots, transcriptChars, transcriptSeconds);
    }
}

/// <summary>
/// The moments of a stored per-file <c>events</c> result that a proposal helper is told about. The choice is the workflow
/// sound designer's own (<see cref="SfxWorthyEvents"/>): caption words, weak events, beats, speech edges and ordinary
/// filmed footage are left out, and what is left is thinned to one moment per place. Pure; the words are worked out here,
/// from the closed tables of <see cref="VideoEventWords"/>, and never stored.
/// </summary>
public static class ProposalMoments
{
    /// <summary>The most moments one clip lists; the strongest stay.</summary>
    public const int MaxPerClip = 12;

    /// <summary>The most moments one view lists over all its clips; the weakest go first.</summary>
    public const int MaxPerView = 48;

    /// <summary>
    /// Which candidate moments of a view stay, as indexes into <paramref name="candidates"/> in their given order. The weakest
    /// go first (ties: the later clip, then the later frame) until at most <see cref="MaxPerView"/> are left and their lines
    /// fit <paramref name="maxChars"/>. One sort and one pass, whatever the number of candidates.
    /// </summary>
    internal static List<int> KeptIndexes(IReadOnlyList<(int Clip, long Frame, double Strength, int Chars)> candidates, int maxChars)
    {
        int left = candidates.Count;
        long chars = 0;
        foreach ((int Clip, long Frame, double Strength, int Chars) c in candidates) chars += c.Chars;
        if (left <= MaxPerView && chars <= maxChars) return [.. Enumerable.Range(0, left)];

        int[] weakestFirst =
        [
            .. Enumerable.Range(0, left)
                .OrderBy(i => candidates[i].Strength)
                .ThenByDescending(i => candidates[i].Clip)
                .ThenByDescending(i => candidates[i].Frame),
        ];
        int dropped = 0;
        while (dropped < weakestFirst.Length && (left > MaxPerView || chars > maxChars))
        {
            chars -= candidates[weakestFirst[dropped]].Chars;
            left--;
            dropped++;
        }

        int[] stay = weakestFirst[dropped..];
        Array.Sort(stay);
        return [.. stay];
    }

    public static IReadOnlyList<FactMoment> FromStored(AnalysisEventsResult? stored)
    {
        if (stored?.Events is not { Count: > 0 } events) return [];
        double fps = stored.Fps > 0 && double.IsFinite(stored.Fps) ? stored.Fps : 30.0;
        List<VideoAnalysisRenderEvent> offered = [];
        Dictionary<string, VideoEventType> types = new(StringComparer.Ordinal);
        foreach (AnalysisEvent? e in events.Take(MediaAnalysisJson.MaxItems))
        {
            if (e is null || VideoEventWords.ParseType(e.Type) is not { } type) continue;
            if (e.Frame < 0 || !double.IsFinite(e.TimeSec) || e.TimeSec < 0 || !double.IsFinite(e.Strength)) continue;
            // Own ids: the selection keys on them, so they must be unique whatever the stored row says.
            string id = "e" + offered.Count.ToString(CultureInfo.InvariantCulture);
            VideoEventRegion? region = e.Region is { } r && double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.W) && double.IsFinite(r.H)
                ? new VideoEventRegion(r.X, r.Y, r.W, r.H)
                : null;
            types[id] = type;
            offered.Add(new VideoAnalysisRenderEvent(
                id, e.Type, e.Type, e.Frame, e.TimeSec, SourceIndex: 0, Type: type.ToString(), Strength: e.Strength, Region: region,
                EndFrame: e.EndFrame, Direction: e.Direction, SpeedWord: e.Speed, Kind: VideoEventKinds.Parse(e.Kind)));
        }

        double? duration = stored.FrameCount > 0 ? stored.FrameCount / fps : null;
        return SfxWorthyEvents.Select(offered, _ => duration)
            .OrderBy(e => e.ContentSec)
            .Select(e => new FactMoment(
                e.ContentSec,
                VideoEventWords.TypeWord(types[e.Id]),
                VideoEventWords.StrengthWord(e.Strength ?? 0),
                VideoEventWords.What(types[e.Id], e.Frame, e.EndFrame, e.Region, e.Direction, e.SpeedWord),
                e.Kind,
                e.Strength ?? 0))
            .ToList();
    }
}

/// <summary>R11.2: the text and word rules of the grounded view. Transcript and captions are untrusted: sanitised, quoted, capped.</summary>
public static class ProposalFactText
{
    public const int MaxLineChars = 200;
    public const int DefaultMaxTranscriptChars = 6000;
    public const int MaxShotsPerClip = 12;
    public const int MaxGapsPerClip = 6;

    private static readonly Regex Space = new(@"\s+", RegexOptions.Compiled);

    private static readonly string[] NumberWords = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];

    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "um", "uh", "uhm", "umm", "er", "erm", "hmm", "mm", "ah", "oh", "like", "so", "okay", "ok", "well", "right", "yeah", "you", "know", "i", "mean", "and", "the"
    };

    /// <summary>
    /// The shared caption/overlay sanitiser (allowlist, NFC, whitespace collapsed), then double quotes and bars removed (the
    /// view fences the text in double quotes), then cut at a word boundary: never mid-word. Empty when nothing usable is left.
    /// </summary>
    public static string Clean(string? raw, int maxChars = MaxLineChars)
    {
        string s = OverlayTextSanitizer.Sanitize(raw, 2000).Replace('"', ' ').Replace('|', ' ');
        s = Space.Replace(s, " ").Trim();
        return CapAtWord(s, maxChars);
    }

    public static string CapAtWord(string s, int maxChars)
    {
        if (s.Length <= maxChars) return s;
        int cut = s.LastIndexOf(' ', maxChars);
        return cut <= 0 ? string.Empty : s[..cut].TrimEnd();
    }

    /// <summary>Groups transcript words into lines: a line ends at a pause, sentence-ending punctuation or after about fourteen words.</summary>
    public static IReadOnlyList<FactLine> LinesFromWords(IEnumerable<(string Text, double StartSec, double EndSec)> words)
    {
        List<FactLine> lines = [];
        StringBuilder sb = new();
        double start = 0, end = 0;
        int count = 0;
        void Flush()
        {
            if (count > 0) lines.Add(new FactLine(start, end, sb.ToString().Trim()));
            sb.Clear();
            count = 0;
        }

        foreach ((string text, double s, double e) in words.OrderBy(w => w.StartSec))
        {
            string t = (text ?? string.Empty).Trim();
            if (t.Length == 0) continue;
            if (count > 0 && s - end > 0.7) Flush();
            if (count == 0) start = s;
            sb.Append(t).Append(' ');
            end = Math.Max(e, s);
            count++;
            if (count >= 14 || t.EndsWith('.') || t.EndsWith('!') || t.EndsWith('?')) Flush();
        }

        Flush();
        return lines;
    }

    /// <summary>Keeps the highest-value lines whose cleaned text fits <paramref name="maxChars"/> in total: filler and repeats go first, then the shortest. Time order is preserved.</summary>
    public static List<(int Clip, string Text, double StartSec, double EndSec)> SelectLines(IReadOnlyList<(int Clip, string Text, double StartSec, double EndSec)> candidates, int maxChars)
    {
        List<(int Clip, string Text, double StartSec, double EndSec, int Score, int Order)> scored = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        int order = 0;
        foreach ((int clip, string text, double s, double e) in candidates)
        {
            string cleaned = Clean(text);
            if (cleaned.Length == 0) continue;
            string norm = new(cleaned.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray());
            string[] w = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool filler = w.Length == 0 || w.All(Filler.Contains);
            bool duplicate = !seen.Add(norm);
            int score = filler || duplicate ? 0 : 1 + w.Length;
            scored.Add((clip, cleaned, s, e, score, order++));
        }

        // The weakest lines go first until the text fits. One sort and one pass: removing from the list one line at a
        // time is quadratic on a long transcript.
        long total = scored.Sum(x => (long)x.Text.Length);
        HashSet<int> dropped = [];
        foreach (var drop in scored.OrderBy(x => x.Score).ThenByDescending(x => x.Order))
        {
            if (total <= maxChars) break;
            dropped.Add(drop.Order);
            total -= drop.Text.Length;
        }

        return scored.Where(x => !dropped.Contains(x.Order)).OrderBy(x => x.Order)
            .Select(x => (x.Clip, x.Text, x.StartSec, x.EndSec)).ToList();
    }

    public static string DurationWord(double seconds) => seconds < 2 ? "short" : seconds < 6 ? "medium" : "long";

    public static string GapWords(double seconds)
    {
        if (seconds < 1) return "a brief quiet gap";
        if (seconds >= 10) return "a long quiet gap";
        int n = (int)Math.Round(seconds);
        return $"quiet gap, about {NumberWords[Math.Clamp(n, 1, 10)]} {(n == 1 ? "second" : "seconds")}";
    }

    public static string BrightnessWord(double luma) => luma < 0.3 ? "dark" : luma < 0.6 ? "balanced light" : "bright";

    public static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
