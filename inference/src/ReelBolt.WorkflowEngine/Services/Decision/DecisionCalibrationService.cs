using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.Decision;

namespace ReelBolt.WorkflowEngine.Services.Decision;

/// <summary>
/// Read-only aggregation over <c>decision_observations</c> that feeds the calibration view.
///
/// <para>
/// The whole point of this service is <b>surviving the data it actually has</b>. The local
/// install has never recorded an observation, and the rows that will exist are a mix of
/// <c>Noul</c> answers whose <c>ProbabilitiesJson</c> is <c>"{}"</c> by construction, rows
/// written before the token columns existed, and rows someone hand-edited into nonsense. No
/// method here may throw on data shape: an unreadable row is counted and then excluded from the
/// specific figure it cannot inform.
/// </para>
///
/// <para>
/// A stored value that is not a <b>finite</b> number is the same kind of fact, and this is part of
/// the contract rather than a detail: <c>NaN</c>, <see cref="double.PositiveInfinity"/> and
/// <see cref="double.NegativeInfinity"/> are counted in <c>ObservationCount</c>, excluded from
/// every figure they cannot inform, and <b>never serialized</b>. They are reachable from stored,
/// third-party-influenced data — an overflowing JSON number in <c>ProbabilitiesJson</c>
/// deserializes to <c>±Infinity</c>, and <c>"Infinity"</c>/<c>"NaN"</c> parse on the <c>Noul</c>
/// answer path — and <see cref="System.Text.Json"/> refuses to write one, so without
/// <c>FiniteOrNull</c> a single bad row would 500 this endpoint on every later request.
/// </para>
/// </summary>
public interface IDecisionCalibrationService
{
    /// <summary>
    /// Builds the calibration report over the stored observations.
    /// </summary>
    /// <param name="acceptAt">Confidence threshold to recompute escalation at.</param>
    /// <param name="minMargin">Top-two margin to recompute escalation at.</param>
    /// <param name="providerId">Restrict to one inference provider; null for all.</param>
    /// <param name="site">Restrict to one decision site; null/empty for all.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DecisionCalibrationReport> GetCalibrationAsync(
        double acceptAt,
        double minMargin,
        Guid? providerId,
        string? site,
        CancellationToken ct);
}

/// <summary>
/// The <see cref="IDecisionCalibrationService"/> implementation — a handful of
/// <c>GroupBy</c>s over the WorkflowEngine's own table, plus the two bodies that are not
/// obvious: the Wilson interval and the projected tokens-saved sum.
/// </summary>
public sealed class DecisionCalibrationService : IDecisionCalibrationService
{
    /// <summary>
    /// Bucket count and the width they partition <c>[0, 1]</c> into. Fixed by the report shape
    /// (<c>web/lib/types/decision-calibration.ts</c> mirrors all ten), not a knob.
    /// </summary>
    private const int BucketCount = 10;

    /// <summary>
    /// z for a 95% two-sided interval. The interval is emitted as a proportion in
    /// <c>[0, 1]</c>, not a percentage — a 3-row bucket then reads as the wide band it is
    /// (1 of 3 agreements gives [0.062, 0.792]) instead of as a point estimate of certainty.
    /// </summary>
    private const double WilsonZ = 1.96;

    /// <summary>
    /// Tolerance applied before taking the floor of <c>confidence × 10</c>. Without it a value
    /// that is exactly a bucket edge lands one bucket low: 0.3 is stored as
    /// 0.2999999999999999889…, so <c>floor(0.3 × 10)</c> is 2, not 3.
    /// </summary>
    private const double BucketEpsilon = 1e-9;

    /// <summary>
    /// Below this many observations the report says out loud that thresholds tuned on the data
    /// are not yet meaningful.
    /// </summary>
    public const int MinimumSampleSize = 30;

    private readonly WorkflowEngineDbContext _db;

    public DecisionCalibrationService(WorkflowEngineDbContext db) => _db = db;

    /// <inheritdoc />
    public async Task<DecisionCalibrationReport> GetCalibrationAsync(
        double acceptAt,
        double minMargin,
        Guid? providerId,
        string? site,
        CancellationToken ct)
    {
        IQueryable<DecisionObservation> query = _db.DecisionObservations.AsNoTracking();

        if (providerId.HasValue)
            query = query.Where(o => o.ProviderId == providerId.Value);

        if (!string.IsNullOrEmpty(site))
            query = query.Where(o => o.Site == site);

        List<DecisionObservation> rows = await query
            .OrderBy(o => o.Site)
            .ThenBy(o => o.Question)
            .ThenBy(o => o.CreatedAt)
            .ThenBy(o => o.Id)
            .ToListAsync(ct);

        List<RowFacts> facts = rows.Select(r => new RowFacts(r, acceptAt, minMargin)).ToList();

        List<DecisionCalibrationSite> sites = facts
            .GroupBy(f => (f.Row.Site, f.Row.Question))
            .OrderBy(g => g.Key.Site, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Question, StringComparer.Ordinal)
            .Select(g => BuildSite(g.Key.Site, g.Key.Question, g.ToList(), BuildSweep(g.Select(x => x.Row).ToList(), minMargin), acceptAt))
            .ToList();

        // Rows with no provider recorded cannot be attributed to one, and the report's provider
        // entry carries a non-null id, so they are omitted rather than lumped into a phantom row.
        List<DecisionCalibrationProvider> providers = facts
            .Where(f => f.Row.ProviderId.HasValue)
            .GroupBy(f => f.Row.ProviderId!.Value)
            .OrderBy(g => g.Key)
            .Select(g => BuildProvider(g.Key, g.ToList()))
            .ToList();

        var totals = new DecisionCalibrationTotals
        {
            ObservationCount = facts.Count,
            AgreementRate = AgreementRate(facts),
            EscalationRate = EscalationRate(facts),
            TokensSaved = facts.Sum(f => f.TokensSaved),
            TokensSavedIsProjected = true,
            AcceptAt = acceptAt,
            MinMargin = minMargin,
        };

        return new DecisionCalibrationReport
        {
            Sites = sites,
            Providers = providers,
            Totals = totals,
            SampleSizeWarning = facts.Count < MinimumSampleSize ? BuildSampleSizeWarning(facts.Count) : null,
        };
    }

    /// <summary>
    /// The per-provider entry. Its reliability diagram is computed by the <b>same</b>
    /// <see cref="BuildBuckets"/> the site entries use, over this provider's rows only — one
    /// implementation of the boundary/Wilson rule, so the per-site and per-provider diagrams
    /// cannot drift (the same reasoning that keeps <c>DecisionProbability.TopTwo</c> shared).
    /// </summary>
    private static DecisionCalibrationProvider BuildProvider(Guid providerId, IReadOnlyList<RowFacts> facts)
    {
        string model = facts.Select(f => f.Row.Model).FirstOrDefault(m => !string.IsNullOrEmpty(m)) ?? "";

        // The warning names the provider: a report whose total clears the minimum can still hold
        // one provider too thin to tune against, and "only 4 observations" without saying whose
        // would send the reader looking in the wrong place.
        string label = model.Length > 0 ? model : $"provider {providerId}";

        return new()
        {
            ProviderId = providerId,
            Model = model,
            ObservationCount = facts.Count,
            AgreementRate = AgreementRate(facts),
            MeanConfidence = MeanConfidence(facts),
            ReliabilityBuckets = BuildBuckets(facts),
            SampleSizeWarning =
                facts.Count < MinimumSampleSize ? BuildSampleSizeWarning(facts.Count, label) : null,
        };
    }

    private static DecisionCalibrationSite BuildSite(
        string site, string question, IReadOnlyList<RowFacts> facts,
        IReadOnlyList<ThresholdSweepPoint> sweep, double acceptAt) =>
        new()
        {
            Site = site,
            Question = question,
            ObservationCount = facts.Count,
            AgreementCount = facts.Count(f => f.Agreed == true),
            AgreementRate = AgreementRate(facts),
            MeanConfidence = MeanConfidence(facts),
            ReliabilityBuckets = BuildBuckets(facts),
            EscalationCount = facts.Count(f => f is { EscalationComputable: true, Escalated: true }),
            EscalationRate = EscalationRate(facts),
            ThresholdSweep = sweep,
            Recommendation = Recommend(facts.Count, facts.Where(f => f.EscalationComputable).Count(), sweep, acceptAt),
        };

    /// <summary>Minimum accepted-agreement a threshold must keep for Gate to be suggested.</summary>
    public const double TargetAcceptedAgreement = 0.95;

    /// <summary>Minimum accepted rows with an outcome before an agreement figure is trusted.</summary>
    public const int MinAcceptedWithOutcome = 10;

    private static IReadOnlyList<ThresholdSweepPoint> BuildSweep(
        IReadOnlyList<DecisionObservation> rows, double minMargin)
    {
        var points = new List<ThresholdSweepPoint>(11);
        for (int i = 0; i <= 10; i++)
        {
            double at = 0.5 + (i * 0.05);
            List<RowFacts> f = rows.Select(r => new RowFacts(r, at, minMargin)).ToList();
            List<RowFacts> computable = f.Where(x => x.EscalationComputable).ToList();
            List<RowFacts> accepted = computable.Where(x => !x.Escalated).ToList();
            List<RowFacts> withOutcome = accepted.Where(x => x.Agreed.HasValue).ToList();
            points.Add(new ThresholdSweepPoint
            {
                AcceptAt = Math.Round(at, 2),
                AcceptedCount = accepted.Count,
                AcceptRate = Rate(accepted.Count, computable.Count),
                AcceptedWithOutcomeCount = withOutcome.Count,
                AcceptedAgreementRate = Rate(withOutcome.Count(x => x.Agreed == true), withOutcome.Count),
                TokensSaved = f.Sum(x => x.TokensSaved),
            });
        }
        return points;
    }

    /// <summary>
    /// Advisory verdict: too few rows -> NotEnoughData; otherwise TryGate at the lowest swept
    /// threshold whose accepted rows agree with the agent at least <see cref="TargetAcceptedAgreement"/>
    /// of the time (over at least <see cref="MinAcceptedWithOutcome"/> rows), else KeepShadow.
    /// </summary>
    private static CalibrationRecommendation Recommend(
        int observationCount, int computableCount, IReadOnlyList<ThresholdSweepPoint> sweep, double acceptAt)
    {
        if (observationCount < MinimumSampleSize)
        {
            return new CalibrationRecommendation
            {
                Verdict = "NotEnoughData",
                Reason = $"Only {observationCount} observation{(observationCount == 1 ? "" : "s")} so far; at least {MinimumSampleSize} are needed before a threshold means anything. Keep running in Shadow.",
            };
        }

        ThresholdSweepPoint? best = sweep.FirstOrDefault(p =>
            p.AcceptedWithOutcomeCount >= MinAcceptedWithOutcome && p.AcceptedAgreementRate >= TargetAcceptedAgreement);
        if (best is null)
        {
            return new CalibrationRecommendation
            {
                Verdict = "KeepShadow",
                Reason = $"No threshold from 0.50 to 1.00 accepts at least {MinAcceptedWithOutcome} decisions while agreeing with the agent {TargetAcceptedAgreement:P0} of the time. Keep running in Shadow.",
            };
        }

        return new CalibrationRecommendation
        {
            Verdict = "TryGate",
            SuggestedAcceptAt = best.AcceptAt,
            Reason = $"At acceptAt {best.AcceptAt:0.00} the model would handle {best.AcceptRate:P0} of decisions and agree with the agent on {best.AcceptedAgreementRate:P0} of those ({best.AcceptedWithOutcomeCount} rows with an outcome). Still a small-sample estimate: enable Gate on one site first.",
        };
    }

    /// <summary>
    /// The ten buckets, always all ten and always lowest-first.
    ///
    /// <para>
    /// <b>One implementation for both diagrams.</b> This is the only place the bucket boundary
    /// convention and the Wilson interval are expressed; the site entries and the provider
    /// entries both call it, over their own row sets. A second copy of the rule is exactly how
    /// the two diagrams would silently disagree.
    /// </para>
    ///
    /// <para>
    /// <b>Boundary convention:</b> bucket <c>i</c> covers <c>[i/10, (i+1)/10)</c> with the
    /// single exception that the top bucket <c>[0.9, 1.0]</c> is closed at the top — a
    /// confidence of exactly 1.0 is clamped into bucket 9 rather than falling off the end of
    /// the range, so every bucketed row lands somewhere. Membership is
    /// <c>floor(confidence × 10 + 1e-9)</c> clamped to <c>[0, 9]</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Which rows are bucketed:</b> only those whose <c>ProbabilitiesJson</c> parses to a
    /// non-empty distribution. An empty <c>"{}"</c> — every <c>Noul</c> row, by construction —
    /// and an unparseable blob leave the row counted in <c>ObservationCount</c> and in
    /// <c>MeanConfidence</c> but out of the diagram. The reliability diagram is a plot of
    /// stated distributions against outcomes; a row with no stated distribution has nothing to
    /// plot, and inventing one from the single <c>Noul</c> number would put a point on the
    /// chart the model never made. A provider whose rows are all unbucketable therefore still
    /// gets ten buckets of zero count and <c>[0, 0]</c> intervals, never <c>NaN</c> and never a
    /// throw.
    /// </para>
    ///
    /// <para>
    /// <b>Non-finite confidences are not plotted.</b> <see cref="RowFacts.Confidence"/> is already
    /// <c>null</c> for a stored <c>NaN</c> or <c>±Infinity</c> — the guard at the read drops it,
    /// and the row falls back to its top-two probability exactly as a <c>null</c> confidence does
    /// — so a bucket's <c>PredictedMean</c> can never be non-finite and the row is never lost.
    /// </para>
    /// </summary>
    private static IReadOnlyList<ReliabilityBucket> BuildBuckets(IReadOnlyList<RowFacts> facts)
    {
        var buckets = new List<ReliabilityBucket>(BucketCount);

        for (int i = 0; i < BucketCount; i++)
        {
            List<RowFacts> members = facts
                .Where(f => f.HasDistribution && f.BucketIndex == i)
                .ToList();

            // The interval is the one that belongs to ObservedAgreementRate, so it shares that
            // figure's denominator: only rows that recorded an outcome can testify about
            // agreement, and a bucket whose rows all lack one gets the degenerate [0, 0].
            List<RowFacts> withOutcome = members.Where(f => f.Agreed.HasValue).ToList();
            int agreements = withOutcome.Count(f => f.Agreed == true);
            (double low, double high) = WilsonInterval(agreements, withOutcome.Count);

            buckets.Add(new ReliabilityBucket
            {
                LowerBound = i / (double)BucketCount,
                Count = members.Count,
                PredictedMean = members.Count == 0 ? 0 : members.Average(f => f.Confidence!.Value),
                ObservedAgreementRate = Rate(agreements, withOutcome.Count),
                ConfidenceIntervalLow = low,
                ConfidenceIntervalHigh = high,
            });
        }

        return buckets;
    }

    /// <summary>
    /// The 95% Wilson score interval for <paramref name="successes"/> of
    /// <paramref name="n"/> trials, clamped to the proportion scale <c>[0, 1]</c>.
    ///
    /// <para>
    /// Wilson rather than the normal approximation because the counts here are tiny: at n = 3
    /// the naive interval runs past 1 (and to <c>NaN</c> at n = 0), which is exactly the "three
    /// rows read as certainty" failure this interval exists to prevent.
    /// </para>
    /// </summary>
    private static (double Low, double High) WilsonInterval(int successes, int n)
    {
        if (n <= 0)
            return (0.0, 0.0);

        double p = (double)successes / n;
        double zSquared = WilsonZ * WilsonZ;
        double denominator = 1 + (zSquared / n);
        double centre = (p + (zSquared / (2 * n))) / denominator;
        double margin = WilsonZ / denominator
            * Math.Sqrt((p * (1 - p) / n) + (zSquared / (4.0 * n * n)));

        return (Math.Max(0.0, centre - margin), Math.Min(1.0, centre + margin));
    }

    private static double AgreementRate(IEnumerable<RowFacts> facts)
    {
        List<RowFacts> scored = facts.ToList();
        return Rate(scored.Count(f => f.Agreed == true), scored.Count(f => f.Agreed.HasValue));
    }

    /// <summary>
    /// The escalation rate: escalations over the rows whose escalation could actually be recomputed.
    ///
    /// <para>
    /// The denominator is <see cref="RowFacts.EscalationComputable"/>, not a bare row count, so a row
    /// whose metric this service cannot reduce — an unrecognised name, or
    /// <see cref="DecisionAcceptMetric.AtOrAbove"/> without the cutoff it sums from — is
    /// <b>counted and excluded</b> rather than guessed at. For every row whose declared metric is
    /// <see cref="DecisionAcceptMetric.Top"/> (which includes every row that recorded none, i.e. all
    /// historic data) this is the same set <see cref="RowFacts.Scoreable"/> described in phase 1, so
    /// nothing already recorded changes meaning.
    /// </para>
    /// </summary>
    private static double EscalationRate(IEnumerable<RowFacts> facts)
    {
        List<RowFacts> computable = facts.Where(f => f.EscalationComputable).ToList();
        return Rate(computable.Count(f => f.Escalated), computable.Count);
    }

    private static double MeanConfidence(IEnumerable<RowFacts> facts)
    {
        List<double> values = facts
            .Where(f => f.Confidence.HasValue)
            .Select(f => f.Confidence!.Value)
            .ToList();
        return values.Count == 0 ? 0.0 : values.Average();
    }

    private static double Rate(int numerator, int denominator) =>
        denominator == 0 ? 0.0 : (double)numerator / denominator;

    /// <summary>
    /// The <b>one</b> non-finite guard for every double this service reads out of stored data.
    ///
    /// <para>
    /// <c>NaN</c>, <see cref="double.PositiveInfinity"/> and <see cref="double.NegativeInfinity"/>
    /// all fail the same <see cref="double.IsFinite"/> test — a single predicate, never three
    /// special cases — and all three carry the same consequence: the value is not a figure, so it
    /// is excluded from the aggregate it cannot inform and never reaches the response.
    /// <see cref="System.Text.Json"/> refuses to write a non-finite double
    /// (<c>ArgumentException</c>: "cannot be written as valid JSON"), and the offending row is
    /// <b>stored</b>, so a single one would make the calibration endpoint 500 permanently.
    /// </para>
    ///
    /// <para>
    /// Returns <paramref name="value"/> unchanged when it is finite and <c>null</c> otherwise, so
    /// callers compose it with <c>??</c> rather than branching on it.
    /// </para>
    /// </summary>
    private static double? FiniteOrNull(double? value) =>
        value.HasValue && double.IsFinite(value.Value) ? value : null;

    /// <summary>
    /// The below-minimum warning, shared by the report level (no <paramref name="subject"/>, so
    /// the sentence is exactly the one T-003 shipped) and the provider level (subject = the
    /// provider's model, or its id when no row recorded a model).
    /// </summary>
    private static string BuildSampleSizeWarning(int count, string? subject = null) =>
        $"Only {count} decision observation{(count == 1 ? "" : "s")} recorded"
        + (subject is null ? "" : $" for {subject}")
        + $", below the {MinimumSampleSize}-row minimum. Thresholds tuned on a sample this small are not yet "
        + "meaningful — treat them as provisional and keep collecting Shadow rows.";

    /// <summary>
    /// Everything the report needs to know about one row, computed once so the grouping passes
    /// cannot disagree about what a row is worth.
    /// </summary>
    private sealed class RowFacts
    {
        public RowFacts(DecisionObservation row, double acceptAt, double minMargin)
        {
            Row = row;

            // The basis this row was written under. A missing name is Top — the rule that wrote every
            // row predating the column — while a name this service does not know is a different, and
            // explicitly non-guessed, case.
            Metric = ParseMetric(row.AcceptMetric, out bool recognised);
            Recognised = recognised;
            ScoreCutoff = row.ScoreCutoff;

            IReadOnlyDictionary<string, double> probabilities = ParseProbabilities(row.ProbabilitiesJson);

            // Only a readable, non-empty distribution is a *stated* distribution, and only a
            // stated distribution belongs on the reliability diagram. An empty "{}" — every
            // Noul row, by construction — is not one.
            HasDistribution = probabilities.Count > 0;

            // A Noul answer carries its whole distribution in Answer (the gate writes
            // Noul?.ToString() there), so it is scorable with an empty probability map. A
            // Choice/Score row with no readable distribution is not scorable at all.
            Scoreable = row.Kind == DecisionKind.Noul || HasDistribution;

            double top = 0.0;
            if (Scoreable)
            {
                var answer = new DecisionAnswer(
                    Name: row.Question,
                    Kind: row.Kind,
                    Choice: row.Kind == DecisionKind.Choice ? row.Answer : null,
                    Score: row.Kind == DecisionKind.Score ? ParseDouble(row.Answer) : null,
                    Noul: row.Kind == DecisionKind.Noul ? ParseDouble(row.Answer) : null,
                    Confidence: row.Confidence,
                    Probabilities: probabilities);

                // The shared rule from D-004 — deliberately not re-derived here, so the
                // escalation rate and the reliability diagram cannot drift from the gate.
                (top, double second) = DecisionProbability.TopTwo(answer);

                // A non-finite top or second is not a score, and BOTH are checked: a NaN
                // runner-up poisons the margin exactly as a NaN top poisons the level
                // (0.1 - NaN is NaN, and NaN < MinMargin is false), so either one read as a
                // number would let this row clear — or fail — a threshold by accident.
                if (FiniteOrNull(top) is null || FiniteOrNull(second) is null)
                {
                    // The row cannot be scored at all — deliberately the SAME state as a
                    // Choice/Score row with no readable distribution, so it leaves the escalation
                    // denominator instead of counting for or against the escalation rate.
                    Scoreable = false;
                    top = 0.0;
                }
                else
                {
                    EscalationComputable = recognised
                        && DecisionAcceptRule.MetricValue(
                            Metric, row.ScoreCutoff, answer.Kind, answer.Probabilities, answer.Noul) is not null;

                    // The row's OWN stored metric and cutoff, at the SUPPLIED thresholds — never the
                    // stored Escalated column, and never a universal rule. This is the whole point:
                    // a ReviewLoop row declares AtOrAbove with its step's MinScore, so recomputing it
                    // top/margin reports an escalation rate for a question that site never asks.
                    Escalated = EscalationComputable
                        && DecisionAcceptRule.Escalates(
                            Metric,
                            row.ScoreCutoff,
                            answer.Kind,
                            answer.Probabilities,
                            answer.Noul,
                            acceptAt,
                            minMargin);
                }
            }

            // The row's own confidence IS the predicted confidence where the provider reported
            // one; otherwise the top-two probability stands in, which is what gives a Noul row
            // (null Confidence by construction) a value at all. A stored confidence that is not a
            // finite number was never a reported one: FiniteOrNull drops it, and the top-two
            // stands in exactly as it would for a null — so the row still contributes a figure
            // rather than being silently removed from the diagram.
            Confidence = FiniteOrNull(row.Confidence) ?? (Scoreable ? top : null);

            if (HasDistribution && Confidence.HasValue)
                BucketIndex = BucketIndexOf(Confidence.Value);

            Agreed = ParseAgreed(row.DownstreamOutcomeJson);

            if (EscalationComputable && !Escalated)
            {
                // Only an accepted row is a row where no agent ran, so the projection is gated on the
                // same predicate as the escalation figure: a row whose metric this service cannot
                // reduce has no known acceptance, and counting it as a saved reviewer run would put a
                // guess inside the token figure. For a Top row — every row that recorded no metric,
                // i.e. all historic data — this is exactly the phase-1 `Scoreable && !Escalated`.
                //
                // The subtraction needs the WHOLE cost basis, and all three columns are nullable with
                // "not recorded, never zero" as their documented meaning. Coalescing a null decision
                // cost to zero (which this did) INVERTED that convention and overstated the figure in
                // the direction a reader acts on: `AgentTokensUsed` 500 with both decision columns null
                // reported the agent's entire cost as saved. A row missing any of the three therefore
                // contributes nothing, which is the conservative direction — an unrecorded basis can
                // understate the figure but can never invent a saving. Floored per row: a decision that
                // cost more than the agent it replaced saved nothing.
                TokensSaved = row.AgentTokensUsed is int agentTokensUsed
                    && row.DecisionInputTokens is int decisionInputTokens
                    && row.DecisionOutputTokens is int decisionOutputTokens
                        ? Math.Max(0, agentTokensUsed - decisionInputTokens - decisionOutputTokens)
                        : 0;
            }
        }

        public DecisionObservation Row { get; }

        /// <summary>
        /// The <see cref="DecisionAcceptMetric"/> this row was written under, read from the stored
        /// short name.
        ///
        /// <para>
        /// <b>A null or empty value is <see cref="DecisionAcceptMetric.Top"/></b>, and that is the
        /// recovered fact rather than a fallback: every row written before the column existed was
        /// written by a gate whose only rule was the top/margin rule. <see cref="Recognised"/> is
        /// false only for a value this service does not understand, which is a different thing from
        /// a missing one and is handled differently — the row is counted and left out of the
        /// escalation figure instead of being guessed at.
        /// </para>
        /// </summary>
        public DecisionAcceptMetric Metric { get; }

        /// <summary>Whether the row's stored metric name is one this service understands.</summary>
        public bool Recognised { get; }

        /// <summary>
        /// The cutoff the row's metric needs, straight off the row. Null for every metric that does
        /// not need one — and an <c>AtOrAbove</c> row whose cutoff is null is not recomputable, which
        /// <see cref="EscalationComputable"/> reports rather than this service inventing a MinScore.
        /// </summary>
        public int? ScoreCutoff { get; }

        /// <summary>
        /// Whether the row carries a distribution the reliability diagram can plot.
        /// </summary>
        public bool HasDistribution { get; }

        /// <summary>
        /// Whether <see cref="DecisionProbability.TopTwo"/> can be applied to the row <b>and
        /// yields a finite score</b>. A row whose top or runner-up is non-finite is not scorable:
        /// it is excluded from the escalation rate rather than read as a threshold result.
        /// </summary>
        public bool Scoreable { get; }

        /// <summary>
        /// Whether this row's escalation could be recomputed <b>at all</b> — the denominator of the
        /// escalation rate, and deliberately not the same predicate as <see cref="Scoreable"/>.
        ///
        /// <para>
        /// A row fails this when its metric name is unrecognised, when it declares
        /// <see cref="DecisionAcceptMetric.AtOrAbove"/> without the cutoff that metric sums from, or
        /// when the reduction yields nothing finite. Such a row is <b>counted and excluded</b>: the
        /// escalation figure answers only questions this service can actually answer, and inventing a
        /// reading for one it cannot would put a number an operator tunes against in front of a
        /// guess. This is the opposite of <see cref="DecisionAcceptRule.Escalates"/>, which fails
        /// <i>closed</i> for the same inputs because an executor must run its agent rather than skip
        /// it — the two callers want opposite things from one fact.
        /// </para>
        /// </summary>
        public bool EscalationComputable { get; }

        public bool Escalated { get; }

        /// <summary>
        /// The row's predicted confidence, or <c>null</c> when none is derivable. <b>Never
        /// non-finite:</b> a stored <c>NaN</c> or <c>±Infinity</c> is dropped at the read, so it
        /// can reach neither the mean, nor a bucket's <c>PredictedMean</c>, nor the response.
        /// </summary>
        public double? Confidence { get; }

        public bool? Agreed { get; }

        public int TokensSaved { get; }

        /// <summary>
        /// The bucket the row's top probability belongs in, or -1 when it is not bucketable.
        /// </summary>
        public int BucketIndex { get; } = -1;

        private static int BucketIndexOf(double confidence) =>
            Math.Clamp((int)Math.Floor((confidence * BucketCount) + BucketEpsilon), 0, BucketCount - 1);

        /// <summary>
        /// Reads the stored distribution. Anything that is not a JSON object of numbers —
        /// <c>"{}"</c>, a malformed blob, an array, a null — degrades to "no distribution"
        /// rather than throwing, because this service is not allowed to fail on data shape.
        /// </summary>
        private static IReadOnlyDictionary<string, double> ParseProbabilities(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return Empty;

            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, double>>(json) ?? Empty;
            }
            catch (JsonException)
            {
                return Empty;
            }
        }

        /// <summary>Reads the row's eagerly-computed <c>{ "agreed": bool }</c>, or null.</summary>
        private static bool? ParseAgreed(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return null;

                if (!document.RootElement.TryGetProperty("agreed", out JsonElement agreed))
                    return null;

                return agreed.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Reads a stored double. Anything that does not parse degrades to <c>null</c>.
        ///
        /// <para>
        /// <b>Deliberately returns a non-finite parse rather than filtering it here.</b>
        /// <c>"Infinity"</c>, <c>"-Infinity"</c> and <c>"NaN"</c> all parse under
        /// <see cref="NumberStyles.Float"/>, and returning <c>null</c> for one would not be the
        /// safe choice it looks like: on the <c>Noul</c> path a <c>null</c> <c>Noul</c> is the
        /// zero-information answer, which <see cref="DecisionProbability.TopTwo"/> reads as
        /// <c>(1.0, 0.0)</c> — the row would silently become <i>maximally confident</i> instead of
        /// unscorable. The non-finiteness has to survive to the top-two guard, which is what marks
        /// the row unscoreable. Every figure derived from here goes through <see cref="FiniteOrNull"/>.
        /// </para>
        /// </summary>
        private static double? ParseDouble(string? value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : null;

        /// <summary>
        /// Reads the stored <see cref="DecisionObservation.AcceptMetric"/> short name.
        ///
        /// <para>
        /// <b>Missing and unrecognised are different answers.</b> A null or blank value returns
        /// <c>true</c> with <see cref="DecisionAcceptMetric.Top"/> and <paramref name="recognised"/>
        /// set: the row predates the column, and the rule that wrote it <i>was</i> the top/margin
        /// rule, so Top is recovered rather than assumed. A non-blank value this service cannot map to
        /// a defined member returns <c>false</c> for <paramref name="recognised"/>, and the caller
        /// then counts the row and excludes it from the escalation figure — a metric the reader does
        /// not understand is not one to guess at, and it must never throw (this service is not allowed
        /// to fail on data shape, and a name written by a future version is exactly that kind of data).
        /// </para>
        /// </summary>
        private static DecisionAcceptMetric ParseMetric(string? value, out bool recognised)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                recognised = true;
                return DecisionAcceptMetric.Top;
            }

            // Enum.IsDefined rejects an out-of-range numeric string ("7"), which TryParse alone
            // would happily produce as an undefined value.
            if (Enum.TryParse(value, ignoreCase: true, out DecisionAcceptMetric metric)
                && Enum.IsDefined(metric))
            {
                recognised = true;
                return metric;
            }

            recognised = false;
            return DecisionAcceptMetric.Top;
        }

        private static readonly IReadOnlyDictionary<string, double> Empty =
            new Dictionary<string, double>();
    }
}
