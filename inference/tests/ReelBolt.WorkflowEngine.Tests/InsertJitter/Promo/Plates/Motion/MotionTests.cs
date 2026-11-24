#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Tracking;
using Xunit;
using FrameQuad = ReelBolt.WorkflowEngine.Services.Video.ChromaQuadTracker.FrameQuad;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo.Plates.Motion;

/// <summary>Fast tests (no ffmpeg, no plates) for the T-014 instruments: the spectral estimators, the ring tracker on an analytic image, the shake generator, the prototype smoother.</summary>
public class MotionTests
{
    private static double?[][] Coords(Func<int, double> f, int n)
    {
        var o = new double?[8][];
        for (int k = 0; k < 8; k++)
            o[k] = Enumerable.Range(0, n).Select(i => (double?)(f(i) + k)).ToArray();
        return o;
    }

    [Fact]
    public void A_pure_tone_has_its_power_in_its_band_and_unit_coherence_with_itself()
    {
        const double amp = 0.5;
        double?[][] x = Coords(i => amp * Math.Sin(2 * Math.PI * 6.0 * i / 24.0), 240);
        CrossSpectrum? s = CrossSpectrum.Compute(x, x);
        s.Should().NotBeNull();
        // variance of a sine of amplitude a is a^2 / 2; the Hann window and 0.5 Hz bins keep 6 Hz inside the 4-8 Hz band
        s!.Power(s.Sxx, 4, 8).Should().BeApproximately(amp * amp / 2, 0.01);
        s.Power(s.Sxx, 0.5, 2).Should().BeLessThan(0.002);
        s.Coherence(4, 8).Should().BeApproximately(1.0, 1e-6);
    }

    [Fact]
    public void The_band_error_of_a_track_against_a_reference_is_the_2D_rms_of_the_injected_wobble()
    {
        // a 6 Hz sine of amplitude 0.3 on every corner's x only: the 2-D corner error is 0.3 / sqrt(2) in the 4-8 Hz band and nothing below 2 Hz
        Quad?[] reference = Enumerable.Range(0, 240).Select(i => (Quad?)Quad.From(i, (100, 100), (500, 100), (100, 300), (500, 300))).ToArray();
        Quad?[] track = Enumerable.Range(0, 240).Select(i =>
        {
            double d = 0.3 * Math.Sin(2 * Math.PI * 6.0 * i / 24.0);
            return (Quad?)Quad.From(i, (100 + d, 100), (500 + d, 100), (100 + d, 300), (500 + d, 300));
        }).ToArray();
        MotionAnalysis.BandError(track, reference, 4, 8).Should().BeApproximately(0.3 / Math.Sqrt(2), 0.01);
        MotionAnalysis.BandError(track, reference, 0.5, 2).Should().BeLessThan(0.005);
        MotionAnalysis.BandError(track, Enumerable.Repeat<Quad?>(null, 240).ToArray(), 4, 8).Should().Be(double.NaN);
    }

    [Fact]
    public void Independent_noise_has_low_coherence_and_a_shared_tone_is_found_through_it()
    {
        var rng = new Random(5);
        double G() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        double[] tone = Enumerable.Range(0, 480).Select(i => 0.3 * Math.Sin(2 * Math.PI * 5.0 * i / 24.0 + 0.7)).ToArray();
        double?[][] x = Enumerable.Range(0, 8).Select(_ => tone.Select(t => (double?)(t + 0.3 * G())).ToArray()).ToArray();
        double?[][] y = Enumerable.Range(0, 8).Select(_ => tone.Select(t => (double?)(t + 0.3 * G())).ToArray()).ToArray();
        CrossSpectrum s = CrossSpectrum.Compute(x, y)!;
        // shared power of the tone (0.3^2 / 2 = 0.045) is recovered from the real part of the cross-spectrum within a band
        s.SharedPower(4, 8).Should().BeApproximately(0.045, 0.012);
        CrossSpectrum noiseOnly = CrossSpectrum.Compute(
            Enumerable.Range(0, 8).Select(_ => Enumerable.Range(0, 480).Select(_ => (double?)G()).ToArray()).ToArray(),
            Enumerable.Range(0, 8).Select(_ => Enumerable.Range(0, 480).Select(_ => (double?)G()).ToArray()).ToArray())!;
        noiseOnly.Coherence(2, 12.01).Should().BeLessThan(0.1);
    }

    [Fact]
    public void The_shake_generator_hits_its_rms_and_stays_in_band()
    {
        double[] s = Shake.Series("t", 480, 24, 0.3, 2, 8);
        Math.Sqrt(s.Average(v => v * v)).Should().BeApproximately(0.3, 1e-9);
        CrossSpectrum sp = CrossSpectrum.Compute([s.Select(v => (double?)v).ToArray()], [s.Select(v => (double?)v).ToArray()])!;
        sp.Power(sp.Sxx, 1.5, 9).Should().BeGreaterThan(0.8 * sp.Power(sp.Sxx, 0, 12.01));
    }

    [Fact]
    public void Homography_from_four_points_round_trips_and_inverts()
    {
        (double X, double Y)[] src = [(0, 0), (100, 0), (0, 60), (100, 60)];
        (double X, double Y)[] dst = [(10, 12), (118, 9), (4, 80), (131, 77)];
        double[] h = Homog.From4(src, dst);
        for (int i = 0; i < 4; i++)
        {
            (double x, double y) = Homog.Apply(h, src[i].X, src[i].Y);
            x.Should().BeApproximately(dst[i].X, 1e-9);
            y.Should().BeApproximately(dst[i].Y, 1e-9);
        }

        (double bx, double by) = Homog.Apply(Homog.Inverse(h), 50, 40);
        (double fx, double fy) = Homog.Apply(h, bx, by);
        fx.Should().BeApproximately(50, 1e-9);
        fy.Should().BeApproximately(40, 1e-9);
    }

    private static byte[] Scene(int w, int h, double dx, double dy)
    {
        // smooth textured analytic picture shifted by (dx, dy): a sum of sinusoids, band-limited so bilinear LK is exact to a small fraction of a pixel
        var g = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double u = x - dx, v = y - dy;
                double val = 110 + 40 * Math.Sin(u / 9.0) * Math.Cos(v / 13.0) + 30 * Math.Sin((u + 2 * v) / 17.0) + 20 * Math.Cos((3 * u - v) / 23.0);
                g[y * w + x] = (byte)Math.Clamp((int)Math.Round(val), 0, 255);
            }
        }

        return g;
    }

    [Fact]
    public void The_ring_estimator_recovers_a_sub_pixel_global_shift()
    {
        const int w = 640, h = 360;
        var a = Pyramid.Build(Scene(w, h, 0, 0), w, h, 3, 1.0);
        var b = Pyramid.Build(Scene(w, h, 0.4, -0.25), w, h, 3, 1.0);
        Quad plate = Quad.From(0, (230, 120), (410, 120), (230, 240), (410, 240));
        var o = new RingOptions(Params: 6, DMax: 120, MinPixels: 500, GradMin: 0.5);
        RingPair p = RingMotion.Estimate(a, b, plate, o, 0);
        p.Ok.Should().BeTrue(p.Why);
        Quad moved = RingMotion.PredictCorners(plate, p.H);
        for (int c = 0; c < 4; c++)
        {
            (moved.Corner(c).X - plate.Corner(c).X).Should().BeApproximately(0.4, 0.04);
            (moved.Corner(c).Y - plate.Corner(c).Y).Should().BeApproximately(-0.25, 0.04);
        }
    }

    [Fact]
    public void The_prototype_smoother_with_production_options_is_the_production_smoother()
    {
        const int sw = 1344, sh = 768, gw = 448, gh = 256;
        var rng = new Random(11);
        double G() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        var run = new List<FrameQuad>();
        for (int f = 0; f < 120; f++)
        {
            double x = 300 + 1.5 * f + 0.3 * G(), y = 200 + 0.2 * G();
            run.Add(new FrameQuad(f, x / sw, y / sh, (x + 600) / sw, y / sh, x / sw, (y + 300) / sh, (x + 600) / sw, (y + 300) / sh, 0.2, 1, 1, default, default));
        }

        bool[] held = new bool[200];
        List<FrameQuad> prod = InsertTrackSmoother.Smooth(run, held, sw / (double)gw, gw, gh);
        List<FrameQuad> proto = ProtoTrackSmoother.Smooth(run, held, sw / (double)gw, gw, gh, ProtoOptions.Production);
        proto.Should().HaveCount(prod.Count);
        for (int i = 0; i < prod.Count; i++)
            proto[i].Should().Be(prod[i]);
    }

    [Fact]
    public void A_leak_of_one_returns_the_raw_track()
    {
        const int sw = 1344, gw = 448, gh = 256;
        var run = new List<FrameQuad>();
        var rng = new Random(2);
        for (int f = 0; f < 60; f++)
        {
            double x = 300 + 0.4 * (rng.NextDouble() - 0.5);
            run.Add(new FrameQuad(f, x / sw, 0.2, (x + 600) / sw, 0.2, x / sw, 0.6, (x + 600) / sw, 0.6, 0.2, 1, 1, default, default));
        }

        List<FrameQuad> sm = ProtoTrackSmoother.Smooth(run, new bool[100], sw / (double)gw, gw, gh, ProtoOptions.Production with { Leak = 1.0 });
        for (int i = 0; i < run.Count; i++)
            sm[i].X0.Should().BeApproximately(run[i].X0, 1e-9);
    }
}
