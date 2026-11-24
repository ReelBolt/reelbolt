using System.Globalization;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// <b>The one implementation of "does this answer escalate, given these thresholds".</b> Every
/// caller — the gate that writes an observation, each step executor that decides whether to run its
/// agent, and the calibration aggregation that recomputes a row's escalation at a supplied threshold
/// — reduces the answer through this type, keyed on the <see cref="DecisionAcceptMetric"/> the site
/// declared. A second copy of the rule anywhere is how the calibration view came to report a number
/// the sites it describes never computed.
///
/// <para>
/// Phase 1 had a single universal rule, <c>top &lt; acceptAt || (top - second) &lt; minMargin</c>,
/// applied by the gate to every question. It is still the rule for <see cref="DecisionAcceptMetric.Top"/>,
/// but two sites never asked it: the <c>ReviewLoop</c> early stop sums the rubric mass at or above
/// its own <c>MinScore</c>, and a <c>Conditional</c> step's Decision mode reads the affirmative
/// probability. Both are expressed here as metric arms rather than as exceptions at the call sites,
/// so a future site declares its metric instead of the aggregation guessing at one.
/// </para>
///
/// <para>
/// <b>Fail closed, never throw.</b> Every input this type cannot reduce to a real number escalates:
/// a <c>NaN</c> or <c>±Infinity</c> probability, the affirmative probability, a cutoff or a
/// threshold, an unsummable probability map, a missing cutoff for a metric that needs one, and an
/// unrecognised <see cref="DecisionAcceptMetric"/> value. That matches the executors' existing
/// behaviour — failing closed can only ever cost a decision call, never an agent that should have
/// run — and it is why no method here is allowed to surface an exception into a workflow.
/// </para>
///
/// <para>
/// <b>Comparison order.</b> For the two metrics that carry a top-two margin, the margin check runs
/// <b>first</b>. The two predicates are strict comparisons OR-ed together, so the order cannot change
/// the result of this method; it is documented because the ordering <i>is</i> load-bearing one level
/// up, where a distribution that is simultaneously high-scoring and bimodal must resolve
/// deterministically to escalate.
/// </para>
/// </summary>
public static class DecisionAcceptRule
{
    /// <summary>
    /// The lowest rubric level the <c>ReviewLoop</c> early stop offers, and the lower bound
    /// <see cref="SumProbabilityAtOrAbove"/> counts. Single-sourced here so the executor's rubric and
    /// the aggregation's recomputation cannot disagree about which keys are levels.
    /// </summary>
    public const int MinRubricLevel = 0;

    /// <summary>
    /// The highest rubric level the <c>ReviewLoop</c> early stop offers, and the upper bound
    /// <see cref="SumProbabilityAtOrAbove"/> counts. See <see cref="MinRubricLevel"/>.
    /// </summary>
    public const int MaxRubricLevel = 10;

    /// <summary>
    /// Whether the metric needs the top-two margin check as well as its own scalar comparison.
    /// <see cref="DecisionAcceptMetric.Affirmative"/> deliberately does not: its site's rule is the
    /// single threshold comparison, and a <c>Noul</c> answer's top-two pair is a restatement of the
    /// same number rather than a second independent signal.
    /// </summary>
    public static bool RequiresTopTwoMargin(DecisionAcceptMetric metric) =>
        metric is DecisionAcceptMetric.Top or DecisionAcceptMetric.AtOrAbove;

    /// <summary>
    /// The two largest probabilities implied by a decision answer, descending.
    ///
    /// <para>
    /// The <b>one</b> place this reduction is expressed, so the gate
    /// (<c>DecisionProbability.TopTwo</c>, which delegates here) and this rule cannot disagree about
    /// what "top" means.
    /// </para>
    ///
    /// <para>
    /// A <see cref="DecisionKind.Noul"/> answer — or any answer whose probability map is empty, which
    /// includes the logprob client's weighted-<c>Score</c> answer — has no option set, so its
    /// distribution is read as the two-point set <c>{p, 1-p}</c> with <c>p = noul ?? 0.0</c>. That is
    /// phase-1 semantics and is unchanged: a zero-information answer (<c>p = 0.0</c>) therefore reads
    /// as <c>(1.0, 0.0)</c>, i.e. maximally confident, and does not escalate under
    /// <see cref="DecisionAcceptMetric.Top"/>. Otherwise the result is the two largest values of the
    /// map, with <c>Second = 0.0</c> when only one value exists so a single-option answer is not
    /// penalised. A <c>NaN</c> or <c>±Infinity</c> value passes through verbatim; rejecting it is
    /// the caller's job, and both <see cref="Escalates"/> and <see cref="MetricValue"/> do.
    /// </para>
    /// </summary>
    /// <param name="kind">The answer's kind.</param>
    /// <param name="probabilities">The answer's probability map. Must not be null.</param>
    /// <param name="noul">The affirmative probability, when the answer carries one.</param>
    /// <returns>The top probability and the runner-up, both in 0..1 for a well-formed answer.</returns>
    public static (double Top, double Second) TopTwo(
        DecisionKind kind, IReadOnlyDictionary<string, double> probabilities, double? noul)
    {
        ArgumentNullException.ThrowIfNull(probabilities);

        if (kind == DecisionKind.Noul || probabilities.Count == 0)
        {
            double p = noul ?? 0.0;
            return (Math.Max(p, 1 - p), Math.Min(p, 1 - p));
        }

        double[] sorted = probabilities.Values.OrderDescending().ToArray();
        double top = sorted.Length > 0 ? sorted[0] : 0.0;
        double second = sorted.Length > 1 ? sorted[1] : 0.0;
        return (top, second);
    }

    /// <summary>
    /// <c>Σ p(level)</c> over every probability-map key that parses as a rubric level in
    /// <c>[minLevel, maxLevel]</c> and is at or above <paramref name="scoreCutoff"/>.
    ///
    /// <para>
    /// Returns <c>null</c> — "this map cannot be summed", never "the sum is zero" — for an empty map,
    /// for a null cutoff (a metric that needs one and does not have one cannot be recomputed), and
    /// for a map that carries no parseable in-range level key at all. A probability map that cannot
    /// be summed is not evidence of confidence, so every caller reads a null as escalate.
    /// </para>
    /// </summary>
    /// <param name="probabilities">The answer's probability map. Level keys are the option labels the question offered ("0".."10").</param>
    /// <param name="scoreCutoff">The lowest level to count, or null when the caller has none.</param>
    /// <param name="minLevel">Lowest level the rubric offers.</param>
    /// <param name="maxLevel">Highest level the rubric offers.</param>
    public static double? SumProbabilityAtOrAbove(
        IReadOnlyDictionary<string, double> probabilities,
        int? scoreCutoff,
        int minLevel = MinRubricLevel,
        int maxLevel = MaxRubricLevel)
    {
        ArgumentNullException.ThrowIfNull(probabilities);

        if (probabilities.Count == 0 || scoreCutoff is not { } cutoff)
            return null;

        double sum = 0;
        bool anyLevel = false;

        foreach ((string label, double probability) in probabilities)
        {
            if (!int.TryParse(label, NumberStyles.Integer, CultureInfo.InvariantCulture, out int level))
                continue;

            if (level < minLevel || level > maxLevel)
                continue;

            anyLevel = true;
            if (level >= cutoff)
                sum += probability;
        }

        return anyLevel ? sum : null;
    }

    /// <summary>
    /// The scalar <paramref name="metric"/> compares against <c>AcceptAt</c>, or <c>null</c> when the
    /// metric cannot be reduced for this answer at all. Exposed so a caller that wants to <i>report</i>
    /// the basis of a decision — rather than act on it — can do so through the same reduction the
    /// decision was taken on.
    ///
    /// <para>
    /// <b>A null here is "excluded", not "escalated".</b> <see cref="Escalates"/> fails closed on a
    /// null, but the calibration aggregation counts such a row and leaves it out of the escalation
    /// figure instead of guessing at it — the two callers want opposite things from the same fact,
    /// which is why this is a separate method rather than an out-parameter.
    /// </para>
    /// </summary>
    /// <param name="metric">The declared metric. An unrecognised value yields null.</param>
    /// <param name="scoreCutoff">The cutoff <see cref="DecisionAcceptMetric.AtOrAbove"/> needs.</param>
    /// <param name="kind">The answer's kind.</param>
    /// <param name="probabilities">The answer's probability map. Must not be null.</param>
    /// <param name="noul">The affirmative probability, when the answer carries one.</param>
    /// <returns>The finite scalar for the metric, or null when it cannot be computed.</returns>
    public static double? MetricValue(
        DecisionAcceptMetric metric,
        int? scoreCutoff,
        DecisionKind kind,
        IReadOnlyDictionary<string, double> probabilities,
        double? noul)
    {
        switch (metric)
        {
            case DecisionAcceptMetric.Top:
                {
                    // Both members must be finite: a NaN runner-up poisons the margin exactly as a NaN
                    // top poisons the level, so this metric is uncomputable unless both are real.
                    (double top, double second) = TopTwo(kind, probabilities, noul);
                    return FiniteOrNull(top) is { } finiteTop && FiniteOrNull(second) is not null
                        ? finiteTop
                        : null;
                }

            case DecisionAcceptMetric.AtOrAbove:
                return FiniteOrNull(SumProbabilityAtOrAbove(probabilities, scoreCutoff));

            case DecisionAcceptMetric.Affirmative:
                return FiniteOrNull(noul);

            default:
                return null;
        }
    }

    /// <summary>
    /// Whether an answer <b>escalates</b> — i.e. fails to clear the thresholds under the metric the
    /// site declared — given <paramref name="acceptAt"/> and <paramref name="minMargin"/>.
    ///
    /// <para>
    /// Escalating is the safe direction: it means the agent still runs and its output wins verbatim.
    /// Everything this method cannot reduce to a real number escalates, and it never throws.
    /// </para>
    /// </summary>
    /// <param name="metric">The declared metric. A value this type does not recognise escalates.</param>
    /// <param name="scoreCutoff">
    /// The level cutoff for <see cref="DecisionAcceptMetric.AtOrAbove"/>, null for every metric that
    /// does not need one.
    /// </param>
    /// <param name="kind">The answer's kind.</param>
    /// <param name="probabilities">The answer's probability map. Must not be null.</param>
    /// <param name="noul">The affirmative probability, when the answer carries one.</param>
    /// <param name="acceptAt">The threshold the metric's scalar must reach.</param>
    /// <param name="minMargin">The top-two margin the metrics that carry one must reach.</param>
    public static bool Escalates(
        DecisionAcceptMetric metric,
        int? scoreCutoff,
        DecisionKind kind,
        IReadOnlyDictionary<string, double> probabilities,
        double? noul,
        double acceptAt,
        double minMargin)
    {
        ArgumentNullException.ThrowIfNull(probabilities);

        // A threshold that is not a number is not a threshold: `x < NaN` is false and would read as
        // "accepted", which is the one direction this rule must never fail in.
        if (!double.IsFinite(acceptAt) || !double.IsFinite(minMargin))
            return true;

        if (MetricValue(metric, scoreCutoff, kind, probabilities, noul) is not { } value)
            return true;

        if (RequiresTopTwoMargin(metric))
        {
            (double top, double second) = TopTwo(kind, probabilities, noul);

            if (FiniteOrNull(top) is not { } finiteTop || FiniteOrNull(second) is not { } finiteSecond)
                return true;

            // 1. Bimodal first — see the class remarks.
            if ((finiteTop - finiteSecond) < minMargin)
                return true;
        }

        // 2. Then the metric's own threshold, as a strict comparison (matching every site's rule:
        // a value landing exactly on AcceptAt clears it).
        return value < acceptAt;
    }

    /// <summary>Returns <paramref name="value"/> when it is finite, and <c>null</c> otherwise.</summary>
    private static double? FiniteOrNull(double? value) =>
        value.HasValue && double.IsFinite(value.Value) ? value : null;
}
