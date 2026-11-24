namespace ReelBolt.Shared.Inference;

/// <summary>
/// Which reduction of a decision model's answer a call site's accept rule is expressed in — the
/// declaration that lets one shared rule serve every site instead of the aggregation guessing.
///
/// <para>
/// <b>Why this exists.</b> The gate's phase-1 accept rule was the single universal
/// <c>top &lt; AcceptAt || (top - second) &lt; MinMargin</c>, but two of the four call sites never
/// asked that question: the <c>ReviewLoop</c> early stop accepts on
/// <c>Σ p(level) for level &gt;= MinScore</c> and a <c>Conditional</c> step in
/// <see cref="DecisionKind.Noul"/> mode accepts on the affirmative probability. A calibration view
/// that recomputes the universal rule therefore reports an escalation rate and a tokens-saved figure
/// answering a question those sites never ask. A site now <b>declares</b> its metric (and any cutoff
/// the metric needs) on the question it asks; the gate persists the declaration on the observation
/// row, and every reader — the gate itself, the executors and the aggregation — reduces the row
/// through the same <see cref="DecisionAcceptRule"/> using that row's own declaration.
/// </para>
///
/// <para>
/// <b>The default is <see cref="Top"/>.</b> A row written before this type existed carries no
/// declaration at all, and those historic rows were written by a gate that only ever applied the
/// top/margin rule, so reading a missing declaration as <see cref="Top"/> is exactly right rather
/// than a guess.
/// </para>
/// </summary>
public enum DecisionAcceptMetric
{
    /// <summary>
    /// The <b>top</b> of the distribution clears <c>AcceptAt</c> and the top-two margin clears
    /// <c>MinMargin</c> — phase 1's universal rule. Used by the <c>Colorist</c> and
    /// <c>MusicSupervisor</c> agent steps and by the colour-grade room's convening gate, and the
    /// default for a row that recorded no metric.
    /// </summary>
    Top = 0,

    /// <summary>
    /// <c>Σ p(level)</c> over every rubric level <c>&gt;= ScoreCutoff</c> clears <c>AcceptAt</c>, and
    /// the top-two margin clears <c>MinMargin</c>. Used by the <c>ReviewLoop</c> early stop, whose
    /// cutoff is the step's own <c>MinScore</c>. <b>Needs the cutoff persisted:</b> the sum cannot be
    /// recomputed from a row that does not record which levels it counted, which is why
    /// <c>DecisionObservation</c>'s <c>ScoreCutoff</c> column exists.
    /// </summary>
    AtOrAbove = 1,

    /// <summary>
    /// The affirmative probability — the answer's <see cref="DecisionAnswer.Noul"/> value — clears
    /// <c>AcceptAt</c>. Used by a <c>Conditional</c> step's Decision mode. Deliberately applies
    /// <b>no</b> top-two margin: the site's rule is a single comparison against the affirmative
    /// probability, and the top-two pair of a <c>Noul</c> answer is
    /// <c>(max(p, 1-p), min(p, 1-p))</c> — a restatement of that same number, never a second
    /// independent signal. Needs no cutoff.
    /// </summary>
    Affirmative = 2,
}
