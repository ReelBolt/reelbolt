#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using ReelBolt.WorkflowEngine.Tests.InsertJitter.RenderPath;
using ReelBolt.WorkflowEngine.Tests.InsertJitter.Smoothing;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo.Plates;

/// <summary>One plate measured by the pseudo-truth meter: per frame the fitted green-plate quad (null where an edge is not clean or leaves the frame) and its four edges, in plate source px.</summary>
internal sealed record PlateMeasurement(int N, Quad?[] Quad, PromoEdge[][] Edges)
{
    public int Valid => Quad.Count(q => q is not null);
}

/// <summary>
/// The pseudo-truth of the raw plates, independent of the tracker: the plate's own green edges measured per frame by <see cref="PromoEdgeFit"/> (the T-007 sub-pixel
/// integrating meter: area-conserving profile integral along each edge normal, two passes re-seeded on the first line, robust parabola-checked fit, straight chord
/// line) on the dominance field <c>max(0, g - max(r, b))</c> of a full-chroma decode at the plate's native resolution. The seeds are FIXED (the staging artifact's
/// keyframes), never the output of the code under test, so the same label-independent measurement scores every run; the two-pass re-seed removes their error (reach 9 px, then 6).
/// </summary>
internal static class PlatesMeter
{
    /// <summary>Chroma 4:2:0 to 4:4:4 at the plate's own size (bicubic, accurate rounding, full chroma interpolation), then to RGB: no chroma smear beyond the codec's own.</summary>
    public const string DecodeFilter = "scale=iw:ih:flags=bicubic+accurate_rnd+full_chroma_inp,format=yuv444p,scale=iw:ih:flags=bicubic+accurate_rnd+full_chroma_int,format=rgb24";

    /// <summary>Dominance span (field levels) below which an edge is not an edge: the plates measure ~100 to 235.</summary>
    public static readonly PromoEdgeOptions Options = new(Reach1: 9, Reach2: 6, MinSpan: 20, MaxResidualRms: 0.6, MinInlier: 0.85);

    public static PlateMeasurement Measure(string clip, IReadOnlyList<Quad> seeds, int w, int h, PromoEdgeOptions? options = null)
    {
        PromoEdgeOptions o = options ?? Options;
        int n = seeds.Count;
        var quads = new Quad?[n];
        var edges = new PromoEdge[n][];
        var field = new float[w * h];
        foreach ((int index, byte[] rgb) in DecodeRgb(clip, w, h, n))
        {
            Dominance(rgb, field);
            PromoQuadFit fit = PromoEdgeFit.FitQuad(field, w, h, seeds[index], o);
            quads[index] = fit.Quad;
            edges[index] = fit.Edges;
        }

        return new PlateMeasurement(n, quads, edges);
    }

    /// <summary>Green dominance, clamped at zero (as the production tracker's own per-pixel signal).</summary>
    public static void Dominance(byte[] rgb, float[] dst)
    {
        for (int p = 0; p < dst.Length; p++)
        {
            int r = rgb[p * 3], g = rgb[p * 3 + 1], b = rgb[p * 3 + 2];
            dst[p] = Math.Max(0, g - Math.Max(r, b));
        }
    }

    /// <summary>Streams the clip as RGB24 frames at its native size through <see cref="DecodeFilter"/>; the buffer is reused, consume it before asking for the next.</summary>
    public static IEnumerable<(int Index, byte[] Rgb)> DecodeRgb(string clip, int w, int h, int maxFrames)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-v", "error", "-nostdin", "-i", clip, "-an", "-vf", DecodeFilter, "-frames:v", maxFrames.ToString(CultureInfo.InvariantCulture), "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" })
            psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        System.Threading.Tasks.Task<string> err = p.StandardError.ReadToEndAsync();
        var buf = new byte[w * h * 3];
        int index = 0;
        while (ReadFull(p.StandardOutput.BaseStream, buf))
            yield return (index++, buf);
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException("ffmpeg plate decode failed: " + err.Result);
    }

    private static bool ReadFull(Stream s, byte[] buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = s.Read(buf, got, buf.Length - got);
            if (n <= 0)
                return false;
            got += n;
        }

        return true;
    }

    // -------------------------------------------------------------------------------------------------- the meter's own floor

    public sealed record SyntheticSpec(string Id, string Label, double Sigma, double Noise, int Crf, (int R, int G, int B) Plate);

    public static readonly (int R, int G, int B) Bezel = (22, 22, 24);

    /// <summary>
    /// A synthetic plate with KNOWN quads at the plate's geometry (1344x768, 24 fps), through x264 4:2:0 like the real one: dark bezel, a flat anti-aliased plate of the
    /// plate's own green, optics blur, noise, then <c>libx264 -crf N -preset medium -pix_fmt yuv420p</c>. <paramref name="truth"/> are plate-px quads (a heavily smoothed
    /// version of the staging keyframes). Frames are piped, nothing raw is stored.
    /// </summary>
    public static void RenderSynthetic(IReadOnlyList<Quad> truth, int w, int h, SyntheticSpec spec, string mp4)
    {
        var cov = new float[w * h];
        var plane = new float[3][];
        for (int c = 0; c < 3; c++)
            plane[c] = new float[w * h];
        var tmp = new float[w * h];
        var rgb = new byte[w * h * 3];
        float[] kernel = GaussKernel(spec.Sigma);
        var rng = new Random(20261013 + spec.Crf);
        int[] bez = [Bezel.R, Bezel.G, Bezel.B], plt = [spec.Plate.R, spec.Plate.G, spec.Plate.B];
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in new[]
                 {
                     "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-video_size", string.Create(CultureInfo.InvariantCulture, $"{w}x{h}"), "-framerate", "24", "-i", "pipe:0",
                     "-c:v", "libx264", "-crf", spec.Crf.ToString(CultureInfo.InvariantCulture), "-preset", "medium", "-pix_fmt", "yuv420p", mp4
                 })
            psi.ArgumentList.Add(a);
        using Process enc = Process.Start(psi)!;
        System.Threading.Tasks.Task<string> err = enc.StandardError.ReadToEndAsync();
        _ = enc.StandardOutput.ReadToEndAsync();
        Stream stdin = enc.StandardInput.BaseStream;
        foreach (Quad q in truth)
        {
            EdgeMeter.Coverage(cov, w, h, q);
            for (int c = 0; c < 3; c++)
            {
                for (int i = 0; i < cov.Length; i++)
                    plane[c][i] = bez[c] + (plt[c] - bez[c]) * cov[i];
                Blur(plane[c], tmp, w, h, kernel);
            }

            for (int i = 0; i < cov.Length; i++)
            {
                for (int c = 0; c < 3; c++)
                {
                    double g = Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
                    rgb[i * 3 + c] = (byte)Math.Clamp(Math.Round(plane[c][i] + spec.Noise * g), 0, 255);
                }
            }

            stdin.Write(rgb);
        }

        stdin.Close();
        enc.WaitForExit();
        if (enc.ExitCode != 0)
            throw new InvalidOperationException("synthetic plate encode failed: " + err.Result);
    }

    /// <summary>The heavy-smoothing oracle of T-007 (fixed 25-frame local quadratic): what "no frame-rate wobble at all" looks like, used as the synthetic truth.</summary>
    public static IReadOnlyList<Quad> SmoothTruth(IReadOnlyList<Quad> keyframes) =>
        new LocalPolySmoother(new LocalPolyOptions(2, [12], double.PositiveInfinity, 2, true, 0)).Smooth(keyframes, PromoInputs.Fps, 0);

    private static float[] GaussKernel(double sigma)
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

    private static void Blur(float[] f, float[] tmp, int w, int h, float[] k)
    {
        int r = k.Length / 2;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int i = -r; i <= r; i++)
                    s += f[y * w + Math.Clamp(x + i, 0, w - 1)] * k[i + r];
                tmp[y * w + x] = s;
            }
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int i = -r; i <= r; i++)
                    s += tmp[Math.Clamp(y + i, 0, h - 1) * w + x] * k[i + r];
                f[y * w + x] = s;
            }
        }
    }
}
