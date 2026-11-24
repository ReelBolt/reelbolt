using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Follow;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Follow;

/// <summary>followSubject to Reframes: track resolution, framing, caps (<see cref="FollowSubjectExpander"/>).</summary>
public class FollowSubjectExpanderTests
{
    private static readonly VideoAnalysisMedia Wide = new(10, 30, 1, 1920, 1080);
    private static readonly VideoAnalysisMedia Vertical = new(10, 30, 1, 1080, 1920);

    private static VideoCompileStepConfig Config(IReadOnlyList<FollowSubjectRequest> follow, IReadOnlyList<VideoReframe>? hand = null) =>
        new(1, new ExtractInputRef(ExtractInputSource.Previous), 1, Reframes: hand, FollowSubject: follow);

    /// <summary>A small subject walking right, sampled every 0.5 s over 0..9 s.</summary>
    private static VideoInsertRegionTrack Feature(string id = "r1", int source = 0, double x0 = 0.2, double x1 = 0.8) =>
        new(Id: id, ShotId: "s0", StartSec: 0, EndSec: 9,
            Keyframes: Enumerable.Range(0, 19).Select(i =>
            {
                double t = i * 0.5;
                double x = x0 + (x1 - x0) * t / 9;
                return new VideoInsertQuadKeyframe(t, x, 0.45, x + 0.08, 0.45, x, 0.55, x + 0.08, 0.55);
            }).ToList(),
            Confidence: 0.2, MeanAreaRatio: 0.01, MeanAspectRatio: 1, MotionClass: "Moving", ColorName: "none",
            SourceIndex: source, Method: "feature");

    private static VideoSegmentationTrack Seg(string id = "g1", IReadOnlyList<VideoSegmentationBoxFrame>? boxes = null) =>
        new(id, 0, "person", 0, 9, 90, 10, 0.1, 0.9, "k", "h", "m",
            BoxKeyframes: boxes ?? Enumerable.Range(0, 19).Select(i => new VideoSegmentationBoxFrame(i * 0.5, 0.3 + i * 0.01, 0.3, 0.1, 0.3)).ToList());

    private static VideoAnalysisArtifact Artifact(IReadOnlyList<VideoInsertRegionTrack>? r = null, IReadOnlyList<VideoSegmentationTrack>? g = null) => new(
        Version: 2, Media: Wide, Shots: [new VideoAnalysisShot("s0", 0, 10)], SilenceSpans: [], Segments: [], Words: [],
        OfferedIds: ["s0"], Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
        InsertRegions: r, SegmentationTracks: g);

    private static FollowSubjectExpansion Run(VideoCompileStepConfig config, VideoAnalysisArtifact artifact,
        VideoAnalysisMedia? canvas = null, Func<int, VideoAnalysisMedia>? media = null) =>
        FollowSubjectExpander.Expand(config, artifact, media ?? (_ => Wide), canvas ?? Vertical, _ => true);

    [Fact]
    public void A_feature_track_becomes_reframes_that_validate()
    {
        FollowSubjectExpansion e = Run(Config([new FollowSubjectRequest("r1")]), Artifact([Feature()]));

        e.ErrorCode.Should().BeNull();
        e.Reframes.Should().NotBeEmpty().And.OnlyContain(r => r.SourceIndex == 0);
        VideoReframe.Validate(e.Reframes).IsValid.Should().BeTrue();
        FollowSubjectReportEntry entry = e.Report.Single();
        entry.Applied.Should().BeTrue();
        entry.Reframes.Should().Be(e.Reframes.Count);
        entry.Keyframes.Should().Be(e.Reframes.Sum(r => r.Keyframes.Count));
        e.ToNode()[0]!["trackId"]!.GetValue<string>().Should().Be("r1");
    }

    [Fact]
    public void A_segmentation_track_with_boxes_becomes_reframes()
    {
        FollowSubjectExpansion e = Run(Config([new FollowSubjectRequest("g1", Tightness: FollowTightness.Tight)]), Artifact(g: [Seg()]));

        e.ErrorCode.Should().BeNull();
        e.Report.Single().Applied.Should().BeTrue();
        VideoReframe.Validate(e.Reframes).IsValid.Should().BeTrue();
    }

    [Fact]
    public void The_first_segmentation_track_g0_of_a_one_prompt_analysis_can_be_followed()
    {
        // VideoAnalyze numbers segmentation tracks from g0 (and feature tracks from r0): a one-prompt
        // analysis has exactly one track, g0, and it must pass the save-time check and expand.
        VideoCompileStepConfig config = Config([new FollowSubjectRequest("g0")]) with { Mode = VideoCompileMode.Reencode };
        VideoReframe.CheckConfig(config).IsValid.Should().BeTrue();

        FollowSubjectExpansion e = Run(config, Artifact(g: [Seg("g0")]));

        e.ErrorCode.Should().BeNull();
        e.Report.Single().Applied.Should().BeTrue();
        e.Report.Single().TrackId.Should().Be("g0");
    }

    [Fact]
    public void The_first_feature_track_r0_can_be_followed()
    {
        FollowSubjectExpansion e = Run(Config([new FollowSubjectRequest("r0")]), Artifact([Feature("r0")]));

        e.ErrorCode.Should().BeNull();
        e.Report.Single().Applied.Should().BeTrue();
    }

    [Fact]
    public void A_follow_that_can_neither_zoom_nor_pan_is_flagged_and_warned()
    {
        // A 1080p 16:9 clip shown whole (padded) in a 1080x1920 canvas: the sharp zoom cap is 1 and the
        // whole frame is visible, so the camera path cannot move at all.
        FollowSubjectExpansion e = FollowSubjectExpander.Expand(
            Config([new FollowSubjectRequest("g1")]), Artifact(g: [Seg()]), _ => Wide, Vertical, _ => false);

        FollowSubjectReportEntry entry = e.Report.Single();
        entry.Applied.Should().BeTrue();
        entry.ZoomCap.Should().Be(1);
        entry.FramingFixed.Should().BeTrue();
        e.ToNode()[0]!["framingFixed"]!.GetValue<bool>().Should().BeTrue();
        JsonObject warning = e.BuildWarnings().Single();
        warning["code"]!.GetValue<string>().Should().Be("follow_subject_cannot_move");
        warning["message"]!.GetValue<string>().Should().Contain("g1");
    }

    [Fact]
    public void A_covered_follow_can_pan_and_is_not_warned()
    {
        // Same clip cover-fitted: zoom cap 1, but the cover crop pans across the frame.
        FollowSubjectExpansion e = Run(Config([new FollowSubjectRequest("g1")]), Artifact(g: [Seg()]));

        e.Report.Single().ZoomCap.Should().Be(1);
        e.Report.Single().FramingFixed.Should().BeFalse();
        e.ToNode()[0]!.AsObject().ContainsKey("framingFixed").Should().BeFalse("the report keeps its shape when the camera can move");
        e.BuildWarnings().Should().BeEmpty();
    }

    [Fact]
    public void A_chroma_track_with_a_matching_id_is_not_a_feature_track()
    {
        VideoInsertRegionTrack chroma = Feature() with { Method = "chroma" };
        Run(Config([new FollowSubjectRequest("r1")]), Artifact([chroma])).ErrorCode.Should().Be("FOLLOW_TRACK_NOT_FOUND");
    }

    [Fact]
    public void An_unknown_track_or_a_disagreeing_source_is_not_found()
    {
        Run(Config([new FollowSubjectRequest("r9")]), Artifact([Feature()])).ErrorCode.Should().Be("FOLLOW_TRACK_NOT_FOUND");
        Run(Config([new FollowSubjectRequest("g1")]), Artifact([Feature()])).ErrorCode.Should().Be("FOLLOW_TRACK_NOT_FOUND");
        Run(Config([new FollowSubjectRequest("r1", SourceIndex: 3)]), Artifact([Feature()])).ErrorCode.Should().Be("FOLLOW_TRACK_NOT_FOUND");
    }

    [Fact]
    public void A_track_without_usable_boxes_is_empty()
    {
        VideoSegmentationTrack none = Seg() with { BoxKeyframes = null };
        VideoSegmentationTrack zero = Seg("g2", [new VideoSegmentationBoxFrame(1, 0.1, 0.1, 0, 0)]);
        Run(Config([new FollowSubjectRequest("g1")]), Artifact(g: [none])).ErrorCode.Should().Be("FOLLOW_TRACK_EMPTY");
        Run(Config([new FollowSubjectRequest("g2")]), Artifact(g: [zero])).ErrorCode.Should().Be("FOLLOW_TRACK_EMPTY");
    }

    [Fact]
    public void An_unknown_source_size_skips_the_entry_and_reports_it()
    {
        FollowSubjectExpansion e = Run(Config([new FollowSubjectRequest("r1")]), Artifact([Feature()]),
            media: _ => new VideoAnalysisMedia(10, 30, 1, 0, 0));

        e.ErrorCode.Should().BeNull();
        e.Reframes.Should().BeEmpty();
        e.Report.Single().Applied.Should().BeFalse();
        e.Report.Single().Reason.Should().Be("source_size_unknown");
    }

    [Fact]
    public void A_wide_source_on_a_vertical_canvas_keeps_the_subject_in_the_visible_window()
    {
        VideoInsertRegionTrack still = Feature(x0: 0.6, x1: 0.6);
        FollowSubjectExpansion e = Run(Config([new FollowSubjectRequest("r1", Tightness: FollowTightness.Loose, AllowSoftZoom: true)]), Artifact([still]));

        e.Report.Single().Applied.Should().BeTrue();
        double visibleWidth = (9.0 / 16.0) / (1920.0 / 1080.0);
        // Soft zoom is on: at the sharp cap (1.0 for a 1080p source into 1920 rows) the engine centres the window and nothing can pan.
        // The camera eases in from the full frame, so check the settled (last) key, after the engine's own centre clamp.
        foreach (ReframeKeyframe k in new[] { VideoReframe.Validate(e.Reframes).Normalized.SelectMany(r => r.Keyframes).Last() })
        {
            double half = visibleWidth / k.Zoom / 2;
            // subject spans x 0.60..0.68 of the source (away from the frame edge, where the engine's own centre clamp is inactive)
            (k.CenterX - half).Should().BeLessThanOrEqualTo(0.60 + 1e-6);
            (k.CenterX + half).Should().BeGreaterThanOrEqualTo(0.68 - 1e-6);
        }
    }

    [Fact]
    public void The_keyframe_cap_is_shared_with_hand_written_reframes_on_another_source()
    {
        // 15 hand reframes x 32 keys = 480 of the 512 per-compile keys, all on source 1.
        List<VideoReframe> hand = Enumerable.Range(0, 15).Select(i =>
            new VideoReframe(1, i, i + 1, Enumerable.Range(0, 32).Select(k => new ReframeKeyframe(k * 0.03)).ToList())).ToList();

        FollowSubjectExpansion e = Run(Config([new FollowSubjectRequest("r1")], hand), Artifact([Feature()]));

        e.ErrorCode.Should().BeNull();
        e.Report.Single().Applied.Should().BeTrue();
        e.Reframes.Sum(r => r.Keyframes.Count).Should().BeLessThanOrEqualTo(32);
        VideoReframe.Validate([.. hand, .. e.Reframes]).IsValid.Should().BeTrue();
    }

    [Fact]
    public void No_keyframe_room_skips_the_entry_without_failing()
    {
        List<VideoReframe> hand = Enumerable.Range(0, 16).Select(i =>
            new VideoReframe(1, i, i + 1, Enumerable.Range(0, 32).Select(k => new ReframeKeyframe(k * 0.03)).ToList())).ToList();

        FollowSubjectExpansion e = Run(Config([new FollowSubjectRequest("r1")], hand), Artifact([Feature()]));

        e.ErrorCode.Should().BeNull();
        e.Report.Single().Reason.Should().Be("reframe_budget_exhausted");
    }

    [Fact]
    public void A_hand_reframe_on_the_tracks_own_source_is_a_conflict()
    {
        VideoReframe hand = new(0, 1, 2, [new ReframeKeyframe(0, 2)]);
        Run(Config([new FollowSubjectRequest("r1")], [hand]), Artifact([Feature()])).ErrorCode.Should().Be("FOLLOW_CONFLICTS_REFRAME");
    }

    [Fact]
    public void An_empty_request_changes_nothing()
    {
        VideoCompileStepConfig config = Config([]);
        FollowSubjectExpansion e = Run(config, Artifact());
        e.ErrorCode.Should().BeNull();
        e.Reframes.Should().BeEmpty();
        e.Report.Should().BeEmpty();
    }

    [Fact]
    public void Expanded_reframes_are_visible_anchored()
    {
        FollowSubjectExpansion e = Run(Config([new FollowSubjectRequest("r1")]), Artifact([Feature()]));

        e.Reframes.Should().NotBeEmpty().And.OnlyContain(r => r.Anchor == ReframeAnchor.Visible);
    }

    [Fact]
    public void A_huge_range_with_an_unknown_duration_is_clamped_to_the_track()
    {
        VideoAnalysisMedia unknown = Wide with { DurationSec = 0 };
        FollowSubjectExpansion e = Run(
            Config([new FollowSubjectRequest("r1", FromSec: 0, ToSec: 1e12)]), Artifact([Feature()]), media: _ => unknown);

        e.ErrorCode.Should().BeNull();
        e.Report.Single().Applied.Should().BeTrue();
        e.Reframes.Max(r => r.ToSec ?? 0).Should().BeLessThanOrEqualTo(9 + 1e-6);
    }

    [Fact]
    public void A_huge_range_with_a_known_duration_is_clamped_to_the_duration()
    {
        FollowSubjectExpansion e = Run(Config([new FollowSubjectRequest("r1", FromSec: 0, ToSec: 1e12)]), Artifact([Feature()]));

        e.ErrorCode.Should().BeNull();
        e.Report.Single().Applied.Should().BeTrue();
        e.Reframes.Max(r => r.ToSec ?? 0).Should().BeLessThanOrEqualTo(Wide.DurationSec + 1e-6);
    }

    [Fact]
    public void A_range_past_the_end_is_skipped_as_empty_and_never_throws()
    {
        FollowSubjectExpansion e = Run(Config([new FollowSubjectRequest("r1", FromSec: 50, ToSec: 60)]), Artifact([Feature()]));

        e.ErrorCode.Should().BeNull();
        e.Reframes.Should().BeEmpty();
        e.Report.Single().Applied.Should().BeFalse();
        e.Report.Single().Reason.Should().Be("range_empty");
    }

    [Fact]
    public void Two_unresolved_entries_on_one_source_with_whole_ranges_skip_the_later_as_overlap()
    {
        FollowSubjectExpansion e = Run(
            Config([new FollowSubjectRequest("r1"), new FollowSubjectRequest("r2")]), Artifact([Feature("r1"), Feature("r2")]));

        e.ErrorCode.Should().BeNull();
        e.Report[0].Applied.Should().BeTrue();
        e.Report[1].Applied.Should().BeFalse();
        e.Report[1].Reason.Should().Be("overlap");
    }

    [Fact]
    public void Framing_follows_each_sources_own_fit_in_a_mixed_compile()
    {
        // Source 0 is covered (cropped) into the tall canvas, source 1 is padded (shown whole).
        FollowSubjectExpansion e = FollowSubjectExpander.Expand(
            Config([new FollowSubjectRequest("r1"), new FollowSubjectRequest("r2")]),
            Artifact([Feature("r1", source: 0), Feature("r2", source: 1)]), _ => Wide, Vertical, idx => idx == 0);

        e.ErrorCode.Should().BeNull();
        double Span(int source)
        {
            var xs = e.Reframes.Where(r => r.SourceIndex == source).SelectMany(r => r.Keyframes).Select(k => k.CenterX).ToList();
            return xs.Max() - xs.Min();
        }

        Span(0).Should().BeGreaterThan(Span(1) + 0.05, "a covered clip's camera pans across the picture, a whole-frame one cannot at this zoom");
    }
}
