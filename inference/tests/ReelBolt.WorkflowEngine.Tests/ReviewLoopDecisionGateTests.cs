using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Decision;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The calibrated early stop on <see cref="StepType.ReviewLoop"/> (decision-engine phase 2, D-008):
/// a <c>Score</c> question over the deterministic review facts, asked before the reviewer runs,
/// which either skips the reviewer (advance, no loop-back) or escalates to today's exact path.
///
/// <para>
/// <see cref="IDecisionGate"/> is mocked throughout — there is no Decision provider row in this
/// environment and no live calibration data exists or can be produced here, so nothing in this file
/// uses a real gate, a real provider or a real threshold. The pure rule is also exercised directly
/// through <c>Evaluate</c>, which is how the load-bearing ordering is pinned independently of the
/// executor's plumbing.
/// </para>
/// </summary>
public class ReviewLoopDecisionGateTests
{
    /// <summary>
    /// A realistic <c>VideoCompile</c> step output — the step a video-editing ReviewLoop follows —
    /// carrying the deterministic facts <c>VideoReviewAgent</c>'s prompt consumes, plus the raw
    /// fields the projection must NOT forward.
    /// </summary>
    private const string PriorCompileOutput =
        """
        {"status":"completed","outputStorageKey":"projects/p/agentFiles/outputFiles/render.mp4",
         "artifactStorageKey":"projects/p/agentFiles/video-analysis/edl.json","segments":4,
         "outputDurationSec":12.5,"retainedRatio":0.61,"droppedSegmentsOverCap":0,
         "sentenceCheck":{"applicable":true,"lastKeptId":"t7","lastSegmentText":"and that is how you ship it",
           "endsAtSentenceBoundary":true,"nextSegmentContinues":false,"src":0,"punctuationRatio":0.82,
           "punctuationReliable":true,"punctuationSampleSize":34},
         "graphics":{"applied":true,"appliedOverlayCount":1,"droppedOverlays":[],
           "appliedOverlays":[{"placementId":"p1","coveragePct":12.5}]},
         "music":{"applied":true,"trackId":"m1","duckBasis":"speech","dialogueHeadroom":{"applicable":true,"headroomDb":4.5,"keptWindowCount":3}}}
        """;

    // ---- harness -------------------------------------------------------

    /// <summary>
    /// The compile node for narration that was DECLARED and NOT APPLIED — shape S1, the
    /// all-lines-lost arm <c>VideoCompileStepExecutor.Finish()</c> emits when
    /// <c>declaredLineCount &gt; 0 &amp;&amp; resolved.Count == 0 &amp;&amp; !amixUnavailable</c>
    /// (<c>reason: "all_lines_unavailable"</c>, <c>lineCount</c> the DECLARED count). Carries the
    /// deterministic facts too, so it exercises the early stop's real state projection rather than
    /// escalating for lack of facts. The <c>fit</c> node is the <c>applicable:false</c> arm, which
    /// emits <c>applicable</c> and <c>reason</c> only.
    /// </summary>
    private const string DeclaredAndLostOutput =
        """
        {"status":"completed","segments":4,"outputDurationSec":12.5,
         "sentenceCheck":{"applicable":true,"lastKeptId":"t7","endsAtSentenceBoundary":true},
         "voiceover":{"enabled":true,"applied":false,"unavailable":false,
           "appliedLineCount":0,"lineCount":3,"lines":[],"dropped":[],
           "reason":"all_lines_unavailable",
           "fit":{"applicable":false,"reason":"no_resolved_lines"}}}
        """;

    /// <summary>
    /// The same node for a narration the writer deliberately left empty — shape S3, the
    /// <c>voiceoverLinesList.Count == 0</c> arm with nothing declared, which sets
    /// <c>reason: "no_valid_voiceover_lines"</c> and keeps <c>lineCount</c> at 0. It differs from
    /// <see cref="DeclaredAndLostOutput"/> in exactly that one key.
    /// </summary>
    private const string DeliberatelyEmptyPlanOutput =
        """
        {"status":"completed","segments":4,"outputDurationSec":12.5,
         "sentenceCheck":{"applicable":true,"lastKeptId":"t7","endsAtSentenceBoundary":true},
         "voiceover":{"enabled":true,"applied":false,"unavailable":false,
           "appliedLineCount":0,"lineCount":0,"lines":[],"dropped":[],
           "reason":"no_valid_voiceover_lines",
           "fit":{"applicable":false,"reason":"no_resolved_lines"}}}
        """;

    private static Mock<IWorkflowExecutionContextAccessor> MockAccessor()
    {
        var accessor = new Mock<IWorkflowExecutionContextAccessor>();
        accessor.Setup(a => a.BeginScope(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>()))
            .Returns(Mock.Of<IDisposable>());
        accessor.Setup(a => a.Current).Returns((WorkflowExecutionContext?)null);
        return accessor;
    }

    private static Mock<IReelBoltAgent> MockAgent(string output = """{"score":3,"passesReview":false,"summary":"cut runs long"}""")
    {
        var agent = new Mock<IReelBoltAgent>();
        agent.Setup(a => a.Name).Returns(AgentType.VideoReviewAgent.ToString());
        agent.Setup(a => a.AgentType).Returns(AgentType.VideoReviewAgent);
        agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = output, Success = true, TokensUsed = 120, InputTokens = 90, OutputTokens = 30 });
        return agent;
    }

    private static ReviewLoopStepExecutor CreateExecutor(Mock<IReelBoltAgent> agent, IDecisionGate gate)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent.Object);

        return new ReviewLoopStepExecutor(
            registry.Object,
            MockAccessor().Object,
            gate,
            NullLogger<ReviewLoopStepExecutor>.Instance);
    }

    private static StepExecutionContext CreateContext(
        string? reviewLoopConfigJson,
        int? minScore = 9,
        string accumulatedOutput = PriorCompileOutput,
        int iterationCount = 0)
    {
        var loopTarget = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.VideoCompile,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = AgentType.VideoTransform, Name = "VideoTransform" }
        };

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = loopTarget.WorkflowDefinitionId,
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 4,
            StepType = StepType.ReviewLoop,
            MinScore = minScore,
            MaxIterations = 3,
            LoopTargetStepOrder = 2,
            ReviewLoopConfigJson = reviewLoopConfigJson,
            AgentDefinition = new AgentDefinition
            {
                Id = Guid.NewGuid(),
                AgentType = AgentType.VideoReviewAgent,
                Name = "VideoReviewAgent"
            }
        };

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
            Step = step,
            AllSteps = [loopTarget, step],
            AccumulatedOutput = accumulatedOutput,
            StepOutputHistory = [new StepOutputHistoryEntry(2, "VideoCompile", accumulatedOutput)],
            CurrentStepIndex = 1,
            IterationCount = iterationCount,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    private const string EnabledConfig = """{"enabled":true}""";

    /// <summary>
    /// A gate outcome whose single <c>Score</c> answer carries <paramref name="levels"/> as its
    /// probability map, with the top-two pair taken from the same shared helper the gate itself
    /// uses (D-002/D-004's <see cref="DecisionProbability.TopTwo"/>), so this test never invents a
    /// margin the production path would compute differently.
    /// </summary>
    private static DecisionGateOutcome ScoreOutcome(params (string Level, double Probability)[] levels)
    {
        var probabilities = levels.ToDictionary(l => l.Level, l => l.Probability);
        var answer = new DecisionAnswer(
            Name: ReviewLoopStepExecutor.GateQuestionName,
            Kind: DecisionKind.Score,
            Choice: null,
            Score: levels.Sum(l => int.Parse(l.Level) * l.Probability),
            Noul: null,
            Confidence: probabilities.Values.Max(),
            Probabilities: probabilities);

        (double top, double second) = DecisionProbability.TopTwo(answer);

        return new DecisionGateOutcome(
            HasDecision: true,
            Accepted: false,
            Result: new DecisionResult(
                "fake-model",
                new Dictionary<string, DecisionAnswer> { [ReviewLoopStepExecutor.GateQuestionName] = answer },
                InputTokens: 40,
                OutputTokens: 2),
            TopTwoProbabilities: new Dictionary<string, (double Top, double Second)>
            {
                [ReviewLoopStepExecutor.GateQuestionName] = (top, second)
            });
    }

    private static Mock<IDecisionGate> GateReturning(DecisionGateOutcome outcome)
    {
        var gate = new Mock<IDecisionGate>();
        gate.Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);
        // Every escalation now completes the gate's own row with the reviewer's score, so the
        // harness answers it the way the real gate does: quietly, and without ever throwing.
        gate.Setup(g => g.AttachSystemTwoAnswersAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return gate;
    }

    private static DecisionGateRequest CapturedRequest(Mock<IDecisionGate> gate)
    {
        gate.Verify(
            g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
        return gate.Invocations
            .Single(i => i.Method.Name == nameof(IDecisionGate.DecideAsync))
            .Arguments[0] as DecisionGateRequest
            ?? throw new InvalidOperationException("no captured DecisionGateRequest");
    }
    // ---- stop early ----------------------------------------------------

    [Fact]
    public async Task Stop_early_never_invokes_the_reviewer_and_advances_without_looping()
    {
        // MinScore 9, so pAtOrAbove = p(9) + p(10) = 0.90, top-two margin 0.30.
        var agent = MockAgent();
        Mock<IDecisionGate> gate = GateReturning(ScoreOutcome(("8", 0.10), ("9", 0.60), ("10", 0.30)));
        ReviewLoopStepExecutor executor = CreateExecutor(agent, gate.Object);
        StepExecutionContext context = CreateContext(EnabledConfig);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        // The reviewer was never paid for — the whole point of the early stop.
        agent.Verify(
            a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Advance, not loop back: the reviewer's score is not even consulted.
        result.Status.Should().Be(StepStatus.Completed);
        result.NextStepIndex.Should().Be(context.CurrentStepIndex + 1);
        result.NewIterationCount.Should().Be(context.IterationCount);
        result.IterationNumber.Should().Be(context.IterationCount);

        // Marked as System One's decision, never as a bare score.
        JsonNode envelope = JsonNode.Parse(result.Output)!;
        envelope["reviewLoopGate"]!["decision"]!.GetValue<string>().Should().Be("stop-early");
        envelope["reviewLoopGate"]!["pAtOrAbove"]!.GetValue<double>().Should().BeApproximately(0.90, 1e-9);
        envelope["sentenceCheck"]!["lastKeptId"]!.GetValue<string>().Should().Be("t7");

        // The decision call's tokens are what the step spent (no reviewer ran).
        result.TokensUsed.Should().Be(42);
        result.DurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void The_gate_marker_wraps_a_non_object_prior_output_rather_than_dropping_it()
    {
        string marked = ReviewLoopStepExecutor.WithGateMarker("plain prose", 0.9);

        JsonNode envelope = JsonNode.Parse(marked)!;
        envelope["reviewLoopGate"]!["decision"]!.GetValue<string>().Should().Be("stop-early");
        envelope["priorOutput"]!.GetValue<string>().Should().Be("plain prose");
    }

    [Fact]
    public async Task The_early_stop_escalates_when_a_deterministic_narration_cap_applies()
    {
        // The early stop returns BEFORE the reviewer runs, which is also before
        // ApplyNarrationCaps is applied — so an accepting gate could otherwise skip the reviewer for,
        // and advance, a step whose narration defect the deterministic caps exist to catch. That
        // would make both caps declinable by configuration.
        //
        // The gate outcome here is the SAME accepting one as the test above (pAtOrAbove = 0.90), so
        // the only thing standing between this step and an advance is the conservatism being pinned.
        var agent = MockAgent("""{"score":9,"passesReview":true,"summary":"looks good"}""");
        Mock<IDecisionGate> gate = GateReturning(ScoreOutcome(("8", 0.10), ("9", 0.60), ("10", 0.30)));
        ReviewLoopStepExecutor executor = CreateExecutor(agent, gate.Object);
        StepExecutionContext context = CreateContext(EnabledConfig, minScore: 9, accumulatedOutput: DeclaredAndLostOutput);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        // The reviewer DID run: the gate may not accept a step the caps would have rejected.
        agent.Verify(
            a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // And the cap then did its job — 9 capped to 4 is below MinScore 9, so the step loops back
        // to the loop target rather than advancing on a narration-less render.
        result.Status.Should().Be(StepStatus.Completed);
        result.NextStepIndex.Should().Be(0, "AllSteps index 0 is step order 2, the loop target");

        // The short-circuit is placed BEFORE the provider call, deliberately: a step the caps will
        // reject never costs a Decision round trip either.
        gate.Verify(
            g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task The_early_stop_still_stops_early_for_a_deliberately_empty_narration_plan()
    {
        // The inertness control for the conservatism above: a compile node for a plan the writer
        // deliberately left empty is identical to the declared-and-lost node in every key the caps
        // read, differing only by the `all_lines_unavailable` token. Nobody asked for narration
        // here, so the gate must still be free to skip the reviewer — otherwise the new check would
        // quietly disable the calibrated early stop for every voiceover workflow.
        var agent = MockAgent();
        Mock<IDecisionGate> gate = GateReturning(ScoreOutcome(("8", 0.10), ("9", 0.60), ("10", 0.30)));
        ReviewLoopStepExecutor executor = CreateExecutor(agent, gate.Object);
        StepExecutionContext context = CreateContext(
            EnabledConfig, minScore: 9, accumulatedOutput: DeliberatelyEmptyPlanOutput);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        agent.Verify(
            a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        result.NextStepIndex.Should().Be(context.CurrentStepIndex + 1);
    }

    // ---- escalation ----------------------------------------------------

    [Fact]
    public async Task A_high_pAtOrAbove_that_is_also_bimodal_escalates_to_the_reviewer()
    {
        // pAtOrAbove (MinScore 8) = 0.40 + 0.35 + 0.20 = 0.95 — comfortably above AcceptAt 0.85 —
        // and the top two are a 0.40/0.35 split, i.e. 0.05 apart, under BimodalMargin 0.15.
        // Both conditions hold at once: the Review Focus case.
        DecisionGateOutcome outcome = ScoreOutcome(("7", 0.05), ("8", 0.20), ("9", 0.35), ("10", 0.40));

        // Pinned first on the pure rule, so the ordering is proven independently of the executor.
        ReviewLoopGateEvaluation evaluation = ReviewLoopStepExecutor.Evaluate(outcome, minScore: 8, acceptAt: 0.85, bimodalMargin: 0.15);
        evaluation.PAtOrAbove.Should().BeApproximately(0.95, 1e-9);
        evaluation.Decision.Should().Be(ReviewLoopGateDecision.Escalate);

        var agent = MockAgent();
        ReviewLoopStepExecutor executor = CreateExecutor(agent, GateReturning(outcome).Object);
        StepExecutionContext context = CreateContext(EnabledConfig, minScore: 8);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        // Today's path, exactly: the reviewer ran, its output is the step output, and a score below
        // MinScore still loops back.
        agent.Verify(
            a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        result.Output.Should().Be("""{"score":3,"passesReview":false,"summary":"cut runs long"}""");
        result.NextStepIndex.Should().Be(0); // the loop target, AllSteps index 0
        result.NewIterationCount.Should().Be(1);
    }

    [Fact]
    public async Task A_flat_low_distribution_escalates_to_the_reviewer()
    {
        var levels = Enumerable.Range(ReviewLoopStepExecutor.MinRubricLevel, ReviewLoopStepExecutor.MaxRubricLevel + 1)
            .Select(level => (level.ToString(), 1.0 / 11))
            .ToArray();

        var agent = MockAgent();
        ReviewLoopStepExecutor executor = CreateExecutor(agent, GateReturning(ScoreOutcome(levels)).Object);

        StepExecutionResult result = await executor.ExecuteAsync(CreateContext(EnabledConfig));

        agent.Verify(
            a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        result.Output.Should().Contain("passesReview");
    }

    [Fact]
    public async Task A_gate_timeout_escalates_to_the_reviewer()
    {
        // The real gate returns NoDecision when its own budget expires (rather than throwing), so
        // that is the shape a timeout takes here.
        var agent = MockAgent();
        ReviewLoopStepExecutor executor = CreateExecutor(agent, GateReturning(DecisionGateOutcome.NoDecision).Object);

        StepExecutionResult result = await executor.ExecuteAsync(CreateContext(EnabledConfig));

        agent.Verify(
            a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        result.Status.Should().Be(StepStatus.Completed);
        result.NewIterationCount.Should().Be(1);
    }

    /// <summary>
    /// QA-16, the ReviewLoop half: the <b>composed</b> path — the real <see cref="DecisionGate"/>, a
    /// real (unresolvable) provider resolution and the real executor — rather than the two halves
    /// asserted separately against a mocked <see cref="DecisionGateOutcome.NoDecision"/> on one side
    /// and the gate's own null-resolver behaviour on the other.
    ///
    /// <para>
    /// The expectation is today's byte-identical path: no <c>Capability = Decision</c> row resolves,
    /// the gate answers <see cref="DecisionGateOutcome.NoDecision"/>, the loop escalates and the
    /// reviewer runs exactly once. Nothing is recorded, because nothing was decided.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_composed_real_gate_path_with_no_resolvable_provider_escalates_to_the_reviewer()
    {
        (DecisionGate gate, DbContextOptions<WorkflowEngineDbContext> options) = RealGateWithNoResolvableProvider();

        var agent = MockAgent();
        ReviewLoopStepExecutor executor = CreateExecutor(agent, gate);

        StepExecutionResult result = await executor.ExecuteAsync(CreateContext(EnabledConfig));

        agent.Verify(
            a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        result.Status.Should().Be(StepStatus.Completed);

        await using var db = new WorkflowEngineDbContext(options);
        (await db.DecisionObservations.CountAsync())
            .Should().Be(0, "an unresolvable provider means no question was ever asked, so no row exists");
    }

    /// <summary>
    /// A real <see cref="DecisionGate"/> over an unresolvable provider. Strict mocks on the client
    /// factory and the scope factory, so reaching either one fails the test rather than passing it
    /// silently — with no provider there is no client to build and no observation store to open.
    /// </summary>
    private static (DecisionGate Gate, DbContextOptions<WorkflowEngineDbContext> Options) RealGateWithNoResolvableProvider()
    {
        var options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase($"ReviewLoopDecisionGateTests_NoProvider_{Guid.NewGuid()}")
            .Options;

        var resolver = new Mock<IInferenceProviderResolver>();
        resolver
            .Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedDecisionProvider?)null);

        var factory = new Mock<IDecisionClientFactory>(MockBehavior.Strict);
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Strict);

        return (
            new DecisionGate(resolver.Object, factory.Object, scopeFactory.Object, NullLogger<DecisionGate>.Instance),
            options);
    }

    [Fact]
    public async Task A_gate_that_throws_a_cancellation_of_its_own_escalates_rather_than_failing_the_step()
    {
        var gate = new Mock<IDecisionGate>();
        gate.Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("gate budget expired"));

        var agent = MockAgent();
        ReviewLoopStepExecutor executor = CreateExecutor(agent, gate.Object);

        StepExecutionResult result = await executor.ExecuteAsync(CreateContext(EnabledConfig));

        agent.Verify(
            a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        result.Status.Should().Be(StepStatus.Completed);
    }

    [Fact]
    public async Task The_workflows_own_cancellation_is_still_propagated()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var gate = new Mock<IDecisionGate>();
        gate.Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var agent = MockAgent();
        ReviewLoopStepExecutor executor = CreateExecutor(agent, gate.Object);

        StepExecutionContext original = CreateContext(EnabledConfig);
        var context = new StepExecutionContext
        {
            Execution = original.Execution,
            Step = original.Step,
            AllSteps = original.AllSteps,
            AccumulatedOutput = original.AccumulatedOutput,
            StepOutputHistory = original.StepOutputHistory,
            CurrentStepIndex = original.CurrentStepIndex,
            IterationCount = original.IterationCount,
            CorrelationId = original.CorrelationId,
            CancellationToken = cts.Token
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => executor.ExecuteAsync(context));
    }

    [Fact]
    public void An_answer_with_no_level_distribution_escalates()
    {
        // What the logprob client's Score answer looks like: a weighted score, but an empty
        // probability map. Nothing can be summed, so nothing may be accepted.
        var answer = new DecisionAnswer(
            Name: ReviewLoopStepExecutor.GateQuestionName,
            Kind: DecisionKind.Score,
            Choice: null,
            Score: 9.4,
            Noul: null,
            Confidence: 0.9,
            Probabilities: new Dictionary<string, double>());

        var outcome = new DecisionGateOutcome(
            HasDecision: true,
            Accepted: false,
            Result: new DecisionResult("m", new Dictionary<string, DecisionAnswer> { [ReviewLoopStepExecutor.GateQuestionName] = answer }, 1, 1),
            TopTwoProbabilities: new Dictionary<string, (double Top, double Second)>
            {
                [ReviewLoopStepExecutor.GateQuestionName] = (0.9, 0.1)
            });

        ReviewLoopStepExecutor.Evaluate(outcome, 9, 0.85, 0.15).Decision.Should().Be(ReviewLoopGateDecision.Escalate);
    }

    [Fact]
    public void An_unanswered_question_escalates()
    {
        var outcome = new DecisionGateOutcome(
            HasDecision: true,
            Accepted: false,
            Result: new DecisionResult("m", new Dictionary<string, DecisionAnswer>(), 1, 1),
            TopTwoProbabilities: new Dictionary<string, (double Top, double Second)>());

        ReviewLoopStepExecutor.Evaluate(outcome, 9, 0.85, 0.15).Decision.Should().Be(ReviewLoopGateDecision.Escalate);
    }

    // ---- the boundary and the ordering ---------------------------------

    [Fact]
    public void A_two_point_split_exactly_at_the_margin_threshold_does_not_oscillate()
    {
        // 0.625 - 0.375 is exactly 0.25 in binary floating point, so this split sits exactly ON the
        // threshold. The rule is a strict "narrower than", so it is not bimodal — and, crucially,
        // the same answer comes back every time.
        DecisionGateOutcome outcome = ScoreOutcome(("9", 0.375), ("10", 0.625));

        var decisions = Enumerable.Range(0, 10)
            .Select(_ => ReviewLoopStepExecutor.Evaluate(outcome, minScore: 9, acceptAt: 0.85, bimodalMargin: 0.25).Decision)
            .Distinct()
            .ToArray();

        decisions.Should().ContainSingle().Which.Should().Be(ReviewLoopGateDecision.StopEarly);
    }

    [Fact]
    public void One_step_narrower_than_the_threshold_is_bimodal_and_escalates()
    {
        // 0.624 - 0.375 = 0.249 < 0.25 — the immediately adjacent case, which must escalate even
        // though pAtOrAbove is 1.0.
        DecisionGateOutcome outcome = ScoreOutcome(("9", 0.376), ("10", 0.624));

        ReviewLoopGateEvaluation evaluation = ReviewLoopStepExecutor.Evaluate(outcome, 9, 0.85, 0.25);

        evaluation.PAtOrAbove.Should().BeApproximately(1.0, 1e-9);
        evaluation.Decision.Should().Be(ReviewLoopGateDecision.Escalate);
    }

    // ---- MinScore ------------------------------------------------------

    [Fact]
    public async Task MinScore_is_what_pAtOrAbove_sums_from()
    {
        // p(8) + p(9) = 0.90, p(9) alone = 0.30, and the top-two gap is a comfortable 0.30 — so the
        // ONLY thing that decides this run is which levels are counted, i.e. MinScore.
        DecisionGateOutcome outcome = ScoreOutcome(("7", 0.10), ("8", 0.60), ("9", 0.30));

        ReviewLoopStepExecutor.Evaluate(outcome, minScore: 8, acceptAt: 0.85, bimodalMargin: 0.15)
            .Decision.Should().Be(ReviewLoopGateDecision.StopEarly);
        ReviewLoopStepExecutor.Evaluate(outcome, minScore: 9, acceptAt: 0.85, bimodalMargin: 0.15)
            .Decision.Should().Be(ReviewLoopGateDecision.Escalate);

        // End to end: MinScore 8 stops early (reviewer skipped), MinScore 9 escalates (reviewer runs).
        var agentAtEight = MockAgent();
        await CreateExecutor(agentAtEight, GateReturning(outcome).Object)
            .ExecuteAsync(CreateContext(EnabledConfig, minScore: 8));
        agentAtEight.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);

        var agentAtNine = MockAgent();
        await CreateExecutor(agentAtNine, GateReturning(outcome).Object)
            .ExecuteAsync(CreateContext(EnabledConfig, minScore: 9));
        agentAtNine.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_unset_MinScore_defaults_to_nine()
    {
        DecisionGateOutcome outcome = ScoreOutcome(("8", 0.60), ("9", 0.30));

        var agent = MockAgent();
        await CreateExecutor(agent, GateReturning(outcome).Object)
            .ExecuteAsync(CreateContext(EnabledConfig, minScore: null));

        // Same as MinScore 9 above: 0.30 is below AcceptAt, so the reviewer runs.
        agent.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- disabled / inert paths ----------------------------------------

    [Fact]
    public async Task Disabled_or_absent_config_is_identical_to_todays_path()
    {
        var agentToday = MockAgent();
        var gateToday = new Mock<IDecisionGate>();
        StepExecutionResult today = await CreateExecutor(agentToday, gateToday.Object)
            .ExecuteAsync(CreateContext(reviewLoopConfigJson: null));

        var agentDisabled = MockAgent();
        var gateDisabled = new Mock<IDecisionGate>();
        StepExecutionResult disabled = await CreateExecutor(agentDisabled, gateDisabled.Object)
            .ExecuteAsync(CreateContext("""{"enabled":false,"acceptAt":0.1}"""));

        // The full result, field by field (DurationMs excepted — it is wall-clock by construction).
        disabled.Output.Should().Be(today.Output);
        disabled.NextStepIndex.Should().Be(today.NextStepIndex);
        disabled.NewIterationCount.Should().Be(today.NewIterationCount);
        disabled.IterationNumber.Should().Be(today.IterationNumber);
        disabled.TokensUsed.Should().Be(today.TokensUsed);
        disabled.Status.Should().Be(today.Status);
        disabled.ErrorDetails.Should().Be(today.ErrorDetails);
        disabled.InputTokens.Should().Be(today.InputTokens);
        disabled.OutputTokens.Should().Be(today.OutputTokens);

        // And the gate is not even consulted.
        gateToday.Verify(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        gateDisabled.Verify(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Unparseable_config_is_treated_as_no_config()
    {
        ReviewLoopStepExecutor.TryReadConfig("{not json").Should().BeNull();
        ReviewLoopStepExecutor.TryReadConfig("   ").Should().BeNull();
        ReviewLoopStepExecutor.TryReadConfig(null).Should().BeNull();
        ReviewLoopStepExecutor.TryReadConfig("""{"enabled":true}""")!.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Enabled_with_no_deterministic_facts_escalates_without_asking_the_gate()
    {
        // The main promo pipeline's ReviewLoop follows an Author step: no compile facts, so there is
        // nothing to ask a score question over. Sending an empty state instead would be worse than
        // escalating.
        var gate = new Mock<IDecisionGate>();
        var agent = MockAgent(output: """{"overallScore":9,"passesReview":true}""");
        ReviewLoopStepExecutor executor = CreateExecutor(agent, gate.Object);

        StepExecutionResult result = await executor.ExecuteAsync(
            CreateContext(EnabledConfig, accumulatedOutput: """{"files":[{"path":"src/App.tsx"}]}"""));

        gate.Verify(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        gate.Verify(
            g => g.AttachSystemTwoAnswersAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        agent.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
        result.Output.Should().Contain("overallScore");
    }

    // ---- what is asked, and what leaves the host -----------------------

    [Fact]
    public async Task The_configured_thresholds_provider_and_timeout_reach_the_gate()
    {
        Guid providerId = Guid.NewGuid();
        string config =
            $$"""{"enabled":true,"acceptAt":0.6,"bimodalMargin":0.4,"providerId":"{{providerId}}","timeoutSeconds":11}""";

        var gate = GateReturning(ScoreOutcome(("9", 0.35), ("10", 0.25), ("8", 0.20)));
        var agent = MockAgent();
        ReviewLoopStepExecutor executor = CreateExecutor(agent, gate.Object);

        StepExecutionResult result = await executor.ExecuteAsync(CreateContext(config, minScore: 8));

        DecisionGateRequest request = CapturedRequest(gate);
        request.Config.AcceptAt.Should().Be(0.6);
        request.Config.MinMargin.Should().Be(0.4);
        request.Config.ProviderId.Should().Be(providerId);
        request.Config.TimeoutSeconds.Should().Be(11);

        // pAtOrAbove = 0.80 clears the configured 0.6, but the 0.10 top-two gap is under the
        // configured 0.4, so this escalates on the bimodal rule alone.
        result.NextStepIndex.Should().Be(0);
        agent.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_gate_is_asked_one_score_question_over_the_review_facts_not_the_raw_output()
    {
        var agent = MockAgent();
        var gate = GateReturning(ScoreOutcome(("9", 0.60), ("10", 0.30)));
        ReviewLoopStepExecutor executor = CreateExecutor(agent, gate.Object);

        await executor.ExecuteAsync(CreateContext(EnabledConfig));

        DecisionGateRequest request = CapturedRequest(gate);

        request.Config.Mode.Should().Be(DecisionGateMode.Gate);
        request.Config.AcceptAt.Should().Be(0.85);
        request.Config.MinMargin.Should().Be(0.15);
        request.Config.ProviderId.Should().BeNull();
        request.Config.TimeoutSeconds.Should().Be(5);
        request.AgentType.Should().Be(AgentType.VideoReviewAgent);

        DecisionGateQuestion question = request.Questions.Should().ContainSingle().Subject;
        question.Site.Should().Be("ReviewLoop.Score");
        question.Name.Should().Be("Score");
        question.Kind.Should().Be(DecisionKind.Score);
        question.Options.Should().Equal("0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10");

        // No reviewer has run when this is asked, so there is no answer to compare against. Null,
        // not an empty string: the gate reads null as "no counterfactual" and leaves the row with
        // no agreement claim, where an empty string would be compared as a disagreement.
        question.SystemTwoAnswer.Should().BeNull();

        // The state is the deterministic facts the reviewer's prompt consumes...
        request.State.Content.Should().Contain("sentenceCheck");
        request.State.Content.Should().Contain("coveragePct");
        request.State.Content.Should().Contain("dialogueHeadroom");
        request.State.Content.Should().Contain("headroomDb");

        // ...not the raw prior output (no storage keys, no artifact keys)...
        request.State.Content.Should().NotContain("outputStorageKey");
        request.State.Content.Should().NotContain("artifactStorageKey");
        request.State.Content.Should().NotContain("timelineStorageKey");

        // ...and it is bounded well inside the 4k-token Decision context.
        request.State.Content.Length.Should().BeLessThanOrEqualTo(ReviewLoopStepExecutor.MaxFactsChars);
    }

    // ---- the declared accept basis (T-017) -----------------------------

    [Fact]
    public async Task The_score_question_declares_AtOrAbove_with_the_steps_MinScore_as_its_cutoff()
    {
        // This site accepts on Σ p(level >= MinScore), never on the top probability — so it says so,
        // and the cutoff it needs travels with the declaration. Without the declaration the
        // calibration view recomputes this row with the universal top/margin rule and reports an
        // escalation rate for a question this step never asks.
        var gate = GateReturning(ScoreOutcome(("9", 0.60), ("10", 0.30)));

        await CreateExecutor(MockAgent(), gate.Object).ExecuteAsync(CreateContext(EnabledConfig, minScore: 8));

        DecisionGateQuestion question = CapturedRequest(gate).Questions.Should().ContainSingle().Subject;

        question.AcceptMetric.Should().Be(DecisionAcceptMetric.AtOrAbove);
        question.ScoreCutoff.Should().Be(8);
    }

    [Fact]
    public async Task An_unset_MinScore_is_the_cutoff_the_declaration_carries()
    {
        // The resolved value, not the nullable step property: `context.Step.MinScore ?? 9` is what the
        // sum actually used, so it is what the row has to record for the recomputation to be exact.
        var gate = GateReturning(ScoreOutcome(("9", 0.60), ("10", 0.30)));

        await CreateExecutor(MockAgent(), gate.Object).ExecuteAsync(CreateContext(EnabledConfig, minScore: null));

        DecisionGateQuestion question = CapturedRequest(gate).Questions.Should().ContainSingle().Subject;

        question.AcceptMetric.Should().Be(DecisionAcceptMetric.AtOrAbove);
        question.ScoreCutoff.Should().Be(9);
    }

    [Fact]
    public async Task The_persisted_row_records_the_basis_and_the_stored_escalation_follows_the_sites_own_rule()
    {
        // top 0.90 with an 0.84 margin — confident enough for the universal top/margin rule, which is
        // why this row used to be stored as NOT escalated — but no mass at or above level 9, so this
        // site escalates and pays for the reviewer.
        (IDecisionGate gate, DbContextOptions<WorkflowEngineDbContext> options) =
            RealGate(ScoreResult(9.0, ("6", 0.90), ("5", 0.06), ("7", 0.04)));

        await CreateExecutor(MockAgent(), gate).ExecuteAsync(CreateContext(EnabledConfig, minScore: 9));

        await using var db = new WorkflowEngineDbContext(options);
        DecisionObservation row = await db.DecisionObservations.SingleAsync(o => o.Mode == "Gate");

        row.AcceptMetric.Should().Be(nameof(DecisionAcceptMetric.AtOrAbove));
        row.ScoreCutoff.Should().Be(9);
        row.Escalated.Should().BeTrue("p(level >= 9) is 0.00, which is the rule this site applies");
    }

    [Fact]
    public void A_huge_prior_output_is_still_projected_within_the_cap()
    {
        var overlays = new JsonArray();
        for (int i = 0; i < 50; i++)
            overlays.Add(new JsonObject { ["placementId"] = $"p{i}", ["coveragePct"] = 1.5 });

        var prior = new JsonObject
        {
            ["status"] = "completed",
            ["sentenceCheck"] = new JsonObject
            {
                ["applicable"] = true,
                ["lastSegmentText"] = new string('x', 200_000),
                ["punctuationRatio"] = 0.5
            },
            ["graphics"] = new JsonObject { ["applied"] = true, ["appliedOverlays"] = overlays }
        };

        string facts = ReviewLoopStepExecutor.BuildReviewFacts(prior.ToJsonString())!;

        facts.Length.Should().BeLessThanOrEqualTo(ReviewLoopStepExecutor.MaxFactsChars + 64);
        facts.Should().NotContain(new string('x', 1000));
        facts.Should().Contain("coveragePct");
    }

    [Fact]
    public void A_prior_output_without_any_review_facts_projects_to_nothing()
    {
        ReviewLoopStepExecutor.BuildReviewFacts(null).Should().BeNull();
        ReviewLoopStepExecutor.BuildReviewFacts("").Should().BeNull();
        ReviewLoopStepExecutor.BuildReviewFacts("not json").Should().BeNull();
        ReviewLoopStepExecutor.BuildReviewFacts("""{"status":"completed"}""").Should().BeNull();
        ReviewLoopStepExecutor.BuildReviewFacts("[1,2,3]").Should().BeNull();
    }

    // =====================================================================
    // The counterfactual: what the gate is told, and what the row ends up saying.
    //
    // This half uses a REAL DecisionGate over an in-memory store, so the observation row — and
    // what DecisionCalibrationService makes of it — are the production ones. The mocked gate
    // above proves which calls the executor makes; only a real one proves what they leave behind.
    // =====================================================================

    /// <summary>
    /// A <see cref="DecisionResult"/> carrying one <c>Score</c> answer whose value is exactly
    /// <paramref name="modelScore"/> — the number the gate stores in <c>Answer</c> and compares
    /// System Two's score against.
    /// </summary>
    private static DecisionResult ScoreResult(double modelScore, params (string Level, double Probability)[] levels)
    {
        var probabilities = levels.ToDictionary(l => l.Level, l => l.Probability);
        var answer = new DecisionAnswer(
            Name: ReviewLoopStepExecutor.GateQuestionName,
            Kind: DecisionKind.Score,
            Choice: null,
            Score: modelScore,
            Noul: null,
            Confidence: probabilities.Values.Max(),
            Probabilities: probabilities);

        return new DecisionResult(
            "fake-model",
            new Dictionary<string, DecisionAnswer> { [ReviewLoopStepExecutor.GateQuestionName] = answer },
            InputTokens: 40,
            OutputTokens: 2);
    }

    /// <summary>
    /// A real <see cref="DecisionGate"/> whose provider lookup, client and store are all
    /// in-memory. The returned options are what a test opens to inspect the rows the gate wrote.
    /// </summary>
    private static (IDecisionGate Gate, DbContextOptions<WorkflowEngineDbContext> Options) RealGate(DecisionResult result)
    {
        var options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase($"ReviewLoopDecisionGateTests_{Guid.NewGuid()}")
            .Options;

        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedDecisionProvider(
                ProviderId: Guid.NewGuid(),
                Name: "mock-provider",
                Kind: InferenceProviderKind.OpenAICompatible,
                Endpoint: "http://test",
                ModelName: "fake-model",
                ApiKey: "key",
                TimeoutSeconds: 300));

        var client = new Mock<IDecisionClient>();
        client.Setup(c => c.DecideAsync(
                It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var factory = new Mock<IDecisionClientFactory>();
        factory.Setup(f => f.Get(It.IsAny<ResolvedDecisionProvider>())).Returns(client.Object);

        // A fresh context per scope: the gate opens one to write the observations and Attach opens
        // its own to complete them, and both must land in the same in-memory store.
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(sf => sf.CreateScope()).Returns(() =>
        {
            var scopeServiceProvider = new Mock<IServiceProvider>();
            scopeServiceProvider.Setup(sp => sp.GetService(typeof(WorkflowEngineDbContext)))
                .Returns(() => (object?)new WorkflowEngineDbContext(options));

            var scope = new Mock<IServiceScope>();
            scope.Setup(s => s.ServiceProvider).Returns(scopeServiceProvider.Object);
            return scope.Object;
        });

        return (
            new DecisionGate(resolver.Object, factory.Object, scopeFactory.Object, NullLogger<DecisionGate>.Instance),
            options);
    }

    /// <summary>
    /// One already-persisted <c>Shadow</c> row carrying a genuine observed agreement, standing in
    /// for the rest of the estate so the agreement rate below is separable: with the ReviewLoop row
    /// excluded it is 1.0, and if that row wrongly claimed a disagreement it is 0.5.
    /// </summary>
    private static DecisionObservation ObservedAgreementRow() =>
        new()
        {
            Id = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            StepOrder = 3,
            AgentType = AgentType.Colorist,
            Site = "Colorist.step",
            Question = "look",
            OptionsHash = "HASH",
            Kind = DecisionKind.Choice,
            Answer = "Warm",
            Confidence = 0.95,
            ProbabilitiesJson = """{"Warm":0.95,"Cool":0.05}""",
            Mode = "Shadow",
            Escalated = false,
            DownstreamOutcomeJson = """{"agreed":true}""",
            ProviderId = null,
            Model = "fake-model",
            DecisionInputTokens = 40,
            DecisionOutputTokens = 2,
            AgentTokensUsed = 120,
            CreatedAt = DateTime.UtcNow,
        };

    [Fact]
    public async Task A_stopped_early_review_loop_leaves_its_row_claiming_no_agreement_at_all()
    {
        // p(9) + p(10) = 0.90 clears AcceptAt 0.85, and the 0.30 top-two gap clears BimodalMargin
        // 0.15 — so the gate is confident and the reviewer is never run. No reviewer means no
        // counterfactual, and the row must say exactly that.
        (IDecisionGate gate, DbContextOptions<WorkflowEngineDbContext> options) =
            RealGate(ScoreResult(9.0, ("8", 0.10), ("9", 0.60), ("10", 0.30)));

        var agent = MockAgent();
        StepExecutionContext context = CreateContext(EnabledConfig);

        StepExecutionResult result = await CreateExecutor(agent, gate).ExecuteAsync(context);

        agent.Verify(
            a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // The stop-early decision itself is unchanged: advance, no loop, marked as System One's.
        result.NextStepIndex.Should().Be(context.CurrentStepIndex + 1);
        JsonNode envelope = JsonNode.Parse(result.Output)!;
        envelope["reviewLoopGate"]!["decision"]!.GetValue<string>().Should().Be("stop-early");

        // The gate still recorded its observation — the decision is the point of the call — but
        // with no agreement claim on it.
        await using var db = new WorkflowEngineDbContext(options);
        DecisionObservation row = db.DecisionObservations.Single();
        row.Site.Should().Be("ReviewLoop.Score");
        row.Question.Should().Be("Score");
        row.Mode.Should().Be("Gate");
        row.Answer.Should().Be("9");
        row.DownstreamOutcomeJson.Should().BeNull(
            "no reviewer ran, so there is no counterfactual to agree or disagree with");
    }

    [Fact]
    public async Task A_stopped_early_review_loop_does_not_dilute_the_calibration_agreement_rate()
    {
        // Proved through the aggregation, not by restating the rule: AgreementRate divides the
        // rows that recorded an outcome by the rows that recorded one, and a row left with a null
        // DownstreamOutcomeJson is in neither. One genuine observed agreement is seeded alongside,
        // so the two rules are separable — excluded gives 1.0, wrongly counted as a disagreement
        // gives 0.5, which is the 0% site this defect produced.
        (IDecisionGate gate, DbContextOptions<WorkflowEngineDbContext> options) =
            RealGate(ScoreResult(9.0, ("8", 0.10), ("9", 0.60), ("10", 0.30)));

        await CreateExecutor(MockAgent(), gate).ExecuteAsync(CreateContext(EnabledConfig));

        await using var db = new WorkflowEngineDbContext(options);
        db.DecisionObservations.Add(ObservedAgreementRow());
        await db.SaveChangesAsync();

        DecisionCalibrationReport report = await new DecisionCalibrationService(db)
            .GetCalibrationAsync(acceptAt: 0.85, minMargin: 0.15, providerId: null, site: null, ct: CancellationToken.None);

        report.Totals.ObservationCount.Should().Be(2, "both rows are still observed");
        report.Totals.AgreementRate.Should().Be(1.0);

        DecisionCalibrationSite site = report.Sites.Single(s => s.Site == "ReviewLoop.Score");
        site.ObservationCount.Should().Be(1);
        site.AgreementCount.Should().Be(0);
        site.AgreementRate.Should().Be(0.0, "a site with no observed outcome has no rate to dilute");
    }

    [Fact]
    public async Task A_reviewer_that_agrees_with_the_gate_completes_the_row_as_agreed()
    {
        // p(9) = 0.05 is far below AcceptAt, so the reviewer runs even though the margin is wide.
        // The gate's own answer for a Score question is its Score — "8" — and the reviewer ends up
        // with the same score, so this row records a real agreement. The reviewer's 8 is below
        // MinScore 9, so this is the LOOP-BACK escalated outcome.
        (IDecisionGate gate, DbContextOptions<WorkflowEngineDbContext> options) =
            RealGate(ScoreResult(8.0, ("8", 0.30), ("9", 0.05)));

        var agent = MockAgent("""{"score":8,"passesReview":true,"summary":"fine"}""");
        StepExecutionContext context = CreateContext(EnabledConfig);

        StepExecutionResult result = await CreateExecutor(agent, gate).ExecuteAsync(context);

        result.NextStepIndex.Should().Be(0, "the loop target, AllSteps index 0");

        await using var db = new WorkflowEngineDbContext(options);
        DecisionObservation row = db.DecisionObservations.Single(o => o.Mode == "Gate");
        row.Answer.Should().Be("8");
        row.DownstreamOutcomeJson.Should().Be("""{"agreed":true}""");
        row.AgentTokensUsed.Should().Be(120, "the reviewer that supplied the counterfactual spent them");
    }

    [Fact]
    public async Task A_reviewer_that_disagrees_with_the_gate_completes_the_row_as_a_disagreement()
    {
        // Same gate answer ("8"), a reviewer that scored 9 — a real, observed disagreement, and
        // the ADVANCE escalated outcome (9 >= MinScore 9, so the loop does not go back).
        (IDecisionGate gate, DbContextOptions<WorkflowEngineDbContext> options) =
            RealGate(ScoreResult(8.0, ("8", 0.30), ("9", 0.05)));

        var agent = MockAgent("""{"score":9,"passesReview":true,"summary":"good"}""");
        StepExecutionContext context = CreateContext(EnabledConfig);

        StepExecutionResult result = await CreateExecutor(agent, gate).ExecuteAsync(context);

        result.NextStepIndex.Should().Be(context.CurrentStepIndex + 1, "advance, not loop back");

        await using var db = new WorkflowEngineDbContext(options);
        DecisionObservation row = db.DecisionObservations.Single(o => o.Mode == "Gate");
        row.Answer.Should().Be("8");
        row.DownstreamOutcomeJson.Should().Be("""{"agreed":false}""");
        row.AgentTokensUsed.Should().Be(120);
    }

    // =====================================================================
    // The same calls, seen from the executor's side: once per reviewer run, on both escalated
    // outcomes, and never where there was no reviewer to supply a counterfactual.
    // =====================================================================

    [Fact]
    public async Task The_reviewers_score_and_tokens_are_attached_once_when_it_loops_back()
    {
        // Model answers "8"; the reviewer scores 3, so the loop goes back. One call, carrying the
        // gate's own question name, the score in the gate's own Score convention, and the
        // reviewer's tokens.
        DecisionGateOutcome outcome = ScoreOutcome(("8", 0.30), ("9", 0.05));
        var gate = GateReturning(outcome);
        var agent = MockAgent();
        StepExecutionContext context = CreateContext(EnabledConfig, minScore: 9);

        StepExecutionResult result = await CreateExecutor(agent, gate.Object).ExecuteAsync(context);

        result.NextStepIndex.Should().Be(0);

        gate.Verify(
            g => g.AttachSystemTwoAnswersAsync(
                context.Execution.Id,
                context.Step.StepOrder,
                It.Is<IReadOnlyDictionary<string, string>>(answers =>
                    answers.Count == 1 && answers[ReviewLoopStepExecutor.GateQuestionName] == "3"),
                120,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task The_reviewers_score_and_tokens_are_attached_once_when_it_advances()
    {
        // The other escalated outcome: the reviewer scored 9, at MinScore, so the step advances.
        // The comparison is real either way, so the attachment is not conditional on the branch.
        var gate = GateReturning(ScoreOutcome(("8", 0.30), ("9", 0.05)));
        var agent = MockAgent("""{"score":9,"passesReview":true,"summary":"good"}""");
        StepExecutionContext context = CreateContext(EnabledConfig, minScore: 9);

        StepExecutionResult result = await CreateExecutor(agent, gate.Object).ExecuteAsync(context);

        result.NextStepIndex.Should().Be(context.CurrentStepIndex + 1);

        gate.Verify(
            g => g.AttachSystemTwoAnswersAsync(
                context.Execution.Id,
                context.Step.StepOrder,
                It.Is<IReadOnlyDictionary<string, string>>(answers =>
                    answers.Count == 1 && answers[ReviewLoopStepExecutor.GateQuestionName] == "9"),
                120,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Nothing_is_attached_when_the_reviewer_never_ran()
    {
        // The stop-early path deliberately skips the reviewer, so there is no counterfactual at
        // all. Attaching one would be a lie about a reviewer that was never paid for.
        var gate = GateReturning(ScoreOutcome(("8", 0.10), ("9", 0.60), ("10", 0.30)));
        var agent = MockAgent();

        await CreateExecutor(agent, gate.Object).ExecuteAsync(CreateContext(EnabledConfig));

        agent.Verify(
            a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        gate.Verify(
            g => g.AttachSystemTwoAnswersAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
