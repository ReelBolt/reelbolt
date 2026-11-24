using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Review;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Observability;
using ReelBolt.WorkflowEngine.Services.Briefs;
using ReelBolt.WorkflowEngine.Services.Review;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// Which way a ReviewLoop gate run resolved.
/// </summary>
internal enum ReviewLoopGateDecision
{
    /// <summary>Run the reviewer, exactly as the step does today.</summary>
    Escalate,

    /// <summary>Skip the reviewer and advance: System One was confident enough.</summary>
    StopEarly
}

/// <summary>
/// A gate evaluation: the decision plus the number it was taken on, so the caller can log and mark
/// its output envelope without recomputing the sum.
/// </summary>
internal sealed record ReviewLoopGateEvaluation(ReviewLoopGateDecision Decision, double PAtOrAbove);

/// <summary>
/// What the pre-reviewer gate call left behind, so the caller knows whether there is anything to
/// complete once the reviewer has run.
/// </summary>
/// <param name="EarlyStop">
/// The step result when the gate was confident enough to skip the reviewer, or <c>null</c> when
/// the caller must run it.
/// </param>
/// <param name="GateObservationWritten">
/// Whether the gate actually got a decision and therefore wrote its own <c>Mode = "Gate"</c>
/// observation for this execution and step. That row is what
/// <see cref="IDecisionGate.AttachSystemTwoAnswersAsync"/> completes with the reviewer's score, so
/// a pre-check that never reached the provider (no facts to ask over, a timeout, a failure) has
/// nothing to complete and must not cost a store round-trip.
/// </param>
internal sealed record ReviewLoopGatePreCheck(StepExecutionResult? EarlyStop, bool GateObservationWritten);

/// <summary>
/// Runs the review agent, parses score, and loops back if score is below threshold.
///
/// <para>
/// <b>Calibrated early stop (decision-engine phase 2, D-008).</b> When the step carries an enabled
/// <see cref="ReviewLoopStepConfig"/> and a Decision provider resolves, a System One <c>Score</c>
/// question is asked over the deterministic review facts the prior step already computed
/// <b>before</b> the reviewer runs. A confident answer (<c>p(level &gt;= MinScore) &gt;= AcceptAt</c>
/// and a top-two gap wider than <c>BimodalMargin</c>) skips the reviewer and advances without
/// looping back; every other shape escalates to today's exact path. On that escalated path the
/// reviewer's score completes the gate's own observation, so the calibration view sees a real
/// comparison instead of a row that claims nothing. Disabled, absent or unparseable
/// config — and any gate failure, timeout or missing provider — are byte-identical to the
/// pre-change executor.
/// </para>
/// </summary>
public class ReviewLoopStepExecutor : IStepExecutor
{
    // Same convention as ExtractStepExecutor/VideoAnalyzeStepExecutor: property names follow
    // JsonSerializerDefaults.Web (camelCase; the frontend schema in web/lib/schemas mirrors them).
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// The observation site and question name of the ReviewLoop gate. The site is a dotted site
    /// string (the <c>Agent.step</c> convention the other gate sites use) and is what the
    /// calibration view groups this step's decisions under.
    /// </summary>
    internal const string GateSite = "ReviewLoop.Score";

    /// <summary>The single question's name, which is also the key into the returned answer map.</summary>
    internal const string GateQuestionName = "Score";

    /// <summary>
    /// The rubric the question offers <b>and</b> the range <see cref="ReadScore"/> clamps a parsed
    /// reviewer score to. The two must stay equal: <c>p(level &gt;= MinScore)</c> is only comparable
    /// with a reviewer's score if both live on the same 0..10 scale.
    ///
    /// <para>
    /// Aliased to <see cref="DecisionAcceptRule"/>'s constants rather than retyped: the shared rule
    /// sums the same levels when the calibration view recomputes a row this site wrote, and two
    /// independently maintained bounds is exactly how the two would come to disagree about which
    /// probability-map keys are levels.
    /// </para>
    /// </summary>
    internal const int MinRubricLevel = DecisionAcceptRule.MinRubricLevel;
    internal const int MaxRubricLevel = DecisionAcceptRule.MaxRubricLevel;

    /// <summary>
    /// Hard cap on the serialized deterministic-facts state handed to the provider (plan Global
    /// Constraints: a bounded descriptor projection, never the full agent input, well inside the
    /// 4k-token Decision context). The projection is bounded by construction — a fixed key
    /// whitelist, truncated strings and capped arrays — so this is defence in depth and a test
    /// pins it.
    /// </summary>
    internal const int MaxFactsChars = 4000;

    /// <summary>Per-string bound inside the projection.</summary>
    private const int MaxFactStringChars = 240;

    /// <summary>Per-array bound inside the projection.</summary>
    private const int MaxFactArrayItems = 8;

    private static readonly string[] ReviewFactKeys = ["sentenceCheck"];

    private readonly IAgentRegistry _agentRegistry;
    private readonly IWorkflowExecutionContextAccessor _executionContextAccessor;
    private readonly IDecisionGate _decisionGate;
    private readonly ILogger<ReviewLoopStepExecutor> _logger;
    private readonly Lazy<IReviewVisualCheck?> _visualCheck;
    private readonly Lazy<IReviewFilmDurationProbe?> _lengthProbe;
    private readonly Lazy<IReviewFilmFacts?> _filmFacts;
    private readonly Lazy<IReviewRenderQuality?> _renderQuality;

    /// <param name="services">Optional; when supplied, the visual check (docs/video-editing.md "Visual check in
    /// review loops") is composed lazily from it, on the first iteration that enables the check.</param>
    public ReviewLoopStepExecutor(
        IAgentRegistry agentRegistry,
        IWorkflowExecutionContextAccessor executionContextAccessor,
        IDecisionGate decisionGate,
        ILogger<ReviewLoopStepExecutor> logger,
        IServiceProvider? services = null)
        : this(agentRegistry, executionContextAccessor, decisionGate, logger,
            new Lazy<IReviewVisualCheck?>(() => services is null ? null : ReviewVisualCheck.TryCreate(services)),
            new Lazy<IReviewFilmDurationProbe?>(() => services is null ? null : FfmpegReviewFilmDurationProbe.TryCreate(services)),
            new Lazy<IReviewFilmFacts?>(() => services is null ? null : FfmpegReviewFilmFacts.TryCreate(services)),
            new Lazy<IReviewRenderQuality?>(() => services is null ? null : ReviewRenderQuality.TryCreate(services)))
    {
    }

    /// <summary>Test seam: the visual check supplied directly (null = not wired in this host).</summary>
    internal ReviewLoopStepExecutor(
        IAgentRegistry agentRegistry,
        IWorkflowExecutionContextAccessor executionContextAccessor,
        IDecisionGate decisionGate,
        ILogger<ReviewLoopStepExecutor> logger,
        IReviewVisualCheck? visualCheck,
        IReviewFilmDurationProbe? lengthProbe = null,
        IReviewFilmFacts? filmFacts = null,
        IReviewRenderQuality? renderQuality = null)
        : this(agentRegistry, executionContextAccessor, decisionGate, logger,
            new Lazy<IReviewVisualCheck?>(visualCheck), new Lazy<IReviewFilmDurationProbe?>(lengthProbe),
            new Lazy<IReviewFilmFacts?>(filmFacts), new Lazy<IReviewRenderQuality?>(renderQuality))
    {
    }

    private ReviewLoopStepExecutor(
        IAgentRegistry agentRegistry,
        IWorkflowExecutionContextAccessor executionContextAccessor,
        IDecisionGate decisionGate,
        ILogger<ReviewLoopStepExecutor> logger,
        Lazy<IReviewVisualCheck?> visualCheck,
        Lazy<IReviewFilmDurationProbe?> lengthProbe,
        Lazy<IReviewFilmFacts?> filmFacts,
        Lazy<IReviewRenderQuality?> renderQuality)
    {
        _renderQuality = renderQuality;
        _lengthProbe = lengthProbe;
        _filmFacts = filmFacts;
        _agentRegistry = agentRegistry;
        _executionContextAccessor = executionContextAccessor;
        _decisionGate = decisionGate;
        _logger = logger;
        _visualCheck = visualCheck;
    }

    public StepType StepType => StepType.ReviewLoop;

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        IReelBoltAgent? agent = _agentRegistry.GetByType(context.Step.AgentDefinition.AgentType);
        if (agent == null)
        {
            _logger.LogWarning(
                "ReviewLoop step {StepOrder}: no agent registered for type {AgentType}",
                context.Step.StepOrder,
                context.Step.AgentDefinition.AgentType);
            return new StepExecutionResult
            {
                Output = context.AccumulatedOutput,
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                Status = StepStatus.Skipped,
                ErrorDetails = $"No agent for type {context.Step.AgentDefinition.AgentType}"
            };
        }

        // Read the calibrated-early-stop config before anything else, but resolve nothing (and call
        // nothing) unless it is explicitly enabled: an absent, empty or unparseable blob leaves this
        // step on exactly the code path it has always taken.
        ReviewLoopStepConfig? gateConfig = TryReadConfig(context.Step.ReviewLoopConfigJson);

        // The visual check (off unless its own Enabled is set) runs before the gate and the reviewer. A
        // finding that caps the score cannot be declined by the early stop, so the gate is then not asked.
        AgentType? reviewerType = context.Step.AgentDefinition?.AgentType;
        VisualCheckOutcome? visual = gateConfig?.VisualCheck is { Enabled: true } visualConfig
            ? await RunVisualCheckAsync(context, visualConfig)
            : null;

        // The length gate (docs/video-editing.md "Length gate"): only when the run brief asked for a length.
        LengthCheckOutcome? length = await LengthCheckReport.RunAsync(
            context, RunBriefAmbient.Current?.LengthSec, _lengthProbe.Value, context.CancellationToken);

        // Render quality checks (docs/video-editing.md "Render quality checks"): off unless QualityChecks is set; free and
        // first-party. A high-severity finding caps the score, so (like the visual and length caps) the early stop is not asked.
        RenderQualityOutcome? quality = gateConfig?.VisualCheck is { QualityChecks: true } qualityConfig
            ? await RunRenderQualityAsync(context, qualityConfig)
            : null;

        ReviewLoopGatePreCheck preCheck = new(EarlyStop: null, GateObservationWritten: false);
        if (gateConfig is { Enabled: true } && visual?.Capped != true && length?.Capped != true && quality?.Capped != true)
        {
            preCheck = await TryStopEarlyAsync(context, gateConfig);
            if (preCheck.EarlyStop is not null)
                return preCheck.EarlyStop;
        }

        Stopwatch sw = Stopwatch.StartNew();
        string input = context.BuildAgentInput();
        if (visual is not null && VisualCheckReport.ReviewInputBlock(visual) is { } visualBlock)
            input = input + "\n\n" + visualBlock;
        // docs/design/review-loop-adjustments.md: the film facts, the settings the reviewer may change and the
        // targeted-look invitation. Each is opt-in; with all of them off nothing is appended (byte-identical).
        if (quality is not null && RenderQualityReport.ReviewInputBlock(quality) is { } qualityBlock)
            input = input + "\n\n" + qualityBlock;
        VisualCheckConfig? lookConfig = gateConfig?.VisualCheck;
        foreach (string block in await BuildFilmFactBlocksAsync(context, lookConfig))
            input = input + "\n\n" + block;
        if (gateConfig is { AllowAdjustments: true }
            && ReviewAdjustmentReport.SettingsBlock(AdjustmentRequest(context, loopsBack: true)) is { } settingsBlock)
        {
            input = input + "\n\n" + settingsBlock;
        }

        int maxLookRounds = lookConfig?.ClampedExtraRounds ?? 0;
        if (maxLookRounds > 0 && ReviewFilmResolver.Resolve(context) is not null)
            input = input + "\n\n" + ReviewTargetedLooks.InvitationBlock(maxLookRounds, lookConfig!.ClampedFramesPerRound);
        else
            maxLookRounds = 0;
        // T-007: the numbers from earlier passes of THIS run. Null on the first pass, and then
        // nothing is appended — a one-pass run's prompt input is byte-identical to before.
        if (context.ReviewPassHistory is { Length: > 0 } passHistory)
            input = input + "\n\n" + passHistory;
        _logger.LogInformation(
            "ReviewLoop step {StepOrder}: executing agent {AgentName} (Iteration={CurrentIteration}, MaxIterations={MaxIterations}, MinScore={MinScore})",
            context.Step.StepOrder,
            agent.Name,
            context.IterationCount + 1,
            context.Step.MaxIterations,
            context.Step.MinScore ?? 9);
        _logger.LogDebug(
            "ReviewLoop step {StepOrder} input preview: {InputPreview}",
            context.Step.StepOrder,
            CreatePreview(input, 250));
        using IDisposable _ = _executionContextAccessor.BeginScope(
            context.Execution.Id,
            context.Execution.ProjectId,
            context.CorrelationId,
            context.Execution.OrganizationId,
            stepResultId: context.StepResultId);
        AgentRunResult result = await agent.RunAsync(input, context.Step.AgentDefinitionId, context.CancellationToken);
        int tokensUsed = result.TokensUsed;
        List<ReviewLookRound> lookRounds = [];
        List<string> skippedLooks = [];
        for (int round = 1; round <= maxLookRounds; round++)
        {
            ReviewLookRequest? request = ReviewTargetedLooks.ReadRequest(result.Output, reviewerType, lookConfig!.ClampedFramesPerRound);
            if (request is null)
                break;
            if (LookBudgetProblem(context, round) is { } budgetProblem)
            {
                skippedLooks.Add(budgetProblem);
                break;
            }

            VisualCheckOutcome look = _visualCheck.Value is { } lookCheck
                ? await lookCheck.LookAsync(context, lookConfig, request.Frames, request.Focus, _filmFacts.Value, context.CancellationToken)
                : VisualCheckOutcome.Unavailable("targeted looks are not available in this host");
            ReviewLookRound done = new(round, request, look);
            lookRounds.Add(done);
            _logger.LogInformation(
                "ReviewLoop step {StepOrder}: targeted look {Round} {Status} ({Frames} frame(s) asked, {Findings} finding(s))",
                context.Step.StepOrder, round, look.Status, request.Frames.Count, look.Findings.Count);
            if (!look.IsChecked)
                break; // nothing new to show the reviewer, so it is not asked again

            input = input + "\n\n" + ReviewTargetedLooks.ResultBlock(done);
            result = await agent.RunAsync(input, context.Step.AgentDefinitionId, context.CancellationToken);
            tokensUsed += result.TokensUsed;
        }

        if (lookRounds.Count > 0)
            visual = ReviewTargetedLooks.Merge(visual, lookRounds, lookConfig!.ClampedFailScoreCap);
        sw.Stop();

        RobustJsonExtractor.VerdictScan scan = ScanReviewVerdicts(result.Output, context.Step.AgentDefinition?.AgentType);
        RobustJsonExtractor.ScoredJsonObject? verdict = scan.Verdict;
        int reviewerScore = ToReviewScore(scan);
        if (scan.UnaccountedKeyCount > 0)
        {
            // Fail closed (T-019): a verdict key sits outside every readable verdict object, so the
            // reviewer's output cannot be trusted to say what it scored. Scored 0 and loops back.
            _logger.LogWarning(
                "ReviewLoop step {StepOrder}: unaccounted verdict key ({UnaccountedKeyCount} occurrence(s) outside every readable verdict object) in the reviewer output (StepResultId={StepResultId}); scoring it 0. Output preview: {OutputPreview}",
                context.Step.StepOrder,
                scan.UnaccountedKeyCount,
                context.StepResultId,
                CreatePreview(result.Output, 250));
        }
        else if (verdict is null)
        {
            // Scored 0 below, which loops back like any failing score. Say so, so a paid extra
            // iteration caused by unreadable output is never mistaken for the reviewer's verdict.
            _logger.LogWarning(
                "ReviewLoop step {StepOrder}: no review score could be read from the reviewer output (StepResultId={StepResultId}); scoring it 0. Output preview: {OutputPreview}",
                context.Step.StepOrder,
                context.StepResultId,
                CreatePreview(result.Output, 250));
        }
        else if (verdict.ScoresDiffer)
        {
            // Several objects in the output carry different scores. The lowest is scored, so a
            // higher echoed or injected score never raised the verdict; say so for the operator.
            _logger.LogWarning(
                "ReviewLoop step {StepOrder}: {CandidateCount} review objects in the reviewer output carry different scores; scoring the lowest, {Score} (StepResultId={StepResultId}).",
                context.Step.StepOrder,
                verdict.AcceptedCount,
                reviewerScore,
                context.StepResultId);
        }

        // The reviewer is TOLD to cap its score when a narration line overruns its picture window,
        // but prompt text is a request to a model, not a guarantee. These are the halves that cannot
        // be talked out of it: overflow (a measured overrun) and declared-and-lost (narration that
        // never reached the picture at all). They adjust only the number the loop decision below
        // uses — the reviewer's own output JSON is persisted verbatim, and the gate's counterfactual
        // (below) is the reviewer's real score, not this adjusted one, because that row compares the
        // model's answer against what the reviewer actually said.
        // The interleave cap (docs/video-editing.md "Using every clip offered") rides the same rails:
        // an edit that left offered clips unused without a verified reason from the brief cannot pass.
        int score = ApplyDeterministicCaps(reviewerScore, context.AccumulatedOutput);
        string output = visual is null ? result.Output : VisualCheckReport.AttachToOutput(result.Output, visual, reviewerType);
        if (lookRounds.Count > 0 || skippedLooks.Count > 0)
        {
            output = ReviewOutputNodes.Mutate(output, reviewerType,
                node => node[ReviewTargetedLooks.RecordNodeName] = ReviewTargetedLooks.ToNode(lookRounds, skippedLooks));
        }
        if (visual is not null)
            score = VisualCheckReport.ApplyRecordedCap(score, output, reviewerType);
        if (quality is not null)
        {
            output = RenderQualityReport.AttachToOutput(output, quality, reviewerType);
            int beforeQuality = score;
            score = RenderQualityReport.ApplyRecordedCap(score, output, reviewerType);
            if (score < beforeQuality)
            {
                _logger.LogInformation(
                    "ReviewLoop step {StepOrder}: a high-severity render quality finding; score capped to {Score}",
                    context.Step.StepOrder, score);
            }
        }
        if (length is not null)
        {
            output = LengthCheckReport.AttachToOutput(output, length, reviewerType);
            if (length.Capped)
            {
                int beforeLength = score;
                score = Math.Min(score, LengthCheckOutcome.CapScore);
                if (score < beforeLength)
                {
                    ReelBoltDiagnostics.ReviewLengthCapped.Add(1);
                    _logger.LogInformation(
                        "ReviewLoop step {StepOrder}: reelbolt.review.length_capped: film is {ActualSec:0.#} s, requested {RequestedSec} s ({DeviationPct:0.#}% off); score capped to {Score}",
                        context.Step.StepOrder, length.ActualSec, length.RequestedSec, length.DeviationPct, score);
                }
            }
        }
        int minScore = context.Step.MinScore ?? 9;
        int maxIterations = context.Step.MaxIterations;
        int newIterationCount = context.IterationCount + 1;

        if (score != reviewerScore)
        {
            _logger.LogInformation(
                "ReviewLoop step {StepOrder}: a deterministic cap lowered the review score from {ReviewerScore} to {CappedScore}",
                context.Step.StepOrder, reviewerScore, score);
        }

        _logger.LogInformation(
            "ReviewLoop step {StepOrder}: score={Score}, minScore={MinScore}, iteration={Iteration}/{Max}, duration={DurationMs}ms, tokens={TokensUsed}",
            context.Step.StepOrder, score, minScore, newIterationCount, maxIterations, sw.ElapsedMilliseconds, tokensUsed);
        _logger.LogDebug(
            "ReviewLoop step {StepOrder} output preview: {OutputPreview}",
            context.Step.StepOrder,
            CreatePreview(result.Output, 250));

        // The gate escalated, so it wrote an observation claiming no agreement (no reviewer had run
        // when it asked). Now one has, and its score IS the counterfactual — complete that row
        // rather than leaving it permanently unable to testify. Both escalated outcomes share this
        // one call: it is the reviewer having run that makes the comparison real, not which way the
        // loop then goes. Never on the stop-early path above, where no reviewer ran and attaching a
        // score would fabricate an agreement about a reviewer that was deliberately skipped.
        if (preCheck.GateObservationWritten)
        {
            await _decisionGate.AttachSystemTwoAnswersAsync(
                context.Execution.Id,
                context.Step.StepOrder,
                new Dictionary<string, string>
                {
                    // The gate's own DecisionKind.Score convention — DecisionGate.ChosenValue is
                    // answer.Score?.ToString() — so an equal score compares equal. The reviewer's
                    // REAL score, deliberately not the narration-fit capped one above: this row
                    // records what the reviewer said, and feeding it a number the reviewer never
                    // produced would make the gate's agreement rate a comparison against our own
                    // post-processing. The cap governs the loop decision only.
                    [GateQuestionName] = reviewerScore.ToString(CultureInfo.InvariantCulture)
                },
                tokensUsed,
                context.CancellationToken);
        }

        // If score is below threshold and we haven't exceeded max iterations, loop back
        int loopTargetIndex = context.Step.LoopTargetStepOrder is { } loopTargetOrder
            ? context.AllSteps.FindIndex(s => s.StepOrder == loopTargetOrder)
            : -1;
        bool loopsBack = score < minScore && newIterationCount < maxIterations && loopTargetIndex >= 0;

        // Review-loop adjustments (docs/design/review-loop-adjustments.md): read only when the step allows them.
        ReviewAdjustmentPlan? adjustments = null;
        if (gateConfig is { AllowAdjustments: true })
        {
            adjustments = ReviewAdjustmentPlanner.Plan(
                AdjustmentRequest(context, loopsBack), ReviewAdjustmentReport.ReadProposals(result.Output, reviewerType));
            output = ReviewAdjustmentReport.AttachToOutput(output, adjustments, reviewerType);
            foreach (ReviewAdjustmentDecision decision in adjustments.Decisions)
            {
                _logger.LogInformation(
                    "ReviewLoop step {StepOrder}: adjustment of step {TargetStep} {Setting} {From} -> {To}: {Status}{Why}",
                    context.Step.StepOrder, decision.StepOrder, decision.Setting, decision.From, decision.To, decision.Status,
                    decision.Why is null ? string.Empty : " (" + decision.Why + ")");
            }
        }

        if (loopsBack)
        {
            int targetIndex = loopTargetIndex;
            if (adjustments?.RerunFromStepOrder is { } rerunFrom
                && context.AllSteps.FindIndex(s => s.StepOrder == rerunFrom) is var rerunIndex and >= 0
                && rerunIndex < targetIndex)
            {
                targetIndex = rerunIndex;
            }

            _logger.LogInformation("Looping back to step order {TargetOrder}", context.AllSteps[targetIndex].StepOrder);
            return new StepExecutionResult
            {
                ReviewAdjustments = adjustments is { HasChanges: true } ? adjustments : null,
                Output = output,
                NextStepIndex = targetIndex,
                NewIterationCount = newIterationCount,
                DurationMs = sw.ElapsedMilliseconds,
                TokensUsed = tokensUsed,
                Status = StepStatus.Completed,
                IterationNumber = newIterationCount
            };
        }

        ReviewExhausted? exhausted = null;
        if (score < minScore)
        {
            exhausted = new ReviewExhausted(context.Step.StepOrder, score, minScore, newIterationCount);
            _logger.LogWarning(
                "ReviewLoop step {StepOrder} stopped below its minimum: score {Score}/10, target {MinScore}/10, after {Iterations} round(s); the run will need attention",
                context.Step.StepOrder, score, minScore, newIterationCount);
            ReelBoltDiagnostics.NeedsAttention.Add(1, new KeyValuePair<string, object?>("reason", "review_below_minimum"));
        }

        return new StepExecutionResult
        {
            Output = output,
            NextStepIndex = context.CurrentStepIndex + 1,
            NewIterationCount = newIterationCount,
            DurationMs = sw.ElapsedMilliseconds,
            TokensUsed = tokensUsed,
            Status = StepStatus.Completed,
            IterationNumber = newIterationCount,
            ReviewExhausted = exhausted
        };
    }

    /// <summary>One iteration's render quality checks; every check "unavailable" when this host has none wired. Never throws but for cancellation.</summary>
    private async Task<RenderQualityOutcome> RunRenderQualityAsync(StepExecutionContext context, VisualCheckConfig config)
    {
        IReviewRenderQuality? check = _renderQuality.Value;
        RenderQualityOutcome outcome = check is null
            ? RenderQualityOutcome.AllUnavailable("the render quality checks are not available in this host") with { FailScoreCap = config.ClampedFailScoreCap }
            : await check.RunAsync(context, config, context.CancellationToken);
        _logger.LogInformation(
            "ReviewLoop step {StepOrder}: render quality {Checks} ({Findings} finding(s), capped={Capped})",
            context.Step.StepOrder,
            string.Join(", ", outcome.Checks.Select(c => c.Name + "=" + c.Status)), outcome.Findings.Count, outcome.Capped);
        return outcome;
    }

    /// <summary>One iteration's visual check; "unavailable" when this host has none wired. Never throws but for cancellation.</summary>
    private async Task<VisualCheckOutcome> RunVisualCheckAsync(StepExecutionContext context, VisualCheckConfig config)
    {
        IReviewVisualCheck? check = _visualCheck.Value;
        if (check is null)
            return VisualCheckOutcome.Unavailable("the visual check is not available in this host");

        VisualCheckOutcome outcome = await check.RunAsync(context, config, context.CancellationToken);
        _logger.LogInformation(
            "ReviewLoop step {StepOrder}: visual check {Status} ({Findings} finding(s), capped={Capped}){Reason}",
            context.Step.StepOrder, outcome.Status, outcome.Findings.Count, outcome.Capped,
            outcome.Reason is null ? string.Empty : ": " + outcome.Reason);
        return outcome;
    }

    /// <summary>
    /// The <c>[TIMELINE FACTS]</c> and <c>[AUDIO FACTS]</c> blocks this iteration asks for (none when both are off, so
    /// the input is unchanged). Each is first-party and bounded; a block that cannot be built is left out, never an error.
    /// </summary>
    private async Task<IReadOnlyList<string>> BuildFilmFactBlocksAsync(StepExecutionContext context, VisualCheckConfig? config)
    {
        if (config is null || (!config.TimelineFacts && !config.AudioFacts))
            return [];

        List<string> blocks = [];
        IReviewFilmFacts? facts = _filmFacts.Value;
        (WorkflowStep? compileStep, StepOutputHistoryEntry? compileEntry) = LatestCompile(context);
        TimelineManifest? manifest = null;
        if (facts is not null && compileEntry is not null && TimelineKey(compileEntry.Output) is { } timelineKey)
            manifest = await facts.ReadTimelineAsync(context, timelineKey, context.CancellationToken);

        if (config.TimelineFacts && compileStep is not null
            && ReviewTimelineFacts.Render(manifest, compileEntry?.Output, compileStep.VideoCompileConfigJson, compileStep.StepOrder) is { } timelineBlock)
        {
            blocks.Add(timelineBlock);
        }

        if (config.AudioFacts && facts is not null && ReviewFilmResolver.Resolve(context) is { } film
            && ReviewAudioFacts.Render(await facts.MeasureAudioAsync(context, film, context.CancellationToken), manifest) is { } audioBlock)
        {
            blocks.Add(audioBlock);
        }

        _logger.LogInformation(
            "ReviewLoop step {StepOrder}: film facts added ({Blocks} block(s); timeline {Timeline}, audio {Audio})",
            context.Step.StepOrder, blocks.Count, config.TimelineFacts, config.AudioFacts);
        return blocks;
    }

    /// <summary>The newest VideoCompile step before the review and its history entry (last wins among equal orders).</summary>
    private static (WorkflowStep? Step, StepOutputHistoryEntry? Entry) LatestCompile(StepExecutionContext context)
    {
        for (int i = context.StepOutputHistory.Count - 1; i >= 0; i--)
        {
            StepOutputHistoryEntry entry = context.StepOutputHistory[i];
            if (entry.StepOrder >= context.Step.StepOrder)
                continue;
            WorkflowStep? step = context.AllSteps.FirstOrDefault(s => s.StepOrder == entry.StepOrder);
            if (step?.StepType == StepType.VideoCompile)
                return (step, entry);
        }

        return (null, null);
    }

    /// <summary>The <c>timelineStorageKey</c> a compile output names, or null.</summary>
    private static string? TimelineKey(string? compileOutput)
    {
        if (string.IsNullOrWhiteSpace(compileOutput))
            return null;
        try
        {
            return JsonNode.Parse(compileOutput) is JsonObject root && root["timelineStorageKey"] is JsonValue v && v.TryGetValue(out string? key)
                ? key
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the adjustment planner reads for this step: the run's steps, whether the run executes its own snapshot
    /// (the only thing an adjustment may ever change), the brief's length and the length the user saved.
    /// </summary>
    internal static ReviewAdjustmentRequest AdjustmentRequest(StepExecutionContext context, bool loopsBack) =>
        new(
            context.AllSteps,
            context.Step,
            RunHasOwnSnapshot: context.Execution.SnapshotDefinitionId is { } snapshot && context.Step.WorkflowDefinitionId == snapshot,
            LoopsBack: loopsBack,
            BriefLengthSec: RunBriefAmbient.Current?.LengthSec,
            SavedTargetDurationSec: order => SavedTargetDurationSec(context, order),
            LatestOutputJson: order => context.StepOutputHistory.LastOrDefault(h => h.StepOrder == order)?.Output);

    private static int? SavedTargetDurationSec(StepExecutionContext context, int stepOrder)
    {
        WorkflowStep? saved = context.Execution.WorkflowDefinition?.Steps?
            .FirstOrDefault(s => s.StepOrder == stepOrder && s.StepType == StepType.VideoCompile);
        if (saved is null)
            return null;
        return ReviewAdjustableSettings.Deserialize(StepType.VideoCompile, saved.VideoCompileConfigJson) is VideoCompileStepConfig config
            && config.TargetDurationSec > 0
                ? config.TargetDurationSec
                : null;
    }

    /// <summary>
    /// Why another targeted-look round would run past this step's credit hold, or null when it fits (or nothing was held:
    /// billing off). A round is started only when what the step has metered so far plus one more average pass (the
    /// reviewer passes and looks so far, divided by their number) stays inside the hold.
    /// </summary>
    internal static string? LookBudgetProblem(StepExecutionContext context, int round)
    {
        if (context.CreditHoldMillis is not { } hold || hold <= 0)
            return null;
        long spent = UsageScope.RunningCreditsMillis;
        long average = spent / Math.Max(1, round);
        return spent + average > hold
            ? "this step's credit hold does not cover another look, so it was skipped"
            : null;
    }

    // ---------------------------------------------------------------------
    // The calibrated early stop — System One decides whether the reviewer is paid for at all.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Asks the gate for a <c>Score</c> and reports either the step result that skips the reviewer,
    /// or <c>null</c> (escalate) plus whether there is a gate observation to complete later.
    ///
    /// <para>
    /// <b>One decision call, before the reviewer, and no second one afterwards.</b> The gate records
    /// its own <c>Mode = "Gate"</c> observation (site <see cref="GateSite"/>) from this call, with
    /// no agreement claim — <see cref="DecisionGateQuestion.SystemTwoAnswer"/> is null here because
    /// no reviewer has run yet, which the gate's contract defines as "no counterfactual exists".
    /// When this method escalates, the reviewer does run, and its parsed score is what completes
    /// that same row via <see cref="IDecisionGate.AttachSystemTwoAnswersAsync"/> — a store update
    /// over the row already written, never a second outbound request to the provider. Paying for a
    /// duplicate decision call would buy nothing: the comparison the calibration view wants is
    /// between the answer already held and the reviewer's score, both of which the caller has.
    /// </para>
    /// </summary>
    private async Task<ReviewLoopGatePreCheck> TryStopEarlyAsync(StepExecutionContext context, ReviewLoopStepConfig config)
    {
        int minScore = context.Step.MinScore ?? 9;

        // CONSERVATIVE FIRST, and before the provider is even called. The early stop returns before
        // the reviewer runs, which is also before ApplyNarrationCaps is applied — so without this
        // check the gate could accept, and skip the reviewer for, a step whose narration defect the
        // deterministic caps exist to catch. That would make both caps declinable by configuration,
        // which is precisely what they are for preventing.
        //
        // `ReviewFactKeys` is deliberately NOT widened with a voiceover fact (the program lead's
        // decision: the set of things that leave the host to a third-party Decision provider is not
        // being increased), so the fix lives here, on the local and inert side. It costs the gate
        // nothing but an escalation — the failure mode it is already required to prefer — and it
        // short-circuits before the outbound call rather than after it, so a step the caps will
        // reject never costs a provider round trip either.
        if (DeterministicCapsWouldFire(context.AccumulatedOutput))
        {
            _logger.LogInformation(
                "ReviewLoop step {StepOrder}: calibrated early stop is enabled but a deterministic cap applies to the prior step's output; escalating to the reviewer so the cap is not declined",
                context.Step.StepOrder);
            return new ReviewLoopGatePreCheck(EarlyStop: null, GateObservationWritten: false);
        }

        // The state is the deterministic review facts the prior step produced, NOT the raw prior
        // output and never context.BuildAgentInput() (plan Global Constraints: a bounded descriptor
        // projection on every Gate path). When the prior step produced none of them — the main promo
        // pipeline's ReviewLoop, whose previous step is an Author, has no compile facts at all —
        // there is nothing to ask a score question over, so escalate rather than send an empty state.
        string? facts = BuildReviewFacts(context.AccumulatedOutput);
        if (facts is null)
        {
            _logger.LogInformation(
                "ReviewLoop step {StepOrder}: calibrated early stop is enabled but the prior step produced no deterministic review facts; escalating to the reviewer",
                context.Step.StepOrder);
            return new ReviewLoopGatePreCheck(EarlyStop: null, GateObservationWritten: false);
        }

        var question = new DecisionGateQuestion(
            Site: GateSite,
            Name: GateQuestionName,
            Kind: DecisionKind.Score,
            Instructions: ScoreInstructions(),
            Options: RubricLevels(),
            // Null, NOT an empty string: no reviewer has run yet on this path, so there is no
            // counterfactual to compare against. Null is the gate's "no agreement observed" value —
            // it leaves the observation's DownstreamOutcomeJson null, which the calibration
            // aggregation reads as "outcome unavailable" and excludes from its agreement
            // denominator. An empty string is a *value*, not an absence, and would be compared
            // ordinally against the model's score as a permanent disagreement, driving the
            // ReviewLoop.Score site's agreement rate to 0%. The reviewer's real score is attached
            // to this row afterwards, on the escalated path only.
            SystemTwoAnswer: null,
            // This site's own accept rule, declared rather than left for a reader to infer: it
            // accepts on the summed rubric mass at or above the step's MinScore, never on the top
            // probability. The declaration is persisted on the row, so the calibration view
            // recomputes this site's escalation with the rule the site actually applied. The cutoff
            // is the RESOLVED `minScore`, not the nullable step property, because the recomputation
            // has to reproduce this site exactly — including the `?? 9` default applied above.
            AcceptMetric: DecisionAcceptMetric.AtOrAbove,
            ScoreCutoff: minScore);

        var gateConfig = new DecisionGateConfig(
            Mode: DecisionGateMode.Gate,
            AcceptAt: config.AcceptAt,
            MinMargin: config.BimodalMargin,
            ProviderId: config.ProviderId,
            // The clamped value, never the raw one. The raw value stays raw in the record and in
            // JSON; a non-positive one would otherwise disable the gate's deadline entirely — which
            // on this site delays the reviewer the gate exists to skip — and an enormous one would
            // otherwise make the gate's own CancelAfter throw and leave it permanently inert.
            TimeoutSeconds: config.ClampedTimeoutSeconds);

        Stopwatch gateWatch = Stopwatch.StartNew();
        DecisionGateOutcome outcome;
        try
        {
            outcome = await _decisionGate.DecideAsync(
                new DecisionGateRequest(
                    context.Execution.Id,
                    context.Step.StepOrder,
                    context.Step.AgentDefinition.AgentType,
                    Site: "ReviewLoop.step",
                    State: new DecisionState(facts),
                    Questions: [question],
                    Config: gateConfig),
                context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // The workflow's own cancellation, not the gate's budget — never swallowed.
            throw;
        }
        catch (Exception ex)
        {
            // Soft failure, same discipline as every other gate call site: the real (System Two)
            // review must never be affected by anything the gate did.
            _logger.LogWarning(
                ex,
                "ReviewLoop step {StepOrder}: gate call failed; escalating to the reviewer",
                context.Step.StepOrder);
            return new ReviewLoopGatePreCheck(EarlyStop: null, GateObservationWritten: false);
        }
        finally
        {
            gateWatch.Stop();
        }

        ReviewLoopGateEvaluation evaluation = Evaluate(outcome, minScore, config.AcceptAt, config.BimodalMargin);
        if (evaluation.Decision == ReviewLoopGateDecision.Escalate)
        {
            _logger.LogInformation(
                "ReviewLoop step {StepOrder}: gate escalated to the reviewer (pAtOrAbove={PAtOrAbove}, minScore={MinScore}, acceptAt={AcceptAt}, bimodalMargin={BimodalMargin})",
                context.Step.StepOrder, evaluation.PAtOrAbove, minScore, config.AcceptAt, config.BimodalMargin);

            // HasDecision is exactly "the provider answered and the gate persisted its rows": the
            // escalation paths that never reached a provider (no provider configured, a timeout, a
            // failure) all return NoDecision, and there is then no row to complete. An escalation
            // whose outcome does carry a decision still may not have written a row for THIS
            // question — Attach is a no-op for a question it does not find.
            return new ReviewLoopGatePreCheck(EarlyStop: null, GateObservationWritten: outcome.HasDecision);
        }

        _logger.LogInformation(
            "ReviewLoop step {StepOrder}: gate stopped early — the reviewer is skipped (pAtOrAbove={PAtOrAbove}, minScore={MinScore}, acceptAt={AcceptAt}, duration={DurationMs}ms, tokens={TokensUsed})",
            context.Step.StepOrder,
            evaluation.PAtOrAbove,
            minScore,
            config.AcceptAt,
            gateWatch.ElapsedMilliseconds,
            outcome.Result is null ? 0 : outcome.Result.InputTokens + outcome.Result.OutputTokens);

        return new ReviewLoopGatePreCheck(
            EarlyStop: new StepExecutionResult
            {
                // The prior output is preserved verbatim, with System One's decision appended as its own
                // envelope key — never a bare score, which would read as a reviewer verdict.
                Output = WithGateMarker(context.AccumulatedOutput, evaluation.PAtOrAbove),
                // Advance: an early stop is a pass, so a loop-back here would re-run the very work the
                // gate just decided was good enough.
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                DurationMs = gateWatch.ElapsedMilliseconds,
                TokensUsed = outcome.Result is null ? 0 : outcome.Result.InputTokens + outcome.Result.OutputTokens,
                InputTokens = outcome.Result?.InputTokens,
                OutputTokens = outcome.Result?.OutputTokens,
                Status = StepStatus.Completed,
                IterationNumber = context.IterationCount
            },
            GateObservationWritten: true);
    }

    /// <summary>
    /// The whole stop-early/escalate rule, as a pure function of the gate's outcome so it is
    /// testable without a fake decision client.
    ///
    /// <para>
    /// <b>This site declares <see cref="DecisionAcceptMetric.AtOrAbove"/>, and the rule itself is
    /// not re-implemented here.</b> The two predicates — the bimodal check and the
    /// <c>p(level &gt;= minScore)</c> accept threshold — are evaluated by
    /// <see cref="DecisionAcceptRule.Escalates"/>, the same call the gate makes when it decides which
    /// Escalated flag to store and the same call the calibration aggregation makes when it recomputes
    /// this row at a supplied threshold. What is left here is this site's exact contract: which
    /// failure shapes return <c>0</c> rather than a sum, and the
    /// <see cref="ReviewLoopGateEvaluation"/> shape the caller logs.
    /// </para>
    ///
    /// <para>
    /// <b>The order is load-bearing (Review Focus, D-008), and it lives in the shared rule.</b> The
    /// bimodal check runs <b>first</b>, so a distribution that is simultaneously high-<c>pAtOrAbove</c>
    /// and bimodal resolves to escalate, deterministically: a fast model split 0.40/0.35 between two
    /// levels is exactly the case it is least reliable on, and letting the accept check win there
    /// would make the outcome depend on which of the two nearly-equal levels happened to come first.
    /// Because both checks are strict comparisons, a two-point split landing exactly on
    /// <c>BimodalMargin</c> is reproducible rather than oscillating.
    /// </para>
    ///
    /// <para>
    /// Everything unanswerable escalates — no decision (no provider, timeout, unrecognised mode),
    /// a question the provider did not answer, no top-two pair, or an answer carrying no usable
    /// level distribution (e.g. a <c>Score</c> answer from the logprob client, which reports a
    /// weighted score but an empty probability map). Failing closed here means the gate can only
    /// ever cost a decision call, never a skipped review.
    /// </para>
    /// </summary>
    internal static ReviewLoopGateEvaluation Evaluate(
        DecisionGateOutcome outcome, int minScore, double acceptAt, double bimodalMargin)
    {
        if (!outcome.HasDecision || outcome.Result is null)
            return new ReviewLoopGateEvaluation(ReviewLoopGateDecision.Escalate, 0);

        if (!outcome.Result.Answers.TryGetValue(GateQuestionName, out DecisionAnswer? answer))
            return new ReviewLoopGateEvaluation(ReviewLoopGateDecision.Escalate, 0);

        if (!outcome.TopTwoProbabilities.TryGetValue(GateQuestionName, out _))
            return new ReviewLoopGateEvaluation(ReviewLoopGateDecision.Escalate, 0);

        double? pAtOrAbove = SumProbabilityAtOrAbove(answer.Probabilities, minScore);
        if (pAtOrAbove is null)
            return new ReviewLoopGateEvaluation(ReviewLoopGateDecision.Escalate, 0);

        bool escalates = DecisionAcceptRule.Escalates(
            DecisionAcceptMetric.AtOrAbove,
            minScore,
            answer.Kind,
            answer.Probabilities,
            answer.Noul,
            acceptAt,
            bimodalMargin);

        return new ReviewLoopGateEvaluation(
            escalates ? ReviewLoopGateDecision.Escalate : ReviewLoopGateDecision.StopEarly,
            pAtOrAbove.Value);
    }

    /// <summary>
    /// <c>p(level &gt;= minScore)</c>: the summed probability of every rubric level at or above the
    /// threshold the loop-back rule itself uses (<c>context.Step.MinScore</c>, default 9) — not the
    /// probability of the single most likely level, which would accept an edit the reviewer would
    /// have failed. Level keys are the option labels the question offered ("0".."10").
    ///
    /// <para>
    /// Returns null when the answer carries no parseable level key at all, which the caller reads as
    /// "escalate": a probability map that cannot be summed is not evidence of confidence.
    /// </para>
    ///
    /// <para>
    /// A thin alias over <see cref="DecisionAcceptRule.SumProbabilityAtOrAbove"/>, which is the one
    /// implementation — the calibration aggregation calls the same one when it recomputes a row this
    /// site wrote. The signature is kept because the executor's own tests and
    /// <see cref="Evaluate"/> speak in it.
    /// </para>
    /// </summary>
    internal static double? SumProbabilityAtOrAbove(IReadOnlyDictionary<string, double> probabilities, int minScore) =>
        DecisionAcceptRule.SumProbabilityAtOrAbove(probabilities, minScore, MinRubricLevel, MaxRubricLevel);

    /// <summary>The ordered rubric levels offered to the decision model, matching the range <see cref="ParseReviewScore"/> clamps to.</summary>
    internal static IReadOnlyList<string> RubricLevels() =>
        Enumerable.Range(MinRubricLevel, MaxRubricLevel - MinRubricLevel + 1)
            .Select(level => level.ToString(CultureInfo.InvariantCulture))
            .ToArray();

    /// <summary>What the decision model is asked, in one line.</summary>
    private static string ScoreInstructions() =>
        $"Score the edited video from {MinRubricLevel} to {MaxRubricLevel} using the deterministic review facts below only. The levels are ordered: a higher level means a better edit.";

    /// <summary>
    /// Projects the deterministic review facts out of a prior step's output for the decision state —
    /// the transcript sentence-boundary check, overlay frame coverage, the music dialogue
    /// headroom and the compile counts (<c>segments</c>, <c>outputDurationSec</c>,
    /// <c>retainedRatio</c>, <c>droppedSegmentsOverCap</c>), i.e. the compile-output facts
    /// <c>VideoReviewAgent</c>'s prompt consumes. The compile step emits no opening, seam or
    /// pacing check, so none is projected. Nothing else is copied: the raw prior
    /// output carries kept-span ids, storage keys and transcript text that a score question does not
    /// need, and this state is transmitted to a hosted third party. Returns null when the prior
    /// output is not a JSON object carrying at least one of those facts.
    /// </summary>
    internal static string? BuildReviewFacts(string? priorOutput)
    {
        if (string.IsNullOrWhiteSpace(priorOutput))
            return null;

        JsonNode? parsed;
        try
        {
            string json = RobustJsonExtractor.ExtractJsonObject(priorOutput) ?? priorOutput;
            parsed = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed is not JsonObject source)
            return null;

        var facts = new JsonObject();

        foreach (string key in ReviewFactKeys)
        {
            if (source.TryGetPropertyValue(key, out JsonNode? node) && node is not null)
                facts[key] = Bound(node, 0);
        }

        if (source["graphics"] is JsonObject graphics)
        {
            var projected = new JsonObject();
            CopyScalar(graphics, "applied", projected, "applied");
            CopyScalar(graphics, "appliedOverlayCount", projected, "appliedOverlayCount");

            if (graphics["appliedOverlays"] is JsonArray overlays)
            {
                var coverage = new JsonArray();
                foreach (JsonNode? overlay in overlays.Take(MaxFactArrayItems))
                {
                    if (overlay is not JsonObject entry)
                        continue;

                    var item = new JsonObject();
                    CopyScalar(entry, "placementId", item, "placementId");
                    CopyScalar(entry, "coveragePct", item, "coveragePct");
                    coverage.Add(item);
                }

                projected["appliedOverlays"] = coverage;
            }

            if (projected.Count > 0)
                facts["graphics"] = projected;
        }

        if (source["music"] is JsonObject music)
        {
            var projected = new JsonObject();
            CopyScalar(music, "applied", projected, "applied");
            CopyScalar(music, "dialogueHeadroom", projected, "dialogueHeadroom");
            if (projected.Count > 0)
                facts["music"] = projected;
        }

        foreach (string key in new[] { "segments", "outputDurationSec", "retainedRatio", "droppedSegmentsOverCap" })
        {
            if (source.TryGetPropertyValue(key, out JsonNode? node) && node is not null)
                facts[key] = Bound(node, 0);
        }

        if (facts.Count == 0)
            return null;

        string serialized = facts.ToJsonString();
        if (serialized.Length <= MaxFactsChars)
            return serialized;

        // Unreachable in practice — the whitelist is fixed and every leaf is bounded above — so this
        // is a hard guarantee rather than a shaping step. Truncation is marked rather than silent:
        // the state is prose to the model, and a quietly shortened JSON blob would read as fact.
        return serialized[..MaxFactsChars]
            + "\n[review facts truncated to fit the decision context budget]";
    }

    /// <summary>
    /// Appends System One's decision to the prior output's envelope. Mutates nothing the caller
    /// holds (the parsed node is local), and falls back to wrapping a non-object prior output
    /// rather than dropping it: the prior output is preserved either way.
    /// </summary>
    internal static string WithGateMarker(string priorOutput, double pAtOrAbove)
    {
        var marker = new JsonObject
        {
            ["decision"] = "stop-early",
            ["pAtOrAbove"] = Math.Round(pAtOrAbove, 4)
        };

        JsonNode? parsed = null;
        if (!string.IsNullOrWhiteSpace(priorOutput))
        {
            try
            {
                parsed = JsonNode.Parse(RobustJsonExtractor.ExtractJsonObject(priorOutput) ?? priorOutput);
            }
            catch (JsonException)
            {
                parsed = null;
            }
        }

        if (parsed is JsonObject envelope)
        {
            envelope["reviewLoopGate"] = marker;
            return envelope.ToJsonString();
        }

        return new JsonObject
        {
            ["reviewLoopGate"] = marker,
            ["priorOutput"] = priorOutput
        }.ToJsonString();
    }

    /// <summary>
    /// Copies a node with every leaf bounded: strings truncated, arrays capped, nesting depth
    /// limited. The projection's own size bound is what keeps the transmitted state inside the
    /// plan's 4k-token Decision context — the depth limit is only a runaway guard, deliberately
    /// deeper than any real facts shape (a compile output nests three levels:
    /// <c>music.dialogueHeadroom.*</c>), because collapsing a nested object to a placeholder would
    /// silently drop the very evidence the question is asked over.
    /// </summary>
    private static JsonNode Bound(JsonNode node, int depth)
    {
        if (depth >= 8)
            return JsonValue.Create("[truncated]")!;

        if (node is JsonObject obj)
        {
            var copy = new JsonObject();
            foreach ((string key, JsonNode? value) in obj)
            {
                if (value is not null)
                    copy[key] = Bound(value, depth + 1);
            }
            return copy;
        }

        if (node is JsonArray array)
        {
            var copy = new JsonArray();
            foreach (JsonNode? item in array.Take(MaxFactArrayItems))
            {
                if (item is not null)
                    copy.Add(Bound(item, depth + 1));
            }
            return copy;
        }

        if (node is JsonValue jsonValue && jsonValue.TryGetValue(out string? text) && text is not null)
        {
            return text.Length <= MaxFactStringChars
                ? JsonValue.Create(text)!
                : JsonValue.Create(text[..MaxFactStringChars] + "…")!;
        }

        return node.DeepClone();
    }

    private static void CopyScalar(JsonObject source, string sourceKey, JsonObject target, string targetKey)
    {
        if (!source.TryGetPropertyValue(sourceKey, out JsonNode? node) || node is null)
            return;

        target[targetKey] = Bound(node, 3);
    }

    /// <summary>
    /// Reads the step's configured gate. Null for absent/blank/whitespace JSON and for a blob that
    /// does not parse — both mean "today's path", never an error: an operator's typo must not be
    /// able to fail or slow a review loop.
    /// </summary>
    internal static ReviewLoopStepConfig? TryReadConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ReviewLoopStepConfig>(json, ConfigJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The score ceiling an overflowing narration line imposes on the review loop. <c>4</c> is the
    /// same number <c>VideoReviewAgent</c>'s prompt tells the reviewer to cap at for
    /// <c>overflowLineCount &gt; 0</c>, and both are below every video template's <c>MinScore</c>
    /// (8), which is what makes the capped score actually iterate the loop rather than merely
    /// score lower.
    /// </summary>
    internal const int NarrationOverflowScoreCap = 4;

    /// <summary>
    /// The score ceiling for narration that was DECLARED and not applied at all — the sibling of
    /// <see cref="NarrationOverflowScoreCap"/> for the other half of the silence story.
    ///
    /// <para>
    /// Deliberately the same number and for the same reason: it is below every video template's
    /// <c>MinScore</c> (8), so the capped score iterates the loop rather than merely scoring lower.
    /// The two carry separate constants because they are separate defects with separate evidence —
    /// "a line overran its window" and "no line reached the picture at all" — and a future wave must
    /// be able to retune one without silently moving the other.
    /// </para>
    /// </summary>
    internal const int NarrationDeclaredLostScoreCap = 4;

    /// <summary>
    /// The compile node's terminal outcome token for "at least one line was declared and not one of
    /// them resolved". Set by <c>VideoCompileStepExecutor</c> at exactly that condition, and by
    /// nothing else, so it is a decision value rather than prose. See
    /// <see cref="IsDeclaredAndLostNarration"/> for why the cap accepts it alongside the
    /// declared-vs-applied key pair.
    /// </summary>
    internal const string AllLinesUnavailableReason = "all_lines_unavailable";

    /// <summary>
    /// Applies the narration picture-window overflow cap to a review score: <c>min(score, 4)</c>
    /// when the prior step's output carries a TOP-LEVEL <c>voiceover.fit</c> with
    /// <c>applicable: true</c> and a positive <c>overflowLineCount</c>, and <paramref name="score"/>
    /// unchanged otherwise.
    ///
    /// <para>
    /// <b>Why this exists next to the prompt.</b> <c>VideoReviewAgent</c> is told to score no
    /// higher than 4 when narration overruns the picture it was written for. That instruction is
    /// what makes the reviewer's FEEDBACK actionable, but it is a request to a model — one that
    /// ignores it silently reintroduces exactly the "a fact nothing reads" failure this cap
    /// prevents. The number below is deterministic and cannot be declined.
    /// </para>
    ///
    /// <para>
    /// <b>The prior output is the immediately preceding step's output</b>
    /// (<c>WorkflowExecutorService</c> assigns <c>accumulatedOutput = result.Output</c> after each
    /// step), which for every video-editing template's ReviewLoop is the <c>VideoCompile</c> step —
    /// the node that publishes the fit.
    /// </para>
    ///
    /// <para>
    /// <b>Inert without measurable fit facts, and that is the contract.</b> <c>voiceover.fit</c> is
    /// read at the top level only, and the method returns the score untouched when the node is
    /// absent, when <c>fit</c> is absent or not an object, when <c>applicable</c> is not literally
    /// <c>true</c>, or when <c>overflowLineCount</c> is not a positive number. The three degraded
    /// compile shapes — <c>fit.applicable:false</c> (whose <c>reason</c> is
    /// <c>no_resolved_lines</c>, the only value reachable from the mapped path — the
    /// <c>window_not_found</c> arm is unreachable there, see
    /// <c>VideoCompileStepExecutor.BuildVoiceoverFitNode</c>), no <c>fit</c> node, and a
    /// <c>voiceover</c> node carrying
    /// <c>reason: voiceover_not_enabled</c> — all land in that first arm, so a workflow without
    /// narration is byte-identically unaffected. A PARTIAL measurement is not exempted: a non-empty
    /// <c>unknownWindowLineIds</c> simply means fewer lines were measured, and the lines that WERE
    /// measured still count.
    /// </para>
    ///
    /// <para>
    /// Never raises a score (<c>min</c>, not <c>max</c>), never throws — malformed, empty or
    /// non-object JSON returns the score unchanged, exactly like every other soft-failure path in
    /// this executor — and rewrites nothing: it returns an int, and the reviewer's own output JSON
    /// is persisted as the reviewer wrote it.
    /// </para>
    /// </summary>
    internal static int ApplyNarrationFitCap(int score, string? priorOutput)
    {
        // Top level, and only top level: this is the compile step's envelope, not a nested search.
        JsonObject? voiceover = TryReadVoiceoverNode(priorOutput);
        if (voiceover is null)
            return score;

        if (voiceover["fit"] is not JsonObject fit)
            return score;

        // `applicable` must be literally true. Anything else — absent, false, a non-boolean — means
        // "not measured", which is never a defect and therefore never a cap.
        if (fit["applicable"] is not JsonValue applicableNode ||
            !applicableNode.TryGetValue(out bool applicable) ||
            !applicable)
        {
            return score;
        }

        // Read as a double so an integral number written as `1.0` is still read as a count; a
        // missing, non-numeric, zero or negative value is not an overflow.
        if (fit["overflowLineCount"] is not JsonValue overflowNode ||
            !overflowNode.TryGetValue(out double overflowLineCount) ||
            overflowLineCount <= 0)
        {
            return score;
        }

        return Math.Min(score, NarrationOverflowScoreCap);
    }

    /// <summary>
    /// Reads the TOP-LEVEL <c>voiceover</c> node out of a step's raw output, or <c>null</c> for
    /// every shape that does not carry one — absent, empty, whitespace, unparseable, a non-object
    /// document, or a document whose <c>voiceover</c> member is not an object.
    ///
    /// <para>
    /// Shared by both narration caps so they walk the envelope exactly once each and cannot drift
    /// apart on what counts as "there is a node here". Never throws: the only failure mode a
    /// hostile or malformed prior output can produce is <see cref="JsonException"/>, and a
    /// non-object member is handled by the pattern match rather than by a cast.
    /// </para>
    /// </summary>
    private static JsonObject? TryReadVoiceoverNode(string? priorOutput)
    {
        if (string.IsNullOrWhiteSpace(priorOutput))
            return null;

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(RobustJsonExtractor.ExtractJsonObject(priorOutput) ?? priorOutput);
        }
        catch (JsonException)
        {
            return null;
        }

        return parsed is JsonObject envelope && envelope["voiceover"] is JsonObject voiceover
            ? voiceover
            : null;
    }

    /// <summary>
    /// The deterministic cap for narration that was DECLARED and not applied — the sibling of
    /// <see cref="ApplyNarrationFitCap"/>, and the half the overflow cap structurally cannot reach.
    ///
    /// <para>
    /// <b>The defect.</b> <see cref="VoiceoverStepExecutor"/> can complete with every declared line
    /// failed, and <c>VideoCompileStepExecutor</c> then renders a green, narration-less video whose
    /// own comment calls it out. That node carries NO measurable fit (<c>fit.applicable:false</c>),
    /// so <see cref="ApplyNarrationFitCap"/> is inert to it by design — and a green reviewer score
    /// would advance the loop. "Nobody asked for narration" and "narration was asked for and every
    /// line was lost" must not be the same outcome, and this is the half that says so.
    /// </para>
    /// <para>
    /// <b>How the node reports it.</b> <see cref="IsDeclaredAndLostNarration"/> accepts either of
    /// the two machine-readable forms the node can carry: the declared-vs-applied KEYS, or the
    /// compile's own terminal outcome token. Neither is prose, and neither is inferred.
    /// </para>
    /// <para>
    /// <b>Inert wherever it must be, and deliberately NOT inert on the amix-<c>unavailable</c>
    /// arm.</b> No <c>voiceover</c> node at all (<c>EnableVoiceover:false</c>), a node whose
    /// <c>applied</c> is not literally false, an <c>appliedLineCount</c> that is absent,
    /// non-numeric or non-zero (which is every partial resolve), and the genuinely-empty plan —
    /// whose declared count is zero and which therefore carries neither form — all return the score
    /// untouched. The amix-unavailable node does NOT: its cause is environmental (an ffmpeg build
    /// whose <c>amix</c> lacks <c>normalize</c>) and a loop cannot fix it, but leaving it inert
    /// means a green render with no narration, which is the one failure this cap exists to prevent.
    /// A bounded loop that ends not-green beats a silent green; the reviewer's feedback names the
    /// cause. Never raises a score (<c>min</c>, not <c>max</c>), never throws, and rewrites
    /// nothing: the reviewer's own output JSON is persisted as the reviewer wrote it.
    /// </para>
    /// </summary>
    internal static int ApplyNarrationDeclaredLostCap(int score, string? priorOutput)
    {
        JsonObject? voiceover = TryReadVoiceoverNode(priorOutput);
        if (voiceover is null || !IsDeclaredAndLostNarration(voiceover))
            return score;

        return Math.Min(score, NarrationDeclaredLostScoreCap);
    }

    /// <summary>
    /// The score ceiling for narration of which MORE THAN HALF the declared lines are missing from
    /// the render. Same number as its siblings, for the same reason: below every video template's
    /// <c>MinScore</c> (8), so the loop iterates instead of passing a mostly mute narrated video.
    /// </summary>
    internal const int NarrationMostlyDroppedScoreCap = 4;

    /// <summary>
    /// The deterministic cap for narration that was MOSTLY lost — the gap between the two caps
    /// above. <see cref="ApplyNarrationDeclaredLostCap"/> fires only when not one line resolved,
    /// and <see cref="ApplyNarrationFitCap"/> only measures the lines that did; a compile that
    /// placed 1 of 8 lines (the multi-clip narration bug: 7 lines dropped as <c>line_cut_away</c>)
    /// passed review green with ~60 s of silence. Here: <c>lineCount &gt; 0</c> and
    /// <c>(lineCount - appliedLineCount) * 2 &gt; lineCount</c> on the compile's TOP-LEVEL
    /// <c>voiceover</c> node — the same declared-vs-applied pair the compile's own
    /// <c>narration_mostly_dropped</c> warning is computed from, so the warning the customer sees
    /// and the loop decision cannot disagree. Exactly half missing is not "mostly". Inert (score
    /// unchanged) for every node without both counts, never raises a score, never throws.
    /// </summary>
    internal static int ApplyNarrationMostlyDroppedCap(int score, string? priorOutput)
    {
        JsonObject? voiceover = TryReadVoiceoverNode(priorOutput);
        if (voiceover is null)
            return score;

        if (voiceover["lineCount"] is not JsonValue declaredNode ||
            !declaredNode.TryGetValue(out double declared) || declared <= 0 ||
            voiceover["appliedLineCount"] is not JsonValue appliedNode ||
            !appliedNode.TryGetValue(out double applied) || applied < 0)
        {
            return score;
        }

        return (declared - applied) * 2 > declared
            ? Math.Min(score, NarrationMostlyDroppedScoreCap)
            : score;
    }

    /// <summary>
    /// Every narration cap, in the order the step applies them. They are independent
    /// <c>Math.Min</c>s, so the order is immaterial — it is fixed here only so a caller cannot pick
    /// one and forget another.
    /// </summary>
    internal static int ApplyNarrationCaps(int score, string? priorOutput)
        => ApplyNarrationMostlyDroppedCap(
            ApplyNarrationDeclaredLostCap(ApplyNarrationFitCap(score, priorOutput), priorOutput),
            priorOutput);

    /// <summary>
    /// The score of a step whose reviewer never ran, chosen so that "a cap fired" is decidable by
    /// calling the caps themselves: both are <c>Math.Min(score, 4)</c>, so
    /// <c>ApplyNarrationCaps(Sentinel, …) &lt; Sentinel</c> is true exactly when one of them would
    /// have lowered a real score. That is deliberate — the predicate cannot drift from the caps it
    /// is supposed to describe, because it IS them.
    /// </summary>
    internal const int UncappedProbeScore = int.MaxValue;

    /// <summary>
    /// True when the deterministic narration caps would lower a score for this prior output.
    ///
    /// <para>
    /// Used by the calibrated decision gate's early stop: that path returns BEFORE the reviewer runs
    /// and therefore before <see cref="ApplyNarrationCaps"/> is applied, so without this check the
    /// gate could accept — and skip the reviewer for — a step whose narration defect the caps exist
    /// to catch. Being conservative here costs only an escalations the gate already treats as its
    /// safe failure mode; being permissive would make the two caps declinable by configuration.
    /// </para>
    /// <para>
    /// A pure function over the same two caps, so it never throws where they do not.
    /// </para>
    /// </summary>
    internal static bool NarrationCapsWouldFire(string? priorOutput)
        => ApplyNarrationCaps(UncappedProbeScore, priorOutput) < UncappedProbeScore;

    /// <summary>
    /// The score ceiling for an edit that left offered clips unused (or a composition unbroken)
    /// without a verified reason from the brief. Below every video template's <c>MinScore</c> (8),
    /// so the loop goes back to the edit instead of passing it; above the narration caps (4),
    /// because a single-clip edit is an editorial omission, not missing or broken content.
    /// </summary>
    internal const int InterleaveScoreCap = 6;

    /// <summary>
    /// The deterministic interleave cap: <c>min(score, 6)</c> when the compile step's TOP-LEVEL
    /// <c>interleave</c> node says <c>required: true</c> and <c>satisfied: false</c> — both literally.
    /// The verdict is computed in code by <c>InterleaveGuard</c> (several usable clips offered, every
    /// kept span from one of them or a composition kept unbroken, and no verbatim quote from the brief
    /// excusing it), so the reviewer's prompt asking for the same cap is feedback, not the guarantee.
    /// Inert for every other shape — no node (single-source compiles, KeepWholeSources), a satisfied
    /// verdict, a malformed document. Never raises a score, never throws, rewrites nothing.
    /// </summary>
    internal static int ApplyInterleaveCap(int score, string? priorOutput)
    {
        if (string.IsNullOrWhiteSpace(priorOutput))
            return score;

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(RobustJsonExtractor.ExtractJsonObject(priorOutput) ?? priorOutput);
        }
        catch (JsonException)
        {
            return score;
        }

        if (parsed is not JsonObject envelope || envelope["interleave"] is not JsonObject interleave)
            return score;

        bool required = interleave["required"] is JsonValue r && r.TryGetValue(out bool rv) && rv;
        bool unsatisfied = interleave["satisfied"] is JsonValue s && s.TryGetValue(out bool sv) && !sv;
        return required && unsatisfied ? Math.Min(score, InterleaveScoreCap) : score;
    }

    /// <summary>
    /// The score ceiling for a compile that was asked to put content on tracked screens and put none there
    /// (T-206). The same level as <see cref="InterleaveScoreCap"/>: below every video template's pass mark, so
    /// the loop goes back with the regions and the compile's reasons instead of passing a film with a bare plate.
    /// </summary>
    internal const int InsertsNotAppliedScoreCap = 6;

    /// <summary>
    /// The deterministic inserts cap: <c>min(score, 6)</c> when the compile step's TOP-LEVEL <c>inserts</c> node
    /// says <c>requested &gt; 0</c> and <c>applied: false</c>, both literally — the reviewer being told to fail
    /// such a film is feedback, this is the guarantee. Inert for every other shape (no node, nothing requested,
    /// some inserts applied, a malformed document). Never raises a score, never throws, rewrites nothing.
    /// </summary>
    internal static int ApplyInsertsNotAppliedCap(int score, string? priorOutput)
        => ReadUnappliedInserts(priorOutput) is null ? score : Math.Min(score, InsertsNotAppliedScoreCap);

    /// <summary>
    /// The compile's <c>inserts</c> node when it requested inserts and applied none, else null. Shared by the cap
    /// and its plain-words reason, so the two cannot disagree.
    /// </summary>
    private static JsonObject? ReadUnappliedInserts(string? priorOutput)
    {
        if (string.IsNullOrWhiteSpace(priorOutput))
            return null;

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(RobustJsonExtractor.ExtractJsonObject(priorOutput) ?? priorOutput);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed is not JsonObject envelope || envelope["inserts"] is not JsonObject inserts)
            return null;

        bool requested = inserts["requested"] is JsonValue r && r.TryGetValue(out int count) && count > 0;
        bool notApplied = inserts["applied"] is JsonValue a && a.TryGetValue(out bool applied) && !applied;
        return requested && notApplied ? inserts : null;
    }

    /// <summary>Most dropped regions the cap reason names; the rest are counted.</summary>
    private const int MaxNamedDroppedInserts = 6;

    /// <summary>"screen inserts not applied (r0: confidence_below_threshold; r1: content_not_found)".</summary>
    internal static string InsertsNotAppliedReason(string? priorOutput)
    {
        JsonObject? inserts = ReadUnappliedInserts(priorOutput);
        List<string> named = [];
        int total = 0;
        if (inserts?["droppedInserts"] is JsonArray dropped)
        {
            foreach (JsonNode? item in dropped)
            {
                if (item is not JsonObject d)
                    continue;
                total++;
                if (named.Count >= MaxNamedDroppedInserts)
                    continue;
                string region = Token(d["regionId"]) ?? "?";
                string reason = Token(d["reason"]) ?? "unknown";
                named.Add($"{region}: {reason}");
            }
        }

        string detail = named.Count == 0
            ? Token(inserts?["reason"]) is { } why ? why : "no region was applied"
            : string.Join("; ", named) + (total > named.Count ? $"; and {total - named.Count} more" : string.Empty);
        return $"screen inserts not applied ({detail})";

        // Region ids and drop reasons are the compile's own tokens; bounded and stripped anyway, since they reach a prompt.
        static string? Token(JsonNode? node)
        {
            if (node is not JsonValue v || !v.TryGetValue(out string? text) || string.IsNullOrWhiteSpace(text))
                return null;
            string clean = new(text.Where(c => !char.IsControl(c)).ToArray());
            return clean.Length <= 160 ? clean : clean[..160];
        }
    }

    /// <summary>Every deterministic cap the loop applies: the narration caps, the interleave cap and the inserts cap.</summary>
    internal static int ApplyDeterministicCaps(int score, string? priorOutput)
        => ReviewEditFacts.ApplyCaps(ApplyInsertsNotAppliedCap(ApplyInterleaveCap(ApplyNarrationCaps(score, priorOutput), priorOutput), priorOutput), priorOutput);

    /// <summary>
    /// True when any deterministic cap would lower a score for this prior output — the calibrated early
    /// stop's guard, so no cap can be declined by configuration (see <see cref="NarrationCapsWouldFire"/>).
    /// </summary>
    internal static bool DeterministicCapsWouldFire(string? priorOutput)
        => ApplyDeterministicCaps(UncappedProbeScore, priorOutput) < UncappedProbeScore;

    /// <summary>
    /// The score a review loop gated on, and why it differs from the reviewer's. <c>ReviewerScore</c> is
    /// what <see cref="ParseReviewScore"/> read; <c>Score</c> is after every deterministic cap; <c>CapReason</c>
    /// names, in plain words, the cap that lowered it, and is null when no cap did.
    /// </summary>
    internal readonly record struct CappedReviewScore(int ReviewerScore, int Score, string? CapReason);

    /// <summary>Plain-words reasons for the caps, as the Author reads them in the revision brief.</summary>
    private const string NarrationOverflowCapReason = "narration overflow";
    private const string NarrationDeclaredLostCapReason = "narration declared but lost";
    private const string NarrationMostlyDroppedCapReason = "most narration dropped";
    private const string InterleaveCapReason = "offered clips left unused";

    /// <summary>
    /// Scores one review exactly as the loop does, and says which cap lowered it. The number is
    /// <see cref="ApplyDeterministicCaps"/>' own, so it cannot drift from the score the loop decides on.
    /// </summary>
    internal static CappedReviewScore ScoreReview(string reviewOutput, string? priorOutput, AgentType? agentType = null, bool visualCheckEnabled = false, bool renderQualityEnabled = false)
    {
        int reviewerScore = ParseReviewScore(reviewOutput, agentType);
        int score = ApplyDeterministicCaps(reviewerScore, priorOutput);
        string? capReason = score < reviewerScore ? DeterministicCapReason(reviewerScore, score, priorOutput) : null;

        // QA-P1-15: the visual check's cap is recorded in the iteration output itself (docs/video-editing.md
        // "Visual check in review loops"), so it is read back from there — but ONLY when the run's own
        // configuration enabled the check. With the check off nothing this executor wrote can cap a score,
        // so a node that is present anyway (a model's own text) must not lower it either.
        if (visualCheckEnabled)
        {
            int visualCapped = VisualCheckReport.ApplyRecordedCap(score, reviewOutput, agentType);
            if (visualCapped < score)
                (score, capReason) = (visualCapped, VisualCheckReport.CapReason);
        }

        // Same rule for the render quality node: read back only when this step's own config turned QualityChecks on.
        if (renderQualityEnabled)
        {
            int qualityCapped = RenderQualityReport.ApplyRecordedCap(score, reviewOutput, agentType);
            if (qualityCapped < score)
                (score, capReason) = (qualityCapped, RenderQualityReport.CapReason);
        }

        int lengthCapped = LengthCheckReport.ApplyRecordedCap(score, reviewOutput, agentType);
        if (lengthCapped < score)
            (score, capReason) = (lengthCapped, LengthCheckReport.CapReason);

        return new CappedReviewScore(reviewerScore, score, capReason);
    }

    /// <summary>
    /// QA-P1-15: whether this ReviewLoop step's own configuration enables the visual check — the one
    /// condition under which <see cref="ScoreReview"/> may read a recorded <c>visualCheck</c> cap.
    /// </summary>
    /// <summary>Whether this ReviewLoop step's own configuration allows adjustments (docs/design/review-loop-adjustments.md).</summary>
    internal static bool AdjustmentsAllowed(WorkflowStep step) =>
        TryReadConfig(step.ReviewLoopConfigJson) is { AllowAdjustments: true };

    internal static bool RenderQualityEnabled(WorkflowStep step) =>
        TryReadConfig(step.ReviewLoopConfigJson)?.VisualCheck is { QualityChecks: true };

    internal static bool VisualCheckEnabled(WorkflowStep step) =>
        TryReadConfig(step.ReviewLoopConfigJson)?.VisualCheck is { Enabled: true } or { TargetedLooks: true };

    /// <summary>
    /// The first cap whose own ceiling is the final score. Each cap is a <c>Math.Min</c> on the score
    /// alone, so the one that set the final number is the one that returns it when applied to the
    /// reviewer's score by itself.
    /// </summary>
    private static string? DeterministicCapReason(int reviewerScore, int score, string? priorOutput)
    {
        if (ApplyNarrationFitCap(reviewerScore, priorOutput) == score)
            return NarrationOverflowCapReason;
        if (ApplyNarrationDeclaredLostCap(reviewerScore, priorOutput) == score)
            return NarrationDeclaredLostCapReason;
        if (ApplyNarrationMostlyDroppedCap(reviewerScore, priorOutput) == score)
            return NarrationMostlyDroppedCapReason;
        // The inserts cap is asked before the interleave cap: both ceilings are the same number, so a
        // compile that fires both cannot be told apart by the score, and the inserts reason is the one
        // that names the regions and why they were dropped — the detail the Author has to act on
        // (QA-P2-9). Asking the interleave cap first lost every region name.
        if (ApplyInsertsNotAppliedCap(reviewerScore, priorOutput) == score)
            return InsertsNotAppliedReason(priorOutput);
        if (ApplyInterleaveCap(reviewerScore, priorOutput) == score)
            return InterleaveCapReason;
        if (ReviewEditFacts.CapReason(score, priorOutput) is { } editReason && ReviewEditFacts.ApplyCaps(reviewerScore, priorOutput) == score)
            return editReason;
        return null;
    }

    /// <summary>"capped from 9: offered clips left unused", for a score a cap lowered.</summary>
    private static string CappedFromNote(CappedReviewScore scored) =>
        $"capped from {scored.ReviewerScore}: {scored.CapReason}";

    /// <summary>
    /// Whether a compile <c>voiceover</c> node reports narration that was declared and not applied.
    ///
    /// <para>
    /// <b>Two forms, because the node can carry the fact in two places.</b> The first is the
    /// declared-vs-applied key pair — <c>lineCount &gt; 0</c> with <c>appliedLineCount == 0</c> —
    /// which is the form the contract names. The second is the compile node's own terminal outcome
    /// token, <c>reason: "all_lines_unavailable"</c>, which the node sets if and only if at least
    /// one line was declared and not one resolved.
    /// </para>
    /// <para>
    /// <b>Which form the shipped node actually carries.</b> The KEY PAIR is the one every
    /// declared-and-lost arm of the shipped compile emits: <c>Finish()</c> reports the DECLARED
    /// count under <c>lineCount</c> (corrected on the media side in <c>0924b7cc</c>), so
    /// <c>lineCount &gt; 0</c> with <c>appliedLineCount == 0</c> is true for all three arms that
    /// produce this defect — the all-lines-lost arm (<c>reason: all_lines_unavailable</c>), the
    /// usability-filter arm of <c>ResolveVoiceoverAsync</c> (<c>voiceoverLinesList.Count == 0</c>
    /// reached with <c>declaredLineCount &gt; 0</c>, i.e. every declared line was filtered out for a
    /// non-positive <c>durationSec</c>) and the amix-unavailable arm (an environment reason with
    /// <c>unavailable: true</c> and NO <c>all_lines_unavailable</c> token at all).
    /// </para>
    /// <para>
    /// <b>The usability-filter arm is identified by its condition and its count, never by a reason
    /// it does not keep.</b> That arm assigns <c>no_valid_voiceover_lines</c> itself, but the
    /// <c>!amixUnavailable</c>-guarded overwrite in <c>Finish()</c> runs after it — <c>Finish()</c>
    /// is what the arm returns through — and replaces that reason with
    /// <c>all_lines_unavailable</c> whenever <c>declaredLineCount &gt; 0</c>, exactly as it does on
    /// the all-lines-lost arm. So with lines declared, the surviving reason is
    /// <c>all_lines_unavailable</c>, and <c>no_valid_voiceover_lines</c> survives only when
    /// <c>declaredLineCount == 0</c>: the empty plan, which reports <c>lineCount</c> 0 and is
    /// precisely the false positive this predicate must avoid. Naming that arm by its own reason
    /// would therefore describe a node the compile cannot produce; the key pair is what matches it.
    /// </para>
    /// <para>
    /// The token form is retained for a node produced
    /// before that correction; it is a closed terminal value rather than free prose, and it is the
    /// same kind of token the rest of this codebase decides on. No new form is added — the key pair
    /// already covers every arm the shipped compile can emit, so this predicate is unchanged, and
    /// what this fact required instead was the reviewer's prompt.
    /// </para>
    /// </summary>
    internal static bool IsDeclaredAndLostNarration(JsonObject voiceover)
    {
        // `applied` must be literally false. Absent, non-boolean or true is not this defect.
        if (voiceover["applied"] is not JsonValue appliedNode ||
            !appliedNode.TryGetValue(out bool applied) ||
            applied)
        {
            return false;
        }

        // `appliedLineCount` must be literally zero. A positive value is a PARTIAL resolve, which
        // this cap deliberately leaves to the fit cap rather than treating as a total loss.
        if (voiceover["appliedLineCount"] is not JsonValue appliedCountNode ||
            !appliedCountNode.TryGetValue(out double appliedLineCount) ||
            appliedLineCount != 0)
        {
            return false;
        }

        // Form 1 — the declared-vs-applied keys, and the form every shipped declared-and-lost arm
        // emits (see the remark on this method). This is the one that fires on the amix-unavailable
        // arm, whose `reason` is an environment sentence and therefore carries no token at all.
        if (voiceover["lineCount"] is JsonValue lineCountNode &&
            lineCountNode.TryGetValue(out double lineCount) &&
            lineCount > 0)
        {
            return true;
        }

        // Form 2 — the compile node's terminal outcome token for "declared at least one, resolved
        // none". Absent on the empty plan (nothing was declared), so the two silences stay apart.
        return voiceover["reason"] is JsonValue reasonNode &&
            reasonNode.TryGetValue(out string? reason) &&
            string.Equals(reason, AllLinesUnavailableReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Internal (not private) so both the main pipeline and video-editing review outputs' scores
    /// are directly unit-testable. The root key read depends on the reviewer's agent type (T-019, see
    /// <see cref="ReviewScoreKeysFor"/>): "overallScore" for <c>AgentType.ReviewAgent</c>, "score" for
    /// <c>AgentType.VideoReviewAgent</c>, and "overallScore" then "score" for an unknown agent. A type
    /// that is not supplied is read as unknown. Historically this method only ever checked "score",
    /// which does not exist at the root of a ReviewOutput completion, so the main pipeline's
    /// review score silently always parsed as 0 (always below MinScore) regardless of what the
    /// review agent actually judged — found while wiring this same method for the new
    /// video-editing review loop, which needed to confirm the property name it should use.
    /// </summary>
    internal static int ParseReviewScore(string reviewOutput, AgentType? agentType = null)
    {
        try
        {
            return ToReviewScore(ScanReviewVerdicts(reviewOutput, agentType));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // A lone surrogate (escaped "\ud800" -> InvalidOperationException; a raw unpaired char ->
            // ArgumentException "Cannot transcode invalid UTF-16") makes System.Text.Json throw on read: the review is unreadable,
            // so it fails closed and the loop goes round again instead of failing the paid run (SEC-18).
            return 0;
        }
    }

    /// <summary>The root key a ReviewAgent verdict is read from, and only that one (T-019).</summary>
    private static readonly string[] ReviewAgentScoreKeys = ["overallScore"];

    /// <summary>The root key a VideoReviewAgent verdict is read from, and only that one (T-019).</summary>
    private static readonly string[] VideoReviewAgentScoreKeys = ["score"];

    /// <summary>Any other or unknown agent: "overallScore" then "score", root only, whichever the root carries first.</summary>
    private static readonly string[] UnknownAgentScoreKeys = ["overallScore", "score"];

    /// <summary>
    /// The root keys a reviewer's verdict is read from, for the agent that produced it (T-019). The
    /// declared root of each schema is its only key: <c>ReviewOutput</c> is "overallScore",
    /// <c>VideoReviewOutput</c> is "score". Any other or unknown agent type reads "overallScore" then
    /// "score". These same keys are the ones whose stray occurrences fail the review closed.
    /// </summary>
    internal static IReadOnlyList<string> ReviewScoreKeysFor(AgentType? agentType) => agentType switch
    {
        AgentType.ReviewAgent => ReviewAgentScoreKeys,
        AgentType.VideoReviewAgent => VideoReviewAgentScoreKeys,
        _ => UnknownAgentScoreKeys,
    };

    /// <summary>
    /// The verdict scan the review score and the revision brief are both read from, for the reviewer's
    /// agent type. See <see cref="RobustJsonExtractor.ScanVerdicts"/>.
    /// </summary>
    internal static RobustJsonExtractor.VerdictScan ScanReviewVerdicts(string reviewOutput, AgentType? agentType) =>
        RobustJsonExtractor.ScanVerdicts(reviewOutput, ReviewScoreKeysFor(agentType));

    /// <summary>
    /// The 0-10 loop score of a scan: the verdict's score rounded DOWN (7.5 is 7, which fails a pass
    /// mark of 8; the output schemas are integers, so a fraction is out of schema). Zero, which loops
    /// back, when no verdict was accepted or any verdict key is unaccounted for (T-019).
    /// </summary>
    private static int ToReviewScore(RobustJsonExtractor.VerdictScan scan) =>
        scan.Verdict is null || scan.UnaccountedKeyCount > 0
            ? 0
            : (int)Math.Clamp(Math.Floor(scan.Verdict.Score), 0, 10);

    /// <summary>
    /// The JSON the revision brief is built from. The verdict object <see cref="ScanReviewVerdicts"/>
    /// chose, so the brief always comes from the object the score came from. When no object is an
    /// accepted verdict, the first balanced object, as before; failing that, the raw text. Only the
    /// brief reads these, never the score, so a truncated object still scores 0.
    /// </summary>
    private static string SelectReviewJson(string reviewOutput, AgentType? agentType) =>
        ScanReviewVerdicts(reviewOutput, agentType).Verdict?.Json
        ?? RobustJsonExtractor.ExtractJsonObject(reviewOutput)
        ?? reviewOutput;

    /// <summary>
    /// Reads a review score as an int, tolerating a non-integer JSON number (e.g. <c>8.5</c> —
    /// entirely plausible output for a "score 1-10" prompt). <see cref="JsonElement.GetInt32"/>
    /// throws <see cref="FormatException"/> — NOT <see cref="JsonException"/> — for a non-integer
    /// number, which the caller's <c>catch (JsonException)</c> does not cover, so an uncaught
    /// exception would previously propagate out of this "never fails the loop" scoring helper.
    /// Falls back to the rounded double value, then clamps to a sane 0-10 range.
    /// </summary>
    private static int ReadScore(JsonElement scoreProp)
    {
        int score = scoreProp.TryGetInt32(out int intValue)
            ? intValue
            : (int)Math.Round(scoreProp.GetDouble(), MidpointRounding.AwayFromZero);

        return Math.Clamp(score, 0, 10);
    }

    /// <summary>
    /// Builds the feedback text a loop-back step is handed from a review agent's raw JSON output.
    /// Schema-tolerant across the two reviewer shapes that flow through this ReviewLoop machinery:
    /// <list type="bullet">
    /// <item><c>ReviewOutput</c> (AgentType.ReviewAgent, the promo pipeline): recognised by an
    /// <c>overallScore</c> or a <c>criteria</c> array, and rendered as a multi-line revision brief —
    /// the overall score against <paramref name="minScore"/> (the step's pass mark), the summary,
    /// every criterion's name, score and feedback, and the improvement areas. Before this, only the
    /// summary and improvement areas reached the Author, and the per-criterion feedback (the part
    /// that says what to change) was dropped.</item>
    /// <item><c>VideoReviewOutput</c> (AgentType.VideoReviewAgent) and anything else: the one-line
    /// "summary Issues: a; b" form it has always had.</item>
    /// </list>
    /// Used by <c>WorkflowExecutorService</c> to seed the loop-back window's
    /// <see cref="Execution.StepExecutionContext.RecordReviewFeedback"/>. Returns null when nothing
    /// usable could be extracted (malformed JSON, or no summary/issues/criteria present at all).
    /// When <paramref name="scored"/> is given (see <see cref="ScoreReview"/>), the overall score stated is the
    /// score the loop gated on, and a cap that lowered it is named there.
    /// </summary>
    internal static string? ExtractFeedbackSummary(
        string reviewOutput, int? minScore = null, CappedReviewScore? scored = null, AgentType? agentType = null)
        => LengthCheckReport.WithBriefBlock(
            RenderQualityReport.WithBriefBlock(
                VisualCheckReport.WithBriefBlock(ExtractReviewerFeedback(reviewOutput, minScore, scored, agentType), reviewOutput, agentType),
                reviewOutput, agentType),
            reviewOutput, agentType);

    /// <summary>The reviewer's own part of <see cref="ExtractFeedbackSummary"/>, before any visual check block.</summary>
    private static string? ExtractReviewerFeedback(string reviewOutput, int? minScore, CappedReviewScore? scored, AgentType? agentType)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(SelectReviewJson(reviewOutput, agentType));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            string? summary = ReadNonBlankString(root, "summary");

            bool isReviewOutputShape =
                (root.TryGetProperty("criteria", out JsonElement criteriaProp) && criteriaProp.ValueKind == JsonValueKind.Array) ||
                (root.TryGetProperty("overallScore", out JsonElement overall) && overall.ValueKind == JsonValueKind.Number);

            if (isReviewOutputShape)
                return BuildReviewOutputBrief(root, summary, minScore, scored);

            List<string> parts = new();
            if (scored is { CapReason: not null } capped)
                parts.Add($"Score {CappedFromNote(capped)}.");
            if (summary is not null)
                parts.Add(summary);

            List<string> issues = new();
            foreach (string propName in new[] { "issues", "improvementAreas" })
                issues.AddRange(ReadNonBlankStrings(root, propName));

            if (issues.Count > 0)
                parts.Add("Issues: " + string.Join("; ", issues));

            return parts.Count == 0 ? null : string.Join(" ", parts);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            // InvalidOperationException: a lone surrogate in the review JSON (SEC-18). No brief, never a throw.
            return null;
        }
    }

    /// <summary>
    /// The <c>ReviewOutput</c> rendering of <see cref="ExtractFeedbackSummary"/>. One fact per line,
    /// so a model reads each criterion as its own instruction.
    /// </summary>
    private static string? BuildReviewOutputBrief(JsonElement root, string? summary, int? minScore, CappedReviewScore? scored)
    {
        List<string> lines = new();

        if (root.TryGetProperty("overallScore", out JsonElement overall) && overall.ValueKind == JsonValueKind.Number)
        {
            string passMark = minScore is { } min ? $" (pass mark {min})" : string.Empty;
            int shown = scored?.Score ?? ReadScore(overall);
            string cap = scored is { CapReason: not null } capped ? $" ({CappedFromNote(capped)})" : string.Empty;
            lines.Add($"Overall score: {shown}/10{passMark}{cap}.");
        }

        if (summary is not null)
            lines.Add("Summary: " + summary);

        List<string> criteria = new();
        if (root.TryGetProperty("criteria", out JsonElement criteriaProp) && criteriaProp.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement criterion in criteriaProp.EnumerateArray())
            {
                if (criterion.ValueKind != JsonValueKind.Object)
                    continue;

                string name = ReadNonBlankString(criterion, "name") ?? "criterion";
                string? feedback = ReadNonBlankString(criterion, "feedback");
                string score = criterion.TryGetProperty("score", out JsonElement s) && s.ValueKind == JsonValueKind.Number
                    ? $" {ReadScore(s)}/10"
                    : string.Empty;

                if (feedback is null && score.Length == 0)
                    continue;

                criteria.Add(feedback is null ? $"- {name}{score}" : $"- {name}{score}: {feedback}");
            }
        }

        if (criteria.Count > 0)
        {
            lines.Add("Per criterion:");
            lines.AddRange(criteria);
        }

        List<string> areas = ReadNonBlankStrings(root, "improvementAreas")
            .Concat(ReadNonBlankStrings(root, "issues"))
            .ToList();
        if (areas.Count > 0)
        {
            lines.Add("Improvement areas:");
            lines.AddRange(areas.Select(a => "- " + a));
        }

        // A bare score with nothing to act on is not feedback.
        bool hasActionableContent = summary is not null || criteria.Count > 0 || areas.Count > 0;
        return hasActionableContent ? string.Join("\n", lines) : null;
    }

    private static string? ReadNonBlankString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement prop) || prop.ValueKind != JsonValueKind.String)
            return null;

        string? value = prop.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static List<string> ReadNonBlankStrings(JsonElement element, string propertyName)
    {
        List<string> values = new();
        if (!element.TryGetProperty(propertyName, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
            return values;

        foreach (JsonElement item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            string? s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s))
                values.Add(s.Trim());
        }

        return values;
    }

    /// <summary>Upper bound on <see cref="BuildPreviousManifestNote"/>'s result.</summary>
    internal const int MaxPreviousManifestNoteChars = 2000;

    /// <summary>
    /// A short note of the compositions (id, component, duration) in an Author's
    /// <c>RenderManifestOutput</c>, for the loop-back Author: the loop-back prunes the Author's own
    /// previous output from the step history, so without this the Author re-runs knowing what the
    /// reviewer said but not what the reviewer was looking at. Bounded to
    /// <see cref="MaxPreviousManifestNoteChars"/>. Null for anything that is not a manifest with at
    /// least one composition.
    /// </summary>
    internal static string? BuildPreviousManifestNote(string? authorOutput)
    {
        if (string.IsNullOrWhiteSpace(authorOutput))
            return null;

        try
        {
            string json = RobustJsonExtractor.ExtractJsonObject(authorOutput) ?? authorOutput;
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("compositions", out JsonElement compositions) ||
                compositions.ValueKind != JsonValueKind.Array ||
                compositions.GetArrayLength() == 0)
            {
                return null;
            }

            int fps = 0;
            int totalFrames = 0;
            if (root.TryGetProperty("video", out JsonElement video) && video.ValueKind == JsonValueKind.Object)
            {
                fps = ReadNonNegativeInt(video, "fps");
                totalFrames = ReadNonNegativeInt(video, "durationInFrames");
            }

            List<string> facts = new();
            if (ReadNonBlankString(root, "projectName") is { } projectName)
                facts.Add($"\"{Truncate(projectName, 80)}\"");
            if (totalFrames > 0)
                facts.Add(FramesText(totalFrames, fps, includeFps: true));

            string header = facts.Count > 0
                ? $"The render the reviewer judged ({string.Join(", ", facts)}) was built from these compositions:"
                : "The render the reviewer judged was built from these compositions:";

            StringBuilder note = new(header);
            int index = 0;
            int total = compositions.GetArrayLength();
            foreach (JsonElement composition in compositions.EnumerateArray())
            {
                if (composition.ValueKind != JsonValueKind.Object)
                {
                    index++;
                    continue;
                }

                string id = Truncate(ReadNonBlankString(composition, "id") ?? "(no id)", 60);
                string? component = ReadNonBlankString(composition, "componentName");
                int frames = ReadNonNegativeInt(composition, "durationInFrames");

                string line = $"\n{index + 1}. {id}" +
                    (component is null ? string.Empty : $" ({Truncate(component, 60)})") +
                    (frames > 0 ? ": " + FramesText(frames, fps, includeFps: false) : string.Empty);

                string remainder = $"\n... and {total - index} more not listed";
                if (note.Length + line.Length + remainder.Length > MaxPreviousManifestNoteChars)
                {
                    note.Append(remainder);
                    break;
                }

                note.Append(line);
                index++;
            }

            return note.Length <= MaxPreviousManifestNoteChars ? note.ToString() : note.ToString(0, MaxPreviousManifestNoteChars);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // Facts from earlier passes of the same review loop (T-007)
    // ---------------------------------------------------------------------

    /// <summary>
    /// The most earlier passes whose numbers are shown to the reviewer. The loop's own
    /// <c>MaxIterations</c> bounds this in practice; this is the defence for a misconfigured step.
    /// </summary>
    internal const int MaxReviewPassFacts = 5;

    /// <summary>
    /// Hard character bound on the rendered earlier-pass block. It leaves room for the five pass
    /// lines AND the "older passes omitted" sentence, so a truncated history is always marked as
    /// truncated rather than silently looking complete.
    /// </summary>
    internal const int MaxReviewPassFactsChars = 700;

    /// <summary>
    /// One earlier pass's numbers. Numbers only — no ids that name footage, no transcript, no agent
    /// prose and no rationale text. <see cref="KeptPieces"/> is the compile's <c>segments</c>,
    /// <see cref="OutputLengthSec"/> its <c>outputDurationSec</c> and <see cref="RetainedRatio"/>
    /// its <c>retainedRatio</c>.
    /// </summary>
    internal readonly record struct ReviewPassFact(
        int Pass, int Score, int KeptPieces, double OutputLengthSec, double RetainedRatio);

    /// <summary>
    /// Reads one pass's numbers out of the plan/cut output the reviewer judged, or null when the
    /// output is not a compile summary carrying all three measured facts. A plan-only compile
    /// carries them too (<c>VideoCompileStepExecutor</c> writes <c>segments</c>,
    /// <c>outputDurationSec</c> and <c>retainedRatio</c> before it skips the encode), so this never
    /// depends on an encoded file existing.
    /// </summary>
    internal static ReviewPassFact? TryReadReviewPassFact(string? compileOutput, int pass, int reviewerScore)
    {
        if (string.IsNullOrWhiteSpace(compileOutput) || pass <= 0)
            return null;

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(RobustJsonExtractor.ExtractJsonObject(compileOutput) ?? compileOutput);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            // Same soft-failure contract as every other reader of a prior step's output here:
            // malformed JSON is "no facts", never a failure of the review step.
            return null;
        }

        if (parsed is not JsonObject source)
            return null;

        if (!TryReadFinite(source, "segments", out double segments) ||
            !TryReadFinite(source, "outputDurationSec", out double outputLengthSec) ||
            !TryReadFinite(source, "retainedRatio", out double retainedRatio))
        {
            return null;
        }

        if (segments < 0 || segments > int.MaxValue || outputLengthSec < 0 || retainedRatio < 0)
            return null;

        return new ReviewPassFact(
            Pass: pass,
            Score: Math.Clamp(reviewerScore, 0, 10),
            KeptPieces: (int)Math.Round(segments, MidpointRounding.AwayFromZero),
            OutputLengthSec: outputLengthSec,
            RetainedRatio: retainedRatio);
    }

    private static bool TryReadFinite(JsonObject source, string key, out double value)
    {
        value = 0;
        return source[key] is JsonValue node && node.TryGetValue(out value) && double.IsFinite(value);
    }

    /// <summary>The bounded earlier-pass block appended to the reviewer's input, or null when there is no earlier pass.</summary>
    internal static string? RenderReviewPassHistory(IReadOnlyList<ReviewPassFact> facts) =>
        RenderReviewPassHistory(facts, MaxReviewPassFacts, MaxReviewPassFactsChars);

    /// <summary>
    /// Renders the earlier-pass block under an explicit pass cap and character cap (the overload is
    /// internal so the bound is testable). Null for no earlier pass — which is what keeps a
    /// one-pass run's prompt input byte-identical. Only the most recent <paramref name="maxPasses"/>
    /// passes are shown; if any were dropped (by either cap) a sentence says so, so the reviewer can
    /// tell a complete history from a truncated one.
    /// </summary>
    internal static string? RenderReviewPassHistory(
        IReadOnlyList<ReviewPassFact> facts, int maxPasses, int maxChars)
    {
        if (facts.Count == 0 || maxChars <= 0)
            return null;

        maxPasses = Math.Max(1, maxPasses);
        int first = Math.Max(0, facts.Count - maxPasses);

        var block = new StringBuilder();
        block.Append("Earlier passes of this run — facts only (numbers, at most ")
            .Append(maxPasses)
            .Append(" earlier passes and ")
            .Append(maxChars)
            .Append(" characters).\n");
        block.Append("No earlier cut is available to you; these numbers are their only trace.\n");

        bool omitted = first > 0;
        for (int i = first; i < facts.Count; i++)
        {
            ReviewPassFact fact = facts[i];
            string line = string.Create(
                CultureInfo.InvariantCulture,
                $"- Pass {fact.Pass}: score {fact.Score}/10; kept {fact.KeptPieces} pieces; output {fact.OutputLengthSec:0.0} s; retained {fact.RetainedRatio * 100:0.0}% of source.\n");

            if (block.Length + line.Length > maxChars)
            {
                omitted = true;
                break;
            }

            block.Append(line);
        }

        if (omitted)
        {
            const string note = "- Older passes omitted to stay inside the size cap.\n";
            if (block.Length + note.Length <= maxChars)
                block.Append(note);
        }

        string rendered = block.ToString().TrimEnd('\n');

        // Defence in depth: the per-line check already bounds this for any real numbers, but the
        // block is prompt text and the cap is the contract, so the final string is checked too.
        return rendered.Length <= maxChars ? rendered : rendered[..maxChars];
    }

    private static int ReadNonNegativeInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement prop) &&
        prop.ValueKind == JsonValueKind.Number &&
        prop.TryGetDouble(out double value) &&
        value > 0 && value < int.MaxValue
            ? (int)Math.Round(value, MidpointRounding.AwayFromZero)
            : 0;

    private static string FramesText(int frames, int fps, bool includeFps)
    {
        if (fps <= 0)
            return $"{frames} frames";

        string seconds = (frames / (double)fps).ToString("0.0", CultureInfo.InvariantCulture);
        return includeFps ? $"{frames} frames at {fps} fps = {seconds} s" : $"{frames} frames = {seconds} s";
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string CreatePreview(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
        if (normalized.Length <= maxLength)
            return normalized;

        return normalized[..maxLength];
    }
}
