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
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.EdgeSignal;

/// <summary>Paths, scene list and scoring shared by the edge-signal bench parts.</summary>
internal static class EdgeSupport
{
    public static string OutDir => Environment.GetEnvironmentVariable("REELBOLT_EDGE_DIR") is { Length: > 0 } d ? d : "/tmp/insert-tracking/edge-signal";

    public static string ClipsDir => Path.Combine(OutDir, "clips");

    public static string SectionsDir => Path.Combine(OutDir, "sections");

    public static string CsvDir => Path.Combine(OutDir, "csv");

    /// <summary>The ten catalogue scenes plus two extra weak-luma-contrast variants of slow-push (the plate colour is the only difference).</summary>
    public static IReadOnlyList<JitterScene> Scenes { get; } = BuildScenes();

    public static readonly string[] ExtraSceneNames = ["slow-push-weakluma", "slow-push-isoluma"];

    private static IReadOnlyList<JitterScene> BuildScenes()
    {
        var all = JitterScenes.All.ToList();
        JitterScene push = JitterScenes.Get("slow-push");
        // Film A's staging plate RGB (61,190,81): BT.601 luma 134 against a background whose luma runs 57..149.
        all.Add(new JitterScene("slow-push-weakluma", push.Width, push.Height, push.Fps, push.Frames / push.Fps, (61, 190, 81), f => [push.TruthAt(f)]));
        // Luma equal to the background mean (~104): contrast changes sign across the picture, so luma helps nowhere reliably.
        all.Add(new JitterScene("slow-push-isoluma", push.Width, push.Height, push.Fps, push.Frames / push.Fps, (30, 150, 60), f => [push.TruthAt(f)]));
        return all;
    }

    public static IEnumerable<JitterScene> Selected()
    {
        string[] pick = (Environment.GetEnvironmentVariable("REELBOLT_EDGE_SCENES") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (JitterScene s in Scenes)
        {
            if (pick.Length == 0 || pick.Contains(s.Name))
                yield return s;
        }
    }

    public sealed record Cell(
        string Scene, string Candidate, string Stage, JitterStats Stats,
        double DecodeCpuSeconds, double RefitMs, double HiResMs, double HiResDecodeCpuSeconds,
        double LumaFraction, double MeanShift, double MeanAbsShift, long Tracks, double HiLumaFraction = double.NaN, double HiMeanShift = double.NaN);

    /// <summary>Runs every candidate on one scene. Each distinct decode runs once; returns S1' and S4 cells per candidate.</summary>
    public static List<Cell> RunScene(JitterScene scene, IReadOnlyList<Candidate> candidates, Action<string>? log = null)
    {
        RenderedClip clip = scene.Render(ClipsDir);
        VideoAnalyzeStepConfig cfg = BenchSupport.DefaultConfig;
        BenchSupport.GridPlan plan = BenchSupport.Plan(scene, cfg);
        ChromaQuadTracker.Options options = BenchSupport.ProductionOptions(cfg);
        var cells = new List<Cell>();
        byte[]? gray = candidates.Any(c => c.GrayLuma || c.HiRes == HiResKind.GrayGrid) ? EdgeDecode.DecodeGray(clip.Path, plan.SampleFps, plan.GridW, plan.GridH).Grid : null;
        foreach (IGrouping<string, Candidate> group in candidates.GroupBy(c => c.DecodeId))
        {
            DecodeResult dec = EdgeDecode.Decode(clip.Path, EdgeDecode.Get(group.Key), plan.SampleFps, plan.GridW, plan.GridH);
            int frames = dec.Grid.Length / (plan.GridW * plan.GridH * 3);
            foreach (Candidate cand in group)
            {
                EdgeRun run = EdgePipeline.Run(cand, dec.Grid, frames, plan.GridW, plan.GridH, plan.SampleFps, options, scene.Width, scene.Height, clip.Path, gray);
                foreach ((string stage, IEnumerable<ChromaQuadTracker.FrameQuad?> q) in new[]
                         {
                             ("S1", run.Stages.S1.AsEnumerable()),
                             ("S3", run.Stages.S3.Cast<ChromaQuadTracker.FrameQuad?>()),
                             ("S4", run.Stages.S4.Cast<ChromaQuadTracker.FrameQuad?>())
                         })
                {
                    List<Quad> quads = StageCapture.ToQuads(q, scene.Width, scene.Height, plan.SampleFps);
                    JitterStats stats = JitterMetrics.Compute(quads, clip.Truth, plan.SampleFps);
                    cells.Add(new Cell(
                        scene.Name, cand.Id, stage, stats, dec.CpuSeconds, run.RefitMs, run.HiResMs, run.HiResDecodeCpuSeconds,
                        run.S1Telemetry.LumaFraction, run.S1Telemetry.MeanShift, run.S1Telemetry.MeanAbsShift, run.Stages.RunCount,
                        run.HiResTelemetry.LumaFraction, run.HiResTelemetry.MeanShift));
                    if (stage == "S4")
                        BenchSupport.WriteCsv(Path.Combine(CsvDir, $"{scene.Name}-{cand.Id}-S4.csv"), quads, clip.Truth, plan.SampleFps);
                }

                log?.Invoke($"{scene.Name} {cand.Id}: S1 slip {cells[^3].Stats.SlipRms:0.000}, S4 slip {cells[^1].Stats.SlipRms:0.000}");
            }
        }

        return cells;
    }

    public static List<Cell> RunAll(IReadOnlyList<Candidate> candidates, int parallelism, Action<string>? log = null)
    {
        Directory.CreateDirectory(CsvDir);
        var bag = new ConcurrentBag<Cell>();
        Parallel.ForEach(Selected().ToList(), new ParallelOptions { MaxDegreeOfParallelism = parallelism }, scene =>
        {
            foreach (Cell c in RunScene(scene, candidates, log))
                bag.Add(c);
        });
        return bag.ToList();
    }

    // -------------------------------------------------------------------------------------------
    // Tables
    // -------------------------------------------------------------------------------------------

    private static string F(double v, string f = "0.000") => double.IsNaN(v) ? "-" : v.ToString(f, CultureInfo.InvariantCulture);

    private static string F(double? v, string f = "0.000") => v is null ? "-" : F(v.Value, f);

    private static IEnumerable<string> SceneOrder(IEnumerable<Cell> cells) =>
        Scenes.Select(s => s.Name).Where(n => cells.Any(c => c.Scene == n));

    /// <summary>Candidates x scenes of one metric at one stage, with the first candidate treated as the baseline.</summary>
    public static string MatrixTable(IReadOnlyList<Cell> cells, IReadOnlyList<Candidate> candidates, string stage, string metricName, Func<JitterStats, double> metric)
    {
        string[] scenes = SceneOrder(cells).ToArray();
        var sb = new StringBuilder();
        sb.AppendLine($"| {metricName} {stage} | " + string.Join(" | ", scenes) + " |");
        sb.AppendLine("|---|" + string.Join("|", scenes.Select(_ => "---:")) + "|");
        foreach (Candidate cand in candidates)
        {
            sb.Append($"| {cand.Id} |");
            foreach (string s in scenes)
            {
                Cell? c = cells.FirstOrDefault(x => x.Scene == s && x.Candidate == cand.Id && x.Stage == stage);
                sb.Append(' ').Append(c is null ? "." : F(metric(c.Stats))).Append(" |");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Scene rows x the ticket's six metrics for one candidate at one stage.</summary>
    public static string FullTable(IReadOnlyList<Cell> cells, string candidate, string stage)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Scene | N | SlipRms | HfRms | ErrMean | ErrMax | Overshoot | LagLS | ContentSlip |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (string s in SceneOrder(cells))
        {
            Cell? c = cells.FirstOrDefault(x => x.Scene == s && x.Candidate == candidate && x.Stage == stage);
            if (c is null)
                continue;
            JitterStats t = c.Stats;
            sb.AppendLine($"| {s} | {t.N} | {F(t.SlipRms)} | {F(t.HfRms)} | {F(t.ErrMean)} | {F(t.ErrMax)} | {F(t.Overshoot, "0.00")} | {F(t.LagLsFrames, "0.00")} | {F(t.ContentSlipRms)} |");
        }

        return sb.ToString();
    }

    /// <summary>The standard section: slip / hf / err matrices for both stages, then every candidate's full tables.</summary>
    public static string Section(string title, string intro, IReadOnlyList<Cell> cells, IReadOnlyList<Candidate> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## {title}");
        sb.AppendLine();
        sb.AppendLine(intro);
        sb.AppendLine();
        sb.AppendLine("Candidates:");
        foreach (Candidate c in candidates)
            sb.AppendLine($"- `{c.Id}`: {c.Label}");
        sb.AppendLine();
        sb.AppendLine(MatrixTable(cells, candidates, "S3", "SlipRms", s => s.SlipRms));
        foreach (string stage in new[] { "S1", "S4" })
        {
            sb.AppendLine(MatrixTable(cells, candidates, stage, "SlipRms", s => s.SlipRms));
            sb.AppendLine(MatrixTable(cells, candidates, stage, "HfRms", s => s.HfRms));
            sb.AppendLine(MatrixTable(cells, candidates, stage, "ErrMean", s => s.ErrMean));
            sb.AppendLine(MatrixTable(cells, candidates, stage, "ErrMax", s => s.ErrMax));
        }

        sb.AppendLine("### Per-candidate tables (S1 = per-frame fit before any smoothing; S4 = what ships, after outlier pass and Smooth)");
        sb.AppendLine();
        foreach (Candidate c in candidates)
        {
            foreach (string stage in new[] { "S1", "S4" })
            {
                sb.AppendLine($"#### `{c.Id}` - {stage}");
                sb.AppendLine();
                sb.AppendLine(FullTable(cells, c.Id, stage));
            }
        }

        return sb.ToString();
    }

    public static void WriteSection(string name, string markdown)
    {
        Directory.CreateDirectory(SectionsDir);
        File.WriteAllText(Path.Combine(SectionsDir, name + ".md"), markdown);
        var sb = new StringBuilder("# Edge-signal (T-003) detection-side measurements\n\n");
        foreach (string f in Directory.GetFiles(SectionsDir, "*.md").OrderBy(f => f, StringComparer.Ordinal))
            sb.AppendLine(File.ReadAllText(f)).AppendLine();
        File.WriteAllText(Path.Combine(OutDir, "report.md"), sb.ToString());
    }
}
