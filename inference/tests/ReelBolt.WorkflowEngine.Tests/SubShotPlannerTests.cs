#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Content-driven sub-shot cut points (<see cref="SubShotPlanner"/>): pure, deterministic, from
/// signal curves plus bounds. The curves here are synthetic so every expected cut time is known.
/// </summary>
public class SubShotPlannerTests
{
    private const double Fps = 6.0;
    private static readonly SubShotBounds Derush = SubShotBounds.Clamp(1.2, 4.0, 8);

    // ---- curves ---------------------------------------------------------------------------------

    /// <summary>Pair samples at (f + 0.5) / fps over [0, length], motion and change from functions of time.</summary>
    private static SubShotSignal Curves(double length, Func<double, double> motion, Func<double, double>? change = null)
    {
        int n = (int)Math.Floor(length * Fps);
        double[] times = Enumerable.Range(0, n).Select(f => (f + 0.5) / Fps).ToArray();
        return new SubShotSignal(
            times,
            times.Select(motion).ToArray(),
            times.Select(change ?? (_ => 0.03)).ToArray(),
            1 / Fps);
    }

    /// <summary>Steady motion with deterministic ripple (±3%), and a dip to 0.02 within 0.2 s of each valley.</summary>
    private static Func<double, double> Valleys(params double[] at) => t =>
        at.Any(v => Math.Abs(t - v) <= 0.2) ? 0.02 : 0.3 * (1 + 0.03 * Math.Sin(7.3 * t));

    private static SubShotPlan Plan(double start, double end, SubShotSignal signal, SubShotBounds? bounds = null,
        IReadOnlyList<(double StartSec, double EndSec)>? silences = null) =>
        SubShotPlanner.Plan(start, end, signal, silences ?? [], bounds ?? Derush);

    // ---- motion pauses --------------------------------------------------------------------------

    [Fact]
    public void Valleys_in_motion_at_known_times_become_the_cuts()
    {
        SubShotPlan plan = Plan(0, 11, Curves(11, Valleys(3.75, 7.25)));

        plan.Method.Should().Be(SubShotSplitMethod.Content);
        plan.Cuts.Should().HaveCount(2);
        plan.Cuts[0].Should().BeApproximately(3.75, 0.2);
        plan.Cuts[1].Should().BeApproximately(7.25, 0.2);
        plan.Reasons.Should().Equal(SubShotCutReason.MotionPause, SubShotCutReason.MotionPause);
    }

    [Fact]
    public void A_valley_in_a_later_shot_is_found_in_that_shots_own_time()
    {
        SubShotPlan plan = Plan(20, 30, Curves(30, Valleys(24.0)));

        plan.Cuts.Should().Contain(c => Math.Abs(c - 24.0) <= 0.2);
        plan.Cuts.Should().OnlyContain(c => c > 20 && c < 30);
    }

    [Fact]
    public void A_gap_between_content_cuts_longer_than_the_maximum_is_filled_with_an_even_cut()
    {
        // One pause at 3 s in a 12 s shot: 0..3 is fine, 3..12 is 9 s and needs 3 parts of 3 s.
        SubShotPlan plan = Plan(0, 12, Curves(12, Valleys(3.0)));

        plan.Method.Should().Be(SubShotSplitMethod.Content);
        plan.Cuts[0].Should().BeApproximately(3.0, 0.2);
        plan.Reasons[0].Should().Be(SubShotCutReason.MotionPause);
        plan.Reasons.Skip(1).Should().OnlyContain(r => r == SubShotCutReason.Even);
        Parts(0, 12, plan).Should().OnlyContain(p => p <= 4.0 + 1e-9 && p >= 1.2 - 1e-9);
    }

    [Fact]
    public void Of_two_equal_pauses_closer_than_the_minimum_the_earlier_wins_every_time()
    {
        // No ripple: the four valley samples score exactly alike.
        SubShotSignal signal = Curves(6, t => Math.Abs(t - 2.5) <= 0.2 || Math.Abs(t - 3.3) <= 0.2 ? 0.02 : 0.3);

        SubShotPlan first = Plan(0, 6, signal);
        SubShotPlan again = Plan(0, 6, signal);

        first.Cuts.Should().ContainSingle().Which.Should().BeApproximately(2.5, 0.2);
        again.Should().BeEquivalentTo(first);
    }

    [Fact]
    public void A_flat_hold_is_cut_at_its_centre_not_its_first_sample()
    {
        // Motion stops for exactly 1 s, 2.5..3.5 and 6.5..7.5: plateaus of zeros, centred at 3.0 and 7.0.
        SubShotPlan plan = Plan(0, 10, Curves(10, t => (t > 2.5 && t < 3.5) || (t > 6.5 && t < 7.5) ? 0 : 0.3));

        plan.Reasons.Should().Equal(SubShotCutReason.MotionPause, SubShotCutReason.MotionPause);
        plan.Cuts[0].Should().BeApproximately(3.0, 0.1);
        plan.Cuts[1].Should().BeApproximately(7.0, 0.1);
    }

    // ---- frame edges ----------------------------------------------------------------------------

    [Theory]
    [InlineData(6.75, 30, 1, 203)]       // a half frame rounds away from zero
    [InlineData(3.2333, 30, 1, 97)]
    [InlineData(3.0, 30000, 1001, 90)]   // 89.91 frames
    [InlineData(7.0, 30000, 1001, 210)]
    [InlineData(1 / 3.0, 25, 1, 8)]
    [InlineData(8.174833, 30000, 1001, 245)] // a frame no single double can name exactly at this rate
    public void A_boundary_snaps_to_an_exact_source_frame_edge(double sec, int num, int den, long frame)
    {
        ShotSplitter.FrameEdge edge = ShotSplitter.SnapToFrameEdge(sec, num, den);

        edge.Frame.Should().Be(frame);
        AssertExactEdge(edge, num, den);
    }

    [Fact]
    public void Every_frame_from_zero_to_an_hour_snaps_exactly_at_ntsc_and_integer_rates()
    {
        foreach ((int num, int den) in new[] { (30000, 1001), (24000, 1001), (60000, 1001), (30, 1), (25, 1), (24, 1), (60, 1) })
            for (long k = 0; k < 3600L * num / den; k++)
            {
                ShotSplitter.FrameEdge edge = ShotSplitter.SnapToFrameEdge((k + 0.3) * den / num, num, den);
                edge.Frame.Should().Be(k);
                AssertExactEdge(edge, num, den);
            }
    }

    /// <summary>
    /// The compile's own quantisation (ceil of a part end, floor of a part start, at num/den) lands both
    /// sides on the edge's frame, and the two times are adjacent well within its 1e-6 s coalescing epsilon,
    /// equal whenever one double names the frame exactly.
    /// </summary>
    private static void AssertExactEdge(ShotSplitter.FrameEdge edge, int num, int den)
    {
        Math.Ceiling(edge.EndSec * num / den).Should().Be(edge.Frame, $"end of the part before frame {edge.Frame} at {num}/{den}");
        Math.Floor(edge.StartSec * num / den).Should().Be(edge.Frame, $"start of the part from frame {edge.Frame} at {num}/{den}");
        edge.StartSec.Should().BeGreaterThanOrEqualTo(edge.EndSec);
        (edge.StartSec - edge.EndSec).Should().BeLessThan(1e-9);
        if (edge.EndSec * num / den == edge.Frame || edge.StartSec * num / den == edge.Frame)
            edge.StartSec.Should().Be(edge.EndSec, "one double names the frame: both parts share it");
    }

    [Fact]
    public void Content_split_parts_share_exact_frame_edges_at_the_source_rate()
    {
        SubShotSignal signal = Curves(20, Valleys(6.75));

        ShotSplitter.ContentSplit split = ShotSplitter.SplitByContent([(0, 20)], signal, [], Derush, (30000, 1001));

        split.Spans.Should().HaveCountGreaterThan(1);
        for (int i = 1; i < split.Spans.Count; i++)
        {
            long end = (long)Math.Ceiling(split.Spans[i - 1].EndSec * 30000 / 1001);
            long start = (long)Math.Floor(split.Spans[i].StartSec * 30000 / 1001);
            start.Should().Be(end, $"boundary {i} is one frame edge for both parts");
            (split.Spans[i].StartSec - split.Spans[i - 1].EndSec).Should().BeInRange(0, 1e-9);
        }
    }

    [Fact]
    public void Even_split_parts_snap_to_frame_edges_too()
    {
        (List<(double StartSec, double EndSec)> spans, _) =
            ShotSplitter.Split([(0, 10.07)], 4, motion: null, silences: [], frameRate: (30, 1));

        // 10.07 / 3 = 3.3567 s parts: the cuts at 100.7 and 201.4 frames land on 101 and 201.
        spans.Take(2).Select(s => Math.Ceiling(s.EndSec * 30)).Should().Equal(101, 201);
        spans.Skip(1).Select(s => Math.Floor(s.StartSec * 30)).Should().Equal(101, 201);
        spans.Last().EndSec.Should().Be(10.07, "the shot's own end is not a sub-shot boundary");
    }

    // ---- fallback and no-split --------------------------------------------------------------------

    [Fact]
    public void A_flat_signal_falls_back_to_the_even_split_and_says_so()
    {
        SubShotPlan plan = Plan(0, 10, Curves(10, t => 0.3 * (1 + 0.03 * Math.Sin(7.3 * t))));

        plan.Method.Should().Be(SubShotSplitMethod.EvenFallback);
        plan.Cuts.Should().HaveCount(2);
        plan.Cuts[0].Should().BeApproximately(10 / 3.0, 1e-9);
        plan.Cuts[1].Should().BeApproximately(20 / 3.0, 1e-9);
        plan.Reasons.Should().OnlyContain(r => r == SubShotCutReason.Even);
    }

    [Fact]
    public void A_missing_signal_falls_back_to_the_even_split()
    {
        SubShotPlan plan = Plan(0, 10, SubShotSignal.Empty);

        plan.Method.Should().Be(SubShotSplitMethod.EvenFallback);
        plan.Cuts.Should().HaveCount(2);
    }

    [Fact]
    public void A_still_shot_with_no_motion_at_all_is_flat_not_a_row_of_pauses()
    {
        // Sensor noise on a locked-off still: tiny values, relative dips everywhere.
        SubShotPlan plan = Plan(0, 10, Curves(10, t => 0.001 * (1 + Math.Sin(13 * t))));

        plan.Method.Should().Be(SubShotSplitMethod.EvenFallback);
    }

    [Fact]
    public void A_shot_shorter_than_twice_the_minimum_is_never_split()
    {
        SubShotPlan plan = Plan(0, 2.3, Curves(2.3, Valleys(1.15)));

        plan.Method.Should().Be(SubShotSplitMethod.Unsplit);
        plan.Cuts.Should().BeEmpty();
    }

    [Fact]
    public void A_shot_within_the_maximum_splits_only_where_the_content_says_so()
    {
        Plan(0, 3.6, Curves(3.6, Valleys(1.8))).Cuts.Should().ContainSingle()
            .Which.Should().BeApproximately(1.8, 0.2);

        SubShotPlan flat = Plan(0, 3.6, Curves(3.6, _ => 0.3));
        flat.Method.Should().Be(SubShotSplitMethod.Unsplit);
        flat.Cuts.Should().BeEmpty();
    }

    // ---- other boundary kinds ------------------------------------------------------------------

    [Fact]
    public void A_pause_in_the_sound_is_an_audio_dip_cut_when_the_picture_gives_nothing()
    {
        SubShotPlan plan = Plan(0, 8, Curves(8, _ => 0.3), silences: [(3.7, 4.3)]);

        plan.Method.Should().Be(SubShotSplitMethod.Content);
        plan.Cuts.Should().ContainSingle().Which.Should().BeApproximately(4.0, 0.2);
        plan.Reasons.Should().Equal(SubShotCutReason.AudioDip);
    }

    [Fact]
    public void A_peak_of_visual_change_is_a_visual_change_cut()
    {
        // Steady motion, and the before/after composition difference peaks at 3.75 s.
        SubShotPlan plan = Plan(0, 7.5, Curves(7.5, _ => 0.3, t => 0.02 + 0.08 * Math.Max(0, 1 - Math.Abs(t - 3.75) / 0.5)));

        plan.Cuts.Should().ContainSingle().Which.Should().BeApproximately(3.75, 0.2);
        plan.Reasons.Should().Equal(SubShotCutReason.VisualChange);
    }

    // ---- from the grid: a pan with a hold, a framing change -------------------------------------

    [Fact]
    public void A_slow_pan_with_a_brief_hold_cuts_at_the_hold()
    {
        // A smooth pattern panning 1 px per frame, holding still for 0.5 s around 5 s.
        FrameGridResult grid = Grid(10, (f, x, y) =>
        {
            double t = f / Fps;
            double offset = t < 4.75 ? f : t < 5.25 ? 4.75 * Fps : f - 0.5 * Fps;
            return 128 + 90 * Math.Sin(2 * Math.PI * (x + offset) / 32.0) + 10 * Math.Sin(2 * Math.PI * y / 18.0);
        });
        SubShotSignal signal = SubShotSignal.FromGrid(grid, [(0, 10)]);

        SubShotPlan plan = Plan(0, 10, signal);

        plan.Method.Should().Be(SubShotSplitMethod.Content);
        plan.Reasons.Should().Contain(SubShotCutReason.MotionPause);
        double hold = plan.Cuts[plan.Reasons.ToList().IndexOf(SubShotCutReason.MotionPause)];
        hold.Should().BeApproximately(5.0, 0.3);
        plan.Reasons.Should().NotContain(SubShotCutReason.VisualChange, "the pan's steady change is not a change of framing");
    }

    [Fact]
    public void A_steady_pan_with_no_hold_is_flat()
    {
        FrameGridResult grid = Grid(10, (f, x, y) =>
            128 + 90 * Math.Sin(2 * Math.PI * (x + f) / 32.0) + 10 * Math.Sin(2 * Math.PI * y / 18.0));

        Plan(0, 10, SubShotSignal.FromGrid(grid, [(0, 10)])).Method.Should().Be(SubShotSplitMethod.EvenFallback);
    }

    [Fact]
    public void A_subject_entering_the_frame_cuts_at_the_framing_change()
    {
        // A slow drifting background; at 6 s a bright subject slides into the right half over a
        // third of a second and stays there.
        FrameGridResult grid = Grid(10, (f, x, y) =>
        {
            double t = f / Fps;
            double background = 70 + 30 * Math.Sin(2 * Math.PI * (x + 0.5 * f) / 32.0);
            double edge = t < 6.0 ? 32 : t < 6.34 ? 32 - 16 * (t - 6.0) / 0.34 : 16;
            return x >= edge ? 235 : background;
        });

        SubShotPlan plan = Plan(0, 10, SubShotSignal.FromGrid(grid, [(0, 10)]));

        plan.Method.Should().Be(SubShotSplitMethod.Content);
        plan.Reasons.Should().Contain(SubShotCutReason.VisualChange);
        double change = plan.Cuts[plan.Reasons.ToList().IndexOf(SubShotCutReason.VisualChange)];
        change.Should().BeApproximately(6.15, 0.25);
    }

    [Fact]
    public void Visual_change_windows_stop_at_the_shot_boundary()
    {
        // Two static shots: black then white, cut at 5 s. Inside each shot nothing changes.
        FrameGridResult grid = Grid(10, (f, _, _) => f / Fps < 5.0 ? 10 : 240);

        SubShotSignal signal = SubShotSignal.FromGrid(grid, [(0, 5), (5, 10)]);

        signal.Times.Select((t, i) => (t, c: signal.Change[i]))
            .Where(s => s.t < 4.9 || s.t > 5.1)
            .Should().OnlyContain(s => s.c < 1e-9, "a window never reaches across the cut into the other shot");
    }

    [Fact]
    public void Motion_from_the_grid_is_per_second_luma_change_between_consecutive_frames()
    {
        FrameGridResult grid = Grid(1, (f, _, _) => f * 3.0); // +3/255 luma per frame

        SubShotSignal signal = SubShotSignal.FromGrid(grid, [(0, 1)]);

        signal.Times[0].Should().BeApproximately(0.5 / Fps, 1e-9);
        signal.Motion.Should().OnlyContain(m => Math.Abs(m - 3 / 255.0 * Fps) < 1e-6);
        signal.SampleIntervalSec.Should().BeApproximately(1 / Fps, 1e-9);
    }

    [Fact]
    public void With_the_source_rate_each_sample_is_timed_at_the_source_frame_the_fps_filter_kept()
    {
        // ffmpeg's fps filter keeps, for output frame i, the last source frame before (i + 0.5) / fps:
        // at 6 fps from 30 fps that is source frame 5i + 2, i.e. i / 6 + 1 / 15 s (measured on ffmpeg 8).
        FrameGridResult grid = Grid(1, (f, _, _) => f * 3.0);

        SubShotSignal signal = SubShotSignal.FromGrid(grid, [(0, 1)], sourceFps: 30);

        signal.Times[0].Should().BeApproximately((2 / 30.0 + 7 / 30.0) / 2, 1e-9);
        signal.Times[2].Should().BeApproximately((12 / 30.0 + 17 / 30.0) / 2, 1e-9);
        SubShotSignal.SampledFrameSec(3, 6, 30000 / 1001.0).Should().BeApproximately(17 * 1001 / 30000.0, 1e-9);
    }

    // ---- bounds ---------------------------------------------------------------------------------

    [Fact]
    public void Bounds_are_clamped_to_something_that_can_always_be_met()
    {
        SubShotBounds b = SubShotBounds.Clamp(0.1, 0.2, 1);

        b.MinSec.Should().Be(SubShotBounds.LowestMinSec);
        b.MaxSec.Should().BeGreaterThanOrEqualTo(2 * b.MinSec);
        b.MaxParts.Should().Be(2);

        SubShotBounds nan = SubShotBounds.Clamp(double.NaN, double.PositiveInfinity, 1000);
        nan.MinSec.Should().Be(SubShotBounds.Default.MinSec);
        nan.MaxSec.Should().Be(SubShotBounds.HighestMaxSec);
        nan.MaxParts.Should().Be(SubShotBounds.HighestMaxParts);

        SubShotBounds.Clamp(1.2, 4, 8).Should().Be(new SubShotBounds(1.2, 4, 8));
    }

    [Fact]
    public void A_shot_too_long_for_the_part_cap_stretches_its_parts_instead_of_exceeding_the_cap()
    {
        SubShotPlan plan = Plan(0, 60, Curves(60, Valleys(10, 20, 30, 40, 50)));

        (plan.Cuts.Count + 1).Should().BeLessThanOrEqualTo(8);
        Parts(0, 60, plan).Should().OnlyContain(p => p <= SubShotPlanner.EffectiveMaxSec(60, Derush) + 1e-9);
    }

    [Fact]
    public void Bounds_and_order_hold_over_random_curves()
    {
        var random = new Random(20261017);
        for (int round = 0; round < 400; round++)
        {
            double length = Math.Round(0.3 + random.NextDouble() * 45, 3);
            double start = Math.Round(random.NextDouble() * 20, 3);
            SubShotBounds bounds = SubShotBounds.Clamp(
                0.2 + random.NextDouble() * 3, 0.3 + random.NextDouble() * 10, random.Next(0, 14));

            double walk = 0.2;
            int n = (int)Math.Floor((start + length) * Fps);
            double[] times = Enumerable.Range(0, n).Select(f => (f + 0.5) / Fps).ToArray();
            double[] motion = times.Select(_ => walk = Math.Clamp(walk + (random.NextDouble() - 0.5) * 0.2, 0, 1)).ToArray();
            double[] change = times.Select(_ => random.NextDouble() * 0.1).ToArray();
            var silences = Enumerable.Range(0, random.Next(0, 4))
                .Select(_ => start + random.NextDouble() * length)
                .Select(s => (s, s + 0.35 + random.NextDouble()))
                .ToList();
            var signal = new SubShotSignal(times, motion, change, 1 / Fps);

            SubShotPlan plan = SubShotPlanner.Plan(start, start + length, signal, silences, bounds);
            SubShotPlan again = SubShotPlanner.Plan(start, start + length, signal, silences, bounds);

            string at = $"round {round}: [{start}, {start + length}] {bounds}";
            again.Should().BeEquivalentTo(plan, at);
            plan.Reasons.Should().HaveCount(plan.Cuts.Count, at);
            (plan.Cuts.Count + 1).Should().BeLessThanOrEqualTo(bounds.MaxParts, at);
            plan.Cuts.Should().BeInAscendingOrder(at);
            plan.Cuts.Should().OnlyHaveUniqueItems(at);
            plan.Cuts.Should().OnlyContain(c => c > start && c < start + length, at);

            if (length < 2 * bounds.MinSec)
            {
                plan.Cuts.Should().BeEmpty(at);
                plan.Method.Should().Be(SubShotSplitMethod.Unsplit, at);
                continue;
            }

            double max = SubShotPlanner.EffectiveMaxSec(length, bounds);
            Parts(start, start + length, plan).Should().OnlyContain(
                p => p >= bounds.MinSec - 1e-9 && p <= max + 1e-9, at);
            if (plan.Cuts.Count == 0)
                plan.Method.Should().Be(SubShotSplitMethod.Unsplit, at);
        }
    }

    // ---- wiring words ---------------------------------------------------------------------------

    [Fact]
    public void Reasons_and_methods_have_one_stable_word_each()
    {
        SubShotCutReason.MotionPause.ToWord().Should().Be("motion_pause");
        SubShotCutReason.VisualChange.ToWord().Should().Be("visual_change");
        SubShotCutReason.AudioDip.ToWord().Should().Be("audio_dip");
        SubShotCutReason.Even.ToWord().Should().Be("even");
        SubShotSplitMethod.Content.ToWord().Should().Be("content");
        SubShotSplitMethod.EvenFallback.ToWord().Should().Be("even_fallback");
    }

    // ---- ShotSplitter over many shots -----------------------------------------------------------

    [Fact]
    public void Splitting_by_content_labels_parts_and_reports_each_split_shot()
    {
        SubShotSignal signal = Curves(20, Valleys(4.0));

        ShotSplitter.ContentSplit split = ShotSplitter.SplitByContent(
            [(0, 2), (2, 8), (8, 20)], signal, [], Derush);

        split.Spans.Should().HaveCount(split.Parts.Count);
        split.Parts[0].Should().BeNull("a 2 s shot is shorter than twice the minimum");
        split.Spans[0].Should().Be((0.0, 2.0));
        split.Spans[1].EndSec.Should().BeApproximately(4.0, 0.2);
        split.Parts.Skip(1).Take(2).Should().Equal("1/2", "2/2");
        split.Spans.Last().EndSec.Should().Be(20);
        split.Shots.Should().HaveCount(2);
        split.Shots[0].Method.Should().Be(SubShotSplitMethod.Content);
        split.Shots[0].Reasons.Should().Equal(SubShotCutReason.MotionPause);
        split.Shots[1].Method.Should().Be(SubShotSplitMethod.EvenFallback);
        split.Shots[1].Parts.Should().Be(3);
        for (int i = 1; i < split.Spans.Count; i++)
            split.Spans[i].StartSec.Should().Be(split.Spans[i - 1].EndSec, "parts stay consecutive");
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static IEnumerable<double> Parts(double start, double end, SubShotPlan plan) =>
        plan.Cuts.Prepend(start).Zip(plan.Cuts.Append(end), (a, b) => b - a);

    /// <summary>A 32x18 grey grid at 6 fps, <paramref name="seconds"/> long, luma from a function of (frame, x, y).</summary>
    private static FrameGridResult Grid(double seconds, Func<int, int, int, double> luma)
    {
        const int w = 32, h = 18;
        int frames = (int)Math.Round(seconds * Fps);
        byte[] data = new byte[frames * w * h * 3];
        for (int f = 0; f < frames; f++)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    byte v = (byte)Math.Clamp(Math.Round(luma(f, x, y)), 0, 255);
                    int i = ((f * h + y) * w + x) * 3;
                    data[i] = data[i + 1] = data[i + 2] = v;
                }

        return new FrameGridResult(data, w, h, Fps, frames);
    }
}
