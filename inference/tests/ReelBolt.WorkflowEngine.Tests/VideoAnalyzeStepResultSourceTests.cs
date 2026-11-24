using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-010: a <see cref="VideoSourceKind.StepResult"/> source resolves a step result of an EARLIER run, but
/// only inside the execution's own project and only for an addressable output of that project (renders under
/// <c>outputFiles/</c>, VideoGenerate takes under <c>agentFiles/generated/</c>). Foreign, missing, pruned and out-of-prefix answer the same
/// <c>SOURCE_NOT_FOUND</c>, and nothing is downloaded for any of them.
/// </summary>
[Collection(VideoFilterProbeCacheCollection.Name)]
public class VideoAnalyzeStepResultSourceTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid OtherProjectId = Guid.NewGuid();

    private static string OwnKey => $"projects/{ProjectId}/outputFiles/{Guid.NewGuid():D}/film.mp4";

    /// <summary>The REAL key shape VideoGenerate delivers a take under (VideoGenerateStepExecutor).</summary>
    private static string GeneratedKey(int step = 2, int take = 1) =>
        $"projects/{ProjectId}/agentFiles/generated/{Guid.NewGuid():D}/step-{step}-v{take}.mp4";

    private static (IServiceScopeFactory Scopes, Guid ResultId) SeedResult(
        Guid projectId, string? outputKey, StepStatus status = StepStatus.Completed)
    {
        DbContextOptions<WorkflowEngineDbContext> options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        WorkflowEngineDbContext db = new(options);
        WorkflowExecution run = new() { Id = Guid.NewGuid(), ProjectId = projectId };
        WorkflowStepResult result = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowExecution = run,
            OutputStorageKey = outputKey,
            Status = status
        };
        db.WorkflowExecutions.Add(run);
        db.WorkflowStepResults.Add(result);
        db.SaveChanges();

        ServiceCollection services = new();
        services.AddSingleton(db);
        return (services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), result.Id);
    }

    [Fact]
    public async Task A_same_project_step_result_resolves_and_its_object_is_the_one_downloaded()
    {
        string key = OwnKey;
        (IServiceScopeFactory scopes, Guid resultId) = SeedResult(ProjectId, key);
        Mock<IProjectFileWorkspace> workspace = Workspace();

        StepExecutionResult result = await Executor(workspace, scopes).ExecuteAsync(Context(resultId));

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails ?? result.Output);
        workspace.Verify(w => w.DownloadStorageKeyToFileAsync(
            It.IsAny<Guid>(), key, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task A_step_result_of_another_project_is_SOURCE_NOT_FOUND_and_nothing_is_downloaded()
    {
        string foreignKey = $"projects/{OtherProjectId}/outputFiles/{Guid.NewGuid():D}/film.mp4";
        (IServiceScopeFactory scopes, Guid resultId) = SeedResult(OtherProjectId, foreignKey);
        Mock<IProjectFileWorkspace> workspace = Workspace();

        StepExecutionResult result = await Executor(workspace, scopes).ExecuteAsync(Context(resultId));

        AssertRefused(result, workspace, resultId);
    }

    [Fact]
    public async Task The_project_check_holds_on_its_own_even_when_a_foreign_row_carries_a_key_under_our_prefix()
    {
        (IServiceScopeFactory scopes, Guid resultId) = SeedResult(OtherProjectId, OwnKey);
        Mock<IProjectFileWorkspace> workspace = Workspace();

        StepExecutionResult result = await Executor(workspace, scopes).ExecuteAsync(Context(resultId));

        AssertRefused(result, workspace, resultId);
    }

    [Fact]
    public async Task A_missing_step_result_is_SOURCE_NOT_FOUND_with_the_same_message_as_a_foreign_one()
    {
        (IServiceScopeFactory foreignScopes, Guid foreignId) = SeedResult(OtherProjectId,
            $"projects/{OtherProjectId}/outputFiles/{Guid.NewGuid():D}/film.mp4");
        (IServiceScopeFactory emptyScopes, _) = SeedResult(ProjectId, OwnKey);
        Guid missingId = Guid.NewGuid();

        StepExecutionResult foreign = await Executor(Workspace(), foreignScopes).ExecuteAsync(Context(foreignId));
        StepExecutionResult missing = await Executor(Workspace(), emptyScopes).ExecuteAsync(Context(missingId));

        foreign.Output.Should().Contain("SOURCE_NOT_FOUND");
        missing.Output.Should().Contain("SOURCE_NOT_FOUND");
        foreign.Output.Replace(foreignId.ToString(), "<id>").Should().Be(missing.Output.Replace(missingId.ToString(), "<id>"));
    }

    [Fact]
    public async Task A_pruned_step_result_with_no_output_key_is_SOURCE_NOT_FOUND()
    {
        (IServiceScopeFactory scopes, Guid resultId) = SeedResult(ProjectId, null);
        Mock<IProjectFileWorkspace> workspace = Workspace();

        StepExecutionResult result = await Executor(workspace, scopes).ExecuteAsync(Context(resultId));

        AssertRefused(result, workspace, resultId);
    }

    [Theory]
    [InlineData("projects/{p}/agentFiles/video-analysis/x/step-1.mp4")]
    [InlineData("projects/{p}/outputFiles")]
    [InlineData("projects/{p}/outputFilesX/film.mp4")]
    [InlineData("projects/{o}/outputFiles/e/film.mp4")]
    public async Task A_key_outside_the_projects_outputFiles_prefix_is_refused(string template)
    {
        string key = template.Replace("{p}", ProjectId.ToString()).Replace("{o}", OtherProjectId.ToString());
        (IServiceScopeFactory scopes, Guid resultId) = SeedResult(ProjectId, key);
        Mock<IProjectFileWorkspace> workspace = Workspace();

        StepExecutionResult result = await Executor(workspace, scopes).ExecuteAsync(Context(resultId));

        AssertRefused(result, workspace, resultId);
    }

    [Fact]
    public async Task Without_a_scope_factory_a_StepResult_source_is_refused_rather_than_resolved()
    {
        Mock<IProjectFileWorkspace> workspace = Workspace();

        StepExecutionResult result = await Executor(workspace, scopes: null).ExecuteAsync(Context(Guid.NewGuid()));

        result.Status.Should().Be(StepStatus.Failed);
        result.Output.Should().Contain("SOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task A_VideoGenerate_clip_under_agentFiles_generated_resolves_and_is_the_object_downloaded()
    {
        string key = GeneratedKey();
        (IServiceScopeFactory scopes, Guid resultId) = SeedResult(ProjectId, key);
        Mock<IProjectFileWorkspace> workspace = Workspace();

        StepExecutionResult result = await Executor(workspace, scopes).ExecuteAsync(Context(resultId));

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails ?? result.Output);
        workspace.Verify(w => w.DownloadStorageKeyToFileAsync(
            It.IsAny<Guid>(), key, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task A_failed_step_result_is_SOURCE_NOT_PLAYABLE_saying_it_failed()
    {
        (IServiceScopeFactory scopes, Guid resultId) = SeedResult(ProjectId, GeneratedKey(), StepStatus.Failed);
        Mock<IProjectFileWorkspace> workspace = Workspace();

        StepExecutionResult result = await Executor(workspace, scopes).ExecuteAsync(Context(resultId));

        AssertNotPlayable(result, workspace, StepResultSourceResolver.FailedMessage);
    }

    [Theory]
    [InlineData(StepStatus.Pending)]
    [InlineData(StepStatus.Running)]
    [InlineData(StepStatus.Skipped)]
    public async Task An_unfinished_step_result_is_SOURCE_NOT_PLAYABLE_saying_it_has_not_finished(StepStatus status)
    {
        (IServiceScopeFactory scopes, Guid resultId) = SeedResult(ProjectId, OwnKey, status);
        Mock<IProjectFileWorkspace> workspace = Workspace();

        StepExecutionResult result = await Executor(workspace, scopes).ExecuteAsync(Context(resultId));

        AssertNotPlayable(result, workspace, StepResultSourceResolver.UnfinishedMessage);
    }

    [Fact]
    public async Task A_completed_step_result_whose_output_is_not_media_is_SOURCE_NOT_PLAYABLE_saying_not_a_video()
    {
        (IServiceScopeFactory scopes, Guid resultId) = SeedResult(ProjectId,
            $"projects/{ProjectId}/outputFiles/{Guid.NewGuid():D}/report.json");
        Mock<IProjectFileWorkspace> workspace = Workspace();

        StepExecutionResult result = await Executor(workspace, scopes).ExecuteAsync(Context(resultId));

        AssertNotPlayable(result, workspace, StepResultSourceResolver.NotMediaMessage);
    }

    [Fact]
    public void SOURCE_NOT_PLAYABLE_is_a_user_input_failure_not_a_refund()
    {
        FailureClassifier.ForCode(StepResultSourceResolver.NotPlayableCode).Should().Be(FailureClass.UserInput);
    }

    private static void AssertNotPlayable(StepExecutionResult result, Mock<IProjectFileWorkspace> workspace, string message)
    {
        result.Status.Should().Be(StepStatus.Failed);
        result.Output.Should().Contain("SOURCE_NOT_PLAYABLE").And.Contain(message).And.NotContain("SOURCE_NOT_FOUND");
        result.Output.Should().NotContain("projects/", "a refusal never echoes a storage key");
        workspace.Verify(w => w.DownloadStorageKeyToFileAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void SOURCE_NOT_FOUND_is_a_user_input_failure_not_a_refund()
    {
        FailureClassifier.ForCode("SOURCE_NOT_FOUND").Should().Be(FailureClass.UserInput);
    }

    private static void AssertRefused(StepExecutionResult result, Mock<IProjectFileWorkspace> workspace, Guid resultId)
    {
        result.Status.Should().Be(StepStatus.Failed);
        result.Output.Should().Contain("SOURCE_NOT_FOUND");
        result.Output.Should().NotContain("projects/", "a refusal never echoes a storage key");
        workspace.Verify(w => w.DownloadStorageKeyToFileAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- harness ----

    private static Mock<IProjectFileWorkspace> Workspace()
    {
        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectWorkspaceFile>());
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) => { File.WriteAllBytes(dest, new byte[10]); return Task.CompletedTask; });
        workspace
            .Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync("projects/p/agentFiles/video-analysis/e/step-1-analysis.json");
        return workspace;
    }

    private static StepExecutionContext Context(Guid stepResultId)
    {
        VideoAnalyzeStepConfig config = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.StepResult, StepResultId: stepResultId),
            DetectSilence: false,
            Transcription: VideoTranscriptionMode.Off);
        string configJson = JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        });
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = configJson
        };
        return new StepExecutionContext
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
    }

    private static VideoAnalyzeStepExecutor Executor(Mock<IProjectFileWorkspace> workspace, IServiceScopeFactory? scopes)
    {
        var probe = new Mock<IMediaProbe>();
        probe.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(6, 30, 1, 1280, 720, "h264", null, null));
        probe.Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(6, 30, 1, 1280, 720, "h264", null, null));
        var silence = new Mock<ISilenceDetector>();
        silence.Setup(s => s.DetectAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(double, double)>());
        var shots = new Mock<IShotDetector>();
        shots.Setup(s => s.DetectShotsAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(double, double)> { (0.0, 6.0) });
        shots.Setup(s => s.DetectShotsAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(double, double)> { (0.0, 6.0) });
        var audioExtractor = new Mock<IAudioExtractor>();
        audioExtractor
            .Setup(a => a.ExtractWavAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<IMediaWorkspace, string, string, CancellationToken>((ws, _, outPath, ct) => ws.WriteBytesAsync(outPath, new byte[100], ct));

        var options = Options.Create(new VideoEditingOptions
        {
            ScratchPath = Path.Combine(Path.GetTempPath(), "video-stepresult-source-tests", Guid.NewGuid().ToString("N")),
            MaxConcurrentJobs = 1,
            AnalyzeTimeoutSeconds = 30,
            CompileTimeoutSeconds = 30
        });

        return new VideoAnalyzeStepExecutor(
            probe.Object, silence.Object, shots.Object, audioExtractor.Object,
            new Mock<IFrameGridSampler>().Object, new Mock<IKeyframeExtractor>().Object, new Mock<ISharpnessSampler>().Object,
            new Mock<IShotCaptioner>().Object, workspace.Object, new Mock<ITranscriptionClientFactory>().Object,
            new Mock<IInferenceProviderResolver>().Object, options, NullLogger<VideoAnalyzeStepExecutor>.Instance,
            scopeFactory: scopes);
    }
}
