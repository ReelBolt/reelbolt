using System.Text.Json.Nodes;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>One speech token (a transcribed word) on a source clip's own clock, in seconds.</summary>
public readonly record struct SpeechToken(double Start, double End, int SourceIndex);

/// <summary>A silence on a source clip's own clock, in seconds.</summary>
public readonly record struct SilenceInterval(double Start, double End, int SourceIndex);

/// <summary>What <see cref="SpeechSafeCuts.Apply"/> did, for the step output and its warning.</summary>
/// <param name="AdjustedBoundaries">Span starts/ends that moved.</param>
/// <param name="CutsInsideWordsBefore">Boundaries that fell inside a spoken word before the pass.</param>
/// <param name="CutsInsideWords">Boundaries still inside a spoken word after the pass (0 unless a boundary could not be made safe).</param>
/// <param name="MaxShiftMs">The largest boundary move, in milliseconds.</param>
/// <param name="PausesChosen">Boundaries that ended inside a pause.</param>
/// <param name="SpansMerged">Spans folded into a neighbour because their cuts now touched.</param>
public sealed record SpeechSafeCutsReport(
    int AdjustedBoundaries,
    int CutsInsideWordsBefore,
    int CutsInsideWords,
    int MaxShiftMs,
    int PausesChosen,
    int SpansMerged)
{
    public static readonly SpeechSafeCutsReport None = new(0, 0, 0, 0, 0, 0);

    public JsonObject ToNode() => new()
    {
        ["adjustedBoundaries"] = AdjustedBoundaries,
        ["cutsInsideWordsBefore"] = CutsInsideWordsBefore,
        ["cutsInsideWords"] = CutsInsideWords,
        ["maxShiftMs"] = MaxShiftMs,
        ["pausesChosen"] = PausesChosen,
        ["spansMerged"] = SpansMerged
    };
}

/// <summary>
/// Moves the final cut list's boundaries off spoken words and, where it can, onto pauses
/// (docs/video-editing.md "Speech-safe cuts"). Pure and deterministic: no I/O, no clock.
/// <list type="number">
/// <item>A start inside a word moves to that word's start minus a short lead (never into the previous word), or, when it is
/// much closer to the word's end, to the gap after the word.</item>
/// <item>An end inside a word moves to that word's end plus a short tail (never into the next word).</item>
/// <item>Within <see cref="MaxPauseShiftSec"/> a boundary prefers the nearest pause (a gap of at least
/// <see cref="PauseSec"/> between words, or a silence) over a mere word boundary. Word-safe is mandatory, pause-safe best effort.</item>
/// <item>Spans of one source that now touch are merged; a span never leaves its source or drops below the minimum length.</item>
/// <item>Sources without tokens, and sources listed in <c>skipSources</c> (compositions, generated clips), are untouched.</item>
/// </list>
/// </summary>
public static class SpeechSafeCuts
{
    public const double LeadSec = 0.12;
    public const double TailSec = 0.15;
    public const double MaxPauseShiftSec = 0.6;
    public const double PauseSec = 0.3;
    /// <summary>A token longer than this is a phrase, not a word: it is not treated as speech to protect.</summary>
    public const double MaxWordSec = 1.5;
    private const double Eps = 0.001;
    private const double LaterWordFraction = 0.25;
    private const double LaterMarginSec = 0.05;

    /// <summary>
    /// <paramref name="targetSec"/> (optional): when the pass would lengthen the cut list past the target by more than
    /// <see cref="TargetSlackSec"/>, it is run again preferring the shortening word edge (a start moves to the word's end,
    /// an end to the word's start) and the shorter result is kept, so a length fit that already ran stays valid.
    /// </summary>
    public static (List<(double Start, double End, int SourceIndex, IReadOnlyList<string> OriginIds)> Spans, SpeechSafeCutsReport Report) Apply(
        IReadOnlyList<(double Start, double End, int SourceIndex, IReadOnlyList<string> OriginIds)> spans,
        IReadOnlyList<SpeechToken> tokens,
        IReadOnlyList<SilenceInterval>? silences,
        IReadOnlyDictionary<int, double> sourceDurations,
        ISet<int>? skipSources,
        double minSegmentSec,
        double? targetSec = null)
    {
        var layouts = new Dictionary<int, SourceSpeech>();
        foreach (IGrouping<int, SpeechToken> g in tokens.GroupBy(t => t.SourceIndex))
        {
            if (skipSources?.Contains(g.Key) == true) continue;
            SourceSpeech? layout = SourceSpeech.Build(
                g.ToList(), silences?.Where(s => s.SourceIndex == g.Key).ToList() ?? [],
                sourceDurations.TryGetValue(g.Key, out double d) ? d : double.PositiveInfinity);
            if (layout is not null) layouts[g.Key] = layout;
        }

        var first = Run(spans, layouts, minSegmentSec, shortenFirst: false);
        if (targetSec is > 0)
        {
            double before = spans.Sum(x => x.End - x.Start);
            double after = first.Spans.Sum(x => x.End - x.Start);
            if (after > targetSec.Value + TargetSlackSec && after > before + 1e-6)
            {
                var second = Run(spans, layouts, minSegmentSec, shortenFirst: true);
                if (second.Spans.Sum(x => x.End - x.Start) < after - 1e-6)
                    return second;
            }
        }

        return first;
    }

    /// <summary>How far past the target the total may grow before the shortening edges are preferred.</summary>
    public const double TargetSlackSec = 0.35;

    private static (List<(double Start, double End, int SourceIndex, IReadOnlyList<string> OriginIds)> Spans, SpeechSafeCutsReport Report) Run(
        IReadOnlyList<(double Start, double End, int SourceIndex, IReadOnlyList<string> OriginIds)> spans,
        Dictionary<int, SourceSpeech> layouts, double minSegmentSec, bool shortenFirst)
    {
        int adjusted = 0, insideBefore = 0, insideAfter = 0, pauses = 0, merged = 0;
        double maxShift = 0;
        var result = new List<(double Start, double End, int SourceIndex, IReadOnlyList<string> OriginIds)>(spans.Count);

        foreach (var span in spans)
        {
            if (!layouts.TryGetValue(span.SourceIndex, out SourceSpeech? sp))
            {
                result.Add(span);
                continue;
            }

            bool startInside = sp.TokenContaining(span.Start) is not null;
            bool endInside = sp.TokenContaining(span.End) is not null;
            if (startInside) insideBefore++;
            if (endInside) insideBefore++;

            // Everything of this source the list has already played: a move may never make this span play it again.
            List<(double Start, double End)> covered = result.Where(r => r.SourceIndex == span.SourceIndex)
                .Select(r => (r.Start, r.End)).ToList();
            double allowed = Overlap(span.Start, span.End, covered) + 1e-9;

            List<double> startOptions = AvoidCovered(sp.StartOptions(span.Start, shortenFirst), covered, isStart: true, sp);
            List<double> endOptions = AvoidCovered(sp.EndOptions(span.End, shortenFirst), covered, isStart: false, sp);
            double newStart = span.Start, newEnd = span.End;
            bool found = false;
            foreach (double s in startOptions)
            {
                foreach (double e in endOptions)
                {
                    if (e - s + 1e-9 < minSegmentSec || e <= s) continue;
                    if (Overlap(s, e, covered) > allowed) continue;
                    newStart = s;
                    newEnd = e;
                    found = true;
                    break;
                }
                if (found) break;
            }

            if (!found)
            {
                // No combination keeps the span long enough: leave it as the editor made it.
                newStart = span.Start;
                newEnd = span.End;
            }

            if (Math.Abs(newStart - span.Start) > 1e-6) { adjusted++; maxShift = Math.Max(maxShift, Math.Abs(newStart - span.Start)); }
            if (Math.Abs(newEnd - span.End) > 1e-6) { adjusted++; maxShift = Math.Max(maxShift, Math.Abs(newEnd - span.End)); }
            if (sp.TokenContaining(newStart) is not null) insideAfter++;
            if (sp.TokenContaining(newEnd) is not null) insideAfter++;
            if (sp.InPause(newStart)) pauses++;
            if (sp.InPause(newEnd)) pauses++;

            var moved = (newStart, newEnd, span.SourceIndex, span.OriginIds);
            if (result.Count > 0)
            {
                var prev = result[^1];
                // Only a span that continues FORWARDS from the one before it folds into it (list order is never changed).
                if (prev.SourceIndex == moved.SourceIndex
                    && Math.Abs(moved.newStart - prev.End) <= 1e-6 && moved.newEnd >= prev.End)
                {
                    result[^1] = (prev.Start, moved.newEnd, prev.SourceIndex, prev.OriginIds.Union(moved.OriginIds).ToList());
                    merged++;
                    continue;
                }
            }

            result.Add(moved);
        }

        return (result, new SpeechSafeCutsReport(adjusted, insideBefore, insideAfter, (int)Math.Round(maxShift * 1000), pauses, merged));
    }

    private static double Overlap(double start, double end, List<(double Start, double End)> covered) =>
        covered.Sum(c => Math.Max(0, Math.Min(end, c.End) - Math.Max(start, c.Start)));

    /// <summary>
    /// A boundary candidate that falls inside something this source already played is first replaced by that range's far edge
    /// (a start moves to its end, an end to its start) when that edge is clear of every word; the original candidate stays
    /// as a later option.
    /// </summary>
    private static List<double> AvoidCovered(List<double> options, List<(double Start, double End)> covered, bool isStart, SourceSpeech sp)
    {
        var result = new List<double>();
        foreach (double o in options)
        {
            foreach ((double a, double b) in covered)
            {
                if (o > a + 1e-6 && o < b - 1e-6)
                {
                    double edge = isStart ? b : a;
                    if (sp.TokenContaining(edge) is null && !result.Contains(edge)) result.Add(edge);
                }
            }

            if (!result.Contains(o)) result.Add(o);
        }

        return result;
    }

    /// <summary>How many span boundaries fall strictly inside a spoken word.</summary>
    public static int CountInsideWords(
        IEnumerable<(double Start, double End, int SourceIndex)> spans, IReadOnlyList<SpeechToken> tokens)
    {
        var bySource = tokens.Where(t => t.End - t.Start <= MaxWordSec).GroupBy(t => t.SourceIndex)
            .ToDictionary(g => g.Key, g => g.ToList());
        int count = 0;
        foreach (var s in spans)
        {
            if (!bySource.TryGetValue(s.SourceIndex, out List<SpeechToken>? list)) continue;
            if (list.Any(t => t.Start + Eps < s.Start && s.Start < t.End - Eps)) count++;
            if (list.Any(t => t.Start + Eps < s.End && s.End < t.End - Eps)) count++;
        }

        return count;
    }

    private sealed class SourceSpeech
    {
        private readonly List<(double Start, double End)> _words;
        private readonly List<(double Start, double End)> _pauses;
        private readonly double _duration;

        private SourceSpeech(List<(double, double)> words, List<(double, double)> pauses, double duration)
        {
            _words = words;
            _pauses = pauses;
            _duration = duration;
        }

        public static SourceSpeech? Build(List<SpeechToken> tokens, List<SilenceInterval> silences, double duration)
        {
            var ordered = tokens
                .Where(t => double.IsFinite(t.Start) && double.IsFinite(t.End) && t.End > t.Start && t.End - t.Start <= MaxWordSec)
                .OrderBy(t => t.Start).ThenBy(t => t.End)
                .ToList();
            if (ordered.Count == 0) return null;

            var words = new List<(double Start, double End)>();
            foreach (SpeechToken t in ordered)
            {
                if (words.Count > 0 && t.Start < words[^1].End)
                    words[^1] = (words[^1].Start, Math.Max(words[^1].End, t.End));
                else
                    words.Add((t.Start, t.End));
            }

            var pauses = new List<(double Start, double End)>();
            double cursor = 0;
            foreach ((double s, double e) in words)
            {
                if (s - cursor >= PauseSec) pauses.Add((cursor, s));
                cursor = e;
            }

            if (double.IsFinite(duration) && duration - cursor >= PauseSec) pauses.Add((cursor, duration));

            // A silence is a pause too, once the words are taken out of it.
            foreach (SilenceInterval sil in silences.OrderBy(s => s.Start))
            {
                double a = sil.Start;
                foreach ((double ws, double we) in words)
                {
                    if (we <= a || ws >= sil.End) continue;
                    if (ws - a >= PauseSec) pauses.Add((a, ws));
                    a = Math.Max(a, we);
                }

                if (sil.End - a >= PauseSec) pauses.Add((a, sil.End));
            }

            pauses = pauses.Where(p => p.End > p.Start).OrderBy(p => p.Start).ThenBy(p => p.End).Distinct().ToList();
            return new SourceSpeech(words, pauses, duration);
        }

        public (double Start, double End)? TokenContaining(double t)
        {
            foreach (var w in _words)
            {
                if (w.Start + Eps < t && t < w.End - Eps) return w;
                if (w.Start > t) break;
            }

            return null;
        }

        public bool InPause(double t) => _pauses.Any(p => t >= p.Start - Eps && t <= p.End + Eps);

        private double PrevWordEnd(double wordStart)
        {
            double prev = 0;
            foreach (var w in _words)
            {
                if (w.End <= wordStart + Eps) prev = w.End; else break;
            }

            return prev;
        }

        private double NextWordStart(double wordEnd)
        {
            foreach (var w in _words)
                if (w.Start >= wordEnd - Eps) return w.Start;
            return _duration;
        }

        /// <summary>Candidate start times, most wanted first; every one is outside every word.</summary>
        public List<double> StartOptions(double start, bool shortenFirst = false)
        {
            var options = new List<double>();
            var word = TokenContaining(start);
            // The pause preference applies only to a cut that was inside a word: a cut already clear of speech stays put.
            double? pause = word is null ? null : PausePoint(start, isStart: true);
            if (pause is { } p) options.Add(p);

            if (word is { } w)
            {
                double earlier = w.Start - Math.Min(LeadSec, w.Start - PrevWordEnd(w.Start));
                double later = w.End + Math.Min(LaterMarginSec, Math.Max(0, NextWordStart(w.End) - w.End));
                bool laterFirst = shortenFirst || w.End - start <= LaterWordFraction * (w.End - w.Start);
                if (laterFirst) { options.Add(later); options.Add(earlier); }
                else { options.Add(earlier); options.Add(later); }
            }
            else
            {
                options.Add(start);
            }

            return Clean(options);
        }

        /// <summary>Candidate end times, most wanted first; every one is outside every word.</summary>
        public List<double> EndOptions(double end, bool shortenFirst = false)
        {
            var options = new List<double>();
            var word = TokenContaining(end);
            double? pause = word is null ? null : PausePoint(end, isStart: false);
            if (pause is { } p) options.Add(p);

            if (word is { } w)
            {
                double later = w.End + Math.Min(TailSec, Math.Max(0, NextWordStart(w.End) - w.End));
                // The shortening edge: stop before the word starts, never inside the word before it.
                double earlier = w.Start - Math.Min(LeadSec, w.Start - PrevWordEnd(w.Start));
                if (shortenFirst) { options.Add(earlier); options.Add(later); }
                else { options.Add(later); options.Add(earlier); }
            }
            else
                options.Add(end);

            return Clean(options);
        }

        private List<double> Clean(List<double> options)
        {
            var clean = new List<double>();
            foreach (double o in options)
            {
                double v = Math.Clamp(o, 0, double.IsFinite(_duration) ? _duration : o);
                if (TokenContaining(v) is not null) continue;
                if (!clean.Any(c => Math.Abs(c - v) < 1e-9)) clean.Add(v);
            }

            return clean;
        }

        /// <summary>The boundary inside the nearest pause within <see cref="MaxPauseShiftSec"/>, or null when there is none or it already sits in one.</summary>
        private double? PausePoint(double t, bool isStart)
        {
            if (InPause(t)) return t;

            double best = double.NaN;
            double bestDist = double.MaxValue;
            foreach ((double a, double b) in _pauses)
            {
                double point = isStart
                    ? b - Math.Min(LeadSec, b - a)
                    : a + Math.Min(TailSec, b - a);
                double dist = Math.Abs(point - t);
                if (dist > MaxPauseShiftSec || dist >= bestDist - 1e-9) continue;
                best = point;
                bestDist = dist;
            }

            return double.IsNaN(best) ? null : best;
        }
    }
}
