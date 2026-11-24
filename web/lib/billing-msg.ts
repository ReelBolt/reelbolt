import { msg, type Msg } from '@/i18n/msg';
import type { CreditEstimateBasis, WorkflowCostEstimate, WorkflowStepCostEstimate } from '@/lib/types/billing';

/**
 * Catalogue-message versions of the credit-estimate prose in `utils/billing.ts`. Each returns a `Msg`
 * the component resolves with `useT().msg(m)`; figures travel as numbers (rounded the way
 * `formatCreditAmount` rounds them) and are formatted by the active locale inside the message.
 */

const finiteOrZero = (value: number | undefined): number => (typeof value === 'number' && Number.isFinite(value) ? value : 0);
/** Two decimals, like `formatCreditAmount`. */
export const creditFigure = (credits: number): number => Math.round(finiteOrZero(credits) * 100) / 100;
/** Round UP to whole credits (a "needs N available to start" figure never reads lower than the hold). */
export const creditFigureUp = (credits: number): number => Math.ceil(Math.round(finiteOrZero(credits) * 1000) / 1000);

/** "≈ 12.5–20 credits", collapsing to "≈ 20 credits" when both ends agree. */
export function creditRangeMsg(low: number, high: number): Msg {
  const lo = finiteOrZero(low);
  const hi = finiteOrZero(high);
  if (Math.round(lo * 100) === Math.round(hi * 100)) return msg('billing.estimate.rangeSingle', { amount: creditFigure(hi) });
  return msg('billing.estimate.range', { low: creditFigure(lo), high: creditFigure(hi) });
}

/** "Needs 13 credits available to start; it can cost more." Null when nothing is needed. */
export function creditsNeededMsg(requiredCredits: number): Msg | null {
  if (!Number.isFinite(requiredCredits) || requiredCredits <= 0) return null;
  return msg('billing.estimate.needed', { count: creditFigureUp(requiredCredits) });
}

/** Charges below this are shown as "less than" it; the figure is formatted for the locale, never typed into a string. */
const TINY_CHARGE = 0.01;

/** What one step was charged. Null when the server recorded no charge for the step. */
export function stepChargeMsg(creditsCharged: number | null | undefined): Msg | null {
  if (creditsCharged == null || !Number.isFinite(creditsCharged)) return null;
  if (creditsCharged <= 0) return msg('billing.estimate.charge.none');
  if (creditsCharged < TINY_CHARGE) return msg('billing.estimate.charge.tiny', { amount: TINY_CHARGE });
  return msg('billing.estimate.charge.amount', { count: creditFigure(creditsCharged) });
}

/** Where a step's figure came from. An unknown member is passed through unchanged (a newer backend). */
export function estimateBasisLabelMsg(basis: CreditEstimateBasis): Msg | string {
  switch (basis) {
    case 'History': return msg('billing.estimate.basis.history');
    case 'StepTypeHistory': return msg('billing.estimate.basis.stepTypeHistory');
    case 'Default': return msg('billing.estimate.basis.default');
    case 'NotCharged': return msg('billing.estimate.basis.notCharged');
    case 'AgentTypeHistory': return msg('billing.estimate.basis.agentTypeHistory');
    case 'OwnProvider': return msg('billing.estimate.basis.ownProvider');
    case 'AudioGeneration': return msg('billing.estimate.basis.audioGeneration');
    default: return basis;
  }
}

/** The run's headline figure: "≈ 40 credits (usually 30–52)". */
export function creditEstimateHeadlineMsg(typical: number, low: number, high: number): Msg {
  const lo = finiteOrZero(low);
  const hi = finiteOrZero(high);
  if (Math.round(lo * 100) === Math.round(hi * 100)) return msg('billing.estimate.headlineSingle', { typical: creditFigure(typical) });
  return msg('billing.estimate.headline', { typical: creditFigure(typical), low: creditFigure(lo), high: creditFigure(hi) });
}

const VIDEO_GENERATION_IN_REVIEW_LOOP = 'video_generation_in_review_loop';
/** A review loop may re-make earlier steps: their worst case is part of the ceiling. */
const REVIEW_STEP_RERUNS = 'review_step_reruns';

/** The fixed-price part of a run (generated video, steps a review may re-make), stated as a ceiling. Null when there is none. */
export function fixedPriceMsg(fixedMaxCredits: number | undefined, reasons: readonly string[] | undefined): Msg | null {
  const amount = finiteOrZero(fixedMaxCredits);
  if (amount <= 0) return null;
  if (reasons?.includes(REVIEW_STEP_RERUNS)) return msg('billing.estimate.fixedReviewReruns', { amount: creditFigure(amount) });
  return reasons?.includes(VIDEO_GENERATION_IN_REVIEW_LOOP)
    ? msg('billing.estimate.fixedReview', { amount: creditFigure(amount) })
    : msg('billing.estimate.fixed', { amount: creditFigure(amount) });
}

/** Why the usual range is as wide as it is: one message per known reason code, in the server's order. */
export function spreadReasonMsgs(reasons: readonly string[] | undefined): Msg[] {
  if (!reasons) return [];
  const out: Msg[] = [];
  const seen = new Set<string>();
  for (const code of reasons) {
    let m: Msg | null = null;
    switch (code) {
      case 'review_loop_repeats': m = msg('billing.estimate.reason.reviewLoop'); break;
      case 'footage_length': m = msg('billing.estimate.reason.footageLength'); break;
      case 'foreach_items_unknown': m = msg('billing.estimate.reason.foreachUnknown'); break;
      case 'own_provider_free': m = msg('billing.estimate.reason.ownProvider'); break;
      default: break;
    }
    if (m && !seen.has(m.$msg)) {
      seen.add(m.$msg);
      out.push(m);
    }
  }
  return out;
}

/** One sentence saying what the estimate was built from. */
export function estimateBasisMsg(
  estimate: Pick<WorkflowCostEstimate, 'hasHistory' | 'rangeBasis' | 'pastRunCount'>,
): Msg {
  switch (estimate.rangeBasis) {
    case 'PastRuns': {
      const n = finiteOrZero(estimate.pastRunCount);
      if (n > 0) return msg('billing.estimate.sentence.pastRuns', { count: n });
      break;
    }
    case 'StepHistory': return msg('billing.estimate.sentence.stepHistory');
    case 'FirstRun': return msg('billing.estimate.sentence.firstRun');
    default: break;
  }
  return estimate.hasHistory ? msg('billing.estimate.sentence.history') : msg('billing.estimate.sentence.noHistory');
}

/** One step as a line in the breakdown: its label (or type), the range, and where the range came from. */
export function stepEstimateMsg(step: WorkflowStepCostEstimate): Msg {
  const name = step.label?.trim() ? step.label.trim() : step.stepType;
  if (step.basis === 'NotCharged') return msg('billing.estimate.step.notCharged', { name });
  const fixedMax = finiteOrZero(step.fixedMaxCredits);
  if (fixedMax > 0) return msg('billing.estimate.step.upTo', { name, amount: creditFigure(fixedMax) });
  const basis = estimateBasisLabelMsg(step.basis);
  const range = creditRangeMsg(step.lowCredits, step.highCredits);
  return finiteOrZero(step.expectedRuns) > 1.05
    ? msg('billing.estimate.step.rangeAgain', { name, range, basis })
    : msg('billing.estimate.step.range', { name, range, basis });
}

/** Why a plan-gated feature is refused (`PLAN_NOT_ENTITLED` with a `feature` key). Unknown keys return null (a newer backend). */
export function planFeatureMsg(feature: string | undefined): Msg | null {
  switch (feature) {
    case 'social_publishing': return msg('billing.planFeature.socialPublishing');
    default: return null;
  }
}
