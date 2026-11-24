#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ReelBolt.Shared.Workflows;
using Xunit;
using Xunit.Abstractions;
using static ReelBolt.WorkflowEngine.Tests.InsertJitter.BenchSupport;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.RenderPath;

/// <summary>
/// T-005 expected-gain bridge. The shipped tracker's wobble (S4 slip 0.70-1.8 px) swamps the render's, so the render fix looks small on the
/// real-track rows. This bench puts the render chain on a track whose ONLY error is i.i.d. Gaussian noise of a stated per-coordinate sigma
/// added to the truth (slip of that track alone = 2 sigma, px): what the render adds on top of a tracker that has been improved to that level.
/// </summary>
public class NoisyTrackBench
{
    private readonly ITestOutputHelper _output;

    public NoisyTrackBench(ITestOutputHelper output) => _output = output;

    private static VideoInsertRegionTrack Noisy(JitterScene scene, double sigmaPx)
    {
        double w = scene.Width, h = scene.Height;
        ulong state = JitterScene.SplitMix(JitterScene.Fnv1a(scene.Name) ^ 0xA5A5);
        double Next()
        {
            state = JitterScene.SplitMix(state);
            return (state >> 11) * (1.0 / (1UL << 53));
        }

        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - Next())) * Math.Cos(2 * Math.PI * Next());
        var kfs = scene.Truth().Select(q =>
        {
            double[] v = new double[8];
            for (int c = 0; c < 4; c++)
            {
                v[2 * c] = (q.Corner(c).X + sigmaPx * Gauss()) / w;
                v[2 * c + 1] = (q.Corner(c).Y + sigmaPx * Gauss()) / h;
            }

            return new VideoInsertQuadKeyframe(q.T, v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7]);
        }).ToList();
        return new VideoInsertRegionTrack("r0", null, 0, scene.Frames / scene.Fps, kfs, 1.0, 0.3, 1.7, "Static", "green");
    }

    [JitterBenchFact]
    public void Render_on_improved_tracks()
    {
        string[] scenes = ["slow-push", "microshake", "hold-cadence", "turn"];
        double[] sigmas = [0.0, 0.05, 0.15, 0.5];
        var variants = new[] { DecompositionBench.Baseline, SceneMatrixBench.Variants[2] };
        var jobs = (from s in scenes from sg in sigmas from v in variants select (s, sg, v)).ToList();
        var results = new ConcurrentDictionary<string, (RigResult R, CompileStage.Result C, JitterStats Track)>();
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = 3 }, j =>
        {
            JitterScene scene = JitterScenes.Get(j.s);
            VideoInsertRegionTrack track = j.sg == 0 ? RenderScenes.PerfectTrack(scene) : Noisy(scene, j.sg);
            CompileStage.Result s5 = RenderScenes.Compile(scene, track);
            RigInput input = RenderScenes.Input(scene, s5, "0xFFFFFF", flatBase: true);
            RigResult r = RenderRig.Run(input, j.v, RenderStage.White, runProd: false);
            JitterStats trackStats = JitterMetrics.Compute(track.Keyframes.Select(k => Quad.FromKeyframe(k, scene.Width, scene.Height, false)).ToList(), scene.Truth(), scene.Fps);
            results[$"{j.s}|{j.sg}|{j.v.Id}"] = (r, s5, trackStats);
        });

        var sb = new StringBuilder();
        sb.AppendLine("## Render on tracks improved to a stated noise level (expected gain)");
        sb.AppendLine();
        sb.AppendLine("Track = truth + i.i.d. Gaussian noise (sigma per coordinate per corner per frame; 0 = the perfect tracker), through the compile step (overscan 0.02) and the flat-base chain, white insert, integrating meter. **track** = SlipRms of the noisy keyframes alone vs truth (about 2 sigma). **total** = SlipRms of the rendered quad vs the truth with the same overscan (source px). **render adds** = sqrt(total^2 - track^2) (quadrature; i.i.d. assumption).");
        sb.AppendLine();
        sb.AppendLine("| scene | sigma | track slip | total (baseline) | render adds (baseline) | total (prerep) | render adds (prerep) | total reduction |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (string s in scenes)
        {
            JitterScene scene = JitterScenes.Get(s);
            foreach (double sg in sigmas)
            {
                var b = results[$"{s}|{sg}|base"];
                var p = results[$"{s}|{sg}|prerep"];
                double tb = JitterMetrics.Compute(b.R.Meter, b.C.IdealSourcePx, scene.Fps).SlipRms;
                double tp = JitterMetrics.Compute(p.R.Meter, p.C.IdealSourcePx, scene.Fps).SlipRms;
                double trk = b.Track.SlipRms;
                double Add(double total) => Math.Sqrt(Math.Max(0, total * total - trk * trk));
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {s} | {sg:0.00} | {trk:0.000} | {tb:0.000} | {Add(tb):0.000} | {tp:0.000} | {Add(tp):0.000} | {(tb > 1e-6 ? 100 * (1 - tp / tb) : 0):0}% |"));
            }
        }

        WriteSection("58-improved-tracks", sb.ToString());
    }
}
