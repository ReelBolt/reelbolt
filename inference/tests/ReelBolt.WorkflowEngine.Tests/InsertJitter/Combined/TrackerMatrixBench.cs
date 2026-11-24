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
using ReelBolt.WorkflowEngine.Tests.InsertJitter.EdgeSignal;
using Xunit;
using Xunit.Abstractions;
using static ReelBolt.WorkflowEngine.Tests.InsertJitter.Combined.CombinedSupport;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Combined;

/// <summary>
/// T-006 part 1: the five tracker configurations (C0 baseline, C1 E1b, C2 smoothing stack, C3 both, C3a cheap decode + smoothing) on the twelve
/// scenes, scored against the exact truth at the S4-equivalent stage (the keyframes that ship), plus the sigma-prior check and the regression list.
/// Writes <c>csv/&lt;scene&gt;-&lt;config&gt;.csv</c> (the tracks the render part reads) and report sections. Rerun from <c>inference/</c>:
/// <c>REELBOLT_JITTER_BENCH=1 dotnet test tests/ReelBolt.WorkflowEngine.Tests --filter "FullyQualifiedName~InsertJitter.Combined"</c>.
/// </summary>
public class TrackerMatrixBench
{
    private static readonly double[] SigmaFractions = [0.03, 0.05, 0.07, 0.10, 0.14, 0.20];

    private readonly ITestOutputHelper _output;
    private readonly object _lock = new();

    public TrackerMatrixBench(ITestOutputHelper output) => _output = output;

    private void Log(string m)
    {
        lock (_lock)
            _output.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}");
    }

    private static int Par => int.TryParse(Environment.GetEnvironmentVariable("REELBOLT_COMBINED_PARALLEL"), out int p) && p > 0 ? p : 3;

    internal static List<CombinedResult> RunScene(JitterScene scene, Action<string>? log)
    {
        RenderedClip clip = scene.Render(EdgeSupport.ClipsDir);
        BenchSupport.GridPlan plan = BenchSupport.Plan(scene, BenchSupport.DefaultConfig);
        var results = new List<CombinedResult>();
        foreach (IGrouping<string, CombinedConfig> group in CombinedConfigs.All.GroupBy(c => c.DecodeId))
        {
            DecodeResult dec = EdgeDecode.Decode(clip.Path, EdgeDecode.Get(group.Key), plan.SampleFps, plan.GridW, plan.GridH);
            EdgeRun run = CombinedPipeline.RunStages(scene, dec.Grid, plan, clip.Path);
            foreach (CombinedConfig cfg in group)
            {
                results.Add(Score(scene, clip, plan, cfg, run, dec.Grid));
                log?.Invoke($"{scene.Name} {cfg.Id}: slip {results[^1].Stats.SlipRms:0.000} errMax {results[^1].Stats.ErrMax:0.000}");
                // The sigma prior sweep: only the smoothing configs depend on it.
                if (cfg.Smoothing)
                {
                    foreach (double g in SigmaFractions)
                        results.Add(Score(scene, clip, plan, cfg with { Id = $"{cfg.Id}@g{g.ToString("0.##", CultureInfo.InvariantCulture)}", SigmaGridFraction = g }, run, dec.Grid));
                }
            }
        }

        return results;
    }

    private static CombinedResult Score(JitterScene scene, RenderedClip clip, BenchSupport.GridPlan plan, CombinedConfig cfg, EdgeRun run, byte[] grid)
    {
        List<Quad> track;
        int rejected = 0, held = 0;
        WidthDiag? width = null;
        double postMs = 0, actual = double.NaN;
        if (cfg.Smoothing)
        {
            (track, rejected, held, width, postMs, actual) = CombinedPipeline.SmoothChain(scene, run.Stages, grid, plan, cfg.SigmaGridFraction, clip.Truth);
        }
        else
        {
            track = StageCapture.ToQuads(run.Stages.S4.Cast<ChromaQuadTracker.FrameQuad?>(), scene.Width, scene.Height, plan.SampleFps);
        }

        JitterStats stats = JitterMetrics.Compute(track, clip.Truth, plan.SampleFps);
        if (!cfg.Id.Contains('@', StringComparison.Ordinal))
            BenchSupport.WriteCsv(CsvPath(scene.Name, cfg.Id), track, clip.Truth, plan.SampleFps);
        return new CombinedResult(cfg, scene.Name, track, stats, run.Stages.RunCount, rejected, held, width, postMs, actual);
    }

    [JitterBenchFact]
    public void Tracker_matrix_and_sigma_prior()
    {
        Directory.CreateDirectory(CsvDir);
        var bag = new ConcurrentBag<CombinedResult>();
        Parallel.ForEach(Selected().ToList(), new ParallelOptions { MaxDegreeOfParallelism = Par }, scene =>
        {
            foreach (CombinedResult r in RunScene(scene, Log))
                bag.Add(r);
        });
        List<CombinedResult> all = bag.ToList();
        string[] ids = CombinedConfigs.All.Select(c => c.Id).ToArray();
        List<CombinedResult> main = all.Where(r => ids.Contains(r.Config.Id)).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("## 1. Tracker matrix: C0 / C1 / C2 / C3 / C3a on the 12 scenes (S4-equivalent = the keyframes that ship)");
        sb.AppendLine();
        sb.AppendLine("Units: source px (1344x768 scenes, 1920x1080 for static-1080p); Overshoot in px; LagLS in frames. Scored against the exact truth. Clips are T-003's (the ten catalogue scenes plus `slow-push-weakluma` = Film A's plate colour and `slow-push-isoluma`).");
        sb.AppendLine();
        sb.AppendLine("- `C0` baseline: shipped decode (`fps,scale=area,format=rgb24`), shipped `RejectImplausibleSamples` + `Smooth`.");
        sb.AppendLine("- `C1`: decode `scale=iw:ih:flags=bilinear+full_chroma_inp,format=yuv444p,scale=W:H:flags=area+full_chroma_int,format=rgb24` (E1b), everything else shipped.");
        sb.AppendLine("- `C2`: shipped decode; hinge outlier rejection (T-004 `RejectPrototype.Mode.Hinge`) then K4ra behind the cadence wrapper (`CadenceSmoother`, held frames detected from the config's own grid and S3).");
        sb.AppendLine("- `C3`: C1 decode + C2 post-processing. `C3a`: E1a decode `scale=W:H:flags=area+full_chroma_int` + C2 post-processing.");
        sb.AppendLine();
        foreach (Metric m in Metrics)
        {
            sb.AppendLine(MatrixTable(main, ids, m));
        }

        sb.AppendLine("### Per-config full tables");
        sb.AppendLine();
        foreach (string id in ids)
        {
            sb.AppendLine($"#### {id}: {CombinedConfigs.All.First(c => c.Id == id).Label}");
            sb.AppendLine();
            sb.AppendLine(FullTable(main, id));
        }

        sb.AppendLine(DiagnosticsTable(main));
        WriteSection("10-tracker-matrix", sb.ToString());

        WriteSection("20-regressions", RegressionSection(main));
        WriteSection("30-sigma-prior", SigmaSection(all));
        WriteSection("05-validation", ValidationSection(main));
    }

    // -------------------------------------------------------------------------------------------

    private static string DiagnosticsTable(IReadOnlyList<CombinedResult> main)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### Per-scene tracker diagnostics (smoothing configs)");
        sb.AppendLine();
        sb.AppendLine("`rej` = detections the hinge outlier pass removed; `held` = frames the cadence detector flagged; `runs` = assembled runs (the longest is scored).");
        sb.AppendLine();
        sb.AppendLine("| Scene | C2 rej | C2 held | C3 rej | C3 held | C3a rej | C3a held | runs C0 | runs C3 |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (JitterScene sc in Scenes)
        {
            CombinedResult? Get(string id) => main.FirstOrDefault(r => r.Scene == sc.Name && r.Config.Id == id);
            if (Get("C0") is null)
                continue;
            sb.AppendLine($"| {sc.Name} | {Get("C2")?.Rejected} | {Get("C2")?.HeldFrames} | {Get("C3")?.Rejected} | {Get("C3")?.HeldFrames} | {Get("C3a")?.Rejected} | {Get("C3a")?.HeldFrames} | {Get("C0")?.RunCount} | {Get("C3")?.RunCount} |");
        }

        return sb.ToString();
    }

    /// <summary>Every (scene, metric) cell where the candidate is worse than C0 by more than 5 %.</summary>
    private static string RegressionSection(IReadOnlyList<CombinedResult> main)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 2. Regression cells: candidate worse than C0 by more than 5 % on any metric");
        sb.AppendLine();
        sb.AppendLine("Every (scene, metric) cell, lower is better; for LagLS the absolute value is compared. `delta` is in the metric's unit (px, or frames for LagLS). A cell is marked `immaterial` when the absolute change is under 0.02 (px or frames), i.e. both values are below any visible threshold; those are listed too so nothing is hidden. Metrics with no value (`-`) are skipped.");
        sb.AppendLine();
        foreach (string id in new[] { "C3", "C3a", "C1", "C2" })
        {
            sb.AppendLine($"### {id} vs C0");
            sb.AppendLine();
            sb.AppendLine("| Scene | metric | C0 | " + id + " | delta | change | note |");
            sb.AppendLine("|---|---|---:|---:|---:|---:|---|");
            int material = 0, immaterial = 0;
            foreach (JitterScene sc in Scenes)
            {
                CombinedResult? b = main.FirstOrDefault(r => r.Scene == sc.Name && r.Config.Id == "C0");
                CombinedResult? c = main.FirstOrDefault(r => r.Scene == sc.Name && r.Config.Id == id);
                if (b is null || c is null)
                    continue;
                foreach (Metric m in Metrics)
                {
                    double? x = m.Get(b.Stats), y = m.Get(c.Stats);
                    if (x is null || y is null || double.IsNaN(x.Value) || double.IsNaN(y.Value))
                        continue;
                    double bv = m.Name == "LagLS" ? Math.Abs(x.Value) : x.Value, cv = m.Name == "LagLS" ? Math.Abs(y.Value) : y.Value;
                    if (cv <= bv * 1.05 + 1e-9)
                        continue;
                    double delta = cv - bv;
                    bool imm = delta < 0.02;
                    if (imm)
                        immaterial++;
                    else
                        material++;
                    string pct = bv > 1e-9 ? ((cv / bv - 1) * 100).ToString("+0;-0", CultureInfo.InvariantCulture) + " %" : "n/a";
                    sb.AppendLine($"| {sc.Name} | {m.Name} | {F(bv, m.Format)} | {F(cv, m.Format)} | {F(delta, "+0.000")} | {pct} | {(imm ? "immaterial" : "MATERIAL")} |");
                }
            }

            sb.AppendLine();
            sb.AppendLine($"{id}: {material} material and {immaterial} immaterial regression cells (of {Scenes.Count * Metrics.Length} scene x metric cells).");
            sb.AppendLine();
        }

        sb.AppendLine("### Stack interactions: C3 against its own components");
        sb.AppendLine();
        sb.AppendLine("Cells where C3 is more than 5 % worse than the better of C1 (decode alone) and C2 (smoothing alone), with at least 0.02 absolute change: the places where stacking the two fixes costs something compared with applying only one.");
        sb.AppendLine();
        sb.AppendLine("| Scene | metric | C0 | C1 | C2 | C3 | note |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---|");
        int interactions = 0;
        foreach (JitterScene sc in Scenes)
        {
            CombinedResult? c0 = main.FirstOrDefault(r => r.Scene == sc.Name && r.Config.Id == "C0");
            CombinedResult? c1 = main.FirstOrDefault(r => r.Scene == sc.Name && r.Config.Id == "C1");
            CombinedResult? c2 = main.FirstOrDefault(r => r.Scene == sc.Name && r.Config.Id == "C2");
            CombinedResult? c3 = main.FirstOrDefault(r => r.Scene == sc.Name && r.Config.Id == "C3");
            if (c0 is null || c1 is null || c2 is null || c3 is null)
                continue;
            foreach (Metric m in Metrics.Where(m => m.Name is not ("LagLS" or "Overshoot")))
            {
                double a = m.Get(c0.Stats) ?? double.NaN, b = m.Get(c1.Stats) ?? double.NaN, c = m.Get(c2.Stats) ?? double.NaN, d = m.Get(c3.Stats) ?? double.NaN;
                double best = Math.Min(b, c);
                if (double.IsNaN(best) || double.IsNaN(d) || d <= best * 1.05 + 1e-9 || d - best < 0.02)
                    continue;
                interactions++;
                string note = d <= a ? "still better than C0" : "WORSE than C0";
                sb.AppendLine($"| {sc.Name} | {m.Name} | {F(a)} | {F(b)} | {F(c)} | {F(d)} | {note} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"{interactions} interaction cells.");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string SigmaSection(IReadOnlyList<CombinedResult> all)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 3. Does K4ra's sigma prior (0.10 x source px per grid px) still behave on the E1b decode?");
        sb.AppendLine();
        sb.AppendLine("K4ra tests window widths H in {4, 6, 8, 12} frames and takes the widest whose robust residual RMS is within tau = 2 sigma; sigma = max(0.05 px, the track's own third-difference sigma, prior). The prior is `0.10 x (source px per detection-grid px)` = 0.28 px at 1344/480 (0.40 px at 1920/480). `actual` = the detrended (third-difference) per-coordinate sigma of the smoother's INPUT (the hinge-rejected S3) against the truth, i.e. what sigma would be if it were known. `mean H` = mean chosen half-width (frames; raw samples count as 0); `raw%` = share of samples passed through unsmoothed; `H12%` = share at the widest window.");
        sb.AppendLine();
        sb.AppendLine("| Scene | sigma used (px) | C2 actual | C3 actual | C2 mean H | C3 mean H | C3a mean H | C2 raw% | C3 raw% | C2 H12% | C3 H12% |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (JitterScene sc in Scenes)
        {
            CombinedResult? c2 = all.FirstOrDefault(r => r.Scene == sc.Name && r.Config.Id == "C2");
            CombinedResult? c3 = all.FirstOrDefault(r => r.Scene == sc.Name && r.Config.Id == "C3");
            CombinedResult? c3a = all.FirstOrDefault(r => r.Scene == sc.Name && r.Config.Id == "C3a");
            if (c3?.Width is null || c2?.Width is null)
                continue;
            double H12(WidthDiag w) => w.Samples == 0 ? 0 : 100.0 * w.WidthCounts[Array.IndexOf(w.Widths, 12)] / w.Samples;
            sb.AppendLine($"| {sc.Name} | {F(c3.Width.SigmaSrc)} | {F(c2.ActualErrSigma)} | {F(c3.ActualErrSigma)} | {F(c2.Width.MeanWidth, "0.00")} | {F(c3.Width.MeanWidth, "0.00")} | {F(c3a?.Width?.MeanWidth ?? double.NaN, "0.00")} | {F(100 * c2.Width.RawFraction, "0.0")} | {F(100 * c3.Width.RawFraction, "0.0")} | {F(H12(c2.Width), "0.0")} | {F(H12(c3.Width), "0.0")} |");
        }

        sb.AppendLine();
        sb.AppendLine("Width counts per half-width (C3, samples at H = 0 (raw), 4, 6, 8, 12) and the sigma components (C3):");
        sb.AppendLine();
        sb.AppendLine("| Scene | H=0 | H=4 | H=6 | H=8 | H=12 | prior floor | own 3rd-diff sigma | used |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (JitterScene sc in Scenes)
        {
            CombinedResult? c3 = all.FirstOrDefault(r => r.Scene == sc.Name && r.Config.Id == "C3");
            if (c3?.Width is not { } w)
                continue;
            sb.AppendLine($"| {sc.Name} | {w.WidthCounts[0]} | {w.WidthCounts[1]} | {w.WidthCounts[2]} | {w.WidthCounts[3]} | {w.WidthCounts[4]} | {F(w.SigmaPriorFloor)} | {F(w.SigmaOwn)} | {F(w.SigmaSrc)} |");
        }

        sb.AppendLine();
        sb.AppendLine("### Sweep of the prior fraction g (sigma floor = g x source px per grid px), C3 (E1b decode) and C2 (shipped decode)");
        sb.AppendLine();
        sb.AppendLine("g = 0.10 is the T-004 recommendation. SlipRms and ErrMax (px) per scene; mean H in the last columns of the first table.");
        sb.AppendLine();
        foreach (string baseId in new[] { "C3", "C2" })
        {
            var ids = new List<string> { "C0" };
            ids.AddRange(SigmaFractions.Select(g => $"{baseId}@g{g.ToString("0.##", CultureInfo.InvariantCulture)}"));
            foreach (Metric m in Metrics.Where(m => m.Name is "SlipRms" or "ErrMax" or "ErrMean" or "HfRms"))
                sb.AppendLine(MatrixTable(all, ids, m));
            sb.AppendLine($"Mean chosen half-width (frames) for {baseId} by g:");
            sb.AppendLine();
            string[] scenes = Scenes.Select(s => s.Name).Where(n => all.Any(r => r.Scene == n)).ToArray();
            sb.AppendLine("| g | " + string.Join(" | ", scenes) + " |");
            sb.AppendLine("|---|" + string.Concat(scenes.Select(_ => "---:|")));
            foreach (double g in SigmaFractions)
            {
                string id = $"{baseId}@g{g.ToString("0.##", CultureInfo.InvariantCulture)}";
                sb.AppendLine($"| {g:0.00} | " + string.Join(" | ", scenes.Select(s => F(all.FirstOrDefault(r => r.Scene == s && r.Config.Id == id)?.Width?.MeanWidth ?? double.NaN, "0.00"))) + " |");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string ValidationSection(IReadOnlyList<CombinedResult> main)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 0. Validation: the glue reproduces the earlier tickets' numbers");
        sb.AppendLine();
        sb.AppendLine("`C0` vs the T-002 baseline bench's S4 csv (`/tmp/insert-tracking/bench/csv/<scene>-S4-artifact.csv`, 4-decimal csv; max coordinate difference, source px). The csv only exists for the ten catalogue scenes.");
        sb.AppendLine();
        sb.AppendLine("| Scene | frames C0 | frames bench | max coordinate difference (px) |");
        sb.AppendLine("|---|---:|---:|---:|");
        foreach (JitterScene sc in Scenes)
        {
            string csv = Path.Combine(RenderPath.RenderScenes.BaselineDir, "csv", sc.Name + "-S4-artifact.csv");
            CombinedResult? c0 = main.FirstOrDefault(r => r.Scene == sc.Name && r.Config.Id == "C0");
            if (c0 is null || !File.Exists(csv))
                continue;
            List<Quad> bench = BenchSupport.ReadCsvTrack(csv);
            double max = double.NaN;
            if (bench.Count == c0.Track.Count)
            {
                max = 0;
                for (int i = 0; i < bench.Count; i++)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        max = Math.Max(max, Math.Abs(bench[i].Corner(c).X - c0.Track[i].Corner(c).X));
                        max = Math.Max(max, Math.Abs(bench[i].Corner(c).Y - c0.Track[i].Corner(c).Y));
                    }
                }
            }

            sb.AppendLine($"| {sc.Name} | {c0.Track.Count} | {bench.Count} | {F(max, "0.0000")} |");
        }

        sb.AppendLine();
        sb.AppendLine("`C1` should equal T-003's `dec-e1b-bilinear-fast` S4 row and `C2` should be close to T-004's recommendation row (`F-cad-K4ra` with the hinge pass): compare the SlipRms/ErrMax rows of sections 1 against `/tmp/insert-tracking/edge-signal/report.md` (E1 flag ablation) and `/tmp/insert-tracking/smoothers/report.md` (Baseline vs recommendation). Differences in C2 are expected to be small: here the held-frame detector runs on the hinge-rejected S3 of this config instead of the production S3 cached by T-004.");
        return sb.ToString();
    }
}
