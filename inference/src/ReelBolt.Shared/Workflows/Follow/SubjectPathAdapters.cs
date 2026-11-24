namespace ReelBolt.Shared.Workflows.Follow;

/// <summary>Turns the existing tracker artifacts into <see cref="SubjectSample"/> lists for <see cref="SubjectCameraPath"/>. Pure.</summary>
public static class SubjectPathAdapters
{
    private const double MinSide = 0.005;

    /// <summary>
    /// Local feature-tracker output: each quad keyframe becomes the min/max box of its four
    /// corners; a degenerate quad is not present. Times outside StartSec..EndSec are dropped.
    /// </summary>
    public static IReadOnlyList<SubjectSample> FromInsertTrack(VideoInsertRegionTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var list = new List<SubjectSample>();
        if (track.Keyframes is null)
            return list;
        foreach (VideoInsertQuadKeyframe k in track.Keyframes)
        {
            if (k is null || !double.IsFinite(k.TimeSec) || k.TimeSec < track.StartSec || k.TimeSec > track.EndSec)
                continue;
            double[] xs = [k.X0, k.X1, k.X2, k.X3];
            double[] ys = [k.Y0, k.Y1, k.Y2, k.Y3];
            if (xs.Concat(ys).Any(v => !double.IsFinite(v)))
            {
                list.Add(new SubjectSample(k.TimeSec, 0, 0, 0, 0, false));
                continue;
            }

            double x0 = Math.Clamp(xs.Min(), 0, 1), x1 = Math.Clamp(xs.Max(), 0, 1);
            double y0 = Math.Clamp(ys.Min(), 0, 1), y1 = Math.Clamp(ys.Max(), 0, 1);
            double w = x1 - x0, h = y1 - y0;
            list.Add(w < MinSide || h < MinSide
                ? new SubjectSample(k.TimeSec, x0, y0, 0, 0, false)
                : new SubjectSample(k.TimeSec, x0, y0, w, h, true));
        }

        return list;
    }

    /// <summary>
    /// Segmentation track: each box frame becomes a present sample; a zero-area box is not
    /// present; null/empty boxes give an empty list. The mask video is never read here.
    /// </summary>
    public static IReadOnlyList<SubjectSample> FromSegmentationTrack(VideoSegmentationTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var list = new List<SubjectSample>();
        if (track.BoxKeyframes is not { Count: > 0 })
            return list;
        foreach (VideoSegmentationBoxFrame b in track.BoxKeyframes)
        {
            if (b is null || !double.IsFinite(b.TimeSec))
                continue;
            bool finite = double.IsFinite(b.X) && double.IsFinite(b.Y) && double.IsFinite(b.W) && double.IsFinite(b.H);
            list.Add(!finite || b.W <= 0 || b.H <= 0
                ? new SubjectSample(b.TimeSec, 0, 0, 0, 0, false)
                : new SubjectSample(b.TimeSec, b.X, b.Y, b.W, b.H, true));
        }

        return list;
    }
}
