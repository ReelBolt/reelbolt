using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

using ResolvedSpan = VideoCompileStepExecutor.ResolvedSpan;

/// <summary>
/// Tests for <see cref="OutputTimeline"/> — in particular that it produces IDENTICAL results to
/// the pre-Cut-transitions <c>VideoCompileStepExecutor.MapSourceToOutputSec</c>/
/// <c>MapSourceWindowToOutput</c> static methods when every seam is a
/// <see cref="SeamTreatment.HardCut"/> (zero overlap), and that a real crossfade overlap shifts a
/// following span's placement backward without shortening its own presented duration. See
/// docs/video-editing.md "Cut transitions".
/// </summary>
public class OutputTimelineTests
{
    private static List<ResolvedSpan> ThreeSpanFixture() =>
    [
        new(0.0, 10.0, 0.0, 10.0, 0, 300),
        new(20.0, 30.0, 20.0, 30.0, 600, 900),
        new(40.0, 45.0, 40.0, 45.0, 1200, 1350)
    ];

    private static List<SeamPlan> AllHardCut(int seamCount)
    {
        var seams = new List<SeamPlan>(seamCount);
        for (int i = 0; i < seamCount; i++)
            seams.Add(new SeamPlan(i, SeamTreatment.HardCut, 0, 0, "", 0, "test:hardCut"));
        return seams;
    }

    [Fact]
    public void Zero_overlap_TotalSec_equals_sum_of_span_durations()
    {
        List<ResolvedSpan> spans = ThreeSpanFixture();
        OutputTimeline timeline = OutputTimeline.Build(spans, AllHardCut(2));

        timeline.TotalSec.Should().Be(10.0 + 10.0 + 5.0);
    }

    [Theory]
    [InlineData(5.0, 5.0)]
    [InlineData(25.0, 15.0)]
    [InlineData(41.0, 21.0)]
    public void Zero_overlap_MapToOutputSec_matches_the_static_executor_method(double sourceSec, double expected)
    {
        List<ResolvedSpan> spans = ThreeSpanFixture();
        OutputTimeline timeline = OutputTimeline.Build(spans, AllHardCut(2));

        timeline.MapToOutputSec(sourceSec).Should().Be(expected);
        timeline.MapToOutputSec(sourceSec).Should().Be(VideoCompileStepExecutor.MapSourceToOutputSec(spans, sourceSec));
    }

    [Theory]
    [InlineData(15.0)]
    [InlineData(46.0)]
    public void Zero_overlap_MapToOutputSec_null_cases_match_the_static_executor_method(double sourceSec)
    {
        List<ResolvedSpan> spans = ThreeSpanFixture();
        OutputTimeline timeline = OutputTimeline.Build(spans, AllHardCut(2));

        timeline.MapToOutputSec(sourceSec).Should().BeNull();
        VideoCompileStepExecutor.MapSourceToOutputSec(spans, sourceSec).Should().BeNull();
    }

    [Fact]
    public void Zero_overlap_MapWindowToOutput_matches_the_static_executor_method()
    {
        List<ResolvedSpan> spans = ThreeSpanFixture();
        OutputTimeline timeline = OutputTimeline.Build(spans, AllHardCut(2));

        (double Start, double End)? actual = timeline.MapWindowToOutput(22.0, 27.0);
        (double Start, double End)? expected = VideoCompileStepExecutor.MapSourceWindowToOutput(spans, 22.0, 27.0);

        actual.Should().Be(expected);
        actual!.Value.Start.Should().Be(12.0);
        actual.Value.End.Should().Be(17.0);
    }

    [Fact]
    public void Overlapping_seam_shifts_the_following_spans_placement_backward_without_shortening_it()
    {
        List<ResolvedSpan> spans =
        [
            new(0, 5, 0, 5, 0, 0),
            new(10, 15, 10, 15, 0, 0)
        ];
        var seams = new List<SeamPlan> { new(0, SeamTreatment.Dissolve, 1.0, 1.0, "fade", 0, "test") };

        OutputTimeline timeline = OutputTimeline.Build(spans, seams);

        timeline.Placements[0].OutputStartSec.Should().Be(0);
        timeline.Placements[0].OutputEndSec.Should().Be(5);
        // Span 1 is pulled back by the 1s overlap but keeps its own full 5s presented duration.
        timeline.Placements[1].OutputStartSec.Should().Be(4);
        timeline.Placements[1].OutputEndSec.Should().Be(9);
        timeline.TotalSec.Should().Be(9);
    }

    [Fact]
    public void MapToOutputSec_within_an_overlapping_spans_own_window_uses_its_shifted_placement()
    {
        List<ResolvedSpan> spans =
        [
            new(0, 5, 0, 5, 0, 0),
            new(10, 15, 10, 15, 0, 0)
        ];
        var seams = new List<SeamPlan> { new(0, SeamTreatment.Dissolve, 1.0, 1.0, "fade", 0, "test") };

        OutputTimeline timeline = OutputTimeline.Build(spans, seams);

        // Source second 12 is 2s into span 1's own [10,15) window; span 1's own output window is
        // [4,9), so this should map to 4+2=6.
        timeline.MapToOutputSec(12.0).Should().Be(6.0);
    }

    [Fact]
    public void MapToOutputSec_respects_source_index_and_ignores_other_sources_spans()
    {
        List<ResolvedSpan> spans =
        [
            new(0, 5, 0, 5, 0, 0, SourceIndex: 0),
            new(0, 5, 0, 5, 0, 0, SourceIndex: 1)
        ];
        OutputTimeline timeline = OutputTimeline.Build(spans, AllHardCut(1));

        timeline.MapToOutputSec(2.0, sourceIndex: 0).Should().Be(2.0);
        timeline.MapToOutputSec(2.0, sourceIndex: 1).Should().Be(7.0); // after source 0's own 5s span
    }

    // -----------------------------------------------------------------------
    // ClipAwayTransitionOverlaps — content that must be pinned to ONE source clip's geometry (a
    // tracked screen insert) cannot paint over frames where two spans are blended together.
    // -----------------------------------------------------------------------

    [Fact]
    public void ClipAwayTransitionOverlaps_is_a_no_op_when_every_seam_is_a_hard_cut()
    {
        OutputTimeline timeline = OutputTimeline.Build(ThreeSpanFixture(), AllHardCut(2));

        timeline.TransitionOverlapRanges().Should().BeEmpty();
        timeline.ClipAwayTransitionOverlaps(3.0, 18.0).Should().Be((3.0, 18.0));
    }

    [Fact]
    public void ClipAwayTransitionOverlaps_trims_a_window_back_to_its_longest_clean_stretch()
    {
        List<ResolvedSpan> spans = ThreeSpanFixture();
        List<SeamPlan> seams =
        [
            new SeamPlan(0, SeamTreatment.Dissolve, DurationSec: 1.0, OverlapSec: 1.0, "fade", 0, "test:dissolve"),
            new SeamPlan(1, SeamTreatment.HardCut, DurationSec: 0, OverlapSec: 0, "", 0, "test:hardCut")
        ];
        OutputTimeline timeline = OutputTimeline.Build(spans, seams);

        // Span 1 is pulled back 1s, so it starts at 9.0 and the blend covers [9, 10).
        timeline.TransitionOverlapRanges().Should().ContainSingle().Which.Should().Be((9.0, 10.0));

        // A window straddling the blend keeps only its longer clean side.
        timeline.ClipAwayTransitionOverlaps(8.5, 15.0).Should().Be((10.0, 15.0));
        timeline.ClipAwayTransitionOverlaps(2.0, 9.5).Should().Be((2.0, 9.0));

        // A window entirely inside the blend has nothing left.
        timeline.ClipAwayTransitionOverlaps(9.2, 9.8).Should().BeNull();

        // A window nowhere near the blend is untouched.
        timeline.ClipAwayTransitionOverlaps(11.0, 14.0).Should().Be((11.0, 14.0));
    }
}
