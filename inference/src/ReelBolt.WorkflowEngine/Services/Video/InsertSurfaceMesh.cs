namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// One edge's measured bow, expressed as a DIMENSIONLESS fraction of that edge's own
/// "across the plate" vector (the vector from a point on this edge to the matching point on the
/// opposite edge). Storing a fraction rather than pixels is what makes a bow resolution- and
/// aspect-independent: <c>ChromaPlateEdgeProfiler</c> measures it on the coarse tracking grid and
/// <see cref="InsertSurfaceMesh"/> replays it against the canonical encode frame, with no unit
/// conversion anywhere between.
///
/// <para>
/// The profile is <c>dev(t) = 4t(1-t) * (Bow + Skew*(2t-1))</c> for <c>t</c> in 0..1 along the
/// edge — deliberately ZERO at both ends, so a bow can never move a corner. The <c>4t(1-t)</c>
/// basis peaks at 1, so <see cref="Bow"/> reads directly as the mid-edge sagitta.
/// <see cref="Bow"/> is the symmetric bulge (a circular/cylindrical bend), <see cref="Skew"/> the
/// antisymmetric part (a bend that leans toward one corner, which is what a yawed curved surface
/// produces). Positive = bowed INWARD, toward the opposite edge.
/// </para>
/// </summary>
public readonly record struct InsertEdgeBow(double Bow, double Skew)
{
    public static readonly InsertEdgeBow Flat = new(0, 0);

    public bool IsFlat => Bow == 0 && Skew == 0;

    /// <summary>Peak |dev| over the edge, as the same dimensionless fraction. Used for the mesh-density heuristic and the "is this plate curved at all" gate.</summary>
    public double Magnitude
    {
        get
        {
            // Sampling 21 points is exact enough for a density heuristic and far
            // cheaper/clearer than solving the profile cubic's extrema in closed form.
            double peak = 0;
            for (int i = 0; i <= 20; i++)
            {
                double t = i / 20.0;
                peak = Math.Max(peak, Math.Abs(Evaluate(t)));
            }

            return peak;
        }
    }

    public double Evaluate(double t) => 4 * t * (1 - t) * (Bow + Skew * (2 * t - 1));
}

/// <summary>
/// The four edge bows of one tracked plate, in the same corner/edge order the rest of the insert
/// pipeline uses: corner 0 = top-left, 1 = top-right, 2 = bottom-left, 3 = bottom-right; edge
/// <see cref="Top"/> runs 0→1, <see cref="Bottom"/> runs 2→3, <see cref="Left"/> runs 0→2,
/// <see cref="Right"/> runs 1→3.
/// </summary>
public readonly record struct InsertPlateBows(
    InsertEdgeBow Top, InsertEdgeBow Right, InsertEdgeBow Bottom, InsertEdgeBow Left)
{
    public static readonly InsertPlateBows Flat = new(InsertEdgeBow.Flat, InsertEdgeBow.Flat, InsertEdgeBow.Flat, InsertEdgeBow.Flat);

    public bool IsFlat => Top.IsFlat && Right.IsFlat && Bottom.IsFlat && Left.IsFlat;

    /// <summary>Largest |dev| across the four edges — the single number that decides "planar or curved".</summary>
    public double MaxMagnitude =>
        Math.Max(Math.Max(Top.Magnitude, Right.Magnitude), Math.Max(Bottom.Magnitude, Left.Magnitude));

    /// <summary>Peak bow of the two edges running left→right (<see cref="Top"/>/<see cref="Bottom"/>) — these are the ones a COLUMN subdivision has to resolve.</summary>
    public double HorizontalMagnitude => Math.Max(Top.Magnitude, Bottom.Magnitude);

    /// <summary>Peak bow of the two edges running top→bottom (<see cref="Left"/>/<see cref="Right"/>) — resolved by a ROW subdivision.</summary>
    public double VerticalMagnitude => Math.Max(Left.Magnitude, Right.Magnitude);

    public InsertPlateBows Lerp(InsertPlateBows other, double t) => new(
        new InsertEdgeBow(Top.Bow + (other.Top.Bow - Top.Bow) * t, Top.Skew + (other.Top.Skew - Top.Skew) * t),
        new InsertEdgeBow(Right.Bow + (other.Right.Bow - Right.Bow) * t, Right.Skew + (other.Right.Skew - Right.Skew) * t),
        new InsertEdgeBow(Bottom.Bow + (other.Bottom.Bow - Bottom.Bow) * t, Bottom.Skew + (other.Bottom.Skew - Bottom.Skew) * t),
        new InsertEdgeBow(Left.Bow + (other.Left.Bow - Left.Bow) * t, Left.Skew + (other.Left.Skew - Left.Skew) * t));
}

/// <summary>
/// Pure, deterministic geometry for NON-PLANAR tracked screen inserts (see docs/video-editing.md
/// "Non-planar surfaces and multi-plate compositing"). Turns one tracked plate — four corners plus
/// four measured edge bows — into a grid of mesh control points that
/// <see cref="ScreenInsertMeshFilterBuilder"/> renders as a tiling of ffmpeg <c>perspective</c>
/// cells.
///
/// <para>
/// <b>The model, stated exactly.</b> The interior is the projective map <c>H</c> of the four
/// corners (identical to what the single-quad <c>perspective</c> path already applies), plus a
/// transfinite (Coons) blend of the four measured boundary deviations:
/// </para>
/// <code>
/// P(u,v) = H(u,v)
///        + (1-v)*devTop(u)   * (H(u,1) - H(u,0))
///        +    v *devBottom(u)* (H(u,0) - H(u,1))
///        + (1-u)*devLeft(v)  * (H(1,v) - H(0,v))
///        +    u *devRight(v) * (H(0,v) - H(1,v))
/// </code>
/// <para>
/// Two properties make this the right base. First, <b>all-flat bows reproduce the planar path
/// exactly</b>: a projective map is uniquely determined by four point correspondences, so a cell
/// whose corners are sampled from <c>H</c> refits to <c>H</c> itself — a mesh of any density over
/// a flat plate is the same warp as one big <c>perspective</c>, which is why the executor routes
/// flat plates to the original single-quad builder unchanged rather than approximating it.
/// Second, every deviation vanishes at the corners, so the four corner terms of the Coons
/// construction are identically zero and the blend needs no corner-correction term.
/// </para>
/// <para>
/// <b>What this is and is not.</b> It is an IMAGE-SPACE deformation fitted to what the chroma
/// silhouette actually shows: the outline. It is not a 3D reconstruction and does not claim to be
/// — the silhouette of a uniform-colour plate carries no texture landmarks, so the along-edge
/// foreshortening of a strongly-yawed curved surface is genuinely unmeasurable from it. What the
/// mesh does guarantee is that the composited content covers exactly the region the plate covers
/// (measured: boundary error drops from up to 54px to under 1.3px across a sweep of cylindrical
/// bends, yaws and convex bulges), which is the error that shows as green fringe. See
/// docs/video-editing.md for the measured table and the honest limits.
/// </para>
/// </summary>
public static class InsertSurfaceMesh
{
    /// <summary>A projective (homography) map of the unit square onto four destination points. Pure; built once per keyframe.</summary>
    public sealed class UnitSquareHomography
    {
        private readonly double[] _h;

        private UnitSquareHomography(double[] h) => _h = h;

        /// <summary>
        /// Solves the 8x8 system taking (0,0),(1,0),(0,1),(1,1) to
        /// <paramref name="tl"/>/<paramref name="tr"/>/<paramref name="bl"/>/<paramref name="br"/>.
        /// Returns <c>null</c> for a degenerate quad (collinear/zero-area corners), which every
        /// caller treats as "not a usable plate this frame" rather than throwing.
        /// </summary>
        public static UnitSquareHomography? Solve(
            (double X, double Y) tl, (double X, double Y) tr, (double X, double Y) bl, (double X, double Y) br)
        {
            (double U, double V)[] src = [(0, 0), (1, 0), (0, 1), (1, 1)];
            (double X, double Y)[] dst = [tl, tr, bl, br];

            // Row-major 8x9 augmented matrix, Gauss-Jordan with partial pivoting.
            var m = new double[8][];
            for (int i = 0; i < 4; i++)
            {
                (double u, double v) = src[i];
                (double x, double y) = dst[i];
                m[i * 2] = [u, v, 1, 0, 0, 0, -u * x, -v * x, x];
                m[i * 2 + 1] = [0, 0, 0, u, v, 1, -u * y, -v * y, y];
            }

            for (int c = 0; c < 8; c++)
            {
                int pivot = c;
                for (int r = c + 1; r < 8; r++)
                {
                    if (Math.Abs(m[r][c]) > Math.Abs(m[pivot][c]))
                        pivot = r;
                }

                if (Math.Abs(m[pivot][c]) < 1e-12)
                    return null;

                (m[c], m[pivot]) = (m[pivot], m[c]);

                double pv = m[c][c];
                for (int r = 0; r < 8; r++)
                {
                    if (r == c)
                        continue;

                    double f = m[r][c] / pv;
                    if (f == 0)
                        continue;

                    for (int k = c; k < 9; k++)
                        m[r][k] -= f * m[c][k];
                }
            }

            var h = new double[9];
            for (int i = 0; i < 8; i++)
                h[i] = m[i][8] / m[i][i];
            h[8] = 1.0;

            return new UnitSquareHomography(h);
        }

        public (double X, double Y) Map(double u, double v)
        {
            double w = _h[6] * u + _h[7] * v + _h[8];
            if (Math.Abs(w) < 1e-12)
                w = w < 0 ? -1e-12 : 1e-12;

            return ((_h[0] * u + _h[1] * v + _h[2]) / w, (_h[3] * u + _h[4] * v + _h[5]) / w);
        }
    }

    /// <summary>
    /// Builds the <c>(rows+1) x (cols+1)</c> control-point grid, row-major from the top-left.
    /// Coordinates are in whatever space the four corners were given in (the executor passes
    /// canonical-encode pixels). Returns <c>null</c> only for a degenerate quad.
    /// </summary>
    public static (double X, double Y)[][]? BuildControlPoints(
        (double X, double Y) tl, (double X, double Y) tr, (double X, double Y) bl, (double X, double Y) br,
        InsertPlateBows bows, int rows, int cols)
    {
        rows = Math.Max(1, rows);
        cols = Math.Max(1, cols);

        UnitSquareHomography? h = UnitSquareHomography.Solve(tl, tr, bl, br);
        if (h is null)
            return null;

        var grid = new (double X, double Y)[rows + 1][];
        for (int i = 0; i <= rows; i++)
        {
            grid[i] = new (double X, double Y)[cols + 1];
            double v = i / (double)rows;
            for (int j = 0; j <= cols; j++)
            {
                double u = j / (double)cols;
                grid[i][j] = MapPoint(h, bows, u, v);
            }
        }

        return grid;
    }

    /// <summary>
    /// One surface point — the projective base plus the Coons blend of the four edge deviations.
    /// Internal + pure so the geometry can be asserted directly against analytically-known
    /// surfaces in tests without going anywhere near ffmpeg.
    /// </summary>
    internal static (double X, double Y) MapPoint(UnitSquareHomography h, InsertPlateBows bows, double u, double v)
    {
        (double bx, double by) = h.Map(u, v);

        // Each deviation is a fraction of that edge's own across-the-plate vector, so the offset
        // is that fraction times the vector itself — no normalization, no unit conversion.
        (double X, double Y) topFar = h.Map(u, 1), topNear = h.Map(u, 0);
        (double X, double Y) leftFar = h.Map(1, v), leftNear = h.Map(0, v);

        double topDev = bows.Top.Evaluate(u);
        double bottomDev = bows.Bottom.Evaluate(u);
        double leftDev = bows.Left.Evaluate(v);
        double rightDev = bows.Right.Evaluate(v);

        double acrossVx = topFar.X - topNear.X, acrossVy = topFar.Y - topNear.Y;
        double acrossHx = leftFar.X - leftNear.X, acrossHy = leftFar.Y - leftNear.Y;

        // Weight each edge's influence by distance from it: the top edge owns v=0, the bottom
        // edge v=1, and so on. Blending this way (rather than summing unweighted) is what makes
        // the surface interpolate all four boundaries exactly.
        double ox = (1 - v) * topDev * acrossVx - v * bottomDev * acrossVx
                  + (1 - u) * leftDev * acrossHx - u * rightDev * acrossHx;
        double oy = (1 - v) * topDev * acrossVy - v * bottomDev * acrossVy
                  + (1 - u) * leftDev * acrossHy - u * rightDev * acrossHy;

        return (bx + ox, by + oy);
    }

    /// <summary>
    /// How many rows/columns this plate's measured bows need. A parabolic bow of peak
    /// <c>s</c> split into <c>n</c> equal cells leaves a per-cell chord error of <c>s/n^2</c>, so
    /// <c>n = ceil(sqrt(s / tolerance))</c> is the exact subdivision that brings the straight-chord
    /// cell edges within <paramref name="tolerancePx"/> of the measured curve. Bows on the
    /// LEFT/RIGHT edges are resolved by rows, bows on TOP/BOTTOM by columns.
    /// </summary>
    /// <param name="bows">Measured bows (dimensionless fractions).</param>
    /// <param name="acrossWidthPx">Mean top/bottom edge length in destination pixels — converts a top/bottom bow fraction to pixels.</param>
    /// <param name="acrossHeightPx">Mean left/right edge length in destination pixels.</param>
    /// <param name="tolerancePx">Target worst-case chord error per cell.</param>
    /// <param name="maxCells">Hard ceiling on <c>rows * cols</c>; the denser axis is reduced first.</param>
    public static (int Rows, int Cols) ChooseDensity(
        InsertPlateBows bows, double acrossWidthPx, double acrossHeightPx, double tolerancePx, int maxCells)
    {
        tolerancePx = Math.Max(0.05, tolerancePx);
        maxCells = Math.Max(1, maxCells);

        // A top/bottom bow is a fraction of the ACROSS-HEIGHT vector but varies along the width,
        // so it is resolved by columns; symmetrically for left/right bows and rows.
        int cols = Subdivisions(bows.HorizontalMagnitude * Math.Max(1, acrossHeightPx), tolerancePx);
        int rows = Subdivisions(bows.VerticalMagnitude * Math.Max(1, acrossWidthPx), tolerancePx);

        while (rows * cols > maxCells && (rows > 1 || cols > 1))
        {
            if (cols >= rows && cols > 1)
                cols--;
            else if (rows > 1)
                rows--;
            else
                break;
        }

        return (rows, cols);

        static int Subdivisions(double peakPx, double tol) =>
            peakPx <= tol ? 1 : Math.Clamp((int)Math.Ceiling(Math.Sqrt(peakPx / tol)), 1, 64);
    }
}
