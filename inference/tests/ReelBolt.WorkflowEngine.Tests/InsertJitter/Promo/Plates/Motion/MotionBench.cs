#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo.Plates.Motion;

/// <summary>
/// T-014 measurement bench. Run (from <c>inference/</c>): <c>REELBOLT_JITTER_BENCH=1 dotnet test tests/ReelBolt.WorkflowEngine.Tests -c Release --filter "FullyQualifiedName~Promo.Plates.Motion.MotionBench"</c>;
/// <c>REELBOLT_MOTION_STAGES</c> selects stages (<c>refs,synth,attr,proto</c>, default all). Output in <c>/tmp/insert-tracking/plate-motion/</c>.
/// </summary>
public class MotionBench
{
    private readonly ITestOutputHelper _out;

    public MotionBench(ITestOutputHelper output) => _out = output;

    internal static RingOptions RingDefault { get; } = new(Params: 6);

    /// <summary>Film B's surround is richly textured (calendar, lamp, boxes): a ring 14-90 px out. Film A's is dark and nearly flat: the widest ring that still sits on the monitor and the desk.</summary>
    internal static RingOptions RingFor(PlateCase c) => c.Id == "filmA" ? new RingOptions(Params: 6, DMax: 250, GradMin: 1.5, MinPixels: 3000) : RingDefault;

    [JitterBenchFact]
    public void Debug_luma_failures()
    {
        var sets = new (string Name, PromoEdgeOptions O)[]
        {
            ("r9", MotionMeasure.LumaOptions),
            ("r5", MotionMeasure.LumaOptions with { Reach1 = 5, Reach2 = 3.5, MaxResidualRms = 0.9 }),
            ("r4", MotionMeasure.LumaOptions with { Reach1 = 4, Reach2 = 3, MaxResidualRms = 0.9 }),
            ("r3", MotionMeasure.LumaOptions with { Reach1 = 3.5, Reach2 = 2.5, MaxResidualRms = 0.9 })
        };
        foreach (PlateCase c in PlateCase.All)
        {
            (List<Quad> seeds, _, int w, int h) = BenchSupport.LoadArtifact(MotionEnv.ArtifactPath("f1f2", c));
            var counts = new int[sets.Length][];
            var reasons = new Dictionary<string, int>[sets.Length];
            for (int s = 0; s < sets.Length; s++)
            {
                counts[s] = new int[6];
                reasons[s] = [];
            }

            foreach ((int index, float[] luma) in PromoVideo.Luma(c.PlatePath, w, h, seeds.Count))
            {
                for (int s = 0; s < sets.Length; s++)
                {
                    PromoQuadFit fit = PromoEdgeFit.FitQuad(luma, w, h, seeds[index], sets[s].O);
                    if (fit.Quad is not null)
                        counts[s][4]++;
                    counts[s][5]++;
                    for (int e = 0; e < 4; e++)
                    {
                        if (fit.Edges[e].Valid)
                            counts[s][e]++;
                        else
                        {
                            string key = PromoQuadFit.EdgeNames[e] + ": " + System.Text.RegularExpressions.Regex.Replace(fit.Edges[e].Reason, @"[0-9.]+", "#");
                            reasons[s][key] = reasons[s].GetValueOrDefault(key) + 1;
                        }
                    }
                }
            }

            for (int s = 0; s < sets.Length; s++)
            {
                _out.WriteLine($"{c.Id} {sets[s].Name}: quads {counts[s][4]}/{counts[s][5]}, edges top {counts[s][0]} right {counts[s][1]} bottom {counts[s][2]} left {counts[s][3]}; failures: {string.Join("; ", reasons[s].OrderBy(k => k.Key).Select(k => k.Key + " x" + k.Value))}");
            }
        }
    }

    [JitterBenchFact]
    public void Explore_ring_variants()
    {
        var variants = new (string Name, RingOptions O)[]
        {
            ("h8 d14-90 g2.5", new RingOptions()),
            ("a6 d14-90 g2.5", new RingOptions(Params: 6)),
            ("t2 d14-90 g2.5", new RingOptions(Params: 2)),
            ("a6 d14-250 g1.5", new RingOptions(Params: 6, DMax: 250, GradMin: 1.5, MinPixels: 3000)),
            ("a6 d14-400 g1.0", new RingOptions(Params: 6, DMax: 400, GradMin: 1.0, MinPixels: 3000)),
            ("h8 d14-250 g1.5", new RingOptions(Params: 8, DMax: 250, GradMin: 1.5, MinPixels: 3000)),
            ("t2 d14-250 g1.5", new RingOptions(Params: 2, DMax: 250, GradMin: 1.5, MinPixels: 3000)),
        };
        foreach (PlateCase c in PlateCase.All.Where(p => MotionEnv.Stage("film-" + p.Id) || MotionEnv.Stages == "all"))
        {
            (List<Quad> geom, _, int w, int h) = BenchSupport.LoadArtifact(MotionEnv.ArtifactPath("f1f2", c));
            PlateMeasurement luma = MotionMeasure.Luma(c.Id, c.PlatePath, geom, w, h);
            double?[][] lv = Chan.Velocity(Chan.Of(luma));
            Quad?[] g = Chan.Of(geom);
            (int a, int b)[] ranges = c.Id == "filmA" ? [(0, 70), (150, 220), (260, 330)] : [(0, 60), (120, 180)];
            foreach ((string name, RingOptions o) in variants)
            {
                foreach ((int a, int b) in ranges)
                {
                    IReadOnlyList<RingPair> pairs = MotionMeasure.Ring(c.Id, c.PlatePath, geom, w, h, o, null, a, b);
                    double?[][] rv = MotionAnalysis.RingVelocity(pairs, g[a..]);
                    double?[][] lvw = lv.Select(s => s.Skip(a).Take(b - a).ToArray()).ToArray();
                    PairStats st = PairStats.Of(rv, lvw);
                    PairStats hf = PairStats.Of(Chan.HighPass(MotionAnalysis.And(rv, lvw), 13), Chan.HighPass(MotionAnalysis.And(lvw, rv), 13));
                    _out.WriteLine($"{c.Id} [{a}-{b}] {name,-18} ok {pairs.Count(p => p.Ok),3}/{pairs.Count} px {pairs.Average(p => p.Pixels),7:0} | vs luma: n {st.N / 8,3} rms ring {st.RmsA * Math.Sqrt(2) * MotionAnalysis.Out:0.000} luma {st.RmsB * Math.Sqrt(2) * MotionAnalysis.Out:0.000} diff {st.RmsDiff * Math.Sqrt(2) * MotionAnalysis.Out:0.000} slope {st.SlopeAOnB:0.00} corr {st.Corr:0.00} | hf diff {hf.RmsDiff * Math.Sqrt(2) * MotionAnalysis.Out:0.000} (ring hf {hf.RmsA * Math.Sqrt(2) * MotionAnalysis.Out:0.000} luma hf {hf.RmsB * Math.Sqrt(2) * MotionAnalysis.Out:0.000})");
                }
            }
        }
    }

    internal static (int N, double MeanGm, double MeanGm2, double Frac) TextureOf(string clip, IReadOnlyList<Quad> geometry, int w, int h, int[] frames, RingOptions o)
    {
        var rows = new List<(int N, double MeanGm, double MeanGm2, double Frac)>();
        var buf = new byte[w * h];
        foreach ((int index, float[] luma) in PromoVideo.Luma(clip, w, h, frames.Max() + 1))
        {
            if (!frames.Contains(index))
                continue;
            for (int i = 0; i < buf.Length; i++)
                buf[i] = (byte)luma[i];
            Pyramid p = Pyramid.Build(buf, w, h, 1, o.BlurSigma);
            rows.Add(RingMotion.TextureStats(p, geometry[index], o));
        }

        return ((int)rows.Average(r => r.N), rows.Average(r => r.MeanGm), rows.Average(r => r.MeanGm2), rows.Average(r => r.Frac));
    }

    [JitterBenchFact]
    public void Calibrate_wall_texture()
    {
        var o = new RingOptions(Params: 6);
        foreach (PlateCase c in PlateCase.All)
        {
            (List<Quad> geom, _, int w, int h) = BenchSupport.LoadArtifact(MotionEnv.ArtifactPath("f1f2", c));
            foreach ((string name, int[] frames) in new[] { ("early", new[] { 5, 40, 80, 120 }), ("mid", new[] { 160, 200, 240 }), ("late", new[] { 290, 320, 350 }) })
            {
                int[] f = frames.Where(x => x < geom.Count).ToArray();
                if (f.Length == 0)
                    continue;
                (int n, double gm, double gm2, double frac) = TextureOf(c.PlatePath, geom, w, h, f, o);
                _out.WriteLine($"REAL {c.Id} {name}: ring px {n}, mean |grad| {gm:0.00}, mean grad^2 {gm2:0.0}, share >= {o.GradMin} {frac:0.000}");
            }
        }

        WallSpec[] walls = [new WallSpec("rich1", 70, 15, 60, 45), new WallSpec("rich2", 70, 15, 50, 60), new WallSpec("poor1", 30, 4, 260, 30, 20, 60), new WallSpec("poor2", 30, 4, 200, 40, 20, 60)];
        foreach (WallSpec wall in walls)
        {
            string film = wall.Id.StartsWith("rich", StringComparison.Ordinal) ? "filmB" : "filmA";
            foreach (int crf in new[] { 20, 24 })
            {
                var spec = new SynthSpec("cal", film, wall, new ShakeSpec("none", 0, 0, 0, true), crf, Frames: 130);
                SynthClip clip = SyntheticPlate.Render(spec, _out.WriteLine);
                (int n, double gm, double gm2, double frac) = TextureOf(clip.Path, clip.Truth, SyntheticPlate.W, SyntheticPlate.H, [5, 40, 80, 120], o);
                _out.WriteLine($"SYNTH {film} {wall.Id} crf {crf}: {clip.Kbps:0} kbit/s; ring px {n}, mean |grad| {gm:0.00}, mean grad^2 {gm2:0.0}, share {frac:0.000}");
            }
        }
    }

    [JitterBenchFact]
    public void Validate_references_on_synthetic()
    {
        var sb = new StringBuilder("# T-014 synthetic validation: the references and the tracker against known truth\n\n");
        foreach (SynthSpec spec in MotionSynth.Matrix)
        {
            SynthResult r = MotionSynth.Measure(spec, _out.WriteLine);
            sb.AppendLine(MotionSynth.Render(r));
            File.WriteAllText(Path.Combine(MotionEnv.Out, "synthetic.md"), sb.ToString());
        }

        _out.WriteLine(sb.ToString());
    }

    internal static RefSet LoadReal(PlateCase c, RingOptions ring, Action<string> log)
    {
        PlatesEnv env = MotionEnv.PlatesEnvironment();
        (List<Quad> staging, _, int w, int h) = BenchSupport.LoadArtifact(c.StagingArtifact);
        PlateMeasurement chroma = PlatesRun.Measure(env, c, staging, w, h, c.PlatePath, "plate");
        var tracks = new List<(string, Quad?[])>();
        foreach (string label in MotionEnv.Labels)
        {
            (List<Quad> surface, _, _, _) = BenchSupport.LoadArtifact(MotionEnv.ArtifactPath(label, c));
            tracks.Add((label, Chan.Of(surface)));
        }

        Quad?[] f1f2 = tracks.First(t => t.Item1 == "f1f2").Item2;
        List<Quad> geometry = f1f2.Select(q => q!.Value).ToList();
        PlateMeasurement luma = MotionMeasure.Luma(c.Id, c.PlatePath, geometry, w, h);
        IReadOnlyList<RingPair> pairs = MotionMeasure.Ring(c.Id, c.PlatePath, geometry, w, h, ring, log);
        return new RefSet(c.Id, geometry.Count, tracks, chroma, luma, pairs, MotionAnalysis.RingPositions(pairs, f1f2), MotionAnalysis.RingVelocity(pairs, f1f2), f1f2);
    }

    /// <summary>Adds the prototype smoothers' tracks (same tracker stages, F1 decode, only the smoother differs) and checks the replica against the stored F1+F2 artifact.</summary>
    internal RefSet WithProto(RefSet r, PlateCase c)
    {
        MotionProto.PlateCapture cap = MotionProto.CapturePlate(c);
        Quad?[] prodPlaced = MotionProto.Smooth(cap, ProtoOptions.Production);
        Quad?[] stored = r.Tracks.First(t => t.Label == "f1f2").Track;
        double max = 0;
        int both = 0;
        for (int i = 0; i < Math.Min(prodPlaced.Length, stored.Length); i++)
        {
            if (prodPlaced[i] is not { } a || stored[i] is not { } b)
                continue;
            both++;
            for (int k = 0; k < 4; k++)
                max = Math.Max(max, Math.Max(Math.Abs(a.Corner(k).X - b.Corner(k).X), Math.Abs(a.Corner(k).Y - b.Corner(k).Y)));
        }

        _out.WriteLine($"{c.Id}: replica of the production chain vs the stored f1f2 artifact: {both} frames, max corner difference {max:0.000000} src px");
        var tracks = r.Tracks.ToList();
        foreach (ProtoOptions v in MotionProto.Variants.Skip(1))
            tracks.Add(("proto: " + v.Name, MotionProto.Smooth(cap, v)));
        return r with { Tracks = tracks };
    }

    [JitterBenchFact]
    public void Proto_matrix()
    {
        string[] names = [.. MotionProto.Variants.Select(v => v.Name), "raw S3 (no smoother)"];
        List<MotionProto.Cell> cells = MotionProto.RunMatrix(MotionProto.Variants, _out.WriteLine);
        string md = "# T-014 prototype smoothers on the synthetic matrix (E1b decode, production tracker stages, only the smoother differs)\n\n" + MotionProto.RenderMatrix(cells, names);
        File.WriteAllText(Path.Combine(MotionEnv.Out, "proto-matrix.md"), md);
        _out.WriteLine(md);
    }

    [JitterBenchFact]
    public void Analyze_real_plates()
    {
        var sb = new StringBuilder("# T-014 real plates: independent references against the four tracker labels\n\n");
        foreach (PlateCase c in PlateCase.All)
        {
            RefSet r = LoadReal(c, RingFor(c), _out.WriteLine);
            if (MotionEnv.Stage("proto"))
                r = WithProto(r, c);
            (string, int, int)[] windows = c.Id == "filmA"
                ? [("whole clip", 0, r.N - 1), ("early", 0, 281), ("late zoomed", 282, r.N - 1)]
                : [("whole clip", 0, r.N - 1), ("first half", 0, 120), ("second half", 121, r.N - 1)];
            sb.AppendLine(MotionAnalysis.Render(r, windows));
        }

        File.WriteAllText(Path.Combine(MotionEnv.Out, "real-plates.md"), sb.ToString());
        _out.WriteLine(sb.ToString());
    }
}
