using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Briefs;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Centrepiece test suite for <see cref="VideoCompileStepExecutor"/> (plan §4.3, WS5 DoD). Covers
/// the id-resolution/normalization pipeline that is the primary correctness surface: unknown-id
/// rejection against the OFFERED set (not merely the full artifact), out-of-order/overlap
/// rejection, exact-rational frame quantization, padding/clamping at the media boundaries,
/// MinSegmentMs dropping, MaxSegments capping, empty-Keep and MinRetainedRatio guardrails, and the
/// codec/preset allowlist (R11).
/// </summary>
[Collection(VideoFilterProbeCacheCollection.Name)]
public partial class VideoCompileStepExecutorTests
{
    private const string ProjectFileStorageRoot = "video-compile-tests";
    private static readonly Guid ProjectId = Guid.NewGuid();

    // Fixed WorkflowExecution.Id used by CreateGraphicsContext (Phase 3 graphics tests only) so a
    // test can construct a RenderedAssetStorageKey that matches the exact execution-scoped
    // "projects/{ProjectId}/outputFiles/{GraphicsExecutionId}/..." prefix
    // VideoCompileStepExecutor validates a rendered-asset overlay against.
    private static readonly Guid GraphicsExecutionId = Guid.NewGuid();
    private const string AnalysisKey = "projects/p/agentFiles/video-analysis/e/step-1-analysis.json";
    private const string SourceVideoKey = "projects/p/outputFiles/e/render.mp4";

    // ---------------------------------------------------------------------
    // Frame-exact rational arithmetic (R9) — direct, no I/O needed.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(1.0, 30, 1, 30)]
    [InlineData(0.0, 30, 1, 0)]
    [InlineData(2.5, 30, 1, 75)]
    public void ToStartFrame_at_30_1_is_exact(double startSec, int fpsNum, int fpsDen, long expectedFrame)
    {
        VideoCompileStepExecutor.ToStartFrame(startSec, fpsNum, fpsDen).Should().Be(expectedFrame);
    }

    [Fact]
    public void Frame_quantization_at_30000_1001_is_exact_not_approximate()
    {
        // 30000/1001 ≈ 29.97003 fps. 1.0s * 30000/1001 = 29.9700... -> floor=29, ceil=30.
        VideoCompileStepExecutor.ToStartFrame(1.0, 30000, 1001).Should().Be(29);
        VideoCompileStepExecutor.ToEndFrame(1.0, 30000, 1001).Should().Be(30);

        // Round-trip: frame -> seconds uses frame * fpsDen / fpsNum exactly.
        double backToSecStart = VideoCompileStepExecutor.FrameToSec(29, 30000, 1001);
        double backToSecEnd = VideoCompileStepExecutor.FrameToSec(30, 30000, 1001);
        backToSecStart.Should().BeApproximately(29.0 * 1001 / 30000, 1e-9);
        backToSecEnd.Should().BeApproximately(30.0 * 1001 / 30000, 1e-9);
    }

    [Fact]
    public void Frame_quantization_at_30_1_matches_naive_multiplication()
    {
        VideoCompileStepExecutor.ToStartFrame(10.0, 30, 1).Should().Be(300);
        VideoCompileStepExecutor.ToEndFrame(10.0, 30, 1).Should().Be(300);
        VideoCompileStepExecutor.FrameToSec(300, 30, 1).Should().Be(10.0);
    }

    // ---------------------------------------------------------------------
    // Root-cause fix for the mid-sentence-cut defect: a transcript segment's raw ASR EndSec is
    // extended forward, bounded, toward the nearest FOLLOWING detected silence gap before being
    // trusted as a Keep span's resolved end time — see ExtendSegmentEndTowardNextSilence's doc
    // comment on VideoCompileStepExecutor.
    // ---------------------------------------------------------------------

    [Fact]
    public void ExtendSegmentEndTowardNextSilence_snaps_to_a_nearby_following_silence_gap()
    {
        var silences = new[] { new VideoAnalysisSilenceSpan("g0", 10.4, 12.0, null) };

        VideoCompileStepExecutor.ExtendSegmentEndTowardNextSilence(10.1, silences).Should().Be(10.4);
    }

    [Fact]
    public void ExtendSegmentEndTowardNextSilence_leaves_the_raw_end_unchanged_when_no_gap_is_close_enough()
    {
        var silences = new[] { new VideoAnalysisSilenceSpan("g0", 30.0, 31.0, null) };

        VideoCompileStepExecutor.ExtendSegmentEndTowardNextSilence(10.1, silences).Should().Be(10.1);
    }

    [Fact]
    public void ExtendSegmentEndTowardNextSilence_never_extends_backward_to_a_gap_that_already_passed()
    {
        var silences = new[] { new VideoAnalysisSilenceSpan("g0", 5.0, 6.0, null) };

        VideoCompileStepExecutor.ExtendSegmentEndTowardNextSilence(10.1, silences).Should().Be(10.1);
    }

    [Fact]
    public void ExtendSegmentEndTowardNextSilence_picks_the_nearest_of_several_following_gaps()
    {
        var silences = new[]
        {
            new VideoAnalysisSilenceSpan("g0", 10.9, 11.5, null),
            new VideoAnalysisSilenceSpan("g1", 10.3, 10.6, null)
        };

        VideoCompileStepExecutor.ExtendSegmentEndTowardNextSilence(10.1, silences).Should().Be(10.3);
    }

    [Fact]
    public void ExtendSegmentEndTowardNextSilence_respects_the_max_extension_cap_exactly_at_the_boundary()
    {
        // Exactly at MaxSegmentEndExtensionSec (1.0s) -> still extends (<=, not <).
        var atCap = new[] { new VideoAnalysisSilenceSpan("g0", 11.1, 12.0, null) };
        VideoCompileStepExecutor.ExtendSegmentEndTowardNextSilence(10.1, atCap).Should().Be(11.1);

        // Just past the cap -> left unchanged.
        var pastCap = new[] { new VideoAnalysisSilenceSpan("g0", 11.100001, 12.0, null) };
        VideoCompileStepExecutor.ExtendSegmentEndTowardNextSilence(10.1, pastCap).Should().Be(10.1);
    }

    [Theory]
    [InlineData("Hello world.", true)]
    [InlineData("Hello world!", true)]
    [InlineData("Hello world?", true)]
    [InlineData("Hello world", false)]
    [InlineData("He said \"stop.\"", true)]
    [InlineData("Trailing space at the end. ", true)]
    [InlineData("   ", false)]
    public void EndsWithSentenceTerminalPunctuation_matches_expected(string text, bool expected)
    {
        VideoCompileStepExecutor.EndsWithSentenceTerminalPunctuation(text).Should().Be(expected);
    }

    [Fact]
    public async Task Single_quoted_python_dict_style_decision_is_repaired_and_parsed()
    {
        // Regression test: found live producing a real promo video. The local model can return
        // Python-dict-style single-quoted JSON for a story-editor decision — the exact near-miss
        // JSON failure mode VisionShotCaptioner already guards against for vision captions —
        // which RobustJsonExtractor.ExtractJsonObject's own prose/fence stripping does NOT repair
        // (it only strips what surrounds a balanced object, never single quotes inside it).
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        const string singleQuotedDecisionJson =
            "{'keep': [{'fromId': 's0', 'toId': 's0', 'reason': 'keep'}], 'editRationale': 'test', 'suggestedTitle': 'Test'}";
        StepExecutionContext context = CreateContext(artifact, singleQuotedDecisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
    }

    [Fact]
    public async Task Encode_is_metered_from_the_probe_of_the_output_file_not_from_wall_clock_or_the_source()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });
        StepExecutionContext context = CreateContext(artifact, BuildDecisionJson(("s0", "s0", "keep")), out Mock<IProjectFileWorkspace> workspace);

        var capture = new CapturingSink();
        var recorder = new ReelBolt.Shared.Metering.UsageRecorder(
            new NonChatPriceService(), capture,
            NullLogger<ReelBolt.Shared.Metering.UsageRecorder>.Instance);
        var orgId = Guid.NewGuid();

        // The source probes as 10s of 1080p (the catch-all); the file the encode wrote probes as 7.25s of 720p.
        // Neither equals the (near-zero) wall time the mocked ffmpeg took.
        VideoCompileStepExecutor executor = CreateExecutor(
            workspace,
            configureMediaProbe: probe => probe
                .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(s => Path.GetFileName(s) == "edited.mp4"), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaProbeResult(7.25, 30, 1, 1280, 720, "h264", "aac", 48000)),
            usageRecorder: recorder);

        StepExecutionResult result;
        using (ReelBolt.Shared.Metering.UsageScope.Begin(orgId, null, null, context.StepResultId, null, UsageSource.Workflow))
            result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);

        UsageEvent row = capture.Events.Should().ContainSingle().Subject;
        row.Kind.Should().Be(UsageKind.EncodeOutputSeconds);
        row.Quantity.Should().Be(7.25m);
        row.Variant.Should().Be("720p");
        row.PriceId.Should().NotBeNull();
        row.OrganizationId.Should().Be(orgId);
    }

    [Fact]
    public async Task A_failed_encode_records_no_encode_usage()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });
        StepExecutionContext context = CreateContext(artifact, BuildDecisionJson(("s0", "s0", "keep")), out Mock<IProjectFileWorkspace> workspace);

        var capture = new CapturingSink();
        var recorder = new ReelBolt.Shared.Metering.UsageRecorder(
            new NonChatPriceService(), capture,
            NullLogger<ReelBolt.Shared.Metering.UsageRecorder>.Instance);

        StepExecutionResult result = await CreateExecutor(workspace, failEncode: true, usageRecorder: recorder).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);
        capture.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task SentenceCheck_reports_applicable_false_when_last_kept_id_is_not_a_transcript_segment()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        doc.RootElement.GetProperty("sentenceCheck").GetProperty("applicable").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task SentenceCheck_reports_true_when_last_kept_segment_ends_with_terminal_punctuation()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            segments: new[] { ("t0", 1.0, 4.0) },
            segmentTexts: new[] { "This is a complete sentence." },
            offeredIds: new[] { "s0", "t0" });

        string decisionJson = BuildDecisionJson(("s0", "t0", "keep"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement sentenceCheck = doc.RootElement.GetProperty("sentenceCheck");
        sentenceCheck.GetProperty("applicable").GetBoolean().Should().BeTrue();
        sentenceCheck.GetProperty("lastKeptId").GetString().Should().Be("t0");
        sentenceCheck.GetProperty("endsAtSentenceBoundary").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task SentenceCheck_flags_a_mid_sentence_cut_and_notices_the_next_segment_continues()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            segments: new[] { ("t0", 1.0, 4.0), ("t1", 4.2, 6.0) },
            segmentTexts: new[] { "This sentence keeps going", "and finishes here." },
            // t1 deliberately NOT offered — the story editor never saw it, so this is purely
            // deterministic evidence for the review agent, never a signal the compile step would
            // trust to extend the actual cut.
            offeredIds: new[] { "s0", "t0" });

        string decisionJson = BuildDecisionJson(("s0", "t0", "keep"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement sentenceCheck = doc.RootElement.GetProperty("sentenceCheck");
        sentenceCheck.GetProperty("applicable").GetBoolean().Should().BeTrue();
        sentenceCheck.GetProperty("endsAtSentenceBoundary").GetBoolean().Should().BeFalse();
        sentenceCheck.GetProperty("lastSegmentText").GetString().Should().Be("This sentence keeps going");
        sentenceCheck.GetProperty("nextSegmentContinues").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("But one of the big things is that you know, I don't really have a fear", true)]
    [InlineData("So anyway, that's the story.", true)]
    [InlineData("However, the cave is deeper than it looks.", true)]
    [InlineData("Well, I wouldn't say that.", true)]
    [InlineData("\"But that's the whole point,\" he said.", true)]
    [InlineData("and so beautiful at the same time.", false)]      // lowercase -> mid-clause, not a new thought
    [InlineData("but I kept going anyway.", false)]
    [InlineData("Butter is not a discourse marker.", false)]       // prefix match must not fire
    [InlineData("The cave goes on for miles.", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void StartsWithNewThoughtMarker_matches_expected(string text, bool expected)
    {
        VideoCompileStepExecutor.StartsWithNewThoughtMarker(text).Should().Be(expected);
    }

    [Fact]
    public async Task SentenceCheck_does_not_claim_continuation_when_the_next_segment_opens_a_new_thought()
    {
        // The real-run regression: the last kept segment is a complete thought that ASR simply left
        // without terminal punctuation, and the next segment starts 0.44s later on a NEW topic
        // introduced by "But". A pure timing-gap test called that a continuation and contributed to
        // a false "ends mid-sentence" verdict from VideoReviewAgent.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            segments: new[] { ("t0", 1.0, 4.0), ("t1", 4.44, 6.0) },
            segmentTexts: new[]
            {
                "It's an underground world that is so strange and so beautiful at the same time",
                "But one of the big things is that you know, I don't really have a fear of a lot of different things"
            },
            offeredIds: new[] { "s0", "t0" });

        string decisionJson = BuildDecisionJson(("s0", "t0", "keep"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement sentenceCheck = doc.RootElement.GetProperty("sentenceCheck");
        sentenceCheck.GetProperty("applicable").GetBoolean().Should().BeTrue();
        sentenceCheck.GetProperty("endsAtSentenceBoundary").GetBoolean().Should().BeFalse();
        sentenceCheck.GetProperty("nextSegmentContinues").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task SentenceCheck_marks_the_punctuation_signal_unreliable_on_a_barely_punctuated_source()
    {
        // 1 of 5 segments punctuated (ratio 0.2) — the review agent must NOT hard-cap the score
        // at 4 for "ends mid-sentence" here, because the transcript itself carries no boundary
        // information to judge that on.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            segments: new[] { ("t0", 1.0, 2.0), ("t1", 2.0, 3.0), ("t2", 3.0, 4.0), ("t3", 4.0, 5.0), ("t4", 5.0, 6.0) },
            segmentTexts: new[]
            {
                "so the thing is that we", "and then we tried", "it worked out.", "which meant", "a lot for the team"
            },
            offeredIds: new[] { "s0", "t0", "t1", "t2", "t3" });

        string decisionJson = BuildDecisionJson(("s0", "t3", "keep"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement sentenceCheck = doc.RootElement.GetProperty("sentenceCheck");

        sentenceCheck.GetProperty("applicable").GetBoolean().Should().BeTrue();
        sentenceCheck.GetProperty("endsAtSentenceBoundary").GetBoolean().Should().BeFalse();
        sentenceCheck.GetProperty("src").GetInt32().Should().Be(0);
        sentenceCheck.GetProperty("punctuationSampleSize").GetInt32().Should().Be(5);
        sentenceCheck.GetProperty("punctuationRatio").GetDouble().Should().BeApproximately(0.2, 1e-9);
        sentenceCheck.GetProperty("punctuationReliable").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task SentenceCheck_still_reports_continuation_for_a_genuine_split_sentence()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            segments: new[] { ("t0", 1.0, 4.0), ("t1", 4.2, 6.0) },
            segmentTexts: new[]
            {
                "It's an underground world that is so strange",
                "in ways I could never have imagined before going down there."
            },
            offeredIds: new[] { "s0", "t0" });

        string decisionJson = BuildDecisionJson(("s0", "t0", "keep"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement sentenceCheck = doc.RootElement.GetProperty("sentenceCheck");
        sentenceCheck.GetProperty("endsAtSentenceBoundary").GetBoolean().Should().BeFalse();
        sentenceCheck.GetProperty("nextSegmentContinues").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task SentenceCheck_marks_the_punctuation_signal_reliable_on_a_well_punctuated_source()
    {
        // 3 of 4 punctuated (0.75): the mid-sentence finding IS trustworthy here, so the review
        // agent's hard cap stays in force.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            segments: new[] { ("t0", 1.0, 2.0), ("t1", 2.0, 3.0), ("t2", 3.0, 4.0), ("t3", 4.0, 5.0) },
            segmentTexts: new[]
            {
                "We shipped it last week.", "The team was thrilled!", "and then", "everything changed?"
            },
            offeredIds: new[] { "s0", "t0", "t1", "t2" });

        string decisionJson = BuildDecisionJson(("s0", "t2", "keep"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement sentenceCheck = doc.RootElement.GetProperty("sentenceCheck");

        sentenceCheck.GetProperty("endsAtSentenceBoundary").GetBoolean().Should()
            .BeFalse("\"and then\" is a genuine mid-sentence ending");
        sentenceCheck.GetProperty("punctuationRatio").GetDouble().Should().BeApproximately(0.75, 1e-9);
        sentenceCheck.GetProperty("punctuationReliable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task SentenceCheck_does_not_claim_continuation_across_a_long_timing_gap()
    {
        // Marker-independent: the next segment reads like a clean continuation, but it starts a
        // full 5s later — the existing timing test alone still rules continuation out.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 20.0) },
            segments: new[] { ("t0", 1.0, 4.0), ("t1", 9.0, 12.0) },
            segmentTexts: new[]
            {
                "It's an underground world that is so strange",
                "in ways I could never have imagined before going down there."
            },
            offeredIds: new[] { "s0", "t0" });

        string decisionJson = BuildDecisionJson(("s0", "t0", "keep"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement sentenceCheck = doc.RootElement.GetProperty("sentenceCheck");
        sentenceCheck.GetProperty("endsAtSentenceBoundary").GetBoolean().Should().BeFalse();
        sentenceCheck.GetProperty("nextSegmentContinues").GetBoolean().Should().BeFalse();
    }

    // ---------------------------------------------------------------------
    // Unknown id rejection
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Keep_span_referencing_id_present_in_artifact_but_not_offered_is_rejected()
    {
        // "w0" exists in the full artifact's Words list (for audit) but is never an OfferedId —
        // the compile step must reject it even though ExtractJsonValue-style lookups could find it
        // "in the artifact" if we looked at the wrong collection.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0) },
            words: new[] { ("w0", 1.0, 1.5) },
            offeredIds: new[] { "s0", "s1" }); // w0 deliberately excluded

        string decisionJson = BuildDecisionJson(("s0", "w0", "keep"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("UNKNOWN_ID");
    }

    [Fact]
    public async Task Keep_span_referencing_a_completely_unknown_id_is_rejected()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s99", "keep"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("UNKNOWN_ID");
    }

    // ---------------------------------------------------------------------
    // Ordering / overlap
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Out_of_order_keep_spans_are_sorted_and_reported()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0), ("s2", 20.0, 30.0) },
            offeredIds: new[] { "s0", "s1", "s2" });

        string decisionJson = BuildDecisionJson(("s2", "s2", "later"), ("s0", "s0", "earlier"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { MinSegmentMs = 0 });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        JsonElement segments = edl.GetProperty("segments");
        segments.GetArrayLength().Should().Be(2);
        segments[0].GetProperty("snappedStartSec").GetDouble().Should().BeLessThan(segments[1].GetProperty("snappedStartSec").GetDouble());
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement norm = doc.RootElement.GetProperty("spanNormalization");
        norm.GetProperty("reordered").GetInt32().Should().BeGreaterThan(0);
        norm.GetProperty("merged").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Overlapping_keep_spans_are_merged_and_reported()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0), ("s2", 15.0, 25.0) },
            offeredIds: new[] { "s0", "s1", "s2" });

        // [0,20) via s0..s1, then [15,25) via s2..s2 — overlaps the first span.
        string decisionJson = BuildDecisionJson(("s0", "s1", "first"), ("s2", "s2", "second"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { MinSegmentMs = 0 });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        edl.GetProperty("segments").GetArrayLength().Should().Be(1);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        doc.RootElement.GetProperty("spanNormalization").GetProperty("merged").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Ordered_non_overlapping_keep_spans_emit_no_span_normalization()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 20.0, 30.0) },
            offeredIds: new[] { "s0", "s1" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "a"), ("s1", "s1", "b"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { MinSegmentMs = 0 });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        doc.RootElement.TryGetProperty("spanNormalization", out _).Should().BeFalse();
    }

    // ---------------------------------------------------------------------
    // Empty Keep / MinRetainedRatio
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Empty_keep_list_fails()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = """{"keep":[],"editRationale":"nothing","suggestedTitle":"x"}""";
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("EMPTY_KEEP");
    }

    [Fact]
    public async Task Explicit_null_keep_fails_with_EMPTY_KEEP_not_an_uncaught_exception()
    {
        // An explicit `"keep": null` (as opposed to an omitted/absent field) overwrites the
        // `= new()` property-initializer default with a real null under System.Text.Json — this
        // must degrade to EMPTY_KEEP exactly like `"keep": []` does, never escape as
        // UNEXPECTED_ERROR via an uncaught NullReferenceException.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = """{"keep":null,"editRationale":"nothing","suggestedTitle":"x"}""";
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("EMPTY_KEEP");
    }

    [Fact]
    public async Task MinRetainedRatio_violation_fails()
    {
        // Duration 100s; keep only [0,5) => 5% retained, well under a 0.9 minimum.
        VideoAnalysisArtifact artifact = BuildArtifact(
            durationSec: 100.0,
            shots: new[] { ("s0", 0.0, 5.0), ("s1", 5.0, 100.0) },
            offeredIds: new[] { "s0", "s1" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { Expect = new VideoCompileExpectation(MinRetainedRatio: 0.9) });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("EXPECT_FAILED");
    }

    // ---------------------------------------------------------------------
    // Padding clamped at t=0 / t=duration
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Padding_is_clamped_at_media_boundaries_and_does_not_go_negative_or_past_duration()
    {
        double duration = 50.0;
        VideoAnalysisArtifact artifact = BuildArtifact(
            durationSec: duration,
            shots: new[] { ("s0", 0.0, 2.0), ("s1", 48.0, 50.0) },
            offeredIds: new[] { "s0", "s1" });

        // Large padding (500ms) around spans that already touch t=0 and t=duration.
        string decisionJson = BuildDecisionJson(("s0", "s0", "start"), ("s1", "s1", "end"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { PrePaddingMs = 500, PostPaddingMs = 500, MinSegmentMs = 0 });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        JsonElement segments = edl.GetProperty("segments");
        segments.GetArrayLength().Should().Be(2);
        segments[0].GetProperty("snappedStartSec").GetDouble().Should().BeGreaterThanOrEqualTo(0.0);
        segments[segments.GetArrayLength() - 1].GetProperty("snappedEndSec").GetDouble().Should().BeLessThanOrEqualTo(duration + 1e-6);
    }

    // ---------------------------------------------------------------------
    // MinSegmentMs drop
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Spans_shorter_than_MinSegmentMs_are_dropped()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            durationSec: 100.0,
            shots: new[] { ("s0", 0.0, 0.1), ("s1", 20.0, 40.0) }, // s0 is only 100ms long
            offeredIds: new[] { "s0", "s1" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "tiny"), ("s1", "s1", "real"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { MinSegmentMs = 250, PrePaddingMs = 0, PostPaddingMs = 0 });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        edl.GetProperty("segments").GetArrayLength().Should().Be(1, "the 100ms span must be dropped as shorter than MinSegmentMs=250");
    }

    // ---------------------------------------------------------------------
    // MaxSegments cap
    // ---------------------------------------------------------------------

    [Fact]
    public async Task MaxSegments_caps_the_number_of_compiled_segments()
    {
        var shots = Enumerable.Range(0, 10)
            .Select(i => ($"s{i}", (double)i * 10, (double)(i * 10 + 5))) // 10 separate 5s shots with 5s gaps
            .ToArray();

        VideoAnalysisArtifact artifact = BuildArtifact(
            durationSec: 100.0,
            shots: shots,
            offeredIds: shots.Select(s => s.Item1).ToArray());

        (string, string, string)[] spans = shots.Select(s => (s.Item1, s.Item1, "keep")).ToArray();
        string decisionJson = BuildDecisionJson(spans);

        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { MaxSegments = 3, PrePaddingMs = 0, PostPaddingMs = 0, MinSegmentMs = 0 });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        edl.GetProperty("segments").GetArrayLength().Should().Be(3);
        edl.GetProperty("droppedSegmentsOverCap").GetInt32().Should().Be(7);
    }

    // ---------------------------------------------------------------------
    // Half-open select/aselect filter expression (R9 cut-accuracy) — ffmpeg's between(x,min,max)
    // is inclusive on both ends, so using it for the cut selects one extra frame (the frame whose
    // PTS is exactly SnappedEnd) per kept span, compounding drift across every span in the cut.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Encoder_select_filter_uses_half_open_gte_lt_not_inclusive_between()
    {
        // 10 short, well-separated kept spans — enough that a systematic +1-frame-per-span error
        // would compound noticeably across the whole select expression, not just wobble once.
        var shots = Enumerable.Range(0, 10)
            .Select(i => ($"s{i}", 3.0 * i, 3.0 * i + 1.0)) // 1s shots, 2s gaps
            .ToArray();

        VideoAnalysisArtifact artifact = BuildArtifact(
            durationSec: 3.0 * shots.Length + 2.0,
            shots: shots,
            offeredIds: shots.Select(s => s.Item1).ToArray());

        (string, string, string)[] spans = shots.Select(s => (s.Item1, s.Item1, "keep")).ToArray();
        string decisionJson = BuildDecisionJson(spans);

        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        IReadOnlyList<string>? capturedArgs = null;
        StepExecutionResult result = await CreateExecutor(
            workspace, ffmpegArgsCaptured: args => capturedArgs ??= args).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        capturedArgs.Should().NotBeNull();

        List<string> argsList = capturedArgs!.ToList();
        int filterIndex = argsList.IndexOf("-filter_complex");
        filterIndex.Should().BeGreaterThanOrEqualTo(0, "10 spans stays well under the filter-complex-script threshold");
        string filterComplex = argsList[filterIndex + 1];

        filterComplex.Should().NotContain("between(t,",
            "ffmpeg's between() is inclusive on both ends, which selects one extra frame (at exactly SnappedEnd) per span");

        int gteCount = System.Text.RegularExpressions.Regex.Matches(filterComplex, @"gte\(t,").Count;
        int ltCount = System.Text.RegularExpressions.Regex.Matches(filterComplex, @"lt\(t,").Count;
        // Each of the 10 spans appears once in the video select and once in the audio aselect.
        gteCount.Should().Be(shots.Length * 2);
        ltCount.Should().Be(shots.Length * 2);
    }

    [Fact]
    public async Task Source_with_no_audio_stream_produces_a_video_only_filtergraph_and_skips_audio_map()
    {
        // Real B-roll/stock footage routinely ships with no audio stream at all. Before this fix,
        // the compile unconditionally built "[0:a]aselect=...[aout]" and unconditionally
        // "-map [aout]"/"-c:a", which made ffmpeg fail outright with "Stream specifier ':a' ...
        // matches no streams" on exactly this kind of source.
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        IReadOnlyList<string>? capturedArgs = null;
        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(
            workspace, ffmpegArgsCaptured: args => capturedArgs ??= args, edlCaptured: e => edl = e,
            configureMediaProbe: mock => mock
                .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaProbeResult(10, 30, 1, 1920, 1080, "h264", null, null)))
            .ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        capturedArgs.Should().NotBeNull();

        string filterComplex = ExtractFilterComplexValue(capturedArgs!);
        filterComplex.Should().NotContain("[0:a]", "the source has no audio stream, so \"[0:a]\" would fail ffmpeg outright");
        filterComplex.Should().NotContain("aselect");
        filterComplex.Should().NotContain("[aout]", "there is no audio output at all, not even an empty one");

        List<string> argsList = capturedArgs!.ToList();
        argsList.Should().NotContain("[aout]");
        argsList.Should().NotContain("-c:a");

        // Bug group C.1: every OTHER degrade path in this feature (graphics.reason, music.dropped,
        // meta.transcription.degraded) records itself in the EDL — a dropped audio stream must too,
        // rather than leaving a silent-video deliverable an unreported silent surprise.
        JsonElement audio = edl.GetProperty("audio");
        audio.GetProperty("applied").GetBoolean().Should().BeFalse();
        audio.GetProperty("reason").GetString().Should().Be("no_audio_stream_in_source");

        using JsonDocument outputDoc = JsonDocument.Parse(result.Output);
        JsonElement outputAudio = outputDoc.RootElement.GetProperty("audio");
        outputAudio.GetProperty("applied").GetBoolean().Should().BeFalse();
        outputAudio.GetProperty("reason").GetString().Should().Be("no_audio_stream_in_source");
    }

    [Fact]
    public async Task Source_with_audio_records_audio_applied_true_with_no_reason_in_the_edl()
    {
        // The positive counterpart of the test above: a normal source with a real audio stream
        // must report audio.applied=true (and no degrade reason) in both the EDL and outputSummary.
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        JsonElement audio = edl.GetProperty("audio");
        audio.GetProperty("applied").GetBoolean().Should().BeTrue();

        using JsonDocument outputDoc = JsonDocument.Parse(result.Output);
        outputDoc.RootElement.GetProperty("audio").GetProperty("applied").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task No_overlay_compile_with_many_segments_stays_on_the_inline_filter_complex_path()
    {
        // Item F: EnableGraphics=false (default) is documented as byte-identical to the
        // pre-Phase-3 compile path. The filterComplex.Length > 4000 clause added for Phase 3
        // overlays must NOT apply when there are no overlays — otherwise a plain cut-only compile
        // with ~50 segments (well under the 64-segment/count-only threshold) already produces a
        // >4000-char filter string from the between(t,...)-equivalent terms alone and silently
        // switches to -filter_complex_script, breaking the byte-identical claim.
        const int segmentCount = 50;
        var shots = Enumerable.Range(0, segmentCount)
            .Select(i => ($"s{i}", 3.0 * i, 3.0 * i + 1.0)) // 1s shots, 2s gaps — well under MaxSegments=200
            .ToArray();

        // NTSC-style non-integer fps (30000/1001): frame-quantized SnappedStart/SnappedEnd then
        // have long, non-terminating decimal representations (the same reason the audit's own
        // estimate is "~45 chars" per between(t,...)-equivalent term, not a short round number) —
        // a clean fps like 30/1 would frame-quantize these shot boundaries back to short round
        // decimals and this test wouldn't actually exercise the >4000-char scenario Item F fixes.
        VideoAnalysisArtifact artifact = BuildArtifact(
            durationSec: 3.0 * shots.Length + 2.0,
            shots: shots,
            offeredIds: shots.Select(s => s.Item1).ToArray(),
            fpsNum: 30000, fpsDen: 1001);

        (string, string, string)[] spans = shots.Select(s => (s.Item1, s.Item1, "keep")).ToArray();
        string decisionJson = BuildDecisionJson(spans);

        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { PrePaddingMs = 0, PostPaddingMs = 0, MinSegmentMs = 0 });

        IReadOnlyList<string>? capturedArgs = null;
        StepExecutionResult result = await CreateExecutor(
            workspace, ffmpegArgsCaptured: args => capturedArgs ??= args).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        capturedArgs.Should().NotBeNull();

        List<string> argsList = capturedArgs!.ToList();
        int filterIndex = argsList.IndexOf("-filter_complex");
        filterIndex.Should().BeGreaterThanOrEqualTo(0,
            "a no-overlay compile must stay on the count-only threshold (50 segments < 64), " +
            "even though its filter string exceeds 4000 characters");

        string filterComplex = argsList[filterIndex + 1];
        filterComplex.Length.Should().BeGreaterThan(4000,
            "the test is only meaningful if this compile would actually have tripped the length clause");

        argsList.Should().NotContain("-filter_complex_script");
    }

    // ---------------------------------------------------------------------
    // Codec / preset allowlist (R11)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Disallowed_video_codec_is_rejected_even_though_it_came_from_config_not_the_model()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { VideoCodec = "libx264; rm -rf /" });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("CODEC_NOT_ALLOWED");
    }

    [Fact]
    public async Task Disallowed_preset_is_rejected()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { Preset = "not-a-real-preset" });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("CODEC_NOT_ALLOWED");
    }

    // ---------------------------------------------------------------------
    // Happy path sanity
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Successful_compile_sets_ArtifactStorageKey_and_OutputStorageKey_and_never_throws()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0) },
            offeredIds: new[] { "s0", "s1" });

        string decisionJson = BuildDecisionJson(("s0", "s1", "keep the whole thing"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        result.ArtifactStorageKey.Should().NotBeNullOrWhiteSpace();
        result.OutputStorageKey.Should().NotBeNullOrWhiteSpace();

        Action parse = () => JsonDocument.Parse(result.Output);
        parse.Should().NotThrow();
    }

    /// <summary>
    /// Regression test for a live-testing-only-discoverable bug: the source video must resolve
    /// via the referenced VideoAnalyze step's own <c>Source</c> (here <c>VideoSourceKind.ProjectFile</c>
    /// — an uploaded video with NO prior step output at all), never via a
    /// "find any prior StepOutputHistory.OutputStorageKey" heuristic. That heuristic can never
    /// succeed for ProjectFile sources since they are never represented as a step output — before
    /// the fix, every VideoCompile step in a standalone (no preceding render step) workflow
    /// against an uploaded video failed with SOURCE_UNRESOLVED.
    /// </summary>
    [Fact]
    public async Task Successful_compile_resolves_ProjectFile_source_with_no_prior_step_output_at_all()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0) },
            offeredIds: new[] { "s0", "s1" });
        string decisionJson = BuildDecisionJson(("s0", "s1", "keep the whole thing"));

        Guid projectFileId = Guid.NewGuid();
        const string projectFileStorageKey = "projects/p/userFiles/uploaded.mp4";

        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), AnalysisKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllText(destPath, JsonSerializer.Serialize(artifact, ArtifactOptions()));
                return Task.CompletedTask;
            });
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), projectFileStorageKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });
        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectWorkspaceFile>
            {
                new(projectFileId, ProjectId, "uploaded.mp4", null, "userFiles",
                    projectFileStorageKey, "video/mp4", 12345, DateTime.UtcNow, null)
            });

        VideoCompileStepConfig config = new(
            Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Previous),
            AnalysisStepOrder: 1);

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 3,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = JsonSerializer.Serialize(config, ConfigOptions())
        };

        VideoAnalyzeStepConfig analyzeConfig = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: projectFileId));
        var analyzeStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(analyzeConfig, ConfigOptions())
        };

        // Deliberately NO entry at all produces an OutputStorageKey — the old heuristic
        // ("search StepOutputHistory for any prior OutputStorageKey") would find nothing here.
        List<StepOutputHistoryEntry> history =
        [
            new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
            new StepOutputHistoryEntry(2, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null)
        ];

        StepExecutionContext context = new()
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [analyzeStep, step],
            AccumulatedOutput = decisionJson,
            StepOutputHistory = history,
            CurrentStepIndex = 2,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        result.OutputStorageKey.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// T-014 (T-010 leftover): when the analyze step's <c>Source</c> is a <see cref="VideoSourceKind.StepResult"/>
    /// of an earlier run, the compile resolves it exactly as VideoAnalyze does — the same shared,
    /// project-scoped, <c>outputFiles/</c>-prefixed lookup.
    /// </summary>
    [Fact]
    public async Task Successful_compile_resolves_a_same_project_StepResult_analysis_source()
    {
        string key = $"projects/{ProjectId}/outputFiles/{Guid.NewGuid():D}/film.mp4";
        (StepExecutionContext context, Mock<IProjectFileWorkspace> workspace, IServiceScopeFactory scopes) =
            StepResultSourceCompile(ProjectId, key);

        StepExecutionResult result = await CreateExecutor(workspace, scopeFactory: scopes).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails ?? result.Output);
        workspace.Verify(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), key, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("sibling-project")]
    [InlineData("missing")]
    [InlineData("pruned")]
    [InlineData("outside-outputFiles")]
    public async Task A_StepResult_analysis_source_outside_the_project_is_SOURCE_NOT_FOUND_and_nothing_is_downloaded(string variant)
    {
        Guid sibling = Guid.NewGuid();
        (Guid owner, string? key) = variant switch
        {
            "sibling-project" => (sibling, $"projects/{sibling}/outputFiles/{Guid.NewGuid():D}/film.mp4"),
            "pruned" => (ProjectId, null),
            "outside-outputFiles" => (ProjectId, $"projects/{ProjectId}/agentFiles/video-analysis/x/film.mp4"),
            _ => (ProjectId, $"projects/{ProjectId}/outputFiles/{Guid.NewGuid():D}/film.mp4")
        };
        (StepExecutionContext context, Mock<IProjectFileWorkspace> workspace, IServiceScopeFactory scopes) =
            StepResultSourceCompile(owner, key, nameMissingId: variant == "missing");

        StepExecutionResult result = await CreateExecutor(workspace, scopeFactory: scopes).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        result.Output.Should().Contain("SOURCE_NOT_FOUND");
        result.Output.Should().Contain(VideoAnalyzeStepExecutor.StepResultSourceNotFoundMessage, "one message for foreign, missing and pruned");
        result.Output.Should().NotContain("projects/", "a refusal never echoes a storage key");
        workspace.Verify(w => w.DownloadStorageKeyToFileAsync(
            It.IsAny<Guid>(), It.Is<string>(k => k != AnalysisKey), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A compile whose analyze step (step 1) reads a <see cref="VideoSourceKind.StepResult"/> source, with an
    /// analysis artifact that predates per-source keys (so the compile re-derives the source from the analyze
    /// config), over an engine database holding one step result owned by <paramref name="ownerProjectId"/>.
    /// </summary>
    private static (StepExecutionContext, Mock<IProjectFileWorkspace>, IServiceScopeFactory) StepResultSourceCompile(
        Guid ownerProjectId, string? outputKey, bool nameMissingId = false)
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0) },
            offeredIds: new[] { "s0", "s1" });
        string decisionJson = BuildDecisionJson(("s0", "s1", "keep the whole thing"));

        WorkflowEngineDbContext db = NewProvenanceDbContext();
        WorkflowExecution earlier = new() { Id = Guid.NewGuid(), ProjectId = ownerProjectId };
        WorkflowStepResult stepResult = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = earlier.Id,
            WorkflowExecution = earlier,
            OutputStorageKey = outputKey,
            Status = StepStatus.Completed
        };
        db.WorkflowExecutions.Add(earlier);
        db.WorkflowStepResults.Add(stepResult);
        db.SaveChanges();

        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), AnalysisKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllText(destPath, JsonSerializer.Serialize(artifact, ArtifactOptions()));
                return Task.CompletedTask;
            });
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.Is<string>(k => k != AnalysisKey), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        VideoCompileStepConfig config = new(
            Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Previous),
            AnalysisStepOrder: 1);
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 3,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = JsonSerializer.Serialize(config, ConfigOptions())
        };
        VideoAnalyzeStepConfig analyzeConfig = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.StepResult, StepResultId: nameMissingId ? Guid.NewGuid() : stepResult.Id));
        var analyzeStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(analyzeConfig, ConfigOptions())
        };

        StepExecutionContext context = new()
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [analyzeStep, step],
            AccumulatedOutput = decisionJson,
            StepOutputHistory =
            [
                new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
                new StepOutputHistoryEntry(2, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null)
            ],
            CurrentStepIndex = 2,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
        return (context, workspace, ScopeFactoryFor(db, resolver: null));
    }

    /// <summary>
    /// Bug group A.2 regression: after a ReviewLoop loop-back re-executes the story editor step,
    /// StepOutputHistory can hold two entries for the SAME StepOrder — the stale iteration-1
    /// decision and the fresh iteration-2 one (belt-and-braces: WorkflowExecutorService now prunes
    /// this on loop-back too, but this test constructs the pre-prune shape directly to prove
    /// ResolveDecisionJson's own resolution is independently correct). An explicit
    /// <c>Decision.From=Step</c>/StepOrder reference must resolve to the LATEST matching entry
    /// (LastOrDefault), never the first one appended (FirstOrDefault) — otherwise every loop
    /// iteration after the first would silently re-compile the stale first-iteration decision.
    /// </summary>
    [Fact]
    public async Task Explicit_step_order_decision_reference_resolves_to_the_latest_entry_when_history_has_a_duplicate_StepOrder()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0) },
            offeredIds: new[] { "s0", "s1" });

        string staleDecisionJson = BuildDecisionJson(("s0", "s0", "stale iteration 1 decision"));
        string freshDecisionJson = BuildDecisionJson(("s0", "s1", "fresh iteration 2 decision"));

        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), AnalysisKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllText(destPath, JsonSerializer.Serialize(artifact, ArtifactOptions()));
                return Task.CompletedTask;
            });
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), SourceVideoKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        VideoCompileStepConfig config = new(
            Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
            AnalysisStepOrder: 1);

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 4,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = JsonSerializer.Serialize(config, ConfigOptions())
        };

        VideoAnalyzeStepConfig analyzeConfig = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput));
        var analyzeStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(analyzeConfig, ConfigOptions())
        };

        // Simulates the exact post-loop-back shape WorkflowExecutorService's StepOutputHistory
        // would hold WITHOUT its own A.3 prune fix: the stale iteration-1 StoryEditor output at
        // StepOrder 2, appended BEFORE the fresh iteration-2 one.
        List<StepOutputHistoryEntry> history =
        [
            new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: SourceVideoKey, ArtifactStorageKey: null),
            new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
            new StepOutputHistoryEntry(2, "StoryEditor", staleDecisionJson, OutputStorageKey: null, ArtifactStorageKey: null),
            new StepOutputHistoryEntry(2, "StoryEditor", freshDecisionJson, OutputStorageKey: null, ArtifactStorageKey: null)
        ];

        StepExecutionContext context = new()
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [analyzeStep, step],
            AccumulatedOutput = freshDecisionJson,
            StepOutputHistory = history,
            CurrentStepIndex = 3,
            IterationCount = 1,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        // Stale decision keeps only s0 (0-10s); fresh decision keeps s0 through s1 (0-20s). Picking
        // the stale entry would compile a 10s output instead of the fresh 20s one.
        edl.GetProperty("totalOutputSeconds").GetDouble().Should().BeApproximately(20.0, 0.01,
            "the FRESH (latest) decision must be resolved, not the stale first-appended one");
    }

    [Fact]
    public async Task Missing_config_json_fails_with_valid_json_never_throws()
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 3,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = null
        };

        var context = new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };

        StepExecutionResult result = await CreateExecutor(new Mock<IProjectFileWorkspace>()).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        Action parse = () => JsonDocument.Parse(result.Output);
        parse.Should().NotThrow();
        ErrorCode(result).Should().Be("CONFIG_INVALID");
    }

    // =======================================================================
    // Timeline provenance (issue #123 §1)
    // =======================================================================

    [Fact]
    public async Task Timeline_video_track_carries_the_upstream_agents_real_name_and_provider()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0) },
            offeredIds: new[] { "s0", "s1" });

        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        // The step that actually produced the decision (StepOrder 2 — the "StoryEditor" history
        // entry) has to exist in AllSteps for provenance to be resolvable at all; the shared
        // harness deliberately leaves it out (nothing before this ticket read AllSteps for it).
        var definition = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "VideoStoryEditor",
            AgentType = AgentType.VideoStoryEditor
        };
        var editorStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.Agent,
            AgentDefinitionId = definition.Id
        };
        context.AllSteps.Add(editorStep);

        var stepResultId = Guid.NewGuid();
        await using WorkflowEngineDbContext db = NewProvenanceDbContext();
        db.AgentDefinitions.Add(definition);
        db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = stepResultId,
            WorkflowExecutionId = context.Execution.Id,
            WorkflowStepId = editorStep.Id,
            Status = StepStatus.Completed,
            IterationNumber = 1,
            CompletedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var resolver = new Mock<IInferenceProviderResolver>();
        resolver
            .Setup(r => r.ResolveAsync(AgentType.VideoStoryEditor, definition.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedInferenceProvider(
                Guid.NewGuid(), "real-provider-name", InferenceProviderKind.OpenAICompatible,
                "http://localhost:8000", "model", "key", 30));

        VideoCompileStepExecutor executor = CreateExecutor(
            workspace, scopeFactory: ScopeFactoryFor(db, resolver.Object));
        Func<JsonElement> timeline = CaptureTimelineManifest(workspace);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);

        JsonElement videoItem = VideoTrackItem(timeline());
        videoItem.GetProperty("agentName").GetString().Should().Be("VideoStoryEditor");
        videoItem.GetProperty("providerName").GetString().Should().Be("real-provider-name");
        videoItem.GetProperty("stepResultId").GetString().Should().Be(stepResultId.ToString());
    }

    [Fact]
    public async Task Unresolvable_provenance_step_degrades_to_null_and_the_compile_still_completes()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));

        // Default config references the decision with From=Previous, which resolves to the
        // StepOrder-2 history entry — but the harness's AllSteps has no step at StepOrder 2, so
        // there is nothing to attribute the track to. The DECISION still resolves (it comes from
        // StepOutputHistory), so the compile must complete with null provenance rather than fail.
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        await using WorkflowEngineDbContext db = NewProvenanceDbContext();
        VideoCompileStepExecutor executor = CreateExecutor(
            workspace, scopeFactory: ScopeFactoryFor(db, resolver: null));
        Func<JsonElement> timeline = CaptureTimelineManifest(workspace);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        JsonElement videoItem = VideoTrackItem(timeline());
        videoItem.TryGetProperty("agentName", out _).Should().BeFalse();
        videoItem.TryGetProperty("providerName", out _).Should().BeFalse();
        videoItem.TryGetProperty("stepResultId", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_bare_mocked_scope_factory_degrades_provenance_to_null_and_still_completes()
    {
        // The hardening contract: every pre-existing fixture builds the executor with a bare
        // `new Mock<IServiceScopeFactory>()`, whose CreateScope() returns null. That must leave the
        // manifest's provenance null — never throw out of the executor.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        VideoCompileStepExecutor executor = CreateExecutor(workspace);
        Func<JsonElement> timeline = CaptureTimelineManifest(workspace);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        VideoTrackItem(timeline()).TryGetProperty("agentName", out _).Should().BeFalse();
    }

    /// <summary>
    /// The EDL is written and uploaded once, BEFORE the encode, and the ENCODE_FAILED return still
    /// carries its storage key — so a failed compile leaves a debuggable EDL on the failed step
    /// result rather than discarding it. Executing coverage for the claim "the first write+upload
    /// is untouched, including on the ENCODE_FAILED path, which still returns edlStorageKey"
    /// (see the single-EDL-upload commit).
    /// </summary>
    [Fact]
    public async Task Encode_failure_still_uploads_the_edl_once_and_returns_its_storage_key()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace, failEncode: true).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("ENCODE_FAILED");
        result.ArtifactStorageKey.Should().Be("projects/p/agentFiles/video-analysis/e/step-3-edl.json");

        // A failed encode must not have uploaded the EDL twice, and must not have produced a
        // timeline manifest at all.
        workspace.Verify(w => w.UploadArtifactAsync(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.Is<string>(fileName => fileName.EndsWith("-edl.json", StringComparison.OrdinalIgnoreCase)),
            "application/json",
            It.IsAny<CancellationToken>(),
            It.IsAny<string>()), Times.Once);
        workspace.Verify(w => w.UploadArtifactAsync(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.Is<string>(fileName => fileName.EndsWith("timeline.json", StringComparison.OrdinalIgnoreCase)),
            "application/json",
            It.IsAny<CancellationToken>(),
            It.IsAny<string>()), Times.Never);
    }

    // =======================================================================
    // Timeline provenance for ROOM-sourced decisions (issue #123 §1)
    //
    // A room step's own AgentDefinitionId points at the non-LLM AgentType.VideoTransform
    // PLACEHOLDER that merely satisfies the step's non-nullable FK, so stamping it would label
    // every room-authored cut "VideoTransform" — a fabricated author, exactly the defect class
    // this initiative removes. The real director comes from the room's own config surface:
    // the configured DirectorAgentDefinitionId when present, else the room kind's built-in
    // director AgentType. These two tests are the only executing coverage of that branch.
    // =======================================================================

    [Fact]
    public async Task Room_sourced_decision_provenance_names_the_builtin_director_never_the_placeholder()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        // The room step at StepOrder 2 is what the compile step's Decision=Previous reference
        // resolves to. Its AgentDefinitionId is the VideoTransform placeholder, whose row IS
        // seeded — so a buggy implementation would happily stamp "VideoTransform".
        var placeholder = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "VideoTransform",
            AgentType = AgentType.VideoTransform
        };
        var director = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "VideoEditDirector",
            AgentType = AgentType.VideoEditDirector
        };
        var roomStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.EditRoom,
            AgentDefinitionId = placeholder.Id,
            EditRoomConfigJson = null  // no override -> the room kind's built-in director
        };
        context.AllSteps.Add(roomStep);

        var roomStepResultId = Guid.NewGuid();
        await using WorkflowEngineDbContext db = NewProvenanceDbContext();
        db.AgentDefinitions.AddRange(placeholder, director);
        db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = roomStepResultId,
            WorkflowExecutionId = context.Execution.Id,
            WorkflowStepId = roomStep.Id,
            Status = StepStatus.Completed,
            IterationNumber = 1,
            CompletedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        VideoCompileStepExecutor executor = CreateExecutor(
            workspace, scopeFactory: ScopeFactoryFor(db, resolver: null));
        Func<JsonElement> timeline = CaptureTimelineManifest(workspace);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        JsonElement videoItem = VideoTrackItem(timeline());
        videoItem.GetProperty("agentName").GetString().Should().Be("VideoEditDirector");
        videoItem.GetProperty("agentName").GetString().Should().NotBe("VideoTransform");
        videoItem.GetProperty("stepResultId").GetString().Should().Be(roomStepResultId.ToString());
    }

    [Fact]
    public async Task Room_sourced_decision_provenance_uses_the_configured_director_override()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        var placeholder = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "VideoTransform",
            AgentType = AgentType.VideoTransform
        };
        // Inserted FIRST, so a built-in-type lookup would find this one: the override can only
        // win by actually honouring DirectorAgentDefinitionId.
        var builtInDirector = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "BuiltInDirector",
            AgentType = AgentType.VideoEditDirector
        };
        var overrideDirector = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "CustomDirector",
            AgentType = AgentType.VideoEditDirector
        };
        var roomStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.EditRoom,
            AgentDefinitionId = placeholder.Id,
            EditRoomConfigJson = JsonSerializer.Serialize(
                new EditRoomStepConfig(DirectorAgentDefinitionId: overrideDirector.Id),
                ConfigOptions())
        };
        context.AllSteps.Add(roomStep);

        var roomStepResultId = Guid.NewGuid();
        await using WorkflowEngineDbContext db = NewProvenanceDbContext();
        db.AgentDefinitions.AddRange(placeholder, builtInDirector, overrideDirector);
        db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = roomStepResultId,
            WorkflowExecutionId = context.Execution.Id,
            WorkflowStepId = roomStep.Id,
            Status = StepStatus.Completed,
            IterationNumber = 1,
            CompletedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        VideoCompileStepExecutor executor = CreateExecutor(
            workspace, scopeFactory: ScopeFactoryFor(db, resolver: null));
        Func<JsonElement> timeline = CaptureTimelineManifest(workspace);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        JsonElement videoItem = VideoTrackItem(timeline());
        videoItem.GetProperty("agentName").GetString().Should().Be("CustomDirector");
        videoItem.GetProperty("agentName").GetString().Should().NotBe("BuiltInDirector");
        videoItem.GetProperty("stepResultId").GetString().Should().Be(roomStepResultId.ToString());
    }

    [Theory]
    [InlineData(StepType.GraphicsRoom, "MotionGraphicsDirector")]
    [InlineData(StepType.ColorGradeRoom, "ColorGradeDirector")]
    public async Task Each_room_kind_falls_back_to_its_own_builtin_director(StepType roomStepType, string expectedDirector)
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        var placeholder = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "VideoTransform",
            AgentType = AgentType.VideoTransform
        };
        AgentType expectedType = roomStepType == StepType.GraphicsRoom
            ? AgentType.MotionGraphicsDirector
            : AgentType.ColorGradeDirector;
        var director = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = expectedDirector,
            AgentType = expectedType
        };
        var roomStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 2,
            StepType = roomStepType,
            AgentDefinitionId = placeholder.Id
            // Both room config blobs left null -> each room kind resolves its own built-in director.
        };
        context.AllSteps.Add(roomStep);

        await using WorkflowEngineDbContext db = NewProvenanceDbContext();
        db.AgentDefinitions.AddRange(placeholder, director);
        db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = context.Execution.Id,
            WorkflowStepId = roomStep.Id,
            Status = StepStatus.Completed,
            IterationNumber = 1,
            CompletedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        VideoCompileStepExecutor executor = CreateExecutor(
            workspace, scopeFactory: ScopeFactoryFor(db, resolver: null));
        Func<JsonElement> timeline = CaptureTimelineManifest(workspace);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        VideoTrackItem(timeline()).GetProperty("agentName").GetString().Should().Be(expectedDirector);
    }

    /// <summary>
    /// Captures the uploaded <c>timeline.json</c> manifest, replacing the harness's own catch-all
    /// upload setup. Returns a reader rather than the value, since the callback fires only once
    /// the compile has actually run.
    /// </summary>
    private static Func<JsonElement> CaptureTimelineManifest(Mock<IProjectFileWorkspace> workspace)
    {
        JsonElement captured = default;

        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Callback<Guid, string, string, string, CancellationToken, string>((_, path, _, _, _, _) =>
            {
                if (path.EndsWith("timeline.json", StringComparison.OrdinalIgnoreCase))
                {
                    captured = JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
                }
            })
            .ReturnsAsync("projects/p/agentFiles/video-analysis/e/step-3-artifact.json");
        workspace.SetupRegisteredRenderPublish();

        return () => captured;
    }

    private static JsonElement VideoTrackItem(JsonElement timeline)
    {
        timeline.ValueKind.Should().NotBe(JsonValueKind.Undefined, "the timeline manifest must have been captured");
        return timeline.GetProperty("tracks").EnumerateArray()
            .First(t => t.GetProperty("type").GetString() == "video")
            .GetProperty("items")[0];
    }

    private static WorkflowEngineDbContext NewProvenanceDbContext() =>
        new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static IServiceScopeFactory ScopeFactoryFor(
        WorkflowEngineDbContext db, IInferenceProviderResolver? resolver, IOrganizationEntitlements? entitlements = null)
    {
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(p => p.GetService(typeof(WorkflowEngineDbContext))).Returns(db);
        serviceProvider.Setup(p => p.GetService(typeof(IInferenceProviderResolver))).Returns(resolver);
        // B8b: the executor reads the run's plan from the same scope it already creates for its
        // provenance reads. Unstubbed (null, the default) is the "no entitlement service" case —
        // exactly what every pre-B8b fixture supplies.
        serviceProvider.Setup(p => p.GetService(typeof(IOrganizationEntitlements))).Returns(entitlements);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        var factory = new Mock<IServiceScopeFactory>();
        factory.Setup(f => f.CreateScope()).Returns(scope.Object);
        return factory.Object;
    }

    // =======================================================================
    // B8b — trial output policy (height cap + watermark)
    // =======================================================================

    /// <summary>The organization every B8b test compiles for — a real id, never the Guid.Empty sentinel.</summary>
    private static readonly Guid PolicyOrgId = Guid.Parse("b8b0b8b0-0000-4000-8000-00000000b8b0");

    /// <summary>A <see cref="IOrganizationEntitlements"/> serving one fixed set for every org.</summary>
    private static IOrganizationEntitlements Entitlements(OrganizationEntitlementSet set)
    {
        var entitlements = new Mock<IOrganizationEntitlements>();
        entitlements
            .Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<OrganizationEntitlementSet>(set));
        return entitlements.Object;
    }

    /// <summary>The Trial plan as the catalog ships it: a 720p cap and a required watermark.</summary>
    private static OrganizationEntitlementSet TrialPlan() => new()
    {
        Tier = "trial",
        MaxOutputHeight = OutputPolicyFilterBuilder.DefaultMaxHeight,
        Watermark = true
    };

    [Fact]
    public async Task Output_policy_caps_the_encode_at_the_plans_max_height()
    {
        // The plan caps output at 720p and the compile is 1080p, so the policy's scale stage is the
        // LAST picture stage of the encode and owns [vout]; the cut stage hands off to [vpol].
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace, organizationId: PolicyOrgId);
        context.Execution.OrganizationId.Should().Be(PolicyOrgId, "the fixture must carry the org the policy is read for");

        IReadOnlyList<string>? capturedArgs = null;
        StepExecutionResult result = await CreateExecutor(
            workspace,
            scopeFactory: ScopeFactoryFor(NewProvenanceDbContext(), resolver: null, entitlements: Entitlements(TrialPlan())),
            ffmpegArgsCaptured: args => capturedArgs ??= args).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        string filter = ExtractFilterComplexValue(capturedArgs!);
        filter.Should().Contain(
            "[vpol]scale=-2:'min(ih,720)':flags=lanczos[vout]",
            "the policy stage consumes the last picture label and produces the final one");
        capturedArgs!.Should().Contain("[vout]", "the encode still maps the policy's own output label");
    }

    [Fact]
    public async Task Output_policy_emits_no_filter_at_all_when_the_plan_caps_nothing()
    {
        // A plan with no output cap and no watermark has nothing to apply: the label plumbing and
        // every filter string stay exactly as they were before B8b.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace, organizationId: PolicyOrgId);

        IReadOnlyList<string>? capturedArgs = null;
        StepExecutionResult result = await CreateExecutor(
            workspace,
            scopeFactory: ScopeFactoryFor(
                NewProvenanceDbContext(), resolver: null,
                entitlements: Entitlements(new OrganizationEntitlementSet { Tier = "pro" })),
            ffmpegArgsCaptured: args => capturedArgs ??= args).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        string filter = ExtractFilterComplexValue(capturedArgs!);
        filter.Should().NotContain("min(ih,", "an uncapped plan must never scale the picture");
        filter.Should().NotContain("[vpol]", "with nothing to apply there is no policy label either");
    }

    [Fact]
    public async Task No_organization_means_no_output_policy_and_no_manifest_node()
    {
        // Guid.Empty is the executor's "no organization / single-tenant" sentinel (the same one
        // WorkflowExecutorService uses to skip its own plan gate), so a run with no org has no
        // policy even when an entitlement service IS registered.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        IReadOnlyList<string>? capturedArgs = null;
        // CaptureTimelineManifest must be installed AFTER CreateExecutor (Moq resolves overlapping
        // setups to the most recently configured one, and CreateExecutor installs a catch-all).
        VideoCompileStepExecutor executor = CreateExecutor(
            workspace,
            scopeFactory: ScopeFactoryFor(NewProvenanceDbContext(), resolver: null, entitlements: Entitlements(TrialPlan())),
            ffmpegArgsCaptured: args => capturedArgs ??= args);
        Func<JsonElement> timeline = CaptureTimelineManifest(workspace);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        ExtractFilterComplexValue(capturedArgs!).Should().NotContain("min(ih,");

        JsonElement manifest = timeline();
        manifest.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        manifest.GetProperty("outputVideo").TryGetProperty("outputPolicy", out _)
            .Should().BeFalse("a run with no organization has no plan to record");
    }

    [Fact]
    public async Task Timeline_provenance_records_the_applied_output_policy()
    {
        // The manifest states the policy as FACTS: what the plan allows, what the encode actually
        // did, and — because no watermark raster ships in this repository — why an entitled mark was
        // skipped rather than composited.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace, organizationId: PolicyOrgId);

        VideoCompileStepExecutor executor = CreateExecutor(
            workspace,
            scopeFactory: ScopeFactoryFor(NewProvenanceDbContext(), resolver: null, entitlements: Entitlements(TrialPlan())));
        Func<JsonElement> timeline = CaptureTimelineManifest(workspace);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        JsonElement policy = OutputPolicyNode(timeline());
        policy.GetProperty("tier").GetString().Should().Be("trial");
        policy.GetProperty("maxHeight").GetInt32().Should().Be(720);
        policy.GetProperty("heightCapApplied").GetBoolean().Should().BeTrue();
        policy.GetProperty("outputHeight").GetInt32().Should().Be(720, "1080 source capped at 720");
        policy.GetProperty("watermarkEntitled").GetBoolean().Should().BeTrue();
        policy.GetProperty("watermarkApplied").GetBoolean().Should().BeFalse();
        policy.GetProperty("watermarkSkippedReason").GetString()
            .Should().Be("watermark_asset_not_configured", "no watermark raster is configured in this deployment");
    }

    [Fact]
    public async Task Output_policy_composites_the_watermark_when_a_raster_is_configured_and_present()
    {
        // The ONE path that arms the watermark: an entitled plan AND a configured, present raster.
        // The mark is read from its own ffmpeg input, added after every other input.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace, organizationId: PolicyOrgId);

        string asset = Path.Combine(Path.GetTempPath(), $"reelbolt-wm-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(asset, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        IReadOnlyList<string>? capturedArgs = null;
        try
        {
            StepExecutionResult result = await CreateExecutor(
                workspace,
                scopeFactory: ScopeFactoryFor(NewProvenanceDbContext(), resolver: null, entitlements: Entitlements(TrialPlan())),
                ffmpegArgsCaptured: args => capturedArgs ??= args,
                watermarkAssetPath: asset).ExecuteAsync(context);

            result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        }
        finally
        {
            File.Delete(asset);
        }

        string filter = ExtractFilterComplexValue(capturedArgs!);
        // The mark is the only non-source input here, so it is ffmpeg input 1; it is scaled to 15%
        // of the CAPPED width (1280) = 192px and pinned bottom-right on the capped frame.
        filter.Should().Contain("[vpol]scale=-2:'min(ih,720)':flags=lanczos[vcap]");
        filter.Should().Contain("[1:v]format=rgba,scale=192:-1:flags=bilinear[wm]");
        filter.Should()
            .Contain("[vcap][wm]overlay=x='main_w-overlay_w-24':y='main_h-overlay_h-24'[vout]",
                "the mark is composited on the CAPPED frame, not the raw one");
        capturedArgs!.Should().Contain(asset);
    }

    [Fact]
    public async Task Output_policy_is_recorded_as_skipped_when_the_compile_is_a_stream_copy()
    {
        // A stream copy remuxes the file and carries no filtergraph, so neither the cap nor the mark
        // can be expressed on it. That is recorded as a fact — never as a cap that did not happen.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with
            {
                Mode = VideoCompileMode.StreamCopy,
                AllowKeyframeSnapping = true
            },
            organizationId: PolicyOrgId);

        IReadOnlyList<string>? capturedArgs = null;
        VideoCompileStepExecutor executor = CreateExecutor(
            workspace,
            scopeFactory: ScopeFactoryFor(NewProvenanceDbContext(), resolver: null, entitlements: Entitlements(TrialPlan())),
            ffmpegArgsCaptured: args => capturedArgs ??= args);
        Func<JsonElement> timeline = CaptureTimelineManifest(workspace);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        capturedArgs!.Should().NotContain("-filter_complex", "a stream copy has no filtergraph to put a policy in");

        JsonElement policy = OutputPolicyNode(timeline());
        policy.GetProperty("heightCapApplied").GetBoolean().Should().BeFalse();
        policy.GetProperty("outputHeight").GetInt32().Should().Be(1080, "the file really is still 1080p");
        policy.GetProperty("watermarkApplied").GetBoolean().Should().BeFalse();
        policy.GetProperty("watermarkSkippedReason").GetString().Should().Be("stream_copy_has_no_filter_stage");
    }

    /// <summary>The <c>outputVideo.outputPolicy</c> node of an already-captured timeline manifest.</summary>
    private static JsonElement OutputPolicyNode(JsonElement timeline)
    {
        timeline.ValueKind.Should().NotBe(JsonValueKind.Undefined, "the timeline manifest must have been captured");
        JsonElement outputVideo = timeline.GetProperty("outputVideo");
        outputVideo.TryGetProperty("outputPolicy", out JsonElement policy)
            .Should().BeTrue("a resolved plan must be recorded on the manifest");
        return policy;
    }

    // =======================================================================
    // Test infrastructure
    // =======================================================================

    private static VideoCompileStepExecutor CreateExecutor(
        Mock<IProjectFileWorkspace> workspace, Action<JsonElement>? edlCaptured = null, bool drawtextAvailable = true,
        Action<IReadOnlyList<string>>? ffmpegArgsCaptured = null,
        Action<Mock<IMediaProbe>>? configureMediaProbe = null,
        Action<Mock<IVideoToolRunner>>? configureToolRunner = null,
        bool amixNormalizeAvailable = true,
        bool insertFiltersAvailable = true,
        bool gradeFiltersAvailable = true,
        IServiceScopeFactory? scopeFactory = null,
        bool failEncode = false,
        ILogger<VideoCompileStepExecutor> logger = null,
        ReelBolt.Shared.Metering.IUsageRecorder? usageRecorder = null,
        string watermarkAssetPath = "")
    {
        // The drawtext-availability probe is a process-lifetime static cache in the executor
        // (see ResolveGraphicsAsync/IsDrawtextAvailableAsync) — reset it per test case so each
        // test's own mocked IVideoToolRunner is actually consulted. Same for the amix
        // normalize-option probe (background music — see ResolveMusicAsync/IsAmixNormalizeAvailableAsync)
        // and the perspective/alphamerge probe (tracked screen inserts — see
        // ResolveInsertsAsync/IsPerspectiveAvailableAsync).
        VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
        VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();

        string filtersStdOut =
            (drawtextAvailable ? "... drawtext ..." : "... (nothing here) ...") +
            (insertFiltersAvailable ? " perspective alphamerge" : "") +
            (gradeFiltersAvailable ? " colorbalance colorlevels hue  eq  " : "");
        var toolRunner = new Mock<IVideoToolRunner>();
        toolRunner
            .Setup(t => t.RunFfmpegAsync(
                It.Is<IReadOnlyList<string>>(a => a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, filtersStdOut, string.Empty, false));
        toolRunner
            .Setup(t => t.RunFfmpegAsync(
                It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken>((args, _, _) => ffmpegArgsCaptured?.Invoke(args))
            .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
        // The reencode path's main encode call goes through the 4-arg overload (real ffmpeg
        // "-progress pipe:1" percentage — see BuildFfmpegProgressLineHandler) whenever a
        // StepExecutionContext + known output duration were supplied, which the executor always
        // does — so this overload needs its own setup, mirroring the 3-arg one above exactly,
        // or every reencode test would hit an unconfigured mock member and NRE on await.
        toolRunner
            .Setup(t => t.RunFfmpegAsync(
                It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>>()))
            .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken, Action<string>>((args, _, _, _) => ffmpegArgsCaptured?.Invoke(args))
            .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
        // Applied AFTER the two catch-all "not -filters" setups above — Moq resolves overlapping
        // setups to the most recently configured one, same precedence discipline the
        // configureMediaProbe comment below documents — so the amix probe (which also doesn't
        // contain "-filters") gets its own realistic response instead of the catch-all's empty
        // stdout (which would make IsAmixNormalizeAvailableAsync always resolve to unavailable).
        // The "unavailable" stdout must NOT contain the literal substring "normalize" anywhere —
        // IsAmixNormalizeAvailableAsync does a plain Contains("normalize") check, so the previous
        // "... (no normalize) ..." wording accidentally read as AVAILABLE, silently breaking both
        // amix-unavailable tests (found while adding the SFX twin of the music test).
        toolRunner
            .Setup(t => t.RunFfmpegAsync(
                It.Is<IReadOnlyList<string>>(a => a.Contains("filter=amix")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, amixNormalizeAvailable ? "... normalize ..." : "... (no such option) ...", string.Empty, false));

        // Optional per-test hook onto the tool runner, for members none of the three setups above
        // stub at all — today that is RunFfprobeAsync, which only GeneratedClipNormalizer calls
        // (a loose mock's unstubbed Task<T> resolves to a null result, so a generated-clip test
        // that does not stub it here fails inside the normalizer rather than on its own assertion).
        // Invoked before the failEncode override below, so that one still wins for its matcher.
        configureToolRunner?.Invoke(toolRunner);

        var mediaProbe = new Mock<IMediaProbe>();
        mediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(10, 30, 1, 1920, 1080, "h264", "aac", 48000));
        // A rendered-asset overlay (path contains "gfx-asset", same discriminator the corrupt-file
        // test below overrides) defaults to a real alpha-carrying pix_fmt — the well-behaved case,
        // since most tests exercising an asset overlay want it to actually apply, not be dropped
        // for AlphaPixelFormats.HasAlpha failing on the catch-all's opaque "h264" default above.
        // Applied AFTER the catch-all so it takes precedence for exactly that one path family.
        mediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(s => s.Contains("gfx-asset")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(1.5, 30, 1, 640, 360, "vp9", null, null, "yuva420p"));
        // Applied AFTER both setups above — Moq resolves overlapping setups to the most
        // recently configured one, so a test-supplied override (e.g. "throw for this one asset
        // path", or "no alpha for this one asset path") takes precedence over the default success
        // response without needing to know about every other path this test's executor run will probe.
        configureMediaProbe?.Invoke(mediaProbe);

        if (edlCaptured is not null)
        {
            workspace
                .Setup(w => w.UploadArtifactAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .Callback<Guid, string, string, string, CancellationToken, string>((_, path, _, _, _, _) =>
                {
                    if (path.EndsWith("edl.json", StringComparison.OrdinalIgnoreCase))
                    {
                        string json = File.ReadAllText(path);
                        edlCaptured(JsonDocument.Parse(json).RootElement.Clone());
                    }
                })
                .ReturnsAsync("projects/p/agentFiles/video-analysis/e/step-3-edl.json");
            workspace.SetupRegisteredRenderPublish();
        }
        else
        {
            workspace
                .Setup(w => w.UploadArtifactAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync("projects/p/agentFiles/video-analysis/e/step-3-edl.json");
            workspace.SetupRegisteredRenderPublish();
        }

        workspace
            .Setup(w => w.UploadBinaryFileAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(),
                It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((Guid projectId, string _, string fileName, string mime, SummaryStatus _, FileIndexingStatus _,
                CancellationToken _, string category, string? _) =>
                new ProjectWorkspaceFile(
                    Guid.NewGuid(), projectId, fileName, null, category,
                    $"projects/{projectId}/{category}/{Guid.NewGuid():D}.mp4", mime, 1024, DateTime.UtcNow, null));
        workspace.SetupRegisteredRenderPublish();

        // Applied LAST so it wins over both catch-all ffmpeg setups for every call that is
        // neither the -filters availability probe nor the amix-normalize probe. Lets a test drive
        // the executor's encode-failure (ENCODE_FAILED) path without a real ffmpeg.
        if (failEncode)
        {
            toolRunner
                .Setup(t => t.RunFfmpegAsync(
                    It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters") && !a.Contains("filter=amix")),
                    It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
                .ReturnsAsync(new VideoToolResult(1, string.Empty, "simulated encode failure", false));
            toolRunner
                .Setup(t => t.RunFfmpegAsync(
                    It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters") && !a.Contains("filter=amix")),
                    It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VideoToolResult(1, string.Empty, "simulated encode failure", false));
        }

        string tempScratchRoot = Path.Combine(Path.GetTempPath(), ProjectFileStorageRoot, Guid.NewGuid().ToString("N"));
        var options = Options.Create(new VideoEditingOptions
        {
            ScratchPath = tempScratchRoot,
            MaxConcurrentJobs = 1,
            AnalyzeTimeoutSeconds = 30,
            CompileTimeoutSeconds = 30,
            WatermarkAssetPath = watermarkAssetPath
        });

        var bareScopeFactory = new Mock<IServiceScopeFactory>();

        return new VideoCompileStepExecutor(
            toolRunner.Object,
            mediaProbe.Object,
            workspace.Object,
            scopeFactory ?? bareScopeFactory.Object,
            options,
            logger ?? NullLogger<VideoCompileStepExecutor>.Instance,
            usageRecorder);
    }

    private static StepExecutionContext CreateContext(
        VideoAnalysisArtifact artifact,
        string decisionJson,
        out Mock<IProjectFileWorkspace> workspace,
        Func<VideoCompileStepConfig, VideoCompileStepConfig>? configOverride = null,
        // B8b: the run's organization, i.e. what the trial output policy is read from. Guid.Empty
        // (the default, and what every pre-B8b fixture gets) is the executor's "no organization"
        // sentinel — see ResolveOutputPolicyAsync.
        Guid organizationId = default)
    {
        workspace = new Mock<IProjectFileWorkspace>();

        string artifactJson = JsonSerializer.Serialize(artifact, ArtifactOptions());

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), AnalysisKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllText(destPath, artifactJson);
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), SourceVideoKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        VideoCompileStepConfig config = new(
            Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Previous),
            AnalysisStepOrder: 1);

        if (configOverride is not null)
            config = configOverride(config);

        string configJson = JsonSerializer.Serialize(config, ConfigOptions());

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 3,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = configJson
        };

        // VideoCompileStepExecutor resolves the source video by reading the referenced
        // VideoAnalyze step's OWN VideoAnalyzeConfigJson.Source (never a StepOutputHistory
        // heuristic — see ResolveSourceStorageKeyAsync) — so a real analyze step, matching
        // AnalysisStepOrder, must be present in AllSteps. Source=PreviousStepOutput here mirrors
        // the shipped video-derush-edit template chaining off a render step at StepOrder 0.
        VideoAnalyzeStepConfig analyzeConfig = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput));
        var analyzeStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(analyzeConfig, ConfigOptions())
        };

        List<StepOutputHistoryEntry> history =
        [
            new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: SourceVideoKey, ArtifactStorageKey: null),
            new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
            new StepOutputHistoryEntry(2, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null)
        ];

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId, OrganizationId = organizationId },
            Step = step,
            AllSteps = [analyzeStep, step],
            AccumulatedOutput = decisionJson,
            StepOutputHistory = history,
            CurrentStepIndex = 2,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    private static string ErrorCode(StepExecutionResult result)
    {
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString() ?? string.Empty;
    }

    private static string BuildDecisionJson(params (string FromId, string ToId, string Reason)[] spans)
    {
        var keep = spans.Select(s => new { fromId = s.FromId, toId = s.ToId, reason = s.Reason });
        return JsonSerializer.Serialize(new { keep, editRationale = "test", suggestedTitle = "Test Edit" });
    }

    private static VideoAnalysisArtifact BuildArtifact(
        (string Id, double Start, double End)[] shots,
        (string Id, double Start, double End)[]? silences = null,
        (string Id, double Start, double End)[]? segments = null,
        (string Id, double Start, double End)[]? words = null,
        string[]? offeredIds = null,
        double? durationSec = null,
        int fpsNum = 30,
        int fpsDen = 1,
        IReadOnlyList<VideoAnalysisPlacement>? placements = null,
        string[]? offeredPlacementIds = null,
        // Parallel to `segments` (same length/order) — lets a test control each segment's own
        // text for sentence-boundary-check tests without disturbing every other BuildArtifact call
        // site, which never sets this and keeps getting the fixed placeholder "text".
        string[]? segmentTexts = null)
    {
        silences ??= Array.Empty<(string, double, double)>();
        segments ??= Array.Empty<(string, double, double)>();
        words ??= Array.Empty<(string, double, double)>();

        double duration = durationSec ?? (shots.Length > 0 ? shots.Max(s => s.End) : 60.0);

        return new VideoAnalysisArtifact(
            Version: 1,
            Media: new VideoAnalysisMedia(duration, fpsNum, fpsDen, 1920, 1080),
            Shots: shots.Select(s => new VideoAnalysisShot(s.Id, s.Start, s.End)).ToList(),
            SilenceSpans: silences.Select(s => new VideoAnalysisSilenceSpan(s.Id, s.Start, s.End, null)).ToList(),
            Segments: segments.Select((s, i) => new VideoAnalysisSegment(
                s.Id, null, s.Start, s.End,
                segmentTexts is not null && i < segmentTexts.Length ? segmentTexts[i] : "text")).ToList(),
            Words: words.Select(s => new VideoAnalysisWord(s.Id, s.Start, s.End, "word")).ToList(),
            OfferedIds: offeredIds?.ToList() ?? shots.Select(s => s.Id).Concat(silences.Select(s => s.Id)).Concat(segments.Select(s => s.Id)).ToList(),
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
            Placements: placements,
            OfferedPlacementIds: offeredPlacementIds?.ToList() ?? (placements is not null ? placements.Select(p => p.Id).ToList() : null));
    }

    private static JsonSerializerOptions ConfigOptions() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static JsonSerializerOptions ArtifactOptions() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    // =======================================================================
    // Phase 3: motion graphics
    // =======================================================================

    [Fact]
    public async Task EnableGraphics_false_produces_no_graphics_key_at_all_byte_identical_to_pre_phase3()
    {
        // The load-bearing backward-compatibility guarantee of Phase 3: even with a GraphicsPlan
        // configured, EnableGraphics=false (the default) must leave the EDL/output shape
        // completely untouched — no "graphics" key anywhere.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0) },
            offeredIds: new[] { "s0", "s1" });

        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with
            {
                EnableGraphics = false,
                GraphicsPlan = new ExtractInputRef(ExtractInputSource.Previous)
            });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        edl.TryGetProperty("graphics", out _).Should().BeFalse("EDL must have no graphics key when EnableGraphics=false");

        using JsonDocument outputDoc = JsonDocument.Parse(result.Output);
        outputDoc.RootElement.TryGetProperty("graphics", out _).Should().BeFalse("output summary must have no graphics key when EnableGraphics=false");
    }

    [Fact]
    public async Task EnableGraphics_true_with_StreamCopy_fails_GRAPHICS_REQUIRE_REENCODE()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with
            {
                EnableGraphics = true,
                Mode = VideoCompileMode.StreamCopy,
                AllowKeyframeSnapping = true
            });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("GRAPHICS_REQUIRE_REENCODE");
    }

    [Fact]
    public async Task Keep_span_naming_a_placement_id_fails_UNKNOWN_ID_since_placements_are_a_separate_namespace()
    {
        var placement = new VideoAnalysisPlacement(
            "p0", "s0", "LowerThird", new VideoAnalysisRect(0.1, 0.8, 0.6, 0.15),
            "ShotMiddle", 4.0, 6.0, 0.8, "Light");

        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" },
            placements: new[] { placement });

        // A Keep span naming "p0" — a placement id, not a cut-anchor id — must fail UNKNOWN_ID
        // exactly like any other id BuildIdTimeIndex does not contain.
        string decisionJson = BuildDecisionJson(("p0", "p0", "wrong namespace"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("UNKNOWN_ID");
    }

    [Fact]
    public async Task Keep_span_naming_a_look_group_id_fails_UNKNOWN_ID_since_look_groups_are_a_separate_namespace()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 5.0), ("s1", 5.0, 10.0) },
            offeredIds: new[] { "s0", "s1" });
        artifact = artifact with
        {
            LookGroups = [new VideoAnalysisLookGroup("k0", ["s0", "s1"], "s0", 90.0, "Warm", "Normal", "Natural")]
        };

        // A Keep span naming "k0" — a look-group id, a purely descriptive namespace never offered
        // to any agent at all — must fail UNKNOWN_ID exactly like a placement/music-track id does
        // (§6.8: BuildIdTimeIndex deliberately excludes it).
        string decisionJson = BuildDecisionJson(("k0", "k0", "wrong namespace"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("UNKNOWN_ID");
    }

    [Fact]
    public async Task Missing_GraphicsPlan_content_produces_graphics_free_but_otherwise_successful_compile()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));

        // GraphicsPlan points at a step order with no history entry at all -> unresolvable.
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with
            {
                EnableGraphics = true,
                GraphicsPlan = new ExtractInputRef(ExtractInputSource.Step, StepOrder: 99)
            });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, "a missing graphics plan must never fail the compile — the cut is the primary deliverable");
        JsonElement graphics = edl.GetProperty("graphics");
        graphics.GetProperty("enabled").GetBoolean().Should().BeTrue();
        graphics.GetProperty("applied").GetBoolean().Should().BeFalse();
        graphics.TryGetProperty("reason", out JsonElement reason).Should().BeTrue();
        reason.GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Overlay_naming_unknown_placement_id_is_dropped_not_a_step_failure()
    {
        var placement = new VideoAnalysisPlacement(
            "p0", "s0", "LowerThird", new VideoAnalysisRect(0.1, 0.8, 0.6, 0.15),
            "ShotMiddle", 4.0, 6.0, 0.8, "Light");

        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" },
            placements: new[] { placement });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string graphicsPlanJson = JsonSerializer.Serialize(new
        {
            overlays = new[]
            {
                new { placementId = "p99", kind = "Tag", text = "Nope", subtext = "", duration = "Short", emphasis = "Normal", reason = "bad id" }
            },
            planRationale = "test"
        });

        StepExecutionContext context = CreateGraphicsContext(
            artifact, decisionJson, graphicsPlanJson, out Mock<IProjectFileWorkspace> workspace);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        JsonElement graphics = edl.GetProperty("graphics");
        graphics.GetProperty("applied").GetBoolean().Should().BeFalse();
        graphics.GetProperty("appliedOverlayCount").GetInt32().Should().Be(0);
        JsonElement dropped = graphics.GetProperty("droppedOverlays");
        dropped.GetArrayLength().Should().Be(1);
        dropped[0].GetProperty("placementId").GetString().Should().Be("p99");
        dropped[0].GetProperty("reason").GetString().Should().Be("unknown_placement_id");
    }

    [Fact]
    public async Task Valid_overlay_is_applied_and_recorded_in_the_graphics_block()
    {
        var placement = new VideoAnalysisPlacement(
            "p0", "s0", "LowerThird", new VideoAnalysisRect(0.1, 0.8, 0.6, 0.15),
            "ShotMiddle", 4.0, 6.0, 0.8, "Light");

        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" },
            placements: new[] { placement });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string graphicsPlanJson = JsonSerializer.Serialize(new
        {
            overlays = new[]
            {
                new { placementId = "p0", kind = "LowerThird", text = "Jane Doe", subtext = "Engineer", duration = "Short", emphasis = "Normal", reason = "intro" }
            },
            planRationale = "test"
        });

        StepExecutionContext context = CreateGraphicsContext(
            artifact, decisionJson, graphicsPlanJson, out Mock<IProjectFileWorkspace> workspace);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        JsonElement graphics = edl.GetProperty("graphics");
        graphics.GetProperty("applied").GetBoolean().Should().BeTrue();
        graphics.GetProperty("appliedOverlayCount").GetInt32().Should().Be(1);
        graphics.GetProperty("droppedOverlays").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Applied_overlay_records_its_exact_deterministic_frame_coverage_percentage()
    {
        // Evidence for AgentType.VideoReviewAgent's oversized-overlay check (docs/video-editing.md
        // "Review loop") — must be the exact geometry the encode itself uses, not an
        // approximation. Band (0.1, 0.8, 0.6, 0.15) at this artifact's 1920x1080 media shrinks via
        // ComputeAccentBoxPixels's defaults to w=945,h=162 (identical arithmetic to
        // DrawtextFilterBuilderTests' "Box_geometry_is_computed..." test) -> coverage =
        // 945*162/(1920*1080) ≈ 7.4%, well under the ~20-25% VideoReviewAgent's prompt treats as
        // oversized — proving the size fix actually shows up in reviewable evidence.
        var placement = new VideoAnalysisPlacement(
            "p0", "s0", "LowerThird", new VideoAnalysisRect(0.1, 0.8, 0.6, 0.15),
            "ShotMiddle", 4.0, 6.0, 0.8, "Light");

        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" },
            placements: new[] { placement });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string graphicsPlanJson = JsonSerializer.Serialize(new
        {
            overlays = new[]
            {
                new { placementId = "p0", kind = "LowerThird", text = "Jane Doe", subtext = "", duration = "Short", emphasis = "Normal", reason = "intro" }
            },
            planRationale = "test"
        });

        StepExecutionContext context = CreateGraphicsContext(
            artifact, decisionJson, graphicsPlanJson, out Mock<IProjectFileWorkspace> workspace);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement appliedOverlays = edl.GetProperty("graphics").GetProperty("appliedOverlays");
        appliedOverlays.GetArrayLength().Should().Be(1);
        appliedOverlays[0].GetProperty("placementId").GetString().Should().Be("p0");

        double coveragePct = appliedOverlays[0].GetProperty("coveragePct").GetDouble();
        coveragePct.Should().BeApproximately(7.4, 0.2);
        coveragePct.Should().BeLessThan(20.0, "a compact accent overlay must not be reported as covering a large fraction of the frame");
    }

    // ---------------------------------------------------------------------
    // Phase 3: overlay window resolution — intersect the FULL placement window with the kept
    // spans FIRST, then apply durationMs to whatever survived (the production "p6" defect).
    // ---------------------------------------------------------------------

    /// <summary>
    /// Fixture for the overlay-window-ordering tests. Kept spans (after the decision below) are
    /// source <c>[0,5]</c> and <c>[30,60]</c>, i.e. output <c>[0,5)</c> and <c>[5,35)</c>, with one
    /// long cut region at source <c>[5,30]</c>. Shot <c>s2</c> = <c>[20,45]</c> deliberately STARTS
    /// inside that cut region and continues well past it — exactly the shape that produced the
    /// production p6 drop.
    /// </summary>
    private static VideoAnalysisArtifact BuildOverlayWindowArtifact(params VideoAnalysisPlacement[] placements) =>
        BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0), ("s2", 20.0, 45.0), ("s3", 45.0, 60.0) },
            segments: new[] { ("t0", 0.0, 5.0), ("t1", 5.0, 30.0), ("t2", 30.0, 60.0) },
            offeredIds: new[] { "s0", "s1", "s2", "s3", "t0", "t1", "t2" },
            durationSec: 60.0,
            placements: placements);

    /// <summary>Keeps t0 (source [0,5]) and t2 (source [30,60]), cutting t1 (source [5,30]).</summary>
    private static string OverlayWindowDecisionJson() =>
        BuildDecisionJson(("t0", "t0", "intro"), ("t2", "t2", "body"));

    /// <summary>Zero padding so the resolved spans are exactly the segment times, keeping the expected output windows exact.</summary>
    private static VideoCompileStepConfig NoPadding(VideoCompileStepConfig cfg) =>
        cfg with { PrePaddingMs = 0, PostPaddingMs = 0 };

    /// <summary>
    /// Pulls the distinct <c>between(t,START,END)</c> windows out of a captured ffmpeg filtergraph —
    /// the drawbox/drawtext <c>enable=</c> terms, which are the overlay's ACTUAL on-screen window on
    /// the output timeline (see <see cref="DrawtextFilterBuilder"/>).
    /// </summary>
    private static List<(double Start, double End)> ExtractOverlayEnableWindows(IReadOnlyList<string> args)
    {
        string filterComplex = ExtractFilterComplexValue(args);
        return Regex.Matches(filterComplex, @"between\(t,([0-9.]+),([0-9.]+)\)")
            .Select(m => (
                Start: double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                End: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
            .Distinct()
            .ToList();
    }

    [Fact]
    public async Task Overlay_whose_full_window_overlaps_a_kept_span_survives_even_when_its_first_duration_seconds_are_cut()
    {
        // Regression test for the production "p6" drop. The overlay's candidate window is
        // [placement.StartSec, owningShot.EndSec]; durationMs only decides HOW LONG it stays up.
        // Truncating to durationMs BEFORE intersecting with the kept spans tested only
        // [20.0, 23.0] here — entirely inside the cut region [5,30] — so the overlay was dropped as
        // "cut_away" despite its full window [20,45] overlapping the kept span [30,60] by 15s.
        // (Real run: p6's window was [0, 29.83]s against a kept span of [4.8, 51.4]s — 25s of
        // genuine overlap, of which only the first 3.0s were ever tested.)
        var survives = new VideoAnalysisPlacement(
            "p6", "s2", "LowerThird", new VideoAnalysisRect(0.1, 0.8, 0.6, 0.15),
            "ShotStart", 20.0, 22.0, 0.8, "Light");
        // Its owning shot s1 = [10,20] lies WHOLLY inside the cut region [5,30], so even the full
        // window never overlaps a kept span — this one must still be dropped (no regression on the
        // legitimate-drop case).
        var legitimatelyCutAway = new VideoAnalysisPlacement(
            "p3", "s1", "LowerThird", new VideoAnalysisRect(0.1, 0.8, 0.6, 0.15),
            "ShotMiddle", 12.0, 14.0, 0.8, "Light");

        VideoAnalysisArtifact artifact = BuildOverlayWindowArtifact(survives, legitimatelyCutAway);

        string graphicsPlanJson = JsonSerializer.Serialize(new
        {
            overlays = new[]
            {
                new { placementId = "p6", kind = "LowerThird", text = "Jane Doe", subtext = "", duration = "Medium", emphasis = "Normal", reason = "intro" },
                new { placementId = "p3", kind = "Tag", text = "Cut away", subtext = "", duration = "Medium", emphasis = "Normal", reason = "in a cut region" }
            },
            planRationale = "test"
        });

        StepExecutionContext context = CreateGraphicsContext(
            artifact, OverlayWindowDecisionJson(), graphicsPlanJson,
            out Mock<IProjectFileWorkspace> workspace, configOverride: NoPadding);

        JsonElement edl = default;
        List<string>? capturedArgs = null;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e, ffmpegArgsCaptured: a => capturedArgs = a.ToList())
            .ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        JsonElement graphics = edl.GetProperty("graphics");
        graphics.GetProperty("applied").GetBoolean().Should().BeTrue();
        graphics.GetProperty("appliedOverlayCount").GetInt32().Should().Be(
            1, "the placement whose FULL window overlaps a kept span must survive");
        graphics.GetProperty("appliedOverlays")[0].GetProperty("placementId").GetString().Should().Be("p6");

        JsonElement dropped = graphics.GetProperty("droppedOverlays");
        dropped.GetArrayLength().Should().Be(1, "only the wholly-cut-away placement may be dropped");
        dropped[0].GetProperty("placementId").GetString().Should().Be("p3");
        dropped[0].GetProperty("reason").GetString().Should().Be("cut_away");

        // The surviving intersection is source [30,45] -> output [5,20); the overlay goes up where
        // that intersection BEGINS (output 5.0s) and stays up for its Medium duration (3.0s).
        capturedArgs.Should().NotBeNull();
        List<(double Start, double End)> windows = ExtractOverlayEnableWindows(capturedArgs!);
        windows.Should().HaveCount(1);
        windows[0].Start.Should().BeApproximately(5.0, 1e-6);
        windows[0].End.Should().BeApproximately(8.0, 1e-6);
    }

    [Fact]
    public async Task Overlay_already_starting_inside_a_kept_span_keeps_its_previous_window_unchanged()
    {
        // Guardrail for the fix above: for a placement whose first durationMs seconds were ALREADY
        // inside a kept span, intersect-then-truncate must produce exactly the same window
        // truncate-then-intersect did. Placement starts at source 31.0, inside the kept span
        // [30,60] -> output 6.0s, + 3.0s Medium = [6,9].
        var placement = new VideoAnalysisPlacement(
            "p1", "s2", "LowerThird", new VideoAnalysisRect(0.1, 0.8, 0.6, 0.15),
            "ShotMiddle", 31.0, 33.0, 0.8, "Light");

        VideoAnalysisArtifact artifact = BuildOverlayWindowArtifact(placement);

        string graphicsPlanJson = JsonSerializer.Serialize(new
        {
            overlays = new[]
            {
                new { placementId = "p1", kind = "LowerThird", text = "Jane Doe", subtext = "", duration = "Medium", emphasis = "Normal", reason = "intro" }
            },
            planRationale = "test"
        });

        StepExecutionContext context = CreateGraphicsContext(
            artifact, OverlayWindowDecisionJson(), graphicsPlanJson,
            out Mock<IProjectFileWorkspace> workspace, configOverride: NoPadding);

        JsonElement edl = default;
        List<string>? capturedArgs = null;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e, ffmpegArgsCaptured: a => capturedArgs = a.ToList())
            .ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        edl.GetProperty("graphics").GetProperty("appliedOverlayCount").GetInt32().Should().Be(1);
        edl.GetProperty("graphics").GetProperty("droppedOverlays").GetArrayLength().Should().Be(0);

        List<(double Start, double End)> windows = ExtractOverlayEnableWindows(capturedArgs!);
        windows.Should().HaveCount(1);
        windows[0].Start.Should().BeApproximately(6.0, 1e-6);
        windows[0].End.Should().BeApproximately(9.0, 1e-6);
    }

    [Fact]
    public async Task Overlay_window_is_clamped_to_the_kept_intersection_when_that_is_shorter_than_its_duration()
    {
        // The surviving intersection can itself be shorter than durationMs — the overlay must be
        // clamped to it rather than running on past the cut into unrelated footage. Shot s0 =
        // [0,10] intersected with the kept span [0,5] -> output [0,5); a Hold (6s) duration must
        // still end at 5.0s, not 6.0s.
        var placement = new VideoAnalysisPlacement(
            "p0", "s0", "LowerThird", new VideoAnalysisRect(0.1, 0.8, 0.6, 0.15),
            "ShotStart", 0.0, 2.0, 0.8, "Light");

        VideoAnalysisArtifact artifact = BuildOverlayWindowArtifact(placement);

        string graphicsPlanJson = JsonSerializer.Serialize(new
        {
            overlays = new[]
            {
                new { placementId = "p0", kind = "Title", text = "Hold me", subtext = "", duration = "Hold", emphasis = "Normal", reason = "long title" }
            },
            planRationale = "test"
        });

        StepExecutionContext context = CreateGraphicsContext(
            artifact, OverlayWindowDecisionJson(), graphicsPlanJson,
            out Mock<IProjectFileWorkspace> workspace, configOverride: NoPadding);

        List<string>? capturedArgs = null;
        StepExecutionResult result = await CreateExecutor(
            workspace, ffmpegArgsCaptured: a => capturedArgs = a.ToList()).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        List<(double Start, double End)> windows = ExtractOverlayEnableWindows(capturedArgs!);
        windows.Should().HaveCount(1);
        windows[0].Start.Should().BeApproximately(0.0, 1e-6);
        windows[0].End.Should().BeApproximately(5.0, 1e-6, "the overlay must never outlive the kept portion it was mapped into");
    }

    // ---------------------------------------------------------------------
    // Phase 3: rendered-asset overlays (MotionGraphicsOverlay.RenderedAssetStorageKey)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Valid_rendered_asset_overlay_is_applied_via_the_overlay_filter_not_drawtext()
    {
        var placement = new VideoAnalysisPlacement(
            "p0", "s0", "LowerThird", new VideoAnalysisRect(0.1, 0.8, 0.6, 0.15),
            "ShotMiddle", 4.0, 6.0, 0.8, "Light");

        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" },
            placements: new[] { placement });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string assetKey = $"projects/{ProjectId}/outputFiles/{GraphicsExecutionId:D}/overlay.webm";
        string graphicsPlanJson = JsonSerializer.Serialize(new
        {
            overlays = new[]
            {
                new { placementId = "p0", kind = "LowerThird", text = "", subtext = "", duration = "Short", emphasis = "Normal", renderedAssetStorageKey = assetKey, reason = "designed graphic" }
            },
            planRationale = "test"
        });

        StepExecutionContext context = CreateGraphicsContext(
            artifact, decisionJson, graphicsPlanJson, out Mock<IProjectFileWorkspace> workspace);

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), assetKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        JsonElement edl = default;
        List<string>? capturedArgs = null;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e, ffmpegArgsCaptured: a => capturedArgs = a.ToList())
            .ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        JsonElement graphics = edl.GetProperty("graphics");
        graphics.GetProperty("applied").GetBoolean().Should().BeTrue();
        graphics.GetProperty("appliedOverlayCount").GetInt32().Should().Be(1);
        graphics.GetProperty("droppedOverlays").GetArrayLength().Should().Be(0);

        capturedArgs.Should().NotBeNull();
        string filterComplex = ExtractFilterComplexValue(capturedArgs!);
        filterComplex.Should().Contain("overlay=x=", "the asset overlay must be composited via ffmpeg's overlay filter");
        filterComplex.Should().NotContain("drawtext=", "a pure asset overlay must never fall through to the drawtext path");
        capturedArgs!.Count(a => a == "-i").Should().Be(2, "the main source video AND the rendered asset must each be their own -i input");
    }

    [Fact]
    public async Task Rendered_asset_overlay_with_unrecognized_extension_falls_back_to_webm_for_the_local_scratch_path()
    {
        // RenderedAssetStorageKey is model-authored (see MotionGraphicsOverlay.RenderedAssetStorageKey's
        // doc comment); the prefix check stops it escaping this execution's own outputFiles prefix,
        // but the extension itself was previously trusted verbatim via a bare Path.GetExtension for
        // the LOCAL scratch file path. It must instead be allowlisted to what
        // RenderVideoAndUploadToStorage's own recipe actually produces (.webm/.mp4/.mov) and fall
        // back to .webm for anything else — an in-prefix, otherwise well-formed key with a
        // surprising extension here.
        var placement = new VideoAnalysisPlacement(
            "p0", "s0", "LowerThird", new VideoAnalysisRect(0.1, 0.8, 0.6, 0.15),
            "ShotMiddle", 4.0, 6.0, 0.8, "Light");

        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" },
            placements: new[] { placement });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string assetKey = $"projects/{ProjectId}/outputFiles/{GraphicsExecutionId:D}/overlay.exe";
        string graphicsPlanJson = JsonSerializer.Serialize(new
        {
            overlays = new[]
            {
                new { placementId = "p0", kind = "LowerThird", text = "", subtext = "", duration = "Short", emphasis = "Normal", renderedAssetStorageKey = assetKey, reason = "designed graphic" }
            },
            planRationale = "test"
        });

        StepExecutionContext context = CreateGraphicsContext(
            artifact, decisionJson, graphicsPlanJson, out Mock<IProjectFileWorkspace> workspace);

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), assetKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        JsonElement edl = default;
        List<string>? capturedArgs = null;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e, ffmpegArgsCaptured: a => capturedArgs = a.ToList())
            .ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        edl.GetProperty("graphics").GetProperty("applied").GetBoolean().Should().BeTrue(
            "an unrecognized asset extension degrades only the local file naming, never the overlay itself");

        capturedArgs.Should().NotBeNull();
        List<int> inputPositions = capturedArgs!
            .Select((a, i) => (a, i))
            .Where(t => t.a == "-i")
            .Select(t => t.i + 1)
            .ToList();
        string? localAssetInputPath = inputPositions.Select(i => capturedArgs[i]).FirstOrDefault(p => p.Contains("gfx-asset-"));

        localAssetInputPath.Should().NotBeNull();
        Path.GetExtension(localAssetInputPath).Should().Be(
            ".webm", "an extension outside the allowlist (.webm/.mp4/.mov) must fall back to .webm rather than being trusted verbatim");
    }

    [Fact]
    public async Task Rendered_asset_overlay_outside_the_execution_output_prefix_is_dropped_without_downloading_it()
    {
        var placement = new VideoAnalysisPlacement(
            "p0", "s0", "LowerThird", new VideoAnalysisRect(0.1, 0.8, 0.6, 0.15),
            "ShotMiddle", 4.0, 6.0, 0.8, "Light");

        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" },
            placements: new[] { placement });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        // Wrong execution id in the path — must never be trusted even though it is otherwise a
        // well-formed "projects/{id}/outputFiles/..." key (see RenderVideoAndUploadToStorage's own
        // key construction and VideoCompileStepExecutor's prefix re-validation).
        string foreignAssetKey = $"projects/{ProjectId}/outputFiles/{Guid.NewGuid():D}/overlay.webm";
        string graphicsPlanJson = JsonSerializer.Serialize(new
        {
            overlays = new[]
            {
                new { placementId = "p0", kind = "LowerThird", text = "", subtext = "", duration = "Short", emphasis = "Normal", renderedAssetStorageKey = foreignAssetKey, reason = "bad key" }
            },
            planRationale = "test"
        });

        StepExecutionContext context = CreateGraphicsContext(
            artifact, decisionJson, graphicsPlanJson, out Mock<IProjectFileWorkspace> workspace);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, "an untrustworthy asset key must degrade this ONE overlay, never fail the whole compile");

        // THE security property, unchanged: the key as the model named it is never followed.
        workspace.Verify(
            w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), foreignAssetKey, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a storage key outside this execution's own prefix must never even be downloaded");

        // What changed, and why: a foreign-looking key is now RE-ANCHORED onto this execution's
        // own prefix rather than dropped outright (see NormalizeRenderedAssetKey). That is
        // strictly narrower — the only reachable object is one this execution itself wrote — and
        // it is what stops a single mistyped hex digit in the execution GUID silently costing the
        // film every overlay and insert, which is exactly what happened on the alpha launch film.
        string expectedPrefix = $"projects/{ProjectId}/outputFiles/{GraphicsExecutionId:D}/";
        workspace.Verify(
            w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(),
                It.Is<string>(k => k.StartsWith(expectedPrefix, StringComparison.Ordinal) && k.EndsWith("overlay.webm", StringComparison.Ordinal)),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the file name is kept but re-anchored inside this execution's own prefix");
    }

    [Fact]
    public async Task Rendered_asset_overlay_that_fails_ffprobe_is_dropped_not_a_step_failure()
    {
        // A corrupt/unreadable asset must never be allowed to reach ffmpeg as an extra -i input —
        // that would fail the WHOLE encode (cut included), violating "graphics must never hold the
        // cut hostage". ResolveGraphicsAsync must catch this per-overlay via IMediaProbe and drop
        // just that one overlay instead.
        var placement = new VideoAnalysisPlacement(
            "p0", "s0", "LowerThird", new VideoAnalysisRect(0.1, 0.8, 0.6, 0.15),
            "ShotMiddle", 4.0, 6.0, 0.8, "Light");

        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" },
            placements: new[] { placement });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string assetKey = $"projects/{ProjectId}/outputFiles/{GraphicsExecutionId:D}/corrupt.webm";
        string graphicsPlanJson = JsonSerializer.Serialize(new
        {
            overlays = new[]
            {
                new { placementId = "p0", kind = "LowerThird", text = "", subtext = "", duration = "Short", emphasis = "Normal", renderedAssetStorageKey = assetKey, reason = "corrupt asset" }
            },
            planRationale = "test"
        });

        StepExecutionContext context = CreateGraphicsContext(
            artifact, decisionJson, graphicsPlanJson, out Mock<IProjectFileWorkspace> workspace);

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), assetKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0xDE, 0xAD });
                return Task.CompletedTask;
            });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e,
            configureMediaProbe: probe => probe
                .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(s => s.Contains("gfx-asset")), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("ffprobe failed for this corrupt file")))
            .ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        JsonElement graphics = edl.GetProperty("graphics");
        graphics.GetProperty("applied").GetBoolean().Should().BeFalse();
        JsonElement dropped = graphics.GetProperty("droppedOverlays");
        dropped.GetArrayLength().Should().Be(1);
        dropped[0].GetProperty("reason").GetString().Should().Be("asset_download_or_probe_failed");
    }

    [Fact]
    public async Task Rendered_asset_overlay_with_no_alpha_channel_is_dropped_not_composited_opaque()
    {
        // A render that ignored the documented --pixel-format=yuva420p --codec=vp9 recipe (e.g.
        // Remotion's default H.264, no alpha plane at all) must never reach the overlay filter —
        // that would composite as a solid, opaque rectangle over the edited video ("a black square
        // background with giant shadows"). ResolveGraphicsAsync must catch this via
        // AlphaPixelFormats.HasAlpha and drop just that one overlay, same degrade-not-fail
        // discipline as a corrupt/unreadable asset.
        var placement = new VideoAnalysisPlacement(
            "p0", "s0", "LowerThird", new VideoAnalysisRect(0.1, 0.8, 0.6, 0.15),
            "ShotMiddle", 4.0, 6.0, 0.8, "Light");

        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" },
            placements: new[] { placement });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string assetKey = $"projects/{ProjectId}/outputFiles/{GraphicsExecutionId:D}/opaque.mov";
        string graphicsPlanJson = JsonSerializer.Serialize(new
        {
            overlays = new[]
            {
                new { placementId = "p0", kind = "LowerThird", text = "", subtext = "", duration = "Short", emphasis = "Normal", renderedAssetStorageKey = assetKey, reason = "no-alpha render" }
            },
            planRationale = "test"
        });

        StepExecutionContext context = CreateGraphicsContext(
            artifact, decisionJson, graphicsPlanJson, out Mock<IProjectFileWorkspace> workspace);

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), assetKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00 });
                return Task.CompletedTask;
            });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e,
            configureMediaProbe: probe => probe
                .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(s => s.Contains("gfx-asset")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaProbeResult(1.5, 30, 1, 640, 360, "h264", null, null, "yuv420p")))
            .ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, "a missing alpha channel must degrade this ONE overlay, never fail the whole compile");
        JsonElement graphics = edl.GetProperty("graphics");
        graphics.GetProperty("applied").GetBoolean().Should().BeFalse();
        graphics.GetProperty("appliedOverlayCount").GetInt32().Should().Be(0);
        JsonElement dropped = graphics.GetProperty("droppedOverlays");
        dropped.GetArrayLength().Should().Be(1);
        dropped[0].GetProperty("reason").GetString().Should().Be("asset_missing_alpha_channel");
    }

    [Fact]
    public async Task Plain_text_overlay_and_rendered_asset_overlay_can_coexist_in_the_same_compile()
    {
        var textPlacement = new VideoAnalysisPlacement(
            "p0", "s0", "LowerThird", new VideoAnalysisRect(0.1, 0.8, 0.6, 0.15),
            "ShotMiddle", 4.0, 6.0, 0.8, "Light");
        var assetPlacement = new VideoAnalysisPlacement(
            "p1", "s0", "UpperThird", new VideoAnalysisRect(0.1, 0.05, 0.6, 0.15),
            "ShotMiddle", 4.0, 6.0, 0.8, "Light");

        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" },
            placements: new[] { textPlacement, assetPlacement });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string assetKey = $"projects/{ProjectId}/outputFiles/{GraphicsExecutionId:D}/overlay.webm";
        string graphicsPlanJson = JsonSerializer.Serialize(new
        {
            overlays = new[]
            {
                new { placementId = "p0", kind = "Tag", text = "Live", subtext = "", duration = "Short", emphasis = "Normal", renderedAssetStorageKey = "", reason = "plain text" },
                new { placementId = "p1", kind = "Title", text = "", subtext = "", duration = "Short", emphasis = "Normal", renderedAssetStorageKey = assetKey, reason = "rendered graphic" }
            },
            planRationale = "test"
        });

        StepExecutionContext context = CreateGraphicsContext(
            artifact, decisionJson, graphicsPlanJson, out Mock<IProjectFileWorkspace> workspace);

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), assetKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00 });
                return Task.CompletedTask;
            });

        JsonElement edl = default;
        List<string>? capturedArgs = null;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e, ffmpegArgsCaptured: a => capturedArgs = a.ToList())
            .ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        JsonElement graphics = edl.GetProperty("graphics");
        graphics.GetProperty("appliedOverlayCount").GetInt32().Should().Be(2);

        string filterComplex = ExtractFilterComplexValue(capturedArgs!);
        filterComplex.Should().Contain("drawtext=", "the plain-text overlay must still use the pre-existing drawtext path");
        filterComplex.Should().Contain("overlay=x=", "the rendered-asset overlay must use the new overlay-filter path");
        filterComplex.Should().Contain("[vtxt]", "the text stage must hand off to an internal label rather than [vout] directly when an asset stage follows it");
    }

    /// <summary>Pulls the value that follows "-filter_complex" out of a captured ffmpeg argv (never "-filter_complex_script", which these small test filter graphs never trigger).</summary>
    private static string ExtractFilterComplexValue(IReadOnlyList<string> args)
    {
        int index = args.ToList().IndexOf("-filter_complex");
        index.Should().BeGreaterThan(-1, "test compiles are small enough to never hit the -filter_complex_script threshold");
        return args[index + 1];
    }

    /// <summary>
    /// Like <see cref="ExtractFilterComplexValue"/>, but also reads the graph back off disk when
    /// the compile was long enough to be handed to ffmpeg as <c>-filter_complex_script</c>. A
    /// non-planar insert's mesh IS long enough — one <c>perspective</c> pair per cell, each with
    /// eight per-frame expressions — so the script path is the normal one there, not an edge case.
    /// MUST be called from inside the <c>ffmpegArgsCaptured</c> callback: the script lives in the
    /// step's scratch space, which is cleaned up before <c>ExecuteAsync</c> returns.
    /// </summary>
    private static string ExtractFilterGraph(IReadOnlyList<string> args)
    {
        int scriptIndex = args.ToList().IndexOf("-filter_complex_script");
        if (scriptIndex >= 0)
        {
            string path = args[scriptIndex + 1];
            File.Exists(path).Should().BeTrue("the filtergraph script must exist while ffmpeg is being invoked");
            return File.ReadAllText(path);
        }

        return ExtractFilterComplexValue(args);
    }

    [Fact]
    public async Task Explicit_null_overlays_in_graphics_plan_degrades_to_no_graphics_not_UNEXPECTED_ERROR()
    {
        // An explicit `"overlays": null` overwrites MotionGraphicsPlanOutput.Overlays' `= new()`
        // default with a real null under System.Text.Json — this must degrade the same way a
        // missing/unresolvable GraphicsPlan already does (cut succeeds, no graphics applied),
        // never escape as UNEXPECTED_ERROR via an uncaught NullReferenceException.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string graphicsPlanJson = """{"overlays":null,"planRationale":"nothing to show"}""";

        StepExecutionContext context = CreateGraphicsContext(
            artifact, decisionJson, graphicsPlanJson, out Mock<IProjectFileWorkspace> workspace);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, "a null overlays list must never fail the compile — the cut is the primary deliverable");
        JsonElement graphics = edl.GetProperty("graphics");
        graphics.GetProperty("enabled").GetBoolean().Should().BeTrue();
        graphics.GetProperty("applied").GetBoolean().Should().BeFalse();
        graphics.GetProperty("appliedOverlayCount").GetInt32().Should().Be(0);
    }

    /// <summary>Builds a context wired for Phase 3 graphics: a 4th history entry (StepOrder 3, the MotionGraphicsPlanner step) plus EnableGraphics=true, GraphicsPlan pointed at it.</summary>
    private static StepExecutionContext CreateGraphicsContext(
        VideoAnalysisArtifact artifact,
        string decisionJson,
        string graphicsPlanJson,
        out Mock<IProjectFileWorkspace> workspace,
        Func<VideoCompileStepConfig, VideoCompileStepConfig>? configOverride = null)
    {
        workspace = new Mock<IProjectFileWorkspace>();

        string artifactJson = JsonSerializer.Serialize(artifact, ArtifactOptions());

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), AnalysisKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllText(destPath, artifactJson);
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), SourceVideoKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        VideoCompileStepConfig config = new(
            Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
            AnalysisStepOrder: 1,
            EnableGraphics: true,
            GraphicsPlan: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 3));

        if (configOverride is not null)
            config = configOverride(config);

        string configJson = JsonSerializer.Serialize(config, ConfigOptions());

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 4,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = configJson
        };

        VideoAnalyzeStepConfig analyzeConfig = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput));
        var analyzeStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(analyzeConfig, ConfigOptions())
        };

        List<StepOutputHistoryEntry> history =
        [
            new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: SourceVideoKey, ArtifactStorageKey: null),
            new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
            new StepOutputHistoryEntry(2, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null),
            new StepOutputHistoryEntry(3, "MotionGraphicsPlanner", graphicsPlanJson, OutputStorageKey: null, ArtifactStorageKey: null)
        ];

        return new StepExecutionContext
        {
            // Fixed (not Guid.NewGuid()) so tests can construct a RenderedAssetStorageKey that
            // matches the exact "projects/{ProjectId}/outputFiles/{GraphicsExecutionId}/..."
            // prefix VideoCompileStepExecutor validates against, without needing an extra out
            // parameter threaded through every call site.
            Execution = new WorkflowExecution { Id = GraphicsExecutionId, ProjectId = ProjectId },
            Step = step,
            AllSteps = [analyzeStep, step],
            AccumulatedOutput = graphicsPlanJson,
            StepOutputHistory = history,
            CurrentStepIndex = 3,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    // =======================================================================
    // Background music (see docs/video-editing.md "Background music")
    // =======================================================================

    private const string MusicTrackStorageKey = "projects/p/userFiles/music.mp3";
    private static readonly Guid MusicProjectFileId = Guid.NewGuid();
    private static readonly Guid MusicExecutionId = Guid.NewGuid();
    private const string VoiceoverStorageKeyPrefix = "projects/p/agentFiles/voiceover";
    // Storage key of the generated-clip manifest (T-123) — the VideoGenerate step's own
    // ArtifactStorageKey, exactly where VideoGenerateStepExecutor writes it.
    private const string GeneratedClipManifestKey = "projects/p/agentFiles/video-analysis/e/step-5-generated-clips.json";
    private static readonly Guid VoiceoverProjectFileId = Guid.NewGuid();

    // Fixed WorkflowExecution.Id for the T-058 voiceover regression tests, so a test can name the
    // exact keys VoiceoverStepExecutor wrote: the producer uploads each synthesized line through
    // UploadArtifactAsync(..., $"voiceover/{context.Execution.Id}/{id}.wav"), which lands as a BARE
    // artifact with no ProjectFile row behind it.
    private static readonly Guid VoiceoverBareArtifactExecutionId = Guid.NewGuid();

    [Fact]
    public async Task EnableMusic_false_leaves_filter_string_and_output_byte_identical_to_pre_music_compile()
    {
        // The load-bearing backward-compatibility guarantee of this whole feature — mirrors
        // EnableGraphics_false_produces_no_graphics_key_at_all_byte_identical_to_pre_phase3 exactly.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));

        string? baselineFilter = null;
        StepExecutionContext baselineContext = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> baselineWorkspace);
        JsonElement baselineEdl = default;
        StepExecutionResult baselineResult = await CreateExecutor(
            baselineWorkspace, edlCaptured: e => baselineEdl = e,
            ffmpegArgsCaptured: args => baselineFilter ??= ExtractFilterComplexValue(args)).ExecuteAsync(baselineContext);

        // Even with a MusicTrackProjectFileId configured, EnableMusic=false must leave everything
        // byte-identical — the flag alone gates the whole feature.
        string? musicOffFilter = null;
        StepExecutionContext musicOffContext = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> musicOffWorkspace,
            configOverride: cfg => cfg with { EnableMusic = false, MusicTrackProjectFileId = Guid.NewGuid() });
        JsonElement musicOffEdl = default;
        StepExecutionResult musicOffResult = await CreateExecutor(
            musicOffWorkspace, edlCaptured: e => musicOffEdl = e,
            ffmpegArgsCaptured: args => musicOffFilter ??= ExtractFilterComplexValue(args)).ExecuteAsync(musicOffContext);

        baselineResult.Status.Should().Be(StepStatus.Completed);
        musicOffResult.Status.Should().Be(StepStatus.Completed);
        musicOffFilter.Should().Be(baselineFilter, "EnableMusic=false must leave the filter string byte-identical");
        baselineFilter.Should().NotContain("amix").And.NotContain("[adial]").And.NotContain("[amus]");

        baselineEdl.TryGetProperty("music", out _).Should().BeFalse();
        musicOffEdl.TryGetProperty("music", out _).Should().BeFalse();
        using JsonDocument outputDoc = JsonDocument.Parse(musicOffResult.Output);
        outputDoc.RootElement.TryGetProperty("music", out _).Should().BeFalse();
    }

    [Fact]
    public async Task EnableMusic_true_with_StreamCopy_fails_MUSIC_REQUIRES_REENCODE()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with
            {
                EnableMusic = true,
                MusicTrackProjectFileId = Guid.NewGuid(),
                Mode = VideoCompileMode.StreamCopy,
                AllowKeyframeSnapping = true
            });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("MUSIC_REQUIRES_REENCODE");
    }

    [Fact]
    public async Task EnableMusic_true_with_AudioCodec_copy_fails_MUSIC_REQUIRES_AUDIO_REENCODE()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with
            {
                EnableMusic = true,
                MusicTrackProjectFileId = Guid.NewGuid(),
                AudioCodec = "copy"
            });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("MUSIC_REQUIRES_AUDIO_REENCODE");
    }

    [Fact]
    public async Task Keep_span_naming_a_music_track_id_fails_UNKNOWN_ID_since_music_tracks_are_a_separate_namespace()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("m0", "m0", "wrong namespace"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("UNKNOWN_ID");
    }

    [Fact]
    public async Task Deterministic_music_track_is_applied_and_recorded_in_the_music_block()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateMusicContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement music = edl.GetProperty("music");
        music.GetProperty("enabled").GetBoolean().Should().BeTrue();
        music.GetProperty("applied").GetBoolean().Should().BeTrue();
        music.GetProperty("source").GetString().Should().Be("config");
        music.GetProperty("trackId").GetString().Should().Be(""); // no plan involved — the config path never assigns an m{n} id
        music.GetProperty("intensity").GetString().Should().Be("Balanced");
        music.GetProperty("ducking").GetString().Should().Be("Normal");
        music.GetProperty("fit").GetString().Should().Be("LoopToFit");
        // No shot in this artifact carries a Phase 1 Audio descriptor, so dialogueHeadroom must
        // report inapplicable rather than fabricate a mean level from nothing.
        music.GetProperty("dialogueHeadroom").GetProperty("applicable").GetBoolean().Should().BeFalse();

        using JsonDocument outputDoc = JsonDocument.Parse(result.Output);
        outputDoc.RootElement.GetProperty("music").GetProperty("applied").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Music_input_is_the_last_ffmpeg_input_and_the_audio_cut_label_flips_to_adial()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateMusicContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        IReadOnlyList<string>? capturedArgs = null;
        StepExecutionResult result = await CreateExecutor(
            workspace, ffmpegArgsCaptured: args => capturedArgs ??= args).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        capturedArgs.Should().NotBeNull();

        List<int> inputFlagIndices = capturedArgs!
            .Select((a, i) => (a, i))
            .Where(t => t.a == "-i")
            .Select(t => t.i)
            .ToList();
        inputFlagIndices.Should().HaveCount(2, "the main source input plus the music input");
        capturedArgs[inputFlagIndices[0] + 1].Should().NotContain("music", "input 0 must still be the main source video");
        capturedArgs[inputFlagIndices[^1] + 1].Should().Contain("music", "the music track must be the LAST ffmpeg input");

        string filterComplex = ExtractFilterComplexValue(capturedArgs);
        filterComplex.Should().Contain("[adial]");
        filterComplex.Should().Contain("[amus]");
        filterComplex.Should().Contain("amix=inputs=2:duration=first:dropout_transition=0:normalize=0[aout]");
        filterComplex.Should().Contain("[1:a]atrim=end=", "music is ffmpeg input index 1 here (no asset overlays present)");
    }

    [Fact]
    public async Task MuteSourceAudio_drops_the_clips_own_sound_and_plays_the_music_alone()
    {
        // The source HAS an audio stream (generated clips ship noise), but the author asked for a
        // music-only program: the clip's audio must never be read, music must not be ducked under
        // it, and the report must say the sound was muted rather than missing.
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateMusicContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { MuteSourceAudio = true });

        IReadOnlyList<string>? capturedArgs = null;
        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(
            workspace, ffmpegArgsCaptured: args => capturedArgs ??= args, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        string filterComplex = ExtractFilterComplexValue(capturedArgs!);
        filterComplex.Should().NotContain("[0:a]", "the clip's own sound is muted");
        filterComplex.Should().NotContain("[adial]");
        filterComplex.Should().NotContain("amix");
        filterComplex.Should().Contain("[1:a]atrim=end=", "the music still plays");
        edl.GetProperty("music").GetProperty("duckBasis").GetString().Should().Be("no_dialogue_audio");
        // The music is the whole soundtrack, so it plays at the track's own level rather than
        // the -20 dB bed it would sit at under speech.
        edl.GetProperty("music").GetProperty("bedDbfs").GetInt32().Should().Be(VideoCompileStepExecutor.SoleSoundtrackMusicDb);
        edl.GetProperty("music").GetProperty("bedBasis").GetString().Should().Be("sole_soundtrack");
        // ...through a peak limiter, since nothing else stands between a loud master and clipping.
        filterComplex.Should().Contain(MusicMixFilterBuilder.SoleSoundtrackLimiter);
        edl.GetProperty("audio").GetProperty("reason").GetString().Should().Be("source_audio_muted");
    }

    [Fact]
    public async Task MusicGainDb_zero_passes_the_track_through_at_unity_with_no_clamp_and_no_limiter()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateMusicContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { MuteSourceAudio = true, MusicGainDb = 0 });

        IReadOnlyList<string>? capturedArgs = null;
        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(
            workspace, ffmpegArgsCaptured: args => capturedArgs ??= args, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        string filterComplex = ExtractFilterComplexValue(capturedArgs!);
        filterComplex.Should().Contain("volume=eval=frame:volume='1'", "unity gain is the identity");
        filterComplex.Should().NotContain(MusicMixFilterBuilder.SoleSoundtrackLimiter);
        JsonElement music = edl.GetProperty("music");
        music.GetProperty("bedDbfs").GetDouble().Should().Be(0);
        music.GetProperty("bedBasis").GetString().Should().Be("absolute");
        music.GetProperty("gain").GetProperty("gainDb").GetDouble().Should().Be(0);
        music.GetProperty("gain").GetProperty("source").GetString().Should().Be("absolute");
    }

    [Fact]
    public async Task MusicGainDb_is_not_clamped_to_minus_six_or_lifted_and_ducking_still_follows_dialogue()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateMusicContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { MusicGainDb = 3 });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement music = edl.GetProperty("music");
        music.GetProperty("bedDbfs").GetDouble().Should().Be(3, "an absolute gain above the old -6 dB ceiling is honoured");
        music.GetProperty("duckedDbfs").GetDouble().Should().BeLessThan(3, "dialogue is present, so the ducking attenuation still applies");
        music.GetProperty("duckBasis").GetString().Should().NotBe("no_dialogue_audio");
    }

    [Fact]
    public async Task Intensity_bed_reports_its_source_and_the_level_the_minus_six_clamp_moved_it_from()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateMusicContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { MusicBedBalancedDb = -3 });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement music = edl.GetProperty("music");
        music.GetProperty("bedDbfs").GetInt32().Should().Be(-6);
        music.GetProperty("gain").GetProperty("source").GetString().Should().Be("intensity");
        music.GetProperty("gain").GetProperty("clampedFromDb").GetDouble().Should().Be(-3);
    }

    [Theory]
    [InlineData(6.5)]
    [InlineData(-41)]
    public async Task MusicGainDb_outside_minus_forty_to_plus_six_fails_CONFIG_INVALID(double gainDb)
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateMusicContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { MusicGainDb = gainDb });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("CONFIG_INVALID");
    }

    [Fact]
    public async Task LoudnessTarget_adds_a_sentence_saying_what_was_done_or_why_not()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { LoudnessTarget = ProgramLoudness.Social });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement loudness = JsonDocument.Parse(result.Output).RootElement.GetProperty("loudness");
        loudness.GetProperty("target").GetString().Should().Be("Social");
        loudness.GetProperty("explanation").GetString().Should().StartWith("The Social target of -14 LUFS");
    }

    private static VideoAnalysisArtifact WithGeneratedPlateSource(VideoAnalysisArtifact artifact) =>
        artifact with
        {
            Sources = [new VideoAnalysisSourceInfo(0, "projects/p/agentFiles/generated/e/step-3-v1.mp4", artifact.Media, ClipId: "v1")]
        };

    [Theory]
    [InlineData(GeneratedAudioMode.Auto, "dialogue", "ducked")]
    [InlineData(GeneratedAudioMode.Keep, "ambience", "no_dialogue_audio")]
    [InlineData(GeneratedAudioMode.Mute, "muted", "no_dialogue_audio")]
    public async Task GeneratedClipAudio_decides_whether_a_generated_plates_sound_ducks_the_music_and_is_reported(
        GeneratedAudioMode mode, string audio, string duckBasis)
    {
        VideoAnalysisArtifact artifact = WithGeneratedPlateSource(
            BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" }));
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateMusicContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { GeneratedClipAudio = mode });

        IReadOnlyList<string>? capturedArgs = null;
        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(
            workspace, ffmpegArgsCaptured: args => capturedArgs ??= args, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement report = edl.GetProperty("audio").GetProperty("generatedSources")[0];
        report.GetProperty("sourceIndex").GetInt32().Should().Be(0);
        report.GetProperty("audio").GetString().Should().Be(audio);
        string basis = edl.GetProperty("music").GetProperty("duckBasis").GetString()!;
        if (duckBasis == "ducked")
            basis.Should().NotBe("no_dialogue_audio");
        else
            basis.Should().Be(duckBasis);
        ExtractFilterComplexValue(capturedArgs!).Contains("[0:a]").Should().Be(mode != GeneratedAudioMode.Mute,
            "Mute drops the plate's sound; Keep leaves it playing as ambience");
    }

    [Theory]
    [InlineData(GeneratedAudioMode.Keep, "intensity", -20, false)]
    [InlineData(GeneratedAudioMode.Mute, "sole_soundtrack", VideoCompileStepExecutor.SoleSoundtrackMusicDb, true)]
    public async Task GeneratedClipAudio_Keep_leaves_the_music_at_its_bed_gain_alongside_the_plates_sound_not_as_the_sole_soundtrack(
        GeneratedAudioMode mode, string basis, int bedDb, bool limited)
    {
        VideoAnalysisArtifact artifact = WithGeneratedPlateSource(
            BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" }));
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateMusicContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { GeneratedClipAudio = mode });

        IReadOnlyList<string>? capturedArgs = null;
        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(
            workspace, ffmpegArgsCaptured: args => capturedArgs ??= args, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement music = edl.GetProperty("music");
        music.GetProperty("bedBasis").GetString().Should().Be(basis);
        music.GetProperty("gain").GetProperty("gainDb").GetDouble().Should().Be(bedDb);
        ExtractFilterComplexValue(capturedArgs!).Contains("alimiter").Should().Be(limited,
            "the sole-soundtrack limiter belongs to music that is the only sound, not to a bed under the plate's own audio");
    }

    [Fact]
    public async Task Source_with_no_audio_stream_and_music_uses_the_music_track_alone_as_output_audio()
    {
        // No dialogue anywhere in the compile to duck against, so the music branch's own output
        // becomes [aout] directly — no [adial], no amix mixing stage — rather than either crashing
        // ffmpeg (the pre-fix bug) or silently dropping the music the user explicitly configured.
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateMusicContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        IReadOnlyList<string>? capturedArgs = null;
        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(
            workspace, ffmpegArgsCaptured: args => capturedArgs ??= args, edlCaptured: e => edl = e,
            configureMediaProbe: mock => mock
                .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(path => !path.Contains("music")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaProbeResult(10, 30, 1, 1920, 1080, "h264", null, null)))
            .ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        capturedArgs.Should().NotBeNull();

        string filterComplex = ExtractFilterComplexValue(capturedArgs!);
        filterComplex.Should().NotContain("[0:a]", "the source has no audio stream");
        filterComplex.Should().NotContain("[adial]", "there is no dialogue branch to duck the music against");
        filterComplex.Should().NotContain("amix", "amix only makes sense when mixing two real inputs");
        filterComplex.Should().Contain("[1:a]atrim=end=", "the music branch itself still runs, as ffmpeg input 1");
        filterComplex.Should().Contain("[aout]", "the music branch's own output becomes the final audio output directly");

        List<string> argsList = capturedArgs!.ToList();
        argsList.Should().Contain("[aout]", "there IS audio output overall — it just comes from music alone");
        argsList.Should().Contain("-c:a");

        // Bug group C.2: with no dialogue audio in the final output at all, there is nothing to
        // duck against — ResolveMusicAsync must skip lift-window planning (flat, undocked bed
        // level) and must not report a dialogueHeadroom number describing dialogue that isn't
        // there, rather than sitting the music at the ducked level through what were dialogue
        // regions in the (dropped) source audio.
        JsonElement music = edl.GetProperty("music");
        music.GetProperty("duckBasis").GetString().Should().Be("no_dialogue_audio");
        music.GetProperty("bedDbfs").GetInt32().Should().Be(music.GetProperty("duckedDbfs").GetInt32(),
            "with nothing to duck against, the bed level must stay flat rather than sitting ducked");
        JsonElement headroom = music.GetProperty("dialogueHeadroom");
        headroom.GetProperty("applicable").GetBoolean().Should().BeFalse();
        headroom.GetProperty("reason").GetString().Should().Be("no_dialogue_audio_in_output");
    }

    [Fact]
    public async Task Plan_naming_an_unoffered_track_id_falls_back_to_the_configured_track()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        artifact = artifact with
        {
            MusicCandidates = [new VideoAnalysisMusicCandidate("m0", MusicProjectFileId, "music.mp3", "audio/mpeg", 4096)],
            OfferedMusicIds = ["m0"]
        };
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string musicPlanJson = JsonSerializer.Serialize(new
        {
            trackId = "m9", // not in OfferedMusicIds
            intensity = "Feature",
            ducking = "Heavy",
            fit = "PlayOnce",
            reason = "test",
            planRationale = "test"
        });

        StepExecutionContext context = CreateMusicContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            // Decision must be pointed explicitly at StepOrder 2 (the story editor) here — this
            // test is the only CreateMusicContext caller that appends a 4th history entry
            // (MusicSupervisor, StepOrder 3), so the default Decision: Previous would otherwise
            // resolve to THAT entry instead, exactly the "Previous relative to the compile step
            // would resolve to the MusicSupervisor step's own output, not the story editor's
            // decision" trap CLAUDE.md documents for this feature.
            configOverride: cfg => cfg with
            {
                Decision = new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
                MusicPlan = new ExtractInputRef(ExtractInputSource.Step, StepOrder: 3)
            },
            musicPlanJson: musicPlanJson);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement music = edl.GetProperty("music");
        music.GetProperty("source").GetString().Should().Be("config");
        JsonElement dropped = music.GetProperty("dropped");
        dropped.GetArrayLength().Should().Be(1);
        dropped[0].GetProperty("reason").GetString().Should().Be("unknown_track_id");
        dropped[0].GetProperty("trackId").GetString().Should().Be("m9");
    }

    [Fact]
    public async Task Non_audio_project_file_is_dropped_as_track_not_audio_and_the_cut_still_succeeds()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateMusicContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace, musicMimeType: "video/mp4");

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, "a bad music track must never fail the cut itself");
        JsonElement music = edl.GetProperty("music");
        music.GetProperty("applied").GetBoolean().Should().BeFalse();
        music.GetProperty("dropped")[0].GetProperty("reason").GetString().Should().Be("track_not_audio");
    }

    [Fact]
    public async Task Track_probe_failure_drops_music_and_the_cut_still_succeeds()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateMusicContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e,
            configureMediaProbe: probe => probe
                .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(path => path.Contains("music")), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("corrupt file"))).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, "a bad music track must never fail the cut itself");
        JsonElement music = edl.GetProperty("music");
        music.GetProperty("applied").GetBoolean().Should().BeFalse();
        music.GetProperty("dropped")[0].GetProperty("reason").GetString().Should().Be("track_download_or_probe_failed");
    }

    [Fact]
    public async Task Amix_without_normalize_option_skips_all_music_and_the_cut_still_succeeds()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateMusicContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e, amixNormalizeAvailable: false).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement music = edl.GetProperty("music");
        music.GetProperty("unavailable").GetBoolean().Should().BeTrue();
        music.GetProperty("applied").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task LoopToFit_with_a_track_shorter_than_the_edit_loops_and_trims_to_the_edit_length()
    {
        // 10s edit, 4s track -> loops (ceil(10/4) = 3), -stream_loop present, atrim=end=10.
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateMusicContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        JsonElement edl = default;
        IReadOnlyList<string>? capturedArgs = null;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e, ffmpegArgsCaptured: args => capturedArgs ??= args,
            configureMediaProbe: probe => probe
                .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(path => path.Contains("music")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaProbeResult(4, 30, 1, 0, 0, null, "mp3", 44100))).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement music = edl.GetProperty("music");
        music.GetProperty("fit").GetString().Should().Be("LoopToFit");
        music.GetProperty("loops").GetInt32().Should().Be(3);
        music.GetProperty("playEndSec").GetDouble().Should().BeApproximately(10.0, 0.01);
        capturedArgs.Should().Contain("-stream_loop");

        string filterComplex = ExtractFilterComplexValue(capturedArgs!);
        filterComplex.Should().Contain("atrim=end=10");
    }

    [Fact]
    public async Task PlayOnce_with_a_track_shorter_than_the_edit_never_loops_and_trims_to_the_track_length()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateMusicContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { MusicFitPolicy = MusicFit.PlayOnce });

        JsonElement edl = default;
        IReadOnlyList<string>? capturedArgs = null;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e, ffmpegArgsCaptured: args => capturedArgs ??= args,
            configureMediaProbe: probe => probe
                .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(path => path.Contains("music")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaProbeResult(4, 30, 1, 0, 0, null, "mp3", 44100))).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement music = edl.GetProperty("music");
        music.GetProperty("fit").GetString().Should().Be("PlayOnce");
        music.GetProperty("playEndSec").GetDouble().Should().BeApproximately(4.0, 0.01);
        capturedArgs.Should().NotContain("-stream_loop");

        string filterComplex = ExtractFilterComplexValue(capturedArgs!);
        filterComplex.Should().Contain("atrim=end=4");
    }

    /// <summary>Builds a context wired for background music: the deterministic MusicTrackProjectFileId path by default, and an optional 4th history entry (StepOrder 3, a MusicSupervisor step) when <paramref name="musicPlanJson"/> is supplied.</summary>
    // =======================================================================
    // Color grading (see docs/video-editing.md "Color grading")
    // =======================================================================

    [Fact]
    public async Task EnableColorGrade_false_produces_no_colorGrade_key_at_all_byte_identical_to_pre_grade()
    {
        // The load-bearing backward-compatibility guarantee: even with a ColorGradePlan
        // configured, EnableColorGrade=false (the default) must leave the EDL/output shape
        // completely untouched — no "colorGrade" key anywhere.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with
            {
                EnableColorGrade = false,
                ColorGradePlan = new ExtractInputRef(ExtractInputSource.Previous)
            });

        JsonElement edl = default;
        IReadOnlyList<string>? capturedArgs = null;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e, ffmpegArgsCaptured: args => capturedArgs ??= args).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        edl.TryGetProperty("colorGrade", out _).Should().BeFalse("EDL must have no colorGrade key when EnableColorGrade=false");

        using JsonDocument outputDoc = JsonDocument.Parse(result.Output);
        outputDoc.RootElement.TryGetProperty("colorGrade", out _).Should().BeFalse();
        string filterComplex = string.Join(" ", capturedArgs!);
        filterComplex.Should().NotContain("colorbalance", "no grade filter may reach ffmpeg when EnableColorGrade=false");
    }

    [Fact]
    public async Task EnableColorGrade_true_with_StreamCopy_fails_COLOR_GRADE_REQUIRES_REENCODE()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with
            {
                EnableColorGrade = true,
                Mode = VideoCompileMode.StreamCopy,
                AllowKeyframeSnapping = true
            });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("COLOR_GRADE_REQUIRES_REENCODE");
    }

    [Fact]
    public async Task Warm_grade_plan_appends_the_first_party_chain_to_the_cut_stage_and_reports_it_in_the_EDL()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string gradePlanJson = JsonSerializer.Serialize(new
        {
            look = "Warm",
            strength = "Subtle",
            shadowTone = "Lifted",
            highlightTone = "Neutral",
            reason = "test",
            planRationale = "test"
        });

        StepExecutionContext context = CreateGradeContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace, gradePlanJson);

        JsonElement edl = default;
        IReadOnlyList<string>? capturedArgs = null;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e, ffmpegArgsCaptured: args => capturedArgs ??= args).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        JsonElement grade = edl.GetProperty("colorGrade");
        grade.GetProperty("applied").GetBoolean().Should().BeTrue();
        grade.GetProperty("look").GetString().Should().Be("Warm");
        grade.GetProperty("strength").GetString().Should().Be("Subtle");
        grade.GetProperty("shadowTone").GetString().Should().Be("Lifted");

        string expectedChain = ColorGradeFilterBuilder.BuildFilterChain("Warm", "Subtle", "Lifted", "Neutral")!;
        grade.GetProperty("filterChain").GetString().Should().Be(expectedChain);

        // The chain must sit in the CUT stage (immediately after setpts) — i.e. before any
        // overlay/insert stage would run — never as a dangling separate stage.
        int fcIndex = capturedArgs!.ToList().IndexOf("-filter_complex");
        fcIndex.Should().BeGreaterThan(0);
        string filterComplex = capturedArgs[fcIndex + 1];
        filterComplex.Should().Contain("setpts=N/FRAME_RATE/TB," + expectedChain);

        using JsonDocument outputDoc = JsonDocument.Parse(result.Output);
        outputDoc.RootElement.GetProperty("colorGrade").GetProperty("applied").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Grade_plan_with_look_None_applies_no_filter_and_reports_look_none()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string gradePlanJson = JsonSerializer.Serialize(new
        {
            look = "None",
            strength = "Normal",
            shadowTone = "Lifted",
            highlightTone = "Softened",
            reason = "already consistent",
            planRationale = "no grade"
        });

        StepExecutionContext context = CreateGradeContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace, gradePlanJson);

        JsonElement edl = default;
        IReadOnlyList<string>? capturedArgs = null;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e, ffmpegArgsCaptured: args => capturedArgs ??= args).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement grade = edl.GetProperty("colorGrade");
        grade.GetProperty("applied").GetBoolean().Should().BeFalse();
        grade.GetProperty("reason").GetString().Should().Be("look_none");
        int fcIndex = capturedArgs!.ToList().IndexOf("-filter_complex");
        string filterComplex = fcIndex >= 0 ? capturedArgs[fcIndex + 1] : string.Join(" ", capturedArgs!);
        filterComplex.Should().NotContain("colorbalance",
            "a plan whose look is None must leave the compiled bytes carrying no grade filter whatsoever — tone words included");
        filterComplex.Should().NotContain("colorlevels");
    }

    [Fact]
    public async Task Grade_plan_with_an_unknown_look_word_degrades_to_no_grade_and_the_cut_still_succeeds()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string gradePlanJson = JsonSerializer.Serialize(new
        {
            look = "Sepia",
            strength = "Normal",
            shadowTone = "Neutral",
            highlightTone = "Neutral",
            reason = "test",
            planRationale = "test"
        });

        StepExecutionContext context = CreateGradeContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace, gradePlanJson);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, "a bad grade plan must never fail the cut itself");
        JsonElement grade = edl.GetProperty("colorGrade");
        grade.GetProperty("applied").GetBoolean().Should().BeFalse();
        grade.GetProperty("reason").GetString().Should().Be("unknown_look_word");
        grade.GetProperty("look").GetString().Should().Be("Sepia");
    }

    [Fact]
    public async Task Missing_ColorGradePlan_content_produces_grade_free_but_otherwise_successful_compile()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));

        // ColorGradePlan references a step order that produced no output at all.
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with
            {
                EnableColorGrade = true,
                ColorGradePlan = new ExtractInputRef(ExtractInputSource.Step, StepOrder: 99)
            });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement grade = edl.GetProperty("colorGrade");
        grade.GetProperty("applied").GetBoolean().Should().BeFalse();
        grade.GetProperty("reason").GetString().Should().Be("plan_unresolved");
    }

    [Fact]
    public async Task Grade_filters_missing_from_the_ffmpeg_build_skip_the_grade_and_the_cut_still_succeeds()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string gradePlanJson = JsonSerializer.Serialize(new
        {
            look = "Cool",
            strength = "Normal",
            shadowTone = "Neutral",
            highlightTone = "Neutral",
            reason = "test",
            planRationale = "test"
        });

        StepExecutionContext context = CreateGradeContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace, gradePlanJson);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e, gradeFiltersAvailable: false).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, "an ffmpeg build without the grade filters must never fail the cut itself");
        JsonElement grade = edl.GetProperty("colorGrade");
        grade.GetProperty("applied").GetBoolean().Should().BeFalse();
        grade.GetProperty("reason").GetString().Should().Be("grade_filters_unavailable");
    }

    /// <summary>
    /// Mirrors <see cref="CreateContext"/> exactly, with one extra history entry: the grade plan
    /// at step order 4 (referenced explicitly, since Previous would resolve to the story
    /// editor's decision) and EnableColorGrade/ColorGradePlan preconfigured — the same pattern
    /// <see cref="CreateMusicContext"/> established for its plan entry.
    /// </summary>
    private static StepExecutionContext CreateGradeContext(
        VideoAnalysisArtifact artifact,
        string decisionJson,
        out Mock<IProjectFileWorkspace> workspace,
        string gradePlanJson)
    {
        StepExecutionContext baseContext = CreateContext(
            artifact, decisionJson, out workspace,
            configOverride: cfg => cfg with
            {
                // Decision must reference the story editor explicitly here — with the grade-plan
                // entry appended at step order 4, "Previous" would resolve to the grade plan
                // itself, exactly the trap the shipped templates' comments document.
                Decision = new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
                EnableColorGrade = true,
                ColorGradePlan = new ExtractInputRef(ExtractInputSource.Step, StepOrder: 4)
            });

        List<StepOutputHistoryEntry> history = baseContext.StepOutputHistory.ToList();
        history.Add(new StepOutputHistoryEntry(4, "Colorist", gradePlanJson, OutputStorageKey: null, ArtifactStorageKey: null));

        return new StepExecutionContext
        {
            Execution = baseContext.Execution,
            Step = baseContext.Step,
            AllSteps = baseContext.AllSteps,
            AccumulatedOutput = baseContext.AccumulatedOutput,
            StepOutputHistory = history,
            CurrentStepIndex = baseContext.CurrentStepIndex,
            IterationCount = baseContext.IterationCount,
            CorrelationId = baseContext.CorrelationId,
            CancellationToken = baseContext.CancellationToken
        };
    }

    private static StepExecutionContext CreateMusicContext(
        VideoAnalysisArtifact artifact,
        string decisionJson,
        out Mock<IProjectFileWorkspace> workspace,
        Func<VideoCompileStepConfig, VideoCompileStepConfig>? configOverride = null,
        string musicMimeType = "audio/mpeg",
        string? musicPlanJson = null)
    {
        workspace = new Mock<IProjectFileWorkspace>();

        string artifactJson = JsonSerializer.Serialize(artifact, ArtifactOptions());

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), AnalysisKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllText(destPath, artifactJson);
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), SourceVideoKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), MusicTrackStorageKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectWorkspaceFile>
            {
                new(MusicProjectFileId, ProjectId, "music.mp3", null, "userFiles", MusicTrackStorageKey, musicMimeType, 4096, DateTime.UtcNow, null)
            });

        VideoCompileStepConfig config = new(
            Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Previous),
            AnalysisStepOrder: 1,
            EnableMusic: true,
            MusicTrackProjectFileId: MusicProjectFileId);

        if (configOverride is not null)
            config = configOverride(config);

        string configJson = JsonSerializer.Serialize(config, ConfigOptions());

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 3,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = configJson
        };

        VideoAnalyzeStepConfig analyzeConfig = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput));
        var analyzeStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(analyzeConfig, ConfigOptions())
        };

        List<StepOutputHistoryEntry> history =
        [
            new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: SourceVideoKey, ArtifactStorageKey: null),
            new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
            new StepOutputHistoryEntry(2, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null)
        ];

        if (musicPlanJson is not null)
            history.Add(new StepOutputHistoryEntry(3, "MusicSupervisor", musicPlanJson, OutputStorageKey: null, ArtifactStorageKey: null));

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = MusicExecutionId, ProjectId = ProjectId },
            Step = step,
            AllSteps = [analyzeStep, step],
            AccumulatedOutput = decisionJson,
            StepOutputHistory = history,
            CurrentStepIndex = 2,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    // =======================================================================
    // Phase 3: source-timeline -> output-timeline mapping (MapSourceToOutputSec /
    // MapSourceWindowToOutput) — the single most important correctness function in Phase 3.
    // Three kept spans, at 30fps (so SnappedStart/End equal the given seconds exactly): [0,10),
    // [20,30) (a 10s cut gap in between), [40,45) (a 10s cut gap before it).
    // Output timeline: [0,10) -> [0,10) ; [20,30) -> [10,20) ; [40,45) -> [20,25).
    // =======================================================================

    private static List<VideoCompileStepExecutor.ResolvedSpan> ThreeSpanFixture() =>
    [
        new(0.0, 10.0, 0.0, 10.0, 0, 300),
        new(20.0, 30.0, 20.0, 30.0, 600, 900),
        new(40.0, 45.0, 40.0, 45.0, 1200, 1350)
    ];

    [Fact]
    public void MapSourceToOutputSec_inside_first_span_maps_directly()
    {
        VideoCompileStepExecutor.MapSourceToOutputSec(ThreeSpanFixture(), 5.0).Should().Be(5.0);
    }

    [Fact]
    public void MapSourceToOutputSec_inside_second_span_accounts_for_first_spans_duration()
    {
        // Source 25.0 is 5s into the second span; first span contributed 10s of output already.
        VideoCompileStepExecutor.MapSourceToOutputSec(ThreeSpanFixture(), 25.0).Should().Be(15.0);
    }

    [Fact]
    public void MapSourceToOutputSec_inside_a_cut_gap_returns_null()
    {
        VideoCompileStepExecutor.MapSourceToOutputSec(ThreeSpanFixture(), 15.0).Should().BeNull();
    }

    [Fact]
    public void MapSourceToOutputSec_past_the_last_span_returns_null()
    {
        VideoCompileStepExecutor.MapSourceToOutputSec(ThreeSpanFixture(), 46.0).Should().BeNull();
    }

    [Fact]
    public void MapSourceWindowToOutput_fully_inside_span2_maps_with_correct_offset()
    {
        // [22, 27) sits fully inside the second kept span [20,30) -> output [12, 17).
        (double Start, double End)? window = VideoCompileStepExecutor.MapSourceWindowToOutput(ThreeSpanFixture(), 22.0, 27.0);
        window.Should().NotBeNull();
        window!.Value.Start.Should().BeApproximately(12.0, 1e-9);
        window.Value.End.Should().BeApproximately(17.0, 1e-9);
    }

    [Fact]
    public void MapSourceWindowToOutput_entirely_inside_a_cut_gap_returns_null()
    {
        // [12, 18) sits entirely inside the [10,20) cut gap between span 1 and span 2.
        VideoCompileStepExecutor.MapSourceWindowToOutput(ThreeSpanFixture(), 12.0, 18.0).Should().BeNull();
    }

    [Fact]
    public void MapSourceWindowToOutput_straddling_a_cut_boundary_is_clipped_to_the_kept_portion()
    {
        // [8, 25) straddles the cut gap [10,20): overlaps span 1 first ([8,10) kept portion),
        // and this implementation clips to the FIRST kept portion it overlaps — output [8,10).
        (double Start, double End)? window = VideoCompileStepExecutor.MapSourceWindowToOutput(ThreeSpanFixture(), 8.0, 25.0);
        window.Should().NotBeNull();
        window!.Value.Start.Should().BeApproximately(8.0, 1e-9);
        window.Value.End.Should().BeApproximately(10.0, 1e-9);
    }

    [Fact]
    public void MapSourceWindowToOutput_degenerate_window_returns_null()
    {
        VideoCompileStepExecutor.MapSourceWindowToOutput(ThreeSpanFixture(), 5.0, 5.0).Should().BeNull();
    }

    [Fact]
    public void MapSourceToOutputSec_accumulates_exact_frame_counts_across_many_spans_no_per_span_drift()
    {
        // 12 kept spans of varying frame-lengths at 30fps, each independently computed from an
        // exact frame count (never a naive seconds multiplication) — enough spans that a
        // systematic +1-frame-per-span accumulation error would produce a clearly wrong, linearly
        // growing offset by the last span, not just sub-frame rounding noise.
        const int fps = 30;
        int[] frameLengths = { 7, 3, 11, 5, 2, 9, 4, 6, 8, 3, 10, 5 };
        var spans = new List<VideoCompileStepExecutor.ResolvedSpan>();
        var expectedOutputStartFrame = new long[frameLengths.Length];

        long cursor = 0;
        long accumulatedFrames = 0;
        for (int i = 0; i < frameLengths.Length; i++)
        {
            long startFrame = cursor + 20; // a 20-frame cut gap before every kept span
            long endFrame = startFrame + frameLengths[i];
            double snappedStart = VideoCompileStepExecutor.FrameToSec(startFrame, fps, 1);
            double snappedEnd = VideoCompileStepExecutor.FrameToSec(endFrame, fps, 1);
            spans.Add(new VideoCompileStepExecutor.ResolvedSpan(snappedStart, snappedEnd, snappedStart, snappedEnd, startFrame, endFrame));

            expectedOutputStartFrame[i] = accumulatedFrames;
            accumulatedFrames += frameLengths[i];
            cursor = endFrame;
        }

        for (int i = 0; i < spans.Count; i++)
        {
            double expectedOutputStartSec = expectedOutputStartFrame[i] / (double)fps;
            double? actual = VideoCompileStepExecutor.MapSourceToOutputSec(spans, spans[i].SnappedStart);
            actual.Should().NotBeNull();
            actual!.Value.Should().BeApproximately(expectedOutputStartSec, 1e-9,
                $"span {i}'s output-timeline start must reflect the exact frame count of every prior span, not an off-by-one-frame-per-span drift");
        }
    }

    // =======================================================================
    // Tracked screen inserts (see docs/video-editing.md "Tracked screen inserts (Phase 5)")
    // =======================================================================

    /// <summary>A well-formed, high-confidence static insert-region track spanning [2, 8]s of shot s0 with two corner keyframes.</summary>
    private static VideoInsertRegionTrack BuildInsertTrack(
        string id = "r0", double startSec = 2.0, double endSec = 8.0, double confidence = 0.9)
        => new(
            Id: id, ShotId: "s0", StartSec: startSec, EndSec: endSec,
            Keyframes:
            [
                new VideoInsertQuadKeyframe(startSec, 0.30, 0.20, 0.60, 0.22, 0.31, 0.70, 0.61, 0.72),
                new VideoInsertQuadKeyframe(endSec, 0.35, 0.20, 0.65, 0.22, 0.36, 0.70, 0.66, 0.72)
            ],
            Confidence: confidence, MeanAreaRatio: 0.12, MeanAspectRatio: 0.6,
            MotionClass: "Slow", ColorName: "green");

    private static string BuildInsertsPlanJson(string regionId, string assetKey) =>
        JsonSerializer.Serialize(new
        {
            overlays = Array.Empty<object>(),
            inserts = new[] { new { regionId, renderedAssetStorageKey = assetKey, reason = "screen content" } },
            planRationale = "test"
        });

    private static string InsertAssetKey(string name = "screen.mp4") =>
        $"projects/{ProjectId}/outputFiles/{GraphicsExecutionId:D}/{name}";

    private static void MockAssetDownload(Mock<IProjectFileWorkspace> workspace, string assetKey) =>
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), assetKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

    [Fact]
    public async Task EnableInserts_false_produces_no_inserts_key_at_all_byte_identical_to_pre_inserts()
    {
        // The load-bearing backward-compatibility guarantee — mirrors EnableGraphics/EnableMusic
        // exactly: even with tracked regions in the artifact AND a plan containing inserts,
        // EnableInserts=false must leave the EDL/output shape completely untouched.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });
        artifact = artifact with
        {
            InsertRegions = [BuildInsertTrack()],
            OfferedInsertRegionIds = ["r0"]
        };

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateGraphicsContext(
            artifact, decisionJson, BuildInsertsPlanJson("r0", InsertAssetKey()),
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { EnableGraphics = false, EnableInserts = false });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        edl.TryGetProperty("inserts", out _).Should().BeFalse("EDL must have no inserts key when EnableInserts=false");
        using JsonDocument outputDoc = JsonDocument.Parse(result.Output);
        outputDoc.RootElement.TryGetProperty("inserts", out _).Should().BeFalse();
    }

    [Fact]
    public async Task EnableInserts_true_with_StreamCopy_fails_INSERTS_REQUIRE_REENCODE()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with
            {
                EnableInserts = true,
                Mode = VideoCompileMode.StreamCopy,
                AllowKeyframeSnapping = true
            });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("INSERTS_REQUIRE_REENCODE");
    }

    [Fact]
    public async Task Whole_source_insert_with_no_plan_and_no_content_fails_INSERT_CONTENT_MISSING()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        StepExecutionContext context = CreateContext(
            artifact, BuildDecisionJson(("s0", "s0", "keep")), out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { EnableInserts = true, KeepWholeSources = true });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("INSERT_CONTENT_MISSING");
    }

    [Fact]
    public async Task Whole_source_insert_fed_only_by_generated_ScreenContent_is_not_INSERT_CONTENT_MISSING()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        StepExecutionContext context = CreateContext(
            artifact, BuildDecisionJson(("s0", "s0", "keep")), out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { EnableInserts = true, KeepWholeSources = true, EnableGeneratedClips = true });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        if (result.Status == StepStatus.Failed)
            ErrorCode(result).Should().NotBe("INSERT_CONTENT_MISSING");
    }

    [Theory]
    [InlineData("""{"unavailable":true,"droppedInserts":[]}""", "INSERT_FILTER_UNAVAILABLE")]
    [InlineData("""{"droppedInserts":[{"regionId":"r0","reason":"content_not_found"}]}""", "INSERT_CONTENT_UNUSABLE")]
    [InlineData("""{"droppedInserts":[{"regionId":"r0","reason":"confidence_below_threshold"}]}""", "INSERT_NOT_APPLIED")]
    [InlineData("""{"droppedInserts":[]}""", "INSERT_NOT_APPLIED")]
    public void The_not_applied_code_names_the_real_cause(string insertsJson, string expected)
        => VideoCompileStepExecutor.InsertNotAppliedCode((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(insertsJson)!).Should().Be(expected);

    [Theory]
    [InlineData(true, false, "INSERT_TRACKING_FAILED")]
    [InlineData(false, false, "INSERT_NOT_APPLIED")]
    [InlineData(true, true, "INSERT_NOT_APPLIED")]
    public void A_degraded_tracker_is_our_failure_not_a_clip_with_no_screen(bool degraded, bool applied, string expected)
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        artifact = artifact with { Provenance = artifact.Provenance with { InsertTrackingDegraded = degraded, InsertTrackingApplied = applied } };

        string code = VideoCompileStepExecutor.InsertNotAppliedCode(
            (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""{"droppedInserts":[]}""")!, artifact);

        code.Should().Be(expected);
        if (degraded && !applied)
            ReelBolt.WorkflowEngine.Services.Billing.FailureClassifier.ForCode(code).Should().Be(FailureClass.PlatformInternal);
        // Filter-unavailable still wins over a degraded tracker.
        VideoCompileStepExecutor.InsertNotAppliedCode(
            (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""{"unavailable":true}""")!, artifact).Should().Be("INSERT_FILTER_UNAVAILABLE");
    }

    [Fact]
    public async Task The_briefs_insert_content_file_reaches_the_insert_and_with_no_screen_found_fails_INSERT_NOT_APPLIED()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        StepExecutionContext context = CreateContext(
            artifact, BuildDecisionJson(("s0", "s0", "keep")), out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { EnableInserts = true, KeepWholeSources = true });
        var brief = new RunBriefState(new StoredRunBrief(
            SourceFiles: [new RunBriefSourceFiles(RunBriefFileRole.InsertContent, [Guid.NewGuid()])]));

        StepExecutionResult result;
        using (RunBriefAmbient.Enter(brief))
            result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("INSERT_NOT_APPLIED");
    }

    [Fact]
    public async Task MixInsertAudio_without_EnableInserts_fails_INSERT_AUDIO_REQUIRES_INSERTS()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { MixInsertAudio = true });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("INSERT_AUDIO_REQUIRES_INSERTS");
    }

    [Fact]
    public async Task MixInsertAudio_with_AudioCodec_copy_fails_INSERT_AUDIO_REQUIRES_AUDIO_REENCODE()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with
            {
                EnableInserts = true,
                MixInsertAudio = true,
                AudioCodec = "copy"
            });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("INSERT_AUDIO_REQUIRES_AUDIO_REENCODE");
    }

    [Theory]
    [InlineData(-25)]
    [InlineData(13)]
    public async Task MixInsertAudio_with_an_out_of_range_gain_fails_INSERT_AUDIO_GAIN_OUT_OF_RANGE(int gainDb)
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with
            {
                EnableInserts = true,
                MixInsertAudio = true,
                InsertAudioGainDb = gainDb
            });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("INSERT_AUDIO_GAIN_OUT_OF_RANGE");
    }

    [Fact]
    public async Task Keep_span_naming_an_insert_region_id_fails_UNKNOWN_ID_since_regions_are_a_separate_namespace()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });
        artifact = artifact with
        {
            InsertRegions = [BuildInsertTrack()],
            OfferedInsertRegionIds = ["r0"]
        };

        // A Keep span naming "r0" — an insert-region id, not a cut-anchor id — must fail
        // UNKNOWN_ID exactly like a placement/music/look id does (BuildIdTimeIndex excludes it).
        string decisionJson = BuildDecisionJson(("r0", "r0", "wrong namespace"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("UNKNOWN_ID");
    }

    [Fact]
    public async Task Insert_naming_unknown_region_id_is_dropped_not_a_step_failure()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });
        artifact = artifact with
        {
            InsertRegions = [BuildInsertTrack()],
            OfferedInsertRegionIds = ["r0"]
        };

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateGraphicsContext(
            artifact, decisionJson, BuildInsertsPlanJson("r99", InsertAssetKey()),
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { EnableGraphics = false, EnableInserts = true });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement inserts = edl.GetProperty("inserts");
        inserts.GetProperty("applied").GetBoolean().Should().BeFalse();
        JsonElement dropped = inserts.GetProperty("droppedInserts");
        dropped.GetArrayLength().Should().Be(1);
        dropped[0].GetProperty("regionId").GetString().Should().Be("r99");
        dropped[0].GetProperty("reason").GetString().Should().Be("unknown_region_id");
    }

    [Fact]
    public async Task Insert_on_a_tracked_but_unoffered_region_is_dropped_offered_is_stricter_than_exists()
    {
        // The region EXISTS in the artifact but was never OFFERED (e.g. suppressed for view
        // budget) — same "offered is a stricter check than exists" discipline as Keep-span ids.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });
        artifact = artifact with
        {
            InsertRegions = [BuildInsertTrack()],
            OfferedInsertRegionIds = []
        };

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateGraphicsContext(
            artifact, decisionJson, BuildInsertsPlanJson("r0", InsertAssetKey()),
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { EnableGraphics = false, EnableInserts = true });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement dropped = edl.GetProperty("inserts").GetProperty("droppedInserts");
        dropped.GetArrayLength().Should().Be(1);
        dropped[0].GetProperty("reason").GetString().Should().Be("unknown_region_id");
    }

    [Fact]
    public async Task Insert_on_low_confidence_region_is_dropped_confidence_below_threshold()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });
        artifact = artifact with
        {
            InsertRegions = [BuildInsertTrack(confidence: 0.3)],
            OfferedInsertRegionIds = ["r0"]
        };

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateGraphicsContext(
            artifact, decisionJson, BuildInsertsPlanJson("r0", InsertAssetKey()),
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { EnableGraphics = false, EnableInserts = true });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement dropped = edl.GetProperty("inserts").GetProperty("droppedInserts");
        dropped.GetArrayLength().Should().Be(1);
        dropped[0].GetProperty("reason").GetString().Should().Be("confidence_below_threshold");
    }

    [Fact]
    public async Task Insert_whose_region_window_is_entirely_cut_away_is_dropped()
    {
        // Track spans [12, 18]s of s1, but only s0 = [0, 10] is kept — the whole window falls in
        // the cut region, so this insert must drop as cut_away, never fail the compile.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0) },
            offeredIds: new[] { "s0", "s1" });
        artifact = artifact with
        {
            InsertRegions = [BuildInsertTrack(startSec: 12.0, endSec: 18.0)],
            OfferedInsertRegionIds = ["r0"]
        };

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateGraphicsContext(
            artifact, decisionJson, BuildInsertsPlanJson("r0", InsertAssetKey()),
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { EnableGraphics = false, EnableInserts = true, PrePaddingMs = 0, PostPaddingMs = 0 });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement dropped = edl.GetProperty("inserts").GetProperty("droppedInserts");
        dropped.GetArrayLength().Should().Be(1);
        dropped[0].GetProperty("reason").GetString().Should().Be("cut_away");
    }

    [Fact]
    public async Task Insert_with_asset_key_outside_this_executions_prefix_is_dropped()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });
        artifact = artifact with
        {
            InsertRegions = [BuildInsertTrack()],
            OfferedInsertRegionIds = ["r0"]
        };

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string foreignKey = $"projects/{ProjectId}/outputFiles/{Guid.NewGuid():D}/other.mp4";
        StepExecutionContext context = CreateGraphicsContext(
            artifact, decisionJson, BuildInsertsPlanJson("r0", foreignKey),
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { EnableGraphics = false, EnableInserts = true });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        // THE security property, unchanged: the key as the model named it is never followed.
        workspace.Verify(
            w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), foreignKey, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a storage key naming another execution must never be downloaded as named");

        // Re-anchored rather than dropped — see the overlay twin above and
        // NormalizeRenderedAssetKey for why that is the narrower behaviour, not the looser one.
        string expectedPrefix = $"projects/{ProjectId}/outputFiles/{GraphicsExecutionId:D}/";
        workspace.Verify(
            w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(),
                It.Is<string>(k => k.StartsWith(expectedPrefix, StringComparison.Ordinal) && k.EndsWith("other.mp4", StringComparison.Ordinal)),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the file name is kept but re-anchored inside this execution's own prefix");
    }

    [Fact]
    public async Task Perspective_filters_unavailable_skips_all_inserts_but_still_compiles()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });
        artifact = artifact with
        {
            InsertRegions = [BuildInsertTrack()],
            OfferedInsertRegionIds = ["r0"]
        };

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateGraphicsContext(
            artifact, decisionJson, BuildInsertsPlanJson("r0", InsertAssetKey()),
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { EnableGraphics = false, EnableInserts = true });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e, insertFiltersAvailable: false).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, "a missing filter must never fail the compile — the cut is the primary deliverable");
        JsonElement inserts = edl.GetProperty("inserts");
        inserts.GetProperty("applied").GetBoolean().Should().BeFalse();
        inserts.GetProperty("unavailable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Applied_insert_adds_its_own_input_and_an_animated_perspective_alphamerge_filtergraph()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });
        artifact = artifact with
        {
            InsertRegions = [BuildInsertTrack()],
            OfferedInsertRegionIds = ["r0"]
        };

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string assetKey = InsertAssetKey();
        StepExecutionContext context = CreateGraphicsContext(
            artifact, decisionJson, BuildInsertsPlanJson("r0", assetKey),
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { EnableGraphics = false, EnableInserts = true, PrePaddingMs = 0, PostPaddingMs = 0 });
        MockAssetDownload(workspace, assetKey);

        JsonElement edl = default;
        List<string>? capturedArgs = null;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e, ffmpegArgsCaptured: a => capturedArgs = a.ToList())
            .ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement inserts = edl.GetProperty("inserts");
        inserts.GetProperty("applied").GetBoolean().Should().BeTrue();
        inserts.GetProperty("appliedInsertCount").GetInt32().Should().Be(1);
        inserts.GetProperty("droppedInserts").GetArrayLength().Should().Be(0);

        capturedArgs.Should().NotBeNull();
        string filterComplex = ExtractFilterComplexValue(capturedArgs!);
        filterComplex.Should().Contain("perspective=", "the insert must be corner-pinned via the perspective filter");
        filterComplex.Should().Contain("sense=destination:eval=frame", "corners animate per-frame toward the tracked quad");
        filterComplex.Should().Contain("alphamerge", "the warped white-plate mask is what keeps the outside of the quad transparent");
        filterComplex.Should().MatchRegex(@"\[sia0\]overlay=x=\d+:y=\d+:eof_action=pass", "the warped, masked content overlays the base at its warp window's integer origin");
        capturedArgs!.Count(a => a == "-i").Should().Be(2, "the main source video AND the insert asset must each be their own -i input");
    }

    [Fact]
    public async Task A_one_keyframe_insert_track_under_a_reframe_still_animates_its_perspective_corners()
    {
        // QA-P1-13 through the WHOLE compile: the tracker sampled this plate exactly once, so if the
        // compile did not also sample the reframe's own knots the insert would be one constant quad
        // (`perspective` with no interpolation term) while the picture zooms out from under it.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });
        artifact = artifact with
        {
            InsertRegions = [BuildInsertTrack() with { Keyframes = [new VideoInsertQuadKeyframe(2.0, 0.30, 0.20, 0.60, 0.22, 0.31, 0.70, 0.61, 0.72)] }],
            OfferedInsertRegionIds = ["r0"]
        };
        IReadOnlyList<VideoReframe> reframes = VideoReframe.Validate(
            [new VideoReframe(0, 0, 10, [new ReframeKeyframe(0, 2), new ReframeKeyframe(4, 1)])]).Normalized;

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string assetKey = InsertAssetKey();
        StepExecutionContext context = CreateGraphicsContext(
            artifact, decisionJson, BuildInsertsPlanJson("r0", assetKey),
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with
            {
                EnableGraphics = false,
                EnableInserts = true,
                PrePaddingMs = 0,
                PostPaddingMs = 0,
                Reframes = reframes
            });
        MockAssetDownload(workspace, assetKey);

        List<string>? capturedArgs = null;
        string? filterGraph = null;
        StepExecutionResult result = await CreateExecutor(
            workspace, ffmpegArgsCaptured: a =>
            {
                if (!a.Contains("-map"))
                    return;
                capturedArgs = a.ToList();
                filterGraph ??= ExtractFilterGraph(a.ToList());
            })
            .ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        capturedArgs.Should().NotBeNull();
        string filterComplex = filterGraph!;
        int start = filterComplex.IndexOf("perspective=", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        string perspective = filterComplex[start..filterComplex.IndexOf(":sense=destination", start, StringComparison.Ordinal)];
        perspective.Should().Contain("if(lt(in,",
            "the reframe's own knot must be a perspective keyframe, or the insert is a fixed quad under a moving picture");
    }

    // ---------------------------------------------------------------------
    // Inserting an existing project video (docs/video-editing.md "Inserting an existing video").
    // ---------------------------------------------------------------------

    private static ProjectWorkspaceFile ContentFile(Guid id, string mime = "video/mp4") =>
        new(id, ProjectId, "promo.mp4", null, "userFiles",
            $"projects/{ProjectId}/userFiles/{id}.mp4", mime, 1024, DateTime.UtcNow, null);

    [Fact]
    public async Task Censoring_fails_closed_when_object_detection_did_not_run_cleanly()
    {
        // No Vision provider / failed lookups upstream: "no plate matched" then means "unknown",
        // and producing the video would publish exactly what was meant to be hidden.
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        artifact = artifact with
        {
            Provenance = artifact.Provenance with
            {
                ObjectTrackingApplied = true,
                ObjectTrackingDegraded = true,
                ObjectTrackingReason = "a vision lookup failed",
                ObjectTrackingLabels = ["licence plate"]
            }
        };
        StepExecutionContext context = CreateGraphicsContext(
            artifact, BuildDecisionJson(("s0", "s0", "keep")), BuildInsertsPlanJson("r0", InsertAssetKey()),
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { GraphicsPlan = null, EnableGraphics = false, CensorLabels = ["licence plate"] });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().Contain("did not run cleanly").And.Contain("a vision lookup failed");
    }

    [Theory]
    [InlineData(false, null, "did not track any objects")]
    [InlineData(true, "license plate", "never looked for that label")]
    public async Task Censoring_fails_closed_when_the_analyze_step_never_looked_for_the_label(
        bool applied, string? searched, string expected)
    {
        // "Nothing matched" only means "nothing there" when the label was actually searched for.
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        artifact = artifact with
        {
            Provenance = artifact.Provenance with
            {
                ObjectTrackingApplied = applied,
                ObjectTrackingLabels = searched is null ? null : [searched]
            }
        };
        StepExecutionContext context = CreateGraphicsContext(
            artifact, BuildDecisionJson(("s0", "s0", "keep")), BuildInsertsPlanJson("r0", InsertAssetKey()),
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { GraphicsPlan = null, EnableGraphics = false, CensorLabels = ["licence plate"] });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().Contain(expected);
    }

    [Fact]
    public async Task KeepWholeSources_does_not_need_a_decision_at_all()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        StepExecutionContext context = CreateGraphicsContext(
            artifact, "{}", BuildInsertsPlanJson("r0", InsertAssetKey()),
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { GraphicsPlan = null, EnableGraphics = false, KeepWholeSources = true, Decision = null! });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
    }

    [Fact]
    public async Task KeepWholeSources_compiles_every_source_uncut_without_resolving_a_decision()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 4.0), ("s1", 4.0, 10.0) }, offeredIds: new[] { "s1" });
        StepExecutionContext context = CreateGraphicsContext(
            artifact, "not even json", BuildInsertsPlanJson("r0", InsertAssetKey()),
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with
            {
                GraphicsPlan = null,
                EnableGraphics = false,
                KeepWholeSources = true,
                PrePaddingMs = 0,
                PostPaddingMs = 0
            });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        edl.GetProperty("retainedRatio").GetDouble().Should().BeApproximately(1.0, 0.01,
            "both shots are kept, including s0, which was never offered to an agent");
    }

    [Fact]
    public async Task InsertContentProjectFileId_composites_a_project_video_with_no_plan_at_all()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        artifact = artifact with { InsertRegions = [BuildInsertTrack()], OfferedInsertRegionIds = ["r0"] };

        Guid fileId = Guid.NewGuid();
        ProjectWorkspaceFile file = ContentFile(fileId);
        StepExecutionContext context = CreateGraphicsContext(
            artifact, BuildDecisionJson(("s0", "s0", "keep")), BuildInsertsPlanJson("r0", InsertAssetKey()),
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with
            {
                GraphicsPlan = null,
                EnableGraphics = false,
                EnableInserts = true,
                InsertContentProjectFileId = fileId,
                PrePaddingMs = 0,
                PostPaddingMs = 0
            });
        workspace.Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([file]);
        MockAssetDownload(workspace, file.StorageKey);

        JsonElement edl = default;
        List<string>? capturedArgs = null;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e, ffmpegArgsCaptured: a => capturedArgs = a.ToList())
            .ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement applied = edl.GetProperty("inserts").GetProperty("appliedInserts")[0];
        applied.GetProperty("regionId").GetString().Should().Be("r0");
        applied.GetProperty("contentOrigin").GetString().Should().Be("config");
        applied.GetProperty("contentProjectFileId").GetString().Should().Be(fileId.ToString());
        capturedArgs!.Count(a => a == "-i").Should().Be(2, "the source and the project video");
        workspace.Verify(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), file.StorageKey, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_planned_insert_may_name_a_project_file_by_id()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        artifact = artifact with { InsertRegions = [BuildInsertTrack()], OfferedInsertRegionIds = ["r0"] };

        Guid fileId = Guid.NewGuid();
        ProjectWorkspaceFile file = ContentFile(fileId);
        StepExecutionContext context = CreateGraphicsContext(
            artifact, BuildDecisionJson(("s0", "s0", "keep")), BuildInsertsPlanJson("r0", fileId.ToString()),
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { EnableGraphics = false, EnableInserts = true, PrePaddingMs = 0, PostPaddingMs = 0 });
        workspace.Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([file]);
        MockAssetDownload(workspace, file.StorageKey);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement applied = edl.GetProperty("inserts").GetProperty("appliedInserts")[0];
        applied.GetProperty("contentOrigin").GetString().Should().Be("plan");
        applied.GetProperty("contentProjectFileId").GetString().Should().Be(fileId.ToString());
    }

    [Fact]
    public async Task Insert_content_that_is_not_video_or_image_is_dropped()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        artifact = artifact with { InsertRegions = [BuildInsertTrack()], OfferedInsertRegionIds = ["r0"] };

        Guid fileId = Guid.NewGuid();
        StepExecutionContext context = CreateGraphicsContext(
            artifact, BuildDecisionJson(("s0", "s0", "keep")), BuildInsertsPlanJson("r0", InsertAssetKey()),
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with
            {
                GraphicsPlan = null,
                EnableGraphics = false,
                EnableInserts = true,
                InsertContentProjectFileId = fileId
            });
        workspace.Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([ContentFile(fileId, mime: "audio/mpeg")]);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement inserts = edl.GetProperty("inserts");
        inserts.GetProperty("appliedInsertCount").GetInt32().Should().Be(0);
        inserts.GetProperty("droppedInserts")[0].GetProperty("reason").GetString().Should().Be("content_not_video_or_image");
    }

    [Fact]
    public async Task Insert_content_from_another_project_is_dropped()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        artifact = artifact with { InsertRegions = [BuildInsertTrack()], OfferedInsertRegionIds = ["r0"] };

        StepExecutionContext context = CreateGraphicsContext(
            artifact, BuildDecisionJson(("s0", "s0", "keep")), BuildInsertsPlanJson("r0", InsertAssetKey()),
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with
            {
                GraphicsPlan = null,
                EnableGraphics = false,
                EnableInserts = true,
                InsertContentProjectFileId = Guid.NewGuid()
            });
        workspace.Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        edl.GetProperty("inserts").GetProperty("droppedInserts")[0].GetProperty("reason").GetString()
            .Should().Be("content_not_in_project");
    }

    // ---------------------------------------------------------------------
    // T-106: insert content by reference (Step / StepResult / ProjectFile).
    // ---------------------------------------------------------------------

    private static string PriorRenderKey(string name = "prior.mp4") =>
        $"projects/{ProjectId}/outputFiles/{Guid.NewGuid():D}/{name}";

    /// <summary>
    /// A compile with a tracked plate and NO plan, whose insert content is named by reference. The engine
    /// database holds one step result owned by <paramref name="ownerProjectId"/> with <paramref name="resultKey"/>;
    /// the run's own history carries one extra step (order 5) producing <paramref name="stepOutputKey"/>.
    /// </summary>
    private static (StepExecutionContext Context, Mock<IProjectFileWorkspace> Workspace, IServiceScopeFactory Scopes) InsertByRefContext(
        Func<Guid, VideoCompileStepConfig, VideoCompileStepConfig> configure,
        Guid? ownerProjectId = null, string? resultKey = null, string? stepOutputKey = null, params ProjectWorkspaceFile[] files)
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        artifact = artifact with { InsertRegions = [BuildInsertTrack()], OfferedInsertRegionIds = ["r0"] };

        WorkflowEngineDbContext db = NewProvenanceDbContext();
        WorkflowExecution earlier = new() { Id = Guid.NewGuid(), ProjectId = ownerProjectId ?? ProjectId };
        WorkflowStepResult stepResult = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = earlier.Id,
            WorkflowExecution = earlier,
            OutputStorageKey = resultKey,
            Status = StepStatus.Completed
        };
        db.WorkflowExecutions.Add(earlier);
        db.WorkflowStepResults.Add(stepResult);
        db.SaveChanges();

        StepExecutionContext context = CreateGraphicsContext(
            artifact, BuildDecisionJson(("s0", "s0", "keep")), "{}",
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => configure(stepResult.Id, cfg with
            {
                GraphicsPlan = null,
                EnableGraphics = false,
                EnableInserts = true,
                PrePaddingMs = 0,
                PostPaddingMs = 0
            }));
        ((List<StepOutputHistoryEntry>)context.StepOutputHistory)
            .Add(new StepOutputHistoryEntry(5, "Render", "{}", OutputStorageKey: stepOutputKey));
        workspace.Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(files);
        foreach (string key in new[] { resultKey, stepOutputKey }.OfType<string>().Concat(files.Select(f => f.StorageKey)))
            MockAssetDownload(workspace, key);
        return (context, workspace, ScopeFactoryFor(db, resolver: null));
    }

    private static async Task<(StepExecutionResult Result, JsonElement Inserts)> RunInsertByRefAsync(
        (StepExecutionContext Context, Mock<IProjectFileWorkspace> Workspace, IServiceScopeFactory Scopes) fixture)
    {
        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(fixture.Workspace, edlCaptured: e => edl = e, scopeFactory: fixture.Scopes)
            .ExecuteAsync(fixture.Context);
        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        return (result, edl.GetProperty("inserts"));
    }

    private static void ShouldHaveApplied(JsonElement inserts, string key, Mock<IProjectFileWorkspace> workspace)
    {
        inserts.GetProperty("appliedInserts")[0].GetProperty("contentOrigin").GetString().Should().Be("config");
        inserts.GetProperty("appliedInserts")[0].GetProperty("contentProjectFileId").ValueKind.Should().Be(JsonValueKind.Null,
            "a reference names a stored render, not a project file");
        workspace.Verify(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), key, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InsertContentRef_Step_composites_an_earlier_steps_output_of_this_run()
    {
        string key = PriorRenderKey();
        var fixture = InsertByRefContext(
            (_, cfg) => cfg with { InsertContentRef = new InsertContentRef("Step", StepOrder: 5) }, stepOutputKey: key);

        (_, JsonElement inserts) = await RunInsertByRefAsync(fixture);

        ShouldHaveApplied(inserts, key, fixture.Workspace);
    }

    [Fact]
    public async Task InsertContentRef_StepResult_composites_a_same_project_render_of_an_earlier_run()
    {
        string key = PriorRenderKey("earlier-run.mp4");
        var fixture = InsertByRefContext(
            (id, cfg) => cfg with { InsertContentRef = new InsertContentRef("StepResult", StepResultId: id) }, resultKey: key);

        (_, JsonElement inserts) = await RunInsertByRefAsync(fixture);

        ShouldHaveApplied(inserts, key, fixture.Workspace);
    }

    [Fact]
    public async Task InsertContentRef_ProjectFile_behaves_like_InsertContentProjectFileId()
    {
        Guid fileId = Guid.NewGuid();
        ProjectWorkspaceFile file = ContentFile(fileId);
        var fixture = InsertByRefContext(
            (_, cfg) => cfg with { InsertContentRef = new InsertContentRef("ProjectFile", ProjectFileId: fileId) }, files: file);

        (_, JsonElement inserts) = await RunInsertByRefAsync(fixture);

        inserts.GetProperty("appliedInserts")[0].GetProperty("contentProjectFileId").GetString().Should().Be(fileId.ToString());
    }

    [Fact]
    public async Task An_InsertContents_entry_may_carry_its_own_reference()
    {
        string key = PriorRenderKey();
        var fixture = InsertByRefContext(
            (_, cfg) => cfg with
            {
                InsertContents = [new VideoInsertContentAssignment(Guid.Empty, RegionId: "r0", InsertContentRef: new InsertContentRef("Step", StepOrder: 5))]
            },
            stepOutputKey: key);

        (_, JsonElement inserts) = await RunInsertByRefAsync(fixture);

        ShouldHaveApplied(inserts, key, fixture.Workspace);
    }

    [Theory]
    [InlineData("sibling-project")]
    [InlineData("missing")]
    [InlineData("pruned")]
    [InlineData("outside-outputFiles")]
    public async Task A_foreign_or_missing_StepResult_reference_is_dropped_as_content_not_found_and_never_downloaded(string variant)
    {
        Guid sibling = Guid.NewGuid();
        (Guid owner, string? key) = variant switch
        {
            "sibling-project" => (sibling, $"projects/{sibling}/outputFiles/{Guid.NewGuid():D}/other.mp4"),
            "pruned" => (ProjectId, null),
            "outside-outputFiles" => (ProjectId, $"projects/{ProjectId}/userFiles/other.mp4"),
            _ => (ProjectId, PriorRenderKey())
        };
        var fixture = InsertByRefContext(
            (id, cfg) => cfg with
            {
                InsertContentRef = new InsertContentRef("StepResult", StepResultId: variant == "missing" ? Guid.NewGuid() : id)
            },
            ownerProjectId: owner, resultKey: key);

        (_, JsonElement inserts) = await RunInsertByRefAsync(fixture);

        inserts.GetProperty("appliedInsertCount").GetInt32().Should().Be(0);
        inserts.GetProperty("droppedInserts")[0].GetProperty("reason").GetString().Should().Be("content_not_found");
        fixture.Workspace.Verify(w => w.DownloadStorageKeyToFileAsync(
            It.IsAny<Guid>(), It.Is<string>(k => k.Contains("other.mp4") || k == key), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(9)]   // no such step in this run
    [InlineData(5)]   // the step exists but produced a key of another project
    public async Task A_Step_reference_that_does_not_resolve_inside_the_project_is_dropped_as_content_not_found(int stepOrder)
    {
        var fixture = InsertByRefContext(
            (_, cfg) => cfg with { InsertContentRef = new InsertContentRef("Step", StepOrder: stepOrder) },
            stepOutputKey: $"projects/{Guid.NewGuid()}/outputFiles/{Guid.NewGuid():D}/foreign.mp4");

        (_, JsonElement inserts) = await RunInsertByRefAsync(fixture);

        inserts.GetProperty("droppedInserts")[0].GetProperty("reason").GetString().Should().Be("content_not_found");
        fixture.Workspace.Verify(w => w.DownloadStorageKeyToFileAsync(
            It.IsAny<Guid>(), It.Is<string>(k => k.Contains("foreign.mp4")), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_reference_to_something_that_is_not_video_or_image_is_dropped_as_content_not_video_or_image()
    {
        var fixture = InsertByRefContext(
            (_, cfg) => cfg with { InsertContentRef = new InsertContentRef("Step", StepOrder: 5) },
            stepOutputKey: PriorRenderKey("narration.mp3"));

        (_, JsonElement inserts) = await RunInsertByRefAsync(fixture);

        inserts.GetProperty("droppedInserts")[0].GetProperty("reason").GetString().Should().Be("content_not_video_or_image");
    }

    [Fact]
    public async Task A_malformed_reference_is_dropped_as_content_not_found()
    {
        var fixture = InsertByRefContext(
            (_, cfg) => cfg with { InsertContentRef = new InsertContentRef("Bogus", StepOrder: 5) },
            stepOutputKey: PriorRenderKey());

        (_, JsonElement inserts) = await RunInsertByRefAsync(fixture);

        inserts.GetProperty("droppedInserts")[0].GetProperty("reason").GetString().Should().Be("content_not_found");
    }

    // ---------------------------------------------------------------------
    // Non-planar inserts — see docs/video-editing.md "Non-planar surfaces and
    // multi-plate compositing". The decision itself (mesh or flat) and the
    // guarantee that a flat plate is untouched by the addition.
    // ---------------------------------------------------------------------

    /// <summary>The same track shape as <see cref="BuildInsertTrack"/>, but with a measured bow on its top and bottom edges.</summary>
    private static VideoInsertRegionTrack BuildCurvedInsertTrack(
        string id = "r0", double startSec = 2.0, double endSec = 8.0, double bow = 0.09)
    {
        var bows = new VideoInsertEdgeBows(bow, 0, 0, 0, bow, 0, 0, 0);
        return BuildInsertTrack(id, startSec, endSec) with
        {
            MeanCurvature = bow,
            Keyframes =
            [
                new VideoInsertQuadKeyframe(startSec, 0.30, 0.20, 0.60, 0.22, 0.31, 0.70, 0.61, 0.72, bows),
                new VideoInsertQuadKeyframe(endSec, 0.35, 0.20, 0.65, 0.22, 0.36, 0.70, 0.66, 0.72, bows)
            ]
        };
    }

    private static List<(long Frame, VideoInsertQuadKeyframe K)> SampleOf(VideoInsertRegionTrack track)
    {
        var spans = new List<VideoCompileStepExecutor.ResolvedSpan> { new(0, 10, 0, 10, 0, 300) };
        OutputTimeline timeline = OutputTimeline.Build(spans, VideoCompileStepExecutor.BuildHardCutSeamsForFallback(spans.Count));
        return VideoCompileStepExecutor.SampleInsertKeyframes(
            track, timeline, new VideoAnalysisMedia(10, 30, 1, 1920, 1080), (2.0, 8.0), maxKeyframes: 96);
    }

    [Fact]
    public void BuildInsertMeshPlan_returns_null_for_a_plate_with_no_measured_bow()
    {
        // A flat plate must produce NO mesh even when the mesh path is asked for, so that the
        // original single-perspective filter string is what actually reaches ffmpeg.
        VideoCompileStepExecutor.BuildInsertMeshPlan(
            SampleOf(BuildInsertTrack()), new VideoAnalysisMedia(10, 30, 1, 1920, 1080),
            overscan: 0, tolerancePx: 0.6, maxCells: 24)
            .Should().BeNull();
    }

    [Fact]
    public void BuildInsertMeshPlan_subdivides_along_the_axis_the_measured_bow_needs()
    {
        InsertMeshPlan? plan = VideoCompileStepExecutor.BuildInsertMeshPlan(
            SampleOf(BuildCurvedInsertTrack()), new VideoAnalysisMedia(10, 30, 1, 1920, 1080),
            overscan: 0, tolerancePx: 0.6, maxCells: 24);

        plan.Should().NotBeNull();
        plan!.Cols.Should().BeGreaterThan(1, "a top/bottom bow is resolved by columns");
        plan.Rows.Should().Be(1, "the side edges of this plate measured straight, so rows resolve nothing");
        (plan.Rows * plan.Cols).Should().BeLessThanOrEqualTo(24);
        plan.Keyframes.Should().HaveCount(2);
        plan.Keyframes.Should().AllSatisfy(k =>
        {
            k.ControlPoints.Should().HaveCount(plan.Rows + 1);
            k.ControlPoints[0].Should().HaveCount(plan.Cols + 1);
        });
    }

    [Fact]
    public void BuildInsertMeshPlan_bows_the_interior_but_pins_the_tracked_corners()
    {
        InsertMeshPlan plan = VideoCompileStepExecutor.BuildInsertMeshPlan(
            SampleOf(BuildCurvedInsertTrack()), new VideoAnalysisMedia(10, 30, 1, 1920, 1080),
            overscan: 0, tolerancePx: 0.6, maxCells: 24)!;

        (double X, double Y)[][] grid = plan.Keyframes[0].ControlPoints;
        grid[0][0].X.Should().BeApproximately(0.30 * 1920, 1e-6, "the tracked TL corner is untouched by the bow");
        grid[0][^1].X.Should().BeApproximately(0.60 * 1920, 1e-6);

        // The middle of the top edge must sit BELOW the straight chord between its corners.
        double chordMidY = (grid[0][0].Y + grid[0][^1].Y) / 2;
        grid[0][plan.Cols / 2].Y.Should().BeGreaterThan(chordMidY + 2,
            "a positive top bow pulls the edge's middle inward, toward the plate");
    }

    [Fact]
    public void BuildInsertMeshPlan_scales_the_whole_surface_with_the_overscan()
    {
        InsertMeshPlan plain = VideoCompileStepExecutor.BuildInsertMeshPlan(
            SampleOf(BuildCurvedInsertTrack()), new VideoAnalysisMedia(10, 30, 1, 1920, 1080),
            overscan: 0, tolerancePx: 0.6, maxCells: 24)!;
        InsertMeshPlan overscanned = VideoCompileStepExecutor.BuildInsertMeshPlan(
            SampleOf(BuildCurvedInsertTrack()), new VideoAnalysisMedia(10, 30, 1, 1920, 1080),
            overscan: 0.10, tolerancePx: 0.6, maxCells: 24)!;

        // The bows are fractions of the across-vectors, so they scale with the corners and the
        // whole curved surface expands uniformly — exactly what the flat path's overscan does.
        plain.Cols.Should().Be(overscanned.Cols);
        overscanned.Keyframes[0].ControlPoints[0][0].X
            .Should().BeLessThan(plain.Keyframes[0].ControlPoints[0][0].X, "the TL corner moves outward");
        overscanned.Keyframes[0].ControlPoints[0][^1].X
            .Should().BeGreaterThan(plain.Keyframes[0].ControlPoints[0][^1].X);
    }

    [Fact]
    public void MedianBows_ignores_a_single_badly_read_frame()
    {
        var good = new InsertPlateBows(new InsertEdgeBow(0.05, 0), InsertEdgeBow.Flat, new InsertEdgeBow(0.05, 0), InsertEdgeBow.Flat);
        var rogue = new InsertPlateBows(new InsertEdgeBow(0.20, 0), InsertEdgeBow.Flat, new InsertEdgeBow(-0.20, 0), InsertEdgeBow.Flat);

        InsertPlateBows median = VideoCompileStepExecutor.MedianBows([good, good, rogue, good, good]);

        median.Top.Bow.Should().BeApproximately(0.05, 1e-9, "one clipped frame must not bend the whole surface");
        median.Bottom.Bow.Should().BeApproximately(0.05, 1e-9);
    }

    [Fact]
    public async Task A_curved_plate_composites_through_a_mesh_and_says_so_in_the_EDL()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });
        artifact = artifact with
        {
            InsertRegions = [BuildCurvedInsertTrack()],
            OfferedInsertRegionIds = ["r0"]
        };

        string assetKey = InsertAssetKey();
        StepExecutionContext context = CreateGraphicsContext(
            artifact, BuildDecisionJson(("s0", "s0", "keep")), BuildInsertsPlanJson("r0", assetKey),
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { EnableGraphics = false, EnableInserts = true, PrePaddingMs = 0, PostPaddingMs = 0 });
        MockAssetDownload(workspace, assetKey);

        JsonElement edl = default;
        List<string>? capturedArgs = null;
        string? filterGraph = null;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e,
            ffmpegArgsCaptured: a => { capturedArgs = a.ToList(); filterGraph ??= ExtractFilterGraph(capturedArgs); })
            .ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement applied = edl.GetProperty("inserts").GetProperty("appliedInserts")[0];
        applied.GetProperty("surface").GetString().Should().Be("mesh");
        applied.GetProperty("meshCols").GetInt32().Should().BeGreaterThan(1);

        filterGraph.Should().NotBeNull();
        filterGraph!.Should().Contain("split=", "one branch per mesh cell");
        filterGraph.Should().Contain("crop=w='floor(iw*", "each cell takes its own sub-rectangle of the asset");
        filterGraph.Should().NotContain("overlay=x=0:y=0",
            "a mesh cell overlays at its own canvas origin, never at the frame origin like the flat path");
        capturedArgs!.Count(a => a == "-i").Should().Be(2, "a mesh insert is still ONE extra input, however many cells it has");
    }

    [Fact]
    public async Task A_FLAT_plate_still_composites_through_the_original_single_perspective_path()
    {
        // The backward-compatibility guarantee: nothing about this addition may change what a
        // flat plate produces.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });
        artifact = artifact with { InsertRegions = [BuildInsertTrack()], OfferedInsertRegionIds = ["r0"] };

        string assetKey = InsertAssetKey();
        StepExecutionContext context = CreateGraphicsContext(
            artifact, BuildDecisionJson(("s0", "s0", "keep")), BuildInsertsPlanJson("r0", assetKey),
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { EnableGraphics = false, EnableInserts = true, PrePaddingMs = 0, PostPaddingMs = 0 });
        MockAssetDownload(workspace, assetKey);

        JsonElement edl = default;
        string? filterGraph = null;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e,
            ffmpegArgsCaptured: a => filterGraph ??= ExtractFilterGraph(a.ToList()))
            .ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        edl.GetProperty("inserts").GetProperty("appliedInserts")[0].GetProperty("surface").GetString().Should().Be("planar");

        filterGraph.Should().NotBeNull();
        filterGraph!.Should().MatchRegex(@"\[sia0\]overlay=x=\d+:y=\d+:eof_action=pass", "the flat recipe overlays its warped window on the base at an integer origin");
        filterGraph.Should().NotContain("[sia0]overlay=x=0:y=0", "the plate is far smaller than the frame, so only its bounding window is warped");
        filterGraph.Should().NotContain("split=", "a flat insert has no cells to split the asset into");
    }

    [Fact]
    public async Task InsertSurface_Planar_forces_a_curved_plate_back_onto_the_flat_path()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });
        artifact = artifact with { InsertRegions = [BuildCurvedInsertTrack()], OfferedInsertRegionIds = ["r0"] };

        string assetKey = InsertAssetKey();
        StepExecutionContext context = CreateGraphicsContext(
            artifact, BuildDecisionJson(("s0", "s0", "keep")), BuildInsertsPlanJson("r0", assetKey),
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with
            {
                EnableGraphics = false,
                EnableInserts = true,
                PrePaddingMs = 0,
                PostPaddingMs = 0,
                InsertSurface = InsertSurfaceMode.Planar
            });
        MockAssetDownload(workspace, assetKey);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        edl.GetProperty("inserts").GetProperty("appliedInserts")[0].GetProperty("surface").GetString().Should().Be("planar");
    }

    [Fact]
    public async Task A_curvature_below_MinInsertCurvature_stays_on_the_flat_path()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });
        artifact = artifact with { InsertRegions = [BuildCurvedInsertTrack()], OfferedInsertRegionIds = ["r0"] };

        string assetKey = InsertAssetKey();
        StepExecutionContext context = CreateGraphicsContext(
            artifact, BuildDecisionJson(("s0", "s0", "keep")), BuildInsertsPlanJson("r0", assetKey),
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with
            {
                EnableGraphics = false,
                EnableInserts = true,
                PrePaddingMs = 0,
                PostPaddingMs = 0,
                MinInsertCurvature = 0.5
            });
        MockAssetDownload(workspace, assetKey);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        edl.GetProperty("inserts").GetProperty("appliedInserts")[0].GetProperty("surface").GetString().Should().Be("planar");
    }

    [Fact]
    public void BuildInsertKeyframes_maps_keyframes_through_the_cut_and_skips_those_in_gaps()
    {
        // Spans keep [0,4) and [6,10) of a 30fps source: output timeline is [0,4)+[4,8).
        var spans = new List<VideoCompileStepExecutor.ResolvedSpan>
        {
            new(0, 4, 0, 4, 0, 120),
            new(6, 10, 6, 10, 180, 300)
        };
        OutputTimeline timeline = OutputTimeline.Build(spans, VideoCompileStepExecutor.BuildHardCutSeamsForFallback(spans.Count));
        var media = new VideoAnalysisMedia(10, 30, 1, 1920, 1080);

        // Track [2, 8] with keyframes at t=2 (kept, output 2.0), t=5 (in the cut gap — skipped),
        // t=8 (kept but OUTSIDE the first-overlap window [2,4) the insert is clipped to — skipped).
        VideoInsertRegionTrack track = new(
            "r0", "s0", 2.0, 8.0,
            Keyframes:
            [
                new VideoInsertQuadKeyframe(2.0, 0.3, 0.2, 0.6, 0.2, 0.3, 0.7, 0.6, 0.7),
                new VideoInsertQuadKeyframe(5.0, 0.4, 0.2, 0.7, 0.2, 0.4, 0.7, 0.7, 0.7),
                new VideoInsertQuadKeyframe(8.0, 0.5, 0.2, 0.8, 0.2, 0.5, 0.7, 0.8, 0.7)
            ],
            Confidence: 0.9, MeanAreaRatio: 0.1, MeanAspectRatio: 0.6, MotionClass: "Slow", ColorName: "green");

        (double Start, double End)? window = timeline.MapWindowToOutput(track.StartSec, track.EndSec, 0);
        window.Should().NotBeNull();
        window!.Value.Start.Should().BeApproximately(2.0, 1e-9);
        window.Value.End.Should().BeApproximately(4.0, 1e-9, "the insert is clipped to the FIRST kept portion");

        IReadOnlyList<InsertQuadFrame> keyframes = VideoCompileStepExecutor.BuildInsertKeyframes(
            track, timeline, media, window.Value, maxKeyframes: 96, overscan: 0);

        keyframes.Should().HaveCount(1, "only the t=2 keyframe survives the cut AND the clipped window");
        keyframes[0].FrameIndex.Should().Be(0, "the surviving keyframe sits exactly at the insert's own start");
        keyframes[0].X0.Should().BeApproximately(0.3 * 1920, 1e-6);
        keyframes[0].Y3.Should().BeApproximately(0.7 * 1080, 1e-6);
    }

    [Fact]
    public void BuildInsertKeyframes_overscan_expands_corners_outward_about_the_centroid()
    {
        var spans = new List<VideoCompileStepExecutor.ResolvedSpan> { new(0, 10, 0, 10, 0, 300) };
        OutputTimeline timeline = OutputTimeline.Build(spans, VideoCompileStepExecutor.BuildHardCutSeamsForFallback(spans.Count));
        var media = new VideoAnalysisMedia(10, 30, 1, 1000, 1000);

        // A centered square quad [0.4..0.6]^2 — centroid (0.5, 0.5).
        VideoInsertRegionTrack track = new(
            "r0", "s0", 2.0, 8.0,
            Keyframes: [new VideoInsertQuadKeyframe(2.0, 0.4, 0.4, 0.6, 0.4, 0.4, 0.6, 0.6, 0.6)],
            Confidence: 0.9, MeanAreaRatio: 0.04, MeanAspectRatio: 1.0, MotionClass: "Static", ColorName: "green");

        IReadOnlyList<InsertQuadFrame> keyframes = VideoCompileStepExecutor.BuildInsertKeyframes(
            track, timeline, media, (2.0, 8.0), maxKeyframes: 96, overscan: 0.10);

        // TL corner: 0.5 + (0.4 - 0.5) * 1.1 = 0.39 -> 390px.
        keyframes[0].X0.Should().BeApproximately(390, 1e-6);
        keyframes[0].Y0.Should().BeApproximately(390, 1e-6);
        // BR corner: 0.5 + (0.6 - 0.5) * 1.1 = 0.61 -> 610px.
        keyframes[0].X3.Should().BeApproximately(610, 1e-6);
    }

    [Fact]
    public async Task Second_insert_beyond_MaxInserts_is_dropped_max_inserts_exceeded()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });
        artifact = artifact with
        {
            InsertRegions = [BuildInsertTrack("r0", 1.0, 4.0), BuildInsertTrack("r1", 5.0, 9.0)],
            OfferedInsertRegionIds = ["r0", "r1"]
        };

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string assetKey = InsertAssetKey();
        string planJson = JsonSerializer.Serialize(new
        {
            overlays = Array.Empty<object>(),
            inserts = new[]
            {
                new { regionId = "r0", renderedAssetStorageKey = assetKey, reason = "a" },
                new { regionId = "r1", renderedAssetStorageKey = assetKey, reason = "b" }
            },
            planRationale = "test"
        });

        StepExecutionContext context = CreateGraphicsContext(
            artifact, decisionJson, planJson,
            out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { EnableGraphics = false, EnableInserts = true, MaxInserts = 1, PrePaddingMs = 0, PostPaddingMs = 0 });
        MockAssetDownload(workspace, assetKey);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement inserts = edl.GetProperty("inserts");
        inserts.GetProperty("appliedInsertCount").GetInt32().Should().Be(1);
        JsonElement dropped = inserts.GetProperty("droppedInserts");
        dropped.GetArrayLength().Should().Be(1);
        dropped[0].GetProperty("regionId").GetString().Should().Be("r1");
        dropped[0].GetProperty("reason").GetString().Should().Be("max_inserts_exceeded");
    }

    // =======================================================================
    // Sound effects (see docs/video-editing.md "Sound effects")
    // =======================================================================

    private const string SfxClipStorageKey = "projects/p/userFiles/whoosh.wav";
    private static readonly Guid SfxProjectFileId = Guid.NewGuid();

    private static VideoAnalysisArtifact WithSfxCandidates(VideoAnalysisArtifact artifact) =>
        artifact with
        {
            SfxCandidates = [new VideoAnalysisSfxCandidate("x0", SfxProjectFileId, "whoosh.wav", "audio/wav", 2048)],
            OfferedSfxIds = ["x0"]
        };

    private static string BuildSfxPlanJson(params (string SfxId, string AnchorId, string Timing, string Volume)[] cues)
    {
        var cueObjects = cues.Select(c => new
        {
            sfxId = c.SfxId,
            anchorId = c.AnchorId,
            timing = c.Timing,
            volume = c.Volume,
            reason = "test"
        });
        return JsonSerializer.Serialize(new { cues = cueObjects, planRationale = "test" });
    }

    /// <summary>Builds a context wired for SFX: a 4th history entry (StepOrder 3, the SoundDesigner step) plus EnableSfx=true, SfxPlan pointed at it — the CreateMusicContext precedent.</summary>
    private static StepExecutionContext CreateSfxContext(
        VideoAnalysisArtifact artifact,
        string decisionJson,
        string sfxPlanJson,
        out Mock<IProjectFileWorkspace> workspace,
        Func<VideoCompileStepConfig, VideoCompileStepConfig>? configOverride = null,
        string sfxMimeType = "audio/wav")
    {
        workspace = new Mock<IProjectFileWorkspace>();

        string artifactJson = JsonSerializer.Serialize(artifact, ArtifactOptions());

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), AnalysisKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllText(destPath, artifactJson);
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), SourceVideoKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), SfxClipStorageKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectWorkspaceFile>
            {
                new(SfxProjectFileId, ProjectId, "whoosh.wav", null, "userFiles", SfxClipStorageKey, sfxMimeType, 2048, DateTime.UtcNow, null)
            });

        VideoCompileStepConfig config = new(
            Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
            AnalysisStepOrder: 1,
            EnableSfx: true,
            SfxPlan: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 3));

        if (configOverride is not null)
            config = configOverride(config);

        string configJson = JsonSerializer.Serialize(config, ConfigOptions());

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 4,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = configJson
        };

        VideoAnalyzeStepConfig analyzeConfig = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput));
        var analyzeStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(analyzeConfig, ConfigOptions())
        };

        List<StepOutputHistoryEntry> history =
        [
            new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: SourceVideoKey, ArtifactStorageKey: null),
            new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
            new StepOutputHistoryEntry(2, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null),
            new StepOutputHistoryEntry(3, "SoundDesigner", sfxPlanJson, OutputStorageKey: null, ArtifactStorageKey: null)
        ];

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [analyzeStep, step],
            AccumulatedOutput = sfxPlanJson,
            StepOutputHistory = history,
            CurrentStepIndex = 3,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    [Fact]
    public async Task EnableSfx_false_leaves_filter_string_and_output_byte_identical_to_pre_sfx_compile()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));

        string? baselineFilter = null;
        StepExecutionContext baselineContext = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> baselineWorkspace);
        JsonElement baselineEdl = default;
        StepExecutionResult baselineResult = await CreateExecutor(
            baselineWorkspace, edlCaptured: e => baselineEdl = e,
            ffmpegArgsCaptured: args => baselineFilter ??= ExtractFilterComplexValue(args)).ExecuteAsync(baselineContext);
        baselineResult.Status.Should().Be(StepStatus.Completed, because: baselineResult.ErrorDetails ?? baselineResult.Output);

        // Even with an SfxPlan configured, EnableSfx=false must leave everything untouched.
        string sfxPlanJson = BuildSfxPlanJson(("x0", "s0", "OnCut", "Normal"));
        string? sfxOffFilter = null;
        StepExecutionContext sfxOffContext = CreateSfxContext(
            WithSfxCandidates(artifact), decisionJson, sfxPlanJson, out Mock<IProjectFileWorkspace> sfxOffWorkspace,
            configOverride: cfg => cfg with { EnableSfx = false, Decision = new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2) });
        JsonElement sfxOffEdl = default;
        StepExecutionResult sfxOffResult = await CreateExecutor(
            sfxOffWorkspace, edlCaptured: e => sfxOffEdl = e,
            ffmpegArgsCaptured: args => sfxOffFilter ??= ExtractFilterComplexValue(args)).ExecuteAsync(sfxOffContext);

        sfxOffResult.Status.Should().Be(StepStatus.Completed, because: sfxOffResult.ErrorDetails ?? sfxOffResult.Output);
        sfxOffFilter.Should().Be(baselineFilter, "EnableSfx=false must leave the filter string byte-identical");

        baselineEdl.TryGetProperty("sfx", out _).Should().BeFalse();
        sfxOffEdl.TryGetProperty("sfx", out _).Should().BeFalse();
        using JsonDocument outputDoc = JsonDocument.Parse(sfxOffResult.Output);
        outputDoc.RootElement.TryGetProperty("sfx", out _).Should().BeFalse();
    }

    [Fact]
    public async Task EnableSfx_true_with_StreamCopy_fails_SFX_REQUIRES_REENCODE()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with
            {
                EnableSfx = true,
                SfxPlan = new ExtractInputRef(ExtractInputSource.Previous),
                Mode = VideoCompileMode.StreamCopy,
                AllowKeyframeSnapping = true
            });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("SFX_REQUIRES_REENCODE");
    }

    [Fact]
    public async Task EnableSfx_true_with_AudioCodec_copy_fails_SFX_REQUIRES_AUDIO_REENCODE()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with
            {
                EnableSfx = true,
                SfxPlan = new ExtractInputRef(ExtractInputSource.Previous),
                AudioCodec = "copy"
            });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("SFX_REQUIRES_AUDIO_REENCODE");
    }

    [Fact]
    public async Task Keep_span_naming_an_sfx_clip_id_fails_UNKNOWN_ID_since_sfx_clips_are_a_separate_namespace()
    {
        VideoAnalysisArtifact artifact = WithSfxCandidates(
            BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" }));
        string decisionJson = BuildDecisionJson(("x0", "x0", "wrong namespace"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("UNKNOWN_ID");
    }

    [Fact]
    public async Task Planned_cue_is_resolved_delayed_to_the_output_timeline_and_recorded_in_the_sfx_block()
    {
        // Two shots kept as one span [0,10); the cue anchors to s1 (source 4.0s), which maps to
        // output 4.0s — so the branch must carry adelay=4000 and the Strong word must resolve to
        // the -4 dB default gain, all server-side.
        VideoAnalysisArtifact artifact = WithSfxCandidates(BuildArtifact(
            shots: new[] { ("s0", 0.0, 4.0), ("s1", 4.0, 10.0) },
            offeredIds: new[] { "s0", "s1" }));
        string decisionJson = BuildDecisionJson(("s0", "s1", "keep all"));
        string sfxPlanJson = BuildSfxPlanJson(("x0", "s1", "OnCut", "Strong"));

        StepExecutionContext context = CreateSfxContext(
            artifact, decisionJson, sfxPlanJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { PrePaddingMs = 0, PostPaddingMs = 0 });

        JsonElement edl = default;
        IReadOnlyList<string>? capturedArgs = null;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e, ffmpegArgsCaptured: args => capturedArgs ??= args).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        JsonElement sfx = edl.GetProperty("sfx");
        sfx.GetProperty("enabled").GetBoolean().Should().BeTrue();
        sfx.GetProperty("applied").GetBoolean().Should().BeTrue();
        sfx.GetProperty("appliedCueCount").GetInt32().Should().Be(1);
        JsonElement cue = sfx.GetProperty("cues")[0];
        cue.GetProperty("sfxId").GetString().Should().Be("x0");
        cue.GetProperty("anchorId").GetString().Should().Be("s1");
        cue.GetProperty("timing").GetString().Should().Be("OnCut");
        cue.GetProperty("volume").GetString().Should().Be("Strong");
        cue.GetProperty("gainDb").GetInt32().Should().Be(-4);
        cue.GetProperty("outputStartSec").GetDouble().Should().BeApproximately(4.0, 0.01);
        sfx.GetProperty("dropped").GetArrayLength().Should().Be(0);

        string filterComplex = ExtractFilterComplexValue(capturedArgs!);
        filterComplex.Should().Contain("[abase]", "the pre-SFX audio chain must end at the internal base label");
        filterComplex.Should().Contain("adelay=4000|4000[sfx0]");
        filterComplex.Should().Contain("[abase][sfx0]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[aout]");

        // The cue's clip must be the LAST ffmpeg input (after the source; no music here).
        List<int> inputFlagIndices = capturedArgs!
            .Select((a, i) => (a, i))
            .Where(t => t.a == "-i")
            .Select(t => t.i)
            .ToList();
        inputFlagIndices.Should().HaveCount(2, "the main source input plus the one cue input");
        capturedArgs[inputFlagIndices[^1] + 1].Should().Contain("sfx-", "the cue clip must be the last ffmpeg input");
        filterComplex.Should().Contain("[1:a]atrim=end=", "the cue is ffmpeg input index 1 here (no asset overlays or music present)");

        using JsonDocument outputDoc = JsonDocument.Parse(result.Output);
        outputDoc.RootElement.GetProperty("sfx").GetProperty("applied").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Cue_naming_an_unoffered_clip_id_is_dropped_and_the_remaining_cue_still_applies()
    {
        VideoAnalysisArtifact artifact = WithSfxCandidates(BuildArtifact(
            shots: new[] { ("s0", 0.0, 4.0), ("s1", 4.0, 10.0) },
            offeredIds: new[] { "s0", "s1" }));
        string decisionJson = BuildDecisionJson(("s0", "s1", "keep all"));
        string sfxPlanJson = BuildSfxPlanJson(
            ("x9", "s0", "OnCut", "Normal"),   // not in OfferedSfxIds
            ("x0", "s1", "Lag", "Subtle"));

        StepExecutionContext context = CreateSfxContext(
            artifact, decisionJson, sfxPlanJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { PrePaddingMs = 0, PostPaddingMs = 0 });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, "a bad cue must never fail the cut itself");
        JsonElement sfx = edl.GetProperty("sfx");
        sfx.GetProperty("appliedCueCount").GetInt32().Should().Be(1);
        JsonElement dropped = sfx.GetProperty("dropped");
        dropped.GetArrayLength().Should().Be(1);
        dropped[0].GetProperty("reason").GetString().Should().Be("unknown_sfx_id");
        dropped[0].GetProperty("sfxId").GetString().Should().Be("x9");

        // The surviving Lag cue fires SfxLagMs (default 150ms) after its anchor's output moment.
        JsonElement cue = sfx.GetProperty("cues")[0];
        cue.GetProperty("timing").GetString().Should().Be("Lag");
        cue.GetProperty("outputStartSec").GetDouble().Should().BeApproximately(4.15, 0.01);
    }

    // ---- T-110: e{n} anchors on events inside a composition ----

    private static VideoAnalysisArtifact WithRenderEvents(VideoAnalysisArtifact artifact, params (string Id, double Sec)[] events) =>
        artifact with
        {
            RenderEvents = events.Select(e => new VideoAnalysisRenderEvent(e.Id, "stamp", "Stamp lands", (int)(e.Sec * 30), e.Sec, SourceIndex: 0)).ToList(),
            OfferedEventIds = events.Select(e => e.Id).ToList()
        };

    [Fact]
    public async Task Cue_anchored_to_an_offered_event_fires_at_the_event_mapped_through_the_cut()
    {
        // Only s1 (source 4-10s) is kept; the event at source 6.0s lands at output 2.0s.
        VideoAnalysisArtifact artifact = WithRenderEvents(WithSfxCandidates(BuildArtifact(
            shots: new[] { ("s0", 0.0, 4.0), ("s1", 4.0, 10.0) },
            offeredIds: new[] { "s0", "s1" })), ("e0", 6.0));
        string decisionJson = BuildDecisionJson(("s1", "s1", "keep"));
        string sfxPlanJson = BuildSfxPlanJson(("x0", "e0", "OnCut", "Normal"));

        StepExecutionContext context = CreateSfxContext(
            artifact, decisionJson, sfxPlanJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { PrePaddingMs = 0, PostPaddingMs = 0 });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement sfx = edl.GetProperty("sfx");
        sfx.GetProperty("appliedCueCount").GetInt32().Should().Be(1);
        JsonElement cue = sfx.GetProperty("cues")[0];
        cue.GetProperty("anchorId").GetString().Should().Be("e0");
        cue.GetProperty("outputStartSec").GetDouble().Should().BeApproximately(2.0, 0.01);
    }

    [Fact]
    public async Task Shot_anchored_cue_on_footage_with_speech_is_dropped_as_sfx_over_speech_but_an_event_cue_is_kept()
    {
        // Speech covers 0-9 s of a 10 s source: the shot-anchored cue is dropped, the e0-anchored one stays.
        VideoAnalysisArtifact artifact = WithRenderEvents(WithSfxCandidates(BuildArtifact(
            shots: new[] { ("s0", 0.0, 4.0), ("s1", 4.0, 10.0) },
            segments: new[] { ("t0", 0.0, 9.0) },
            offeredIds: new[] { "s0", "s1" })), ("e0", 6.0));
        string decisionJson = BuildDecisionJson(("s0", "s1", "keep all"));
        string sfxPlanJson = BuildSfxPlanJson(("x0", "s1", "OnCut", "Normal"), ("x0", "e0", "OnCut", "Normal"));

        StepExecutionContext context = CreateSfxContext(
            artifact, decisionJson, sfxPlanJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { PrePaddingMs = 0, PostPaddingMs = 0 });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement sfx = edl.GetProperty("sfx");
        sfx.GetProperty("appliedCueCount").GetInt32().Should().Be(1);
        sfx.GetProperty("cues")[0].GetProperty("anchorId").GetString().Should().Be("e0");
        sfx.GetProperty("dropped")[0].GetProperty("reason").GetString().Should().Be("sfx_over_speech");
        sfx.GetProperty("dropped")[0].GetProperty("anchorId").GetString().Should().Be("s1");
    }

    [Fact]
    public async Task Cue_anchored_to_an_event_the_edit_cut_away_is_dropped()
    {
        VideoAnalysisArtifact artifact = WithRenderEvents(WithSfxCandidates(BuildArtifact(
            shots: new[] { ("s0", 0.0, 4.0), ("s1", 4.0, 10.0) },
            offeredIds: new[] { "s0", "s1" })), ("e0", 1.0));
        string decisionJson = BuildDecisionJson(("s1", "s1", "keep"));
        string sfxPlanJson = BuildSfxPlanJson(("x0", "e0", "OnCut", "Normal"));

        StepExecutionContext context = CreateSfxContext(
            artifact, decisionJson, sfxPlanJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { PrePaddingMs = 0, PostPaddingMs = 0 });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement sfx = edl.GetProperty("sfx");
        sfx.GetProperty("applied").GetBoolean().Should().BeFalse();
        sfx.GetProperty("dropped")[0].GetProperty("reason").GetString().Should().Be("anchor_cut_away");
        sfx.GetProperty("dropped")[0].GetProperty("anchorId").GetString().Should().Be("e0");
    }

    [Fact]
    public async Task Cue_anchored_to_an_event_that_was_not_offered_is_dropped_as_unknown_anchor()
    {
        VideoAnalysisArtifact withEvents = WithRenderEvents(WithSfxCandidates(BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" })), ("e0", 6.0));
        VideoAnalysisArtifact artifact = withEvents with { OfferedEventIds = [] };
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string sfxPlanJson = BuildSfxPlanJson(("x0", "e0", "OnCut", "Normal"));

        StepExecutionContext context = CreateSfxContext(artifact, decisionJson, sfxPlanJson, out Mock<IProjectFileWorkspace> workspace);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        edl.GetProperty("sfx").GetProperty("dropped")[0].GetProperty("reason").GetString().Should().Be("unknown_anchor_id");
    }

    [Fact]
    public async Task Keep_span_naming_an_event_id_fails_UNKNOWN_ID()
    {
        VideoAnalysisArtifact artifact = WithRenderEvents(WithSfxCandidates(BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" })), ("e0", 6.0));
        string decisionJson = BuildDecisionJson(("e0", "e0", "wrong namespace"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("UNKNOWN_ID");
    }

    [Fact]
    public async Task Cue_anchored_to_a_cut_away_moment_is_dropped_never_relocated()
    {
        // Only s1 is kept, so s0's start (source 0.0) has no output moment at all.
        VideoAnalysisArtifact artifact = WithSfxCandidates(BuildArtifact(
            shots: new[] { ("s0", 0.0, 4.0), ("s1", 4.0, 10.0) },
            offeredIds: new[] { "s0", "s1" }));
        string decisionJson = BuildDecisionJson(("s1", "s1", "keep"));
        string sfxPlanJson = BuildSfxPlanJson(("x0", "s0", "OnCut", "Normal"));

        StepExecutionContext context = CreateSfxContext(
            artifact, decisionJson, sfxPlanJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { PrePaddingMs = 0, PostPaddingMs = 0 });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement sfx = edl.GetProperty("sfx");
        sfx.GetProperty("applied").GetBoolean().Should().BeFalse();
        sfx.GetProperty("dropped")[0].GetProperty("reason").GetString().Should().Be("anchor_cut_away");
    }

    [Fact]
    public async Task Empty_cue_list_is_a_valid_plan_reported_as_empty_plan_not_a_failure()
    {
        VideoAnalysisArtifact artifact = WithSfxCandidates(
            BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" }));
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string sfxPlanJson = BuildSfxPlanJson();

        StepExecutionContext context = CreateSfxContext(
            artifact, decisionJson, sfxPlanJson, out Mock<IProjectFileWorkspace> workspace);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement sfx = edl.GetProperty("sfx");
        sfx.GetProperty("applied").GetBoolean().Should().BeFalse();
        sfx.GetProperty("reason").GetString().Should().Be("empty_plan");
        sfx.GetProperty("dropped").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Source_with_no_audio_stream_and_no_music_mixes_the_cue_over_a_synthesized_silent_base()
    {
        // Changed deliberately (finding 42, docs/video-editing.md "Program audio on silent
        // footage"): this compile used to drop every cue as no_base_audio and ship with NO audio
        // track. A program with sound effects must have sound, so a silent base is synthesized
        // and the cue is mixed into it.
        VideoAnalysisArtifact artifact = WithSfxCandidates(
            BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" }));
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string sfxPlanJson = BuildSfxPlanJson(("x0", "s0", "OnCut", "Normal"));

        StepExecutionContext context = CreateSfxContext(
            artifact, decisionJson, sfxPlanJson, out Mock<IProjectFileWorkspace> workspace);

        JsonElement edl = default;
        IReadOnlyList<string>? capturedArgs = null;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e, ffmpegArgsCaptured: a => { if (a.Contains("-map")) capturedArgs = a; },
            configureMediaProbe: mock => mock
                .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(path => !path.Contains("sfx-")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaProbeResult(10, 30, 1, 1920, 1080, "h264", null, null)))
            .ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement sfx = edl.GetProperty("sfx");
        sfx.GetProperty("applied").GetBoolean().Should().BeTrue();
        edl.GetProperty("audio").GetProperty("silentBase").GetBoolean().Should().BeTrue();

        string filterComplex = ExtractFilterComplexValue(capturedArgs!);
        filterComplex.Should().Contain("anullsrc=r=48000:cl=stereo:d=10[abase]");
        filterComplex.Should().NotContain("[0:a]", "the source has no audio stream to read");
        capturedArgs.Should().Contain("[aout]");
        capturedArgs.Should().Contain("-c:a");
    }

    [Fact]
    public async Task Non_audio_clip_is_dropped_as_sfx_not_audio_and_the_cut_still_succeeds()
    {
        VideoAnalysisArtifact artifact = WithSfxCandidates(
            BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" }));
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string sfxPlanJson = BuildSfxPlanJson(("x0", "s0", "OnCut", "Normal"));

        StepExecutionContext context = CreateSfxContext(
            artifact, decisionJson, sfxPlanJson, out Mock<IProjectFileWorkspace> workspace, sfxMimeType: "video/mp4");

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, "a bad clip must never fail the cut itself");
        JsonElement sfx = edl.GetProperty("sfx");
        sfx.GetProperty("applied").GetBoolean().Should().BeFalse();
        sfx.GetProperty("dropped")[0].GetProperty("reason").GetString().Should().Be("sfx_not_audio");
    }

    [Fact]
    public async Task Cues_beyond_MaxSfxCues_are_dropped_in_plan_order()
    {
        VideoAnalysisArtifact artifact = WithSfxCandidates(BuildArtifact(
            shots: new[] { ("s0", 0.0, 4.0), ("s1", 4.0, 10.0) },
            offeredIds: new[] { "s0", "s1" }));
        string decisionJson = BuildDecisionJson(("s0", "s1", "keep all"));
        string sfxPlanJson = BuildSfxPlanJson(
            ("x0", "s0", "OnCut", "Normal"),
            ("x0", "s1", "OnCut", "Normal"));

        StepExecutionContext context = CreateSfxContext(
            artifact, decisionJson, sfxPlanJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { MaxSfxCues = 1, PrePaddingMs = 0, PostPaddingMs = 0 });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement sfx = edl.GetProperty("sfx");
        sfx.GetProperty("appliedCueCount").GetInt32().Should().Be(1);
        sfx.GetProperty("cues")[0].GetProperty("anchorId").GetString().Should().Be("s0", "cues apply in plan order");
        sfx.GetProperty("dropped")[0].GetProperty("reason").GetString().Should().Be("max_cues_exceeded");
    }

    [Fact]
    public async Task Amix_without_normalize_option_skips_all_sfx_and_the_cut_still_succeeds()
    {
        VideoAnalysisArtifact artifact = WithSfxCandidates(
            BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" }));
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string sfxPlanJson = BuildSfxPlanJson(("x0", "s0", "OnCut", "Normal"));

        StepExecutionContext context = CreateSfxContext(
            artifact, decisionJson, sfxPlanJson, out Mock<IProjectFileWorkspace> workspace);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e, amixNormalizeAvailable: false).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement sfx = edl.GetProperty("sfx");
        sfx.GetProperty("unavailable").GetBoolean().Should().BeTrue();
        sfx.GetProperty("applied").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task VideoCompileStep_generates_and_uploads_timeline_manifest_and_keeps_timelineStorageKey_out_of_the_edl()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0) },
            offeredIds: new[] { "s0", "s1" });

        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);

        // The EDL is written and uploaded EXACTLY ONCE, before the manifest exists — there is no
        // second re-write/re-upload to stamp timelineStorageKey onto it, so the uploaded EDL
        // deliberately does NOT carry that key inline. It reaches callers through OutputJson (and
        // WorkflowStepResult.TimelineStorageKey) only.
        edl.TryGetProperty("timelineStorageKey", out _).Should().BeFalse();

        using JsonDocument outputDoc = JsonDocument.Parse(result.Output);
        outputDoc.RootElement.TryGetProperty("timelineStorageKey", out JsonElement outputKey).Should().BeTrue();
        outputKey.GetString().Should().NotBeNullOrWhiteSpace();
        outputKey.GetString().Should().Be(result.TimelineStorageKey);

        // Verify UploadArtifactAsync was called for timeline.json
        workspace.Verify(w => w.UploadArtifactAsync(
            It.IsAny<Guid>(),
            It.Is<string>(path => path.EndsWith("timeline.json", StringComparison.OrdinalIgnoreCase)),
            It.Is<string>(fileName => fileName.EndsWith("timeline.json", StringComparison.OrdinalIgnoreCase)),
            "application/json",
            It.IsAny<CancellationToken>(),
            It.IsAny<string>()), Times.Once);

        // ...and that the EDL artifact is uploaded exactly once per run, not twice.
        workspace.Verify(w => w.UploadArtifactAsync(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.Is<string>(fileName => fileName.EndsWith("-edl.json", StringComparison.OrdinalIgnoreCase)),
            "application/json",
            It.IsAny<CancellationToken>(),
            It.IsAny<string>()), Times.Once);
    }

    /// <summary>
    /// End-to-end proof that a cut's <c>AgentRationale</c> follows its OWN originating
    /// <c>Keep[i]</c> through the REAL span pipeline — pre-padding, post-padding coalescing and
    /// MinSegmentMs dropping — instead of the raw <c>Keep</c> index.
    ///
    /// <para>
    /// s0 and s1 are 0.5 s apart in the source and <c>PrePaddingMs = 500</c> pushes them into each
    /// other, so they coalesce into ONE span carrying both origin ids; s2 is dropped by
    /// <c>MinSegmentMs</c>; s3 survives untouched. The surviving cut 2 sits at resolved index 1,
    /// which the old by-index join answered with <c>Keep[1]</c>'s reason
    /// ("COALESCED-SECOND-REASON") — another cut's reason.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Timeline_manifest_cut_rationales_follow_each_cut_s_own_keep_span_through_padding_and_coalescing()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 1.0), ("s1", 1.5, 9.0), ("s2", 20.0, 21.0), ("s3", 40.0, 50.0) },
            offeredIds: new[] { "s0", "s1", "s2", "s3" });

        string decisionJson = BuildDecisionJson(
            ("s0", "s0", "OPENING-REASON"),
            ("s1", "s1", "COALESCED-SECOND-REASON"),
            ("s2", "s2", "DROPPED-REASON"),
            ("s3", "s3", "PAYOFF-REASON"));

        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with { PrePaddingMs = 500, PostPaddingMs = 0, MinSegmentMs = 5000 });

        VideoCompileStepExecutor executor = CreateExecutor(workspace);

        // Configured AFTER CreateExecutor's own catch-all UploadArtifactAsync setup, so Moq's
        // most-recent-setup-wins precedence (the same discipline the harness's amix-probe setup
        // above relies on) routes timeline.json here instead. The manifest is a real local scratch
        // file by the time this callback runs.
        // (the scratch directory is deleted as soon as the step finishes, so the manifest has to be
        // read inside the callback rather than deferred to the assertions)
        string timelineJson = null;
        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Callback<Guid, string, string, string, CancellationToken, string>((_, path, fileName, _, _, _) =>
            {
                if (fileName.EndsWith("timeline.json", StringComparison.OrdinalIgnoreCase))
                    timelineJson = File.ReadAllText(path);
            })
            .ReturnsAsync("projects/p/agentFiles/video-analysis/e/step-3-timeline.json");
        workspace.SetupRegisteredRenderPublish();

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        timelineJson.Should().NotBeNull();

        TimelineManifest manifest = JsonSerializer.Deserialize<TimelineManifest>(
            timelineJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var items = manifest.Tracks.Single(t => t.Type == "video").Items;
        items.Should().HaveCount(2);

        // cut 1 came out of the coalesced s0+s1 span -> the FIRST origin's reason wins.
        items[0].AgentRationale.Should().Be("OPENING-REASON");

        // cut 2 is s3's own span -> s3's reason, never the reason of Keep[1].
        items[1].AgentRationale.Should().Be("PAYOFF-REASON");
        items[1].AgentRationale.Should().NotBe("COALESCED-SECOND-REASON");
        timelineJson.Should().NotContain("DROPPED-REASON");
    }

    /// <summary>
    /// Test that a completed VideoCompile step's persisted TimelineStorageKey equals
    /// the uploaded manifest key.
    /// </summary>
    [Fact]
    public async Task Successful_compile_sets_TimelineStorageKey()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0) },
            offeredIds: new[] { "s0", "s1" });

        string decisionJson = BuildDecisionJson(("s0", "s1", "keep the whole thing"));
        StepExecutionContext context = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        result.TimelineStorageKey.Should().NotBeNullOrWhiteSpace();

        // Verify the TimelineStorageKey is also in the output JSON
        JsonDocument outputDoc = JsonDocument.Parse(result.Output);
        outputDoc.RootElement.TryGetProperty("timelineStorageKey", out JsonElement outputKey).Should().BeTrue();
        outputKey.GetString().Should().Be(result.TimelineStorageKey);
    }

    /// <summary>
    /// Test that TimelineStorageKey survives a cache-hit rerun.
    /// </summary>
    [Fact]
    public async Task TimelineStorageKey_survives_cache_hit_rerun()
    {
        // First execution: compute and cache
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0) },
            offeredIds: new[] { "s0", "s1" });

        string decisionJson = BuildDecisionJson(("s0", "s1", "keep the whole thing"));
        StepExecutionContext context1 = CreateContext(artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result1 = await CreateExecutor(workspace).ExecuteAsync(context1);

        result1.Status.Should().Be(StepStatus.Completed);
        result1.TimelineStorageKey.Should().NotBeNullOrWhiteSpace();

        // The TimelineStorageKey should have been set from the execution
        string timelineKeyValue = result1.TimelineStorageKey;

        // Verify TimelineStorageKey is in the output JSON
        JsonDocument outputDoc = JsonDocument.Parse(result1.Output);
        outputDoc.RootElement.TryGetProperty("timelineStorageKey", out JsonElement outputKey).Should().BeTrue();
        outputKey.GetString().Should().Be(timelineKeyValue);
    }

    // =======================================================================
    // Voiceover integration (T-021 — add ResolveVoiceoverAsync + amix wiring)
    // =======================================================================

    private static VideoAnalysisArtifact WithMusicCandidates(VideoAnalysisArtifact artifact) =>
        artifact with
        {
            MusicCandidates = [new VideoAnalysisMusicCandidate("m0", MusicProjectFileId, "music.mp3", "audio/mpeg", 4096)],
            OfferedMusicIds = ["m0"]
        };

    private static VideoAnalysisArtifact WithAudioDescriptors(VideoAnalysisArtifact artifact) =>
        artifact with
        {
            Shots = artifact.Shots.Select(s => s with
            {
                Audio = new VideoAnalysisShotAudio(
                RmsDbfs: -24.5,
                PeakDbfs: -15.0,
                SpeechRatio: 0.8,
                LoudnessClass: "Moderate")
            }).ToList()
        };

    private static string BuildMusicPlanJson(string trackId, string intensity, string ducking, string fit) =>
        JsonSerializer.Serialize(new
        {
            trackId,
            intensity,
            ducking,
            fit,
            planRationale = "test"
        });

    /// <summary>A 0.5 s, 16 kHz, mono, 16-bit PCM sine at -12 dBFS — enough for WavRmsSampler to measure a level from.</summary>
    internal static byte[] TestToneWav()
    {
        const int sampleRate = 16000;
        const int samples = sampleRate / 2;
        var pcm = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            short v = (short)Math.Round(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * short.MaxValue * 0.25);
            pcm[i * 2] = (byte)(v & 0xFF);
            pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("RIFF"u8); bw.Write(36 + pcm.Length); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1); bw.Write(sampleRate);
        bw.Write(sampleRate * 2); bw.Write((short)2); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(pcm.Length); bw.Write(pcm);
        bw.Flush();
        return ms.ToArray();
    }

    private static string BuildVoiceoverOutputJson(params (string Id, double StartSec, double DurationSec)[] lines) =>
        JsonSerializer.Serialize(new
        {
            view = new
            {
                lines = lines.Select(l => new
                {
                    id = l.Id,
                    startSec = l.StartSec,
                    durationSec = l.DurationSec,
                    status = "ok"
                }).ToList()
            },
            meta = new { }
        });

    private static StepExecutionContext CreateVoiceoverContext(
        VideoAnalysisArtifact artifact,
        string decisionJson,
        out Mock<IProjectFileWorkspace> workspace,
        Func<VideoCompileStepConfig, VideoCompileStepConfig>? configOverride = null)
    {
        workspace = new Mock<IProjectFileWorkspace>();

        string artifactJson = JsonSerializer.Serialize(artifact, ArtifactOptions());
        string voiceoverOutputJson = BuildVoiceoverOutputJson(
            ("vo1", 2.0, 3.0),
            ("vo2", 12.0, 5.0)
        );

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), AnalysisKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllText(destPath, artifactJson);
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), SourceVideoKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        // Mock voiceover line WAV files — a REAL (tiny) 16-bit PCM tone, so the compile's
        // voiceover.headroom fact has a line level to measure against the shot's dialogue level.
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), It.Is<string>(s => s.Contains("vo1.wav") || s.Contains("vo2.wav")), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, TestToneWav());
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ProjectWorkspaceFile(
                    Guid.NewGuid(), ProjectId, "vo1.wav", null, "userFiles",
                    $"{VoiceoverStorageKeyPrefix}/vo1.wav", "audio/wav", 1024, DateTime.UtcNow, null),
                new ProjectWorkspaceFile(
                    Guid.NewGuid(), ProjectId, "vo2.wav", null, "userFiles",
                    $"{VoiceoverStorageKeyPrefix}/vo2.wav", "audio/wav", 1024, DateTime.UtcNow, null)
            ]);

        VideoCompileStepConfig config = new(
            Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
            AnalysisStepOrder: 1,
            EnableVoiceover: true,
            VoiceoverStepOrder: 3);

        if (configOverride is not null)
            config = configOverride(config);

        string configJson = JsonSerializer.Serialize(config, ConfigOptions());

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 4,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = configJson
        };

        VideoAnalyzeStepConfig analyzeConfig = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput));
        var analyzeStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(analyzeConfig, ConfigOptions())
        };

        List<StepOutputHistoryEntry> history =
        [
            new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: SourceVideoKey, ArtifactStorageKey: null),
            new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
            new StepOutputHistoryEntry(2, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null),
            new StepOutputHistoryEntry(3, "Voiceover", voiceoverOutputJson, OutputStorageKey: null, ArtifactStorageKey: null)
        ];

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [analyzeStep, step],
            AccumulatedOutput = decisionJson,
            StepOutputHistory = history,
            CurrentStepIndex = 3,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    private static StepExecutionContext CreateVoiceoverMusicContext(
        VideoAnalysisArtifact artifact,
        string decisionJson,
        string musicPlanJson,
        out Mock<IProjectFileWorkspace> workspace)
    {
        workspace = new Mock<IProjectFileWorkspace>();

        string artifactJson = JsonSerializer.Serialize(artifact, ArtifactOptions());
        string voiceoverOutputJson = BuildVoiceoverOutputJson(("vo1", 2.0, 3.0));

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), AnalysisKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllText(destPath, artifactJson);
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), SourceVideoKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), MusicTrackStorageKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), It.Is<string>(s => s.Contains("vo1.wav")), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectWorkspaceFile>
            {
                new(MusicProjectFileId, ProjectId, "music.mp3", null, "userFiles", MusicTrackStorageKey, "audio/mpeg", 4096, DateTime.UtcNow, null),
                new ProjectWorkspaceFile(
                    Guid.NewGuid(), ProjectId, "vo1.wav", null, "userFiles",
                    $"{VoiceoverStorageKeyPrefix}/vo1.wav", "audio/wav", 1024, DateTime.UtcNow, null)
            });

        VideoCompileStepConfig config = new(
            Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
            AnalysisStepOrder: 1,
            EnableVoiceover: true,
            VoiceoverStepOrder: 3,
            EnableMusic: true,
            MusicTrackProjectFileId: MusicProjectFileId,
            MusicPlan: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 4));

        string configJson = JsonSerializer.Serialize(config, ConfigOptions());

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 5,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = configJson
        };

        VideoAnalyzeStepConfig analyzeConfig = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput));
        var analyzeStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(analyzeConfig, ConfigOptions())
        };

        List<StepOutputHistoryEntry> history =
        [
            new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: SourceVideoKey, ArtifactStorageKey: null),
            new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
            new StepOutputHistoryEntry(2, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null),
            new StepOutputHistoryEntry(3, "Voiceover", voiceoverOutputJson, OutputStorageKey: null, ArtifactStorageKey: null),
            new StepOutputHistoryEntry(4, "MusicSupervisor", musicPlanJson, OutputStorageKey: null, ArtifactStorageKey: null)
        ];

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [analyzeStep, step],
            AccumulatedOutput = decisionJson,
            StepOutputHistory = history,
            CurrentStepIndex = 4,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    private static StepExecutionContext CreateVoiceoverContextWithOverlappingLines(
        VideoAnalysisArtifact artifact,
        string decisionJson,
        out Mock<IProjectFileWorkspace> workspace)
    {
        workspace = new Mock<IProjectFileWorkspace>();

        string artifactJson = JsonSerializer.Serialize(artifact, ArtifactOptions());
        // Lines at [5,10) and [8,15) overlap
        string voiceoverOutputJson = BuildVoiceoverOutputJson(
            ("vo1", 5.0, 5.0),
            ("vo2", 8.0, 7.0)
        );

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), AnalysisKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllText(destPath, artifactJson);
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), SourceVideoKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), It.Is<string>(s => s.Contains("vo1.wav") || s.Contains("vo2.wav")), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ProjectWorkspaceFile(
                    Guid.NewGuid(), ProjectId, "vo1.wav", null, "userFiles",
                    $"{VoiceoverStorageKeyPrefix}/vo1.wav", "audio/wav", 1024, DateTime.UtcNow, null),
                new ProjectWorkspaceFile(
                    Guid.NewGuid(), ProjectId, "vo2.wav", null, "userFiles",
                    $"{VoiceoverStorageKeyPrefix}/vo2.wav", "audio/wav", 1024, DateTime.UtcNow, null)
            ]);

        VideoCompileStepConfig config = new(
            Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
            AnalysisStepOrder: 1,
            EnableVoiceover: true,
            VoiceoverStepOrder: 3);

        string configJson = JsonSerializer.Serialize(config, ConfigOptions());

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 4,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = configJson
        };

        VideoAnalyzeStepConfig analyzeConfig = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput));
        var analyzeStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(analyzeConfig, ConfigOptions())
        };

        List<StepOutputHistoryEntry> history =
        [
            new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: SourceVideoKey, ArtifactStorageKey: null),
            new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
            new StepOutputHistoryEntry(2, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null),
            new StepOutputHistoryEntry(3, "Voiceover", voiceoverOutputJson, OutputStorageKey: null, ArtifactStorageKey: null)
        ];

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [analyzeStep, step],
            AccumulatedOutput = decisionJson,
            StepOutputHistory = history,
            CurrentStepIndex = 3,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    // =======================================================================
    // Voiceover x generated clips: ffmpeg input NUMBERING, not text (T-123)
    // =======================================================================

    private const int GeneratedClipStepOrder = 5;

    private static string GeneratedClipStorageKey(int ordinal) =>
        $"projects/p/agentFiles/generated/e/step-{GeneratedClipStepOrder}-v{ordinal}.mp4";

    /// <summary>Probe output for a normalized generated clip — a real h264 video stream, exactly what <c>GeneratedClipNormalizer</c> requires before a clip may be placed.</summary>
    private const string GeneratedClipProbeJson = """
        { "streams": [ { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080,
          "r_frame_rate": "30/1", "duration": "10" } ], "format": { "duration": "10" } }
        """;

    /// <summary>One applied ColdOpen, under the exact field names <c>VideoGenerateStepExecutor</c> writes into the manifest.</summary>
    private static string BuildVoiceoverGeneratedClipsManifestJson() =>
        JsonSerializer.Serialize(new
        {
            version = 1,
            planStepOrder = GeneratedClipStepOrder,
            analysisStepOrder = 1,
            providerId = Guid.NewGuid().ToString(),
            model = "MiniMax-H3",
            resolution = "768P",
            ratio = "16:9",
            takes = 1,
            clips = new[]
            {
                new
                {
                    clipId = "v1",
                    shotIndex = 1,
                    take = 1,
                    purpose = "ColdOpen",
                    anchor = "s0",
                    camera = "Static",
                    duration = "Short",
                    durationSeconds = 10,
                    status = "succeeded",
                    storageKey = GeneratedClipStorageKey(1),
                    cost = 0.32m,
                    costBasis = "actual",
                    keyframeRequested = false,
                    keyframeApplied = false
                }
            },
            meta = new { }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>
    /// A compile that has BOTH a voiceover (two resolved lines, so the term the generated-clip
    /// input base is missing is neither 0 nor 1) and one applied generated clip — the one
    /// combination nothing in this suite covered before T-123.
    /// </summary>
    private static StepExecutionContext CreateVoiceoverGeneratedClipsContext(
        VideoAnalysisArtifact artifact,
        string decisionJson,
        string manifestJson,
        out Mock<IProjectFileWorkspace> workspace)
    {
        workspace = new Mock<IProjectFileWorkspace>();

        string artifactJson = JsonSerializer.Serialize(artifact, ArtifactOptions());
        string voiceoverOutputJson = BuildVoiceoverOutputJson(
            ("vo1", 2.0, 3.0),
            ("vo2", 12.0, 5.0)
        );

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), AnalysisKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllText(destPath, artifactJson);
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), SourceVideoKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), It.Is<string>(s => s.Contains("vo1.wav") || s.Contains("vo2.wav")),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(),
                It.Is<string>(k => k.StartsWith("projects/p/agentFiles/generated/", StringComparison.Ordinal)),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x0A, 0x0B, 0x0C });
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), GeneratedClipManifestKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllText(destPath, manifestJson);
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ProjectWorkspaceFile(
                    Guid.NewGuid(), ProjectId, "vo1.wav", null, "userFiles",
                    $"{VoiceoverStorageKeyPrefix}/vo1.wav", "audio/wav", 1024, DateTime.UtcNow, null),
                new ProjectWorkspaceFile(
                    Guid.NewGuid(), ProjectId, "vo2.wav", null, "userFiles",
                    $"{VoiceoverStorageKeyPrefix}/vo2.wav", "audio/wav", 1024, DateTime.UtcNow, null)
            ]);

        VideoCompileStepConfig config = new(
            Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
            AnalysisStepOrder: 1,
            EnableVoiceover: true,
            VoiceoverStepOrder: 3,
            EnableGeneratedClips: true,
            GeneratedClips: new ExtractInputRef(ExtractInputSource.Step, StepOrder: GeneratedClipStepOrder),
            // Zeroed so each resolved span is exactly the artifact's own shot time, matching the
            // generated-clips suite's own convention.
            PrePaddingMs: 0,
            PostPaddingMs: 0);

        string configJson = JsonSerializer.Serialize(config, ConfigOptions());

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 6,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = configJson
        };

        VideoAnalyzeStepConfig analyzeConfig = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput));
        var analyzeStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(analyzeConfig, ConfigOptions())
        };

        List<StepOutputHistoryEntry> history =
        [
            new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: SourceVideoKey, ArtifactStorageKey: null),
            new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
            new StepOutputHistoryEntry(2, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null),
            new StepOutputHistoryEntry(3, "Voiceover", voiceoverOutputJson, OutputStorageKey: null, ArtifactStorageKey: null),
            new StepOutputHistoryEntry(GeneratedClipStepOrder, "VideoGenerate", "{}", OutputStorageKey: null,
                ArtifactStorageKey: GeneratedClipManifestKey)
        ];

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [analyzeStep, step],
            AccumulatedOutput = decisionJson,
            StepOutputHistory = history,
            CurrentStepIndex = history.Count,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    /// <summary>
    /// T-123 regression: a compile that uses voiceover AND generated clips reads every generated
    /// clip from the WRONG <c>-i</c> unless the generated-clip input base counts the voiceover
    /// inputs, which sit between the music and the SFX inputs in argv order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mechanism: the real <c>-i</c> argv order and the segmented path's filtergraph SCRIPT are
    /// both read, and the graph's own stream label for the generated cold open
    /// (<c>[N:v]...setpts=PTS-STARTPTS[gcold0]</c>) is asserted to be the index the argv order
    /// actually gives that clip. That is deliberately stronger than either half alone: the argv
    /// order is the ground truth of what ffmpeg will read (so the expectation is not re-derived
    /// from the same arithmetic under test), and the label is where the executor COMMITS to an
    /// index. A test that only counted <c>-i</c>s would pass on a tree whose filtergraph still
    /// pointed at the voiceover WAV.
    /// </para>
    /// <para>
    /// The segmented path always writes its graph to <c>-filter_complex_script</c> rather than
    /// inline (see <c>EncodeReencodeSegmentedAsync</c>), and <c>EnableGeneratedClips</c> is itself
    /// what routes this compile there, so the script is read off disk while ffmpeg is being
    /// invoked — the scratch space is gone by the time <c>ExecuteAsync</c> returns.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task T5_Voiceover_and_generated_clips_together_read_the_generated_clip_from_its_real_input_index()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0) },
            offeredIds: new[] { "s0", "s1" });

        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));
        StepExecutionContext context = CreateVoiceoverGeneratedClipsContext(
            artifact, decisionJson, BuildVoiceoverGeneratedClipsManifestJson(),
            out Mock<IProjectFileWorkspace> workspace);

        IReadOnlyList<string>? encodeArgs = null;
        string? filterGraph = null;

        StepExecutionResult result = await CreateExecutor(
            workspace,
            ffmpegArgsCaptured: args =>
            {
                // -map is emitted by the encode and by nothing else, so this skips
                // GeneratedClipNormalizer's own per-clip re-encode of the downloaded clip.
                if (!args.Contains("-map"))
                    return;

                encodeArgs = args;
                filterGraph ??= ExtractFilterGraph(args);
            },
            configureToolRunner: runner => runner
                .Setup(t => t.RunFfprobeAsync(
                    It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VideoToolResult(0, GeneratedClipProbeJson, string.Empty, false)))
            .ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        // Both features must genuinely be ON in this run — on either side of the fix, a compile
        // that quietly applied only one of them would prove nothing about their interaction.
        using (JsonDocument outputDoc = JsonDocument.Parse(result.Output))
        {
            outputDoc.RootElement.GetProperty("voiceover").GetProperty("applied").GetBoolean()
                .Should().BeTrue("the two voiceover lines must actually apply for this test to mean anything");
            outputDoc.RootElement.GetProperty("voiceover").GetProperty("lineCount").GetInt32()
                .Should().Be(2);
            outputDoc.RootElement.GetProperty("generatedClips").GetProperty("coldOpensApplied").GetInt32()
                .Should().Be(1, "the one manifest ColdOpen must actually be placed");
        }

        encodeArgs.Should().NotBeNull("the encode must have run");

        // The REAL -i order, which is what ffmpeg numbers its inputs by.
        List<string> inputPaths = new();
        for (int i = 0; i < encodeArgs!.Count - 1; i++)
        {
            if (encodeArgs[i] == "-i")
                inputPaths.Add(encodeArgs[i + 1]);
        }

        inputPaths.Should().HaveCount(4, "one source + two voiceover lines + one generated clip");

        int generatedClipIndex = inputPaths.FindIndex(
            p => Path.GetFileName(p).StartsWith("gclip-", StringComparison.Ordinal));
        generatedClipIndex.Should().Be(3,
            "the generated clip follows the single source and BOTH voiceover lines in argv order");

        // Grounded, not assumed: inputs 1 and 2 really are the voiceover lines, so an index that
        // stopped short of 3 would be reading a WAV where a video clip is expected.
        Path.GetFileName(inputPaths[1]).Should().StartWith("voiceover-");
        Path.GetFileName(inputPaths[2]).Should().StartWith("voiceover-");

        // `var`, not `Match`: Moq also exports a `Match` type, so naming it here is ambiguous.
        var branch = Regex.Match(filterGraph!, @"\[(\d+):v\][^;]*\[gcold0\]");
        branch.Success.Should().BeTrue(
            $"expected the generated cold open's video branch in the filtergraph, got: {filterGraph}");
        branch.Groups[1].Value.Should().Be(
            generatedClipIndex.ToString(CultureInfo.InvariantCulture),
            "the generated clip must be read from the -i that actually holds it — input {0} is voiceover line 1, not the clip",
            generatedClipIndex - 2);
    }

    [Fact]
    public async Task T1_EnableVoiceover_true_with_2_non_overlapping_lines_produces_amix_inputs_at_mapped_offsets()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0) },
            offeredIds: new[] { "s0", "s1" });

        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));
        StepExecutionContext context = CreateVoiceoverContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        edl.TryGetProperty("voiceover", out JsonElement voElement).Should().BeTrue("EDL must have voiceover key");
        voElement.GetProperty("applied").GetBoolean().Should().BeTrue("voiceover should be applied");
        voElement.GetProperty("lineCount").GetInt32().Should().Be(2, "should have 2 applied voiceover lines");
    }

    [Fact]
    public async Task T2_EnableVoiceover_and_EnableMusic_together_exercise_MusicMixPlanner_additionalNoLiftWindows()
    {
        VideoAnalysisArtifact artifact = WithMusicCandidates(BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0) },
            offeredIds: new[] { "s0", "s1" }));

        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));
        string musicPlanJson = BuildMusicPlanJson("m0", "Balanced", "SpeechEnvelope", "LoopToFit");

        StepExecutionContext context = CreateVoiceoverMusicContext(
            artifact, decisionJson, musicPlanJson, out Mock<IProjectFileWorkspace> workspace);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        // Both voiceover and music should be applied
        edl.TryGetProperty("voiceover", out JsonElement voElement).Should().BeTrue();
        voElement.GetProperty("applied").GetBoolean().Should().BeTrue();
        edl.TryGetProperty("music", out JsonElement musicElement).Should().BeTrue();
        musicElement.GetProperty("applied").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task T3_Overlapping_voiceover_lines_never_crash_compile_and_output_flags_overlap()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 20.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateVoiceoverContextWithOverlappingLines(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument outputDoc = JsonDocument.Parse(result.Output);
        outputDoc.RootElement.TryGetProperty("voiceover", out JsonElement voElement).Should().BeTrue();

        // The intervals the compile ACTUALLY resolved, read out of the JSON structure rather than
        // trusted from the helper: shot s0 is kept whole, so source time maps 1:1 onto the output
        // timeline and the two authored lines land at [5, 10) and [8, 15). Establishing the overlap
        // here keeps the flag assertion below from being vacuous for a second reason — if someone
        // edits the helper's lines apart, this fails and says so, instead of leaving an absent flag
        // to be misread as a missing feature.
        JsonElement linesElement = voElement.GetProperty("lines");
        List<(double Start, double End)> intervals = linesElement
            .EnumerateArray()
            .Select(l => (
                Start: l.GetProperty("outputStartSec").GetDouble(),
                End: l.GetProperty("outputStartSec").GetDouble() + l.GetProperty("playDurationSec").GetDouble()))
            .OrderBy(i => i.Start)
            .ToList();

        intervals.Should().HaveCount(2, "both voiceover lines must resolve for an overlap to exist at all");
        intervals[0].Start.Should().Be(5.0);
        intervals[0].End.Should().Be(10.0);
        intervals[1].Start.Should().Be(8.0);
        intervals[1].End.Should().Be(15.0);
        (intervals[0].Start < intervals[1].End && intervals[1].Start < intervals[0].End)
            .Should().BeTrue("the two lines must genuinely overlap, or the flag assertion proves nothing");

        // The overlap FLAG itself — the property this test is named for. A substring check on the
        // serialized document could never fail here (the assertion above already proves the
        // "voiceover" key is present), so the flag is read out of the node BY NAME and asserted as
        // a value: the test now fails if the flag stops being emitted.
        voElement.TryGetProperty("overlap", out JsonElement overlapElement)
            .Should().BeTrue("overlapping voiceover lines must annotate the voiceover node with an overlap flag");
        overlapElement.ValueKind.Should().Be(JsonValueKind.True);
        overlapElement.GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task T4_VoiceoverStepOrder_pointing_at_nonexistent_step_degrades_to_no_voiceover_applied()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: cfg => cfg with
            {
                EnableVoiceover = true,
                VoiceoverStepOrder = 99  // nonexistent step
            });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument outputDoc = JsonDocument.Parse(result.Output);
        outputDoc.RootElement.TryGetProperty("voiceover", out JsonElement voElement).Should().BeTrue();
        voElement.GetProperty("applied").GetBoolean().Should().BeFalse("voiceover should not be applied when step is nonexistent");
    }

    [Fact]
    public async Task Voiceover_headroom_section_added_to_compile_output_JSON_next_to_music_dialogueHeadroom()
    {
        VideoAnalysisArtifact artifact = WithAudioDescriptors(BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0) },
            offeredIds: new[] { "s0", "s1" }));

        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));
        StepExecutionContext context = CreateVoiceoverContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        edl.TryGetProperty("voiceover", out JsonElement voElement).Should().BeTrue();
        voElement.TryGetProperty("headroom", out JsonElement headroomElement).Should().BeTrue("voiceover.headroom must exist");
        headroomElement.GetProperty("applicable").GetBoolean().Should().BeTrue();
        headroomElement.GetProperty("meanHeadroomDb").GetDouble().Should().BeApproximately(-15.05 - (-24.5), 0.6, "line RMS minus the shot's measured dialogue RMS");
    }

    // =======================================================================
    // T-058 — voiceover lines are bare artifacts addressed by the key the producer wrote,
    // never by a ProjectFile row (which does not exist).
    // =======================================================================

    /// <summary>
    /// The two line ids the voiceover step's output advertises, and the two line ids the executor
    /// must therefore resolve.
    /// </summary>
    private static readonly string[] VoiceoverBareArtifactLineIds = ["vo1", "vo2"];

    /// <summary>
    /// The exact storage key <c>VoiceoverStepExecutor</c> wrote for a line — built here as a plain
    /// string, deliberately NOT via <c>ProjectFilePath.BuildStorageKey</c>, so the assertion is an
    /// independent statement of the on-disk layout rather than a mirror of the implementation.
    /// </summary>
    private static string ExpectedVoiceoverBareArtifactKey(string lineId) =>
        $"projects/{ProjectId}/agentFiles/voiceover/{VoiceoverBareArtifactExecutionId}/{lineId}.wav";

    /// <summary>
    /// Builds a compile context whose Voiceover step advertises <c>vo1</c> and <c>vo2</c>, with a
    /// FIXED execution id and a project-file listing that is EMPTY — the production truth. The
    /// producer stores each line through <c>UploadArtifactAsync</c>, which creates no
    /// <c>ProjectFile</c> row, so an empty listing is what a real compile sees.
    /// <paramref name="voiceoverDownloadKeys"/> captures every voiceover key the executor asks for.
    /// </summary>
    private static StepExecutionContext CreateVoiceoverBareArtifactContext(
        VideoAnalysisArtifact artifact,
        string decisionJson,
        out Mock<IProjectFileWorkspace> workspace,
        out List<string> voiceoverDownloadKeys,
        string? voiceoverOutputJsonOverride = null,
        Guid? executionIdOverride = null)
    {
        workspace = new Mock<IProjectFileWorkspace>();
        List<string> capturedKeys = [];
        voiceoverDownloadKeys = capturedKeys;

        string artifactJson = JsonSerializer.Serialize(artifact, ArtifactOptions());
        string voiceoverOutputJson = voiceoverOutputJsonOverride ?? BuildVoiceoverOutputJson(
            ("vo1", 2.0, 3.0),
            ("vo2", 12.0, 5.0));

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), AnalysisKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllText(destPath, artifactJson);
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), SourceVideoKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        // Scoped to the voiceover bare-artifact layout only, so the analysis/source setups above
        // keep their own behaviour. Configured last, so a test adding a further override for one
        // specific line (e.g. "this one is genuinely absent") wins by Moq's most-recent-wins
        // precedence without having to know about this setup at all.
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(),
                It.Is<string>(s => s.Contains("/agentFiles/voiceover/", StringComparison.Ordinal)),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, string, string, CancellationToken>((_, key, _, _) => capturedKeys.Add(key))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        // EMPTY on purpose: no ProjectFile row backs a bare voiceover artifact.
        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectWorkspaceFile>());

        VideoCompileStepConfig config = new(
            Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
            AnalysisStepOrder: 1,
            EnableVoiceover: true,
            VoiceoverStepOrder: 3);

        string configJson = JsonSerializer.Serialize(config, ConfigOptions());

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 4,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = configJson
        };

        // PreviousStepOutput: the analyze source is a step result, not a ProjectFile row, so this
        // run's ONLY potential Consumer of ListFilesAsync was the voiceover lookup under test.
        VideoAnalyzeStepConfig analyzeConfig = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput));
        var analyzeStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(analyzeConfig, ConfigOptions())
        };

        List<StepOutputHistoryEntry> history =
        [
            new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: SourceVideoKey, ArtifactStorageKey: null),
            new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
            new StepOutputHistoryEntry(2, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null),
            new StepOutputHistoryEntry(3, "Voiceover", voiceoverOutputJson, OutputStorageKey: null, ArtifactStorageKey: null)
        ];

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = executionIdOverride ?? VoiceoverBareArtifactExecutionId, ProjectId = ProjectId },
            Step = step,
            AllSteps = [analyzeStep, step],
            AccumulatedOutput = decisionJson,
            StepOutputHistory = history,
            CurrentStepIndex = 3,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    [Fact]
    public async Task Voiceover_lines_are_downloaded_by_the_bare_artifact_key_the_producer_wrote_without_consulting_the_project_file_table()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0) },
            offeredIds: new[] { "s0", "s1" });

        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));
        StepExecutionContext context = CreateVoiceoverBareArtifactContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace, out List<string> requestedKeys);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        requestedKeys.Should().BeEquivalentTo(
            new[]
            {
                ExpectedVoiceoverBareArtifactKey("vo1"),
                ExpectedVoiceoverBareArtifactKey("vo2")
            },
            because: "each line must be addressed at the exact key VoiceoverStepExecutor's "
                   + "UploadArtifactAsync wrote, under projects/{projectId}/agentFiles/voiceover/{executionId}/");

        workspace.Verify(
            w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a bare voiceover artifact has no ProjectFile row to look up — consulting the file "
            + "table is the defect: it always finds nothing and drops every line");

        edl.TryGetProperty("voiceover", out JsonElement voElement).Should().BeTrue("EDL must have voiceover key");
        voElement.GetProperty("applied").GetBoolean().Should().BeTrue("both lines resolved from their artifacts");
        voElement.GetProperty("lineCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Voiceover_line_whose_bare_artifact_is_missing_is_dropped_as_line_file_not_found_while_the_other_still_applies()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0) },
            offeredIds: new[] { "s0", "s1" });

        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));
        StepExecutionContext context = CreateVoiceoverBareArtifactContext(
            artifact, decisionJson, out Mock<IProjectFileWorkspace> workspace, out _);

        // vo2's object genuinely does not exist in the bucket.
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(),
                It.Is<string>(s => s.EndsWith("/vo2.wav", StringComparison.Ordinal)),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("NoSuchKey")
            {
                StatusCode = HttpStatusCode.NotFound,
                ErrorCode = "NoSuchKey"
            });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        edl.TryGetProperty("voiceover", out JsonElement voElement).Should().BeTrue();
        voElement.GetProperty("applied").GetBoolean().Should().BeTrue("vo1's artifact exists and must still apply");
        // DECLARED, not applied: the fixture declared vo1 AND vo2, and vo2's object is missing, so
        // this is the declared-2 / applied-1 partial shape. `lineCount` reports what the producer
        // was asked for; `applied`/`appliedLineCount` report what survived. The two assertions
        // therefore disagree on purpose — that distinction is the whole point of the key.
        voElement.GetProperty("lineCount").GetInt32().Should().Be(2);

        JsonElement dropped = voElement.GetProperty("dropped");
        dropped.EnumerateArray()
            .Select(d => (Reason: d.GetProperty("reason").GetString(), LineId: d.GetProperty("lineId").GetString()))
            .Should().ContainSingle()
            .Which.Should().Be(("line_file_not_found", "vo2"));
    }

    // =======================================================================
    // T-066 condition 1 — the consumer reads the content-addressed "storageKey"
    // the producer (T-065) now emits, and never derives a key from its OWN
    // execution id. Before this, a step-cache hit replayed a prior execution's
    // Voiceover output JSON while nothing had uploaded WAVs for this execution:
    // every line was dropped as line_file_not_found and the workflow still
    // reported Completed.
    // =======================================================================

    // Content-addressed keys are sha256(sanitizedText|voiceId|model) — no execution id anywhere.
    // These two fixed hex digests stand in for that shape.
    private const string VoiceoverContentHashA =
        "1111111111111111111111111111111111111111111111111111111111111111";
    private const string VoiceoverContentHashB =
        "2222222222222222222222222222222222222222222222222222222222222222";
    private const string VoiceoverContentHashC =
        "3333333333333333333333333333333333333333333333333333333333333333";

    /// <summary>The execution that PRODUCED the artifacts (the step-cache hit's origin).</summary>
    private static readonly Guid VoiceoverProducingExecutionId =
        Guid.Parse("aaaa1111-2222-3333-4444-555555555555");

    /// <summary>The execution that is COMPILING now — one no WAV was ever uploaded under.</summary>
    private static readonly Guid VoiceoverCompilingExecutionId =
        Guid.Parse("bbbb9999-8888-7777-6666-555555555555");

    /// <summary>
    /// The exact key <c>VoiceoverStepExecutor</c> uploads a line under since T-065:
    /// <c>projects/{projectId}/agentFiles/voiceover/lines/{sha256(content|voice|model)}.wav</c>.
    /// It carries no execution id, which is the whole point — it still names a real object when a
    /// LATER execution replays this step's output from the step cache.
    /// </summary>
    private static string ContentAddressedVoiceoverKey(string contentHash) =>
        $"projects/{ProjectId}/agentFiles/voiceover/lines/{contentHash}.wav";

    /// <summary>
    /// Mirrors the producer's own JSON: <c>storageKey</c> is written for an <c>ok</c> line and
    /// <b>omitted entirely</b> otherwise (JsonIgnore WhenWritingNull), which is why every reader has
    /// to use TryGetProperty — GetProperty throws on exactly the lines that have no key.
    /// </summary>
    private static string BuildVoiceoverOutputJsonWithStorageKeys(
        params (string Id, double StartSec, double DurationSec, string? StorageKey)[] lines) =>
        JsonSerializer.Serialize(new
        {
            view = new
            {
                lines = lines.Select(l => new
                {
                    id = l.Id,
                    startSec = l.StartSec,
                    durationSec = l.DurationSec,
                    status = "ok",
                    storageKey = l.StorageKey
                }).ToList()
            },
            meta = new { }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

    /// <summary>
    /// Makes every voiceover-line download fail as a genuinely absent object — the bucket state a
    /// cache-hit execution lands in when the producer's keys are ignored.
    /// </summary>
    private static void SetupVoiceoverDownloadsAsAbsent(Mock<IProjectFileWorkspace> workspace)
    {
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(),
                It.Is<string>(s => s.Contains("/agentFiles/voiceover/", StringComparison.Ordinal)),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("NoSuchKey")
            {
                StatusCode = HttpStatusCode.NotFound,
                ErrorCode = "NoSuchKey"
            });
    }

    [Fact]
    public async Task Voiceover_ResolvesLinesFromStorageKeysProducedByAnEarlierExecution_CacheHitNeverDropsEveryLine()
    {
        // CONDITION 1's headline regression. A repeat execution reaches the compile through a
        // Voiceover step whose output came from the STEP CACHE — it was produced by an earlier
        // execution. The compiling execution's id is therefore one no WAV was ever uploaded under,
        // so ANY key derived from it resolves nothing. That is the exact shape that yielded a green,
        // narration-less render: applied=false, every line dropped as line_file_not_found, the
        // compile still Completed.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0) },
            offeredIds: new[] { "s0", "s1" });

        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));

        string producingKey1 = ContentAddressedVoiceoverKey(VoiceoverContentHashA);
        string producingKey2 = ContentAddressedVoiceoverKey(VoiceoverContentHashB);

        string voiceoverOutputJson = BuildVoiceoverOutputJsonWithStorageKeys(
            ("vo1", 2.0, 3.0, producingKey1),
            ("vo2", 12.0, 5.0, producingKey2));

        StepExecutionContext context = CreateVoiceoverBareArtifactContext(
            artifact,
            decisionJson,
            out Mock<IProjectFileWorkspace> workspace,
            out List<string> requestedKeys,
            voiceoverOutputJsonOverride: voiceoverOutputJson,
            executionIdOverride: VoiceoverCompilingExecutionId);

        // The bucket contains exactly what execution A uploaded and NOTHING else — a real object
        // store has no object under an execution id that never produced one. Configured AFTER the
        // context's success setup, so Moq's most-recent-wins gives this predicate precedence for
        // every voiceover key that is not one of A's two: asking for a derived, execution-B-scoped
        // key gets a genuine NoSuchKey, which is precisely how the defect surfaced.
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(),
                It.Is<string>(s => s.Contains("/agentFiles/voiceover/", StringComparison.Ordinal)
                    && s != producingKey1 && s != producingKey2),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, string, string, CancellationToken>((_, key, _, _) => requestedKeys.Add(key))
            .ThrowsAsync(new AmazonS3Exception("NoSuchKey")
            {
                StatusCode = HttpStatusCode.NotFound,
                ErrorCode = "NoSuchKey"
            });

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        edl.TryGetProperty("voiceover", out JsonElement voElement).Should().BeTrue("EDL must have voiceover key");

        // The behaviour first, and the whole node in the failure message: on the pre-T-066 consumer
        // this assertion is the RED one, and it has to show BOTH facts — applied=false AND every line
        // dropped as line_file_not_found — alongside the execution-B-scoped keys it asked for.
        voElement.GetProperty("applied").GetBoolean().Should().BeTrue(
            "a step-cache hit brings its artifacts with it, so every line must apply. "
            + $"requestedKeys=[{string.Join(", ", requestedKeys)}]; voiceover={voElement.GetRawText()}");
        voElement.GetProperty("appliedLineCount").GetInt32().Should().Be(2);
        voElement.GetProperty("lineCount").GetInt32().Should().Be(2);
        voElement.GetProperty("dropped").GetArrayLength().Should().Be(0, "no line may be dropped");
        voElement.TryGetProperty("missingArtifactLineIds", out _).Should().BeFalse(
            "no line's artifact was missing");
        voElement.TryGetProperty("reason", out _).Should().BeFalse(
            "a fully applied voiceover node carries no degradation reason");

        requestedKeys.Should().BeEquivalentTo(
            new[] { producingKey1, producingKey2 },
            because: "the consumer must download the content-addressed keys the producer emitted; "
                   + "deriving a key from THIS execution's id finds nothing and drops every line");

        workspace.Verify(
            w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(),
                It.Is<string>(s => s.Contains(VoiceoverCompilingExecutionId.ToString(), StringComparison.Ordinal)),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "no object was ever uploaded under this execution's id — asking for one IS the defect: "
            + "it makes a cache hit indistinguishable from a completely failed synthesis");
    }

    [Fact]
    public async Task Voiceover_with_every_declared_line_missing_reports_all_lines_unavailable_and_names_each_line()
    {
        // CONDITION 5, compile layer. The compile cannot fix a missing artifact, but it must not
        // stay silent about it either: a green compile that simply has no narration is the failure
        // mode this test pins.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0) },
            offeredIds: new[] { "s0", "s1" });

        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));

        string missingKey1 = ContentAddressedVoiceoverKey(VoiceoverContentHashA);
        string missingKey2 = ContentAddressedVoiceoverKey(VoiceoverContentHashB);
        string missingKey3 = ContentAddressedVoiceoverKey(VoiceoverContentHashC);

        string voiceoverOutputJson = BuildVoiceoverOutputJsonWithStorageKeys(
            ("vo1", 2.0, 3.0, missingKey1),
            ("vo2", 8.0, 3.0, missingKey2),
            ("vo3", 12.0, 5.0, missingKey3));

        StepExecutionContext context = CreateVoiceoverBareArtifactContext(
            artifact,
            decisionJson,
            out Mock<IProjectFileWorkspace> workspace,
            out _,
            voiceoverOutputJsonOverride: voiceoverOutputJson,
            executionIdOverride: VoiceoverCompilingExecutionId);

        SetupVoiceoverDownloadsAsAbsent(workspace);

        var logger = new Mock<ILogger<VideoCompileStepExecutor>>();

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e, logger: logger.Object).ExecuteAsync(context);

        // Observability, never a new hard failure: the step still completes with a valid result.
        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonDocument.Parse(result.Output).RootElement.ValueKind.Should().Be(JsonValueKind.Object);

        edl.TryGetProperty("voiceover", out JsonElement voElement).Should().BeTrue("EDL must have voiceover key");
        voElement.GetProperty("applied").GetBoolean().Should().BeFalse();
        voElement.GetProperty("appliedLineCount").GetInt32().Should().Be(0);
        voElement.GetProperty("reason").GetString().Should().Be(
            "all_lines_unavailable",
            "lines were declared and not one resolved — the operator must be able to see that "
            + "without reading the ffmpeg log");

        voElement.GetProperty("missingArtifactLineIds").EnumerateArray()
            .Select(e => e.GetString()).Should().Equal("vo1", "vo2", "vo3");

        voElement.GetProperty("dropped").EnumerateArray()
            .Select(d => d.GetProperty("reason").GetString())
            .Should().AllBe("line_file_not_found");

        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains(missingKey1, StringComparison.Ordinal) &&
                    state.ToString()!.Contains("vo1", StringComparison.Ordinal)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeastOnce,
            "a missing voiceover artifact must be logged at Warning AND name the full storage key "
            + "and the line id, not just the id at Information level");
    }

    [Fact]
    public async Task Voiceover_with_one_missing_line_out_of_three_applies_the_other_two_and_does_not_claim_all_lines_unavailable()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0) },
            offeredIds: new[] { "s0", "s1" });

        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));

        string presentKey1 = ContentAddressedVoiceoverKey(VoiceoverContentHashA);
        string missingKey2 = ContentAddressedVoiceoverKey(VoiceoverContentHashB);
        string presentKey3 = ContentAddressedVoiceoverKey(VoiceoverContentHashC);

        string voiceoverOutputJson = BuildVoiceoverOutputJsonWithStorageKeys(
            ("vo1", 2.0, 3.0, presentKey1),
            ("vo2", 8.0, 3.0, missingKey2),
            ("vo3", 12.0, 5.0, presentKey3));

        StepExecutionContext context = CreateVoiceoverBareArtifactContext(
            artifact,
            decisionJson,
            out Mock<IProjectFileWorkspace> workspace,
            out _,
            voiceoverOutputJsonOverride: voiceoverOutputJson,
            executionIdOverride: VoiceoverCompilingExecutionId);

        // Exactly one object is absent.
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(),
                It.Is<string>(s => s == missingKey2),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("NoSuchKey")
            {
                StatusCode = HttpStatusCode.NotFound,
                ErrorCode = "NoSuchKey"
            });

        JsonElement edl = default;
        var logger = new Mock<ILogger<VideoCompileStepExecutor>>();
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e, logger: logger.Object).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        edl.TryGetProperty("voiceover", out JsonElement voElement).Should().BeTrue();
        voElement.GetProperty("applied").GetBoolean().Should().BeTrue(
            "two of three lines resolved, so the voiceover IS applied");
        voElement.GetProperty("appliedLineCount").GetInt32().Should().Be(2);
        voElement.GetProperty("missingArtifactLineIds").EnumerateArray()
            .Select(e => e.GetString()).Should().Equal("vo2");
        voElement.TryGetProperty("reason", out _).Should().BeFalse(
            "a partial resolve is not all_lines_unavailable");

        voElement.GetProperty("dropped").EnumerateArray()
            .Select(d => (Reason: d.GetProperty("reason").GetString(), LineId: d.GetProperty("lineId").GetString()))
            .Should().ContainSingle()
            .Which.Should().Be(("line_file_not_found", "vo2"));

        // The log level is asserted on the PARTIAL path too, not only on the all-missing one: the
        // warning is emitted per missing line, so a regression that downgraded just this branch
        // would leave every other assertion in this file green and the operator with no signal.
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains(missingKey2, StringComparison.Ordinal) &&
                    state.ToString()!.Contains("vo2", StringComparison.Ordinal)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeastOnce,
            "one of three artifacts is missing, so this line too must be logged at Warning naming "
            + "the full storage key and the line id");
    }

    [Fact]
    public async Task Voiceover_line_without_a_storageKey_falls_back_to_the_legacy_execution_scoped_key()
    {
        // The absent case is per LINE, not per step: T-065 omits "storageKey" entirely on
        // skipped/failed lines and on every step output persisted before it, so a reader that used
        // GetProperty would THROW on those and a reader that used one key shape for the whole step
        // would resolve the wrong object for the other line.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0), ("s1", 10.0, 20.0) },
            offeredIds: new[] { "s0", "s1" });

        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));

        string contentAddressedKey = ContentAddressedVoiceoverKey(VoiceoverContentHashA);

        string voiceoverOutputJson = BuildVoiceoverOutputJsonWithStorageKeys(
            ("vo1", 2.0, 3.0, contentAddressedKey),
            ("vo2", 12.0, 5.0, null));

        StepExecutionContext context = CreateVoiceoverBareArtifactContext(
            artifact,
            decisionJson,
            out Mock<IProjectFileWorkspace> workspace,
            out List<string> requestedKeys,
            voiceoverOutputJsonOverride: voiceoverOutputJson);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        requestedKeys.Should().BeEquivalentTo(
            new[]
            {
                contentAddressedKey,
                ExpectedVoiceoverBareArtifactKey("vo2")
            },
            because: "vo1 carries T-065's content-addressed key while vo2 has none and can only "
                   + "mean the pre-T-065 execution-scoped key");

        edl.TryGetProperty("voiceover", out JsonElement voElement).Should().BeTrue();
        voElement.GetProperty("appliedLineCount").GetInt32().Should().Be(2);
    }
}
