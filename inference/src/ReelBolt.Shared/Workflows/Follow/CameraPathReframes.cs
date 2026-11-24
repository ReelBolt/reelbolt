namespace ReelBolt.Shared.Workflows.Follow;

/// <summary>Converts a <see cref="CameraPathResult"/> into engine <see cref="VideoReframe"/> spans.</summary>
public static class CameraPathReframes
{
    /// <summary>
    /// The most camera keys one source clip can carry: <see cref="VideoReframe.MaxReframesPerSource"/>
    /// spans of <see cref="VideoReframe.MaxKeyframes"/> keys that share their boundary key, i.e.
    /// 16 x 31 + 1 = 497. Callers size <see cref="FollowOptions.MaxKeys"/> to at most this.
    /// </summary>
    public const int MaxKeysForWorkflow = VideoReframe.MaxReframesPerSource * (VideoReframe.MaxKeyframes - 1) + 1;

    /// <summary>
    /// Splits the keys into consecutive spans (adjacent spans share their boundary key, touching
    /// but not overlapping). Keyframe times are relative to each span's FromSec; easing is Linear.
    /// </summary>
    public static IReadOnlyList<VideoReframe> ToReframes(CameraPathResult path, int sourceIndex, double fromSec, double toSec)
    {
        ArgumentNullException.ThrowIfNull(path);
        IReadOnlyList<CameraKey> keys = path.Keys;
        if (keys is null || keys.Count == 0)
            throw new ArgumentException("The camera path has no keys.");
        if (!double.IsFinite(fromSec) || !double.IsFinite(toSec) || toSec <= fromSec)
            throw new ArgumentException("toSec must be after fromSec (finite numbers).");
        if (keys.Count > MaxKeysForWorkflow)
            throw new ArgumentException($"{keys.Count} camera keys need more than {VideoReframe.MaxReframesPerSource} reframes; at most {MaxKeysForWorkflow} keys fit.");
        if (keys[0].TimeSec < fromSec - 1e-5 || keys[^1].TimeSec > toSec + 1e-5)
            throw new ArgumentException("Camera keys lie outside fromSec..toSec.");

        int step = VideoReframe.MaxKeyframes - 1;
        var spans = new List<VideoReframe>();
        if (keys.Count == 1)
        {
            spans.Add(new VideoReframe(sourceIndex, fromSec, toSec,
                [new ReframeKeyframe(Round(Math.Max(0, keys[0].TimeSec - fromSec)), keys[0].Zoom, keys[0].CenterX, keys[0].CenterY, ReframeEasing.Linear)]));
            return spans;
        }

        for (int start = 0; start < keys.Count - 1; start += step)
        {
            int end = Math.Min(start + step, keys.Count - 1);
            double spanFrom = start == 0 ? fromSec : keys[start].TimeSec;
            double spanTo = end == keys.Count - 1 ? toSec : keys[end].TimeSec;
            var frames = new List<ReframeKeyframe>(end - start + 1);
            for (int i = start; i <= end; i++)
            {
                CameraKey k = keys[i];
                frames.Add(new ReframeKeyframe(AtSec(k.TimeSec - spanFrom, spanTo - spanFrom), k.Zoom, k.CenterX, k.CenterY, ReframeEasing.Linear));
            }

            spans.Add(new VideoReframe(sourceIndex, spanFrom, spanTo, frames));
        }

        return spans;
    }

    private static double Round(double v) => Math.Round(v, FollowTuning.Decimals);

    /// <summary>
    /// A keyframe time inside its span: rounded first, then clamped to [0, span length], so rounding can never push the
    /// last key past the span's ToSec (VideoReframe allows 1e-9 of slack only).
    /// </summary>
    private static double AtSec(double offset, double spanLength) => Math.Max(0, Math.Min(Round(offset), spanLength));
}
