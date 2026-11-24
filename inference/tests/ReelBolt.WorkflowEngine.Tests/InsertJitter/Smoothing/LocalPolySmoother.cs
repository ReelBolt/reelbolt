#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Smoothing;

/// <summary>Options of the robust local-polynomial core (shared by K4, K5 and K6).</summary>
internal sealed record LocalPolyOptions(
    int Degree = 2,
    /// <summary>Candidate half-widths in frames, ascending. 0 means "pass the sample through" and is only chosen when nothing larger passes the residual test.</summary>
    int[]? HalfWidths = null,
    /// <summary>The fit of a half-width is accepted when its robust residual RMS is at most this many sigma.</summary>
    double Tau = 2.0,
    int RobustIterations = 2,
    bool Tukey = true,
    /// <summary>Median filter (±) over the chosen half-width index; 0 = off.</summary>
    int SelectionMedianHalf = 2,
    /// <summary>When no width is accepted and even the narrowest fit leaves a robust residual above this many sigma, the sample is passed through raw (a kink: whip, stop). Infinity = never.</summary>
    double RawAbove = double.PositiveInfinity)
{
    public int[] Widths => HalfWidths ?? [4, 6, 8, 12];
}

/// <summary>
/// K4: per-frame robust local polynomial regression with an adaptive bandwidth. For every candidate half-width H the
/// window is fitted with tricube x Tukey-bisquare weights (the robust weight is shared by the eight coordinates, driven by
/// the quad's joint residual, so a bad frame is dropped as a whole). The widest H whose robust residual RMS stays within
/// <c>Tau</c> sigma wins: a plate moving along a quadratic path explains its window to noise level at H = 12, a kink
/// (whip, stop) fails at every H that straddles it and falls back to a short window or the raw sample.
/// </summary>
internal static class LocalPolyCore
{
    public static double[][] Run(double[] t, double[][] y, double sigma, LocalPolyOptions o, out int[] chosenWidth)
    {
        int n = t.Length, ns = y.Length;
        int[] widths = o.Widths;
        var best = new double[ns][];
        for (int c = 0; c < ns; c++)
            best[c] = (double[])y[c].Clone();
        chosenWidth = new int[n];
        if (n < 5)
            return best;

        // Fits for every width, then selection.
        var fitValue = new double[widths.Length][][]; // [width][series][i]
        var fitRms = new double[widths.Length][];
        for (int wi = 0; wi < widths.Length; wi++)
        {
            fitValue[wi] = Enumerable.Range(0, ns).Select(_ => new double[n]).ToArray();
            fitRms[wi] = new double[n];
            if (widths[wi] == 0)
            {
                for (int c = 0; c < ns; c++)
                    Array.Copy(y[c], fitValue[wi][c], n);
                Array.Fill(fitRms[wi], 0.0);
                continue;
            }

            FitAll(t, y, sigma, o, widths[wi], fitValue[wi], fitRms[wi]);
        }

        // Selection: widest accepted width; accepted = rms <= tau * sigma.
        var pick = new int[n];
        for (int i = 0; i < n; i++)
        {
            int sel = -1;
            for (int wi = widths.Length - 1; wi >= 0; wi--)
            {
                if (widths[wi] == 0 || fitRms[wi][i] <= o.Tau * sigma)
                {
                    sel = wi;
                    break;
                }
            }

            if (sel < 0)
            {
                // Nothing passed: the narrowest real width, unless even that misfits grossly.
                int narrow = Array.FindIndex(widths, w => w > 0);
                bool gross = narrow >= 0 && fitRms[narrow][i] > o.RawAbove * sigma;
                sel = narrow < 0 || gross ? Array.IndexOf(widths, 0) : narrow;
                if (sel < 0)
                    sel = -2; // raw, not listed
            }

            pick[i] = sel;
        }

        if (o.SelectionMedianHalf > 0)
        {
            var med = new int[n];
            for (int i = 0; i < n; i++)
            {
                int from = Math.Max(0, i - o.SelectionMedianHalf), to = Math.Min(n - 1, i + o.SelectionMedianHalf);
                var win = new List<int>();
                for (int j = from; j <= to; j++)
                    win.Add(pick[j] < 0 ? -1 : pick[j]);
                win.Sort();
                // Never widen beyond what this frame itself accepted: the median may only narrow a pick.
                med[i] = pick[i] < 0 ? pick[i] : Math.Min(pick[i], win[win.Count / 2]);
            }

            pick = med;
        }

        for (int i = 0; i < n; i++)
        {
            if (pick[i] < 0)
            {
                chosenWidth[i] = 0; // raw: best[c][i] already holds y
                continue;
            }

            chosenWidth[i] = widths[pick[i]];
            for (int c = 0; c < ns; c++)
                best[c][i] = fitValue[pick[i]][c][i];
        }

        return best;
    }

    private static void FitAll(double[] t, double[][] y, double sigma, LocalPolyOptions o, int half, double[][] value, double[] rms)
    {
        int n = t.Length, ns = y.Length;
        int m = o.Degree + 1;
        var w = new double[n];
        var rw = new double[n];
        var cw = new double[n];
        var resid = new double[n];
        var rs = new List<double>(2 * half + 2);
        var coef = Enumerable.Range(0, ns).Select(_ => new double[m]).ToArray();
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
                w[j] = k * k * k; // tricube
                rw[j] = 1.0;
            }

            bool ok = count >= m + 1;
            double fitRms = double.PositiveInfinity;
            if (ok)
            {
                for (int it = 0; it <= o.RobustIterations && ok; it++)
                {
                    // Combined weights for this iteration.
                    for (int j = from; j <= to; j++)
                        cw[j] = w[j] * rw[j];
                    ok = PolyFit.Fit(t, y, from, to, t[i], cw, o.Degree, coef);
                    if (!ok)
                        break;
                    // Joint residuals per sample.
                    double sumW = 0, sumWr2 = 0;
                    rs.Clear();
                    for (int j = from; j <= to; j++)
                    {
                        double u = t[j] - t[i], r2 = 0;
                        for (int c = 0; c < ns; c++)
                        {
                            double p = 0, pu = 1;
                            for (int k = 0; k < m; k++)
                            {
                                p += coef[c][k] * pu;
                                pu *= u;
                            }

                            double d = y[c][j] - p;
                            r2 += d * d;
                        }

                        resid[j] = Math.Sqrt(r2 / ns);
                        rs.Add(resid[j]);
                        sumW += cw[j];
                        sumWr2 += cw[j] * resid[j] * resid[j];
                    }

                    fitRms = sumW > 0 ? Math.Sqrt(sumWr2 / sumW) : double.PositiveInfinity;
                    if (it == o.RobustIterations || !o.Tukey)
                        break;
                    double scale = Math.Max(sigma, 1.4826 * TrackSeries.Median(rs));
                    const double c4685 = 4.685;
                    for (int j = from; j <= to; j++)
                    {
                        double z = resid[j] / (c4685 * scale);
                        rw[j] = z >= 1 ? 0.0 : (1 - z * z) * (1 - z * z);
                    }
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
                    value[c][i] = coef[c][0];
                rms[i] = fitRms;
            }
        }
    }
}

/// <summary>K4 on the eight corner coordinates.</summary>
internal sealed class LocalPolySmoother : ITrackSmoother
{
    private readonly LocalPolyOptions _o;
    private readonly SigmaPolicy _sigma;

    public LocalPolySmoother(LocalPolyOptions o, SigmaPolicy? sigma = null)
    {
        _o = o; _sigma = sigma ?? new SigmaPolicy(0.1);
    }

    public string Name => string.Create(CultureInfo.InvariantCulture,
        $"K4 local poly deg {_o.Degree} H[{string.Join(",", _o.Widths)}] tau {_o.Tau:0.##}{(double.IsInfinity(_o.RawAbove) ? "" : $" raw>{_o.RawAbove:0.#}")}{(_o.Tukey ? " tukey" : "")} medsel {_o.SelectionMedianHalf} {_sigma.Label}");

    public IReadOnlyList<Quad> Smooth(IReadOnlyList<Quad> track, double fps, double noisePx)
    {
        if (track.Count < 5)
            return track;
        double[][] s = TrackSeries.ToSeries(track);
        double sigma = _sigma.Resolve(s, noisePx);
        double[][] o = LocalPolyCore.Run(TrackSeries.Times(track, fps), s, sigma, _o, out _);
        return TrackSeries.Build(track, o);
    }
}
