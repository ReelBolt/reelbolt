using System;
using System.Collections.Generic;
using System.Linq;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;

namespace ReelBolt.WorkflowEngine.Tests.Quality;

/// <summary>
/// A deterministic, synthetic single-speaker recording: about 170 s, about 600 word tokens (0 to 60 ms between
/// words inside a phrase, 300 to 900 ms between sentences), 85 shots of 2 s that ignore word boundaries, and ten
/// kept shot ranges. Generated from a fixed seed, never read from a file.
/// </summary>
public sealed class SyntheticTalkingHead
{
    public const int ShotCount = 85;
    public const double ShotSec = 2.0;

    public IReadOnlyList<(string Id, double Start, double End)> Words { get; }
    public IReadOnlyList<(string Id, double Start, double End)> Shots { get; }

    /// <summary>
    /// Detected events in time order, ids e0..: three sound-worthy ones (a Hit, a Transition, a Hit) in pauses inside the first
    /// three kept ranges, and 120 weak-to-middling Beat events spread over the whole recording (many inside words).
    /// </summary>
    public IReadOnlyList<VideoAnalysisRenderEvent> Events { get; }

    /// <summary>The ids of the three sound-worthy events.</summary>
    public IReadOnlyList<string> WorthyEventIds => Events.Where(e => e.Type != nameof(VideoEventType.Beat)).Select(e => e.Id).ToList();

    /// <summary>Ten kept ranges as (first shot, last shot) indices, in source order, with gaps between them.</summary>
    public IReadOnlyList<(int FirstShot, int LastShot)> KeptRanges { get; } =
        [(1, 3), (6, 8), (12, 14), (18, 21), (25, 27), (31, 34), (40, 42), (50, 53), (60, 62), (70, 73)];

    public double DurationSec => ShotCount * ShotSec;

    public SyntheticTalkingHead(int seed = 7)
    {
        var rng = new Lcg(seed);
        var words = new List<(string, double, double)>();
        double t = 0.6;
        int inSentence = 0;
        int sentenceLen = 9 + rng.Next(8);
        while (words.Count < 600 && t < DurationSec - 1.0)
        {
            double len = 0.12 + rng.NextDouble() * 0.14;
            words.Add(($"t{words.Count}", Math.Round(t, 3), Math.Round(t + len, 3)));
            t += len;
            inSentence++;
            if (inSentence >= sentenceLen)
            {
                t += 0.3 + rng.NextDouble() * 0.6;
                inSentence = 0;
                sentenceLen = 9 + rng.Next(8);
            }
            else
            {
                t += rng.NextDouble() * 0.06;
            }
        }

        Words = words;
        Events = BuildEvents(words);
        Shots = Enumerable.Range(0, ShotCount)
            .Select(i => ($"s{i}", i * ShotSec, (i + 1) * ShotSec))
            .ToList();
    }

    private IReadOnlyList<VideoAnalysisRenderEvent> BuildEvents(List<(string, double, double)> words)
    {
        var raw = new List<(double Sec, string Type, double Strength)>();
        string[] types = [nameof(VideoEventType.Hit), nameof(VideoEventType.Transition), nameof(VideoEventType.Hit)];
        for (int k = 0; k < 3; k++)
        {
            (double from, double to) = (KeptRanges[k].FirstShot * ShotSec, (KeptRanges[k].LastShot + 1) * ShotSec);
            double best = from + 1, width = 0;
            for (int i = 1; i < words.Count; i++)
            {
                double gap = words[i].Item2 - words[i - 1].Item3;
                double mid = words[i - 1].Item3 + gap / 2;
                if (mid > from + 1.5 && mid < to - 1.5 && gap > width)
                    (best, width) = (mid, gap);
            }

            raw.Add((Math.Round(best, 3), types[k], 0.8));
        }

        for (int i = 0; i < 120; i++)
            raw.Add((Math.Round((i + 0.5) * DurationSec / 120, 3), nameof(VideoEventType.Beat), 0.5));

        return raw.OrderBy(r => r.Sec).Select((r, i) => new VideoAnalysisRenderEvent(
            "e" + i, r.Type.ToLowerInvariant(), r.Type, (int)(r.Sec * 30), r.Sec, SourceIndex: 0,
            Type: r.Type, Origin: r.Type == nameof(VideoEventType.Beat) ? "Audio" : "Pixels", Strength: r.Strength)).ToList();
    }

    /// <summary>The kept ranges as source-second spans (shot starts and ends: they ignore word boundaries).</summary>
    public IReadOnlyList<(double Start, double End)> KeptSpans =>
        KeptRanges.Select(r => (r.FirstShot * ShotSec, (r.LastShot + 1) * ShotSec)).ToList();

    private sealed class Lcg(int seed)
    {
        private long _state = seed * 2654435761L + 12345;

        private double Next01()
        {
            _state = (_state * 6364136223846793005L + 1442695040888963407L) & long.MaxValue;
            return ((_state >> 11) & ((1L << 52) - 1)) / (double)(1L << 52);
        }

        public double NextDouble() => Next01();
        public int Next(int max) => (int)(Next01() * max);
    }
}
