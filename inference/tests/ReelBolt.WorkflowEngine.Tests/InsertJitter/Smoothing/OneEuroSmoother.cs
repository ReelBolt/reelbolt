#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Smoothing;

/// <summary>
/// K2: One-Euro filter (Casiez et al. 2012) on the eight coordinates, run forward and backward. The cutoff follows the
/// quad's joint speed (the mean corner speed, so all eight coordinates share the same alpha and the quad is not torn):
/// <c>fc = minCutoff + beta * speed</c>. A causal One-Euro lags a moving plate by about 1 / (2 pi fc); the backward pass
/// leads by the same amount, so the two outputs are averaged (<c>Average</c>) or the backward pass is run on the forward
/// output (<c>ZeroPhase</c>, a squared response).
/// </summary>
internal sealed class OneEuroSmoother : ITrackSmoother
{
    public enum Combine { Average, ZeroPhase }

    private readonly double _minCutoff, _beta, _dCutoff;
    private readonly Combine _mode;

    /// <param name="minCutoffHz">Cutoff at rest (Hz, with time in seconds).</param>
    /// <param name="beta">Cutoff growth per px/s of joint speed (Hz per px/s).</param>
    public OneEuroSmoother(double minCutoffHz, double beta, Combine mode = Combine.Average, double dCutoffHz = 1.0)
    {
        _minCutoff = minCutoffHz; _beta = beta; _mode = mode; _dCutoff = dCutoffHz;
    }

    public string Name => string.Create(CultureInfo.InvariantCulture, $"K2 One-Euro fwd-bwd {_mode} minCutoff {_minCutoff:0.##} Hz beta {_beta:0.###}");

    public IReadOnlyList<Quad> Smooth(IReadOnlyList<Quad> track, double fps, double noisePx)
    {
        int n = track.Count;
        if (n < 3)
            return track;
        double[][] s = TrackSeries.ToSeries(track);
        double[] ts = new double[n];
        for (int i = 0; i < n; i++)
            ts[i] = track[i].T;

        double[][] fwd = Pass(s, ts, false);
        double[][] result;
        if (_mode == Combine.ZeroPhase)
        {
            result = Pass(fwd, ts, true);
        }
        else
        {
            double[][] bwd = Pass(s, ts, true);
            result = new double[8][];
            for (int c = 0; c < 8; c++)
            {
                result[c] = new double[n];
                for (int i = 0; i < n; i++)
                    result[c][i] = 0.5 * (fwd[c][i] + bwd[c][i]);
            }
        }

        return TrackSeries.Build(track, result);
    }

    private static double Alpha(double cutoff, double te) => 1.0 / (1.0 + 1.0 / (2 * Math.PI * cutoff) / te);

    private double[][] Pass(double[][] s, double[] ts, bool backward)
    {
        int n = ts.Length;
        var o = new double[8][];
        for (int c = 0; c < 8; c++)
            o[c] = new double[n];
        double[] prevRaw = new double[8], prevHat = new double[8], dHat = new double[8];
        for (int step = 0; step < n; step++)
        {
            int i = backward ? n - 1 - step : step;
            if (step == 0)
            {
                for (int c = 0; c < 8; c++)
                {
                    o[c][i] = s[c][i];
                    prevRaw[c] = s[c][i];
                    prevHat[c] = s[c][i];
                    dHat[c] = 0;
                }

                continue;
            }

            int ip = backward ? i + 1 : i - 1;
            double te = Math.Abs(ts[i] - ts[ip]);
            if (te <= 0)
                te = 1e-6;
            double ad = Alpha(_dCutoff, te);
            var dx = new double[8];
            for (int c = 0; c < 8; c++)
            {
                double d = (s[c][i] - prevRaw[c]) / te;
                dHat[c] = ad * d + (1 - ad) * dHat[c];
                dx[c] = dHat[c];
            }

            // Joint speed: mean corner speed (px/s).
            double speed = 0;
            for (int k = 0; k < 4; k++)
                speed += Math.Sqrt(dx[2 * k] * dx[2 * k] + dx[2 * k + 1] * dx[2 * k + 1]);
            speed /= 4;
            double a = Alpha(_minCutoff + _beta * speed, te);
            for (int c = 0; c < 8; c++)
            {
                prevHat[c] = a * s[c][i] + (1 - a) * prevHat[c];
                prevRaw[c] = s[c][i];
                o[c][i] = prevHat[c];
            }
        }

        return o;
    }
}
