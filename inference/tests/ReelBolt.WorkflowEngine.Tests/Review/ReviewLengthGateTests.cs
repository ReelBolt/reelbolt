using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Briefs;
using ReelBolt.WorkflowEngine.Services.Review;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Review;

/// <summary>The length gate: the requested length is measured on the real final film and enforced as a score cap.</summary>
public class ReviewLengthGateTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static string Key(string n) => $"projects/{ProjectId:D}/outputFiles/{n}";
    private const string ReviewerOutput = """{"score":9,"passesReview":true,"summary":"tight cut"}""";

    private sealed class FakeProbe(double? duration) : IReviewFilmDurationProbe
    {
        public VisualCheckSource? Seen { get; private set; }

        public Task<double?> ProbeDurationSecAsync(StepExecutionContext context, VisualCheckSource film, CancellationToken ct)
        {
            Seen = film;
            return Task.FromResult(duration);
        }
    }

    private static async Task<(StepExecutionResult Result, FakeProbe Probe)> Run(
        double? actual, int? requested, string? configJson = null, IDecisionGate? gate = null, bool compileAfterTarget = false)
    {
        FakeProbe probe = new(actual);
        Mock<IReelBoltAgent> agent = new();
        agent.Setup(a => a.Name).Returns("VideoReviewAgent");
        agent.Setup(a => a.AgentType).Returns(AgentType.VideoReviewAgent);
        agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = ReviewerOutput, Success = true, TokensUsed = 10 });
        Mock<IAgentRegistry> registry = new();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent.Object);
        Mock<IWorkflowExecutionContextAccessor> accessor = new();
        accessor.Setup(a => a.BeginScope(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>())).Returns(Mock.Of<IDisposable>());
        ReviewLoopStepExecutor executor = new(
            registry.Object, accessor.Object, gate ?? Mock.Of<IDecisionGate>(), NullLogger<ReviewLoopStepExecutor>.Instance,
            (IReviewVisualCheck?)null, probe);

        WorkflowStep author = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.Agent,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = AgentType.AuthorAgent, Name = "Author" }
        };
        WorkflowStep review = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 4,
            StepType = StepType.ReviewLoop,
            MinScore = 8,
            MaxIterations = 3,
            LoopTargetStepOrder = 2,
            ReviewLoopConfigJson = configJson,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = AgentType.VideoReviewAgent, Name = "VideoReviewAgent" },
        };
        List<StepOutputHistoryEntry> history = [new(2, "Author", "{}", Key("render.mp4"))];
        if (compileAfterTarget)
            history.Add(new(3, "VideoCompile", "{}", Key("compile.mp4")));
        StepExecutionContext context = new()
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = review,
            AllSteps = [author, review],
            AccumulatedOutput = "{}",
            StepOutputHistory = history,
            CurrentStepIndex = 1,
            IterationCount = 0,
            CorrelationId = "t",
            CancellationToken = CancellationToken.None,
        };

        using IDisposable _ = RunBriefAmbient.Enter(requested is null ? null : new RunBriefState(new StoredRunBrief(LengthSec: requested)));
        return (await executor.ExecuteAsync(context), probe);
    }

    [Fact]
    public async Task Within_tolerance_does_not_cap()
    {
        (StepExecutionResult r, _) = await Run(actual: 16.5, requested: 15);
        JsonObject node = JsonNode.Parse(r.Result())!["lengthCheck"]!.AsObject();
        node["withinTolerance"]!.GetValue<bool>().Should().BeTrue();
        node["status"]!.GetValue<string>().Should().Be("checked");
        r.NextStepIndex.Should().Be(2);
    }

    [Fact]
    public async Task Exactly_fifteen_percent_off_is_still_within_tolerance()
    {
        (StepExecutionResult r, _) = await Run(actual: 17.25, requested: 15);
        JsonNode.Parse(r.Result())!["lengthCheck"]!["withinTolerance"]!.GetValue<bool>().Should().BeTrue();
        r.NextStepIndex.Should().Be(2);
    }

    [Fact]
    public async Task Sixteen_percent_off_caps_to_six_and_loops_back()
    {
        (StepExecutionResult r, _) = await Run(actual: 17.4, requested: 15);
        JsonObject node = JsonNode.Parse(r.Result())!["lengthCheck"]!.AsObject();
        node["withinTolerance"]!.GetValue<bool>().Should().BeFalse();
        node["requestedSec"]!.GetValue<int>().Should().Be(15);
        r.NextStepIndex.Should().Be(0);
        ReviewLoopStepExecutor.ScoreReview(r.Output, "{}", AgentType.VideoReviewAgent).Score.Should().Be(6);
        ReviewLoopStepExecutor.ExtractFeedbackSummary(r.Output, 8, null, AgentType.VideoReviewAgent)
            .Should().Contain("The film is 17.4 s; the request was 15 s. Make it 15 s (within 15 %).");
    }

    [Fact]
    public async Task An_unavailable_probe_does_not_cap()
    {
        (StepExecutionResult r, _) = await Run(actual: null, requested: 15);
        JsonNode.Parse(r.Result())!["lengthCheck"]!["status"]!.GetValue<string>().Should().Be("unavailable");
        r.NextStepIndex.Should().Be(2);
    }

    [Fact]
    public async Task No_brief_length_leaves_the_review_byte_identical()
    {
        (StepExecutionResult r, FakeProbe probe) = await Run(actual: 99, requested: null);
        r.Output.Should().Be(ReviewerOutput);
        probe.Seen.Should().BeNull();
    }

    [Fact]
    public async Task A_length_miss_means_the_early_stop_gate_is_not_asked()
    {
        Mock<IDecisionGate> gate = new(MockBehavior.Strict);
        (StepExecutionResult r, _) = await Run(actual: 30, requested: 15, """{"enabled":true}""", gate.Object);
        r.NextStepIndex.Should().Be(0);
        gate.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task The_compile_after_the_loop_target_is_the_film_that_is_measured()
    {
        (_, FakeProbe probe) = await Run(actual: 15, requested: 15, compileAfterTarget: true);
        probe.Seen!.StepOrder.Should().Be(3);
        probe.Seen.StorageKey.Should().Be(Key("compile.mp4"));
    }
}

internal static class ResultExt
{
    public static string Result(this StepExecutionResult r) => r.Output;
}
