#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ReelBolt.WorkflowEngine.Services.Video;
using FrameQuad = ReelBolt.WorkflowEngine.Services.Video.ChromaQuadTracker.FrameQuad;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Smoothing;

/// <summary>One cached tracker frame (only the fields the smoothing stages read or lerp).</summary>
public sealed record FqDto(int F, double[] C, double[] S, double[] B, bool I, double A, double Fl, double Cm, double Ec, double Pd);

/// <summary>
/// The tracker's hand-off products for one synthetic scene, cached to JSON so smoothers can be iterated without ffmpeg or the
/// tracker: S2 (the outlier pass' input), S3 (its output, what <c>Smooth</c> receives), the pass' noise (grid px), the held-frame flags
/// detected on the grid, and the exact truth.
/// </summary>
public sealed class SceneData
{
    public string Name { get; set; } = "";
    public int W { get; set; }
    public int H { get; set; }
    public double Fps { get; set; }
    public int Gw { get; set; }
    public int Gh { get; set; }
    public double NoiseGrid { get; set; }
    public List<FqDto> S2 { get; set; } = [];
    public List<FqDto> S3 { get; set; } = [];
    public bool[] Held { get; set; } = [];
    public double[] EdgeDiff { get; set; } = [];
    public double[] NoiseDiff { get; set; } = [];
    public List<double[]> Truth { get; set; } = [];

    /// <summary>Source px per grid px, horizontally: converts the pass' grid-px noise to source px.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double NoiseSrc => NoiseGrid * W / Gw;

    internal static FqDto ToDto(FrameQuad q) => new(
        q.FrameIndex,
        [q.X0, q.Y0, q.X1, q.Y1, q.X2, q.Y2, q.X3, q.Y3],
        [q.Surface.X0, q.Surface.Y0, q.Surface.X1, q.Surface.Y1, q.Surface.X2, q.Surface.Y2, q.Surface.X3, q.Surface.Y3],
        [q.Bows.Top.Bow, q.Bows.Top.Skew, q.Bows.Right.Bow, q.Bows.Right.Skew, q.Bows.Bottom.Bow, q.Bows.Bottom.Skew, q.Bows.Left.Bow, q.Bows.Left.Skew],
        q.Interpolated, q.AreaRatio, q.FillScore, q.ChromaMargin, q.EdgeContrast, q.PlateDominance);

    internal static FrameQuad FromDto(FqDto d) => new(
        d.F, d.C[0], d.C[1], d.C[2], d.C[3], d.C[4], d.C[5], d.C[6], d.C[7],
        d.A, d.Fl, d.Cm,
        new InsertPlateBows(new InsertEdgeBow(d.B[0], d.B[1]), new InsertEdgeBow(d.B[2], d.B[3]), new InsertEdgeBow(d.B[4], d.B[5]), new InsertEdgeBow(d.B[6], d.B[7])),
        (d.S[0], d.S[1], d.S[2], d.S[3], d.S[4], d.S[5], d.S[6], d.S[7]),
        EdgeContrast: d.Ec, Interpolated: d.I, PlateDominance: d.Pd);

    internal List<FrameQuad> S2Frames() => S2.Select(FromDto).ToList();

    internal List<FrameQuad> S3Frames() => S3.Select(FromDto).ToList();

    /// <summary>The scored quads of a frame list (surface corners when present), stamped at <c>FrameIndex / fps</c>.</summary>
    internal List<Quad> Quads(IEnumerable<FrameQuad> frames) => StageCapture.ToQuads(frames.Cast<FrameQuad?>(), W, H, Fps);

    public IReadOnlyList<Quad> TruthQuads() => Truth.Select(t => new Quad(t[0], t[1], t[2], t[3], t[4], t[5], t[6], t[7], t[8])).ToList();

    public static string CachePath(string dir, string name) => Path.Combine(dir, name + ".json");

    public void Save(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(CachePath(dir, Name), JsonSerializer.Serialize(this));
    }

    public static SceneData Load(string dir, string name) =>
        WithHeld(JsonSerializer.Deserialize<SceneData>(File.ReadAllText(CachePath(dir, name)))!);

    private static SceneData WithHeld(SceneData d)
    {
        d.Held = HeldFrameDetector.FromEdgeDiff(d.EdgeDiff);
        return d;
    }

    public static bool Exists(string dir, string name) => File.Exists(CachePath(dir, name));

    /// <summary>
    /// A copy whose S3 is rebuilt from the cached S2 by <see cref="RejectPrototype"/> (then the production upright pass) and whose noise is
    /// the prototype's; everything else (truth, held flags) is shared. <paramref name="rejected"/> is the number of detections it removed.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    internal List<int> RejectedFrames { get; private set; } = [];

    internal SceneData WithReject(RejectPrototype.Mode mode, out int rejected)
    {
        ChromaQuadTracker.Options options = BenchSupport.ProductionOptions(BenchSupport.DefaultConfig);
        (List<FrameQuad> run, double noise, int rej, List<int> rejFrames) = RejectPrototype.Run(S2Frames(), Fps, Gw, Gh, options, mode);
        rejected = rej;
        RejectedFrames = rejFrames;
        List<FrameQuad> s3 = run.Count == 0 ? [] : ChromaQuadTracker.UprightTrackLabels(run, Gw, Gh);
        return new SceneData
        {
            Name = Name,
            W = W,
            H = H,
            Fps = Fps,
            Gw = Gw,
            Gh = Gh,
            NoiseGrid = noise,
            S2 = S2,
            S3 = s3.Select(ToDto).ToList(),
            Held = Held,
            EdgeDiff = EdgeDiff,
            NoiseDiff = NoiseDiff,
            Truth = Truth
        };
    }
}
