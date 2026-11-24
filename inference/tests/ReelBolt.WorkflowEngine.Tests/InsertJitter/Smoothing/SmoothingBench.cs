#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using FrameQuad = ReelBolt.WorkflowEngine.Services.Video.ChromaQuadTracker.FrameQuad;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Smoothing;

/// <summary>
/// T-004 (insert-tracking-smoothness): temporal smoother candidates against the T-002 benchmark's scenes.
/// Part 1 (<see cref="Capture_scene_caches"/>) runs the real tracker stage by stage once per scene (needs ffmpeg and the
/// clips) and caches S2/S3/noise/held flags/truth as JSON; part 2 (<see cref="Sweep_smoothers"/>) is pure C# on the caches.
/// </summary>
public class SmoothingBench
{
    public static string OutDir => Environment.GetEnvironmentVariable("REELBOLT_SMOOTH_DIR") is { Length: > 0 } d ? d : "/tmp/insert-tracking/smoothers";
    public static string CacheDir => Path.Combine(OutDir, "cache");

    private readonly ITestOutputHelper _output;

    public SmoothingBench(ITestOutputHelper output) => _output = output;

    [JitterBenchFact]
    public void Capture_scene_caches()
    {
        Parallel.ForEach(SelectedScenes().ToList(), new ParallelOptions { MaxDegreeOfParallelism = 3 }, scene =>
        {
            RenderedClip clip = scene.Render(ClipsDir);
            VideoAnalyzeStepConfig cfg = DefaultConfig;
            GridPlan plan = Plan(scene, cfg);
            byte[] grid = SampleGrid(clip.Path, plan.SampleFps, plan.GridW, plan.GridH);
            int frames = grid.Length / (plan.GridW * plan.GridH * 3);
            ChromaQuadTracker.Options options = ProductionOptions(cfg);
            StageSet stages = StageCapture.Capture(grid, frames, plan.GridW, plan.GridH, plan.SampleFps, options, scene.Width, scene.Height, withS0: false);
            var data = new SceneData
            {
                Name = scene.Name,
                W = scene.Width,
                H = scene.Height,
                Fps = plan.SampleFps,
                Gw = plan.GridW,
                Gh = plan.GridH,
                NoiseGrid = stages.Noise,
                S2 = stages.S2.Select(SceneData.ToDto).ToList(),
                S3 = stages.S3.Select(SceneData.ToDto).ToList(),
                Truth = clip.Truth.Select(q => new[] { q.T, q.X0, q.Y0, q.X1, q.Y1, q.X2, q.Y2, q.X3, q.Y3 }).ToList()
            };
            HeldFrameDetector.MeasureEdgeDiff(grid, frames, plan.GridW, plan.GridH, stages.S3, out double[] edge, out double[] noiseDiff);
            data.EdgeDiff = edge;
            data.Held = HeldFrameDetector.FromEdgeDiff(edge);
            data.NoiseDiff = noiseDiff;
            data.Save(CacheDir);
            lock (_output)
                _output.WriteLine($"{scene.Name}: {data.S3.Count} S3 frames, noise {stages.Noise:0.000} grid px, held frames {data.Held.Count(h => h)}/{frames}");
        });
    }

    private static IEnumerable<Candidate> AllCandidates() =>
        Candidates.Baselines().Concat(Candidates.K1()).Concat(Candidates.K2()).Concat(Candidates.K3()).Concat(Candidates.K4()).Concat(Candidates.K4Raw()).Concat(Candidates.K5()).Concat(Candidates.K6()).Concat(Candidates.Cadence()).Concat(Candidates.Final());

    /// <summary>Runs every candidate on the cached scenes and writes <c>summary.md</c> and <c>matrix.md</c> under the output directory.</summary>
    [JitterBenchFact]
    public void Sweep_smoothers()
    {
        RejectPrototype.Mode? reject = Environment.GetEnvironmentVariable("REELBOLT_SMOOTH_REJECT") switch { "both" => RejectPrototype.Mode.BothSides, "chord" => RejectPrototype.Mode.Chord, "hinge" => RejectPrototype.Mode.Hinge, _ => null };
        var data = SweepRunner.LoadAll(CacheDir, reject);
        string only = Environment.GetEnvironmentVariable("REELBOLT_SMOOTH_ONLY") ?? "";
        var list = AllCandidates().Where(c => only.Length == 0 || only.Split(',').Any(o => c.Id.StartsWith(o, StringComparison.Ordinal)) || c.Id == "K0").ToList();
        var results = new SweepRunner.CandidateResult[list.Count];
        for (int i = 0; i < list.Count; i++)
            results[i] = SweepRunner.Evaluate(list[i], data);
        SweepRunner.CandidateResult baseline = results.First(r => r.Candidate.Id == "K0");

        var summary = new StringBuilder();
        summary.AppendLine(SweepRunner.SummaryHeader());
        var matrix = new StringBuilder();
        foreach (SweepRunner.CandidateResult r in results)
        {
            summary.AppendLine(SweepRunner.SummaryRow(r, baseline));
            matrix.AppendLine(SweepRunner.Matrix(r));
        }

        Directory.CreateDirectory(OutDir);
        string tag = (only.Length == 0 ? "all" : only) + (reject is null ? "" : "-reject" + reject);
        File.WriteAllText(Path.Combine(OutDir, $"summary-{tag}.md"), summary.ToString());
        File.WriteAllText(Path.Combine(OutDir, $"matrix-{tag}.md"), matrix.ToString());
        _output.WriteLine(summary.ToString());
    }

    /// <summary>Per scene: the outlier pass' noise, the track's own third-difference sigma, and the actual per-coordinate error statistics of S3 against truth.</summary>
    [JitterBenchFact]
    public void Noise_estimates_vs_actual_error()
    {
        var data = SweepRunner.LoadAll(CacheDir);
        var sb = new StringBuilder("| Scene | noise src px (outlier pass) | own sigma3 | err std per coord (detrended 3rd diff) | err RMS per coord | held frames |\n|---|---:|---:|---:|---:|---:|\n");
        foreach ((string name, SceneData d) in data)
        {
            IReadOnlyList<Quad> q = d.Quads(d.S3Frames());
            double[][] s = TrackSeries.ToSeries(q);
            double own = TrackSeries.SigmaThirdDiff(s);
            IReadOnlyList<Quad> truth = d.TruthQuads();
            var err = new double[8][];
            for (int c = 0; c < 8; c++)
                err[c] = new double[q.Count];
            for (int i = 0; i < q.Count; i++)
            {
                int f = (int)Math.Round(q[i].T * d.Fps);
                Quad g = truth[f];
                err[0][i] = q[i].X0 - g.X0; err[1][i] = q[i].Y0 - g.Y0; err[2][i] = q[i].X1 - g.X1; err[3][i] = q[i].Y1 - g.Y1;
                err[4][i] = q[i].X2 - g.X2; err[5][i] = q[i].Y2 - g.Y2; err[6][i] = q[i].X3 - g.X3; err[7][i] = q[i].Y3 - g.Y3;
            }

            double rms = Math.Sqrt(err.SelectMany(e => e).Average(v => v * v));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {name} | {d.NoiseSrc:0.000} | {own:0.000} | {TrackSeries.SigmaThirdDiff(err):0.000} | {rms:0.000} | {d.Held.Count(h => h)} |"));
        }

        _output.WriteLine(sb.ToString());
    }

    /// <summary>The outlier-rejection prototype against production on every scene: detections removed, and the S3 error before any smoothing.</summary>
    [JitterBenchFact]
    public void Reject_prototype_vs_production()
    {
        var sb = new StringBuilder("| Scene | variant | rejected | noise grid px | N | ErrMean | ErrMax | SlipRms | HfRms | ContentSlip | Overshoot |\n|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|\n");
        foreach (string scene in SweepRunner.Scenes)
        {
            SceneData d = SceneData.Load(CacheDir, scene);
            IReadOnlyList<Quad> truth = d.TruthQuads();
            foreach ((string label, RejectPrototype.Mode? mode) in new (string, RejectPrototype.Mode?)[] { ("cached production S3", null), ("copy: Production", RejectPrototype.Mode.Production), ("prototype: BothSides", RejectPrototype.Mode.BothSides), ("prototype: Chord", RejectPrototype.Mode.Chord), ("prototype: Hinge", RejectPrototype.Mode.Hinge) })
            {
                int rej = -1;
                SceneData v = mode is null ? d : d.WithReject(mode.Value, out rej);
                JitterStats st = JitterMetrics.Compute(v.Quads(v.S3Frames()), truth, v.Fps);
                if (mode is not null && scene is "occlusion" or "whip")
                {
                    Quad[] tq = truth.ToArray();
                    string worst = string.Join(" ", v.Quads(v.S3Frames()).Select(q => (F: (int)Math.Round(q.T * v.Fps), E: Enumerable.Range(0, 4).Max(c => Math.Sqrt(Math.Pow(q.Corner(c).X - tq[(int)Math.Round(q.T * v.Fps)].Corner(c).X, 2) + Math.Pow(q.Corner(c).Y - tq[(int)Math.Round(q.T * v.Fps)].Corner(c).Y, 2))))).Where(x => x.E > 3).Select(x => $"{x.F}:{x.E:0.0}"));
                    _output.WriteLine($"{scene} {label} frames with err>3: {worst}");
                }

                if (mode is not null && d.RejectedFrames.Count > 0)
                    _output.WriteLine($"{scene} {label} rejected frames: {string.Join(",", d.RejectedFrames)}");
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {scene} | {label} | {(rej < 0 ? "-" : rej.ToString(CultureInfo.InvariantCulture))} | {v.NoiseGrid:0.000} | {st.N} | {st.ErrMean:0.000} | {st.ErrMax:0.000} | {st.SlipRms:0.000} | {st.HfRms:0.000} | {st.ContentSlipRms:0.000} | {JitterMetrics.Cell(st.Overshoot, "0.00")} |"));
            }
        }

        Directory.CreateDirectory(OutDir);
        File.WriteAllText(Path.Combine(OutDir, "reject-prototype.md"), sb.ToString());
        _output.WriteLine(sb.ToString());
    }

    private static SceneData StubScene() => new() { Name = "real", W = 1344, H = 768, Fps = 24, Gw = 480, Gh = 274 };

    /// <summary>
    /// Everything the final report needs, in one run: the full sweep on the production S3 and on the hinge-rejected S3 (summary, matrices, Pareto
    /// front), the held-frame detector's accuracy against the truth, and the no-truth numbers on the two real staging tracks.
    /// </summary>
    [JitterBenchFact]
    public void Final_report()
    {
        Directory.CreateDirectory(OutDir);
        var report = new StringBuilder();
        foreach ((string label, RejectPrototype.Mode? mode) in new (string, RejectPrototype.Mode?)[] { ("production-reject", null), ("hinge-reject", RejectPrototype.Mode.Hinge) })
        {
            var data = SweepRunner.LoadAll(CacheDir, mode);
            var results = AllCandidates().Select(c => SweepRunner.Evaluate(c, data)).ToList();
            SweepRunner.CandidateResult baseline = results.First(r => r.Candidate.Id == "K0");
            var summary = new StringBuilder(SweepRunner.SummaryHeader() + "\n");
            var matrix = new StringBuilder();
            foreach (SweepRunner.CandidateResult r in results)
            {
                summary.AppendLine(SweepRunner.SummaryRow(r, baseline));
                matrix.AppendLine(SweepRunner.Matrix(r));
            }

            File.WriteAllText(Path.Combine(OutDir, $"summary-{label}.md"), summary.ToString());
            File.WriteAllText(Path.Combine(OutDir, $"matrix-{label}.md"), matrix.ToString());

            List<SweepRunner.CandidateResult> front = SweepRunner.ParetoFront(results);
            var fs = new StringBuilder($"### Pareto front ({label}): {front.Count} of {results.Count} candidates are not dominated on all nine objectives\n\n");
            fs.AppendLine("| Id | " + string.Join(" | ", SweepRunner.ObjectiveNames) + " | regress cells |");
            fs.AppendLine("|---|" + string.Join("|", SweepRunner.ObjectiveNames.Select(_ => "---:")) + "|---:|");
            foreach (SweepRunner.CandidateResult r in front.OrderBy(r => SweepRunner.Objectives(r).Sum()))
                fs.AppendLine($"| {r.Candidate.Id} | " + string.Join(" | ", SweepRunner.Objectives(r).Select(v => v.ToString("0.000", CultureInfo.InvariantCulture))) + $" | {SweepRunner.Regressions(r, baseline, out _)} |");
            fs.AppendLine();
            var objK0 = SweepRunner.Objectives(baseline);
            fs.AppendLine("Baseline K0 objectives: " + string.Join(", ", objK0.Select((v, i) => $"{SweepRunner.ObjectiveNames[i]} {v:0.000}")));
            File.WriteAllText(Path.Combine(OutDir, $"pareto-{label}.md"), fs.ToString());
            report.AppendLine(fs.ToString());
        }

        report.AppendLine(CadenceAccuracy());
        report.AppendLine(RealTracks());
        File.WriteAllText(Path.Combine(OutDir, "final-extras.md"), report.ToString());
        _output.WriteLine(report.ToString());
    }

    private string CadenceAccuracy()
    {
        var sb = new StringBuilder("### Held-frame detector against the truth (held = the truth geometry is identical to the previous frame's)\n\n| Scene | truly held | flagged | true positives | false positives | false negatives |\n|---|---:|---:|---:|---:|---:|\n");
        foreach (string scene in SweepRunner.Scenes)
        {
            SceneData d = SceneData.Load(CacheDir, scene);
            double[][] t = d.Truth.ToArray();
            int truly = 0, flagged = 0, tp = 0, fp = 0, fn = 0;
            for (int f = 1; f < t.Length; f++)
            {
                bool isHeld = Enumerable.Range(1, 8).All(k => Math.Abs(t[f][k] - t[f - 1][k]) < 1e-6);
                bool flag = d.Held[f];
                truly += isHeld ? 1 : 0;
                flagged += flag ? 1 : 0;
                tp += isHeld && flag ? 1 : 0;
                fp += !isHeld && flag ? 1 : 0;
                fn += isHeld && !flag ? 1 : 0;
            }

            sb.AppendLine($"| {scene} | {truly} | {flagged} | {tp} | {fp} | {fn} |");
        }

        return sb.ToString();
    }

    private string RealTracks()
    {
        string promo = Environment.GetEnvironmentVariable("REELBOLT_JITTER_PROMO") is { Length: > 0 } p ? p : "/tmp/insert-tracking/promo";
        var rows = new List<(string, JitterStats)>();
        var d0 = StubScene();
        var cands = new List<Candidate>
        {
            new("box3 (gate off)", "ref", _ => new BoxSmoother(3)),
            new("K1p-n5-k2", "K1", d => new GatedBoxSmoother(5, 2.0, 4, Candidates.Prior(d, 0.18))),
            new("K3cvp-L0.05-ad", "K3", d => new KalmanSmoother(2, 0.05, adaptive: true, sigma: Candidates.Prior(d, 0.18))),
            new("K4fix-H4", "K4", _ => new LocalPolySmoother(new LocalPolyOptions(2, [4], double.PositiveInfinity, 2, true, 0))),
            new("K4ra-t2-g0.1-r6", "K4", d => new LocalPolySmoother(new LocalPolyOptions(2, [4, 6, 8, 12], 2.0, RawAbove: 6.0), Candidates.Prior(d, 0.1))),
            new("K4ra-t1.5-g0.18-r6", "K4", d => new LocalPolySmoother(new LocalPolyOptions(2, [4, 6, 8, 12], 1.5, RawAbove: 6.0), Candidates.Prior(d, 0.18)))
        };

        foreach ((string label, string pattern) in new[] { ("Film A", "artifact-65fe46f0-*.json"), ("Film B", "artifact-c712a68f-*.json") })
        {
            string file = Directory.GetFiles(promo, pattern).FirstOrDefault() ?? throw new FileNotFoundException(pattern);
            (List<Quad> surface, _, _, _) = LoadArtifact(file);
            rows.Add(($"{label} shipped artifact (already smoothed once by K0)", JitterMetrics.ComputeNoTruth(surface, 24)));
            double noise = TrackSeries.Median(Enumerable.Range(0, 1).Select(_ => 0.0));
            foreach (Candidate c in cands)
            {
                IReadOnlyList<Quad> o = c.Make(d0).Smooth(surface, 24, noise);
                rows.Add(($"{label} re-smoothed by {c.Id}", JitterMetrics.ComputeNoTruth(o, 24)));
            }
        }

        // The synthetic references: slow-push and hold-cadence S3 (before smoothing) and S4 (shipped), same metric.
        foreach (string scene in new[] { "slow-push", "microshake", "turn" })
        {
            SceneData d = SceneData.Load(CacheDir, scene);
            IReadOnlyList<Quad> s3 = d.Quads(d.S3Frames());
            rows.Add(($"synthetic {scene} S3 (raw)", JitterMetrics.ComputeNoTruth(s3, d.Fps)));
            rows.Add(($"synthetic {scene} S4 (K0)", JitterMetrics.ComputeNoTruth(new ProductionSmoother(d.S3Frames(), d.Gw, d.Gh, d.W, d.H).Smooth(s3, d.Fps, d.NoiseSrc), d.Fps)));
            rows.Add(($"synthetic {scene} K4ra-t2-g0.1-r6", JitterMetrics.ComputeNoTruth(cands[4].Make(d).Smooth(s3, d.Fps, d.NoiseSrc), d.Fps)));
        }

        return "### Real staging tracks without truth (surfaceCorners; grid 480x274 assumed for the sigma prior)\n\n" + NoTruthTable(rows);
    }

    /// <summary>Parameter sensitivity of the recommended smoother on the hinge-rejected S3 (full list under the output directory).</summary>
    [JitterBenchFact]
    public void Sensitivity_of_recommended()
    {
        var data = SweepRunner.LoadAll(CacheDir, RejectPrototype.Mode.Hinge);
        var rec = SweepRunner.Evaluate(Candidates.Final().First(c => c.Id == "F-cad-K4ra"), data);
        var results = Candidates.Sensitivity().Select(c => SweepRunner.Evaluate(c, data)).ToList();
        var sb = new StringBuilder(SweepRunner.SummaryHeader() + "\n");
        sb.AppendLine(SweepRunner.SummaryRow(rec, rec));
        foreach (SweepRunner.CandidateResult r in results)
            sb.AppendLine(SweepRunner.SummaryRow(r, rec));
        File.WriteAllText(Path.Combine(OutDir, "sensitivity.md"), sb.ToString());
        _output.WriteLine(sb.ToString());
    }

    /// <summary>
    /// Noisy kink stress, analytic and deterministic (no ffmpeg): truth paths with an abrupt velocity change, corrupted by iid noise plus an
    /// AR(1) wobble (the correlated error 4:2:0 sampling puts on a moving plate), run through the candidates. The T-002 whip scene is noise-free,
    /// so this is what shows whether an adaptive smoother still follows a whip start/stop when the track is noisy.
    /// </summary>
    [JitterBenchFact]
    public void Stress_kinks_with_noise()
    {
        SceneData d = StubScene();
        var rng = new Random(20261013);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        const int n = 240;
        (string Name, Func<int, double> Cx, Func<int, double> Cy, Func<int, double> Scale)[] paths =
        [
            ("whip 30 px/frame x12 frames", f => 300 + 30 * Math.Clamp(f - 96, 0, 12), _ => 380, _ => 1.0),
            ("stop-and-go 6 px/frame then still", f => 300 + 6 * Math.Min(f, 100), _ => 380, _ => 1.0),
            ("1 Hz sway, 25 px amplitude", f => 600 + 25 * Math.Sin(2 * Math.PI * f / 24.0), f => 380 + 10 * Math.Sin(2 * Math.PI * f / 36.0), _ => 1.0),
            ("zoom ramp 1.0->1.3 over 30 frames", _ => 600, _ => 380, f => 1.0 + 0.3 * Math.Clamp((f - 100) / 30.0, 0, 1)),
        ];
        var cands = new List<Candidate>
        {
            new("raw", "ref", _ => new IdentitySmoother()),
            new("K0", "ref", null!),
            new("K1p-n5-k2", "K1", x => new GatedBoxSmoother(5, 2.0, 4, Candidates.Prior(x, 0.18))),
            new("K3cvp-L0.05-ad", "K3", x => new KalmanSmoother(2, 0.05, adaptive: true, sigma: Candidates.Prior(x, 0.18))),
            new("K4fix-H4", "K4", _ => new LocalPolySmoother(new LocalPolyOptions(2, [4], double.PositiveInfinity, 2, true, 0))),
            new("K4p-t2-g0.1 (no raw fallback)", "K4", x => new LocalPolySmoother(new LocalPolyOptions(2, [4, 6, 8, 12], 2.0), Candidates.Prior(x, 0.1))),
            new("K4ra-t2-g0.1-r6", "K4", x => new LocalPolySmoother(new LocalPolyOptions(2, [4, 6, 8, 12], 2.0, RawAbove: 6.0), Candidates.Prior(x, 0.1)))
        };

        var sb = new StringBuilder("| Path | noise (iid sigma / AR1 sigma) | outlier pass | Smoother | ErrMean | ErrMax | SlipRms | Overshoot |\n|---|---|---|---|---:|---:|---:|---:|\n");
        foreach (var path in paths)
        {
            foreach ((double iid, double ar) in new[] { (0.0, 0.0), (0.3, 0.5) })
            {
                var truth = new List<Quad>();
                var track = new List<Quad>();
                var wob = new double[8];
                for (int f = 0; f < n; f++)
                {
                    double cx = path.Cx(f), cy = path.Cy(f), s = path.Scale(f), hw = 250 * s, hh = 140 * s;
                    Quad t = Quad.From(f / 24.0, (cx - hw, cy - hh), (cx + hw, cy - hh), (cx - hw, cy + hh), (cx + hw, cy + hh));
                    truth.Add(t);
                    double[] c = [t.X0, t.Y0, t.X1, t.Y1, t.X2, t.Y2, t.X3, t.Y3];
                    for (int k = 0; k < 8; k++)
                    {
                        wob[k] = 0.7 * wob[k] + ar * Math.Sqrt(1 - 0.49) * Gauss();
                        c[k] += iid * Gauss() + wob[k];
                    }

                    track.Add(new Quad(t.T, c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7]));
                }

                // Frames for the shipped Smooth (normalized to the 1344x768 frame); the outlier pass runs in both its shipped and its hinge form.
                var frames = track.Select((q, i) => new FrameQuad(i, q.X0 / 1344, q.Y0 / 768, q.X1 / 1344, q.Y1 / 768, q.X2 / 1344, q.Y2 / 768, q.X3 / 1344, q.Y3 / 768, 0.2, 1, 1)).ToList();
                foreach (RejectPrototype.Mode mode in new[] { RejectPrototype.Mode.Production, RejectPrototype.Mode.Hinge })
                {
                    (List<FrameQuad> kept, double noiseGrid, _, _) = RejectPrototype.Run(frames, 24, d.Gw, d.Gh, ProductionOptions(DefaultConfig), mode);
                    double noiseSrc = noiseGrid * d.W / d.Gw;
                    foreach (Candidate c in cands)
                    {
                        ITrackSmoother sm = c.Id == "K0" ? new ProductionSmoother(kept, d.Gw, d.Gh, d.W, d.H) : c.Make(d);
                        IReadOnlyList<Quad> input = kept.Select(q => new Quad(q.FrameIndex / 24.0, q.X0 * 1344, q.Y0 * 768, q.X1 * 1344, q.Y1 * 768, q.X2 * 1344, q.Y2 * 768, q.X3 * 1344, q.Y3 * 768)).ToList();
                        JitterStats st = JitterMetrics.Compute(sm.Smooth(input, 24, noiseSrc), truth, 24);
                        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                            $"| {path.Name} | {iid:0.#} / {ar:0.#} | {mode} | {c.Id} | {st.ErrMean:0.000} | {st.ErrMax:0.000} | {st.SlipRms:0.000} | {JitterMetrics.Cell(st.Overshoot, "0.00")} |"));
                    }
                }
            }
        }

        File.WriteAllText(Path.Combine(OutDir, "stress-kinks.md"), sb.ToString());
        _output.WriteLine(sb.ToString());
    }
}
