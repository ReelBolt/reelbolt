namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>Why a sub-shot boundary sits where it does. One word each in meta (<see cref="SubShotPlanner.ToWord(SubShotCutReason)"/>).</summary>
public enum SubShotCutReason
{
    /// <summary>No content boundary was strong enough here: an even division of a span that was too long.</summary>
    Even,
    /// <summary>A pause in the movement: a valley of frame-to-frame motion, lower than the motion on both sides.</summary>
    MotionPause,
    /// <summary>A change of framing or composition: a peak of the before/after layout difference.</summary>
    VisualChange,
    /// <summary>A pause in the sound: the middle of a detected silence.</summary>
    AudioDip
}

/// <summary>How a shot's cut points were chosen.</summary>
public enum SubShotSplitMethod
{
    /// <summary>Not split: too short, or within the maximum with no content boundary.</summary>
    Unsplit,
    /// <summary>At least one cut came from the content.</summary>
    Content,
    /// <summary>The signal was flat or missing: split evenly, as <c>MaxShotSec</c> would.</summary>
    EvenFallback
}

/// <summary>
/// The bounds a content split must respect: every part at least <see cref="MinSec"/> and at most
/// <see cref="MaxSec"/> long, at most <see cref="MaxParts"/> parts per shot. Build it with
/// <see cref="Clamp"/>, which always returns bounds that can be met (<c>MaxSec &gt;= 2 * MinSec</c>).
/// </summary>
public sealed record SubShotBounds(double MinSec, double MaxSec, int MaxParts)
{
    public const double LowestMinSec = 0.5;
    public const double HighestMinSec = 10;
    public const double HighestMaxSec = 60;
    public const int HighestMaxParts = 64;

    /// <summary>The derush defaults: parts of 1.2..4 s, at most 8 per shot.</summary>
    public static readonly SubShotBounds Default = new(1.2, 4.0, 8);

    /// <summary>
    /// Clamps configured values: <c>MinSec</c> to [0.5, 10] (non-finite: 1.2), <c>MaxSec</c> to
    /// [2 x MinSec, 60] (NaN: 4, +inf: 60) so any span of at least MinSec can be divided within
    /// bounds, <c>MaxParts</c> to [2, 64].
    /// </summary>
    public static SubShotBounds Clamp(double minSec, double maxSec, int maxParts)
    {
        double min = double.IsFinite(minSec) ? Math.Clamp(minSec, LowestMinSec, HighestMinSec) : Default.MinSec;
        double max = double.IsNaN(maxSec) ? Default.MaxSec : maxSec;
        max = Math.Clamp(max, 2 * min, HighestMaxSec);
        return new SubShotBounds(min, max, Math.Clamp(maxParts, 2, HighestMaxParts));
    }
}

/// <summary>A shot's planned cut times (absolute, strictly increasing, inside the shot), one reason per cut, and the method.</summary>
public sealed record SubShotPlan(IReadOnlyList<double> Cuts, IReadOnlyList<SubShotCutReason> Reasons, SubShotSplitMethod Method)
{
    public static readonly SubShotPlan Unsplit = new([], [], SubShotSplitMethod.Unsplit);
}

/// <summary>
/// The visual signal curves the planner reads, one value per pair of consecutive sampled frames,
/// timed at the pair's midpoint: <see cref="Motion"/>, the mean absolute luma change between the
/// two frames per second (0..fps), and <see cref="Change"/>, the mean absolute difference between
/// the coarse luma layout (8x6 cells) averaged over the half second before and the half second
/// after that moment, never reaching across a shot boundary.
/// </summary>
public sealed class SubShotSignal
{
    /// <summary>Half-width of the before/after windows of <see cref="Change"/>.</summary>
    public const double ChangeWindowSec = 0.5;

    private const int LayoutColumns = 8;
    private const int LayoutRows = 6;

    private readonly double[] _times;
    private readonly double[] _motion;
    private readonly double[] _change;

    public SubShotSignal(double[] times, double[] motion, double[] change, double sampleIntervalSec)
    {
        if (motion.Length != times.Length || change.Length != times.Length)
            throw new ArgumentException("Every curve needs one value per sample time.");
        _times = times;
        _motion = motion;
        _change = change;
        SampleIntervalSec = sampleIntervalSec;
    }

    /// <summary>No samples: every split falls back to even.</summary>
    public static SubShotSignal Empty { get; } = new([], [], [], 0);

    public int Count => _times.Length;
    public IReadOnlyList<double> Times => _times;
    public IReadOnlyList<double> Motion => _motion;
    public IReadOnlyList<double> Change => _change;

    /// <summary>Seconds between consecutive samples (1 / effective fps).</summary>
    public double SampleIntervalSec { get; }

    /// <summary>
    /// Derives both curves from an RGB24 grid (<see cref="FrameGridResult"/>). <paramref name="shots"/>
    /// keeps the <see cref="Change"/> windows inside the shot each moment belongs to; empty means one
    /// shot covering everything. With <paramref name="sourceFps"/> (the probe's r_frame_rate) each
    /// sampled frame is timed at the source frame it really is (<see cref="SampledFrameSec"/>);
    /// without it, at <c>i / fps</c>.
    /// </summary>
    public static SubShotSignal FromGrid(
        FrameGridResult grid, IReadOnlyList<(double StartSec, double EndSec)> shots, double? sourceFps = null)
    {
        int width = grid.GridWidth, height = grid.GridHeight;
        int pixels = width * height;
        int frames = grid.FrameCount;
        double fps = grid.EffectiveFps;
        if (fps <= 0 || pixels <= 0 || frames < 2 || grid.PixelData.Length < frames * pixels * 3)
            return Empty;

        int columns = Math.Min(LayoutColumns, width), rows = Math.Min(LayoutRows, height);
        int cells = columns * rows;
        int[] cellOf = new int[pixels];
        int[] cellSize = new int[cells];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int cell = (y * rows / height) * columns + (x * columns / width);
                cellOf[y * width + x] = cell;
                cellSize[cell]++;
            }

        var luma = new double[frames][];
        var layout = new double[frames][];
        for (int f = 0; f < frames; f++)
        {
            luma[f] = new double[pixels];
            layout[f] = new double[cells];
            int offset = f * pixels * 3;
            for (int p = 0; p < pixels; p++)
            {
                int i = offset + p * 3;
                double l = (0.299 * grid.PixelData[i] + 0.587 * grid.PixelData[i + 1] + 0.114 * grid.PixelData[i + 2]) / 255.0;
                luma[f][p] = l;
                layout[f][cellOf[p]] += l;
            }

            for (int c = 0; c < cells; c++)
                layout[f][c] /= cellSize[c];
        }

        var frameTime = new double[frames];
        for (int f = 0; f < frames; f++)
            frameTime[f] = sourceFps is > 0 ? SampledFrameSec(f, fps, sourceFps.Value) : f / fps;

        int pairs = frames - 1;
        int window = Math.Max(1, (int)Math.Round(ChangeWindowSec * fps));
        var times = new double[pairs];
        var motion = new double[pairs];
        var change = new double[pairs];
        for (int f = 0; f < pairs; f++)
        {
            double t = (frameTime[f] + frameTime[f + 1]) / 2;
            times[f] = t;

            double sum = 0;
            for (int p = 0; p < pixels; p++)
                sum += Math.Abs(luma[f + 1][p] - luma[f][p]);
            motion[f] = sum / pixels * fps;

            (double shotStart, double shotEnd) = ShotAt(shots, t);
            int firstBefore = f, lastAfter = f + 1;
            if (frameTime[f] < shotStart - 1e-9 || frameTime[f + 1] >= shotEnd - 1e-9)
                continue; // the pair straddles a cut: there is no "inside the shot" on one side
            while (firstBefore - 1 >= 0 && f - (firstBefore - 1) < window && frameTime[firstBefore - 1] >= shotStart - 1e-9)
                firstBefore--;
            while (lastAfter + 1 < frames && lastAfter + 1 - f <= window && frameTime[lastAfter + 1] < shotEnd - 1e-9)
                lastAfter++;

            double diff = 0;
            for (int c = 0; c < cells; c++)
            {
                double before = 0, after = 0;
                for (int g = firstBefore; g <= f; g++)
                    before += layout[g][c];
                for (int g = f + 1; g <= lastAfter; g++)
                    after += layout[g][c];
                diff += Math.Abs(before / (f - firstBefore + 1) - after / (lastAfter - f));
            }

            change[f] = diff / cells;
        }

        return new SubShotSignal(times, motion, change, 1 / fps);
    }

    /// <summary>
    /// The source time of sampled frame <paramref name="index"/> of an <c>fps=<paramref name="sampleFps"/></c>
    /// pass over a <paramref name="sourceFps"/> source. ffmpeg's fps filter (round=near) keeps, for each
    /// output slot, the LAST source frame whose time is before the slot's upper half-boundary
    /// <c>(index + 0.5) / sampleFps</c>: at 6 fps from 30 fps that is source frame <c>5 i + 2</c>, 1/15 s
    /// after <c>i / 6</c> (measured on ffmpeg 8). Timing the sample at <c>i / 6</c> put every cut early.
    /// </summary>
    public static double SampledFrameSec(int index, double sampleFps, double sourceFps)
    {
        double bound = (index + 0.5) * sourceFps / sampleFps;
        double frame = Math.Max(0, Math.Ceiling(bound - 1e-9) - 1);
        return frame / sourceFps;
    }

    private static (double Start, double End) ShotAt(IReadOnlyList<(double StartSec, double EndSec)> shots, double t)
    {
        foreach ((double start, double end) in shots)
        {
            if (t >= start && t < end)
                return (start, end);
        }

        return shots.Count == 0 ? (double.NegativeInfinity, double.PositiveInfinity) : (t, t);
    }
}

/// <summary>
/// Plans where a long shot is cut into sub-shots from its CONTENT (<c>VideoSubShotMode.Content</c>,
/// docs/video-editing.md "Keeping part of a long shot"). Pure: curves and bounds in, cut times and a
/// reason per cut out. Deterministic, never a model.
/// <para>
/// <b>Signals</b> (<see cref="SubShotSignal"/>, one dedicated grid pass at <see cref="SampleFps"/>
/// over the same 32x18 area-scaled stream Phase 1 samples): frame-to-frame <b>motion</b>, the
/// before/after <b>layout change</b>, and the <b>silences</b> silencedetect already found.
/// </para>
/// <para>
/// <b>Score</b>, per sample strictly inside the shot, each in 0..1 and two-sided so a steady pan (or
/// steady anything) scores nothing:
/// <list type="bullet">
/// <item><b>motion pause</b>: at a local minimum of motion (smoothed over three samples), widened to the
/// contiguous hold around it (raw samples within <see cref="PlateauTolerance"/> of the local range of
/// the lowest raw value) and placed at the hold's centre, its depth
/// below the LOWER of the highest motion in the second before and the second after:
/// <c>(ref - m) / ref</c>. Zero when <c>ref</c> is under <see cref="MotionFloor"/> (a locked-off still
/// has no pauses, only noise). A pan with a brief hold scores high at the hold.</item>
/// <item><b>visual change</b>: at a local maximum of layout change, its height above the HIGHER of
/// the lowest change in the second before and the second after: <c>(c - base) / c</c>. Zero under
/// <see cref="ChangeFloor"/>. A subject entering, or a composition shift without a scene cut, peaks
/// here; a steady pan does not (its change is flat), nor do the flanks of a hold (one side is flat).</item>
/// <item><b>audio dip</b>: at the middle of each silence, <c>min(1, length / 1 s)</c>.</item>
/// </list>
/// A sample's score is the larger of pause and change (ties: pause); a candidate is a moment scoring
/// at least <see cref="MinScore"/> at least <c>MinSec</c> from either end.
/// </para>
/// <para>
/// <b>Selection</b> (exact, dynamic programming over the candidates in time order): choose the cuts
/// maximising the sum of <c>score - MinScore</c> such that every gap between chosen cuts (and the
/// shot's ends) is at least <c>MinSec</c>. A gap longer than the effective maximum
/// (<see cref="EffectiveMaxSec"/>) is divided evenly into <c>ceil(gap / max)</c> parts (reason
/// <see cref="SubShotCutReason.Even"/>), each fill costing <see cref="FillCost"/>; the total number
/// of parts never exceeds <c>MaxParts</c>. Ties (within 1e-9) keep fewer parts, then earlier cuts.
/// Every strong boundary that fits is offered; the editor may ignore any of them.
/// </para>
/// <para>
/// <b>Bounds</b>: a shot shorter than <c>2 x MinSec</c> is never split. A shot within the maximum
/// is split only at content. When no content cut is chosen and the shot is longer than the
/// maximum, it is split evenly and the method is <see cref="SubShotSplitMethod.EvenFallback"/>.
/// When even the cap cannot keep parts under <c>MaxSec</c> (a shot longer than
/// <c>MaxParts x MaxSec</c>), the maximum stretches to <c>1.25 x length / MaxParts</c>.
/// </para>
/// </summary>
public static class SubShotPlanner
{
    /// <summary>Sample rate of the content signal pass (bounded by <c>MaxVisualSampleFrames</c> for long sources).</summary>
    public const double SampleFps = 6.0;

    /// <summary>Lowest score that makes a moment a cut candidate.</summary>
    public const double MinScore = 0.25;

    /// <summary>Cost of each even fill cut: prefers fewer non-content boundaries.</summary>
    public const double FillCost = 0.05;

    /// <summary>Seconds either side a pause or a change is compared against.</summary>
    public const double ContextSec = 1.0;

    /// <summary>Motion (luma change per second) under which the surroundings count as still.</summary>
    public const double MotionFloor = 0.02;

    /// <summary>Layout change under which a peak is noise.</summary>
    public const double ChangeFloor = 0.01;

    /// <summary>A silence this long scores 1.</summary>
    public const double FullAudioDipSec = 1.0;

    /// <summary>Most candidates the selection considers per shot (strongest first, then earliest).</summary>
    public const int MaxCandidates = 400;

    private const double Epsilon = 1e-9;

    /// <summary>The longest part a shot of <paramref name="lengthSec"/> may have under <paramref name="bounds"/>.</summary>
    public static double EffectiveMaxSec(double lengthSec, SubShotBounds bounds) =>
        Math.Ceiling(lengthSec / bounds.MaxSec - Epsilon) > bounds.MaxParts
            ? Math.Max(bounds.MaxSec, 1.25 * lengthSec / bounds.MaxParts)
            : bounds.MaxSec;

    /// <summary>Plans the cuts of the shot <c>[startSec, endSec]</c>. See the class remarks.</summary>
    public static SubShotPlan Plan(
        double startSec,
        double endSec,
        SubShotSignal signal,
        IReadOnlyList<(double StartSec, double EndSec)> silences,
        SubShotBounds bounds)
    {
        double length = endSec - startSec;
        if (!(length >= 2 * bounds.MinSec - Epsilon))
            return SubShotPlan.Unsplit;

        double max = EffectiveMaxSec(length, bounds);
        List<Candidate> candidates = Candidates(startSec, endSec, signal, silences, bounds.MinSec);

        // Nodes: 0 = shot start, 1..K = candidates in time order, K+1 = shot end.
        int k = candidates.Count;
        var times = new double[k + 2];
        times[0] = startSec;
        times[k + 1] = endSec;
        for (int i = 0; i < k; i++)
            times[i + 1] = candidates[i].Time;

        int cap = bounds.MaxParts;
        var best = new double[k + 2, cap + 1];
        var from = new (int Node, int Parts)[k + 2, cap + 1];
        for (int i = 0; i < k + 2; i++)
            for (int p = 0; p <= cap; p++)
                best[i, p] = double.NegativeInfinity;
        best[0, 0] = 0;

        for (int i = 1; i < k + 2; i++)
        {
            double gain = i <= k ? candidates[i - 1].Score - MinScore : 0;
            for (int j = 0; j < i; j++)
            {
                double gap = times[i] - times[j];
                if (gap < bounds.MinSec - Epsilon)
                    continue;
                int parts = PartsFor(gap, max);
                for (int p = 0; p + parts <= cap; p++)
                {
                    if (double.IsNegativeInfinity(best[j, p]))
                        continue;
                    double value = best[j, p] + gain - FillCost * (parts - 1);
                    if (value > best[i, p + parts] + Epsilon)
                    {
                        best[i, p + parts] = value;
                        from[i, p + parts] = (j, p);
                    }
                }
            }
        }

        int end = k + 1, total = -1;
        for (int p = 1; p <= cap; p++)
        {
            if (!double.IsNegativeInfinity(best[end, p]) && (total < 0 || best[end, p] > best[end, total] + Epsilon))
                total = p;
        }

        if (total < 0)
            return SubShotPlan.Unsplit; // unreachable: start -> end always fits (see EffectiveMaxSec)

        var path = new List<int>();
        for (int node = end, p = total; node != 0;)
        {
            path.Add(node);
            (node, p) = from[node, p];
        }

        path.Add(0);
        path.Reverse();

        var cuts = new List<double>();
        var reasons = new List<SubShotCutReason>();
        bool content = false;
        for (int n = 1; n < path.Count; n++)
        {
            double a = times[path[n - 1]], b = times[path[n]];
            int parts = PartsFor(b - a, max);
            for (int m = 1; m < parts; m++)
            {
                cuts.Add(a + (b - a) * m / parts);
                reasons.Add(SubShotCutReason.Even);
            }

            if (path[n] != end)
            {
                cuts.Add(b);
                reasons.Add(candidates[path[n] - 1].Reason);
                content = true;
            }
        }

        SubShotSplitMethod method = content ? SubShotSplitMethod.Content
            : cuts.Count > 0 ? SubShotSplitMethod.EvenFallback
            : SubShotSplitMethod.Unsplit;
        return new SubShotPlan(cuts, reasons, method);
    }

    /// <summary>The one word a reason is written as.</summary>
    public static string ToWord(this SubShotCutReason reason) => reason switch
    {
        SubShotCutReason.MotionPause => "motion_pause",
        SubShotCutReason.VisualChange => "visual_change",
        SubShotCutReason.AudioDip => "audio_dip",
        _ => "even"
    };

    /// <summary>The one word a method is written as.</summary>
    public static string ToWord(this SubShotSplitMethod method) => method switch
    {
        SubShotSplitMethod.Content => "content",
        SubShotSplitMethod.EvenFallback => "even_fallback",
        _ => "unsplit"
    };

    private static int PartsFor(double gap, double max) => Math.Max(1, (int)Math.Ceiling(gap / max - Epsilon));

    private readonly record struct Candidate(double Time, double Score, SubShotCutReason Reason);

    private static List<Candidate> Candidates(
        double startSec,
        double endSec,
        SubShotSignal signal,
        IReadOnlyList<(double StartSec, double EndSec)> silences,
        double minSec)
    {
        double lo = startSec + minSec - Epsilon, hi = endSec - minSec + Epsilon;
        var found = new List<Candidate>();

        // Visual samples strictly inside the shot: a pair straddling the cut into the next shot measures the cut.
        double margin = signal.SampleIntervalSec;
        var inner = new List<int>();
        for (int i = 0; i < signal.Count; i++)
        {
            double t = signal.Times[i];
            if (margin > 0 && t >= startSec + margin - Epsilon && t <= endSec - margin + Epsilon)
                inner.Add(i);
        }

        int n = inner.Count;
        var time = new double[n];
        var motion = new double[n];
        var raw = new double[n];
        var change = new double[n];
        for (int q = 0; q < n; q++)
        {
            time[q] = signal.Times[inner[q]];
            raw[q] = signal.Motion[inner[q]];
            change[q] = signal.Change[inner[q]];
        }

        for (int q = 0; q < n; q++)
        {
            double a = signal.Motion[inner[Math.Max(0, q - 1)]];
            double b = signal.Motion[inner[q]];
            double c = signal.Motion[inner[Math.Min(n - 1, q + 1)]];
            motion[q] = (a + b + c) / 3;
        }

        int plateauEnd = -1;
        for (int q = 1; q < n - 1; q++)
        {
            if (time[q] < lo || time[q] > hi)
                continue;

            double pause = 0;
            double pauseTime = time[q];
            if (q > plateauEnd && motion[q] <= motion[q - 1] && motion[q] <= motion[q + 1])
            {
                // A hold is a run of samples, not one. Its extent is read on the RAW motion (smoothing
                // bleeds a hold's edges into its neighbours unevenly): the contiguous run within a small
                // tolerance of the lowest raw value next to this minimum. The cut goes at the run's
                // centre; the score is the smoothed depth, as for a single-sample valley.
                int seed = q;
                if (raw[q - 1] < raw[seed])
                    seed = q - 1;
                if (raw[q + 1] < raw[seed])
                    seed = q + 1;
                (int first, int last) = Plateau(raw, seed);
                plateauEnd = Math.Max(q, last);

                (double leftMax, double rightMax) = Around(time, motion, Math.Min(first, q), Math.Max(last, q));
                double reference = Math.Min(leftMax, rightMax);
                if (reference >= MotionFloor)
                {
                    pause = Math.Max(0, (reference - motion[q]) / reference);
                    pauseTime = (time[first] + time[last]) / 2;
                }
            }

            double shift = 0;
            if (change[q] >= ChangeFloor && change[q] >= change[q - 1] && change[q] >= change[q + 1])
            {
                (double leftMin, double rightMin) = Around(time, change, q, takeMax: false);
                double baseline = Math.Max(leftMin, rightMin);
                shift = Math.Max(0, (change[q] - baseline) / change[q]);
            }

            double score = Math.Max(pause, shift);
            bool isPause = pause >= shift;
            double at = isPause ? pauseTime : time[q];
            if (score >= MinScore && at >= lo && at <= hi)
                found.Add(new Candidate(at, Math.Min(1, score), isPause ? SubShotCutReason.MotionPause : SubShotCutReason.VisualChange));
        }

        foreach ((double s, double e) in silences)
        {
            double middle = (s + e) / 2, score = Math.Min(1, Math.Max(0, e - s) / FullAudioDipSec);
            if (middle >= lo && middle <= hi && score >= MinScore)
                found.Add(new Candidate(middle, score, SubShotCutReason.AudioDip));
        }

        if (found.Count > MaxCandidates)
            found = [.. found.OrderByDescending(c => c.Score).ThenBy(c => c.Time).Take(MaxCandidates)];

        return [.. found.OrderBy(c => c.Time).ThenBy(c => c.Reason)];
    }

    /// <summary>Share of the local motion range a sample may sit above a hold's minimum and still belong to it.</summary>
    public const double PlateauTolerance = 0.1;

    /// <summary>
    /// The contiguous run of samples around the local minimum <paramref name="q"/> whose motion is within
    /// <see cref="PlateauTolerance"/> of the local range (max of the context second around it minus the
    /// minimum; at least 1e-9) of <c>motion[q]</c>, either way.
    /// </summary>
    private static (int First, int Last) Plateau(double[] motion, int q)
    {
        double rangeMax = motion[q];
        for (int r = Math.Max(0, q - 12); r <= Math.Min(motion.Length - 1, q + 12); r++)
            rangeMax = Math.Max(rangeMax, motion[r]);
        double tolerance = Math.Max(1e-9, PlateauTolerance * (rangeMax - motion[q]));
        int first = q, last = q;
        while (first - 1 >= 0 && Math.Abs(motion[first - 1] - motion[q]) <= tolerance)
            first--;
        while (last + 1 < motion.Length && Math.Abs(motion[last + 1] - motion[q]) <= tolerance)
            last++;
        return (first, last);
    }

    /// <summary>The max of <paramref name="values"/> over the context second before <paramref name="first"/> and after <paramref name="last"/>; a side with no sample gives the run's own edge value.</summary>
    private static (double Left, double Right) Around(double[] time, double[] values, int first, int last)
    {
        double left = double.NaN, right = double.NaN;
        for (int r = first - 1; r >= 0 && time[r] >= time[first] - ContextSec - Epsilon; r--)
            left = double.IsNaN(left) ? values[r] : Math.Max(left, values[r]);
        for (int r = last + 1; r < time.Length && time[r] <= time[last] + ContextSec + Epsilon; r++)
            right = double.IsNaN(right) ? values[r] : Math.Max(right, values[r]);
        return (double.IsNaN(left) ? values[first] : left, double.IsNaN(right) ? values[last] : right);
    }

    /// <summary>The max (or min) of <paramref name="values"/> over the context second before and after sample <paramref name="q"/>; NaN-free, the sample's own value when a side is empty.</summary>
    private static (double Left, double Right) Around(double[] time, double[] values, int q, bool takeMax)
    {
        double left = double.NaN, right = double.NaN;
        for (int r = q - 1; r >= 0 && time[r] >= time[q] - ContextSec - Epsilon; r--)
            left = double.IsNaN(left) ? values[r] : takeMax ? Math.Max(left, values[r]) : Math.Min(left, values[r]);
        for (int r = q + 1; r < time.Length && time[r] <= time[q] + ContextSec + Epsilon; r++)
            right = double.IsNaN(right) ? values[r] : takeMax ? Math.Max(right, values[r]) : Math.Min(right, values[r]);
        return (double.IsNaN(left) ? values[q] : left, double.IsNaN(right) ? values[q] : right);
    }
}
