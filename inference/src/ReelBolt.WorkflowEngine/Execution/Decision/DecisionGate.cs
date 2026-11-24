using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Observability;

namespace ReelBolt.WorkflowEngine.Execution.Decision;

/// <summary>
/// Runs the System One (fast, cheap) decision model alongside (Shadow) or instead of (Gate) an
/// agent's real System Two decision, and records every question it asked as a
/// <see cref="DecisionObservation"/>.
///
/// <para>
/// <b>What leaves the host.</b> The <see cref="DecisionState"/> handed to
/// <see cref="DecideAsync"/> is transmitted verbatim to the configured Decision provider, which in
/// the normal deployment is a <b>hosted third party</b> — an additional party beyond the model
/// backend the agent itself uses, with its own retention and logging. The caller composes that
/// state deliberately, per <see cref="DecisionGateRequest.State"/>: Shadow passes the agent's full
/// input for phase-1 byte-identical behaviour (a deliberate, reviewed decision — decisions.md §8 —
/// not an oversight), while every Gate caller is expected to pass a bounded descriptor view of
/// exactly what its questions need. Never widen what a Gate call transmits without re-reading that
/// decision.
/// </para>
///
/// <para>
/// <b>Fail closed, fail soft.</b> Shadow mode is deliberately inert on any failure: a
/// missing/unconfigured Decision provider, an unrecognised mode, a transport error, a malformed
/// response and a gate-level timeout all degrade to "no decision" rather than surfacing to the
/// caller. Same soft-failure discipline as
/// <see cref="StepExecutors.MotionGraphicsPlacementAnnotator"/> — the real (agent) decision this
/// shadows must never be affected by anything in this class. The one exception is the caller's own
/// cancellation, which is rethrown rather than swallowed: an unresponsive provider must never be
/// able to masquerade as the workflow being cancelled, nor the workflow's cancellation as a
/// timeout.
/// </para>
/// </summary>
public sealed class DecisionGate : IDecisionGate
{
    private readonly IInferenceProviderResolver _resolver;
    private readonly IDecisionClientFactory _clientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DecisionGate> _logger;

    public DecisionGate(
        IInferenceProviderResolver resolver,
        IDecisionClientFactory clientFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<DecisionGate> logger)
    {
        _resolver = resolver;
        _clientFactory = clientFactory;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RunShadowAsync(
        Guid executionId, int stepOrder, AgentType agentType,
        DecisionState state, IReadOnlyList<DecisionGateQuestion> questions,
        DecisionGateConfig config, CancellationToken ct)
    {
        try
        {
            await DecideAsync(
                new DecisionGateRequest(
                    executionId, stepOrder, agentType, Site: "Shadow", state, questions, config),
                ct);
        }
        catch (OperationCanceledException)
        {
            // Phase-1 contract: RunShadowAsync never throws, because it observes an agent whose
            // real decision is already made. The caller owns the workflow's token and observes its
            // own cancellation; converting it into a new failure mode here would be a phase-2
            // behaviour change to a phase-1 call site (AgentStepExecutor).
        }
    }

    /// <inheritdoc />
    public async Task<DecisionGateOutcome> DecideAsync(DecisionGateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Every mode is explicit. The two live arms fall through to the one provider-call core;
        // every other value — including an unrecognised one produced by a bad binding or a cast —
        // logs and returns, so it is structurally unable to reach the outbound call. "Unrecognised"
        // means "do nothing", never "not Off".
        switch (request.Config.Mode)
        {
            case DecisionGateMode.Off:
                return DecisionGateOutcome.NoDecision;

            case DecisionGateMode.Shadow:
            case DecisionGateMode.Gate:
                break;

            default:
                _logger.LogWarning(
                    "DecisionGate: unrecognised mode {Mode} for execution {ExecutionId} step {StepOrder} ({AgentType}) at {Site}; doing nothing — no provider call, no observation",
                    (int)request.Config.Mode,
                    request.ExecutionId,
                    request.StepOrder,
                    request.AgentType,
                    request.Site);
                return DecisionGateOutcome.NoDecision;
        }

        if (request.Questions.Count == 0)
            return DecisionGateOutcome.NoDecision;

        return await ExecuteAsync(request, ct);
    }

    /// <inheritdoc />
    public async Task AttachSystemTwoAnswersAsync(
        Guid executionId, int stepOrder,
        IReadOnlyDictionary<string, string> answersByQuestion,
        int? agentTokensUsed, CancellationToken ct)
    {
        // Nothing to complete is a legal and common case — the gate timed out, the step never
        // escalated, the mode was Off — and it must not cost a database round-trip.
        if (answersByQuestion is null || answersByQuestion.Count == 0)
            return;

        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

            // "Gate", and only "Gate": a Shadow row was written with its answer already in hand, so
            // it is the one thing this method must never rewrite (phase 1's byte-identical contract).
            string gateMode = DecisionGateMode.Gate.ToString();

            List<DecisionObservation> rows = await db.DecisionObservations
                .Where(o => o.ExecutionId == executionId
                    && o.StepOrder == stepOrder
                    && o.Mode == gateMode)
                .ToListAsync(ct);

            bool changed = false;

            foreach (DecisionObservation row in rows)
            {
                // A missing entry, or an entry carrying no answer, means the caller does not have
                // System Two's answer for this question. That is not an answer: leave the row
                // claiming nothing rather than recording a disagreement against a missing value.
                if (!answersByQuestion.TryGetValue(row.Question, out string? systemTwoAnswer)
                    || systemTwoAnswer is null)
                {
                    continue;
                }

                bool agreed = string.Equals(row.Answer, systemTwoAnswer, StringComparison.Ordinal);
                row.DownstreamOutcomeJson = JsonSerializer.Serialize(new { agreed });
                changed = true;

                if (agentTokensUsed.HasValue)
                    row.AgentTokensUsed = agentTokensUsed.Value;
            }

            if (!changed)
                return;

            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Deliberately swallowed, unlike DecideAsync's rethrow. This call runs *behind* a
            // decision the agent has already made and that the workflow has already recorded, so
            // there is no workflow outcome left for it to protect — propagating here would fail a
            // step over bookkeeping.
            _logger.LogWarning(
                "DecisionGate: attaching System Two answers for execution {ExecutionId} step {StepOrder} was cancelled; the observations remain as written",
                executionId,
                stepOrder);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "DecisionGate: failed to attach System Two answers for execution {ExecutionId} step {StepOrder}; the observations remain as written",
                executionId,
                stepOrder);
        }
    }

    /// <summary>
    /// The single provider-call path, shared by both live modes. Runs the call under the gate's own
    /// deadline, records the observations, and reports the outcome.
    /// </summary>
    private async Task<DecisionGateOutcome> ExecuteAsync(DecisionGateRequest request, CancellationToken ct)
    {
        DecisionGateConfig config = request.Config;
        Stopwatch sw = Stopwatch.StartNew();

        // Resolved before the try so the timeout catch below can report the effective budget the
        // deadline actually used, which is what a reader diagnosing a "gate gave up" warning needs.
        int effectiveTimeoutSeconds = DecisionGateLimits.Clamp(config.TimeoutSeconds);

        try
        {
            // Gate-level budget. The provider's own HttpClient timeout is up to 300 s and this gate
            // sits on a step's critical path, so the deadline here — not the provider's — is what
            // bounds how long a slow or hanging Decision provider can delay the agent.
            //
            // The deadline is unconditional and always clamped. Unconditional because the
            // `TimeoutSeconds > 0 ? … : null` shape this replaced let a non-positive configured
            // value create no cancellation source at all, silently re-exposing the up-to-300 s
            // provider delay this budget exists to bound. Clamped because CancelAfter throws on a
            // delay beyond int.MaxValue milliseconds, and that throw used to happen inside this
            // try — swallowed by the generic catch below, leaving the gate permanently inert with
            // one causeless warning per call. This is the real control: it protects every caller
            // of DecisionGateConfig, not just the two step-config executors that also clamp.
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(effectiveTimeoutSeconds));

            CancellationToken callToken = timeoutCts.Token;

            ResolvedDecisionProvider? provider = await _resolver.ResolveDecisionAsync(config.ProviderId, callToken);
            if (provider is null)
                return DecisionGateOutcome.NoDecision;

            IDecisionClient client = _clientFactory.Get(provider);

            var wireQuestions = request.Questions
                .Select(q => new DecisionQuestion(q.Name, q.Kind, q.Instructions, q.Options))
                .ToList();

            DecisionResult result = await client.DecideAsync(request.State, wireQuestions, callToken);

            using IServiceScope scope = _scopeFactory.CreateScope();
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

            // Tenancy (A2): denormalize the execution's project and organization onto each row so
            // metering and per-org calibration need no join. Unknown (e.g. a deleted execution)
            // stays null — the columns are nullable and the gate must never fail on this.
            var owner = await db.WorkflowExecutions
                .AsNoTracking()
                .Where(e => e.Id == request.ExecutionId)
                .Select(e => new { e.ProjectId, e.OrganizationId })
                .FirstOrDefaultAsync(callToken);

            bool anyEscalated = false;

            foreach (DecisionGateQuestion question in request.Questions)
            {
                if (!result.Answers.TryGetValue(question.Name, out DecisionAnswer? answer))
                    continue;

                // The site's declared metric, not a universal rule. A ReviewLoop question declares
                // AtOrAbove with the step's own MinScore and a Conditional one declares Affirmative;
                // reducing those through the top/margin rule here is what made the stored Escalated
                // column — and, for the sites that read it, the step-level Accepted flag — answer a
                // question the site never asked.
                bool escalated = DecisionAcceptRule.Escalates(
                    question.AcceptMetric,
                    question.ScoreCutoff,
                    answer.Kind,
                    answer.Probabilities,
                    answer.Noul,
                    config.AcceptAt,
                    config.MinMargin);

                anyEscalated |= escalated;

                if (answer.Confidence.HasValue)
                    ReelBoltDiagnostics.DecisionConfidence.Record(answer.Confidence.Value);

                string chosenValue = ChosenValue(answer);

                // A null SystemTwoAnswer means no agent has run — the pre-agent Gate path — so
                // there is no counterfactual and no agreement to claim. string.Equals(x, null, …)
                // is false, so comparing unconditionally would record a disagreement that was never
                // observed on every such row, and the calibration view's agreement rate is computed
                // from exactly this column. Null is the aggregation's existing "outcome unavailable"
                // value; do not substitute a sentinel shape.
                bool? agreed = question.SystemTwoAnswer is null
                    ? null
                    : string.Equals(chosenValue, question.SystemTwoAnswer, StringComparison.Ordinal);

                var observation = new DecisionObservation
                {
                    Id = Guid.NewGuid(),
                    ExecutionId = request.ExecutionId,
                    ProjectId = owner?.ProjectId,
                    OrganizationId = owner?.OrganizationId,
                    StepOrder = request.StepOrder,
                    AgentType = request.AgentType,
                    Site = question.Site,
                    Question = question.Name,
                    OptionsHash = HashOptions(question.Options),
                    Kind = question.Kind,
                    Answer = chosenValue,
                    Confidence = answer.Confidence,
                    ProbabilitiesJson = JsonSerializer.Serialize(answer.Probabilities),
                    Mode = config.Mode.ToString(),
                    Escalated = escalated,
                    // The basis this row's escalation was taken on, so the calibration view can
                    // recompute it at a supplied threshold with the site's own rule rather than a
                    // universal one. ScoreCutoff is null for every metric that needs no cutoff.
                    AcceptMetric = question.AcceptMetric.ToString(),
                    ScoreCutoff = question.ScoreCutoff,
                    DownstreamOutcomeJson = agreed is null
                        ? null
                        : JsonSerializer.Serialize(new { agreed = agreed.Value }),
                    ProviderId = provider.ProviderId,
                    Model = result.Model,
                    DecisionInputTokens = result.InputTokens,
                    DecisionOutputTokens = result.OutputTokens,
                    AgentTokensUsed = request.AgentTokensUsed,
                    CreatedAt = DateTime.UtcNow,
                };

                db.DecisionObservations.Add(observation);
            }

            if (anyEscalated)
                ReelBoltDiagnostics.DecisionEscalations.Add(1);

            await db.SaveChangesAsync(ct);

            return BuildOutcome(request, result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller's own cancellation. Observed, never converted into a "no decision".
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own deadline fired. The caller escalates to the agent; this must never propagate
            // as a cancellation, or a slow provider would cancel the workflow. Both the effective
            // budget the deadline used and the raw configured value are logged: when they differ
            // the clamp is the reason, and a warning that named neither is exactly the causeless
            // "gate is inert" line this replaced.
            _logger.LogWarning(
                "DecisionGate: timed out after {EffectiveTimeoutSeconds}s (configured {ConfiguredTimeoutSeconds}s) for execution {ExecutionId} step {StepOrder} ({AgentType}) at {Site}; escalating to the agent (no decision recorded)",
                effectiveTimeoutSeconds,
                config.TimeoutSeconds,
                request.ExecutionId,
                request.StepOrder,
                request.AgentType,
                request.Site);
            return DecisionGateOutcome.NoDecision;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "DecisionGate run failed for execution {ExecutionId} step {StepOrder} ({AgentType}) at {Site}; the real agent decision is unaffected",
                request.ExecutionId,
                request.StepOrder,
                request.AgentType,
                request.Site);
            return DecisionGateOutcome.NoDecision;
        }
        finally
        {
            sw.Stop();
            ReelBoltDiagnostics.DecisionLatency.Record(sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Applies the step-level all-or-nothing accept rule (decisions.md §4) to a provider result.
    ///
    /// <para>
    /// Deliberately a second pass over the questions rather than a by-product of the persistence
    /// loop: a question is less than the step, so the per-row <c>Escalated</c> flag (unchanged
    /// phase-1 semantics, one row per answered question) and this step-level <c>Accepted</c> flag
    /// are different answers to different questions and must not be conflated.
    /// </para>
    ///
    /// <para>
    /// Each question is accepted or rejected through <b>that question's own declared
    /// <see cref="DecisionAcceptMetric"/></b>, via the same shared rule the stored <c>Escalated</c>
    /// column is computed with — one implementation, so the flag this returns and the row written
    /// beside it can never disagree.
    /// </para>
    /// </summary>
    private static DecisionGateOutcome BuildOutcome(DecisionGateRequest request, DecisionResult result)
    {
        var topTwo = new Dictionary<string, (double Top, double Second)>();
        bool accepted = true;

        foreach (DecisionGateQuestion question in request.Questions)
        {
            if (!result.Answers.TryGetValue(question.Name, out DecisionAnswer? answer))
            {
                // Not answering a question cannot be accepted — all-or-nothing means escalation.
                accepted = false;
                continue;
            }

            // Kept for the callers that read it (ReviewLoopStepExecutor.Evaluate reports the number
            // it was taken on), and now computed by the shared reduction rather than a private copy.
            topTwo[question.Name] = DecisionProbability.TopTwo(answer);

            if (DecisionAcceptRule.Escalates(
                question.AcceptMetric,
                question.ScoreCutoff,
                answer.Kind,
                answer.Probabilities,
                answer.Noul,
                request.Config.AcceptAt,
                request.Config.MinMargin))
            {
                accepted = false;
            }
        }

        return new DecisionGateOutcome(
            HasDecision: true,
            Accepted: accepted && request.Questions.Count > 0,
            Result: result,
            TopTwoProbabilities: topTwo);
    }

    private static string ChosenValue(DecisionAnswer answer) => answer.Kind switch
    {
        DecisionKind.Choice => answer.Choice ?? string.Empty,
        DecisionKind.Score => answer.Score?.ToString() ?? string.Empty,
        DecisionKind.Noul => answer.Noul?.ToString() ?? string.Empty,
        _ => string.Empty,
    };

    private static string HashOptions(IReadOnlyList<string> options)
    {
        string joined = string.Join('|', options.OrderBy(o => o, StringComparer.Ordinal));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash);
    }
}
