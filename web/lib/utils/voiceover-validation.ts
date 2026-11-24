import type { VoiceoverSource } from '@/lib/types/workflow';

/**
 * Client-side validation for the Voiceover step's source config. Pure: no React, no DOM, no fetch —
 * the builder UI (T-002) calls this and renders the string it returns.
 */

/** Shown when a `kind: 'NarrationPlan'` source has no NarrationWriter step chosen. */
export const NARRATION_PLAN_STEP_REQUIRED_ERROR = 'No NarrationWriter step selected.';

/** Shown when a `kind: 'PickupPlan'` source has no PickupPlanner step chosen. */
export const PICKUP_PLAN_STEP_REQUIRED_ERROR = 'No PickupPlanner step selected.';

/**
 * Returns the error message for one Voiceover source, or null when it is configured.
 *
 * A `NarrationPlan` with no chosen step is reported as UNCONFIGURED rather than merely empty: the
 * source has nothing to speak until a step is picked, so the builder must block on it instead of
 * letting an already-configured-looking step reach execution and fail there. Step orders are
 * 1-based, so `0` (and any non-integer or negative value) counts as no step chosen. Every `Inline`
 * and `ScriptScenes` source is valid here — their own rules live elsewhere.
 */
export function getVoiceoverSourceError(source: VoiceoverSource): string | null {
  if (source.kind === 'PickupPlan') {
    const pickupOrder = source.pickupPlannerStepOrder;
    return typeof pickupOrder === 'number' && Number.isInteger(pickupOrder) && pickupOrder >= 1
      ? null
      : PICKUP_PLAN_STEP_REQUIRED_ERROR;
  }
  if (source.kind !== 'NarrationPlan') return null;
  const stepOrder = source.narrationWriterStepOrder;
  if (typeof stepOrder !== 'number' || !Number.isInteger(stepOrder) || stepOrder < 1) {
    return NARRATION_PLAN_STEP_REQUIRED_ERROR;
  }
  return null;
}
