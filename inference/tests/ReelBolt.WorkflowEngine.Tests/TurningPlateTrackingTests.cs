using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Tracking a plate that TURNS (docs/video-editing.md "Tracking through a turn"): orientation-
/// aware coarse corners so a plate at 45 degrees is still a quad, corner labels that follow the
/// physical corners through the turn, and the rejection of detections that cannot be the plate.
/// </summary>
public class TurningPlateTrackingTests
{
    private const int GW = 320, GH = 180;

    /// <summary>A square plate of the given side, centred at (cx, cy), rotated by <paramref name="degrees"/>, on a neutral grey background.</summary>
    private static byte[] RotatedPlateFrame(double cx, double cy, double side, double degrees, double aspect = 1.0)
    {
        var frame = new byte[GW * GH * 3];
        for (int i = 0; i < GW * GH; i++)
        {
            frame[i * 3] = 90;
            frame[i * 3 + 1] = 90;
            frame[i * 3 + 2] = 95;
        }

        double th = degrees * Math.PI / 180.0, ct = Math.Cos(th), st = Math.Sin(th);
        double halfW = side / 2, halfH = side / (2 * aspect);
        for (int y = 0; y < GH; y++)
        {
            for (int x = 0; x < GW; x++)
            {
                double dx = x - cx, dy = y - cy;
                double u = dx * ct + dy * st, v = -dx * st + dy * ct;
                if (Math.Abs(u) > halfW || Math.Abs(v) > halfH)
                    continue;
                int p = (y * GW + x) * 3;
                frame[p] = 20;
                frame[p + 1] = 200;
                frame[p + 2] = 30;
            }
        }

        return frame;
    }

    /// <summary>The true corners of <see cref="RotatedPlateFrame"/>'s plate, TL, TR, BR, BL in the plate's own frame, as image coordinates.</summary>
    private static (double X, double Y)[] TrueCorners(double cx, double cy, double side, double degrees, double aspect = 1.0)
    {
        double th = degrees * Math.PI / 180.0, ct = Math.Cos(th), st = Math.Sin(th);
        double hw = side / 2, hh = side / (2 * aspect);
        (double u, double v)[] local = [(-hw, -hh), (hw, -hh), (hw, hh), (-hw, hh)];
        return local.Select(c => (cx + c.u * ct - c.v * st, cy + c.u * st + c.v * ct)).ToArray();
    }

    private static (double X, double Y)[] Corners(ChromaQuadTracker.FrameQuad q) =>
        [(q.X0 * GW - 0.5, q.Y0 * GH - 0.5), (q.X1 * GW - 0.5, q.Y1 * GH - 0.5), (q.X3 * GW - 0.5, q.Y3 * GH - 0.5), (q.X2 * GW - 0.5, q.Y2 * GH - 0.5)];

    /// <summary>Worst corner distance between two TL,TR,BR,BL cycles under the best cyclic relabelling — the labelling is a convention, the geometry is what is being tested.</summary>
    private static double WorstCornerError((double X, double Y)[] got, (double X, double Y)[] truth)
    {
        double best = double.MaxValue;
        for (int k = 0; k < 4; k++)
        {
            double worst = 0;
            for (int i = 0; i < 4; i++)
            {
                var a = got[(i + k) % 4];
                worst = Math.Max(worst, Math.Sqrt(Math.Pow(a.X - truth[i].X, 2) + Math.Pow(a.Y - truth[i].Y, 2)));
            }

            best = Math.Min(best, worst);
        }

        return best;
    }

    // -----------------------------------------------------------------------
    // ChromaQuadOrientation
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(-30)]
    [InlineData(44)]
    [InlineData(60)]  // reads as -30: a rectangle's orientation is only defined modulo a quarter turn
    public void Dominant_angle_is_the_plate_s_own_orientation_modulo_a_quarter_turn(double degrees)
    {
        byte[] frame = RotatedPlateFrame(160, 90, 100, degrees, aspect: 1.6);
        var component = new List<int>();
        for (int p = 0; p < GW * GH; p++)
        {
            if (frame[p * 3 + 1] > 150)
                component.Add(p);
        }

        double angle = ChromaQuadOrientation.DominantAngle(component, GW, GH) * 180 / Math.PI;
        double expected = ChromaQuadOrientation.Normalize(degrees * Math.PI / 180) * 180 / Math.PI;

        angle.Should().BeApproximately(expected, 1.0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(-40)]
    [InlineData(75)]
    public void A_turned_plate_is_fitted_as_a_quad_on_its_true_corners(double degrees)
    {
        // The extreme-point fit in image axes collapses at 45 degrees (two extremes on the same
        // physical corner) and fits a kite anywhere near it. In the plate's own frame the four
        // extremes are always its four corners.
        byte[] frame = RotatedPlateFrame(160, 90, 90, degrees, aspect: 1.5);
        ChromaQuadTracker.FrameQuad? quad = ChromaQuadTracker.DetectQuad(
            frame, 0, GW, GH, 0, new ChromaQuadTracker.Options(GrowExcessFraction: 0.5));

        quad.Should().NotBeNull();
        quad!.FillScore.Should().BeGreaterThan(0.9, "a quad that bounds its own component fills it; a kite fills half of it");
        WorstCornerError(Corners(quad), TrueCorners(160, 90, 90, degrees, aspect: 1.5))
            .Should().BeLessThan(2.0, "every corner lands within two grid pixels of the truth at any angle");
    }

    [Fact]
    public void Corner_labels_follow_the_physical_corners_through_a_turn()
    {
        // A plate turned from 0 to 90 degrees over 60 frames. The per-frame fit renames the
        // corners as it passes 45 degrees; the track must not — content pinned to "top-left"
        // would otherwise snap a quarter turn mid-shot.
        const int frames = 60;
        byte[] pixels = Enumerable.Range(0, frames)
            .SelectMany(i => RotatedPlateFrame(160, 90, 80, 90.0 * i / (frames - 1), aspect: 1.6))
            .ToArray();

        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(
            pixels, frames, GW, GH, 24, new ChromaQuadTracker.Options(GrowExcessFraction: 0.5));

        tracks.Should().ContainSingle();
        List<VideoInsertQuadKeyframe> kf = tracks[0].Keyframes.ToList();
        kf.Count.Should().Be(frames);

        // Each corner moves a little per frame (the plate turns 1.5 degrees a frame, ~1.3 grid px
        // at the corner); a relabelling would move a corner by a whole side at once.
        double worstStep = 0;
        for (int i = 1; i < kf.Count; i++)
        {
            worstStep = Math.Max(worstStep, Math.Max(
                Math.Max(StepPx(kf[i].X0, kf[i].Y0, kf[i - 1].X0, kf[i - 1].Y0), StepPx(kf[i].X1, kf[i].Y1, kf[i - 1].X1, kf[i - 1].Y1)),
                Math.Max(StepPx(kf[i].X2, kf[i].Y2, kf[i - 1].X2, kf[i - 1].Y2), StepPx(kf[i].X3, kf[i].Y3, kf[i - 1].X3, kf[i - 1].Y3))));
        }

        worstStep.Should().BeLessThan(8, "no corner jumps between frames: the labels followed the plate through 45 degrees");

        // And the corner called top-left at the end is where the INITIAL top-left physically went.
        (double X, double Y)[] start = TrueCorners(160, 90, 80, 0, aspect: 1.6);
        (double X, double Y)[] end = TrueCorners(160, 90, 80, 90, aspect: 1.6);
        VideoInsertQuadKeyframe last = kf[^1];
        Math.Sqrt(Math.Pow(last.X0 * GW - 0.5 - end[0].X, 2) + Math.Pow(last.Y0 * GH - 0.5 - end[0].Y, 2)).Should().BeLessThan(4,
            "the physical corner that started top-left is still labelled top-left after the turn");
        Math.Sqrt(Math.Pow(start[0].X - end[0].X, 2) + Math.Pow(start[0].Y - end[0].Y, 2)).Should().BeGreaterThan(40,
            "sanity: the corner really did travel");

        // Normalized keyframe coordinates to a grid-pixel distance.
        static double StepPx(double ax, double ay, double bx, double by) =>
            Math.Sqrt(Math.Pow((ax - bx) * GW, 2) + Math.Pow((ay - by) * GH, 2));
    }

    [Fact]
    public void The_track_s_labelling_is_the_pose_it_holds_longest_not_the_pose_it_started_in()
    {
        // The whip clip in miniature: 15 frames tilted near 45 degrees (where the per-frame
        // labelling is a coin toss), then 45 frames upright. Continuity alone would carry the
        // first frame's choice to the end; the content must read upright on the upright phone.
        var frames = new List<byte[]>();
        for (int i = 0; i < 15; i++)
            frames.Add(RotatedPlateFrame(160, 90, 70, 50 - 50.0 * i / 15, aspect: 0.5));
        for (int i = 0; i < 45; i++)
            frames.Add(RotatedPlateFrame(160, 90, 70, 0, aspect: 0.5));

        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(
            frames.SelectMany(f => f).ToArray(), frames.Count, GW, GH, 24, new ChromaQuadTracker.Options(GrowExcessFraction: 0.5));

        tracks.Should().ContainSingle();
        VideoInsertQuadKeyframe last = tracks[0].Keyframes[^1];
        (double X, double Y)[] upright = TrueCorners(160, 90, 70, 0, aspect: 0.5);
        Math.Sqrt(Math.Pow(last.X0 * GW - 0.5 - upright[0].X, 2) + Math.Pow(last.Y0 * GH - 0.5 - upright[0].Y, 2))
            .Should().BeLessThan(4, "top-left is the upright plate's own top-left for the pose held longest");
        Math.Sqrt(Math.Pow(last.X1 * GW - 0.5 - upright[1].X, 2) + Math.Pow(last.Y1 * GH - 0.5 - upright[1].Y, 2))
            .Should().BeLessThan(4);
    }

    // -----------------------------------------------------------------------
    // RejectImplausibleSamples
    // -----------------------------------------------------------------------

    private static ChromaQuadTracker.FrameQuad Quad(int frame, double x, double y, double w = 0.2, double h = 0.3, bool interpolated = false) =>
        new(frame, x, y, x + w, y, x, y + h, x + w, y + h, w * h, 1.0, 1.0, Interpolated: interpolated);

    private static readonly ChromaQuadTracker.Options Opts = new();

    [Fact]
    public void A_clean_run_is_returned_untouched()
    {
        List<ChromaQuadTracker.FrameQuad> run = Enumerable.Range(0, 30).Select(i => Quad(i, 0.2 + 0.004 * i, 0.3)).ToList();

        (List<ChromaQuadTracker.FrameQuad> cleaned, double noise) = ChromaQuadTracker.RejectImplausibleSamples(run, 24, GW, GH, Opts);

        cleaned.Should().BeSameAs(run);
        noise.Should().BeApproximately(0, 1e-9, "a perfectly linear run has no residual");
    }

    [Fact]
    public void A_corrupted_interior_frame_is_replaced_by_interpolation_and_a_corrupted_end_frame_is_dropped()
    {
        // The phone clip's three bad frames in miniature: a corner yanked a tenth of the frame away
        // mid-run (the reflection streak the edge fit followed), and a first frame that bounded
        // only half the plate.
        List<ChromaQuadTracker.FrameQuad> run = Enumerable.Range(0, 30).Select(i => Quad(i, 0.2 + 0.004 * i, 0.3)).ToList();
        run[12] = run[12] with { Y0 = run[12].Y0 - 0.1, Y1 = run[12].Y1 - 0.1 };
        run[0] = run[0] with { X1 = run[0].X1 - 0.1, X3 = run[0].X3 - 0.1 };

        (List<ChromaQuadTracker.FrameQuad> cleaned, _) = ChromaQuadTracker.RejectImplausibleSamples(run, 24, GW, GH, Opts);

        cleaned[0].FrameIndex.Should().Be(1, "the corrupted first frame has nothing to interpolate from and is dropped");
        cleaned.Count.Should().Be(29);
        ChromaQuadTracker.FrameQuad bridged = cleaned.Single(q => q.FrameIndex == 12);
        bridged.Interpolated.Should().BeTrue();
        bridged.Y0.Should().BeApproximately(0.3, 1e-9, "the bad frame is replaced by the line through its clean neighbours");
        bridged.X0.Should().BeApproximately(0.2 + 0.004 * 12, 1e-9);
        cleaned.Where(q => q.FrameIndex is not 12).Should().OnlyContain(q => !q.Interpolated, "every other detection is kept verbatim");
    }

    [Fact]
    public void A_frame_hold_followed_by_a_double_step_is_within_tolerance()
    {
        // Generated footage holds a frame every so often and then jumps twice as far. That is a
        // property of the source, not a bad detection, and the speed arm of the tolerance is what
        // lets it through: the deviation from a straight line is one frame's travel.
        var run = new List<ChromaQuadTracker.FrameQuad>();
        double x = 0.2;
        for (int i = 0; i < 40; i++)
        {
            if (i % 5 != 4)
                x += 0.01; // 3.2 grid px a frame; the hold on every fifth frame
            run.Add(Quad(i, x, 0.3));
        }

        (List<ChromaQuadTracker.FrameQuad> cleaned, _) = ChromaQuadTracker.RejectImplausibleSamples(run, 24, GW, GH, Opts);

        cleaned.Should().BeSameAs(run, "nothing in a staircase is implausible given the plate's own speed");
    }

    [Fact]
    public void Rejection_is_capped_so_a_noisy_track_is_not_rewritten()
    {
        // Every frame jittered by a different large amount: the greedy pass stops at the cap, and
        // what survives is still a run of detections, not a straight line through nothing.
        var rng = new Random(7);
        List<ChromaQuadTracker.FrameQuad> run = Enumerable.Range(0, 20)
            .Select(i => Quad(i, 0.2 + 0.05 * (rng.NextDouble() - 0.5), 0.3 + 0.05 * (rng.NextDouble() - 0.5)))
            .ToList();

        (List<ChromaQuadTracker.FrameQuad> cleaned, _) = ChromaQuadTracker.RejectImplausibleSamples(run, 24, GW, GH, Opts with { MaxOutlierFraction = 0.25 });

        cleaned.Count(q => !q.Interpolated).Should().BeGreaterThanOrEqualTo(15, "at most a quarter of the detections may be rejected");
    }

    [Fact]
    public void Aligning_labels_picks_the_cyclic_shift_closest_to_the_reference_and_shifts_edges_with_it()
    {
        ChromaQuadTracker.FrameQuad reference = Quad(0, 0.2, 0.3);

        // The same quad with its labels rotated one step: what was TR is now called TL.
        var d = new ChromaQuadTracker.FrameQuad(
            1,
            reference.X1, reference.Y1, reference.X3, reference.Y3, reference.X0, reference.Y0, reference.X2, reference.Y2,
            reference.AreaRatio, 1.0, 1.0,
            new InsertPlateBows(new InsertEdgeBow(0.1, 0), new InsertEdgeBow(0.2, 0), new InsertEdgeBow(0.3, 0), new InsertEdgeBow(0.4, 0)));

        ChromaQuadTracker.FrameQuad aligned = ChromaQuadTracker.AlignCornerLabels(d, reference);

        aligned.X0.Should().Be(reference.X0);
        aligned.Y0.Should().Be(reference.Y0);
        aligned.X1.Should().Be(reference.X1);
        aligned.X2.Should().Be(reference.X2);
        aligned.X3.Should().Be(reference.X3);
        aligned.Bows.Top.Bow.Should().Be(0.4, "the edge that was called 'left' in the shifted labelling is the top edge in the reference's");
        aligned.Bows.Left.Bow.Should().Be(0.3);
        ChromaQuadTracker.AlignCornerLabels(reference, reference).Should().BeSameAs(reference, "shift zero returns the input");
    }
}
