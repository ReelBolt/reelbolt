using System.Globalization;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// One voiceover line as the caption stage sees it: the SANITIZED text the Voiceover step actually
/// synthesized (never the narration plan's raw text), where that line lands on the OUTPUT
/// timeline, how long it plays, and — when the Voiceover step's alignment pass produced them — the
/// per-word timings RELATIVE to the start of the line's own audio.
/// </summary>
public sealed record CaptionSourceLine(
    string LineId,
    string Text,
    double OutputStartSec,
    double PlayDurationSec,
    IReadOnlyList<CaptionSourceWord>? Words);

/// <summary>A recognised word with timings relative to the start of its line's audio.</summary>
public sealed record CaptionSourceWord(string Text, double StartSec, double EndSec);

/// <summary>
/// A fully-resolved caption cue, ready for <see cref="CaptionFilterBuilder"/>. Every field is a
/// safe, concrete value: already-sanitized text, OUTPUT-timeline seconds computed in C#, and for
/// <see cref="CaptionStyle.Karaoke"/> the prefix of <see cref="Text"/> that has been spoken by the
/// time this cue is showing. The model never supplied any of the timing here.
/// </summary>
public sealed record ResolvedCaptionCue(
    string LineId,
    int CueIndex,
    string Text,
    double OutputStartSec,
    double OutputEndSec,
    string? HighlightPrefix = null,
    /// <summary>Draw this cue at the top instead of the bottom: the shot under it carries its own text in the bottom band (see <c>CaptionPlacementProbe</c>).</summary>
    bool AtTop = false);

/// <summary>The outcome of cutting cues from a set of lines: the cues, the lines that could not contribute (with a reason each), and whether the cue cap truncated the list.</summary>
public sealed record CaptionCueBuildResult(
    IReadOnlyList<ResolvedCaptionCue> Cues,
    IReadOnlyList<(string LineId, string Reason)> Dropped,
    bool Truncated);

/// <summary>
/// Cuts caption cues from applied voiceover lines (see docs/voiceover.md "Captions"). Pure — no
/// I/O. The three styles differ only in how a line is divided and timed:
/// <list type="bullet">
/// <item><see cref="CaptionStyle.Subtitle"/>: the line is split at word boundaries into chunks of at
/// most <c>maxCharsPerCue</c> characters; each chunk shows from its first word's start to its last
/// word's end when word timings exist, and for a proportional share of the line's play duration
/// (by character count) when they do not. Works with or without alignment.</item>
/// <item><see cref="CaptionStyle.Karaoke"/>: the same chunks, but one cue per WORD, each carrying
/// the chunk's text plus the prefix spoken so far — drawn left-anchored in the highlight colour over
/// the base text, so the spoken words change colour as they are spoken. Needs word timings; a line
/// without them is dropped with <c>no_word_alignment</c>.</item>
/// <item><see cref="CaptionStyle.Punch"/>: one word per cue. Needs word timings, same degrade.</item>
/// </list>
/// When a line's recognised word count differs from its text's token count (the recogniser split or
/// merged a token), tokens are mapped onto the recognised words proportionally by index — a caption
/// that says the words the narrator spoke, timed to within a word, rather than a dropped line.
/// </summary>
public static class CaptionCueBuilder
{
    /// <summary>The shortest a cue is ever shown, so a word boundary the recogniser placed at zero width still reads.</summary>
    public const double MinCueSec = 0.08;

    public const string NoWordAlignmentReason = "no_word_alignment";
    public const string EmptyTextReason = "empty_text";
    public const string MaxCuesExceededReason = "max_caption_cues_exceeded";

    public static CaptionCueBuildResult Build(
        CaptionStyle style,
        IReadOnlyList<CaptionSourceLine> lines,
        int maxCharsPerCue,
        int maxCues)
    {
        int charsPerCue = Math.Clamp(maxCharsPerCue, 12, 120);
        int cueCap = Math.Max(1, maxCues);

        List<ResolvedCaptionCue> cues = [];
        List<(string LineId, string Reason)> dropped = [];
        bool truncated = false;

        foreach (CaptionSourceLine line in lines.OrderBy(l => l.OutputStartSec))
        {
            string[] tokens = Tokenize(line.Text);
            if (tokens.Length == 0)
            {
                dropped.Add((line.LineId, EmptyTextReason));
                continue;
            }

            IReadOnlyList<CaptionSourceWord>? words = NormalizeWords(line.Words);
            bool needsWords = style is not (CaptionStyle.Subtitle or CaptionStyle.Bold);
            if (needsWords && words is null)
            {
                dropped.Add((line.LineId, NoWordAlignmentReason));
                continue;
            }

            double lineStart = Math.Max(0, line.OutputStartSec);
            double lineDuration = double.IsFinite(line.PlayDurationSec) && line.PlayDurationSec > 0 ? line.PlayDurationSec : 0;
            double lineEnd = lineStart + lineDuration;

            // Token i's [start, end) relative to the line's own audio.
            (double Start, double End)[] tokenWindows = words is not null
                ? MapTokensToWords(tokens, words, lineDuration)
                : ProportionalWindows(tokens, lineDuration);

            List<ResolvedCaptionCue> lineCues = style switch
            {
                CaptionStyle.Punch => BuildPunch(line.LineId, tokens, tokenWindows, lineStart, lineEnd),
                CaptionStyle.Karaoke => BuildKaraoke(line.LineId, tokens, tokenWindows, charsPerCue, lineStart, lineEnd),
                CaptionStyle.Bold => BuildSubtitle(line.LineId, tokens, tokenWindows, BoldCharsPerCue(charsPerCue), lineStart, lineEnd, BoldMaxWords),
                _ => BuildSubtitle(line.LineId, tokens, tokenWindows, charsPerCue, lineStart, lineEnd)
            };

            foreach (ResolvedCaptionCue cue in lineCues)
            {
                if (cues.Count >= cueCap)
                {
                    truncated = true;
                    break;
                }

                cues.Add(cue with { CueIndex = cues.Count });
            }

            if (truncated)
            {
                dropped.Add((line.LineId, MaxCuesExceededReason));
                break;
            }
        }

        return new CaptionCueBuildResult(cues, dropped, truncated);
    }

    internal static string[] Tokenize(string text) =>
        (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyList<CaptionSourceWord>? NormalizeWords(IReadOnlyList<CaptionSourceWord>? words)
    {
        if (words is null || words.Count == 0)
            return null;

        List<CaptionSourceWord> clean = [];
        foreach (CaptionSourceWord w in words)
        {
            if (w is null || !double.IsFinite(w.StartSec) || !double.IsFinite(w.EndSec))
                continue;
            double start = Math.Max(0, w.StartSec);
            double end = Math.Max(start, w.EndSec);
            clean.Add(new CaptionSourceWord(w.Text ?? string.Empty, start, end));
        }

        return clean.Count == 0 ? null : clean.OrderBy(w => w.StartSec).ToList();
    }

    /// <summary>
    /// Maps text tokens onto recognised words. Equal counts pair 1:1; otherwise token <c>i</c> takes
    /// the window of recognised word <c>floor(i * words / tokens)</c>, which keeps the mapping
    /// monotonic and total — every token gets a window, no word is skipped past the line's end.
    /// </summary>
    internal static (double Start, double End)[] MapTokensToWords(
        string[] tokens, IReadOnlyList<CaptionSourceWord> words, double lineDuration)
    {
        var result = new (double Start, double End)[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            int wordIndex = tokens.Length == words.Count
                ? i
                : Math.Min(words.Count - 1, (int)Math.Floor(i * (double)words.Count / tokens.Length));
            CaptionSourceWord word = words[wordIndex];
            double start = word.StartSec;
            // A word's end is the next word's start when the recogniser left a gap shorter than the
            // minimum cue, so consecutive cues abut rather than flicker.
            double end = word.EndSec;
            if (wordIndex + 1 < words.Count)
            {
                double nextStart = words[wordIndex + 1].StartSec;
                if (nextStart > start && nextStart - end < MinCueSec)
                    end = nextStart;
            }
            else if (lineDuration > 0 && end < lineDuration && lineDuration - end < 0.5)
            {
                end = lineDuration;
            }

            result[i] = (start, Math.Max(start + MinCueSec, end));
        }

        return result;
    }

    /// <summary>Without word timings, each token owns a share of the line's duration proportional to its character count (plus one for the space).</summary>
    internal static (double Start, double End)[] ProportionalWindows(string[] tokens, double lineDuration)
    {
        var result = new (double Start, double End)[tokens.Length];
        double totalChars = tokens.Sum(t => t.Length + 1);
        if (totalChars <= 0 || lineDuration <= 0)
        {
            for (int i = 0; i < tokens.Length; i++)
                result[i] = (0, MinCueSec);
            return result;
        }

        double cursor = 0;
        for (int i = 0; i < tokens.Length; i++)
        {
            double share = (tokens[i].Length + 1) / totalChars * lineDuration;
            result[i] = (cursor, cursor + share);
            cursor += share;
        }

        return result;
    }

    private static List<(int From, int To)> Chunk(string[] tokens, int charsPerCue)
    {
        List<(int From, int To)> chunks = [];
        int from = 0;
        int length = 0;
        for (int i = 0; i < tokens.Length; i++)
        {
            int added = tokens[i].Length + (length > 0 ? 1 : 0);
            if (length > 0 && length + added > charsPerCue)
            {
                chunks.Add((from, i));
                from = i;
                length = tokens[i].Length;
            }
            else
            {
                length += added;
            }
        }

        if (from < tokens.Length)
            chunks.Add((from, tokens.Length));

        return chunks;
    }

    /// <summary>Most words a Bold cue shows at once.</summary>
    internal const int BoldMaxWords = 2;

    /// <summary>A Bold cue's character budget: short enough for big type, never above the configured one.</summary>
    internal static int BoldCharsPerCue(int charsPerCue) => Math.Clamp(charsPerCue, 6, 12);

    private static List<ResolvedCaptionCue> BuildSubtitle(
        string lineId, string[] tokens, (double Start, double End)[] windows, int charsPerCue, double lineStart, double lineEnd,
        int maxWords = int.MaxValue)
    {
        List<ResolvedCaptionCue> cues = [];
        foreach ((int from, int to) in Chunk(tokens, charsPerCue).SelectMany(c => SplitByWords(c, maxWords)))
        {
            double start = lineStart + windows[from].Start;
            double end = lineStart + windows[to - 1].End;
            cues.Add(MakeCue(lineId, string.Join(' ', tokens, from, to - from), start, end, lineStart, lineEnd));
        }

        return cues;
    }

    /// <summary>A chunk <c>[from, to)</c> cut into pieces of at most <paramref name="maxWords"/> tokens.</summary>
    private static IEnumerable<(int From, int To)> SplitByWords((int From, int To) chunk, int maxWords)
    {
        if (chunk.To - chunk.From <= maxWords)
        {
            yield return chunk;
            yield break;
        }

        for (int i = chunk.From; i < chunk.To; i += maxWords)
            yield return (i, Math.Min(chunk.To, i + maxWords));
    }

    private static List<ResolvedCaptionCue> BuildKaraoke(
        string lineId, string[] tokens, (double Start, double End)[] windows, int charsPerCue, double lineStart, double lineEnd)
    {
        List<ResolvedCaptionCue> cues = [];
        foreach ((int from, int to) in Chunk(tokens, charsPerCue))
        {
            string chunkText = string.Join(' ', tokens, from, to - from);
            for (int i = from; i < to; i++)
            {
                double start = lineStart + windows[i].Start;
                // The cue holds until the NEXT word in the chunk begins (so the highlight never goes
                // dark between words), or until the chunk's last word ends.
                double end = i + 1 < to ? lineStart + windows[i + 1].Start : lineStart + windows[to - 1].End;
                string prefix = string.Join(' ', tokens, from, i - from + 1);
                cues.Add(MakeCue(lineId, chunkText, start, end, lineStart, lineEnd, prefix));
            }
        }

        return cues;
    }

    private static List<ResolvedCaptionCue> BuildPunch(
        string lineId, string[] tokens, (double Start, double End)[] windows, double lineStart, double lineEnd)
    {
        List<ResolvedCaptionCue> cues = [];
        for (int i = 0; i < tokens.Length; i++)
        {
            double start = lineStart + windows[i].Start;
            double end = i + 1 < tokens.Length ? lineStart + windows[i + 1].Start : lineStart + windows[i].End;
            cues.Add(MakeCue(lineId, tokens[i], start, end, lineStart, lineEnd));
        }

        return cues;
    }

    private static ResolvedCaptionCue MakeCue(
        string lineId, string text, double start, double end, double lineStart, double lineEnd, string? highlightPrefix = null)
    {
        // Clamp to the line's own output window — a cue can never show before its line begins or
        // after it has finished playing — then guarantee the minimum visible duration.
        double clampedStart = Math.Clamp(start, lineStart, Math.Max(lineStart, lineEnd));
        double clampedEnd = lineEnd > lineStart ? Math.Min(end, lineEnd) : end;
        if (clampedEnd < clampedStart + MinCueSec)
            clampedEnd = clampedStart + MinCueSec;

        return new ResolvedCaptionCue(
            lineId, 0, text, Math.Round(clampedStart, 3), Math.Round(clampedEnd, 3), highlightPrefix);
    }
}

/// <summary>
/// Builds the filter-chain fragment for burned captions (see docs/voiceover.md "Captions"): the
/// libass <c>ass</c> filter (<see cref="BuildAssFilter"/>, the normal path), or — only when this
/// ffmpeg build has no libass — the drawbox/drawtext chain (<see cref="BuildFilterChain"/>).
/// Pure — no I/O, no ffmpeg. Exactly like <see cref="DrawtextFilterBuilder"/>, text
/// never appears in the returned filter string: the caller writes each cue's sanitized text to its
/// own scratch file BEFORE calling this (<see cref="MainTextSlot"/>/<see cref="HighlightSlot"/>) and
/// passes only a path-resolving callback, and every drawtext carries <c>textfile=</c> plus
/// <c>expansion=none</c>. All geometry and timing is computed here from already-validated numbers
/// (a probed frame size and output-timeline windows) and formatted culture-invariantly.
/// </summary>
public static class CaptionFilterBuilder
{
    /// <summary>The scratch-file slot for cue <paramref name="cueIndex"/>'s text.</summary>
    public static int MainTextSlot(int cueIndex) => cueIndex * 2;

    /// <summary>The scratch-file slot for cue <paramref name="cueIndex"/>'s Karaoke highlight prefix (only used when the cue carries one).</summary>
    public static int HighlightSlot(int cueIndex) => cueIndex * 2 + 1;

    /// <summary>The scratch file name for a slot — the single source of truth shared with the executor's writer.</summary>
    public static string SlotFileName(int slot) => $"cap-{slot}.txt";

    public static string BuildFilterChain(
        string baseLabel,
        IReadOnlyList<ResolvedCaptionCue> cues,
        int probedWidth,
        int probedHeight,
        CaptionStyle style,
        int fontSizePct,
        string fontColor,
        string boxColor,
        string highlightColor,
        string? fontFilePath,
        Func<int, string> textFilePathForSlot,
        string finalLabel = "[vout]",
        int? fontSizePx = null)
    {
        if (cues.Count == 0)
            throw new ArgumentException("BuildFilterChain requires at least one cue.", nameof(cues));

        int width = Math.Max(16, probedWidth);
        int height = Math.Max(16, probedHeight);
        // fontSizePx: an already-resolved size (the automatic caption size, see CaptionLayout)
        // that takes precedence over the percentage.
        int fontSize = fontSizePx is int px && px > 0 ? px : ComputeFontSize(height, fontSizePct, style);
        bool skipBox = string.Equals(boxColor, "none", StringComparison.OrdinalIgnoreCase);

        // A single band near the bottom of the frame for Subtitle/Karaoke; Punch sits higher and
        // centred with no band (its one word IS the graphic).
        int boxH = (int)Math.Round(fontSize * 1.7);
        int boxX = (int)Math.Round(width * 0.08);
        int boxW = width - 2 * boxX;
        int boxY = height - (int)Math.Round(height * 0.10) - boxH;
        int textPadX = Math.Max(4, (int)Math.Round(fontSize * 0.5));

        List<string> segments = new();
        string currentLabel = baseLabel;
        string font = DrawtextFilterBuilder.FontFileOption(fontFilePath);

        for (int i = 0; i < cues.Count; i++)
        {
            ResolvedCaptionCue cue = cues[i];
            bool isLast = i == cues.Count - 1;
            bool hasHighlight = style == CaptionStyle.Karaoke && !string.IsNullOrEmpty(cue.HighlightPrefix);

            string start = FfmpegArgvFormat.Number(cue.OutputStartSec);
            string end = FfmpegArgvFormat.Number(cue.OutputEndSec);
            // gte/lt rather than between(): between is inclusive on both ends, which would show two
            // abutting cues on the same frame at their shared boundary.
            string enableExpr = $"gte(t,{start})*lt(t,{end})";

            string label = currentLabel;
            if (style is not (CaptionStyle.Punch or CaptionStyle.Bold) && !skipBox)
            {
                label = $"[cap{i}b]";
                segments.Add(
                    $"{currentLabel}drawbox=x={boxX}:y={boxY}:w={boxW}:h={boxH}:color={boxColor}:t=fill:" +
                    $"enable='{enableExpr}'{label}");
            }

            string mainPath = DrawtextFilterBuilder.EscapeFilterPath(textFilePathForSlot(MainTextSlot(cue.CueIndex)));
            string mainLabel = isLast && !hasHighlight ? finalLabel : hasHighlight ? $"[cap{i}t]" : $"[cap{i}]";

            string x = style switch
            {
                // Karaoke: LEFT-anchored, so the highlight prefix drawn at the same x in the same font
                // lands exactly over the words it repeats — that overlap is the whole mechanism.
                CaptionStyle.Karaoke => $"{boxX + textPadX}",
                _ => "(w-text_w)/2"
            };
            string y = style switch
            {
                CaptionStyle.Punch => "(h-text_h)*0.62",
                _ => $"{boxY}+({boxH}-text_h)/2"
            };

            segments.Add(
                $"{label}drawtext={font}textfile='{mainPath}':expansion=none:fontsize={fontSize}:" +
                $"fontcolor={fontColor}:x={x}:y={y}:enable='{enableExpr}'{mainLabel}");
            currentLabel = mainLabel;

            if (hasHighlight)
            {
                string highlightPath = DrawtextFilterBuilder.EscapeFilterPath(textFilePathForSlot(HighlightSlot(cue.CueIndex)));
                string highlightLabel = isLast ? finalLabel : $"[cap{i}]";
                segments.Add(
                    $"{currentLabel}drawtext={font}textfile='{highlightPath}':expansion=none:fontsize={fontSize}:" +
                    $"fontcolor={highlightColor}:x={x}:y={y}:enable='{enableExpr}'{highlightLabel}");
                currentLabel = highlightLabel;
            }
        }

        return string.Join(";", segments);
    }

    /// <summary>
    /// The libass caption stage: one <c>ass</c> filter burning the script
    /// <see cref="AssSubtitleBuilder"/> wrote to <paramref name="assFilePath"/>, with
    /// <c>fontsdir=</c> pointing at the bundled caption fonts when that directory exists. Exactly
    /// like the drawtext path, no caption text appears here — only two paths, each escaped with
    /// <see cref="DrawtextFilterBuilder.EscapeFilterPath"/> inside single quotes.
    /// </summary>
    public static string BuildAssFilter(string baseLabel, string assFilePath, string? fontsDir, string finalLabel = "[vout]")
    {
        string fonts = string.IsNullOrWhiteSpace(fontsDir)
            ? string.Empty
            : $":fontsdir='{DrawtextFilterBuilder.EscapeFilterPath(fontsDir)}'";
        return $"{baseLabel}ass=filename='{DrawtextFilterBuilder.EscapeFilterPath(assFilePath)}'{fonts}{finalLabel}";
    }

    /// <summary><c>max(12, round(h * clamp(pct, 2, 12) / 100))</c>; Punch doubles the percentage before the same clamp.</summary>
    internal static int ComputeFontSize(int probedHeight, int fontSizePct, CaptionStyle style)
    {
        double pct = style == CaptionStyle.Punch ? fontSizePct * 2.0 : style == CaptionStyle.Bold ? fontSizePct * 2.3 : fontSizePct;
        pct = Math.Clamp(pct, 2, 12);
        return Math.Max(12, (int)Math.Round(probedHeight * pct / 100.0, MidpointRounding.AwayFromZero));
    }

    internal static string Invariant(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
