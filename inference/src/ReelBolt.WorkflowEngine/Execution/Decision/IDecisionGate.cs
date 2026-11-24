using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.WorkflowEngine.Execution.Decision;

/// <summary>
/// One question the gate asks, together with the System Two (agent) answer it is to be compared
/// against.
/// </summary>
/// <param name="Site">The call site the question belongs to; lands on the observation row.</param>
/// <param name="Name">The question's identifier, as the provider knows it and as it is persisted.</param>
/// <param name="Kind">Choice, Score or Noul.</param>
/// <param name="Instructions">The instruction text sent to the Decision provider.</param>
/// <param name="Options">
/// The option set offered to the provider. A <see cref="DecisionKind.Noul"/> question has none.
/// </param>
/// <param name="SystemTwoAnswer">
/// The agent's real answer, for the observation's agreement column.
///
/// <para>
/// <b>Null means no agent has run yet</b> — the normal pre-agent Gate path, where the gate decides
/// <i>whether</i> to run the agent and only learns System Two's answer afterwards, if at all. A
/// null is not "the agent answered nothing": there is no counterfactual to compare against, so the
/// observation records no agreement claim at all (<see cref="DecisionObservation.DownstreamOutcomeJson"/>
/// is left null, which the calibration aggregation reads as "outcome unavailable") rather than
/// asserting a disagreement that was never observed. A caller that learns the answer later — the
/// escalated path — completes the row through
/// <see cref="IDecisionGate.AttachSystemTwoAnswersAsync"/>.
/// </para>
///
/// <para>
/// Never null on the Shadow path, where the agent has already run by construction.
/// </para>
/// </param>
/// <param name="AcceptMetric">
/// The reduction of the answer this question's accept rule is expressed in — the site's own
/// declaration, applied by <see cref="DecisionAcceptRule"/> and persisted on the observation so the
/// calibration view can recompute this row at a supplied threshold <b>using the same rule the site
/// used</b>. Defaults to <see cref="DecisionAcceptMetric.Top"/>, which is phase 1's universal rule
/// and therefore the correct declaration for every call site that has not declared one (and for
/// every row written before the column existed).
/// </param>
/// <param name="ScoreCutoff">
/// The level cutoff <see cref="DecisionAcceptMetric.AtOrAbove"/> needs — the <c>ReviewLoop</c>
/// early stop's <c>MinScore</c>. Null for every metric that does not need one, which is why it is
/// nullable: it is a step property the observation has no foreign key to, so it is denormalized onto
/// the row. An <c>AtOrAbove</c> declaration with a null cutoff cannot be recomputed, and the
/// calibration aggregation excludes such a row from its escalation figure rather than guessing.
/// </param>
public sealed record DecisionGateQuestion(
    string Site, string Name, DecisionKind Kind, string Instructions,
    IReadOnlyList<string> Options, string? SystemTwoAnswer,
    DecisionAcceptMetric AcceptMetric = DecisionAcceptMetric.Top,
    int? ScoreCutoff = null);

/// <summary>
/// One call to the gate: what to ask, where it happened, and what System Two decided (when System
/// Two has already run).
/// </summary>
/// <param name="ExecutionId">The execution the decision belongs to. Denormalized onto the observation.</param>
/// <param name="StepOrder">The step the decision belongs to. Denormalized onto the observation.</param>
/// <param name="AgentType">The agent type the decision belongs to.</param>
/// <param name="Site">
/// The call site that invoked the gate (e.g. <c>"Colorist.step"</c>), used for logging and
/// diagnosis only. The <see cref="DecisionGateQuestion.Site"/> of each individual question is what
/// lands in the observation row, so this does not change the persisted shape.
/// </param>
/// <param name="State">
/// The state sent to the Decision provider. <b>Callers own this deliberately:</b> it is transmitted
/// to a hosted third party, so it must be composed from exactly what the questions need — see
/// <see cref="DecisionGate"/>'s class remarks. The Shadow path passes the agent's full input
/// (phase-1 behaviour, deliberately unchanged); the Gate path passes a bounded descriptor view.
/// </param>
/// <param name="Questions">The questions to ask. An empty list is a no-op.</param>
/// <param name="Config">Per-site configuration, including the mode and the gate-level timeout.</param>
/// <param name="SystemTwoAnswer">
/// The agent's real answer, for the observation's agreement column. Null on the pre-agent Gate
/// path, where no agent has run yet.
/// </param>
/// <param name="AgentTokensUsed">
/// Tokens the shadowed agent spent, persisted on the observation. Null when no agent ran (the
/// pre-agent Gate path) and on the phase-1 Shadow path, whose signature carries no token count.
/// </param>
public sealed record DecisionGateRequest(
    Guid ExecutionId,
    int StepOrder,
    AgentType AgentType,
    string Site,
    DecisionState State,
    IReadOnlyList<DecisionGateQuestion> Questions,
    DecisionGateConfig Config,
    string? SystemTwoAnswer = null,
    int? AgentTokensUsed = null);

public interface IDecisionGate
{
    /// <summary>
    /// Phase-1 signature, preserved: runs the gate purely to observe, never throws, and records the
    /// same observation rows it has always recorded.
    /// </summary>
    Task RunShadowAsync(
        Guid executionId, int stepOrder, AgentType agentType,
        DecisionState state, IReadOnlyList<DecisionGateQuestion> questions,
        DecisionGateConfig config, CancellationToken ct);

    /// <summary>
    /// Runs the gate and reports what it decided, for callers that act on the outcome.
    ///
    /// <para>
    /// This is the single provider-call path: <see cref="RunShadowAsync"/> is a thin adapter over
    /// it. Throws <see cref="OperationCanceledException"/> only for the caller's own cancellation —
    /// a gate-level timeout returns <see cref="DecisionGateOutcome.NoDecision"/> instead, so an
    /// unresponsive provider can never surface as a workflow cancellation.
    /// </para>
    /// </summary>
    Task<DecisionGateOutcome> DecideAsync(DecisionGateRequest request, CancellationToken ct);

    /// <summary>
    /// Completes observation rows the pre-agent Gate path already wrote, once System Two's real
    /// answers are known.
    ///
    /// <para>
    /// The pre-agent Gate call records an observation with no agreement claim, because at that point
    /// no agent has run (see <see cref="DecisionGateQuestion.SystemTwoAnswer"/>). A caller that
    /// escalated and then ran the agent — D-005's path — learns the answers only afterwards, and
    /// calls this to fill in the comparison rather than throwing the observation away.
    /// </para>
    ///
    /// <para>
    /// Only <c>Mode == "Gate"</c> rows for this <paramref name="executionId"/> and
    /// <paramref name="stepOrder"/> are considered, and only those whose <c>Question</c> appears in
    /// <paramref name="answersByQuestion"/>. A <c>Shadow</c> row already carried its answer at write
    /// time, so it is never touched — rewriting one would break phase 1's byte-identical guarantee.
    /// A question absent from the map, or a map entry with a null answer, leaves its row as it was.
    /// </para>
    ///
    /// <para>
    /// <b>Never throws into a workflow.</b> Nothing matching is a no-op, and any failure on the way
    /// to the store is logged and swallowed: this is bookkeeping about a decision that has already
    /// been made, and it must never be able to fail a step.
    /// </para>
    /// </summary>
    /// <param name="executionId">The execution whose rows to complete.</param>
    /// <param name="stepOrder">The step whose rows to complete.</param>
    /// <param name="answersByQuestion">System Two's answer per question name.</param>
    /// <param name="agentTokensUsed">
    /// Tokens the agent spent, recorded on the completed rows. Null leaves the stored value alone.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task AttachSystemTwoAnswersAsync(
        Guid executionId, int stepOrder,
        IReadOnlyDictionary<string, string> answersByQuestion,
        int? agentTokensUsed, CancellationToken ct);
}
