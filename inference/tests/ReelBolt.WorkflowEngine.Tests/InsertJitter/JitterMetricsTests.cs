#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter;

/// <summary>Fast, always-on checks of the jitter metric definitions (no ffmpeg, no clips).</summary>
public class JitterMetricsTests
{
    private const double Fps = 24;

    private static Quad Static(double t) => Quad.From(t, (100, 100), (700, 102), (98, 500), (702, 498));

    /// <summary>A truth that moves with several incommensurate components, so its velocity autocorrelation has one sharp peak.</summary>
    private static Quad Moving(int n)
    {
        double t = n / Fps;
        double dx = 40 * Math.Sin(2 * Math.PI * 0.31 * t) + 12 * Math.Sin(2 * Math.PI * 1.7 * t + 0.4) + 3 * Math.Sin(2 * Math.PI * 4.9 * t + 1.1);
        double dy = 25 * Math.Sin(2 * Math.PI * 0.23 * t + 0.8) + 9 * Math.Sin(2 * Math.PI * 2.3 * t) + 2 * Math.Sin(2 * Math.PI * 5.3 * t + 2.0);
        double s = 1 + 0.05 * Math.Sin(2 * Math.PI * 0.4 * t);
        return Quad.From(t, (100 + dx, 100 + dy), (700 * s + dx, 102 + dy), (98 + dx, 500 * s + dy), (702 * s + dx, 498 * s + dy));
    }

    private static double Gaussian(Random rng)
    {
        double u1 = 1 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    [Fact]
    public void Iid_gaussian_noise_on_a_static_truth_has_slip_rms_two_sigma()
    {
        // Per coordinate e ~ N(0, 1). Frame-to-frame difference has variance 2 per coordinate, so the
        // 2-D slip |de|^2 has expectation 4: SlipRms = 2*sigma = 2.0. ErrMean is the Rayleigh mean sqrt(pi/2).
        var rng = new Random(12345);
        const int frames = 4000;
        var truth = Enumerable.Range(0, frames).Select(n => Static(n / Fps)).ToList();
        var track = truth.Select(q => new Quad(q.T,
            q.X0 + Gaussian(rng), q.Y0 + Gaussian(rng), q.X1 + Gaussian(rng), q.Y1 + Gaussian(rng),
            q.X2 + Gaussian(rng), q.Y2 + Gaussian(rng), q.X3 + Gaussian(rng), q.Y3 + Gaussian(rng))).ToList();

        JitterStats s = JitterMetrics.Compute(track, truth, Fps);

        s.SlipRms.Should().BeApproximately(2.0, 2.0 * 0.05);
        s.ErrMean.Should().BeApproximately(Math.Sqrt(Math.PI / 2), 0.05);
        // e minus its 25-sample centred mean (itself included): variance sigma^2*(1-1/25) per coordinate, two coordinates.
        s.HfRms.Should().BeApproximately(Math.Sqrt(2 * (1 - 1.0 / 25)), 0.05);
        s.LagFrames.Should().BeNull("a static truth has no velocity to correlate");
        s.N.Should().Be(frames);
    }

    [Fact]
    public void A_three_frame_delayed_copy_of_a_moving_truth_lags_by_three_frames()
    {
        const int delay = 3;
        var truth = Enumerable.Range(0, 400).Select(Moving).ToList();
        var track = Enumerable.Range(delay, 400 - delay).Select(n => Moving(n - delay) with { T = n / Fps }).ToList();

        JitterStats s = JitterMetrics.Compute(track, truth, Fps);

        s.LagFrames.Should().NotBeNull();
        s.LagFrames!.Value.Should().BeApproximately(3.0, 0.2);
        s.LagLsFrames.Should().NotBeNull();
        s.LagLsFrames!.Value.Should().BeApproximately(3.0, 0.2);
    }

    [Fact]
    public void A_constant_two_pixel_offset_is_an_offset_not_wobble()
    {
        var truth = Enumerable.Range(0, 300).Select(Moving).ToList();
        var track = truth.Select(q => new Quad(q.T, q.X0 + 2, q.Y0, q.X1 + 2, q.Y1, q.X2 + 2, q.Y2, q.X3 + 2, q.Y3)).ToList();

        JitterStats s = JitterMetrics.Compute(track, truth, Fps);

        s.ErrMean.Should().BeApproximately(2.0, 1e-9);
        s.ErrMax.Should().BeApproximately(2.0, 1e-9);
        s.SlipRms.Should().BeApproximately(0.0, 1e-9);
        s.ContentSlipRms.Should().BeApproximately(0.0, 1e-9);
        s.HfRms.Should().BeApproximately(0.0, 1e-9);
        s.DriftPxPerSec.Should().BeApproximately(0.0, 1e-9);
        s.LagFrames!.Value.Should().BeApproximately(0.0, 0.05);
    }

    [Fact]
    public void A_linear_error_ramp_is_drift_and_a_zero_slip_free_of_noise()
    {
        var truth = Enumerable.Range(0, 240).Select(n => Static(n / Fps)).ToList();
        // 0.5 px per second of x drift on every corner.
        var track = truth.Select(q => new Quad(q.T, q.X0 + 0.5 * q.T, q.Y0, q.X1 + 0.5 * q.T, q.Y1, q.X2 + 0.5 * q.T, q.Y2, q.X3 + 0.5 * q.T, q.Y3)).ToList();

        JitterStats s = JitterMetrics.Compute(track, truth, Fps);

        s.DriftPxPerSec.Should().BeApproximately(0.5, 1e-6);
        s.SlipRms.Should().BeApproximately(0.5 / Fps, 1e-9);
    }

    [Fact]
    public void Track_samples_missing_from_the_track_are_skipped_not_interpolated()
    {
        var truth = Enumerable.Range(0, 100).Select(n => Static(n / Fps)).ToList();
        var track = truth.Where(q => (int)Math.Round(q.T * Fps) is < 40 or > 60).ToList();

        JitterStats s = JitterMetrics.Compute(track, truth, Fps);

        s.N.Should().Be(100 - 21);
        s.SlipRms.Should().BeApproximately(0.0, 1e-9);
    }

    [Fact]
    public void Truth_at_a_higher_rate_is_sampled_at_the_tracks_own_times()
    {
        var truth = Enumerable.Range(0, 240).Select(n => Moving(n)).ToList();
        var track = truth.Where(q => (int)Math.Round(q.T * Fps) % 2 == 0).ToList();

        JitterStats s = JitterMetrics.Compute(track, truth, Fps / 2);

        s.N.Should().Be(120);
        s.ErrMax.Should().BeApproximately(0.0, 1e-9);
    }

    [Fact]
    public void Unit_square_map_hits_the_corners_and_the_centre_of_a_perspective_quad()
    {
        Quad q = Quad.From(0, (100, 80), (700, 120), (60, 500), (760, 520));
        UnitSquareMap m = q.ToMap();

        m.Map(0, 0).Should().Be((100.0, 80.0));
        AssertNear(m.Map(1, 0), (700, 120));
        AssertNear(m.Map(0, 1), (60, 500));
        AssertNear(m.Map(1, 1), (760, 520));

        // The centre of a projective map is where the DIAGONALS cross.
        (double cx, double cy) = m.Map(0.5, 0.5);
        IntersectDiagonals(q).X.Should().BeApproximately(cx, 1e-9);
        IntersectDiagonals(q).Y.Should().BeApproximately(cy, 1e-9);

        static void AssertNear((double X, double Y) a, (double X, double Y) b)
        {
            a.X.Should().BeApproximately(b.X, 1e-9);
            a.Y.Should().BeApproximately(b.Y, 1e-9);
        }
    }

    [Fact]
    public void No_truth_residual_of_a_quadratic_path_is_zero_and_noise_is_not()
    {
        var smooth = Enumerable.Range(0, 100).Select(n => Quad.From(n / Fps, (100 + 0.01 * n * n, 100), (700, 100 + n), (100, 500), (700, 500 - 0.02 * n * n))).ToList();
        JitterStats a = JitterMetrics.ComputeNoTruth(smooth, Fps);
        a.ResidualRms.Should().BeApproximately(0, 1e-6);
        a.SecondDiffRms.Should().BeGreaterThan(0);

        var rng = new Random(7);
        var noisy = Enumerable.Range(0, 400).Select(n => Quad.From(n / Fps,
            (100 + Gaussian(rng), 100 + Gaussian(rng)), (700 + Gaussian(rng), 100 + Gaussian(rng)),
            (100 + Gaussian(rng), 500 + Gaussian(rng)), (700 + Gaussian(rng), 500 + Gaussian(rng)))).ToList();
        JitterStats b = JitterMetrics.ComputeNoTruth(noisy, Fps);
        // Leave-one-out quadratic through 8 iid neighbours: prediction variance 0.343 sigma^2 plus the sample's own sigma^2,
        // per coordinate, two coordinates: RMS = sqrt(2 * 1.343) = 1.639.
        b.ResidualRms.Should().BeApproximately(Math.Sqrt(2 * 1.343), 0.1);
        b.SecondDiffRms.Should().BeApproximately(Math.Sqrt(2 * 6.0), 0.2);
        b.PeakToPeak.Should().BeGreaterThan(2);
    }

    private static (double X, double Y) IntersectDiagonals(Quad q)
    {
        // TL-BR and TR-BL.
        double x1 = q.X0, y1 = q.Y0, x2 = q.X3, y2 = q.Y3, x3 = q.X1, y3 = q.Y1, x4 = q.X2, y4 = q.Y2;
        double den = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
        double px = ((x1 * y2 - y1 * x2) * (x3 - x4) - (x1 - x2) * (x3 * y4 - y3 * x4)) / den;
        double py = ((x1 * y2 - y1 * x2) * (y3 - y4) - (y1 - y2) * (x3 * y4 - y3 * x4)) / den;
        return (px, py);
    }
}
