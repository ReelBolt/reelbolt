using FrameQuad = ReelBolt.WorkflowEngine.Services.Video.ChromaQuadTracker.FrameQuad;

namespace ReelBolt.WorkflowEngine.Services.Video.Tracking;

/// <summary>
/// The temporal smoother of a tracked screen-insert plate (initiative insert-tracking-smoothness, F2b): a robust, motion-adaptive local
/// quadratic regression that is aware of held (repeated) source frames. It replaces the 3-frame box blend that switched itself off whenever the
/// plate moved, which left exactly the motion the viewer watches unsmoothed (and, on generated footage that holds a frame and then jumps, made
/// the track worse than the raw per-frame fits).
///
/// <para>
/// <b>Per frame</b>, for each candidate half-width H in {4, 6, 8, 12} frames, a degree-2 polynomial is fitted to the plate's corner series over
/// the window with tricube x Tukey-bisquare weights (c = 4.685, two reweighting passes; ONE robust weight per quad, driven by its joint residual
/// over the eight pinned-corner coordinates, so a bad frame is dropped as a whole). The widest H whose robust weighted residual RMS is at most
/// <see cref="Tau"/> sigma wins: a plate moving along a smooth path is explained to noise level by the widest window, while a kink fails every
/// window that straddles it and falls back to the narrowest. If even the narrowest window leaves more than <see cref="RawAbove"/> sigma the sample
/// is passed through RAW (a hard cut mid-move, a whip's start or stop must not be rounded off). The chosen width index is median-filtered over
/// +-<see cref="SelectionMedianHalf"/> frames and may only narrow.
/// </para>
///
/// <para>
/// <b>One fit per frame for the whole plate.</b> The pinned corners (the silhouette-refined surface corners when the track measured them, else
/// the fitted corners), the other corner set, and the eight edge bow/skew values are all fitted with the SAME window, widths and robust weights;
/// the width and weights are driven by the pinned corners alone. Smoothing the bows or the corners alone makes a curved plate worse (a bow measured
/// against corners that moved is an inconsistent pair), smoothing them together removes the content slip.
/// </para>
///
/// <para>
/// <b>Held frames.</b> Generated clips are often frame-rate converted by repeating frames, so the plate's true geometry changes only on the first
/// frame of a repeat group. Frames flagged held (<see cref="DetectHeld"/>) are grouped (at most <see cref="MaxHeldGroup"/>), averaged into one
/// sample stamped at the group's first frame, smoothed as a series of irregular time stamps, and expanded back as a STEP: every frame of a group
/// gets the group's value. The steps are kept, not smeared into a ramp, and the noise of a group of m repeats drops by sqrt(m).
/// </para>
///
/// <para>
/// <b>Noise unit.</b> sigma (source pixels) = max(<see cref="SigmaFloor"/>, the track's own third-difference sigma,
/// <see cref="SigmaGridFraction"/> x source pixels per detection-grid pixel). The outlier pass' noise estimate is NOT used: measured 2-14x the
/// true white sigma. Measured on the synthetic scene matrix: slow-push SlipRms 1.031 to 0.074, hold-cadence 1.835 to 0.475, whip 2.277 to 0.108
/// (ErrMax 20.8 to 1.7 px) with the hinge outlier pass, lag at most 0.07 frames.
/// </para>
/// </summary>
internal static class InsertTrackSmoother
{
    /// <summary>Polynomial degree of the local fit.</summary>
    internal const int Degree = 2;

    /// <summary>Candidate half-widths in frames, narrowest first.</summary>
    internal static readonly int[] HalfWidths = [4, 6, 8, 12];

    /// <summary>A width is accepted when its robust residual RMS is at most this many sigma.</summary>
    internal const double Tau = 2.0;

    /// <summary>When even the narrowest window leaves more than this many sigma, the sample is a kink and passes through raw.</summary>
    internal const double RawAbove = 6.0;

    /// <summary>Tukey-bisquare tuning constant.</summary>
    internal const double TukeyC = 4.685;

    /// <summary>Reweighting passes after the first fit.</summary>
    internal const int RobustIterations = 2;

    /// <summary>The chosen width index is median-filtered over +- this many frames (it may only narrow).</summary>
    internal const int SelectionMedianHalf = 2;

    /// <summary>Largest run of repeated frames collapsed into one sample.</summary>
    internal const int MaxHeldGroup = 4;

    /// <summary>Smallest sigma, in source pixels.</summary>
    internal const double SigmaFloor = 0.05;

    /// <summary>The tracker's accuracy prior as a fraction of one detection-grid pixel.</summary>
    internal const double SigmaGridFraction = 0.10;

    private const int Coords = 8;
    private const int BowValues = 8;
    private const int PolyTerms = Degree + 1;

    /// <summary>
    /// Smooths one assembled, uprighted track. <paramref name="held"/> is indexed by frame index (<see cref="DetectHeld"/>; a frame index beyond
    /// its length is not held). <paramref name="sourcePxPerGridPx"/> converts the grid to source pixels for the noise unit (and scales both
    /// axes: <paramref name="gridWidth"/> x <paramref name="gridHeight"/> is the grid the normalized corners refer to; pass 1 when the grid is
    /// the source). Only corners, surface corners and bows change; every other field of every frame is kept, and a frame passed through raw
    /// is returned untouched. Fewer than five frames are returned unchanged.
    /// </summary>
    internal static List<FrameQuad> Smooth(IReadOnlyList<FrameQuad> run, bool[] held, double sourcePxPerGridPx, int gridWidth, int gridHeight)
    {
        int count = run.Count;
        if (count < 5 || gridWidth <= 0 || gridHeight <= 0)
            return [.. run];

        // A frame with a non-finite value anywhere is a missing sample: it is left out of every fit and comes back as its own input.
        if (run.Any(q => !IsFinite(q)))
        {
            var finiteIdx = new List<int>(count);
            for (int i = 0; i < count; i++)
            {
                if (IsFinite(run[i]))
                    finiteIdx.Add(i);
            }

            List<FrameQuad> fitted = Smooth([.. finiteIdx.Select(i => run[i])], held, sourcePxPerGridPx, gridWidth, gridHeight);
            var merged = new List<FrameQuad>(run);
            for (int k = 0; k < finiteIdx.Count; k++)
                merged[finiteIdx[k]] = fitted[k];
            return merged;
        }

        double ratio = sourcePxPerGridPx > 0 ? sourcePxPerGridPx : 1.0;
        double sx = gridWidth * ratio, sy = gridHeight * ratio;
        bool hasSurface = run.All(q => q.Surface != default);

        // Repeat groups: a held frame joins the group of the frame before it, up to MaxHeldGroup.
        var groups = new List<List<int>>(count);
        for (int i = 0; i < count; i++)
        {
            int frame = run[i].FrameIndex;
            bool heldFrame = frame >= 0 && frame < held.Length && held[frame];
            if (i > 0 && heldFrame && groups[^1].Count < MaxHeldGroup && run[i - 1].FrameIndex == frame - 1)
                groups[^1].Add(i);
            else
                groups.Add([i]);
        }

        // Series: [0, 8) the pinned corners (drive width and weights), then the other corner set, then the eight bow values.
        int total = Coords + (hasSurface ? Coords : 0) + BowValues;
        int n = groups.Count;
        var t = new double[n];
        double[][] y = new double[total][];
        for (int c = 0; c < total; c++)
            y[c] = new double[n];

        var values = new double[total];
        for (int g = 0; g < n; g++)
        {
            Array.Clear(values);
            foreach (int i in groups[g])
            {
                Read(run[i], hasSurface, sx, sy, values);
                for (int c = 0; c < total; c++)
                    y[c][g] += values[c];
            }

            double inv = 1.0 / groups[g].Count;
            for (int c = 0; c < total; c++)
                y[c][g] *= inv;
            t[g] = run[groups[g][0]].FrameIndex;
        }

        double sigma = Math.Max(Math.Max(SigmaFloor, SigmaGridFraction * ratio), ThirdDifferenceSigma(y, Coords));
        double[][] smoothed = Fit(t, y, Coords, sigma, out bool[] raw);

        var byIndex = new FrameQuad[count];
        for (int g = 0; g < n; g++)
        {
            if (raw[g] && groups[g].Count == 1)
            {
                byIndex[groups[g][0]] = run[groups[g][0]];
                continue;
            }

            for (int c = 0; c < total; c++)
                values[c] = smoothed[c][g];
            foreach (int i in groups[g])
                byIndex[i] = Write(run[i], hasSurface, sx, sy, values);
        }

        return [.. byIndex];
    }

    /// <summary>
    /// Flags held source frames from the detection grid (<paramref name="frames"/> RGB24 frames of <paramref name="gw"/> x <paramref name="gh"/>).
    /// For each frame of <paramref name="run"/> the mean absolute difference (R+G+B levels) against the previous grid frame is taken at points one
    /// grid pixel apart along the four edges of the tracked quad, one pixel either side of the edge; frame f is held when that number is at most
    /// 0.3 x the largest of frames f-2..f+2 (a repeat sits next to the frame that introduced the new geometry; a smoothly decelerating plate has a
    /// ratio near 1 and is never flagged) or at most 0.2 levels (nothing moved at all). A held frame's edge difference is not zero (codec residue),
    /// so a fixed floor cannot work. Frames outside the run are never flagged. Length <paramref name="frames"/>.
    /// </summary>
    internal static bool[] DetectHeld(byte[] grid, int frames, int gw, int gh, IReadOnlyList<FrameQuad> run)
    {
        var held = new bool[Math.Max(0, frames)];
        if (frames < 2 || gw <= 0 || gh <= 0 || run.Count == 0)
            return held;

        int frameSize = gw * gh * 3;
        if (grid.Length < (long)frames * frameSize)
            frames = grid.Length / frameSize;
        if (frames < 2)
            return held;

        var edge = new double[frames];
        var present = new bool[frames];
        foreach (FrameQuad q in run)
        {
            int f = q.FrameIndex;
            if (f < 1 || f >= frames)
                continue;
            (double X, double Y) tl = (q.X0 * gw, q.Y0 * gh), tr = (q.X1 * gw, q.Y1 * gh), bl = (q.X2 * gw, q.Y2 * gh), br = (q.X3 * gw, q.Y3 * gh);
            double strip = 0;
            int samples = 0;
            for (int side = 0; side < 4; side++)
            {
                (double X, double Y) a = side switch { 0 => tl, 1 => tr, 2 => br, _ => bl };
                (double X, double Y) b = side switch { 0 => tr, 1 => br, 2 => bl, _ => tl };
                double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                if (len < 2)
                    continue;
                double nx = -(b.Y - a.Y) / len, ny = (b.X - a.X) / len;
                int steps = (int)Math.Floor(len);
                for (int k = 0; k < steps; k++)
                {
                    double px = a.X + (b.X - a.X) * (k + 0.5) / steps, py = a.Y + (b.Y - a.Y) * (k + 0.5) / steps;
                    for (int s = -1; s <= 1; s += 2)
                    {
                        double d = LevelDifference(grid, f, frameSize, gw, gh, px + nx * s, py + ny * s);
                        if (!double.IsNaN(d))
                        {
                            strip += d;
                            samples++;
                        }
                    }
                }
            }

            if (samples == 0)
                continue;
            edge[f] = strip / samples;
            present[f] = true;
        }

        for (int f = 1; f < frames; f++)
        {
            if (!present[f])
                continue;
            double peak = 0;
            for (int j = Math.Max(1, f - 2); j <= Math.Min(frames - 1, f + 2); j++)
            {
                if (j != f)
                    peak = Math.Max(peak, edge[j]);
            }

            held[f] = edge[f] <= 0.2 || edge[f] <= 0.3 * peak;
        }

        return held;
    }

    private static double LevelDifference(byte[] grid, int f, int frameSize, int gw, int gh, double x, double y)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        if (ix < 0 || iy < 0 || ix >= gw || iy >= gh)
            return double.NaN;
        int a = f * frameSize + (iy * gw + ix) * 3, b = a - frameSize;
        return Math.Abs(grid[a] - grid[b]) + Math.Abs(grid[a + 1] - grid[b + 1]) + Math.Abs(grid[a + 2] - grid[b + 2]);
    }

    // ---- series <-> FrameQuad ------------------------------------------------------------------------------------------------------------

    private static bool IsFinite(FrameQuad q)
    {
        var s = q.Surface;
        InsertPlateBows b = q.Bows;
        return double.IsFinite(q.X0 + q.Y0 + q.X1 + q.Y1 + q.X2 + q.Y2 + q.X3 + q.Y3)
            && double.IsFinite(s.Item1 + s.Item2 + s.Item3 + s.Item4 + s.Item5 + s.Item6 + s.Item7 + s.Item8)
            && double.IsFinite(b.Top.Bow + b.Top.Skew + b.Right.Bow + b.Right.Skew + b.Bottom.Bow + b.Bottom.Skew + b.Left.Bow + b.Left.Skew);
    }

    private static void Read(FrameQuad q, bool hasSurface, double sx, double sy, double[] v)
    {
        int o = 0;
        if (hasSurface)
        {
            var s = q.Surface;
            o = PutCorners(v, o, sx, sy, s.X0, s.Y0, s.X1, s.Y1, s.X2, s.Y2, s.X3, s.Y3);
        }

        // With a surface the pinned corners come first (above) and the fitted corners ride along; without one the fitted corners ARE the pinned ones.
        o = PutCorners(v, o, sx, sy, q.X0, q.Y0, q.X1, q.Y1, q.X2, q.Y2, q.X3, q.Y3);
        InsertPlateBows b = q.Bows;
        v[o++] = b.Top.Bow; v[o++] = b.Top.Skew;
        v[o++] = b.Right.Bow; v[o++] = b.Right.Skew;
        v[o++] = b.Bottom.Bow; v[o++] = b.Bottom.Skew;
        v[o++] = b.Left.Bow; v[o] = b.Left.Skew;
    }

    private static int PutCorners(double[] v, int o, double sx, double sy, double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3)
    {
        v[o++] = x0 * sx; v[o++] = y0 * sy; v[o++] = x1 * sx; v[o++] = y1 * sy;
        v[o++] = x2 * sx; v[o++] = y2 * sy; v[o++] = x3 * sx; v[o++] = y3 * sy;
        return o;
    }

    private static FrameQuad Write(FrameQuad q, bool hasSurface, double sx, double sy, double[] v)
    {
        int o = 0;
        (double, double, double, double, double, double, double, double) surface = q.Surface;
        if (hasSurface)
        {
            surface = (v[0] / sx, v[1] / sy, v[2] / sx, v[3] / sy, v[4] / sx, v[5] / sy, v[6] / sx, v[7] / sy);
            o = Coords;
        }

        int c = o;
        var bows = new InsertPlateBows(
            new InsertEdgeBow(v[c + 8], v[c + 9]),
            new InsertEdgeBow(v[c + 10], v[c + 11]),
            new InsertEdgeBow(v[c + 12], v[c + 13]),
            new InsertEdgeBow(v[c + 14], v[c + 15]));
        return q with
        {
            X0 = v[c] / sx,
            Y0 = v[c + 1] / sy,
            X1 = v[c + 2] / sx,
            Y1 = v[c + 3] / sy,
            X2 = v[c + 4] / sx,
            Y2 = v[c + 5] / sy,
            X3 = v[c + 6] / sx,
            Y3 = v[c + 7] / sy,
            Surface = surface,
            Bows = bows
        };
    }

    // ---- robust local polynomial core ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Per-sample robust local quadratic over irregular time stamps <paramref name="t"/> (frames). The first <paramref name="driving"/> series drive
    /// the width and the robust weights; all series are fitted with them. <paramref name="raw"/>[i] is true when the sample passed through unfitted.
    /// </summary>
    private static double[][] Fit(double[] t, double[][] y, int driving, double sigma, out bool[] raw)
    {
        int n = t.Length, ns = y.Length, widths = HalfWidths.Length;
        var fitValue = new double[widths][][];
        var fitRms = new double[widths][];
        var scratch = new FitScratch(ns);
        for (int wi = 0; wi < widths; wi++)
        {
            fitValue[wi] = new double[ns][];
            for (int c = 0; c < ns; c++)
                fitValue[wi][c] = new double[n];
            fitRms[wi] = new double[n];
            FitWidth(t, y, driving, sigma, HalfWidths[wi], fitValue[wi], fitRms[wi], scratch);
        }

        // Widest accepted width; else the narrowest, unless even that misfits grossly, in which case raw (-1).
        var pick = new int[n];
        for (int i = 0; i < n; i++)
        {
            int sel = -1;
            for (int wi = widths - 1; wi >= 0; wi--)
            {
                if (fitRms[wi][i] <= Tau * sigma)
                {
                    sel = wi;
                    break;
                }
            }

            if (sel < 0)
                sel = fitRms[0][i] > RawAbove * sigma ? -1 : 0;
            pick[i] = sel;
        }

        // The median of the neighbours' picks may narrow a frame's width but never widen it beyond what it accepted itself.
        var narrowed = new int[n];
        var window = new List<int>(2 * SelectionMedianHalf + 1);
        for (int i = 0; i < n; i++)
        {
            if (pick[i] < 0)
            {
                narrowed[i] = -1;
                continue;
            }

            window.Clear();
            for (int j = Math.Max(0, i - SelectionMedianHalf); j <= Math.Min(n - 1, i + SelectionMedianHalf); j++)
                window.Add(pick[j] < 0 ? -1 : pick[j]);
            window.Sort();
            narrowed[i] = Math.Min(pick[i], window[window.Count / 2]);
        }

        var result = new double[ns][];
        for (int c = 0; c < ns; c++)
            result[c] = (double[])y[c].Clone();
        raw = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (narrowed[i] < 0)
            {
                raw[i] = true;
                continue;
            }

            for (int c = 0; c < ns; c++)
                result[c][i] = fitValue[narrowed[i]][c][i];
        }

        return result;
    }

    private sealed class FitScratch(int series)
    {
        public readonly double[] Coef = new double[series * PolyTerms];
        public readonly double[] Tri = new double[64];
        public readonly double[] Robust = new double[64];
        public readonly double[] Combined = new double[64];
        public readonly double[] Resid = new double[64];
        public readonly double[] Sorted = new double[64];
    }

    private static void FitWidth(double[] t, double[][] y, int driving, double sigma, int half, double[][] value, double[] rms, FitScratch s)
    {
        int n = t.Length, ns = y.Length;
        for (int i = 0; i < n; i++)
        {
            int from = i, to = i;
            while (from > 0 && t[i] - t[from - 1] <= half)
                from--;
            while (to < n - 1 && t[to + 1] - t[i] <= half)
                to++;
            int count = to - from + 1;
            for (int j = from; j <= to; j++)
            {
                double u = Math.Abs(t[j] - t[i]) / (half + 1.0);
                double k = 1 - u * u * u;
                int w = j - from;
                s.Tri[w] = k * k * k;
                s.Robust[w] = 1.0;
            }

            bool ok = count >= PolyTerms + 1;
            double fitRms = double.PositiveInfinity;
            for (int it = 0; it <= RobustIterations && ok; it++)
            {
                for (int w = 0; w < count; w++)
                    s.Combined[w] = s.Tri[w] * s.Robust[w];
                bool last = it == RobustIterations;
                ok = FitPolynomial(t, y, last ? ns : driving, from, count, t[i], s.Combined, s.Coef);
                if (!ok)
                    break;

                // Joint residual of each sample over the driving series.
                double sumW = 0, sumWr2 = 0;
                for (int j = from; j <= to; j++)
                {
                    int w = j - from;
                    double u = t[j] - t[i], r2 = 0;
                    for (int c = 0; c < driving; c++)
                    {
                        int b = c * PolyTerms;
                        double d = y[c][j] - (s.Coef[b] + u * (s.Coef[b + 1] + u * s.Coef[b + 2]));
                        r2 += d * d;
                    }

                    s.Resid[w] = Math.Sqrt(r2 / driving);
                    sumW += s.Combined[w];
                    sumWr2 += s.Combined[w] * s.Resid[w] * s.Resid[w];
                }

                fitRms = sumW > 0 ? Math.Sqrt(sumWr2 / sumW) : double.PositiveInfinity;
                if (last)
                    break;

                double scale = Math.Max(sigma, 1.4826 * Median(s.Resid, count, s.Sorted));
                for (int w = 0; w < count; w++)
                {
                    double z = s.Resid[w] / (TukeyC * scale);
                    s.Robust[w] = z >= 1 ? 0.0 : (1 - z * z) * (1 - z * z);
                }
            }

            if (!ok)
            {
                for (int c = 0; c < ns; c++)
                    value[c][i] = y[c][i];
                rms[i] = double.PositiveInfinity;
            }
            else
            {
                for (int c = 0; c < ns; c++)
                    value[c][i] = s.Coef[c * PolyTerms];
                rms[i] = fitRms;
            }
        }
    }

    /// <summary>
    /// Weighted least-squares quadratic y(t) = c0 + c1 u + c2 u^2 (u = t - tc) over <paramref name="count"/> samples starting at <paramref name="from"/>
    /// for the first <paramref name="series"/> series; false when the normal equations are singular. Coefficients land in <paramref name="coef"/>[series * 3 + k].
    /// </summary>
    private static bool FitPolynomial(double[] t, double[][] y, int series, int from, int count, double tc, double[] w, double[] coef)
    {
        Span<double> a = stackalloc double[PolyTerms * PolyTerms];
        Span<double> pw = stackalloc double[2 * PolyTerms];
        a.Clear();
        for (int k = 0; k < count; k++)
        {
            double wk = w[k];
            if (wk <= 0)
                continue;
            double u = t[from + k] - tc, p = 1;
            for (int e = 0; e < 2 * PolyTerms - 1; e++)
            {
                pw[e] = p;
                p *= u;
            }

            for (int r = 0; r < PolyTerms; r++)
            {
                for (int c = 0; c < PolyTerms; c++)
                    a[r * PolyTerms + c] += wk * pw[r + c];
            }
        }

        Span<double> inv = stackalloc double[PolyTerms * PolyTerms];
        if (!Invert(a, inv))
            return false;
        Span<double> b = stackalloc double[PolyTerms];
        for (int s = 0; s < series; s++)
        {
            b.Clear();
            double[] ys = y[s];
            for (int k = 0; k < count; k++)
            {
                double wk = w[k];
                if (wk <= 0)
                    continue;
                double u = t[from + k] - tc, p = 1;
                for (int e = 0; e < PolyTerms; e++)
                {
                    b[e] += wk * p * ys[from + k];
                    p *= u;
                }
            }

            for (int e = 0; e < PolyTerms; e++)
            {
                double v = 0;
                for (int c = 0; c < PolyTerms; c++)
                    v += inv[e * PolyTerms + c] * b[c];
                coef[s * PolyTerms + e] = v;
            }
        }

        return true;
    }

    private static bool Invert(ReadOnlySpan<double> a, Span<double> inv)
    {
        const int m = PolyTerms;
        Span<double> w = stackalloc double[m * 2 * m];
        for (int r = 0; r < m; r++)
        {
            for (int c = 0; c < m; c++)
            {
                w[r * 2 * m + c] = a[r * m + c];
                w[r * 2 * m + m + c] = r == c ? 1 : 0;
            }
        }

        for (int col = 0; col < m; col++)
        {
            int piv = col;
            for (int r = col + 1; r < m; r++)
            {
                if (Math.Abs(w[r * 2 * m + col]) > Math.Abs(w[piv * 2 * m + col]))
                    piv = r;
            }

            double pv = w[piv * 2 * m + col];
            if (Math.Abs(pv) < 1e-10)
                return false;
            if (piv != col)
            {
                for (int c = 0; c < 2 * m; c++)
                    (w[col * 2 * m + c], w[piv * 2 * m + c]) = (w[piv * 2 * m + c], w[col * 2 * m + c]);
            }

            for (int c = 0; c < 2 * m; c++)
                w[col * 2 * m + c] /= pv;
            for (int r = 0; r < m; r++)
            {
                if (r == col)
                    continue;
                double f = w[r * 2 * m + col];
                if (f == 0)
                    continue;
                for (int c = 0; c < 2 * m; c++)
                    w[r * 2 * m + c] -= f * w[col * 2 * m + c];
            }
        }

        for (int r = 0; r < m; r++)
        {
            for (int c = 0; c < m; c++)
                inv[r * m + c] = w[r * 2 * m + m + c];
        }

        return true;
    }

    /// <summary>
    /// The track's own white-noise sigma per coordinate (source px): the median over the first <paramref name="driving"/> series of
    /// 1.4826 x MAD(third difference) / sqrt(20). A quadratic has a zero third difference, iid noise of sigma s gives a third-difference variance
    /// of 20 s^2, and the MAD keeps a kink or a bridged gap out of the estimate. Correlated wobble is under-estimated by construction, which is
    /// what the grid-fraction prior covers. Zero for series shorter than 8.
    /// </summary>
    internal static double ThirdDifferenceSigma(double[][] series, int driving)
    {
        var per = new List<double>(driving);
        for (int c = 0; c < driving && c < series.Length; c++)
        {
            double[] x = series[c];
            if (x.Length < 8)
                continue;
            var d = new double[x.Length - 3];
            for (int i = 0; i < d.Length; i++)
                d[i] = x[i + 3] - 3 * x[i + 2] + 3 * x[i + 1] - x[i];
            double med = Median(d, d.Length, new double[d.Length]);
            for (int i = 0; i < d.Length; i++)
                d[i] = Math.Abs(d[i] - med);
            double mad = Median(d, d.Length, new double[d.Length]);
            per.Add(1.4826 * mad / Math.Sqrt(20.0));
        }

        if (per.Count == 0)
            return 0;
        var arr = per.ToArray();
        return Median(arr, arr.Length, new double[arr.Length]);
    }

    /// <summary>Median of the first <paramref name="count"/> values (mean of the two middles for an even count); <paramref name="sorted"/> is scratch.</summary>
    private static double Median(double[] values, int count, double[] sorted)
    {
        if (count == 0)
            return 0;
        Array.Copy(values, sorted, count);
        Array.Sort(sorted, 0, count);
        int m = count / 2;
        return count % 2 == 1 ? sorted[m] : (sorted[m - 1] + sorted[m]) / 2;
    }
}
