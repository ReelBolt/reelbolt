namespace ReelBolt.WorkflowEngine.Services.Video.Quality;

/// <summary>
/// Reusable sampling/analysis helpers over a <see cref="FrameGridResult"/>: region luma
/// percentiles, mean luma and consecutive-frame difference. Pure, no I/O, never throws on
/// malformed grids. Time of frame <c>i</c> is <c>i / EffectiveFps</c>.
/// </summary>
public static class FrameGridStats
{
    private static readonly double[] LumaLut = BuildLut();

    private static double[] BuildLut()
    {
        var lut = new double[256];
        for (var i = 0; i < 256; i++) lut[i] = ContrastMath.Linearise(i);
        return lut;
    }

    private static bool Valid(FrameGridResult? g) =>
        g is { PixelData: not null, GridWidth: > 0, GridHeight: > 0, FrameCount: > 0 }
        && g.PixelData.Length >= (long)g.GridWidth * g.GridHeight * 3 * g.FrameCount;

    /// <summary>
    /// Percentiles (each 0..100) of BT.709 relative luminance (0..1, gamma-decoded sRGB) over a
    /// normalised rect (nx, ny, nw, nh in 0..1) of one frame. Rects are clamped to the frame; an
    /// empty region or bad frame index yields null.
    /// </summary>
    public static double[]? RegionLumaPercentiles(FrameGridResult grid, int frameIndex, double nx, double ny, double nw, double nh, params double[] percentiles)
    {
        if (!Valid(grid) || frameIndex < 0 || frameIndex >= grid.FrameCount || percentiles is null) return null;
        if (double.IsNaN(nx) || double.IsNaN(ny) || double.IsNaN(nw) || double.IsNaN(nh)) return null;
        var x0 = (int)Math.Floor(Math.Clamp(nx, 0, 1) * grid.GridWidth);
        var y0 = (int)Math.Floor(Math.Clamp(ny, 0, 1) * grid.GridHeight);
        var x1 = (int)Math.Ceiling(Math.Clamp(nx + nw, 0, 1) * grid.GridWidth);
        var y1 = (int)Math.Ceiling(Math.Clamp(ny + nh, 0, 1) * grid.GridHeight);
        if (x1 <= x0 || y1 <= y0) return null;

        var frameBytes = grid.GridWidth * grid.GridHeight * 3;
        var baseOff = frameIndex * frameBytes;
        var values = new List<double>((x1 - x0) * (y1 - y0));
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var o = baseOff + (((y * grid.GridWidth) + x) * 3);
                values.Add((0.2126 * LumaLut[grid.PixelData[o]]) + (0.7152 * LumaLut[grid.PixelData[o + 1]]) + (0.0722 * LumaLut[grid.PixelData[o + 2]]));
            }
        }

        values.Sort();
        var result = new double[percentiles.Length];
        for (var i = 0; i < percentiles.Length; i++)
        {
            var p = double.IsNaN(percentiles[i]) ? 0 : Math.Clamp(percentiles[i], 0, 100);
            var pos = p / 100.0 * (values.Count - 1);
            var lo = (int)Math.Floor(pos);
            var hi = Math.Min(lo + 1, values.Count - 1);
            result[i] = values[lo] + ((values[hi] - values[lo]) * (pos - lo));
        }

        return result;
    }

    /// <summary>Mean of the raw (gamma-encoded) channel average, 0..255, for one frame; null if invalid.</summary>
    public static double? MeanLuma(FrameGridResult grid, int frameIndex)
    {
        if (!Valid(grid) || frameIndex < 0 || frameIndex >= grid.FrameCount) return null;
        var n = grid.GridWidth * grid.GridHeight * 3;
        long sum = 0;
        var off = frameIndex * n;
        for (var i = 0; i < n; i++) sum += grid.PixelData[off + i];
        return (double)sum / n;
    }

    /// <summary>
    /// Mean absolute difference (0..255) between each pair of consecutive frames; element i compares
    /// frame i with frame i+1. Empty for fewer than two frames or an invalid grid.
    /// </summary>
    public static double[] ConsecutiveMeanAbsDiff(FrameGridResult grid)
    {
        if (!Valid(grid) || grid.FrameCount < 2) return [];
        var n = grid.GridWidth * grid.GridHeight * 3;
        var diffs = new double[grid.FrameCount - 1];
        for (var f = 0; f < diffs.Length; f++)
        {
            long sum = 0;
            var a = f * n;
            var b = (f + 1) * n;
            for (var i = 0; i < n; i++) sum += Math.Abs(grid.PixelData[a + i] - grid.PixelData[b + i]);
            diffs[f] = (double)sum / n;
        }

        return diffs;
    }

    /// <summary>Time in seconds of frame <paramref name="index"/>; 0 when fps is not usable.</summary>
    public static double TimeOf(FrameGridResult grid, int index) =>
        grid.EffectiveFps > 0 && double.IsFinite(grid.EffectiveFps) ? index / grid.EffectiveFps : 0;
}
