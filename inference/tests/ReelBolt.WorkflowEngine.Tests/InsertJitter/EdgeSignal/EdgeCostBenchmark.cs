#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.EdgeSignal;

/// <summary>
/// Cost of the detection-side candidates: ffmpeg CPU seconds and peak RSS per decode (min of 3, <c>/usr/bin/time -v</c>),
/// and tracker-side milliseconds per 10 s clip (min of 3 serial runs). Run alone: the numbers are only meaningful when the
/// machine is not saturated, and say so in the report when sibling jobs were running.
/// </summary>
public class EdgeCostBenchmark
{
    private readonly ITestOutputHelper _output;

    public EdgeCostBenchmark(ITestOutputHelper output) => _output = output;

    private static readonly Regex TimeLine = new(@"RSS_KB=(\d+) USER=([0-9.]+) SYS=([0-9.]+) WALL=([0-9.]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed record Measure(double Cpu, double Wall, long RssKb);

    private static Measure RunTimed(IEnumerable<string> ffmpegArgs, int repeats = 3)
    {
        Measure? best = null;
        for (int i = 0; i < repeats; i++)
        {
            var psi = new ProcessStartInfo("/usr/bin/time") { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string a in new[] { "-f", "RSS_KB=%M USER=%U SYS=%S WALL=%e", "ffmpeg", "-hide_banner", "-nostdin", "-nostats", "-loglevel", "error", "-threads", "0" })
                psi.ArgumentList.Add(a);
            foreach (string a in ffmpegArgs)
                psi.ArgumentList.Add(a);
            using Process p = Process.Start(psi)!;
            Task<string> err = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync();
            p.WaitForExit();
            Match m = TimeLine.Match(err.Result);
            if (!m.Success || p.ExitCode != 0)
                throw new InvalidOperationException(err.Result);
            var cur = new Measure(
                double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
                long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            if (best is null || cur.Cpu < best.Cpu)
                best = cur;
        }

        return best!;
    }

    [JitterBenchFact]
    public void Cost_of_decode_variants_and_tracker_passes()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Cost (serial, min of 3)");
        sb.AppendLine();
        sb.AppendLine($"Machine load at start: `{File.ReadAllText("/proc/loadavg").Trim()}` ({Environment.ProcessorCount} logical CPUs). ffmpeg CPU = user+sys seconds from `/usr/bin/time`, RSS = peak resident set of the ffmpeg process. Clips are 10 s: `slow-push` 1344x768 @ 24 fps (240 frames), `static-1080p` 1920x1080 @ 30 fps (300 frames); x6 for a minute of footage.");
        sb.AppendLine();

        foreach (string sceneName in new[] { "slow-push", "static-1080p" })
        {
            JitterScene scene = JitterScenes.Get(sceneName);
            RenderedClip clip = scene.Render(EdgeSupport.ClipsDir);
            BenchSupport.GridPlan plan = BenchSupport.Plan(scene, BenchSupport.DefaultConfig);
            sb.AppendLine($"### {sceneName}: {scene.Width}x{scene.Height} @ {scene.Fps:0.##} fps, grid {plan.GridW}x{plan.GridH} @ {plan.SampleFps:0.##} fps");
            sb.AppendLine();

            sb.AppendLine("| ffmpeg pass | CPU s / 10 s clip | CPU s / min footage | wall s | peak RSS MiB |");
            sb.AppendLine("|---|---:|---:|---:|---:|");
            Measure floor = RunTimed(["-i", clip.Path, "-an", "-sn", "-f", "null", "-"]);
            sb.AppendLine(Row("decode only (h264 -> null; the floor every pass shares)", floor));
            foreach (DecodeVariant v in EdgeDecode.All)
            {
                string tmp = Path.Combine(Path.GetTempPath(), "edge-cost-" + Guid.NewGuid().ToString("N") + ".rgb");
                try
                {
                    Measure m = RunTimed(["-y", "-i", clip.Path, "-an", "-sn", "-vf", v.FilterFor(plan.SampleFps, plan.GridW, plan.GridH), "-f", "rawvideo", "-pix_fmt", "rgb24", tmp]);
                    sb.AppendLine(Row($"grid `{v.Id}`", m));
                }
                finally
                {
                    File.Delete(tmp);
                }
            }

            string fpsText = plan.SampleFps.ToString("0.######", CultureInfo.InvariantCulture);
            Measure g2 = RunTimed(["-i", clip.Path, "-an", "-sn", "-vf", $"fps={fpsText},scale={plan.GridW * 2}:{plan.GridH * 2}:flags=area,format=gray", "-f", "rawvideo", "-pix_fmt", "gray", "-y", "/dev/null"]);
            sb.AppendLine(Row($"hi-res luma 2x grid ({plan.GridW * 2}x{plan.GridH * 2} gray, streamed)", g2));
            Measure gn = RunTimed(["-i", clip.Path, "-an", "-sn", "-vf", $"fps={fpsText},format=gray", "-f", "rawvideo", "-pix_fmt", "gray", "-y", "/dev/null"]);
            sb.AppendLine(Row($"hi-res luma native ({scene.Width}x{scene.Height} gray, streamed)", gn));
            sb.AppendLine();
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"Hi-res frame held in the tracker process: 2x grid {plan.GridW * 2 * plan.GridH * 2 / 1024.0:0} KiB, native {scene.Width * scene.Height / 1024.0:0} KiB (one frame; never the whole clip). Grid RGB held today: {plan.GridW * plan.GridH * 3 / 1024.0:0} KiB/frame x frames (capped by the 640 MiB analysis budget = {BenchSupport.Plan(scene, BenchSupport.DefaultConfig).FrameCap} frames at this grid)."));
            sb.AppendLine();

            // Tracker side, on the production grid.
            DecodeResult dec = EdgeDecode.Decode(clip.Path, EdgeDecode.Get(EdgeDecode.BaseId), plan.SampleFps, plan.GridW, plan.GridH);
            int frames = dec.Grid.Length / (plan.GridW * plan.GridH * 3);
            ChromaQuadTracker.Options options = BenchSupport.ProductionOptions(BenchSupport.DefaultConfig);
            var cands = new List<Candidate>
            {
                EdgeSignalBenchmark.Baseline,
                EdgeSignalBenchmark.Control,
                new("e2-int", "", S1Fit: new EdgeFitOptions(Crossing.LumaIntegral)),
                new("e2-half", "", S1Fit: new EdgeFitOptions(Crossing.LumaHalf)),
                new("e4-both", "", S1Fit: new EdgeFitOptions(), TrackLevel: true, Seed: SeedKind.Previous),
                new("e3-2x-int", "", HiRes: HiResKind.Gray2x, HiResFit: new EdgeFitOptions(Crossing.LumaIntegral)),
                new("e3-nat-int", "", HiRes: HiResKind.GrayNative, HiResFit: new EdgeFitOptions(Crossing.LumaIntegral))
            };
            sb.AppendLine("| tracker pass (grid already decoded) | total ms / 10 s | S1 re-fit ms | hi-res fit ms | allocated MiB |");
            sb.AppendLine("|---|---:|---:|---:|---:|");
            foreach (Candidate c in cands)
            {
                double bestTotal = double.MaxValue, bestRefit = 0, bestHi = 0, alloc = 0;
                for (int i = 0; i < 3; i++)
                {
                    long a0 = GC.GetAllocatedBytesForCurrentThread();
                    var sw = Stopwatch.StartNew();
                    EdgeRun run = EdgePipeline.Run(c, dec.Grid, frames, plan.GridW, plan.GridH, plan.SampleFps, options, scene.Width, scene.Height, clip.Path);
                    double ms = sw.Elapsed.TotalMilliseconds;
                    if (ms < bestTotal)
                    {
                        bestTotal = ms;
                        bestRefit = run.RefitMs;
                        bestHi = run.HiResMs;
                        alloc = (GC.GetAllocatedBytesForCurrentThread() - a0) / 1048576.0;
                    }
                }

                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| `{c.Id}` | {bestTotal:0} | {bestRefit:0} | {bestHi:0} | {alloc:0} |"));
            }

            sb.AppendLine();
            sb.AppendLine("`base` re-fits nothing (its fit runs inside detection); `ctl-chroma` is the prototype's re-fit in chroma mode, i.e. the cost of a second fit pass with production arithmetic, so a candidate's increment over production is its row minus `ctl-chroma`'s only when the real change would replace (not add to) the production fit. The hi-res fit time excludes the second ffmpeg decode (listed in the table above) and, for the streamed pass, includes waiting on the pipe.");
            sb.AppendLine();
            _output.WriteLine($"{sceneName} cost done");
        }

        EdgeSupport.WriteSection("90-cost", sb.ToString());
    }

    private static string Row(string label, Measure m) =>
        string.Create(CultureInfo.InvariantCulture, $"| {label} | {m.Cpu:0.00} | {m.Cpu * 6:0.0} | {m.Wall:0.00} | {m.RssKb / 1024.0:0} |");
}
