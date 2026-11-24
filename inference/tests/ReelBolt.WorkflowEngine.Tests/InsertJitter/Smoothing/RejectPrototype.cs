#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ReelBolt.WorkflowEngine.Services.Video;
using FrameQuad = ReelBolt.WorkflowEngine.Services.Video.ChromaQuadTracker.FrameQuad;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Smoothing;

/// <summary>
/// PROTOTYPE COPY of inference/src/ReelBolt.WorkflowEngine/Services/Video/ChromaQuadTracker.cs@4ecffac2f
/// (<c>RejectImplausibleSamples</c>, <c>ResidualAgainstNeighbours</c>, and the private helpers they use). Mode.Production is the copy unchanged
/// (checked against the shipped method); Mode.BothSides changes only WHICH residual a sample is judged by: the smallest of the
/// two-sided line (production), a line through the LEFT neighbours extrapolated to the sample, and a line through the RIGHT neighbours.
/// <para>
/// Mode.Hinge (the proposal) judges a sample by the smaller of the two-sided line residual and its residual against the best CONTINUOUS TWO-SEGMENT
/// (single-kink) path fitted to its neighbours (all eight coordinates share the kink): a whip start/stop is one kink and is explained exactly, while a spike or a
/// short excursion needs two or more kinks within the window and is not. Mode.BothSides and Mode.Chord are earlier attempts kept for the record (both
/// fix the whip but accept the two-frame occluder excursion of the <c>occlusion</c> scene).
/// <para>
/// Mode.Chord judges a sample by the smaller of the two-sided line residual and its distance from the CHORD joining its nearest
/// accepted neighbours on either side (linear in frame index): a continuous path with a velocity kink (a whip start) lies on its chord, a spike
/// or the first frame of a jump-and-hold does not (the next accepted frame is still on the old plate).
/// <para>
/// Why: a genuine fast move has a velocity kink (whip start / stop). A single line through both sides cannot bend there, so the
/// kink frame looks like an outlier although one of the two sides still predicts it perfectly. A real outlier (a reflection
/// streak) is far from BOTH one-sided predictions. The reported noise is still the two-sided residual's median, so the downstream
/// Smooth gate sees the same number as today.
/// </para>
/// </summary>
internal static class RejectPrototype
{
    public enum Mode { Production, BothSides, Chord, Hinge }

    public static (List<FrameQuad> Run, double Noise, int Rejected, List<int> RejectedFrames) Run(List<FrameQuad> run, double fps, int width, int height, ChromaQuadTracker.Options options, Mode mode)
    {
        double floor = Math.Max(0, options.OutlierFloorFraction);
        double speedFactor = Math.Max(0, options.OutlierSpeedFactor);
        if (run.Count < 4 || width <= 0 || height <= 0)
            return (run, 0, 0, []);
        bool rejectionEnabled = floor > 0 || speedFactor > 0;

        int half = Math.Max(2, (int)Math.Round(ChromaQuadTracker.OutlierHalfWindowSeconds * Math.Max(fps, 1)));
        var detected = new List<int>();
        for (int i = 0; i < run.Count; i++)
        {
            if (!run[i].Interpolated)
                detected.Add(i);
        }

        if (detected.Count < 4)
            return (run, 0, 0, []);

        var rejected = new bool[run.Count];
        int maxRejections = (int)Math.Floor(Math.Clamp(options.MaxOutlierFraction, 0, 1) * detected.Count);
        var residuals = new double[run.Count];

        for (int pass = 0; ; pass++)
        {
            int worst = -1;
            double worstRatio = 1;
            foreach (int i in detected)
            {
                residuals[i] = 0;
                if (rejected[i])
                    continue;

                (double residual, double judged, double speed) = Residual(run, detected, rejected, i, half, width, height, mode);
                if (double.IsNaN(residual))
                    continue;

                residuals[i] = residual;
                double tolerance = Math.Max(floor * Diagonal(run[i], width, height), speedFactor * speed);
                if (tolerance <= 0)
                    continue;

                double ratio = judged / tolerance;
                if (ratio > worstRatio)
                {
                    worstRatio = ratio;
                    worst = i;
                }
            }

            if (worst < 0 || pass >= maxRejections || !rejectionEnabled)
                break;
            rejected[worst] = true;
        }

        var accepted = detected.Where(i => !rejected[i]).ToList();
        if (accepted.Count == 0)
            return (run, 0, 0, []);

        double noise = ChromaQuadTracker.Median(accepted.Select(i => residuals[i]).ToList());
        if (accepted.Count == detected.Count)
            return (run, noise, 0, []);

        var cleaned = new List<FrameQuad>(run.Count);
        for (int a = 0; a + 1 < accepted.Count; a++)
        {
            int from = accepted[a], to = accepted[a + 1];
            cleaned.Add(run[from]);
            int span = run[to].FrameIndex - run[from].FrameIndex;
            for (int j = from + 1; j < to; j++)
                cleaned.Add(Lerp(run[from], run[to], run[j].FrameIndex, (run[j].FrameIndex - run[from].FrameIndex) / (double)span));
        }

        cleaned.Add(run[accepted[^1]]);
        return (cleaned, noise, detected.Count - accepted.Count, detected.Where(i => rejected[i]).Select(i => run[i].FrameIndex).ToList());
    }

    /// <summary>(two-sided residual as production, the residual the decision uses, neighbours' median per-frame travel).</summary>
    private static (double Residual, double Judged, double Speed) Residual(
        List<FrameQuad> run, List<int> detected, bool[] rejected, int i, int half, int width, int height, Mode mode)
    {
        int frame = run[i].FrameIndex;
        var idx = new List<int>();
        foreach (int j in detected)
        {
            if (j == i || rejected[j])
                continue;
            int dt = run[j].FrameIndex - frame;
            if (dt < -half || dt > half)
                continue;
            idx.Add(j);
        }

        if (idx.Count < 3)
            return (double.NaN, double.NaN, 0);

        double residual = LineResidual(run, idx, i, width, height);
        double judged = residual;
        if (mode == Mode.Hinge && idx.Count >= 5)
            judged = Math.Min(judged, HingeResidual(run, idx, i, width, height));

        if (mode == Mode.Chord)
        {
            int prev = -1, next = -1;
            for (int j = i - 1; j >= 0 && prev < 0; j--)
            {
                if (!run[j].Interpolated && !rejected[j])
                    prev = j;
            }

            for (int j = i + 1; j < run.Count && next < 0; j++)
            {
                if (!run[j].Interpolated && !rejected[j])
                    next = j;
            }

            if (prev >= 0 && next >= 0)
                judged = Math.Min(judged, ChordResidual(run[prev], run[i], run[next], width, height));
        }

        if (mode == Mode.BothSides)
        {
            var left = idx.Where(j => run[j].FrameIndex < frame).ToList();
            var right = idx.Where(j => run[j].FrameIndex > frame).ToList();
            if (left.Count >= 2)
                judged = Math.Min(judged, LineResidual(run, left, i, width, height));
            if (right.Count >= 2)
                judged = Math.Min(judged, LineResidual(run, right, i, width, height));
        }

        var steps = new List<double>(idx.Count);
        for (int k = 0; k + 1 < idx.Count; k++)
        {
            int a = idx[k], b = idx[k + 1];
            int dt = run[b].FrameIndex - run[a].FrameIndex;
            if (dt <= 0)
                continue;
            steps.Add(MaxCornerDistance(run[a], run[b], width, height) / dt);
        }

        return (residual, judged, steps.Count == 0 ? 0 : ChromaQuadTracker.Median(steps));
    }

    /// <summary>
    /// Furthest corner distance of run[i] from the best continuous two-segment path through <paramref name="idx"/> (sample itself excluded):
    /// x(t) = a + b t + c max(0, t - knot) fitted by least squares for every candidate knot (the neighbours' own frames and the sample's frame),
    /// the knot with the smallest total squared residual over the eight coordinates winning; evaluated at the sample's frame (t = 0).
    /// </summary>
    private static double HingeResidual(List<FrameQuad> run, List<int> idx, int i, int width, int height)
    {
        int frame = run[i].FrameIndex;
        var knots = new SortedSet<double> { 0 };
        foreach (int j in idx)
            knots.Add(run[j].FrameIndex - frame);
        double minT = run[idx[0]].FrameIndex - frame, maxT = run[idx[^1]].FrameIndex - frame;
        double bestSse = double.PositiveInfinity;
        var bestPred = new double[8];
        var pred = new double[8];
        foreach (double knot in knots)
        {
            if (knot <= minT || knot >= maxT)
                continue;
            double s11 = 0, s12 = 0, s13 = 0, s22 = 0, s23 = 0, s33 = 0, s1 = 0;
            foreach (int j in idx)
            {
                double t = run[j].FrameIndex - frame, h = Math.Max(0, t - knot);
                s11 += 1; s12 += t; s13 += h; s22 += t * t; s23 += t * h; s33 += h * h;
            }

            double det = s11 * (s22 * s33 - s23 * s23) - s12 * (s12 * s33 - s23 * s13) + s13 * (s12 * s23 - s22 * s13);
            if (Math.Abs(det) < 1e-9)
                continue;
            double sse = 0;
            for (int c = 0; c < 8; c++)
            {
                double b1 = 0, b2 = 0, b3 = 0;
                foreach (int j in idx)
                {
                    double t = run[j].FrameIndex - frame, h = Math.Max(0, t - knot), y = Coordinate(run[j], c, width, height);
                    b1 += y; b2 += t * y; b3 += h * y;
                }

                // Cramer's rule for (a, b, cc).
                double a = (b1 * (s22 * s33 - s23 * s23) - s12 * (b2 * s33 - s23 * b3) + s13 * (b2 * s23 - s22 * b3)) / det;
                double bb = (s11 * (b2 * s33 - s23 * b3) - b1 * (s12 * s33 - s23 * s13) + s13 * (s12 * b3 - b2 * s13)) / det;
                double cc = (s11 * (s22 * b3 - b2 * s23) - s12 * (s12 * b3 - b2 * s13) + b1 * (s12 * s23 - s22 * s13)) / det;
                foreach (int j in idx)
                {
                    double t = run[j].FrameIndex - frame, h = Math.Max(0, t - knot);
                    double r = Coordinate(run[j], c, width, height) - (a + bb * t + cc * h);
                    sse += r * r;
                }

                pred[c] = a + cc * Math.Max(0, -knot); // value at t = 0
            }

            if (sse < bestSse)
            {
                bestSse = sse;
                Array.Copy(pred, bestPred, 8);
            }
        }

        if (double.IsPositiveInfinity(bestSse))
            return double.PositiveInfinity;
        double worst = 0;
        for (int k = 0; k < 4; k++)
        {
            double dx = Coordinate(run[i], 2 * k, width, height) - bestPred[2 * k];
            double dy = Coordinate(run[i], 2 * k + 1, width, height) - bestPred[2 * k + 1];
            worst = Math.Max(worst, Math.Sqrt(dx * dx + dy * dy));
        }

        return worst;
    }

    /// <summary>Furthest corner distance of <paramref name="mid"/> from the straight line (in frame index) between <paramref name="a"/> and <paramref name="b"/>.</summary>
    private static double ChordResidual(FrameQuad a, FrameQuad mid, FrameQuad b, int width, int height)
    {
        double span = b.FrameIndex - a.FrameIndex;
        if (span <= 0)
            return double.PositiveInfinity;
        double u = (mid.FrameIndex - a.FrameIndex) / span, worst = 0;
        for (int k = 0; k < 4; k++)
        {
            double px = Coordinate(a, 2 * k, width, height) + (Coordinate(b, 2 * k, width, height) - Coordinate(a, 2 * k, width, height)) * u;
            double py = Coordinate(a, 2 * k + 1, width, height) + (Coordinate(b, 2 * k + 1, width, height) - Coordinate(a, 2 * k + 1, width, height)) * u;
            double dx = Coordinate(mid, 2 * k, width, height) - px, dy = Coordinate(mid, 2 * k + 1, width, height) - py;
            worst = Math.Max(worst, Math.Sqrt(dx * dx + dy * dy));
        }

        return worst;
    }

    /// <summary>Furthest corner distance of run[i] from a least-squares line through <paramref name="idx"/> (the intercept at the sample's frame).</summary>
    private static double LineResidual(List<FrameQuad> run, List<int> idx, int i, int width, int height)
    {
        int frame = run[i].FrameIndex;
        double n = idx.Count, st = 0, stt = 0;
        foreach (int j in idx)
        {
            double t = run[j].FrameIndex - frame;
            st += t;
            stt += t * t;
        }

        double det = n * stt - st * st;
        double residual = 0;
        Span<double> coords = stackalloc double[8];
        for (int c = 0; c < 8; c++)
        {
            double sy = 0, sty = 0;
            foreach (int j in idx)
            {
                double t = run[j].FrameIndex - frame;
                double y = Coordinate(run[j], c, width, height);
                sy += y;
                sty += t * y;
            }

            coords[c] = Math.Abs(det) < 1e-12 ? sy / n : (stt * sy - st * sty) / det;
        }

        for (int k = 0; k < 4; k++)
        {
            double dx = Coordinate(run[i], 2 * k, width, height) - coords[2 * k];
            double dy = Coordinate(run[i], 2 * k + 1, width, height) - coords[2 * k + 1];
            residual = Math.Max(residual, Math.Sqrt(dx * dx + dy * dy));
        }

        return residual;
    }

    private static double Coordinate(FrameQuad q, int c, int width, int height) => c switch
    {
        0 => q.X0 * width,
        1 => q.Y0 * height,
        2 => q.X1 * width,
        3 => q.Y1 * height,
        4 => q.X2 * width,
        5 => q.Y2 * height,
        6 => q.X3 * width,
        _ => q.Y3 * height
    };

    private static double MaxCornerDistance(FrameQuad a, FrameQuad b, int width, int height)
    {
        double worst = 0;
        for (int k = 0; k < 4; k++)
        {
            double dx = Coordinate(a, 2 * k, width, height) - Coordinate(b, 2 * k, width, height);
            double dy = Coordinate(a, 2 * k + 1, width, height) - Coordinate(b, 2 * k + 1, width, height);
            worst = Math.Max(worst, Math.Sqrt(dx * dx + dy * dy));
        }

        return worst;
    }

    private static double Diagonal(FrameQuad q, int width, int height) => Math.Max(
        Math.Sqrt(Math.Pow((q.X3 - q.X0) * width, 2) + Math.Pow((q.Y3 - q.Y0) * height, 2)),
        Math.Sqrt(Math.Pow((q.X2 - q.X1) * width, 2) + Math.Pow((q.Y2 - q.Y1) * height, 2)));

    private static FrameQuad Lerp(FrameQuad a, FrameQuad b, int frameIndex, double t) => new(
        frameIndex,
        a.X0 + (b.X0 - a.X0) * t, a.Y0 + (b.Y0 - a.Y0) * t,
        a.X1 + (b.X1 - a.X1) * t, a.Y1 + (b.Y1 - a.Y1) * t,
        a.X2 + (b.X2 - a.X2) * t, a.Y2 + (b.Y2 - a.Y2) * t,
        a.X3 + (b.X3 - a.X3) * t, a.Y3 + (b.Y3 - a.Y3) * t,
        a.AreaRatio + (b.AreaRatio - a.AreaRatio) * t,
        a.FillScore + (b.FillScore - a.FillScore) * t,
        a.ChromaMargin + (b.ChromaMargin - a.ChromaMargin) * t,
        a.Bows.Lerp(b.Bows, t),
        (a.Surface.X0 + (b.Surface.X0 - a.Surface.X0) * t, a.Surface.Y0 + (b.Surface.Y0 - a.Surface.Y0) * t,
         a.Surface.X1 + (b.Surface.X1 - a.Surface.X1) * t, a.Surface.Y1 + (b.Surface.Y1 - a.Surface.Y1) * t,
         a.Surface.X2 + (b.Surface.X2 - a.Surface.X2) * t, a.Surface.Y2 + (b.Surface.Y2 - a.Surface.Y2) * t,
         a.Surface.X3 + (b.Surface.X3 - a.Surface.X3) * t, a.Surface.Y3 + (b.Surface.Y3 - a.Surface.Y3) * t),
        EdgeContrast: a.EdgeContrast + (b.EdgeContrast - a.EdgeContrast) * t,
        Interpolated: true);
}
