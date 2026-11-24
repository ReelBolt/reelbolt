using System.Text;
using ReelBolt.Shared.Inference;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Discards transcript segments that were recognised from audio carrying no speech (see
/// docs/video-editing.md "Transcripts from clips with no speech"). Whisper-family recognisers
/// fed music, room tone or silence routinely "hear" stock phrases — "Thank you for watching.",
/// "you", a subtitle credit — and before this gate the story editor treated such a line as real
/// dialogue (it chose a closing shot because of one). Pure — no I/O. A segment is dropped when
/// ANY of these holds:
/// <list type="bullet">
/// <item><c>no_speech</c>: the recogniser itself says so — Whisper's own rule,
/// <c>no_speech_prob ≥ 0.6</c> with <c>avg_logprob &lt; -1</c> (or no log-prob reported), or
/// <c>no_speech_prob ≥ 0.9</c> outright.</item>
/// <item><c>repetition</c>: <c>compression_ratio &gt; 2.4</c>, Whisper's looping-output signature.</item>
/// <item><c>no_speech_energy</c>: the measured audio never rises above the step's silence
/// threshold anywhere in the segment's window, or silence detection covers ≥ 80% of it.</item>
/// <item><c>stock_phrase</c>: the whole segment is one of the phrases recognisers invent on
/// non-speech audio, and nothing vouches for it — the recogniser reported
/// <c>no_speech_prob ≥ 0.2</c>, or reported no confidence at all and it is the clip's only
/// segment. A confidently recognised "thank you" in real dialogue is kept.</item>
/// </list>
/// A clip with no audio stream never reaches this gate: it is not transcribed at all.
/// </summary>
public static class TranscriptSpeechGate
{
    public const string NoSpeechReason = "no_speech";
    public const string RepetitionReason = "repetition";
    public const string NoSpeechEnergyReason = "no_speech_energy";
    public const string StockPhraseReason = "stock_phrase";

    internal const double NoSpeechProbThreshold = 0.6;
    internal const double NoSpeechProbAlone = 0.9;
    internal const double LogprobThreshold = -1.0;
    internal const double CompressionRatioThreshold = 2.4;
    internal const double SilenceCoverageThreshold = 0.8;
    internal const double StockPhraseNoSpeechProb = 0.2;

    /// <summary>Normalised (lower-case, letters/digits/spaces only) phrases recognisers produce from non-speech audio.</summary>
    private static readonly HashSet<string> StockPhrases = new(StringComparer.Ordinal)
    {
        "you",
        "thank you",
        "thank you very much",
        "thanks for watching",
        "thank you for watching",
        "thank you so much for watching",
        "thank you for watching and see you next time",
        "please subscribe",
        "like and subscribe",
        "please like and subscribe",
        "subscribe to my channel",
        "dont forget to subscribe",
        "see you next time",
        "bye",
        "subtitles by the amaraorg community",
        "subtitles by the amara org community"
    };

    public sealed record Result(
        IReadOnlyList<TranscriptSegment> Kept,
        IReadOnlyList<TranscriptWord> KeptWords,
        IReadOnlyList<(TranscriptSegment Segment, string Reason)> Dropped);

    /// <param name="segments">The recogniser's segments, absolute source seconds.</param>
    /// <param name="words">The recogniser's words; those inside a dropped segment (and no kept one) go with it.</param>
    /// <param name="silenceSpans">Detected silence, or empty when silence detection was off.</param>
    /// <param name="levels">Measured per-window audio levels, or null when they were not sampled.</param>
    /// <param name="silenceThresholdDb">The step's silence threshold — "speech energy" means rising above it.</param>
    public static Result Apply(
        IReadOnlyList<TranscriptSegment> segments,
        IReadOnlyList<TranscriptWord> words,
        IReadOnlyList<(double StartSec, double EndSec)> silenceSpans,
        IReadOnlyList<WavRmsSampler.RmsWindow>? levels,
        double silenceThresholdDb)
    {
        List<TranscriptSegment> kept = [];
        List<(TranscriptSegment, string)> dropped = [];
        foreach (TranscriptSegment segment in segments)
        {
            string? reason = Judge(segment, segments.Count, silenceSpans, levels, silenceThresholdDb);
            if (reason is null)
                kept.Add(segment);
            else
                dropped.Add((segment, reason));
        }

        if (dropped.Count == 0)
            return new Result(segments, words, []);

        List<TranscriptWord> keptWords = words
            .Where(w =>
            {
                double mid = (w.StartSec + w.EndSec) / 2;
                bool inDropped = dropped.Any(d => mid >= d.Item1.StartSec && mid <= d.Item1.EndSec);
                bool inKept = kept.Any(k => mid >= k.StartSec && mid <= k.EndSec);
                return !inDropped || inKept;
            })
            .ToList();

        return new Result(kept, keptWords, dropped);
    }

    internal static string? Judge(
        TranscriptSegment segment,
        int segmentCountInSource,
        IReadOnlyList<(double StartSec, double EndSec)> silenceSpans,
        IReadOnlyList<WavRmsSampler.RmsWindow>? levels,
        double silenceThresholdDb)
    {
        if (segment.NoSpeechProb is double noSpeech &&
            (noSpeech >= NoSpeechProbAlone ||
             (noSpeech >= NoSpeechProbThreshold && (segment.AvgLogprob is not double lp || lp < LogprobThreshold))))
        {
            return NoSpeechReason;
        }

        if (segment.CompressionRatio is double ratio && ratio > CompressionRatioThreshold)
            return RepetitionReason;

        double start = segment.StartSec;
        double end = Math.Max(segment.EndSec, start + 0.01);

        if (silenceSpans.Count > 0)
        {
            double covered = silenceSpans.Sum(s => Math.Max(0, Math.Min(end, s.EndSec) - Math.Max(start, s.StartSec)));
            if (covered / (end - start) >= SilenceCoverageThreshold)
                return NoSpeechEnergyReason;
        }

        if (levels is { Count: > 0 })
        {
            List<WavRmsSampler.RmsWindow> under = levels.Where(w => w.EndSec > start && w.StartSec < end).ToList();
            if (under.Count > 0 && under.All(w => !double.IsFinite(w.RmsDbfs) || w.RmsDbfs < silenceThresholdDb))
                return NoSpeechEnergyReason;
        }

        if (StockPhrases.Contains(Normalize(segment.Text)))
        {
            bool vouchedFor = segment.NoSpeechProb is double p && p < StockPhraseNoSpeechProb;
            bool suspicious = segment.NoSpeechProb is double q
                ? q >= StockPhraseNoSpeechProb
                : segmentCountInSource == 1;
            if (!vouchedFor && suspicious)
                return StockPhraseReason;
        }

        return null;
    }

    internal static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        StringBuilder sb = new(text.Length);
        bool space = false;
        foreach (char c in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                if (space && sb.Length > 0)
                    sb.Append(' ');
                sb.Append(c);
                space = false;
            }
            else if (char.IsWhiteSpace(c))
            {
                space = true;
            }
        }

        return sb.ToString();
    }
}
