using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Prints the beat F-measure (+-70 ms) of the old analyzer grid and the new tracker for every audio file in
/// <c>REELBOLT_BEAT_EVAL_DIR</c> that has a sibling <c>&lt;name&gt;.beats.txt</c> (one reference beat time in seconds per line).
/// Asserts nothing beyond "ran"; skipped when the variable is unset.
/// </summary>
public sealed class BeatEvalFactAttribute : FactAttribute
{
    public static string Dir => Environment.GetEnvironmentVariable("REELBOLT_BEAT_EVAL_DIR") ?? string.Empty;

    public BeatEvalFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Dir) || !Directory.Exists(Dir))
            Skip = "Set REELBOLT_BEAT_EVAL_DIR to a folder of audio files with sibling <name>.beats.txt references.";
    }
}

public class MusicBeatTrackerEvalTests
{
    private const double Tolerance = 0.07;
    private readonly ITestOutputHelper _output;

    public MusicBeatTrackerEvalTests(ITestOutputHelper output) => _output = output;

    [BeatEvalFact]
    public void Prints_f_measure_of_the_old_grid_and_the_tracker()
    {
        int files = 0;
        foreach (string reference in Directory.EnumerateFiles(BeatEvalFactAttribute.Dir, "*.beats.txt").OrderBy(p => p, StringComparer.Ordinal))
        {
            string stem = reference[..^".beats.txt".Length];
            string? audio = Directory.EnumerateFiles(Path.GetDirectoryName(stem)!, Path.GetFileName(stem) + ".*")
                .FirstOrDefault(p => !p.EndsWith(".beats.txt", StringComparison.Ordinal));
            if (audio is null)
                continue;

            double[] refBeats = File.ReadAllLines(reference)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && double.TryParse(l, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                .Select(l => double.Parse(l, CultureInfo.InvariantCulture))
                .OrderBy(v => v)
                .ToArray();

            string wav = Path.Combine(Path.GetTempPath(), $"beat-eval-{Guid.NewGuid():N}.wav");
            try
            {
                using (Process ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg",
                           ["-nostdin", "-v", "error", "-y", "-i", audio, "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wav])
                { RedirectStandardError = true })!)
                {
                    ffmpeg.WaitForExit(120_000);
                    if (ffmpeg.ExitCode != 0)
                    {
                        _output.WriteLine($"{Path.GetFileName(audio)}: ffmpeg failed");
                        continue;
                    }
                }

                float[]? samples = WavRmsSampler.ReadMonoSamples(File.ReadAllBytes(wav), out int rate);
                if (samples is null)
                    continue;

                MusicBeatAnalysis? old = MusicBeatAnalyzer.Analyze(samples, rate);
                MusicBeatTrack? tracked = MusicBeatTracker.Track(samples, rate);
                double oldF = old is null ? 0 : FMeasure(refBeats, old.BeatTimes(old.DurationSec));
                double newF = tracked is null ? 0 : FMeasure(refBeats, tracked.BeatTimesSec);
                double newShiftedF = tracked is null ? 0 : BestShiftedFMeasure(refBeats, tracked.BeatTimesSec);
                _output.WriteLine($"{Path.GetFileName(audio)}: old grid F={oldF:F3} (bpm {old?.Bpm}), tracker F={newF:F3} (bpm {tracked?.Bpm}), tracker F with the best constant phase shift of the reference={newShiftedF:F3}");
                files++;
            }
            finally
            {
                File.Delete(wav);
            }
        }

        _output.WriteLine($"evaluated {files} file(s)");
    }

    /// <summary>
    /// F-measure after shifting the reference by the constant offset (within half a median reference interval, 10 ms steps)
    /// that scores best: it separates "the tracker has the wrong period" from "the reference grid starts on a different phase".
    /// </summary>
    internal static double BestShiftedFMeasure(IReadOnlyList<double> reference, IReadOnlyList<double> estimate)
    {
        if (reference.Count < 2)
            return FMeasure(reference, estimate);
        double period = reference.Zip(reference.Skip(1), (a, b) => b - a).OrderBy(v => v).ElementAt((reference.Count - 1) / 2);
        double best = 0;
        for (double shift = -period / 2; shift <= period / 2; shift += 0.01)
            best = Math.Max(best, FMeasure(reference.Select(r => r + shift).ToArray(), estimate));
        return best;
    }

    /// <summary>F-measure with one-to-one greedy matching within <see cref="Tolerance"/>.</summary>
    internal static double FMeasure(IReadOnlyList<double> reference, IReadOnlyList<double> estimate)
    {
        if (reference.Count == 0 || estimate.Count == 0)
            return 0;
        var used = new bool[estimate.Count];
        int hits = 0;
        foreach (double r in reference)
        {
            int best = -1;
            double bestDist = Tolerance + 1e-9;
            for (int i = 0; i < estimate.Count; i++)
            {
                double d = Math.Abs(estimate[i] - r);
                if (!used[i] && d < bestDist)
                {
                    best = i;
                    bestDist = d;
                }
            }

            if (best >= 0)
            {
                used[best] = true;
                hits++;
            }
        }

        double precision = hits / (double)estimate.Count, recall = hits / (double)reference.Count;
        return precision + recall > 0 ? 2 * precision * recall / (precision + recall) : 0;
    }
}
