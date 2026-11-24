using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Tests for <see cref="VideoGenerateStepExecutor"/>: budget-gated, crash-resumable
/// submit/poll/download, normalize, and upload — never throws.
/// </summary>
public class VideoGenerateStepExecutorTests
{
    internal readonly VideoEditingOptions _videoOptions = new() { AnalyzeTimeoutSeconds = 10, CompileTimeoutSeconds = 60 };

    [Fact]
    public async Task ExecuteAsync_WithBudgetExceeded_ReturnsFailedWithoutSubmitting()
    {
        var (result, mockClient) = await RunDeniedAsync(
            "BUDGET_EXCEEDED: This needs about 150 credits of video generation but this step's spending limit is 100 credits. Generate fewer clips or raise the step's limit.");

        result.Status.Should().Be(StepStatus.Failed);
        result.Output.Should().Contain("BUDGET_EXCEEDED");
        result.NextStepIndex.Should().Be(1);
        mockClient.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_gate_denial_surfaces_its_sentence_as_ErrorDetails_without_the_code_prefix()
    {
        const string sentence = "This needs about 150 credits of video generation but this step's spending limit is 100 credits. Generate fewer clips or raise the step's limit.";

        var (result, _) = await RunDeniedAsync("BUDGET_EXCEEDED: " + sentence);

        result.ErrorDetails.Should().Be(sentence, "the workflow's 'Last error:' text reads ErrorDetails and was blank");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("BUDGET_EXCEEDED:")]
    [InlineData("BUDGET_EXCEEDED:   ")]
    public async Task A_budget_denial_with_no_text_still_yields_a_non_empty_ErrorDetails(string? denyReason)
    {
        var (result, _) = await RunDeniedAsync(denyReason);

        result.ErrorDetails.Should().NotBeNullOrWhiteSpace();
        result.ErrorDetails.Should().NotContain("BUDGET_EXCEEDED");
        result.Output.Should().Contain("\"failed\":\"BUDGET_EXCEEDED\"");
    }

    private async Task<(StepExecutionResult Result, Mock<IVideoGenerationClient> Client)> RunDeniedAsync(string? denyReason)
    {
        // Arrange
        var executionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var stepOrder = 1;

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            StepOrder = stepOrder,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = """{"maxSpendCredits": 100000.0, "inlinePrompt": "Create a video"}"""
        };

        var execution = new WorkflowExecution
        {
            Id = executionId,
            ProjectId = projectId,
            Status = ExecutionStatus.Running
        };

        var mockBudgetGate = new Mock<IVideoGenerationBudgetGate>();
        mockBudgetGate
            .Setup(g => g.ReserveAsync(It.Is<VideoGenerationReservationRequest>(r => r.ProjectId == projectId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoGenerationReservation(
                Allowed: false,
                Estimated: 150.0m,
                ProjectSpentToday: 0m,
                GlobalSpentToday: 0m,
                DenyReason: denyReason,
                Takes: []));

        var provider = new ResolvedVideoGenerationProvider(
            Guid.NewGuid(), "MiniMax", InferenceProviderKind.MiniMax, "https://api.minimax.io", "MiniMax-H3", "key123", 1800);

        var mockProviderResolver = new Mock<IInferenceProviderResolver>();
        mockProviderResolver
            .Setup(r => r.ResolveVideoGenerationAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);

        var mockClient = new Mock<IVideoGenerationClient>();
        mockClient
            .Setup(c => c.Describe(It.IsAny<string>()))
            .Returns(new VideoGenerationCapabilities(
                new Dictionary<string, decimal> { { "768P", 0.08m } },
                4, 15, new[] { "16:9" }));

        var mockClientFactory = new Mock<IVideoGenerationClientFactory>();
        mockClientFactory
            .Setup(f => f.Get(provider))
            .Returns(mockClient.Object);

        var mockWorkspace = new Mock<IProjectFileWorkspace>();
        var mockDbContextFactory = new Mock<IDbContextFactory<WorkflowEngineDbContext>>();
        var mockVideoRunner = new Mock<IVideoToolRunner>();
        var normalizer = new GeneratedClipNormalizer(
            mockVideoRunner.Object,
            Options.Create(_videoOptions),
            NullLogger<GeneratedClipNormalizer>.Instance);

        var executor = new VideoGenerateStepExecutor(
            mockBudgetGate.Object,
            mockProviderResolver.Object,
            mockClientFactory.Object,
            mockWorkspace.Object,
            mockDbContextFactory.Object,
            normalizer,
            Options.Create(new VideoGenerationOptions()),
            NullLogger<VideoGenerateStepExecutor>.Instance,
            NoMediaWorkspaces(),
            NoKeyframeExtractor());

        var context = new StepExecutionContext
        {
            StepResultId = Guid.NewGuid(),
            Execution = execution,
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test-correlation",
            StepOutputHistory = [],
            CancellationToken = CancellationToken.None
        };

        return (await executor.ExecuteAsync(context), mockClient);
    }

    [Fact]
    public async Task ExecuteAsync_WithProviderNotFound_ReturnsFailedResult()
    {
        // Arrange
        var executionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var stepOrder = 1;

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            StepOrder = stepOrder,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = """{"maxSpendCredits": 100000.0, "inlinePrompt": "Create a video"}"""
        };

        var execution = new WorkflowExecution
        {
            Id = executionId,
            ProjectId = projectId,
            Status = ExecutionStatus.Running
        };

        var mockBudgetGate = new Mock<IVideoGenerationBudgetGate>(MockBehavior.Strict);

        var mockProviderResolver = new Mock<IInferenceProviderResolver>();
        mockProviderResolver
            .Setup(r => r.ResolveVideoGenerationAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedVideoGenerationProvider?)null);

        var mockClientFactory = new Mock<IVideoGenerationClientFactory>();
        var mockWorkspace = new Mock<IProjectFileWorkspace>();
        var mockDbContextFactory = new Mock<IDbContextFactory<WorkflowEngineDbContext>>();
        var mockVideoRunner = new Mock<IVideoToolRunner>();
        var normalizer = new GeneratedClipNormalizer(
            mockVideoRunner.Object,
            Options.Create(_videoOptions),
            NullLogger<GeneratedClipNormalizer>.Instance);

        var executor = new VideoGenerateStepExecutor(
            mockBudgetGate.Object,
            mockProviderResolver.Object,
            mockClientFactory.Object,
            mockWorkspace.Object,
            mockDbContextFactory.Object,
            normalizer,
            Options.Create(new VideoGenerationOptions()),
            NullLogger<VideoGenerateStepExecutor>.Instance,
            NoMediaWorkspaces(),
            NoKeyframeExtractor());

        var context = new StepExecutionContext
        {
            StepResultId = Guid.NewGuid(),
            Execution = execution,
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test-correlation",
            StepOutputHistory = [],
            CancellationToken = CancellationToken.None
        };

        // Act
        var result = await executor.ExecuteAsync(context);

        // Assert
        result.Status.Should().Be(StepStatus.Failed);
        result.Output.Should().Contain("PROVIDER_NOT_FOUND");
    }

    [Fact]
    public async Task ExecuteAsync_NeverThrows()
    {
        // Arrange
        var executionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var stepOrder = 1;

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            StepOrder = stepOrder,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = """{"maxSpendCredits": 100000.0, "inlinePrompt": "Create a video"}"""
        };

        var execution = new WorkflowExecution
        {
            Id = executionId,
            ProjectId = projectId,
            Status = ExecutionStatus.Running
        };

        var mockBudgetGate = new Mock<IVideoGenerationBudgetGate>();
        mockBudgetGate
            .Setup(g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Unexpected error"));

        var provider = new ResolvedVideoGenerationProvider(
            Guid.NewGuid(), "MiniMax", InferenceProviderKind.MiniMax, "https://api.minimax.io", "MiniMax-H3", "key123", 1800);

        var mockProviderResolver = new Mock<IInferenceProviderResolver>();
        mockProviderResolver
            .Setup(r => r.ResolveVideoGenerationAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);

        var mockClient = new Mock<IVideoGenerationClient>();
        mockClient
            .Setup(c => c.Describe(It.IsAny<string>()))
            .Returns(new VideoGenerationCapabilities(
                new Dictionary<string, decimal> { { "768P", 0.08m } },
                4, 15, new[] { "16:9" }));

        var mockClientFactory = new Mock<IVideoGenerationClientFactory>();
        mockClientFactory
            .Setup(f => f.Get(provider))
            .Returns(mockClient.Object);

        var mockWorkspace = new Mock<IProjectFileWorkspace>();
        var mockDbContextFactory = new Mock<IDbContextFactory<WorkflowEngineDbContext>>();
        var mockVideoRunner = new Mock<IVideoToolRunner>();
        var normalizer = new GeneratedClipNormalizer(
            mockVideoRunner.Object,
            Options.Create(_videoOptions),
            NullLogger<GeneratedClipNormalizer>.Instance);

        var executor = new VideoGenerateStepExecutor(
            mockBudgetGate.Object,
            mockProviderResolver.Object,
            mockClientFactory.Object,
            mockWorkspace.Object,
            mockDbContextFactory.Object,
            normalizer,
            Options.Create(new VideoGenerationOptions()),
            NullLogger<VideoGenerateStepExecutor>.Instance,
            NoMediaWorkspaces(),
            NoKeyframeExtractor());

        var context = new StepExecutionContext
        {
            StepResultId = Guid.NewGuid(),
            Execution = execution,
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test-correlation",
            StepOutputHistory = [],
            CancellationToken = CancellationToken.None
        };

        // Act & Assert - should not throw
        var result = await executor.ExecuteAsync(context);

        // The step executor should never throw, always returning a valid result
        result.Should().NotBeNull();
        result.Status.Should().Be(StepStatus.Failed);
        result.Output.Should().Contain("UNEXPECTED_ERROR");
    }

    [Fact]
    public async Task ExecuteAsync_CrashResume_ReusesExistingJobAndSubmitsNewOne()
    {
        // Arrange
        var executionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var stepOrder = 1;
        var storageKey1 = "projects/test-project/agentFiles/generated/test-id/step-1-v1.mp4";
        var storageKey2 = "projects/test-project/agentFiles/generated/test-id/step-1-v2.mp4";

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            StepOrder = stepOrder,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = """{"maxSpendCredits": 200.0, "takes": 2, "maxClips": 2, "inlinePrompt": "Create a video", "timeoutSeconds": 60, "model": "MiniMax-H3", "durationSeconds": 10, "resolution": "1080P", "ratio": "16:9"}"""
        };

        var execution = new WorkflowExecution
        {
            Id = executionId,
            ProjectId = projectId,
            Status = ExecutionStatus.Running
        };


        var provider = new ResolvedVideoGenerationProvider(
            Guid.NewGuid(), "MiniMax", InferenceProviderKind.MiniMax, "https://api.minimax.io", "MiniMax-H3", "key123", 1800);

        var mockProviderResolver = new Mock<IInferenceProviderResolver>();
        mockProviderResolver
            .Setup(r => r.ResolveVideoGenerationAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);

        var mockClient = new Mock<IVideoGenerationClient>();
        var jobHandle1 = new ExternalJobHandle("remote-job-1");
        var jobHandle2 = new ExternalJobHandle("remote-job-2");

        mockClient
            .Setup(c => c.Describe(It.IsAny<string>()))
            .Returns(new VideoGenerationCapabilities(
                new Dictionary<string, decimal> { { "1080P", 0.025m } },
                4, 15, new[] { "16:9" }));

        var submitCallCount = 0;
        mockClient
            .Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => submitCallCount++)
            .ReturnsAsync(() => submitCallCount == 1 ? jobHandle1 : jobHandle2);

        mockClient
            .Setup(c => c.PollAsync(It.IsAny<ExternalJobHandle>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExternalJobHandle handle, CancellationToken ct) =>
                new ExternalJobStatus(ExternalJobState.Succeeded, "https://example.com/video.mp4", null));

        mockClient
            .Setup(c => c.DownloadAsync("https://example.com/video.mp4", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3, 4, 5]));

        var mockClientFactory = new Mock<IVideoGenerationClientFactory>();
        mockClientFactory
            .Setup(f => f.Get(provider))
            .Returns(mockClient.Object);

        var mockWorkspace = new Mock<IProjectFileWorkspace>();
        var storageKeyIndex = 0;
        mockWorkspace
            .Setup(w => w.UploadArtifactAsync(
                projectId, It.IsAny<string>(), It.IsAny<string>(), "video/mp4", It.IsAny<CancellationToken>(), "agentFiles"))
            .ReturnsAsync(() => storageKeyIndex++ == 0 ? storageKey1 : storageKey2);

        // T-020: a working ffprobe+ffmpeg runner, so both clips really deliver (previously the
        // normalizer threw on a null ffprobe result and both clips silently failed).
        var mockVideoRunner = WorkingRunner();

        var normalizer = new GeneratedClipNormalizer(
            mockVideoRunner.Object,
            Options.Create(_videoOptions),
            NullLogger<GeneratedClipNormalizer>.Instance);

        var dbContextOptions = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase("VideoGenerateTest_CrashResume_" + Guid.NewGuid())
            .Options;

        var mockDbContextFactory = new Mock<IDbContextFactory<WorkflowEngineDbContext>>();

        // Pre-seed the database with a Submitted job for clip 1
        using (var dbContext = new WorkflowEngineDbContext(dbContextOptions))
        {
            dbContext.Database.EnsureCreated();
            // Pre-seed a Submitted row for clip 1 (simulating crash-resume scenario)
            var existingJob = new ExternalGenerationJob
            {
                Id = Guid.NewGuid(),
                ExecutionId = executionId,
                StepResultId = Guid.Empty,
                ProjectId = projectId,
                ClipId = "clip-1",
                RequestHash = ComputeTestRequestHash("Create a video", "MiniMax-H3", "1080P", "16:9", 10, 1),
                ProviderId = provider.ProviderId ?? Guid.Empty,
                RemoteJobId = "remote-job-1",
                Status = ExternalGenerationJobStatus.Submitted,
                CostEstimate = 25m,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            dbContext.Add(existingJob);
            await dbContext.SaveChangesAsync();
        }

        mockDbContextFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new WorkflowEngineDbContext(dbContextOptions));
        var budgetGate = RealGate(mockDbContextFactory.Object);

        var executor = new VideoGenerateStepExecutor(
            budgetGate,
            mockProviderResolver.Object,
            mockClientFactory.Object,
            mockWorkspace.Object,
            mockDbContextFactory.Object,
            normalizer,
            Options.Create(new VideoGenerationOptions()),
            NullLogger<VideoGenerateStepExecutor>.Instance,
            NoMediaWorkspaces(),
            NoKeyframeExtractor());

        var context = new StepExecutionContext
        {
            StepResultId = Guid.NewGuid(),
            Execution = execution,
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test-correlation",
            StepOutputHistory = [],
            CancellationToken = CancellationToken.None
        };

        // Act
        var result = await executor.ExecuteAsync(context);

        // Assert
        result.Status.Should().Be(StepStatus.Completed);
        // Should have 2 succeeded clips
        result.Output.Should().Contain("\"clipId\":\"clip-1\"");
        result.Output.Should().Contain("\"clipId\":\"clip-2\"");
        result.Output.Should().NotContain("\"failed\"");
        // Verify SubmitAsync was called only once (for clip 2, not clip 1 which was resumed)
        mockClient.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_PollTimeout_ReturnsFailedWithTimeoutReason()
    {
        // Arrange
        var executionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var stepOrder = 1;

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            StepOrder = stepOrder,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = """{"maxSpendCredits": 100000.0, "takes": 1, "inlinePrompt": "Create a video", "timeoutSeconds": 60, "pollIntervalSeconds": 1, "model": "MiniMax-H3", "durationSeconds": 10, "resolution": "1080P", "ratio": "16:9"}"""
        };

        var execution = new WorkflowExecution
        {
            Id = executionId,
            ProjectId = projectId,
            Status = ExecutionStatus.Running
        };


        var provider = new ResolvedVideoGenerationProvider(
            Guid.NewGuid(), "MiniMax", InferenceProviderKind.MiniMax, "https://api.minimax.io", "MiniMax-H3", "key123", 1800);

        var mockProviderResolver = new Mock<IInferenceProviderResolver>();
        mockProviderResolver
            .Setup(r => r.ResolveVideoGenerationAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);

        var mockClient = new Mock<IVideoGenerationClient>();
        mockClient
            .Setup(c => c.Describe(It.IsAny<string>()))
            .Returns(new VideoGenerationCapabilities(
                new Dictionary<string, decimal> { { "1080P", 0.025m } },
                4, 15, new[] { "16:9" }));

        var jobHandle = new ExternalJobHandle("remote-job-123");
        mockClient
            .Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobHandle);

        // Always return "Running" state to simulate timeout; each poll moves the executor's clock
        // past the (in-range) 60s TimeoutSeconds, so the timeout is reached after one poll.
        var clock = new ManualTimeProvider();
        mockClient
            .Setup(c => c.PollAsync(jobHandle, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                clock.Advance(TimeSpan.FromSeconds(61));
                return new ExternalJobStatus(ExternalJobState.Running, null, null);
            });

        var mockClientFactory = new Mock<IVideoGenerationClientFactory>();
        mockClientFactory
            .Setup(f => f.Get(provider))
            .Returns(mockClient.Object);

        var mockWorkspace = new Mock<IProjectFileWorkspace>();
        var mockVideoRunner = new Mock<IVideoToolRunner>();
        var normalizer = new GeneratedClipNormalizer(
            mockVideoRunner.Object,
            Options.Create(_videoOptions),
            NullLogger<GeneratedClipNormalizer>.Instance);

        var dbContextOptions = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase("VideoGenerateTest_Timeout_" + Guid.NewGuid())
            .Options;

        var mockDbContextFactory = new Mock<IDbContextFactory<WorkflowEngineDbContext>>();
        mockDbContextFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new WorkflowEngineDbContext(dbContextOptions));
        var budgetGate = RealGate(mockDbContextFactory.Object);

        var executor = new VideoGenerateStepExecutor(
            budgetGate,
            mockProviderResolver.Object,
            mockClientFactory.Object,
            mockWorkspace.Object,
            mockDbContextFactory.Object,
            normalizer,
            Options.Create(new VideoGenerationOptions()),
            NullLogger<VideoGenerateStepExecutor>.Instance,
            NoMediaWorkspaces(),
            NoKeyframeExtractor(),
            clock);

        var context = new StepExecutionContext
        {
            StepResultId = Guid.NewGuid(),
            Execution = execution,
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test-correlation",
            StepOutputHistory = [],
            CancellationToken = CancellationToken.None
        };

        // Act
        var result = await executor.ExecuteAsync(context);

        // Assert
        // T-020: a single take that failed means all takes failed -> step Failed.
        result.Status.Should().Be(StepStatus.Failed);
        result.Output.Should().Contain("POLL_TIMEOUT");
        result.Output.Should().Contain("failed");
    }

    [Fact]
    public async Task ExecuteAsync_Provider402Error_ReturnsInsufficientBalanceNotBudgetExceeded()
    {
        // Arrange
        var executionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var stepOrder = 1;

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            StepOrder = stepOrder,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = """{"maxSpendCredits": 100000.0, "takes": 1, "inlinePrompt": "Create a video", "timeoutSeconds": 60, "model": "MiniMax-H3", "durationSeconds": 10, "resolution": "1080P", "ratio": "16:9"}"""
        };

        var execution = new WorkflowExecution
        {
            Id = executionId,
            ProjectId = projectId,
            Status = ExecutionStatus.Running
        };


        var provider = new ResolvedVideoGenerationProvider(
            Guid.NewGuid(), "MiniMax", InferenceProviderKind.MiniMax, "https://api.minimax.io", "MiniMax-H3", "key123", 1800);

        var mockProviderResolver = new Mock<IInferenceProviderResolver>();
        mockProviderResolver
            .Setup(r => r.ResolveVideoGenerationAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);

        var mockClient = new Mock<IVideoGenerationClient>();
        mockClient
            .Setup(c => c.Describe(It.IsAny<string>()))
            .Returns(new VideoGenerationCapabilities(
                new Dictionary<string, decimal> { { "1080P", 0.025m } },
                4, 15, new[] { "16:9" }));

        var jobHandle = new ExternalJobHandle("remote-job-123");
        mockClient
            .Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobHandle);

        mockClient
            .Setup(c => c.PollAsync(jobHandle, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalJobStatus(
                ExternalJobState.Failed,
                null,
                "API Error 402: insufficient credits"));

        var mockClientFactory = new Mock<IVideoGenerationClientFactory>();
        mockClientFactory
            .Setup(f => f.Get(provider))
            .Returns(mockClient.Object);

        var mockWorkspace = new Mock<IProjectFileWorkspace>();
        var mockVideoRunner = new Mock<IVideoToolRunner>();
        var normalizer = new GeneratedClipNormalizer(
            mockVideoRunner.Object,
            Options.Create(_videoOptions),
            NullLogger<GeneratedClipNormalizer>.Instance);

        var dbContextOptions = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase("VideoGenerateTest_402Error_" + Guid.NewGuid())
            .Options;

        var mockDbContextFactory = new Mock<IDbContextFactory<WorkflowEngineDbContext>>();
        mockDbContextFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new WorkflowEngineDbContext(dbContextOptions));
        var budgetGate = RealGate(mockDbContextFactory.Object);

        var executor = new VideoGenerateStepExecutor(
            budgetGate,
            mockProviderResolver.Object,
            mockClientFactory.Object,
            mockWorkspace.Object,
            mockDbContextFactory.Object,
            normalizer,
            Options.Create(new VideoGenerationOptions()),
            NullLogger<VideoGenerateStepExecutor>.Instance,
            NoMediaWorkspaces(),
            NoKeyframeExtractor());

        var context = new StepExecutionContext
        {
            StepResultId = Guid.NewGuid(),
            Execution = execution,
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test-correlation",
            StepOutputHistory = [],
            CancellationToken = CancellationToken.None
        };

        // Act
        var result = await executor.ExecuteAsync(context);

        // Assert
        // T-020: the only take failed -> step Failed (all takes failed).
        result.Status.Should().Be(StepStatus.Failed);
        result.Output.Should().Contain("PROVIDER_INSUFFICIENT_BALANCE");
        result.Output.Should().NotContain("BUDGET_EXCEEDED");
    }

    private static string ComputeTestRequestHash(string prompt, string model, string resolution, string ratio, int durationSeconds, int clipIndex)
    {
        using (var sha256 = System.Security.Cryptography.SHA256.Create())
        {
            var material = $"{model}|{resolution}|{ratio}|{durationSeconds}|{clipIndex}|{prompt}";
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(material));
            return Convert.ToHexString(hash);
        }
    }

    [Fact]
    public async Task ExecuteAsync_BudgetGateReceivesRealProviderPricing()
    {
        // Arrange: Create a real MiniMax client that returns known pricing (768P=$0.08/s, 2K=$0.13/s)
        var mockHttpClient = new Mock<HttpClient>();
        var realClient = new MiniMaxVideoGenerationClient(mockHttpClient.Object, "key123");

        var config = new VideoGenerateStepConfig
        {
            Model = "MiniMax-H3",
            InlinePrompt = "Create a video",
            DurationSeconds = 5,
            Resolution = "768P",
            Ratio = "16:9",
            MaxSpendCredits = 100m,
            Takes = 2,
            MaxClips = 2,
            ProviderId = null,
            TimeoutSeconds = 60,
            PollIntervalSeconds = 1
        };

        // Expected vendor cost: 0.08 USD/sec * 5 sec * 2 takes = 0.80 USD = 133.333… credits.
        var expectedEstimatedCost = CreditCurrency.CreditsFromUsd(0.08m * 5 * 2);

        var executionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var stepOrder = 1;

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            StepOrder = stepOrder,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = JsonSerializer.Serialize(config, ConfigJsonOptions)
        };

        var execution = new WorkflowExecution
        {
            Id = executionId,
            ProjectId = projectId,
            Status = ExecutionStatus.Running
        };

        VideoGenerationReservationRequest? captured = null;
        var mockBudgetGate = new Mock<IVideoGenerationBudgetGate>();
        mockBudgetGate
            .Setup(g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()))
            .Callback((VideoGenerationReservationRequest r, CancellationToken _) => captured = r)
            .ReturnsAsync(new VideoGenerationReservation(false, expectedEstimatedCost, 0m, 0m, "BUDGET_EXCEEDED: stop here", []));

        var provider = new ResolvedVideoGenerationProvider(
            Guid.NewGuid(), "MiniMax", InferenceProviderKind.MiniMax, "https://api.minimax.io", "MiniMax-H3", "key123", 1800);

        var mockProviderResolver = new Mock<IInferenceProviderResolver>();
        mockProviderResolver
            .Setup(r => r.ResolveVideoGenerationAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);

        var mockClientFactory = new Mock<IVideoGenerationClientFactory>();
        mockClientFactory
            .Setup(f => f.Get(provider))
            .Returns(realClient);

        var mockWorkspace = new Mock<IProjectFileWorkspace>();
        var mockDbContextFactory = new Mock<IDbContextFactory<WorkflowEngineDbContext>>();
        var mockVideoRunner = new Mock<IVideoToolRunner>();
        var normalizer = new GeneratedClipNormalizer(
            mockVideoRunner.Object,
            Options.Create(_videoOptions),
            NullLogger<GeneratedClipNormalizer>.Instance);

        var executor = new VideoGenerateStepExecutor(
            mockBudgetGate.Object,
            mockProviderResolver.Object,
            mockClientFactory.Object,
            mockWorkspace.Object,
            mockDbContextFactory.Object,
            normalizer,
            Options.Create(new VideoGenerationOptions()),
            NullLogger<VideoGenerateStepExecutor>.Instance,
            NoMediaWorkspaces(),
            NoKeyframeExtractor());

        var context = new StepExecutionContext
        {
            StepResultId = Guid.NewGuid(),
            Execution = execution,
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test-correlation",
            StepOutputHistory = [],
            CancellationToken = CancellationToken.None
        };

        // Act
        var result = await executor.ExecuteAsync(context);

        // Assert: The budget gate should have been called with the real MiniMax-H3 pricing for 768P
        // (0.08 USD/sec * 5 sec * 2 takes = 0.80 USD), not the placeholder hardcoded values
        captured.Should().NotBeNull();
        captured!.ProjectId.Should().Be(projectId);
        captured.ExecutionId.Should().Be(executionId);
        captured.ProviderId.Should().Be(provider.ProviderId!.Value);
        captured.StepCap.Should().Be(config.MaxSpendCredits);
        captured.Takes.Select(t => t.ClipId).Should().Equal("clip-1", "clip-2");
        captured.Takes.Should().OnlyContain(t => t.CostEstimate == CreditCurrency.CreditsFromUsd(0.40m));
        captured.Takes.Sum(t => t.CostEstimate).Should().Be(expectedEstimatedCost);
        captured.Takes.Select(t => t.RequestHash).Should().Equal(
            ComputeTestRequestHash("Create a video", "MiniMax-H3", "768P", "16:9", 5, 1),
            ComputeTestRequestHash("Create a video", "MiniMax-H3", "768P", "16:9", 5, 2));

        // Verify the gate was called exactly once
        mockBudgetGate.Verify(
            g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static readonly System.Text.Json.JsonSerializerOptions ConfigJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>A real budget gate with caps far above anything these tests spend.</summary>
    internal static VideoGenerationBudgetGate RealGate(IDbContextFactory<WorkflowEngineDbContext> factory) =>
        new(factory, Options.Create(new VideoGenerationOptions { DailyBudgetCredits = 1_000_000m, GlobalDailyBudgetCredits = 1_000_000m }));

    private static readonly MiniMaxVideoGenerationClient PricingOnlyMiniMax =
        new(new Mock<HttpClient>().Object, "key123");

    /// <summary>
    /// A client whose <c>Describe</c> is the real MiniMax table (pure, no network) and whose
    /// submit/poll/download are mocks — no paid API is ever reachable.
    /// </summary>
    internal static Mock<IVideoGenerationClient> MiniMaxDescribingClient()
    {
        var client = new Mock<IVideoGenerationClient>();
        client
            .Setup(c => c.Describe(It.IsAny<string>()))
            .Returns((string model) => PricingOnlyMiniMax.Describe(model));
        return client;
    }

    internal (VideoGenerateStepExecutor Executor, StepExecutionContext Context) BuildExecutor(
        IVideoGenerationBudgetGate gate,
        IVideoGenerationClient client,
        IDbContextFactory<WorkflowEngineDbContext> factory,
        string configJson,
        WorkflowExecution execution,
        IProjectFileWorkspace? workspace = null,
        IVideoToolRunner? runner = null,
        TimeProvider? timeProvider = null,
        Guid? providerId = null,
        ExecutionRunFlags? runFlags = null,
        int iterationCount = 0,
        ReelBolt.Shared.Metering.IUsageRecorder? usageRecorder = null,
        ExecutionLeaseService? leases = null)
    {
        var provider = new ResolvedVideoGenerationProvider(
            providerId ?? Guid.NewGuid(), "MiniMax", InferenceProviderKind.MiniMax, "https://api.minimax.io", "MiniMax-H3", "key123", 1800);
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver
            .Setup(r => r.ResolveVideoGenerationAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);
        var clientFactory = new Mock<IVideoGenerationClientFactory>();
        clientFactory.Setup(f => f.Get(provider)).Returns(client);

        var normalizer = new GeneratedClipNormalizer(
            runner ?? new Mock<IVideoToolRunner>().Object,
            Options.Create(_videoOptions),
            NullLogger<GeneratedClipNormalizer>.Instance);

        var executor = new VideoGenerateStepExecutor(
            gate,
            resolver.Object,
            clientFactory.Object,
            workspace ?? new Mock<IProjectFileWorkspace>().Object,
            factory,
            normalizer,
            Options.Create(new VideoGenerationOptions()),
            NullLogger<VideoGenerateStepExecutor>.Instance,
            NoMediaWorkspaces(),
            NoKeyframeExtractor(),
            timeProvider,
            runFlags,
            usageRecorder,
            leases);

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = configJson
        };

        var context = new StepExecutionContext
        {
            StepResultId = Guid.NewGuid(),
            Execution = execution,
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            CurrentStepIndex = 0,
            IterationCount = iterationCount,
            CorrelationId = "test-correlation",
            StepOutputHistory = [],
            CancellationToken = CancellationToken.None
        };

        return (executor, context);
    }

    [Theory]
    [InlineData("MiniMax-H3", -5, "768P", "16:9")]
    [InlineData("MiniMax-H3", 0, "768P", "16:9")]
    [InlineData("MiniMax-H3", 3, "768P", "16:9")]
    [InlineData("MiniMax-H3-Max", 4, "768P", "16:9")]
    [InlineData("MiniMax-H3", 16, "768P", "16:9")]
    [InlineData("MiniMax-H3-Max", 6, "768p", "16:9")]
    [InlineData("MiniMax-H3-Max", 6, "1080P", "16:9")]
    [InlineData("MiniMax-H3-Max", 6, "2K", "16:9")]
    [InlineData("MiniMax-H3", 6, "768P", "adaptive")]
    [InlineData("MiniMax-Unknown", 6, "768P", "16:9")]
    public async Task ExecuteAsync_InvalidAgainstModelCapabilities_FailsConfigInvalidWithoutRowsOrSubmit(
        string model, int duration, string resolution, string ratio)
    {
        var factory = new SharedInMemoryDbFactory();
        var client = MiniMaxDescribingClient();
        var execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Status = ExecutionStatus.Running };
        var configJson = JsonSerializer.Serialize(new VideoGenerateStepConfig
        {
            InlinePrompt = "Create a video",
            Model = model,
            DurationSeconds = duration,
            Resolution = resolution,
            Ratio = ratio,
            MaxSpendCredits = 100m,
            Takes = 2,
            MaxClips = 2
        }, ConfigJsonOptions);

        var (executor, context) = BuildExecutor(RealGate(factory), client.Object, factory, configJson, execution);

        var result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        result.Output.Should().Contain("CONFIG_INVALID");
        using (var db = factory.CreateDbContext())
        {
            db.ExternalGenerationJobs.Count().Should().Be(0, "no ledger row may exist for an invalid config");
        }

        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_GateConfigInvalidDeny_MapsToConfigInvalid()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = MiniMaxDescribingClient();
        var gate = new Mock<IVideoGenerationBudgetGate>();
        gate
            .Setup(g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoGenerationReservation(false, 0m, 0m, 0m, "CONFIG_INVALID: no takes requested", []));
        var execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Status = ExecutionStatus.Running };

        var (executor, context) = BuildExecutor(gate.Object, client.Object, factory,
            """{"maxSpendCredits": 100000.0, "inlinePrompt": "Create a video"}""", execution);

        var result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        result.Output.Should().Contain("\"CONFIG_INVALID\"");
        result.Output.Should().NotContain("\"BUDGET_EXCEEDED\"");
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_PromptExactlyMaxLength_Submits()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = SubmittingClient("r1");
        PollReturns(client, "r1", new ExternalJobStatus(ExternalJobState.Succeeded, "https://example.com/v.mp4", null));
        client
            .Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3]));

        var prompt = new string('a', 7000);
        var configJson = JsonSerializer.Serialize(new VideoGenerateStepConfig
        {
            InlinePrompt = prompt,
            MaxSpendCredits = 100m
        }, ConfigJsonOptions);

        var (executor, context) = BuildExecutor(
            RealGate(factory), client.Object, factory, configJson, NewExecution(),
            UploadingWorkspace().Object, WorkingRunner().Object);

        var result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        client.Verify(
            c => c.SubmitAsync(It.Is<VideoGenerationRequest>(r => r.PromptText.Length == 7000), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_SettledTake_RecordsVideoGenSecondsKeyedOnTheLedgerRow()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = SubmittingClient("r1");
        PollReturns(client, "r1", new ExternalJobStatus(ExternalJobState.Succeeded, "https://example.com/v.mp4", null, UsageTotalSeconds: 6));
        client
            .Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3]));

        var capture = new CapturingSink();
        var recorder = new ReelBolt.Shared.Metering.UsageRecorder(
            new NonChatPriceService(), capture, NullLogger<ReelBolt.Shared.Metering.UsageRecorder>.Instance);
        var orgId = Guid.NewGuid();
        var execution = NewExecution();

        var (executor, context) = BuildExecutor(
            RealGate(factory), client.Object, factory, Cfg(), execution,
            UploadingWorkspace().Object, WorkingRunner().Object, usageRecorder: recorder);

        StepExecutionResult result;
        using (ReelBolt.Shared.Metering.UsageScope.Begin(orgId, execution.ProjectId, execution.Id, context.StepResultId, null, UsageSource.Workflow))
            result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);

        UsageEvent row = capture.Events.Should().ContainSingle().Subject;
        row.Kind.Should().Be(UsageKind.VideoGenSeconds);
        row.Quantity.Should().Be(6m, "the take's requested seconds, the platform-side job spec");
        row.OrganizationId.Should().Be(orgId);
        row.ProviderKind.Should().Be(InferenceProviderKind.MiniMax);
        row.BillingMode.Should().Be(UsageBillingMode.Platform);
        row.Variant.Should().Be("768P", "the resolution the take was requested at is the price variant");
        row.PriceId.Should().NotBeNull();
        using var db = factory.CreateDbContext();
        row.IdempotencyKey.Should().Be($"videogen:{db.ExternalGenerationJobs.Single().Id:N}");
    }

    /// <summary>
    /// An oracle-priced take (Higgsfield, async price oracle) has no price row: it is charged the vendor's own quote for it at the
    /// platform markup, and the row says so by carrying no price id (docs/design/model-catalog.md §5.3).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_OraclePricedTake_RecordsTheQuotedUsdAtTheMarkupWithNoPriceRow()
    {
        var factory = new SharedInMemoryDbFactory();
        var quote = VendorQuote();
        var client = HiggsfieldShapedClient(quote);
        Deliveries(client, [], "hf-1");
        client
            .Setup(c => c.PollAsync(It.IsAny<ExternalJobHandle>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalJobStatus(ExternalJobState.Succeeded, "https://example.invalid/hf-1.mp4", null,
                UsageTotalSeconds: VendorQuoteDurationSeconds));
        var capture = new CapturingSink();
        var recorder = new ReelBolt.Shared.Metering.UsageRecorder(
            new NonChatPriceService(), capture, NullLogger<ReelBolt.Shared.Metering.UsageRecorder>.Instance);
        var execution = NewExecution();

        var (executor, context) = BuildVendorExecutor(
            RealGate(factory), client.Object, factory, HiggsfieldCfg(), execution, CancellationToken.None, usageRecorder: recorder);

        using (ReelBolt.Shared.Metering.UsageScope.Begin(Guid.NewGuid(), execution.ProjectId, execution.Id, context.StepResultId, null, UsageSource.Workflow))
            (await executor.ExecuteAsync(context)).Status.Should().Be(StepStatus.Completed);
        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);

        UsageEvent row = capture.Events.Should().ContainSingle().Subject;
        row.ProviderKind.Should().Be(InferenceProviderKind.Higgsfield);
        row.CostUsd.Should().Be(VendorQuoteTotalUsd);
        row.CreditsMillis.Should().Be((long)(VendorQuoteTotalUsd * 1.2m * 1000m / 0.006m));
        row.PriceId.Should().BeNull("an oracle quote is the only billed usage without a price row");
        row.Variant.Should().Be(VendorQuoteResolution);
    }

    [Fact]
    public async Task ExecuteAsync_FailedTake_RecordsNoVideoGenUsage()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = SubmittingClient("r1");
        PollReturns(client, "r1", new ExternalJobStatus(ExternalJobState.Failed, null, "vendor said no"));

        var capture = new CapturingSink();
        var recorder = new ReelBolt.Shared.Metering.UsageRecorder(
            new NonChatPriceService(), capture, NullLogger<ReelBolt.Shared.Metering.UsageRecorder>.Instance);
        var (executor, context) = BuildExecutor(
            RealGate(factory), client.Object, factory, Cfg(), NewExecution(),
            UploadingWorkspace().Object, WorkingRunner().Object, usageRecorder: recorder);

        await executor.ExecuteAsync(context);
        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);

        capture.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_PromptOverMaxLength_ReturnsInvalidPromptWithoutSubmitting()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = MiniMaxDescribingClient();

        var prompt = new string('a', 7001);
        var configJson = JsonSerializer.Serialize(new VideoGenerateStepConfig
        {
            InlinePrompt = prompt,
            MaxSpendCredits = 100m
        }, ConfigJsonOptions);

        var (executor, context) = BuildExecutor(
            RealGate(factory), client.Object, factory, configJson, NewExecution());

        var result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        result.Output.Should().Contain("INVALID_PROMPT");
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_PromptControlCharacters_AreStrippedFromSubmittedBody()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = SubmittingClient("r1");
        PollReturns(client, "r1", new ExternalJobStatus(ExternalJobState.Succeeded, "https://example.com/v.mp4", null));
        client
            .Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3]));

        var configJson = JsonSerializer.Serialize(new VideoGenerateStepConfig
        {
            InlinePrompt = "Hello\u0001World\u0002",
            MaxSpendCredits = 100m
        }, ConfigJsonOptions);

        var (executor, context) = BuildExecutor(
            RealGate(factory), client.Object, factory, configJson, NewExecution(),
            UploadingWorkspace().Object, WorkingRunner().Object);

        var result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        client.Verify(
            c => c.SubmitAsync(It.Is<VideoGenerationRequest>(r => r.PromptText == "HelloWorld"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_PromptOnlyControlCharacters_ReturnsInvalidPromptWithoutSubmitting()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = MiniMaxDescribingClient();

        var configJson = JsonSerializer.Serialize(new VideoGenerateStepConfig
        {
            InlinePrompt = "\u0001\u0002\u0003",
            MaxSpendCredits = 100m
        }, ConfigJsonOptions);

        var (executor, context) = BuildExecutor(
            RealGate(factory), client.Object, factory, configJson, NewExecution());

        var result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        result.Output.Should().Contain("INVALID_PROMPT");
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_RealGateOverCap_ReturnsBudgetExceededWithoutSubmitting()
    {
        // A real gate whose project + global daily caps are below one take's estimate ($0.48):
        // the reserve must deny inside the executor and SubmitAsync must never run, with no ledger row.
        var factory = new SharedInMemoryDbFactory();
        var client = MiniMaxDescribingClient();
        var overCapGate = new VideoGenerationBudgetGate(
            factory, Options.Create(new VideoGenerationOptions { DailyBudgetCredits = 0.01m, GlobalDailyBudgetCredits = 0.01m }));

        var (executor, context) = BuildExecutor(overCapGate, client.Object, factory, Cfg(1), NewExecution());

        var result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        result.Output.Should().Contain("BUDGET_EXCEEDED");
        using (var db = factory.CreateDbContext())
        {
            db.ExternalGenerationJobs.Count().Should().Be(0, "a denied reservation must write no ledger row");
        }
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ReservesOnceBeforeAnySubmit_AndSubmitsAgainstReturnedJobIds()
    {
        var factory = new SharedInMemoryDbFactory();
        var realGate = RealGate(factory);
        var calls = new List<string>();
        VideoGenerationReservation? reservation = null;

        var gate = new Mock<IVideoGenerationBudgetGate>();
        gate
            .Setup(g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (VideoGenerationReservationRequest r, CancellationToken ct) =>
            {
                calls.Add("reserve");
                reservation = await realGate.ReserveAsync(r, ct);
                return reservation;
            });

        var client = MiniMaxDescribingClient();
        var submitted = 0;
        client
            .Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                calls.Add("submit");
                using var db = factory.CreateDbContext();
                db.ExternalGenerationJobs.Count().Should().Be(3, "every take is reserved before the first submit");
                return new ExternalJobHandle($"remote-{++submitted}");
            });
        client
            .Setup(c => c.PollAsync(It.IsAny<ExternalJobHandle>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalJobStatus(ExternalJobState.Failed, null, "vendor said no"));

        var execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Status = ExecutionStatus.Running };
        var (executor, context) = BuildExecutor(gate.Object, client.Object, factory,
            """{"maxSpendCredits": 100000.0, "takes": 3, "maxClips": 3, "inlinePrompt": "Create a video", "timeoutSeconds": 60}""", execution);

        var result = await executor.ExecuteAsync(context);

        // T-020: every take vendor-failed -> step Failed, rows released as Failed.
        result.Status.Should().Be(StepStatus.Failed);
        calls.Should().Equal("reserve", "submit", "submit", "submit");
        gate.Verify(g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        using (var db = factory.CreateDbContext())
        {
            var rows = db.ExternalGenerationJobs.ToList();
            rows.Should().HaveCount(3, "the executor must not insert rows of its own");
            rows.Select(r => r.Id).Should().BeEquivalentTo(reservation!.Takes.Select(t => t.JobId));
            rows.Should().OnlyContain(r => r.Status == ExternalGenerationJobStatus.Failed && r.RemoteJobId != null);
            rows.OrderBy(r => r.ClipId).Select(r => r.RemoteJobId).Should().Equal("remote-1", "remote-2", "remote-3");
        }
    }

    [Fact]
    public async Task ExecuteAsync_LedgerRowsCarryTheContextStepResultId()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = MiniMaxDescribingClient();
        var submitted = 0;
        client
            .Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ExternalJobHandle($"remote-{++submitted}"));
        client
            .Setup(c => c.PollAsync(It.IsAny<ExternalJobHandle>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalJobStatus(ExternalJobState.Failed, null, "vendor said no"));

        var execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Status = ExecutionStatus.Running };
        var (executor, context) = BuildExecutor(RealGate(factory), client.Object, factory,
            """{"maxSpendCredits": 100000.0, "takes": 2, "maxClips": 2, "inlinePrompt": "Create a video", "timeoutSeconds": 60}""", execution);
        var stepResultId = Guid.NewGuid();
        context.StepResultId = stepResultId;

        await executor.ExecuteAsync(context);

        using var db = factory.CreateDbContext();
        var rows = db.ExternalGenerationJobs.ToList();
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.StepResultId == stepResultId);
    }

    // ------------------------------------------------------------------------------------------
    // T-020: ledger status contract per exit path, vendor-usage cost, orphan handling, cost
    // labels, and all-takes-failed. Real VideoGenerationBudgetGate over InMemory unless a test
    // needs a reservation shape the gate cannot produce (then a thin mock).
    // Default config: MiniMax-H3, 768P ($0.08/s), 6s => vendor estimate $0.48 per take, which is
    // 80 credits at the rate card's credit price (1 credit = $0.006) — the unit the ledger holds.
    // ------------------------------------------------------------------------------------------

    private static readonly decimal TakeEstimate = CreditCurrency.CreditsFromUsd(0.48m);

    // Bounds (T-025): pollIntervalSeconds 1..300, timeoutSeconds 60..7200. Tests that need a
    // POLL_TIMEOUT drive a ManualTimeProvider past the timeout rather than using a tiny value.
    private static string Cfg(int takes = 1, int timeoutSeconds = 60) =>
        $$"""{"maxSpendCredits": 100000.0, "takes": {{takes}}, "maxClips": {{takes}}, "inlinePrompt": "Create a video", "timeoutSeconds": {{timeoutSeconds}}, "pollIntervalSeconds": 1}""";

    internal static string DefaultHash(int clipIndex) =>
        ComputeTestRequestHash("Create a video", "MiniMax-H3", "768P", "16:9", 6, clipIndex);

    private static WorkflowExecution NewExecution() =>
        new() { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Status = ExecutionStatus.Running };

    private static Mock<IVideoToolRunner> WorkingRunner()
    {
        const string probeJson = """
            { "streams": [ { "codec_type": "video", "codec_name": "h264", "width": 640, "height": 480,
              "r_frame_rate": "30/1", "duration": "6" } ], "format": { "duration": "6" } }
            """;
        var runner = new Mock<IVideoToolRunner>();
        runner
            .Setup(r => r.RunFfprobeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(ExitCode: 0, StdOut: probeJson, StdErr: "", TimedOut: false));
        runner
            .Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(ExitCode: 0, StdOut: "", StdErr: "", TimedOut: false));
        runner
            .Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new VideoToolResult(ExitCode: 0, StdOut: "", StdErr: "", TimedOut: false));
        return runner;
    }


    /// <summary>
    /// The media-workspace factory an INLINE-path test hands the executor: strict, so a plan-shaped
    /// assumption that leaked into the inline path — a frame request where the inline config has no
    /// frame vocabulary at all — fails loudly instead of quietly opening a session.
    /// </summary>
    private static IMediaWorkspaceFactory NoMediaWorkspaces() =>
        new Mock<IMediaWorkspaceFactory>(MockBehavior.Strict).Object;

    /// <summary>The keyframe extractor for the same tests: strict, for the same reason.</summary>
    private static IKeyframeExtractor NoKeyframeExtractor() =>
        new Mock<IKeyframeExtractor>(MockBehavior.Strict).Object;

    private static Mock<IProjectFileWorkspace> UploadingWorkspace()
    {
        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), "video/mp4", It.IsAny<CancellationToken>(), "agentFiles"))
            .ReturnsAsync((Guid _, string _, string name, string _, CancellationToken _, string _) => "stored/" + name);
        return workspace;
    }

    private static Mock<IVideoGenerationClient> SubmittingClient(params string[] remoteIds)
    {
        var client = MiniMaxDescribingClient();
        var i = 0;
        client
            .Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ExternalJobHandle(remoteIds[i++]));
        return client;
    }

    private static void PollReturns(Mock<IVideoGenerationClient> client, string remoteId, ExternalJobStatus status) =>
        client
            .Setup(c => c.PollAsync(It.Is<ExternalJobHandle>(h => h.RemoteJobId == remoteId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

    private static JsonNode Clip(StepExecutionResult result, int index = 0) =>
        JsonNode.Parse(result.Output)!["clips"]![index]!;

    private static JsonNode Meta(StepExecutionResult result) =>
        JsonNode.Parse(result.Output)!["meta"]!;

    private static ExternalGenerationJob Row(SharedInMemoryDbFactory factory, string clipId = "clip-1")
    {
        using var db = factory.CreateDbContext();
        return db.ExternalGenerationJobs.AsNoTracking().Single(j => j.ClipId == clipId);
    }

    private async Task<(StepExecutionResult Result, SharedInMemoryDbFactory Factory, DateTime StartedAt)> RunOneTake(
        Mock<IVideoGenerationClient> client, int timeoutSeconds = 60,
        Mock<IProjectFileWorkspace>? workspace = null, Mock<IVideoToolRunner>? runner = null,
        TimeProvider? timeProvider = null)
    {
        var factory = new SharedInMemoryDbFactory();
        var startedAt = DateTime.UtcNow;
        var (executor, context) = BuildExecutor(RealGate(factory), client.Object, factory,
            Cfg(1, timeoutSeconds), NewExecution(), workspace?.Object, runner?.Object, timeProvider);
        var result = await executor.ExecuteAsync(context);
        return (result, factory, startedAt);
    }

    [Theory]
    [InlineData("vendor said no", "PROVIDER_FAILED")]
    [InlineData("API Error 402: payment required", "PROVIDER_INSUFFICIENT_BALANCE")]
    [InlineData("insufficient balance", "PROVIDER_INSUFFICIENT_BALANCE")]
    public async Task PollFailed_MarksRowFailed_AndReleasesCost(string vendorReason, string expectedCode)
    {
        var client = SubmittingClient("r1");
        PollReturns(client, "r1", new ExternalJobStatus(ExternalJobState.Failed, null, vendorReason));

        var (result, factory, startedAt) = await RunOneTake(client);

        var row = Row(factory);
        row.Status.Should().Be(ExternalGenerationJobStatus.Failed);
        row.UpdatedAt.Should().BeOnOrAfter(startedAt);
        var clip = Clip(result);
        clip["failureReason"]!.GetValue<string>().Should().Be(expectedCode);
        clip["detail"]!.GetValue<string>().Should().Contain(vendorReason);
        clip["costBasis"]!.GetValue<string>().Should().Be("released");
        clip["cost"]!.GetValue<decimal>().Should().Be(0m);
    }

    [Fact]
    public async Task PollCancelled_MarksRowCancelled_WithoutWaitingForTimeout()
    {
        var client = SubmittingClient("r1");
        PollReturns(client, "r1", new ExternalJobStatus(ExternalJobState.Cancelled, null, null));

        var (result, factory, _) = await RunOneTake(client);

        Row(factory).Status.Should().Be(ExternalGenerationJobStatus.Cancelled);
        Clip(result)["failureReason"]!.GetValue<string>().Should().Be("PROVIDER_CANCELLED");
        Clip(result)["costBasis"]!.GetValue<string>().Should().Be("released");
        client.Verify(c => c.PollAsync(It.IsAny<ExternalJobHandle>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(402, "PROVIDER_INSUFFICIENT_BALANCE")]
    [InlineData(401, "PROVIDER_AUTH_FAILED")]
    [InlineData(403, "PROVIDER_AUTH_FAILED")]
    [InlineData(400, "PROVIDER_REJECTED")]
    [InlineData(422, "PROVIDER_REJECTED")]
    public async Task Submit4xxRejection_MarksRowFailed(int statusCode, string expectedCode)
    {
        var client = MiniMaxDescribingClient();
        client
            .Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("rejected", null, (System.Net.HttpStatusCode)statusCode));

        var (result, factory, startedAt) = await RunOneTake(client);

        var row = Row(factory);
        row.Status.Should().Be(ExternalGenerationJobStatus.Failed);
        row.UpdatedAt.Should().BeOnOrAfter(startedAt);
        Clip(result)["failureReason"]!.GetValue<string>().Should().Be(expectedCode);
        Clip(result)["costBasis"]!.GetValue<string>().Should().Be("released");
        Clip(result)["cost"]!.GetValue<decimal>().Should().Be(0m);
    }

    public static TheoryData<string> UnknownSubmitOutcomes => new() { "500", "503", "408", "no-status", "malformed", "cancelled" };

    [Theory]
    [MemberData(nameof(UnknownSubmitOutcomes))]
    public async Task SubmitOutcomeUnknown_LeavesRowReservedAndCharged(string kind)
    {
        Exception ex = kind switch
        {
            "no-status" => new HttpRequestException("connection reset"),
            "malformed" => new JsonException("unexpected 2xx body"),
            "cancelled" => new TaskCanceledException("client timeout"),
            _ => new HttpRequestException("server", null, (System.Net.HttpStatusCode)int.Parse(kind))
        };
        var client = MiniMaxDescribingClient();
        client
            .Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ex);

        var (result, factory, startedAt) = await RunOneTake(client);

        var row = Row(factory);
        row.Status.Should().Be(ExternalGenerationJobStatus.Reserved);
        row.RemoteJobId.Should().BeNull();
        row.UpdatedAt.Should().BeOnOrAfter(startedAt);
        Clip(result)["failureReason"]!.GetValue<string>().Should().Be("SUBMIT_OUTCOME_UNKNOWN");
        Clip(result)["costBasis"]!.GetValue<string>().Should().Be("estimate");
        Clip(result)["cost"]!.GetValue<decimal>().Should().Be(TakeEstimate);
    }

    [Fact]
    public async Task SubmitReturnsEmptyRemoteJobId_TreatedAsUnknownOutcome()
    {
        var client = SubmittingClient("");

        var (result, factory, _) = await RunOneTake(client);

        Row(factory).Status.Should().Be(ExternalGenerationJobStatus.Reserved);
        Clip(result)["failureReason"]!.GetValue<string>().Should().Be("SUBMIT_OUTCOME_UNKNOWN");
        client.Verify(c => c.PollAsync(It.IsAny<ExternalJobHandle>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollTimeout_LeavesRowSubmitted_AndReportsEstimate()
    {
        var clock = new ManualTimeProvider();
        var client = SubmittingClient("r1");
        client
            .Setup(c => c.PollAsync(It.Is<ExternalJobHandle>(h => h.RemoteJobId == "r1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                clock.Advance(TimeSpan.FromSeconds(61)); // past the in-range 60s TimeoutSeconds
                return new ExternalJobStatus(ExternalJobState.Running, null, null);
            });

        var (result, factory, startedAt) = await RunOneTake(client, timeProvider: clock);

        var row = Row(factory);
        row.Status.Should().Be(ExternalGenerationJobStatus.Submitted);
        row.RemoteJobId.Should().Be("r1");
        row.UpdatedAt.Should().BeOnOrAfter(startedAt);
        Clip(result)["failureReason"]!.GetValue<string>().Should().Be("POLL_TIMEOUT");
        Clip(result)["costBasis"]!.GetValue<string>().Should().Be("estimate");
        Clip(result)["cost"]!.GetValue<decimal>().Should().Be(TakeEstimate);
    }

    [Fact]
    public async Task PollThrows_LeavesRowSubmitted_PollError()
    {
        var client = SubmittingClient("r1");
        client
            .Setup(c => c.PollAsync(It.IsAny<ExternalJobHandle>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("poll 503", null, System.Net.HttpStatusCode.ServiceUnavailable));

        var (result, factory, _) = await RunOneTake(client);

        Row(factory).Status.Should().Be(ExternalGenerationJobStatus.Submitted);
        Clip(result)["failureReason"]!.GetValue<string>().Should().Be("POLL_ERROR");
        Clip(result)["costBasis"]!.GetValue<string>().Should().Be("estimate");
        Clip(result)["cost"]!.GetValue<decimal>().Should().Be(TakeEstimate);
    }

    [Fact]
    public async Task SucceededWithoutDownloadUrl_LeavesRowSubmitted_WithActualCost()
    {
        var client = SubmittingClient("r1");
        PollReturns(client, "r1", new ExternalJobStatus(ExternalJobState.Succeeded, null, null, UsageTotalSeconds: 10));

        var (result, factory, _) = await RunOneTake(client);

        var row = Row(factory);
        row.Status.Should().Be(ExternalGenerationJobStatus.Submitted);
        row.ActualCost.Should().Be(CreditCurrency.CreditsFromUsd(0.80m));
        Clip(result)["failureReason"]!.GetValue<string>().Should().Be("NO_DOWNLOAD_URL");
        Clip(result)["costBasis"]!.GetValue<string>().Should().Be("actual");
        Clip(result)["cost"]!.GetValue<decimal>().Should().Be(CreditCurrency.CreditsFromUsd(0.80m));
    }

    [Fact]
    public async Task DeliveryFailure_LeavesRowSubmitted_WithActualCost()
    {
        var client = SubmittingClient("r1");
        PollReturns(client, "r1", new ExternalJobStatus(ExternalJobState.Succeeded, "https://example.com/v.mp4", null, UsageTotalSeconds: 6));
        client
            .Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("download broke"));

        var (result, factory, _) = await RunOneTake(client);

        var row = Row(factory);
        row.Status.Should().Be(ExternalGenerationJobStatus.Submitted);
        row.ActualCost.Should().Be(TakeEstimate);
        row.StorageKey.Should().BeNull();
        Clip(result)["failureReason"]!.GetValue<string>().Should().Be("DELIVERY_FAILED");
        Clip(result)["costBasis"]!.GetValue<string>().Should().Be("actual");
    }

    /// <summary>S-13 (round 2): the clip detail of a failed delivery is a cleaned sentence; the exception's type and text
    /// (here an internal host:port) stay in the logs.</summary>
    [Fact]
    public async Task DeliveryFailure_detail_never_carries_the_raw_exception_text()
    {
        var client = SubmittingClient("r1");
        PollReturns(client, "r1", new ExternalJobStatus(ExternalJobState.Succeeded, "https://example.com/v.mp4", null, UsageTotalSeconds: 6));
        client
            .Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused (minio.internal:9000)"));

        var (result, _, _) = await RunOneTake(client);

        string detail = Clip(result)["detail"]!.GetValue<string>();
        detail.Should().NotContain("minio.internal").And.NotContain("9000").And.NotContain("Exception");
        detail.Should().StartWith("The provider finished the clip");
        result.Output.Should().NotContain("minio.internal");
    }

    /// <summary>S-13 (round 2): the step-level UNEXPECTED_ERROR output carries the code and a fixed sentence, not ex.Message.</summary>
    [Fact]
    public async Task An_unexpected_step_error_output_never_carries_the_raw_exception_text()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = new Mock<IVideoGenerationClient>();
        client.As<IVideoGenerationPriceOracle>()
            .Setup(o => o.QuoteAsync(It.IsAny<VideoGenerationPriceQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("price service at 10.1.2.3:8080 refused the connection"));
        var gate = new Mock<IVideoGenerationBudgetGate>(MockBehavior.Strict);

        var (executor, context) = BuildVendorExecutor(
            gate.Object, client.Object, factory, HiggsfieldCfg(), NewExecution(), CancellationToken.None);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        Meta(result)["failed"]!.GetValue<string>().Should().Be("UNEXPECTED_ERROR");
        string error = Meta(result)["error"]!.GetValue<string>();
        error.Should().NotBeNullOrWhiteSpace();
        error.Should().NotContain("10.1.2.3").And.NotContain("8080").And.NotContain("Exception");
        (result.ErrorDetails ?? string.Empty).Should().NotContain("10.1.2.3");
    }

    [Fact]
    public async Task Delivered_MarksRowSucceeded_WithVendorUsageCostNotEstimate()
    {
        var client = SubmittingClient("r1");
        PollReturns(client, "r1", new ExternalJobStatus(ExternalJobState.Succeeded, "https://example.com/v.mp4", null, UsageTotalSeconds: 10));
        client
            .Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3]));

        var (result, factory, startedAt) = await RunOneTake(client, workspace: UploadingWorkspace(), runner: WorkingRunner());

        result.Status.Should().Be(StepStatus.Completed);
        var row = Row(factory);
        row.Status.Should().Be(ExternalGenerationJobStatus.Succeeded);
        row.StorageKey.Should().NotBeNullOrEmpty();
        row.ActualCost.Should().Be(CreditCurrency.CreditsFromUsd(0.80m), "10 metered seconds x $0.08/s converted to credits, not the 6s estimate");
        row.UpdatedAt.Should().BeOnOrAfter(startedAt);
        Clip(result)["status"]!.GetValue<string>().Should().Be("succeeded");
        Clip(result)["costBasis"]!.GetValue<string>().Should().Be("actual");
        Clip(result)["cost"]!.GetValue<decimal>().Should().Be(CreditCurrency.CreditsFromUsd(0.80m));
    }

    [Fact]
    public async Task Delivered_WithoutUsage_LeavesActualNull_AndReportsEstimate()
    {
        var client = SubmittingClient("r1");
        PollReturns(client, "r1", new ExternalJobStatus(ExternalJobState.Succeeded, "https://example.com/v.mp4", null));
        client
            .Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3]));

        var (result, factory, _) = await RunOneTake(client, workspace: UploadingWorkspace(), runner: WorkingRunner());

        var row = Row(factory);
        row.Status.Should().Be(ExternalGenerationJobStatus.Succeeded);
        row.ActualCost.Should().BeNull("the copy-of-estimate is gone");
        Clip(result)["costBasis"]!.GetValue<string>().Should().Be("estimate");
        Clip(result)["cost"]!.GetValue<decimal>().Should().Be(TakeEstimate);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(-3, null)]
    public async Task ActualCost_ZeroUsageIsZero_NegativeUsageIsNull(int usage, string? expected)
    {
        var client = SubmittingClient("r1");
        PollReturns(client, "r1", new ExternalJobStatus(ExternalJobState.Succeeded, null, null, UsageTotalSeconds: usage));

        var (_, factory, _) = await RunOneTake(client);

        Row(factory).ActualCost.Should().Be(expected is null ? null : decimal.Parse(expected));
    }

    [Fact]
    public async Task FreshTakeNeverSubmitted_WhenStepCancelled_MarksRowCancelled()
    {
        var factory = new SharedInMemoryDbFactory();
        var realGate = RealGate(factory);
        using var cts = new CancellationTokenSource();
        var gate = new Mock<IVideoGenerationBudgetGate>();
        gate
            .Setup(g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (VideoGenerationReservationRequest r, CancellationToken _) =>
            {
                var reservation = await realGate.ReserveAsync(r, CancellationToken.None);
                cts.Cancel(); // the step is stopped between reserve and the first submit
                return reservation;
            });
        var client = SubmittingClient("r1", "r2");
        var (executor, context) = BuildExecutor(gate.Object, client.Object, factory, Cfg(2), NewExecution());
        context = new StepExecutionContext
        {
            StepResultId = Guid.NewGuid(),
            Execution = context.Execution,
            Step = context.Step,
            AllSteps = context.AllSteps,
            AccumulatedOutput = context.AccumulatedOutput,
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test-correlation",
            StepOutputHistory = [],
            CancellationToken = cts.Token
        };

        var result = await executor.ExecuteAsync(context);

        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Row(factory, "clip-1").Status.Should().Be(ExternalGenerationJobStatus.Cancelled);
        Row(factory, "clip-2").Status.Should().Be(ExternalGenerationJobStatus.Cancelled);
        Clip(result, 0)["failureReason"]!.GetValue<string>().Should().Be("NOT_SUBMITTED");
        Clip(result, 1)["costBasis"]!.GetValue<string>().Should().Be("released");
        result.Status.Should().Be(StepStatus.Failed);
    }

    [Fact]
    public async Task UnexpectedPerTakeException_ReportsUnexpectedError_AndChargedEstimate()
    {
        var factory = new SharedInMemoryDbFactory();
        var gate = new Mock<IVideoGenerationBudgetGate>();
        gate
            .Setup(g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoGenerationReservation(true, TakeEstimate, 0m, 0m, null,
                [new ReservedTake(Guid.NewGuid(), "clip-1", DefaultHash(1), TakeEstimate, ExternalGenerationJobStatus.Reserved, Existing: false)]));
        var client = SubmittingClient("r1");
        var (executor, context) = BuildExecutor(gate.Object, client.Object, factory, Cfg(1), NewExecution());

        var result = await executor.ExecuteAsync(context);

        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Clip(result)["failureReason"]!.GetValue<string>().Should().Be("UNEXPECTED_ERROR");
        Clip(result)["detail"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        Clip(result)["costBasis"]!.GetValue<string>().Should().Be("estimate");
        Clip(result)["cost"]!.GetValue<decimal>().Should().Be(TakeEstimate);
    }

    [Fact]
    public async Task ExistingReservationWithoutRemoteJobId_IsOrphaned_NeverSubmitted_RowUntouched()
    {
        var factory = new SharedInMemoryDbFactory();
        var execution = NewExecution();
        var createdAt = DateTime.UtcNow.AddMinutes(-7);
        var seededId = Guid.NewGuid();
        using (var db = factory.CreateDbContext())
        {
            db.ExternalGenerationJobs.Add(new ExternalGenerationJob
            {
                Id = seededId,
                ExecutionId = execution.Id,
                ProjectId = execution.ProjectId,
                ClipId = "clip-1",
                RequestHash = DefaultHash(1),
                Status = ExternalGenerationJobStatus.Reserved,
                CostEstimate = TakeEstimate,
                CreatedAt = createdAt,
                UpdatedAt = createdAt
            });
            await db.SaveChangesAsync();
        }

        var client = SubmittingClient("r1");
        var (executor, context) = BuildExecutor(RealGate(factory), client.Object, factory, Cfg(1), execution);

        var result = await executor.ExecuteAsync(context);

        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        var row = Row(factory);
        row.Id.Should().Be(seededId);
        row.Status.Should().Be(ExternalGenerationJobStatus.Reserved);
        row.RemoteJobId.Should().BeNull();
        row.UpdatedAt.Should().Be(createdAt, "an orphaned row is left untouched");
        var clip = Clip(result);
        clip["failureReason"]!.GetValue<string>().Should().Be("RESERVATION_ORPHANED");
        var detail = clip["detail"]!.GetValue<string>();
        detail.Should().Contain("provider console").And.Contain("CreatedAt").And.Contain("double billing").And.Contain("UTC day");
        clip["costBasis"]!.GetValue<string>().Should().Be("estimate");
        clip["cost"]!.GetValue<decimal>().Should().Be(TakeEstimate);
    }

    [Theory]
    [InlineData(ExternalGenerationJobStatus.Submitted)]
    [InlineData(ExternalGenerationJobStatus.Running)]
    public async Task ExistingTakeWithRemoteJobId_IsPolled_NeverResubmitted(ExternalGenerationJobStatus seededStatus)
    {
        var factory = new SharedInMemoryDbFactory();
        var execution = NewExecution();
        var seededId = Guid.NewGuid();
        using (var db = factory.CreateDbContext())
        {
            db.ExternalGenerationJobs.Add(new ExternalGenerationJob
            {
                Id = seededId,
                ExecutionId = execution.Id,
                ProjectId = execution.ProjectId,
                ClipId = "clip-1",
                RequestHash = DefaultHash(1),
                RemoteJobId = "remote-prior",
                Status = seededStatus,
                CostEstimate = TakeEstimate
            });
            await db.SaveChangesAsync();
        }

        // The real gate only reports Reserved/Submitted rows as Existing; a Running row is fed via a thin mock.
        var gate = new Mock<IVideoGenerationBudgetGate>();
        gate
            .Setup(g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoGenerationReservation(true, TakeEstimate, 0m, 0m, null,
                [new ReservedTake(seededId, "clip-1", DefaultHash(1), TakeEstimate, seededStatus, Existing: true)]));
        var client = SubmittingClient("should-not-happen");
        PollReturns(client, "remote-prior", new ExternalJobStatus(ExternalJobState.Failed, null, "vendor said no"));

        var (executor, context) = BuildExecutor(
            seededStatus == ExternalGenerationJobStatus.Submitted ? RealGate(factory) : gate.Object,
            client.Object, factory, Cfg(1), execution);

        var result = await executor.ExecuteAsync(context);

        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(c => c.PollAsync(It.Is<ExternalJobHandle>(h => h.RemoteJobId == "remote-prior"), It.IsAny<CancellationToken>()), Times.Once);
        Row(factory).Status.Should().Be(ExternalGenerationJobStatus.Failed);
        Clip(result)["failureReason"]!.GetValue<string>().Should().Be("PROVIDER_FAILED");
    }

    [Fact]
    public async Task PartialSuccess_CompletesWithCostLabelsAndMetaTotals()
    {
        var client = SubmittingClient("r1", "r2", "r3");
        PollReturns(client, "r1", new ExternalJobStatus(ExternalJobState.Succeeded, "https://example.com/v.mp4", null, UsageTotalSeconds: 10));
        PollReturns(client, "r2", new ExternalJobStatus(ExternalJobState.Failed, null, "vendor said no"));
        client
            .Setup(c => c.PollAsync(It.Is<ExternalJobHandle>(h => h.RemoteJobId == "r3"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network"));
        client
            .Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3]));
        var factory = new SharedInMemoryDbFactory();
        var (executor, context) = BuildExecutor(RealGate(factory), client.Object, factory, Cfg(3), NewExecution(),
            UploadingWorkspace().Object, WorkingRunner().Object);

        var result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        result.OutputStorageKey.Should().NotBeNullOrEmpty();
        Clip(result, 0)["costBasis"]!.GetValue<string>().Should().Be("actual");
        Clip(result, 1)["costBasis"]!.GetValue<string>().Should().Be("released");
        Clip(result, 2)["costBasis"]!.GetValue<string>().Should().Be("estimate");
        var meta = Meta(result);
        meta["actualCost"]!.GetValue<decimal>().Should().Be(CreditCurrency.CreditsFromUsd(0.80m));
        meta["unconfirmedCost"]!.GetValue<decimal>().Should().Be(TakeEstimate);
        meta["totalCost"]!.GetValue<decimal>().Should().Be(CreditCurrency.CreditsFromUsd(0.80m) + TakeEstimate);
        meta["estimatedCost"]!.GetValue<decimal>().Should().Be(3 * TakeEstimate);
        meta["clipsGenerated"]!.GetValue<int>().Should().Be(1);
        meta["clipsFailed"]!.GetValue<int>().Should().Be(2);
        meta["failed"].Should().BeNull();
    }

    [Fact]
    public async Task AllTakesFailed_ReturnsFailedWithFullOutputAndErrorDetails()
    {
        var client = SubmittingClient("r1", "r2");
        PollReturns(client, "r1", new ExternalJobStatus(ExternalJobState.Failed, null, "vendor said no"));
        client
            .Setup(c => c.PollAsync(It.Is<ExternalJobHandle>(h => h.RemoteJobId == "r2"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network"));
        var factory = new SharedInMemoryDbFactory();
        var (executor, context) = BuildExecutor(RealGate(factory), client.Object, factory, Cfg(2), NewExecution());

        var result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().StartWith("clip-1:PROVIDER_FAILED, clip-2:POLL_ERROR");
        // The vendor's own reason rides along, so a failed step no longer reads as a bare code.
        result.ErrorDetails.Should().Contain("vendor said no");
        JsonNode.Parse(result.Output)!["clips"]!.AsArray().Should().HaveCount(2);
        var meta = Meta(result);
        meta["failed"]!.GetValue<string>().Should().Be("ALL_TAKES_FAILED");
        meta["clipsFailed"]!.GetValue<int>().Should().Be(2);
        meta["totalCost"]!.GetValue<decimal>().Should().Be(TakeEstimate);
        meta["unconfirmedCost"]!.GetValue<decimal>().Should().Be(TakeEstimate);
        meta["actualCost"]!.GetValue<decimal>().Should().Be(0m);
    }

    [Fact]
    public async Task RetryWithSameExecution_AfterPollTimeout_PollsSameRemoteJob_WithoutResubmitting()
    {
        // Real gate over InMemory, same ExecutionId across two ExecuteAsync attempts — what
        // WorkflowExecutorService's retry does when a VideoGenerate step returns Failed.
        var factory = new SharedInMemoryDbFactory();
        var execution = NewExecution();
        var client = SubmittingClient("remote-once", "remote-twice");
        var vendorDone = false;
        var clock = new ManualTimeProvider();
        client
            .Setup(c => c.PollAsync(It.IsAny<ExternalJobHandle>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExternalJobHandle h, CancellationToken _) =>
            {
                if (vendorDone && h.RemoteJobId == "remote-once")
                {
                    return new ExternalJobStatus(ExternalJobState.Succeeded, null, null, UsageTotalSeconds: 6);
                }

                clock.Advance(TimeSpan.FromSeconds(61)); // past the in-range 60s TimeoutSeconds
                return new ExternalJobStatus(ExternalJobState.Running, null, null);
            });

        var (executor1, context1) = BuildExecutor(RealGate(factory), client.Object, factory, Cfg(1), execution, timeProvider: clock);
        var attempt1 = await executor1.ExecuteAsync(context1);
        attempt1.Status.Should().Be(StepStatus.Failed);
        Clip(attempt1)["failureReason"]!.GetValue<string>().Should().Be("POLL_TIMEOUT");
        vendorDone = true; // the vendor finishes between attempts

        var (executor2, context2) = BuildExecutor(RealGate(factory), client.Object, factory, Cfg(1), execution, timeProvider: clock);
        var attempt2 = await executor2.ExecuteAsync(context2);

        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        using var db = factory.CreateDbContext();
        var rows = db.ExternalGenerationJobs.ToList();
        rows.Should().ContainSingle();
        rows[0].RemoteJobId.Should().Be("remote-once");
        rows[0].ActualCost.Should().Be(TakeEstimate);
        Clip(attempt2)["failureReason"]!.GetValue<string>().Should().Be("NO_DOWNLOAD_URL");
    }

    // ------------------------------------------------------------------------------------------
    // T-021: MaxClips hard ceiling (Security C5) and the per-provider concurrency limiter
    // (VideoGeneration:MaxConcurrentPerProvider). Concurrency is driven by TaskCompletionSource-
    // gated PollAsync/DownloadAsync fakes; the only real waits are short "prove it did NOT
    // progress" windows. One executor instance is shared across executions, since the limiter
    // is instance-level state on the (DI-singleton) executor.
    // ------------------------------------------------------------------------------------------

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NoProgressWindow = TimeSpan.FromMilliseconds(200);
    private static readonly Guid ProviderA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ProviderB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    [Theory]
    [InlineData(3, 2)]
    [InlineData(1, 65)]
    [InlineData(1, 0)]
    public async Task OverCeilingConfig_FailsConfigInvalid_BeforeAnyReservationSubmitOrRow(int takes, int maxClips)
    {
        var factory = new SharedInMemoryDbFactory();
        var gate = new Mock<IVideoGenerationBudgetGate>();
        var client = SubmittingClient("r1", "r2", "r3");
        var configJson = $$"""{"maxSpendCredits": 100000.0, "takes": {{takes}}, "maxClips": {{maxClips}}, "inlinePrompt": "Create a video"}""";
        var (executor, context) = BuildExecutor(gate.Object, client.Object, factory, configJson, NewExecution());

        var result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        Meta(result)["failed"]!.GetValue<string>().Should().Be("CONFIG_INVALID");
        Meta(result)["error"]!.GetValue<string>().Should().Contain("MaxClips");
        gate.Verify(g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        using var db = factory.CreateDbContext();
        db.ExternalGenerationJobs.Count().Should().Be(0);
    }

    [Fact]
    public async Task TimeoutSecondsZero_FailsConfigInvalid_BeforeAnyReservationOrSubmit()
    {
        // T-025: TimeoutSeconds 0 used to submit a paid take and end it POLL_TIMEOUT without a poll.
        var factory = new SharedInMemoryDbFactory();
        var gate = new Mock<IVideoGenerationBudgetGate>();
        var client = SubmittingClient("r1");
        var configJson = """{"maxSpendCredits": 100000.0, "takes": 1, "inlinePrompt": "Create a video", "timeoutSeconds": 0}""";
        var (executor, context) = BuildExecutor(gate.Object, client.Object, factory, configJson, NewExecution());

        var result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        Meta(result)["failed"]!.GetValue<string>().Should().Be("CONFIG_INVALID");
        Meta(result)["error"]!.GetValue<string>().Should().Contain("TimeoutSeconds");
        gate.Verify(g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        using var db = factory.CreateDbContext();
        db.ExternalGenerationJobs.Count().Should().Be(0);
    }

    [Fact]
    public async Task Capacity1_SecondExecutionOnSameProvider_DoesNotSubmitUntilFirstPollingEnds()
    {
        var vendor = new GatedVendor();
        var factory = new SharedInMemoryDbFactory();
        var (executor, ctx) = BuildSharedExecutor(RealGate(factory), vendor.Client.Object, factory, maxConcurrentPerProvider: 1);

        // ProviderId null on both: the limiter key falls back to Guid.Empty, shared by both.
        var run1 = executor.ExecuteAsync(ctx(CfgFor(null), NewExecution(), CancellationToken.None));
        await vendor.PollStarted("r1").WaitAsync(Deadline);
        var run2 = executor.ExecuteAsync(ctx(CfgFor(null), NewExecution(), CancellationToken.None));
        await Task.Delay(NoProgressWindow);

        vendor.SubmitCount.Should().Be(1, "the only slot is held by the first execution's take until its polling ends");
        run2.IsCompleted.Should().BeFalse();

        vendor.CompletePoll("r2", new ExternalJobStatus(ExternalJobState.Failed, null, "vendor said no"));
        vendor.CompletePoll("r1", new ExternalJobStatus(ExternalJobState.Failed, null, "vendor said no"));
        await Task.WhenAll(run1, run2).WaitAsync(Deadline);

        var events = vendor.Events.ToList();
        events.Should().Equal("submit:r1", "poll:r1", "poll-end:r1", "submit:r2", "poll:r2", "poll-end:r2");
    }

    [Fact]
    public async Task Capacity1_OtherProvider_IsNotBlocked()
    {
        var vendor = new GatedVendor();
        var factory = new SharedInMemoryDbFactory();
        var (executor, ctx) = BuildSharedExecutor(RealGate(factory), vendor.Client.Object, factory, maxConcurrentPerProvider: 1);

        var run1 = executor.ExecuteAsync(ctx(CfgFor(ProviderA), NewExecution(), CancellationToken.None));
        await vendor.PollStarted("r1").WaitAsync(Deadline);
        vendor.CompletePoll("r2", new ExternalJobStatus(ExternalJobState.Failed, null, "vendor said no"));

        var result2 = await executor.ExecuteAsync(ctx(CfgFor(ProviderB), NewExecution(), CancellationToken.None)).WaitAsync(Deadline);

        Clip(result2)["failureReason"]!.GetValue<string>().Should().Be("PROVIDER_FAILED");
        run1.IsCompleted.Should().BeFalse("provider A's slot is still held");
        vendor.CompletePoll("r1", new ExternalJobStatus(ExternalJobState.Failed, null, "vendor said no"));
        await run1.WaitAsync(Deadline);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(2, 2)]
    public async Task LimiterCapacity_IsMaxOfOneAndConfiguredValue(int configured, int expectedConcurrentSubmits)
    {
        var vendor = new GatedVendor();
        var factory = new SharedInMemoryDbFactory();
        var (executor, ctx) = BuildSharedExecutor(RealGate(factory), vendor.Client.Object, factory, maxConcurrentPerProvider: configured);

        var run1 = executor.ExecuteAsync(ctx(CfgFor(ProviderA), NewExecution(), CancellationToken.None));
        await vendor.PollStarted("r1").WaitAsync(Deadline);
        var run2 = executor.ExecuteAsync(ctx(CfgFor(ProviderA), NewExecution(), CancellationToken.None));
        if (expectedConcurrentSubmits > 1)
        {
            // Two slots: the second submit must happen while r1 still polls. Wait for it (bounded), rather than
            // asserting inside a fixed window a loaded CI host can miss (QA T-021).
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (vendor.SubmitCount < expectedConcurrentSubmits && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
        }
        else
        {
            // One slot: prove the second take does NOT submit while r1 holds it.
            await Task.Delay(NoProgressWindow);
        }

        vendor.SubmitCount.Should().Be(expectedConcurrentSubmits);

        vendor.CompletePoll("r1", new ExternalJobStatus(ExternalJobState.Failed, null, "vendor said no"));
        vendor.CompletePoll("r2", new ExternalJobStatus(ExternalJobState.Failed, null, "vendor said no"));
        await Task.WhenAll(run1, run2).WaitAsync(Deadline);
        vendor.SubmitCount.Should().Be(2);
    }

    [Fact]
    public async Task FreshTakeCancelledWhileWaitingForSlot_EndsCancelledNotSubmitted()
    {
        var vendor = new GatedVendor();
        var factory = new SharedInMemoryDbFactory();
        var (executor, ctx) = BuildSharedExecutor(RealGate(factory), vendor.Client.Object, factory, maxConcurrentPerProvider: 1);

        var run1 = executor.ExecuteAsync(ctx(CfgFor(ProviderA), NewExecution(), CancellationToken.None));
        await vendor.PollStarted("r1").WaitAsync(Deadline);

        using var cts = new CancellationTokenSource();
        var execution2 = NewExecution();
        var run2 = executor.ExecuteAsync(ctx(CfgFor(ProviderA), execution2, cts.Token));
        await Task.Delay(NoProgressWindow);
        cts.Cancel();
        var result2 = await run2.WaitAsync(Deadline);

        vendor.SubmitCount.Should().Be(1);
        RowFor(factory, execution2.Id).Status.Should().Be(ExternalGenerationJobStatus.Cancelled);
        Clip(result2)["failureReason"]!.GetValue<string>().Should().Be("NOT_SUBMITTED");
        Clip(result2)["costBasis"]!.GetValue<string>().Should().Be("released");

        vendor.CompletePoll("r1", new ExternalJobStatus(ExternalJobState.Failed, null, "vendor said no"));
        await run1.WaitAsync(Deadline);
    }

    [Fact]
    public async Task ResumedTakeCancelledWhileWaitingForSlot_StaysSubmitted_PollError()
    {
        var vendor = new GatedVendor();
        var factory = new SharedInMemoryDbFactory();
        var execution2 = NewExecution();
        using (var db = factory.CreateDbContext())
        {
            db.ExternalGenerationJobs.Add(new ExternalGenerationJob
            {
                Id = Guid.NewGuid(),
                ExecutionId = execution2.Id,
                ProjectId = execution2.ProjectId,
                ClipId = "clip-1",
                RequestHash = DefaultHash(1),
                RemoteJobId = "remote-prior",
                Status = ExternalGenerationJobStatus.Submitted,
                CostEstimate = TakeEstimate
            });
            await db.SaveChangesAsync();
        }

        var (executor, ctx) = BuildSharedExecutor(RealGate(factory), vendor.Client.Object, factory, maxConcurrentPerProvider: 1);
        var run1 = executor.ExecuteAsync(ctx(CfgFor(ProviderA), NewExecution(), CancellationToken.None));
        await vendor.PollStarted("r1").WaitAsync(Deadline);

        using var cts = new CancellationTokenSource();
        var run2 = executor.ExecuteAsync(ctx(CfgFor(ProviderA), execution2, cts.Token));
        await Task.Delay(NoProgressWindow);
        cts.Cancel();
        var result2 = await run2.WaitAsync(Deadline);

        vendor.Events.Should().NotContain("poll:remote-prior", "a take waiting for a slot must not poll");
        var row = RowFor(factory, execution2.Id);
        row.Status.Should().Be(ExternalGenerationJobStatus.Submitted);
        row.RemoteJobId.Should().Be("remote-prior");
        Clip(result2)["failureReason"]!.GetValue<string>().Should().Be("POLL_ERROR");
        Clip(result2)["costBasis"]!.GetValue<string>().Should().Be("estimate");

        vendor.CompletePoll("r1", new ExternalJobStatus(ExternalJobState.Failed, null, "vendor said no"));
        await run1.WaitAsync(Deadline);
    }

    [Fact]
    public async Task SlotIsReleasedBeforeDownload_SoAnotherExecutionProceedsDuringDelivery()
    {
        var vendor = new GatedVendor();
        vendor.GateDownload();
        vendor.CompletePoll("r1", new ExternalJobStatus(ExternalJobState.Succeeded, "https://example.com/v.mp4", null, UsageTotalSeconds: 6));
        vendor.CompletePoll("r2", new ExternalJobStatus(ExternalJobState.Failed, null, "vendor said no"));
        var factory = new SharedInMemoryDbFactory();
        var (executor, ctx) = BuildSharedExecutor(RealGate(factory), vendor.Client.Object, factory, maxConcurrentPerProvider: 1,
            workspace: UploadingWorkspace().Object, runner: WorkingRunner().Object);

        var run1 = executor.ExecuteAsync(ctx(CfgFor(ProviderA), NewExecution(), CancellationToken.None));
        await vendor.DownloadStarted.WaitAsync(Deadline);

        var result2 = await executor.ExecuteAsync(ctx(CfgFor(ProviderA), NewExecution(), CancellationToken.None)).WaitAsync(Deadline);

        Clip(result2)["failureReason"]!.GetValue<string>().Should().Be("PROVIDER_FAILED");
        run1.IsCompleted.Should().BeFalse("execution 1 is still downloading, without holding the slot");
        vendor.ReleaseDownload();
        (await run1.WaitAsync(Deadline)).Status.Should().Be(StepStatus.Completed);
    }

    [Theory]
    [InlineData("submit-rejected")]
    [InlineData("submit-unknown")]
    [InlineData("submit-no-job-id")]
    [InlineData("poll-error")]
    [InlineData("poll-failed")]
    [InlineData("poll-cancelled")]
    [InlineData("poll-timeout")]
    [InlineData("no-download-url")]
    [InlineData("delivery-failed")]
    [InlineData("unexpected")]
    public async Task Capacity1_SlotIsReleasedOnEveryTakeExit_SoTheNextTakeRuns(string exit)
    {
        var clock = new ManualTimeProvider();
        var client = MiniMaxDescribingClient();
        var submits = 0;
        client
            .Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                var n = Interlocked.Increment(ref submits);
                if (n == 1)
                {
                    switch (exit)
                    {
                        case "submit-rejected":
                            return Task.FromException<ExternalJobHandle>(new HttpRequestException("bad", null, System.Net.HttpStatusCode.BadRequest));
                        case "submit-unknown":
                            return Task.FromException<ExternalJobHandle>(new HttpRequestException("boom", null, System.Net.HttpStatusCode.InternalServerError));
                        case "submit-no-job-id":
                            return Task.FromResult<ExternalJobHandle>(new ExternalJobHandle(""));
                    }
                }

                return Task.FromResult<ExternalJobHandle>(new ExternalJobHandle($"r{n}"));
            });
        client
            .Setup(c => c.PollAsync(It.Is<ExternalJobHandle>(h => h.RemoteJobId == "r1"), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                switch (exit)
                {
                    case "poll-error":
                        return Task.FromException<ExternalJobStatus>(new HttpRequestException("network"));
                    case "poll-failed":
                        return Task.FromResult(new ExternalJobStatus(ExternalJobState.Failed, null, "vendor said no"));
                    case "poll-cancelled":
                        return Task.FromResult(new ExternalJobStatus(ExternalJobState.Cancelled, null, null));
                    case "poll-timeout":
                        clock.Advance(TimeSpan.FromHours(1));
                        return Task.FromResult(new ExternalJobStatus(ExternalJobState.Running, null, null));
                    case "no-download-url":
                        return Task.FromResult(new ExternalJobStatus(ExternalJobState.Succeeded, null, null, UsageTotalSeconds: 6));
                    case "delivery-failed":
                        return Task.FromResult(new ExternalJobStatus(ExternalJobState.Succeeded, "https://example.com/v.mp4", null, UsageTotalSeconds: 6));
                    default: // "unexpected": a null status blows up outside every mapped path
                        return Task.FromResult<ExternalJobStatus>(null!);
                }
            });
        PollReturns(client, "r2", new ExternalJobStatus(ExternalJobState.Failed, null, "vendor said no"));
        client
            .Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));

        var factory = new SharedInMemoryDbFactory();
        var (executor, ctx) = BuildSharedExecutor(RealGate(factory), client.Object, factory, maxConcurrentPerProvider: 1, timeProvider: clock);

        var result = await executor.ExecuteAsync(ctx(CfgFor(ProviderA, takes: 2), NewExecution(), CancellationToken.None)).WaitAsync(Deadline);

        submits.Should().Be(2, "take 2 must acquire the slot take 1 released");
        Clip(result, 1)["failureReason"]!.GetValue<string>().Should().Be("PROVIDER_FAILED");
    }

    [Fact]
    public async Task PollTimeoutClock_StartsAfterTheSlotIsAcquired()
    {
        var clock = new ManualTimeProvider();
        var vendor = new GatedVendor();
        var factory = new SharedInMemoryDbFactory();
        var (executor, ctx) = BuildSharedExecutor(RealGate(factory), vendor.Client.Object, factory, maxConcurrentPerProvider: 1, timeProvider: clock);

        var run1 = executor.ExecuteAsync(ctx(CfgFor(ProviderA), NewExecution(), CancellationToken.None));
        await vendor.PollStarted("r1").WaitAsync(Deadline);
        var run2 = executor.ExecuteAsync(ctx(CfgFor(ProviderA, timeoutSeconds: 60), NewExecution(), CancellationToken.None));
        await Task.Delay(NoProgressWindow);
        vendor.SubmitCount.Should().Be(1);

        // Execution 2 "waits" an hour for the slot — far past its 60s TimeoutSeconds.
        clock.Advance(TimeSpan.FromHours(1));
        vendor.CompletePoll("r2", new ExternalJobStatus(ExternalJobState.Failed, null, "vendor said no"));
        vendor.CompletePoll("r1", new ExternalJobStatus(ExternalJobState.Failed, null, "vendor said no"));
        await run1.WaitAsync(Deadline);
        var result2 = await run2.WaitAsync(Deadline);

        Clip(result2)["failureReason"]!.GetValue<string>().Should().Be("PROVIDER_FAILED", "time spent waiting for a slot is not polling time");
        vendor.Events.Should().Contain("poll:r2");
    }

    [Fact]
    public async Task OrphanedReservation_NeverWaitsForASlot()
    {
        var vendor = new GatedVendor();
        var factory = new SharedInMemoryDbFactory();
        var execution2 = NewExecution();
        using (var db = factory.CreateDbContext())
        {
            db.ExternalGenerationJobs.Add(new ExternalGenerationJob
            {
                Id = Guid.NewGuid(),
                ExecutionId = execution2.Id,
                ProjectId = execution2.ProjectId,
                ClipId = "clip-1",
                RequestHash = DefaultHash(1),
                Status = ExternalGenerationJobStatus.Reserved,
                CostEstimate = TakeEstimate
            });
            await db.SaveChangesAsync();
        }

        var (executor, ctx) = BuildSharedExecutor(RealGate(factory), vendor.Client.Object, factory, maxConcurrentPerProvider: 1);
        var run1 = executor.ExecuteAsync(ctx(CfgFor(ProviderA), NewExecution(), CancellationToken.None));
        await vendor.PollStarted("r1").WaitAsync(Deadline);

        var result2 = await executor.ExecuteAsync(ctx(CfgFor(ProviderA), execution2, CancellationToken.None)).WaitAsync(Deadline);

        Clip(result2)["failureReason"]!.GetValue<string>().Should().Be("RESERVATION_ORPHANED");
        run1.IsCompleted.Should().BeFalse();
        vendor.CompletePoll("r1", new ExternalJobStatus(ExternalJobState.Failed, null, "vendor said no"));
        await run1.WaitAsync(Deadline);
    }

    // ---- T-021 helpers ----

    private static string CfgFor(Guid? providerId, int takes = 1, int timeoutSeconds = 60) =>
        providerId is { } id
            ? $$"""{"maxSpendCredits": 100000.0, "takes": {{takes}}, "maxClips": {{takes}}, "inlinePrompt": "Create a video", "timeoutSeconds": {{timeoutSeconds}}, "pollIntervalSeconds": 1, "providerId": "{{id}}"}"""
            : Cfg(takes, timeoutSeconds);

    private static ExternalGenerationJob RowFor(SharedInMemoryDbFactory factory, Guid executionId, string clipId = "clip-1")
    {
        using var db = factory.CreateDbContext();
        return db.ExternalGenerationJobs.AsNoTracking().Single(j => j.ExecutionId == executionId && j.ClipId == clipId);
    }

    /// <summary>
    /// One executor (so one set of limiters) shared by every execution the test starts; the
    /// resolver hands back a provider whose ProviderId is the config's (null stays null).
    /// </summary>
    private (VideoGenerateStepExecutor Executor, Func<string, WorkflowExecution, CancellationToken, StepExecutionContext> Context) BuildSharedExecutor(
        IVideoGenerationBudgetGate gate,
        IVideoGenerationClient client,
        IDbContextFactory<WorkflowEngineDbContext> factory,
        int maxConcurrentPerProvider,
        TimeProvider? timeProvider = null,
        IProjectFileWorkspace? workspace = null,
        IVideoToolRunner? runner = null)
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver
            .Setup(r => r.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid? id, CancellationToken _) => new ResolvedVideoGenerationProvider(
                id, "MiniMax", InferenceProviderKind.MiniMax, "https://api.minimax.io", "MiniMax-H3", "key123", 1800));
        var clientFactory = new Mock<IVideoGenerationClientFactory>();
        clientFactory.Setup(f => f.Get(It.IsAny<ResolvedVideoGenerationProvider>())).Returns(client);

        var executor = new VideoGenerateStepExecutor(
            gate,
            resolver.Object,
            clientFactory.Object,
            workspace ?? new Mock<IProjectFileWorkspace>().Object,
            factory,
            new GeneratedClipNormalizer(
                runner ?? new Mock<IVideoToolRunner>().Object,
                Options.Create(_videoOptions),
                NullLogger<GeneratedClipNormalizer>.Instance),
            Options.Create(new VideoGenerationOptions { MaxConcurrentPerProvider = maxConcurrentPerProvider }),
            NullLogger<VideoGenerateStepExecutor>.Instance,
            NoMediaWorkspaces(),
            NoKeyframeExtractor(),
            timeProvider);

        StepExecutionContext Context(string configJson, WorkflowExecution execution, CancellationToken ct)
        {
            var step = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Guid.NewGuid(),
                StepOrder = 1,
                StepType = StepType.VideoGenerate,
                VideoGenerateConfigJson = configJson
            };
            return new StepExecutionContext
            {
                StepResultId = Guid.NewGuid(),
                Execution = execution,
                Step = step,
                AllSteps = [step],
                AccumulatedOutput = string.Empty,
                CurrentStepIndex = 0,
                IterationCount = 0,
                CorrelationId = "test-correlation",
                StepOutputHistory = [],
                CancellationToken = ct
            };
        }

        return (executor, Context);
    }

    /// <summary>A clock that only moves when the test says so (drives the poll timeout).</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>
    /// A vendor fake whose submits hand out r1, r2, ... in call order and whose polls block on a
    /// per-job gate until the test completes it; every call is recorded in order.
    /// </summary>
    private sealed class GatedVendor
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<ExternalJobStatus>> _pollGates = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource> _pollStarted = new();
        private readonly TaskCompletionSource _downloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _downloadGate;
        private int _submits;

        public GatedVendor()
        {
            Client = MiniMaxDescribingClient();
            Client
                .Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var id = $"r{Interlocked.Increment(ref _submits)}";
                    Events.Enqueue($"submit:{id}");
                    return new ExternalJobHandle(id);
                });
            Client
                .Setup(c => c.PollAsync(It.IsAny<ExternalJobHandle>(), It.IsAny<CancellationToken>()))
                .Returns(async (ExternalJobHandle h, CancellationToken ct) =>
                {
                    Events.Enqueue($"poll:{h.RemoteJobId}");
                    StartedGate(h.RemoteJobId).TrySetResult();
                    var status = await PollGate(h.RemoteJobId).Task.WaitAsync(ct);
                    Events.Enqueue($"poll-end:{h.RemoteJobId}");
                    return status;
                });
            Client
                .Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(async (string _, CancellationToken ct) =>
                {
                    _downloadStarted.TrySetResult();
                    if (_downloadGate is { } gate)
                    {
                        await gate.Task.WaitAsync(ct);
                    }

                    return (Stream)new MemoryStream([1, 2, 3]);
                });
        }

        public Mock<IVideoGenerationClient> Client { get; }

        public System.Collections.Concurrent.ConcurrentQueue<string> Events { get; } = new();

        public int SubmitCount => Volatile.Read(ref _submits);

        public Task DownloadStarted => _downloadStarted.Task;

        public Task PollStarted(string remoteJobId) => StartedGate(remoteJobId).Task;

        public void CompletePoll(string remoteJobId, ExternalJobStatus status) => PollGate(remoteJobId).TrySetResult(status);

        public void GateDownload() => _downloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseDownload() => _downloadGate?.TrySetResult();

        private TaskCompletionSource<ExternalJobStatus> PollGate(string id) =>
            _pollGates.GetOrAdd(id, _ => new TaskCompletionSource<ExternalJobStatus>(TaskCreationOptions.RunContinuationsAsynchronously));

        private TaskCompletionSource StartedGate(string id) =>
            _pollStarted.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    // ==========================================================================================
    // the async price oracle on the step path: the pricing basis replaces a bare Describe(Model)
    // ==========================================================================================
    //
    // These tests pin the properties the decision note makes the price of option B:
    //   1. a Higgsfield-shaped client IS offerable and priced — the proof option B shipped
    //      something observable rather than only a wire dialect;
    //   2. an oracle failure REFUSES the step (CONFIG_INVALID, budget gate never reached, zero
    //      ledger rows, no submit) — a fail-closed spend gate must not become fail-open because a
    //      price could not be fetched;
    //   3. a quote in hand is charged as quote.TotalUsd, NOT as a re-multiplied per-second rate;
    //   4. no path reaches a price of zero.
    //
    // NO NETWORK, NO VENDOR KEY. Every "vendor" below is an in-memory Moq proxy: no HTTP call is
    // made anywhere in this section, no API key is read from the environment, and none is needed.

    private const string HiggsfieldEndpointId = "hf-ep-9f1c2b";
    private const decimal VendorQuoteTotalUsd = 10.00m;
    private const int VendorQuoteDurationSeconds = 3;
    private const string VendorQuoteResolution = "1080P";
    private const string VendorQuoteRatio = "16:9";

    /// <summary>
    /// HEADLINE — the async price oracle's observable outcome, on the step path.
    /// <para>
    /// A Higgsfield-shaped client has a <c>Describe</c> that throws <c>NotSupportedException</c>
    /// (the vendor publishes no price table, which is exactly why the model offered ZERO models
    /// before the oracle existed) and an oracle that answers with the vendor's own quote. That step
    /// must NOT be refused at capability validation: it submits, and the ledger row it writes
    /// carries the vendor's own figure.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_higgsfield_shaped_client_is_offerable_and_priced()
    {
        var factory = new SharedInMemoryDbFactory();
        var quote = VendorQuote();
        var client = HiggsfieldShapedClient(quote);
        var submitted = new List<VideoGenerationRequest>();
        Deliveries(client, submitted, "hf-1");
        var gate = new RecordingBudgetGate(RealGate(factory));

        var (executor, context) = BuildVendorExecutor(
            gate, client.Object, factory, HiggsfieldCfg(), NewExecution(), CancellationToken.None);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(
            StepStatus.Completed,
            "a model priced by the vendor's own oracle is offerable — this is what option B shipped");

        client.Verify(
            c => c.Describe(It.IsAny<string>()),
            Times.Never,
            "on this path Describe is the fail-closed table that offers nothing; the oracle answered instead");

        submitted.Should().ContainSingle();
        client.Verify(
            c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the model is offerable, so the step really buys a clip");

        // (1) the step asked the gate to charge the vendor's own figure converted to credits, quote and all.
        gate.Requests.Should().ContainSingle();
        gate.LastRequest!.Takes.Should().ContainSingle();
        gate.LastRequest.Takes[0].CostEstimate.Should().Be(CreditCurrency.CreditsFromUsd(VendorQuoteTotalUsd));
        gate.LastRequest.Takes[0].Quote.Should().BeSameAs(quote);

        // (2) and that is what the ledger recorded.
        using var db = factory.CreateDbContext();
        var rows = db.ExternalGenerationJobs.ToList();
        rows.Should().ContainSingle("one paid take, one ledger row");
        rows[0].CostEstimate.Should().Be(CreditCurrency.CreditsFromUsd(VendorQuoteTotalUsd));
        rows[0].Status.Should().Be(ExternalGenerationJobStatus.Succeeded);
    }

    /// <summary>
    /// MANDATED TEST — price-oracle condition 1.
    /// <para>
    /// An oracle that cannot price throws <c>NotSupportedException</c>, the exception
    /// <c>Describe</c> already threw for an unpriceable model. The step must be REFUSED as
    /// <c>CONFIG_INVALID</c> carrying the oracle's own reason, with the budget gate never reached,
    /// zero <c>ExternalGenerationJobs</c> rows and no submit.
    /// </para>
    /// <para>
    /// The comment the decision note asks for, stated here: this is the deliberate, TESTED choice
    /// required by the price-oracle change. A fail-closed spend gate must not become fail-open because a price could
    /// not be fetched — "we could not find out what this costs" is a refusal, never an implied
    /// zero, and it costs nothing: no lock, no row, no vendor call.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_oracle_failure_refuses_the_step_and_writes_no_ledger_row()
    {
        const string reason = "Higgsfield: POST /estimate/hf-ep-9f1c2b timed out after 10s; the model stays unofferable.";

        var factory = new SharedInMemoryDbFactory();
        var client = new Mock<IVideoGenerationClient>();
        client.As<IVideoGenerationPriceOracle>()
            .Setup(o => o.QuoteAsync(It.IsAny<VideoGenerationPriceQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException(reason));

        // Strict: a mock gate that was never configured for ReserveAsync would throw if the step
        // reached it at all, so this both records "never called" and fails loudly if it is.
        var gate = new Mock<IVideoGenerationBudgetGate>(MockBehavior.Strict);

        var (executor, context) = BuildVendorExecutor(
            gate.Object, client.Object, factory, HiggsfieldCfg(), NewExecution(), CancellationToken.None);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        Meta(result)["failed"]!.GetValue<string>().Should().Be("CONFIG_INVALID");
        Meta(result)["error"]!.GetValue<string>().Should().Contain(reason, "the oracle's own reason is surfaced, unchanged");

        client.As<IVideoGenerationPriceOracle>().Verify(
            o => o.QuoteAsync(It.IsAny<VideoGenerationPriceQuery>(), It.IsAny<CancellationToken>()), Times.Once);

        // (c) the budget gate is never reached: nothing to reserve, because nothing has a price.
        gate.Verify(
            g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // (d) zero ledger rows.
        using (var db = factory.CreateDbContext())
        {
            db.ExternalGenerationJobs.Count().Should().Be(0, "an unpriced step must not be able to write money");
        }

        // (e) no submit.
        client.Verify(
            c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A vendor or network failure of the price lookup is not the person's doing: it stays retryable under a platform code,
    /// while "the model or setting is not supported" (a plain NotSupportedException) stops at once as CONFIG_INVALID.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_price_lookup_that_could_not_reach_the_vendor_stays_retryable_and_an_unsupported_model_does_not(bool vendorUnavailable)
    {
        var factory = new SharedInMemoryDbFactory();
        var client = new Mock<IVideoGenerationClient>();
        client.As<IVideoGenerationPriceOracle>()
            .Setup(o => o.QuoteAsync(It.IsAny<VideoGenerationPriceQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(vendorUnavailable
                ? new VideoGenerationPriceUnavailableException("Higgsfield: the estimate timed out.")
                : new NotSupportedException("Model 'x' is not supported."));
        var gate = new Mock<IVideoGenerationBudgetGate>(MockBehavior.Strict);

        var (executor, context) = BuildVendorExecutor(
            gate.Object, client.Object, factory, HiggsfieldCfg(), NewExecution(), CancellationToken.None);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        Meta(result)["failed"]!.GetValue<string>().Should().Be(vendorUnavailable ? "VIDEO_PRICE_UNAVAILABLE" : "CONFIG_INVALID");
        result.Retryable.Should().Be(vendorUnavailable);
        FailureClassifier.ForCode(Meta(result)["failed"]!.GetValue<string>())
            .Should().Be(vendorUnavailable ? FailureClass.PlatformProviderError : FailureClass.UserInput);
        Meta(result)["error"]!.GetValue<string>().Should().NotContain("Higgsfield", "the vendor's text is for the log, not the output");
    }

    /// <summary>
    /// The widened catch, pinned: backend's documented shape for a MALFORMED query is
    /// <c>ArgumentException</c> (blank model/resolution/ratio, non-positive duration). That is a
    /// config fault and must refuse the step as <c>CONFIG_INVALID</c> — never escape to the outer
    /// catch as <c>UNEXPECTED_ERROR</c>, which would read as an engine defect rather than the
    /// unofferable configuration it is.
    /// </summary>
    [Fact]
    public async Task A_malformed_query_from_the_oracle_also_refuses_the_step_as_CONFIG_INVALID()
    {
        const string reason = "The endpoint id (model) is blank, so there is nothing to price.";

        var factory = new SharedInMemoryDbFactory();
        var client = new Mock<IVideoGenerationClient>();
        client.As<IVideoGenerationPriceOracle>()
            .Setup(o => o.QuoteAsync(It.IsAny<VideoGenerationPriceQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException(reason));

        var gate = new Mock<IVideoGenerationBudgetGate>(MockBehavior.Strict);

        var (executor, context) = BuildVendorExecutor(
            gate.Object, client.Object, factory, HiggsfieldCfg(), NewExecution(), CancellationToken.None);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        Meta(result)["failed"]!.GetValue<string>().Should().Be("CONFIG_INVALID");
        Meta(result)["failed"]!.GetValue<string>().Should().NotBe("UNEXPECTED_ERROR");
        Meta(result)["error"]!.GetValue<string>().Should().Contain(reason);
        gate.Verify(
            g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        client.Verify(
            c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        using var db = factory.CreateDbContext();
        db.ExternalGenerationJobs.Count().Should().Be(0);
    }

    /// <summary>
    /// EXACT-FIGURE — the vendor's own total is the price; a re-multiplied per-second rate is not.
    /// <para>
    /// The numbers are chosen so the two genuinely differ under <see cref="decimal"/>: <c>$5.00</c>
    /// over <c>3s</c> derives <c>1.6666666666666666666666666667m</c> whose re-multiplication is
    /// <c>5.0000000000000000000000000001m</c>. (<c>$10.00</c> at 3s — the figure the ticket
    /// suggested — was tried first and is NOT a discriminator: .NET rounds
    /// <c>(10m / 3m) * 3m</c> back to a value equal to <c>10m</c>, so that pair would assert
    /// nothing.) The ledger must record the vendor's <c>5.00m</c>, and NOT the rate's figure.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_oracle_priced_take_is_charged_the_vendors_own_total_not_a_re_multiplied_rate()
    {
        const decimal vendorTotalUsd = 5.00m;
        const decimal reMultiplied = 5.0000000000000000000000000001m;

        var quote = VendorQuote(vendorTotalUsd);
        decimal derivedRate = vendorTotalUsd / VendorQuoteDurationSeconds;

        // The separation this test rests on, asserted rather than assumed.
        (derivedRate * VendorQuoteDurationSeconds).Should().Be(reMultiplied);
        (derivedRate * VendorQuoteDurationSeconds).Should().NotBe(vendorTotalUsd);

        var factory = new SharedInMemoryDbFactory();
        var client = HiggsfieldShapedClient(quote);
        Deliveries(client, new List<VideoGenerationRequest>(), "hf-1");
        var gate = new RecordingBudgetGate(RealGate(factory));

        var (executor, context) = BuildVendorExecutor(
            gate, client.Object, factory, HiggsfieldCfg(), NewExecution(), CancellationToken.None);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);

        // The reservation the gate received carried the vendor's own figure converted to credits,
        // TOGETHER WITH the quote it came from — that pairing is what lets the gate enforce the
        // invariant below it. The USD drift separation was asserted above; in credits the charge is
        // exactly CreditsFromUsd(vendorTotalUsd), the one conversion the platform permits.
        decimal vendorTotalCredits = CreditCurrency.CreditsFromUsd(vendorTotalUsd);

        gate.Requests.Should().ContainSingle();
        gate.LastRequest!.Takes.Should().ContainSingle();
        gate.LastRequest.Takes[0].CostEstimate.Should().Be(vendorTotalCredits);
        gate.LastRequest.Takes[0].Quote.Should().BeSameAs(quote);

        using var db = factory.CreateDbContext();
        var row = db.ExternalGenerationJobs.Single();
        row.CostEstimate.Should().Be(vendorTotalCredits);

        // No path reaches a price of zero, either: the recorded figure is strictly positive.
        row.CostEstimate.Should().BePositive();
    }

    /// <summary>
    /// CANCELLATION RESIDUAL — pinned rather than left silent.
    /// <para>
    /// When the step's own <see cref="CancellationToken"/> is already cancelled while the oracle is
    /// in flight, the resulting <c>OperationCanceledException</c> is NOT in the widened catch
    /// (only <c>NotSupportedException</c> and <c>ArgumentException</c> are), so it reaches the
    /// executor's outer catch and the step records <c>UNEXPECTED_ERROR</c>.
    /// </para>
    /// <para>
    /// This is a documented residual and it is acceptable: the EXECUTION-level cancellation
    /// (<c>ExecutionCancellationRegistry</c> / <c>MarkCancelledAsync</c>) is the authoritative
    /// record of why the run stopped, and it is unaffected by a step's failure code. Nothing is
    /// written and nothing is submitted either way. Production code is deliberately NOT changed to
    /// make this nicer — a new failure code for it would be a contract change nobody asked for.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_oracle_cancelled_mid_flight_surfaces_as_UNEXPECTED_ERROR()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = new Mock<IVideoGenerationClient>();
        client.As<IVideoGenerationPriceOracle>()
            .Setup(o => o.QuoteAsync(It.IsAny<VideoGenerationPriceQuery>(), It.IsAny<CancellationToken>()))
            .Returns((VideoGenerationPriceQuery _, CancellationToken ct) =>
                Task.FromCanceled<VideoGenerationPriceQuote>(ct));

        var gate = new Mock<IVideoGenerationBudgetGate>(MockBehavior.Strict);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var (executor, context) = BuildVendorExecutor(
            gate.Object, client.Object, factory, HiggsfieldCfg(), NewExecution(), cts.Token);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        Meta(result)["failed"]!.GetValue<string>().Should().Be(
            "UNEXPECTED_ERROR",
            "the residual this test exists to pin: a cancellation is not one of the two mapped config faults");

        gate.Verify(
            g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        client.Verify(
            c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        using var db = factory.CreateDbContext();
        db.ExternalGenerationJobs.Count().Should().Be(0, "a cancelled step cannot have bought anything");
    }

    /// <summary>
    /// NO NETWORK, NO KEY — on the non-oracle arm the step reaches the provider's compiled-in
    /// table and nothing else.
    /// <para>
    /// This is STRUCTURAL, not a promise: <c>Mock&lt;IVideoGenerationClient&gt;</c> is an in-memory
    /// proxy with no <see cref="HttpMessageHandler"/> behind it, so no HTTP call is even
    /// expressible on this path, and a <c>Describe</c> implemented as a table lookup has no
    /// endpoint to reach. Nothing here reads a vendor API key from the environment, and none is
    /// required to run the suite.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_non_oracle_client_prices_from_its_published_table_with_no_http_handler_at_all()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = MiniMaxDescribingClient();
        Deliveries(client, new List<VideoGenerationRequest>(), "r1");
        var gate = new RecordingBudgetGate(RealGate(factory));

        client.Object.Should().NotBeAssignableTo<IVideoGenerationPriceOracle>(
            "MiniMax publishes a table, so there is no oracle to reach and no network call to make");

        var execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Status = ExecutionStatus.Running };
        var (executor, stepContext) = BuildVendorExecutor(
            gate, client.Object, factory,
            """{"maxSpendCredits": 100000.0, "inlinePrompt": "Create a video", "timeoutSeconds": 60, "pollIntervalSeconds": 1}""",
            execution, CancellationToken.None,
            InferenceProviderKind.MiniMax, "MiniMax-H3");

        StepExecutionResult result = await executor.ExecuteAsync(stepContext);

        result.Status.Should().Be(StepStatus.Completed);
        client.Verify(c => c.Describe(It.IsAny<string>()), Times.Once, "the published table is the only pricing source reached");

        using var db = factory.CreateDbContext();
        db.ExternalGenerationJobs.Single().CostEstimate.Should().Be(TakeEstimate);
    }

    // ---- the async price oracle: fixtures ----

    /// <summary>
    /// The mode of the executor the tests above share, so it can run against MiniMax-shaped and
    /// Higgsfield-shaped providers. Same wiring as <see cref="BuildExecutor"/> apart from the
    /// provider kind, which is what makes the prompt-length cap and the client's identity right.
    /// </summary>
    private (VideoGenerateStepExecutor Executor, StepExecutionContext Context) BuildVendorExecutor(
        IVideoGenerationBudgetGate gate,
        IVideoGenerationClient client,
        IDbContextFactory<WorkflowEngineDbContext> factory,
        string configJson,
        WorkflowExecution execution,
        CancellationToken ct,
        InferenceProviderKind kind = InferenceProviderKind.Higgsfield,
        string modelName = HiggsfieldEndpointId,
        ReelBolt.Shared.Metering.IUsageRecorder? usageRecorder = null)
    {
        var provider = new ResolvedVideoGenerationProvider(
            Guid.NewGuid(), kind.ToString(), kind, "https://api.higgsfield.ai", modelName, "key123", 1800);

        var resolver = new Mock<IInferenceProviderResolver>();
        resolver
            .Setup(r => r.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);

        var clientFactory = new Mock<IVideoGenerationClientFactory>();
        clientFactory.Setup(f => f.Get(provider)).Returns(client);

        var executor = new VideoGenerateStepExecutor(
            gate,
            resolver.Object,
            clientFactory.Object,
            UploadingWorkspace().Object,
            factory,
            new GeneratedClipNormalizer(
                WorkingRunner().Object, Options.Create(_videoOptions), NullLogger<GeneratedClipNormalizer>.Instance),
            Options.Create(new VideoGenerationOptions()),
            NullLogger<VideoGenerateStepExecutor>.Instance,
            NoMediaWorkspaces(),
            NoKeyframeExtractor(),
            usageRecorder: usageRecorder);

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = configJson
        };

        var context = new StepExecutionContext
        {
            StepResultId = Guid.NewGuid(),
            Execution = execution,
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test-correlation",
            StepOutputHistory = [],
            CancellationToken = ct
        };

        return (executor, context);
    }

    /// <summary>An inline config for the Higgsfield-shaped provider: the endpoint id IS the model.</summary>
    private static string HiggsfieldCfg(int durationSeconds = VendorQuoteDurationSeconds, int takes = 1) =>
        $$"""{"maxSpendCredits": 100000.0, "takes": {{takes}}, "maxClips": {{takes}}, "inlinePrompt": "Create a video", "model": "{{HiggsfieldEndpointId}}", "resolution": "{{VendorQuoteResolution}}", "ratio": "{{VendorQuoteRatio}}", "durationSeconds": {{durationSeconds}}, "timeoutSeconds": 60, "pollIntervalSeconds": 1}""";

    /// <summary>The vendor's own quote for one clip at the configuration the step submits.</summary>
    private static VideoGenerationPriceQuote VendorQuote(
        decimal totalUsd = VendorQuoteTotalUsd, int durationSeconds = VendorQuoteDurationSeconds) =>
        new(
            HiggsfieldEndpointId,
            VendorQuoteResolution,
            VendorQuoteRatio,
            durationSeconds,
            pricePerSecondUsd: totalUsd / durationSeconds,
            totalUsd: totalUsd,
            credits: 12,
            source: VideoGenerationPriceSource.VendorEstimate);

    /// <summary>
    /// A Higgsfield-shaped client: <c>Describe</c> throws <c>NotSupportedException</c> — the real,
    /// unchanged, fail-closed behaviour of a provider that publishes no price table — and the async
    /// oracle answers with <paramref name="quote"/>. Nothing here can reach the network.
    /// </summary>
    private static Mock<IVideoGenerationClient> HiggsfieldShapedClient(VideoGenerationPriceQuote quote)
    {
        var client = new Mock<IVideoGenerationClient>();
        client
            .Setup(c => c.Describe(It.IsAny<string>()))
            .Throws(new NotSupportedException("Higgsfield publishes no per-second price table."));
        client.As<IVideoGenerationPriceOracle>()
            .Setup(o => o.QuoteAsync(It.IsAny<VideoGenerationPriceQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(quote);
        return client;
    }

    /// <summary>
    /// Submit / poll / download on an already-built client mock, so a client shaped for the oracle
    /// arm can still deliver a clip. Hands out <paramref name="remoteIds"/> in call order.
    /// </summary>
    private static void Deliveries(
        Mock<IVideoGenerationClient> client, List<VideoGenerationRequest> submitted, params string[] remoteIds)
    {
        var i = 0;
        client
            .Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<VideoGenerationRequest, CancellationToken>((r, _) => submitted.Add(r))
            .ReturnsAsync(() => new ExternalJobHandle(remoteIds[i++]));
        client
            .Setup(c => c.PollAsync(It.IsAny<ExternalJobHandle>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExternalJobHandle h, CancellationToken _) =>
                new ExternalJobStatus(ExternalJobState.Succeeded, $"https://example.invalid/{h.RemoteJobId}.mp4", null));
        client
            .Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3, 4]));
    }
}

/// <summary>
/// A spy over the REAL <see cref="VideoGenerationBudgetGate"/>: it records every reservation request
/// the executor hands it and then delegates, so a test can assert BOTH what the step asked to be
/// charged (the take's <c>CostEstimate</c> and the <c>Quote</c> beside it) AND that the ledger
/// rows really exist. Delegating rather than re-implementing is the point: "zero rows" then says
/// something about the component that writes money, not about an unimplemented mock.
/// </summary>
internal sealed class RecordingBudgetGate : IVideoGenerationBudgetGate
{
    private readonly IVideoGenerationBudgetGate _inner;

    public RecordingBudgetGate(IVideoGenerationBudgetGate inner) => _inner = inner;

    public List<VideoGenerationReservationRequest> Requests { get; } = new();

    public VideoGenerationReservationRequest? LastRequest => Requests.Count == 0 ? null : Requests[^1];

    public Task<VideoGenerationReservation> ReserveAsync(
        VideoGenerationReservationRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return _inner.ReserveAsync(request, ct);
    }
}
