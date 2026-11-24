#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.RenderPath;

/// <summary>Scene plumbing for the render-path benches: a perfect (truth) or the shipped (S4) track, through the compile step, into a <see cref="RigInput"/>.</summary>
internal static class RenderScenes
{
    /// <summary>The baseline bench's own output (read-only): encoded clips and per-frame S4 csv.</summary>
    public const string BaselineDir = "/tmp/insert-tracking/bench";

    public static readonly string[] AllScenes =
        ["static-1344", "static-1080p", "static-small", "static-flicker", "microshake", "slow-push", "hold-cadence", "turn", "occlusion", "whip"];

    public sealed record Prepared(JitterScene Scene, CompileStage.Result Compile, RigInput White, RigInput Magenta, RigInput WhiteFlat, RigInput MagentaFlat);

    public static string ClipPath(JitterScene scene)
    {
        string baseline = Path.Combine(BaselineDir, "clips", scene.Name + ".mp4");
        if (File.Exists(baseline))
            return baseline;
        return scene.Render(Path.Combine(BenchSupport.OutDir, "clips")).Path;
    }

    /// <summary>The ground truth as a flawless tracker would emit it (one keyframe per frame, normalized).</summary>
    public static VideoInsertRegionTrack PerfectTrack(JitterScene scene)
    {
        double w = scene.Width, h = scene.Height;
        List<VideoInsertQuadKeyframe> kfs = scene.Truth().Select(q =>
            new VideoInsertQuadKeyframe(q.T, q.X0 / w, q.Y0 / h, q.X1 / w, q.Y1 / h, q.X2 / w, q.Y2 / h, q.X3 / w, q.Y3 / h)).ToList();
        return new VideoInsertRegionTrack("r0", null, 0, scene.Frames / scene.Fps, kfs, 1.0, 0.3, 1.7, "Static", "green");
    }

    /// <summary>The shipped tracker's output (S4, <c>surfaceCorners</c> preferred) read back from the baseline bench's csv.</summary>
    public static VideoInsertRegionTrack RealTrack(JitterScene scene)
    {
        string csv = Path.Combine(BaselineDir, "csv", scene.Name + "-S4-artifact.csv");
        List<Quad> q = BenchSupport.ReadCsvTrack(csv);
        double w = scene.Width, h = scene.Height;
        var kfs = q.Select(s => new VideoInsertQuadKeyframe(s.T, s.X0 / w, s.Y0 / h, s.X1 / w, s.Y1 / h, s.X2 / w, s.Y2 / h, s.X3 / w, s.Y3 / h)).ToList();
        return new VideoInsertRegionTrack("r0", null, kfs[0].TimeSec, kfs[^1].TimeSec, kfs, 1.0, 0.3, 1.7, "Static", "green");
    }

    public static CompileStage.Result Compile(JitterScene scene, VideoInsertRegionTrack track, double overscan = 0.02)
    {
        int fpsNum = (int)Math.Round(scene.Fps);
        return CompileStage.Run("T005", track, scene.TruthAt, scene.Width, scene.Height, scene.Width, scene.Height, false, overscan, fpsNum, 1, scene.Frames / scene.Fps);
    }

    public static RigInput Input(JitterScene scene, CompileStage.Result s5, string contentLavfi, bool flatBase)
    {
        int fpsNum = (int)Math.Round(scene.Fps);
        long start = (long)Math.Round(s5.OutputStartSec * scene.Fps);
        return new RigInput(scene.Width, scene.Height, fpsNum, 1, scene.Frames, s5.Keyframes, s5.CanvasPx, contentLavfi,
            flatBase ? null : ClipPath(scene), "0x404040", (int)start);
    }

    public static JitterStats Score(IReadOnlyList<Quad> rendered, IReadOnlyList<Quad> reference, double fps) =>
        JitterMetrics.Compute(rendered, reference, fps);
}
