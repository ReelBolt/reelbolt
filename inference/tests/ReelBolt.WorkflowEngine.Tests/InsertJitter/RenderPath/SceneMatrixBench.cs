#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using Xunit.Abstractions;
using static ReelBolt.WorkflowEngine.Tests.InsertJitter.BenchSupport;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.RenderPath;

/// <summary>
/// T-005 item 2 (full scene matrix) and the real-track half of the expected-gain question. All ten scenes, the baseline chain against the
/// render-side candidates, driven by the perfect tracker (render in isolation) and by the shipped tracker's S4 output (render on top of real
/// tracking noise).
/// </summary>
public class SceneMatrixBench
{
    private readonly ITestOutputHelper _output;

    public SceneMatrixBench(ITestOutputHelper output) => _output = output;

    internal static readonly RigVariant[] Variants =
    [
        DecompositionBench.Baseline,
        new("o444", "overlay yuv444 (composite 4:4:4, then to 4:2:0 as the encoder would)", OverlayFormat: "yuv444", Via420: true),
        new("prerep", "PROTOTYPE prescale + smeared content", Prescale: true, CompensateBorder: true, ContentEdgeReplicate: true),
        new("rec", "PROTOTYPE prescale + smeared content + overlay yuv444 (via 4:2:0)", OverlayFormat: "yuv444", Prescale: true, CompensateBorder: true, ContentEdgeReplicate: true, Via420: true)
    ];

    private static readonly (RenderStage.InsertContent Content, string Lavfi)[] Contents =
        [(RenderStage.White, "0xFFFFFF"), (RenderStage.Magenta, "0xFF00FF")];

    private sealed record Job(string Scene, string Track, string Series, string Content, string Variant);

    [JitterBenchFact]
    public void Scene_matrix_baseline_vs_candidates()
    {
        string only = Environment.GetEnvironmentVariable("REELBOLT_RENDER_TRACKS") ?? "perfect,real";
        string[] tracks = only.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var scenes = BenchSupport.SelectedScenes(RenderScenes.AllScenes).ToList();
        var prepared = new ConcurrentDictionary<(string Scene, string Track), CompileStage.Result>();
        foreach (JitterScene scene in scenes)
        {
            foreach (string track in tracks)
            {
                prepared[(scene.Name, track)] = RenderScenes.Compile(scene, track == "perfect" ? RenderScenes.PerfectTrack(scene) : RenderScenes.RealTrack(scene));
            }
        }

        var jobs = new List<Job>();
        foreach (JitterScene scene in scenes)
        {
            foreach (string track in tracks)
            {
                foreach ((RenderStage.InsertContent c, _) in Contents)
                {
                    foreach (RigVariant v in Variants)
                    {
                        jobs.Add(new Job(scene.Name, track, "flat", c.Label, v.Id));
                        if (c.Label == "white")
                            jobs.Add(new Job(scene.Name, track, "clip", c.Label, v.Id));
                    }
                }
            }
        }

        var results = new ConcurrentDictionary<Job, RigResult>();
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = 3 }, job =>
        {
            JitterScene scene = JitterScenes.Get(job.Scene);
            RigVariant v = Variants.First(x => x.Id == job.Variant);
            (RenderStage.InsertContent c, string lavfi) = Contents.First(x => x.Content.Label == job.Content);
            RigInput input = RenderScenes.Input(scene, prepared[(job.Scene, job.Track)], lavfi, flatBase: job.Series == "flat");
            RigResult r = RenderRig.Run(input, v, c, runProd: job.Series == "clip" || job.Content == "white", runMeter: job.Series == "flat");
            results[job] = r;
            _output.WriteLine($"{job}: meter {r.Meter.Count} prod {r.Prod.Count} cpu {r.CpuMs:0} ms");
        });

        var sb = new StringBuilder();
        sb.AppendLine("## Render path scene matrix: baseline chain vs candidates (item 2)");
        sb.AppendLine();
        sb.AppendLine("All ten scenes. **perfect** = the scene's truth as the keyframes (render in isolation); **real** = the shipped tracker's S4 output (read from the baseline bench csv) through `BuildInsertKeyframes` (render on top of real tracking noise). Reference = the truth pushed through the same overscan (0.02), so the error includes the tracker's own error in the **real** tables and only the render's in the **perfect** ones. `flat` series scored with the integrating meter `[meter]` (and S6's detector `[S6]` where noted); `clip` series (white only) with S6's detector on the scene's own plate clip, directly comparable with the baseline report.");
        sb.AppendLine("Variants: `baseline` = production chain; `o444` = overlay `format=yuv444` then to 4:2:0 as the final encode does (a one-line production change); `prerep` and `rec` are test-only PROTOTYPES (`RenderRig`): content and mask prescaled to the largest quad of the insert and pinned through the quad's own projective map, content edge pixels smeared past the quad instead of a 1 px black rim, `rec` adds the 4:4:4 overlay.");
        sb.AppendLine();
        foreach (string track in tracks)
        {
            foreach ((RenderStage.InsertContent c, _) in Contents)
            {
                foreach (string series in new[] { "flat", "clip" })
                {
                    if (series == "clip" && c.Label != "white")
                        continue;
                    var rows = new List<Row>();
                    foreach (JitterScene scene in scenes)
                    {
                        CompileStage.Result s5 = prepared[(scene.Name, track)];
                        foreach (RigVariant v in Variants)
                        {
                            if (!results.TryGetValue(new Job(scene.Name, track, series, c.Label, v.Id), out RigResult? r))
                                continue;
                            IReadOnlyList<Quad> q = series == "flat" ? r.Meter : r.Prod;
                            rows.Add(new Row($"{scene.Name} / {v.Id}", RenderScenes.Score(q, s5.IdealSourcePx, scene.Fps)));
                        }
                    }

                    sb.AppendLine($"### {track} track, {c.Label} insert, {series} base ({(series == "flat" ? "meter" : "S6 detector")})");
                    sb.AppendLine();
                    sb.AppendLine(Table(rows));
                    if (series == "flat" && c.Label == "white" && results.Values.Any(r => r.Prod.Count > 0))
                    {
                        var rows2 = new List<Row>();
                        foreach (JitterScene scene in scenes)
                        {
                            CompileStage.Result s5 = prepared[(scene.Name, track)];
                            foreach (RigVariant v in Variants)
                            {
                                if (results.TryGetValue(new Job(scene.Name, track, series, c.Label, v.Id), out RigResult? r))
                                    rows2.Add(new Row($"{scene.Name} / {v.Id}", RenderScenes.Score(r.Prod, s5.IdealSourcePx, scene.Fps)));
                            }
                        }

                        sb.AppendLine($"Same renders read with the S6 production fitter ({track}, white, flat):");
                        sb.AppendLine();
                        sb.AppendLine(Table(rows2));
                    }
                }
            }

            // Compact SlipRms summary.
            sb.AppendLine($"### SlipRms summary, {track} track (px, source px)");
            sb.AppendLine();
            sb.AppendLine("| scene | " + string.Join(" | ", Contents.SelectMany(c => Variants.Select(v => $"{c.Content.Label[0]}:{v.Id}"))) + " |");
            sb.AppendLine("|---|" + string.Concat(Enumerable.Repeat("---:|", Contents.Length * Variants.Length)));
            foreach (JitterScene scene in scenes)
            {
                CompileStage.Result s5 = prepared[(scene.Name, track)];
                var cells = new List<string>();
                foreach ((RenderStage.InsertContent c, _) in Contents)
                {
                    foreach (RigVariant v in Variants)
                    {
                        string cell = "-";
                        if (results.TryGetValue(new Job(scene.Name, track, "flat", c.Label, v.Id), out RigResult? r))
                            cell = JitterMetrics.Cell(RenderScenes.Score(r.Meter, s5.IdealSourcePx, scene.Fps).SlipRms);
                        cells.Add(cell);
                    }
                }

                sb.AppendLine($"| {scene.Name} | {string.Join(" | ", cells)} |");
            }

            sb.AppendLine();
        }

        WriteSection("52-scene-matrix", sb.ToString());
    }
}
