using System;
using System.Collections.Generic;
using System.Linq;
using static ReelBolt.WorkflowEngine.Services.Video.PlatePoseMath;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Tracks a chroma plate as what it is — one RIGID surface seen by one camera — and solves, per
/// frame, the 6-DoF pose that puts it where the visible stretches of its edges say it is. See
/// docs/video-editing.md "Occluded corners and edges".
///
/// <para>
/// <b>Why.</b> The per-frame detector measures the plate's four corners from the largest green
/// component of that frame alone. Anything in front of the plate changes that component: a hand over
/// a corner moves the corner onto the hand, a body along an edge moves the edge onto the body, a tail
/// across the plate cuts the component in two and the smaller part is dropped. The corner then
/// follows the occluder, the content shrinks and slides with it, and raw plate is left uncovered.
/// None of that is a property of the plate. The plate did not change shape; part of it stopped being
/// visible.
/// </para>
///
/// <para>
/// <b>How.</b> A corner is never measured here. Each edge is located from whatever part of it is in
/// view: probes across the predicted edge find the OUTERMOST plate boundary (so a tail crossing the
/// plate does not end the plate at the tail), and the pose is the one whose projected edges pass
/// through those crossings. Three things make that robust:
/// </para>
/// <list type="bullet">
/// <item><b>Rigidity.</b> The plate's shape (aspect, and curvature for a bent one) and the camera's
/// focal length are estimated once per track. A pose has six degrees of freedom, so three edges fix
/// it and the fourth is known without being seen; a covered corner needs only part of each
/// neighbouring edge.</item>
/// <item><b>One-sidedness.</b> Something in front of a plate can only REMOVE plate. A crossing inside
/// the model's edge is an occluder's outline and is discounted quickly; the shape itself is learned
/// from the least covered frames and then grown to the largest plate any frame shows, never the
/// consensus (which a long occlusion owns).</item>
/// <item><b>Holding what is not observed.</b> With fewer than three edges in view the pose is not
/// determined, so the plate keeps its last well-observed shape and may only slide and turn in the
/// image.</item>
/// </list>
///
/// <para>
/// Pure and deterministic; never throws. A run it cannot model (too few detections, a shape the
/// rigid model does not explain) is returned exactly as it came.
/// </para>
/// </summary>
internal static class ChromaPlatePoseTracker
{
    /// <summary>Probes per edge.</summary>
    internal const int Stations = 48;

    /// <summary>Fraction of an edge, from either end, left without probes: the corner's own rounding lives there.</summary>
    private const double StationMargin = 0.03;

    /// <summary>Spacing of the samples along a probe, in grid pixels.</summary>
    private const double ProbeStep = 0.5;

    /// <summary>Share of an edge's stations that must sit on the model's edge for the edge to count as in view.</summary>
    internal const double SupportedEdge = 0.3;

    /// <summary>How far a crossing may sit from the model's edge and still support it, in grid pixels.</summary>
    private const double SupportDistance = 1.0;

    /// <summary>
    /// The solve's three passes: how far the probes reach either side of the predicted edge, and the
    /// loss's inward and outward scales, all in grid pixels. Wide first, to follow motion; then
    /// narrow, and much narrower inward than outward — an occluder's outline is always inside.
    /// </summary>
    private static readonly (double Reach, double Inward, double Outward)[] Passes = [(6.0, 4.0, 6.0), (4.0, 2.0, 4.0), (3.0, 1.25, 3.0)];

    /// <summary>Median inward offset of an edge's crossings, under the pose its three neighbours give, beyond which the edge is an occluder's outline.</summary>
    private const double InwardTolerance = 0.75;

    /// <summary>Pooled rms (clipped, grid pixels) of three edges' crossings under which they agree on a pose without the fourth.</summary>
    private const double LeftOutFit = 0.6;

    /// <summary>Focal lengths tried, as multiples of the grid's longer side. 1.0 is the default a still plate keeps.</summary>
    private static readonly double[] FocalCandidates = [0.5, 0.6, 0.72, 0.86, 1.0, 1.2, 1.45, 1.8, 2.3, 3.0, 4.0, 6.0];

    /// <summary>Frames with no edge in view after which a track that has left its run's own span ends.</summary>
    private const int LostFrames = 3;

    /// <summary>Flat-model cost (grid pixels) under which a plate is flat without asking the cylinder.</summary>
    private const double FlatEnoughCost = 0.4;

    /// <summary>
    /// The least bend (arc over radius) that is called a bend: a sag of about 2% of the width. A
    /// screen with rounded corners, or a lens that bows straight lines a little, fits a weaker
    /// cylinder slightly better than a plane; it is still a plane, and a track that says otherwise
    /// is composited through the mesh warp for nothing. A 4000R television is about 0.3.
    /// </summary>
    private const double MinBend = 0.15;

    /// <summary>
    /// The worst edge fit (grid px) a bend may leave and still be called the explanation. A real
    /// bent plate fits to a few tenths; a flat one that something else disturbs (a head along the
    /// bottom edge, tracking marks, a plate the frame cuts) fits a strong cylinder a little better
    /// than a plane and neither well, and one such clip came back as a wall television bent
    /// through 86 degrees.
    /// </summary>
    private const double MaxBentCost = 0.8;

    /// <summary>Model growth, in plate heights, below which the plate is taken as fully seen.</summary>
    private const double MinGrowth = 0.02;

    internal sealed record Diagnostics(double Focal, double Aspect, double Kappa, int GrowthRounds, int HeldFrames);

    /// <summary>One frame's solved plate: the pose, and per edge the share of stations that sit on the model's edge.</summary>
    internal readonly record struct Solved(double[] Pose, double[] Support, double Rms)
    {
        public int EdgesInView => Support.Count(s => s >= SupportedEdge);
    }

    /// <summary>A pose with what its evidence says about it: per-edge support, and the crossings' rms distance from the model's edges (clipped at 2 px).</summary>
    private static Solved Describe(in Camera cam, Plate plate, double[] pose, EdgeEvidence[] evidence)
    {
        double[][] r = EdgeResiduals(cam, pose, plate, evidence);
        var support = new double[4];
        double sum = 0;
        int n = 0;
        for (int k = 0; k < 4; k++)
        {
            int on = 0;
            foreach (double v in r[k])
            {
                double a = Math.Abs(v);
                if (a < SupportDistance)
                    on++;
                double c = Math.Min(a, 2.0);
                sum += c * c;
                n++;
            }

            support[k] = on / (double)Stations;
        }

        return new Solved(pose, support, n == 0 ? 0 : Math.Sqrt(sum / n));
    }

    /// <summary>The plate boundary found along one predicted edge.</summary>
    private readonly record struct EdgeEvidence((double X, double Y)[] Points, int Covered, int Beyond);

    /// <summary>Dominance fields of the frames being worked on, computed on demand and kept for a few frames only.</summary>
    internal sealed class FrameSource
    {
        private readonly byte[] _pixels;
        private readonly string _color;
        private readonly Dictionary<int, float[]> _cache = [];
        private readonly Queue<int> _order = new();
        private const int Capacity = 12;

        public FrameSource(byte[] pixels, int width, int height, string color)
        {
            _pixels = pixels;
            Width = width;
            Height = height;
            _color = color;
            FrameCount = pixels.Length / Math.Max(1, width * height * 3);
        }

        public int Width { get; }
        public int Height { get; }
        public int FrameCount { get; }

        /// <summary>Single-frame pose solves spent on this clip so far, over every run (see <see cref="WorkBudget"/>).</summary>
        public long FramesSolved { get; set; }

        /// <summary>
        /// How many single-frame pose solves one clip may spend: a deterministic stand-in for a time
        /// limit. A clean clip costs one per frame; a repair with a grown model about four. A clip
        /// whose detections come in many short fragments would otherwise pay that for each of them.
        /// </summary>
        public long WorkBudget => 8L * FrameCount + 400;

        public bool OverBudget => FramesSolved > WorkBudget;

        public float[] Dominance(int frame)
        {
            if (_cache.TryGetValue(frame, out float[]? hit))
                return hit;
            int n = Width * Height;
            var d = new float[n];
            int offset = frame * n * 3;
            for (int p = 0, b = offset; p < n; p++, b += 3)
                d[p] = Math.Max(0, ChromaEdgeLineFitter.Dominance(_color, _pixels[b], _pixels[b + 1], _pixels[b + 2]));
            if (_order.Count >= Capacity)
                _cache.Remove(_order.Dequeue());
            _cache[frame] = d;
            _order.Enqueue(frame);
            return d;
        }
    }

    // ------------------------------------------------------------------ evidence

    private static double Sample(float[] dom, int width, int height, double x, double y)
    {
        x = Math.Clamp(x, 0, width - 1.001);
        y = Math.Clamp(y, 0, height - 1.001);
        int x0 = (int)x, y0 = (int)y;
        double fx = x - x0, fy = y - y0;
        int i = y0 * width + x0;
        return dom[i] * (1 - fx) * (1 - fy) + dom[i + 1] * fx * (1 - fy) + dom[i + width] * (1 - fx) * fy + dom[i + width + 1] * fx * fy;
    }

    private static (double X, double Y) Centre((double X, double Y)[][] polys)
    {
        double x = 0, y = 0;
        foreach ((double X, double Y)[] p in polys)
        {
            x += p[0].X;
            y += p[0].Y;
        }

        return (x / polys.Length, y / polys.Length);
    }

    /// <summary><see cref="Stations"/> points at even arc fractions along a polyline, with unit normals pointing away from <paramref name="centre"/>.</summary>
    private static double PlaceStations(
        (double X, double Y)[] poly, (double X, double Y) centre, Span<(double X, double Y)> at, Span<(double X, double Y)> normal)
    {
        int segments = poly.Length - 1;
        Span<double> cum = stackalloc double[segments + 1];
        for (int k = 0; k < segments; k++)
        {
            double dx = poly[k + 1].X - poly[k].X, dy = poly[k + 1].Y - poly[k].Y;
            cum[k + 1] = cum[k] + Math.Sqrt(dx * dx + dy * dy);
        }

        double total = cum[segments];
        int seg = 0;
        for (int s = 0; s < Stations; s++)
        {
            double d = (StationMargin + (1 - 2 * StationMargin) * s / (Stations - 1)) * total;
            while (seg < segments - 1 && cum[seg + 1] < d)
                seg++;
            double len = Math.Max(cum[seg + 1] - cum[seg], 1e-9);
            double ux = (poly[seg + 1].X - poly[seg].X) / len, uy = (poly[seg + 1].Y - poly[seg].Y) / len;
            double bx = poly[seg].X + ux * (d - cum[seg]), by = poly[seg].Y + uy * (d - cum[seg]);
            double nx = uy, ny = -ux;
            if ((centre.X - bx) * nx + (centre.Y - by) * ny > 0)
            {
                nx = -nx;
                ny = -ny;
            }

            at[s] = (bx, by);
            normal[s] = (nx, ny);
        }

        return total;
    }

    /// <summary>
    /// Along probes across each predicted edge, the OUTERMOST place the plate ends: scanning from
    /// outside in, the first three plate samples in a row after a non-plate one. Outermost, because
    /// the plate's own boundary is what is wanted and something crossing the plate further in is not
    /// it. A station that sees plate at the probe's outer end is "beyond" (the prediction is inside
    /// the plate there, or green scenery adjoins it); one that sees none at all is "covered".
    /// </summary>
    private static EdgeEvidence[] Gather(float[] dom, int width, int height, (double X, double Y)[][] polys, double reach, double threshold)
    {
        (double X, double Y) centre = Centre(polys);
        int steps = (int)Math.Ceiling(2 * reach / ProbeStep) + 1;
        var evidence = new EdgeEvidence[4];
        Span<(double X, double Y)> at = stackalloc (double X, double Y)[Stations];
        Span<(double X, double Y)> normal = stackalloc (double X, double Y)[Stations];
        Span<double> values = steps <= 128 ? stackalloc double[steps] : new double[steps];
        for (int k = 0; k < 4; k++)
        {
            double total = PlaceStations(polys[k], centre, at, normal);
            if (total < 2)
            {
                evidence[k] = new EdgeEvidence([], Stations, 0);
                continue;
            }

            var points = new List<(double X, double Y)>(Stations);
            int covered = 0, beyond = 0;
            for (int s = 0; s < Stations; s++)
            {
                for (int j = 0; j < steps; j++)
                {
                    double off = reach - ProbeStep * j;
                    values[j] = Sample(dom, width, height, at[s].X + off * normal[s].X, at[s].Y + off * normal[s].Y);
                }

                if (values[0] >= threshold && values[1] >= threshold)
                {
                    beyond++;
                    continue;
                }

                int hit = -1;
                for (int j = 1; j < steps - 2; j++)
                {
                    if (values[j] >= threshold && values[j + 1] >= threshold && values[j + 2] >= threshold && values[j - 1] < threshold)
                    {
                        hit = j;
                        break;
                    }
                }

                if (hit < 0)
                {
                    covered++;
                    continue;
                }

                double v0 = values[hit - 1], v1 = values[hit];
                double frac = (threshold - v0) / Math.Max(1e-6, v1 - v0);
                double offset = reach - ProbeStep * (hit - 1) - frac * ProbeStep;
                points.Add((at[s].X + offset * normal[s].X, at[s].Y + offset * normal[s].Y));
            }

            evidence[k] = new EdgeEvidence(points.ToArray(), covered, beyond);
        }

        return evidence;
    }

    /// <summary>Per edge, the signed distance of each crossing from the model's edge (positive = outside the model's plate).</summary>
    private static double[][] EdgeResiduals(in Camera cam, ReadOnlySpan<double> pose, Plate plate, EdgeEvidence[] evidence)
    {
        (double X, double Y)[][] polys = ProjectEdges(cam, pose, plate);
        (double X, double Y) centre = Centre(polys);
        var r = new double[4][];
        for (int k = 0; k < 4; k++)
        {
            (double X, double Y)[] pts = evidence[k].Points;
            r[k] = new double[pts.Length];
            for (int i = 0; i < pts.Length; i++)
                r[k][i] = SignedDistance(pts[i], polys[k], centre);
        }

        return r;
    }

    private static double[] Support(in Camera cam, ReadOnlySpan<double> pose, Plate plate, EdgeEvidence[] evidence)
    {
        double[][] r = EdgeResiduals(cam, pose, plate, evidence);
        var s = new double[4];
        for (int k = 0; k < 4; k++)
            s[k] = r[k].Count(v => Math.Abs(v) < SupportDistance) / (double)Stations;
        return s;
    }

    /// <summary>Per edge: the crossings' median signed residual, their rms clipped at 2 px, and how many there are.</summary>
    private static (double Median, double Rms, int Count)[] EdgeStats(in Camera cam, ReadOnlySpan<double> pose, Plate plate, EdgeEvidence[] evidence)
    {
        double[][] r = EdgeResiduals(cam, pose, plate, evidence);
        var stats = new (double, double, int)[4];
        for (int k = 0; k < 4; k++)
        {
            if (r[k].Length == 0)
            {
                stats[k] = (0, 0, 0);
                continue;
            }

            double[] sorted = [.. r[k]];
            Array.Sort(sorted);
            double median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
            double sum = 0;
            foreach (double v in r[k])
            {
                double c = Math.Min(Math.Abs(v), 2.0);
                sum += c * c;
            }

            stats[k] = (median, Math.Sqrt(sum / r[k].Length), r[k].Length);
        }

        return stats;
    }

    // ------------------------------------------------------------------ solve

    /// <summary>
    /// Levenberg-Marquardt on a robust cost: <paramref name="evaluate"/> fills the evidence residuals
    /// (weighed by <see cref="Rho"/>) and the prior residuals (plain squares). A step is taken only
    /// when the ROBUST cost falls, not the reweighted one, so the minimiser cannot improve by
    /// declaring evidence an outlier.
    /// </summary>
    private static double[] Minimise(
        double[] start, Func<double[], (double[] Evidence, double[] Prior)> evaluate, double inward, double outward, int iterations = 8)
    {
        int n = start.Length;
        double[] x = [.. start];
        (double[] r, double[] p) = evaluate(x);
        double mu = 1e-3;
        var a = new double[n, n + 1];
        var step = new double[n];
        for (int it = 0; it < iterations; it++)
        {
            int rows = r.Length + p.Length;
            if (rows < n)
                break;
            var jac = new double[rows, n];
            for (int k = 0; k < n; k++)
            {
                double[] dx = [.. x];
                const double eps = 1e-6;
                dx[k] += eps;
                (double[] r2, double[] p2) = evaluate(dx);
                if (r2.Length != r.Length)
                    return x;
                for (int i = 0; i < r.Length; i++)
                    jac[i, k] = (r2[i] - r[i]) / eps;
                for (int i = 0; i < p.Length; i++)
                    jac[r.Length + i, k] = (p2[i] - p[i]) / eps;
            }

            var jtj = new double[n, n];
            var jtr = new double[n];
            double cost = 0;
            for (int i = 0; i < rows; i++)
            {
                double res = i < r.Length ? r[i] : p[i - r.Length];
                double w = i < r.Length ? Weight(res, inward, outward) : 1;
                cost += i < r.Length ? Rho(res, inward, outward) : 0.5 * res * res;
                if (w == 0)
                    continue;
                for (int c1 = 0; c1 < n; c1++)
                {
                    double j1 = jac[i, c1] * w;
                    jtr[c1] += j1 * res;
                    for (int c2 = c1; c2 < n; c2++)
                        jtj[c1, c2] += j1 * jac[i, c2];
                }
            }

            for (int c1 = 0; c1 < n; c1++)
            {
                for (int c2 = 0; c2 < c1; c2++)
                    jtj[c1, c2] = jtj[c2, c1];
            }

            bool improved = false;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                for (int c1 = 0; c1 < n; c1++)
                {
                    for (int c2 = 0; c2 < n; c2++)
                        a[c1, c2] = jtj[c1, c2];
                    a[c1, c1] += mu * (jtj[c1, c1] + 1e-9);
                    a[c1, n] = -jtr[c1];
                }

                if (!SolveInPlace(a, n, step))
                {
                    mu *= 10;
                    continue;
                }

                var candidate = new double[n];
                for (int k = 0; k < n; k++)
                    candidate[k] = x[k] + step[k];
                (double[] r2, double[] p2) = evaluate(candidate);
                double cost2 = 0;
                foreach (double v in r2)
                    cost2 += Rho(v, inward, outward);
                foreach (double v in p2)
                    cost2 += 0.5 * v * v;
                if (r2.Length == r.Length && cost2 < cost)
                {
                    x = candidate;
                    r = r2;
                    p = p2;
                    mu = Math.Max(mu / 5, 1e-7);
                    improved = true;
                    break;
                }

                mu *= 10;
            }

            if (!improved)
                break;
        }

        return x;
    }

    private static double[] Flatten(double[][] perEdge)
    {
        var all = new double[perEdge.Sum(e => e.Length)];
        int o = 0;
        foreach (double[] e in perEdge)
        {
            e.CopyTo(all, o);
            o += e.Length;
        }

        return all;
    }

    /// <summary>The pose that best explains <paramref name="evidence"/>, held loosely to <paramref name="priorCorners"/> (corner positions, <paramref name="priorSigma"/> grid pixels).</summary>
    private static double[] SolvePose(
        Camera cam, double[] pose, Plate plate, EdgeEvidence[] evidence, (double X, double Y)[] priorCorners, double priorSigma,
        double inward, double outward)
    {
        double w = 1.0 / priorSigma;
        return Minimise(pose, x =>
        {
            double[] r = Flatten(EdgeResiduals(cam, x, plate, evidence));
            (double X, double Y)[] c = ProjectCorners(cam, x, plate);
            var prior = new double[8];
            for (int i = 0; i < 4; i++)
            {
                prior[2 * i] = (c[i].X - priorCorners[i].X) * w;
                prior[2 * i + 1] = (c[i].Y - priorCorners[i].Y) * w;
            }

            return (r, prior);
        }, inward, outward);
    }

    /// <summary>
    /// The pose that puts <paramref name="plate"/>'s corners on <paramref name="quad"/>. For a flat
    /// plate that is the quad's own decomposition; a bent plate's corners sit nearer the camera than
    /// its middle, so the decomposition is only the starting point and the corners are then fitted.
    /// </summary>
    private static bool PoseOnQuad(Camera cam, Plate plate, (double X, double Y)[] quad, double[] pose)
    {
        if (!Decompose(cam, quad, out _, out _, pose))
            return false;
        if (plate.IsFlat)
            return true;
        double[] fitted = Minimise(pose, x =>
        {
            (double X, double Y)[] c = ProjectCorners(cam, x, plate);
            var r = new double[8];
            for (int i = 0; i < 4; i++)
            {
                r[2 * i] = c[i].X - quad[i].X;
                r[2 * i + 1] = c[i].Y - quad[i].Y;
            }

            return ([], r);
        }, 1, 1, iterations: 12);
        if (Sane(cam, plate, fitted, quad))
            fitted.CopyTo(pose, 0);
        return true;
    }

    /// <summary>As <see cref="SolvePose"/>, with the plate's aspect free too (a seventh parameter, its logarithm). Used only while the shape is being estimated.</summary>
    private static (double[] Pose, double Aspect) SolvePoseAndAspect(
        Camera cam, double[] pose, double aspect, double kappa, EdgeEvidence[] evidence, double inward, double outward)
    {
        double[] x = Minimise([.. pose, Math.Log(aspect)], v =>
        {
            var plate = new Plate(Math.Exp(Math.Clamp(v[6], -3, 3)), kappa);
            return (Flatten(EdgeResiduals(cam, v.AsSpan(0, 6), plate, evidence)), []);
        }, inward, outward);
        return (x[..6], Math.Exp(Math.Clamp(x[6], -3, 3)));
    }

    /// <summary>A solved pose must still be a plate in front of the camera, convex, and near where it started.</summary>
    private static bool Sane(in Camera cam, Plate plate, ReadOnlySpan<double> pose, (double X, double Y)[] reference)
    {
        foreach (double v in pose)
        {
            if (!double.IsFinite(v))
                return false;
        }

        Span<(double X, double Y)> c = stackalloc (double X, double Y)[4];
        Span<double> depth = stackalloc double[4];
        Project(cam, pose, plate.Corners, c, depth);
        foreach (double d in depth)
        {
            if (d < 0.2)
                return false;
        }

        ReadOnlySpan<(double X, double Y)> ring = [c[0], c[1], c[3], c[2]];
        double sign = 0;
        for (int k = 0; k < 4; k++)
        {
            (double X, double Y) p0 = ring[k], p1 = ring[(k + 1) % 4], p2 = ring[(k + 2) % 4];
            double z = (p1.X - p0.X) * (p2.Y - p1.Y) - (p1.Y - p0.Y) * (p2.X - p1.X);
            if (Math.Abs(z) < 1e-9 || (sign != 0 && (z > 0) != (sign > 0)))
                return false;
            sign = z;
        }

        double diagonal = Math.Max(Dist(reference[0], reference[3]), Dist(reference[1], reference[2]));
        for (int i = 0; i < 4; i++)
        {
            if (Math.Max(Math.Abs(c[i].X - reference[i].X), Math.Abs(c[i].Y - reference[i].Y)) >= 0.35 * diagonal)
                return false;
        }

        return true;
    }

    private static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>
    /// A whole edge can sit on an occluder's boundary — something straight lying along it. The rigid
    /// model says so: solve the pose from the other three edges, and if they agree with each other
    /// (<see cref="LeftOutFit"/>) while this edge's crossings fall INSIDE the model's edge, the edge
    /// was the occluder's and is left out. Only when the other three are each well observed (two
    /// edges do not fix a pose), and only inward (occlusion removes plate, never adds it).
    ///
    /// <para>
    /// A plate that reads shorter has lost one of two OPPOSITE edges, and the three-edge fit is
    /// equally happy either way: drop the bottom and the plate stays, drop the top and it slides up
    /// until its bottom edge is the occluder's outline. Nothing in one frame tells those apart, so
    /// the answer that leaves the plate where it was predicted wins. The test deliberately does NOT
    /// ask for the three-edge fit to beat the four-edge one: the robust loss has usually discounted
    /// the occluder's outline already, so the right answer improves nothing while the wrong one
    /// (which adopts the outline as an edge) improves a lot.
    /// </para>
    /// </summary>
    private static double[] LeaveOneOut(
        Camera cam, double[] pose, double[] predicted, Plate plate, float[] dom, int width, int height, double threshold,
        (double X, double Y)[] priorCorners, double priorSigma)
    {
        EdgeEvidence[] evidence = Gather(dom, width, height, ProjectEdges(cam, pose, plate), 4.0, threshold);
        (double Median, double Rms, int Count)[] baseStats = EdgeStats(cam, pose, plate, evidence);

        // Only an edge the four-edge fit itself could not reconcile is in question, and with it the
        // edge opposite (the other half of the same ambiguity). Without this, a clean frame seen at a
        // steep angle loses an edge at random: three edges fix a pose only loosely there, so the
        // three-edge fit puts the fourth a few pixels out, and half the time that reads as "inside".
        Span<bool> inQuestion = stackalloc bool[4];
        bool any = false;
        for (int k = 0; k < 4; k++)
        {
            if (baseStats[k].Count >= 8 && baseStats[k].Median < -InwardTolerance / 2)
            {
                inQuestion[k] = inQuestion[(k + 2) % 4] = true;
                any = true;
            }
        }

        // The four-edge fit can also have ABSORBED an outline: one that meets the true edge at a
        // slight angle is nearly collinear with it, and a small turn of the plate fits all four. The
        // prediction has not: where three edges still sit on the predicted plate and a quarter of
        // the fourth's crossings are inside it, that fourth edge is in question too.
        double[][] againstPrediction = EdgeResiduals(cam, predicted, plate, evidence);
        for (int k = 0; k < 4; k++)
        {
            if (againstPrediction[k].Length < 8)
                continue;
            int inside = againstPrediction[k].Count(v => v < -InwardTolerance);
            double sum = 0;
            int n = 0;
            for (int j = 0; j < 4; j++)
            {
                if (j == k)
                    continue;
                foreach (double v in againstPrediction[j])
                {
                    double c = Math.Min(Math.Abs(v), 2.0);
                    sum += c * c;
                    n++;
                }
            }

            if (inside >= againstPrediction[k].Length / 4 && n > 0 && Math.Sqrt(sum / n) < LeftOutFit)
            {
                inQuestion[k] = inQuestion[(k + 2) % 4] = true;
                any = true;
            }
        }

        if (!any)
            return pose;

        double[]? best = null;
        double bestMoved = double.MaxValue;
        for (int k = 0; k < 4; k++)
        {
            if (!inQuestion[k] || baseStats[k].Count < 8)
                continue;
            bool othersObserved = true;
            for (int j = 0; j < 4; j++)
                othersObserved &= j == k || baseStats[j].Count >= Stations / 3;
            if (!othersObserved)
                continue;

            EdgeEvidence[] without = [.. evidence];
            without[k] = new EdgeEvidence([], evidence[k].Covered, evidence[k].Beyond);
            double[] p = SolvePose(cam, pose, plate, without, priorCorners, priorSigma, 1.25, 3.0);
            (double Median, double Rms, int Count)[] stats = EdgeStats(cam, p, plate, evidence);
            if (stats[k].Median < -InwardTolerance && PooledRms(stats, k) < LeftOutFit)
            {
                (double X, double Y)[] c = ProjectCorners(cam, p, plate);
                double moved = 0;
                for (int i = 0; i < 4; i++)
                    moved += (Math.Abs(c[i].X - priorCorners[i].X) + Math.Abs(c[i].Y - priorCorners[i].Y)) / 8;
                if (moved < bestMoved)
                {
                    bestMoved = moved;
                    best = p;
                }
            }
        }

        return best ?? pose;

        static double PooledRms((double Median, double Rms, int Count)[] stats, int except)
        {
            double sum = 0;
            int n = 0;
            for (int j = 0; j < 4; j++)
            {
                if (j == except)
                    continue;
                sum += stats[j].Rms * stats[j].Rms * stats[j].Count;
                n += stats[j].Count;
            }

            return Math.Sqrt(sum / Math.Max(1, n));
        }
    }

    private static double TotalSupport(double[] support) => support[0] + support[1] + support[2] + support[3];

    /// <summary>
    /// One frame's pose from a starting pose: the three passes, the leave-one-edge-out test, and a
    /// final rule that keeps the starting pose whenever the solved one explains LESS of the plate's
    /// boundary.
    /// </summary>
    private static (double[] Pose, EdgeEvidence[] Evidence) FitPose(
        Camera cam, Plate plate, float[] dom, int width, int height, double threshold, double[] init,
        (double X, double Y)[] priorCorners, double priorSigma, bool leaveOneOut = true)
    {
        double[] pose = init;
        (double X, double Y)[] start = ProjectCorners(cam, init, plate);
        foreach ((double reach, double inward, double outward) in Passes)
        {
            EdgeEvidence[] ev = Gather(dom, width, height, ProjectEdges(cam, pose, plate), reach, threshold);
            double[] candidate = SolvePose(cam, pose, plate, ev, priorCorners, priorSigma, inward, outward);
            if (Sane(cam, plate, candidate, start))
                pose = candidate;
        }

        if (leaveOneOut)
        {
            double[] candidate = LeaveOneOut(cam, pose, init, plate, dom, width, height, threshold, priorCorners, priorSigma);
            if (Sane(cam, plate, candidate, start))
                pose = candidate;
        }

        EdgeEvidence[] ev0 = Gather(dom, width, height, ProjectEdges(cam, init, plate), 3.0, threshold);
        EdgeEvidence[] ev1 = Gather(dom, width, height, ProjectEdges(cam, pose, plate), 3.0, threshold);
        return TotalSupport(Support(cam, pose, plate, ev1)) < TotalSupport(Support(cam, init, plate, ev0)) - 0.05
            ? (init, ev0)
            : (pose, ev1);
    }

    /// <summary>
    /// With fewer than three edges in view the 6-DoF pose is not determined, and solving it anyway
    /// lets the hidden part of the plate creep. Here the plate keeps the shape it last had and may
    /// only slide and turn in the image — and scale, when two opposite edges are both in view.
    /// </summary>
    private static (double[] Pose, EdgeEvidence[] Evidence) FitRestricted(
        Camera cam, Plate plate, float[] dom, int width, int height, double threshold, double[] from, bool allowScale)
    {
        double[] q = new double[allowScale ? 4 : 3];
        var moved = new double[6];
        foreach ((double reach, double inward, double outward) in Passes)
        {
            Restricted(from, q, moved);
            EdgeEvidence[] ev = Gather(dom, width, height, ProjectEdges(cam, moved, plate), reach, threshold);
            if (ev.Sum(e => e.Points.Length) < 6)
                continue;
            q = Minimise(q, v =>
            {
                var m = new double[6];
                Restricted(from, v, m);
                return (Flatten(EdgeResiduals(cam, m, plate, ev)), []);
            }, inward, outward);
        }

        var pose = new double[6];
        Restricted(from, q, pose);
        (double X, double Y)[] start = ProjectCorners(cam, from, plate);
        if (!Sane(cam, plate, pose, start))
            pose = from;
        EdgeEvidence[] ev0 = Gather(dom, width, height, ProjectEdges(cam, from, plate), 3.0, threshold);
        EdgeEvidence[] ev1 = Gather(dom, width, height, ProjectEdges(cam, pose, plate), 3.0, threshold);
        return TotalSupport(Support(cam, pose, plate, ev1)) < TotalSupport(Support(cam, from, plate, ev0)) - 0.05
            ? (from, ev0)
            : (pose, ev1);
    }

    // ------------------------------------------------------------------ the shape

    private static double QuadArea((double X, double Y)[] q)
    {
        static double Cross((double X, double Y) u, (double X, double Y) v) => u.X * v.Y - u.Y * v.X;
        (double X, double Y) a = (q[1].X - q[0].X, q[1].Y - q[0].Y), d = (q[3].X - q[0].X, q[3].Y - q[0].Y), b = (q[2].X - q[0].X, q[2].Y - q[0].Y);
        return 0.5 * Math.Abs(Cross(a, d)) + 0.5 * Math.Abs(Cross(b, d));
    }

    /// <summary>
    /// Focal length and aspect from a set of detections. For each candidate focal length every quad
    /// gives an aspect and how far from perpendicular its axes come out; the right focal length makes
    /// the aspects agree (their tightest half, since covered frames scatter) and the axes
    /// perpendicular. A plate that never changes its pose says nothing about the focal length, so the
    /// default stays unless another is clearly better.
    /// </summary>
    internal static (double Focal, double Aspect) EstimateCamera(int width, int height, IReadOnlyList<(double X, double Y)[]> quads)
    {
        (double Score, double Focal, double Aspect) best = (double.MaxValue, 0, 0), fallback = (double.MaxValue, 0, 0);
        Span<double> pose = stackalloc double[6];
        foreach (double k in FocalCandidates)
        {
            Camera cam = Camera.ForGrid(width, height, k * Math.Max(width, height));
            var logAspect = new List<double>(quads.Count);
            var cosines = new List<double>(quads.Count);
            foreach ((double X, double Y)[] q in quads)
            {
                if (Decompose(cam, q, out double aspect, out double cosine, pose) && aspect > 0)
                {
                    logAspect.Add(Math.Log(aspect));
                    cosines.Add(Math.Abs(cosine));
                }
            }

            if (logAspect.Count < 2)
                continue;
            logAspect.Sort();
            cosines.Sort();
            int half = Math.Max(2, logAspect.Count / 2);
            int at = 0;
            double width0 = double.MaxValue;
            for (int i = 0; i + half - 1 < logAspect.Count; i++)
            {
                double w = logAspect[i + half - 1] - logAspect[i];
                if (w < width0)
                {
                    width0 = w;
                    at = i;
                }
            }

            double score = width0 + Median(cosines);
            var candidate = (score, cam.F, Math.Exp(Median(logAspect.GetRange(at, half))));
            if (k == 1.0)
                fallback = candidate;
            if (score < best.Score)
                best = candidate;
        }

        if (fallback.Focal > 0 && !(best.Score < 0.8 * fallback.Score - 0.002))
            best = fallback;
        return (best.Focal, best.Aspect);
    }

    private static double Median(List<double> sorted) =>
        sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

    /// <summary>
    /// How well a rigid shape of curvature <paramref name="kappa"/> explains the sample frames. The
    /// width is fitted too (per frame, then the median), since a detection's aspect is only the
    /// chord's. The cost is the crossings' rms clipped at 2 px, with a station that found no boundary
    /// where the model put one counted as a full miss.
    /// </summary>
    private static (double Cost, Plate Plate) ShapeCost(
        Camera cam, double aspect, double kappa, FrameSource frames, double threshold, IReadOnlyList<(int Frame, (double X, double Y)[] Quad)> samples)
    {
        var aspects = new List<double>(samples.Count);
        var start = new double[6];
        foreach ((int frame, (double X, double Y)[] quad) in samples)
        {
            if (!PoseOnQuad(cam, new Plate(aspect, kappa), quad, start))
                continue;
            float[] dom = frames.Dominance(frame);
            frames.FramesSolved += 2;
            double[] pose = [.. start];
            double a = aspect;
            foreach ((double reach, double inward, double outward) in (ReadOnlySpan<(double, double, double)>)[(10.0, 8.0, 10.0), (6.0, 4.0, 6.0), (3.0, 1.5, 3.0)])
            {
                EdgeEvidence[] ev = Gather(dom, frames.Width, frames.Height, ProjectEdges(cam, pose, new Plate(a, kappa)), reach, threshold);
                (pose, a) = SolvePoseAndAspect(cam, pose, a, kappa, ev, inward, outward);
            }

            aspects.Add(a);
        }

        if (aspects.Count == 0)
            return (double.MaxValue, new Plate(aspect, kappa));
        aspects.Sort();
        var plate = new Plate(Median(aspects), kappa);
        double total = 0;
        int n = 0;
        foreach ((int frame, (double X, double Y)[] quad) in samples)
        {
            if (!PoseOnQuad(cam, plate, quad, start))
                continue;
            float[] dom = frames.Dominance(frame);
            double[] init = [.. start];
            (double[] pose, EdgeEvidence[] ev) = FitPose(
                cam, plate, dom, frames.Width, frames.Height, threshold, init, ProjectCorners(cam, init, plate), 1e6, leaveOneOut: false);
            foreach (double[] r in EdgeResiduals(cam, pose, plate, ev))
            {
                foreach (double v in r)
                {
                    double c = Math.Min(Math.Abs(v), 2.0);
                    total += c * c;
                }

                total += 4.0 * (Stations - r.Length);
                n += Stations;
            }
        }

        return (Math.Sqrt(total / Math.Max(1, n)), plate);
    }

    /// <summary>Flat unless a cylinder explains the edges clearly better (three quarters of the flat cost or less, and 0.15 px less) bends by <see cref="MinBend"/> or more, and fits to <see cref="MaxBentCost"/> or better.</summary>
    private static Plate EstimateShape(
        Camera cam, double aspect, FrameSource frames, double threshold, IReadOnlyList<(int Frame, (double X, double Y)[] Quad)> samples, Action<string>? log)
    {
        (double flatCost, Plate flat) = ShapeCost(cam, aspect, 0, frames, threshold, samples);
        (double Cost, Plate Plate) best = (flatCost, flat);
        var tried = new HashSet<int> { 0 };

        void Consider(double kappa)
        {
            int key = (int)Math.Round(kappa * 1000);
            if (key == 0 || !tried.Add(key))
                return;
            kappa = key / 1000.0;
            (double Cost, Plate Plate) c = ShapeCost(cam, aspect * (kappa / 2) / Math.Sin(kappa / 2), kappa, frames, threshold, samples);
            log?.Invoke(FormattableString.Invariant($"  kappa {kappa:+0.00;-0.00}: cost {c.Cost:0.000}, aspect {c.Plate.Aspect:0.0000}"));
            if (c.Cost < best.Cost)
                best = c;
        }

        if (flatCost >= FlatEnoughCost)
        {
            foreach (double kappa in (ReadOnlySpan<double>)[-1.2, -0.9, -0.6, -0.3, 0.3, 0.6, 0.9, 1.2])
                Consider(kappa);
            if (!best.Plate.IsFlat)
            {
                double k0 = best.Plate.Kappa;
                foreach (double dk in (ReadOnlySpan<double>)[-0.2, -0.1, 0.1, 0.2])
                    Consider(k0 + dk);
                k0 = best.Plate.Kappa;
                Consider(k0 - 0.05);
                Consider(k0 + 0.05);
            }
        }

        log?.Invoke(FormattableString.Invariant(
            $"shape: flat cost {flatCost:0.000} (aspect {flat.Aspect:0.0000}); best {best.Cost:0.000} at kappa {best.Plate.Kappa:+0.00;-0.00}, aspect {best.Plate.Aspect:0.0000}"));
        if (best.Plate.IsFlat || Math.Abs(best.Plate.Kappa) < MinBend)
            return flat;
        return best.Cost <= MaxBentCost && best.Cost < 0.75 * flatCost && flatCost - best.Cost > 0.15 ? best.Plate : flat;
    }

    /// <summary>Per station of edge <paramref name="k"/>, the length of the unbroken run of plate starting just outside the model's edge, in grid pixels.</summary>
    private static void BeyondRuns(
        float[] dom, int width, int height, (double X, double Y)[][] polys, int k, double limit, double threshold, Span<double> runs)
    {
        (double X, double Y) centre = Centre(polys);
        Span<(double X, double Y)> at = stackalloc (double X, double Y)[Stations];
        Span<(double X, double Y)> normal = stackalloc (double X, double Y)[Stations];
        PlaceStations(polys[k], centre, at, normal);
        limit = Math.Max(2.0, Math.Min(limit, Math.Sqrt((double)width * width + (double)height * height)));
        for (int s = 0; s < Stations; s++)
        {
            double run = 0;
            for (double d = 1.0; d < limit; d += 1.0)
            {
                double x = at[s].X + d * normal[s].X, y = at[s].Y + d * normal[s].Y;
                if (x < 0 || y < 0 || x > width - 1 || y > height - 1 || Sample(dom, width, height, x, y) < threshold)
                    break;
                run = d;
            }

            runs[s] = run >= limit - 1.0 ? double.PositiveInfinity : run;
        }
    }

    /// <summary>
    /// Per edge (top, right, bottom, left), in plate heights: how much further the plate is ever seen
    /// to reach than the model says. A frame votes when it is locked on (the two neighbouring edges
    /// explain their own evidence) and a third of this edge's stations see plate continuing past it
    /// and then ENDING (a run that never ends is scenery). The edge's growth is the upper quartile of
    /// the votes, when a twentieth of the frames (three at least) cast one.
    /// </summary>
    private static double[] MeasureGrowth(Camera cam, Plate plate, FrameSource frames, double threshold, SortedDictionary<int, Solved> poses)
    {
        var grow = new double[4];
        Span<double> runs = stackalloc double[Stations];
        for (int k = 0; k < 4; k++)
        {
            var seen = new List<double>();
            foreach ((int frame, Solved solved) in poses)
            {
                if (solved.Support[(k + 1) % 4] < 0.4 || solved.Support[(k + 3) % 4] < 0.4)
                    continue;
                (double X, double Y)[][] polys = ProjectEdges(cam, solved.Pose, plate);
                double across = Dist(Mean(polys[k]), Mean(polys[(k + 2) % 4]));
                double limit = 0.9 * across;
                BeyondRuns(frames.Dominance(frame), frames.Width, frames.Height, polys, k, limit, threshold, runs);
                var far = new List<double>();
                foreach (double r in runs)
                {
                    if (r > 2.5 && r < limit - 2)
                        far.Add(r);
                }

                if (far.Count >= Stations / 3)
                {
                    far.Sort();
                    seen.Add(Median(far) / Math.Max(1.0, across) * (k is 0 or 2 ? 1.0 : plate.Aspect));
                }
            }

            if (seen.Count >= Math.Max(3, poses.Count / 20))
            {
                seen.Sort();
                double upperQuartile = seen[(int)Math.Floor(0.75 * (seen.Count - 1))];
                grow[k] = Math.Min(0.6 * (k is 0 or 2 ? 1.0 : plate.Aspect), upperQuartile);
            }
        }

        return grow;

        static (double X, double Y) Mean((double X, double Y)[] p) => (p.Average(v => v.X), p.Average(v => v.Y));
    }

    // ------------------------------------------------------------------ the track

    private static double PlateLevel(float[] dom, int width, int height, (double X, double Y)[] quad)
    {
        double cx = quad.Average(p => p.X), cy = quad.Average(p => p.Y);
        (double X, double Y) In(int i) => (cx + (quad[i].X - cx) * 0.6, cy + (quad[i].Y - cy) * 0.6);
        var values = new List<double>(49);
        for (int iv = 0; iv < 7; iv++)
        {
            double v = iv / 6.0;
            (double X, double Y) l = (In(0).X + (In(2).X - In(0).X) * v, In(0).Y + (In(2).Y - In(0).Y) * v);
            (double X, double Y) r = (In(1).X + (In(3).X - In(1).X) * v, In(1).Y + (In(3).Y - In(1).Y) * v);
            for (int iu = 0; iu < 7; iu++)
            {
                double u = iu / 6.0;
                values.Add(Sample(dom, width, height, l.X + (r.X - l.X) * u, l.Y + (r.Y - l.Y) * u));
            }
        }

        values.Sort();
        return Median(values);
    }

    /// <summary>
    /// Follows the plate frame by frame outward from <paramref name="anchor"/>, over
    /// <paramref name="first"/>..<paramref name="last"/>. The prediction (constant velocity in pose)
    /// keeps the track while it still explains two edges; only a lost lock hands over to the frame's
    /// own detection, because an occluder's outline can score as support for a smaller plate.
    /// </summary>
    /// <param name="coreFirst">First frame the track must cover whatever it finds there (the tracker run's own span).</param>
    /// <param name="coreLast">Last such frame.</param>
    private static SortedDictionary<int, Solved> TrackPass(
        Camera cam, Plate plate, FrameSource frames, double threshold, IReadOnlyDictionary<int, (double X, double Y)[]> detections,
        int anchor, int first, int last, int coreFirst, int coreLast, IReadOnlyDictionary<int, double[]>? seeds, ref int held)
    {
        int w = frames.Width, h = frames.Height;
        var poses = new SortedDictionary<int, Solved>();
        var start = new double[6];
        if (seeds is not null && seeds.TryGetValue(anchor, out double[]? seed))
            start = [.. seed];
        else if (!detections.TryGetValue(anchor, out (double X, double Y)[]? anchorQuad) || !PoseOnQuad(cam, plate, anchorQuad, start))
            return poses;

        (double[] pose0, EdgeEvidence[] ev0) = FitPose(cam, plate, frames.Dominance(anchor), w, h, threshold, start, ProjectCorners(cam, start, plate), 50.0);
        poses[anchor] = Describe(cam, plate, pose0, ev0);
        frames.FramesSolved++;

        foreach (int direction in (ReadOnlySpan<int>)[1, -1])
        {
            double[]? prev2 = null;
            double[] prev = poses[anchor].Pose;
            int lost = 0;
            for (int i = anchor + direction; i >= first && i <= last; i += direction)
            {
                // Outside the frames the track must cover, the plate is followed for as long as it is
                // there to follow: once no edge has been in view for LostFrames frames (the plate left
                // the picture, the shot changed, glare took the screen), the track ends where it was
                // last seen.
                bool beyondCore = i < coreFirst || i > coreLast;
                if (beyondCore && lost >= LostFrames)
                {
                    for (int back = i - direction; lost > 0; back -= direction, lost--)
                        poses.Remove(back);
                    break;
                }

                // Out of budget (see FrameSource.WorkBudget): no track, and the run stays as the
                // detector made it.
                if (frames.OverBudget)
                {
                    poses.Clear();
                    return poses;
                }

                float[] dom = frames.Dominance(i);
                frames.FramesSolved++;
                double[] predicted = prev;
                (double X, double Y)[] prevCorners = ProjectCorners(cam, prev, plate);
                if (prev2 is not null)
                {
                    var extrapolated = new double[6];
                    for (int k = 0; k < 6; k++)
                        extrapolated[k] = 2 * prev[k] - prev2[k];
                    if (Sane(cam, plate, extrapolated, prevCorners))
                        predicted = extrapolated;
                }

                (double X, double Y)[] predictedCorners = ProjectCorners(cam, predicted, plate);
                double speed = 0;
                for (int c = 0; c < 4; c++)
                    speed = Math.Max(speed, Math.Max(Math.Abs(predictedCorners[c].X - prevCorners[c].X), Math.Abs(predictedCorners[c].Y - prevCorners[c].Y)));

                (double[] pose, EdgeEvidence[] ev) = FitPose(cam, plate, dom, w, h, threshold, predicted, predictedCorners, 3.0 + speed);
                Solved best = Describe(cam, plate, pose, ev);

                if (best.Support.Count(v => v >= CleanEdgeSupport) < 3)
                {
                    // The prediction does not hold three edges: the plate moved further than the
                    // probes reach (a whip), or little of it is in view. The frame's own detection (or
                    // the earlier pass's pose for this frame) is a second starting point, and it wins
                    // when it shows the WHOLE plate, all four edges in view, or when the prediction
                    // lost the plate altogether and this explains more. With three edges held the
                    // prediction is kept: all that a second answer could add is a fourth edge, and a
                    // fourth edge that fits is exactly what an outline lying along it looks like.
                    var other = new double[6];
                    bool have = seeds is not null && seeds.TryGetValue(i, out double[]? s)
                        ? Copy(s, other)
                        : detections.TryGetValue(i, out (double X, double Y)[]? quad) && PoseOnQuad(cam, plate, quad, other);
                    if (have)
                    {
                        frames.FramesSolved++;
                        (double[] p2, EdgeEvidence[] e2) = FitPose(cam, plate, dom, w, h, threshold, other, predictedCorners, 3.0 + speed);
                        Solved second = Describe(cam, plate, p2, e2);
                        if (second.Support.All(v => v >= CleanEdgeSupport)
                            || (best.EdgesInView < 2 && TotalSupport(second.Support) > TotalSupport(best.Support)))
                        {
                            best = second;
                        }
                    }
                }

                if (best.EdgesInView < 3)
                {
                    // Too little of the plate in view to fix a pose: keep the shape, follow the motion.
                    bool[] seen = [.. best.Support.Select(v => v >= SupportedEdge)];
                    (double[] p3, EdgeEvidence[] e3) = FitRestricted(cam, plate, dom, w, h, threshold, prev, (seen[0] && seen[2]) || (seen[1] && seen[3]));
                    Solved kept = Describe(cam, plate, p3, e3);
                    if (!(kept.EdgesInView == 0 && TotalSupport(best.Support) > TotalSupport(kept.Support)))
                    {
                        best = kept;
                        held++;
                    }
                }

                poses[i] = best;
                lost = beyondCore && best.EdgesInView == 0 ? lost + 1 : 0;
                prev2 = prev;
                prev = best.Pose;
            }

            // A run of lost frames that reached the end of the clip is dropped the same way.
            for (int back = direction > 0 ? last : first; lost > 0 && (back < coreFirst || back > coreLast); back -= direction, lost--)
                poses.Remove(back);
        }

        return poses;

        static bool Copy(double[] from, double[] to)
        {
            from.CopyTo(to, 0);
            return true;
        }
    }

    /// <summary>
    /// A robust straight line through an edge's crossings (total least squares, Tukey-reweighted),
    /// the share of the edge's stations that sit on the edge (line or bow), and how far the edge
    /// bows off its line, in grid pixels. <c>null</c> with fewer than eight crossings.
    /// </summary>
    private static ((double Px, double Py, double Dx, double Dy) Line, double Support, double BowPx)? FreeLine((double X, double Y)[] pts)
    {
        if (pts.Length < 8)
            return null;
        var w = new double[pts.Length];
        Array.Fill(w, 1.0);
        double px = 0, py = 0, dx = 1, dy = 0;
        for (int it = 0; it < 5; it++)
        {
            double sw = 0, mx = 0, my = 0;
            for (int i = 0; i < pts.Length; i++)
            {
                sw += w[i];
                mx += w[i] * pts[i].X;
                my += w[i] * pts[i].Y;
            }

            if (!(sw > 1e-9))
                return null;
            mx /= sw;
            my /= sw;
            double sxx = 0, sxy = 0, syy = 0;
            for (int i = 0; i < pts.Length; i++)
            {
                double ex = pts[i].X - mx, ey = pts[i].Y - my;
                sxx += w[i] * ex * ex;
                sxy += w[i] * ex * ey;
                syy += w[i] * ey * ey;
            }

            double angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
            (px, py, dx, dy) = (mx, my, Math.Cos(angle), Math.Sin(angle));
            for (int i = 0; i < pts.Length; i++)
            {
                double r = (pts[i].X - px) * -dy + (pts[i].Y - py) * dx;
                double x = Math.Min(Math.Abs(r) / 1.5, 1);
                w[i] = (1 - x * x) * (1 - x * x);
            }
        }

        // A bent plate's edge is not a line. Fit r(t) = a + b t + c t^2 to the residuals along the
        // line (t in grid pixels from its anchor point) and count support against that curve, so a
        // bowed edge in full view still reads as in view; the bow itself is c * (span / 2)^2.
        double a0 = 0, a1 = 0, a2 = 0, tMin = double.MaxValue, tMax = double.MinValue;
        Span<double> sol = stackalloc double[3];
        for (int it = 0; it < 3; it++)
        {
            var m = new double[3, 4];
            for (int i = 0; i < pts.Length; i++)
            {
                double t = (pts[i].X - px) * dx + (pts[i].Y - py) * dy;
                double r = (pts[i].X - px) * -dy + (pts[i].Y - py) * dx;
                double x = Math.Min(Math.Abs(r - (a0 + a1 * t + a2 * t * t)) / 1.5, 1);
                double wi = it == 0 ? w[i] : (1 - x * x) * (1 - x * x);
                tMin = Math.Min(tMin, t);
                tMax = Math.Max(tMax, t);
                ReadOnlySpan<double> basis = [1, t, t * t];
                for (int c1 = 0; c1 < 3; c1++)
                {
                    for (int c2 = 0; c2 < 3; c2++)
                        m[c1, c2] += wi * basis[c1] * basis[c2];
                    m[c1, 3] += wi * basis[c1] * r;
                }
            }

            if (!SolveInPlace(m, 3, sol))
                break;
            (a0, a1, a2) = (sol[0], sol[1], sol[2]);
        }

        int on = 0;
        foreach ((double X, double Y) p in pts)
        {
            double t = (p.X - px) * dx + (p.Y - py) * dy;
            if (Math.Abs((p.X - px) * -dy + (p.Y - py) * dx - (a0 + a1 * t + a2 * t * t)) < SupportDistance)
                on++;
        }

        double half = (tMax - tMin) / 2;
        return ((px, py, dx, dy), on / (double)Stations, Math.Abs(a2) * half * half);
    }

    /// <summary>
    /// The frame's plate as four FREE edge lines through the outermost boundary found around a
    /// detection — no rigid model involved — and whether the frame is clean by its own evidence:
    /// every edge straight and in view along <see cref="CleanEdgeSupport"/> of its length. A covered
    /// corner fails that (the stations under the cover find no boundary), and so does fur or a hand
    /// along an edge; only something straight lying exactly along a whole edge passes, and the
    /// growth loop is what answers that.
    /// </summary>
    private static (double X, double Y)[]? FreeQuad(float[] dom, int width, int height, double threshold, (double X, double Y)[] detection, out bool clean)
    {
        clean = false;
        bool bowed = false;
        (double X, double Y)[] q = detection;
        for (int pass = 0; pass < 2; pass++)
        {
            (double X, double Y)[][] polys = [[q[0], q[1]], [q[1], q[3]], [q[3], q[2]], [q[2], q[0]]];
            EdgeEvidence[] ev = Gather(dom, width, height, polys, pass == 0 ? 6.0 : 3.0, threshold);
            var lines = new (double Px, double Py, double Dx, double Dy)[4];
            bool all = true;
            for (int k = 0; k < 4; k++)
            {
                if (FreeLine(ev[k].Points) is not { } fit)
                    return null;
                lines[k] = fit.Line;
                all &= fit.Support >= CleanEdgeSupport;
                bowed |= pass == 1 && fit.BowPx > 1.0;
            }

            // Corners: TL = left x top, TR = top x right, BR = right x bottom, BL = bottom x left.
            if (ChromaEdgeLineFitter.IntersectLines(lines[3], lines[0]) is not { } tl
                || ChromaEdgeLineFitter.IntersectLines(lines[0], lines[1]) is not { } tr
                || ChromaEdgeLineFitter.IntersectLines(lines[1], lines[2]) is not { } br
                || ChromaEdgeLineFitter.IntersectLines(lines[2], lines[3]) is not { } bl)
            {
                return null;
            }

            q = [tl, tr, bl, br];
            clean = all;
        }

        // The piece of a plate left of a pole is four straight edges in full view too. What gives it
        // away is more plate on the other side of the pole.
        clean = clean && !PlateResumesBeyond(dom, width, height, threshold, q);

        // Four straight lines do not meet at a bent plate's corners; its detection (measured against
        // the silhouette) is the better quad there.
        return bowed ? detection : q;
    }

    /// <summary>
    /// Does plate show again beyond one of the quad's edges, after a gap? For a quarter of an edge's
    /// stations, within half the quad's size across: then this quad is one piece of a plate that
    /// something crosses, not the plate. (A second plate close by reads the same; such a frame is
    /// simply not used to learn the shape from.)
    /// </summary>
    private static bool PlateResumesBeyond(float[] dom, int width, int height, double threshold, (double X, double Y)[] q)
    {
        (double X, double Y)[][] polys = [[q[0], q[1]], [q[1], q[3]], [q[3], q[2]], [q[2], q[0]]];
        (double X, double Y) centre = Centre(polys);
        Span<(double X, double Y)> at = stackalloc (double X, double Y)[Stations];
        Span<(double X, double Y)> normal = stackalloc (double X, double Y)[Stations];
        for (int k = 0; k < 4; k++)
        {
            PlaceStations(polys[k], centre, at, normal);
            (double X, double Y) a = polys[k][0], b = polys[k][1], c = polys[(k + 2) % 4][0], d = polys[(k + 2) % 4][1];
            double across = Dist(((a.X + b.X) / 2, (a.Y + b.Y) / 2), ((c.X + d.X) / 2, (c.Y + d.Y) / 2));
            if (Dist(a, b) < 1.0)
                continue;

            double limit = Math.Min(0.5 * across, Math.Sqrt((double)width * width + (double)height * height));
            int resumed = 0;
            for (int s = 0; s < Stations; s++)
            {
                bool gap = false;
                int run = 0;
                for (double dist = 2.0; dist < limit; dist += 1.0)
                {
                    double x = at[s].X + dist * normal[s].X, y = at[s].Y + dist * normal[s].Y;
                    if (x < 0 || y < 0 || x > width - 1 || y > height - 1)
                        break;
                    bool plate = Sample(dom, width, height, x, y) >= threshold;
                    gap |= !plate;
                    run = plate && gap ? run + 1 : 0;
                    if (run >= 3)
                    {
                        resumed++;
                        break;
                    }
                }
            }

            if (resumed >= Stations / 4)
                return true;
        }

        return false;
    }

    /// <summary>Share of an edge's stations that must sit on its own free line for the edge to count as fully in view.</summary>
    private const double CleanEdgeSupport = 0.85;

    /// <summary>The whole solve for one plate: camera and shape from its detections, a tracking pass, then the grow-and-retrack loop.</summary>
    internal sealed record Track(Camera Camera, Plate Plate, SortedDictionary<int, Solved> Poses, Diagnostics Diagnostics);

    /// <summary>
    /// Solves one plate over <paramref name="first"/>..<paramref name="last"/> from its per-frame
    /// detections (grid pixels, TL TR BL BR). <c>null</c> when there is too little to model.
    /// </summary>
    internal static Track? Solve(
        FrameSource frames, IReadOnlyDictionary<int, (double X, double Y)[]> detections, int first, int last, Action<string>? log = null,
        bool extend = true)
    {
        int coreFirst = first, coreLast = last;
        if (extend)
        {
            first = 0;
            last = frames.FrameCount - 1;
        }

        if (detections.Count < 4 || frames.OverBudget)
            return null;
        int w = frames.Width, h = frames.Height;

        // The plate's own level: the detections' interiors, the upper half of them (a detection that
        // sits on an occluder reads low).
        List<int> byArea = [.. detections.Keys.OrderByDescending(f => QuadArea(detections[f])).ThenBy(f => f)];
        List<double> levels = [.. byArea.Take(Math.Max(4, byArea.Count / 2)).Select(f => PlateLevel(frames.Dominance(f), w, h, detections[f]))];
        levels.Sort();
        double threshold = Median(levels) / 2;
        if (!(threshold > 1))
            return null;

        // Which frames to learn the shape from. First choice: the frames that are clean by their own
        // evidence — four straight edges, each in view along its whole length — re-measured as free
        // lines, so neither the detector's corners nor any model shapes them. When a clip has too few
        // of those (something sits on the plate nearly throughout), the largest detections instead:
        // occlusion only ever removes plate, so the largest are the least covered, and what is still
        // hidden there the growth loop below recovers. Never the consensus of all frames, which a
        // long occlusion owns.
        var learn = new Dictionary<int, (double X, double Y)[]>();
        int stride = Math.Max(1, detections.Count / 60);
        int index = 0;
        foreach (int f in detections.Keys.OrderBy(f => f))
        {
            if (index++ % stride != 0)
                continue;
            if (FreeQuad(frames.Dominance(f), w, h, threshold, detections[f], out bool clean) is { } free && clean)
                learn[f] = free;
        }

        bool fromClean = learn.Count >= 4;
        if (!fromClean)
        {
            learn.Clear();
            foreach (int f in byArea.Take(Math.Max(4, byArea.Count / 4)))
                learn[f] = detections[f];
        }

        (double focal, double aspect) = EstimateCamera(w, h, [.. learn.Values]);
        if (!(focal > 0) || !(aspect > 0))
            return null;
        Camera cam = Camera.ForGrid(w, h, focal);
        log?.Invoke(FormattableString.Invariant(
            $"camera: focal {focal:0} grid px, aspect {aspect:0.0000}, learned from {learn.Count} {(fromClean ? "clean frames" : "largest detections")}"));

        var scratch = new double[6];
        List<int> agreeing =
        [
            .. learn.Keys
                .Where(f => Decompose(cam, learn[f], out double a, out _, scratch) && Math.Abs(Math.Log(a / aspect)) < 0.03)
                .OrderByDescending(f => QuadArea(learn[f])).ThenBy(f => f)
        ];
        if (agreeing.Count == 0)
            agreeing = [.. learn.Keys.OrderByDescending(f => QuadArea(learn[f])).ThenBy(f => f)];

        int anchor = agreeing[0];
        var samples = new List<(int, (double X, double Y)[])>();
        for (int i = 0; i < agreeing.Count && samples.Count < 6; i += Math.Max(1, agreeing.Count / 6))
            samples.Add((agreeing[i], learn[agreeing[i]]));

        log?.Invoke($"stage: shape from {samples.Count} samples, anchor {anchor}, {detections.Count} detections, frames {first}-{last} (core {coreFirst}-{coreLast})");
        Plate plate = EstimateShape(cam, aspect, frames, threshold, samples, log);
        log?.Invoke("stage: shape done");
        if (!plate.IsFlat)
        {
            // A bent plate's corner quad is not a rectangle's image, so the focal length read from
            // it above is biased. With the curvature known, ask the edges themselves: one sweep of
            // the focal length, then the curvature once more around where it was.
            (double Cost, Plate Plate, Camera Cam) best = (ShapeCost(cam, plate.Aspect, plate.Kappa, frames, threshold, samples).Cost, plate, cam);
            foreach (double k in FocalCandidates)
            {
                Camera c2 = Camera.ForGrid(w, h, k * Math.Max(w, h));
                (double cost, Plate p2) = ShapeCost(c2, plate.Aspect, plate.Kappa, frames, threshold, samples);
                if (cost < best.Cost - 0.01)
                    best = (cost, p2, c2);
            }

            cam = best.Cam;
            plate = best.Plate;
            foreach (double dk in (ReadOnlySpan<double>)[-0.15, -0.1, -0.05, 0.05, 0.1, 0.15])
            {
                (double cost, Plate p2) = ShapeCost(cam, plate.Aspect, plate.Kappa + dk, frames, threshold, samples);
                if (Math.Abs(plate.Kappa + dk) >= MinBend && cost < best.Cost)
                    best = (cost, p2, cam);
            }

            plate = best.Plate;
            log?.Invoke(FormattableString.Invariant($"bent plate refined: focal {cam.F:0}, kappa {plate.Kappa:+0.00;-0.00}, aspect {plate.Aspect:0.0000}, cost {best.Cost:0.000}"));
        }

        int held = 0;
        log?.Invoke("stage: first pass");
        SortedDictionary<int, Solved> poses = TrackPass(cam, plate, frames, threshold, detections, anchor, first, last, coreFirst, coreLast, null, ref held);
        log?.Invoke($"stage: first pass done, {poses.Count} frames");
        if (poses.Count == 0)
            return null;

        // The consensus shape is the plate only if some frames show all of it. Something that hides
        // an edge for most of the clip leaves the model the VISIBLE part, and the frames that show
        // more say so, as plate beyond the model's edges. Grow to the largest plate the evidence
        // ever supports and track again. (A cylinder grows along its axis only.)
        int rounds = 0;
        var r = new double[9];
        for (; rounds < 3; rounds++)
        {
            log?.Invoke("stage: measuring growth");
            double[] g = MeasureGrowth(cam, plate, frames, threshold, poses);
            log?.Invoke("stage: growth measured");
            if (!plate.IsFlat)
                g[1] = g[3] = 0;
            if (g.Max() < MinGrowth)
                break;

            double scale = 1 + g[0] + g[2];
            (double X, double Y) centre = ((g[1] - g[3]) / 2, (g[2] - g[0]) / 2);
            // Kappa is an angle (arc over radius), so rescaling the plate's unit leaves it alone.
            var grown = new Plate((plate.Aspect + g[1] + g[3]) / scale, plate.Kappa);
            log?.Invoke(FormattableString.Invariant(
                $"plate seen beyond the model (top {g[0]:0.000}, right {g[1]:0.000}, bottom {g[2]:0.000}, left {g[3]:0.000}); aspect {plate.Aspect:0.0000} -> {grown.Aspect:0.0000}"));

            var seeds = new Dictionary<int, double[]>(poses.Count);
            foreach ((int frame, Solved s) in poses)
            {
                Rodrigues(s.Pose.AsSpan(0, 3), r);
                seeds[frame] =
                [
                    s.Pose[0], s.Pose[1], s.Pose[2],
                    (r[0] * centre.X + r[1] * centre.Y + s.Pose[3]) / scale,
                    (r[3] * centre.X + r[4] * centre.Y + s.Pose[4]) / scale,
                    (r[6] * centre.X + r[7] * centre.Y + s.Pose[5]) / scale
                ];
            }

            // Start where the most of the grown plate is in view.
            int bestFrame = anchor;
            double bestSupport = -1;
            foreach ((int frame, double[] seed) in seeds)
            {
                double s = TotalSupport(Support(cam, seed, grown, Gather(frames.Dominance(frame), w, h, ProjectEdges(cam, seed, grown), 4.0, threshold)));
                if (s > bestSupport)
                {
                    bestSupport = s;
                    bestFrame = frame;
                }
            }

            plate = grown;
            anchor = bestFrame;
            held = 0;
            poses = TrackPass(cam, plate, frames, threshold, detections, anchor, first, last, coreFirst, coreLast, seeds, ref held);
            if (poses.Count == 0)
                return null;
        }

        log?.Invoke(FormattableString.Invariant(
            $"model: focal {cam.F:0} grid px, aspect {plate.Aspect:0.0000}, kappa {plate.Kappa:+0.00;-0.00}, growth rounds {rounds}, held frames {held}"));
        return new Track(cam, plate, poses, new Diagnostics(cam.F, plate.Aspect, plate.Kappa, rounds, held));
    }

    /// <summary>The plate shapes the model may take, width over height. 32:9, the widest screen sold, is 3.6; a phone upright is 0.45.</summary>
    internal const double MinAspect = 0.2, MaxAspect = 6.0;

    /// <summary>Corner distance between a detection and the model, in grid pixels, above which the detection is taken as wrong (something covers part of the plate).</summary>
    internal const double RepairDistance = 1.5;

    /// <summary>
    /// Median distance of the plate's boundary from the model's edges (grid pixels, over the frames
    /// with at least three edges in view) above which the rigid model does not describe this plate
    /// and the run is left alone. A model that fits reads 0.2 to 0.35: the boundary's own noise.
    /// </summary>
    internal const double MaxModelRms = 0.6;

    /// <summary>
    /// Repairs a tracker run: solves the plate's rigid pose over the run's span and, when any frame's
    /// detection disagrees with it (or is missing), emits the model's geometry. Returns the run
    /// untouched — the same list — when there is nothing to repair, or when the rigid model does not
    /// explain this plate's clean frames.
    /// </summary>
    internal static (List<ChromaQuadTracker.FrameQuad> Run, bool Repaired) Refine(
        List<ChromaQuadTracker.FrameQuad> run, FrameSource frames, bool measureCurvature, Action<string>? log = null)
    {
        if (run.Count < 4)
            return (run, false);
        int w = frames.Width, h = frames.Height;
        (double X, double Y)[] Quad(ChromaQuadTracker.FrameQuad q) => q.Surface == default
            ? [(q.X0 * w - 0.5, q.Y0 * h - 0.5), (q.X1 * w - 0.5, q.Y1 * h - 0.5), (q.X2 * w - 0.5, q.Y2 * h - 0.5), (q.X3 * w - 0.5, q.Y3 * h - 0.5)]
            :
            [
                (q.Surface.X0 * w - 0.5, q.Surface.Y0 * h - 0.5), (q.Surface.X1 * w - 0.5, q.Surface.Y1 * h - 0.5),
                (q.Surface.X2 * w - 0.5, q.Surface.Y2 * h - 0.5), (q.Surface.X3 * w - 0.5, q.Surface.Y3 * h - 0.5)
            ];

        var detected = new Dictionary<int, ChromaQuadTracker.FrameQuad>();
        var detections = new Dictionary<int, (double X, double Y)[]>();
        foreach (ChromaQuadTracker.FrameQuad q in run)
        {
            if (q.Interpolated || q.FrameIndex < 0 || q.FrameIndex >= frames.FrameCount)
                continue;
            detected[q.FrameIndex] = q;
            detections[q.FrameIndex] = Quad(q);
        }

        int first = run[0].FrameIndex, last = Math.Min(run[^1].FrameIndex, frames.FrameCount - 1);
        Track? track;
        try
        {
            track = Solve(frames, detections, first, last, log);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.Invoke($"pose solve failed: {ex.GetType().Name}");
            return (run, false);
        }

        if (track is null)
            return (run, false);

        // Does the rigid model describe this plate at all? Its own evidence answers: where most of
        // the plate is in view, the boundary must lie on the model's edges to within its noise.
        var corners = new Dictionary<int, (double X, double Y)[]>(track.Poses.Count);
        var fit = new List<double>();
        bool anyRepair = false;
        foreach ((int frame, Solved solved) in track.Poses)
        {
            (double X, double Y)[] c = ProjectCorners(track.Camera, solved.Pose, track.Plate);
            corners[frame] = c;
            if (solved.Support.Count(v => v >= 0.5) >= 3)
                fit.Add(solved.Rms);
            if (!detections.TryGetValue(frame, out (double X, double Y)[]? d) || WorstCorner(c, d) > RepairDistance)
                anyRepair = true;
        }

        if (log is not null && ChromaQuadTracker.PoseLogEveryFrame)
        {
            foreach ((int frame, Solved solved) in track.Poses)
            {
                (double X, double Y)[] c = corners[frame];
                log(FormattableString.Invariant(
                    $"  f{frame}: support {solved.Support[0]:0.00} {solved.Support[1]:0.00} {solved.Support[2]:0.00} {solved.Support[3]:0.00} rms {solved.Rms:0.00} corners {c[0].X:0.0},{c[0].Y:0.0} {c[1].X:0.0},{c[1].Y:0.0} {c[2].X:0.0},{c[2].Y:0.0} {c[3].X:0.0},{c[3].Y:0.0}"));
            }
        }

        fit.Sort();
        double rms = fit.Count == 0 ? double.MaxValue : Median(fit);
        log?.Invoke(FormattableString.Invariant(
            $"model fit over {fit.Count} frames with three edges in view: median rms {(fit.Count == 0 ? 0 : rms):0.00} grid px; frames {track.Poses.Keys.First()}-{track.Poses.Keys.Last()}; repair needed: {anyRepair}"));
        if (fit.Count < 4 || rms > MaxModelRms || !anyRepair)
            return (run, false);

        // A model has to have been SEEN to be believed: on a quarter of the frames at least, three of
        // its edges in view. A plate at a grazing angle decomposes into almost any shape, and a shape
        // that only a handful of frames support is one of those. Its corners must also stay near the
        // picture; a plate can leave the frame, but not by more than its own size.
        if (fit.Count < track.Poses.Count / 4 || track.Plate.Aspect is < MinAspect or > MaxAspect)
        {
            log?.Invoke(FormattableString.Invariant($"model not trusted: {fit.Count} of {track.Poses.Count} frames support it, aspect {track.Plate.Aspect:0.00}"));
            return (run, false);
        }

        foreach ((double X, double Y)[] c in corners.Values)
        {
            foreach ((double X, double Y) p in c)
            {
                if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < -w || p.X > 2 * w || p.Y < -h || p.Y > 2 * h)
                {
                    log?.Invoke("model not trusted: a corner left the picture by more than the picture's size");
                    return (run, false);
                }
            }
        }

        // What a repaired frame scores on the per-frame confidence terms: the run's own typical
        // values where its detections were right. A frame whose detection was the fragment an
        // occluder left would otherwise be priced by evidence that is missing BY CONSTRUCTION.
        List<ChromaQuadTracker.FrameQuad> good =
        [
            .. detected.Values.Where(q => corners.TryGetValue(q.FrameIndex, out (double X, double Y)[]? c) && WorstCorner(c, detections[q.FrameIndex]) <= RepairDistance)
        ];
        if (good.Count == 0)
            good = [.. detected.Values];
        double typicalFill = MedianOf(good.Select(q => q.FillScore)), typicalEdge = MedianOf(good.Select(q => q.EdgeContrast));
        double typicalArea = MedianOf(good.Select(q => q.AreaRatio)), typicalMargin = MedianOf(good.Select(q => q.ChromaMargin));
        double typicalDominance = MedianOf(good.Select(q => q.PlateDominance));

        var repaired = new List<ChromaQuadTracker.FrameQuad>(track.Poses.Count);
        foreach ((int frame, Solved solved) in track.Poses)
        {
            (double X, double Y)[] c = corners[frame];
            double nx(double x) => (x + 0.5) / w;
            double ny(double y) => (y + 0.5) / h;
            InsertPlateBows bows = measureCurvature ? Bows(track, solved.Pose) : InsertPlateBows.Flat;
            var surface = measureCurvature
                ? (nx(c[0].X), ny(c[0].Y), nx(c[1].X), ny(c[1].Y), nx(c[2].X), ny(c[2].Y), nx(c[3].X), ny(c[3].Y))
                : default((double, double, double, double, double, double, double, double));
            bool had = detected.TryGetValue(frame, out ChromaQuadTracker.FrameQuad? d);
            bool trusted = had && WorstCorner(c, detections[frame]) <= RepairDistance;
            repaired.Add(new ChromaQuadTracker.FrameQuad(
                frame, nx(c[0].X), ny(c[0].Y), nx(c[1].X), ny(c[1].Y), nx(c[2].X), ny(c[2].Y), nx(c[3].X), ny(c[3].Y),
                trusted ? d!.AreaRatio : typicalArea, trusted ? d!.FillScore : typicalFill, trusted ? d!.ChromaMargin : typicalMargin,
                bows, surface,
                Component: had ? d!.Component : null, CoreMask: had ? d!.CoreMask : null, GrowMask: had ? d!.GrowMask : null,
                EdgeContrast: trusted ? d!.EdgeContrast : typicalEdge,
                Interpolated: false,
                PlateDominance: had && d!.PlateDominance > 0 ? d.PlateDominance : typicalDominance));
        }

        return (repaired, true);

        static double WorstCorner((double X, double Y)[] a, (double X, double Y)[] b)
        {
            double worst = 0;
            for (int i = 0; i < 4; i++)
                worst = Math.Max(worst, Dist(a[i], b[i]));
            return worst;
        }

        static double MedianOf(IEnumerable<double> values)
        {
            List<double> v = [.. values];
            v.Sort();
            return Median(v);
        }
    }

    /// <summary>
    /// The edge bows (the <see cref="InsertEdgeBow"/> convention: fractions of the across-the-plate
    /// vector, positive inward) that make the corner homography's Coons surface follow the model's
    /// projected edges. Flat for a flat plate.
    /// </summary>
    internal static InsertPlateBows Bows(Track track, double[] pose)
    {
        if (track.Plate.IsFlat)
            return InsertPlateBows.Flat;
        (double X, double Y)[] c = ProjectCorners(track.Camera, pose, track.Plate);
        var h = InsertSurfaceMesh.UnitSquareHomography.Solve(c[0], c[1], c[2], c[3]);
        if (h is null)
            return InsertPlateBows.Flat;
        (double X, double Y)[][] polys = ProjectEdges(track.Camera, pose, track.Plate);

        InsertEdgeBow Fit(int edge, Func<double, (double U, double V)> near, Func<double, (double U, double V)> far)
        {
            // Least squares of dev(t) = 4t(1-t) * (Bow + Skew * (2t - 1)) over 19 stations.
            double s11 = 0, s12 = 0, s22 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < 19; i++)
            {
                double t = 0.05 + 0.9 * i / 18.0;
                (double nu, double nv) = near(t);
                (double fu, double fv) = far(t);
                (double X, double Y) p = h.Map(nu, nv), q = h.Map(fu, fv);
                double dev = CrossLine(polys[edge], p, (q.X - p.X, q.Y - p.Y));
                double b1 = 4 * t * (1 - t), b2 = b1 * (2 * t - 1);
                s11 += b1 * b1; s12 += b1 * b2; s22 += b2 * b2;
                y1 += b1 * dev; y2 += b2 * dev;
            }

            double det = s11 * s22 - s12 * s12;
            return Math.Abs(det) < 1e-12 ? InsertEdgeBow.Flat : new InsertEdgeBow((y1 * s22 - y2 * s12) / det, (y2 * s11 - y1 * s12) / det);
        }

        return new InsertPlateBows(
            Top: Fit(0, t => (t, 0), t => (t, 1)),
            Bottom: Fit(2, t => (t, 1), t => (t, 0)),
            Left: Fit(3, t => (0, t), t => (1, t)),
            Right: Fit(1, t => (1, t), t => (0, t)));

        // Where the polyline crosses the line p + s * d, as s (the smallest |s| of its crossings).
        static double CrossLine((double X, double Y)[] poly, (double X, double Y) p, (double X, double Y) d)
        {
            double best = 0;
            bool found = false;
            for (int k = 0; k + 1 < poly.Length; k++)
            {
                double ex = poly[k + 1].X - poly[k].X, ey = poly[k + 1].Y - poly[k].Y;
                double den = d.X * -ey - d.Y * -ex;
                if (Math.Abs(den) < 1e-12)
                    continue;
                double rx = poly[k].X - p.X, ry = poly[k].Y - p.Y;
                double s = (rx * -ey - ry * -ex) / den;
                double t = (d.X * ry - d.Y * rx) / den;
                if (t >= -0.02 && t <= 1.02 && (!found || Math.Abs(s) < Math.Abs(best)))
                {
                    best = s;
                    found = true;
                }
            }

            return best;
        }
    }
}
