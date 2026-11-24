#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Tests.InsertJitter.EdgeSignal;
using ReelBolt.WorkflowEngine.Tests.InsertJitter.Smoothing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Combined;

/// <summary>Fast, always-on checks of the T-006 glue (no ffmpeg, no clips).</summary>
public class CombinedTests
{
    private const double Fps = 24;

    private static Quad PlateAt(int f, double cx) =>
        Quad.From(f / Fps, (cx - 250, 240), (cx + 250, 240), (cx - 250, 520), (cx + 250, 520));

    private static List<Quad> Noisy(int n, double sigma, int seed)
    {
        var rng = new Random(seed);
        double G() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        return Enumerable.Range(0, n).Select(f =>
        {
            Quad q = PlateAt(f, 300 + 0.4 * f);
            return new Quad(q.T, q.X0 + sigma * G(), q.Y0 + sigma * G(), q.X1 + sigma * G(), q.Y1 + sigma * G(), q.X2 + sigma * G(), q.Y2 + sigma * G(), q.X3 + sigma * G(), q.Y3 + sigma * G());
        }).ToList();
    }

    [Fact]
    public void Config_table_names_the_ticket_decodes_and_the_five_configurations()
    {
        CombinedConfigs.All.Select(c => c.Id).Should().Equal("C0", "C1", "C2", "C3", "C3a");
        EdgeDecode.Get(CombinedConfigs.E1b).Filter.Should().Be("fps={fps},scale=iw:ih:flags=bilinear+full_chroma_inp,format=yuv444p,scale={w}:{h}:flags=area+full_chroma_int,format=rgb24");
        EdgeDecode.Get(CombinedConfigs.E1a).Filter.Should().Be("fps={fps},scale={w}:{h}:flags=area+full_chroma_int,format=rgb24");
        CombinedConfigs.C0.Smoothing.Should().BeFalse();
        CombinedConfigs.C1.Smoothing.Should().BeFalse();
        CombinedConfigs.C2.DecodeId.Should().Be(EdgeDecode.BaseId);
        CombinedConfigs.C3.Smoothing.Should().BeTrue();
        CombinedConfigs.C3a.DecodeId.Should().Be(CombinedConfigs.E1a);
    }

    [Fact]
    public void Probed_smoother_is_the_local_poly_smoother_with_a_histogram()
    {
        var d = new SceneData { Name = "stub", W = 1344, H = 768, Fps = Fps, Gw = 480, Gh = 274 };
        List<Quad> track = Noisy(200, 0.15, 3);
        var reference = new LocalPolySmoother(CombinedPipeline.K4raOptions, Candidates.Prior(d, 0.1));
        var probed = new ProbedPolySmoother(CombinedPipeline.K4raOptions, CombinedPipeline.Prior(1344, 480, 0.1));
        IReadOnlyList<Quad> a = reference.Smooth(track, Fps, 0), b = probed.Smooth(track, Fps, 0);
        for (int i = 0; i < track.Count; i++)
            a[i].Should().Be(b[i]);
        probed.Diag.Should().NotBeNull();
        probed.Diag!.Samples.Should().Be(track.Count);
        probed.Diag.WidthCounts.Sum().Should().Be(track.Count);
        probed.Diag.SigmaSrc.Should().BeApproximately(0.1 * 1344 / 480, 0.02);
    }

    [Fact]
    public void Smoothing_a_noisy_straight_move_reduces_slip_and_keeps_a_noiseless_one_exact()
    {
        var truth = Enumerable.Range(0, 200).Select(f => PlateAt(f, 300 + 0.4 * f)).ToList();
        var smoother = new CadenceSmoother(new LocalPolySmoother(CombinedPipeline.K4raOptions, CombinedPipeline.Prior(1344, 480, 0.1)), new bool[200]);
        IReadOnlyList<Quad> clean = smoother.Smooth(truth, Fps, 0);
        for (int i = 0; i < truth.Count; i++)
        {
            clean[i].X0.Should().BeApproximately(truth[i].X0, 1e-6);
            clean[i].Y3.Should().BeApproximately(truth[i].Y3, 1e-6);
        }

        List<Quad> noisy = Noisy(200, 0.15, 5);
        double raw = JitterMetrics.Compute(noisy, truth, Fps).SlipRms;
        double sm = JitterMetrics.Compute(smoother.Smooth(noisy, Fps, 0), truth, Fps).SlipRms;
        sm.Should().BeLessThan(raw * 0.5);
    }

    [Fact]
    public void Error_sigma_of_a_track_is_the_noise_sigma()
    {
        var truth = Enumerable.Range(0, 300).Select(f => PlateAt(f, 300 + 0.4 * f)).ToList();
        CombinedPipeline.ErrSigma(Noisy(300, 0.2, 9), truth, Fps).Should().BeApproximately(0.2, 0.03);
    }
}
