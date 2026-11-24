#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter;

/// <summary>
/// Jitter statistics for a quad track against ground truth (or, without truth, against its own
/// local behaviour). Everything is in SOURCE pixels unless the caller scaled the quads, pooled
/// over the four corners, and computed on the 2-D error vector e(t) = track(t) - truth(t) of each
/// corner.
///
/// <para>
/// Definitions (fixed here so the numbers mean the same thing everywhere):
/// <list type="bullet">
/// <item><b>Err*</b> — |e| (Euclidean, per corner per frame): mean, 95th percentile, maximum.</item>
/// <item><b>Slip*</b> — |e(t) - e(t-1)| over consecutive sampled frames: RMS and 95th percentile.
/// THE headline wobble metric: a constant error is an offset, a changing one is content sliding
/// on the glass. For i.i.d. noise of per-coordinate sigma s the analytic RMS is 2·s (each of the
/// two coordinates of the difference has variance 2s²).</item>
/// <item><b>HfRms</b> — RMS of |e(t) - mean of e over the centred 1-second window| (frames with a
/// full window only).</item>
/// <item><b>ContentSlipRms</b> — the Slip of the error of a 5×5 grid of content points mapped
/// through the unit-square-to-quad homography of the track and of the truth.</item>
/// <item><b>LagFrames</b> — the delay (frames, parabolic sub-frame peak) at which the track's corner
/// velocities best match the truth's (normalized cross-correlation, k &gt; 0 meaning the track lags); null for a static
/// truth, and null when the peak sits on the search boundary (slow smooth motion has a flat velocity correlation, so
/// the estimate would be noise). <b>LagLsFrames</b> — the same delay estimated in the position domain: the k (parabolic
/// sub-frame) minimizing the mean squared distance between track(t) and truth(t - k); well conditioned on slow motion.</item>
/// <item><b>Overshoot</b> — max |e| within 0.5 s after a motion change (a frame where the truth's mean corner speed
/// changes by at least <see cref="MotionChangePx"/> px/frame); null when there is none.</item>
/// <item><b>DriftPxPerSec</b> — magnitude of the linear-fit slope of the corner-mean error vector.</item>
/// <item><b>PeakToPeak</b> — the largest per-corner, per-axis range of the TRACK (meaningful on static truth).</item>
/// <item><b>ResidualRms/P95</b> (no-truth) — per corner, distance of the sample from a quadratic fitted through its
/// 8 neighbours (±4, itself excluded — the same definition as the real-clip harness' jitter), and
/// <b>SecondDiffRms</b> — RMS of |x(t+1) - 2x(t) + x(t-1)|.</item>
/// </list>
/// </para>
/// </summary>
public sealed record JitterStats(
    int N,
    double ErrMean, double ErrP95, double ErrMax,
    double SlipRms, double SlipP95,
    double HfRms,
    double ContentSlipRms,
    double? LagFrames,
    double? LagLsFrames,
    double? Overshoot,
    double DriftPxPerSec,
    double PeakToPeak,
    double ResidualRms, double ResidualP95,
    double SecondDiffRms);

public static class JitterMetrics
{
    /// <summary>Truth speed change (px/frame, mean over corners) that counts as a motion change for <see cref="JitterStats.Overshoot"/>.</summary>
    public const double MotionChangePx = 4.0;

    private const double TruthStaticVelocityRms = 0.02;
    private const int MaxLag = 10;
    private static readonly (double U, double V)[] ContentGrid = BuildContentGrid();

    private static (double, double)[] BuildContentGrid()
    {
        var g = new (double, double)[25];
        for (int i = 0; i < 5; i++)
        {
            for (int j = 0; j < 5; j++)
                g[i * 5 + j] = (i / 4.0, j / 4.0);
        }

        return g;
    }

    /// <summary>
    /// Scores <paramref name="track"/> against <paramref name="truth"/>. Samples are aligned by
    /// time: a track sample at time T is compared with the truth sample nearest T (within a
    /// quarter of a frame at <paramref name="fps"/>, the TRACK's sample rate); frames the track does
    /// not have are simply absent (slip only spans consecutive frames).
    /// </summary>
    public static JitterStats Compute(IReadOnlyList<Quad> track, IReadOnlyList<Quad> truth, double fps)
    {
        if (fps <= 0)
            throw new ArgumentOutOfRangeException(nameof(fps));

        var truthSorted = truth.OrderBy(q => q.T).ToArray();
        var aligned = new SortedDictionary<int, (Quad Tr, Quad Gt)>();
        foreach (Quad q in track)
        {
            Quad? gt = Nearest(truthSorted, q.T, 0.25 / fps);
            if (gt is not null)
                aligned[Index(q.T, fps)] = (q, gt.Value);
        }

        if (aligned.Count == 0)
            return Empty();

        int[] idx = aligned.Keys.ToArray();
        var err = new List<double>();
        var errAt = new Dictionary<int, (double X, double Y)[]>();
        foreach ((int n, (Quad tr, Quad gt)) in aligned)
        {
            var e = new (double, double)[4];
            for (int c = 0; c < 4; c++)
            {
                (double tx, double ty) = tr.Corner(c);
                (double gx, double gy) = gt.Corner(c);
                e[c] = (tx - gx, ty - gy);
                err.Add(Math.Sqrt(e[c].Item1 * e[c].Item1 + e[c].Item2 * e[c].Item2));
            }

            errAt[n] = e;
        }

        var sortedErr = err.OrderBy(v => v).ToArray();

        // Slip: consecutive sampled frames only.
        var slip = new List<double>();
        foreach (int n in idx)
        {
            if (!errAt.TryGetValue(n - 1, out var prev))
                continue;
            for (int c = 0; c < 4; c++)
            {
                double dx = errAt[n][c].X - prev[c].X, dy = errAt[n][c].Y - prev[c].Y;
                slip.Add(Math.Sqrt(dx * dx + dy * dy));
            }
        }

        // High-frequency residual against the centred 1-second moving average.
        int half = Math.Max(1, (int)Math.Round(fps / 2));
        var hf = new List<double>();
        foreach (int n in idx)
        {
            bool full = true;
            for (int m = n - half; m <= n + half && full; m++)
                full = errAt.ContainsKey(m);
            if (!full)
                continue;
            for (int c = 0; c < 4; c++)
            {
                double mx = 0, my = 0;
                for (int m = n - half; m <= n + half; m++)
                {
                    mx += errAt[m][c].X;
                    my += errAt[m][c].Y;
                }

                int cnt = 2 * half + 1;
                double dx = errAt[n][c].X - mx / cnt, dy = errAt[n][c].Y - my / cnt;
                hf.Add(dx * dx + dy * dy);
            }
        }

        // Content slip: a 5x5 grid of content points through the track's and the truth's homography.
        var contentSlip = new List<double>();
        var contentErr = new Dictionary<int, (double X, double Y)[]>();
        foreach ((int n, (Quad tr, Quad gt)) in aligned)
        {
            UnitSquareMap mt = tr.ToMap(), mg = gt.ToMap();
            var e = new (double, double)[ContentGrid.Length];
            for (int p = 0; p < ContentGrid.Length; p++)
            {
                (double ax, double ay) = mt.Map(ContentGrid[p].U, ContentGrid[p].V);
                (double bx, double by) = mg.Map(ContentGrid[p].U, ContentGrid[p].V);
                e[p] = (ax - bx, ay - by);
            }

            contentErr[n] = e;
            if (contentErr.TryGetValue(n - 1, out var prev))
            {
                for (int p = 0; p < e.Length; p++)
                {
                    double dx = e[p].Item1 - prev[p].X, dy = e[p].Item2 - prev[p].Y;
                    contentSlip.Add(dx * dx + dy * dy);
                }
            }
        }

        (double? lag, double? overshoot) = LagAndOvershoot(aligned, fps);
        double? lagLs = LagLeastSquares(aligned, truthSorted, fps);

        return new JitterStats(
            aligned.Count,
            err.Average(), Percentile(sortedErr, 0.95), sortedErr[^1],
            Rms(slip.Select(v => v * v)), slip.Count == 0 ? double.NaN : Percentile(slip.OrderBy(v => v).ToArray(), 0.95),
            hf.Count == 0 ? double.NaN : Math.Sqrt(hf.Average()),
            contentSlip.Count == 0 ? double.NaN : Math.Sqrt(contentSlip.Average()),
            lag, lagLs, overshoot,
            Drift(aligned, errAt, fps),
            PeakToPeak(aligned.Values.Select(v => v.Tr)),
            double.NaN, double.NaN, double.NaN);
    }

    /// <summary>
    /// Scores a track with no ground truth (a real clip): the residual against a centred local
    /// quadratic and the second difference. The truth-based fields are NaN / null.
    /// </summary>
    public static JitterStats ComputeNoTruth(IReadOnlyList<Quad> track, double fps)
    {
        if (fps <= 0)
            throw new ArgumentOutOfRangeException(nameof(fps));

        Quad[] q = track.OrderBy(v => v.T).ToArray();
        var residual = new List<double>();
        const int half = 4;
        for (int i = half; i + half < q.Length; i++)
        {
            for (int c = 0; c < 4; c++)
            {
                double ex = LeaveOneOutQuadratic(j => q[j].Corner(c).X, i, half) - q[i].Corner(c).X;
                double ey = LeaveOneOutQuadratic(j => q[j].Corner(c).Y, i, half) - q[i].Corner(c).Y;
                residual.Add(Math.Sqrt(ex * ex + ey * ey));
            }
        }

        var second = new List<double>();
        for (int i = 1; i + 1 < q.Length; i++)
        {
            for (int c = 0; c < 4; c++)
            {
                double dx = q[i + 1].Corner(c).X - 2 * q[i].Corner(c).X + q[i - 1].Corner(c).X;
                double dy = q[i + 1].Corner(c).Y - 2 * q[i].Corner(c).Y + q[i - 1].Corner(c).Y;
                second.Add(dx * dx + dy * dy);
            }
        }

        double[] sortedRes = residual.OrderBy(v => v).ToArray();
        return new JitterStats(
            q.Length,
            double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
            null, null, null, double.NaN,
            q.Length == 0 ? double.NaN : PeakToPeak(q),
            residual.Count == 0 ? double.NaN : Rms(residual.Select(v => v * v)),
            residual.Count == 0 ? double.NaN : Percentile(sortedRes, 0.95),
            second.Count == 0 ? double.NaN : Math.Sqrt(second.Average()));
    }

    /// <summary>Per-sample frame index of a time stamp at the track's sample rate.</summary>
    internal static int Index(double t, double fps) => (int)Math.Round(t * fps);

    private static JitterStats Empty() =>
        new(0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, null, null, null, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);

    private static Quad? Nearest(Quad[] sorted, double t, double tolerance)
    {
        int lo = 0, hi = sorted.Length - 1;
        if (hi < 0)
            return null;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (sorted[mid].T < t)
                lo = mid + 1;
            else
                hi = mid;
        }

        Quad best = sorted[lo];
        if (lo > 0 && Math.Abs(sorted[lo - 1].T - t) < Math.Abs(best.T - t))
            best = sorted[lo - 1];
        return Math.Abs(best.T - t) <= tolerance ? best : null;
    }

    private static double Rms(IEnumerable<double> squares)
    {
        double[] a = squares.ToArray();
        return a.Length == 0 ? double.NaN : Math.Sqrt(a.Average());
    }

    private static double Percentile(double[] sorted, double p) =>
        sorted.Length == 0 ? double.NaN : sorted[(int)Math.Round(p * (sorted.Length - 1))];

    private static double PeakToPeak(IEnumerable<Quad> track)
    {
        Quad[] q = track.ToArray();
        double best = 0;
        for (int c = 0; c < 4; c++)
        {
            best = Math.Max(best, q.Max(v => v.Corner(c).X) - q.Min(v => v.Corner(c).X));
            best = Math.Max(best, q.Max(v => v.Corner(c).Y) - q.Min(v => v.Corner(c).Y));
        }

        return best;
    }

    private static double Drift(SortedDictionary<int, (Quad Tr, Quad Gt)> aligned, Dictionary<int, (double X, double Y)[]> errAt, double fps)
    {
        if (aligned.Count < 3)
            return double.NaN;
        double[] t = aligned.Keys.Select(n => n / fps).ToArray();
        double[] mx = aligned.Keys.Select(n => errAt[n].Average(e => e.X)).ToArray();
        double[] my = aligned.Keys.Select(n => errAt[n].Average(e => e.Y)).ToArray();
        double sx = Slope(t, mx), sy = Slope(t, my);
        return Math.Sqrt(sx * sx + sy * sy);

        static double Slope(double[] x, double[] y)
        {
            double mxv = x.Average(), myv = y.Average();
            double num = 0, den = 0;
            for (int i = 0; i < x.Length; i++)
            {
                num += (x[i] - mxv) * (y[i] - myv);
                den += (x[i] - mxv) * (x[i] - mxv);
            }

            return den > 0 ? num / den : 0;
        }
    }

    private static (double? Lag, double? Overshoot) LagAndOvershoot(SortedDictionary<int, (Quad Tr, Quad Gt)> aligned, double fps)
    {
        // Corner velocities (px/frame) for frames whose predecessor is also present.
        var vt = new Dictionary<int, (double X, double Y)[]>();
        var vg = new Dictionary<int, (double X, double Y)[]>();
        foreach (int n in aligned.Keys)
        {
            if (!aligned.TryGetValue(n - 1, out var prev))
                continue;
            var a = new (double, double)[4];
            var b = new (double, double)[4];
            for (int c = 0; c < 4; c++)
            {
                a[c] = (aligned[n].Tr.Corner(c).X - prev.Tr.Corner(c).X, aligned[n].Tr.Corner(c).Y - prev.Tr.Corner(c).Y);
                b[c] = (aligned[n].Gt.Corner(c).X - prev.Gt.Corner(c).X, aligned[n].Gt.Corner(c).Y - prev.Gt.Corner(c).Y);
            }

            vt[n] = a;
            vg[n] = b;
        }

        // Motion changes: the truth's mean corner speed jumping by MotionChangePx or more.
        var speed = vg.ToDictionary(kv => kv.Key, kv => kv.Value.Average(v => Math.Sqrt(v.X * v.X + v.Y * v.Y)));
        var changes = new List<int>();
        foreach (int n in speed.Keys)
        {
            if (speed.TryGetValue(n - 1, out double before) && Math.Abs(speed[n] - before) >= MotionChangePx)
                changes.Add(n);
        }

        double? overshoot = null;
        if (changes.Count > 0)
        {
            double worst = 0;
            int span = (int)Math.Round(0.5 * fps);
            foreach (int n0 in changes)
            {
                for (int n = n0; n <= n0 + span; n++)
                {
                    if (!aligned.TryGetValue(n, out var s))
                        continue;
                    for (int c = 0; c < 4; c++)
                    {
                        double dx = s.Tr.Corner(c).X - s.Gt.Corner(c).X, dy = s.Tr.Corner(c).Y - s.Gt.Corner(c).Y;
                        worst = Math.Max(worst, Math.Sqrt(dx * dx + dy * dy));
                    }
                }
            }

            overshoot = worst;
        }

        if (vg.Count < 8)
            return (null, overshoot);
        double energy = Math.Sqrt(vg.Values.SelectMany(v => v).Average(v => v.X * v.X + v.Y * v.Y));
        if (energy < TruthStaticVelocityRms)
            return (null, overshoot);

        // r(k): normalized cross-correlation of corner velocities, track at n against truth at n-k.
        var corr = new double[2 * MaxLag + 1];
        for (int k = -MaxLag; k <= MaxLag; k++)
        {
            double sum = 0, ea = 0, eb = 0;
            foreach ((int n, var a) in vt)
            {
                if (!vg.TryGetValue(n - k, out var b))
                    continue;
                for (int c = 0; c < 4; c++)
                {
                    sum += a[c].X * b[c].X + a[c].Y * b[c].Y;
                    ea += a[c].X * a[c].X + a[c].Y * a[c].Y;
                    eb += b[c].X * b[c].X + b[c].Y * b[c].Y;
                }
            }

            corr[k + MaxLag] = ea > 0 && eb > 0 ? sum / Math.Sqrt(ea * eb) : double.NegativeInfinity;
        }

        int best = 0;
        for (int i = 1; i < corr.Length; i++)
        {
            if (corr[i] > corr[best])
                best = i;
        }

        if (best == 0 || best == corr.Length - 1 || !double.IsFinite(corr[best - 1]) || !double.IsFinite(corr[best + 1]))
            return (null, overshoot);

        double lag = best - MaxLag;
        double denom = corr[best - 1] - 2 * corr[best] + corr[best + 1];
        if (Math.Abs(denom) > 1e-12)
            lag += 0.5 * (corr[best - 1] - corr[best + 1]) / denom;

        return (lag, overshoot);
    }

    /// <summary>Position-domain delay: argmin over k of the mean squared corner distance between track(t) and truth(t - k) (truth interpolated in time), parabolic refinement.</summary>
    private static double? LagLeastSquares(SortedDictionary<int, (Quad Tr, Quad Gt)> aligned, Quad[] truthSorted, double fps)
    {
        if (aligned.Count < 8 || truthSorted.Length < 2)
            return null;
        double speed = aligned.Values.Select(v => v.Gt).Zip(aligned.Values.Select(v => v.Gt).Skip(1), (a, b) =>
            Enumerable.Range(0, 4).Average(c => Math.Sqrt(Math.Pow(b.Corner(c).X - a.Corner(c).X, 2) + Math.Pow(b.Corner(c).Y - a.Corner(c).Y, 2)))).DefaultIfEmpty(0).Max();
        if (speed < 0.05)
            return null;

        var cost = new double[2 * MaxLag + 1];
        for (int k = -MaxLag; k <= MaxLag; k++)
        {
            double sum = 0;
            int cnt = 0;
            foreach ((Quad tr, _) in aligned.Values)
            {
                Quad? g = Interpolate(truthSorted, tr.T - k / fps);
                if (g is null)
                    continue;
                for (int c = 0; c < 4; c++)
                {
                    double dx = tr.Corner(c).X - g.Value.Corner(c).X, dy = tr.Corner(c).Y - g.Value.Corner(c).Y;
                    sum += dx * dx + dy * dy;
                }

                cnt += 4;
            }

            cost[k + MaxLag] = cnt < 16 ? double.PositiveInfinity : sum / cnt;
        }

        int best = 0;
        for (int i = 1; i < cost.Length; i++)
        {
            if (cost[i] < cost[best])
                best = i;
        }

        if (best == 0 || best == cost.Length - 1 || !double.IsFinite(cost[best - 1]) || !double.IsFinite(cost[best + 1]))
            return null;
        double lag = best - MaxLag;
        double denom = cost[best - 1] - 2 * cost[best] + cost[best + 1];
        return Math.Abs(denom) > 1e-12 ? lag + 0.5 * (cost[best - 1] - cost[best + 1]) / denom : lag;
    }

    private static Quad? Interpolate(Quad[] sorted, double t)
    {
        if (t < sorted[0].T - 1e-9 || t > sorted[^1].T + 1e-9)
            return null;
        int lo = 0, hi = sorted.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (sorted[mid].T <= t)
                lo = mid;
            else
                hi = mid;
        }

        Quad a = sorted[lo], b = sorted[hi];
        double u = b.T > a.T ? Math.Clamp((t - a.T) / (b.T - a.T), 0, 1) : 0;
        double L(double p, double q) => p + (q - p) * u;
        return new Quad(t, L(a.X0, b.X0), L(a.Y0, b.Y0), L(a.X1, b.X1), L(a.Y1, b.Y1), L(a.X2, b.X2), L(a.Y2, b.Y2), L(a.X3, b.X3), L(a.Y3, b.Y3));
    }

    /// <summary>Quadratic least-squares through the neighbours of <paramref name="i"/> (itself excluded), evaluated at <paramref name="i"/>.</summary>
    private static double LeaveOneOutQuadratic(Func<int, double> v, int i, int half)
    {
        double s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0, y0 = 0, y1 = 0, y2 = 0;
        for (int j = i - half; j <= i + half; j++)
        {
            if (j == i)
                continue;
            double x = j - i, y = v(j);
            s0 += 1; s1 += x; s2 += x * x; s3 += x * x * x; s4 += x * x * x * x;
            y0 += y; y1 += x * y; y2 += x * x * y;
        }

        double det = s0 * (s2 * s4 - s3 * s3) - s1 * (s1 * s4 - s3 * s2) + s2 * (s1 * s3 - s2 * s2);
        double da = y0 * (s2 * s4 - s3 * s3) - s1 * (y1 * s4 - s3 * y2) + s2 * (y1 * s3 - s2 * y2);
        return da / det;
    }

    /// <summary>Invariant-culture number for report cells; NaN/null render as a dash.</summary>
    public static string Cell(double? v, string format = "0.000") =>
        v is null || double.IsNaN(v.Value) ? "-" : v.Value.ToString(format, CultureInfo.InvariantCulture);
}
