#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.EdgeSignal;

/// <summary>Where on the probe the sub-pixel edge position is read.</summary>
internal enum Crossing
{
    /// <summary>Production: half the plate's dominance level on the chroma-defined dominance signal.</summary>
    Chroma,

    /// <summary>Luma half-level crossing (midway between the plateaus either side of the chroma crossing), inside a band around it.</summary>
    LumaHalf,

    /// <summary>
    /// Luma coverage integral: the edge sits where the area under (1 - normalised luma) along the probe says it does.
    /// A box-filtered step conserves that area whatever the pixel phase, so it is the estimator least sensitive to the
    /// sampling phase.
    /// </summary>
    LumaIntegral
}

/// <summary>Settings of the prototype edge fit. <see cref="Crossing.Chroma"/> with defaults reproduces production exactly.</summary>
internal sealed record EdgeFitOptions(
    Crossing Crossing = Crossing.Chroma,
    /// <summary>Half-width, in GRID pixels, of the luma search band around the chroma crossing (or around the seed line when refining).</summary>
    double Band = 2.0,
    /// <summary>Smallest |outside - inside| luma step (0..255 levels) at which a probe trusts luma; below it the probe keeps the chroma crossing.</summary>
    double MinContrast = 16.0,
    /// <summary>Luma sampling step along the probe, GRID pixels.</summary>
    double LumaStep = 0.25,
    /// <summary>When more than half an edge's probes lack luma contrast, use the chroma crossing for the WHOLE edge (no mixing of the two on one line).</summary>
    bool PerEdgeFallback = false,
    /// <summary>Refine only: a refined corner further than this (grid px) from its seed rejects the whole re-fit (0 = off). A true correction is under a grid pixel; more means the line latched onto something else.</summary>
    double MaxRefineShift = 0,
    /// <summary>Refine only: an edge's surviving probes must cover at least three of the four quarters of the edge, else the edge keeps its seed line (no tilt from a clustered minority).</summary>
    bool RequireSpread = false);

/// <summary>Counters the fit fills while it runs (probe level), so a table can say how often luma was trusted.</summary>
internal sealed class EdgeTelemetry
{
    public long Probes;
    public long LumaProbes;
    public long Edges;
    public long EdgesWithoutLuma;
    public double SumShift;
    public double SumAbsShift;
    public long Frames;
    public long FramesFitted;

    public double LumaFraction => Probes == 0 ? double.NaN : LumaProbes / (double)Probes;

    public double MeanShift => LumaProbes == 0 ? double.NaN : SumShift / LumaProbes;

    public double MeanAbsShift => LumaProbes == 0 ? double.NaN : SumAbsShift / LumaProbes;
}

/// <summary>
/// PROTOTYPE COPY of inference/src/ReelBolt.WorkflowEngine/Services/Video/ChromaEdgeLineFitter.cs@4ecffac2f
/// (the line fit: probes, robust total-least-squares, corner intersection, plausibility checks), generalised so the
/// sub-pixel crossing can be read off a LUMA field while the chroma dominance signal still decides WHERE each edge is.
/// With <see cref="Crossing.Chroma"/> it is arithmetic-identical to the production fit (checked in EdgeFitTests).
/// Production code is untouched.
/// </summary>
internal static class EdgeFit
{
    internal const int SamplesPerEdge = 40;
    internal const double EdgeSpanStart = 0.10;
    internal const double EdgeSpanEnd = 0.90;
    private const double ProbeStep = 0.25;
    private const double ProbeReachFraction = 0.12;
    private const double MinProbeReach = 4.0;
    private const double MinSurvivingFraction = 0.4;
    private const double TukeyC = 4.685;
    private const int RobustIterations = 4;
    private const double MaxCornerShiftFraction = 0.12;

    internal readonly record struct Result((double X, double Y)[] Corners, double MaxBowFraction);

    private readonly record struct Line(double Px, double Py, double Dx, double Dy);

    /// <summary>
    /// Production <c>Fit</c> with an optional luma field. Corners TL, TR, BR, BL in GRID pixels; seeds are the corners
    /// the probes march from (production: the integer extreme points).
    /// </summary>
    public static Result? Fit(
        IScalarField dominance, IScalarField? luma, int width, int height, double plateLevel,
        (double X, double Y) tl, (double X, double Y) tr, (double X, double Y) br, (double X, double Y) bl,
        EdgeFitOptions options, EdgeTelemetry? tel = null)
    {
        if (width < 2 || height < 2 || !(plateLevel > 1))
            return null;

        var corners = new[] { tl, tr, br, bl };
        double cx = (tl.X + tr.X + br.X + bl.X) / 4, cy = (tl.Y + tr.Y + br.Y + bl.Y) / 4;
        double shorter = Math.Min(Math.Min(Dist(tl, tr), Dist(bl, br)), Math.Min(Dist(tl, bl), Dist(tr, br)));
        if (shorter < 3)
            return null;

        double reach = Math.Max(MinProbeReach, ProbeReachFraction * shorter);
        double threshold = plateLevel / 2;

        var lines = new Line[4];
        double maxBow = 0;
        for (int k = 0; k < 4; k++)
        {
            var edge = FitEdge(dominance, luma, corners[k], corners[(k + 1) % 4], cx, cy, reach, threshold, options, tel);
            if (edge is null)
                return null;
            lines[k] = edge.Value.Line;
            double across = (Dist(corners[(k + 1) % 4], corners[(k + 2) % 4]) + Dist(corners[(k + 3) % 4], corners[k])) / 2;
            maxBow = Math.Max(maxBow, edge.Value.SagittaPx / Math.Max(1, across));
        }

        return Finish(lines, corners, tl, tr, br, bl, maxBow);
    }

    /// <summary>
    /// Re-fits a plate whose edges are already known well (seeds from a previous fit) on a LUMA field only: every probe is
    /// centred on its seed edge and searched <c>±Band</c> grid pixels. A probe without luma contrast is dropped; an edge
    /// whose probes mostly fail keeps its seed line. Used for the hi-resolution second look.
    /// </summary>
    public static Result? Refine(
        IScalarField luma, int width, int height,
        (double X, double Y) tl, (double X, double Y) tr, (double X, double Y) br, (double X, double Y) bl,
        EdgeFitOptions options, EdgeTelemetry? tel = null)
    {
        if (width < 2 || height < 2)
            return null;
        var corners = new[] { tl, tr, br, bl };
        double cx = (tl.X + tr.X + br.X + bl.X) / 4, cy = (tl.Y + tr.Y + br.Y + bl.Y) / 4;
        double shorter = Math.Min(Math.Min(Dist(tl, tr), Dist(bl, br)), Math.Min(Dist(tl, bl), Dist(tr, br)));
        if (shorter < 3)
            return null;

        var lines = new Line[4];
        for (int k = 0; k < 4; k++)
        {
            (double X, double Y) a = corners[k], b = corners[(k + 1) % 4];
            double vx = b.X - a.X, vy = b.Y - a.Y, length = Math.Sqrt(vx * vx + vy * vy);
            if (length < 2)
                return null;
            double ux = vx / length, uy = vy / length;
            double nx = uy, ny = -ux;
            if ((cx - a.X) * nx + (cy - a.Y) * ny > 0)
            {
                nx = -nx;
                ny = -ny;
            }

            var xs = new List<double>(SamplesPerEdge);
            var ys = new List<double>(SamplesPerEdge);
            var quarters = new bool[4];
            for (int i = 0; i < SamplesPerEdge; i++)
            {
                double t = EdgeSpanStart + (EdgeSpanEnd - EdgeSpanStart) * i / (SamplesPerEdge - 1);
                double px = a.X + t * vx, py = a.Y + t * vy;
                if (tel is not null) tel.Probes++;
                double? s = LumaCrossing(luma, px, py, nx, ny, 0, options);
                if (s is null)
                    continue;
                if (tel is not null)
                {
                    tel.LumaProbes++;
                    tel.SumShift += s.Value;
                    tel.SumAbsShift += Math.Abs(s.Value);
                }

                xs.Add(px + s.Value * nx);
                ys.Add(py + s.Value * ny);
                quarters[Math.Min(3, i * 4 / SamplesPerEdge)] = true;
            }

            if (tel is not null) tel.Edges++;
            int minSurvivors = (int)Math.Ceiling(SamplesPerEdge * MinSurvivingFraction);
            bool spread = !options.RequireSpread || quarters.Count(q => q) >= 3;
            Line? fitted = xs.Count >= minSurvivors && spread ? RobustLine(xs, ys, minSurvivors)?.Line : null;
            if (fitted is null && tel is not null)
                tel.EdgesWithoutLuma++;
            lines[k] = fitted ?? LineThrough(a, b);
        }

        Result? result = Finish(lines, corners, tl, tr, br, bl, 0);
        if (result is { } r && options.MaxRefineShift > 0)
        {
            for (int k = 0; k < 4; k++)
            {
                if (Dist(r.Corners[k], corners[k]) > options.MaxRefineShift)
                    return null;
            }
        }

        return result;
    }

    private static Result? Finish(Line[] lines, (double X, double Y)[] corners, (double X, double Y) tl, (double X, double Y) tr, (double X, double Y) br, (double X, double Y) bl, double maxBow)
    {
        var refined = new (double X, double Y)[4];
        for (int k = 0; k < 4; k++)
        {
            var p = Intersect(lines[(k + 3) % 4], lines[k]);
            if (p is null)
                return null;
            refined[k] = p.Value;
        }

        double diagonal = Math.Max(Dist(tl, br), Dist(tr, bl));
        for (int k = 0; k < 4; k++)
        {
            if (Dist(refined[k], corners[k]) > MaxCornerShiftFraction * diagonal)
                return null;
        }

        if (!IsConvex(refined))
            return null;
        return new Result(refined, maxBow);
    }

    private static (Line Line, double SagittaPx)? FitEdge(
        IScalarField dominance, IScalarField? luma,
        (double X, double Y) a, (double X, double Y) b, double cx, double cy, double reach, double threshold,
        EdgeFitOptions options, EdgeTelemetry? tel)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y;
        double length = Math.Sqrt(vx * vx + vy * vy);
        if (length < 2)
            return null;
        double ux = vx / length, uy = vy / length;
        double nx = uy, ny = -ux;
        if ((cx - a.X) * nx + (cy - a.Y) * ny > 0)
        {
            nx = -nx;
            ny = -ny;
        }

        bool useLuma = options.Crossing != Crossing.Chroma && luma is not null;
        int steps = (int)Math.Ceiling(2 * reach / ProbeStep);
        var xs = new List<double>(SamplesPerEdge);
        var ys = new List<double>(SamplesPerEdge);
        var ts = new List<double>(SamplesPerEdge);
        var cxs = new List<double>(SamplesPerEdge);
        var cys = new List<double>(SamplesPerEdge);
        var isLuma = new List<bool>(SamplesPerEdge);
        var shifts = new List<double>(SamplesPerEdge);
        for (int i = 0; i < SamplesPerEdge; i++)
        {
            double t = EdgeSpanStart + (EdgeSpanEnd - EdgeSpanStart) * i / (SamplesPerEdge - 1);
            double px = a.X + t * vx, py = a.Y + t * vy;

            double s = -reach;
            double prev = dominance.Sample(px + s * nx, py + s * ny);
            if (prev < threshold)
                continue;

            for (int j = 1; j <= steps; j++)
            {
                double sNext = -reach + j * ProbeStep;
                double cur = dominance.Sample(px + sNext * nx, py + sNext * ny);
                if (cur < threshold)
                {
                    double crossing = s + (prev - threshold) / (prev - cur) * (sNext - s);
                    double place = crossing;
                    bool lumaUsed = false;
                    if (useLuma)
                    {
                        double? sl = LumaCrossing(luma!, px, py, nx, ny, crossing, options);
                        if (sl is not null)
                        {
                            place = sl.Value;
                            lumaUsed = true;
                        }
                    }

                    xs.Add(px + place * nx);
                    ys.Add(py + place * ny);
                    cxs.Add(px + crossing * nx);
                    cys.Add(py + crossing * ny);
                    ts.Add(t);
                    isLuma.Add(lumaUsed);
                    shifts.Add(place - crossing);
                    break;
                }

                s = sNext;
                prev = cur;
            }
        }

        int minSurvivors = (int)Math.Ceiling(SamplesPerEdge * MinSurvivingFraction);
        if (xs.Count < minSurvivors)
            return null;

        if (options.PerEdgeFallback && useLuma)
        {
            int lumaCount = 0;
            foreach (bool l in isLuma)
                lumaCount += l ? 1 : 0;
            if (lumaCount * 2 < xs.Count)
            {
                for (int i = 0; i < xs.Count; i++)
                {
                    xs[i] = cxs[i];
                    ys[i] = cys[i];
                    isLuma[i] = false;
                    shifts[i] = 0;
                }
            }
        }

        if (tel is not null)
        {
            tel.Edges++;
            bool any = false;
            for (int i = 0; i < xs.Count; i++)
            {
                tel.Probes++;
                if (isLuma[i])
                {
                    any = true;
                    tel.LumaProbes++;
                    tel.SumShift += shifts[i];
                    tel.SumAbsShift += Math.Abs(shifts[i]);
                }
            }

            if (!any)
                tel.EdgesWithoutLuma++;
        }

        var fit = RobustLine(xs, ys, minSurvivors);
        if (fit is null)
            return null;
        return (fit.Value.Line, Sagitta(xs, ys, ts, fit.Value.Line, fit.Value.Weights));
    }

    /// <summary>
    /// The luma-placed sub-pixel crossing along the probe through (px, py) with outward unit normal (nx, ny), searched
    /// <c>±Band</c> around <paramref name="center"/>; <c>null</c> when the luma step across the band is under
    /// <see cref="EdgeFitOptions.MinContrast"/> or the transition is not contained in the band.
    /// </summary>
    internal static double? LumaCrossing(IScalarField luma, double px, double py, double nx, double ny, double center, EdgeFitOptions o)
    {
        double lo = center - o.Band, hi = center + o.Band;
        double L(double s) => luma.Sample(px + s * nx, py + s * ny);

        double lin = 0.5 * (L(lo - 0.5) + L(lo - 1.0));
        double lout = 0.5 * (L(hi + 0.5) + L(hi + 1.0));
        double step = lout - lin;
        if (Math.Abs(step) < o.MinContrast)
            return null;

        // The band must contain the whole transition: both ends already on their plateau.
        if (Math.Abs(L(lo) - lin) > 0.3 * Math.Abs(step) || Math.Abs(L(hi) - lout) > 0.3 * Math.Abs(step))
            return null;

        int n = Math.Max(2, (int)Math.Ceiling((hi - lo) / o.LumaStep));
        double h = (hi - lo) / n;
        if (o.Crossing == Crossing.LumaHalf)
        {
            double mid = 0.5 * (lin + lout), sign = Math.Sign(step);
            double prevS = lo, prevV = L(lo);
            for (int i = 1; i <= n; i++)
            {
                double s = lo + i * h, v = L(s);
                if ((prevV - mid) * sign < 0 && (v - mid) * sign >= 0)
                    return prevS + (mid - prevV) / (v - prevV) * (s - prevS);
                prevS = s;
                prevV = v;
            }

            return null;
        }

        // LumaIntegral: lo + the area of the "still inside" fraction (1 - c), trapezoid rule.
        double acc = 0;
        double fPrev = 1 - (L(lo) - lin) / step;
        for (int i = 1; i <= n; i++)
        {
            double f = 1 - (L(lo + i * h) - lin) / step;
            acc += 0.5 * (fPrev + f) * h;
            fPrev = f;
        }

        return lo + acc;
    }

    // ---- verbatim from production -------------------------------------------------------------

    private static double Sagitta(List<double> xs, List<double> ys, List<double> ts, Line line, double[] w)
    {
        double s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0, r0 = 0, r1 = 0, r2 = 0;
        double mid = (EdgeSpanStart + EdgeSpanEnd) / 2;
        for (int i = 0; i < xs.Count; i++)
        {
            if (w[i] <= 0)
                continue;
            double u = ts[i] - mid;
            double r = -(xs[i] - line.Px) * line.Dy + (ys[i] - line.Py) * line.Dx;
            double u2 = u * u;
            s0 += w[i];
            s1 += w[i] * u;
            s2 += w[i] * u2;
            s3 += w[i] * u2 * u;
            s4 += w[i] * u2 * u2;
            r0 += w[i] * r;
            r1 += w[i] * r * u;
            r2 += w[i] * r * u2;
        }

        double det = s0 * (s2 * s4 - s3 * s3) - s1 * (s1 * s4 - s3 * s2) + s2 * (s1 * s3 - s2 * s2);
        if (Math.Abs(det) < 1e-12)
            return 0;
        double detC = s0 * (s2 * r2 - s3 * r1) - s1 * (s1 * r2 - s3 * r0) + s2 * (s1 * r1 - s2 * r0);
        double c = detC / det;
        double half = (EdgeSpanEnd - EdgeSpanStart) / 2;
        return Math.Abs(c) * half * half;
    }

    private static (Line Line, double[] Weights)? RobustLine(List<double> xs, List<double> ys, int minSurvivors)
    {
        int n = xs.Count;
        var w = new double[n];
        Array.Fill(w, 1.0);
        var residual = new double[n];
        double mx = 0, my = 0, dx = 0, dy = 0;

        for (int iter = 0; iter <= RobustIterations; iter++)
        {
            double sw = 0;
            mx = 0;
            my = 0;
            for (int i = 0; i < n; i++)
            {
                sw += w[i];
                mx += w[i] * xs[i];
                my += w[i] * ys[i];
            }

            if (sw <= 1e-9)
                return null;
            mx /= sw;
            my /= sw;

            double sxx = 0, sxy = 0, syy = 0;
            for (int i = 0; i < n; i++)
            {
                double ex = xs[i] - mx, ey = ys[i] - my;
                sxx += w[i] * ex * ex;
                sxy += w[i] * ex * ey;
                syy += w[i] * ey * ey;
            }

            double angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
            dx = Math.Cos(angle);
            dy = Math.Sin(angle);

            if (iter == RobustIterations)
                break;

            for (int i = 0; i < n; i++)
                residual[i] = Math.Abs(-(xs[i] - mx) * dy + (ys[i] - my) * dx);

            double sigma = Math.Max(1.4826 * MedianOf(residual), 0.05);
            for (int i = 0; i < n; i++)
            {
                double u = residual[i] / (TukeyC * sigma);
                w[i] = u >= 1 ? 0 : (1 - u * u) * (1 - u * u);
            }
        }

        int survivors = 0;
        for (int i = 0; i < n; i++)
        {
            if (w[i] > 0)
                survivors++;
        }

        return survivors < minSurvivors ? null : (new Line(mx, my, dx, dy), w);
    }

    private static double MedianOf(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static Line LineThrough((double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Max(1e-9, Math.Sqrt(dx * dx + dy * dy));
        return new Line(a.X, a.Y, dx / len, dy / len);
    }

    private static (double X, double Y)? Intersect(Line l1, Line l2)
    {
        double det = l1.Dx * -l2.Dy - l1.Dy * -l2.Dx;
        if (Math.Abs(det) < 1e-6)
            return null;
        double rx = l2.Px - l1.Px, ry = l2.Py - l1.Py;
        double t = (rx * -l2.Dy - ry * -l2.Dx) / det;
        return (l1.Px + t * l1.Dx, l1.Py + t * l1.Dy);
    }

    private static bool IsConvex((double X, double Y)[] q)
    {
        int sign = 0;
        for (int k = 0; k < 4; k++)
        {
            var a = q[k];
            var b = q[(k + 1) % 4];
            var c = q[(k + 2) % 4];
            double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            int s = Math.Sign(cross);
            if (s == 0)
                return false;
            if (sign == 0)
                sign = s;
            else if (s != sign)
                return false;
        }

        return true;
    }

    private static double Dist((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
