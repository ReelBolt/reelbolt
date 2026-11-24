using System.Globalization;

namespace ReelBolt.WorkflowEngine.Services.Video.Quality;

/// <summary>A span of near-identical frames, in seconds.</summary>
public readonly record struct FrozenRun(double StartSec, double EndSec)
{
    public double Duration => EndSec - StartSec;
}

/// <summary>Detector options. Tolerance is a mean absolute difference out of 255.</summary>
public sealed record FrozenFrameOptions(
    double Tolerance = 0.6,
    double MinSeconds = 2,
    double TrailingHoldMaxSeconds = 4);

/// <summary>
/// Reusable analysis helper: finds runs of near-identical frames in a sampled grid and turns them
/// into findings under the conservative severity policy. Pure, deterministic, never throws.
/// </summary>
public static class FrozenFrameDetector
{
    public static IReadOnlyList<FrozenRun> Find(FrameGridResult grid, FrozenFrameOptions? options = null)
    {
        options ??= new FrozenFrameOptions();
        if (grid is null || grid.FrameCount < 2 || !(grid.EffectiveFps > 0) || !double.IsFinite(grid.EffectiveFps))
            return [];
        var diffs = FrameGridStats.ConsecutiveMeanAbsDiff(grid);
        if (diffs.Length == 0) return [];

        var tol = double.IsNaN(options.Tolerance) ? 0.6 : options.Tolerance;
        var fps = grid.EffectiveFps;
        var endTime = grid.FrameCount / fps;
        var runs = new List<FrozenRun>();
        var i = 0;
        while (i < diffs.Length)
        {
            if (!(diffs[i] <= tol)) { i++; continue; }
            var startFrame = i;
            while (i < diffs.Length && diffs[i] <= tol) i++;
            var lastFrame = i; // frame index of last frame in the run
            var start = startFrame / fps;
            var reachesEnd = lastFrame >= grid.FrameCount - 1;
            var end = reachesEnd ? endTime : (lastFrame + 1) / fps;
            var len = end - start;
            if (len < options.MinSeconds) continue;
            if (reachesEnd && len <= options.TrailingHoldMaxSeconds) continue;
            runs.Add(new FrozenRun(start, end));
        }

        return runs;
    }

    public static IReadOnlyList<QualityFinding> Assess(IReadOnlyList<FrozenRun> runs, double durationSec)
    {
        var findings = new List<QualityFinding>();
        if (runs is null) return findings;
        var dur = double.IsFinite(durationSec) ? durationSec : 0;
        foreach (var run in runs)
        {
            var len = run.Duration;
            if (!(len >= 2)) continue;
            var high = len >= 8 && dur > 0 && len >= 0.5 * dur;
            var detail = string.Create(CultureInfo.InvariantCulture,
                $"The picture does not change for {len:0.0} seconds (from {run.StartSec:0.0} s to {run.EndSec:0.0} s), so it may look frozen.");
            findings.Add(new QualityFinding(QualityKinds.FrozenFrames, high ? QualitySeverity.High : QualitySeverity.Medium,
                run.StartSec, run.EndSec, len, 2, detail));
        }

        return findings;
    }
}
