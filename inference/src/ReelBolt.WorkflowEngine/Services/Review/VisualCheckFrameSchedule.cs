using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Review;

/// <summary>
/// Which moments a visual check samples: <c>frames</c> evenly spaced from 0 s (so an opening title card is
/// seen), plus the last frame (end cards and lockups are where cropping showed up in the launch film).
/// Times are rounded to hundredths of a second, strictly inside the clip, and never repeat.
/// </summary>
public static class VisualCheckFrameSchedule
{
    /// <summary>How far before the end the "last frame" is taken: seeking to the exact duration yields no frame.</summary>
    public const double LastFrameBackoffSec = 0.1;

    public static IReadOnlyList<VisualCheckFrameTime> For(double durationSec, int frames)
    {
        if (!double.IsFinite(durationSec) || durationSec <= 0)
            return [];

        int count = Math.Clamp(frames, VisualCheckConfig.MinFrames, VisualCheckConfig.MaxFrames);
        List<double> times = new(count + 1);
        for (int i = 0; i < count; i++)
            times.Add(durationSec * i / count);
        times.Add(durationSec - Math.Min(LastFrameBackoffSec, durationSec / (2.0 * count)));

        List<VisualCheckFrameTime> schedule = new(times.Count);
        HashSet<double> seen = [];
        foreach (double raw in times)
        {
            double at = Math.Round(raw, 2, MidpointRounding.AwayFromZero);
            if (at >= durationSec)
                at = Math.Floor(raw * 100) / 100;
            at = Math.Max(0, at);
            if (seen.Add(at))
                schedule.Add(new VisualCheckFrameTime(schedule.Count + 1, at));
        }

        return schedule;
    }
}
