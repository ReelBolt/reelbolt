#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Smoothing;

/// <summary>
/// Detects HELD source frames — frames whose picture is a repeat of the previous one — from the sampled detection grid.
/// Generated video is often frame-rate converted by repeating frames, so the plate's true geometry changes only on the
/// first frame of each repeat group; every per-frame detection inside the group measures the same geometry.
/// <para>
/// The detector looks only at the plate's boundary: for each frame it samples points every grid pixel along the four edges of the
/// tracked quad, ±1 px across the edge, and averages the absolute difference (summed over R, G, B) between this frame and the
/// previous one (<see cref="MeasureEdgeDiff"/>); the held decision is then made on the sequence of those numbers
/// (<see cref="FromEdgeDiff"/>). Needs the sampled grid, so a production use belongs inside <c>Track()</c>, not in a smoother
/// that only sees the quads.
/// </para>
/// </summary>
internal static class HeldFrameDetector
{
    /// <summary>Per grid frame: the mean absolute difference (R+G+B levels) against the previous frame along the quad's edges, and over its interior (noise floor).</summary>
    public static void MeasureEdgeDiff(byte[] grid, int frames, int gw, int gh, IReadOnlyList<ChromaQuadTracker.FrameQuad> quads, out double[] edgeDiff, out double[] noiseDiff)
    {
        edgeDiff = new double[frames];
        noiseDiff = new double[frames];
        int frameSize = gw * gh * 3;
        var byFrame = quads.ToDictionary(q => q.FrameIndex);
        for (int f = 1; f < frames; f++)
        {
            if (!byFrame.TryGetValue(f, out ChromaQuadTracker.FrameQuad? q))
                continue;
            (double X, double Y) tl = (q.X0 * gw, q.Y0 * gh), tr = (q.X1 * gw, q.Y1 * gh), bl = (q.X2 * gw, q.Y2 * gh), br = (q.X3 * gw, q.Y3 * gh);
            double strip = 0;
            int cnt = 0;
            foreach (((double X, double Y) a, (double X, double Y) b) in new[] { (tl, tr), (tr, br), (br, bl), (bl, tl) })
            {
                double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                if (len < 2)
                    continue;
                double nx = -(b.Y - a.Y) / len, ny = (b.X - a.X) / len;
                int steps = (int)Math.Floor(len);
                for (int k = 0; k < steps; k++)
                {
                    double px = a.X + (b.X - a.X) * (k + 0.5) / steps, py = a.Y + (b.Y - a.Y) * (k + 0.5) / steps;
                    foreach (double off in new[] { -1.0, 1.0 })
                    {
                        double d = Diff(grid, f, frameSize, gw, gh, px + nx * off, py + ny * off);
                        if (!double.IsNaN(d))
                        {
                            strip += d;
                            cnt++;
                        }
                    }
                }
            }

            double noise = 0;
            int ncnt = 0;
            for (int iu = 0; iu < 15; iu++)
            {
                for (int iv = 0; iv < 15; iv++)
                {
                    double u = 0.15 + 0.7 * iu / 14.0, v = 0.15 + 0.7 * iv / 14.0;
                    double px = (tl.X * (1 - u) + tr.X * u) * (1 - v) + (bl.X * (1 - u) + br.X * u) * v;
                    double py = (tl.Y * (1 - u) + tr.Y * u) * (1 - v) + (bl.Y * (1 - u) + br.Y * u) * v;
                    double d = Diff(grid, f, frameSize, gw, gh, px, py);
                    if (!double.IsNaN(d))
                    {
                        noise += d;
                        ncnt++;
                    }
                }
            }

            if (cnt == 0 || ncnt == 0)
                continue;
            strip /= cnt;
            noise /= ncnt;
            edgeDiff[f] = strip;
            noiseDiff[f] = noise;
        }

    }

    /// <summary>
    /// Held flags from the per-frame edge statistic alone: frame f is held when its edge difference is at most 0.3 x the largest of its
    /// neighbours' (f-2..f+2) — a repeat sits next to the frame that introduced the new geometry — or below 0.2 levels (nothing moved at all).
    /// The edge difference of a held frame is not zero (codec residue, 1-2 levels on the hold-cadence scene) so a fixed floor cannot work; the
    /// alternation against neighbours does, and a smoothly decelerating plate (ratio near 1) is never flagged.
    /// </summary>
    public static bool[] FromEdgeDiff(double[] s)
    {
        var held = new bool[s.Length];
        for (int f = 1; f < s.Length; f++)
        {
            double peak = 0;
            for (int j = Math.Max(1, f - 2); j <= Math.Min(s.Length - 1, f + 2); j++)
            {
                if (j != f)
                    peak = Math.Max(peak, s[j]);
            }

            held[f] = s[f] <= 0.2 || s[f] <= 0.3 * peak;
        }

        return held;
    }

    private static double Diff(byte[] grid, int f, int frameSize, int gw, int gh, double x, double y)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        if (ix < 0 || iy < 0 || ix >= gw || iy >= gh)
            return double.NaN;
        int a = f * frameSize + (iy * gw + ix) * 3, b = a - frameSize;
        return Math.Abs(grid[a] - grid[b]) + Math.Abs(grid[a + 1] - grid[b + 1]) + Math.Abs(grid[a + 2] - grid[b + 2]);
    }
}

/// <summary>
/// Frame-hold cadence wrapper: consecutive frames flagged as held (at most <c>MaxGroup</c> long) form a group measuring ONE geometry.
/// The group's measurements are averaged into a single sample stamped at the group's FIRST frame (the frame where the new geometry
/// appears), the inner smoother runs on that decimated series, and its output is expanded back as a step function — every frame of a group gets the
/// group's value. The steps are preserved rather than smeared, and the noise of a group of m repeats drops by sqrt(m).
/// With no held frames this is exactly the inner smoother.
/// </summary>
internal sealed class CadenceSmoother : ITrackSmoother
{
    private readonly ITrackSmoother _inner;
    private readonly bool[] _held;
    private readonly int _maxGroup;

    public CadenceSmoother(ITrackSmoother inner, bool[] held, int maxGroup = 4)
    {
        _inner = inner; _held = held; _maxGroup = maxGroup;
    }

    public string Name => $"cadence-aware({_inner.Name})";

    public IReadOnlyList<Quad> Smooth(IReadOnlyList<Quad> track, double fps, double noisePx)
    {
        int n = track.Count;
        var groups = new List<List<int>>();
        for (int i = 0; i < n; i++)
        {
            int frame = (int)Math.Round(track[i].T * fps);
            bool heldFrame = frame >= 0 && frame < _held.Length && _held[frame];
            if (i > 0 && heldFrame && groups[^1].Count < _maxGroup && (int)Math.Round(track[i - 1].T * fps) == frame - 1)
                groups[^1].Add(i);
            else
                groups.Add([i]);
        }

        if (groups.Count == n)
            return _inner.Smooth(track, fps, noisePx);

        var collapsed = new List<Quad>(groups.Count);
        foreach (List<int> g in groups)
        {
            Quad first = track[g[0]];
            double Mean(Func<Quad, double> sel) => g.Average(i => sel(track[i]));
            collapsed.Add(new Quad(first.T, Mean(q => q.X0), Mean(q => q.Y0), Mean(q => q.X1), Mean(q => q.Y1), Mean(q => q.X2), Mean(q => q.Y2), Mean(q => q.X3), Mean(q => q.Y3)));
        }

        IReadOnlyList<Quad> sm = _inner.Smooth(collapsed, fps, noisePx);
        var o = new Quad[n];
        for (int gi = 0; gi < groups.Count; gi++)
        {
            foreach (int i in groups[gi])
                o[i] = sm[gi] with { T = track[i].T };
        }

        return o;
    }
}
