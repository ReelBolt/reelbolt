using System.Globalization;
using System.Text;
using System.Text.Json;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>What <see cref="TranscriptCaptionLines.Build"/> cut from a transcript: the caption lines, and what it could not use.</summary>
/// <param name="Lines">One caption line per usable transcript segment, in source order; <c>LineId</c> is the segment id.</param>
/// <param name="SegmentCount">Segments the analysis produced.</param>
/// <param name="CutAwayCount">Segments the edit cut away entirely (no caption: their words are not in the output).</param>
/// <param name="EmptyCount">Segments with no words once trimmed.</param>
/// <param name="TranslatedCount">Lines whose text came from the translation.</param>
/// <param name="UntranslatedCount">Lines kept in the original language because the translation did not cover them (only counted when a translation was supplied).</param>
public sealed record TranscriptCaptionLinesResult(
    IReadOnlyList<CaptionSourceLine> Lines,
    int SegmentCount,
    int CutAwayCount,
    int EmptyCount,
    int TranslatedCount,
    int UntranslatedCount);

/// <summary>
/// Cuts caption lines from a <c>VideoAnalyze</c> transcript (<see cref="CaptionSource.Transcript"/>; see
/// docs/voiceover.md "Subtitles from the transcript"). Pure: the segment's id, its words' timing and its place on the
/// OUTPUT timeline are all computed here from the analysis artifact and the cut, never from a model. A translation
/// replaces a line's text but never its anchor or its timing.
/// </summary>
public static class TranscriptCaptionLines
{
    /// <summary>The longest a single caption line's text is, before the cue builder splits it into cues.</summary>
    public const int MaxLineChars = 400;

    /// <summary>One kept stretch of a segment: the source seconds it covers and where they land on the output timeline.</summary>
    public readonly record struct Portion(double SrcStart, double SrcEnd, double OutStart, double OutEnd);

    public static TranscriptCaptionLinesResult Build(
        IReadOnlyList<VideoAnalysisSegment> segments,
        IReadOnlyList<VideoAnalysisWord> words,
        Func<VideoAnalysisSegment, IReadOnlyList<Portion>> portionsOf,
        IReadOnlyDictionary<string, string>? translations,
        Func<string, string> sanitize)
    {
        List<CaptionSourceLine> lines = [];
        int cutAway = 0, empty = 0, translated = 0, untranslated = 0;

        foreach (VideoAnalysisSegment segment in segments.OrderBy(s => s.SourceIndex).ThenBy(s => s.StartSec))
        {
            IReadOnlyList<Portion> portions = portionsOf(segment);
            if (portions.Count == 0)
            {
                cutAway++;
                continue;
            }

            bool hasTranslation = translations is not null && translations.TryGetValue(segment.Id, out string? t) && !string.IsNullOrWhiteSpace(t);
            string fullText = sanitize((hasTranslation ? translations![segment.Id] : segment.Text) ?? string.Empty);
            if (fullText.Length == 0 && hasTranslation)
            {
                // A translation that sanitizes to nothing falls back to the original words rather than a hole.
                hasTranslation = false;
                fullText = sanitize(segment.Text ?? string.Empty);
            }

            if (fullText.Length == 0)
            {
                empty++;
                continue;
            }

            if (translations is not null)
            {
                if (hasTranslation) translated++;
                else untranslated++;
            }

            double segDuration = Math.Max(1e-6, segment.EndSec - segment.StartSec);
            string[] allTokens = CaptionCueBuilder.Tokenize(fullText);
            List<VideoAnalysisWord> segmentWords = words
                .Where(w => w.SourceIndex == segment.SourceIndex && w.StartSec >= segment.StartSec - 0.001 && w.EndSec <= segment.EndSec + 0.001)
                .ToList();

            for (int k = 0; k < portions.Count; k++)
            {
                Portion portion = portions[k];
                double duration = Math.Max(0, portion.OutEnd - portion.OutStart);
                bool whole = portions.Count == 1 && Math.Abs((portion.SrcEnd - portion.SrcStart) - segDuration) <= 0.05;

                string text;
                IReadOnlyList<CaptionSourceWord> lineWords;
                if (whole)
                {
                    text = fullText;
                    lineWords = hasTranslation || segmentWords.Count == 0
                        ? SpreadWords(text, duration)
                        : segmentWords.Select(w => new CaptionSourceWord(w.Text, w.StartSec - segment.StartSec, w.EndSec - segment.StartSec)).ToList();
                }
                else
                {
                    // A cut passes through the segment: caption only what falls in this kept piece.
                    List<VideoAnalysisWord> kept = hasTranslation
                        ? []
                        : segmentWords.Where(w => (w.StartSec + w.EndSec) / 2 >= portion.SrcStart && (w.StartSec + w.EndSec) / 2 < portion.SrcEnd).ToList();
                    if (kept.Count > 0)
                    {
                        text = sanitize(string.Join(' ', kept.Select(w => w.Text)));
                        lineWords = kept.Select(w => new CaptionSourceWord(w.Text, Math.Max(0, w.StartSec - portion.SrcStart), Math.Max(0, w.EndSec - portion.SrcStart))).ToList();
                    }
                    else
                    {
                        // No recognised words (or a translation): share the tokens out by the piece's share of the segment.
                        int from = (int)Math.Round((portion.SrcStart - segment.StartSec) / segDuration * allTokens.Length, MidpointRounding.AwayFromZero);
                        int to = (int)Math.Round((portion.SrcEnd - segment.StartSec) / segDuration * allTokens.Length, MidpointRounding.AwayFromZero);
                        from = Math.Clamp(from, 0, allTokens.Length);
                        to = Math.Clamp(to, from, allTokens.Length);
                        text = string.Join(' ', allTokens[from..to]);
                        lineWords = SpreadWords(text, duration);
                    }
                }

                if (text.Length == 0)
                    continue;

                lines.Add(new CaptionSourceLine(k == 0 ? segment.Id : $"{segment.Id}~{k + 1}", text, portion.OutStart, duration, lineWords));
            }
        }

        return new TranscriptCaptionLinesResult(lines, segments.Count, cutAway, empty, translated, untranslated);
    }

    /// <summary>Word timings spread over the line by character weight: what a translated line (no recogniser words) uses so every caption style can time it.</summary>
    internal static List<CaptionSourceWord> SpreadWords(string text, double duration)
    {
        string[] tokens = CaptionCueBuilder.Tokenize(text);
        double total = tokens.Sum(t => t.Length + 1.0);
        List<CaptionSourceWord> spread = [];
        double cursor = 0;
        foreach (string token in tokens)
        {
            double slice = total <= 0 ? 0 : duration * (token.Length + 1.0) / total;
            spread.Add(new CaptionSourceWord(token, cursor, cursor + slice));
            cursor += slice;
        }

        return spread;
    }
}

/// <summary>
/// Reads a translation step's output (a <c>NarrationPlanOutput</c> whose lines are keyed by transcript segment id) into
/// an id-to-text map. Anything but a line naming a segment id with text is ignored: the translation can only ever
/// replace words, never add a line or move one.
/// </summary>
public static class CaptionTranslations
{
    public static (IReadOnlyDictionary<string, string>? Map, string? Error) Parse(string? output, IReadOnlySet<string> knownSegmentIds)
    {
        if (string.IsNullOrWhiteSpace(output))
            return (null, "translation_output_empty");

        NarrationPlanOutput? plan;
        try
        {
            plan = JsonSerializer.Deserialize<NarrationPlanOutput>(
                Execution.StepExecutors.VoiceoverStepExecutor.UpstreamJson(output), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return (null, "translation_output_invalid");
        }

        if (plan?.Lines is null)
            return (null, "translation_output_invalid");

        Dictionary<string, string> map = new(StringComparer.Ordinal);
        foreach (NarrationLine? line in plan.Lines)
        {
            if (line is null || string.IsNullOrWhiteSpace(line.Text) || !knownSegmentIds.Contains(line.AnchorId))
                continue;
            map.TryAdd(line.AnchorId, line.Text);
        }

        return (map, null);
    }
}

/// <summary>
/// First-party reading-comfort rules for subtitle cues, applied to the cues the caption builder cut (so the burned
/// captions and the SRT/VTT files obey the same ones). A cue never grows past <see cref="MaxCueSec"/>, never overlaps
/// the next one, and is held on screen long enough to read at <see cref="MaxReadingCharsPerSec"/> when the gap
/// after it allows.
/// </summary>
public static class SubtitleCueFit
{
    /// <summary>Characters per line before the cue builder splits (the broadcast convention for one line).</summary>
    public const int MaxCharsPerLine = 42;

    /// <summary>Reading speed ceiling, characters per second (adult broadcast subtitles sit at 17 to 21).</summary>
    public const double MaxReadingCharsPerSec = 20;

    /// <summary>The shortest a cue is held when there is room.</summary>
    public const double MinCueSec = 0.9;

    /// <summary>The longest a cue is held, even when the speech is slow.</summary>
    public const double MaxCueSec = 7;

    /// <summary>The blank gap kept between two cues that were extended.</summary>
    public const double GapSec = 0.04;

    public static IReadOnlyList<ResolvedCaptionCue> Apply(IReadOnlyList<ResolvedCaptionCue> cues)
    {
        List<ResolvedCaptionCue> ordered = cues.OrderBy(c => c.OutputStartSec).ThenBy(c => c.CueIndex).ToList();
        List<ResolvedCaptionCue> fitted = new(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            ResolvedCaptionCue cue = ordered[i];
            double nextStart = i + 1 < ordered.Count ? ordered[i + 1].OutputStartSec : double.PositiveInfinity;
            double end = cue.OutputEndSec;

            // Never overlap the next cue (crossfade seams can place two lines at the same moment).
            if (end > nextStart && nextStart - cue.OutputStartSec >= CaptionCueBuilder.MinCueSec)
                end = nextStart;

            double wanted = Math.Min(MaxCueSec, Math.Max(MinCueSec, cue.Text.Length / MaxReadingCharsPerSec));
            if (end - cue.OutputStartSec < wanted)
            {
                double room = double.IsPositiveInfinity(nextStart) ? cue.OutputStartSec + wanted : nextStart - GapSec;
                end = Math.Max(end, Math.Min(cue.OutputStartSec + wanted, room));
            }

            if (end - cue.OutputStartSec > MaxCueSec)
                end = cue.OutputStartSec + MaxCueSec;

            fitted.Add(cue with { OutputEndSec = end });
        }

        return fitted;
    }
}

/// <summary>
/// Writes cues as SubRip (<c>.srt</c>) and WebVTT (<c>.vtt</c>) text. Pure and deterministic: timestamps are formatted
/// from the cue seconds with invariant culture, and cue text is flattened to one line with the characters that would
/// end a cue or open a tag removed or escaped.
/// </summary>
public static class SubtitleFiles
{
    public const string SrtFileName = "subtitles.srt";
    public const string VttFileName = "subtitles.vtt";

    public static string Srt(IReadOnlyList<ResolvedCaptionCue> cues)
    {
        StringBuilder sb = new();
        int n = 0;
        foreach (ResolvedCaptionCue cue in cues)
        {
            string text = Flatten(cue.Text);
            if (text.Length == 0)
                continue;
            n++;
            sb.Append(n.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
              .Append(Stamp(cue.OutputStartSec, ',')).Append(" --> ").Append(Stamp(cue.OutputEndSec, ',')).Append("\r\n")
              .Append(text).Append("\r\n\r\n");
        }

        return sb.ToString();
    }

    public static string Vtt(IReadOnlyList<ResolvedCaptionCue> cues)
    {
        StringBuilder sb = new();
        sb.Append("WEBVTT\n\n");
        foreach (ResolvedCaptionCue cue in cues)
        {
            string text = Flatten(cue.Text);
            if (text.Length == 0)
                continue;
            sb.Append(Stamp(cue.OutputStartSec, '.')).Append(" --> ").Append(Stamp(cue.OutputEndSec, '.')).Append('\n')
              .Append(text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal))
              .Append("\n\n");
        }

        return sb.ToString();
    }

    internal static string Stamp(double seconds, char millisSeparator)
    {
        long totalMs = (long)Math.Round(Math.Max(0, double.IsFinite(seconds) ? seconds : 0) * 1000.0, MidpointRounding.AwayFromZero);
        long ms = totalMs % 1000;
        long totalSec = totalMs / 1000;
        return string.Create(CultureInfo.InvariantCulture,
            $"{totalSec / 3600:00}:{totalSec / 60 % 60:00}:{totalSec % 60:00}{millisSeparator}{ms:000}");
    }

    private static string Flatten(string text)
    {
        StringBuilder sb = new(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
                sb.Append(' ');
            space = false;
            sb.Append(c);
        }

        // The cue-timing arrow inside text would be read as a timing line by a lenient parser.
        return sb.ToString().Replace("-->", "->", StringComparison.Ordinal);
    }
}
