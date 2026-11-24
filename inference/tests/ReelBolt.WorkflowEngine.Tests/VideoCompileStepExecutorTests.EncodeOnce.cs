using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Encode once after the review (docs/video-editing.md "Encoding once after the review"): a Plan-stage compile resolves
/// every fact the review reads and encodes nothing; the Final-stage compile after the review encodes the same cut once.
/// The encoder here is the executor's own mocked ffmpeg: an encode is the call whose last argument is the output file.
/// </summary>
public partial class VideoCompileStepExecutorTests
{
    private static bool IsEncodeCall(IReadOnlyList<string> args) => args.Count > 0 && args[^1].EndsWith("edited.mp4", StringComparison.Ordinal);

    private static (StepExecutionContext Context, Mock<IProjectFileWorkspace> Workspace) EncodeOnceContext(CompileEncodeStage? stage)
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });
        StepExecutionContext context = CreateContext(
            artifact, BuildDecisionJson(("s0", "s0", "keep")), out Mock<IProjectFileWorkspace> workspace,
            configOverride: c => c with { EncodeStage = stage });
        return (context, workspace);
    }

    private static JsonObject Facts(StepExecutionResult result)
    {
        JsonObject node = JsonNode.Parse(result.Output)!.AsObject();
        foreach (string key in new[] { "outputStorageKey", "timelineStorageKey", "artifactStorageKey", "planOnly", "encoded" })
            node.Remove(key);
        return node;
    }

    [Fact]
    public async Task A_plan_stage_compile_resolves_the_plan_and_encodes_nothing()
    {
        (StepExecutionContext context, Mock<IProjectFileWorkspace> workspace) = EncodeOnceContext(CompileEncodeStage.Plan);
        List<IReadOnlyList<string>> calls = [];
        var capture = new CapturingSink();
        var recorder = new ReelBolt.Shared.Metering.UsageRecorder(
            new NonChatPriceService(), capture, NullLogger<ReelBolt.Shared.Metering.UsageRecorder>.Instance);

        StepExecutionResult result;
        using (ReelBolt.Shared.Metering.UsageScope.Begin(Guid.NewGuid(), null, null, context.StepResultId, null, UsageSource.Workflow))
            result = await CreateExecutor(workspace, ffmpegArgsCaptured: calls.Add, usageRecorder: recorder).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        calls.Where(IsEncodeCall).Should().BeEmpty("a plan pass never runs the encoder");
        result.OutputStorageKey.Should().BeNull();
        result.TimelineStorageKey.Should().BeNull();
        result.ArtifactStorageKey.Should().BeNull("a plan pass writes nothing, not even the EDL");

        JsonObject summary = JsonNode.Parse(result.Output)!.AsObject();
        summary["planOnly"]!.GetValue<bool>().Should().BeTrue();
        summary["encoded"]!.GetValue<bool>().Should().BeFalse();
        summary.ContainsKey("outputStorageKey").Should().BeFalse();
        summary.ContainsKey("artifactStorageKey").Should().BeFalse();

        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);
        capture.Events.Should().BeEmpty("no encode, no usage");
        workspace.Verify(w => w.UploadBinaryFileAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SummaryStatus>(),
            It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()), Times.Never);
        workspace.Verify(w => w.RegisterStoredObjectAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<SummaryStatus>(),
            It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task A_plan_stage_compile_reports_the_same_facts_as_a_real_compile()
    {
        (StepExecutionContext planContext, Mock<IProjectFileWorkspace> planWorkspace) = EncodeOnceContext(CompileEncodeStage.Plan);
        (StepExecutionContext realContext, Mock<IProjectFileWorkspace> realWorkspace) = EncodeOnceContext(null);

        StepExecutionResult plan = await CreateExecutor(planWorkspace).ExecuteAsync(planContext);
        StepExecutionResult real = await CreateExecutor(realWorkspace).ExecuteAsync(realContext);

        plan.Status.Should().Be(StepStatus.Completed, because: plan.ErrorDetails ?? plan.Output);
        real.Status.Should().Be(StepStatus.Completed, because: real.ErrorDetails ?? real.Output);
        JsonObject planFacts = Facts(plan);
        planFacts.ContainsKey("sentenceCheck").Should().BeTrue();
        planFacts.ContainsKey("outputDurationSec").Should().BeTrue();
        planFacts.ContainsKey("retainedRatio").Should().BeTrue();
        planFacts.ContainsKey("segments").Should().BeTrue();
        planFacts.ContainsKey("droppedSegmentsOverCap").Should().BeTrue();
        planFacts.ToJsonString().Should().Be(Facts(real).ToJsonString(), "the review must see the numbers a real compile would have reported");
    }

    [Fact]
    public async Task A_three_pass_loop_encodes_exactly_once()
    {
        List<IReadOnlyList<string>> calls = [];
        var capture = new CapturingSink();
        var recorder = new ReelBolt.Shared.Metering.UsageRecorder(
            new NonChatPriceService(), capture, NullLogger<ReelBolt.Shared.Metering.UsageRecorder>.Instance);
        var orgId = Guid.NewGuid();

        for (int pass = 0; pass < 3; pass++)
        {
            (StepExecutionContext context, Mock<IProjectFileWorkspace> workspace) = EncodeOnceContext(CompileEncodeStage.Plan);
            StepExecutionResult plan;
            using (ReelBolt.Shared.Metering.UsageScope.Begin(orgId, null, null, context.StepResultId, null, UsageSource.Workflow))
                plan = await CreateExecutor(workspace, ffmpegArgsCaptured: calls.Add, usageRecorder: recorder).ExecuteAsync(context);
            plan.Status.Should().Be(StepStatus.Completed, because: plan.ErrorDetails ?? plan.Output);
        }

        (StepExecutionContext finalContext, Mock<IProjectFileWorkspace> finalWorkspace) = EncodeOnceContext(CompileEncodeStage.Final);
        StepExecutionResult final;
        using (ReelBolt.Shared.Metering.UsageScope.Begin(orgId, null, null, finalContext.StepResultId, null, UsageSource.Workflow))
            final = await CreateExecutor(finalWorkspace, ffmpegArgsCaptured: calls.Add, usageRecorder: recorder).ExecuteAsync(finalContext);

        final.Status.Should().Be(StepStatus.Completed, because: final.ErrorDetails ?? final.Output);
        final.OutputStorageKey.Should().NotBeNull("the final row carries the output");
        final.TimelineStorageKey.Should().NotBeNull("the final row carries the timeline");
        final.ArtifactStorageKey.Should().NotBeNull("the final row carries the artifact");
        calls.Where(IsEncodeCall).Should().HaveCount(1, "three plan passes and one final: the encoder runs once");

        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);
        capture.Events.Should().ContainSingle().Which.Kind.Should().Be(UsageKind.EncodeOutputSeconds);
    }

    [Fact]
    public async Task A_final_stage_compile_is_byte_identical_to_a_compile_with_no_stage()
    {
        (StepExecutionContext finalContext, Mock<IProjectFileWorkspace> finalWorkspace) = EncodeOnceContext(CompileEncodeStage.Final);
        (StepExecutionContext offContext, Mock<IProjectFileWorkspace> offWorkspace) = EncodeOnceContext(null);

        StepExecutionResult final = await CreateExecutor(finalWorkspace).ExecuteAsync(finalContext);
        StepExecutionResult off = await CreateExecutor(offWorkspace).ExecuteAsync(offContext);

        off.Output.Should().NotContain("planOnly").And.NotContain("\"encoded\"", "a compile with no stage keeps today's output shape");
        // Each context has its own execution id, which the storage keys carry; nothing else may differ.
        static string Same(string? text) => System.Text.RegularExpressions.Regex.Replace(text ?? string.Empty, "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "<id>");
        Same(final.Output).Should().Be(Same(off.Output));
        Same(final.OutputStorageKey).Should().Be(Same(off.OutputStorageKey));
        Same(final.TimelineStorageKey).Should().Be(Same(off.TimelineStorageKey));
    }

    [Fact]
    public async Task A_failed_final_encode_fails_the_step_and_records_no_usage()
    {
        (StepExecutionContext context, Mock<IProjectFileWorkspace> workspace) = EncodeOnceContext(CompileEncodeStage.Final);
        var capture = new CapturingSink();
        var recorder = new ReelBolt.Shared.Metering.UsageRecorder(
            new NonChatPriceService(), capture, NullLogger<ReelBolt.Shared.Metering.UsageRecorder>.Instance);

        StepExecutionResult result = await CreateExecutor(workspace, failEncode: true, usageRecorder: recorder).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("ENCODE_FAILED");
        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);
        capture.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task A_stage_is_refused_on_a_config_whose_result_the_review_would_read_from_the_encode()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        StepExecutionContext context = CreateContext(
            artifact, BuildDecisionJson(("s0", "s0", "keep")), out Mock<IProjectFileWorkspace> workspace,
            configOverride: c => c with { EncodeStage = CompileEncodeStage.Plan, EnableMusic = true });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("CONFIG_INVALID");
        result.Output.Should().Contain("music");
    }

    [Fact]
    public async Task A_plan_stage_is_refused_while_a_review_over_it_has_its_visual_check_on()
    {
        (StepExecutionContext context, Mock<IProjectFileWorkspace> workspace) = EncodeOnceContext(CompileEncodeStage.Plan);
        WorkflowStep review = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 4,
            StepType = StepType.ReviewLoop,
            LoopTargetStepOrder = 2,
            ReviewLoopConfigJson = """{"visualCheck":{"enabled":true}}"""
        };
        context.AllSteps.Add(review);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("CONFIG_INVALID");
        result.Output.Should().Contain("visual check");
    }

    [Fact]
    public async Task A_plan_stage_uses_the_stored_probe_and_does_not_stage_the_source()
    {
        VideoAnalysisArtifact baseArtifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        VideoAnalysisArtifact artifact = baseArtifact with
        {
            Sources = [new VideoAnalysisSourceInfo(0, SourceVideoKey, baseArtifact.Media with { VideoCodec = "h264", AudioCodec = "aac" })]
        };
        StepExecutionContext context = CreateContext(
            artifact, BuildDecisionJson(("s0", "s0", "keep")), out Mock<IProjectFileWorkspace> workspace,
            configOverride: c => c with { EncodeStage = CompileEncodeStage.Plan });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        result.OutputStorageKey.Should().BeNull();
        workspace.Verify(w => w.DownloadStorageKeyToFileAsync(
            It.IsAny<Guid>(), SourceVideoKey, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "the analysis already probed this source, so a plan pass need not download it again");
    }

    [Fact]
    public void EncodeStage_is_read_off_a_stored_step_and_a_missing_one_means_encode_every_pass()
    {
        WorkflowStep Compile(string? json) => new() { StepType = StepType.VideoCompile, VideoCompileConfigJson = json };

        CompileEncodeStages.Of(Compile("""{"version":1,"decision":{"from":"Previous"},"analysisStepOrder":1,"encodeStage":"Plan"}""")).Should().Be(CompileEncodeStage.Plan);
        CompileEncodeStages.Of(Compile("""{"version":1,"decision":{"from":"Previous"},"analysisStepOrder":1,"encodeStage":"Final"}""")).Should().Be(CompileEncodeStage.Final);
        CompileEncodeStages.Of(Compile("""{"version":1,"decision":{"from":"Previous"},"analysisStepOrder":1}""")).Should().BeNull();
        CompileEncodeStages.Of(Compile("not json")).Should().BeNull();
        CompileEncodeStages.Of(Compile(null)).Should().BeNull();
        CompileEncodeStages.IsPlanStage(new WorkflowStep { StepType = StepType.Agent, VideoCompileConfigJson = """{"encodeStage":"Plan"}""" }).Should().BeFalse();
    }
}
