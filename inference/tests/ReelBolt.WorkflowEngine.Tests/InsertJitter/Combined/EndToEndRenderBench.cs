#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Tests.InsertJitter.RenderPath;
using Xunit;
using Xunit.Abstractions;
using static ReelBolt.WorkflowEngine.Tests.InsertJitter.BenchSupport;
using static ReelBolt.WorkflowEngine.Tests.InsertJitter.Combined.CombinedSupport;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Combined;

/// <summary>
/// T-006 part 3: the tracks the tracker part wrote (C0 baseline and C3 = E1b + smoothing stack, plus the perfect-tracker control) through the real
/// compile-step keyframe sampling (<see cref="CompileStage"/>) and the unmatted flat-path chain (<see cref="RenderRig"/>), measured with T-005's
/// INTEGRATING meter (<see cref="EdgeMeter"/>, white insert, flat base, linear luma field). Two set-ups: <c>source</c> (canvas = the 1344x768 scene, overscan 0.02,
/// scored in source px) and <c>staging</c> (1920x1080 Crop from 1344x768 via <c>SourceCanvasFit.For(cover: true)</c>, overscan 0.10, scored in output px and converted
/// back to source px). The matte, reflection transfer and guard fill are NOT in this chain (the rig is the unmatted flat path): overscan is geometry only, so
/// 0.10 is honoured, but a matte's own edge behaviour is not measured. Rows: <c>base</c> = production chain, <c>prerep</c> = T-005 prescale + smeared content
/// prototype, <c>prerep+M</c> = prerep and the additive overscan vector from a centred 15-frame mean (T-005 item 3's M variant).
/// </summary>
public class EndToEndRenderBench
{
    private static readonly string[] DefaultScenes = ["static-1344", "microshake", "slow-push", "hold-cadence", "turn"];

    private readonly ITestOutputHelper _output;
    private readonly object _lock = new();

    public EndToEndRenderBench(ITestOutputHelper output) => _output = output;

    private void Log(string m)
    {
        lock (_lock)
            _output.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}");
    }

    internal sealed record Setup(string Id, string Label, int CanvasW, int CanvasH, bool Cover, double Overscan);

    private sealed record Row(string TrackId, string Variant, bool AdditiveOverscan);

    private sealed record Job(string Scene, string Setup, Row Row);

    private static readonly Row[] Rows =
    [
        new("P", "base", false), new("P", "prerep", false),
        new("C0", "base", false), new("C0", "prerep", false),
        new("C3", "base", false), new("C3", "prerep", false), new("C3", "prerep", true)
    ];

    internal static VideoInsertRegionTrack ToTrack(IReadOnlyList<Quad> q, JitterScene scene)
    {
        double w = scene.Width, h = scene.Height;
        var kfs = q.Select(s => new VideoInsertQuadKeyframe(s.T, s.X0 / w, s.Y0 / h, s.X1 / w, s.Y1 / h, s.X2 / w, s.Y2 / h, s.X3 / w, s.Y3 / h)).ToList();
        return new VideoInsertRegionTrack("r0", null, kfs[0].TimeSec, kfs[^1].TimeSec, kfs, 1.0, 0.3, 1.7, "Static", "green");
    }

    internal static CompileStage.Result Compile(JitterScene scene, VideoInsertRegionTrack track, Setup setup, double overscan) =>
        CompileStage.Run("T006", track, scene.TruthAt, scene.Width, scene.Height, setup.CanvasW, setup.CanvasH, setup.Cover, overscan,
            (int)Math.Round(scene.Fps), 1, scene.Frames / scene.Fps);

    /// <summary>Canvas px back to source px through the inverse fit (the same mapping <see cref="CompileStage"/> uses for its own SourcePx).</summary>
    internal static List<Quad> ToSource(IEnumerable<Quad> canvas, VideoCompileStepExecutor.SourceCanvasFit fit, int sw, int sh)
    {
        double X(double v) => (v - fit.OffsetX) / fit.Width * sw;
        double Y(double v) => (v - fit.OffsetY) / fit.Height * sh;
        return canvas.Select(c => new Quad(c.T, X(c.X0), Y(c.Y0), X(c.X1), Y(c.Y1), X(c.X2), Y(c.Y2), X(c.X3), Y(c.Y3))).ToList();
    }

    [JitterBenchFact]
    public void End_to_end_render_C0_vs_C3()
    {
        string[] sceneNames = (Environment.GetEnvironmentVariable("REELBOLT_COMBINED_RENDER_SCENES") is { Length: > 0 } e ? e.Split(',') : DefaultScenes);
        Setup[] setups =
        [
            new("source", "canvas = 1344x768 scene, overscan 0.02, source px", 1344, 768, false, 0.02),
            new("staging", "1920x1080 Crop (cover) from 1344x768, overscan 0.10, output px", 1920, 1080, true, 0.10)
        ];
        var scenes = sceneNames.Select(JitterScenes.Get).ToList();

        var tracks = new Dictionary<(string Scene, string Track), List<Quad>>();
        foreach (JitterScene s in scenes)
        {
            tracks[(s.Name, "P")] = s.Truth().ToList();
            foreach (string id in new[] { "C0", "C3" })
                tracks[(s.Name, id)] = ReadCsvTrack(CsvPath(s.Name, id));
        }

        var compiled = new ConcurrentDictionary<(string Scene, string Setup, string Track, bool M), CompileStage.Result>();
        CompileStage.Result Compiled(JitterScene scene, Setup setup, Row row)
        {
            return compiled.GetOrAdd((scene.Name, setup.Id, row.TrackId, row.AdditiveOverscan), _ =>
            {
                List<Quad> q = tracks[(scene.Name, row.TrackId)];
                CompileStage.Result ideal = Compile(scene, ToTrack(q, scene), setup, setup.Overscan);
                if (!row.AdditiveOverscan)
                    return ideal;
                // M: the expansion vector is o x (smoothed shape - its centroid) added to the raw quad; the compile step then sees overscan 0.
                CompileStage.Result m = Compile(scene, ToTrack(OverscanBench.Smoothed(q, setup.Overscan, 15), scene), setup, 0.0);
                return m with { IdealCanvasPx = ideal.IdealCanvasPx, IdealSourcePx = ideal.IdealSourcePx, Overscan = setup.Overscan };
            });
        }

        var jobs = new List<Job>();
        foreach (Setup setup in setups)
        {
            foreach (JitterScene scene in scenes)
            {
                foreach (Row row in Rows)
                    jobs.Add(new Job(scene.Name, setup.Id, row));
            }
        }

        var results = new ConcurrentDictionary<Job, RigResult>();
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = Par }, job =>
        {
            JitterScene scene = JitterScenes.Get(job.Scene);
            Setup setup = setups.First(s => s.Id == job.Setup);
            CompileStage.Result s5 = Compiled(scene, setup, job.Row);
            long start = (long)Math.Round(s5.OutputStartSec * scene.Fps);
            var input = new RigInput(setup.CanvasW, setup.CanvasH, (int)Math.Round(scene.Fps), 1, scene.Frames, s5.Keyframes, s5.CanvasPx, "0xFFFFFF", null, "0x404040", (int)start);
            RigVariant v = SceneMatrixBench.Variants.First(x => x.Id == job.Row.Variant);
            results[job] = RenderRig.Run(input, v, RenderStage.White, runProd: false, runMeter: true);
            Log($"{job.Setup} {job.Scene} {job.Row.TrackId}/{job.Row.Variant}{(job.Row.AdditiveOverscan ? "+M" : "")}: meter {results[job].Meter.Count}/{scene.Frames} cpu {results[job].CpuMs:0} ms");
        });

        // Frames whose (ideal) overscanned quad lies fully inside the canvas by this margin: the meter integrates 6 px each side of an edge, and a quad that
        // touches the frame border is clipped, which no longer measures the composite. The same frames are scored for every row of a scene.
        const double Margin = 10.0;
        var keepCache = new Dictionary<(string Scene, string Setup), HashSet<int>>();
        HashSet<int> Keep(JitterScene scene, Setup setup)
        {
            if (keepCache.TryGetValue((scene.Name, setup.Id), out HashSet<int>? hit))
                return hit;
            CompileStage.Result s5 = Compiled(scene, setup, Rows[0]);
            var keep = new HashSet<int>();
            foreach (Quad q in s5.IdealCanvasPx)
            {
                bool inside = Enumerable.Range(0, 4).All(c =>
                {
                    (double x, double y) = q.Corner(c);
                    return x >= Margin && x <= setup.CanvasW - 1 - Margin && y >= Margin && y <= setup.CanvasH - 1 - Margin;
                });
                if (inside)
                    keep.Add((int)Math.Round(q.T * scene.Fps));
            }

            keepCache[(scene.Name, setup.Id)] = keep;
            return keep;
        }

        JitterStats Stats(JitterScene scene, Setup setup, Row row, bool sourcePx)
        {
            CompileStage.Result s5 = Compiled(scene, setup, row);
            RigResult r = results[new Job(scene.Name, setup.Id, row)];
            HashSet<int> keep = Keep(scene, setup);
            List<Quad> meter = r.Meter.Where(q => keep.Contains((int)Math.Round(q.T * scene.Fps))).ToList();
            return sourcePx
                ? JitterMetrics.Compute(ToSource(meter, s5.Fit, scene.Width, scene.Height), s5.IdealSourcePx, scene.Fps)
                : JitterMetrics.Compute(meter, s5.IdealCanvasPx, scene.Fps);
        }

        var sb = new StringBuilder();
        sb.AppendLine("## 4. End-to-end render: tracker output through the compile step and the ffmpeg composite");
        sb.AppendLine();
        sb.AppendLine("**Meter**: T-005's integrating edge meter (`EdgeMeter`: along the inward normal the linear field is sampled bilinearly and summed, `d = reach - integral` is the edge position, least-squares line per edge, corners = intersections), applied to the rendered frame of the unmatted flat-path chain (flat `0x404040` base, white insert, luma field `0.2126R+0.7152G+0.0722B`, no thresholding). Reference = the truth pushed through the same overscan and fit. **No matte** (the rig is the production flat chain without plate matte, reflection transfer or guard fill): the staging overscan 0.10 is applied as geometry, but a matte's own edge behaviour is not measured here.");
        sb.AppendLine();
        sb.AppendLine("Tracks: `P` = perfect (truth as keyframes, the render floor), `C0` = shipped tracker, `C3` = E1b decode + hinge + cadence + K4ra (tracks from section 1's csv, 4-decimal). Chains: `base` = production, `prerep` = T-005 prescale + smeared-content prototype (keyframes from the production per-keyframe-centroid overscan), `prerep+M` = prerep with T-005 item 3's additive overscan vector from a centred 15-frame mean of the track.");
        sb.AppendLine();
        sb.AppendLine($"Scoring window: only frames whose ideal overscanned quad lies at least {Margin:0} px inside the canvas are scored (the same frames for every row of a scene); frames where the quad touches the border are clipped and the meter reads the border, not the composite. The count of scored frames per scene is in the `N` column of the full tables; the number of meter failures (unmeasurable frames) is in brackets in the summary tables.");
        sb.AppendLine();
        foreach (Setup setup in setups)
        {
            sb.AppendLine($"### Set-up `{setup.Id}`: {setup.Label}");
            sb.AppendLine();
            sb.AppendLine("Frames scored / total: " + string.Join(", ", scenes.Select(sc => $"{sc.Name} {Keep(sc, setup).Count}/{sc.Frames}")));
            sb.AppendLine();
            // Summary: SlipRms and ErrMax per scene x row.
            foreach ((string name, Func<JitterStats, double> get) in new (string, Func<JitterStats, double>)[] { ("SlipRms", s => s.SlipRms), ("HfRms", s => s.HfRms), ("ErrMax", s => s.ErrMax), ("ErrMean", s => s.ErrMean) })
            {
                string unit = setup.Id == "staging" ? "output px" : "source px";
                sb.AppendLine($"**{name}** ({unit}; failed meter frames in brackets when > 0)");
                sb.AppendLine();
                sb.AppendLine("| scene | " + string.Join(" | ", Rows.Select(RowLabel)) + " |");
                sb.AppendLine("|---|" + string.Concat(Rows.Select(_ => "---:|")));
                foreach (JitterScene scene in scenes)
                {
                    var cells = new List<string>();
                    foreach (Row row in Rows)
                    {
                        var job = new Job(scene.Name, setup.Id, row);
                        RigResult r = results[job];
                        JitterStats st = Stats(scene, setup, row, false);
                        cells.Add(JitterMetrics.Cell(get(st)) + (r.MeterFailed > 0 ? $" ({r.MeterFailed})" : string.Empty));
                    }

                    sb.AppendLine($"| {scene.Name} | {string.Join(" | ", cells)} |");
                }

                sb.AppendLine();
            }

            if (setup.Id == "staging")
            {
                foreach ((string name, Func<JitterStats, double> get) in new (string, Func<JitterStats, double>)[] { ("SlipRms", s => s.SlipRms), ("HfRms", s => s.HfRms), ("ErrMax", s => s.ErrMax) })
                {
                    sb.AppendLine($"**{name}**, the same renders converted back to SOURCE px (divide by the fit scale {Compiled(scenes[0], setup, Rows[0]).Fit.Width / 1344.0:0.0000})");
                    sb.AppendLine();
                    sb.AppendLine("| scene | " + string.Join(" | ", Rows.Select(RowLabel)) + " |");
                    sb.AppendLine("|---|" + string.Concat(Rows.Select(_ => "---:|")));
                    foreach (JitterScene scene in scenes)
                    {
                        var cells = new List<string>();
                        foreach (Row row in Rows)
                        {
                            cells.Add(JitterMetrics.Cell(get(Stats(scene, setup, row, true))));
                        }

                        sb.AppendLine($"| {scene.Name} | {string.Join(" | ", cells)} |");
                    }

                    sb.AppendLine();
                }
            }

            sb.AppendLine("Full metric tables per row:");
            sb.AppendLine();
            var tableRows = new List<BenchSupport.Row>();
            foreach (JitterScene scene in scenes)
            {
                foreach (Row row in Rows)
                {
                    tableRows.Add(new BenchSupport.Row($"{scene.Name} / {RowLabel(row)}", Stats(scene, setup, row, false)));
                }
            }

            sb.AppendLine(Table(tableRows));
        }

        CombinedSupport.WriteSection("40-end-to-end-render", sb.ToString());
    }

    private static string RowLabel(Row r) => $"{r.TrackId}/{r.Variant}{(r.AdditiveOverscan ? "+M" : string.Empty)}";

    private static int Par => int.TryParse(Environment.GetEnvironmentVariable("REELBOLT_COMBINED_PARALLEL"), out int p) && p > 0 ? p : 3;
}
