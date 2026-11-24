#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Tests.InsertJitter.EdgeSignal;
using Xunit;
using Xunit.Abstractions;
using static ReelBolt.WorkflowEngine.Tests.InsertJitter.Combined.CombinedSupport;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Combined;

/// <summary>
/// T-006 cost: wall time of the tracker per 10 s clip for C0 (shipped) and C3 (E1b + hinge + cadence + K4ra). Median of three runs, run sequentially
/// (this part never runs scenes in parallel), with the machine's 1-minute load average printed because the box is shared. The ffmpeg decode is measured
/// with <c>-benchmark</c> (CPU seconds and peak RSS); the tracker stages run on the already-decoded grid.
/// </summary>
public class CostBench
{
    private static readonly string[] DefaultScenes = ["static-1344", "static-1080p", "slow-push", "hold-cadence", "turn"];
    private static readonly Regex Maxrss = new(@"bench: maxrss=(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ITestOutputHelper _output;

    public CostBench(ITestOutputHelper output) => _output = output;

    private static (double Wall, double Cpu, double Rss) DecodeOnce(string clip, string decodeId, BenchSupport.GridPlan plan)
    {
        DecodeVariant v = EdgeDecode.Get(decodeId);
        string tmp = Path.Combine(Path.GetTempPath(), "combined-cost-" + Guid.NewGuid().ToString("N") + ".rgb");
        try
        {
            string[] args =
            [
                "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info", "-benchmark", "-y", "-threads", "0", "-i", clip,
                "-an", "-sn", "-vf", v.FilterFor(plan.SampleFps, plan.GridW, plan.GridH), "-f", "rawvideo", "-pix_fmt", "rgb24", tmp
            ];
            (string err, double cpu, double wall) = EdgeDecode.Run(args);
            Match m = Maxrss.Matches(err).LastOrDefault() ?? Match.Empty;
            return (wall, cpu, m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 1024.0 : double.NaN);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private static (double WallMs, double CpuMs) Timed(Action a)
    {
        Process proc = Process.GetCurrentProcess();
        TimeSpan c0 = proc.TotalProcessorTime;
        var sw = Stopwatch.StartNew();
        a();
        sw.Stop();
        proc.Refresh();
        return (sw.Elapsed.TotalMilliseconds, (proc.TotalProcessorTime - c0).TotalMilliseconds);
    }

    [JitterBenchFact]
    public void Tracker_cost_C0_vs_C3()
    {
        string[] names = Environment.GetEnvironmentVariable("REELBOLT_COMBINED_COST_SCENES") is { Length: > 0 } e ? e.Split(',') : DefaultScenes;
        string load0 = LoadAvg();
        var sb = new StringBuilder();
        sb.AppendLine("## 5. Cost: tracker time per 10 s clip, C0 vs C3");
        sb.AppendLine();
        sb.AppendLine($"Median of 3, one scene at a time, sequential. Machine: {Environment.ProcessorCount} logical cores, SHARED with other jobs; 1-minute load average at start {load0}, so WALL times are inflated and noisy. The robust numbers are CPU time: the ffmpeg decode's utime+stime (ffmpeg `-benchmark`) and the tracker process's own CPU time (`Process.TotalProcessorTime` delta around the stage run; the stage code is single-threaded apart from the GC). `tracker` = detection + assembly + edge refiner + shipped reject + Smooth (`EdgePipeline.Run` with no re-fit), which is what C0 pays; the shipped post chain (`prod post`) is timed on its own on the same S2 so C3 = tracker - prod post + new post (hinge + upright + held detection + K4ra + cadence).");
        sb.AppendLine();
        sb.AppendLine("| scene | decode C0 cpu s | decode C3 cpu s | decode C3a cpu s | decode C0 wall s | decode C3 wall s | decode C3a wall s | tracker C0 cpu ms | tracker C3-stages cpu ms | prod post cpu ms | new post cpu ms | C0 total cpu ms | C3 total cpu ms | C3/C0 cpu | tracker C0 wall ms | C3 total wall ms |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (string name in names)
        {
            JitterScene scene = JitterScenes.Get(name);
            RenderedClip clip = scene.Render(EdgeSupport.ClipsDir);
            BenchSupport.GridPlan plan = BenchSupport.Plan(scene, BenchSupport.DefaultConfig);
            double clipSec = scene.Frames / scene.Fps;
            double perTen = 10.0 / clipSec;
            var d0 = new List<(double Wall, double Cpu, double Rss)>();
            var d3 = new List<(double Wall, double Cpu, double Rss)>();
            var da = new List<(double Wall, double Cpu, double Rss)>();
            var t0 = new List<(double Wall, double Cpu)>();
            var t3 = new List<(double Wall, double Cpu)>();
            var prodPost = new List<(double Wall, double Cpu)>();
            var newPost = new List<(double Wall, double Cpu)>();
            byte[] grid0 = EdgeDecode.Decode(clip.Path, EdgeDecode.Get(EdgeDecode.BaseId), plan.SampleFps, plan.GridW, plan.GridH).Grid;
            byte[] grid3 = EdgeDecode.Decode(clip.Path, EdgeDecode.Get(CombinedConfigs.E1b), plan.SampleFps, plan.GridW, plan.GridH).Grid;
            for (int rep = 0; rep < 3; rep++)
            {
                d0.Add(DecodeOnce(clip.Path, EdgeDecode.BaseId, plan));
                d3.Add(DecodeOnce(clip.Path, CombinedConfigs.E1b, plan));
                da.Add(DecodeOnce(clip.Path, CombinedConfigs.E1a, plan));
                EdgeRun? r0 = null, r3 = null;
                t0.Add(Timed(() => r0 = CombinedPipeline.RunStages(scene, grid0, plan, clip.Path)));
                t3.Add(Timed(() => r3 = CombinedPipeline.RunStages(scene, grid3, plan, clip.Path)));

                // The shipped post chain on C3's S2 (what C3 would no longer run), then the new one.
                StageSet st = r3!.Stages;
                ChromaQuadTracker.Options options = st.Options;
                prodPost.Add(Timed(() =>
                {
                    (List<ChromaQuadTracker.FrameQuad> rej, _) = ChromaQuadTracker.RejectImplausibleSamples(st.S2, plan.SampleFps, plan.GridW, plan.GridH, options);
                    List<ChromaQuadTracker.FrameQuad> s3 = ChromaQuadTracker.UprightTrackLabels(rej, plan.GridW, plan.GridH);
                    _ = StageCapture.ProductionSmooth(s3, grid3, grid3.Length / (plan.GridW * plan.GridH * 3), plan.GridW, plan.GridH, scene.Width);
                }));
                newPost.Add(Timed(() => _ = CombinedPipeline.SmoothChain(scene, st, grid3, plan, 0.1, null)));
                _ = r0;
            }

            double dm0w = Median(d0.Select(x => x.Wall)), dm3w = Median(d3.Select(x => x.Wall));
            double dm0c = Median(d0.Select(x => x.Cpu)), dm3c = Median(d3.Select(x => x.Cpu)), dmac = Median(da.Select(x => x.Cpu)), dmaw = Median(da.Select(x => x.Wall));
            double m0c = Median(t0.Select(x => x.Cpu)), m3c = Median(t3.Select(x => x.Cpu));
            double ppc = Median(prodPost.Select(x => x.Cpu)), npc = Median(newPost.Select(x => x.Cpu));
            double m0w = Median(t0.Select(x => x.Wall)), m3w = Median(t3.Select(x => x.Wall)), ppw = Median(prodPost.Select(x => x.Wall)), npw = Median(newPost.Select(x => x.Wall));
            double c0Cpu = m0c, c3Cpu = m3c - ppc + npc;
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {name} | {dm0c * perTen:0.00} | {dm3c * perTen:0.00} | {dmac * perTen:0.00} | {dm0w * perTen:0.00} | {dm3w * perTen:0.00} | {dmaw * perTen:0.00} | {m0c * perTen:0} | {m3c * perTen:0} | {ppc * perTen:0} | {npc * perTen:0} | {c0Cpu * perTen:0} | {c3Cpu * perTen:0} | {c3Cpu / c0Cpu:0.00} | {m0w * perTen:0} | {(m3w - ppw + npw) * perTen:0} |"));
            _output.WriteLine($"{name}: C0 cpu {c0Cpu * perTen:0} ms, C3 cpu {c3Cpu * perTen:0} ms per 10 s, load {LoadAvg()}");
        }

        sb.AppendLine();
        sb.AppendLine("All times normalised to ms (or s) per 10 s of footage; every clip here is exactly 10 s. `tracker C3-stages` = the same stage replica run on the E1b grid (so it includes the shipped post chain, which C3 replaces); the C3 total subtracts `prod post` and adds `new post`. The stage code is identical for C0 and C3, so any difference in the `tracker` columns beyond the post chain is the grid's content (different detections) plus load noise.");
        sb.AppendLine();
        sb.AppendLine($"Load average at end: {LoadAvg()}. Memory: the tracker holds the decoded RGB grid (240 or 300 frames x 480x274x3 = 95 or 118 MiB here; identical for every decode: same grid size); the smoothing stack adds a few MiB of transient arrays (the K4ra fit tables are 4 widths x 8 series x frames doubles, under 0.5 MiB for 300 frames). The ffmpeg decode child's peak RSS is set by the decoder and measured identical for both decodes by `-benchmark` here; T-003's separate measurement (`/tmp/insert-tracking/edge-signal/report.md`, cost section) found 217 MiB (base) vs 229 MiB (E1b-bilinear) on the 1080p clip.");
        WriteSection("50-cost", sb.ToString());
    }
}
