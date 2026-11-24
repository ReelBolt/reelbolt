using ReelBolt.Shared.Inference;

namespace ReelBolt.WorkflowEngine.Execution.Decision;

/// <summary>
/// What a <see cref="IDecisionGate.DecideAsync"/> run produced, for a caller that intends to act on
/// it (the Gate path of a step executor, the room convening gate) rather than merely observe it.
///
/// <para>
/// <see cref="HasDecision"/> and <see cref="Accepted"/> answer two different questions and a caller
/// must not conflate them:
/// </para>
/// <list type="bullet">
/// <item><see cref="HasDecision"/> — did the decision model actually answer? False when no Decision
/// provider resolved (the normal local case), on a gate-level timeout, and on any failure. On a
/// false, <see cref="Result"/> is null and the caller must fall back to its System Two path: the
/// gate is inert, never an error.</item>
/// <item><see cref="Accepted"/> — may the model's answer be used <i>instead of</i> running the
/// agent? This is step-level <b>all-or-nothing</b> (decisions.md §4): true only when every question
/// was answered and every one of them clears both <see cref="DecisionGateConfig.AcceptAt"/> and
/// <see cref="DecisionGateConfig.MinMargin"/>. A single failing question escalates the whole step,
/// and the agent's output then wins verbatim. All-or-nothing is what makes escalation rate the one
/// number to calibrate against; it also means a caller can never see a partially-accepted step.</item>
/// </list>
/// </summary>
/// <param name="HasDecision">Whether the decision model produced an answer that survived the gate.</param>
/// <param name="Accepted">
/// Whether <see cref="Result"/> may replace the agent's output. Always false when
/// <see cref="HasDecision"/> is false.</param>
/// <param name="Result">The raw provider result, or null when <see cref="HasDecision"/> is false.</param>
/// <param name="TopTwoProbabilities">
/// Per question name, the (top, second) probability pair the accept rule was evaluated against —
/// see <see cref="DecisionProbability.TopTwo"/>. Contains an entry only for questions the provider
/// actually answered.
/// </param>
public sealed record DecisionGateOutcome(
    bool HasDecision,
    bool Accepted,
    DecisionResult? Result,
    IReadOnlyDictionary<string, (double Top, double Second)> TopTwoProbabilities)
{
    /// <summary>
    /// The uniform "nothing happened" outcome: no provider resolved, the gate was off, the mode was
    /// unrecognised, the call timed out, or it failed. Callers treat this as "run the agent".
    /// </summary>
    public static DecisionGateOutcome NoDecision { get; } =
        new(false, false, null, new Dictionary<string, (double Top, double Second)>());
}
