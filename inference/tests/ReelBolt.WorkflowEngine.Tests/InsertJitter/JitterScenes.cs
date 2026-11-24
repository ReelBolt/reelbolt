#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter;

/// <summary>How a scene is rendered and encoded.</summary>
public enum RenderMode
{
    /// <summary>Sensor noise sigma 2 (8-bit), encoded as the ticket specifies: libx264 veryfast crf 20 yuv420p.</summary>
    Standard,

    /// <summary>Oracle o1: no sensor noise, lossless RGB (libx264rgb crf 0) — leaves only the grid/quantization floor.</summary>
    NoiselessLossless,

    /// <summary>Oracle o1b: no sensor noise, the standard lossy encode (libx264 veryfast crf 20 yuv420p) — isolates the codec.</summary>
    NoiselessStandardEncode,

    /// <summary>Oracle o1c: sensor noise sigma 2, lossless RGB — isolates the sensor noise.</summary>
    NoisyLossless,

    /// <summary>Oracle o1d: no sensor noise, lossless but 4:2:0 (libx264 crf 0 yuv420p) — isolates chroma subsampling from quantization.</summary>
    NoiselessLosslessYuv420
}

/// <summary>A scene encoded to disk together with its exact ground truth.</summary>
public sealed record RenderedClip(JitterScene Scene, string Path, IReadOnlyList<Quad> Truth);

/// <summary>
/// A deterministic synthetic locked-off or moving chroma-plate scene. Geometry is analytic, so the
/// per-frame ground-truth corners are exact (continuous source-pixel coordinates, pixel i covers
/// [i, i+1)); the picture is a seeded value-noise background in non-green tones, a plate with
/// anti-aliased edges (4x4 supersampled coverage near an edge, analytic inside), optional flicker,
/// an optional skin-coloured occluder and additive Gaussian sensor noise.
/// </summary>
public sealed class JitterScene
{
    /// <summary>Bump when ANY pixel the renderer produces could change, so cached clips are re-rendered.</summary>
    public const int RendererVersion = 2;

    private const int TexMargin = 24;

    private readonly Func<int, Quad[]> _plateAt;
    private readonly Func<int, (double Tx, double Ty, double Rot)>? _camera;

    internal JitterScene(
        string name, int width, int height, double fps, double durationSec, (byte R, byte G, byte B) plateColor,
        Func<int, Quad[]> plateAt, Func<int, (double, double, double)>? camera = null, bool flicker = false, bool occluder = false)
    {
        Name = name;
        Width = width;
        Height = height;
        Fps = fps;
        Frames = (int)Math.Round(durationSec * fps);
        PlateColor = plateColor;
        _plateAt = plateAt;
        _camera = camera;
        Flicker = flicker;
        Occluder = occluder;
        Seed = Fnv1a(name);
    }

    public string Name { get; }
    public int Width { get; }
    public int Height { get; }
    public double Fps { get; }
    public int Frames { get; }
    public (byte R, byte G, byte B) PlateColor { get; }
    public bool Flicker { get; }
    public bool Occluder { get; }
    public ulong Seed { get; }

    /// <summary>Exact plate corners in frame <paramref name="f"/> (source pixels, after any camera motion).</summary>
    public Quad TruthAt(int f)
    {
        Quad p = _plateAt(f)[0];
        if (_camera is null)
            return p with { T = f / Fps };
        (double tx, double ty, double rot) = _camera(f);
        double c = Math.Cos(rot), s = Math.Sin(rot), cx = Width / 2.0, cy = Height / 2.0;
        (double X, double Y) Map(double x, double y) => (cx + (x - cx) * c - (y - cy) * s + tx, cy + (x - cx) * s + (y - cy) * c + ty);
        return Quad.From(f / Fps, Map(p.X0, p.Y0), Map(p.X1, p.Y1), Map(p.X2, p.Y2), Map(p.X3, p.Y3));
    }

    public IReadOnlyList<Quad> Truth() => Enumerable.Range(0, Frames).Select(TruthAt).ToList();

    /// <summary>
    /// Renders (or reuses, when an up-to-date clip is on disk) the scene into
    /// <paramref name="dir"/>/<c>name[-o1].mp4</c> and returns it with its ground truth.
    /// </summary>
    public RenderedClip Render(string dir, RenderMode mode = RenderMode.Standard)
    {
        Directory.CreateDirectory(dir);
        string suffix = mode switch { RenderMode.Standard => string.Empty, RenderMode.NoiselessLossless => "-o1", RenderMode.NoiselessStandardEncode => "-o1b", RenderMode.NoisyLossless => "-o1c", _ => "-o1d" };
        string path = Path.Combine(dir, $"{Name}{suffix}.mp4");
        bool lossless = mode is RenderMode.NoiselessLossless or RenderMode.NoisyLossless;
        bool lossless420 = mode == RenderMode.NoiselessLosslessYuv420;
        string stamp = path + ".stamp";
        string want = string.Create(CultureInfo.InvariantCulture, $"v{RendererVersion}|{mode}|{Width}x{Height}@{Fps}|{Frames}");
        if (File.Exists(path) && File.Exists(stamp) && File.ReadAllText(stamp) == want)
            return new RenderedClip(this, path, Truth());

        double sigma = mode is RenderMode.Standard or RenderMode.NoisyLossless ? 2.0 : 0.0;
        string tmp = path + ".part.mp4";
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in new[]
                 {
                     "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24",
                     "-s", $"{Width}x{Height}", "-r", Fps.ToString(CultureInfo.InvariantCulture), "-i", "pipe:0",
                     "-c:v", lossless ? "libx264rgb" : "libx264",
                     "-preset", lossless || lossless420 ? "ultrafast" : "veryfast",
                     "-crf", lossless || lossless420 ? "0" : "20",
                     "-pix_fmt", lossless ? "rgb24" : "yuv420p",
                     "-f", "mp4", tmp
                 })
            psi.ArgumentList.Add(a);

        using Process ffmpeg = Process.Start(psi)!;
        Task<string> err = ffmpeg.StandardError.ReadToEndAsync();
        _ = ffmpeg.StandardOutput.ReadToEndAsync();

        byte[] background = BuildTexture();
        var frame = new byte[Width * Height * 3];
        Stream stdin = ffmpeg.StandardInput.BaseStream;
        for (int f = 0; f < Frames; f++)
        {
            RenderFrame(f, sigma, background, frame);
            stdin.Write(frame, 0, frame.Length);
        }

        stdin.Close();
        ffmpeg.WaitForExit();
        if (ffmpeg.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed encoding {Name}: {err.Result}");

        File.Move(tmp, path, overwrite: true);
        File.WriteAllText(stamp, want);
        return new RenderedClip(this, path, Truth());
    }

    // -------------------------------------------------------------------------------------------
    // Pixels
    // -------------------------------------------------------------------------------------------

    /// <summary>The (W + 2m) x (H + 2m) value-noise background, so a shaking camera can resample it.</summary>
    private byte[] BuildTexture()
    {
        int tw = Width + 2 * TexMargin, th = Height + 2 * TexMargin;
        var tex = new byte[tw * th * 3];
        Parallel.For(0, th, y =>
        {
            for (int x = 0; x < tw; x++)
            {
                double px = x - TexMargin, py = y - TexMargin;
                double l = Fbm(Seed ^ 0x1111, px, py);
                double rr = Fbm(Seed ^ 0x2222, px, py);
                double bb = Fbm(Seed ^ 0x3333, px, py);
                double gg = Fbm(Seed ^ 0x4444, px, py);
                // Non-green tones: g never exceeds the larger of r and b, and sits well below the plate's.
                double luma = 62 + 100 * l;
                double r = luma * (1 + 0.14 * (rr - 0.5));
                double b = luma * (1 + 0.14 * (bb - 0.5));
                double g = luma * 0.86 * (1 + 0.06 * (gg - 0.5));
                int o = (y * tw + x) * 3;
                tex[o] = Clamp(r);
                tex[o + 1] = Clamp(g);
                tex[o + 2] = Clamp(b);
            }
        });
        return tex;
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
        ulong h = SplitMix(seed ^ ((ulong)(uint)ix * 0x9E3779B97F4A7C15UL) ^ ((ulong)(uint)iy * 0xC2B2AE3D27D4EB4FUL));
        return (h >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>The background texture (for in-memory control renders).</summary>
    internal byte[] Texture() => BuildTexture();

    /// <summary>One frame rendered in memory with the plate in <paramref name="plate"/>, no noise and no encode — a control for detectors.</summary>
    internal void RenderControlFrame(int f, byte[] texture, byte[] dst, (byte R, byte G, byte B) plate) => RenderFrame(f, 0, texture, dst, plate);

    private void RenderFrame(int f, double sigma, byte[] texture, byte[] dst, (byte R, byte G, byte B)? plateOverride = null)
    {
        int w = Width, h = Height, tw = w + 2 * TexMargin;

        // 1. Background — a straight crop when the camera is locked off, a bilinear resample when it shakes.
        if (_camera is null)
        {
            Parallel.For(0, h, y => Buffer.BlockCopy(texture, ((y + TexMargin) * tw + TexMargin) * 3, dst, y * w * 3, w * 3));
        }
        else
        {
            (double tx, double ty, double rot) = _camera(f);
            double c = Math.Cos(-rot), s = Math.Sin(-rot), cx = w / 2.0, cy = h / 2.0;
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    double qx = x + 0.5 - cx - tx, qy = y + 0.5 - cy - ty;
                    double sx = cx + qx * c - qy * s - 0.5 + TexMargin, sy = cy + qx * s + qy * c - 0.5 + TexMargin;
                    int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                    double fx = sx - x0, fy = sy - y0;
                    int xa = Math.Clamp(x0, 0, tw - 1), xb = Math.Clamp(x0 + 1, 0, tw - 1);
                    int ya = Math.Clamp(y0, 0, h + 2 * TexMargin - 1), yb = Math.Clamp(y0 + 1, 0, h + 2 * TexMargin - 1);
                    for (int ch = 0; ch < 3; ch++)
                    {
                        double v00 = texture[(ya * tw + xa) * 3 + ch], v10 = texture[(ya * tw + xb) * 3 + ch];
                        double v01 = texture[(yb * tw + xa) * 3 + ch], v11 = texture[(yb * tw + xb) * 3 + ch];
                        dst[(y * w + x) * 3 + ch] = Clamp((v00 * (1 - fx) + v10 * fx) * (1 - fy) + (v01 * (1 - fx) + v11 * fx) * fy);
                    }
                }
            });
        }

        // 2. The plate: flat colour, anti-aliased edges.
        double brightness = 1.0;
        if (Flicker)
        {
            ulong h0 = SplitMix(Seed ^ 0xF11C0E12UL ^ (ulong)f * 0xD6E8FEB86659FD93UL);
            brightness = 1 + 0.05 * (2 * ((h0 >> 11) * (1.0 / (1UL << 53))) - 1);
        }

        Quad t = TruthAt(f);
        (byte R, byte G, byte B) pc = plateOverride ?? PlateColor;
        FillConvexQuad(dst, t, (pc.R * brightness, pc.G * brightness, pc.B * brightness));

        // 3. The occluder: a skin-coloured rounded blob that enters from the right, crosses the
        //    plate's BR corner at t = 4.5 s and leaves downward at t = 5 s.
        if (Occluder)
        {
            double sec = f / Fps;
            if (sec >= 4.0 && sec <= 5.0)
            {
                double u = sec - 4.0;
                DrawRoundedBox(dst, t.X3 + (1 - 2 * u) * 260, t.Y3 + (2 * u - 1) * 260, 130, 90, 50, (224, 172, 140));
            }
        }

        // 4. Sensor noise.
        if (sigma > 0)
        {
            Parallel.For(0, h, y =>
            {
                ulong state = SplitMix(Seed ^ ((ulong)f << 20) ^ (ulong)y * 0x9E3779B97F4A7C15UL ^ 0x5EED);
                int row = y * w * 3;
                for (int i = 0; i < w * 3; i += 2)
                {
                    double u1 = 1 - Next(ref state), u2 = Next(ref state);
                    double mag = sigma * Math.Sqrt(-2 * Math.Log(u1));
                    dst[row + i] = Clamp(dst[row + i] + mag * Math.Cos(2 * Math.PI * u2));
                    if (i + 1 < w * 3)
                        dst[row + i + 1] = Clamp(dst[row + i + 1] + mag * Math.Sin(2 * Math.PI * u2));
                }
            });
        }
    }

    private void FillConvexQuad(byte[] dst, Quad q, (double R, double G, double B) color)
    {
        // Edge order around the outline: TL -> TR -> BR -> BL.
        (double X, double Y)[] p = [q.Corner(0), q.Corner(1), q.Corner(3), q.Corner(2)];
        double cx = p.Average(v => v.X), cy = p.Average(v => v.Y);
        var nx = new double[4];
        var ny = new double[4];
        var off = new double[4];
        for (int k = 0; k < 4; k++)
        {
            (double ax, double ay) = p[k];
            (double bx, double by) = p[(k + 1) % 4];
            double ex = bx - ax, ey = by - ay, len = Math.Sqrt(ex * ex + ey * ey);
            double ux = -ey / len, uy = ex / len;
            if ((cx - ax) * ux + (cy - ay) * uy < 0)
            {
                ux = -ux;
                uy = -uy;
            }

            nx[k] = ux;
            ny[k] = uy;
            off[k] = -(ax * ux + ay * uy);
        }

        int w = Width, h = Height;
        int x0 = Math.Max(0, (int)Math.Floor(p.Min(v => v.X)) - 2), x1 = Math.Min(w - 1, (int)Math.Ceiling(p.Max(v => v.X)) + 2);
        int y0 = Math.Max(0, (int)Math.Floor(p.Min(v => v.Y)) - 2), y1 = Math.Min(h - 1, (int)Math.Ceiling(p.Max(v => v.Y)) + 2);

        Parallel.For(y0, y1 + 1, y =>
        {
            for (int x = x0; x <= x1; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                double dmin = double.MaxValue;
                for (int k = 0; k < 4; k++)
                    dmin = Math.Min(dmin, px * nx[k] + py * ny[k] + off[k]);

                double cov;
                if (dmin >= 0.75)
                {
                    cov = 1;
                }
                else if (dmin <= -0.75)
                {
                    continue;
                }
                else
                {
                    int inside = 0;
                    for (int sy = 0; sy < 4; sy++)
                    {
                        for (int sx = 0; sx < 4; sx++)
                        {
                            double qx = x + (sx + 0.5) / 4, qy = y + (sy + 0.5) / 4;
                            bool ok = true;
                            for (int k = 0; k < 4 && ok; k++)
                                ok = qx * nx[k] + qy * ny[k] + off[k] >= 0;
                            if (ok)
                                inside++;
                        }
                    }

                    cov = inside / 16.0;
                    if (cov == 0)
                        continue;
                }

                int o = (y * w + x) * 3;
                dst[o] = Clamp(dst[o] * (1 - cov) + color.R * cov);
                dst[o + 1] = Clamp(dst[o + 1] * (1 - cov) + color.G * cov);
                dst[o + 2] = Clamp(dst[o + 2] * (1 - cov) + color.B * cov);
            }
        });
    }

    private void DrawRoundedBox(byte[] dst, double cx, double cy, double hx, double hy, double radius, (int R, int G, int B) color)
    {
        int w = Width, h = Height;
        int x0 = Math.Max(0, (int)(cx - hx) - 2), x1 = Math.Min(w - 1, (int)(cx + hx) + 2);
        int y0 = Math.Max(0, (int)(cy - hy) - 2), y1 = Math.Min(h - 1, (int)(cy + hy) + 2);
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                double qx = Math.Abs(x + 0.5 - cx) - (hx - radius), qy = Math.Abs(y + 0.5 - cy) - (hy - radius);
                double sd = Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0)) + Math.Min(Math.Max(qx, qy), 0) - radius;
                double cov = Math.Clamp(0.5 - sd, 0, 1);
                if (cov <= 0)
                    continue;
                int o = (y * w + x) * 3;
                dst[o] = Clamp(dst[o] * (1 - cov) + color.R * cov);
                dst[o + 1] = Clamp(dst[o + 1] * (1 - cov) + color.G * cov);
                dst[o + 2] = Clamp(dst[o + 2] * (1 - cov) + color.B * cov);
            }
        }
    }

    // -------------------------------------------------------------------------------------------
    // Small deterministic helpers (nothing here depends on the process-randomized string hash)
    // -------------------------------------------------------------------------------------------

    private static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    internal static ulong Fnv1a(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in s)
        {
            h ^= c;
            h *= 1099511628211UL;
        }

        return h;
    }

    internal static ulong SplitMix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    private static double Next(ref ulong state)
    {
        state = SplitMix(state);
        return (state >> 11) * (1.0 / (1UL << 53));
    }
}

/// <summary>The scene catalogue (see the ticket): ten scenes, 24 fps / 10 s unless noted.</summary>
public static class JitterScenes
{
    /// <summary>The real staging plate, normalized: TL, TR, BL, BR.</summary>
    private static readonly (double X, double Y)[] BaseNormalized = [(0.2128, 0.1075), (0.7414, 0.1080), (0.2119, 0.6531), (0.7418, 0.6529)];

    private static readonly (byte, byte, byte) Green = (0, 236, 1);

    public static IReadOnlyList<JitterScene> All { get; } = Build();

    public static JitterScene Get(string name) => All.First(s => s.Name == name);

    private static Quad[] Base(int w, int h, double dx = 0) =>
        [Quad.From(0, (BaseNormalized[0].X * w + dx, BaseNormalized[0].Y * h), (BaseNormalized[1].X * w + dx, BaseNormalized[1].Y * h),
            (BaseNormalized[2].X * w + dx, BaseNormalized[2].Y * h), (BaseNormalized[3].X * w + dx, BaseNormalized[3].Y * h))];

    private static double Smoothstep(double t) => t * t * (3 - 2 * t);

    private static (double X, double Y) CentroidOf(Quad q) =>
        ((q.X0 + q.X1 + q.X2 + q.X3) / 4, (q.Y0 + q.Y1 + q.Y2 + q.Y3) / 4);

    private static Quad Transform(Quad q, Func<(double X, double Y), (double X, double Y)> f) =>
        Quad.From(q.T, f(q.Corner(0)), f(q.Corner(1)), f(q.Corner(2)), f(q.Corner(3)));

    private static IReadOnlyList<JitterScene> Build()
    {
        const int W = 1344, H = 768;
        Quad[] baseQuad = Base(W, H);

        // slow-push: 400 px of x travel and 1.0 -> 1.25 growth cannot start from the real plate's
        // position (its right edge would leave the frame), so the start is shifted 197 px left; the
        // end state then fits the frame with 56 px to spare.
        const double PushShift = -197;
        Quad[] PushAt(int f, int frames)
        {
            Quad start = Base(W, H, PushShift)[0];
            double s = Smoothstep(Math.Clamp(f / (double)(frames - 1), 0, 1));
            double scale = 1 + 0.25 * s;
            (double cx, double cy) = CentroidOf(start);
            return [Transform(start, c => (cx + (c.X - cx) * scale + 400 * s, cy + (c.Y - cy) * scale))];
        }

        // turn: a 35 degree turn of the full-size plate would leave the 768 px frame, so the plate is
        // scaled to 0.85 and centred on the frame centre.
        Quad[] TurnAt(int f)
        {
            double angle = 35.0 * Math.PI / 180 * Math.Clamp((f / 24.0 - 2.0) / 1.5, 0, 1);
            (double cx, double cy) = CentroidOf(baseQuad[0]);
            double ca = Math.Cos(angle), sa = Math.Sin(angle);
            return [Transform(baseQuad[0], c =>
            {
                double rx = (c.X - cx) * 0.85, ry = (c.Y - cy) * 0.85;
                return (W / 2.0 + rx * ca - ry * sa, H / 2.0 + rx * sa + ry * ca);
            })];
        }

        // whip: start 250 px left of the real plate so 360 px of travel (12 frames x 30 px) stays in frame.
        Quad[] WhipAt(int f) =>
            [Transform(Base(W, H, -250)[0], c => (c.X + 30 * Math.Clamp(f - 96, 0, 12), c.Y))];

        // static-small: a portrait phone-sized plate, 0.13 of the frame area, turned 8 degrees.
        Quad[] SmallAt()
        {
            double area = 0.13 * W * H, aspect = 0.46;
            double ph = Math.Sqrt(area / aspect), pw = ph * aspect, a = 8 * Math.PI / 180;
            double ca = Math.Cos(a), sa = Math.Sin(a);
            (double X, double Y) P(double x, double y) => (0.55 * W + x * ca - y * sa, 0.5 * H + x * sa + y * ca);
            return [Quad.From(0, P(-pw / 2, -ph / 2), P(pw / 2, -ph / 2), P(-pw / 2, ph / 2), P(pw / 2, ph / 2))];
        }

        // microshake: three sinusoids per axis (0.5-3 Hz, seeded), translation 1.5 px RMS per axis,
        // rotation 0.1 degree RMS about the frame centre; the whole picture (background too) moves.
        Func<int, (double, double, double)> Shake(string seedName)
        {
            ulong s = JitterScene.Fnv1a(seedName);
            double[] Series(ulong tag, double rms)
            {
                var v = new double[6];
                for (int i = 0; i < 3; i++)
                {
                    ulong h1 = JitterScene.SplitMix(s ^ tag ^ (ulong)(i * 2 + 1)), h2 = JitterScene.SplitMix(s ^ tag ^ (ulong)(i * 2 + 2));
                    v[i * 2] = 0.5 + 2.5 * ((h1 >> 11) * (1.0 / (1UL << 53)));
                    v[i * 2 + 1] = 2 * Math.PI * ((h2 >> 11) * (1.0 / (1UL << 53)));
                }

                return [v[0], v[1], v[2], v[3], v[4], v[5], rms * Math.Sqrt(2.0 / 3.0)];
            }

            double[] fx = Series(0x100, 1.5), fy = Series(0x200, 1.5), fr = Series(0x300, 0.1 * Math.PI / 180);
            double Eval(double[] p, double t) =>
                p[6] * (Math.Sin(2 * Math.PI * p[0] * t + p[1]) + Math.Sin(2 * Math.PI * p[2] * t + p[3]) + Math.Sin(2 * Math.PI * p[4] * t + p[5]));
            return f => (Eval(fx, f / 24.0), Eval(fy, f / 24.0), Eval(fr, f / 24.0));
        }

        return
        [
            new JitterScene("static-1344", W, H, 24, 10, Green, _ => baseQuad),
            new JitterScene("static-1080p", 1920, 1080, 30, 10, Green, _ => Base(1920, 1080)),
            new JitterScene("static-small", W, H, 24, 10, (61, 190, 81), _ => SmallAt()),
            new JitterScene("static-flicker", W, H, 24, 10, Green, _ => baseQuad, flicker: true),
            new JitterScene("microshake", W, H, 24, 10, Green, _ => baseQuad, camera: Shake("microshake")),
            new JitterScene("slow-push", W, H, 24, 10, Green, f => PushAt(f, 240)),
            new JitterScene("hold-cadence", W, H, 24, 10, Green, f => PushAt(f - f % 2, 240)),
            new JitterScene("turn", W, H, 24, 10, Green, TurnAt),
            new JitterScene("occlusion", W, H, 24, 10, Green, _ => baseQuad, occluder: true),
            new JitterScene("whip", W, H, 24, 10, Green, WhipAt)
        ];
    }
}
