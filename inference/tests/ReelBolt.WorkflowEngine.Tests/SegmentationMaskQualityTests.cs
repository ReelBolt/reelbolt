#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Segmentation;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The mask-quality self-check policy (T-017, plan §12.2 / D-18): <c>Empty</c> when the object is missing
/// from every frame, <c>Degraded</c> when it is missing from more than a fifth of the clip OR from any
/// single run longer than a second, <c>Clean</c> otherwise. Pure: no harness, no vendor, no clock. The
/// thresholds are checked against the real answers — the reported run warns, the live two-frame gap and
/// the bar fixture's half-second occlusion do not.
/// </summary>
public sealed class SegmentationMaskQualityTests
{
    [Fact]
    public void The_reported_run_is_degraded_with_its_leading_gap_and_seconds()
    {
        SegmentationMaskAssessment a =
            SegmentationMaskQualityPolicy.Assess(827, 211, 211, 59.94, 211, 826);

        a.Quality.Should().Be(SegmentationMaskQuality.Degraded);
        a.LeadingGapFrames.Should().Be(211);
        a.EmptyFraction.Should().BeApproximately(0.2551, 1e-4);
        a.LongestGapSeconds.Should().BeApproximately(3.52, 1e-2);
    }

    [Fact]
    public void A_mask_empty_on_every_frame_is_empty()
    {
        SegmentationMaskAssessment a =
            SegmentationMaskQualityPolicy.Assess(827, 827, 827, 59.94, null, null);

        a.Quality.Should().Be(SegmentationMaskQuality.Empty);
        a.LeadingGapFrames.Should().Be(0);
        a.TrailingGapFrames.Should().Be(0);
    }

    [Fact]
    public void The_live_two_frame_gap_stays_clean()
    {
        SegmentationMaskQualityPolicy.Assess(141, 2, 2, 59.94, 2, 140)
            .Quality.Should().Be(SegmentationMaskQuality.Clean);
    }

    [Fact]
    public void The_bar_fixtures_half_second_occlusion_stays_clean()
    {
        SegmentationMaskQualityPolicy.Assess(144, 12, 12, 24, 12, 143)
            .Quality.Should().Be(SegmentationMaskQuality.Clean);
    }

    [Fact]
    public void A_fifth_of_the_frames_empty_is_degraded_by_fraction()
    {
        SegmentationMaskQualityPolicy.Assess(100, 21, 1, 30, 21, 99)
            .Quality.Should().Be(SegmentationMaskQuality.Degraded);
    }

    [Fact]
    public void A_run_longer_than_a_second_is_degraded_by_run()
    {
        SegmentationMaskQualityPolicy.Assess(600, 40, 31, 30, 40, 599)
            .Quality.Should().Be(SegmentationMaskQuality.Degraded);
    }

    [Fact]
    public void A_nonsense_input_is_clean_and_never_throws()
    {
        SegmentationMaskAssessment a =
            SegmentationMaskQualityPolicy.Assess(0, 0, 0, 0, null, null);

        a.Quality.Should().Be(SegmentationMaskQuality.Clean);
        a.FrameCount.Should().Be(0);
        a.LeadingGapFrames.Should().Be(0);
        a.TrailingGapFrames.Should().Be(0);
    }

    [Fact]
    public void An_unknown_first_frame_reports_no_gap_rather_than_the_whole_clip()
    {
        // An artifact or cache entry written before these facts existed: the empty-frame count is known
        // and the first frame is not. Reporting the whole clip as a leading gap would be a fabricated
        // number in the EDL, and "found on none" would be a fabricated sentence.
        SegmentationMaskAssessment a =
            SegmentationMaskQualityPolicy.Assess(827, 0, 0, 59.94, null, null);

        a.Quality.Should().Be(SegmentationMaskQuality.Clean);
        a.LeadingGapFrames.Should().Be(0);
        a.TrailingGapFrames.Should().Be(0);
        SegmentationMaskQualityPolicy.Describe(a).Should().Contain("827").And.NotContain("none");
    }

    [Fact]
    public void An_out_of_range_frame_index_is_treated_as_unknown()
    {
        SegmentationMaskAssessment a =
            SegmentationMaskQualityPolicy.Assess(100, 5, 1, 30, 900, -3);

        a.FirstNonEmptyFrame.Should().BeNull();
        a.LastNonEmptyFrame.Should().BeNull();
        a.LeadingGapFrames.Should().Be(0);
        a.TrailingGapFrames.Should().Be(0);
    }

    [Fact]
    public void Describe_names_the_frames_and_the_seconds_without_a_prompt()
    {
        SegmentationMaskAssessment a =
            SegmentationMaskQualityPolicy.Assess(827, 211, 211, 59.94, 211, 826);

        string sentence = SegmentationMaskQualityPolicy.Describe(a);

        sentence.Should().Contain("827");
        sentence.Should().Contain("211");
        sentence.Should().Contain("3.52");
        sentence.Should().NotContain("speedometer", because: "the prompt never reaches a log line (F-1)");
    }

    // ---------------------------------------------------------------------------------------------
    // The step output's warnings — what the customer actually sees on the run page.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_reported_run_warns_that_the_effect_misses_the_head_of_the_clip()
    {
        List<JsonObject> warnings = VideoAnalyzeStepExecutor.BuildSegmentationWarnings(
        [
            Track("g0", "speedometer display", frameCount: 827, emptyFrames: 211, longestGapFrames: 211,
                firstNonEmptyFrame: 211, lastNonEmptyFrame: 826, fps: 59.94)
        ]);

        warnings.Should().ContainSingle();
        warnings[0]["code"]!.GetValue<string>().Should().Be("segmentation_gap");
        string message = warnings[0]["message"]!.GetValue<string>();
        message.Should().Contain("speedometer display")
            .And.Contain("616 of 827 frames")
            .And.Contain("the first was frame 211")
            .And.Contain("first 3.52 s");
    }

    [Fact]
    public void An_internal_gap_does_not_claim_the_head()
    {
        // No leading gap at all: the object is found on frame 0 and lost for 90 frames in the middle.
        List<JsonObject> warnings = VideoAnalyzeStepExecutor.BuildSegmentationWarnings(
        [
            Track("g0", "speedometer display", frameCount: 400, emptyFrames: 90, longestGapFrames: 90,
                firstNonEmptyFrame: 0, lastNonEmptyFrame: 399, fps: 30)
        ]);

        warnings.Should().ContainSingle();
        warnings[0]["code"]!.GetValue<string>().Should().Be("segmentation_gap");
        warnings[0]["message"]!.GetValue<string>()
            .Should().Contain("the longest stretch with no object is 90 frames")
            .And.NotContain("from the first");
    }

    [Fact]
    public void An_all_empty_mask_warns_that_the_effect_would_do_nothing()
    {
        List<JsonObject> warnings = VideoAnalyzeStepExecutor.BuildSegmentationWarnings(
        [
            Track("g0", "speed indication numbers", frameCount: 827, emptyFrames: 827, longestGapFrames: 827,
                firstNonEmptyFrame: null, lastNonEmptyFrame: null, fps: 59.94)
        ]);

        warnings.Should().ContainSingle();
        warnings[0]["code"]!.GetValue<string>().Should().Be("segmentation_no_object");
        warnings[0]["message"]!.GetValue<string>().Should().Contain("no frame of this clip");
    }

    [Fact]
    public void A_clean_mask_and_a_step_with_no_tracks_warn_about_nothing()
    {
        // The additive property: a compile that has nothing to warn about keeps its exact shape, so a
        // clean run's step output carries no "warnings" key at all.
        VideoAnalyzeStepExecutor.BuildSegmentationWarnings(
        [
            Track("g0", "speedometer", frameCount: 141, emptyFrames: 2, longestGapFrames: 2,
                firstNonEmptyFrame: 2, lastNonEmptyFrame: 140, fps: 59.94)
        ]).Should().BeEmpty();

        VideoAnalyzeStepExecutor.BuildSegmentationWarnings([]).Should().BeEmpty();
    }

    [Fact]
    public void Only_the_affected_tracks_warn()
    {
        List<JsonObject> warnings = VideoAnalyzeStepExecutor.BuildSegmentationWarnings(
        [
            Track("g0", "steady", frameCount: 141, emptyFrames: 0, longestGapFrames: 0,
                firstNonEmptyFrame: 0, lastNonEmptyFrame: 140, fps: 59.94),
            Track("g1", "late", frameCount: 827, emptyFrames: 211, longestGapFrames: 211,
                firstNonEmptyFrame: 211, lastNonEmptyFrame: 826, fps: 59.94)
        ]);

        warnings.Should().ContainSingle();
        warnings[0]["message"]!.GetValue<string>().Should().Contain("late").And.NotContain("steady");
    }

    private static VideoSegmentationTrack Track(
        string id, string prompt, int frameCount, int emptyFrames, int longestGapFrames,
        int? firstNonEmptyFrame, int? lastNonEmptyFrame, double fps) =>
        new(id, 0, prompt, StartSec: 0, EndSec: frameCount / fps, FrameCount: frameCount, Fps: fps,
            Coverage: 0.05, Confidence: 0.9,
            MaskStorageKey: $"projects/p/agentFiles/segmentation/{id}.mkv",
            MaskContentHash: new string('a', 64), Model: "fal-ai/sam-3-1/video-rle",
            Facts: new VideoSegmentationFacts(
                emptyFrames, longestGapFrames, 0.5, false, firstNonEmptyFrame, lastNonEmptyFrame));
}
