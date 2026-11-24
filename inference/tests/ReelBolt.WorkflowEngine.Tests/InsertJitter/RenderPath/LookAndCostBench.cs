#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using Xunit.Abstractions;
using static ReelBolt.WorkflowEngine.Tests.InsertJitter.BenchSupport;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.RenderPath;

/// <summary>
/// T-005 visual risk and cost. <b>Look</b>: one frame of detailed content (<c>testsrc2</c>: colour bars, gradients, text, fine lines)
/// pinned into a 700x420 quad with the production recipe and the render candidates, against an IDEAL (the content resized with lanczos to the
/// quad's size and pasted at the quad's integer position). Measures interior sharpness (mean luma gradient, ratio to the ideal's), interior RMSE
/// vs the ideal, and the edge rim (the outermost 0-2 px inside the quad: signed mean luma error vs the ideal, i.e. a dark halo is negative). Half-pixel
/// positions and a 20 degree turn have no pixel-exact ideal, so only sharpness is reported there. <b>Cost</b>: CPU time and peak RSS of the filtergraph alone (to <c>-f null</c>, yuv420p).
/// </summary>
public class LookAndCostBench
{
    private readonly ITestOutputHelper _output;

    public LookAndCostBench(ITestOutputHelper output) => _output = output;

    private static readonly RigVariant[] Variants =
    [
        DecompositionBench.Baseline,
        new("comp", "PROTOTYPE border-compensated pin only", CompensateBorder: true),
        new("prerep", "PROTOTYPE prescale + smeared content", Prescale: true, CompensateBorder: true, ContentEdgeReplicate: true),
        SceneMatrixBench.Variants[3],
        new("reccub", "PROTOTYPE prescale + smeared content + overlay yuv444 + perspective interpolation=cubic", OverlayFormat: "yuv444", Prescale: true, CompensateBorder: true, ContentEdgeReplicate: true, Via420: true, Interpolation: "cubic")
    ];

    private static float[] Luma(byte[] rgb)
    {
        var l = new float[rgb.Length / 3];
        for (int i = 0, o = 0; i < l.Length; i++, o += 3)
            l[i] = (float)(0.2126 * rgb[o] + 0.7152 * rgb[o + 1] + 0.0722 * rgb[o + 2]);
        return l;
    }

    private static double Gradient(float[] l, int w, int x0, int y0, int x1, int y1)
    {
        double sum = 0;
        int n = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                double gx = (l[y * w + x + 1] - l[y * w + x - 1]) / 2.0, gy = (l[(y + 1) * w + x] - l[(y - 1) * w + x]) / 2.0;
                sum += Math.Sqrt(gx * gx + gy * gy);
                n++;
            }
        }

        return sum / n;
    }

    private static byte[] IdealFrame(int w, int h, int x0, int y0, int qw, int qh)
    {
        string graph = $"color=c=0x404040:s={w}x{h}:r=24:d=1[b];testsrc2=s={w}x{h}:r=24:d=1,scale={qw}:{qh}:flags=lanczos[c];[b][c]overlay=x={x0}:y={y0},format=rgb24[o]";
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-v", "error", "-nostdin", "-filter_complex", graph, "-map", "[o]", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" })
            psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException(err.Result);
        return ms.ToArray();
    }

    private static RigInput OneFrame(Quad q)
    {
        var keys = new List<InsertQuadFrame> { new(0, q.X0, q.Y0, q.X1, q.Y1, q.X2, q.Y2, q.X3, q.Y3) };
        return new RigInput(1344, 768, 24, 1, 1, keys, [q], "testsrc2=s=1344x768:r=24:d=3", null, "0x404040");
    }

    [JitterBenchFact]
    public void Look_sharpness_and_rim()
    {
        const int w = 1344, h = 768;
        var sb = new StringBuilder();
        sb.AppendLine("## Visual risk: sharpness and edge rim (testsrc2 content, 700x420 quad)");
        sb.AppendLine();
        sb.AppendLine("Ideal = testsrc2 resized to 700x420 with lanczos and pasted at the quad's position over the same flat base. **Sharp** = mean luma gradient magnitude over the interior (quad shrunk by 8 px), as a ratio to the ideal's (1.00 = same detail; above 1 = aliasing/ringing adds energy, below 1 = softer). **RMSE** = interior luma RMSE vs the ideal (levels of 255; integer-aligned quad only). **Rim** = mean signed luma error (candidate minus ideal) over the outermost 2 px band just inside the quad edge (negative = dark halo) and its mean absolute error. The production recipe pins the 1 px bordered frame, so its content is also ~1 px smaller than the quad (the `comp` row shows that effect removed).");
        sb.AppendLine();

        var cases = new (string Name, Quad Quad, bool Exact)[]
        {
            ("integer-aligned (300,100)-(1000,520)", Quad.From(0, (300, 100), (1000, 100), (300, 520), (1000, 520)), true),
            ("half-pixel offset (300.5,100.5)", Quad.From(0, (300.5, 100.5), (1000.5, 100.5), (300.5, 520.5), (1000.5, 520.5)), false),
            ("quarter-pixel offset + 3% larger (300.25,100.25)-(1021.25,532.85)", Quad.From(0, (300.25, 100.25), (1021.25, 100.25), (300.25, 532.85), (1021.25, 532.85)), false)
        };
        foreach ((string name, Quad quad, bool exact) in cases)
        {
            double qw = quad.X1 - quad.X0, qh = quad.Y2 - quad.Y0;
            byte[] ideal = IdealFrame(w, h, 300, 100, 700, 420);
            float[] idealL = Luma(ideal);
            int ix0 = 308, iy0 = 108, ix1 = 992, iy1 = 512;
            double idealGrad = Gradient(idealL, w, ix0, iy0, ix1, iy1);
            sb.AppendLine($"### {name}");
            sb.AppendLine();
            sb.AppendLine("| variant | Sharp (ratio to ideal) | RMSE | Rim signed mean | Rim MAE |");
            sb.AppendLine("|---|---:|---:|---:|---:|");
            foreach (RigVariant v in Variants)
            {
                byte[] frame = RenderRig.RenderRaw(OneFrame(quad), v)[0];
                float[] l = Luma(frame);
                int jx0 = (int)Math.Ceiling(quad.X0) + 8, jy0 = (int)Math.Ceiling(quad.Y0) + 8, jx1 = (int)Math.Floor(quad.X1) - 8, jy1 = (int)Math.Floor(quad.Y2) - 8;
                double grad = Gradient(l, w, jx0, jy0, jx1, jy1) / idealGrad;
                string rmse = "-", rim = "-", rimAbs = "-";
                if (exact)
                {
                    double se = 0;
                    int n = 0;
                    for (int y = iy0; y < iy1; y++)
                    {
                        for (int x = ix0; x < ix1; x++)
                        {
                            double d = l[y * w + x] - idealL[y * w + x];
                            se += d * d;
                            n++;
                        }
                    }

                    rmse = Math.Sqrt(se / n).ToString("0.00", CultureInfo.InvariantCulture);
                    double sum = 0, abs = 0;
                    int m = 0;
                    for (int k = 0; k < 2; k++)
                    {
                        for (int x = 320; x < 980; x++)
                        {
                            foreach (int y in new[] { 100 + k, 519 - k })
                            {
                                double d = l[y * w + x] - idealL[y * w + x];
                                sum += d;
                                abs += Math.Abs(d);
                                m++;
                            }
                        }

                        for (int y = 120; y < 500; y++)
                        {
                            foreach (int x in new[] { 300 + k, 999 - k })
                            {
                                double d = l[y * w + x] - idealL[y * w + x];
                                sum += d;
                                abs += Math.Abs(d);
                                m++;
                            }
                        }
                    }

                    rim = (sum / m).ToString("0.00", CultureInfo.InvariantCulture);
                    rimAbs = (abs / m).ToString("0.00", CultureInfo.InvariantCulture);
                }

                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {v.Label} | {grad:0.000} | {rmse} | {rim} | {rimAbs} |"));
                _output.WriteLine($"{name} {v.Id}: sharp {grad:0.000} rmse {rmse} rim {rim}");
            }

            sb.AppendLine();
        }

        // 20 degree turn: sharpness only (ratio to the same-size axis-aligned ideal).
        {
            double a = 20 * Math.PI / 180, ca = Math.Cos(a), sa = Math.Sin(a), cx = 650, cy = 310;
            (double X, double Y) R(double x, double y) => (cx + (x - cx) * ca - (y - cy) * sa, cy + (x - cx) * sa + (y - cy) * ca);
            Quad quad = Quad.From(0, R(300, 100), R(1000, 100), R(300, 520), R(1000, 520));
            float[] idealL = Luma(IdealFrame(w, h, 300, 100, 700, 420));
            double idealGrad = Gradient(idealL, w, 308, 108, 992, 512);
            sb.AppendLine("### 20 degree turn about (650,310), 700x420 plate");
            sb.AppendLine();
            sb.AppendLine("| variant | Sharp (ratio to the axis-aligned ideal) |");
            sb.AppendLine("|---|---:|");
            foreach (RigVariant v in Variants)
            {
                float[] l = Luma(RenderRig.RenderRaw(OneFrame(quad), v)[0]);
                // interior: a central axis-aligned box that lies inside the rotated plate (350 x 200).
                double grad = Gradient(l, w, 475, 210, 825, 410) / Gradient(idealL, w, 475, 210, 825, 410);
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {v.Label} | {grad:0.000} |"));
            }

            sb.AppendLine();
        }

        WriteSection("55-look", sb.ToString());
    }

    [JitterBenchFact]
    public void Cost_cpu_and_memory()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Cost: CPU time and peak memory of the insert filtergraph (ffmpeg to -f null, yuv420p)");
        sb.AppendLine();
        sb.AppendLine("CPU = user+sys ms of the ffmpeg process for the whole 10 s clip (median of 3; wall time is meaningless on this loaded host: load average ~60 on 32 cores), peak = VmHWM. Perfect-track keyframes, flat base (no base decode), white content. The baseline matte/reflection/guard stages are not in this graph.");
        sb.AppendLine();
        sb.AppendLine("| scene | variant | frames | CPU ms / clip | CPU ms / frame | peak RSS MB |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|");
        foreach (string name in new[] { "static-1344", "slow-push", "static-1080p" })
        {
            JitterScene scene = JitterScenes.Get(name);
            CompileStage.Result s5 = RenderScenes.Compile(scene, RenderScenes.PerfectTrack(scene));
            RigInput input = RenderScenes.Input(scene, s5, "0xFFFFFF", flatBase: true);
            foreach (RigVariant v in SceneMatrixBench.Variants)
            {
                var cpu = new List<double>();
                double rss = 0;
                for (int rep = 0; rep < 3; rep++)
                {
                    (double c, double r) = RenderRig.Cost(input, v);
                    cpu.Add(c);
                    rss = Math.Max(rss, r);
                }

                cpu.Sort();
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {name} | {v.Id} | {scene.Frames} | {cpu[1]:0} | {cpu[1] / scene.Frames:0.0} | {rss:0} |"));
                _output.WriteLine($"{name} {v.Id}: {cpu[1]:0} ms");
            }
        }

        WriteSection("56-cost", sb.ToString());
    }
}
