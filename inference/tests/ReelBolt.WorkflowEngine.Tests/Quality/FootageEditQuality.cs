using System;
using System.Collections.Generic;
using System.Linq;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Tests.Quality;

/// <summary>Scorers for how well an edit of real footage turned out; each is a pure function over plain values.</summary>
public static partial class FootageEditQuality
{
    private const double Eps = 0.001;

    /// <summary>How many span boundaries (starts and ends) fall strictly inside a speech token of the same source.</summary>
    public static int CutsInsideWords(
        IEnumerable<(double Start, double End, int SourceIndex)> spans,
        IEnumerable<(double Start, double End, int SourceIndex)> tokens)
    {
        var bySource = tokens.GroupBy(t => t.SourceIndex).ToDictionary(g => g.Key, g => g.ToList());
        int count = 0;
        foreach ((double start, double end, int source) in spans)
        {
            if (!bySource.TryGetValue(source, out var list)) continue;
            if (list.Any(t => t.Start + Eps < start && start < t.End - Eps)) count++;
            if (list.Any(t => t.Start + Eps < end && end < t.End - Eps)) count++;
        }

        return count;
    }

    /// <summary>Single-source form: every span and token is on source 0.</summary>
    public static int CutsInsideWords(IEnumerable<(double Start, double End)> spans, IEnumerable<(double Start, double End)> tokens) =>
        CutsInsideWords(spans.Select(s => (s.Start, s.End, 0)), tokens.Select(t => (t.Start, t.End, 0)));

    /// <summary>The share of sound-effect cues (output seconds) that sit within <paramref name="toleranceMs"/> of an event (output seconds). 1 when there are no cues.</summary>
    public static double SfxOnEventPrecision(IReadOnlyList<double> cues, IReadOnlyList<double> events, int toleranceMs = 120)
    {
        if (cues.Count == 0)
            return 1.0;
        double tolerance = toleranceMs / 1000.0;
        return cues.Count(c => events.Any(e => Math.Abs(e - c) <= tolerance)) / (double)cues.Count;
    }

    /// <summary>How many cues start inside a spoken word (source or output seconds, same clock for both).</summary>
    public static int SfxOverSpeechCount(IReadOnlyList<double> cues, IReadOnlyList<VideoAnalysisSegment> speech) =>
        cues.Count(c => speech.Any(s => c >= s.StartSec && c < s.EndSec));

    /// <summary>The highest absolute sample value in dB relative to full scale (samples in -1..1); negative infinity for silence or no samples.</summary>
    public static double PeakDb(IEnumerable<float> samples)
    {
        double peak = 0;
        foreach (float sample in samples)
            peak = Math.Max(peak, Math.Abs((double)sample));
        return peak <= 0 ? double.NegativeInfinity : 20 * Math.Log10(peak);
    }

    /// <summary>The share (0 to 100) of speech seconds of the source that no kept span covers.</summary>
    public static double DroppedSpeechPct(IEnumerable<(double Start, double End)> spans, IEnumerable<(double Start, double End)> tokens)
    {
        var kept = spans.ToList();
        double total = 0, covered = 0;
        foreach ((double start, double end) in tokens)
        {
            total += end - start;
            covered += kept.Sum(s => Math.Max(0, Math.Min(end, s.End) - Math.Max(start, s.Start)));
        }

        return total <= 0 ? 0 : 100 * Math.Max(0, 1 - covered / total);
    }
}

public static partial class FootageEditQuality
{
    /// <summary>The share of detected sound-worthy moments (seconds) that sit within <paramref name="toleranceMs"/> of a hand-labelled visual moment. 1 when nothing was detected.</summary>
    public static double SfxOnVisualMomentPrecision(IReadOnlyList<double> detected, IReadOnlyList<double> labelled, int toleranceMs = 120)
    {
        if (detected.Count == 0)
            return 1.0;
        double tolerance = toleranceMs / 1000.0;
        return detected.Count(d => labelled.Any(l => Math.Abs(l - d) <= tolerance)) / (double)detected.Count;
    }

    /// <summary>The share of hand-labelled visual moments (seconds) that have a detected sound-worthy moment within <paramref name="toleranceMs"/>. 1 when nothing is labelled.</summary>
    public static double SfxOnVisualMomentRecall(IReadOnlyList<double> detected, IReadOnlyList<double> labelled, int toleranceMs = 120)
    {
        if (labelled.Count == 0)
            return 1.0;
        double tolerance = toleranceMs / 1000.0;
        return labelled.Count(l => detected.Any(d => Math.Abs(l - d) <= tolerance)) / (double)labelled.Count;
    }
}
