namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Sub-pixel corner fit for a chroma plate: each of the plate's four edges is located as a LINE
/// fitted through many sub-pixel boundary measurements, and the corners are where adjacent lines
/// meet. Replaces the extreme-point corner as the tracked geometry whenever it succeeds.
///
/// <para>
/// <b>Why.</b> The extreme-point fit picks ONE grid pixel per corner — the pixel minimising or
/// maximising <c>x±y</c>. That costs twice. It quantizes: on the default 320-wide grid one grid
/// pixel is ~4 source pixels on a 1344-wide clip, and which pixel wins flips from frame to frame
/// as the plate moves by fractions of a pixel, so each corner jitters independently and the
/// composited content visibly shears and wobbles. And it is biased: on a rounded-corner screen
/// the extreme pixel is the rounding's tangent point, several pixels INSIDE the true corner.
/// Measured on the project's generated phone clip against a full-resolution reference: the
/// extreme-point corners sat 5.5 px inside the plate on average and wobbled 3.3 px RMS around
/// that offset; this fit, on the same grid, sits 1.4 px from the reference and wobbles 1.9 px
/// (1.2 px on a 626-wide grid), which is about the reference's own noise.
/// </para>
///
/// <para>
/// <b>How.</b> Along the middle of each edge (corner rounding lives at the ends and is excluded),
/// a probe marches across the edge through a continuous, bilinearly interpolated <i>dominance</i>
/// signal — target channel minus the strongest rival, in absolute 0..255 units — and records
/// where it falls through half the plate's own level. That is the 50% point of the soft
/// plate/bezel transition, located to a fraction of a grid pixel from the intensity ramp rather
/// than from a binary mask. Absolute dominance, not <see cref="ChromaQuadTracker.ChromaExcess"/>'s
/// brightness-normalised purity, because the plate is usually framed by a near-black bezel where
/// purity divides small numbers by small numbers and is noise: fitting to purity put every edge
/// 3-5 px out on the bezel. A robust (Tukey-weighted) total-least-squares line through each
/// edge's crossings ignores the samples a reflection, a fingertip or a notch displaced.
/// </para>
///
/// <para>
/// Pure, deterministic and allocation-light; never throws. Returns <c>null</c> whenever the
/// evidence is too thin or the result is implausible, and the caller keeps the extreme-point
/// corners — exactly the pre-existing behaviour.
/// </para>
/// </summary>
public static class ChromaEdgeLineFitter
{
    /// <summary>
    /// A successful fit: corners in TL, TR, BR, BL order (grid pixel space), plus the largest
    /// bow any edge's crossings showed against its own fitted line — the peak of a quadratic
    /// through the residuals, as a fraction of the plate's extent ACROSS that edge (the same
    /// dimensionless convention as <see cref="InsertPlateBows"/>). Near zero means every edge is
    /// straight, i.e. the plate is flat and the four corners describe it completely.
    /// </summary>
    public readonly record struct Result((double X, double Y)[] Corners, double MaxBowFraction);

    /// <summary>Probe positions per edge, spread evenly over <see cref="EdgeSpanStart"/>..<see cref="EdgeSpanEnd"/>.</summary>
    internal const int SamplesPerEdge = 40;

    /// <summary>
    /// Fraction of each edge, measured from either end, left out of the fit. Corner rounding and
    /// the bend into the adjacent edge live there. Ten percent was measured best on the phone clip
    /// (0.15 and 0.2 lose more to fewer samples than they gain in purity).
    /// </summary>
    internal const double EdgeSpanStart = 0.10;
    internal const double EdgeSpanEnd = 0.90;

    /// <summary>Probe step along the edge normal, in grid pixels.</summary>
    private const double ProbeStep = 0.25;

    /// <summary>How far the probe reaches to either side of the coarse edge, as a fraction of the plate's shorter side (and never less than <see cref="MinProbeReach"/> grid pixels).</summary>
    private const double ProbeReachFraction = 0.12;

    private const double MinProbeReach = 4.0;

    /// <summary>Fraction of an edge's probes that must produce a crossing AND survive the robust fit for that edge to be trusted.</summary>
    private const double MinSurvivingFraction = 0.4;

    /// <summary>Tukey biweight tuning constant (in robust sigmas) and the number of reweighting passes.</summary>
    private const double TukeyC = 4.685;

    private const int RobustIterations = 4;

    /// <summary>
    /// The furthest a refined corner may sit from its extreme-point counterpart, as a fraction of
    /// the quad's longer diagonal. Corner rounding plus sub-pixel correction is a few percent;
    /// anything past this is a fit that latched onto something other than the plate's edge.
    /// </summary>
    private const double MaxCornerShiftFraction = 0.12;

    /// <summary>
    /// Fits the four edge lines of the plate whose coarse (extreme-point) corners are given, in
    /// GRID pixel coordinates (pixel centres at integer positions), and returns the refined
    /// corners in the same space, or <c>null</c> when it cannot do better than the input.
    /// </summary>
    /// <param name="dominance">Row-major per-pixel target-minus-rival dominance in 0..255 (negative allowed — clamped to 0 internally).</param>
    /// <param name="plateLevel">The plate's own typical dominance (e.g. the median over its core component). The edge is where the signal falls through half of this.</param>
    public static Result? Fit(
        float[] dominance, int width, int height, double plateLevel,
        (double X, double Y) tl, (double X, double Y) tr, (double X, double Y) br, (double X, double Y) bl)
    {
        if (width < 2 || height < 2 || dominance.Length < width * height || !(plateLevel > 1))
            return null;

        var corners = new[] { tl, tr, br, bl };
        double cx = (tl.X + tr.X + br.X + bl.X) / 4, cy = (tl.Y + tr.Y + br.Y + bl.Y) / 4;

        double shorter = Math.Min(
            Math.Min(Dist(tl, tr), Dist(bl, br)),
            Math.Min(Dist(tl, bl), Dist(tr, br)));
        if (shorter < 3)
            return null;

        double reach = Math.Max(MinProbeReach, ProbeReachFraction * shorter);
        double threshold = plateLevel / 2;

        // Edge k runs corners[k] -> corners[k+1]: top, right, bottom, left.
        var lines = new (double Px, double Py, double Dx, double Dy)[4];
        double maxBow = 0;
        for (int k = 0; k < 4; k++)
        {
            var edge = FitEdge(dominance, width, height, corners[k], corners[(k + 1) % 4], cx, cy, reach, threshold);
            if (edge is null)
                return null;
            lines[k] = edge.Value.Line;

            // Across = the length of the edges adjacent to this one, which is how far the plate
            // extends perpendicular to it.
            double across = (Dist(corners[(k + 1) % 4], corners[(k + 2) % 4]) + Dist(corners[(k + 3) % 4], corners[k])) / 2;
            maxBow = Math.Max(maxBow, edge.Value.SagittaPx / Math.Max(1, across));
        }

        // Corner k is where edge k-1 (arriving) meets edge k (leaving).
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

    /// <summary>
    /// One edge's robust line, located by probing across the edge that runs <paramref name="a"/> →
    /// <paramref name="b"/> (grid pixels) of a plate centred near (<paramref name="cx"/>,
    /// <paramref name="cy"/>) — the same probe and fit <see cref="Fit"/> uses per edge, exposed so
    /// the tracker can re-locate an edge from a TEMPORAL prior (the previous frame's edge) rather
    /// than from this frame's own, possibly occluded, extreme-point corners. <c>null</c> when too
    /// few probes crossed the edge.
    /// </summary>
    internal static (double Px, double Py, double Dx, double Dy)? FitEdgeLine(
        float[] dominance, int width, int height, double plateLevel,
        (double X, double Y) a, (double X, double Y) b, double cx, double cy, double reach)
    {
        if (width < 2 || height < 2 || dominance.Length < width * height || !(plateLevel > 1))
            return null;
        return FitEdge(dominance, width, height, a, b, cx, cy, Math.Max(MinProbeReach, reach), plateLevel / 2)?.Line;
    }

    /// <summary>Probe reach (grid pixels) <see cref="Fit"/> uses for a plate whose shorter side is <paramref name="shorterSide"/> grid pixels.</summary>
    internal static double ReachFor(double shorterSide) => Math.Max(MinProbeReach, ProbeReachFraction * shorterSide);

    /// <summary>
    /// How much a candidate edge line looks like the PLATE'S OWN boundary, 0..1: along its middle
    /// 80% (between <paramref name="a"/> and <paramref name="b"/>, the line's span on this plate),
    /// the dominance just inside the line minus the dominance just outside, over the plate's level,
    /// averaged over <see cref="SamplesPerEdge"/> probes, each clamped to 0..1.
    ///
    /// <para>
    /// What it separates, measured on the customer clips: the true edge of a screen has plate
    /// inside and a near-black bezel outside (dominance ≈ 0), so it scores close to 1 wherever it
    /// is visible and 0 only where something covers it. The two things the per-frame fit latched
    /// onto instead both score low — a line dragged onto a FINGER crossing the plate has plate on
    /// both sides along most of its length, and a line along the boundary of a green-tinted window
    /// reflection on a tablet's glass has dim plate (dominance ~25 against a level of ~140) outside
    /// it, or brighter plate (~100 against ~150) further down. Inside/outside are read
    /// <paramref name="offset"/> grid pixels from the line, past the anti-aliased ramp.
    /// </para>
    /// </summary>
    internal static double EdgeContrastScore(
        float[] dominance, int width, int height, double plateLevel,
        (double Px, double Py, double Dx, double Dy) line, (double X, double Y) a, (double X, double Y) b,
        double cx, double cy, double offset = 2.0)
    {
        if (!(plateLevel > 1))
            return 0;

        // The line's normal, pointing away from the plate's centre.
        double nx = -line.Dy, ny = line.Dx;
        if ((cx - line.Px) * nx + (cy - line.Py) * ny > 0)
        {
            nx = -nx;
            ny = -ny;
        }

        // Project the span's ends onto the line so the probes walk the line itself.
        double ta = (a.X - line.Px) * line.Dx + (a.Y - line.Py) * line.Dy;
        double tb = (b.X - line.Px) * line.Dx + (b.Y - line.Py) * line.Dy;
        double sum = 0;
        for (int i = 0; i < SamplesPerEdge; i++)
        {
            double f = EdgeSpanStart + (EdgeSpanEnd - EdgeSpanStart) * i / (SamplesPerEdge - 1);
            double t = ta + (tb - ta) * f;
            double px = line.Px + t * line.Dx, py = line.Py + t * line.Dy;
            double inside = Sample(dominance, width, height, px - offset * nx, py - offset * ny);
            double outside = Sample(dominance, width, height, px + offset * nx, py + offset * ny);
            sum += Math.Clamp((inside - outside) / plateLevel, 0, 1);
        }

        return sum / SamplesPerEdge;
    }

    /// <summary>Where two lines meet, or <c>null</c> when they are (nearly) parallel.</summary>
    internal static (double X, double Y)? IntersectLines(
        (double Px, double Py, double Dx, double Dy) l1, (double Px, double Py, double Dx, double Dy) l2) => Intersect(l1, l2);

    /// <summary>The line through two points, as a point and a unit direction.</summary>
    internal static (double Px, double Py, double Dx, double Dy) LineThrough((double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Max(1e-9, Math.Sqrt(dx * dx + dy * dy));
        return (a.X, a.Y, dx / len, dy / len);
    }

    /// <summary>True when the four corners (TL, TR, BR, BL) form a strictly convex quadrilateral.</summary>
    internal static bool IsConvexQuad((double X, double Y)[] q) => IsConvex(q);

    private static ((double Px, double Py, double Dx, double Dy) Line, double SagittaPx)? FitEdge(
        float[] dominance, int width, int height,
        (double X, double Y) a, (double X, double Y) b, double cx, double cy, double reach, double threshold)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y;
        double length = Math.Sqrt(vx * vx + vy * vy);
        if (length < 2)
            return null;
        double ux = vx / length, uy = vy / length;

        // Outward normal: perpendicular to the edge, pointing away from the plate's centre.
        double nx = uy, ny = -ux;
        if ((cx - a.X) * nx + (cy - a.Y) * ny > 0)
        {
            nx = -nx;
            ny = -ny;
        }

        int steps = (int)Math.Ceiling(2 * reach / ProbeStep);
        var xs = new List<double>(SamplesPerEdge);
        var ys = new List<double>(SamplesPerEdge);
        var ts = new List<double>(SamplesPerEdge);
        for (int i = 0; i < SamplesPerEdge; i++)
        {
            double t = EdgeSpanStart + (EdgeSpanEnd - EdgeSpanStart) * i / (SamplesPerEdge - 1);
            double px = a.X + t * vx, py = a.Y + t * vy;

            // March outward from inside the plate; the first fall through the threshold is the
            // edge. A probe that does not START above it is sitting on a hole — a reflection, a
            // fingertip, a notch — and says nothing about where this edge is.
            double s = -reach;
            double prev = Sample(dominance, width, height, px + s * nx, py + s * ny);
            if (prev < threshold)
                continue;

            for (int j = 1; j <= steps; j++)
            {
                double sNext = -reach + j * ProbeStep;
                double cur = Sample(dominance, width, height, px + sNext * nx, py + sNext * ny);
                if (cur < threshold)
                {
                    double crossing = s + (prev - threshold) / (prev - cur) * (sNext - s);
                    xs.Add(px + crossing * nx);
                    ys.Add(py + crossing * ny);
                    ts.Add(t);
                    break;
                }

                s = sNext;
                prev = cur;
            }
        }

        int minSurvivors = (int)Math.Ceiling(SamplesPerEdge * MinSurvivingFraction);
        if (xs.Count < minSurvivors)
            return null;

        var fit = RobustLine(xs, ys, minSurvivors);
        if (fit is null)
            return null;

        return (fit.Value.Line, Sagitta(xs, ys, ts, fit.Value.Line, fit.Value.Weights));
    }

    /// <summary>
    /// Peak deviation of the edge's crossings from its straight line, in pixels: a weighted
    /// quadratic <c>r = a + b·u + c·u²</c> (<c>u</c> = position along the edge minus its middle)
    /// through the signed residuals, and the bulge of that parabola between the fitted span's
    /// ends and its middle, <c>|c|·(span/2)²</c>. A straight edge reads ~0 however it is tilted,
    /// since the line already absorbed <c>a</c> and <c>b</c>.
    /// </summary>
    private static double Sagitta(
        List<double> xs, List<double> ys, List<double> ts,
        (double Px, double Py, double Dx, double Dy) line, double[] w)
    {
        // Weighted normal equations for the 3-parameter fit.
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

        // Cramer's rule on [[s0 s1 s2][s1 s2 s3][s2 s3 s4]] · [a b c] = [r0 r1 r2], solving for c.
        double det = s0 * (s2 * s4 - s3 * s3) - s1 * (s1 * s4 - s3 * s2) + s2 * (s1 * s3 - s2 * s2);
        if (Math.Abs(det) < 1e-12)
            return 0;
        double detC = s0 * (s2 * r2 - s3 * r1) - s1 * (s1 * r2 - s3 * r0) + s2 * (s1 * r1 - s2 * r0);
        double c = detC / det;
        double half = (EdgeSpanEnd - EdgeSpanStart) / 2;
        return Math.Abs(c) * half * half;
    }

    /// <summary>
    /// Total-least-squares line through the points, reweighted with Tukey's biweight against the
    /// median absolute residual so a minority of displaced crossings gets zero weight.
    /// </summary>
    private static ((double Px, double Py, double Dx, double Dy) Line, double[] Weights)? RobustLine(List<double> xs, List<double> ys, int minSurvivors)
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

            // Principal direction of the weighted scatter = the line's direction.
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

        return survivors < minSurvivors ? null : ((mx, my, dx, dy), w);
    }

    private static double MedianOf(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>Bilinear sample with edge clamping; negative dominance reads as 0 (not the target colour at all).</summary>
    private static double Sample(float[] dominance, int width, int height, double x, double y)
    {
        x = Math.Clamp(x, 0, width - 1.000001);
        y = Math.Clamp(y, 0, height - 1.000001);
        int x0 = (int)x, y0 = (int)y;
        double fx = x - x0, fy = y - y0;
        int i = y0 * width + x0;
        double v00 = Math.Max(0, dominance[i]);
        double v10 = Math.Max(0, dominance[i + 1]);
        double v01 = Math.Max(0, dominance[i + width]);
        double v11 = Math.Max(0, dominance[i + width + 1]);
        return (v00 * (1 - fx) + v10 * fx) * (1 - fy) + (v01 * (1 - fx) + v11 * fx) * fy;
    }

    private static (double X, double Y)? Intersect(
        (double Px, double Py, double Dx, double Dy) l1, (double Px, double Py, double Dx, double Dy) l2)
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

    /// <summary>
    /// Target-minus-rival in absolute channel units — the signal <see cref="Fit"/> reads edges
    /// from. Same target/rival definition as <see cref="ChromaQuadTracker.ChromaExcess"/>, without
    /// the division by brightness.
    /// </summary>
    internal static float Dominance(string color, byte r, byte g, byte b) => color switch
    {
        "blue" => b - Math.Max(r, g),
        "magenta" => Math.Min(r, b) - g,
        _ => g - Math.Max(r, b)
    };
}
