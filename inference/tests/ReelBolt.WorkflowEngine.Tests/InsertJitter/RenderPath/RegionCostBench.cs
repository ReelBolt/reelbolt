#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using Xunit.Abstractions;
using static ReelBolt.WorkflowEngine.Tests.InsertJitter.BenchSupport;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.RenderPath;

/// <summary>
/// T-010b. Does warping only the union bounding region of the insert's quads make cubic content sampling affordable
/// (AC5: compile insert filtergraph CPU at most +10 %)? Four variants of the SAME insert, interleaved run by run so machine-load
/// drift hits all of them alike: <b>base</b> (the pre-F3 full-canvas bilinear chain), <b>t010</b> (F3 as first shipped: prescale + smeared content +
/// cubic, but <c>perspective</c> over the full canvas), <b>rcub</b> (the shipped chain: the same, confined to the bounding region) and
/// <b>rbil</b> (the shipped chain with the content kernel back to bilinear). Cases: the synthetic slow-push scene at the staging geometry
/// and Film A's own geometry (the staging plate artifact, 1344x768 plate covered onto a 1920x1080 canvas, push-in to near full frame).
/// CPU = user+sys of the ffmpeg child (<c>-benchmark</c>), median of the interleaved repetitions; wall time is meaningless on a loaded host.
/// </summary>
public class RegionCostBench
{
    private readonly ITestOutputHelper _output;

    public RegionCostBench(ITestOutputHelper output) => _output = output;

    internal static readonly RigVariant Base = new("base", "pre-F3 full-canvas bilinear chain");

    /// <summary>F3 as first shipped (T-010): the warp window is the whole <paramref name="w"/> x <paramref name="h"/> canvas.</summary>
    internal static RigVariant T010(int w, int h) => new("t010", "F3 prescale + smear + cubic, perspective over the full canvas", Production: true,
        Adjust: insert =>
        {
            (int mw, int mh) = ScreenInsertFilterBuilder.PrescaleSize(insert.Keyframes, w, h);
            (int cw, int ch) = insert.ContentKeyframes is { Count: > 0 } plate ? ScreenInsertFilterBuilder.PrescaleSize(plate, w, h) : (mw, mh);
            return insert with { Prescale = new InsertPrescale(mw, mh, cw, ch, new InsertBounds(0, 0, w, h)) };
        });

    internal static readonly RigVariant RegionCubic = new("rcub", "shipped: perspective confined to the bounding region, content cubic", Production: true);

    internal static readonly RigVariant RegionBilinear = new("rbil", "bounding region, content bilinear", Production: true,
        Rewrite: g => g.Replace(ScreenInsertFilterBuilder.ContentInterpolation, string.Empty, StringComparison.Ordinal));

    internal static RigVariant[] Variants(int w, int h) => [Base, T010(w, h), RegionCubic, RegionBilinear];

    private static double Load1() => double.Parse(File.ReadAllText("/proc/loadavg").Split(' ')[0], CultureInfo.InvariantCulture);

    private static int Reps => int.TryParse(Environment.GetEnvironmentVariable("REELBOLT_REGION_REPS"), out int n) && n > 0 ? n : 5;

    /// <summary>Film A: the staging plate artifact's tracked quads, covered onto 1920x1080 and expanded by the compile step's 2 % overscan about the centroid; content pinned to the plate itself (a matted insert's smeared overscan).</summary>
    internal static RigInput FilmA(out IReadOnlyList<InsertQuadFrame> plateKeys)
    {
        const string path = "/tmp/insert-tracking/promo/artifact-d0fd129e-03c8-4200-a051-f83dd3484186.json";
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement r = doc.RootElement.GetProperty("insertRegions")[0];
        const double sw = 1344, sh = 768, cw = 1920, ch = 1080;
        double s = Math.Max(cw / sw, ch / sh), ox = (sw * s - cw) / 2, oy = (sh * s - ch) / 2;
        var plate = new List<InsertQuadFrame>();
        var warp = new List<InsertQuadFrame>();
        long f = 0;
        foreach (JsonElement k in r.GetProperty("keyframes").EnumerateArray())
        {
            double[] x = new double[4], y = new double[4];
            JsonElement c = k.TryGetProperty("surfaceCorners", out JsonElement sc) && sc.ValueKind == JsonValueKind.Object ? sc : k;
            for (int i = 0; i < 4; i++)
            {
                x[i] = c.GetProperty("x" + i).GetDouble() * sw * s - ox;
                y[i] = c.GetProperty("y" + i).GetDouble() * sh * s - oy;
            }

            plate.Add(new InsertQuadFrame(f, x[0], y[0], x[1], y[1], x[2], y[2], x[3], y[3]));
            double mx = x.Average(), my = y.Average(), g = 1.02;
            double E(double v, double m) => m + (v - m) * g;
            warp.Add(new InsertQuadFrame(f, E(x[0], mx), E(y[0], my), E(x[1], mx), E(y[1], my), E(x[2], mx), E(y[2], my), E(x[3], mx), E(y[3], my)));
            f++;
        }

        plateKeys = plate;
        var commanded = warp.Select(q => new Quad(q.FrameIndex / 24.0, q.X0, q.Y0, q.X1, q.Y1, q.X2, q.Y2, q.X3, q.Y3)).ToList();
        return new RigInput((int)cw, (int)ch, 24, 1, warp.Count, warp, commanded, "0xFFFFFF", null, "0x404040");
    }

    private static RigVariant WithPlate(RigVariant v, IReadOnlyList<InsertQuadFrame> plate)
    {
        Func<ResolvedScreenInsert, ResolvedScreenInsert>? inner = v.Adjust;
        return v with { Adjust = insert => (inner is null ? insert : inner(insert)) with { ContentKeyframes = plate } };
    }

    private static RigInput SlowPush()
    {
        JitterScene scene = JitterScenes.Get("slow-push");
        CompileStage.Result s5 = RenderScenes.Compile(scene, RenderScenes.PerfectTrack(scene));
        return RenderScenes.Input(scene, s5, "0xFFFFFF", flatBase: true);
    }

    [JitterBenchFact]
    public void Cpu_interleaved_on_slow_push_and_film_a()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## T-010b: CPU of the insert filtergraph, bounding-region warp");
        sb.AppendLine();
        sb.AppendLine($"Interleaved repetitions (variant order rotated each round), n = {Reps}. CPU = user+sys ms of ffmpeg `-benchmark` over the whole clip to `-f null` (yuv420p, flat base, no matte), median and min..max. Host load average (1 min) read before each round.");
        sb.AppendLine();

        var cases = new List<(string Name, RigInput Input, IReadOnlyList<InsertQuadFrame>? Plate)>();
        cases.Add(("slow-push 10 s 1344x768 (staging geometry)", SlowPush(), null));
        RigInput filmA = FilmA(out IReadOnlyList<InsertQuadFrame> plate);
        cases.Add(("Film A 15 s 1920x1080 (cover crop of 1344x768, push-in)", filmA, plate));

        foreach ((string name, RigInput input, IReadOnlyList<InsertQuadFrame>? plateKeys) in cases)
        {
            RigVariant[] variants = Variants(input.W, input.H).Select(v => plateKeys is null || !v.Production ? v : WithPlate(v, plateKeys)).ToArray();
            var cpu = variants.ToDictionary(v => v.Id, _ => new List<double>());
            var rss = variants.ToDictionary(v => v.Id, _ => 0.0);
            var loads = new List<double>();
            for (int rep = 0; rep < Reps; rep++)
            {
                loads.Add(Load1());
                for (int k = 0; k < variants.Length; k++)
                {
                    RigVariant v = variants[(k + rep) % variants.Length];
                    (double c, double m) = RenderRig.Cost(input, v);
                    cpu[v.Id].Add(c);
                    rss[v.Id] = Math.Max(rss[v.Id], m);
                    _output.WriteLine($"{name} rep {rep} {v.Id}: {c:0} ms");
                }
            }

            double Median(List<double> l)
            {
                var s = l.OrderBy(x => x).ToList();
                return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
            }

            double baseMed = Median(cpu["base"]);
            sb.AppendLine($"### {name}");
            sb.AppendLine();
            sb.AppendLine($"{input.Frames} frames; load average at the start of each round: {string.Join(", ", loads.Select(l => l.ToString("0.0", CultureInfo.InvariantCulture)))}.");
            sb.AppendLine();
            sb.AppendLine("| variant | CPU ms median | min..max | vs base | peak RSS MB |");
            sb.AppendLine("|---|---:|---:|---:|---:|");
            foreach (RigVariant v in variants)
            {
                double med = Median(cpu[v.Id]);
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {v.Id} ({v.Label}) | {med:0} | {cpu[v.Id].Min():0}..{cpu[v.Id].Max():0} | {(med / baseMed - 1) * 100:+0.0;-0.0} % | {rss[v.Id]:0} |"));
            }

            sb.AppendLine();
            string graph = RenderRig.BuildGraph(input, variants.First(v => v.Id == "rcub"), out _);
            _output.WriteLine(name + " rcub graph: " + graph[..Math.Min(900, graph.Length)]);
        }

        WriteSection("57-region-cost", sb.ToString());
        _output.WriteLine(sb.ToString());
    }

    // ------------------------------------------------------------------------- render metrics

    private static (double Slip, double ErrMean, int Failed) Slip(string scene, int frames, RigVariant variant)
    {
        JitterScene s = JitterScenes.Get(scene);
        CompileStage.Result compiled = RenderScenes.Compile(s, RenderScenes.PerfectTrack(s));
        RigInput full = RenderScenes.Input(s, compiled, "0xFFFFFF", flatBase: true);
        RigInput input = full with { Frames = frames, Keyframes = full.Keyframes.Take(frames).ToList(), Commanded = full.Commanded.Take(frames).ToList() };
        RigResult r = RenderRig.Run(input, variant, RenderStage.White, runProd: false, runMeter: true);
        JitterStats st = RenderScenes.Score(r.Meter, compiled.IdealSourcePx.Take(frames).ToList(), s.Fps);
        return (st.SlipRms, st.ErrMean, r.MeterFailed);
    }

    private static byte[] Ideal(int w, int h)
    {
        string graph = $"color=c=0x404040:s={w}x{h}:r=24:d=1[b];testsrc2=s={w}x{h}:r=24:d=1,scale=700:420:flags=lanczos[c];[b][c]overlay=x=300:y=100,format=rgb24[o]";
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-v", "error", "-nostdin", "-filter_complex", graph, "-map", "[o]", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" })
            psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        return p.ExitCode == 0 ? ms.ToArray() : throw new InvalidOperationException(err.Result);
    }

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

    private static double Rim(float[] l, float[] ideal, int w)
    {
        double sum = 0;
        int m = 0;
        for (int k = 0; k < 2; k++)
        {
            for (int x = 320; x < 980; x++)
            {
                foreach (int y in new[] { 100 + k, 519 - k })
                {
                    sum += l[y * w + x] - ideal[y * w + x];
                    m++;
                }
            }

            for (int y = 120; y < 500; y++)
            {
                foreach (int x in new[] { 300 + k, 999 - k })
                {
                    sum += l[y * w + x] - ideal[y * w + x];
                    m++;
                }
            }
        }

        return sum / m;
    }

    [JitterBenchFact]
    public void Render_metrics_by_variant()
    {
        const int w = 1344, h = 768;
        var sb = new StringBuilder();
        sb.AppendLine("## T-010b: render metrics (perfect track, flat base, integrating meter, white content)");
        sb.AppendLine();
        sb.AppendLine("SlipRms in source px (1344x768 canvas); ErrMean = constant inset (mean signed corner error); rim = signed mean luma error over the outer 2 px of a 700x420 testsrc2 insert vs a lanczos ideal; sharpness = interior mean luma gradient as a ratio to the ideal's, with the quad at x/y offset 0, 0.25 and 0.5 px.");
        sb.AppendLine();
        sb.AppendLine("| variant | slow-push 240 | microshake 120 | hold-cadence 240 | turn 240 | static 24 | inset (slow-push ErrMean) | rim | sharp @0 | sharp @0.25 | sharp @0.5 |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        float[] ideal = Luma(Ideal(w, h));
        double idealGradient = Gradient(ideal, w, 308, 108, 992, 512);
        foreach (RigVariant v in Variants(w, h))
        {
            (double sp, double inset, int f1) = Slip("slow-push", 240, v);
            (double ms, _, int f2) = Slip("microshake", 120, v);
            (double hc, _, int f3) = Slip("hold-cadence", 240, v);
            (double tn, _, int f4) = Slip("turn", 240, v);
            (double st, _, int f5) = Slip("static-1344", 24, v);
            string Render(double off, out float[] luma)
            {
                var k = new InsertQuadFrame(0, 300 + off, 100 + off, 1000 + off, 100 + off, 300 + off, 520 + off, 1000 + off, 520 + off);
                var quad = new Quad(0, k.X0, k.Y0, k.X1, k.Y1, k.X2, k.Y2, k.X3, k.Y3);
                var input = new RigInput(w, h, 24, 1, 1, [k], [quad], "testsrc2=s=1344x768:r=24:d=3", null, "0x404040");
                luma = Luma(RenderRig.RenderRaw(input, v)[0]);
                return string.Empty;
            }

            Render(0, out float[] l0);
            Render(0.25, out float[] l25);
            Render(0.5, out float[] l50);
            double rim = Rim(l0, ideal, w);
            // The interior box is the ideal's own (308..992) for the aligned quad and one pixel inside it for the sub-pixel ones, as the sharpness test does.
            double S(float[] l, int shrink) => Gradient(l, w, 308 + shrink, 108 + shrink, 992 - shrink, 512 - shrink) / idealGradient;
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {v.Id} | {sp:0.000} | {ms:0.000} | {hc:0.000} | {tn:0.000} | {st:0.000} | {inset:0.00} | {rim:0.00} | {S(l0, 0):0.000} | {S(l25, 1):0.000} | {S(l50, 1):0.000} |"));
            _output.WriteLine($"{v.Id}: meter failures {f1}/{f2}/{f3}/{f4}/{f5}");
        }

        WriteSection("58-region-metrics", sb.ToString());
        _output.WriteLine(sb.ToString());
    }
}
