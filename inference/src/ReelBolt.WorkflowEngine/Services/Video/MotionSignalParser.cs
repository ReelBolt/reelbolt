using System.Globalization;
using ReelBolt.Shared.IntegrationEvents;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// R09.1: turns the <c>signalstats</c> YDIF samples of <see cref="FfmpegArgvBuilder.BuildMotionArgs"/> into one-second
/// motion windows. Pure and deterministic. YDIF is the mean luma difference between consecutive sampled frames (0-255).
/// </summary>
public static class MotionSignalParser
{
    /// <summary>Below this a window reads as a still picture.</summary>
    public const double StillBelow = 0.5;

    /// <summary>Below this (and at least <see cref="StillBelow"/>) a window reads as low motion; above it, high.</summary>
    public const double LowBelow = 4.0;

    public const double WindowSec = 1.0;

    /// <summary>(pts_time, YDIF) samples from the metadata filter's stdout.</summary>
    public static IReadOnlyList<(double TimeSec, double Ydif)> ParseSamples(string stdout)
    {
        List<(double, double)> samples = [];
        double? time = null;
        foreach (string raw in (stdout ?? string.Empty).Split('\n'))
        {
            string line = raw.Trim();
            int ptsAt = line.IndexOf("pts_time:", StringComparison.Ordinal);
            if (ptsAt >= 0)
            {
                string rest = line[(ptsAt + "pts_time:".Length)..];
                int end = rest.IndexOf(' ');
                if (double.TryParse(end < 0 ? rest : rest[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out double t))
                    time = t;
                continue;
            }

            const string key = "lavfi.signalstats.YDIF=";
            int keyAt = line.IndexOf(key, StringComparison.Ordinal);
            if (keyAt >= 0 && time is { } at
                && double.TryParse(line[(keyAt + key.Length)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double ydif)
                && double.IsFinite(ydif))
            {
                samples.Add((at, ydif));
            }
        }

        return samples;
    }

    public static string Classify(double mean) => mean < StillBelow ? "still" : mean < LowBelow ? "low" : "high";

    /// <summary>Averages the samples into consecutive one-second windows; a window with no sample is skipped.</summary>
    public static IReadOnlyList<AnalysisMotionWindow> BuildWindows(IReadOnlyList<(double TimeSec, double Ydif)> samples)
    {
        List<AnalysisMotionWindow> windows = [];
        foreach (IGrouping<long, (double TimeSec, double Ydif)> group in samples
                     .Where(s => s.TimeSec >= 0)
                     .GroupBy(s => (long)Math.Floor(s.TimeSec / WindowSec))
                     .OrderBy(g => g.Key))
        {
            double mean = Math.Round(group.Average(s => s.Ydif), 3);
            windows.Add(new AnalysisMotionWindow(group.Key * WindowSec, (group.Key + 1) * WindowSec, mean, Classify(mean)));
        }

        return windows;
    }
}
