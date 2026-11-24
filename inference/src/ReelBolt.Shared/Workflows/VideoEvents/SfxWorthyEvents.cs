using ReelBolt.Shared.Workflows;

namespace ReelBolt.Shared.Workflows.VideoEvents;

/// <summary>
/// Which detected events are worth a sound effect, and how many of them to offer. A beat found in speech, a speech edge,
/// a caption word or a weak blip is not a moment a sound belongs to; an impact, a flash, something appearing, leaving,
/// moving or changing, a transition, text coming in or a clear camera move is. Pure; the artifact keeps every event (music
/// and cut lanes still use the beats), only the offer made to the sound designer is narrowed. Ids are never renumbered.
/// </summary>
public static class SfxWorthyEvents
{
    /// <summary>The weakest strength offered: the same boundary as the <c>weak</c> word (below 0.35).</summary>
    public const double MinStrength = 0.35;

    /// <summary>At most one offered event per this many seconds of a source, on average.</summary>
    public const double SecondsPerEvent = 2.0;

    /// <summary>Two offered events are never closer than this: one sound per moment.</summary>
    public const double MinGapSec = 0.8;

    /// <summary>True when an event of this type and strength is a moment worth a sound effect.</summary>
    public static bool IsWorthy(VideoAnalysisRenderEvent e)
    {
        if (e.Type is null || !Enum.TryParse(e.Type, out VideoEventType type))
            return false;
        if ((e.Strength ?? 0) < MinStrength)
            return false;
        return type is VideoEventType.Hit or VideoEventType.Appear or VideoEventType.Disappear or VideoEventType.Transition
            or VideoEventType.MoveStart or VideoEventType.MoveStop or VideoEventType.TextIn or VideoEventType.Change
            or VideoEventType.CameraMoveStart or VideoEventType.CameraMoveStop or VideoEventType.Flash;
    }

    /// <summary>
    /// True when the event is ordinary filmed footage moving (<see cref="VideoEventKinds.Footage"/>) and not one of the two
    /// moments a sound belongs to even there: a flash of light and an impact.
    /// </summary>
    public static bool IsPlainFootage(VideoAnalysisRenderEvent e) =>
        VideoEventKinds.Parse(e.Kind) == VideoEventKinds.Footage
        && !(Enum.TryParse(e.Type, out VideoEventType type) && type is VideoEventType.Flash or VideoEventType.Hit);

    /// <summary>
    /// How much a type counts beside its strength when two moments compete for one place in the offer: the kinds a sound
    /// designer most wants to know about (an impact, a flash, something or some text arriving) first, the tail of a move last.
    /// A cut gets nothing extra: cuts are many and already strong, and a pop right after a cut should not always lose to it.
    /// </summary>
    private static double Priority(VideoAnalysisRenderEvent e) => (e.Strength ?? 0) + (Enum.TryParse(e.Type, out VideoEventType type) ? type switch
    {
        VideoEventType.Hit or VideoEventType.Flash => 0.3,
        VideoEventType.TextIn => 0.25,
        VideoEventType.Appear => 0.2,
        VideoEventType.MoveStart or VideoEventType.CameraMoveStart => 0.1,
        VideoEventType.Disappear => 0.05,
        VideoEventType.MoveStop => -0.1,
        VideoEventType.CameraMoveStop => -0.2,
        _ => 0.0
    } : 0.0);

    /// <summary>
    /// The events to offer a sound designer: the worthy ones only, thinned per source to at most one per
    /// <see cref="SecondsPerEvent"/> seconds on average and never closer than <see cref="MinGapSec"/>; where two compete, the
    /// one that counts more stays (see <see cref="Priority"/>). Returned in the input order.
    /// <paramref name="sourceDurationSec"/> gives a source's length; a missing one uses its last event. Ordinary filmed
    /// footage (<see cref="IsPlainFootage"/>) is left out unless <paramref name="onFootageToo"/>.
    /// </summary>
    public static List<VideoAnalysisRenderEvent> Select(
        IReadOnlyList<VideoAnalysisRenderEvent> detected, Func<int, double?> sourceDurationSec, bool onFootageToo = false)
    {
        HashSet<string> keep = new(StringComparer.Ordinal);
        foreach (IGrouping<int, VideoAnalysisRenderEvent> group in detected
                     .Where(e => IsWorthy(e) && (onFootageToo || !IsPlainFootage(e)))
                     .GroupBy(e => e.SourceIndex ?? 0))
        {
            List<VideoAnalysisRenderEvent> items = [.. group];
            double duration = sourceDurationSec(group.Key) is { } d && d > 0 ? d : items.Max(e => e.ContentSec);
            int cap = Math.Max(1, (int)Math.Ceiling(duration / SecondsPerEvent));
            // sorted list of the times already taken: a place is free when no taken time is within the gap
            List<double> taken = [];
            foreach (VideoAnalysisRenderEvent e in items.OrderByDescending(Priority).ThenBy(e => e.ContentSec).ThenBy(e => e.Id, StringComparer.Ordinal))
            {
                if (taken.Count >= cap)
                    break;
                int at = taken.BinarySearch(e.ContentSec);
                if (at < 0)
                    at = ~at;
                bool crowded = (at < taken.Count && taken[at] - e.ContentSec < MinGapSec) || (at > 0 && e.ContentSec - taken[at - 1] < MinGapSec);
                if (crowded)
                    continue;
                taken.Insert(at, e.ContentSec);
                keep.Add(e.Id);
            }
        }

        return [.. detected.Where(e => keep.Contains(e.Id))];
    }
}
