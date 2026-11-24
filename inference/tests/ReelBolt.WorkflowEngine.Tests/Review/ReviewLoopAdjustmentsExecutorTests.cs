using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Review;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Review;

/// <summary>
/// The ReviewLoop executor's adjustments and stronger evidence (docs/design/review-loop-adjustments.md): off is
/// byte-identical even when the reviewer answers with proposals; on, the settings block reaches the reviewer, the
/// record replaces its proposals, and the loop goes back to the earliest changed step.
/// </summary>
public class ReviewLoopAdjustmentsExecutorTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid SnapshotId = Guid.Parse("22222222-0000-0000-0000-000000000005");
    private static readonly string FilmKey = $"projects/{ProjectId:D}/outputFiles/film.mp4";
    private const string CompileOutput = """{"status":"completed","segments":4,"outputDurationSec":21.5}""";

    private const string ReviewerWithAdjustments =
        """{"score":6,"passesReview":false,"issues":["title too small"],"summary":"small title","adjustments":[{"stepOrder":4,"setting":"overlayFontSizePct","value":"8","reason":"title cap height 2%"},{"stepOrder":2,"setting":"maxOutputChars","value":"60000","reason":"no placement offered"},{"stepOrder":4,"setting":"allowSourceMediaEgress","value":"true","reason":"x"}]}""";

    [Theory]
    [InlineData(null)]
    [InlineData("""{"allowAdjustments":false}""")]
    [InlineData("""{"visualCheck":{"enabled":false}}""")]
    public async Task Off_is_byte_identical_even_when_the_reviewer_proposes_changes(string? config)
    {
        (ReviewLoopStepExecutor executor, Mock<IReelBoltAgent> agent) = Create(ReviewerWithAdjustments);
        StepExecutionContext context = Context(config);
        string expectedInput = Context(config).BuildAgentInput();

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Output.Should().Be(ReviewerWithAdjustments);
        result.ReviewAdjustments.Should().BeNull();
        result.NextStepIndex.Should().Be(2, "the loop goes back to its own target, the edit room");
        agent.Verify(a => a.RunAsync(expectedInput, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task On_the_reviewer_sees_the_settings_and_the_loop_reruns_from_the_earliest_change()
    {
        (ReviewLoopStepExecutor executor, Mock<IReelBoltAgent> agent) = Create(ReviewerWithAdjustments);
        string? sent = null;
        agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Guid?, CancellationToken>((input, _, _) => sent = input)
            .ReturnsAsync(new AgentRunResult { Output = ReviewerWithAdjustments, TokensUsed = 10 });
        StepExecutionContext context = Context("""{"allowAdjustments":true}""");

        StepExecutionResult result = await executor.ExecuteAsync(context);

        sent.Should().Contain("[ADJUSTABLE SETTINGS]").And.Contain("overlayFontSizePct = 5 (whole number, 3..12)").And.Contain("Step 2 (VideoAnalyze)");
        result.NextStepIndex.Should().Be(1, "the analysis (step 2, index 1) changed, so the loop re-runs from it");
        result.ReviewAdjustments.Should().NotBeNull();
        result.ReviewAdjustments!.Changes.Select(c => c.StepOrder).Should().Equal(2, 4);

        JsonObject output = JsonNode.Parse(ReviewOutputNodes.WithoutRecord(result.Output))!.AsObject();
        JsonArray records = output["adjustments"]!.AsArray();
        records.Should().HaveCount(3);
        records[0]!["status"]!.GetValue<string>().Should().Be("applied");
        records[0]!["from"]!.GetValue<string>().Should().Be("5");
        records[0]!["to"]!.GetValue<string>().Should().Be("8");
        records[2]!["status"]!.GetValue<string>().Should().Be("refused");
        output["adjustmentRerun"]!["fromStepOrder"]!.GetValue<int>().Should().Be(2);
        output["warnings"]!.AsArray().Select(w => w!["code"]!.GetValue<string>()).Should().Equal("review_adjusted_setting", "review_adjusted_setting");
    }

    [Fact]
    public async Task A_passing_review_applies_nothing()
    {
        const string passing = """{"score":9,"passesReview":true,"summary":"good","adjustments":[{"stepOrder":4,"setting":"overlayFontSizePct","value":"8","reason":"r"}]}""";
        (ReviewLoopStepExecutor executor, _) = Create(passing);

        StepExecutionResult result = await executor.ExecuteAsync(Context("""{"allowAdjustments":true}"""));

        result.ReviewAdjustments.Should().BeNull();
        result.NextStepIndex.Should().Be(5, "a pass moves on past the review");
        JsonNode.Parse(result.Output)!["adjustments"]![0]!["status"]!.GetValue<string>().Should().Be("refused");
    }

    [Fact]
    public async Task A_run_without_its_own_snapshot_changes_nothing()
    {
        (ReviewLoopStepExecutor executor, _) = Create(ReviewerWithAdjustments);
        StepExecutionContext context = Context("""{"allowAdjustments":true}""", ownSnapshot: false);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.ReviewAdjustments.Should().BeNull();
        result.NextStepIndex.Should().Be(2);
    }

    [Fact]
    public async Task Timeline_and_audio_facts_reach_the_reviewer_when_asked_for()
    {
        Mock<IReviewFilmFacts> facts = new();
        facts.Setup(f => f.MeasureAudioAsync(It.IsAny<StepExecutionContext>(), It.IsAny<VisualCheckSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReviewAudioMeasurement(-14.2, 6.1, -0.05, [(0.1, -20), (0.2, -19)]));
        (ReviewLoopStepExecutor executor, Mock<IReelBoltAgent> agent) = Create(ReviewerWithAdjustments, facts: facts.Object);
        string? sent = null;
        agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Guid?, CancellationToken>((input, _, _) => sent = input)
            .ReturnsAsync(new AgentRunResult { Output = """{"score":9,"passesReview":true,"summary":"ok"}""", TokensUsed = 1 });

        await executor.ExecuteAsync(Context("""{"visualCheck":{"timelineFacts":true,"audioFacts":true}}"""));

        sent.Should().Contain("[TIMELINE FACTS]").And.Contain("title/overlay text 5% of frame height");
        sent.Should().Contain("[AUDIO FACTS]").And.Contain("-14.2 LUFS").And.Contain("Probable clipping");
    }

    [Fact]
    public async Task A_targeted_look_is_answered_fed_back_and_its_high_finding_caps_the_score()
    {
        const string asking = """{"score":9,"passesReview":true,"summary":"looks fine","looks":[{"atSec":[4.8,5.1],"region":{"x":0,"y":0.6,"w":1,"h":0.4},"why":"title size"}]}""";
        const string final = """{"score":9,"passesReview":true,"summary":"still fine","looks":[]}""";
        Mock<IReviewVisualCheck> check = new();
        check.Setup(c => c.LookAsync(It.IsAny<StepExecutionContext>(), It.IsAny<VisualCheckConfig>(), It.Is<IReadOnlyList<ReviewLookFrame>>(f => f.Count == 2 && f[0].Region != null),
                "title size", It.IsAny<IReviewFilmFacts?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(VisualCheckOutcome.Checked(4, [new VisualCheckFrameTime(1, 4.8), new VisualCheckFrameTime(2, 5.1)],
                [new VisualCheckFinding(1, 4.8, "text_too_small", "title cap height is 2%", "high")], 6));
        (ReviewLoopStepExecutor executor, Mock<IReelBoltAgent> agent) = Create(asking, visualCheck: check.Object);
        List<string> inputs = [];
        agent.SetupSequence(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = asking, TokensUsed = 10 })
            .ReturnsAsync(new AgentRunResult { Output = final, TokensUsed = 7 });
        agent.Setup(a => a.Name).Returns("VideoReviewAgent");
        agent.Invocations.Clear();

        StepExecutionResult result = await executor.ExecuteAsync(Context("""{"visualCheck":{"targetedLooks":true,"maxExtraRounds":2}}"""));

        agent.Verify(a => a.RunAsync(It.Is<string>(s => s.Contains("[TARGETED LOOKS AVAILABLE]")), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        agent.Verify(a => a.RunAsync(It.Is<string>(s => s.Contains("[TARGETED LOOK 1]") && s.Contains("title cap height is 2%")), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
        result.TokensUsed.Should().Be(17);
        JsonObject output = JsonNode.Parse(result.Output)!.AsObject();
        output["targetedLooks"]!.AsArray().Should().ContainSingle();
        output["visualCheck"]!["capped"]!.GetValue<bool>().Should().BeTrue();
        result.NextStepIndex.Should().Be(2, "the high finding caps the 9 at 6, below the pass mark of 8");
    }

    [Fact]
    public async Task A_look_the_hold_cannot_cover_is_skipped_with_a_reason()
    {
        const string asking = """{"score":9,"passesReview":true,"summary":"x","looks":[{"atSec":[1],"why":"w"}]}""";
        Mock<IReviewVisualCheck> check = new(MockBehavior.Strict);
        (ReviewLoopStepExecutor executor, _) = Create(asking, visualCheck: check.Object);
        StepExecutionContext context = Context("""{"visualCheck":{"targetedLooks":true}}""");
        context.CreditHoldMillis = 1;
        using IDisposable scope = ReelBolt.Shared.Metering.UsageScope.Begin(null, null, null, null, null, UsageSource.Workflow);
        ReelBolt.Shared.Metering.UsageScope.Current!.GetType()
            .GetMethod("AddCredits", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(ReelBolt.Shared.Metering.UsageScope.Current, [5L]);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        JsonNode.Parse(result.Output)!["targetedLooks"]![0]!["status"]!.GetValue<string>().Should().Be("skipped");
        check.VerifyNoOtherCalls();
    }

    private static (ReviewLoopStepExecutor Executor, Mock<IReelBoltAgent> Agent) Create(
        string reviewerOutput, IReviewVisualCheck? visualCheck = null, IReviewFilmFacts? facts = null)
    {
        Mock<IReelBoltAgent> agent = new();
        agent.Setup(a => a.Name).Returns(AgentType.VideoReviewAgent.ToString());
        agent.Setup(a => a.AgentType).Returns(AgentType.VideoReviewAgent);
        agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = reviewerOutput, Success = true, TokensUsed = 10 });

        Mock<IAgentRegistry> registry = new();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent.Object);

        Mock<IWorkflowExecutionContextAccessor> accessor = new();
        accessor.Setup(a => a.BeginScope(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>())).Returns(Mock.Of<IDisposable>());

        return (new ReviewLoopStepExecutor(
            registry.Object, accessor.Object, Mock.Of<IDecisionGate>(), NullLogger<ReviewLoopStepExecutor>.Instance,
            visualCheck, lengthProbe: null, filmFacts: facts), agent);
    }

    private static StepExecutionContext Context(string? reviewConfig, bool ownSnapshot = true)
    {
        Guid definitionId = ownSnapshot ? SnapshotId : Guid.NewGuid();
        WorkflowStep Make(int order, StepType type, AgentType agentType) => new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = definitionId,
            StepOrder = order,
            StepType = type,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = agentType, Name = type.ToString() },
        };

        WorkflowStep audio = Make(1, StepType.AudioGenerate, AgentType.VideoTransform);
        WorkflowStep analyze = Make(2, StepType.VideoAnalyze, AgentType.VideoTransform);
        analyze.VideoAnalyzeConfigJson = """{"transcription":"Off","vision":"Off"}""";
        WorkflowStep room = Make(3, StepType.EditRoom, AgentType.VideoEditDirector);
        WorkflowStep compile = Make(4, StepType.VideoCompile, AgentType.VideoTransform);
        compile.VideoCompileConfigJson = """{"version":1,"enableGraphics":true,"overlayFontSizePct":5}""";
        WorkflowStep review = Make(5, StepType.ReviewLoop, AgentType.VideoReviewAgent);
        review.MinScore = 8;
        review.MaxIterations = 3;
        review.LoopTargetStepOrder = 3;
        review.ReviewLoopConfigJson = reviewConfig;

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution
            {
                Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
                ProjectId = ProjectId,
                SnapshotDefinitionId = SnapshotId,
            },
            Step = review,
            AllSteps = [audio, analyze, room, compile, review],
            AccumulatedOutput = CompileOutput,
            StepOutputHistory = new List<StepOutputHistoryEntry> { new(4, "Compile", CompileOutput, FilmKey) },
            CurrentStepIndex = 4,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None,
        };
    }
}
