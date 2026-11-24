using ReelBolt.Shared.Workflows.VideoEvents;

namespace ReelBolt.WorkflowEngine.Services.Video.Events;

/// <summary>Hands every frame to two consumers.</summary>
internal sealed class TeeConsumer(IFrameConsumer first, IFrameConsumer second) : IFrameConsumer
{
    public void OnFrame(int index, ReadOnlySpan<byte> gray)
    {
        first.OnFrame(index, gray);
        second.OnFrame(index, gray);
    }
}

/// <summary>
/// Merges the two pixel readers into one list. <see cref="EditMomentAnalyzer"/> owns what an edit does to the picture
/// (cuts, transitions, things appearing, leaving, moving or changing, text, flashes, caption words);
/// <see cref="VisualEventClassifier"/> keeps what only it can see: camera moves and impacts of a tracked object.
/// </summary>
internal static class MomentEvents
{
    /// <summary>The frame rate the three windows below were tuned at. At this rate they are 150, 18 and 4 frames.</summary>
    internal const double ReferenceFps = 60.0;

    /// <summary>A camera move longer than this is a slow push or drift, not a moment; its strength is capped.</summary>
    internal const double SlowMoveSeconds = 2.5;

    /// <summary>A camera move the classifier found this close to a whole-picture moment of the edit is that moment.</summary>
    internal const double SameMoveSeconds = 0.3;

    /// <summary>An impact this close (4 frames at 60 fps) to a cut, a flash or a moment of the edit is that same instant seen less exactly.</summary>
    internal const double SameInstantSeconds = 4.0 / ReferenceFps;

    /// <summary>
    /// The three windows in frames of a grid analysed at <paramref name="fps"/>, so they mean the same time at any rate.
    /// A rate that is not a positive number reads as <see cref="ReferenceFps"/>. Never less than one frame.
    /// </summary>
    internal static (int SlowMove, int SameMove, int SameInstant) FrameWindows(double fps)
    {
        if (!(fps > 0) || !double.IsFinite(fps))
            fps = ReferenceFps;
        return (
            Math.Max(1, (int)Math.Round(SlowMoveSeconds * fps, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(SameMoveSeconds * fps, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(fps * 4.0 / ReferenceFps, MidpointRounding.AwayFromZero)));
    }

    /// <param name="inFilmedShot">
    /// Says whether a frame lies in a full, filmed picture (<see cref="EditMomentAnalyzer.InFullPicture"/>). A camera move
    /// there is the camera of that footage, so it is marked <see cref="VideoEventKinds.Footage"/>; null marks nothing.
    /// </param>
    /// <param name="fps">The frame rate of the analysed grid the frame numbers count on.</param>
    public static List<VideoEvent> Combine(
        List<VideoEvent> classified, List<VideoEvent> moments, Func<int, bool>? inFilmedShot = null, double fps = ReferenceFps)
    {
        (int slowMoveFrames, int sameMoveFrames, int sameInstantFrames) = FrameWindows(fps);
        VideoEvent Camera(VideoEvent e) => inFilmedShot is not null && inFilmedShot(e.Frame) ? e with { Kind = VideoEventKinds.Footage } : e;
        bool NearAny(int[] sorted, int frame) => Near(sorted, frame, sameInstantFrames);

        int[] instants = [.. moments.Where(m => m.Type != VideoEventType.CaptionTick).Select(m => m.Frame).Order()];
        int[] ticks = [.. moments.Where(m => m.Type == VideoEventType.CaptionTick).Select(m => m.Frame).Order()];
        List<(int Start, int End)> spans = [.. moments
            .Where(m => m.EndFrame is not null && m.Type is VideoEventType.Transition or VideoEventType.MoveStart)
            .Select(m => (m.Frame, m.EndFrame!.Value))];

        int[] wholePicture = [.. moments
            .Where(m => m.Type is VideoEventType.Transition or VideoEventType.CameraMoveStart)
            .Select(m => m.Frame).Order()];
        List<VideoEvent> cameraStops = [.. classified.Where(c => c.Type == VideoEventType.CameraMoveStop).OrderBy(c => c.Frame)];

        List<VideoEvent> result = [];
        HashSet<int> droppedStops = [];
        foreach (VideoEvent e in classified.OrderBy(c => c.Frame))
        {
            switch (e.Type)
            {
                case VideoEventType.CameraMoveStart:
                    {
                        VideoEvent? stop = cameraStops.FirstOrDefault(c => c.Frame >= e.Frame && !droppedStops.Contains(c.Frame));
                        // the edit moments already hold this instant (a cut, a transition, the whole picture starting to move)
                        if (Near(wholePicture, e.Frame, sameMoveFrames) || spans.Any(s => s.Start - sameMoveFrames <= e.Frame && e.Frame <= s.End))
                        {
                            if (stop is not null)
                                droppedStops.Add(stop.Frame);
                            break;
                        }

                        // a long, slow push or drift is not a moment: it stays in the list, weak
                        bool slow = stop is not null && stop.Frame - e.Frame > slowMoveFrames;
                        result.Add(Camera(slow ? e with { Strength = Math.Min(e.Strength, SlowMoveStrength), EndFrame = stop!.Frame } : e));
                        if (slow)
                        {
                            droppedStops.Add(stop!.Frame);
                            result.Add(Camera(stop with { Strength = Math.Min(stop.Strength, SlowMoveStrength) }));
                        }

                        break;
                    }

                case VideoEventType.CameraMoveStop:
                    if (!droppedStops.Contains(e.Frame))
                        result.Add(Camera(e));
                    break;
                case VideoEventType.Hit when e.EndFrame is null:
                    // a tracked object bounced or stopped dead; not when it is only a caption word or part of a moment already found
                    if (!NearAny(instants, e.Frame) && !NearAny(ticks, e.Frame) && !spans.Any(s => s.Start <= e.Frame && e.Frame <= s.End))
                        result.Add(e);
                    break;
            }
        }

        result.AddRange(moments);
        return result;
    }

    private const double SlowMoveStrength = 0.3;

    /// <summary>True when a sorted instant lies within <paramref name="window"/> frames of <paramref name="frame"/>: one binary search, O(log m).</summary>
    private static bool Near(int[] sorted, int frame, int window)
    {
        int i = Array.BinarySearch(sorted, frame - window);
        if (i < 0)
            i = ~i;
        return i < sorted.Length && sorted[i] <= frame + window;
    }
}
