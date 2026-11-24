using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Production 2026-11-13: "stage 'analysis.json' failed on the runner: the object store answered 404" surfaced
/// as an unclassified "Unexpected error". A missing analysis artifact is a named failure with a class, a
/// plain customer sentence (never a storage key) and a log line that carries the key.
/// </summary>
public class RemoteAnalysisArtifactStageTests
{
    private const string Key = "projects/p/agentFiles/video-analysis/e/step-1-analysis.json";
    private static readonly Guid ProjectId = Guid.NewGuid();

    private static RunnerSessionOpException Stage404() => new(
        "stage_failed", "stage 'analysis.json' failed on the runner: the object store answered 404", sessionLost: false, retryable: false);

    // ---- the shared helper -------------------------------------------------------------------

    [Fact]
    public async Task A_404_on_the_analysis_artifact_is_reported_false_and_logs_the_key()
    {
        var workspace = new Mock<IMediaWorkspace>();
        workspace.Setup(w => w.StageAsync(Key, "analysis.json", It.IsAny<CancellationToken>())).ThrowsAsync(Stage404());
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<Microsoft.Extensions.Logging.LogLevel>())).Returns(true);

        bool staged = await AnalysisArtifactStaging.TryStageAsync(workspace.Object, Key, "analysis.json", logger.Object, CancellationToken.None);

        staged.Should().BeFalse();
        logger.Invocations.Any(i => i.Arguments.Count > 2 && (i.Arguments[2]?.ToString() ?? "").Contains(Key)).Should().BeTrue("the key belongs in the log");
    }

    [Fact]
    public async Task Other_stage_failures_are_not_swallowed()
    {
        var workspace = new Mock<IMediaWorkspace>();
        workspace.Setup(w => w.StageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RunnerSessionOpException("stage_failed", "stage 'x' failed on the runner: the object store answered 503", false, true));

        Func<Task> act = () => AnalysisArtifactStaging.TryStageAsync(workspace.Object, "k", "analysis.json", NullLogger.Instance, CancellationToken.None);

        await act.Should().ThrowAsync<RunnerSessionOpException>();
    }

    [Fact]
    public async Task A_successful_stage_returns_true()
    {
        var workspace = new Mock<IMediaWorkspace>();

        (await AnalysisArtifactStaging.TryStageAsync(workspace.Object, "k", "analysis.json", NullLogger.Instance, CancellationToken.None))
            .Should().BeTrue();
    }

    [Fact]
    public void The_failure_code_is_classified_platform_internal_so_it_stays_refundable()
    {
        FailureClassifier.Classify(FailureClass.None, AnalysisArtifactStaging.UnavailableCode, null)
            .Should().Be(FailureClass.PlatformInternal);
    }

    [Fact]
    public void The_customer_messages_carry_no_storage_key()
    {
        AnalysisArtifactStaging.ReadMessage.Should().NotContain("projects/").And.NotContain(".json");
        AnalysisArtifactStaging.WriteMessage.Should().NotContain("projects/").And.NotContain(".json");
    }

    // ---- C: the matcher is pinned to the worker's wording ------------------------------------

    [Fact]
    public void The_matcher_reads_the_wording_the_Go_worker_writes()
    {
        string root = FindRepoRoot();
        foreach (string file in new[] { "transfer.go", "sourcecache.go" })
        {
            string go = File.ReadAllText(Path.Combine(root, "sandbox", "pkg", "agent", file));
            go.Should().Contain("the object store answered %d", $"{file} is where the 404 text is produced");
        }

        RunnerSessionOpException.ObjectStoreNotFoundText.Should().Be(string.Format("the object store answered {0}", 404));
        Stage404().IsObjectStoreNotFound.Should().BeTrue();
        new RunnerSessionOpException("stage_failed", "the object store answered 403", false, false).IsObjectStoreNotFound.Should().BeFalse();
        new RunnerSessionOpException("op_failed", "the object store answered 404", false, false).IsObjectStoreNotFound.Should().BeFalse();
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    // ---- A: the VideoCompile call site, through the executor ---------------------------------

    private sealed class Factory(IMediaWorkspace workspace) : IMediaWorkspaceFactory
    {
        public Task<MediaWorkspaceOpenResult> OpenAsync(MediaWorkspaceRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(MediaWorkspaceOpenResult.Opened(new MediaWorkspaceLease(
                workspace, scratch: null, ComputeDecision.Local(ComputeDecisionReasons.LocalMode),
                job: null, request.ExecutionId, request.StepOrder, NullLogger.Instance)));
    }

    private static JsonSerializerOptions Json() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static async Task<(StepExecutionResult Result, StepExecutionContext Context)> RunCompileAsync(Exception stageFailure)
    {
        var workspace = new Mock<IMediaWorkspace>();
        workspace.SetupGet(w => w.IsRemoteSession).Returns(true);
        workspace.Setup(w => w.StageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(stageFailure);

        var options = Options.Create(new VideoEditingOptions
        {
            ScratchPath = Path.Combine(Path.GetTempPath(), "analysis-stage-tests", Guid.NewGuid().ToString("N")),
            MaxConcurrentJobs = 1
        });
        var config = new VideoCompileStepConfig(
            Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2), AnalysisStepOrder: 1)
            with
        { Mode = VideoCompileMode.Reencode, RegisterProjectFile = false, LoudnessTarget = ProgramLoudness.Off };
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 3,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = JsonSerializer.Serialize(config, Json())
        };
        var context = new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = "{}",
            StepOutputHistory = [new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: Key)],
            CurrentStepIndex = 2,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
        var executor = new VideoCompileStepExecutor(
            new Mock<IVideoToolRunner>().Object, new Mock<IMediaProbe>().Object, new Mock<IProjectFileWorkspace>().Object,
            new Mock<IServiceScopeFactory>().Object, options, NullLogger<VideoCompileStepExecutor>.Instance,
            mediaWorkspaces: new Factory(workspace.Object));

        return (await executor.ExecuteAsync(context), context);
    }

    [Fact]
    public async Task VideoCompile_names_a_404_on_the_analysis_artifact_and_hides_the_key()
    {
        (StepExecutionResult result, _) = await RunCompileAsync(Stage404());

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("ANALYSIS_ARTIFACT_UNAVAILABLE");
        result.ErrorDetails.Should().Be(AnalysisArtifactStaging.ReadMessage).And.NotContain("projects/");
        result.Output.Should().NotContain("projects/");
    }

    [Fact]
    public async Task VideoCompile_keeps_the_verifiers_code_for_a_rejected_runner_output_and_stays_platform_internal()
    {
        (StepExecutionResult result, _) = await RunCompileAsync(
            new RunnerOutputRejectedException(
                "RUNNER_OUTPUT_UNREADABLE", FailureClass.Runner,
                "ffprobe could not read the runner's output as media (52637 bytes, starts with 7b2276657273696f6e: " +
                "projects/p/outputFiles/x/edited.mp4: Invalid data found when processing input)."));

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("RUNNER_OUTPUT_UNREADABLE");
        result.ErrorDetails.Should().Be(RunnerOutputRejectedException.CustomerMessage);
        foreach (string text in new[] { result.ErrorDetails!, result.Output })
            text.Should().NotContain("ffprobe").And.NotContain("7b2276").And.NotContain("projects/").And.NotContain("Unexpected error");
        result.FailureClass.Should().Be(FailureClass.PlatformInternal, "the operator sees the code; the customer's refund is unchanged");
    }
}
