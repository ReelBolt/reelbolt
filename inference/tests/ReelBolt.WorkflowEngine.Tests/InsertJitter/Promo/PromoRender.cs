#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Tests.InsertJitter.RenderPath;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo;

/// <summary>
/// The content layer of the compile, re-rendered with the production chain pieces (<c>scale=1920:1080 bilinear, fps, format=yuv444p, perspective
/// sense=destination eval=frame</c> built by <see cref="ScreenInsertFilterBuilder.BuildPerspectiveFilter"/>) from any set of plate quads, optionally through
/// the film's own x264 settings (crf 20, veryfast, yuv420p). Matte, reflection transfer and overlay are left out: they do not move the card, so
/// the card edges measured here are what the content layer contributes to the film.
/// </summary>
internal static class PromoRender
{
    public enum Content
    {
        /// <summary>The 960x540 proxy upscaled x2 (production scales the 1920x1080 original by 1: the proxy is softer than the real content).</summary>
        Proxy,

        /// <summary>An analytic 1920x1080 card with anti-aliased edges at the measured content position and the proxy's margin/card luma: the sharpness of the real content.</summary>
        Crisp
    }

    public sealed record Levels(double Margin, double Card);

    /// <summary>Median luma of the proxy's margin ring and of the card interior (tv-range numbers as decoded).</summary>
    public static Levels MeasureLevels(PromoInputs inp, CardRect card)
    {
        const int w = 960, h = 540;
        foreach ((int index, float[] luma) in PromoVideo.Luma(inp.ProxyPath, w, h, 1))
        {
            var margin = new List<float>();
            var interior = new List<float>();
            for (int y = 0; y < h; y += 2)
            {
                for (int x = 0; x < w; x += 2)
                {
                    double u = (x + 0.5) / w, v = (y + 0.5) / h;
                    bool inCard = u > card.U0 + 0.01 && u < card.U1 - 0.01 && v > card.V0 + 0.01 && v < card.V1 - 0.01;
                    bool inMargin = u < card.U0 - 0.01 || u > card.U1 + 0.01 || v < card.V0 - 0.01 || v > card.V1 + 0.01;
                    if (inMargin)
                        margin.Add(luma[y * w + x]);
                    else if (inCard)
                        interior.Add(luma[y * w + x]);
                }
            }

            margin.Sort();
            interior.Sort();
            return new Levels(margin[margin.Count / 2], interior[interior.Count / 2]);
        }

        throw new InvalidOperationException("no proxy frame");
    }

    private static string CrispFile(PromoInputs inp, CardRect card, Levels lv)
    {
        string path = Path.Combine(inp.WorkDir, "crisp-card.yuv");
        const int w = 1920, h = 1080;
        var cov = new float[w * h];
        EdgeMeter.Coverage(cov, w, h, Quad.From(0, (card.U0 * w, card.V0 * h), (card.U1 * w, card.V0 * h), (card.U0 * w, card.V1 * h), (card.U1 * w, card.V1 * h)));
        var y = new byte[w * h];
        for (int i = 0; i < y.Length; i++)
            y[i] = (byte)Math.Clamp(Math.Round(lv.Margin + (lv.Card - lv.Margin) * cov[i]), 0, 255);
        using var fs = File.Create(path);
        fs.Write(y);
        var c = new byte[w * h * 2];
        Array.Fill(c, (byte)128);
        fs.Write(c);
        return path;
    }

    private static List<string> InputArgs(PromoInputs inp, Content content, CardRect card, Levels lv, out string chainStart)
    {
        if (content == Content.Proxy)
        {
            chainStart = "[0:v]scale=1920:1080:flags=bilinear,fps=24,format=yuv444p";
            return ["-i", inp.ProxyPath];
        }

        string yuv = CrispFile(inp, card, lv);
        chainStart = "[0:v]loop=loop=-1:size=1:start=0,fps=24,format=yuv444p";
        return ["-f", "rawvideo", "-pix_fmt", "yuv444p", "-video_size", "1920x1080", "-framerate", "24", "-i", yuv];
    }

    /// <summary>
    /// Renders the content layer warped onto <paramref name="plateOut"/> (canvas px, one quad per frame; frame i uses <c>perspective</c>'s <c>in = i + 1</c> exactly
    /// like the compile) and returns the path of the decoded-luma source: the lossless path when <paramref name="codec"/> is false is realised as a yuv444p
    /// intermediate (the same luma the filter produced), otherwise the x264 file.
    /// </summary>
    public static string Render(PromoInputs inp, IReadOnlyList<Quad> plateOut, Content content, CardRect card, Levels lv, bool codec, string tag)
    {
        var keys = plateOut.Select((q, i) => new InsertQuadFrame(i, q.X0, q.Y0, q.X1, q.Y1, q.X2, q.Y2, q.X3, q.Y3)).ToList();
        string persp = ScreenInsertFilterBuilder.BuildPerspectiveFilter(keys);
        string dir = inp.WorkDir;
        string graph = Path.Combine(dir, $"graph-{tag}.txt");
        List<string> inputs = InputArgs(inp, content, card, lv, out string start);
        File.WriteAllText(graph, $"{start},{persp}[vout]");
        string output = Path.Combine(dir, codec ? $"layer-{tag}.mp4" : $"layer-{tag}.nut");
        var args = new List<string> { "-v", "error", "-nostdin", "-y" };
        args.AddRange(inputs);
        args.AddRange(["-filter_complex_script", graph, "-map", "[vout]", "-frames:v", plateOut.Count.ToString(CultureInfo.InvariantCulture)]);
        if (codec)
            args.AddRange(["-c:v", "libx264", "-crf", "20", "-preset", "veryfast", "-pix_fmt", "yuv420p", output]);
        else
            args.AddRange(["-c:v", "ffv1", "-pix_fmt", "yuv444p", output]);
        PromoVideo.Run(args);
        return output;
    }

    // ------------------------------------------------------------------------------------------- synthetic floor

    public sealed record SyntheticSpec(string Id, string Label, double Sigma, double Noise);

    public static readonly SyntheticSpec[] SyntheticSpecs =
    [
        new("nominal", "blur sigma 1.2 px, noise 1.0 level", 1.2, 1.0),
        new("harsh", "blur sigma 1.8 px, noise 2.5 levels", 1.8, 2.5)
    ];

    /// <summary>
    /// Analytic anti-aliased sequence at the film's geometry with KNOWN quads: dark bezel, lighter screen margin, a card of the measured content position, optics
    /// blur, noise. Yields each frame's luma (the lossless reference, quantised to 8 bit) and, while it is enumerated, pipes the same frames as yuv420p through the
    /// film's x264 settings (crf 20, veryfast) into <paramref name="mp4"/>; the encode finishes when the enumeration does. Nothing raw is stored.
    /// <paramref name="screenTruth"/> are canvas-px quads; the card is the content card pushed through each frame's screen quad.
    /// </summary>
    public static IEnumerable<(int Index, float[] Luma)> SyntheticFrames(PromoInputs inp, IReadOnlyList<Quad> screenTruth, CardRect card, Levels lv, SyntheticSpec spec, double bezelLevel, double screenLevel, string mp4)
    {
        const int w = PromoInputs.OutW, h = PromoInputs.OutH;
        double cardLevel = screenLevel + (lv.Card - lv.Margin);
        var rng = new Random(20261013);
        var covS = new float[w * h];
        var covC = new float[w * h];
        var field = new float[w * h];
        var tmp = new float[w * h];
        var yb = new byte[w * h];
        var chroma = new byte[w * h / 2];
        Array.Fill(chroma, (byte)128);
        float[] kernel = GaussKernel(spec.Sigma);
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in new[] { "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "yuv420p", "-video_size", "1920x1080", "-framerate", "24", "-i", "pipe:0", "-c:v", "libx264", "-crf", "20", "-preset", "veryfast", "-pix_fmt", "yuv420p", mp4 })
            psi.ArgumentList.Add(a);
        using Process enc = Process.Start(psi)!;
        System.Threading.Tasks.Task<string> err = enc.StandardError.ReadToEndAsync();
        _ = enc.StandardOutput.ReadToEndAsync();
        Stream stdin = enc.StandardInput.BaseStream;
        for (int f = 0; f < screenTruth.Count; f++)
        {
            Quad s = screenTruth[f];
            Quad c = PromoMeasure.PredictCard(s, card);
            EdgeMeter.Coverage(covS, w, h, s);
            EdgeMeter.Coverage(covC, w, h, c);
            for (int i = 0; i < field.Length; i++)
                field[i] = (float)(bezelLevel + (screenLevel - bezelLevel) * covS[i] + (cardLevel - screenLevel) * covC[i] * covS[i]);
            Blur(field, tmp, w, h, kernel);
            for (int i = 0; i < yb.Length; i++)
            {
                double g = Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
                yb[i] = (byte)Math.Clamp(Math.Round(field[i] + spec.Noise * g), 0, 255);
                field[i] = yb[i];
            }

            stdin.Write(yb);
            stdin.Write(chroma);
            yield return (f, field);
        }

        stdin.Close();
        enc.WaitForExit();
        if (enc.ExitCode != 0)
            throw new InvalidOperationException("synthetic encode failed: " + err.Result);
    }

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
