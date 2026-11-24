#nullable enable
using System;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.EdgeSignal;

/// <summary>
/// A scalar image the edge fitter probes. Coordinates are GRID pixels with pixel centres at integers
/// (the convention <c>ChromaEdgeLineFitter</c> uses), whatever the underlying plane's resolution.
/// </summary>
internal interface IScalarField
{
    double Sample(double x, double y);
}

/// <summary>Luma weights used to turn the decoded RGB grid back into the decoded Y plane.</summary>
internal enum LumaMatrix
{
    /// <summary>BT.601 (0.299, 0.587, 0.114): the matrix swscale assumes for an untagged clip, i.e. what the production grid decode inverts.</summary>
    Bt601,

    /// <summary>BT.709 (0.2126, 0.7152, 0.0722).</summary>
    Bt709
}

internal static class LumaPlane
{
    public static (float R, float G, float B) Weights(LumaMatrix m) =>
        m == LumaMatrix.Bt709 ? (0.2126f, 0.7152f, 0.0722f) : (0.299f, 0.587f, 0.114f);

    /// <summary>Luma of one RGB24 frame inside <paramref name="rgb"/> starting at <paramref name="offset"/>.</summary>
    public static float[] FromRgb(byte[] rgb, int offset, int width, int height, LumaMatrix matrix, float[]? reuse = null)
    {
        (float wr, float wg, float wb) = Weights(matrix);
        float[] y = reuse is { } r && r.Length == width * height ? r : new float[width * height];
        for (int p = 0; p < y.Length; p++)
        {
            int b0 = offset + p * 3;
            y[p] = wr * rgb[b0] + wg * rgb[b0 + 1] + wb * rgb[b0 + 2];
        }

        return y;
    }
}

/// <summary>
/// Bilinear field over a float plane. <paramref name="scaleX"/>/<paramref name="scaleY"/> are plane pixels per GRID
/// pixel (1 for a grid-resolution plane, 2 for a plane at twice the grid). Edge-clamped like the production sampler;
/// <paramref name="clampNegative"/> reproduces its per-pixel <c>Math.Max(0, v)</c> (the dominance signal).
/// </summary>
internal sealed class FloatPlaneField : IScalarField
{
    private readonly float[] _data;
    private readonly int _w, _h;
    private readonly double _sx, _sy;
    private readonly bool _clampNegative;

    public FloatPlaneField(float[] data, int width, int height, double scaleX = 1, double scaleY = 1, bool clampNegative = false)
    {
        _data = data;
        _w = width;
        _h = height;
        _sx = scaleX;
        _sy = scaleY;
        _clampNegative = clampNegative;
    }

    public double Sample(double x, double y)
    {
        if (_sx != 1)
            x = (x + 0.5) * _sx - 0.5;
        if (_sy != 1)
            y = (y + 0.5) * _sy - 0.5;
        x = Math.Clamp(x, 0, _w - 1.000001);
        y = Math.Clamp(y, 0, _h - 1.000001);
        int x0 = (int)x, y0 = (int)y;
        double fx = x - x0, fy = y - y0;
        int i = y0 * _w + x0;
        double v00 = _data[i], v10 = _data[i + 1], v01 = _data[i + _w], v11 = _data[i + _w + 1];
        if (_clampNegative)
        {
            v00 = Math.Max(0, v00);
            v10 = Math.Max(0, v10);
            v01 = Math.Max(0, v01);
            v11 = Math.Max(0, v11);
        }

        return (v00 * (1 - fx) + v10 * fx) * (1 - fy) + (v01 * (1 - fx) + v11 * fx) * fy;
    }
}

/// <summary>Bilinear field over an 8-bit gray plane (a streamed hi-resolution luma frame; no float copy).</summary>
internal sealed class BytePlaneField : IScalarField
{
    private readonly byte[] _data;
    private readonly int _w, _h, _off;
    private readonly double _sx, _sy;

    public BytePlaneField(byte[] data, int width, int height, double scaleX, double scaleY, int offset = 0)
    {
        _off = offset;
        _data = data;
        _w = width;
        _h = height;
        _sx = scaleX;
        _sy = scaleY;
    }

    public double Sample(double x, double y)
    {
        x = (x + 0.5) * _sx - 0.5;
        y = (y + 0.5) * _sy - 0.5;
        x = Math.Clamp(x, 0, _w - 1.000001);
        y = Math.Clamp(y, 0, _h - 1.000001);
        int x0 = (int)x, y0 = (int)y;
        double fx = x - x0, fy = y - y0;
        int i = _off + y0 * _w + x0;
        return (_data[i] * (1 - fx) + _data[i + 1] * fx) * (1 - fy) + (_data[i + _w] * (1 - fx) + _data[i + _w + 1] * fx) * fy;
    }
}
