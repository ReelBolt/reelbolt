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
using Xunit;
using Xunit.Abstractions;
using static ReelBolt.WorkflowEngine.Tests.InsertJitter.BenchSupport;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter;

/// <summary>
/// The insert-tracking jitter benchmark (initiative insert-tracking-smoothness, T-002). Synthetic scenes with exact
/// ground truth are encoded through ffmpeg, tracked by the REAL tracker exactly as the analyze step does, and every
/// stage of the pipeline is scored. Gated by <see cref="JitterBenchFactAttribute"/> (never in CI). Parts can be
/// run separately; each writes a section of <c>report.md</c> and the report is re-assembled from whatever sections exist.
/// </summary>
public class JitterBenchmark
{
    private static readonly string[] SweepScenes = ["static-1344", "static-small", "static-flicker", "slow-push", "turn"];
    private static readonly string[] RenderScenes = ["static-1344", "slow-push"];

    private readonly ITestOutputHelper _output;
    private readonly object _logLock = new();

    public JitterBenchmark(ITestOutputHelper output) => _output = output;

    private void Log(string message)
    {
        lock (_logLock)
            _output.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private static ParallelOptions Parallelism(int n) => new() { MaxDegreeOfParallelism = n };

    // ---------------------------------------------------------------------------------------------
    // Part 1: S0..S6 per scene
    // ---------------------------------------------------------------------------------------------

    [JitterBenchFact]
    public void Stages_S0_to_S6_per_scene()
    {
        Directory.CreateDirectory(CsvDir);
        Parallel.ForEach(SelectedScenes().ToList(), Parallelism(6), scene =>
        {
            Log($"{scene.Name}: render");
            RenderedClip clip = scene.Render(ClipsDir);
            Log($"{scene.Name}: track");
            string md = RunScene(clip);
            WriteSection($"10-stages-{JitterScenes.All.ToList().FindIndex(s => s.Name == scene.Name):00}-{scene.Name}", md);
            Log($"{scene.Name}: done");
        });
    }

    private string RunScene(RenderedClip clip)
    {
        JitterScene scene = clip.Scene;
        VideoAnalyzeStepConfig cfg = DefaultConfig;
        GridPlan plan = Plan(scene, cfg);
        byte[] grid = SampleGrid(clip.Path, plan.SampleFps, plan.GridW, plan.GridH);
        int frames = grid.Length / (plan.GridW * plan.GridH * 3);
        ChromaQuadTracker.Options options = ProductionOptions(cfg);
        double fps = plan.SampleFps;

        // The shipped path: the real Track().
        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(grid, frames, plan.GridW, plan.GridH, fps, options, scene.Width, scene.Height);
        VideoInsertRegionTrack? track = Longest(tracks);

        // The replica, stage by stage.
        StageSet stages = StageCapture.Capture(grid, frames, plan.GridW, plan.GridH, fps, options, scene.Width, scene.Height, withS0: true);

        var sb = new StringBuilder();
        sb.AppendLine($"## {scene.Name}");
        sb.AppendLine();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"{scene.Width}x{scene.Height} @ {scene.Fps:0.###} fps, {scene.Frames} frames; tracker grid {plan.GridW}x{plan.GridH} @ {fps:0.###} fps ({frames} grid frames, ~{scene.Width / (double)plan.GridW:0.00}x{scene.Height / (double)plan.GridH:0.00} src px per grid px); " +
            $"{tracks.Count} track(s); growExcessFraction {stages.Tuning?.Parameters[ChromaTrackerTuner.GrowExcessFractionKey].ToString("0.###", CultureInfo.InvariantCulture) ?? "-"}; outlier-pass noise {stages.Noise:0.000} grid px; {stages.RunCount} assembled run(s)."));
        sb.AppendLine();

        if (track is null)
        {
            sb.AppendLine("**No track produced** - tracker found nothing to score.");
            return sb.ToString();
        }

        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Track {track.Id}: {track.StartSec:0.000}-{track.EndSec:0.000} s, confidence {track.Confidence:0.000}, motion {track.MotionClass}, {track.Keyframes.Count} keyframes."));
        sb.AppendLine();

        List<Quad> s4 = track.Keyframes.Select(k => Quad.FromKeyframe(k, scene.Width, scene.Height)).ToList();
        List<Quad> s4Corners = track.Keyframes.Select(k => Quad.FromKeyframe(k, scene.Width, scene.Height, preferSurface: false)).ToList();

        // Replica fidelity.
        List<Quad> replica = StageCapture.ToQuads(stages.S4.Cast<ChromaQuadTracker.FrameQuad?>(), scene.Width, scene.Height, fps);
        double replicaDiff = MaxDiff(replica, s4);
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Stage replica vs `Track()` output: max coordinate difference {replicaDiff:0.0000} px over {Math.Min(replica.Count, s4.Count)} keyframes (artifact rounds to 6 decimals; {replica.Count} replica vs {s4.Count} shipped keyframes)."));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"S4 `surfaceCorners` vs `x0..y3`: max difference {MaxDiff(s4, s4Corners):0.0000} px (the compile step pins to surfaceCorners)."));
        sb.AppendLine();

        var rows = new List<Row>();
        void Add(string label, IReadOnlyList<Quad> q, double rate, string? csv)
        {
            JitterStats st = JitterMetrics.Compute(q, clip.Truth, rate);
            rows.Add(new Row(label, st));
            if (csv is not null)
                WriteCsv(Path.Combine(CsvDir, $"{scene.Name}-{csv}.csv"), q, clip.Truth, rate);
        }

        if (stages.S0 is { Count: > 0 })
            Add("S0-coarse (integer extreme points)", stages.S0, fps, "S0-coarse");
        Add("S1-linefit (per-frame DetectQuads)", StageCapture.ToQuads(stages.S1, scene.Width, scene.Height, fps), fps, "S1-linefit");
        Add("S2a trim only (assembly + interpolation)", StageCapture.ToQuads(stages.S2a.Cast<ChromaQuadTracker.FrameQuad?>(), scene.Width, scene.Height, fps), fps, "S2a-trimmed");
        Add("S2-assembled (+ edge refiner)", StageCapture.ToQuads(stages.S2.Cast<ChromaQuadTracker.FrameQuad?>(), scene.Width, scene.Height, fps), fps, "S2-assembled");
        Add("S3-rejected (+ outlier rejection, upright)", StageCapture.ToQuads(stages.S3.Cast<ChromaQuadTracker.FrameQuad?>(), scene.Width, scene.Height, fps), fps, "S3-rejected");
        Add("S4-artifact (Track(), after Smooth) - ships", s4, fps, "S4-artifact");
        if (MaxDiff(s4, s4Corners) > 0.01)
            Add("S4 x0..y3 instead of surfaceCorners", s4Corners, fps, null);

        // S5: through the compile step.
        double dur = scene.Frames / scene.Fps;
        int fpsNum = (int)Math.Round(scene.Fps);
        Func<int, Quad> truthAt = scene.TruthAt;
        CompileStage.Result s5a = CompileStage.Run("S5a", track, truthAt, scene.Width, scene.Height, scene.Width, scene.Height, false, 0.02, fpsNum, 1, dur);
        Add("S5a-compile same-size, no cut, overscan 0.02", s5a.SourcePx, scene.Fps, null);
        // S5 rows score against the ideal-with-overscan, so replace the last row with the right truth.
        rows[^1] = new Row(rows[^1].Label, JitterMetrics.Compute(s5a.SourcePx, s5a.IdealSourcePx, scene.Fps));
        WriteCsv(Path.Combine(CsvDir, $"{scene.Name}-S5a-compile.csv"), s5a.SourcePx, s5a.IdealSourcePx, scene.Fps);

        CompileStage.Result s5b = CompileStage.Run("S5b", track, truthAt, scene.Width, scene.Height, 1920, 1080, true, 0.10, fpsNum, 1, dur);
        rows.Add(new Row("S5b-compile 1920x1080 Crop, matted overscan 0.10 (src px)", JitterMetrics.Compute(s5b.SourcePx, s5b.IdealSourcePx, scene.Fps)));
        rows.Add(new Row("S5b-compile (output px)", JitterMetrics.Compute(s5b.CanvasPx, s5b.IdealCanvasPx, scene.Fps)));
        WriteCsv(Path.Combine(CsvDir, $"{scene.Name}-S5b-compile.csv"), s5b.SourcePx, s5b.IdealSourcePx, scene.Fps);

        // S6: really render it, with a chroma-contrast insert (magenta) and a luma-contrast one (white).
        string s6note = string.Empty;
        if (RenderScenes.Contains(scene.Name))
        {
            CompileStage.Result perfect5 = CompileStage.Run("S5-ideal", PerfectTrack(scene), truthAt, scene.Width, scene.Height, scene.Width, scene.Height, false, 0.02, fpsNum, 1, dur);
            foreach ((RenderStage.InsertContent content, (byte, byte, byte) rgb) in new[] { (RenderStage.Magenta, ((byte)255, (byte)0, (byte)255)), (RenderStage.White, ((byte)255, (byte)255, (byte)255)) })
            {
                RenderStage.Result r = RenderStage.Run(clip.Path, scene.Width, scene.Height, s5a, scene.Frames, content);
                rows.Add(new Row($"S6-render [{content.Label} insert] ffmpeg perspective+overlay, re-detected, vs truth", JitterMetrics.Compute(r.Rendered, s5a.IdealSourcePx, scene.Fps)));
                rows.Add(new Row($"S6-render [{content.Label}] vs S5a expressions (what ffmpeg adds)", JitterMetrics.Compute(r.Rendered, s5a.SourcePx, scene.Fps)));
                WriteCsv(Path.Combine(CsvDir, $"{scene.Name}-S6-render-{content.Label}.csv"), r.Rendered, s5a.IdealSourcePx, scene.Fps);
                s6note += $" S6 {content.Label}: {r.Frames} frames in the insert window, {r.Failed} edge fits failed.";

                // Control: a PERFECT tracker (the truth itself as keyframes) through the same chain. What survives is the
                // filter chain's own quantization plus the re-detection's noise floor.
                RenderStage.Result perfect6 = RenderStage.Run(clip.Path, scene.Width, scene.Height, perfect5, scene.Frames, content);
                rows.Add(new Row($"S6-control [{content.Label}]: PERFECT tracker (truth keyframes) rendered, vs truth", JitterMetrics.Compute(perfect6.Rendered, perfect5.IdealSourcePx, scene.Fps)));
                WriteCsv(Path.Combine(CsvDir, $"{scene.Name}-S6-control-{content.Label}.csv"), perfect6.Rendered, perfect5.IdealSourcePx, scene.Fps);

                List<Quad> detector = RenderStage.DetectorControl(scene, content, rgb);
                rows.Add(new Row($"S6-detector control [{content.Label}]: analytic plate in the insert colour, same re-detection, no ffmpeg/encode, vs truth", JitterMetrics.Compute(detector, clip.Truth, scene.Fps)));
            }
        }

        sb.AppendLine(Table(rows));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"S5 notes: expression evaluator vs piecewise-linear definition max difference {Math.Max(s5a.AgreementMaxDiff, s5b.AgreementMaxDiff):0.0e+0} px; " +
            $"S5b fit offset ({s5b.Fit.OffsetX:0.#}, {s5b.Fit.OffsetY:0.#}), scale {s5b.Fit.Width / scene.Width:0.0000}; {s5a.Keyframes.Count} keyframes -> {(s5a.Keyframes.Count > ScreenInsertFilterBuilder.LinearChainMaxKeyframes ? "balanced tree" : "linear chain")} expressions. {s6note}"));
        sb.AppendLine();

        JitterStats notruth = JitterMetrics.ComputeNoTruth(s4, fps);
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"S4 without truth: ResidualRms {notruth.ResidualRms:0.000}, ResidualP95 {notruth.ResidualP95:0.000}, SecondDiffRms {notruth.SecondDiffRms:0.000}, P2P {notruth.PeakToPeak:0.00}."));
        return sb.ToString();
    }

    /// <summary>A track whose keyframes are the ground truth itself (one per frame, normalized), as a flawless tracker would emit.</summary>
    private static VideoInsertRegionTrack PerfectTrack(JitterScene scene)
    {
        double w = scene.Width, h = scene.Height;
        List<VideoInsertQuadKeyframe> kfs = scene.Truth().Select(q =>
            new VideoInsertQuadKeyframe(q.T, q.X0 / w, q.Y0 / h, q.X1 / w, q.Y1 / h, q.X2 / w, q.Y2 / h, q.X3 / w, q.Y3 / h)).ToList();
        return new VideoInsertRegionTrack("r0", null, 0, scene.Frames / scene.Fps, kfs, 1.0, 0.3, 1.7, "Static", "green");
    }

    private static double MaxDiff(IReadOnlyList<Quad> a, IReadOnlyList<Quad> b)
    {
        var byFrame = b.ToDictionary(q => Math.Round(q.T * 1000));
        double worst = 0;
        foreach (Quad q in a)
        {
            if (!byFrame.TryGetValue(Math.Round(q.T * 1000), out Quad o))
                continue;
            for (int c = 0; c < 4; c++)
            {
                worst = Math.Max(worst, Math.Abs(q.Corner(c).X - o.Corner(c).X));
                worst = Math.Max(worst, Math.Abs(q.Corner(c).Y - o.Corner(c).Y));
            }
        }

        return worst;
    }

    // ---------------------------------------------------------------------------------------------
    // Part 2: oracle and option sweeps (S4 metrics)
    // ---------------------------------------------------------------------------------------------

    private sealed record Variant(string Label, string Id, RenderMode Mode, int? GridW, int? GridH, double? SampleFps, Func<ChromaQuadTracker.Options, ChromaQuadTracker.Options>? Tweak);

    [JitterBenchFact]
    public void Oracle_and_option_sweeps_on_S4()
    {
        Directory.CreateDirectory(CsvDir);
        var variants = new List<Variant>
        {
            new("baseline (defaults)", "base", RenderMode.Standard, null, null, null, null),
            new("o1 no sensor noise, lossless RGB encode", "o1", RenderMode.NoiselessLossless, null, null, null, null),
            new("o1b no sensor noise, standard crf20 yuv420p encode (codec only)", "o1b", RenderMode.NoiselessStandardEncode, null, null, null, null),
            new("o1c sensor noise sigma 2, lossless RGB encode (noise only)", "o1c", RenderMode.NoisyLossless, null, null, null, null),
            new("o1d no sensor noise, lossless 4:2:0 encode (chroma subsampling only)", "o1d", RenderMode.NoiselessLosslessYuv420, null, null, null, null),
            new("o2 grid 320x(aspect)", "o2-320", RenderMode.Standard, 320, null, null, null),
            new("o2 grid 480x(aspect)", "o2-480", RenderMode.Standard, 480, null, null, null),
            new("o2 grid 640 (630x360, height cap)", "o2-640", RenderMode.Standard, 630, 360, null, null),
            new("o4 RefineEdgesFromNeighbours=false", "o4", RenderMode.Standard, null, null, null, o => o with { RefineEdgesFromNeighbours = false }),
            new("o5 sample fps = half the source fps", "o5", RenderMode.Standard, null, null, -1, null)
        };

        var work = SelectedScenes(SweepScenes).SelectMany(s => variants.Select(v => (Scene: s, Variant: v))).ToList();
        var results = new ConcurrentDictionary<(string, string), JitterStats>();
        var notes = new ConcurrentDictionary<(string, string), string>();
        Parallel.ForEach(work, Parallelism(10), w =>
        {
            JitterScene scene = w.Scene;
            Variant v = w.Variant;
            RenderedClip clip = scene.Render(ClipsDir, v.Mode);
            try
            {
                VideoAnalyzeStepConfig cfg = DefaultConfig with
                {
                    InsertGridWidth = v.GridW is { } gw ? gw : 0,
                    InsertGridHeight = v.GridW is null ? 0 : v.GridH ?? (int)Math.Round(v.GridW.Value * scene.Height / (double)scene.Width),
                    InsertSampleFps = v.SampleFps is null ? 0 : scene.Fps / 2
                };
                GridPlan plan = Plan(scene, cfg);
                byte[] grid = SampleGrid(clip.Path, plan.SampleFps, plan.GridW, plan.GridH);
                int frames = grid.Length / (plan.GridW * plan.GridH * 3);
                ChromaQuadTracker.Options options = ProductionOptions(cfg);
                if (v.Tweak is not null)
                    options = v.Tweak(options);
                VideoInsertRegionTrack? t = Longest(ChromaQuadTracker.Track(grid, frames, plan.GridW, plan.GridH, plan.SampleFps, options, scene.Width, scene.Height));
                if (t is null)
                {
                    notes[(scene.Name, v.Id)] = "no track";
                    results[(scene.Name, v.Id)] = JitterMetrics.Compute([], clip.Truth, plan.SampleFps);
                    return;
                }

                List<Quad> q = t.Keyframes.Select(k => Quad.FromKeyframe(k, scene.Width, scene.Height)).ToList();
                results[(scene.Name, v.Id)] = JitterMetrics.Compute(q, clip.Truth, plan.SampleFps);
                notes[(scene.Name, v.Id)] = $"grid {plan.GridW}x{plan.GridH} @ {plan.SampleFps:0.##}";
                WriteCsv(Path.Combine(CsvDir, $"{scene.Name}-sweep-{v.Id}.csv"), q, clip.Truth, plan.SampleFps);
                Log($"{scene.Name} {v.Id}: slip {results[(scene.Name, v.Id)].SlipRms:0.000}");
            }
            finally
            {
                // The noisy lossless oracle is ~360 MB per clip; keep the report, not the clip.
                if (v.Mode == RenderMode.NoisyLossless)
                {
                    File.Delete(clip.Path);
                    File.Delete(clip.Path + ".stamp");
                }
            }
        });

        foreach (JitterScene scene in SelectedScenes(SweepScenes))
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## {scene.Name} - oracle and option sweeps (S4 = what ships)");
            sb.AppendLine();
            sb.AppendLine(Table(variants.Where(v => results.ContainsKey((scene.Name, v.Id)))
                .Select(v => new Row($"{v.Label} [{notes[(scene.Name, v.Id)]}]", results[(scene.Name, v.Id)]))));
            WriteSection($"20-sweeps-{Array.IndexOf(SweepScenes, scene.Name):00}-{scene.Name}", sb.ToString());
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Part 3: real staging tracks vs the synthetic S4 tracks
    // ---------------------------------------------------------------------------------------------

    [JitterBenchFact]
    public void Real_staging_tracks_vs_synthetic()
    {
        string promo = Environment.GetEnvironmentVariable("REELBOLT_JITTER_PROMO") is { Length: > 0 } p ? p : "/tmp/insert-tracking/promo";
        var rows = new List<(string, JitterStats)>();
        var sb = new StringBuilder();
        sb.AppendLine("## Real staging tracks (no truth) vs synthetic S4");
        sb.AppendLine();
        foreach ((string label, string pattern) in new[] { ("Film A (65fe46f0, motion Slow, 362 kf)", "artifact-65fe46f0-*.json"), ("Film B (c712a68f, labelled Static, 243 kf)", "artifact-c712a68f-*.json") })
        {
            string file = Directory.GetFiles(promo, pattern).FirstOrDefault()
                ?? throw new FileNotFoundException($"{pattern} not found in {promo}");
            (List<Quad> surface, List<Quad> corners, int w, int h) = LoadArtifact(file);
            rows.Add(($"{label} - surfaceCorners ({w}x{h})", JitterMetrics.ComputeNoTruth(surface, 24)));
            rows.Add(($"{label} - x0..y3", JitterMetrics.ComputeNoTruth(corners, 24)));
        }

        foreach (JitterScene scene in JitterScenes.All)
        {
            string csv = Path.Combine(CsvDir, $"{scene.Name}-S4-artifact.csv");
            if (!File.Exists(csv))
                continue;
            List<Quad> q = ReadCsvTrack(csv);
            rows.Add(($"synthetic {scene.Name} S4", JitterMetrics.ComputeNoTruth(q, scene.Fps)));
        }

        sb.AppendLine(NoTruthTable(rows));
        sb.AppendLine("ResidualRms/P95: distance of each corner from a quadratic through its 8 neighbours (+-4 frames, leave-one-out, 2-D, source px; the real-clip harness' jitter). SecondDiffRms: RMS |x(t+1) - 2x(t) + x(t-1)|. Synthetic rows read the S4 csv written by the stages part.");
        WriteSection("30-real", sb.ToString());
    }
}
