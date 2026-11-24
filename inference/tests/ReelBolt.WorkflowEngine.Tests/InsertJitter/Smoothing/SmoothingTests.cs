#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using FrameQuad = ReelBolt.WorkflowEngine.Services.Video.ChromaQuadTracker.FrameQuad;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Smoothing;

/// <summary>Fast, always-on checks of the T-004 smoother prototypes (analytic tracks, no ffmpeg, no clips).</summary>
public class SmoothingTests
{
    private const double Fps = 24;

    private static double Gaussian(Random rng) =>
        Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());

    private static Quad PlateAt(int f, double cx, double cy = 380, double scale = 1.0) =>
        Quad.From(f / Fps, (cx - 250 * scale, cy - 140 * scale), (cx + 250 * scale, cy - 140 * scale), (cx - 250 * scale, cy + 140 * scale), (cx + 250 * scale, cy + 140 * scale));

    private static List<Quad> Truth(int n, Func<int, double> cx, Func<int, double>? cy = null, Func<int, double>? scale = null) =>
        Enumerable.Range(0, n).Select(f => PlateAt(f, cx(f), cy?.Invoke(f) ?? 380, scale?.Invoke(f) ?? 1.0)).ToList();

    private static List<Quad> Noisy(IReadOnlyList<Quad> truth, double sigma, int seed)
    {
        var rng = new Random(seed);
        return truth.Select(q => new Quad(q.T, q.X0 + sigma * Gaussian(rng), q.Y0 + sigma * Gaussian(rng), q.X1 + sigma * Gaussian(rng), q.Y1 + sigma * Gaussian(rng),
            q.X2 + sigma * Gaussian(rng), q.Y2 + sigma * Gaussian(rng), q.X3 + sigma * Gaussian(rng), q.Y3 + sigma * Gaussian(rng))).ToList();
    }

    private static SceneData Stub() => new() { Name = "stub", W = 1344, H = 768, Fps = Fps, Gw = 480, Gh = 274 };

    private static LocalPolySmoother K4ra(SceneData d) =>
        new(new LocalPolyOptions(2, [4, 6, 8, 12], 2.0, RawAbove: 6.0), Candidates.Prior(d, 0.1));

    private static double MaxErr(IReadOnlyList<Quad> a, IReadOnlyList<Quad> b) =>
        a.Zip(b, (x, y) => Enumerable.Range(0, 4).Max(c => Math.Sqrt(Math.Pow(x.Corner(c).X - y.Corner(c).X, 2) + Math.Pow(x.Corner(c).Y - y.Corner(c).Y, 2)))).Max();

    [Fact]
    public void Poly_fit_recovers_an_exact_quadratic()
    {
        double[] t = Enumerable.Range(0, 11).Select(i => (double)i).ToArray();
        double[][] y = [t.Select(v => 3 - 2 * v + 0.5 * v * v).ToArray()];
        var w = Enumerable.Repeat(1.0, 11).ToArray();
        var coef = new[] { new double[3] };
        PolyFit.Fit(t, y, 0, 10, 5, w, 2, coef).Should().BeTrue();
        coef[0][0].Should().BeApproximately(3 - 10 + 12.5, 1e-9);
        coef[0][1].Should().BeApproximately(-2 + 5, 1e-9);
        coef[0][2].Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void Third_difference_sigma_is_zero_on_a_quadratic_and_sigma_on_white_noise()
    {
        List<Quad> clean = Truth(200, f => 300 + 0.5 * f + 0.01 * f * f);
        TrackSeries.SigmaThirdDiff(TrackSeries.ToSeries(clean)).Should().BeLessThan(1e-6);
        TrackSeries.SigmaThirdDiff(TrackSeries.ToSeries(Noisy(clean, 0.5, 7))).Should().BeApproximately(0.5, 0.06);
    }

    [Fact]
    public void Box_smoother_is_exact_on_a_straight_path_away_from_the_ends_and_shrinks_noise()
    {
        List<Quad> line = Truth(100, f => 300 + 3 * f);
        IReadOnlyList<Quad> o = new BoxSmoother(5).Smooth(line, Fps, 0);
        MaxErr(o.Skip(3).Take(90).ToList(), line.Skip(3).Take(90).ToList()).Should().BeLessThan(1e-9);

        List<Quad> still = Truth(400, _ => 500);
        List<Quad> noisy = Noisy(still, 1.0, 3);
        double raw = JitterMetrics.Compute(noisy, still, Fps).SlipRms;
        double box = JitterMetrics.Compute(new BoxSmoother(5).Smooth(noisy, Fps, 0), still, Fps).SlipRms;
        box.Should().BeLessThan(raw / 2);
    }

    [Fact]
    public void Local_polynomial_follows_a_smooth_quadratic_path_and_removes_noise_from_a_slow_one()
    {
        SceneData d = Stub();
        List<Quad> quad = Truth(160, f => 300 + 0.4 * f + 0.004 * f * f);
        MaxErr(K4ra(d).Smooth(quad, Fps, 0), quad).Should().BeLessThan(1e-6);

        List<Quad> slow = Truth(240, f => 300 + 400 * (3 * Math.Pow(f / 239.0, 2) - 2 * Math.Pow(f / 239.0, 3)));
        List<Quad> noisy = Noisy(slow, 0.8, 11);
        double raw = JitterMetrics.Compute(noisy, slow, Fps).SlipRms;
        double sm = JitterMetrics.Compute(K4ra(d).Smooth(noisy, Fps, 0), slow, Fps).SlipRms;
        sm.Should().BeLessThan(raw / 5);
    }

    [Fact]
    public void Local_polynomial_with_raw_fallback_keeps_a_whip_kink()
    {
        SceneData d = Stub();
        List<Quad> whip = Truth(200, f => 300 + 30 * Math.Clamp(f - 96, 0, 12));
        MaxErr(K4ra(d).Smooth(whip, Fps, 0), whip).Should().BeLessThan(0.5);

        // Without the fallback the narrowest quadratic straddles the kink and smears it by several pixels.
        var noFallback = new LocalPolySmoother(new LocalPolyOptions(2, [4, 6, 8, 12], 2.0), Candidates.Prior(d, 0.1));
        MaxErr(noFallback.Smooth(whip, Fps, 0), whip).Should().BeGreaterThan(3);
    }

    [Fact]
    public void Kalman_cv_rts_recovers_a_constant_velocity_track_and_adaptive_follows_a_stop()
    {
        SceneData d = Stub();
        List<Quad> line = Truth(150, f => 200 + 4 * f);
        MaxErr(new KalmanSmoother(2, 0.05, sigma: Candidates.Prior(d, 0.18)).Smooth(line, Fps, 0), line).Should().BeLessThan(1e-4);

        List<Quad> stop = Truth(150, f => 200 + 6 * Math.Min(f, 70));
        double fixedErr = MaxErr(new KalmanSmoother(2, 0.02, sigma: Candidates.Prior(d, 0.18)).Smooth(stop, Fps, 0), stop);
        double adaptiveErr = MaxErr(new KalmanSmoother(2, 0.02, adaptive: true, sigma: Candidates.Prior(d, 0.18)).Smooth(stop, Fps, 0), stop);
        adaptiveErr.Should().BeLessThan(fixedErr);
    }

    [Fact]
    public void One_euro_forward_backward_has_no_lag_on_a_ramp()
    {
        List<Quad> ramp = Truth(120, f => 300 + 5 * f);
        IReadOnlyList<Quad> o = new OneEuroSmoother(2.0, 0.05).Smooth(ramp, Fps, 0);
        double meanSignedX = o.Skip(20).Take(80).Select((q, i) => q.X0 - ramp[i + 20].X0).Average();
        Math.Abs(meanSignedX).Should().BeLessThan(0.05);
    }

    [Fact]
    public void Hadamard_and_homography_representations_round_trip()
    {
        List<Quad> q = Truth(60, f => 300 + f, f => 380 + 0.2 * f, f => 1 + 0.002 * f);
        var slightlyOff = q.Select(x => x with { X3 = x.X3 + 3, Y1 = x.Y1 - 2 }).ToList();
        foreach (Quad x in slightlyOff.Take(5))
        {
            Quad back = HomographySmoother.FromParams(HomographySmoother.ToParams(x), x.T);
            for (int c = 0; c < 4; c++)
            {
                back.Corner(c).X.Should().BeApproximately(x.Corner(c).X, 1e-6);
                back.Corner(c).Y.Should().BeApproximately(x.Corner(c).Y, 1e-6);
            }
        }

        // An exactly straight path passes through the Hadamard smoother unchanged.
        var h = new HadamardSmoother(new LocalPolyOptions(2, [8], double.PositiveInfinity, 2, true, 0), new LocalPolyOptions(2, [8], double.PositiveInfinity, 2, true, 0), new LocalPolyOptions(2, [8], double.PositiveInfinity, 2, true, 0));
        MaxErr(h.Smooth(slightlyOff, Fps, 0), slightlyOff).Should().BeLessThan(1e-6);
    }

    [Fact]
    public void Held_frame_flags_follow_the_alternation_of_edge_differences_not_a_fixed_floor()
    {
        // Moving frames 6..25 levels, repeats 1..2 levels (codec residue), as on the hold-cadence scene.
        double[] s = [0, 0.9, 1.0, 0.7, 2.4, 2.3, 6.0, 1.6, 6.8, 1.6, 10.4, 1.1, 12.9, 1.7];
        bool[] held = HeldFrameDetector.FromEdgeDiff(s);
        held[7].Should().BeTrue();
        held[9].Should().BeTrue();
        held[11].Should().BeTrue();
        held[8].Should().BeFalse();
        held[10].Should().BeFalse();
        held[12].Should().BeFalse();

        // A smoothly decelerating plate has no repeats.
        bool[] none = HeldFrameDetector.FromEdgeDiff([0, 12, 11, 10, 9, 8, 7, 6, 5, 4.5, 4, 3.6, 3.2]);
        none.Count(h => h).Should().Be(0);
    }

    [Fact]
    public void Cadence_wrapper_is_the_identity_without_repeats_and_holds_steps_with_them()
    {
        // Geometry changes on even frames only (repeat on odd), plus noise.
        List<Quad> truth = Truth(120, f => 300 + 4 * (f - f % 2));
        List<Quad> noisy = Noisy(truth, 0.4, 5);
        var held = new bool[120];
        for (int f = 1; f < 120; f += 2)
            held[f] = true;

        var inner = new BoxSmoother(3);
        new CadenceSmoother(inner, new bool[120]).Smooth(noisy, Fps, 0).Should().BeEquivalentTo(inner.Smooth(noisy, Fps, 0));

        IReadOnlyList<Quad> o = new CadenceSmoother(new IdentitySmoother(), held).Smooth(noisy, Fps, 0);
        for (int f = 0; f < 120; f += 2)
        {
            o[f].X0.Should().Be(o[f + 1].X0); // a repeat pair is one value: a step, not a ramp
            o[f].X0.Should().BeApproximately((noisy[f].X0 + noisy[f + 1].X0) / 2, 1e-9);
        }
    }

    [Fact]
    public void Lock_holds_a_still_plate_but_never_a_drifting_one()
    {
        SceneData d = Stub();
        List<Quad> still = Truth(120, _ => 500);
        List<Quad> jitter = Noisy(still, 0.15, 9);
        IReadOnlyList<Quad> o = new LockSmoother(null, 4, 6, 8, Candidates.Prior(d, 0.1)).Smooth(jitter, Fps, 0);
        JitterMetrics.Compute(o, still, Fps).SlipRms.Should().BeLessThan(0.02);

        List<Quad> drift = Truth(120, f => 500 + 0.05 * f);
        MaxErr(new LockSmoother(null, 4, 6, 8, Candidates.Prior(d, 0.1)).Smooth(Noisy(drift, 0.02, 2), Fps, 0), drift).Should().BeLessThan(6 * Candidates.Prior(d, 0.1).Prior + 0.2);
    }

    private static List<FrameQuad> Frames(IEnumerable<Quad> q) =>
        q.Select((x, i) => new FrameQuad(i, x.X0 / 1344, x.Y0 / 768, x.X1 / 1344, x.Y1 / 768, x.X2 / 1344, x.Y2 / 768, x.X3 / 1344, x.Y3 / 768, 0.2, 1, 1)).ToList();

    [Fact]
    public void Reject_prototype_hinge_copy_matches_the_shipped_method_and_the_old_line_residual_clips_a_whip()
    {
        // Phase 2 (T-009): the shipped RejectImplausibleSamples now judges by the hinge residual, so the copy's Hinge mode is the
        // one that must equal it; the copy's Production mode is the retired line-only residual, kept to show the defect it had.
        ChromaQuadTracker.Options options = new("green");
        List<FrameQuad> whip = Frames(Truth(240, f => 300 + 30 * Math.Clamp(f - 96, 0, 12)));

        (List<FrameQuad> shippedRun, double shippedNoise) = ChromaQuadTracker.RejectImplausibleSamples(whip, Fps, 480, 274, options);
        (List<FrameQuad> hingeRun, double hingeNoise, int hingeRejected, _) = RejectPrototype.Run(whip, Fps, 480, 274, options, RejectPrototype.Mode.Hinge);
        hingeRun.Select(q => q.X0).Should().Equal(shippedRun.Select(q => q.X0));
        hingeNoise.Should().Be(shippedNoise);
        hingeRejected.Should().Be(0);
        shippedRun.Select(q => q.X0).Should().Equal(whip.Select(q => q.X0));

        (_, _, int lineRejected, _) = RejectPrototype.Run(whip, Fps, 480, 274, options, RejectPrototype.Mode.Production);
        lineRejected.Should().BeGreaterThan(0, "the retired line-only pass rejects the whip's kink frames: that was the defect");
    }

    [Fact]
    public void Hinge_still_rejects_a_spike_and_a_two_frame_excursion()
    {
        ChromaQuadTracker.Options options = new("green");
        var rng = new Random(4);
        List<Quad> clean = Truth(200, f => 300 + 30 * Math.Clamp(f - 96, 0, 12));
        List<Quad> bad = clean.Select(q => q).ToList();
        bad[40] = bad[40] with { X0 = bad[40].X0 + 120, X2 = bad[40].X2 + 120 };   // one-frame spike
        bad[150] = bad[150] with { X1 = bad[150].X1 + 150, X3 = bad[150].X3 + 150 }; // two-frame excursion that persists...
        bad[151] = bad[151] with { X1 = bad[151].X1 + 190, X3 = bad[151].X3 + 190 }; // ...and grows, then the plate is back
        rng.Next();
        (_, _, int rejected, List<int> frames) = RejectPrototype.Run(Frames(bad), Fps, 480, 274, options, RejectPrototype.Mode.Hinge);
        rejected.Should().BeGreaterThanOrEqualTo(3);
        frames.Should().Contain([40, 150, 151]);
    }
}
