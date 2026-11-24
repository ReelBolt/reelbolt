/**
 * TS mirror of the WorkflowEngine's `GET /api/v1/workflow-engine/decision-calibration`
 * response records (`DecisionCalibrationModels.cs`, plan task D-002). Field names are
 * exactly the serialized names — the backend emits camelCase, like every other
 * ReelBolt JSON surface.
 *
 * Read-only: the calibration view reports on `decision_observations`; nothing here
 * writes and no field is optional in a response.
 */

/**
 * One of the fixed 10 predicted-confidence buckets (0.1 wide, always all ten present —
 * `count === 0` is a real, expected state for an empty bucket, not a missing entry).
 */
export interface ReliabilityBucket {
  /** Inclusive lower edge of the bucket, e.g. 0.7. */
  lowerBound: number;
  /** Observations that landed in this bucket. 0 is normal. */
  count: number;
  /** Mean predicted confidence of the observations in this bucket. */
  predictedMean: number;
  /** How often the model actually agreed with the agent, within this bucket. */
  observedAgreementRate: number;
  /** Wilson interval lower bound — a 3-row bucket must not read as certainty. */
  confidenceIntervalLow: number;
  /** Wilson interval upper bound. */
  confidenceIntervalHigh: number;
}

/** One point of a site's acceptAt sweep (0.50..1.00 step 0.05, 11 points, at the report's minMargin). */
export interface ThresholdSweepPoint {
  acceptAt: number;
  acceptedCount: number;
  /** Share of observations the model would have handled alone (no agent run). */
  acceptRate: number;
  /** Of accepted rows with a recorded outcome, how often the model matched the agent. */
  acceptedAgreementRate: number;
  acceptedWithOutcomeCount: number;
  /** Projected, never measured. */
  tokensSaved: number;
}

export type CalibrationVerdict = 'NotEnoughData' | 'TryGate' | 'KeepShadow';

/** Advisory verdict computed by the engine; nothing applies it. */
export interface CalibrationRecommendation {
  verdict: CalibrationVerdict;
  suggestedAcceptAt: number | null;
  reason: string;
}

/** One entry per `(site, question)` pair observed in `decision_observations`. */
export interface DecisionCalibrationSite {
  /** The decision site (agent name) the observation belongs to, e.g. `Colorist`. */
  site: string;
  /** The question kind asked at that site, e.g. `Noul`. */
  question: string;
  observationCount: number;
  agreementCount: number;
  /** `agreementCount / observationCount`. */
  agreementRate: number;
  /** Mean confidence, with `Noul` rows derived via the top-two-probabilities rule. */
  meanConfidence: number;
  reliabilityBuckets: ReliabilityBucket[];
  /**
   * Escalations recomputed from the thresholds the request supplied — never the stored
   * `Escalated` column, which reflects whatever thresholds were in force when the row
   * was written.
   */
  escalationCount: number;
  escalationRate: number;
  thresholdSweep: ThresholdSweepPoint[];
  recommendation: CalibrationRecommendation;
}

/** Per-provider roll-up, keyed by the inference provider that produced the decisions. */
export interface DecisionCalibrationProvider {
  providerId: string;
  /** Model name the provider ran, e.g. `gpt-4o-mini`. Empty when no row recorded one. */
  model: string;
  observationCount: number;
  agreementRate: number;
  meanConfidence: number;
  /**
   * The reliability diagram **for this provider alone** — deliberately the *same*
   * `ReliabilityBucket` shape the site entries use, computed by the same backend routine over
   * this provider's rows only, so the two diagrams cannot drift apart. Its figures are
   * recomputed over that provider's rows and are therefore not expected to match the site's.
   *
   * Always ten buckets, lowest first, whether or not the provider has buckets able to carry
   * observations — "all ten empty" is a real state, and the UI says so rather than drawing a
   * blank chart.
   */
  reliabilityBuckets: ReliabilityBucket[];
  /**
   * Non-null when **this one provider** contributed fewer than 30 observations — the same
   * threshold and wording shape as the report-level `sampleSizeWarning`, scoped to one provider
   * so a single thin provider stays visible even in a report whose total clears the minimum.
   * Rendered verbatim; never replaced by copy of the UI's own.
   */
  sampleSizeWarning: string | null;
}

/** Whole-report roll-up, including the thresholds the report was computed at. */
export interface DecisionCalibrationTotals {
  observationCount: number;
  agreementRate: number;
  escalationRate: number;
  /**
   * Tokens the decision model saved versus having the agent decide, floored at 0 per
   * row. Rows with no recorded `AgentTokensUsed` contribute 0.
   */
  tokensSaved: number;
  /** Always true this phase — `tokensSaved` is a projection, and the UI must say so. */
  tokensSavedIsProjected: boolean;
  /** Threshold the report was computed at (backend default 0.85 when not supplied). */
  acceptAt: number;
  /** Margin threshold the report was computed at (backend default 0.25). */
  minMargin: number;
}

export interface DecisionCalibrationReport {
  sites: DecisionCalibrationSite[];
  providers: DecisionCalibrationProvider[];
  totals: DecisionCalibrationTotals;
  /** Non-null when the total observation count is below 30 — thresholds tuned on this data are not yet meaningful. */
  sampleSizeWarning: string | null;
}

/**
 * Optional query params. All four are optional; the backend defaults are
 * `acceptAt = 0.85` and `minMargin = 0.25`, and the client deliberately does **not**
 * send them — an omitted param is omitted, so the server's defaults stay the single
 * source of truth.
 */
export interface DecisionCalibrationParams {
  /** Confidence threshold ∈ [0, 1]. `0` is a valid, explicitly supplied value. */
  acceptAt?: number;
  /** Minimum margin ∈ [0, 1]. `0` is a valid, explicitly supplied value. */
  minMargin?: number;
  /** Restrict to one inference provider. */
  providerId?: string;
  /** Restrict to one decision site (agent name). */
  site?: string;
}
