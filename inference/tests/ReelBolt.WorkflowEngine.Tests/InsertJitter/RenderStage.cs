#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter;

/// <summary>
/// S6: renders the real insert filter chain (<c>ScreenInsertFilterBuilder.BuildFilterChain</c>) with ffmpeg
/// over the encoded plate clip, the content a solid magenta (255,0,255), and re-detects the magenta quad in
/// the rendered frames at native resolution with <see cref="ChromaEdgeLineFitter"/> (colour "magenta" for the chroma case, a luma field for the white case). This
/// measures what <c>perspective</c>, the mask warp and the overlay add on top of the S5 expressions.
///
/// <para>
/// Simplifications (the closest honest thing): the unmatted flat path only — no plate matte, no reflection
/// transfer, no guard fill, no content prefilter — so the visible magenta region is exactly the warped
/// quad mask and the edge fit sees no plate-colour interaction; overscan 0.02 on a same-size canvas (S5a).
/// The warp's own 1 px black border (the chain pads the content by 1 px before warping) makes the
/// visible quad ~1 px smaller than the corners — a constant offset, visible in ErrMean, not in Slip.
/// </para>
/// </summary>
internal static class RenderStage
{
    public sealed record Result(List<Quad> Rendered, int Frames, int Failed);

    /// <summary>
    /// The insert's content and how its edge is read back. The final picture is yuv420p, so a CHROMA-only contrast
    /// (magenta over green/grey) is resolved at half resolution (2 px steps), while a LUMA contrast (white) is
    /// resolved at full resolution — the two bracket what a viewer's content can show at the edge.
    /// </summary>
    public sealed record InsertContent(string Label, string Lavfi, Func<byte, byte, byte, float> Field, double Level);

    public static readonly InsertContent Magenta = new(
        "magenta", "0xFF00FF", (r, g, b) => ChromaEdgeLineFitter.Dominance("magenta", r, g, b), 255);

    /// <summary>White over a textured background no brighter than luma 170: field = luma above 170, edge at luma 212.</summary>
    public static readonly InsertContent White = new(
        "white", "0xFFFFFF", (r, g, b) => (float)Math.Clamp(0.2126 * r + 0.7152 * g + 0.0722 * b - 170, 0, 85), 85);

    public static Result Run(string clipPath, int w, int h, CompileStage.Result s5, int totalFrames, InsertContent content)
    {
        var insert = new ResolvedScreenInsert(
            "r0", "magenta", s5.OutputStartSec, s5.OutputEndSec, s5.Keyframes, s5.FpsNum, s5.FpsDen);
        string graph = "[0:v]null[b0];" + ScreenInsertFilterBuilder.BuildFilterChain("[b0]", [insert], w, h, i => i + 1, "[vc]") + ";[vc]format=rgb24[vout]";

        string tmp = Path.Combine(Path.GetTempPath(), "insert-jitter-s6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string script = Path.Combine(tmp, "graph.txt");
            File.WriteAllText(script, graph);
            double fps = s5.FpsNum / (double)s5.FpsDen;
            var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in new[]
                     {
                         "-v", "error", "-i", clipPath,
                         "-f", "lavfi", "-i", string.Create(CultureInfo.InvariantCulture, $"color=c={content.Lavfi}:s={w}x{h}:r={s5.FpsNum}/{s5.FpsDen}:d={totalFrames / fps + 2}"),
                         "-filter_complex_script", script, "-map", "[vout]", "-frames:v", totalFrames.ToString(CultureInfo.InvariantCulture),
                         "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"
                     })
                psi.ArgumentList.Add(a);

            using Process p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            var frame = new byte[w * h * 3];
            var dom = new float[w * h];
            var rendered = new List<Quad>();
            int failed = 0, seen = 0;
            long startFrame = (long)Math.Round(s5.OutputStartSec * fps);
            Stream stdout = p.StandardOutput.BaseStream;
            int g = -1;
            while (ReadFull(stdout, frame))
            {
                g++;
                long i = g - startFrame;
                if (i < 0 || i >= s5.CanvasPx.Count)
                    continue;
                seen++;
                for (int q = 0, o = 0; q < dom.Length; q++, o += 3)
                    dom[q] = content.Field(frame[o], frame[o + 1], frame[o + 2]);

                Quad seed = s5.CanvasPx[(int)i];
                (double X, double Y) S((double X, double Y) c) => (c.X - 0.5, c.Y - 0.5);
                ChromaEdgeLineFitter.Result? fit = ChromaEdgeLineFitter.Fit(
                    dom, w, h, content.Level, S(seed.Corner(0)), S(seed.Corner(1)), S(seed.Corner(3)), S(seed.Corner(2)));
                if (fit is null)
                {
                    failed++;
                    continue;
                }

                (double X, double Y)[] c4 = fit.Value.Corners; // TL, TR, BR, BL in pixel-centre coordinates
                (double X, double Y) C((double X, double Y) c) => (c.X + 0.5, c.Y + 0.5);
                rendered.Add(Quad.From(g / fps, C(c4[0]), C(c4[1]), C(c4[3]), C(c4[2])));
            }

            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new InvalidOperationException("S6 ffmpeg failed: " + err.Result + "\n" + graph);
            return new Result(rendered, seen, failed);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    /// <summary>
    /// Detector control: the scene's own analytic anti-aliased plate drawn in the insert's colour (no encode, no noise, no ffmpeg
    /// filter), re-detected with the same magenta edge fit seeded from the truth. Its slip is the re-detection's own floor.
    /// </summary>
    public static List<Quad> DetectorControl(JitterScene scene, InsertContent content, (byte R, byte G, byte B) rgb)
    {
        int w = scene.Width, h = scene.Height;
        byte[] texture = scene.Texture();
        var frame = new byte[w * h * 3];
        var dom = new float[w * h];
        var result = new List<Quad>();
        for (int f = 0; f < scene.Frames; f++)
        {
            scene.RenderControlFrame(f, texture, frame, rgb);
            for (int q = 0, o = 0; q < dom.Length; q++, o += 3)
                dom[q] = content.Field(frame[o], frame[o + 1], frame[o + 2]);
            Quad seed = scene.TruthAt(f);
            (double X, double Y) S((double X, double Y) c) => (c.X - 0.5, c.Y - 0.5);
            ChromaEdgeLineFitter.Result? fit = ChromaEdgeLineFitter.Fit(
                dom, w, h, content.Level, S(seed.Corner(0)), S(seed.Corner(1)), S(seed.Corner(3)), S(seed.Corner(2)));
            if (fit is null)
                continue;
            (double X, double Y)[] c4 = fit.Value.Corners;
            (double X, double Y) C((double X, double Y) c) => (c.X + 0.5, c.Y + 0.5);
            result.Add(Quad.From(f / scene.Fps, C(c4[0]), C(c4[1]), C(c4[3]), C(c4[2])));
        }

        return result;
    }

    private static bool ReadFull(Stream s, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer, read, buffer.Length - read);
            if (n <= 0)
                return false;
            read += n;
        }

        return true;
    }
}
