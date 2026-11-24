#nullable enable
using System;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter;

/// <summary>
/// One plate quad at one instant, in SOURCE pixels (continuous coordinates: pixel i covers
/// [i, i+1), so a normalized coordinate times the frame size lands here directly — the same
/// convention <c>ChromaQuadTracker</c> uses for <c>(x + 0.5) / width</c>). Corner order is
/// TL, TR, BL, BR, identical to the artifact's <c>x0..y3</c>.
/// </summary>
public readonly record struct Quad(double T, double X0, double Y0, double X1, double Y1, double X2, double Y2, double X3, double Y3)
{
    /// <summary>Corner <paramref name="c"/> (0 TL, 1 TR, 2 BL, 3 BR).</summary>
    public (double X, double Y) Corner(int c) => c switch
    {
        0 => (X0, Y0),
        1 => (X1, Y1),
        2 => (X2, Y2),
        _ => (X3, Y3)
    };

    /// <summary>The same quad with every coordinate multiplied by <paramref name="sx"/>, <paramref name="sy"/>.</summary>
    public Quad Scaled(double sx, double sy) =>
        new(T, X0 * sx, Y0 * sy, X1 * sx, Y1 * sy, X2 * sx, Y2 * sy, X3 * sx, Y3 * sy);

    /// <summary>Builds a quad from four corner tuples in TL, TR, BL, BR order.</summary>
    public static Quad From(double t, (double X, double Y) tl, (double X, double Y) tr, (double X, double Y) bl, (double X, double Y) br) =>
        new(t, tl.X, tl.Y, tr.X, tr.Y, bl.X, bl.Y, br.X, br.Y);

    /// <summary>
    /// An artifact keyframe (normalized) in source pixels. With <paramref name="preferSurface"/>
    /// the silhouette-refined <c>SurfaceCorners</c> are used when present — the corners the
    /// compile step actually pins to (<c>BuildInsertKeyframes</c>); otherwise <c>x0..y3</c>.
    /// </summary>
    public static Quad FromKeyframe(VideoInsertQuadKeyframe k, int w, int h, bool preferSurface = true)
    {
        if (preferSurface && k.SurfaceCorners is { } s)
            return new Quad(k.TimeSec, s.X0 * w, s.Y0 * h, s.X1 * w, s.Y1 * h, s.X2 * w, s.Y2 * h, s.X3 * w, s.Y3 * h);
        return new Quad(k.TimeSec, k.X0 * w, k.Y0 * h, k.X1 * w, k.Y1 * h, k.X2 * w, k.Y2 * h, k.X3 * w, k.Y3 * h);
    }

    /// <summary>
    /// A tracker-internal per-frame quad (normalized) in source pixels, stamped at
    /// <paramref name="t"/>. <paramref name="preferSurface"/> as in <see cref="FromKeyframe"/>.
    /// </summary>
    internal static Quad FromFrameQuad(ChromaQuadTracker.FrameQuad q, int w, int h, double t, bool preferSurface = true)
    {
        if (preferSurface && q.Surface != default)
        {
            var s = q.Surface;
            return new Quad(t, s.X0 * w, s.Y0 * h, s.X1 * w, s.Y1 * h, s.X2 * w, s.Y2 * h, s.X3 * w, s.Y3 * h);
        }

        return new Quad(t, q.X0 * w, q.Y0 * h, q.X1 * w, q.Y1 * h, q.X2 * w, q.Y2 * h, q.X3 * w, q.Y3 * h);
    }

    /// <summary>The unit-square to quad homography: (0,0) to TL, (1,0) to TR, (0,1) to BL, (1,1) to BR.</summary>
    public UnitSquareMap ToMap() => UnitSquareMap.Create(this);
}

/// <summary>
/// Projective map from the unit square onto a <see cref="Quad"/> (Heckbert's closed form).
/// What a viewer sees inside the screen: a point (u,v) of the inserted content lands here.
/// </summary>
public readonly struct UnitSquareMap
{
    private readonly double _a, _b, _c, _d, _e, _f, _g, _h;

    private UnitSquareMap(double a, double b, double c, double d, double e, double f, double g, double h)
    {
        _a = a; _b = b; _c = c; _d = d; _e = e; _f = f; _g = g; _h = h;
    }

    public static UnitSquareMap Create(Quad q)
    {
        // Heckbert order: p0 = (0,0), p1 = (1,0), p2 = (1,1), p3 = (0,1)  ->  TL, TR, BR, BL.
        double x0 = q.X0, y0 = q.Y0, x1 = q.X1, y1 = q.Y1, x2 = q.X3, y2 = q.Y3, x3 = q.X2, y3 = q.Y2;
        double sx = x0 - x1 + x2 - x3, sy = y0 - y1 + y2 - y3;
        if (Math.Abs(sx) < 1e-12 && Math.Abs(sy) < 1e-12)
            return new UnitSquareMap(x1 - x0, x3 - x0, x0, y1 - y0, y3 - y0, y0, 0, 0);

        double dx1 = x1 - x2, dx2 = x3 - x2, dy1 = y1 - y2, dy2 = y3 - y2;
        double den = dx1 * dy2 - dx2 * dy1;
        if (Math.Abs(den) < 1e-12)
            return new UnitSquareMap(x1 - x0, x3 - x0, x0, y1 - y0, y3 - y0, y0, 0, 0);

        double g = (sx * dy2 - dx2 * sy) / den;
        double h = (dx1 * sy - sx * dy1) / den;
        return new UnitSquareMap(x1 - x0 + g * x1, x3 - x0 + h * x3, x0, y1 - y0 + g * y1, y3 - y0 + h * y3, y0, g, h);
    }

    /// <summary>Maps (u,v) in the unit square to source pixels.</summary>
    public (double X, double Y) Map(double u, double v)
    {
        double w = _g * u + _h * v + 1;
        return ((_a * u + _b * v + _c) / w, (_d * u + _e * v + _f) / w);
    }
}
