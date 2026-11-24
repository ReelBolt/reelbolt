using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.WorkflowEngine.Execution.Context;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.Snapshots;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// Evaluates a condition expression and branches to true/false step order.
///
/// <para>
/// Two modes, carried by <see cref="WorkflowStep.ConditionalConfigJson"/>:
/// <see cref="ConditionMode.Expression"/> (the default, and what a null/unparseable config means)
/// is the original NCalc-only path; <see cref="ConditionMode.Decision"/> asks a System One
/// <c>Noul</c> question over a <b>bounded</b> view of the prior step's output first, then runs the
/// same expression with that decision's parameters in scope.
/// </para>
///
/// <para>
/// <b>The Decision arm can never fail this step.</b> No Decision provider configured, a gate-level
/// timeout, a transport error, an answer the model simply did not give — every one of them is
/// logged and evaluates to <c>false</c>, i.e. the False branch, which is exactly the branch an
/// unresolvable identifier already takes in the Expression path. The step keeps today's
/// <c>Output</c>, <c>NewIterationCount</c>, <c>Status</c> and <c>FindIndex</c>-based branch
/// resolution unchanged. The one thing that still propagates is the workflow's own cancellation,
/// which must never be mistaken for a gate failure (same rule as <c>DecisionGate.DecideAsync</c>).
/// </para>
/// </summary>
public class ConditionalStepExecutor : IStepExecutor
{
    /// <summary>
    /// The site recorded on the decision observation, so every Conditional step's decisions are
    /// groupable in the calibration view. Per-question <c>Site</c> is what lands in the observation
    /// row (see <see cref="DecisionGateQuestion"/>); this constant is that value.
    /// </summary>
    internal const string DecisionSite = "Conditional.Decision";

    /// <summary>
    /// The question name used when neither <c>DecisionQuestion</c> nor <c>ConditionExpression</c>
    /// supplies one — i.e. the step decides purely on
    /// <c>decision.p_yes &gt;= Threshold</c>.
    /// </summary>
    internal const string ThresholdQuestionName = "Conditional.Threshold";

    /// <summary>
    /// Cap, in characters, on the state sent to the Decision provider. 12 000 chars is the same
    /// per-step figure <c>WorkflowEngine:ContextBudget:MaxDigestedStepChars</c> already uses, and at
    /// the codebase's ~4-chars-per-token rule of thumb it keeps the whole state comfortably inside
    /// the plan's 4k-token Decision-context constraint.
    ///
    /// <para>
    /// This matters because the Decision arm's natural input — <c>AccumulatedOutput</c> — is
    /// unbounded and grows across a pipeline, whereas a Shadow call already had a
    /// prompt-budgeted agent input to work from. Every new Gate path must bound what it sends; this
    /// is that bound for the Conditional step.
    /// </para>
    /// </summary>
    internal const int MaxStateChars = 12_000;

    /// <summary>
    /// The digest caps applied by <see cref="JsonOutputDigest"/>. Mirrors
    /// <c>AgentInputBudgetOptions</c>' own defaults so the bounded view an agent would have seen and
    /// the bounded view a decision model sees are shaped by the same numbers.
    /// </summary>
    private static readonly JsonDigestSettings StateDigest = new(
        MaxOutputChars: MaxStateChars,
        MaxStringChars: 600,
        MaxArrayItems: 25,
        MaxDepth: 12);

    // Enum values in ConditionalConfigJson use the exact C# member name (e.g. "Expression",
    // "Decision"), matching ExtractStepExecutor's convention and the frontend's ConditionMode TS
    // union. Property names still follow JsonSerializerDefaults.Web (camelCase).
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILogger<ConditionalStepExecutor> _logger;
    private readonly IDecisionGate _decisionGate;

    public ConditionalStepExecutor(ILogger<ConditionalStepExecutor> logger, IDecisionGate decisionGate)
    {
        _logger = logger;
        _decisionGate = decisionGate;
    }

    public StepType StepType => StepType.Conditional;

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        StepExecutionResult result = await ResolveBranchAsync(context);
        return result;
    }

    private async Task<StepExecutionResult> ResolveBranchAsync(StepExecutionContext context)
    {
        ConditionalStepConfig? config = TryReadConfig(context.Step.ConditionalConfigJson);
        string evaluatedOutput = ResolveEvaluatedOutput(context, config);

        bool decision;
        if (config is null || config.Mode == ConditionMode.Expression)
        {
            // Today's exact path — reached by a null config, an unparseable config, an explicit
            // Mode = Expression, AND an unrecognised Mode value (the switch is exhaustive and this
            // is its fallback, not "not Decision").
            decision = EvaluateExpression(context, evaluatedOutput);
        }
        else if (config.Mode == ConditionMode.Decision)
        {
            decision = await EvaluateDecisionAsync(context, config, evaluatedOutput);
        }
        else
        {
            _logger.LogWarning(
                "Conditional step {StepOrder}: unrecognised ConditionMode {Mode}; evaluating the expression path (no decision provider will be called)",
                context.Step.StepOrder,
                (int)config.Mode);
            decision = EvaluateExpression(context, evaluatedOutput);
        }

        int nextIndex;
        string? branch = decision ? context.Step.TrueBranchStepOrder : context.Step.FalseBranchStepOrder;
        if (IsEndToken(branch))
        {
            // docs/design/app-inputs.md 9.4: "end" finishes the run here (the loop ends at AllSteps.Count).
            nextIndex = context.AllSteps.Count;
        }
        else if (decision && context.Step.TrueBranchStepOrder != null
            && int.TryParse(context.Step.TrueBranchStepOrder, out int trueOrder))
        {
            nextIndex = context.AllSteps.FindIndex(s => s.StepOrder == trueOrder);
        }
        else if (!decision && context.Step.FalseBranchStepOrder != null
            && int.TryParse(context.Step.FalseBranchStepOrder, out int falseOrder))
        {
            nextIndex = context.AllSteps.FindIndex(s => s.StepOrder == falseOrder);
        }
        else
        {
            nextIndex = context.CurrentStepIndex + 1;
        }

        if (nextIndex < 0) nextIndex = context.CurrentStepIndex + 1;

        return new StepExecutionResult
        {
            Output = context.AccumulatedOutput,
            NextStepIndex = nextIndex,
            NewIterationCount = context.IterationCount,
            Status = StepStatus.Completed
        };
    }

    /// <summary>
    /// What the condition reads: the previous step's output (<c>AccumulatedOutput</c>, the default), or
    /// the output of <see cref="ConditionalStepConfig.SourceStepOrder"/> when the config names one. A named
    /// step with no output in this execution (it was skipped, or comes later) reads as an empty output,
    /// so every identifier is unresolvable and the condition is false — logged, never a failed step.
    /// </summary>
    private string ResolveEvaluatedOutput(StepExecutionContext context, ConditionalStepConfig? config)
    {
        if (config?.SourceStepOrder is not { } sourceOrder)
            return context.AccumulatedOutput;

        StepOutputHistoryEntry? source = context.StepOutputHistory.LastOrDefault(h => h.StepOrder == sourceOrder);
        if (source is null)
        {
            _logger.LogWarning(
                "Conditional step {StepOrder}: source step {SourceStepOrder} has no output in this execution; evaluating false",
                context.Step.StepOrder,
                sourceOrder);
            return string.Empty;
        }

        return source.Output;
    }

    /// <summary>
    /// Today's expression evaluation — same call and log line, over <paramref name="evaluatedOutput"/>
    /// (the previous step's output unless the config names a source step).
    /// </summary>
    private bool EvaluateExpression(StepExecutionContext context, string evaluatedOutput)
    {
        // A run with app inputs reads them as input.<id> parameters that no step output can shadow (app-inputs 9.2); a run
        // without any makes exactly today's call.
        bool result = AppInputsAmbient.Current is { } appInputs
            ? ExpressionEvaluator.EvaluateCore(
                context.Step.ConditionExpression ?? "", evaluatedOutput, appInputs.ExpressionParameters, _logger, AppInputPlaceholders.ExpressionPrefix)
            : ExpressionEvaluator.Evaluate(
                context.Step.ConditionExpression ?? "",
                evaluatedOutput);

        _logger.LogInformation("Conditional step {StepOrder}: expression '{Expression}' evaluated to {Result}",
            context.Step.StepOrder, context.Step.ConditionExpression, result);

        return result;
    }

    /// <summary>
    /// The <see cref="ConditionMode.Decision"/> arm: one <c>Noul</c> question over a bounded view of
    /// the prior step's output, then the step's own condition over the answer.
    ///
    /// <para>
    /// Never throws (except for the workflow's own cancellation) and never fails the step: every
    /// failure mode — no provider, a gate-level timeout, a transport error, a thrown gate, a
    /// question the model did not answer — is logged and returns <c>false</c>.
    /// </para>
    /// </summary>
    private async Task<bool> EvaluateDecisionAsync(
        StepExecutionContext context, ConditionalStepConfig config, string evaluatedOutput)
    {
        try
        {
            string conditionExpression = context.Step.ConditionExpression ?? "";

            // A bounded view of the prior step's output is what is sent to the provider — never the
            // raw accumulated output, which is unbounded across a pipeline.
            string boundedState = JsonOutputDigest.Digest(evaluatedOutput, StateDigest);

            string questionName = ResolveQuestionName(config, conditionExpression);

            var request = new DecisionGateRequest(
                ExecutionId: context.Execution.Id,
                StepOrder: context.Step.StepOrder,
                AgentType: context.Step.AgentDefinition?.AgentType ?? AgentType.Custom,
                Site: DecisionSite,
                State: new DecisionState(boundedState),
                Questions:
                [
                    new DecisionGateQuestion(
                        DecisionSite,
                        questionName,
                        DecisionKind.Noul,
                        BuildInstructions(conditionExpression),
                        Options: [],
                        // Plain null — the parameter is string? — because no System Two ran: this
                        // is a pre-decision Gate path, which
                        // DecisionGateQuestion.SystemTwoAnswer's own contract describes as "no agent
                        // has run yet". There is no counterfactual to compare against, so the
                        // observation's `agreed` column is left NULL: no agreement claim at all,
                        // which the calibration aggregation reads as "outcome unavailable" and
                        // excludes from its denominator. Deliberately not a disagreement — nothing
                        // was ever observed to disagree with, and recording one would dilute the
                        // agreement rate with rows that testified about nothing.
                        SystemTwoAnswer: null,
                        // This site's own accept rule, declared rather than left to be inferred. The
                        // step decides on the AFFIRMATIVE probability (`p_yes`, or the expression
                        // over `decision.p_yes`), never on the top of the two-point set TopTwo
                        // derives from it: for p_yes = 0.10 the top is 0.90, so a reader applying the
                        // top/margin rule reports this step as confident and accepted on precisely
                        // the answers the step rejects. The declaration is persisted on the row, so
                        // the calibration view recomputes this site with this site's rule. No
                        // cutoff — the comparison is against the threshold directly.
                        AcceptMetric: DecisionAcceptMetric.Affirmative)
                ],
                Config: new DecisionGateConfig(
                    // Gate, never Shadow: this decision IS the step's answer (and the gate persists
                    // a Mode = "Gate" observation for it, which is what puts the site in
                    // calibration). AcceptAt/MinMargin keep their phase-1 fallback values — they
                    // answer "is the model trustworthy", which is the separate `decision.escalated`
                    // parameter, NOT this step's `Threshold`. Because this site declares the
                    // Affirmative metric, `decision.escalated` is now `p_yes` measured against
                    // AcceptAt rather than against the top of the two-point set: that is this step's
                    // own accept rule, and it is the same number the calibration view recomputes for
                    // this row. The branch the step takes is still `Threshold`/the expression.
                    Mode: DecisionGateMode.Gate,
                    ProviderId: config.ProviderId,
                    // The clamped value, never the raw one: the raw value is what the client sent
                    // and stays raw in the record and in JSON, but a non-positive one would mean
                    // "no deadline at all" here and an enormous one would make the gate's own
                    // CancelAfter throw. The gate clamps again as the real control; this is the
                    // explicit, readable half of the same single definition.
                    TimeoutSeconds: config.ClampedTimeoutSeconds));

            DecisionGateOutcome? outcome = await _decisionGate.DecideAsync(request, context.CancellationToken);

            if (outcome is null || !outcome.HasDecision)
            {
                _logger.LogWarning(
                    "Conditional step {StepOrder}: the decision gate produced no decision (no Decision provider configured, a timeout, or a failure); evaluating false",
                    context.Step.StepOrder);
                return false;
            }

            DecisionAnswer? answer = null;
            outcome.Result?.Answers.TryGetValue(questionName, out answer);

            if (answer?.Noul is not double pYes)
            {
                _logger.LogWarning(
                    "Conditional step {StepOrder}: the decision gate answered without a Noul probability for question '{Question}'; evaluating false",
                    context.Step.StepOrder,
                    questionName);
                return false;
            }

            double pNo = 1.0 - pYes;

            // The provider's own confidence when it reports one, and otherwise the shared
            // top-probability definition — one reduction, so `decision.confidence` never disagrees
            // with how any other site reads the same answer's distribution.
            double confidence = answer.Confidence ?? DecisionProbability.TopTwo(answer).Top;

            // Step-level all-or-nothing: with exactly one question, `Accepted` is that question
            // clearing the thresholds under the metric the question declared — for this site the
            // Affirmative metric, i.e. `p_yes` against AcceptAt. The step does NOT require acceptance
            // to act — the expression (or Threshold) decides — but the author can gate on it
            // explicitly.
            bool escalated = !outcome.Accepted;

            bool result;
            if (string.IsNullOrWhiteSpace(conditionExpression))
            {
                result = pYes >= config.Threshold;

                _logger.LogInformation(
                    "Conditional step {StepOrder}: decision.p_yes {PYes} >= threshold {Threshold} evaluated to {Result} (escalated: {Escalated})",
                    context.Step.StepOrder, pYes, config.Threshold, result, escalated);
            }
            else
            {
                var decisionParameters = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["decision.p_yes"] = pYes,
                    ["decision.p_no"] = pNo,
                    ["decision.confidence"] = confidence,
                    ["decision.escalated"] = escalated,
                };

                // Evaluated against the SAME accumulated output the Expression path uses, never the
                // bounded view: Decision mode is "Expression mode, plus one question asked and four
                // decision.* parameters in scope", so a `$.field` reference keeps full fidelity and
                // cannot be silently digested away. The bound applies to what LEAVES THE HOST (the
                // provider call), which is the only place the 4k-token constraint is about.
                // App inputs ride along as input.<id> parameters, reserved as on the Expression path (app-inputs 9.2).
                string? reserved = null;
                if (AppInputsAmbient.Current is { } appInputs)
                {
                    foreach ((string name, object? value) in appInputs.ExpressionParameters)
                        decisionParameters.TryAdd(name, value);
                    reserved = AppInputPlaceholders.ExpressionPrefix;
                }

                result = ExpressionEvaluator.EvaluateCore(
                    conditionExpression, evaluatedOutput, decisionParameters, _logger, reserved);

                _logger.LogInformation(
                    "Conditional step {StepOrder}: expression '{Expression}' over the decision (p_yes {PYes}, escalated {Escalated}) evaluated to {Result}",
                    context.Step.StepOrder, conditionExpression, pYes, escalated, result);
            }

            return result;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // The workflow itself was cancelled. Returning `false` here would let a cancelled
            // execution carry on down the False branch, so this is the one thing that propagates —
            // exactly DecisionGate.DecideAsync's own rule.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Conditional step {StepOrder}: the decision evaluation failed; evaluating false (the step is unaffected)",
                context.Step.StepOrder);
            return false;
        }
    }

    /// <summary>
    /// The <c>Noul</c> question's name: the configured one, else the step's own condition
    /// expression (which is what makes each distinct site separately groupable in the calibration
    /// view), else a fixed default for a pure <c>Threshold</c> comparison.
    /// </summary>
    private static string ResolveQuestionName(ConditionalStepConfig config, string conditionExpression)
    {
        if (!string.IsNullOrWhiteSpace(config.DecisionQuestion))
            return config.DecisionQuestion;

        return string.IsNullOrWhiteSpace(conditionExpression)
            ? ThresholdQuestionName
            : conditionExpression.Trim();
    }

    private static string BuildInstructions(string conditionExpression) =>
        string.IsNullOrWhiteSpace(conditionExpression)
            ? "Answer YES if the state above satisfies this step's success condition, otherwise NO."
            : $"Answer YES if the following condition holds for the state above, otherwise NO. Condition: {conditionExpression.Trim()}";

    /// <summary>
    /// Reads <see cref="WorkflowStep.ConditionalConfigJson"/>. Returns null for a null/blank column
    /// and for anything that does not deserialise — both of which mean today's expression path, not
    /// an error. Never throws.
    /// </summary>
    /// <summary>The branch target that ends the run (<see cref="StaticBranchResolver.EndToken"/>, docs/design/app-inputs.md 9.4).</summary>
    internal static bool IsEndToken(string? branch) =>
        string.Equals(branch?.Trim(), StaticBranchResolver.EndToken, StringComparison.OrdinalIgnoreCase);

    private ConditionalStepConfig? TryReadConfig(string? conditionalConfigJson)
    {
        if (string.IsNullOrWhiteSpace(conditionalConfigJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ConditionalStepConfig>(conditionalConfigJson, ConfigJsonOptions);
        }
        catch (Exception ex)
        {
            // Deliberately broad: this runs on EVERY Conditional step, before any mode is even
            // known, and the contract is that a bad config is today's expression path — never a
            // thrown step. JsonException is the realistic case; anything else a deserialiser could
            // surface must not become this step's first-ever failure mode either.
            _logger.LogWarning(
                ex,
                "Conditional step: ConditionalConfigJson could not be read; falling back to the expression path");
            return null;
        }
    }
}
