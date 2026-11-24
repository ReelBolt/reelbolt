using ReelBolt.Shared.Workflows.VideoEvents;

namespace ReelBolt.WorkflowEngine.Services.Video.Events;

/// <summary>Orders, de-duplicates and caps a pile of detected events into one <see cref="VideoEventSet"/>'s event list.</summary>
public static class VideoEventMerger
{
    /// <summary>Two events of one type this close in frames, in overlapping regions, are the same event.</summary>
    private const int DedupFrames = 2;

    public const int DefaultMaxEvents = 400;

    /// <summary>
    /// Clamps strengths to 0..1, drops events outside the video, merges duplicates (the stronger one stays, its end
    /// frame stretches over the other), then keeps the <paramref name="maxEvents"/> strongest. The number removed by
    /// the cap is returned so a capped set is never mistaken for a complete one.
    /// </summary>
    public static (List<VideoEvent> Events, int Suppressed) Merge(IEnumerable<VideoEvent> events, int frameCount, int maxEvents)
    {
        List<VideoEvent> clean = [];
        foreach (VideoEvent e in events)
        {
            if (e.Frame < 0 || (frameCount > 0 && e.Frame >= frameCount))
                continue;
            double strength = double.IsNaN(e.Strength) ? 0 : Math.Clamp(e.Strength, 0, 1);
            double confidence = double.IsNaN(e.Confidence) ? 0 : Math.Clamp(e.Confidence, 0, 1);
            int? end = e.EndFrame is { } ef && ef > e.Frame ? Math.Min(ef, Math.Max(frameCount - 1, e.Frame)) : null;
            clean.Add(e with { Strength = Math.Round(strength, 3), Confidence = Math.Round(confidence, 3), EndFrame = end });
        }

        List<VideoEvent> merged = [];
        foreach (IGrouping<VideoEventType, VideoEvent> byType in clean.GroupBy(e => e.Type).OrderBy(g => g.Key))
        {
            List<VideoEvent> kept = [];
            foreach (VideoEvent e in byType.OrderBy(e => e.Frame).ThenByDescending(e => e.Strength))
            {
                int index = kept.FindLastIndex(k => e.Frame - k.Frame <= DedupFrames && Overlaps(k.Region, e.Region));
                if (index < 0)
                {
                    kept.Add(e);
                    continue;
                }

                VideoEvent k = kept[index];
                VideoEvent winner = e.Strength > k.Strength ? e : k;
                int? end = MaxEnd(k.EndFrame, e.EndFrame);
                kept[index] = winner with { EndFrame = end is { } x && x > winner.Frame ? x : null };
            }

            merged.AddRange(kept);
        }

        int suppressed = 0;
        if (maxEvents > 0 && merged.Count > maxEvents)
        {
            suppressed = merged.Count - maxEvents;
            merged = merged
                .OrderByDescending(e => e.Strength)
                .ThenBy(e => e.Frame)
                .ThenBy(e => e.Type)
                .Take(maxEvents)
                .ToList();
        }

        merged = merged.OrderBy(e => e.Frame).ThenBy(e => e.Type).ToList();
        return (merged, suppressed);
    }

    private static int? MaxEnd(int? a, int? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    private static bool Overlaps(VideoEventRegion? a, VideoEventRegion? b)
    {
        if (a is null || b is null)
            return true;
        return a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;
    }
}
