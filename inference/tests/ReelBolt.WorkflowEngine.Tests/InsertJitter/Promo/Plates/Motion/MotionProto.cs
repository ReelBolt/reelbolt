#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Tracking;
using ReelBolt.WorkflowEngine.Tests.InsertJitter.EdgeSignal;
using FrameQuad = ReelBolt.WorkflowEngine.Services.Video.ChromaQuadTracker.FrameQuad;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo.Plates.Motion;

/// <summary>PROTOTYPE (T-014): the smallest changes to the smoother that would let real sub-pixel motion through, measured on both plates and on the whole synthetic matrix.</summary>
internal static class MotionProto
{
    public static readonly ProtoOptions[] Variants =
    [
        ProtoOptions.Production,
        ProtoOptions.Production with { Name = "sigma-prior 0.07", SigmaGridFraction = 0.07 },
        ProtoOptions.Production with { Name = "sigma-prior 0.05", SigmaGridFraction = 0.05 },
        ProtoOptions.Production with { Name = "sigma-prior 0.03", SigmaGridFraction = 0.03 },
        ProtoOptions.Production with { Name = "tau 1.0", Tau = 1.0 },
        ProtoOptions.Production with { Name = "width cap 8", HalfWidths = [4, 6, 8] },
        ProtoOptions.Production with { Name = "width cap 6", HalfWidths = [4, 6] },
        ProtoOptions.Production with { Name = "leak 0.15", Leak = 0.15 },
        ProtoOptions.Production with { Name = "leak 0.30", Leak = 0.30 },
        ProtoOptions.Production with { Name = "leak 0.45", Leak = 0.45 }
    ];

    public sealed record PlateCapture(StageSet Stages, byte[] Grid, int Frames, int Gw, int Gh, int W, int H, bool[] Held);

    /// <summary>The production tracker's stages on a real plate with the F1 grid decode (the chain the step runs); S3 is what the smoother is handed.</summary>
    public static PlateCapture CapturePlate(PlateCase c)
    {
        (_, _, int w, int h) = BenchSupport.LoadArtifact(c.StagingArtifact);
        var cfg = PlatesStepRun.StagingConfig();
        (int gw, int gh) = ChromaQuadTracker.DeriveGridSize(w, h, cfg.InsertGridWidth, cfg.InsertGridHeight);
        const double fps = PromoInputs.Fps;
        DecodeResult dec = EdgeDecode.Decode(c.PlatePath, EdgeDecode.Get("e1b-bilinear-fast"), fps, gw, gh);
        int frames = dec.Grid.Length / (gw * gh * 3);
        StageSet st = StageCapture.Capture(dec.Grid, frames, gw, gh, fps, BenchSupport.ProductionOptions(cfg), w, h, false);
        bool[] held = InsertTrackSmoother.DetectHeld(dec.Grid, frames, gw, gh, st.S3);
        return new PlateCapture(st, dec.Grid, frames, gw, gh, w, h, held);
    }

    public static Quad?[] Place(IEnumerable<FrameQuad> run, int frames, int w, int h)
    {
        var o = new Quad?[frames];
        foreach (FrameQuad q in run)
        {
            if (q.FrameIndex >= 0 && q.FrameIndex < frames)
                o[q.FrameIndex] = Quad.FromFrameQuad(q, w, h, q.FrameIndex / PromoInputs.Fps);
        }

        return o;
    }

    public static Quad?[] Smooth(PlateCapture p, ProtoOptions o) =>
        Place(ProtoTrackSmoother.Smooth(p.Stages.S3, p.Held, p.W / (double)p.Gw, p.Gw, p.Gh, o), p.Frames, p.W, p.H);

    // ------------------------------------------------------------------------------------------------ synthetic matrix

    public sealed record Cell(string Scene, string Variant, JitterStats Stats);

    public static List<Cell> RunMatrix(IReadOnlyList<ProtoOptions> variants, Action<string> log)
    {
        var cells = new List<Cell>();
        foreach (JitterScene scene in EdgeSupport.Scenes)
        {
            RenderedClip clip = scene.Render(EdgeSupport.ClipsDir);
            BenchSupport.GridPlan plan = BenchSupport.Plan(scene, BenchSupport.DefaultConfig);
            DecodeResult dec = EdgeDecode.Decode(clip.Path, EdgeDecode.Get("e1b-bilinear-fast"), plan.SampleFps, plan.GridW, plan.GridH);
            int frames = dec.Grid.Length / (plan.GridW * plan.GridH * 3);
            StageSet st = StageCapture.Capture(dec.Grid, frames, plan.GridW, plan.GridH, plan.SampleFps, BenchSupport.ProductionOptions(BenchSupport.DefaultConfig), scene.Width, scene.Height, false);
            bool[] held = InsertTrackSmoother.DetectHeld(dec.Grid, frames, plan.GridW, plan.GridH, st.S3);
            foreach (ProtoOptions v in variants)
            {
                List<FrameQuad> sm = ProtoTrackSmoother.Smooth(st.S3, held, scene.Width / (double)plan.GridW, plan.GridW, plan.GridH, v);
                List<Quad> track = StageCapture.ToQuads(sm.Cast<FrameQuad?>(), scene.Width, scene.Height, plan.SampleFps);
                JitterStats s = JitterMetrics.Compute(track, clip.Truth, plan.SampleFps);
                cells.Add(new Cell(scene.Name, v.Name, s));
            }

            // The raw stage for reference (what any smoothing starts from).
            JitterStats raw = JitterMetrics.Compute(StageCapture.ToQuads(st.S3.Cast<FrameQuad?>(), scene.Width, scene.Height, plan.SampleFps), clip.Truth, plan.SampleFps);
            cells.Add(new Cell(scene.Name, "raw S3 (no smoother)", raw));
            log($"{scene.Name}: {variants.Count} variants scored");
        }

        return cells;
    }

    /// <summary>AC1 of the plan (SlipRms, ErrMax per scene; |LagLS| 0.15 everywhere; ErrMean against C0 + 0.02 where C0 is known), and the baselines it refers to.</summary>
    public static readonly Dictionary<string, (double Slip, double ErrMax, double? C0ErrMean, double? C0ErrMax, double? Overshoot)> Ac1 = new()
    {
        ["static-1344"] = (0.005, 0.738 + 0.05, 0.382, 0.738, null),
        ["static-1080p"] = (0.005, 1.272 + 0.05, 0.653, 1.272, null),
        ["static-small"] = (0.005, 0.624 + 0.05, 0.479, 0.624, null),
        ["static-flicker"] = (0.031, 0.742 + 0.05, 0.402, 0.742, null),
        ["microshake"] = (0.40, 1.7, 0.874, 2.734, null),
        ["slow-push"] = (0.05, 1.6, 0.725, 2.346, null),
        ["slow-push-weakluma"] = (0.05, 1.6, null, null, null),
        ["slow-push-isoluma"] = (0.05, 1.6, null, null, null),
        ["hold-cadence"] = (0.08, 0.8, 1.029, 3.165, null),
        ["turn"] = (0.19, 1.7, 0.352, 2.199, 1.7),
        ["occlusion"] = (0.05, 1.2, 0.417, 2.646, null),
        ["whip"] = (0.05, 0.8, 1.494, 20.832, null)
    };

    public static bool Pass(string scene, JitterStats s, out string why)
    {
        var f = new List<string>();
        (double slip, double errMax, double? c0Mean, _, double? over) = Ac1[scene];
        if (s.SlipRms > slip)
            f.Add("slip");
        if (s.ErrMax > errMax)
            f.Add("errMax");
        if (c0Mean is { } m && s.ErrMean > m + 0.02)
            f.Add("errMean");
        if (s.LagLsFrames is { } lag && Math.Abs(lag) > 0.15)
            f.Add("lag");
        if (over is { } ov && s.Overshoot is { } so && so > ov)
            f.Add("overshoot");
        why = string.Join('+', f);
        return f.Count == 0;
    }

    public static string RenderMatrix(IReadOnlyList<Cell> cells, IReadOnlyList<string> variants)
    {
        var sb = new StringBuilder();
        string[] scenes = EdgeSupport.Scenes.Select(s => s.Name).ToArray();
        string F(double? v, string f = "0.000") => v is null || double.IsNaN(v.Value) ? "-" : v.Value.ToString(f, CultureInfo.InvariantCulture);
        foreach ((string metric, Func<JitterStats, double?> get, string fmt) in new (string, Func<JitterStats, double?>, string)[]
                 {
                     ("SlipRms", s => s.SlipRms, "0.000"), ("ErrMax", s => s.ErrMax, "0.00"), ("ErrMean", s => s.ErrMean, "0.000"), ("LagLS", s => s.LagLsFrames, "0.00"), ("HfRms", s => s.HfRms, "0.000")
                 })
        {
            sb.AppendLine($"**{metric}** (source px; AC1 limits in the first row where one exists)");
            sb.AppendLine();
            sb.AppendLine("| variant | " + string.Join(" | ", scenes) + " |");
            sb.AppendLine("|---|" + string.Concat(scenes.Select(_ => "---:|")));
            if (metric is "SlipRms" or "ErrMax")
                sb.AppendLine("| AC1 limit | " + string.Join(" | ", scenes.Select(sc => F(metric == "SlipRms" ? Ac1[sc].Slip : Ac1[sc].ErrMax, "0.000"))) + " |");
            foreach (string v in variants)
            {
                sb.Append($"| {v} |");
                foreach (string sc in scenes)
                {
                    Cell? c = cells.FirstOrDefault(x => x.Scene == sc && x.Variant == v);
                    sb.Append(' ').Append(c is null ? "." : F(get(c.Stats), fmt)).Append(" |");
                }

                sb.AppendLine();
            }

            sb.AppendLine();
        }

        sb.AppendLine("**AC1 verdict per variant** (scene: failing criteria)");
        sb.AppendLine();
        foreach (string v in variants.Where(x => x != "raw S3 (no smoother)"))
        {
            var fails = new List<string>();
            foreach (string sc in scenes)
            {
                Cell? c = cells.FirstOrDefault(x => x.Scene == sc && x.Variant == v);
                if (c is not null && !Pass(sc, c.Stats, out string why))
                    fails.Add($"{sc}: {why}");
            }

            sb.AppendLine($"- `{v}`: {(fails.Count == 0 ? "passes AC1 on all 12 scenes" : "FAILS " + string.Join("; ", fails))}");
        }

        sb.AppendLine();
        return sb.ToString();
    }
}
