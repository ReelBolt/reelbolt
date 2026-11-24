namespace ReelBolt.WorkflowEngine.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

/// <summary>
/// Pins the cut-rationale join in <see cref="TimelineManifestBuilder"/> to each resolved span's
/// OWN originating <see cref="VideoEditKeepSpan"/> — never to the loop index <c>i</c>.
///
/// <para>
/// The index join was only valid while the raw <c>Keep</c> list survived the executor's span
/// pipeline 1:1, and it does not: <c>VideoCompileStepExecutor</c> coalesces adjacent spans, drops
/// spans shorter than <c>MinSegmentMs</c> and caps the list at <c>MaxSegments</c>. It also silently
/// ran backwards through <c>decision.Keep.FirstOrDefault(non-empty reason)</c>, so a cut with no
/// reason of its own inherited the FIRST reason anywhere in <c>Keep</c> — routinely another cut's.
/// </para>
/// </summary>
public class VideoTimelineRationaleJoinTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void Coalesced_span_takes_the_reason_of_its_own_first_originating_keep_span()
    {
        // 3 Keep spans with distinct reasons. keep[0] was dropped by MinSegmentMs; keep[1] and
        // keep[2] were coalesced into ONE resolved span whose origins are
        // [keep1.FromId, keep2.FromId] in that order.
        var decision = new VideoEditDecisionOutput
        {
            Keep = new List<VideoEditKeepSpan>
            {
                new() { FromId = "s0", ToId = "s0", Reason = "DROPPED-CLIP-REASON" },
                new() { FromId = "s1", ToId = "s1", Reason = "MERGED-FIRST-REASON" },
                new() { FromId = "s2", ToId = "s2", Reason = "MERGED-SECOND-REASON" },
            },
            EditRationale = "EDIT-LEVEL-RATIONALE",
        };

        var spans = new List<VideoCompileStepExecutor.ResolvedSpan>
        {
            Span(4.0, 12.0, new[] { "s1", "s2" }),
        };

        var manifest = BuildManifest(spans, decision);

        var videoTrack = manifest.Tracks.Single(t => t.Type == "video");
        videoTrack.Items.Should().ContainSingle();
        videoTrack.Items[0].AgentRationale.Should().Be("MERGED-FIRST-REASON");
        videoTrack.Items[0].AgentRationale.Should().NotBe("MERGED-SECOND-REASON");
        videoTrack.Items[0].AgentRationale.Should().NotBe("EDIT-LEVEL-RATIONALE");

        // The dropped span's reason must appear NOWHERE in the manifest — the old fallback
        // (decision.Keep.FirstOrDefault(non-empty reason)) is exactly what used to leak it in.
        string json = JsonSerializer.Serialize(manifest, JsonOptions);
        json.Should().NotContain("DROPPED-CLIP-REASON");
    }

    [Fact]
    public void Multiple_surviving_spans_each_take_their_own_originating_reason()
    {
        var decision = new VideoEditDecisionOutput
        {
            Keep = new List<VideoEditKeepSpan>
            {
                new() { FromId = "s0", ToId = "s0", Reason = "HOOK-REASON" },
                new() { FromId = "s1", ToId = "s1", Reason = "COALESCED-INTO-FIRST-REASON" },
                new() { FromId = "s2", ToId = "s2", Reason = "DROPPED-REASON" },
                new() { FromId = "s3", ToId = "s3", Reason = "PAYOFF-REASON" },
            },
            EditRationale = "EDIT-LEVEL-RATIONALE",
        };

        var spans = new List<VideoCompileStepExecutor.ResolvedSpan>
        {
            // keep[0] + keep[1] coalesced; keep[2] dropped; keep[3] alone.
            Span(0.0, 9.0, new[] { "s0", "s1" }),
            Span(39.5, 50.0, new[] { "s3" }),
        };

        var manifest = BuildManifest(spans, decision);

        var items = manifest.Tracks.Single(t => t.Type == "video").Items;
        items.Should().HaveCount(2);
        items[0].AgentRationale.Should().Be("HOOK-REASON");
        items[1].AgentRationale.Should().Be("PAYOFF-REASON");

        string json = JsonSerializer.Serialize(manifest, JsonOptions);
        json.Should().NotContain("DROPPED-REASON");
        json.Should().NotContain("COALESCED-INTO-FIRST-REASON");
    }

    [Fact]
    public void Empty_or_null_origins_fall_back_to_the_edit_rationale_without_throwing()
    {
        var decision = new VideoEditDecisionOutput
        {
            Keep = new List<VideoEditKeepSpan>
            {
                new() { FromId = "s0", ToId = "s0", Reason = "UNRELATED-REASON" },
            },
            EditRationale = "EDIT-LEVEL-RATIONALE",
        };

        var spans = new List<VideoCompileStepExecutor.ResolvedSpan>
        {
            Span(0.0, 5.0, Array.Empty<string>()),
            Span(10.0, 15.0, null!),
        };

        Action act = () => BuildManifest(spans, decision);
        act.Should().NotThrow();

        var items = BuildManifest(spans, decision).Tracks.Single(t => t.Type == "video").Items;
        items.Should().HaveCount(2);
        items[0].AgentRationale.Should().Be("EDIT-LEVEL-RATIONALE");
        items[1].AgentRationale.Should().Be("EDIT-LEVEL-RATIONALE");
        items[0].AgentRationale.Should().NotBe("UNRELATED-REASON");
    }

    [Fact]
    public void Origins_resolving_to_blank_or_unknown_reasons_never_borrow_another_cut_s_reason()
    {
        var decision = new VideoEditDecisionOutput
        {
            Keep = new List<VideoEditKeepSpan>
            {
                new() { FromId = "s0", ToId = "s0", Reason = "   " },
                new() { FromId = "s1", ToId = "s1", Reason = "ANOTHER-CUTS-REASON" },
            },
            EditRationale = "EDIT-LEVEL-RATIONALE",
        };

        var spans = new List<VideoCompileStepExecutor.ResolvedSpan>
        {
            Span(0.0, 5.0, new[] { "s0" }),
            Span(10.0, 15.0, new[] { "s-does-not-exist" }),
        };

        var items = BuildManifest(spans, decision).Tracks.Single(t => t.Type == "video").Items;

        items.Should().HaveCount(2);
        items[0].AgentRationale.Should().Be("EDIT-LEVEL-RATIONALE");
        items[1].AgentRationale.Should().Be("EDIT-LEVEL-RATIONALE");
        JsonSerializer.Serialize(BuildManifest(spans, decision), JsonOptions)
            .Should().NotContain("ANOTHER-CUTS-REASON");
    }

    [Fact]
    public void Null_decision_with_null_origins_never_throws()
    {
        var spans = new List<VideoCompileStepExecutor.ResolvedSpan>
        {
            Span(0.0, 5.0, null),
            Span(10.0, 15.0, Array.Empty<string>()),
        };

        TimelineManifest manifest = null!;
        Action act = () => manifest = BuildManifest(spans, decision: null);
        act.Should().NotThrow();

        manifest.Tracks.Single(t => t.Type == "video").Items
            .Should().OnlyContain(item => item.AgentRationale == null);
    }

    // ------------------------------------------------------------------
    // Fixture helpers
    // ------------------------------------------------------------------

    private static VideoCompileStepExecutor.ResolvedSpan Span(
        double startSec, double endSec, IReadOnlyList<string>? originKeepFromIds) =>
        new(
            RequestedStart: startSec,
            RequestedEnd: endSec,
            SnappedStart: startSec,
            SnappedEnd: endSec,
            StartFrame: 0,
            EndFrame: 0,
            SourceIndex: 0,
            OriginKeepFromIds: originKeepFromIds);

    private static TimelineManifest BuildManifest(
        IReadOnlyList<VideoCompileStepExecutor.ResolvedSpan> spans,
        VideoEditDecisionOutput? decision)
    {
        var artifact = new VideoAnalysisArtifact(
            Version: 1,
            Media: new VideoAnalysisMedia(60.0, 30, 1, 1920, 1080),
            Shots: Array.Empty<VideoAnalysisShot>(),
            SilenceSpans: Array.Empty<VideoAnalysisSilenceSpan>(),
            Segments: Array.Empty<VideoAnalysisSegment>(),
            Words: Array.Empty<VideoAnalysisWord>(),
            OfferedIds: Array.Empty<string>(),
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false));

        var seams = VideoCompileStepExecutor.BuildHardCutSeamsForFallback(spans.Count);
        OutputTimeline timeline = OutputTimeline.Build(spans, seams);

        return TimelineManifestBuilder.Build(
            projectId: Guid.NewGuid(),
            executionId: Guid.NewGuid(),
            artifact: artifact,
            resolvedSpans: spans,
            timeline: timeline,
            outputStorageKey: "projects/p/output.mp4",
            width: 1920,
            height: 1080,
            fps: 30.0,
            decision: decision);
    }
}
