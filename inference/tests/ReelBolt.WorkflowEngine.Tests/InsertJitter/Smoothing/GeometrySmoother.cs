#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Smoothing;

/// <summary>
/// K5a: geometry-consistent smoothing in the orthogonal (Hadamard) basis of the four corners, per axis:
/// mean m = (TL+TR+BL+BR)/4 (translation), h = (TR+BR-TL-BL)/4 (width / roll), v = (BL+BR-TL-TR)/4 (height / roll),
/// d = (TL+BR-TR-BL)/4 (the non-parallelogram part: perspective, shear-free trapezoid). A rigid or affine motion has d = 0
/// and the components of independent corner noise are independent with sigma / 2 each, so the shape components can be smoothed
/// harder than the translation: corners no longer wobble independently. Each group is smoothed by the robust local-polynomial core.
/// </summary>
internal sealed class HadamardSmoother : ITrackSmoother
{
    private readonly LocalPolyOptions _mean, _shape, _diag;
    private readonly SigmaPolicy _sigma;

    public HadamardSmoother(LocalPolyOptions mean, LocalPolyOptions shape, LocalPolyOptions diag, SigmaPolicy? sigma = null)
    {
        _mean = mean; _shape = shape; _diag = diag; _sigma = sigma ?? new SigmaPolicy(0.1);
    }

    public string Name => string.Create(CultureInfo.InvariantCulture,
        $"K5a Hadamard mean H[{string.Join(",", _mean.Widths)}] tau {_mean.Tau:0.#} | shape H[{string.Join(",", _shape.Widths)}] tau {_shape.Tau:0.#} | diag H[{string.Join(",", _diag.Widths)}] tau {_diag.Tau:0.#}");

    public IReadOnlyList<Quad> Smooth(IReadOnlyList<Quad> track, double fps, double noisePx)
    {
        int n = track.Count;
        if (n < 5)
            return track;
        double[][] s = TrackSeries.ToSeries(track);
        double[] t = TrackSeries.Times(track, fps);
        double sigma = _sigma.Resolve(s, noisePx) / 2.0; // each Hadamard component averages four corners

        // comp[axis][k][i]: k = 0 m, 1 h, 2 v, 3 d.
        var comp = new double[2][][];
        for (int ax = 0; ax < 2; ax++)
        {
            double[] tl = s[0 + ax], tr = s[2 + ax], bl = s[4 + ax], br = s[6 + ax];
            comp[ax] = [new double[n], new double[n], new double[n], new double[n]];
            for (int i = 0; i < n; i++)
            {
                comp[ax][0][i] = (tl[i] + tr[i] + bl[i] + br[i]) / 4;
                comp[ax][1][i] = (tr[i] + br[i] - tl[i] - bl[i]) / 4;
                comp[ax][2][i] = (bl[i] + br[i] - tl[i] - tr[i]) / 4;
                comp[ax][3][i] = (tl[i] + br[i] - tr[i] - bl[i]) / 4;
            }
        }

        double[][] mean = LocalPolyCore.Run(t, [comp[0][0], comp[1][0]], sigma, _mean, out _);
        double[][] shape = LocalPolyCore.Run(t, [comp[0][1], comp[1][1], comp[0][2], comp[1][2]], sigma, _shape, out _);
        double[][] diag = LocalPolyCore.Run(t, [comp[0][3], comp[1][3]], sigma, _diag, out _);

        var o = new double[8][];
        for (int c = 0; c < 8; c++)
            o[c] = new double[n];
        for (int i = 0; i < n; i++)
        {
            for (int ax = 0; ax < 2; ax++)
            {
                double m = mean[ax][i], h = shape[ax][i], v = shape[2 + ax][i], d = diag[ax][i];
                o[0 + ax][i] = m - h - v + d; // TL
                o[2 + ax][i] = m + h - v - d; // TR
                o[4 + ax][i] = m - h + v - d; // BL
                o[6 + ax][i] = m + h + v + d; // BR
            }
        }

        return TrackSeries.Build(track, o);
    }
}

/// <summary>
/// K5b: smooth the eight homography parameters of the unit-square-to-quad map (Heckbert) instead of the corners, in three groups
/// (translation c,f; linear a,b,d,e; perspective g,h), each with a fixed-width robust local quadratic (no adaptivity: this isolates
/// the effect of the parameterization).
/// </summary>
internal sealed class HomographySmoother : ITrackSmoother
{
    private readonly int _hTrans, _hLin, _hPersp;

    public HomographySmoother(int halfTranslation, int halfLinear, int halfPerspective)
    {
        _hTrans = halfTranslation; _hLin = halfLinear; _hPersp = halfPerspective;
    }

    public string Name => $"K5b homography params, fixed local quadratic H trans {_hTrans} / linear {_hLin} / perspective {_hPersp}";

    public IReadOnlyList<Quad> Smooth(IReadOnlyList<Quad> track, double fps, double noisePx)
    {
        int n = track.Count;
        if (n < 5)
            return track;
        double[] t = TrackSeries.Times(track, fps);
        var p = new double[8][]; // a b c d e f g h
        for (int k = 0; k < 8; k++)
            p[k] = new double[n];
        for (int i = 0; i < n; i++)
        {
            double[] hp = ToParams(track[i]);
            for (int k = 0; k < 8; k++)
                p[k][i] = hp[k];
        }

        double[][] trans = Run(t, [p[2], p[5]], _hTrans);
        double[][] lin = Run(t, [p[0], p[1], p[3], p[4]], _hLin);
        double[][] per = Run(t, [p[6], p[7]], _hPersp);
        var q = new List<Quad>(n);
        for (int i = 0; i < n; i++)
        {
            double[] hp = [lin[0][i], lin[1][i], trans[0][i], lin[2][i], lin[3][i], trans[1][i], per[0][i], per[1][i]];
            q.Add(FromParams(hp, track[i].T));
        }

        return q;
    }

    private static double[][] Run(double[] t, double[][] series, int half)
    {
        if (half <= 0)
            return series;
        double sigma = Math.Max(1e-12, TrackSeries.SigmaThirdDiff(series));
        return LocalPolyCore.Run(t, series, sigma, new LocalPolyOptions(2, [half], double.PositiveInfinity, 2, true, 0), out _);
    }

    /// <summary>Heckbert's unit-square-to-quad coefficients (the same closed form as <see cref="UnitSquareMap"/>): (a,b,c,d,e,f,g,h).</summary>
    internal static double[] ToParams(Quad q)
    {
        double x0 = q.X0, y0 = q.Y0, x1 = q.X1, y1 = q.Y1, x2 = q.X3, y2 = q.Y3, x3 = q.X2, y3 = q.Y2;
        double sx = x0 - x1 + x2 - x3, sy = y0 - y1 + y2 - y3;
        if (Math.Abs(sx) < 1e-12 && Math.Abs(sy) < 1e-12)
            return [x1 - x0, x3 - x0, x0, y1 - y0, y3 - y0, y0, 0, 0];
        double dx1 = x1 - x2, dx2 = x3 - x2, dy1 = y1 - y2, dy2 = y3 - y2;
        double den = dx1 * dy2 - dx2 * dy1;
        if (Math.Abs(den) < 1e-12)
            return [x1 - x0, x3 - x0, x0, y1 - y0, y3 - y0, y0, 0, 0];
        double g = (sx * dy2 - dx2 * sy) / den, h = (dx1 * sy - sx * dy1) / den;
        return [x1 - x0 + g * x1, x3 - x0 + h * x3, x0, y1 - y0 + g * y1, y3 - y0 + h * y3, y0, g, h];
    }

    internal static Quad FromParams(double[] p, double t)
    {
        (double X, double Y) M(double u, double v)
        {
            double w = p[6] * u + p[7] * v + 1;
            return ((p[0] * u + p[1] * v + p[2]) / w, (p[3] * u + p[4] * v + p[5]) / w);
        }

        return Quad.From(t, M(0, 0), M(1, 0), M(0, 1), M(1, 1));
    }
}
