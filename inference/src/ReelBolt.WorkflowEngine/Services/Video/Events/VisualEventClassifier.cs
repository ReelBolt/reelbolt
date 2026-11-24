using ReelBolt.Shared.Workflows.VideoEvents;

namespace ReelBolt.WorkflowEngine.Services.Video.Events;

/// <summary>
/// Turns the camera trace and the impacts of one <see cref="FrameAnalysis"/> into typed events on the analysed
/// frame grid: where a camera move starts and stops, and where a tracked object lands or turns back. Everything
/// else an edit does to the picture is read by <see cref="EditMomentAnalyzer"/>.
/// Deterministic and pure: the same analysis always yields the same events.
/// </summary>
internal static class VisualEventClassifier
{
    /// <summary>Impacts before this frame are the stream starting up (first keyframe settling), not events.</summary>
    private const int WarmupFrames = 8;
    /// <summary>Camera runs: gaps up to this long are bridged, shorter runs dropped; displacement that marks start and stop (160 px units).</summary>
    private const int CameraBridgeFrames = 6;
    private const int CameraMinFrames = 8;
    private const double CameraEdgeDisplacement = 0.1;
    private const int CameraTailFrames = 15;
    private const float CameraTailMotion = 0.004f;
    /// <summary>Impacts closer than this to the previous one are the same event (a shaking or bouncing object).</summary>
    private const int ImpactDebounceFrames = 4;
    /// <summary>
    /// A camera move has a direction when its net travel one way is at least this share of everything the picture
    /// moved during it. A shake goes back and forth, so its net travel is small and it gets no direction.
    /// A starting value, not measured on real footage.
    /// </summary>
    internal const double CameraDirectionMinShare = 0.4;

    public static List<VideoEvent> Classify(FrameAnalysis analysis)
    {
        List<VideoEvent> events = [];
        List<(int Start, int End, double Displacement)> cameraRuns = CameraRuns(analysis.CameraMagnitude, analysis.CameraDriven);
        foreach ((int start, int end, double displacement) in cameraRuns)
        {
            (int a, int b) = CameraEdges(analysis.CameraMagnitude, start, end);
            double strength = Math.Clamp(displacement / 10.0, 0.05, 1.0);
            events.Add(new VideoEvent(
                VideoEventType.CameraMoveStart, a, null, null, strength, VideoEventOrigin.Pixels, 0.8,
                Direction: CameraDirection(analysis.CameraShifts, start, end, displacement)));
            events.Add(new VideoEvent(VideoEventType.CameraMoveStop, b, null, null, strength, VideoEventOrigin.Pixels, 0.8));
        }

        foreach (BlobImpact impact in DebounceImpacts(analysis.Impacts))
        {
            if (impact.Frame < WarmupFrames)
                continue;
            events.Add(new VideoEvent(
                VideoEventType.Hit, impact.Frame, null, CellRegion(impact.X - 1, impact.Y - 1, impact.X + 1, impact.Y + 1),
                Math.Clamp(impact.SpeedBefore / 3.0, 0.1, 1.0), VideoEventOrigin.Pixels, 0.6));
        }

        return events;
    }

    private static VideoEventRegion CellRegion(double x0, double y0, double x1, double y1)
    {
        double left = Math.Clamp(x0 / FrameEventAnalyzer.CellsX, 0, 1);
        double top = Math.Clamp(y0 / FrameEventAnalyzer.CellsY, 0, 1);
        double right = Math.Clamp(x1 / FrameEventAnalyzer.CellsX, 0, 1);
        double bottom = Math.Clamp(y1 / FrameEventAnalyzer.CellsY, 0, 1);
        return new VideoEventRegion(Math.Round(left, 4), Math.Round(top, 4), Math.Round(right - left, 4), Math.Round(bottom - top, 4));
    }

    // ---- camera -----------------------------------------------------------------------------------------

    private static List<(int Start, int End, double Displacement)> CameraRuns(List<float> magnitude, List<bool> driven)
    {
        List<(int, int, double)> runs = [];
        int t = 0;
        while (t < driven.Count)
        {
            if (!driven[t])
            {
                t++;
                continue;
            }

            int start = t, last = t;
            while (t < driven.Count && t <= last + CameraBridgeFrames)
            {
                if (driven[t])
                    last = t;
                t++;
            }

            if (last - start + 1 < CameraMinFrames)
                continue;

            // Ease-in and ease-out tails are too faint to cover the frame: follow the fitted motion out while it lasts.
            int from = start, to = last;
            while (from > 0 && start - from < CameraTailFrames && magnitude[from - 1] >= CameraTailMotion)
                from--;
            while (to < magnitude.Count - 1 && to - last < CameraTailFrames && magnitude[to + 1] >= CameraTailMotion)
                to++;
            double total = 0;
            for (int k = from; k <= to; k++)
                total += magnitude[k];
            runs.Add((from, to, total));
        }

        return runs;
    }

    /// <summary>
    /// First frame the camera has moved a visible distance, and first frame it is within that distance of where it
    /// ends up: ease-in and ease-out tails are tiny per frame, so thresholding speed would put both edges too far in.
    /// </summary>
    private static (int Start, int Stop) CameraEdges(List<float> magnitude, int start, int end)
    {
        double total = 0;
        for (int t = start; t <= end; t++)
            total += magnitude[t];

        int a = start, b = end;
        double cumulative = 0;
        for (int t = start; t <= end; t++)
        {
            cumulative += magnitude[t];
            if (cumulative >= CameraEdgeDisplacement)
            {
                a = t;
                break;
            }
        }

        cumulative = 0;
        for (int t = start; t <= end; t++)
        {
            cumulative += magnitude[t];
            if (total - cumulative < CameraEdgeDisplacement)
            {
                b = t;
                break;
            }
        }

        return (a, Math.Max(a, b));
    }

    /// <summary>
    /// The way the camera went over a run, or null when it went no clear way. The shifts say where the content went;
    /// the camera goes the other way: content sliding right is a pan to the left, content spreading out from the
    /// centre is a push in. A zoom wins over a pan when its net travel is the larger of the two.
    /// </summary>
    internal static string? CameraDirection(List<CameraShift> shifts, int start, int end, double displacement)
    {
        double x = 0, y = 0, zoom = 0;
        for (int t = Math.Max(start, 0); t <= end && t < shifts.Count; t++)
        {
            x += shifts[t].X;
            y += shifts[t].Y;
            zoom += shifts[t].Zoom;
        }

        double pan = Math.Sqrt(x * x + y * y);
        if (displacement <= 0 || Math.Max(pan, Math.Abs(zoom)) < CameraDirectionMinShare * displacement)
            return null;
        if (Math.Abs(zoom) >= pan)
            return zoom > 0 ? VideoEventWords.CameraIn : VideoEventWords.CameraOut;
        return VideoEventWords.DirectionWord(-x, -y);
    }

    private static List<BlobImpact> DebounceImpacts(List<BlobImpact> impacts)
    {
        List<BlobImpact> kept = [];
        int last = int.MinValue / 2;
        foreach (BlobImpact impact in impacts.OrderBy(i => i.Frame))
        {
            if (impact.Frame - last > ImpactDebounceFrames)
                kept.Add(impact);
            last = impact.Frame;
        }

        return kept;
    }
}
