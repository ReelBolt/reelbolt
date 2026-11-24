#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Smoothing;

/// <summary>
/// K0: the retired shipped smoother (<see cref="LegacyBoxSmooth"/>: 3-frame box blended by the motion gate), called on the
/// scene's cached S3 <c>FrameQuad</c> list. The <c>track</c> argument is only used for its length check:
/// the production method needs the full FrameQuad (bows, surface) so it is handed the cached list.
/// </summary>
internal sealed class ProductionSmoother : ITrackSmoother
{
    private readonly List<ChromaQuadTracker.FrameQuad> _s3;
    private readonly int _gw, _gh, _sw, _sh, _window;
    private readonly double _gate;

    public ProductionSmoother(List<ChromaQuadTracker.FrameQuad> s3, int gw, int gh, int sw, int sh, int window = 3, double gate = 2.0)
    {
        _s3 = s3; _gw = gw; _gh = gh; _sw = sw; _sh = sh; _window = window; _gate = gate;
    }

    public string Name => string.Create(CultureInfo.InvariantCulture, $"K0 shipped Smooth (box {_window}, gate {_gate:0.#}x noise)");

    public IReadOnlyList<Quad> Smooth(IReadOnlyList<Quad> track, double fps, double noisePx)
    {
        double noiseGrid = noisePx * _gw / _sw;
        List<ChromaQuadTracker.FrameQuad> o = LegacyBoxSmooth.Smooth(_s3, Math.Max(1, _window | 1), _gw, _gh, Math.Max(0, _gate) * noiseGrid);
        return StageCapture.ToQuads(o.Cast<ChromaQuadTracker.FrameQuad?>(), _sw, _sh, fps);
    }
}

/// <summary>Reference: centred box of N frames (edges truncated like production), no gate.</summary>
internal sealed class BoxSmoother : ITrackSmoother
{
    private readonly int _n;

    public BoxSmoother(int n) => _n = n | 1;

    public string Name => $"box {_n} gate-off";

    public IReadOnlyList<Quad> Smooth(IReadOnlyList<Quad> track, double fps, double noisePx)
    {
        double[][] s = TrackSeries.ToSeries(track);
        return TrackSeries.Build(track, Apply(s, _n / 2, null));
    }

    /// <summary>Box mean over ±half samples; with <paramref name="blend"/> each frame is blended between its raw value and the mean.</summary>
    public static double[][] Apply(double[][] s, int half, double[]? blend)
    {
        int n = s[0].Length;
        var o = new double[s.Length][];
        for (int c = 0; c < s.Length; c++)
        {
            o[c] = new double[n];
            for (int i = 0; i < n; i++)
            {
                int from = Math.Max(0, i - half), to = Math.Min(n - 1, i + half);
                double sum = 0;
                for (int j = from; j <= to; j++)
                    sum += s[c][j];
                double mean = sum / (to - from + 1);
                double b = blend?[i] ?? 1.0;
                o[c][i] = s[c][i] + b * (mean - s[c][i]);
            }
        }

        return o;
    }
}

/// <summary>
/// K1: the same box, but gated on TRUE acceleration instead of the frame step. At each frame a quadratic is fitted to
/// the ±<c>FitHalf</c> neighbourhood; the box's own bias on a quadratic path is |c2| * h(h+1)/3 (h = N/2), and the frame is
/// blended 1 / (1 + (bias / (k sigma))^2) towards the box mean — smoothing is spent only while it costs less than the noise it removes.
/// A constant-velocity plate (any speed) therefore keeps the full box; a whip's kink closes the gate.
/// </summary>
internal sealed class GatedBoxSmoother : ITrackSmoother
{
    private readonly int _n, _fitHalf;
    private readonly double _k;
    private readonly SigmaPolicy _sigma;

    public GatedBoxSmoother(int n, double k, int fitHalf = 4, SigmaPolicy? sigma = null)
    {
        _n = n | 1; _k = k; _fitHalf = fitHalf; _sigma = sigma ?? new SigmaPolicy(0.25);
    }

    public string Name => string.Create(CultureInfo.InvariantCulture, $"K1 acceleration-gated box {_n} (k {_k:0.##})");

    public IReadOnlyList<Quad> Smooth(IReadOnlyList<Quad> track, double fps, double noisePx)
    {
        double[][] s = TrackSeries.ToSeries(track);
        int n = track.Count;
        if (n < 5)
            return track;
        double[] t = TrackSeries.Times(track, fps);
        double sigma = _sigma.Resolve(s, noisePx);
        int h = _n / 2;
        var blend = new double[n];
        var w = new double[n];
        Array.Fill(w, 1.0);
        var coef = Enumerable.Range(0, 8).Select(_ => new double[3]).ToArray();
        for (int i = 0; i < n; i++)
        {
            int from = Math.Max(0, i - _fitHalf), to = Math.Min(n - 1, i + _fitHalf);
            double bias;
            if (to - from + 1 >= 5 && PolyFit.Fit(t, s, from, to, t[i], w, 2, coef))
            {
                bias = 0;
                for (int k = 0; k < 4; k++)
                {
                    double ax = coef[2 * k][2], ay = coef[2 * k + 1][2];
                    bias = Math.Max(bias, Math.Sqrt(ax * ax + ay * ay) * h * (h + 1) / 3.0);
                }
            }
            else
            {
                bias = double.PositiveInfinity;
            }

            double r = bias / (_k * sigma);
            blend[i] = double.IsInfinity(r) ? 0 : 1.0 / (1.0 + r * r);
        }

        return TrackSeries.Build(track, BoxSmoother.Apply(s, h, blend));
    }
}

/// <summary>
/// The RETIRED production smoother (3-frame box blended by a motion gate, <c>ChromaQuadTracker.Smooth</c> before phase 2 of
/// insert-tracking-smoothness), kept verbatim as a test-only reference so the K0 baseline of the benches stays reproducible after the tracker moved
/// to <c>InsertTrackSmoother</c>.
/// </summary>
internal static class LegacyBoxSmooth
{
    public static List<ChromaQuadTracker.FrameQuad> Smooth(List<ChromaQuadTracker.FrameQuad> run, int window, int width = 0, int height = 0, double motionGate = 0)
    {
        if (window <= 1 || run.Count <= 2)
            return run;

        int half = window / 2;
        bool gated = motionGate > 0 && width > 0 && height > 0;
        var smoothed = new List<ChromaQuadTracker.FrameQuad>(run.Count);
        var steps = new List<double>(window);
        for (int i = 0; i < run.Count; i++)
        {
            double blend = 1.0;
            int from = Math.Max(0, i - half);
            int to = Math.Min(run.Count - 1, i + half);
            int n = to - from + 1;

            if (gated)
            {
                steps.Clear();
                for (int j = from; j < to; j++)
                {
                    int dt = run[j + 1].FrameIndex - run[j].FrameIndex;
                    if (dt > 0)
                        steps.Add(MaxCornerDistance(run[j], run[j + 1], width, height) / dt);
                }

                double motion = steps.Count > 0 ? ChromaQuadTracker.Median(steps) : 0;
                blend = 1.0 / (1.0 + (motion / motionGate) * (motion / motionGate));
                if (blend < 1e-3)
                {
                    smoothed.Add(run[i]);
                    continue;
                }
            }

            double x0 = 0, y0 = 0, x1 = 0, y1 = 0, x2 = 0, y2 = 0, x3 = 0, y3 = 0;
            double tb = 0, ts = 0, rb = 0, rs = 0, bb = 0, bs = 0, lb = 0, ls = 0;
            double sx0 = 0, sy0 = 0, sx1 = 0, sy1 = 0, sx2 = 0, sy2 = 0, sx3 = 0, sy3 = 0;
            for (int j = from; j <= to; j++)
            {
                ChromaQuadTracker.FrameQuad q = run[j];
                x0 += q.X0; y0 += q.Y0; x1 += q.X1; y1 += q.Y1;
                x2 += q.X2; y2 += q.Y2; x3 += q.X3; y3 += q.Y3;
                sx0 += q.Surface.X0; sy0 += q.Surface.Y0; sx1 += q.Surface.X1; sy1 += q.Surface.Y1;
                sx2 += q.Surface.X2; sy2 += q.Surface.Y2; sx3 += q.Surface.X3; sy3 += q.Surface.Y3;
                tb += q.Bows.Top.Bow; ts += q.Bows.Top.Skew;
                rb += q.Bows.Right.Bow; rs += q.Bows.Right.Skew;
                bb += q.Bows.Bottom.Bow; bs += q.Bows.Bottom.Skew;
                lb += q.Bows.Left.Bow; ls += q.Bows.Left.Skew;
            }

            ChromaQuadTracker.FrameQuad q0 = run[i];
            double B(double avg, double raw) => raw + blend * (avg - raw);
            smoothed.Add(q0 with
            {
                X0 = B(x0 / n, q0.X0),
                Y0 = B(y0 / n, q0.Y0),
                X1 = B(x1 / n, q0.X1),
                Y1 = B(y1 / n, q0.Y1),
                X2 = B(x2 / n, q0.X2),
                Y2 = B(y2 / n, q0.Y2),
                X3 = B(x3 / n, q0.X3),
                Y3 = B(y3 / n, q0.Y3),
                Bows = new InsertPlateBows(
                    new InsertEdgeBow(B(tb / n, q0.Bows.Top.Bow), B(ts / n, q0.Bows.Top.Skew)),
                    new InsertEdgeBow(B(rb / n, q0.Bows.Right.Bow), B(rs / n, q0.Bows.Right.Skew)),
                    new InsertEdgeBow(B(bb / n, q0.Bows.Bottom.Bow), B(bs / n, q0.Bows.Bottom.Skew)),
                    new InsertEdgeBow(B(lb / n, q0.Bows.Left.Bow), B(ls / n, q0.Bows.Left.Skew))),
                Surface = (B(sx0 / n, q0.Surface.X0), B(sy0 / n, q0.Surface.Y0), B(sx1 / n, q0.Surface.X1), B(sy1 / n, q0.Surface.Y1),
                           B(sx2 / n, q0.Surface.X2), B(sy2 / n, q0.Surface.Y2), B(sx3 / n, q0.Surface.X3), B(sy3 / n, q0.Surface.Y3))
            });
        }

        return smoothed;
    }

    private static double MaxCornerDistance(ChromaQuadTracker.FrameQuad a, ChromaQuadTracker.FrameQuad b, int width, int height)
    {
        double worst = 0;
        double[] ax = [a.X0 * width, a.X1 * width, a.X2 * width, a.X3 * width], ay = [a.Y0 * height, a.Y1 * height, a.Y2 * height, a.Y3 * height];
        double[] bx = [b.X0 * width, b.X1 * width, b.X2 * width, b.X3 * width], by = [b.Y0 * height, b.Y1 * height, b.Y2 * height, b.Y3 * height];
        for (int k = 0; k < 4; k++)
            worst = Math.Max(worst, Math.Sqrt((ax[k] - bx[k]) * (ax[k] - bx[k]) + (ay[k] - by[k]) * (ay[k] - by[k])));
        return worst;
    }
}
