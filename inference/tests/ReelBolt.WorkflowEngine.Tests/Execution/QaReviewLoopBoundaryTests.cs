#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Controllers;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Decision;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution;

/// <summary>
/// Independent verification of four boundary behaviours on the decision-engine phase-2 surface
/// (ticket T-501). Every test here is written against the CODE, not against a commit message or a
/// doc comment, and several deliberately use the <b>real</b> <see cref="DecisionGate"/> rather than
/// a mock that returns the answer under test — a mock that returns
/// <see cref="DecisionGateOutcome.NoDecision"/> proves the executor handles that value, not that an
/// unconfigured provider produces it.
///
/// <para><b>1. Expression-mode parity, including the error path.</b>
/// <see cref="ConditionalStepExecutor"/> is asserted <i>differentially</i>: for each case the
/// expected branch is computed by calling <see cref="ExpressionEvaluator.Evaluate(string, string)"/>
/// directly — the pre-change oracle — and the executor's <c>NextStepIndex</c> must agree with it.
/// A named test also pins <see cref="ExpressionEvaluator"/> on its own, without passing through any
/// <c>ConditionMode</c> code, so a regression inside the evaluator is caught separately.</para>
///
/// <para><b>2. Decision mode with no provider configured.</b> Built on a real
/// <see cref="DecisionGate"/> whose <see cref="IInferenceProviderResolver"/> resolves nothing — the
/// real "no <c>Decision</c> provider row exists" signal — rather than on a mocked
/// <c>NoDecision</c>.</para>
///
/// <para><b>3. The ReviewLoop bimodal boundary.</b> The bimodal check's position ahead of the
/// <c>AcceptAt</c> check is pinned both as source order and as behaviour, repeatedly, including a
/// two-point split sitting exactly ON <c>BimodalMargin</c>.</para>
///
/// <para><b>4. Calibration aggregation and endpoint validation.</b> Hostile and older-schema
/// <c>decision_observations</c> rows, then the endpoint's own parameters.</para>
/// </summary>
public sealed class QaReviewLoopBoundaryTests
{
    // =====================================================================
    // 1a. ExpressionEvaluator pinned on its own — no ConditionMode code in the path.
    // =====================================================================

    /// <summary>
    /// The pre-existing evaluator contract, exercised without the executor. These are the cases
    /// acceptance criterion 2 asks to be pinned independently, so that a regression inside
    /// <see cref="ExpressionEvaluator"/> cannot hide behind the new mode dispatch.
    /// </summary>
    [Fact]
    public void ExpressionEvaluator_unknown_identifier_is_false_and_never_throws()
    {
        // "decision.p_yes" is not a field of the JSON below, so no parameter is bound to it. NCalc
        // throws on evaluation of an unbound identifier and EvaluateCore's catch turns that into
        // false — the False branch, never an exception out of the executor.
        bool result = ExpressionEvaluator.Evaluate("decision.p_yes >= 0.65", """{"score":9}""");

        result.Should().BeFalse("an unresolvable identifier is false, not an exception");
    }

    [Fact]
    public void ExpressionEvaluator_unparseable_expression_is_false_and_never_throws()
    {
        ExpressionEvaluator.Evaluate("$.score >", """{"score":9}""").Should().BeFalse();
        ExpressionEvaluator.Evaluate("((($.score > 8", """{"score":9}""").Should().BeFalse();
        ExpressionEvaluator.Evaluate("$.score > 8 &&", """{"score":9}""").Should().BeFalse();
    }

    /// <summary>
    /// <b>The pre-change contract for a null/blank expression is <c>true</c>, not <c>false</c>.</b>
    /// <see cref="ExpressionEvaluator.EvaluateCore"/> short-circuits on
    /// <c>string.IsNullOrWhiteSpace(expression)</c> and returns <c>true</c> before any NCalc work,
    /// and <c>ConditionalStepExecutor.EvaluateExpression</c> passes
    /// <c>context.Step.ConditionExpression ?? ""</c> straight into it. The ticket's acceptance
    /// wording expects a null <c>ConditionExpression</c> to resolve to <b>false</b>; it does not,
    /// and never did. This test states the real behaviour so the discrepancy is visible rather than
    /// assumed away.
    /// </summary>
    [Fact]
    public void ExpressionEvaluator_null_and_blank_expression_is_TRUE_not_false()
    {
        ExpressionEvaluator.Evaluate("", """{"score":9}""")
            .Should().BeTrue("EvaluateCore short-circuits IsNullOrWhiteSpace to true");
        ExpressionEvaluator.Evaluate("   ", """{"score":9}""").Should().BeTrue();
        ExpressionEvaluator.Evaluate(null!, """{"score":9}""").Should().BeTrue();
    }

    [Fact]
    public void ExpressionEvaluator_over_long_or_deeply_nested_expression_is_false()
    {
        string tooLong = "$.score > 8 && " + new string('x', 2100);
        ExpressionEvaluator.Evaluate(tooLong, """{"score":9}""").Should().BeFalse();

        string tooDeep = new string('(', 25) + "$.score > 8" + new string(')', 25);
        ExpressionEvaluator.Evaluate(tooDeep, """{"score":9}""").Should().BeFalse();
    }

    // =====================================================================
    // 1b. Expression-mode parity — the executor against that same oracle.
    // =====================================================================

    private const string QuestionName = "should-promote";

    private static StepExecutionContext CreateConditionalContext(
        string? conditionExpression,
        string? conditionalConfigJson,
        string accumulatedOutput = """{"score":9}""")
    {
        var conditionalStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.Conditional,
            ConditionExpression = conditionExpression,
            ConditionalConfigJson = conditionalConfigJson,
            TrueBranchStepOrder = "2",
            FalseBranchStepOrder = "3",
            AgentDefinition = new AgentDefinition
            {
                Id = Guid.NewGuid(),
                AgentType = AgentType.ExtractTransform,
                Name = "ExtractTransform"
            }
        };

        var trueBranch = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = conditionalStep.WorkflowDefinitionId,
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.Agent
        };

        var falseBranch = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = conditionalStep.WorkflowDefinitionId,
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 3,
            StepType = StepType.Agent
        };

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
            Step = conditionalStep,
            AllSteps = [conditionalStep, trueBranch, falseBranch],
            AccumulatedOutput = accumulatedOutput,
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 3,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    private static string ConditionalConfig(string mode) =>
        JsonSerializer.Serialize(new
        {
            version = 1,
            mode,
            decisionQuestion = QuestionName,
            threshold = 0.5,
            providerId = (Guid?)null,
            timeoutSeconds = 5
        });

    /// <summary>
    /// The differential parity test: the expected branch is whatever
    /// <see cref="ExpressionEvaluator.Evaluate(string, string)"/> — the ORACLE, called directly, not
    /// through any new code — says, and the executor must land on exactly that branch. Every case
    /// in the matrix, error path included, is run against a gate mock that <b>must never be called</b>.
    ///
    /// <para>
    /// <c>ConstTrueIndex</c>/<c>ConstFalseIndex</c> are the indices the executor resolves for a
    /// <c>true</c>/<c>false</c> decision when both branch step orders exist: <c>StepOrder 2</c> is
    /// <c>AllSteps[1]</c> and <c>StepOrder 3</c> is <c>AllSteps[2]</c>.
    /// </para>
    /// </summary>
    [Theory]
    // mode label,              condition expression,          accumulated output,      config json
    [InlineData("Expression", "$.score > 8", "{\"score\":9}")]
    [InlineData("Expression", "$.score > 8", "{\"score\":3}")]
    [InlineData("Expression", "score > 8", "{\"score\":9}")]
    [InlineData("Expression", "decision.p_yes >= 0.65", "{\"score\":9}")]
    [InlineData("Expression", "$.score >", "{\"score\":9}")]
    [InlineData("Expression", "$.missing > 8", "{\"score\":9}")]
    [InlineData("Expression", "", "{\"score\":9}")]
    [InlineData("Expression", "   ", "{\"score\":9}")]
    [InlineData("Expression", null, "{\"score\":9}")]
    public async Task Expression_mode_takes_exactly_the_branch_ExpressionEvaluator_takes(
        string mode, string? conditionExpression, string accumulatedOutput)
    {
        // The oracle: the pre-change evaluator, called with the same arguments the executor uses.
        bool expected = ExpressionEvaluator.Evaluate(conditionExpression ?? "", accumulatedOutput);

        StepExecutionContext context = CreateConditionalContext(
            conditionExpression, ConditionalConfig(mode), accumulatedOutput);

        StepExecutionResult result =
            await new ConditionalStepExecutor(NullLogger<ConditionalStepExecutor>.Instance, NeverCalledGate().Object)
                .ExecuteAsync(context);

        int expectedIndex = expected ? 1 : 2;
        result.NextStepIndex.Should().Be(
            expectedIndex,
            "ExpressionEvaluator returned {0} for '{1}', so the {2} branch is the parity answer",
            expected, conditionExpression, expected ? "True" : "False");
        result.Status.Should().Be(StepStatus.Completed);
        result.ErrorDetails.Should().BeNull();
    }

    /// <summary>
    /// The same parity, for the two configurations that mean "today's expression path" without ever
    /// naming it: a <b>null/absent</b> <c>ConditionalConfigJson</c> and an <b>unparseable</b> one.
    /// Both must reach the evaluator and neither may touch the gate.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ this is not json")]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task Absent_or_unparseable_config_takes_the_oracle_branch_and_never_asks_the_gate(
        string? conditionalConfigJson)
    {
        const string expression = "$.score > 8";
        const string output = """{"score":9}""";
        bool expected = ExpressionEvaluator.Evaluate(expression, output);

        var gate = NeverCalledGate();
        StepExecutionContext context = CreateConditionalContext(expression, conditionalConfigJson, output);

        StepExecutionResult result =
            await new ConditionalStepExecutor(NullLogger<ConditionalStepExecutor>.Instance, gate.Object)
                .ExecuteAsync(context);

        result.NextStepIndex.Should().Be(expected ? 1 : 2);
        result.Status.Should().Be(StepStatus.Completed);
        VerifyGateNeverCalled(gate);
    }

    /// <summary>
    /// The <c>else</c> arm of <see cref="ConditionalStepExecutor"/>"'s mode dispatch — a
    /// <c>Mode</c> value that is neither <c>Expression</c> nor <c>Decision</c>. Reached through a
    /// numeric enum value, which <c>JsonStringEnumConverter</c> admits by default
    /// (<c>AllowIntegerValues</c> is on), so it is genuinely reachable from stored JSON and not
    /// merely theoretical. It must evaluate the expression and must NOT call a provider.
    /// </summary>
    [Fact]
    public async Task An_unrecognised_numeric_condition_mode_falls_back_to_the_expression_path()
    {
        var gate = NeverCalledGate();
        const string output = """{"score":9}""";

        StepExecutionContext context = CreateConditionalContext(
            "$.score > 8",
            """{"version":1,"mode":99,"threshold":0.5,"timeoutSeconds":5}""",
            output);

        StepExecutionResult result =
            await new ConditionalStepExecutor(NullLogger<ConditionalStepExecutor>.Instance, gate.Object)
                .ExecuteAsync(context);

        result.NextStepIndex.Should().Be(1, "the expression is true, so the True branch");
        result.Status.Should().Be(StepStatus.Completed);
        VerifyGateNeverCalled(gate);
    }

    [Fact]
    public async Task An_unrecognised_string_condition_mode_is_treated_as_no_config()
    {
        // A string the enum does not name makes JsonStringEnumConverter throw, TryReadConfig
        // returns null, and the expression path runs — the documented "a bad config is today's
        // path, never a thrown step" contract.
        var gate = NeverCalledGate();
        StepExecutionContext context = CreateConditionalContext(
            "$.score > 8", """{"version":1,"mode":"Nonsense"}""");

        StepExecutionResult result =
            await new ConditionalStepExecutor(NullLogger<ConditionalStepExecutor>.Instance, gate.Object)
                .ExecuteAsync(context);

        result.NextStepIndex.Should().Be(1);
        result.Status.Should().Be(StepStatus.Completed);
        result.ErrorDetails.Should().BeNull();
        VerifyGateNeverCalled(gate);
    }

    // =====================================================================
    // 2. Decision mode with NO Decision provider configured — the real gate.
    // =====================================================================

    /// <summary>
    /// A <see cref="DecisionGate"/> wired the way the real container wires it, with the one
    /// difference that matters: <see cref="IInferenceProviderResolver.ResolveDecisionAsync"/>
    /// resolves nothing, which is exactly what it does when no enabled
    /// <c>Capability == Decision</c> row exists.
    ///
    /// <para>
    /// The client factory and the scope factory are STRICT: any call on either is a failure of the
    /// assertion, not a detail. That is what proves "no provider configured" reaches neither a
    /// model call nor a database write.
    /// </para>
    /// </summary>
    private static (DecisionGate Gate, Mock<IDecisionClientFactory> Factory, Mock<IServiceScopeFactory> ScopeFactory)
        RealGateWithNoDecisionProvider()
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver
            .Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedDecisionProvider?)null);

        var factory = new Mock<IDecisionClientFactory>(MockBehavior.Strict);
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Strict);

        var gate = new DecisionGate(
            resolver.Object,
            factory.Object,
            scopeFactory.Object,
            NullLogger<DecisionGate>.Instance);

        return (gate, factory, scopeFactory);
    }

    /// <summary>
    /// Acceptance criterion 3, on a REAL gate: with no <c>Decision</c> provider configured, a
    /// <c>Conditional</c> step in <c>ConditionMode.Decision</c> evaluates to <c>false</c> (the False
    /// branch), does NOT throw, is NOT recorded as a failure, and its
    /// <see cref="StepExecutionResult.Status"/> is <see cref="StepStatus.Completed"/> — unchanged.
    /// </summary>
    [Fact]
    public async Task Decision_mode_with_no_provider_evaluates_false_completes_and_touches_nothing()
    {
        (DecisionGate gate, Mock<IDecisionClientFactory> factory, Mock<IServiceScopeFactory> scopeFactory) =
            RealGateWithNoDecisionProvider();

        StepExecutionContext context = CreateConditionalContext(
            "decision.p_yes >= 0.65", ConditionalConfig("Decision"));

        StepExecutionResult result =
            await new ConditionalStepExecutor(NullLogger<ConditionalStepExecutor>.Instance, gate)
                .ExecuteAsync(context);

        result.NextStepIndex.Should().Be(2, "false falls to the False branch (StepOrder 3, AllSteps[2])");
        result.Status.Should().Be(StepStatus.Completed, "the gate never fails the step");
        result.ErrorDetails.Should().BeNull("no failure is recorded");
        result.Output.Should().Be(context.AccumulatedOutput, "the step output is unchanged");
        result.NewIterationCount.Should().Be(3, "the iteration count is unchanged");
        result.TokensUsed.Should().Be(0);
        result.IterationNumber.Should().BeNull();

        // Stronger than "no observation was written": neither the client factory nor the scope
        // factory was reached at all, so no outbound call and no store round-trip happened.
        factory.Invocations.Should().BeEmpty();
        scopeFactory.Invocations.Should().BeEmpty();
    }

    /// <summary>
    /// The same real-gate shape on the <c>Threshold</c> path: a blank <c>ConditionExpression</c>
    /// under <c>ConditionMode.Decision</c> with no provider must also resolve to false rather than
    /// to the "blank expression is true" answer the Expression path gives.
    /// </summary>
    [Fact]
    public async Task Decision_mode_threshold_path_with_no_provider_also_evaluates_false()
    {
        (DecisionGate gate, _, _) = RealGateWithNoDecisionProvider();

        StepExecutionContext context = CreateConditionalContext(null, ConditionalConfig("Decision"));

        StepExecutionResult result =
            await new ConditionalStepExecutor(NullLogger<ConditionalStepExecutor>.Instance, gate)
                .ExecuteAsync(context);

        result.NextStepIndex.Should().Be(2);
        result.Status.Should().Be(StepStatus.Completed);
        result.ErrorDetails.Should().BeNull();
    }

    /// <summary>
    /// A resolver that throws is also a "gate failure" and must be swallowed, not propagated.
    /// </summary>
    [Fact]
    public async Task Decision_mode_with_a_resolver_that_throws_still_evaluates_false_and_completes()
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver
            .Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider store unreachable"));

        var factory = new Mock<IDecisionClientFactory>(MockBehavior.Strict);
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Strict);

        var gate = new DecisionGate(
            resolver.Object, factory.Object, scopeFactory.Object, NullLogger<DecisionGate>.Instance);

        StepExecutionContext context = CreateConditionalContext(
            "decision.p_yes >= 0.65", ConditionalConfig("Decision"));

        StepExecutionResult result =
            await new ConditionalStepExecutor(NullLogger<ConditionalStepExecutor>.Instance, gate)
                .ExecuteAsync(context);

        result.NextStepIndex.Should().Be(2);
        result.Status.Should().Be(StepStatus.Completed);
        result.ErrorDetails.Should().BeNull();
    }

    // =====================================================================
    // 3. The ReviewLoop bimodal boundary and its order relative to AcceptAt.
    // =====================================================================

    private static Mock<IDecisionGate> NeverCalledGate()
    {
        var gate = new Mock<IDecisionGate>();
        gate.Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the gate must not be consulted on this path"));
        return gate;
    }

    private static void VerifyGateNeverCalled(Mock<IDecisionGate> gate) =>
        gate.Verify(
            g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);

    /// <summary>
    /// A <c>Score</c> outcome carrying <paramref name="levels"/> as its probability map, with the
    /// top-two pair taken from the SAME shared helper the production gate uses, so this file never
    /// invents a margin the real path would compute differently.
    /// </summary>
    private static DecisionGateOutcome ScoreOutcome(
        bool accepted, params (string Level, double Probability)[] levels)
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
            Accepted: accepted,
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

    // ---- ordering: bimodal checked BEFORE AcceptAt ----------------------

    /// <summary>
    /// The ordering acceptance criterion 4 asks for, as a pure repeated property: a distribution
    /// that is SIMULTANEOUSLY high <c>p(level &gt;= MinScore)</c> and bimodal escalates, every
    /// single time.
    ///
    /// <para>
    /// Levels are 7:0.05, 8:0.20, 9:0.35, 10:0.40 with <c>MinScore = 8</c>, so
    /// <c>pAtOrAbove = 0.20 + 0.35 + 0.40 = 0.95</c>, comfortably above <c>AcceptAt = 0.85</c>.
    /// The top two are 0.40/0.35 — 0.05 apart, under <c>BimodalMargin = 0.15</c>. Both conditions
    /// hold at once; only the ordering decides the outcome.
    /// </para>
    /// </summary>
    [Fact]
    public void A_high_and_bimodal_distribution_escalates_on_every_repetition()
    {
        DecisionGateOutcome outcome = ScoreOutcome(
            accepted: false, ("7", 0.05), ("8", 0.20), ("9", 0.35), ("10", 0.40));

        ReviewLoopGateEvaluation[] evaluations = Enumerable.Range(0, 500)
            .Select(_ => ReviewLoopStepExecutor.Evaluate(outcome, minScore: 8, acceptAt: 0.85, bimodalMargin: 0.15))
            .ToArray();

        evaluations.Select(e => e.Decision).Distinct()
            .Should().ContainSingle()
            .Which.Should().Be(
                ReviewLoopGateDecision.Escalate,
                "the bimodal check is first, so a high-but-bimodal split can never stop early");
        evaluations.Select(e => e.PAtOrAbove).Distinct().Should().ContainSingle();
        evaluations[0].PAtOrAbove.Should().BeApproximately(0.95, 1e-9);
    }

    /// <summary>
    /// The ordering is not a by-product of the gate's own <c>Accepted</c> flag: an outcome the gate
    /// itself marked <c>Accepted = true</c> still escalates when the distribution is bimodal,
    /// because <see cref="ReviewLoopStepExecutor.Evaluate"/> never reads <c>Accepted</c>.
    /// </summary>
    [Fact]
    public void A_gate_marked_Accepted_true_still_escalates_on_a_bimodal_split()
    {
        DecisionGateOutcome outcome = ScoreOutcome(
            accepted: true, ("7", 0.05), ("8", 0.20), ("9", 0.35), ("10", 0.40));

        ReviewLoopStepExecutor.Evaluate(outcome, minScore: 8, acceptAt: 0.85, bimodalMargin: 0.15)
            .Decision.Should().Be(ReviewLoopGateDecision.Escalate);
    }

    /// <summary>
    /// The exact arithmetic that makes the ordering load-bearing: remove the bimodal check and this
    /// same outcome would stop early, because <c>0.95 &gt;= 0.85</c>. Stated as an explicit
    /// counterfactual so the ordering cannot be dropped without a test going red.
    /// </summary>
    [Fact]
    public void The_accept_check_alone_would_have_stopped_early_on_that_same_distribution()
    {
        DecisionGateOutcome outcome = ScoreOutcome(
            accepted: false, ("7", 0.05), ("8", 0.20), ("9", 0.35), ("10", 0.40));

        ReviewLoopGateEvaluation evaluation =
            ReviewLoopStepExecutor.Evaluate(outcome, minScore: 8, acceptAt: 0.85, bimodalMargin: 0.15);

        evaluation.PAtOrAbove.Should().BeGreaterThanOrEqualTo(0.85);
        evaluation.Decision.Should().Be(ReviewLoopGateDecision.Escalate);
    }

    // ---- the boundary, exactly ON the margin ---------------------------

    /// <summary>
    /// A two-point distribution sitting exactly ON <c>BimodalMargin</c>. The comparison is a strict
    /// <c>(top - second) &lt; bimodalMargin</c>, so an exact landing is NOT bimodal. Values are
    /// chosen to be exactly representable in binary floating point
    /// (0.625 - 0.375 = 0.25 exactly), so this is a genuine boundary and not an artefact of
    /// rounding, and the result is asserted identical across repetitions.
    /// </summary>
    [Fact]
    public void A_two_point_split_exactly_on_BimodalMargin_is_not_bimodal_and_never_oscillates()
    {
        DecisionGateOutcome outcome = ScoreOutcome(accepted: false, ("9", 0.375), ("10", 0.625));

        ReviewLoopGateDecision[] decisions = Enumerable.Range(0, 500)
            .Select(_ => ReviewLoopStepExecutor.Evaluate(outcome, minScore: 9, acceptAt: 0.85, bimodalMargin: 0.25).Decision)
            .ToArray();

        decisions.Distinct().Should().ContainSingle()
            .Which.Should().Be(
                ReviewLoopGateDecision.StopEarly,
                "(top - second) == bimodalMargin is not '< bimodalMargin', so it is not bimodal; "
                + "pAtOrAbove is 1.0, which clears AcceptAt 0.85");

        // The immediately adjacent value, one representable step inside the boundary, is bimodal.
        DecisionGateOutcome justInside = ScoreOutcome(accepted: false, ("9", 0.3750000001), ("10", 0.6249999999));
        ReviewLoopStepExecutor.Evaluate(justInside, minScore: 9, acceptAt: 0.85, bimodalMargin: 0.25)
            .Decision.Should().Be(ReviewLoopGateDecision.Escalate);
    }

    /// <summary>
    /// The <c>AcceptAt</c> comparison is likewise strict — <c>pAtOrAbove &lt; acceptAt</c> escalates
    /// — so a <c>pAtOrAbove</c> landing exactly ON <c>AcceptAt</c> is ACCEPTED. Values are exactly
    /// representable (0.25 + 0.75 = 1.0; single-level 0.75), so the equality is exact.
    /// </summary>
    [Fact]
    public void A_pAtOrAbove_exactly_on_AcceptAt_is_accepted_and_a_hair_below_is_not()
    {
        // p(9) = 0.75 alone with MinScore 9 → pAtOrAbove = 0.75 exactly. Top two are 0.75/0.25,
        // a 0.50 gap, comfortably clear of the margin.
        DecisionGateOutcome outcome = ScoreOutcome(accepted: false, ("9", 0.25), ("10", 0.75));

        ReviewLoopStepExecutor.Evaluate(outcome, minScore: 10, acceptAt: 0.75, bimodalMargin: 0.25)
            .Decision.Should().Be(
                ReviewLoopGateDecision.StopEarly,
                "pAtOrAbove == acceptAt is not '< acceptAt', so it is accepted");

        ReviewLoopStepExecutor.Evaluate(outcome, minScore: 10, acceptAt: 0.7500000001, bimodalMargin: 0.25)
            .Decision.Should().Be(
                ReviewLoopGateDecision.Escalate,
                "pAtOrAbove < acceptAt escalates");
    }

    /// <summary>
    /// Contract as source: the bimodal comparison precedes the metric's threshold comparison inside
    /// the shared accept rule, and <see cref="ReviewLoopStepExecutor.Evaluate"/> implements neither
    /// comparison itself.
    ///
    /// <para>
    /// <b>This test moved with the rule (T-017).</b> The ordering used to live in
    /// <c>ReviewLoopStepExecutor.Evaluate</c> and was grepped from that file. The rule is now the one
    /// implementation every site <i>and</i> the calibration aggregation call — that is the point of
    /// T-017 — so the ordering lives in <c>DecisionAcceptRule.Escalates</c>. The executor is
    /// additionally pinned here as a delegator: a private copy of either comparison reappearing in
    /// the executor is exactly what would let the site's rule and the view's recomputation drift
    /// apart again, and it now fails this test rather than silently.
    /// </para>
    /// </summary>
    [Fact]
    public void The_bimodal_comparison_precedes_the_threshold_comparison_in_the_shared_rule()
    {
        string rule = ReadSource("ReelBolt.Shared", "Inference/DecisionAcceptRule.cs");

        int bimodal = rule.IndexOf("(finiteTop - finiteSecond) < minMargin", StringComparison.Ordinal);
        int accept = rule.IndexOf("return value < acceptAt;", StringComparison.Ordinal);

        bimodal.Should().BeGreaterThan(0, "the bimodal comparison must exist in DecisionAcceptRule.Escalates");
        accept.Should().BeGreaterThan(0, "the metric's threshold comparison must exist in DecisionAcceptRule.Escalates");
        bimodal.Should().BeLessThan(
            accept,
            "the bimodal check must run BEFORE the accept check (Review Focus, D-008)");

        string executor = ReadSource(
            "ReelBolt.WorkflowEngine", "Execution/StepExecutors/ReviewLoopStepExecutor.cs");

        executor.Should().Contain(
            "DecisionAcceptRule.Escalates(",
            "this site must apply the shared rule, not a private copy of it");
        executor.Should().NotContain(
            "< bimodalMargin",
            "this site must not re-implement the bimodal comparison");
        executor.Should().NotContain(
            ".Value < acceptAt",
            "this site must not re-implement the accept-threshold comparison");
    }

    /// <summary>Locates <c>src/&lt;project&gt;/&lt;relativePath&gt;</c> above the test assembly.</summary>
    private static string ReadSource(string project, string relativePath)
    {
        var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = System.IO.Path.Combine(
                directory.FullName, "src", project, relativePath);
            if (System.IO.File.Exists(candidate))
                return System.IO.File.ReadAllText(candidate);

            directory = directory.Parent;
        }

        throw new System.IO.FileNotFoundException(
            $"Could not locate src/{project}/{relativePath} above {AppContext.BaseDirectory}");
    }

    // ---- the whole executor path, repeatedly ---------------------------

    private static Mock<IReelBoltAgent> MockReviewer(
        string output = """{"score":3,"passesReview":false,"summary":"cut runs long"}""")
    {
        var agent = new Mock<IReelBoltAgent>();
        agent.Setup(a => a.Name).Returns(AgentType.VideoReviewAgent.ToString());
        agent.Setup(a => a.AgentType).Returns(AgentType.VideoReviewAgent);
        agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult
            {
                Output = output,
                Success = true,
                TokensUsed = 120,
                InputTokens = 90,
                OutputTokens = 30
            });
        return agent;
    }

    private static ReviewLoopStepExecutor CreateReviewLoopExecutor(
        Mock<IReelBoltAgent> agent, IDecisionGate gate)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent.Object);

        var accessor = new Mock<IWorkflowExecutionContextAccessor>();
        accessor.Setup(a => a.BeginScope(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>()))
            .Returns(Mock.Of<IDisposable>());
        accessor.Setup(a => a.Current).Returns((WorkflowExecutionContext?)null);

        return new ReviewLoopStepExecutor(
            registry.Object, accessor.Object, gate, NullLogger<ReviewLoopStepExecutor>.Instance);
    }

    private const string PriorCompileOutput =
        """
        {"segments":4,"outputDurationSec":12.5,"retainedRatio":0.61,"droppedSegmentsOverCap":0,
         "sentenceCheck":{"applicable":true,"endsAtSentenceBoundary":true}}
        """;

    private static StepExecutionContext CreateReviewLoopContext(
        string? reviewLoopConfigJson = """{"enabled":true}""",
        int? minScore = 8,
        string accumulatedOutput = PriorCompileOutput)
    {
        var loopTarget = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.VideoCompile,
            AgentDefinition = new AgentDefinition
            {
                Id = Guid.NewGuid(),
                AgentType = AgentType.VideoTransform,
                Name = "VideoTransform"
            }
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
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    private static Mock<IDecisionGate> ReviewLoopGateReturning(DecisionGateOutcome outcome)
    {
        var gate = new Mock<IDecisionGate>();
        gate.Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);
        gate.Setup(g => g.AttachSystemTwoAnswersAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return gate;
    }

    /// <summary>
    /// Acceptance criterion 4, end to end: on every one of many fresh runs of the SAME
    /// simultaneously-high-and-bimodal distribution, the reviewer runs (escalation) and the step
    /// loops back — never stop-early, never alternating.
    /// </summary>
    [Fact]
    public async Task A_high_and_bimodal_distribution_runs_the_reviewer_on_every_repetition()
    {
        const int repetitions = 25;

        for (int i = 0; i < repetitions; i++)
        {
            DecisionGateOutcome outcome = ScoreOutcome(
                accepted: false, ("7", 0.05), ("8", 0.20), ("9", 0.35), ("10", 0.40));

            Mock<IReelBoltAgent> reviewer = MockReviewer();
            Mock<IDecisionGate> gate = ReviewLoopGateReturning(outcome);
            StepExecutionContext context = CreateReviewLoopContext();

            StepExecutionResult result =
                await CreateReviewLoopExecutor(reviewer, gate.Object).ExecuteAsync(context);

            reviewer.Verify(
                a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
                Times.Once,
                $"repetition {i}: the gate must escalate, so the reviewer runs exactly once");

            result.Status.Should().Be(StepStatus.Completed);
            result.NextStepIndex.Should().Be(
                0, $"the loop target (StepOrder 2, AllSteps[0]) on repetition {i}");
            result.NewIterationCount.Should().Be(1);

            // The reviewer ran, so the gate's own observation is completed with its score — one call,
            // never a second decision call.
            gate.Verify(
                g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()),
                Times.Once);
            gate.Verify(
                g => g.AttachSystemTwoAnswersAsync(
                    It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                    It.IsAny<int?>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }
    }

    /// <summary>
    /// A hostile or nonsensical <c>ReviewLoopConfigJson</c> must be today's path — the reviewer
    /// runs — and must never throw out of the step or consult the gate.
    ///
    /// <para>
    /// <c>ReviewLoopStepExecutor.TryReadConfig</c> catches <see cref="JsonException"/> only, while
    /// its sibling in <c>ConditionalStepExecutor</c> catches every <see cref="Exception"/> with an
    /// explicit comment that "anything else a deserialiser could surface must not become this
    /// step's first-ever failure mode". This theory is the evidence for whether that asymmetry is
    /// reachable: every shape below is fed through the real executor.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("{ this is not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("3")]
    [InlineData("\"text\"")]
    [InlineData("{\"enabled\":\"yes\"}")]
    [InlineData("{\"enabled\":true,\"acceptAt\":\"high\"}")]
    [InlineData("{\"enabled\":true,\"bimodalMargin\":null}")]
    [InlineData("{\"enabled\":true,\"providerId\":\"not-a-guid\"}")]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"enabled\":false}")]
    [InlineData("{\"enabled\":true,\"timeoutSeconds\":\"five\"}")]
    public async Task A_hostile_review_loop_config_is_todays_path_and_never_throws(string? reviewLoopConfigJson)
    {
        Mock<IReelBoltAgent> reviewer = MockReviewer();
        Mock<IDecisionGate> gate = NeverCalledGate();

        StepExecutionResult result = await CreateReviewLoopExecutor(reviewer, gate.Object)
            .ExecuteAsync(CreateReviewLoopContext(reviewLoopConfigJson));

        reviewer.Verify(
            a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once,
            $"'{(reviewLoopConfigJson ?? "<null>")}' means today's path, so the reviewer runs");
        result.Status.Should().Be(StepStatus.Completed);
        result.ErrorDetails.Should().BeNull();

        gate.Invocations.Should().BeEmpty(
            $"'{(reviewLoopConfigJson ?? "<null>")}' must not reach the gate");
    }

    /// <summary>
    /// The same repetition discipline on the accepted side: a cleanly confident distribution skips
    /// the reviewer and does NOT loop back, every time.
    /// </summary>
    [Fact]
    public async Task A_confident_unimodal_distribution_skips_the_reviewer_on_every_repetition()
    {
        const int repetitions = 25;

        for (int i = 0; i < repetitions; i++)
        {
            DecisionGateOutcome outcome = ScoreOutcome(accepted: false, ("9", 0.25), ("10", 0.75));

            Mock<IReelBoltAgent> reviewer = MockReviewer();
            Mock<IDecisionGate> gate = ReviewLoopGateReturning(outcome);

            StepExecutionResult result = await CreateReviewLoopExecutor(reviewer, gate.Object)
                .ExecuteAsync(CreateReviewLoopContext());

            reviewer.Verify(
                a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
                Times.Never,
                $"repetition {i}: a confident unimodal answer stops early");

            result.NextStepIndex.Should().Be(2, $"advance past the ReviewLoop step on repetition {i}");
            result.NewIterationCount.Should().Be(0, "an early stop does not increment the iteration count");
            result.Status.Should().Be(StepStatus.Completed);

            // No reviewer ran, so nothing may be attached: attaching a score here would fabricate an
            // agreement about a reviewer that was deliberately skipped.
            gate.Verify(
                g => g.AttachSystemTwoAnswersAsync(
                    It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                    It.IsAny<int?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
    }

    // =====================================================================
    // 4a. Calibration aggregation against hostile and older-schema rows.
    // =====================================================================

    private static WorkflowEngineDbContext NewCalibrationDb() =>
        new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DecisionObservation Observation(
        string probabilitiesJson = "{\"a\":0.9,\"b\":0.1}",
        DecisionKind kind = DecisionKind.Choice,
        string answer = "a",
        double? confidence = null,
        string? outcomeJson = "{\"agreed\":true}",
        Guid? providerId = null,
        string? model = null,
        int? agentTokens = null,
        int? decisionInputTokens = null,
        int? decisionOutputTokens = null,
        string site = "Colorist") =>
        new()
        {
            Id = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            StepOrder = 1,
            AgentType = AgentType.Colorist,
            Site = site,
            Question = "Look",
            OptionsHash = "hash",
            Kind = kind,
            Answer = answer,
            Confidence = confidence,
            ProbabilitiesJson = probabilitiesJson,
            Mode = "Shadow",
            Escalated = false,
            DownstreamOutcomeJson = outcomeJson,
            ProviderId = providerId,
            Model = model,
            AgentTokensUsed = agentTokens,
            DecisionInputTokens = decisionInputTokens,
            DecisionOutputTokens = decisionOutputTokens,
            CreatedAt = DateTime.UtcNow,
        };

    private static async Task<DecisionCalibrationReport> CalibrateAsync(params DecisionObservation[] rows)
    {
        await using WorkflowEngineDbContext db = NewCalibrationDb();
        db.DecisionObservations.AddRange(rows);
        await db.SaveChangesAsync();

        return await new DecisionCalibrationService(db)
            .GetCalibrationAsync(0.85, 0.25, null, null, CancellationToken.None);
    }

    /// <summary>
    /// The hostile <c>ProbabilitiesJson</c> matrix. For each shape the stated outcome is: the row is
    /// <b>counted</b> in <c>ObservationCount</c> but <b>un-bucketed</b> (zero contribution to the
    /// reliability diagram) and — for a non-<c>Noul</c> kind — <b>not scoreable</b>, so it is also
    /// excluded from the escalation denominator. <b>Nothing throws.</b>
    /// </summary>
    [Theory]
    [InlineData("{}")]           // every Noul row, by construction
    [InlineData("{not json")]    // malformed / non-JSON
    [InlineData("[]")]           // valid JSON, but not an object
    [InlineData("3")]            // valid JSON, but not an object
    [InlineData("\"text\"")]     // valid JSON, but not an object
    [InlineData("null")]         // JSON null
    [InlineData("   ")]          // whitespace
    [InlineData("{\"a\":\"NaN\"}")] // object whose value is not a number
    public async Task Hostile_probabilities_json_is_counted_but_never_bucketed_and_never_throws(
        string probabilitiesJson)
    {
        DecisionCalibrationReport report = await CalibrateAsync(
            Observation(probabilitiesJson: probabilitiesJson, kind: DecisionKind.Choice));

        DecisionCalibrationSite site = report.Sites.Single();
        site.ObservationCount.Should().Be(1, "'{0}' is a counted row", probabilitiesJson);
        site.ReliabilityBuckets.Should().HaveCount(10);
        site.ReliabilityBuckets.Sum(b => b.Count).Should().Be(
            0, "'{0}' states no distribution, so it is un-bucketed, not invented", probabilitiesJson);
        site.MeanConfidence.Should().Be(0.0);
        site.EscalationRate.Should().Be(0.0);
        site.ReliabilityBuckets.SelectMany(b => new[] { b.ConfidenceIntervalLow, b.ConfidenceIntervalHigh })
            .Should().OnlyContain(v => !double.IsNaN(v), "an unbucketable row must never produce NaN");
    }

    /// <summary>
    /// A <c>Noul</c> row with <c>ProbabilitiesJson = "{}"</c> — the shape every Noul row has — is
    /// counted, is <b>scorable</b> (its whole distribution lives in <c>Answer</c>), takes its
    /// confidence from the shared top-two rule, and is still <b>un-bucketed</b>.
    /// </summary>
    [Fact]
    public async Task An_empty_probabilities_Noul_row_is_scored_from_its_answer_but_un_bucketed()
    {
        DecisionCalibrationReport report = await CalibrateAsync(
            Observation(probabilitiesJson: "{}", kind: DecisionKind.Noul, answer: "0.9"));

        DecisionCalibrationSite site = report.Sites.Single();
        site.ObservationCount.Should().Be(1);
        site.MeanConfidence.Should().BeApproximately(0.9, 1e-9, "top = max(0.9, 0.1)");
        site.ReliabilityBuckets.Sum(b => b.Count).Should().Be(0, "'{}' is not a stated distribution");
        site.EscalationRate.Should().Be(0.0, "top 0.9 clears AcceptAt 0.85 and the margin is 0.8");
    }

    /// <summary>
    /// A row written by the OLDER schema — no token columns at all, i.e. all three token fields
    /// null — is fully aggregated and contributes 0 tokens saved. Null means "not recorded", never
    /// zero, so an accepted row with no counterfactual cannot claim a saving.
    /// </summary>
    [Fact]
    public async Task An_older_schema_row_with_no_token_columns_is_aggregated_and_saves_nothing()
    {
        DecisionCalibrationReport report = await CalibrateAsync(
            Observation(
                probabilitiesJson: "{\"a\":0.9,\"b\":0.1}",
                kind: DecisionKind.Choice,
                answer: "a",
                outcomeJson: "{\"agreed\":true}",
                agentTokens: null,
                decisionInputTokens: null,
                decisionOutputTokens: null));

        DecisionCalibrationSite site = report.Sites.Single();
        site.ObservationCount.Should().Be(1);
        site.AgreementRate.Should().BeApproximately(1.0, 1e-9);
        report.Totals.TokensSaved.Should().Be(0, "the counterfactual was never recorded");
        report.Totals.TokensSavedIsProjected.Should().BeTrue();
    }

    /// <summary>
    /// Null <c>DecisionInputTokens</c>/<c>DecisionOutputTokens</c> on an accepted row that DOES have
    /// an <c>AgentTokensUsed</c>. The row never throws and is fully aggregated; the stated outcome
    /// is that <c>TokensSaved</c> is <b>zero</b>, because the cost basis is incomplete.
    ///
    /// <para>
    /// <b>This test used to pin the opposite</b> — the full 500, via <c>row.DecisionInputTokens ?? 0</c>
    /// — with a comment saying it was stated but not endorsed. QA-11 charged it, and the fix landed
    /// here: the projection now needs all three columns before subtracting anything, so the same
    /// "null means not recorded, never zero" contract the three columns document holds in the
    /// aggregation too. The old arithmetic OVERSTATED the figure in the direction a reader acts on —
    /// it credited the agent's whole cost as saved whenever the decision's own usage was missing.
    /// An unrecorded basis now understates instead, which is the safe direction.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Null_decision_token_counts_leave_the_basis_unrecorded_and_save_nothing()
    {
        DecisionCalibrationReport report = await CalibrateAsync(
            Observation(
                probabilitiesJson: "{\"a\":0.95,\"b\":0.05}",
                kind: DecisionKind.Choice,
                answer: "a",
                outcomeJson: "{\"agreed\":true}",
                agentTokens: 500,
                decisionInputTokens: null,
                decisionOutputTokens: null));

        report.Totals.TokensSaved.Should().Be(
            0,
            "two of the three cost columns were not recorded, so the row cannot claim a saving");
        report.Totals.TokensSavedIsProjected.Should().BeTrue();
        report.Sites.Single().EscalationRate.Should().Be(0.0);
        report.Sites.Single().ObservationCount.Should().Be(1);
    }

    /// <summary>
    /// A null (or malformed, or <c>agreed</c>-less) outcome is <b>excluded from the agreement
    /// denominator</b> rather than counted as a disagreement — the same "no observation is not a
    /// disagreement" rule the gate's null <c>SystemTwoAnswer</c> relies on.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("{\"other\":true}")]
    [InlineData("{\"agreed\":\"yes\"}")]
    public async Task A_row_with_no_usable_outcome_is_excluded_from_the_agreement_denominator(
        string? outcomeJson)
    {
        DecisionCalibrationReport report = await CalibrateAsync(
            Observation(outcomeJson: outcomeJson),
            Observation(outcomeJson: "{\"agreed\":true}"),
            Observation(outcomeJson: "{\"agreed\":false}"));

        DecisionCalibrationSite site = report.Sites.Single();
        site.ObservationCount.Should().Be(3);
        site.AgreementCount.Should().Be(1);
        site.AgreementRate.Should().BeApproximately(
            0.5, 1e-9, "'{0}' testifies about nothing, so it is out of the denominator, not a disagreement",
            outcomeJson ?? "<null>");
    }

    /// <summary>
    /// All of the above at once, over one site, asserting only that the call returns and produces
    /// ten well-formed buckets. This is the "does the aggregation throw on hostile data" question
    /// asked directly.
    /// </summary>
    [Fact]
    public async Task The_aggregation_survives_every_hostile_shape_in_one_call()
    {
        DecisionCalibrationReport report = await CalibrateAsync(
            Observation(probabilitiesJson: "{}", kind: DecisionKind.Noul, answer: "0.9", outcomeJson: null),
            Observation(probabilitiesJson: "{not json", outcomeJson: null),
            Observation(probabilitiesJson: "[]", outcomeJson: "{\"agreed\":true}"),
            Observation(probabilitiesJson: "null", outcomeJson: "{broken"),
            Observation(probabilitiesJson: "{\"a\":0.9,\"b\":0.1}", outcomeJson: null,
                agentTokens: null, decisionInputTokens: null, decisionOutputTokens: null),
            Observation(probabilitiesJson: "{}", kind: DecisionKind.Score, answer: "not-a-number",
                outcomeJson: "{\"agreed\":false}"),
            Observation(probabilitiesJson: "{\"a\":0.5,\"b\":0.5}", outcomeJson: "{\"agreed\":true}",
                agentTokens: 100, decisionInputTokens: 10, decisionOutputTokens: 5));

        report.Totals.ObservationCount.Should().Be(7);
        report.Sites.Single().ObservationCount.Should().Be(7);
        report.Sites.Single().ReliabilityBuckets.Should().HaveCount(10);
        report.Sites.Single().ReliabilityBuckets.Should().OnlyContain(b => !double.IsNaN(b.ObservedAgreementRate));
        report.Totals.TokensSaved.Should().BeGreaterThanOrEqualTo(0);
    }

    // =====================================================================
    // 4b. The endpoint's own parameters.
    // =====================================================================

    private const string TestAuthenticationType = "TestAuth";

    private static DecisionCalibrationController CreateCalibrationController(
        IDecisionCalibrationService service, bool isAdmin = true)
    {
        var identity = new System.Security.Claims.ClaimsIdentity(
            isAdmin ? [new System.Security.Claims.Claim("isAdmin", "true")] : [],
            TestAuthenticationType);

        return new DecisionCalibrationController(service)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(identity)
                }
            }
        };
    }

    private static Mock<IDecisionCalibrationService> CapturingService(out List<(double AcceptAt, double MinMargin, Guid? ProviderId, string? Site)> captures)
    {
        var local = new List<(double AcceptAt, double MinMargin, Guid? ProviderId, string? Site)>();
        captures = local;

        var service = new Mock<IDecisionCalibrationService>();
        service
            .Setup(s => s.GetCalibrationAsync(
                It.IsAny<double>(), It.IsAny<double>(), It.IsAny<Guid?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<double, double, Guid?, string?, CancellationToken>(
                (acceptAt, minMargin, providerId, site, _) => local.Add((acceptAt, minMargin, providerId, site)))
            .ReturnsAsync(new DecisionCalibrationReport());

        return service;
    }

    /// <summary>
    /// <c>acceptAt</c> and <c>minMargin</c> are the two parameters the endpoint range-checks. Every
    /// out-of-range and non-finite value returns <b>400</b> with the service never called — nothing
    /// is clamped and nothing is substituted.
    /// </summary>
    [Theory]
    [InlineData(1.0000000001)]
    [InlineData(1.5)]
    [InlineData(-0.0000000001)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task An_out_of_range_or_non_finite_acceptAt_is_400_and_never_reaches_the_service(
        double acceptAt)
    {
        Mock<IDecisionCalibrationService> service = CapturingService(out _);
        DecisionCalibrationController controller = CreateCalibrationController(service.Object);

        Microsoft.AspNetCore.Mvc.ActionResult<DecisionCalibrationReport> result =
            await controller.Get(acceptAt, null, null, null, CancellationToken.None);

        result.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>();
        service.Invocations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(1.0000000001)]
    [InlineData(-0.0000000001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task An_out_of_range_or_non_finite_minMargin_is_400_and_never_reaches_the_service(
        double minMargin)
    {
        Mock<IDecisionCalibrationService> service = CapturingService(out _);
        DecisionCalibrationController controller = CreateCalibrationController(service.Object);

        Microsoft.AspNetCore.Mvc.ActionResult<DecisionCalibrationReport> result =
            await controller.Get(null, minMargin, null, null, CancellationToken.None);

        result.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>();
        service.Invocations.Should().BeEmpty();
    }

    /// <summary>
    /// The inclusive boundaries <c>0</c> and <c>1</c> are accepted (200) and passed through
    /// <b>unchanged</b> — <c>0</c> is a value, not an absent parameter, so it must not be replaced
    /// by the default.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public async Task Acceptance_boundaries_are_passed_through_unchanged_not_substituted(double value)
    {
        Mock<IDecisionCalibrationService> service = CapturingService(out List<(double AcceptAt, double MinMargin, Guid? ProviderId, string? Site)> captures);
        DecisionCalibrationController controller = CreateCalibrationController(service.Object);

        Microsoft.AspNetCore.Mvc.ActionResult<DecisionCalibrationReport> result =
            await controller.Get(value, value, null, null, CancellationToken.None);

        result.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>();
        captures.Should().ContainSingle();
        captures[0].AcceptAt.Should().Be(value);
        captures[0].MinMargin.Should().Be(value);
    }

    /// <summary>
    /// <c>providerId</c> and <c>site</c> have no validation at all — by design, an unknown value
    /// "simply matches nothing rather than erroring". This asserts the pass-through for a set of
    /// values that a hostile caller could supply as a <b>bound</b> value.
    ///
    /// <para>
    /// <b>What is NOT covered here:</b> a <c>providerId</c> query string that is not a GUID at all
    /// (e.g. <c>?providerId=notaguid</c>). That is rejected by MVC model binding <i>before</i> this
    /// action runs, and this repository has no <c>WebApplicationFactory</c>/<c>TestHost</c> harness
    /// (see <c>docs/ci.md</c>), so the status code it produces cannot be observed by a test in this
    /// project. See the risk note in the ticket report.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_unknown_providerId_and_site_are_passed_through_and_match_nothing()
    {
        Mock<IDecisionCalibrationService> service = CapturingService(out List<(double AcceptAt, double MinMargin, Guid? ProviderId, string? Site)> captures);
        DecisionCalibrationController controller = CreateCalibrationController(service.Object);

        Guid unknownProvider = Guid.NewGuid();
        Microsoft.AspNetCore.Mvc.ActionResult<DecisionCalibrationReport> result =
            await controller.Get(null, null, unknownProvider, "No.Such.Site", CancellationToken.None);

        result.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>();
        captures.Should().ContainSingle();
        captures[0].ProviderId.Should().Be(unknownProvider);
        captures[0].Site.Should().Be("No.Such.Site");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../../../etc/passwd")]
    [InlineData("site; DROP TABLE decision_observations")]
    public async Task An_out_of_range_site_string_is_passed_through_verbatim(string site)
    {
        Mock<IDecisionCalibrationService> service = CapturingService(out List<(double AcceptAt, double MinMargin, Guid? ProviderId, string? Site)> captures);
        DecisionCalibrationController controller = CreateCalibrationController(service.Object);

        Microsoft.AspNetCore.Mvc.ActionResult<DecisionCalibrationReport> result =
            await controller.Get(null, null, null, site, CancellationToken.None);

        result.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>(
            "site is not validated, so any string is a 200 that matches nothing");
        captures.Should().ContainSingle();
        captures[0].Site.Should().Be(site);
    }

    /// <summary>
    /// <see cref="Guid.Empty"/> is a legal (if meaningless) <c>providerId</c>: it is a value, so it
    /// is passed through rather than treated as absent.
    /// </summary>
    [Fact]
    public async Task A_Guid_Empty_providerId_is_a_value_not_an_absent_parameter()
    {
        Mock<IDecisionCalibrationService> service = CapturingService(out List<(double AcceptAt, double MinMargin, Guid? ProviderId, string? Site)> captures);
        DecisionCalibrationController controller = CreateCalibrationController(service.Object);

        Microsoft.AspNetCore.Mvc.ActionResult<DecisionCalibrationReport> result =
            await controller.Get(null, null, Guid.Empty, null, CancellationToken.None);

        result.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>();
        captures.Should().ContainSingle();
        captures[0].ProviderId.Should().Be(Guid.Empty);
    }
}
