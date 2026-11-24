#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using Xunit.Abstractions;
using static ReelBolt.WorkflowEngine.Tests.InsertJitter.BenchSupport;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.RenderPath;

/// <summary>
/// T-005 items 2 and 5. The baseline S6 chain with a PERFECT tracker (truth keyframes, the compile step's own overscan 0.02) on the
/// <c>slow-push</c> scene, changed one thing at a time; then the scene matrix for the candidates. Two series: <b>clip</b> (the scene's
/// encoded plate clip behind the insert, the production S6 edge fitter on a "luma above 170" field: directly comparable with the baseline
/// report's 0.64 / 0.96) and <b>flat</b> (a flat grey base so the integrating meter reads a clean edge; the base never changes the
/// composite's geometry, only what the detector sees).
/// </summary>
public class DecompositionBench
{
    private readonly ITestOutputHelper _output;

    public DecompositionBench(ITestOutputHelper output) => _output = output;

    internal static readonly RigVariant Baseline = new("base", "baseline (production chain: default 4:2:0 overlay, rgb24 out)");

    internal static readonly RigVariant[] DecompositionVariants =
    [
        Baseline,
        new("o444", "overlay format=yuv444", OverlayFormat: "yuv444"),
        new("gbrp", "overlay format=gbrp", OverlayFormat: "gbrp"),
        new("cub", "perspective interpolation=cubic", Interpolation: "cubic"),
        new("comp", "PROTOTYPE border-compensated pin", CompensateBorder: true),
        new("rep", "PROTOTYPE content smeared past the edge (no black rim)", CompensateBorder: true, ContentEdgeReplicate: true),
        new("pre", "PROTOTYPE prescale content+mask to the largest quad", Prescale: true, CompensateBorder: true),
        new("ss2", "PROTOTYPE mask supersampled 2x", CompensateBorder: true, MaskSupersample: 2),
        new("prerep", "PROTOTYPE prescale + smeared content", Prescale: true, CompensateBorder: true, ContentEdgeReplicate: true),
        new("prerep444", "PROTOTYPE prescale + smeared content + overlay yuv444", OverlayFormat: "yuv444", Prescale: true, CompensateBorder: true, ContentEdgeReplicate: true),
        new("mask", "warped mask branch alone (perspective only)", CompensateBorder: true, MaskOnly: true)
    ];

    private static readonly (RenderStage.InsertContent Content, string Lavfi)[] Contents =
        [(RenderStage.White, "0xFFFFFF"), (RenderStage.Magenta, "0xFF00FF")];

    [JitterBenchFact]
    public void Decompose_slow_push_perfect_track()
    {
        JitterScene scene = JitterScenes.Get("slow-push");
        CompileStage.Result s5 = RenderScenes.Compile(scene, RenderScenes.PerfectTrack(scene));
        var jobs = (from c in Contents
                    from series in new[] { "clip", "flat" }
                    from v in DecompositionVariants
                    where !(series == "clip" && v.MaskOnly)
                    select (c, series, v)).ToList();
        var results = new ConcurrentDictionary<string, RigResult>();
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = 4 }, j =>
        {
            RigInput input = RenderScenes.Input(scene, s5, j.c.Lavfi, flatBase: j.series == "flat");
            RigResult r = RenderRig.Run(input, j.v, j.c.Content, runProd: true, runMeter: j.series == "flat");
            results[$"{j.c.Content.Label}|{j.series}|{j.v.Id}"] = r;
            _output.WriteLine($"{j.c.Content.Label}|{j.series}|{j.v.Id}: prod {r.Prod.Count} meter {r.Meter.Count}");
        });

        var sb = new StringBuilder();
        sb.AppendLine("## Render path decomposition on slow-push, perfect tracker (items 2 and 5)");
        sb.AppendLine();
        sb.AppendLine("Perfect tracker = the scene's truth as one keyframe per frame through the compile step's own `BuildInsertKeyframes` (overscan 0.02) and `BuildPerspectiveFilter`; source px = canvas px (1344x768). Slip is the headline wobble (frame-to-frame change of corner error). Reference = truth pushed through the same overscan. `[S6]` = the production edge fitter on S6's field (white: luma above 170, fixed 50% level crossing, 40 probes/edge); `[meter]` = the integrating meter on a linear field (flat base only). `clip` = the scene's encoded plate clip is the base picture, `flat` = flat 0x404040 base. The 1 px black border inset makes the rendered quad ~0.5-1.5 px smaller than commanded (ErrMean), constant for the baseline and removed by the PROTOTYPE border-compensated pin.");
        sb.AppendLine("Every row below `baseline` that says PROTOTYPE is a test-only filtergraph variant built in `RenderRig`, not production code.");
        sb.AppendLine();
        foreach ((RenderStage.InsertContent content, _) in Contents)
        {
            foreach (string series in new[] { "clip", "flat" })
            {
                sb.AppendLine($"### {content.Label} insert, {series} base");
                sb.AppendLine();
                var rows = new List<Row>();
                foreach (RigVariant v in DecompositionVariants)
                {
                    if (!results.TryGetValue($"{content.Label}|{series}|{v.Id}", out RigResult? r))
                        continue;
                    if (!v.MaskOnly)
                        rows.Add(new Row($"{v.Label} [S6]", RenderScenes.Score(r.Prod, s5.IdealSourcePx, scene.Fps)));
                    if (series == "flat")
                        rows.Add(new Row($"{v.Label} [meter]", RenderScenes.Score(r.Meter, s5.IdealSourcePx, scene.Fps)));
                }

                sb.AppendLine(Table(rows));
            }
        }

        WriteSection("51-decomposition-slow-push", sb.ToString());
    }
}
