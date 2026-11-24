#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Smoothing;

/// <summary>
/// K3: Kalman filter + Rauch-Tung-Striebel smoother per coordinate, constant-velocity (<c>order</c> 2: state x, v) or
/// constant-acceleration (<c>order</c> 3: x, v, a), time in frames. The measurement noise is R = sigma^2 with sigma from
/// the outlier pass / the track's own estimate; the process noise is given as a TRACKING INDEX Lambda = sqrt(q / R) (frame^-1.5 for
/// CV, frame^-2.5 for CA), so the smoothing bandwidth does not depend on the absolute noise level. All eight coordinates share
/// F, Q, R and the adaptive decisions, hence one covariance sequence (the quad moves as one object).
/// <c>Adaptive</c> gates on the innovation: if any coordinate's normalized innovation squared exceeds <c>GateChi</c>, the
/// step is re-predicted with Q multiplied by <c>Boost</c>, so a whip or a stop is followed instead of smeared, and the RTS pass
/// does not carry information across it.
/// </summary>
internal sealed class KalmanSmoother : ITrackSmoother
{
    private readonly int _order;
    private readonly double _lambda, _gateChi, _boost;
    private readonly SigmaPolicy _sigma;
    private readonly bool _adaptive;

    public KalmanSmoother(int order, double lambda, bool adaptive = false, double gateChi = 9.0, double boost = 1000.0, SigmaPolicy? sigma = null)
    {
        _order = order; _lambda = lambda; _adaptive = adaptive; _gateChi = gateChi; _boost = boost; _sigma = sigma ?? new SigmaPolicy(0.25);
    }

    public string Name => string.Create(CultureInfo.InvariantCulture,
        $"K3 Kalman {(_order == 2 ? "CV" : "CA")}+RTS Lambda {_lambda:0.###}{(_adaptive ? $" adaptive(chi {_gateChi:0.#}, x{_boost:0})" : "")} {_sigma.Label}");

    public IReadOnlyList<Quad> Smooth(IReadOnlyList<Quad> track, double fps, double noisePx)
    {
        int n = track.Count;
        if (n < 4)
            return track;
        double[][] y = TrackSeries.ToSeries(track);
        double[] t = TrackSeries.Times(track, fps);
        double sigma = _sigma.Resolve(y, noisePx);
        double r = sigma * sigma;
        double q = _lambda * _lambda * r;
        int m = _order;

        var xf = new double[n][][]; // [k][coord][state]
        var xp = new double[n][][];
        var pf = new double[n][,];
        var pp = new double[n][,];
        var fk = new double[n][,];

        // Initial state: first measurement, diffuse derivatives.
        xf[0] = new double[8][];
        for (int c = 0; c < 8; c++)
        {
            xf[0][c] = new double[m];
            xf[0][c][0] = y[c][0];
        }

        pf[0] = new double[m, m];
        pf[0][0, 0] = r;
        for (int d = 1; d < m; d++)
            pf[0][d, d] = 1e4;
        xp[0] = xf[0];
        pp[0] = pf[0];
        fk[0] = Identity(m);

        for (int k = 1; k < n; k++)
        {
            double dt = Math.Max(1e-6, t[k] - t[k - 1]);
            double[,] f = Transition(m, dt);
            fk[k] = f;
            double[,] qm = Process(m, dt, q);
            double[][] pred = PredictState(f, xf[k - 1], m);
            double[,] ppk = Add(Mul(Mul(f, pf[k - 1]), Transpose(f)), qm);

            if (_adaptive)
            {
                double s0 = ppk[0, 0] + r;
                double worst = 0;
                for (int c = 0; c < 8; c++)
                {
                    double nu = y[c][k] - pred[c][0];
                    worst = Math.Max(worst, nu * nu / s0);
                }

                if (worst > _gateChi)
                    ppk = Add(Mul(Mul(f, pf[k - 1]), Transpose(f)), Scale(qm, _boost));
            }

            pp[k] = ppk;
            xp[k] = pred;
            double s = ppk[0, 0] + r;
            var gain = new double[m];
            for (int d = 0; d < m; d++)
                gain[d] = ppk[d, 0] / s;
            xf[k] = new double[8][];
            for (int c = 0; c < 8; c++)
            {
                xf[k][c] = new double[m];
                double nu = y[c][k] - pred[c][0];
                for (int d = 0; d < m; d++)
                    xf[k][c][d] = pred[c][d] + gain[d] * nu;
            }

            var pn = new double[m, m];
            for (int a = 0; a < m; a++)
            {
                for (int b = 0; b < m; b++)
                    pn[a, b] = ppk[a, b] - gain[a] * ppk[0, b];
            }

            // Symmetrize.
            for (int a = 0; a < m; a++)
            {
                for (int b = a + 1; b < m; b++)
                {
                    double v = 0.5 * (pn[a, b] + pn[b, a]);
                    pn[a, b] = v;
                    pn[b, a] = v;
                }
            }

            pf[k] = pn;
        }

        // RTS.
        var xs = new double[n][][];
        xs[n - 1] = xf[n - 1];
        for (int k = n - 2; k >= 0; k--)
        {
            double[,] inv = Inverse(pp[k + 1], m);
            double[,] cgain = Mul(Mul(pf[k], Transpose(fk[k + 1])), inv);
            xs[k] = new double[8][];
            for (int c = 0; c < 8; c++)
            {
                xs[k][c] = new double[m];
                for (int a = 0; a < m; a++)
                {
                    double corr = 0;
                    for (int b = 0; b < m; b++)
                        corr += cgain[a, b] * (xs[k + 1][c][b] - xp[k + 1][c][b]);
                    xs[k][c][a] = xf[k][c][a] + corr;
                }
            }
        }

        var o = new double[8][];
        for (int c = 0; c < 8; c++)
        {
            o[c] = new double[n];
            for (int k = 0; k < n; k++)
                o[c][k] = xs[k][c][0];
        }

        return TrackSeries.Build(track, o);
    }

    private static double[][] PredictState(double[,] f, double[][] x, int m)
    {
        var o = new double[8][];
        for (int c = 0; c < 8; c++)
        {
            o[c] = new double[m];
            for (int a = 0; a < m; a++)
            {
                double v = 0;
                for (int b = 0; b < m; b++)
                    v += f[a, b] * x[c][b];
                o[c][a] = v;
            }
        }

        return o;
    }

    private static double[,] Identity(int m)
    {
        var i = new double[m, m];
        for (int a = 0; a < m; a++)
            i[a, a] = 1;
        return i;
    }

    private static double[,] Transition(int m, double dt)
    {
        var f = Identity(m);
        f[0, 1] = dt;
        if (m == 3)
        {
            f[0, 2] = dt * dt / 2;
            f[1, 2] = dt;
        }

        return f;
    }

    private static double[,] Process(int m, double dt, double q)
    {
        var qm = new double[m, m];
        if (m == 2)
        {
            qm[0, 0] = q * dt * dt * dt / 3;
            qm[0, 1] = qm[1, 0] = q * dt * dt / 2;
            qm[1, 1] = q * dt;
        }
        else
        {
            double d2 = dt * dt, d3 = d2 * dt, d4 = d3 * dt, d5 = d4 * dt;
            qm[0, 0] = q * d5 / 20;
            qm[0, 1] = qm[1, 0] = q * d4 / 8;
            qm[0, 2] = qm[2, 0] = q * d3 / 6;
            qm[1, 1] = q * d3 / 3;
            qm[1, 2] = qm[2, 1] = q * d2 / 2;
            qm[2, 2] = q * dt;
        }

        return qm;
    }

    private static double[,] Mul(double[,] a, double[,] b)
    {
        int n = a.GetLength(0), k = a.GetLength(1), p = b.GetLength(1);
        var o = new double[n, p];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < p; j++)
            {
                double v = 0;
                for (int l = 0; l < k; l++)
                    v += a[i, l] * b[l, j];
                o[i, j] = v;
            }
        }

        return o;
    }

    private static double[,] Transpose(double[,] a)
    {
        int n = a.GetLength(0), p = a.GetLength(1);
        var o = new double[p, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < p; j++)
                o[j, i] = a[i, j];
        }

        return o;
    }

    private static double[,] Add(double[,] a, double[,] b)
    {
        int n = a.GetLength(0), p = a.GetLength(1);
        var o = new double[n, p];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < p; j++)
                o[i, j] = a[i, j] + b[i, j];
        }

        return o;
    }

    private static double[,] Scale(double[,] a, double f)
    {
        int n = a.GetLength(0), p = a.GetLength(1);
        var o = new double[n, p];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < p; j++)
                o[i, j] = a[i, j] * f;
        }

        return o;
    }

    private static double[,] Inverse(double[,] a, int m)
    {
        var w = new double[m, 2 * m];
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < m; j++)
                w[i, j] = a[i, j];
            w[i, m + i] = 1;
        }

        for (int col = 0; col < m; col++)
        {
            int piv = col;
            for (int r = col + 1; r < m; r++)
            {
                if (Math.Abs(w[r, col]) > Math.Abs(w[piv, col]))
                    piv = r;
            }

            if (Math.Abs(w[piv, col]) < 1e-300)
                throw new InvalidOperationException("Singular predicted covariance.");
            if (piv != col)
            {
                for (int c = 0; c < 2 * m; c++)
                    (w[col, c], w[piv, c]) = (w[piv, c], w[col, c]);
            }

            double pv = w[col, col];
            for (int c = 0; c < 2 * m; c++)
                w[col, c] /= pv;
            for (int r = 0; r < m; r++)
            {
                if (r == col)
                    continue;
                double f = w[r, col];
                for (int c = 0; c < 2 * m; c++)
                    w[r, c] -= f * w[col, c];
            }
        }

        var inv = new double[m, m];
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < m; j++)
                inv[i, j] = w[i, m + j];
        }

        return inv;
    }
}
