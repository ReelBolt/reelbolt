#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ReelBolt.WorkflowEngine.Tests.InsertJitter.RenderPath;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo.Plates.Motion;

/// <summary>3x3 homographies from four correspondences.</summary>
internal static class Homog
{
    /// <summary>The homography (row-major, h22 = 1) mapping the four source points onto the four destination points.</summary>
    public static double[] From4((double X, double Y)[] s, (double X, double Y)[] d)
    {
        var a = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            double x = s[i].X, y = s[i].Y, u = d[i].X, v = d[i].Y;
            double[] r1 = [x, y, 1, 0, 0, 0, -u * x, -u * y, u];
            double[] r2 = [0, 0, 0, x, y, 1, -v * x, -v * y, v];
            for (int c = 0; c < 9; c++)
            {
                a[2 * i, c] = r1[c];
                a[2 * i + 1, c] = r2[c];
            }
        }

        for (int col = 0; col < 8; col++)
        {
            int piv = col;
            for (int r = col + 1; r < 8; r++)
            {
                if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col]))
                    piv = r;
            }

            for (int k = 0; k < 9; k++)
                (a[col, k], a[piv, k]) = (a[piv, k], a[col, k]);
            for (int r = 0; r < 8; r++)
            {
                if (r == col)
                    continue;
                double f = a[r, col] / a[col, col];
                for (int k = col; k < 9; k++)
                    a[r, k] -= f * a[col, k];
            }
        }

        var h = new double[9];
        for (int i = 0; i < 8; i++)
            h[i] = a[i, 8] / a[i, i];
        h[8] = 1;
        return h;
    }

    public static double[] Inverse(double[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        return
        [
            (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det,
            (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det,
            (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det
        ];
    }

    public static (double X, double Y) Apply(double[] h, double x, double y) => RingMotion.Apply(h, x, y);

    public static (double X, double Y)[] Corners(Quad q) => [q.Corner(0), q.Corner(1), q.Corner(2), q.Corner(3)];
}

/// <summary>Band-limited shake: a sum of sinusoids with random phases and frequencies in [lo, hi] Hz, normalised to an exact RMS (deterministic).</summary>
internal static class Shake
{
    public static double[] Series(string seed, int frames, double fps, double rmsPx, double loHz, double hiHz, int tones = 28)
    {
        var x = new double[frames];
        if (rmsPx <= 0)
            return x;
        ulong s = JitterScene.Fnv1a(seed);
        double Rand(ulong tag) => (JitterScene.SplitMix(s ^ tag) >> 11) * (1.0 / (1UL << 53));
        var f = new double[tones];
        var ph = new double[tones];
        for (int k = 0; k < tones; k++)
        {
            f[k] = loHz + (hiHz - loHz) * (k + Rand((ulong)(1000 + k))) / tones;
            ph[k] = 2 * Math.PI * Rand((ulong)(2000 + k));
        }

        for (int i = 0; i < frames; i++)
        {
            double t = i / fps, v = 0;
            for (int k = 0; k < tones; k++)
                v += Math.Sin(2 * Math.PI * f[k] * t + ph[k]);
            x[i] = v;
        }

        double rms = Math.Sqrt(x.Average(v => v * v));
        for (int i = 0; i < frames; i++)
            x[i] *= rmsPx / rms;
        return x;
    }
}

/// <summary>The wall behind the plate: smooth fbm luma plus sharp-edged rectangles (calendars, posters, boxes: what gives the real ring its edges). <paramref name="RectCell"/> = spacing of the rectangle grid (px; 0 = none).</summary>
internal sealed record WallSpec(string Id, double Mean, double Amp, double RectCell = 0, double RectAmp = 0, double RectMin = 20, double RectMax = 80);

/// <summary>What the camera/generator does on top of the smooth path: translation shake of the whole object, and whether the wall behind shares it.</summary>
internal sealed record ShakeSpec(string Id, double RmsPx, double LoHz, double HiHz, bool WallFollows);

internal sealed record SynthSpec(string Id, string Film, WallSpec Wall, ShakeSpec Shake, int Crf, double Blur = 1.2, double Noise = 2.0, double BezelPx = 26, int Frames = 0);

internal sealed record SynthClip(SynthSpec Spec, string Path, IReadOnlyList<Quad> Truth, IReadOnlyList<Quad> Base, long Bytes, double Kbps);

/// <summary>
/// A known-truth stand-in for the promo plates. The plate quad follows the real plate's smooth path (the staging keyframes heavily smoothed: slow push or travel), plus
/// an additive band-limited translation shake of known amplitude; a dark bezel band surrounds it; around that a textured wall. In RIGID mode the wall (the scene ring)
/// moves with the same homography as the plate (a real camera shake), in PLATE-ONLY mode the wall stays on the smooth path (the generator wobbles the plate region alone).
/// Same encode path as the real plates: optics blur, sensor noise, libx264 yuv420p at a crf chosen to land near the real bit rate (650-940 kbit/s).
/// </summary>
internal static class SyntheticPlate
{
    public const int W = 1344, H = 768;

    public static string Dir => Directory.CreateDirectory(Path.Combine(MotionEnv.Cache, "synthetic")).FullName;

    public static (byte R, byte G, byte B) PlateColor(string film) => film == "filmA" ? ((byte)61, (byte)190, (byte)81) : ((byte)0, (byte)236, (byte)1);

    public static IReadOnlyList<Quad> BasePath(string film)
    {
        PlateCase c = PlateCase.All.First(p => p.Id == film);
        (List<Quad> staging, _, _, _) = BenchSupport.LoadArtifact(c.StagingArtifact);
        return PlatesMeter.SmoothTruth(staging).Select((q, i) => q with { T = i / PromoInputs.Fps }).ToList();
    }

    public static IReadOnlyList<Quad> TruthPath(IReadOnlyList<Quad> basePath, ShakeSpec s, string film)
    {
        int n = basePath.Count;
        double[] sx = Shake.Series($"{s.Id}-{film}-x", n, PromoInputs.Fps, s.RmsPx, s.LoHz, s.HiHz);
        double[] sy = Shake.Series($"{s.Id}-{film}-y", n, PromoInputs.Fps, s.RmsPx, s.LoHz, s.HiHz);
        var o = new List<Quad>(n);
        for (int i = 0; i < n; i++)
        {
            Quad q = basePath[i];
            o.Add(new Quad(q.T, q.X0 + sx[i], q.Y0 + sy[i], q.X1 + sx[i], q.Y1 + sy[i], q.X2 + sx[i], q.Y2 + sy[i], q.X3 + sx[i], q.Y3 + sy[i]));
        }

        return o;
    }

    /// <summary>The quad grown by <paramref name="px"/> on every side (edge lines offset outward, corners re-intersected).</summary>
    public static Quad Expand(Quad q, double px)
    {
        (double X, double Y)[] c = [q.Corner(0), q.Corner(1), q.Corner(3), q.Corner(2)];
        double cx = c.Average(p => p.X), cy = c.Average(p => p.Y);
        var line = new (double X, double Y, double Dx, double Dy)[4];
        for (int e = 0; e < 4; e++)
        {
            (double ax, double ay) = c[e];
            (double bx, double by) = c[(e + 1) % 4];
            double dx = bx - ax, dy = by - ay, len = Math.Sqrt(dx * dx + dy * dy);
            dx /= len;
            dy /= len;
            double nx = dy, ny = -dx;
            if ((ax - cx) * nx + (ay - cy) * ny < 0)
            {
                nx = -nx;
                ny = -ny;
            }

            line[e] = (ax + nx * px, ay + ny * px, dx, dy);
        }

        (double X, double Y) Meet(int e1, int e2)
        {
            var a = line[e1];
            var b = line[e2];
            double den = a.Dx * b.Dy - a.Dy * b.Dx;
            double t = ((b.X - a.X) * b.Dy - (b.Y - a.Y) * b.Dx) / den;
            return (a.X + t * a.Dx, a.Y + t * a.Dy);
        }

        // polygon order TL, TR, BR, BL; lines: 0 top, 1 right, 2 bottom, 3 left.
        (double X, double Y) tl = Meet(3, 0), tr = Meet(0, 1), br = Meet(1, 2), bl = Meet(2, 3);
        return Quad.From(q.T, tl, tr, bl, br);
    }

    public static SynthClip Render(SynthSpec spec, Action<string>? log = null)
    {
        IReadOnlyList<Quad> bp = BasePath(spec.Film);
        IReadOnlyList<Quad> truth = TruthPath(bp, spec.Shake, spec.Film);
        if (spec.Frames > 0 && spec.Frames < bp.Count)
        {
            bp = bp.Take(spec.Frames).ToList();
            truth = truth.Take(spec.Frames).ToList();
        }

        string key = string.Join('-', spec.Id, spec.Film, spec.Wall.Id, spec.Shake.Id, "crf" + spec.Crf.ToString(CultureInfo.InvariantCulture), "n" + spec.Noise.ToString("0.#", CultureInfo.InvariantCulture), "f" + spec.Frames.ToString(CultureInfo.InvariantCulture), "v5");
        string path = Path.Combine(Dir, key + ".mp4");
        if (!File.Exists(path))
        {
            log?.Invoke($"   rendering synthetic {key}");
            string tmp = path + ".part.mp4";
            RenderFile(spec, bp, truth, tmp);
            File.Move(tmp, path, true);
        }

        long bytes = new FileInfo(path).Length;
        return new SynthClip(spec, path, truth, bp, bytes, bytes * 8 / (truth.Count / PromoInputs.Fps) / 1000);
    }

    private static void RenderFile(SynthSpec spec, IReadOnlyList<Quad> bp, IReadOnlyList<Quad> truth, string mp4)
    {
        int n = truth.Count;
        Quad scene = bp[0];
        Quad bezelScene = Expand(scene, spec.BezelPx);
        double[][] hWall = new double[n][];
        double[][] hObj = new double[n][];
        var sceneCorners = Homog.Corners(scene);
        for (int i = 0; i < n; i++)
        {
            hObj[i] = Homog.From4(sceneCorners, Homog.Corners(truth[i]));
            hWall[i] = spec.Shake.WallFollows ? hObj[i] : Homog.From4(sceneCorners, Homog.Corners(bp[i]));
        }

        // Wall canvas: the scene-space bounding box of everything the frame ever sees.
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            double[] inv = Homog.Inverse(hWall[i]);
            foreach ((double x, double y) in new[] { (0.0, 0.0), (W, 0), (0, H), (W, H) })
            {
                (double sx, double sy) = Homog.Apply(inv, x, y);
                minX = Math.Min(minX, sx);
                minY = Math.Min(minY, sy);
                maxX = Math.Max(maxX, sx);
                maxY = Math.Max(maxY, sy);
            }
        }

        int cx0 = (int)Math.Floor(minX) - 4, cy0 = (int)Math.Floor(minY) - 4;
        int cw = (int)Math.Ceiling(maxX) + 4 - cx0, ch = (int)Math.Ceiling(maxY) + 4 - cy0;
        float[][] wall = BuildWall(spec, cx0, cy0, cw, ch);
        AddRectangles(spec, wall, cx0, cy0, cw, ch);

        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in new[]
                 {
                     "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-video_size", string.Create(CultureInfo.InvariantCulture, $"{W}x{H}"), "-framerate", "24", "-i", "pipe:0",
                     "-c:v", "libx264", "-crf", spec.Crf.ToString(CultureInfo.InvariantCulture), "-preset", "medium", "-pix_fmt", "yuv420p", mp4
                 })
            psi.ArgumentList.Add(a);
        using Process enc = Process.Start(psi)!;
        Task<string> err = enc.StandardError.ReadToEndAsync();
        _ = enc.StandardOutput.ReadToEndAsync();
        Stream stdin = enc.StandardInput.BaseStream;

        var plane = new float[3][];
        for (int c = 0; c < 3; c++)
            plane[c] = new float[W * H];
        var cov = new float[W * H];
        var tmpPlane = new float[W * H];
        var rgb = new byte[W * H * 3];
        var rng = new Random(20261014 + spec.Crf);
        (byte pr, byte pg, byte pb) = PlateColor(spec.Film);
        float[] plateCol = [pr, pg, pb];
        float[] bezelCol = [22, 22, 24];
        float[] kernel = Gauss(spec.Blur);
        for (int i = 0; i < n; i++)
        {
            double[] invW = Homog.Inverse(hWall[i]);
            Parallel.For(0, H, y =>
            {
                for (int x = 0; x < W; x++)
                {
                    (double sx, double sy) = Homog.Apply(invW, x + 0.5, y + 0.5);
                    double u = sx - cx0, v = sy - cy0;
                    for (int c = 0; c < 3; c++)
                        plane[c][y * W + x] = Pyramid.Sample(wall[c], cw, ch, u, v);
                }
            });
            Quad bezelFrame = ApplyQuad(hObj[i], bezelScene, truth[i].T);
            EdgeMeter.Coverage(cov, W, H, bezelFrame, 4);
            Blend(plane, cov, bezelCol);
            EdgeMeter.Coverage(cov, W, H, truth[i], 8);
            Blend(plane, cov, plateCol);
            for (int c = 0; c < 3; c++)
                Blur(plane[c], tmpPlane, kernel);
            var rowRng = new Random(rng.Next());
            for (int p = 0; p < W * H; p++)
            {
                for (int c = 0; c < 3; c++)
                {
                    double g = Math.Sqrt(-2 * Math.Log(1 - rowRng.NextDouble())) * Math.Cos(2 * Math.PI * rowRng.NextDouble());
                    rgb[p * 3 + c] = (byte)Math.Clamp(Math.Round(plane[c][p] + spec.Noise * g), 0, 255);
                }
            }

            stdin.Write(rgb);
        }

        stdin.Close();
        enc.WaitForExit();
        if (enc.ExitCode != 0)
            throw new InvalidOperationException("synthetic encode failed: " + err.Result);
    }

    private static Quad ApplyQuad(double[] h, Quad q, double t)
    {
        (double X, double Y) P(int c) => Homog.Apply(h, q.Corner(c).X, q.Corner(c).Y);
        return Quad.From(t, P(0), P(1), P(2), P(3));
    }

    private static void Blend(float[][] plane, float[] cov, float[] col)
    {
        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                int p = y * W + x;
                float a = cov[p];
                if (a <= 0)
                    continue;
                for (int c = 0; c < 3; c++)
                    plane[c][p] = plane[c][p] * (1 - a) + col[c] * a;
            }
        });
    }

    /// <summary>The wall: value-noise fbm luma (cells 70/26/9 px) around a mean, non-green tones, on a canvas that covers every scene coordinate the frame ever sees.</summary>
    private static float[][] BuildWall(SynthSpec spec, int x0, int y0, int w, int h)
    {
        ulong seed = JitterScene.Fnv1a("wall-" + spec.Wall.Id + spec.Film);
        var o = new float[3][];
        for (int c = 0; c < 3; c++)
            o[c] = new float[w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                double px = x0 + x, py = y0 + y;
                double l = Fbm(seed ^ 0x1111, px, py), rr = Fbm(seed ^ 0x2222, px, py), bb = Fbm(seed ^ 0x3333, px, py);
                double luma = spec.Wall.Mean + spec.Wall.Amp * 2 * (l - 0.5);
                int i = y * w + x;
                o[0][i] = (float)Math.Clamp(luma * (1 + 0.10 * (rr - 0.5)), 0, 255);
                o[1][i] = (float)Math.Clamp(luma * 0.86, 0, 255);
                o[2][i] = (float)Math.Clamp(luma * (1 + 0.10 * (bb - 0.5)), 0, 255);
            }
        });
        return o;
    }

    private static void AddRectangles(SynthSpec spec, float[][] wall, int x0, int y0, int w, int h)
    {
        WallSpec ws = spec.Wall;
        if (ws.RectCell <= 0 || ws.RectAmp <= 0)
            return;
        ulong seed = JitterScene.Fnv1a("rects-" + ws.Id + spec.Film);
        int gx0 = (int)Math.Floor(x0 / ws.RectCell) - 1, gx1 = (int)Math.Ceiling((x0 + w) / ws.RectCell) + 1;
        int gy0 = (int)Math.Floor(y0 / ws.RectCell) - 1, gy1 = (int)Math.Ceiling((y0 + h) / ws.RectCell) + 1;
        for (int gy = gy0; gy <= gy1; gy++)
        {
            for (int gx = gx0; gx <= gx1; gx++)
            {
                double R(ulong tag) => (JitterScene.SplitMix(seed ^ ((ulong)(uint)gx * 0x9E3779B97F4A7C15UL) ^ ((ulong)(uint)gy * 0xC2B2AE3D27D4EB4FUL) ^ tag) >> 11) * (1.0 / (1UL << 53));
                if (R(1) < 0.25)
                    continue;
                double rw = ws.RectMin + (ws.RectMax - ws.RectMin) * R(2), rh = ws.RectMin + (ws.RectMax - ws.RectMin) * R(3);
                double cx = (gx + R(4)) * ws.RectCell, cy = (gy + R(5)) * ws.RectCell;
                double delta = ws.RectAmp * (R(6) < 0.5 ? -1 : 1) * (0.4 + 0.6 * R(7));
                int xa = Math.Max(0, (int)Math.Floor(cx - rw / 2 - x0)), xb = Math.Min(w - 1, (int)Math.Ceiling(cx + rw / 2 - x0));
                int ya = Math.Max(0, (int)Math.Floor(cy - rh / 2 - y0)), yb = Math.Min(h - 1, (int)Math.Ceiling(cy + rh / 2 - y0));
                for (int y = ya; y <= yb; y++)
                {
                    for (int x = xa; x <= xb; x++)
                    {
                        // 1 px soft edge
                        double ex = Math.Min(x + 0.5 + x0 - (cx - rw / 2), (cx + rw / 2) - (x + 0.5 + x0)), ey = Math.Min(y + 0.5 + y0 - (cy - rh / 2), (cy + rh / 2) - (y + 0.5 + y0));
                        double a = Math.Clamp(Math.Min(ex, ey) + 0.5, 0, 1);
                        if (a <= 0)
                            continue;
                        int i = y * w + x;
                        wall[0][i] = (float)Math.Clamp(wall[0][i] + a * delta, 0, 255);
                        wall[1][i] = (float)Math.Clamp(wall[1][i] + a * delta * 0.86, 0, 255);
                        wall[2][i] = (float)Math.Clamp(wall[2][i] + a * delta, 0, 255);
                    }
                }
            }
        }
    }

    private static double Fbm(ulong seed, double x, double y) =>
        0.55 * ValueNoise(seed, x, y, 70) + 0.30 * ValueNoise(seed + 1, x, y, 26) + 0.15 * ValueNoise(seed + 2, x, y, 9);

    private static double ValueNoise(ulong seed, double x, double y, double cell)
    {
        double gx = x / cell, gy = y / cell;
        int ix = (int)Math.Floor(gx), iy = (int)Math.Floor(gy);
        double fx = gx - ix, fy = gy - iy;
        fx = fx * fx * (3 - 2 * fx);
        fy = fy * fy * (3 - 2 * fy);
        double a = Lattice(seed, ix, iy), b = Lattice(seed, ix + 1, iy), c = Lattice(seed, ix, iy + 1), d = Lattice(seed, ix + 1, iy + 1);
        return (a + (b - a) * fx) + ((c + (d - c) * fx) - (a + (b - a) * fx)) * fy;
    }

    private static double Lattice(ulong seed, int ix, int iy)
    {
        ulong h = JitterScene.SplitMix(seed ^ ((ulong)(uint)ix * 0x9E3779B97F4A7C15UL) ^ ((ulong)(uint)iy * 0xC2B2AE3D27D4EB4FUL));
        return (h >> 11) * (1.0 / (1UL << 53));
    }

    private static float[] Gauss(double sigma)
    {
        int r = (int)Math.Ceiling(3 * sigma);
        var k = new float[2 * r + 1];
        double sum = 0;
        for (int i = -r; i <= r; i++)
        {
            double v = Math.Exp(-0.5 * i * i / (sigma * sigma));
            k[i + r] = (float)v;
            sum += v;
        }

        for (int i = 0; i < k.Length; i++)
            k[i] = (float)(k[i] / sum);
        return k;
    }

    private static void Blur(float[] f, float[] tmp, float[] k)
    {
        int r = k.Length / 2;
        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                float s = 0;
                for (int i = -r; i <= r; i++)
                    s += f[y * W + Math.Clamp(x + i, 0, W - 1)] * k[i + r];
                tmp[y * W + x] = s;
            }
        });
        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                float s = 0;
                for (int i = -r; i <= r; i++)
                    s += tmp[Math.Clamp(y + i, 0, H - 1) * W + x] * k[i + r];
                f[y * W + x] = s;
            }
        });
    }
}
