namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Splits long shots into sub-shots (<c>VideoAnalyzeStepConfig.MaxShotSec</c>, see
/// docs/video-editing.md "Keeping part of a long shot") so an editor that can only name ids can
/// still keep part of a long shot. Pure — no ffmpeg, no I/O.
/// </summary>
public static class ShotSplitter
{
    /// <summary>Shortest sub-shot a split may leave.</summary>
    public const double MinPartSec = 0.5;

    /// <summary>
    /// Every span longer than <paramref name="maxShotSec"/> becomes <c>n = ceil(length / max)</c>
    /// parts. Each split point starts at the even division and moves, within 30% of a part either
    /// way, to the stillest moment <paramref name="motion"/> reports there; failing that, to the
    /// middle of the nearest pause in <paramref name="silences"/>; failing that, it stays even. A
    /// moved point that would leave a part under <see cref="MinPartSec"/> falls back to the even
    /// one. Returns the new spans in order, and per span its "k/n" part label (null when unsplit).
    /// </summary>
    /// <para>
    /// With <paramref name="frameRate"/> (the source's own <c>r_frame_rate</c>), every split point is
    /// snapped to an exact frame edge (<see cref="SnapToFrameEdge"/>), so adjacent parts share one edge and
    /// each part owns whole frames. The shot's own start and end are left as detected.
    /// </para>
    public static (List<(double StartSec, double EndSec)> Spans, List<string?> Parts) Split(
        IReadOnlyList<(double StartSec, double EndSec)> spans,
        double maxShotSec,
        MotionCurve? motion,
        IReadOnlyList<(double StartSec, double EndSec)> silences,
        (int Num, int Den)? frameRate = null)
    {
        var outSpans = new List<(double StartSec, double EndSec)>();
        var parts = new List<string?>();
        double max = Math.Max(1.0, maxShotSec);
        foreach ((double start, double end) in spans)
        {
            double length = end - start;
            if (length <= max + 1e-9)
            {
                outSpans.Add((start, end));
                parts.Add(null);
                continue;
            }

            int n = (int)Math.Ceiling(length / max - 1e-9);
            double step = length / n;
            double previous = start;
            var cuts = new List<double>();
            for (int k = 1; k < n; k++)
            {
                double even = start + k * step;
                double lo = even - 0.3 * step, hi = even + 0.3 * step;
                double? chosen = motion?.StillestIn(lo, hi);
                chosen ??= silences
                    .Select(s => (s.StartSec + s.EndSec) / 2)
                    .Where(m => m >= lo && m <= hi)
                    .OrderBy(m => Math.Abs(m - even))
                    .Select(m => (double?)m)
                    .FirstOrDefault();
                double cut = chosen ?? even;
                if (cut - previous < MinPartSec || end - cut < MinPartSec)
                    cut = even;
                cuts.Add(cut);
                previous = cut;
            }

            List<(double StartSec, double EndSec)> laid = Lay(start, end, cuts, frameRate);
            for (int k = 0; k < n; k++)
            {
                outSpans.Add(laid[k]);
                parts.Add($"{k + 1}/{n}");
            }
        }

        return (outSpans, parts);
    }

    /// <summary>The spans after a content split, the "k/n" label per span (null when unsplit), and one record per shot that was split.</summary>
    public sealed record ContentSplit(
        List<(double StartSec, double EndSec)> Spans,
        List<string?> Parts,
        List<SplitShot> Shots);

    /// <summary>One split shot: its original span, how many parts it became, how its cuts were chosen and why each cut is where it is.</summary>
    public sealed record SplitShot(double StartSec, double EndSec, int Parts, SubShotSplitMethod Method, IReadOnlyList<SubShotCutReason> Reasons);

    /// <summary>
    /// <c>VideoSubShotMode.Content</c>: every span is planned by <see cref="SubShotPlanner.Plan"/>, so
    /// its cut points come from the content (motion pauses, framing changes, pauses in the sound)
    /// within <paramref name="bounds"/>, falling back to an even split where the signal is flat.
    /// </summary>
    public static ContentSplit SplitByContent(
        IReadOnlyList<(double StartSec, double EndSec)> spans,
        SubShotSignal signal,
        IReadOnlyList<(double StartSec, double EndSec)> silences,
        SubShotBounds bounds,
        (int Num, int Den)? frameRate = null)
    {
        var outSpans = new List<(double StartSec, double EndSec)>();
        var parts = new List<string?>();
        var shots = new List<SplitShot>();
        foreach ((double start, double end) in spans)
        {
            SubShotPlan plan = SubShotPlanner.Plan(start, end, signal, silences, bounds);
            if (plan.Cuts.Count == 0)
            {
                outSpans.Add((start, end));
                parts.Add(null);
                continue;
            }

            int n = plan.Cuts.Count + 1;
            List<(double StartSec, double EndSec)> laid = Lay(start, end, plan.Cuts, frameRate);
            for (int k = 0; k < n; k++)
            {
                outSpans.Add(laid[k]);
                parts.Add($"{k + 1}/{n}");
            }

            shots.Add(new SplitShot(start, end, n, plan.Method, plan.Reasons));
        }

        return new ContentSplit(outSpans, parts, shots);
    }

    /// <summary>
    /// A sub-shot boundary on a source frame edge: <see cref="Frame"/> is the first frame of the part
    /// after it. <see cref="EndSec"/> ends the part before (the compile's ceiling of
    /// <c>EndSec * num / den</c> is exactly <see cref="Frame"/>) and <see cref="StartSec"/> starts the part
    /// after (its floor is exactly <see cref="Frame"/>), so each part owns whole frames and none is
    /// dropped or written twice. They are the same double whenever one double names the frame exactly;
    /// otherwise (a few percent of frames at any rate) <see cref="StartSec"/> is the next double or two
    /// up, far inside the compile's 1e-6 s adjacency epsilon.
    /// </summary>
    public readonly record struct FrameEdge(long Frame, double EndSec, double StartSec);

    /// <summary>
    /// Snaps <paramref name="sec"/> to the nearest frame edge (half frames round away from zero) at
    /// <paramref name="fpsNum"/>/<paramref name="fpsDen"/> — the source's own <c>r_frame_rate</c>,
    /// 30000/1001 included. See <see cref="FrameEdge"/>.
    /// </summary>
    public static FrameEdge SnapToFrameEdge(double sec, int fpsNum, int fpsDen)
    {
        long frame = (long)Math.Round(sec * fpsNum / fpsDen, MidpointRounding.AwayFromZero);
        double t = frame * (double)fpsDen / fpsNum;
        if (Product(t) == frame)
            return new FrameEdge(frame, t, t);

        // The largest double whose product is <= frame (ceiling = frame) ends the part before; the
        // smallest whose product is >= frame (floor = frame) starts the part after. The product is
        // monotone in t, so both are a walk of a few ulps from t.
        double end = t;
        while (Product(end) > frame)
            end = Math.BitDecrement(end);
        while (Product(Math.BitIncrement(end)) <= frame)
            end = Math.BitIncrement(end);
        double start = t;
        while (Product(start) < frame)
            start = Math.BitIncrement(start);
        while (Product(Math.BitDecrement(start)) >= frame)
            start = Math.BitDecrement(start);

        if (Product(end) == frame)
            start = end;
        else if (Product(start) == frame)
            end = start;
        return new FrameEdge(frame, end, start);

        // Exactly the compile's expression (VideoCompileStepExecutor.ToStartFrame/ToEndFrame): t * num / den.
        double Product(double x) => x * fpsNum / fpsDen;
    }

    /// <summary>
    /// Turns a shot's cut times into frame edges (with <paramref name="frameRate"/>) and lays out its
    /// parts: each part runs from the previous edge's <see cref="FrameEdge.StartSec"/> to the next edge's
    /// <see cref="FrameEdge.EndSec"/>. A cut whose frame would not fall strictly between its neighbours'
    /// stays as planned. Without a frame rate the cut times are used as they are.
    /// </summary>
    private static List<(double StartSec, double EndSec)> Lay(
        double start, double end, IReadOnlyList<double> cuts, (int Num, int Den)? frameRate)
    {
        var parts = new List<(double StartSec, double EndSec)>(cuts.Count + 1);
        double from = start, previousCut = start;
        foreach (double cut in cuts)
        {
            double partEnd = cut, nextStart = cut;
            if (frameRate is { Num: > 0, Den: > 0 } rate)
            {
                FrameEdge edge = SnapToFrameEdge(cut, rate.Num, rate.Den);
                if (edge.EndSec > previousCut && edge.StartSec < end)
                    (partEnd, nextStart) = (edge.EndSec, edge.StartSec);
            }

            parts.Add((from, partEnd));
            from = nextStart;
            previousCut = nextStart;
        }

        parts.Add((from, end));
        return parts;
    }

    /// <summary>
    /// Per-moment motion of a sampled grid: the mean absolute luma change between consecutive
    /// frames, 0..1, timed at the midpoint between the two frames.
    /// </summary>
    public sealed class MotionCurve
    {
        private readonly double[] _times;
        private readonly double[] _values;

        public MotionCurve(double[] times, double[] values)
        {
            _times = times;
            _values = values;
        }

        public int Count => _values.Length;

        /// <summary>The time of the stillest moment in <c>[lo, hi]</c> (values smoothed over three samples), or null when no sample falls there.</summary>
        public double? StillestIn(double lo, double hi)
        {
            double? best = null;
            double bestValue = double.MaxValue;
            for (int i = 0; i < _values.Length; i++)
            {
                if (_times[i] < lo || _times[i] > hi)
                    continue;
                double a = _values[Math.Max(0, i - 1)], b = _values[i], c = _values[Math.Min(_values.Length - 1, i + 1)];
                double v = (a + b + c) / 3;
                if (v < bestValue - 1e-12)
                {
                    bestValue = v;
                    best = _times[i];
                }
            }

            return best;
        }

        /// <summary>Builds the curve from an RGB24 grid (<see cref="FrameGridResult"/>).</summary>
        public static MotionCurve FromGrid(FrameGridResult grid)
        {
            int pixels = grid.GridWidth * grid.GridHeight;
            int frameSize = pixels * 3;
            int pairs = Math.Max(0, grid.FrameCount - 1);
            var times = new double[pairs];
            var values = new double[pairs];
            if (grid.EffectiveFps <= 0 || pixels == 0)
                return new MotionCurve([], []);

            for (int f = 0; f < pairs; f++)
            {
                double sum = 0;
                int a = f * frameSize, b = (f + 1) * frameSize;
                for (int p = 0; p < pixels; p++)
                {
                    double la = Luma(grid.PixelData, a + p * 3);
                    double lb = Luma(grid.PixelData, b + p * 3);
                    sum += Math.Abs(lb - la);
                }

                values[f] = sum / pixels;
                times[f] = (f + 0.5) / grid.EffectiveFps;
            }

            return new MotionCurve(times, values);
        }

        private static double Luma(byte[] data, int i) =>
            (0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2]) / 255.0;
    }
}
