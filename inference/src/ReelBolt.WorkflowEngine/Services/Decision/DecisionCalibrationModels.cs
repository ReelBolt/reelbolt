namespace ReelBolt.WorkflowEngine.Services.Decision;

/// <summary>
/// One of the report's fixed ten predicted-confidence buckets (0.1 wide, always all ten
/// present — <see cref="Count"/> 0 is a real, expected state for an empty bucket, not a
/// missing entry).
///
/// <para>
/// Serialized with camelCase property names, exactly as
/// <c>web/lib/types/decision-calibration.ts</c> mirrors them.
/// </para>
/// </summary>
public sealed record ReliabilityBucket
{
    /// <summary>Inclusive lower edge of the bucket, e.g. 0.7.</summary>
    public double LowerBound { get; init; }

    /// <summary>Observations that landed in this bucket, by predicted confidence.</summary>
    public int Count { get; init; }

    /// <summary>Mean predicted confidence of the observations in this bucket; 0 when empty.</summary>
    public double PredictedMean { get; init; }

    /// <summary>
    /// How often the model actually agreed with the agent, over the observations in this
    /// bucket that have a usable outcome. 0 when the bucket holds no such observation.
    /// </summary>
    public double ObservedAgreementRate { get; init; }

    /// <summary>
    /// Lower bound of the 95% Wilson score interval on <see cref="ObservedAgreementRate"/>,
    /// normalised to <c>[0, 1]</c> (a proportion, not a percentage). Degenerate <c>0</c> for
    /// an empty bucket rather than <c>NaN</c>.
    /// </summary>
    public double ConfidenceIntervalLow { get; init; }

    /// <summary>Upper bound of the same 95% Wilson interval, normalised to <c>[0, 1]</c>.</summary>
    public double ConfidenceIntervalHigh { get; init; }
}

/// <summary>One entry per <c>(Site, Question)</c> pair present in <c>decision_observations</c>.</summary>
public sealed record DecisionCalibrationSite
{
    /// <summary>The decision site (agent name) the observation belongs to, e.g. <c>Colorist</c>.</summary>
    public string Site { get; init; } = "";

    /// <summary>The question kind asked at that site, e.g. <c>Noul</c>.</summary>
    public string Question { get; init; } = "";

    public int ObservationCount { get; init; }

    /// <summary>Observations whose recorded outcome says the model agreed with the agent.</summary>
    public int AgreementCount { get; init; }

    /// <summary>
    /// <see cref="AgreementCount"/> over the observations that actually recorded a usable
    /// outcome — rows with a null, malformed or <c>agreed</c>-less
    /// <c>DownstreamOutcomeJson</c> are excluded from the denominator rather than counted as
    /// disagreements. 0 when no observation in the group recorded an outcome.
    /// </summary>
    public double AgreementRate { get; init; }

    /// <summary>
    /// Mean confidence over the observations whose confidence is derivable — the stored
    /// <c>Confidence</c> where present, otherwise <c>DecisionProbability.TopTwo</c>'s top
    /// (which is what gives a <c>Noul</c> row, whose <c>Confidence</c> is null by
    /// construction, a value at all).
    /// </summary>
    public double MeanConfidence { get; init; }

    /// <summary>Always ten buckets, lowest first.</summary>
    public IReadOnlyList<ReliabilityBucket> ReliabilityBuckets { get; init; } = Array.Empty<ReliabilityBucket>();

    /// <summary>
    /// Observations that <b>would</b> have escalated at the <c>acceptAt</c>/<c>minMargin</c>
    /// this report was asked for.
    /// </summary>
    public int EscalationCount { get; init; }

    /// <summary>
    /// <see cref="EscalationCount"/> over the observations that carry a scorable
    /// distribution. <b>Recomputed from the supplied thresholds</b>, never read from the
    /// stored <c>Escalated</c> column: that column reflects whatever thresholds were in force
    /// when the row was written and is kept only as a historical record, so reading it back
    /// would make the calibration view unable to answer "what would this threshold have done?".
    /// </summary>
    public double EscalationRate { get; init; }

    /// <summary>
    /// What this site's rows would have done at every acceptAt from 0.50 to 1.00 (step 0.05) at the
    /// report's own minMargin. Always 11 points, lowest threshold first. Derived by re-running the
    /// same per-row rule the escalation figure uses, never read from a stored column.
    /// </summary>
    public IReadOnlyList<ThresholdSweepPoint> ThresholdSweep { get; init; } = Array.Empty<ThresholdSweepPoint>();

    /// <summary>Plain verdict on whether Gate looks safe to try at the report's thresholds.</summary>
    public CalibrationRecommendation Recommendation { get; init; } = new();
}

/// <summary>One point of the threshold sweep: the trade-off at a single acceptAt.</summary>
public sealed record ThresholdSweepPoint
{
    public double AcceptAt { get; init; }

    /// <summary>Observations that would have been accepted (no agent run) at this threshold.</summary>
    public int AcceptedCount { get; init; }

    /// <summary>Accepted over the observations whose acceptance is computable; 0 when none are.</summary>
    public double AcceptRate { get; init; }

    /// <summary>Of the accepted rows with a recorded outcome, the share where the model matched the agent. 0 when none.</summary>
    public double AcceptedAgreementRate { get; init; }

    /// <summary>Accepted rows that have a recorded outcome (the denominator of <see cref="AcceptedAgreementRate"/>).</summary>
    public int AcceptedWithOutcomeCount { get; init; }

    /// <summary>Projected tokens saved at this threshold; a projection, never a measurement.</summary>
    public int TokensSaved { get; init; }
}

/// <summary>
/// A verdict derived from a site's data. <see cref="Verdict"/> is one of
/// <c>NotEnoughData</c>, <c>TryGate</c>, <c>KeepShadow</c>. Advisory only: nothing applies it.
/// </summary>
public sealed record CalibrationRecommendation
{
    public string Verdict { get; init; } = "NotEnoughData";

    /// <summary>For <c>TryGate</c>: the lowest swept acceptAt that keeps accepted-agreement at or above the target.</summary>
    public double? SuggestedAcceptAt { get; init; }

    /// <summary>One or two sentences, plain language, quoting the numbers the verdict rests on.</summary>
    public string Reason { get; init; } = "";
}

/// <summary>Per-provider roll-up, keyed by the inference provider that produced the decisions.</summary>
public sealed record DecisionCalibrationProvider
{
    public Guid ProviderId { get; init; }

    /// <summary>Model name the provider ran; empty when no row in the group recorded one.</summary>
    public string Model { get; init; } = "";

    public int ObservationCount { get; init; }

    /// <summary>Same denominator rule as <see cref="DecisionCalibrationSite.AgreementRate"/>.</summary>
    public double AgreementRate { get; init; }

    /// <summary>Same derivation as <see cref="DecisionCalibrationSite.MeanConfidence"/>.</summary>
    public double MeanConfidence { get; init; }

    /// <summary>
    /// The reliability diagram <b>for this provider alone</b> — the brief's goal 0 asks for
    /// "predicted confidence vs observed agreement <b>per site and provider</b>", and a
    /// per-provider diagram is not derivable from the site entries (one site's rows can be split
    /// across providers). Always ten buckets, lowest first, whether or not the provider has rows.
    ///
    /// <para>
    /// Deliberately the <b>same <see cref="ReliabilityBucket"/> record</b> the site entries use,
    /// computed by the same implementation, so the two diagrams cannot drift apart — and so the
    /// frontend mirrors one shape for one concept. The figures are recomputed over this
    /// provider's rows only, and are therefore not expected to match the site's.
    /// </para>
    /// </summary>
    public IReadOnlyList<ReliabilityBucket> ReliabilityBuckets { get; init; } = Array.Empty<ReliabilityBucket>();

    /// <summary>
    /// Non-null when <b>this provider</b> contributed fewer than
    /// <see cref="DecisionCalibrationService.MinimumSampleSize"/> observations — the same
    /// threshold, and the same wording shape, as the report-level
    /// <see cref="DecisionCalibrationReport.SampleSizeWarning"/>, but scoped to one provider so a
    /// single thin provider is visible even in a report whose total clears the minimum. Names the
    /// provider so a reader knows which one is thin.
    /// </summary>
    public string? SampleSizeWarning { get; init; }
}

/// <summary>Whole-report roll-up, including the thresholds the report was computed at.</summary>
public sealed record DecisionCalibrationTotals
{
    public int ObservationCount { get; init; }

    public double AgreementRate { get; init; }

    public double EscalationRate { get; init; }

    /// <summary>
    /// Tokens the decision model saved against having the agent decide, summed over the
    /// observations accepted at these thresholds, floored at 0 per row.
    ///
    /// <para>
    /// A row contributes only when <b>all three</b> token columns are recorded — the agent's
    /// counterfactual and the decision model's own input and output. Any unrecorded column means
    /// the cost basis is incomplete, so the row contributes 0: subtracting an absent decision cost
    /// as if it were zero would report the agent's whole cost as saved, which is the opposite of
    /// what "not recorded, never zero" means on those columns. The direction of the residual is
    /// deliberate — an incomplete basis understates the figure, and can never invent a saving.
    /// </para>
    /// </summary>
    public int TokensSaved { get; init; }

    /// <summary>
    /// Always true in this phase. <see cref="TokensSaved"/> is <b>projected at a supplied
    /// threshold, never measured</b>: Shadow pays for the agent it shadows, and a Gate run is
    /// what would actually skip it, so the UI must label the figure as a projection.
    /// </summary>
    public bool TokensSavedIsProjected { get; init; }

    /// <summary>Confidence threshold this report was computed at.</summary>
    public double AcceptAt { get; init; }

    /// <summary>Margin threshold this report was computed at.</summary>
    public double MinMargin { get; init; }
}

/// <summary>
/// The calibration report — read-only, derived entirely from <c>decision_observations</c>.
/// Serialized camelCase; <c>web/lib/types/decision-calibration.ts</c> mirrors this shape field
/// for field.
/// </summary>
public sealed record DecisionCalibrationReport
{
    public IReadOnlyList<DecisionCalibrationSite> Sites { get; init; } = Array.Empty<DecisionCalibrationSite>();

    public IReadOnlyList<DecisionCalibrationProvider> Providers { get; init; } = Array.Empty<DecisionCalibrationProvider>();

    public DecisionCalibrationTotals Totals { get; init; } = new();

    /// <summary>
    /// Non-null when the report covers fewer than
    /// <see cref="DecisionCalibrationService.MinimumSampleSize"/> observations, saying out loud
    /// that thresholds tuned on this data are not yet meaningful. The local install has zero
    /// rows and cannot produce one, so this warning is the normal state, not an edge case.
    /// </summary>
    public string? SampleSizeWarning { get; init; }
}
