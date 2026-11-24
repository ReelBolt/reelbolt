#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using Xunit.Abstractions;
using static ReelBolt.WorkflowEngine.Tests.InsertJitter.BenchSupport;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.RenderPath;

/// <summary>
/// T-005 item 5. The staging setup renders 1344x768 plates into a 1920x1080 canvas by <c>SourceCanvasFit.For(..., cover: true)</c> (x1.4286, matted
/// overscan 0.10). Same scenes, same flat-base measurement, with the insert chain on the 1920x1080 canvas. Reported in OUTPUT px (what the viewer sees)
/// and in source px (output / 1.4286), next to the same-size 1344x768 canvas at the same overscan, so linear scaling of the tracker's wobble and the
/// render's own can be told apart.
/// </summary>
public class MagnificationBench
{
    private readonly ITestOutputHelper _output;

    public MagnificationBench(ITestOutputHelper output) => _output = output;

    private static readonly string[] Scenes = ["slow-push", "microshake", "static-1344"];

    [JitterBenchFact]
    public void Output_magnification_1344_to_1920()
    {
        var variants = new[] { DecompositionBench.Baseline, SceneMatrixBench.Variants[3] };
        var jobs = (from s in Scenes from t in new[] { "perfect", "real" } from canvas in new[] { "same", "crop" } from v in variants select (s, t, canvas, v)).ToList();
        var results = new ConcurrentDictionary<string, (RigResult R, CompileStage.Result C, double Scale)>();
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = 3 }, j =>
        {
            JitterScene scene = JitterScenes.Get(j.s);
            VideoInsertRegionTrack track = j.t == "perfect" ? RenderScenes.PerfectTrack(scene) : RenderScenes.RealTrack(scene);
            int fpsNum = (int)Math.Round(scene.Fps);
            bool crop = j.canvas == "crop";
            int cw = crop ? 1920 : scene.Width, ch = crop ? 1080 : scene.Height;
            CompileStage.Result s5 = CompileStage.Run("S5", track, scene.TruthAt, scene.Width, scene.Height, cw, ch, crop, 0.10, fpsNum, 1, scene.Frames / scene.Fps);
            long start = (long)Math.Round(s5.OutputStartSec * scene.Fps);
            var input = new RigInput(cw, ch, fpsNum, 1, scene.Frames, s5.Keyframes, s5.CanvasPx, "0xFFFFFF", null, "0x404040", (int)start);
            RigResult r = RenderRig.Run(input, j.v, RenderStage.White, runProd: false);
            results[$"{j.s}|{j.t}|{j.canvas}|{j.v.Id}"] = (r, s5, crop ? s5.Fit.Width / scene.Width : 1.0);
            WriteCsv(System.IO.Path.Combine(CsvDir, $"{j.s}-mag-{j.t}-{j.canvas}-{j.v.Id}.csv"), r.Meter, s5.IdealCanvasPx, scene.Fps);
            _output.WriteLine($"{j.s} {j.t} {j.canvas} {j.v.Id}: {r.Meter.Count}");
        });

        var sb = new StringBuilder();
        sb.AppendLine("## Output magnification: 1344x768 plate into a 1920x1080 cover crop (item 5)");
        sb.AppendLine();
        sb.AppendLine("`same` = the 1344x768 canvas at overscan 0.10; `crop` = the staging fit (`SourceCanvasFit.For(..., cover: true)`, x1.4286, 8.6 px vertical offset), same overscan. White insert on a flat base, integrating meter. Errors against the truth pushed through the same overscan and fit. Rows are in the units named: `out px` = 1920x1080 pixels, `src px` = out px / 1.4286. If a wobble scales linearly with magnification, the crop row's `out px` SlipRms is 1.4286 x the `same` row's.");
        sb.AppendLine();
        sb.AppendLine("Frames whose commanded quad leaves the canvas are excluded in every row (slow-push end frames in the crop).");
        sb.AppendLine();
        sb.AppendLine("| scene | track | variant | canvas | SlipRms (out px) | SlipRms (src px) | HfRms (out px) | ErrMean (out px) | ErrMax (out px) | crop/same SlipRms (out px ratio) |");
        sb.AppendLine("|---|---|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (string s in Scenes)
        {
            foreach (string t in new[] { "perfect", "real" })
            {
                foreach (RigVariant v in variants)
                {
                    double sameSlip = double.NaN;
                    foreach (string canvas in new[] { "same", "crop" })
                    {
                        var (r, c, scale) = results[$"{s}|{t}|{canvas}|{v.Id}"];
                        JitterScene scene = JitterScenes.Get(s);
                        // Frames whose commanded quad leaves the canvas (the cover crop trims the top and bottom; with 10% overscan the plate's
                        // edge sits off-screen at the end of slow-push) cannot be measured; they are dropped from every row alike.
                        int cwid = canvas == "crop" ? 1920 : scene.Width, chei = canvas == "crop" ? 1080 : scene.Height;
                        bool OnCanvas(Quad q) => Enumerable.Range(0, 4).All(k => q.Corner(k).X >= 3 && q.Corner(k).X <= cwid - 3 && q.Corner(k).Y >= 3 && q.Corner(k).Y <= chei - 3);
                        var keep = c.CanvasPx.Select((q, i) => (q, i)).Where(x => OnCanvas(x.q)).Select(x => (int)Math.Round(x.q.T * scene.Fps)).ToHashSet();
                        List<Quad> measured = r.Meter.Where(q => keep.Contains((int)Math.Round(q.T * scene.Fps))).ToList();
                        JitterStats st = JitterMetrics.Compute(measured, c.IdealCanvasPx, scene.Fps);
                        if (canvas == "same")
                            sameSlip = st.SlipRms;
                        string ratio = canvas == "crop" && sameSlip > 1e-6 ? (st.SlipRms / sameSlip).ToString("0.00", CultureInfo.InvariantCulture) : "-";
                        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                            $"| {s} | {t} | {v.Id} | {canvas} | {st.SlipRms:0.000} | {st.SlipRms / scale:0.000} | {st.HfRms:0.000} | {st.ErrMean:0.000} | {st.ErrMax:0.000} | {ratio} |"));
                    }
                }
            }
        }

        WriteSection("57-magnification", sb.ToString());
    }
}
