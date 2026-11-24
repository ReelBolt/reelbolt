using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video.Dynamics;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class DynamicsPlannerTests
{
    private const double Fps = 30.0;
    private const double Beat140 = 60.0 / 140.0;

    // Two 10 s spans on two sources, 20 s program; 140 bpm grid, downbeat at 0, drop at 8 bars? (kept simple: 0.0 phase).
    private static readonly IReadOnlyList<DynamicsSpan> Spans =
    [
        new("s1", 0, 0, 10, true),
        new("s2", 1, 0, 10, true)
    ];

    private static DynamicsGrid Grid(double? drop = null, IReadOnlyList<double>? accents = null) =>
        new(Beat140, 0, 0, drop, accents);

    private static DynamicsEntry E(string effect, string anchor = "s1", string moment = "OnBeat", string? other = null,
        string? beats = null, string? division = null, string? winner = null, string? intensity = null) =>
        new(effect, anchor, other, moment, beats, division, winner, intensity, "r");

    private static DynamicsPlan Plan(DynamicPace pace, params DynamicsEntry[] entries) =>
        DynamicsPlanner.Plan(Spans, Grid(drop: 5.0), [], entries, pace, Fps, fill: false);

    // The real proof film's body (QA-DYN-8): seven spans cut on the 140 bpm grid, at 30000/1001 fps, with the words a real model chose.
    // The program's frames must equal the spans' frames whatever the words are: later cuts stay where the beat sync put them.
    private static (IReadOnlyList<DynamicsSpan> Spans, DynamicsGrid Grid) ProofBody()
    {
        double[] lens = [4.7407, 2.1433, 3.4281, 1.2857, 2.5714, 1.2857, 2.1429];
        var spans = new List<DynamicsSpan>();
        double src = 0;
        for (int i = 0; i < lens.Length; i++)
        {
            spans.Add(new($"s{i}", 0, src, src + lens[i], true));
            src += lens[i];
        }
        return (spans, new DynamicsGrid(Beat140, 0.0149, 0.0149, 6.884, null));
    }

    private static void SpanFramesMustBeKept(IReadOnlyList<DynamicsSpan> spans, DynamicsPlan p, double fps)
    {
        long want = spans.Sum(x => (long)Math.Round(x.EndSec * fps, MidpointRounding.AwayFromZero) - (long)Math.Round(x.StartSec * fps, MidpointRounding.AwayFromZero));
        p.Segments.Sum(s => s.OutputFrames).Should().Be(want, "the program keeps the beat-synced length, so later cuts stay on the grid");
    }

    [Theory]
    [InlineData("StrobeCut", "OnAccent", "HalfBeat")]
    [InlineData("StrobeCut", "OnBar", "Beat")]
    [InlineData("SpeedRamp", "OnAccent", null)]
    [InlineData("SpeedRamp", "OnDrop", null)]
    public void Strobe_and_ramp_keep_every_span_at_its_beat_synced_frame_count(string effect, string moment, string? division)
    {
        const double fps = 30000.0 / 1001.0;
        (IReadOnlyList<DynamicsSpan> spans, DynamicsGrid grid) = ProofBody();
        foreach (string anchor in new[] { "s1", "s2", "s3", "s4", "s5" })
        {
            string? other = effect == "StrobeCut" ? "s" + (int.Parse(anchor[1..]) + 1) : null;
            DynamicsPlan p = DynamicsPlanner.Plan(spans, grid, [], [E(effect, anchor, moment, other, division: division, intensity: "Normal")], DynamicPace.Fast, fps, fill: false);
            SpanFramesMustBeKept(spans, p, fps);
        }
    }

    [Fact]
    public void The_real_models_words_keep_the_program_on_the_beat_synced_length()
    {
        const double fps = 30000.0 / 1001.0;
        (IReadOnlyList<DynamicsSpan> spans, DynamicsGrid grid) = ProofBody();
        DynamicsEntry[] words =
        [
            E("SplitOnBeat", "s0", "OnBar", intensity: "Strong"), E("PunchIn", "s1", "OnBeat"), E("SplitOnBeat", "s2", "OnBeat", intensity: "Strong"),
            E("StrobeCut", "s3", "OnAccent", other: "s4", division: "HalfBeat", intensity: "Strong"), E("FlashFrame", "s4", "OnDrop", intensity: "Strong"),
            E("SpeedRamp", "s5", "OnAccent"), E("WhipOnBar", "s6", "OnBar")
        ];
        DynamicsPlan p = DynamicsPlanner.Plan(spans, grid, [], words, DynamicPace.Fast, fps, fill: true);
        SpanFramesMustBeKept(spans, p, fps);
    }

    [Fact]
    public void Min_gap_runs_from_the_start_of_a_move_so_the_llm_words_whip_and_punch_are_kept_at_fast()
    {
        // QA-DYN-17: the real model's list (the llm-words variant). The whip on the last shot and the punch on s1 were dropped as
        // min_gap because the gap was measured from the END of the split/strobe/ramp spans that precede them.
        const double fps = 30000.0 / 1001.0;
        (IReadOnlyList<DynamicsSpan> spans, DynamicsGrid grid) = ProofBody();
        DynamicsEntry[] words =
        [
            E("SplitOnBeat", "s0", "OnBar", intensity: "Strong"), E("PunchIn", "s1", "OnBeat"), E("SplitOnBeat", "s2", "OnBeat", intensity: "Strong"),
            E("StrobeCut", "s3", "OnAccent", other: "s4", division: "HalfBeat", intensity: "Strong"), E("FlashFrame", "s4", "OnDrop", intensity: "Strong"),
            E("SpeedRamp", "s5", "OnAccent"), E("WhipOnBar", "s6", "OnBar")
        ];
        DynamicsPlan p = DynamicsPlanner.Plan(spans, grid, [], words, DynamicPace.Fast, fps, fill: false);
        p.Report.Dropped.Where(d => d.Reason == "min_gap").Select(d => d.AnchorId + ":" + d.Effect).Should().NotContain(["s1:PunchIn", "s6:WhipOnBar"]);
        p.Report.Applied.Should().Contain(a => a.Effect == "PunchIn" && a.AnchorId == "s1");
        p.Report.Applied.Should().Contain(a => a.Effect == "WhipOnBar" && a.AnchorId == "s6");
        SpanFramesMustBeKept(spans, p, fps);
    }

    [Fact]
    public void Min_gap_still_keeps_every_pair_of_moves_a_gap_apart_start_to_start()
    {
        DynamicsPlan p = Plan(DynamicPace.Medium, E("PunchIn"), E("PunchIn"), E("PunchIn", moment: "OnBar"), E("SplitOnBeat", moment: "OnBar"));
        long gap = 2 * (long)Math.Round(Beat140 * Fps);
        var frames = p.Report.Applied.Select(a => a.ProgramFrame).Order().ToList();
        for (int i = 1; i < frames.Count; i++)
            (frames[i] - frames[i - 1]).Should().BeGreaterThanOrEqualTo(gap, "applied moves start at least the pace's gap apart");
        p.Report.Dropped.Should().Contain(d => d.Reason == "min_gap");
    }

    [Fact]
    public void SplitOnBeat_never_leaves_a_sliver_at_the_start_or_end_of_a_span()
    {
        const double fps = 30000.0 / 1001.0;
        (IReadOnlyList<DynamicsSpan> spans, DynamicsGrid grid) = ProofBody();
        long beat = (long)Math.Round(Beat140 * fps);
        DynamicsPlan p = DynamicsPlanner.Plan(spans, grid, [], [E("SplitOnBeat", "s0", "OnBar", intensity: "Strong")], DynamicPace.Fast, fps, fill: false);
        p.Segments.Where(s => s.SpanId == "s0").Should().OnlyContain(s => s.OutputFrames >= beat, "no piece is shorter than a beat");
    }


    [Fact]
    public void Dynamics_pieces_snap_on_their_own_frame_not_a_frame_either_side()
    {
        // frame / fps seconds multiplied back by fps lands a hair off a whole number; floor/ceil made a 1-frame piece 3 frames long.
        for (long f = 0; f < 3000; f++)
        {
            double sec = f / (30000.0 / 1001.0);
            ReelBolt.WorkflowEngine.Execution.StepExecutors.VideoCompileStepExecutor.SnapToFrame(sec, 30000, 1001).Should().Be(f);
        }
    }

    [Fact]
    public void A_ramp_or_strobe_tail_of_one_or_two_frames_is_folded_not_left_as_a_flicker()
    {
        const double fps = 30000.0 / 1001.0;
        (double st, int fr)[] sp = [(0.0, 142), (4.938266666666666, 64), (8.008, 103), (11.745066666666666, 39), (13.246566666666666, 77), (15.982633333333334, 39), (17.684333333333335, 64)];
        var spans = sp.Select((x, i) => new DynamicsSpan($"p{i}", 0, x.st, x.st + x.fr / fps, true)).ToList();
        DynamicsEntry[] words = [E("StrobeCut", "p3", "OnAccent", other: "p4", division: "HalfBeat", intensity: "Strong"), E("SpeedRamp", "p5", "OnAccent")];
        DynamicsPlan p = DynamicsPlanner.Plan(spans, new DynamicsGrid(Beat140, 0.0149, 0.0149, 6.884, null), [], words, DynamicPace.Fast, fps, fill: false);
        p.Segments.Sum(s => s.OutputFrames).Should().Be(528);
        p.Segments.Should().OnlyContain(s => s.OutputFrames >= 3, "no 1- or 2-frame piece");
    }

    [Theory]
    [InlineData(0.0149)]
    [InlineData(0.1)]
    [InlineData(0.2)]
    public void SplitOnBeat_leaves_no_piece_shorter_than_a_beat_on_the_real_spans(double phase)
    {
        const double fps = 30000.0 / 1001.0;
        (double st, int fr)[] sp = [(0.0, 142), (4.938266666666666, 64)];
        var spans = sp.Select((x, i) => new DynamicsSpan($"p{i}", 0, x.st, x.st + x.fr / fps, true)).ToList();
        DynamicsPlan p = DynamicsPlanner.Plan(spans, new DynamicsGrid(Beat140, phase, phase, null, null), [], [E("SplitOnBeat", "p0", "OnBar", intensity: "Strong")], DynamicPace.Fast, fps, fill: false);
        p.Segments.Where(s => s.SpanId == "p0").Should().OnlyContain(s => s.OutputFrames >= 13, "a split line within a beat of the span edge is ignored");
        p.Segments.Sum(s => s.OutputFrames).Should().Be(206);
    }

    [Fact]
    public void PunchIn_makes_a_hold_then_ease_out_reframe_on_a_beat_frame()
    {
        DynamicsPlan p = Plan(DynamicPace.Calm, E("PunchIn", intensity: "Strong"));
        p.Reframes.Should().ContainSingle();
        VideoReframe r = p.Reframes[0];
        r.SourceIndex.Should().Be(0);
        r.Keyframes[0].Zoom.Should().Be(1.08, "Calm caps intensity at Subtle");
        r.Keyframes[0].Easing.Should().Be(ReframeEasing.Hold);
        r.Keyframes.Should().Contain(k => k.Easing == ReframeEasing.EaseOut);
        r.Keyframes[^1].Zoom.Should().Be(1.0);
        p.Report.Applied.Should().ContainSingle(a => a.Effect == "PunchIn");
    }

    [Fact]
    public void PunchOut_zooms_from_punch_to_wide_over_a_beat()
    {
        DynamicsPlan p = Plan(DynamicPace.Medium, E("PunchOut", intensity: "Normal"));
        VideoReframe r = p.Reframes.Single();
        r.Keyframes.Should().HaveCount(2);
        r.Keyframes[0].Zoom.Should().Be(1.15);
        r.Keyframes[1].Zoom.Should().Be(1.0);
        r.Keyframes[1].AtSec.Should().BeApproximately(13 / Fps, 1e-9); // one beat = 12.857 -> 13 frames
    }

    [Fact]
    public void FlashFrame_is_one_frame_white_or_black_on_strong()
    {
        Plan(DynamicPace.Medium, E("FlashFrame")).Flashes.Should().ContainSingle(f => f.Color == "white");
        Plan(DynamicPace.Fast, E("FlashFrame", intensity: "Strong")).Flashes.Should().ContainSingle(f => f.Color == "black");
    }

    [Fact]
    public void SplitOnBeat_cuts_the_take_on_grid_lines_and_alternates_wide_and_punch()
    {
        DynamicsPlan p = Plan(DynamicPace.Medium, E("SplitOnBeat", moment: "OnBar"));
        p.Segments.Count(s => s.SpanId == "s1").Should().BeGreaterThan(2);
        p.Reframes.Should().NotBeEmpty();
        p.Segments.Sum(s => s.OutputFrames).Should().Be(600, "program length is unchanged");
    }

    [Fact]
    public void BeatAlternate_cells_at_140_bpm_and_winner_holds_to_the_end()
    {
        DynamicsPlan p = Plan(DynamicPace.Fast,
            E("BeatAlternate", moment: "OnBeat", other: "s2", beats: "Four", division: "HalfBeat", winner: "B"));
        // half beat = 0.2143 s = 6.43 frames: 8 cells from the first beat line, rounded from exact grid times.
        List<DynamicsSegment> head = p.Segments.Where(s => s.SpanId is "s1" or "s2").Take(9).ToList();
        head.Select(s => s.SpanId).Take(9).Should().Equal("s1", "s2", "s1", "s2", "s1", "s2", "s1", "s2", "s2");
        p.Segments.Sum(s => s.OutputFrames).Should().Be(600);
        // Cell 0 starts on the first beat line (0.0, frame 0) and is round(0.2143*30)=6 frames.
        p.Segments[0].SpanId.Should().Be("s1");
        p.Segments[0].OutputFrames.Should().Be(6);
        // B plays consecutive footage: its cells chain from B's start.
        List<DynamicsSegment> b = p.Segments.Where(s => s.SpanId == "s2").Take(3).ToList();
        b[1].StartFrame.Should().Be(b[0].EndFrame);
        // winner B holds through the end of the anchor window: last segment of the s1 window is B.
        p.Segments.Last(s => s.SpanId is "s1" or "s2" && s.SourceIndex == 1).OutputFrames.Should().BeGreaterThan(100);
        p.Report.MaxBeatOffsetMs.Should().BeLessThan(Beat140 * 1000 / 2);
    }

    [Fact]
    public void BeatAlternate_full_beat_cell_is_0_4286_s()
    {
        DynamicsPlan p = Plan(DynamicPace.Fast,
            E("BeatAlternate", other: "s2", beats: "Two", division: "Beat", winner: "A"));
        p.Segments[0].SpanId.Should().Be("s1");
        p.Segments[0].OutputFrames.Should().Be(13); // 0.4286 s * 30 = 12.86 -> 13
        p.Segments[1].SpanId.Should().Be("s2");
        p.Segments[1].OutputFrames.Should().Be(13); // boundary 0.8571 s -> frame 26
    }

    [Fact]
    public void StrobeCut_is_one_bar_of_alternation_on_the_drop_then_the_anchor_resumes()
    {
        DynamicsPlan p = Plan(DynamicPace.Fast, E("StrobeCut", moment: "OnDrop", other: "s2", division: "Beat"));
        p.Report.Applied.Should().ContainSingle(a => a.Effect == "StrobeCut");
        p.Segments.Count(s => s.SpanId == "s2" && s.SourceIndex == 1).Should().BeGreaterThan(1);
        p.Segments.Sum(s => s.OutputFrames).Should().Be(600);
    }

    [Fact]
    public void SpeedRamp_retimes_one_beat_before_the_drop_and_keeps_program_length()
    {
        DynamicsPlan p = Plan(DynamicPace.Fast, E("SpeedRamp", moment: "OnDrop", intensity: "Normal"));
        p.Retimes.Should().ContainSingle(r => r.Speed == 0.5);
        DynamicsSegment ramp = p.Segments[p.Retimes[0].SegmentIndex];
        ramp.OutputFrames.Should().Be(13);
        (ramp.EndFrame - ramp.StartFrame).Should().Be(7);
        p.Segments.Sum(s => s.OutputFrames).Should().Be(600);
    }

    [Fact]
    public void SpeedRamp_into_a_drop_that_sits_exactly_on_the_cut_belongs_to_the_span_before_it()
    {
        // Real material: the beat sync snaps a cut onto the drop, so the drop is the end of one span and the start of the next.
        DynamicsPlan p = DynamicsPlanner.Plan(Spans, Grid(drop: 10.0), [], [E("SpeedRamp", moment: "OnDrop", intensity: "Normal")], DynamicPace.Fast, Fps, fill: false);
        p.Report.Dropped.Should().BeEmpty();
        p.Retimes.Should().ContainSingle();
        DynamicsSegment ramp = p.Segments[p.Retimes[0].SegmentIndex];
        ramp.SpanId.Should().Be("s1");
        p.Segments.Sum(s => s.OutputFrames).Should().Be(600);
    }

    [Fact]
    public void A_BeatAlternate_cell_cut_short_by_the_window_end_is_not_counted_as_a_beat_offset()
    {
        // Real material: the anchor span ends mid-cell. The last cell ends on the span's own cut, which is not a grid cut of the plan.
        IReadOnlyList<DynamicsSpan> spans = [new("s1", 0, 0, 1.5, true), new("s2", 1, 0, 10, true)];
        DynamicsPlan p = DynamicsPlanner.Plan(spans, Grid(), [], [E("BeatAlternate", moment: "OnBeat", other: "s2", beats: "Four", division: "Beat", winner: "A")], DynamicPace.Fast, Fps, fill: false);
        p.Report.Applied.Should().ContainSingle();
        p.Report.MaxBeatOffsetMs.Should().BeLessThanOrEqualTo(1000.0 / Fps);
    }

    [Fact]
    public void An_alternation_whose_window_holds_fewer_than_two_whole_cells_is_dropped_not_flickered()
    {
        // Real material: a strobe on a 0.8 s span (one whole beat cell at 140 bpm plus a clipped scrap) used to cut a one-frame segment.
        IReadOnlyList<DynamicsSpan> spans = [new("s1", 0, 0, 0.8, true), new("s2", 1, 0, 10, true)];
        DynamicsPlan p = DynamicsPlanner.Plan(spans, Grid(), [], [E("StrobeCut", moment: "OnBeat", other: "s2")], DynamicPace.Fast, Fps, fill: false);
        p.Report.Dropped.Should().ContainSingle(d => d.Reason == "window_too_short");
        p.Segments.Should().OnlyContain(s => s.OutputFrames >= 2);
    }

    [Fact]
    public void A_moment_an_event_move_owns_is_left_to_it_and_never_gets_a_second_move()
    {
        // The config's own EventMoves/FlashCues/SpeedRamps stay independent: Dynamics yields within the min gap of their moment.
        long gap = (long)Math.Round(Beat140 * Fps);
        DynamicsPlan free = DynamicsPlanner.Plan(Spans, Grid(), [], [E("FlashFrame", moment: "OnBar")], DynamicPace.Fast, Fps, fill: false);
        long flashFrame = free.Flashes.Single().ProgramFrame;

        DynamicsPlan owned = DynamicsPlanner.Plan(Spans, Grid(), [], [E("FlashFrame", moment: "OnBar")], DynamicPace.Fast, Fps, fill: false,
            ownedFrames: [flashFrame + gap / 2]);
        owned.Flashes.Should().BeEmpty();
        owned.Report.Dropped.Should().ContainSingle(d => d.Reason == "event_move_owns_moment");

        // The fill is silent about it but never lands a punch within the gap of the owned frame either.
        DynamicsPlan filled = DynamicsPlanner.Plan(Spans, Grid(), [], [], DynamicPace.Fast, Fps, ownedFrames: [flashFrame]);
        filled.Report.Applied.Should().NotBeEmpty().And.NotContain(a => Math.Abs(a.ProgramFrame - flashFrame) < gap);
        filled.Report.Dropped.Should().BeEmpty();

        // Far from the owned frame nothing changes.
        DynamicsPlanner.Plan(Spans, Grid(), [], [E("FlashFrame", moment: "OnBar")], DynamicPace.Fast, Fps, fill: false, ownedFrames: [flashFrame + 10 * gap])
            .Flashes.Should().ContainSingle();
    }

    [Theory]
    [InlineData("PunchIn, PunchOut")]
    [InlineData("PunchIn,PunchOut")]
    [InlineData("Punch")]
    [InlineData("3")]
    [InlineData("PunchIn PunchOut")]
    public void A_word_that_is_a_list_or_a_fragment_is_dropped_as_unknown_not_parsed_into_another_effect(string word)
    {
        // Enum.TryParse ORs a comma list: "PunchIn, PunchOut" used to become BeatAlternate.
        DynamicsPlan p = Plan(DynamicPace.Fast, E(word));
        p.Report.Applied.Should().BeEmpty();
        p.Report.Dropped.Should().ContainSingle(d => d.Reason == "unknown_effect");
        DynamicsPlan m = Plan(DynamicPace.Fast, E("PunchIn", moment: "OnBeat, OnBar"));
        m.Report.Dropped.Should().ContainSingle(d => d.Reason == "unknown_moment");
        Plan(DynamicPace.Fast, E("punchin", moment: " onbeat ", intensity: " strong ")).Report.Applied.Should().ContainSingle();
    }

    [Fact]
    public void SpeedRamp_is_dropped_when_source_audio_is_not_muted()
    {
        IReadOnlyList<DynamicsSpan> spans = [new("s1", 0, 0, 10, false), new("s2", 1, 0, 10, true)];
        DynamicsPlan p = DynamicsPlanner.Plan(spans, Grid(drop: 5.0), [], [E("SpeedRamp", moment: "OnDrop")], DynamicPace.Fast, Fps, fill: false);
        p.Retimes.Should().BeEmpty();
        p.Report.Dropped.Should().ContainSingle(d => d.Reason == "source_audio_not_muted");
    }

    [Fact]
    public void WhipOnBar_needs_a_seam_on_a_bar_line_and_uses_an_eighth_of_a_beat()
    {
        // s1 = 4 bars exactly at 140 bpm? 4 bars = 6.857 s; make the first span end on a bar line.
        IReadOnlyList<DynamicsSpan> spans = [new("s1", 0, 0, 6.857, true), new("s2", 1, 0, 10, true)];
        DynamicsPlan p = DynamicsPlanner.Plan(spans, Grid(), [], [E("WhipOnBar", anchor: "s2", moment: "OnBar")], DynamicPace.Calm, Fps, fill: false);
        DynamicsSeam seam = p.Seams.Should().ContainSingle().Subject;
        seam.Word.Should().Be("WhipLeft");
        seam.DurationFrames.Should().Be(2); // 0.4286/8 = 0.0536 s = 1.6 frames -> 2
        Plan(DynamicPace.Calm, E("WhipOnBar", anchor: "s2", moment: "OnBar")).Seams.Should().BeEmpty("s2 starts at 300, not on a bar line");
    }

    [Theory]
    [InlineData(DynamicPace.Calm, 2)]
    [InlineData(DynamicPace.Medium, 6)]
    [InlineData(DynamicPace.Fast, 12)]
    public void Pace_bounds_entries_per_ten_seconds(DynamicPace pace, int expected)
    {
        DynamicsEntry[] many = Enumerable.Range(0, 30).Select(i => E("PunchIn", i % 2 == 0 ? "s1" : "s2")).ToArray();
        DynamicsPlan p = Plan(pace, many);
        p.Report.Applied.Count.Should().BeLessThanOrEqualTo(expected);
        p.Report.Applied.Count.Should().BeGreaterThan(0);
        p.Report.Dropped.Should().NotBeEmpty();
    }

    [Fact]
    public void Effects_outside_the_pace_are_dropped_with_a_reason()
    {
        DynamicsPlan calm = Plan(DynamicPace.Calm, E("SplitOnBeat"), E("BeatAlternate", other: "s2", beats: "Four", division: "Beat", winner: "A"));
        calm.Report.Dropped.Should().OnlyContain(d => d.Reason == "effect_not_allowed_at_pace").And.HaveCount(2);
        Plan(DynamicPace.Medium, E("SpeedRamp", moment: "OnDrop")).Report.Dropped
            .Should().ContainSingle(d => d.Reason == "effect_not_allowed_at_pace");
    }

    [Fact]
    public void Intensity_is_clamped_to_the_pace_maximum()
    {
        Plan(DynamicPace.Medium, E("PunchIn", intensity: "Strong")).Reframes[0].Keyframes[0].Zoom.Should().Be(1.15);
        Plan(DynamicPace.Fast, E("PunchIn", intensity: "Strong")).Reframes[0].Keyframes[0].Zoom.Should().Be(1.30);
    }

    [Fact]
    public void Min_gap_between_effects_drops_the_lower_priority_one_at_medium()
    {
        // Medium: 2 beats start to start; two PunchIns on consecutive beats of s1 (the second is within the gap of the first).
        DynamicsPlan p = Plan(DynamicPace.Medium, E("PunchIn"), E("PunchIn"));
        p.Report.Applied.Should().ContainSingle();
        p.Report.Dropped.Should().ContainSingle(d => d.Reason == "min_gap");
        VideoReframe.Validate(p.Reframes).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Min_gap_at_fast_is_one_beat_and_a_punch_a_beat_after_another_cuts_the_first_short_instead_of_overlapping()
    {
        // QA-DYN-23: the Fast case of the min-gap test (it was moved to Medium). Fast keeps both punches a beat apart (the gap is one beat,
        // start to start); the first lasts two beats, so it is bounded by the start of the second, never overlapping it. Two moves on
        // the same beat are still a min_gap drop.
        DynamicsPlan p = Plan(DynamicPace.Fast, E("PunchIn"), E("PunchIn"));
        p.Report.Applied.Should().HaveCount(2);
        VideoReframe.Validate(p.Reframes).IsValid.Should().BeTrue();
        var ordered = p.Reframes.OrderBy(r => r.StartSec).ToList();
        ordered[0].ToSec!.Value.Should().BeLessThanOrEqualTo(ordered[1].StartSec + 1e-9);
        ordered[0].Keyframes.Max(k => k.AtSec).Should().BeLessThanOrEqualTo(ordered[0].ToSec!.Value - ordered[0].StartSec + 1e-9);

        DynamicsPlan same = Plan(DynamicPace.Fast, E("PunchIn"), E("FlashFrame"));
        same.Report.Applied.Should().ContainSingle();
        same.Report.Dropped.Should().ContainSingle(d => d.Reason == "min_gap");
    }

    // ---- QA-DYN-23: the planner never emits overlapping or otherwise invalid reframes, at any pace. ----

    // The real model's list of the llm-words proof film (verbatim, as in the neighbouring tests).
    private static DynamicsEntry[] LlmWords() =>
    [
        E("SplitOnBeat", "s0", "OnBar", intensity: "Strong"), E("PunchIn", "s1", "OnBeat"), E("SplitOnBeat", "s2", "OnBeat", intensity: "Strong"),
        E("StrobeCut", "s3", "OnAccent", other: "s4", division: "HalfBeat", intensity: "Strong"), E("FlashFrame", "s4", "OnDrop", intensity: "Strong"),
        E("SpeedRamp", "s5", "OnAccent"), E("WhipOnBar", "s6", "OnBar")
    ];

    // The shape of the llm2-words list (the DynamicsPlanner agent's answer on the proof film: an alternation of the static source
    // against the generated clip, punches and splits, three moves anchored on generated clip ids). The verbatim list was lost with the
    // proof directory in a reboot; this list keeps its shape (every effect family, crowded starts one beat apart, generated anchors),
    // it is not the model's text. The phase-2 answer on the final prompt is checked by the proof harness itself.
    private static DynamicsEntry[] Llm2Words() =>
    [
        E("BeatAlternate", "s0", "OnBeat", other: "v2t1", beats: "Four", division: "Beat", winner: "B"),
        E("PunchIn", "s1", "OnBeat", intensity: "Strong"), E("PunchIn", "s1", "OnBar", intensity: "Normal"), E("PunchIn", "s2", "OnBeat"),
        E("SplitOnBeat", "s2", "OnBar", intensity: "Strong"), E("PunchIn", "v1t1", "OnBeat"), E("PunchOut", "v2t1", "OnBar"),
        E("FlashFrame", "s3", "OnBar"), E("PunchIn", "s4", "OnDrop", intensity: "Strong"), E("PunchOut", "s4", "OnBeat"),
        E("PunchIn", "s5", "OnBeat"), E("PunchIn", "s5", "OnAccent"), E("WhipOnBar", "s6", "OnBar"), E("PunchIn", "v3t1", "OnBeat")
    ];

    private static void PlanMustHaveValidReframes(DynamicsPlan p, string because)
    {
        VideoReframeValidation v = VideoReframe.Validate(p.Reframes);
        v.IsValid.Should().BeTrue($"{because}: {v.Message}");
    }

    [Theory]
    [InlineData(DynamicPace.Calm)]
    [InlineData(DynamicPace.Medium)]
    [InlineData(DynamicPace.Fast)]
    public void The_llm_words_list_gives_a_valid_reframe_set_at_every_pace_with_the_fill_on(DynamicPace pace)
    {
        const double fps = 30000.0 / 1001.0;
        (IReadOnlyList<DynamicsSpan> spans, DynamicsGrid grid) = ProofBody();
        DynamicsPlan p = DynamicsPlanner.Plan(spans, grid, [], LlmWords(), pace, fps, fill: true);
        PlanMustHaveValidReframes(p, "llm-words " + pace);
        SpanFramesMustBeKept(spans, p, fps);
        if (pace == DynamicPace.Fast) p.Reframes.Count.Should().BeGreaterThan(8, "the fill still lands its punches at Fast");
    }

    [Theory]
    [InlineData(DynamicPace.Calm)]
    [InlineData(DynamicPace.Medium)]
    [InlineData(DynamicPace.Fast)]
    public void The_llm2_words_list_gives_a_valid_reframe_set_at_every_pace_with_the_fill_on(DynamicPace pace)
    {
        const double fps = 30000.0 / 1001.0;
        (IReadOnlyList<DynamicsSpan> spans, DynamicsGrid grid) = ProofBody();
        DynamicsSpan[] pool = [new("v1t1", 5, 0, 9, true), new("v2t1", 6, 0, 9, true), new("v3t1", 7, 0, 9, true)];
        DynamicsPlan p = DynamicsPlanner.Plan(spans, grid, [], Llm2Words(), pace, fps, pool, fill: true);
        PlanMustHaveValidReframes(p, "llm2-words " + pace);
        SpanFramesMustBeKept(spans, p, fps);
    }

    [Fact]
    public void A_single_agent_punch_at_fast_with_the_fill_on_does_not_overlap_the_fill_punch_after_it()
    {
        // QA-DYN-23's second repro: an agent PunchIn lasts 2 beats, the fill punch lands a beat after it.
        DynamicsPlan p = DynamicsPlanner.Plan(Spans, Grid(drop: 5.0), [], [E("PunchIn")], DynamicPace.Fast, Fps, fill: true);
        PlanMustHaveValidReframes(p, "one agent punch at Fast");
        p.Reframes.Count.Should().BeGreaterThan(3);
        // The agent's punch is cut short where the next reframe on its span starts, never overlapping it.
        var ordered = p.Reframes.Where(r => r.SourceIndex == 0).OrderBy(r => r.StartSec).ToList();
        for (int i = 1; i < ordered.Count; i++)
            ordered[i].StartSec.Should().BeGreaterThanOrEqualTo(ordered[i - 1].ToSec!.Value - 1e-9);
    }

    [Fact]
    public void Random_entry_lists_at_every_pace_always_give_a_valid_reframe_set()
    {
        string[] effects = ["SplitOnBeat", "PunchIn", "PunchOut", "BeatAlternate", "StrobeCut", "FlashFrame", "SpeedRamp", "WhipOnBar"];
        string[] moments = ["OnBeat", "OnBar", "OnDrop", "OnAccent"];
        string[] intensities = ["Subtle", "Normal", "Strong", null!];
        string[] divisions = ["Beat", "HalfBeat", "Bar"];
        string[] beats = ["Two", "Four", "Eight", "Sixteen"];
        const double fps = 30000.0 / 1001.0;
        (IReadOnlyList<DynamicsSpan> spans, DynamicsGrid grid) = ProofBody();
        var rnd = new Random(20261119);
        int plans = 0;
        for (int iteration = 0; iteration < 400; iteration++)
        {
            int n = rnd.Next(0, 16);
            var entries = new List<DynamicsEntry>();
            for (int k = 0; k < n; k++)
            {
                string effect = effects[rnd.Next(effects.Length)];
                string anchor = "s" + rnd.Next(0, 7);
                string? other = effect is "BeatAlternate" or "StrobeCut" ? "s" + rnd.Next(0, 7) : null;
                entries.Add(E(effect, anchor, moments[rnd.Next(moments.Length)], other, beats[rnd.Next(beats.Length)], divisions[rnd.Next(divisions.Length)],
                    rnd.Next(2) == 0 ? "A" : "B", intensities[rnd.Next(intensities.Length)]));
            }
            foreach (DynamicPace pace in new[] { DynamicPace.Calm, DynamicPace.Medium, DynamicPace.Fast })
            {
                foreach (bool fill in new[] { false, true })
                {
                    DynamicsPlan p = DynamicsPlanner.Plan(spans, grid, [], entries, pace, fps, fill: fill);
                    PlanMustHaveValidReframes(p, $"iteration {iteration} {pace} fill={fill}: " + string.Join(";", entries.Select(e => $"{e.Effect}/{e.AnchorId}/{e.Moment}")));
                    SpanFramesMustBeKept(spans, p, fps);
                    plans++;
                }
            }
        }
        plans.Should().Be(2400);
    }

    [Fact]
    public void Unknown_words_and_ids_are_dropped_with_a_reason()
    {
        DynamicsPlan p = Plan(DynamicPace.Fast,
            E("Explode"), E("PunchIn", anchor: "zz"), E("PunchIn", moment: "OnWhenever"),
            E("PunchIn", intensity: "Huge"), E("PunchIn", intensity: "3"),
            E("BeatAlternate", other: "nope", beats: "Four", division: "Beat", winner: "A"),
            E("BeatAlternate", other: "s2", beats: "Seven", division: "Beat", winner: "A"));
        p.Reframes.Should().BeEmpty();
        p.Report.Dropped.Select(d => d.Reason).Should().Equal(
            "unknown_effect", "unknown_anchor_id", "unknown_moment", "unknown_intensity", "unknown_intensity",
            "unknown_other_id", "unknown_beats");
    }

    [Fact]
    public void Words_parse_case_insensitively()
    {
        Plan(DynamicPace.Medium, E("punchin", moment: "onbeat", intensity: "NORMAL")).Reframes.Should().ContainSingle();
    }

    private static DynamicsPlan Filled(DynamicPace pace, params DynamicsEntry[] entries) =>
        DynamicsPlanner.Plan(Spans, Grid(drop: 5.0), [], entries, pace, Fps);

    [Fact]
    public void Auto_fill_follows_the_pace_when_the_agent_lists_nothing()
    {
        Filled(DynamicPace.Calm).Reframes.Should().BeEmpty();
        DynamicsPlan medium = Filled(DynamicPace.Medium);
        DynamicsPlan fast = Filled(DynamicPace.Fast);
        medium.Report.Applied.Should().NotBeEmpty().And.OnlyContain(a => a.Auto && a.Effect == "PunchIn");
        fast.Report.Applied.Count.Should().BeGreaterThan(medium.Report.Applied.Count);
    }

    [Fact]
    public void Auto_fill_adds_punches_around_the_agents_entries_inside_the_gap_and_the_budget()
    {
        DynamicsEntry[] agent = [E("FlashFrame", moment: "OnBar"), E("SpeedRamp", moment: "OnDrop", intensity: "Normal")];
        DynamicsPlan p = Filled(DynamicPace.Fast, agent);
        DynamicsPlan agentOnly = DynamicsPlanner.Plan(Spans, Grid(drop: 5.0), [], agent, DynamicPace.Fast, Fps, fill: false);
        long gap = (long)Math.Round(Beat140 * Fps);
        var filled = p.Report.Applied.Where(a => a.Auto).ToList();
        filled.Should().NotBeEmpty();
        p.Report.Applied.Count(a => !a.Auto).Should().Be(agentOnly.Report.Applied.Count, "the agent's words are never displaced by the fill");
        // 20 s of program at Fast: 6 per 10 s.
        p.Report.Applied.Count.Should().BeLessThanOrEqualTo(12);
        foreach (long other in agentOnly.Flashes.Select(f => f.ProgramFrame))
            filled.Should().NotContain(a => Math.Abs(a.ProgramFrame - other) < gap, "no fill punch within the min gap of a flash");
        // Never one move on every beat: consecutive fill punches are at least 2 beats apart.
        filled.Select(a => a.ProgramFrame).Order().Zip(filled.Select(a => a.ProgramFrame).Order().Skip(1), (x, y) => y - x)
            .Should().OnlyContain(d => d >= 2 * gap - 2);
    }

    [Fact]
    public void Auto_fill_varies_the_framing_and_alternates_strength()
    {
        DynamicsPlan p = Filled(DynamicPace.Fast);
        p.Reframes.Select(r => (r.Keyframes[0].CenterX, r.Keyframes[0].CenterY)).Distinct().Count().Should().BeGreaterThan(1);
        p.Reframes.Select(r => r.Keyframes[0].Zoom).Distinct().Count().Should().Be(2);
    }

    [Fact]
    public void The_film_length_raises_the_pace_budget_when_it_is_longer_than_the_body()
    {
        int body = Filled(DynamicPace.Fast).Report.Applied.Count;
        DynamicsPlan film = DynamicsPlanner.Plan(Spans, Grid(drop: 5.0), [], [], DynamicPace.Fast, Fps, budgetSec: 40);
        film.Report.Applied.Count.Should().BeGreaterThan(body);
        film.Report.Applied.Count.Should().BeLessThanOrEqualTo(24, "6 per 10 s of 40 s");
    }

    [Fact]
    public void Plan_is_deterministic_and_frame_aligned()
    {
        DynamicsEntry[] es = [E("PunchIn", moment: "OnBar"), E("FlashFrame", moment: "OnBeat"), E("SplitOnBeat", anchor: "s2", moment: "OnBar")];
        DynamicsPlan a = Plan(DynamicPace.Fast, es);
        DynamicsPlan b = Plan(DynamicPace.Fast, es);
        System.Text.Json.JsonSerializer.Serialize(a).Should().Be(System.Text.Json.JsonSerializer.Serialize(b));
        foreach (VideoReframe r in a.Reframes)
        {
            (r.FromSec!.Value * Fps).Should().BeApproximately(Math.Round(r.FromSec.Value * Fps), 1e-6);
            foreach (ReframeKeyframe k in r.Keyframes)
                (k.AtSec * Fps).Should().BeApproximately(Math.Round(k.AtSec * Fps), 1e-6);
        }
        a.Report.MaxBeatOffsetMs.Should().BeLessThanOrEqualTo(1000.0 / Fps / 2 + 0.01);
    }

    [Fact]
    public void Reframe_caps_are_respected()
    {
        // 40 s of one source at 200 bpm gives many lines; Fast budget is large enough to hit the per-source cap of 16.
        IReadOnlyList<DynamicsSpan> spans = [new("s1", 0, 0, 120, true)];
        DynamicsPlan p = DynamicsPlanner.Plan(spans, new(0.3, 0, 0, null),
            [], Enumerable.Range(0, 60).Select(_ => E("PunchIn", moment: "OnBar")).ToArray(), DynamicPace.Fast, Fps);
        p.Reframes.Count.Should().BeLessThanOrEqualTo(VideoReframe.MaxReframesPerSource);
        VideoReframe.Validate(p.Reframes).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Accent_falls_back_to_drop_plus_bar_lines_and_uses_measured_accents_when_given()
    {
        DynamicsPlan fallback = DynamicsPlanner.Plan(Spans, Grid(drop: 5.0), [], [E("FlashFrame", moment: "OnAccent")], DynamicPace.Medium, Fps, fill: false);
        fallback.Flashes.Single().ProgramFrame.Should().Be(0, "the first bar line");
        DynamicsPlan measured = DynamicsPlanner.Plan(Spans, Grid(accents: [2.0]), [], [E("FlashFrame", moment: "OnAccent")], DynamicPace.Medium, Fps, fill: false);
        measured.Flashes.Single().ProgramFrame.Should().Be(60);
    }

    [Fact]
    public void Unusable_grid_returns_the_spans_untouched()
    {
        DynamicsPlan p = DynamicsPlanner.Plan(Spans, new(0, 0, 0, null), [], [E("PunchIn")], DynamicPace.Fast, Fps, fill: false);
        p.Segments.Should().HaveCount(2);
        p.Report.Dropped.Should().ContainSingle();
    }
}

public class DynamicsContractTests
{
    [Fact]
    public void DynamicsEntryOutput_and_the_decision_output_carry_only_strings()
    {
        foreach (Type t in new[] { typeof(DynamicsEntryOutput), typeof(VideoEditDecisionOutput) })
            t.GetProperties().Where(p => p.PropertyType != typeof(string) && !p.PropertyType.IsGenericType)
                .Should().BeEmpty(t.Name);
        typeof(VideoEditDecisionOutput).GetProperty("Dynamics")!.PropertyType.Should().Be(typeof(List<DynamicsEntryOutput>));
    }

    [Fact]
    public void Enums_serialize_by_name()
    {
        System.Text.Json.JsonSerializer.Serialize(DynamicEffect.BeatAlternate).Should().Be("\"BeatAlternate\"");
        System.Text.Json.JsonSerializer.Serialize(DynamicPace.Fast).Should().Be("\"Fast\"");
    }

    [Fact]
    public void Dynamics_requires_reencode_and_null_is_byte_identical()
    {
        VideoCompileStepConfig baseCfg = new(1, new ExtractInputRef(ExtractInputSource.Step, 0), 0);
        baseCfg.Validate().Should().NotContain(e => e.Contains("DYNAMICS"));
        System.Text.Json.JsonSerializer.Serialize(baseCfg).Should().Contain("\"Dynamics\":null");
        (baseCfg with { Mode = VideoCompileMode.StreamCopy, Dynamics = new VideoDynamicsConfig() }).Validate()
            .Should().Contain(e => e.StartsWith("DYNAMICS_REQUIRES_REENCODE"));
        (baseCfg with { Dynamics = new VideoDynamicsConfig() }).Validate().Should().NotContain(e => e.Contains("DYNAMICS"));
    }
}
