using ReelBolt.Shared.Inference;

namespace ReelBolt.WorkflowEngine.Execution.Decision;

/// <summary>
/// The adapter through which this assembly reads a <see cref="DecisionAnswer"/> as the (top, second)
/// probability pair. <b>The reduction itself now lives in <c>DecisionAcceptRule.TopTwo</c></b>, in
/// <c>ReelBolt.Shared</c>, because the shared accept rule needs it too and a shared type cannot
/// call back into this one; this class is the this-assembly spelling of it, unchanged in signature
/// and semantics.
///
/// <para>
/// This was a private helper on <see cref="DecisionGate"/>; it is shared so the gate
/// (D-004/D-005) and the calibration aggregation (D-002) cannot drift apart on what "top" means —
/// a divergence there would silently change both the escalation rate and the reliability diagram.
/// T-017 extended that reasoning: the accept <i>rule</i> is shared in the same way, so there are now
/// two names for one implementation rather than two implementations.
/// <b>Do not change <see cref="TopTwo"/>'s signature or semantics without a memo</b>: D-002 is
/// written against it.
/// </para>
///
/// <para>
/// The two branches are not interchangeable: a <c>Noul</c> answer has no option set at all — it is
/// a single probability of "yes" — so its distribution is the two-point set <c>{p, 1-p}</c>, and a
/// <c>Choice</c>/<c>Score</c> answer whose <see cref="DecisionAnswer.Probabilities"/> is empty (a
/// provider that reported no distribution) is read the same way, as a two-point set rather than as
/// a zero-confidence answer.
/// </para>
///
/// <para>
/// <b>An empty or <c>Noul</c>-without-value answer does not escalate.</b> Those two branches return
/// <c>(max(p, 1-p), min(p, 1-p))</c> with <c>p = answer.Noul ?? 0.0</c>, so a zero-information
/// answer — <c>p = 0.0</c> — yields <c>(1.0, 0.0)</c>. <c>Top</c> is 1.0, which clears
/// <c>AcceptAt</c>, and the margin is 1.0, which clears <c>MinMargin</c>; the gate therefore reads
/// such a row as a maximally confident decision and does <b>not</b> escalate it. That is phase-1
/// semantics, deliberately unchanged — three other tickets are built on it — and this paragraph
/// states the arithmetic rather than arguing for it.
/// </para>
/// </summary>
public static class DecisionProbability
{
    /// <summary>
    /// The two largest probabilities in <paramref name="answer"/>, descending.
    ///
    /// <para>
    /// For a <see cref="DecisionKind.Noul"/> answer, or any answer whose
    /// <see cref="DecisionAnswer.Probabilities"/> is empty, the result is <c>(max, min)</c> of
    /// <c>{p, 1-p}</c> where <c>p = answer.Noul ?? 0.0</c>. Otherwise it is the two largest values
    /// of the probability map, with <c>Second = 0.0</c> when only one value exists (so the margin
    /// is the whole of <c>Top</c>, and a single-option answer is not penalised).
    /// </para>
    /// </summary>
    /// <param name="answer">The answer to score. Must not be null.</param>
    /// <returns>The top probability and the runner-up, both in 0..1.</returns>
    public static (double Top, double Second) TopTwo(DecisionAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        // The reduction itself lives in ReelBolt.Shared, on the accept rule that every site and the
        // calibration aggregation share: DecisionAcceptRule is in Shared and cannot call back into
        // this assembly, so the dependency runs this way. This method's signature and semantics are
        // unchanged — it is now an adapter over the single implementation, never a second one.
        return DecisionAcceptRule.TopTwo(answer.Kind, answer.Probabilities, answer.Noul);
    }
}
