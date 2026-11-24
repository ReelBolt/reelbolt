namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// What <see cref="ChromaPlateEdgeProfiler"/> reads off one frame's chroma silhouette: the four
/// REFINED corners of the plate, plus how far each of its four edges bows away from them. The
/// corners are refined and the bows measured in one pass because they are not separable — see the
/// class docs.
/// </summary>
public readonly record struct PlateSurface(
    (double X, double Y) Tl, (double X, double Y) Tr, (double X, double Y) Bl, (double X, double Y) Br,
    InsertPlateBows Bows);

/// <summary>
/// Measures how far a detected chroma plate's actual silhouette departs from the straight-edged
/// quadrilateral <c>ChromaQuadTracker</c> fits to it — the detection half of non-planar tracked
/// compositing (see docs/video-editing.md "Non-planar surfaces and multi-plate compositing").
/// Pure, deterministic, no I/O, no dependency beyond the boolean chroma mask the tracker already
/// built for that frame.
///
/// <para>
/// <b>What it does.</b> For each of the four edges, it walks the edge in the quad's own projective
/// parameter <c>t</c> (the same parameterization <see cref="InsertSurfaceMesh"/> replays, so
/// measurement and reconstruction cannot disagree), and at each sample marches inward-to-outward
/// along the "across the plate" direction until the mask stops being set. That gives a signed
/// deviation profile in units of the across-vector — dimensionless, so it survives the jump from
/// the coarse tracking grid to the canonical encode frame untouched.
/// </para>
/// <para>
/// <b>Why corners are refined before curvature is fitted, and why real footage forced it.</b> The
/// first version of this profiler deliberately did NOT move the tracker's corners: the deviation
/// profile was pinned to zero at both ends, which kept curvature measurement strictly separable
/// from corner-fitting accuracy. Run against the project's real green-screen clips, that was
/// simply wrong. Every real phone and laptop screen has ROUNDED corners, and the extreme-point
/// corner fit (min of x+y, and so on) lands on the rounding's 45-degree tangent point, several
/// pixels inside where the straight edges would actually meet. Relative to those inset corners,
/// every edge of a perfectly flat plate appears to bow outward — the real phone clip measured
/// 0.027 of spurious curvature against a 0.01 threshold, and a genuinely barrel-curved version of
/// the SAME clip measured 0.025. Flat and curved were indistinguishable, and the flat case was
/// over the line.
/// </para>
/// <para>
/// So the measurement is two passes. Pass one fits a FREE quadratic to each edge's deviation
/// profile over its middle band only (<see cref="CornerExclusion"/> of the edge is dropped at each
/// end, which is precisely where corner rounding lives) and extrapolates it back out to the
/// endpoints; those extrapolated values are how far each corner is inset, and each corner is moved
/// by the sum of the two contributions from the two edges that meet there — roughly orthogonal
/// components, so they compose into a proper 2D correction. Pass two re-probes the silhouette
/// against the refined quad with the profile pinned to zero at the ends again, and what is left is
/// the genuine bend. A rounded-corner rectangle comes out as "corners nudged outward, edges
/// straight"; a curved plate comes out as "corners roughly unchanged, edges bowed".
/// </para>
/// <para>
/// <b>Why a two-parameter bow and not a free-form boundary.</b> A free-form boundary would follow
/// every local dent — a phone's notch, a thumb crossing the bezel, a compression artifact along
/// the plate edge — and a Coons blend would then ripple that dent through the whole interior. The
/// symmetric+antisymmetric pair captures exactly the family that a real bend produces (a circular
/// bow, and the leaning bow a yawed bend produces) while being the wrong shape to express a notch,
/// so notches fall out as fit outliers instead of becoming geometry. It is also only four numbers
/// per frame, which keeps the per-frame track temporally stable and the artifact small.
/// </para>
/// </summary>
public static class ChromaPlateEdgeProfiler
{
    /// <summary>Samples taken along each edge.</summary>
    private const int SamplesPerEdge = 24;

    /// <summary>
    /// Fraction of each edge dropped at BOTH ends before fitting. Corner rounding lives here, and
    /// including it is what made a flat real plate read as curved. It also means the fit never
    /// sees the region where two edges' probes would disagree about the same pixels.
    /// </summary>
    private const double CornerExclusion = 0.15;

    /// <summary>How far outward/inward the probe may march, as a fraction of the across-vector, before giving up on that sample.</summary>
    private const double MaxProbeFraction = 0.28;

    /// <summary>Probe step along the across-vector, as a fraction of it. Fine enough to resolve well under one grid pixel on a typical plate.</summary>
    private const double ProbeStepFraction = 0.0025;

    /// <summary>Where along the across-vector the inside-seeking probe starts. Must be inside the plate for the march to mean anything.</summary>
    private const double ProbeSeedFraction = 0.30;

    /// <summary>Fraction of an edge's samples that must survive trimming for the edge's fit to be trusted at all.</summary>
    private const double MinSurvivingFraction = 0.6;

    /// <summary>Hard clamp on |Bow|/|Skew| — a fitted bow past this is a detection pathology, not a bend, and would produce a self-intersecting mesh.</summary>
    private const double MaxBowFraction = 0.22;

    /// <summary>Hard clamp on how far one edge may push a corner, as a fraction of its across-vector. Corner rounding is a few percent; anything larger is a bad fit, not a corner.</summary>
    private const double MaxCornerCorrection = 0.08;

    /// <summary>
    /// Above this much wanted corner correction, the whole plate's curvature is discarded and the
    /// insert keeps the tracker's own flat corner pin. Measured on the project's real clips: the
    /// flat phone plate wants 0.025 (its corner rounding), the barrel-curved one 0.055, and the
    /// laptop clip — where the extreme-point corner fit genuinely FAILS, putting "top-left"
    /// partway down the left edge — wants 0.141. See <see cref="MeasureCore"/>.
    /// </summary>
    private const double CornerCorrectionValidityLimit = 0.10;

    /// <summary>
    /// An edge's bow is only believed when the fitted profile actually EXPLAINS the boundary it
    /// was fitted to, to this RMS residual. Deliberately ABSOLUTE and not scaled by the bow's own
    /// size: the residual floor is grid quantization, which does not grow with curvature. The
    /// real clips bear that out — residuals of 0.0051 (flat phone) and 0.0041 (barrel-curved,
    /// four times the bow) against 0.0362 for the laptop's broken fit. See <see cref="FitPinned"/>.
    /// </summary>
    private const double MaxFitResidual = 0.012;

    /// <summary>
    /// Measures the refined corners and edge bows of the plate whose extreme-point corners are
    /// <paramref name="tl"/>/<paramref name="tr"/>/<paramref name="bl"/>/<paramref name="br"/>
    /// (grid pixel coordinates, the tracker's own corner order) against <paramref name="mask"/>
    /// (row-major, <paramref name="width"/> x <paramref name="height"/>). Never throws: a
    /// degenerate quad or an unreadable edge yields the input corners and a flat surface.
    /// </summary>
    public static PlateSurface Measure(
        bool[] mask, int width, int height,
        (double X, double Y) tl, (double X, double Y) tr, (double X, double Y) bl, (double X, double Y) br) =>
        MeasureCore(mask, width, height, tl, tr, bl, br, out _, out _);

    private static PlateSurface MeasureCore(
        bool[] mask, int width, int height,
        (double X, double Y) tl, (double X, double Y) tr, (double X, double Y) bl, (double X, double Y) br,
        out (double Top, double Right, double Bottom, double Left) residuals,
        out double maxCornerCorrection)
    {
        residuals = default;
        maxCornerCorrection = 0;
        var unrefined = new PlateSurface(tl, tr, bl, br, InsertPlateBows.Flat);
        if (mask.Length < width * height || width <= 0 || height <= 0)
            return unrefined;

        InsertSurfaceMesh.UnitSquareHomography? h = InsertSurfaceMesh.UnitSquareHomography.Solve(tl, tr, bl, br);
        if (h is null)
            return unrefined;

        // Pass 1 — free quadratic per edge, extrapolated to the endpoints, giving how far each
        // corner is inset by rounding.
        var ends = new Dictionary<Edge, (double Start, double End)>();
        foreach (Edge edge in new[] { Edge.Top, Edge.Right, Edge.Bottom, Edge.Left })
        {
            List<(double T, double Dev)> samples = ProbeEdge(mask, width, height, h, edge);
            ends[edge] = samples.Count < MinSamples ? (0, 0) : FreeQuadraticEndpoints(samples);
        }

        maxCornerCorrection = ends.Values
            .SelectMany(e => new[] { Math.Abs(e.Start), Math.Abs(e.End) })
            .DefaultIfEmpty(0)
            .Max();

        // If pass one wanted to shove a corner further than corner rounding could ever explain,
        // the input quad is not a quad fit to this silhouette and nothing measured against it
        // means anything. Report the plate flat: the insert then takes the original corner pin,
        // which is no worse than before this feature existed. Real footage made this necessary —
        // see the constant's own docs.
        if (maxCornerCorrection > CornerCorrectionValidityLimit)
            return unrefined;

        (double X, double Y) rTl = Move(h, tl, (Edge.Top, 0, ends[Edge.Top].Start), (Edge.Left, 0, ends[Edge.Left].Start));
        (double X, double Y) rTr = Move(h, tr, (Edge.Top, 1, ends[Edge.Top].End), (Edge.Right, 0, ends[Edge.Right].Start));
        (double X, double Y) rBl = Move(h, bl, (Edge.Bottom, 0, ends[Edge.Bottom].Start), (Edge.Left, 1, ends[Edge.Left].End));
        (double X, double Y) rBr = Move(h, br, (Edge.Bottom, 1, ends[Edge.Bottom].End), (Edge.Right, 1, ends[Edge.Right].End));

        // Pass 2 — re-probe against the refined quad with the profile pinned at both ends. What
        // survives is curvature the corner correction could not explain away.
        InsertSurfaceMesh.UnitSquareHomography? refined = InsertSurfaceMesh.UnitSquareHomography.Solve(rTl, rTr, rBl, rBr);
        if (refined is null)
            return unrefined;

        (InsertEdgeBow top, double rTop) = FitPinned(mask, width, height, refined, Edge.Top);
        (InsertEdgeBow right, double rRight) = FitPinned(mask, width, height, refined, Edge.Right);
        (InsertEdgeBow bottom, double rBottom) = FitPinned(mask, width, height, refined, Edge.Bottom);
        (InsertEdgeBow left, double rLeft) = FitPinned(mask, width, height, refined, Edge.Left);

        residuals = (rTop, rRight, rBottom, rLeft);
        return new PlateSurface(rTl, rTr, rBl, rBr, new InsertPlateBows(top, right, bottom, left));
    }

    /// <summary>
    /// <see cref="Measure"/> plus the per-edge RMS fit residuals, for calibration and for tests
    /// that need to see WHY an edge was rejected rather than only that it was.
    /// </summary>
    public static PlateSurface MeasureWithDiagnostics(
        bool[] mask, int width, int height,
        (double X, double Y) tl, (double X, double Y) tr, (double X, double Y) bl, (double X, double Y) br,
        out (double Top, double Right, double Bottom, double Left) residuals,
        out double maxCornerCorrection) =>
        MeasureCore(mask, width, height, tl, tr, bl, br, out residuals, out maxCornerCorrection);

    private static int MinSamples => (int)Math.Ceiling(SamplesPerEdge * MinSurvivingFraction);

    /// <summary>
    /// Moves one corner outward by the two contributions of the edges meeting there. Each is a
    /// displacement along that edge's own across-vector — the two are roughly perpendicular, so
    /// summing them composes a 2D correction rather than double-counting one direction.
    /// </summary>
    private static (double X, double Y) Move(
        InsertSurfaceMesh.UnitSquareHomography h,
        (double X, double Y) corner,
        (Edge Edge, double T, double Dev) a,
        (Edge Edge, double T, double Dev) b)
    {
        (double x, double y) = corner;
        foreach ((Edge edge, double t, double dev) in new[] { a, b })
        {
            double clamped = Math.Clamp(dev, -MaxCornerCorrection, MaxCornerCorrection);
            (_, (double ax, double ay)) = EdgeFrame(h, edge, t);
            x += clamped * ax;
            y += clamped * ay;
        }

        return (x, y);
    }

    internal enum Edge
    {
        Top,
        Right,
        Bottom,
        Left
    }

    /// <summary>
    /// The edge point and the "across the plate" vector at parameter <paramref name="t"/>. The
    /// across-vector always points from this edge toward the opposite one, so a positive deviation
    /// consistently means "bowed inward" on all four edges.
    /// </summary>
    internal static ((double X, double Y) Point, (double X, double Y) Across) EdgeFrame(
        InsertSurfaceMesh.UnitSquareHomography h, Edge edge, double t)
    {
        (double X, double Y) near, far;
        switch (edge)
        {
            case Edge.Top: near = h.Map(t, 0); far = h.Map(t, 1); break;
            case Edge.Bottom: near = h.Map(t, 1); far = h.Map(t, 0); break;
            case Edge.Left: near = h.Map(0, t); far = h.Map(1, t); break;
            default: near = h.Map(1, t); far = h.Map(0, t); break;
        }

        return (near, (far.X - near.X, far.Y - near.Y));
    }

    /// <summary>Probes one edge's boundary over its middle band, returning (t, deviation) samples in across-vector units.</summary>
    private static List<(double T, double Dev)> ProbeEdge(
        bool[] mask, int width, int height, InsertSurfaceMesh.UnitSquareHomography h, Edge edge)
    {
        var samples = new List<(double T, double Dev)>(SamplesPerEdge);
        for (int i = 0; i < SamplesPerEdge; i++)
        {
            double t = CornerExclusion + (1 - 2 * CornerExclusion) * i / (SamplesPerEdge - 1.0);
            ((double X, double Y) point, (double X, double Y) across) = EdgeFrame(h, edge, t);

            double acrossLen = Math.Sqrt(across.X * across.X + across.Y * across.Y);
            if (acrossLen < 1e-9)
                continue;

            double? dev = ProbeBoundary(mask, width, height, point, across);
            if (dev is not null)
                samples.Add((t, dev.Value + 0.5 / acrossLen));
        }

        return samples;
    }

    /// <summary>
    /// Fits <c>dev(t) = c0 + c1*t + c2*t^2</c> to the middle-band samples (one trimming pass) and
    /// returns the value extrapolated to <c>t = 0</c> and <c>t = 1</c> — how far each end of this
    /// edge sits from the corner the tracker picked.
    /// </summary>
    internal static (double Start, double End) FreeQuadraticEndpoints(IReadOnlyList<(double T, double Dev)> samples)
    {
        double[] fit = SolveQuadratic(samples);
        List<(double T, double Dev)> kept = Trim(samples, t => fit[0] + fit[1] * t + fit[2] * t * t);
        if (kept.Count >= MinSamples)
            fit = SolveQuadratic(kept);

        return (fit[0], fit[0] + fit[1] + fit[2]);
    }

    private static double[] SolveQuadratic(IReadOnlyList<(double T, double Dev)> samples)
    {
        // Normal equations for the basis {1, t, t^2}, solved by Gauss-Jordan on a 3x4 matrix.
        var a = new double[3, 4];
        foreach ((double t, double dev) in samples)
        {
            double[] basis = [1, t, t * t];
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                    a[r, c] += basis[r] * basis[c];
                a[r, 3] += basis[r] * dev;
            }
        }

        for (int c = 0; c < 3; c++)
        {
            int pivot = c;
            for (int r = c + 1; r < 3; r++)
            {
                if (Math.Abs(a[r, c]) > Math.Abs(a[pivot, c]))
                    pivot = r;
            }

            if (Math.Abs(a[pivot, c]) < 1e-12)
                return [0, 0, 0];

            if (pivot != c)
            {
                for (int k = c; k < 4; k++)
                    (a[c, k], a[pivot, k]) = (a[pivot, k], a[c, k]);
            }

            double pv = a[c, c];
            for (int r = 0; r < 3; r++)
            {
                if (r == c)
                    continue;

                double f = a[r, c] / pv;
                for (int k = c; k < 4; k++)
                    a[r, k] -= f * a[c, k];
            }
        }

        return [a[0, 3] / a[0, 0], a[1, 3] / a[1, 1], a[2, 3] / a[2, 2]];
    }

    /// <summary>
    /// The final, pinned bow fit for one edge, plus its RMS residual — and the gate that throws
    /// the fit away when the residual says the model does not describe this boundary.
    ///
    /// <para>
    /// The gate exists because of a real-footage failure, not as belt-and-braces. The project's
    /// laptop clip defeats the tracker's extreme-point corner fit: the plate is a strong
    /// trapezoid, and the "top-left" extreme lands partway down the left edge, so the fitted quad
    /// cuts the whole upper-left triangle off the plate. The silhouette then genuinely does sit
    /// far outside that quad's top edge, and the profiler dutifully measured a 0.118 bow. Meshing
    /// on that reading made the composite WORSE than the flat pin — it bent the top edge into a
    /// parabola that matches a triangle nowhere and crushed the interior. The tell is that a
    /// straight boundary offset from a wrongly-placed corner is a RAMP in <c>t</c>, and a profile
    /// pinned to zero at both ends cannot be a ramp, so the residual is large: 0.0362 there,
    /// against 0.0051 and 0.0041 on the two clips whose corners ARE right. This is the
    /// second of the two independent gates on that failure — the first, in
    /// <see cref="MeasureCore"/>, catches it by how far the corner wanted to move.
    /// </para>
    /// </summary>
    private static (InsertEdgeBow Bow, double Residual) FitPinned(
        bool[] mask, int width, int height, InsertSurfaceMesh.UnitSquareHomography h, Edge edge)
    {
        List<(double T, double Dev)> samples = ProbeEdge(mask, width, height, h, edge);
        if (samples.Count < MinSamples)
            return (InsertEdgeBow.Flat, 0);

        InsertEdgeBow fit = LeastSquares(samples);
        List<(double T, double Dev)> kept = Trim(samples, t => fit.Evaluate(t));
        if (kept.Count >= MinSamples)
            fit = LeastSquares(kept);
        else
            kept = samples.ToList();

        var clamped = new InsertEdgeBow(
            Math.Clamp(fit.Bow, -MaxBowFraction, MaxBowFraction),
            Math.Clamp(fit.Skew, -MaxBowFraction, MaxBowFraction));

        double sumSq = kept.Sum(s => Math.Pow(s.Dev - clamped.Evaluate(s.T), 2));
        double residual = Math.Sqrt(sumSq / kept.Count);

        return residual > MaxFitResidual ? (InsertEdgeBow.Flat, residual) : (clamped, residual);
    }

    /// <summary>
    /// Drops samples more than 2.5 median-absolute residuals off the current fit — a notch, a
    /// thumb, a mask speck. Deliberately ONE pass wherever it is used: iterating to convergence
    /// would let a large enough occlusion pull the model onto the occluder.
    /// </summary>
    private static List<(double T, double Dev)> Trim(
        IReadOnlyList<(double T, double Dev)> samples, Func<double, double> model)
    {
        var residuals = samples.Select(s => Math.Abs(s.Dev - model(s.T))).OrderBy(r => r).ToList();
        double median = residuals[residuals.Count / 2];
        if (median <= 1e-9)
            return samples.ToList();

        double cutoff = median * 2.5;
        return samples.Where(s => Math.Abs(s.Dev - model(s.T)) <= cutoff).ToList();
    }

    /// <summary>
    /// Least-squares fit of <c>dev(t) = 4t(1-t)*(Bow + Skew*(2t-1))</c> — a 2x2 normal system in
    /// the basis <c>b0 = 4t(1-t)</c>, <c>b1 = 4t(1-t)(2t-1)</c>, solved in closed form.
    /// </summary>
    internal static InsertEdgeBow LeastSquares(IReadOnlyList<(double T, double Dev)> samples)
    {
        double a00 = 0, a01 = 0, a11 = 0, r0 = 0, r1 = 0;
        foreach ((double t, double dev) in samples)
        {
            double b0 = 4 * t * (1 - t);
            double b1 = b0 * (2 * t - 1);
            a00 += b0 * b0;
            a01 += b0 * b1;
            a11 += b1 * b1;
            r0 += b0 * dev;
            r1 += b1 * dev;
        }

        double det = a00 * a11 - a01 * a01;
        if (Math.Abs(det) < 1e-12)
            return a00 > 1e-12 ? new InsertEdgeBow(r0 / a00, 0) : InsertEdgeBow.Flat;

        return new InsertEdgeBow((r0 * a11 - r1 * a01) / det, (a00 * r1 - a01 * r0) / det);
    }

    /// <summary>
    /// Finds the mask boundary along <paramref name="point"/> + s * <paramref name="across"/>,
    /// returning <c>s</c> (a fraction of the across-vector; positive = the plate starts INSIDE the
    /// fitted edge, negative = it bulges past it). Marches from a seed known to be inside the
    /// plate back toward — and if necessary past — the fitted edge, so it reads a bow of either
    /// sign. Returns <c>null</c> when the seed is not on the plate at all, which is what makes an
    /// occluded or badly-fitted edge drop out as a missing sample rather than a wrong number.
    ///
    /// <para>
    /// The value returned sits at the mask's OUTER EDGE — halfway between the last pixel whose
    /// centre is set and the first whose centre is not. The corners it is measured against are
    /// pixel CENTRES, so the caller adds half a pixel back before fitting. Without that the
    /// half-pixel gap is a constant offset on every sample of an edge, and a profile pinned to
    /// zero at both ends can only absorb a constant as curvature: a perfectly straight edge came
    /// out with a measured bow of 0.0075, most of the way to the 0.01 default that decides a plate
    /// is curved at all.
    /// </para>
    /// </summary>
    internal static double? ProbeBoundary(
        bool[] mask, int width, int height, (double X, double Y) point, (double X, double Y) across)
    {
        if (!Sample(mask, width, height, point, across, ProbeSeedFraction))
            return null;

        double step = ProbeStepFraction;
        double lastInside = ProbeSeedFraction;
        for (double s = ProbeSeedFraction - step; s >= -MaxProbeFraction; s -= step)
        {
            if (Sample(mask, width, height, point, across, s))
            {
                lastInside = s;
                continue;
            }

            // Boundary lies between s (outside) and lastInside (inside); take the midpoint.
            return (s + lastInside) / 2;
        }

        return null;
    }

    private static bool Sample(
        bool[] mask, int width, int height, (double X, double Y) point, (double X, double Y) across, double s)
    {
        int x = (int)Math.Round(point.X + across.X * s);
        int y = (int)Math.Round(point.Y + across.Y * s);
        if (x < 0 || y < 0 || x >= width || y >= height)
            return false;

        return mask[y * width + x];
    }
}
