using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Coverage for the semantic (Decision-mode) <see cref="ConditionalStepExecutor"/>: a `Noul`
/// question asked over a bounded view of the prior step's output, then today's branch resolution
/// driven by either the caller's <c>ConditionExpression</c> or the step's own `Threshold`.
///
/// <para>
/// The two load-bearing properties here are (a) <c>Mode = Expression</c>, a null config and an
/// unparseable config are ALL today's exact path — asserted against a fully-specified
/// <see cref="StepExecutionResult"/>, not merely "it returned false" — and (b) every failure in
/// the Decision arm (no provider, a throwing gate, a missing answer) degrades to `false` and
/// <see cref="StepStatus.Completed"/> rather than throwing or failing the step.
/// </para>
/// </summary>
public sealed class ConditionalStepDecisionModeTests
{
    private const string QuestionName = "should-promote";

    // ---------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------

    private static StepExecutionContext CreateContext(
        string? conditionExpression,
        string? conditionalConfigJson,
        string accumulatedOutput = """{"score":9}""",
        string? trueBranchStepOrder = "2",
        string? falseBranchStepOrder = "3",
        int iterationCount = 3)
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
            TrueBranchStepOrder = trueBranchStepOrder,
            FalseBranchStepOrder = falseBranchStepOrder,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = AgentType.ExtractTransform, Name = "ExtractTransform" }
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
            IterationCount = iterationCount,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    private static ConditionalStepExecutor CreateExecutor(IDecisionGate gate) =>
        new(NullLogger<ConditionalStepExecutor>.Instance, gate);

    /// <summary>
    /// A <see cref="DecisionGateOutcome"/> carrying one answered `Noul` question under
    /// <paramref name="questionName"/> (the name the executor asked under), exactly the shape
    /// <c>DecisionGate</c> builds for a single answered question.
    /// </summary>
    private static DecisionGateOutcome AnsweredNoul(
        double pYes, bool accepted = true, string questionName = QuestionName)
    {
        var answer = new DecisionAnswer(
            questionName, DecisionKind.Noul, null, null, pYes, null, new Dictionary<string, double>());

        var result = new DecisionResult(
            "test-model",
            new Dictionary<string, DecisionAnswer> { [questionName] = answer },
            11,
            4);

        return new DecisionGateOutcome(
            HasDecision: true,
            Accepted: accepted,
            Result: result,
            TopTwoProbabilities: new Dictionary<string, (double Top, double Second)>
            {
                [questionName] = (Math.Max(pYes, 1 - pYes), Math.Min(pYes, 1 - pYes))
            });
    }

    private static Mock<IDecisionGate> GateReturning(DecisionGateOutcome? outcome)
    {
        var gate = new Mock<IDecisionGate>();
        gate.Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);
        return gate;
    }

    private static string DecisionConfig(
        string? decisionQuestion = QuestionName,
        double threshold = 0.5,
        string mode = "Decision") =>
        JsonSerializer.Serialize(new
        {
            version = 1,
            mode,
            decisionQuestion,
            threshold,
            providerId = (Guid?)null,
            timeoutSeconds = 5
        });

    // ---------------------------------------------------------------------
    // Decision mode — the branch follows the model's answer
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Decision_mode_takes_the_true_branch_when_the_expression_holds()
    {
        var gate = GateReturning(AnsweredNoul(0.9));
        var context = CreateContext("decision.p_yes >= 0.65", DecisionConfig());

        StepExecutionResult result = await CreateExecutor(gate.Object).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        result.NextStepIndex.Should().Be(1, "the True branch is the step at index 1 (StepOrder 2)");
    }

    [Fact]
    public async Task Decision_mode_takes_the_false_branch_when_the_expression_does_not_hold()
    {
        var gate = GateReturning(AnsweredNoul(0.1));
        var context = CreateContext("decision.p_yes >= 0.65", DecisionConfig());

        StepExecutionResult result = await CreateExecutor(gate.Object).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        result.NextStepIndex.Should().Be(2, "the False branch is the step at index 2 (StepOrder 3)");
    }

    [Fact]
    public async Task Decision_mode_derives_the_threshold_comparison_when_the_expression_is_blank()
    {
        var gate = GateReturning(AnsweredNoul(0.7, questionName: ConditionalStepExecutor.ThresholdQuestionName));
        var context = CreateContext(null, DecisionConfig(decisionQuestion: null, threshold: 0.6));

        StepExecutionResult result = await CreateExecutor(gate.Object).ExecuteAsync(context);

        result.NextStepIndex.Should().Be(1, "p_yes 0.7 >= Threshold 0.6");
    }

    [Fact]
    public async Task Derived_threshold_comparison_falls_to_the_false_branch_below_the_threshold()
    {
        var gate = GateReturning(AnsweredNoul(0.4, questionName: ConditionalStepExecutor.ThresholdQuestionName));
        var context = CreateContext("   ", DecisionConfig(decisionQuestion: null, threshold: 0.6));

        StepExecutionResult result = await CreateExecutor(gate.Object).ExecuteAsync(context);

        result.NextStepIndex.Should().Be(2, "p_yes 0.4 < Threshold 0.6");
    }

    [Fact]
    public async Task Decision_mode_exposes_p_no_confidence_and_escalated_to_the_expression()
    {
        // p_yes 0.2 means p_no 0.8, and top = max(0.2, 0.8) = 0.8 — a confident "no".
        var gate = GateReturning(AnsweredNoul(0.2, accepted: true));
        var context = CreateContext(
            "decision.p_no >= 0.75 && decision.confidence > 0.5 && decision.escalated == false",
            DecisionConfig());

        StepExecutionResult result = await CreateExecutor(gate.Object).ExecuteAsync(context);

        result.NextStepIndex.Should().Be(1);
    }

    [Fact]
    public async Task An_escalated_decision_is_visible_to_the_expression_as_true()
    {
        var gate = GateReturning(AnsweredNoul(0.9, accepted: false));
        var context = CreateContext("decision.escalated", DecisionConfig());

        StepExecutionResult result = await CreateExecutor(gate.Object).ExecuteAsync(context);

        result.NextStepIndex.Should().Be(1, "escalated is true, so the True branch is taken");
    }

    [Fact]
    public async Task Decision_mode_still_reads_json_parameters_from_the_accumulated_output()
    {
        var gate = GateReturning(AnsweredNoul(0.9));
        var context = CreateContext(
            "$.score > 8 && decision.p_yes >= 0.65",
            DecisionConfig(),
            accumulatedOutput: """{"score":9}""");

        StepExecutionResult result = await CreateExecutor(gate.Object).ExecuteAsync(context);

        result.NextStepIndex.Should().Be(1);
    }

    [Fact]
    public async Task Decision_mode_preserves_output_iteration_count_and_status()
    {
        var gate = GateReturning(AnsweredNoul(0.9));
        var context = CreateContext("decision.p_yes >= 0.65", DecisionConfig());

        StepExecutionResult result = await CreateExecutor(gate.Object).ExecuteAsync(context);

        result.Output.Should().Be(context.AccumulatedOutput);
        result.NewIterationCount.Should().Be(3);
        result.TokensUsed.Should().Be(0);
        result.ErrorDetails.Should().BeNull();
        result.Status.Should().Be(StepStatus.Completed);
    }

    // ---------------------------------------------------------------------
    // Decision mode — the request the gate receives
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Decision_mode_asks_one_noul_question_at_the_gate_mode_and_site()
    {
        DecisionGateRequest? captured = null;
        var gate = new Mock<IDecisionGate>();
        gate.Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DecisionGateRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(AnsweredNoul(0.9));

        var context = CreateContext("decision.p_yes >= 0.65", DecisionConfig(), iterationCount: 7);
        await CreateExecutor(gate.Object).ExecuteAsync(context);

        captured.Should().NotBeNull();
        captured!.Config.Mode.Should().Be(DecisionGateMode.Gate);
        captured.ExecutionId.Should().Be(context.Execution.Id);
        captured.StepOrder.Should().Be(1);
        captured.AgentType.Should().Be(AgentType.ExtractTransform);
        captured.Questions.Should().HaveCount(1);
        captured.Questions[0].Kind.Should().Be(DecisionKind.Noul);
        captured.Questions[0].Name.Should().Be(QuestionName);
        captured.Questions[0].Site.Should().NotBeNullOrWhiteSpace();
        captured.Questions[0].Options.Should().BeEmpty();

        // No agent has run on a pre-decision Gate path, so there is no counterfactual. Null — the
        // gate's "no agreement observed" value, which leaves the observation claiming nothing —
        // and deliberately not an empty string, which is a *value* and would be compared against
        // the model's answer as a disagreement that was never observed.
        captured.Questions[0].SystemTwoAnswer.Should().BeNull();

        captured.State.Content.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Decision_mode_declares_the_affirmative_metric_and_needs_no_cutoff()
    {
        // T-017: this site decides on the AFFIRMATIVE probability, never on the top of the two-point
        // set TopTwo derives from it. Declaring it is what stops the calibration view recomputing this
        // row with the universal top/margin rule and reporting "confident and accepted" on exactly the
        // answers this step rejects (p_yes 0.10 has a top of 0.90).
        DecisionGateRequest? captured = null;
        var gate = new Mock<IDecisionGate>();
        gate.Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DecisionGateRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(AnsweredNoul(0.9));

        await CreateExecutor(gate.Object).ExecuteAsync(CreateContext("decision.p_yes >= 0.65", DecisionConfig()));

        DecisionGateQuestion question = captured!.Questions.Should().ContainSingle().Subject;

        question.AcceptMetric.Should().Be(DecisionAcceptMetric.Affirmative);
        question.ScoreCutoff.Should().BeNull("the affirmative comparison is against the threshold directly");
    }

    [Fact]
    public async Task Decision_mode_derives_the_question_name_from_the_expression_when_unset()
    {
        DecisionGateRequest? captured = null;
        var gate = new Mock<IDecisionGate>();
        gate.Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DecisionGateRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(AnsweredNoul(0.9));

        var context = CreateContext("decision.p_yes >= 0.65", DecisionConfig(decisionQuestion: null));
        await CreateExecutor(gate.Object).ExecuteAsync(context);

        captured!.Questions[0].Name.Should().Contain("0.65");
    }

    // ---------------------------------------------------------------------
    // The bounded state — the 4k-token context constraint applies to every Gate path
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Decision_mode_sends_a_bounded_view_never_the_raw_accumulated_output()
    {
        DecisionGateRequest? captured = null;
        var gate = new Mock<IDecisionGate>();
        gate.Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DecisionGateRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(AnsweredNoul(0.9));

        string huge = JsonSerializer.Serialize(new
        {
            score = 9,
            steps = Enumerable.Range(0, 500).Select(i => new { order = i, output = new string('x', 900) }).ToArray()
        });
        huge.Length.Should().BeGreaterThan(400_000);

        var context = CreateContext("decision.p_yes >= 0.65", DecisionConfig(), accumulatedOutput: huge);
        await CreateExecutor(gate.Object).ExecuteAsync(context);

        captured!.State.Content.Length.Should().BeLessThanOrEqualTo(ConditionalStepExecutor.MaxStateChars);
        captured.State.Content.Length.Should().BeLessThan(huge.Length);
    }

    [Fact]
    public async Task Bounded_view_keeps_a_small_output_verbatim_and_valid()
    {
        DecisionGateRequest? captured = null;
        var gate = new Mock<IDecisionGate>();
        gate.Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DecisionGateRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(AnsweredNoul(0.9));

        const string small = """{"score":9,"kept":"yes"}""";
        var context = CreateContext("decision.p_yes >= 0.65", DecisionConfig(), accumulatedOutput: small);
        await CreateExecutor(gate.Object).ExecuteAsync(context);

        captured!.State.Content.Should().Be(small);
    }

    // ---------------------------------------------------------------------
    // Fail closed, fail soft — every gate failure is `false`, never an exception
    // ---------------------------------------------------------------------

    [Fact]
    public async Task No_decision_from_the_gate_evaluates_false_and_still_completes()
    {
        var gate = new Mock<IDecisionGate>();
        gate.Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DecisionGateOutcome.NoDecision);

        var context = CreateContext("decision.p_yes >= 0.65", DecisionConfig());

        StepExecutionResult result = await CreateExecutor(gate.Object).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        result.NextStepIndex.Should().Be(2, "false falls to the False branch");
        result.Output.Should().Be(context.AccumulatedOutput);
        result.NewIterationCount.Should().Be(3);
    }

    [Fact]
    public async Task A_null_outcome_from_the_gate_evaluates_false_and_still_completes()
    {
        var context = CreateContext("decision.p_yes >= 0.65", DecisionConfig());

        StepExecutionResult result = await CreateExecutor(GateReturning(null).Object).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        result.NextStepIndex.Should().Be(2);
    }

    [Fact]
    public async Task A_gate_that_throws_evaluates_false_and_still_completes()
    {
        var gate = new Mock<IDecisionGate>();
        gate.Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider exploded"));

        var context = CreateContext("decision.p_yes >= 0.65", DecisionConfig());

        StepExecutionResult result = await CreateExecutor(gate.Object).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        result.NextStepIndex.Should().Be(2);
    }

    [Fact]
    public async Task An_unanswered_question_evaluates_false_and_still_completes()
    {
        var result = new DecisionResult(
            "test-model", new Dictionary<string, DecisionAnswer>(), 1, 1);

        var outcome = new DecisionGateOutcome(
            true, false, result, new Dictionary<string, (double, double)>());

        var context = CreateContext("decision.p_yes >= 0.65", DecisionConfig());

        StepExecutionResult stepResult = await CreateExecutor(GateReturning(outcome).Object).ExecuteAsync(context);

        stepResult.Status.Should().Be(StepStatus.Completed);
        stepResult.NextStepIndex.Should().Be(2);
    }

    [Fact]
    public async Task A_noul_answer_without_a_probability_evaluates_false_and_still_completes()
    {
        var answer = new DecisionAnswer(
            QuestionName, DecisionKind.Noul, null, null, Noul: null, null, new Dictionary<string, double>());
        var result = new DecisionResult(
            "test-model", new Dictionary<string, DecisionAnswer> { [QuestionName] = answer }, 1, 1);
        var outcome = new DecisionGateOutcome(
            true, false, result, new Dictionary<string, (double, double)> { [QuestionName] = (0.0, 0.0) });

        var context = CreateContext("decision.p_yes >= 0.65", DecisionConfig());

        StepExecutionResult stepResult = await CreateExecutor(GateReturning(outcome).Object).ExecuteAsync(context);

        stepResult.Status.Should().Be(StepStatus.Completed);
        stepResult.NextStepIndex.Should().Be(2);
    }

    // ---------------------------------------------------------------------
    // Mode = Expression / null config / unparseable config — today's exact path
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Expression_mode_is_byte_identical_to_todays_executor()
    {
        var gate = new Mock<IDecisionGate>();
        var context = CreateContext("$.score > 8", DecisionConfig(mode: "Expression"));

        StepExecutionResult result = await CreateExecutor(gate.Object).ExecuteAsync(context);

        result.Output.Should().Be("""{"score":9}""");
        result.NextStepIndex.Should().Be(1);
        result.NewIterationCount.Should().Be(3);
        result.TokensUsed.Should().Be(0);
        result.InputTokens.Should().BeNull();
        result.OutputTokens.Should().BeNull();
        result.DurationMs.Should().Be(0);
        result.AttemptCount.Should().Be(1);
        result.RetryCount.Should().Be(0);
        result.ToolCalls.Should().BeEmpty();
        result.Reasoning.Should().BeEmpty();
        result.Status.Should().Be(StepStatus.Completed);
        result.ErrorDetails.Should().BeNull();
        result.IterationNumber.Should().BeNull();
        result.OutputStorageKey.Should().BeNull();
        result.ArtifactStorageKey.Should().BeNull();

        gate.Verify(
            g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_null_config_takes_todays_expression_path_and_never_asks_the_gate()
    {
        var gate = new Mock<IDecisionGate>();
        var context = CreateContext("$.score > 8", conditionalConfigJson: null);

        StepExecutionResult result = await CreateExecutor(gate.Object).ExecuteAsync(context);

        result.NextStepIndex.Should().Be(1);
        result.Status.Should().Be(StepStatus.Completed);
        gate.Verify(
            g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task An_unparseable_config_takes_todays_expression_path_and_never_asks_the_gate()
    {
        var gate = new Mock<IDecisionGate>();
        var context = CreateContext("$.score > 8", conditionalConfigJson: "{ this is not json");

        StepExecutionResult result = await CreateExecutor(gate.Object).ExecuteAsync(context);

        result.NextStepIndex.Should().Be(1);
        result.Status.Should().Be(StepStatus.Completed);
        gate.Verify(
            g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_decision_reference_in_expression_mode_is_false_not_an_exception()
    {
        var gate = new Mock<IDecisionGate>();
        var context = CreateContext("decision.p_yes >= 0.65", conditionalConfigJson: null);

        StepExecutionResult result = await CreateExecutor(gate.Object).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        result.NextStepIndex.Should().Be(2, "an unresolvable identifier is false, never an exception");
    }

    [Fact]
    public async Task An_unknown_true_branch_step_order_falls_through_to_the_next_step()
    {
        var gate = GateReturning(AnsweredNoul(0.9));
        var context = CreateContext(
            "decision.p_yes >= 0.65", DecisionConfig(), trueBranchStepOrder: "99");

        StepExecutionResult result = await CreateExecutor(gate.Object).ExecuteAsync(context);

        result.NextStepIndex.Should().Be(1, "CurrentStepIndex + 1 when the branch step does not exist");
    }
}
