using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Review;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Review;

/// <summary>
/// The ReviewLoop executor's visual-check hook: off is byte-identical; on, the check runs before the reviewer,
/// its block is appended to the reviewer's input, <c>visualCheck</c> is recorded in the iteration output and a
/// high finding caps the score so the loop goes back to the step it targets.
/// </summary>
public class ReviewLoopVisualCheckExecutorTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly string RenderKey = $"projects/{ProjectId:D}/outputFiles/render.mp4";
    private const string ReviewerOutput = """{"score":9,"passesReview":true,"summary":"tight cut"}""";

    private static readonly VisualCheckOutcome HighFinding = VisualCheckOutcome.Checked(
        3,
        [new VisualCheckFrameTime(1, 0), new VisualCheckFrameTime(2, 6.2), new VisualCheckFrameTime(3, 12.4)],
        [new VisualCheckFinding(3, 12.4, "text_overflow", "the card label runs past its box", "high")],
        6);

    [Theory]
    [InlineData(null)]
    [InlineData("""{"enabled":false}""")]
    [InlineData("""{"visualCheck":{"enabled":false,"frames":4}}""")]
    [InlineData("""{"visualCheck":null}""")]
    public async Task Off_is_byte_identical(string? configJson)
    {
        Mock<IReviewVisualCheck> check = new(MockBehavior.Strict);
        (ReviewLoopStepExecutor executor, Mock<IReelBoltAgent> agent) = Create(check.Object);
        StepExecutionContext context = Context(configJson);
        string expectedInput = Context(configJson).BuildAgentInput();

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Output.Should().Be(ReviewerOutput);
        result.NextStepIndex.Should().Be(context.CurrentStepIndex + 1);
        agent.Verify(a => a.RunAsync(expectedInput, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
        check.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_high_finding_reaches_the_reviewer_is_recorded_and_sends_the_loop_back()
    {
        Mock<IReviewVisualCheck> check = new();
        check.Setup(c => c.RunAsync(It.IsAny<StepExecutionContext>(), It.Is<VisualCheckConfig>(v => v.Enabled && v.Frames == 4), It.IsAny<CancellationToken>()))
            .ReturnsAsync(HighFinding);
        (ReviewLoopStepExecutor executor, Mock<IReelBoltAgent> agent) = Create(check.Object);
        string? sentInput = null;
        agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Guid?, CancellationToken>((input, _, _) => sentInput = input)
            .ReturnsAsync(new AgentRunResult { Output = ReviewerOutput, Success = true, TokensUsed = 10 });

        StepExecutionContext context = Context("""{"visualCheck":{"enabled":true,"frames":4}}""", minScore: 8);
        StepExecutionResult result = await executor.ExecuteAsync(context);

        sentInput.Should().StartWith(Context(null, minScore: 8).BuildAgentInput());
        sentInput.Should().Contain("[VISUAL CHECK]").And.Contain("Frame 3 at 12.4 s: text overflows its box");

        JsonObject visual = JsonNode.Parse(result.Output)!["visualCheck"]!.AsObject();
        visual["capped"]!.GetValue<bool>().Should().BeTrue();
        visual["findings"]!.AsArray().Should().HaveCount(1);
        visual["frames"]!.AsArray().Should().HaveCount(3);

        result.NextStepIndex.Should().Be(0, "a reviewer 9 capped to 6 is below the pass mark of 8, so the loop goes back");
        ReviewLoopStepExecutor.ScoreReview(result.Output, context.AccumulatedOutput, AgentType.VideoReviewAgent, visualCheckEnabled: true).Score.Should().Be(6);
    }

    [Fact]
    public async Task No_vision_provider_records_unavailable_and_does_not_cap()
    {
        Mock<IReviewVisualCheck> check = new();
        check.Setup(c => c.RunAsync(It.IsAny<StepExecutionContext>(), It.IsAny<VisualCheckConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(VisualCheckOutcome.Unavailable("no vision provider is configured"));
        (ReviewLoopStepExecutor executor, Mock<IReelBoltAgent> agent) = Create(check.Object);

        StepExecutionContext context = Context("""{"visualCheck":{"enabled":true}}""", minScore: 8);
        StepExecutionResult result = await executor.ExecuteAsync(context);

        JsonObject visual = JsonNode.Parse(result.Output)!["visualCheck"]!.AsObject();
        visual["status"]!.GetValue<string>().Should().Be("unavailable");
        result.NextStepIndex.Should().Be(context.CurrentStepIndex + 1);
        agent.Verify(a => a.RunAsync(It.Is<string>(s => !s.Contains("[VISUAL CHECK]")), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_host_without_the_check_wired_records_unavailable()
    {
        (ReviewLoopStepExecutor executor, _) = Create(visualCheck: null);

        StepExecutionResult result = await executor.ExecuteAsync(Context("""{"visualCheck":{"enabled":true}}""", minScore: 8));

        JsonNode.Parse(result.Output)!["visualCheck"]!["status"]!.GetValue<string>().Should().Be("unavailable");
    }

    [Fact]
    public async Task A_capping_finding_means_the_early_stop_gate_is_not_asked()
    {
        Mock<IReviewVisualCheck> check = new();
        check.Setup(c => c.RunAsync(It.IsAny<StepExecutionContext>(), It.IsAny<VisualCheckConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(HighFinding);
        Mock<IDecisionGate> gate = new(MockBehavior.Strict);
        (ReviewLoopStepExecutor executor, _) = Create(check.Object, gate.Object);

        StepExecutionResult result = await executor.ExecuteAsync(Context("""{"enabled":true,"visualCheck":{"enabled":true}}""", minScore: 8));

        result.NextStepIndex.Should().Be(0);
        gate.VerifyNoOtherCalls();
    }

    [Fact]
    public void The_check_is_composed_from_the_engine_services_the_host_already_registers()
    {
        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(Mock.Of<IInferenceProviderResolver>());
        services.AddSingleton(Mock.Of<IChatClientFactory>());
        services.AddSingleton(Mock.Of<IMediaWorkspaceFactory>());
        services.AddSingleton(Mock.Of<IMediaProbe>());
        services.AddSingleton(Mock.Of<IKeyframeExtractor>());

        ReviewVisualCheck.TryCreate(services.BuildServiceProvider()).Should().NotBeNull();

        services.RemoveAll<IKeyframeExtractor>();
        ReviewVisualCheck.TryCreate(services.BuildServiceProvider()).Should().BeNull();
    }

    private static (ReviewLoopStepExecutor Executor, Mock<IReelBoltAgent> Agent) Create(IReviewVisualCheck? visualCheck, IDecisionGate? gate = null)
    {
        Mock<IReelBoltAgent> agent = new();
        agent.Setup(a => a.Name).Returns(AgentType.VideoReviewAgent.ToString());
        agent.Setup(a => a.AgentType).Returns(AgentType.VideoReviewAgent);
        agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = ReviewerOutput, Success = true, TokensUsed = 10 });

        Mock<IAgentRegistry> registry = new();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent.Object);

        Mock<IWorkflowExecutionContextAccessor> accessor = new();
        accessor.Setup(a => a.BeginScope(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>())).Returns(Mock.Of<IDisposable>());

        return (new ReviewLoopStepExecutor(
            registry.Object, accessor.Object, gate ?? Mock.Of<IDecisionGate>(), NullLogger<ReviewLoopStepExecutor>.Instance, visualCheck), agent);
    }

    private static StepExecutionContext Context(string? configJson, int? minScore = 9)
    {
        WorkflowStep author = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.Agent,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = AgentType.AuthorAgent, Name = "Author" },
        };
        WorkflowStep review = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 3,
            StepType = StepType.ReviewLoop,
            MinScore = minScore,
            MaxIterations = 3,
            LoopTargetStepOrder = 2,
            ReviewLoopConfigJson = configJson,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = AgentType.VideoReviewAgent, Name = "VideoReviewAgent" },
        };

        const string renderOutput = """{"composition":"Main","durationInFrames":900}""";
        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), ProjectId = ProjectId },
            Step = review,
            AllSteps = [author, review],
            AccumulatedOutput = renderOutput,
            StepOutputHistory = new List<StepOutputHistoryEntry> { new(2, "Author", renderOutput, RenderKey) },
            CurrentStepIndex = 1,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None,
        };
    }
}
