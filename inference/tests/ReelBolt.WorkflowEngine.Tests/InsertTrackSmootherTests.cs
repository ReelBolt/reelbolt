#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Tracking;
using ReelBolt.WorkflowEngine.Tests.InsertJitter;
using ReelBolt.WorkflowEngine.Tests.InsertJitter.Combined;
using ReelBolt.WorkflowEngine.Tests.InsertJitter.Smoothing;
using Xunit;
using FrameQuad = ReelBolt.WorkflowEngine.Services.Video.ChromaQuadTracker.FrameQuad;
using TruthQuad = ReelBolt.WorkflowEngine.Tests.InsertJitter.Quad;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The insert-track smoother (<see cref="InsertTrackSmoother"/>, initiative insert-tracking-smoothness F2b): analytic tracks with known truth, no
/// ffmpeg. Source 1344x768 seen through a 448x256 detection grid (3 source px per grid px, so the noise prior is 0.3 source px), 24 fps.
/// </summary>
public class InsertTrackSmootherTests
{
    private const int SW = 1344, SH = 768, GW = 448, GH = 256;
    private const double Ratio = 3.0, Fps = 24;

    private static readonly bool[] NoneHeld = new bool[2000];

    private static double Gaussian(Random rng) =>
        Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());

    /// <summary>A 600x300 plate whose top-left corner is at (<paramref name="x"/>, <paramref name="y"/>) source px.</summary>
    private static TruthQuad Plate(int frame, double x, double y = 200) =>
        TruthQuad.From(frame / Fps, (x, y), (x + 600, y), (x, y + 300), (x + 600, y + 300));

    private static FrameQuad ToFrame(int frame, TruthQuad q, InsertPlateBows bows = default, bool surface = false)
    {
        (double, double, double, double, double, double, double, double) s = surface
            ? (q.X0 / SW, q.Y0 / SH, q.X1 / SW, q.Y1 / SH, q.X2 / SW, q.Y2 / SH, q.X3 / SW, q.Y3 / SH)
            : default;
        return new FrameQuad(frame, q.X0 / SW, q.Y0 / SH, q.X1 / SW, q.Y1 / SH, q.X2 / SW, q.Y2 / SH, q.X3 / SW, q.Y3 / SH, 0.2, 1, 1, bows, s);
    }

    private static TruthQuad Noisy(TruthQuad q, double sigma, Random rng) => q with
    {
        X0 = q.X0 + sigma * Gaussian(rng),
        Y0 = q.Y0 + sigma * Gaussian(rng),
        X1 = q.X1 + sigma * Gaussian(rng),
        Y1 = q.Y1 + sigma * Gaussian(rng),
        X2 = q.X2 + sigma * Gaussian(rng),
        Y2 = q.Y2 + sigma * Gaussian(rng),
        X3 = q.X3 + sigma * Gaussian(rng),
        Y3 = q.Y3 + sigma * Gaussian(rng)
    };

    private static List<TruthQuad> Truth(int n, Func<int, double> x, Func<int, double>? y = null) =>
        Enumerable.Range(0, n).Select(f => Plate(f, x(f), y?.Invoke(f) ?? 200)).ToList();

    private static List<FrameQuad> Frames(IEnumerable<TruthQuad> q) => q.Select((x, f) => ToFrame(f, x)).ToList();

    private static List<TruthQuad> InSourcePx(IEnumerable<FrameQuad> run) =>
        run.Select(q => TruthQuad.FromFrameQuad(q, SW, SH, q.FrameIndex / Fps, preferSurface: false)).ToList();

    private static JitterStats Score(IEnumerable<FrameQuad> output, IReadOnlyList<TruthQuad> truth) =>
        JitterMetrics.Compute(InSourcePx(output), truth, Fps);

    private static List<FrameQuad> Smooth(IReadOnlyList<FrameQuad> run, bool[]? held = null) =>
        InsertTrackSmoother.Smooth(run, held ?? NoneHeld, Ratio, GW, GH);

    [Fact]
    public void Static_noise_is_removed()
    {
        var rng = new Random(1);
        List<TruthQuad> truth = Truth(240, _ => 500);
        List<FrameQuad> raw = Frames(truth.Select(q => Noisy(q, 0.3, rng)));

        double rawSlip = Score(raw, truth).SlipRms;
        JitterStats smoothed = Score(Smooth(raw), truth);

        rawSlip.Should().BeGreaterThan(0.5, "iid noise of 0.3 px per coordinate makes a 2 x 0.3 px frame-to-frame slip");
        smoothed.SlipRms.Should().BeLessThanOrEqualTo(0.045, "measured 0.0375 against 0.6 raw: the 25-frame window averages ~12 independent samples");
        smoothed.ErrMean.Should().BeLessThan(0.15);
    }

    [Fact]
    public void Slow_ramp_has_no_lag()
    {
        var rng = new Random(2);
        List<TruthQuad> truth = Truth(240, f => 100 + 2.0 * f);
        List<FrameQuad> raw = Frames(truth.Select(q => Noisy(q, 0.3, rng)));

        JitterStats smoothed = Score(Smooth(raw), truth);

        smoothed.LagLsFrames.Should().NotBeNull();
        Math.Abs(smoothed.LagLsFrames!.Value).Should().BeLessThanOrEqualTo(0.1, "a quadratic fit follows a ramp with no delay");
        smoothed.SlipRms.Should().BeLessThanOrEqualTo(0.05);
    }

    [Fact]
    public void Hard_kink_passes_through()
    {
        // A clip that cuts mid-move: 12 px a frame to the right, then 12 px a frame back (a velocity change of 24 px a frame, 80 sigma). A wide smoother
        // rounds the corner by several pixels; the samples of the kink must come out as measured.
        var rng = new Random(3);
        List<TruthQuad> truth = Truth(61, f => 300 + 12.0 * (f < 30 ? f : 60 - f));
        List<FrameQuad> raw = Frames(truth.Select(q => Noisy(q, 0.1, rng)));

        JitterStats smoothed = Score(Smooth(raw), truth);

        smoothed.ErrMax.Should().BeLessThanOrEqualTo(0.4, "measured 0.31: the kink frames are the raw samples (0.1 px noise), not a rounded corner");
        smoothed.ErrMean.Should().BeLessThan(0.15);
    }

    [Fact]
    public void Held_cadence_steps_are_kept()
    {
        // Generated footage repeats every other frame: the truth changes on even frames only (a 4 px step), odd frames repeat it.
        var rng = new Random(4);
        List<TruthQuad> truth = Truth(240, f => 100 + 4.0 * (f / 2));
        var held = new bool[240];
        for (int f = 1; f < 240; f += 2)
            held[f] = true;
        List<FrameQuad> raw = Frames(truth.Select(q => Noisy(q, 0.2, rng)));

        List<FrameQuad> output = Smooth(raw, held);

        for (int f = 0; f < 240; f += 2)
        {
            output[f + 1].X0.Should().Be(output[f].X0, "a repeat pair is one value: a step, not a ramp");
            output[f + 1].Y3.Should().Be(output[f].Y3);
        }

        JitterStats stats = Score(output, truth);
        stats.ErrMax.Should().BeLessThanOrEqualTo(0.5, "no half-step values: a smoother that ignored the cadence would sit 2 px off every frame");
        stats.ErrMean.Should().BeLessThan(0.15);
    }

    [Fact]
    public void Held_cadence_beats_ignoring_it()
    {
        var rng = new Random(5);
        List<TruthQuad> truth = Truth(240, f => 100 + 4.0 * (f / 2));
        var held = new bool[240];
        for (int f = 1; f < 240; f += 2)
            held[f] = true;
        List<FrameQuad> raw = Frames(truth.Select(q => Noisy(q, 0.2, rng)));

        double aware = Score(Smooth(raw, held), truth).ErrMean;
        double blind = Score(Smooth(raw), truth).ErrMean;

        aware.Should().BeLessThan(blind);
    }

    [Fact]
    public void Bows_are_smoothed_with_corners()
    {
        var rng = new Random(6);
        List<TruthQuad> truth = Truth(240, f => 100 + 2.0 * f);
        InsertPlateBows constant = new(new InsertEdgeBow(0.060, 0.010), new InsertEdgeBow(0.030, 0.0), new InsertEdgeBow(0.050, -0.010), new InsertEdgeBow(0.025, 0.005));
        InsertPlateBows NoisyBows(InsertPlateBows b) => new(
            new InsertEdgeBow(b.Top.Bow + 0.004 * Gaussian(rng), b.Top.Skew + 0.004 * Gaussian(rng)),
            new InsertEdgeBow(b.Right.Bow + 0.004 * Gaussian(rng), b.Right.Skew + 0.004 * Gaussian(rng)),
            new InsertEdgeBow(b.Bottom.Bow + 0.004 * Gaussian(rng), b.Bottom.Skew + 0.004 * Gaussian(rng)),
            new InsertEdgeBow(b.Left.Bow + 0.004 * Gaussian(rng), b.Left.Skew + 0.004 * Gaussian(rng)));
        List<FrameQuad> raw = truth.Select((q, f) => ToFrame(f, Noisy(q, 0.3, rng), NoisyBows(constant))).ToList();

        List<FrameQuad> output = Smooth(raw);

        double BowRms(IEnumerable<FrameQuad> run) => Math.Sqrt(run.Average(q =>
            (Math.Pow(q.Bows.Top.Bow - constant.Top.Bow, 2) + Math.Pow(q.Bows.Right.Bow - constant.Right.Bow, 2) +
             Math.Pow(q.Bows.Bottom.Bow - constant.Bottom.Bow, 2) + Math.Pow(q.Bows.Left.Bow - constant.Left.Bow, 2) +
             Math.Pow(q.Bows.Top.Skew - constant.Top.Skew, 2) + Math.Pow(q.Bows.Right.Skew - constant.Right.Skew, 2) +
             Math.Pow(q.Bows.Bottom.Skew - constant.Bottom.Skew, 2) + Math.Pow(q.Bows.Left.Skew - constant.Left.Skew, 2)) / 8));
        BowRms(output).Should().BeLessThan(BowRms(raw) / 3, "noisy bows are smoothed along with the corners");
    }

    [Fact]
    public void Bows_and_surface_corners_share_the_corner_fit()
    {
        // The width and the robust weights come from the pinned corners alone. A track that cuts hard at frame 30 passes the cut frames
        // through raw, so a bow spike sitting on the cut frame must come out untouched too, while the same spike far from the cut is averaged away.
        List<TruthQuad> truth = Truth(61, f => 300 + 12.0 * (f < 30 ? f : 60 - f));
        InsertPlateBows spike = new(new InsertEdgeBow(0.2, 0), new InsertEdgeBow(0.2, 0), new InsertEdgeBow(0.2, 0), new InsertEdgeBow(0.2, 0));
        List<FrameQuad> raw = truth.Select((q, f) => ToFrame(f, q, f is 30 or 50 ? spike : InsertPlateBows.Flat, surface: true)).ToList();

        List<FrameQuad> output = Smooth(raw);

        output[30].Bows.Top.Bow.Should().Be(0.2, "the corners passed this frame through raw, and the bows ride the same decision");
        output[50].Bows.Top.Bow.Should().BeLessThan(0.05, "far from any cut the same spike is smoothed away by the wide window");
    }

    [Fact]
    public void Surface_corners_ride_the_pinned_fit_and_keep_their_offset_from_the_corners()
    {
        var rng = new Random(7);
        List<TruthQuad> truth = Truth(240, f => 100 + 2.0 * f);
        // Surface corners are the pinned ones; the fitted corners sit a constant 1.5 px off them. One linear fit with shared weights keeps that offset exactly.
        List<FrameQuad> raw = truth.Select((q, f) =>
        {
            TruthQuad n = Noisy(q, 0.3, rng);
            FrameQuad frame = ToFrame(f, n, surface: true);
            return frame with { X0 = frame.X0 + 1.5 / SW, Y3 = frame.Y3 - 1.5 / SH };
        }).ToList();

        List<FrameQuad> output = Smooth(raw);

        for (int f = 0; f < 240; f++)
        {
            (output[f].X0 - output[f].Surface.X0).Should().BeApproximately(1.5 / SW, 1e-9);
            (output[f].Y3 - output[f].Surface.Y3).Should().BeApproximately(-1.5 / SH, 1e-9);
        }

        Score(output.Select(q => q with { X0 = q.Surface.X0, Y0 = q.Surface.Y0, X1 = q.Surface.X1, Y1 = q.Surface.Y1, X2 = q.Surface.X2, Y2 = q.Surface.Y2, X3 = q.Surface.X3, Y3 = q.Surface.Y3 }), truth)
            .SlipRms.Should().BeLessThan(0.1, "the pinned corners are smoothed");
    }

    [Fact]
    public void A_clean_track_comes_out_unchanged_and_other_fields_are_kept()
    {
        List<TruthQuad> truth = Truth(60, f => 100 + 2.0 * f);
        List<FrameQuad> raw = truth.Select((q, f) => ToFrame(f, q) with { FillScore = 0.9, EdgeContrast = 0.7, Interpolated = f % 7 == 3 }).ToList();

        List<FrameQuad> output = Smooth(raw);

        output.Select(q => q.FrameIndex).Should().Equal(raw.Select(q => q.FrameIndex));
        output.Select(q => q.Interpolated).Should().Equal(raw.Select(q => q.Interpolated));
        output.Should().OnlyContain(q => q.FillScore == 0.9 && q.EdgeContrast == 0.7);
        for (int f = 0; f < 60; f++)
            output[f].X0.Should().BeApproximately(raw[f].X0, 1e-9, "a noise-free quadratic path is reproduced exactly");
    }

    [Fact]
    public void Fewer_than_five_frames_are_returned_as_they_are()
    {
        List<FrameQuad> raw = Frames(Truth(4, f => 100 + 50.0 * f));

        List<FrameQuad> output = Smooth(raw);

        output.Should().Equal(raw);
    }

    [Fact]
    public void Production_smoother_reproduces_the_measured_prototype_arithmetic()
    {
        // The bench numbers in plan.md Part A (K4ra behind the cadence wrapper) were measured with the test project's prototype. The production port
        // must be the same arithmetic: ramp, a hard kink, repeated frames and noise, identical to nine decimals.
        var rng = new Random(9);
        var held = new bool[200];
        for (int f = 101; f < 200; f += 2)
            held[f] = true;
        List<TruthQuad> truth = Truth(200, f => f < 100 ? 200 + 3.0 * f : 500 + 4.0 * ((f - 100) / 2) - (f > 150 ? 10.0 * (f - 150) : 0));
        List<TruthQuad> noisy = truth.Select(q => Noisy(q, 0.25, rng)).ToList();
        List<FrameQuad> raw = Frames(noisy);

        List<FrameQuad> production = Smooth(raw, held);
        IReadOnlyList<TruthQuad> prototype = new CadenceSmoother(new LocalPolySmoother(CombinedPipeline.K4raOptions, CombinedPipeline.Prior(SW, GW, 0.1)), held)
            .Smooth(noisy, Fps, 0);

        List<TruthQuad> asSource = InSourcePx(production);
        for (int i = 0; i < 200; i++)
        {
            asSource[i].X0.Should().BeApproximately(prototype[i].X0, 1e-9, $"frame {i}");
            asSource[i].Y3.Should().BeApproximately(prototype[i].Y3, 1e-9, $"frame {i}");
        }
    }

    [Fact]
    public void Smoothing_is_deterministic()
    {
        var rng = new Random(8);
        List<FrameQuad> raw = Frames(Truth(120, f => 100 + 2.0 * f).Select(q => Noisy(q, 0.3, rng)));

        Smooth(raw).Should().Equal(Smooth(raw));
    }

    [Fact]
    public void Held_frames_are_detected_from_the_grid_edges()
    {
        // A green plate on grey that moves 3 grid px on even frames and is repeated on odd ones (a frame-doubled clip).
        const int w = 96, h = 64, frames = 40;
        var grid = new byte[frames * w * h * 3];
        var run = new List<FrameQuad>();
        for (int f = 0; f < frames; f++)
        {
            int x0 = 10 + 3 * (f / 2);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool plate = x >= x0 && x < x0 + 30 && y >= 16 && y < 48;
                    int p = (f * w * h + y * w + x) * 3;
                    grid[p] = (byte)(plate ? 20 : 90);
                    grid[p + 1] = (byte)(plate ? 200 : 90);
                    grid[p + 2] = (byte)(plate ? 30 : 95);
                }
            }

            run.Add(new FrameQuad(f, x0 / (double)w, 16.0 / h, (x0 + 30) / (double)w, 16.0 / h, x0 / (double)w, 48.0 / h, (x0 + 30) / (double)w, 48.0 / h, 0.2, 1, 1));
        }

        bool[] held = InsertTrackSmoother.DetectHeld(grid, frames, w, h, run);

        held.Length.Should().Be(frames);
        for (int f = 2; f < frames; f++)
            held[f].Should().Be(f % 2 == 1, $"frame {f}: odd frames repeat the previous picture");
    }

    [Fact]
    public void A_steadily_moving_plate_has_no_held_frames()
    {
        const int w = 96, h = 64, frames = 40;
        var grid = new byte[frames * w * h * 3];
        var run = new List<FrameQuad>();
        for (int f = 0; f < frames; f++)
        {
            int x0 = 6 + f;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool plate = x >= x0 && x < x0 + 30 && y >= 16 && y < 48;
                    int p = (f * w * h + y * w + x) * 3;
                    grid[p] = (byte)(plate ? 20 : 90);
                    grid[p + 1] = (byte)(plate ? 200 : 90);
                    grid[p + 2] = (byte)(plate ? 30 : 95);
                }
            }

            run.Add(new FrameQuad(f, x0 / (double)w, 16.0 / h, (x0 + 30) / (double)w, 16.0 / h, x0 / (double)w, 48.0 / h, (x0 + 30) / (double)w, 48.0 / h, 0.2, 1, 1));
        }

        InsertTrackSmoother.DetectHeld(grid, frames, w, h, run).Count(x => x).Should().Be(0);
    }

    private static bool IsFiniteQuad(FrameQuad q) =>
        new[] { q.X0, q.Y0, q.X1, q.Y1, q.X2, q.Y2, q.X3, q.Y3 }.All(double.IsFinite)
        && new[]
        {
            q.Bows.Top.Bow, q.Bows.Top.Skew, q.Bows.Right.Bow, q.Bows.Right.Skew,
            q.Bows.Bottom.Bow, q.Bows.Bottom.Skew, q.Bows.Left.Bow, q.Bows.Left.Skew
        }.All(double.IsFinite);

    private static List<FrameQuad> NoisyRamp(int seed) =>
        Frames(Truth(60, f => 100 + 2.0 * f).Select(q => Noisy(q, 0.3, new Random(seed + (int)q.T))));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Non_finite_corner_is_passed_through_and_does_not_poison_neighbours(double bad)
    {
        List<FrameQuad> clean = NoisyRamp(10);
        List<FrameQuad> dirty = [.. clean];
        dirty[30] = dirty[30] with { X1 = bad };

        List<FrameQuad> cleanOut = Smooth(clean);
        List<FrameQuad> dirtyOut = Smooth(dirty);

        dirtyOut.Should().HaveCount(60);
        dirtyOut[30].Should().Be(dirty[30], "a non-finite frame is its own input, untouched");
        for (int i = 0; i < 60; i++)
        {
            if (i == 30)
                continue;
            IsFiniteQuad(dirtyOut[i]).Should().BeTrue($"frame {i} must stay finite");
            Math.Abs(dirtyOut[i].X0 - cleanOut[i].X0).Should().BeLessThanOrEqualTo(0.5 / SW, $"frame {i} X0 stays near the clean smoothed run");
            Math.Abs(dirtyOut[i].Y3 - cleanOut[i].Y3).Should().BeLessThanOrEqualTo(0.5 / SH, $"frame {i} Y3 stays near the clean smoothed run");
        }
    }

    [Fact]
    public void Non_finite_bow_value_does_not_poison_neighbours()
    {
        var bows = new InsertPlateBows(new InsertEdgeBow(0.01, 0.0), new InsertEdgeBow(0.0, 0.0), new InsertEdgeBow(0.01, 0.0), new InsertEdgeBow(0.0, 0.0));
        List<FrameQuad> run = Truth(60, f => 100 + 2.0 * f).Select((q, f) => ToFrame(f, q, bows)).ToList();
        run[30] = run[30] with { Bows = bows with { Top = new InsertEdgeBow(double.NaN, 0.0) } };

        List<FrameQuad> output = Smooth(run);

        output.Should().HaveCount(60);
        for (int i = 0; i < 60; i++)
        {
            if (i == 30)
                continue;
            IsFiniteQuad(output[i]).Should().BeTrue($"frame {i} must stay finite");
        }

        double.IsNaN(output[30].Bows.Top.Bow).Should().BeTrue("the non-finite frame is passed through, not invented");
    }

    [Fact]
    public void All_non_finite_run_is_returned_same_length_without_throwing()
    {
        List<FrameQuad> run = NoisyRamp(20).Select(q => q with { Y2 = double.NaN }).ToList();

        List<FrameQuad> output = [];
        Action act = () => output = Smooth(run);

        act.Should().NotThrow();
        output.Should().HaveCount(60);
    }
}
