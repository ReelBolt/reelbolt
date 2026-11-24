using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-206: a graphics plan written by a PinInsert step names server-resolved content (an earlier run's render, a
/// project file id), so the compile composites it as-is; a model-written plan naming the same key is still
/// re-anchored to this run's prefix and never reads the other run's object.
/// </summary>
public partial class VideoCompileStepExecutorTests
{
    private static StepExecutionContext PinnedPlanContext(string assetKey, StepType planStepType, out Mock<IProjectFileWorkspace> workspace)
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
        artifact = artifact with { InsertRegions = [BuildInsertTrack()], OfferedInsertRegionIds = ["r0"] };

        StepExecutionContext context = CreateGraphicsContext(
            artifact, BuildDecisionJson(("s0", "s0", "keep")), BuildInsertsPlanJson("r0", assetKey),
            out workspace,
            configOverride: cfg => cfg with { EnableGraphics = false, EnableInserts = true, PrePaddingMs = 0, PostPaddingMs = 0 });
        context.AllSteps.Add(new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 3, StepType = planStepType });
        workspace.Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        MockAssetDownload(workspace, assetKey);
        return context;
    }

    [Fact]
    public async Task A_PinInsert_plan_composites_an_earlier_runs_render_as_is()
    {
        string key = PriorRenderKey("author-render.mp4");
        StepExecutionContext context = PinnedPlanContext(key, StepType.PinInsert, out Mock<IProjectFileWorkspace> workspace);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement inserts = edl.GetProperty("inserts");
        inserts.GetProperty("applied").GetBoolean().Should().BeTrue();
        inserts.GetProperty("appliedInserts")[0].GetProperty("contentOrigin").GetString().Should().Be("pinned");
        workspace.Verify(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), key, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_model_written_plan_naming_the_same_key_is_still_re_anchored_and_never_reads_the_other_run()
    {
        string key = PriorRenderKey("author-render.mp4");
        StepExecutionContext context = PinnedPlanContext(key, StepType.Agent, out Mock<IProjectFileWorkspace> workspace);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        workspace.Verify(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), key, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("other-project")]
    [InlineData("traversal")]
    [InlineData("not-media")]
    public async Task A_PinInsert_plan_key_outside_the_project_or_not_media_is_dropped(string variant)
    {
        string key = variant switch
        {
            "other-project" => $"projects/{Guid.NewGuid()}/outputFiles/{Guid.NewGuid():D}/x.mp4",
            "traversal" => $"projects/{ProjectId}/outputFiles/../../other/x.mp4",
            _ => PriorRenderKey("narration.mp3")
        };
        StepExecutionContext context = PinnedPlanContext(key, StepType.PinInsert, out Mock<IProjectFileWorkspace> workspace);

        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        edl.GetProperty("inserts").GetProperty("droppedInserts")[0].GetProperty("reason").GetString().Should().Be("invalid_asset_storage_key");
        workspace.Verify(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), key, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
