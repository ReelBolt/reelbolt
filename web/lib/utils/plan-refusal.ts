import { ApiError } from '@/lib/api/client';
import { msg, type Msg } from '@/i18n/msg';
import type { WorkflowExecution } from '@/lib/types/workflow';

/** The code the API and the engine use for "your plan does not include this". */
export const PLAN_NOT_ENTITLED = 'PLAN_NOT_ENTITLED';

/** What a plan refusal says: the feature key (`video_generation`, `resolution`, ...) and, when the server named it, the plan that unlocks it. */
export interface PlanRefusal {
  feature: string | null;
  unlockPlanKey: string | null;
  unlockPlanName: string | null;
}

const str = (v: unknown): string | null => (typeof v === 'string' && v.trim() ? v : null);

/**
 * The plan refusal in an API error, or null when it is not one. Reads the gate's body, `{ code, feature, unlockPlan,
 * unlockPlanName }`; an assistant tool's `{ error, planGate: {...} }` is read the same way through `planRefusalFromBody`.
 */
export function planRefusalOf(error: unknown): PlanRefusal | null {
  if (!(error instanceof ApiError) || error.status !== 402) return null;
  return planRefusalFromBody(error.body);
}

/** The refusal in a gate body (or an object holding one under `planGate`), or null when its code is not PLAN_NOT_ENTITLED. */
export function planRefusalFromBody(body: unknown): PlanRefusal | null {
  if (typeof body !== 'object' || body === null) return null;
  const record = body as Record<string, unknown>;
  const gate = typeof record.planGate === 'object' && record.planGate !== null ? (record.planGate as Record<string, unknown>) : record;
  if (str(gate.code)?.toUpperCase() !== PLAN_NOT_ENTITLED) return null;
  return { feature: str(gate.feature), unlockPlanKey: str(gate.unlockPlan), unlockPlanName: str(gate.unlockPlanName) };
}

/** The plan feature a gated step type needs, by the same mapping as the server's gate; null for a step every plan may run. */
export function planFeatureOfStep(stepType: string | null | undefined): string | null {
  switch (stepType) {
    case 'VideoGenerate': return 'video_generation';
    case 'AudioGenerate': return 'audio_generation';
    case 'EditRoom':
    case 'GraphicsRoom':
    case 'ColorGradeRoom': return 'rooms';
    case 'AudioCleanup': return 'audio_cleanup';
    case 'VideoEnhance': return 'video_enhance';
    case 'VideoAnalyze': return 'segmentation';
    default: return null;
  }
}

/**
 * The refusal behind a failed run: set when a failed step carries PLAN_NOT_ENTITLED, with the feature read from that step's
 * type. Null for any other failure.
 */
export function failedRunPlanRefusal(execution: Pick<WorkflowExecution, 'stepResults'>): PlanRefusal | null {
  const failed = execution.stepResults?.find((r) => r.status === 'Failed' && r.failureCode?.toUpperCase() === PLAN_NOT_ENTITLED);
  if (!failed) return null;
  return { feature: planFeatureOfStep(failed.stepType), unlockPlanKey: null, unlockPlanName: null };
}

/** The feature in plain words, as a catalogue message; an unknown feature reads as "This feature". */
export function planFeatureMsg(feature: string | null | undefined): Msg {
  switch (feature) {
    case 'video_generation': return msg('billing.upgrade.feature.video_generation');
    case 'avatars': return msg('billing.upgrade.feature.avatars');
    case 'audio_generation': return msg('billing.upgrade.feature.audio_generation');
    case 'rooms': return msg('billing.upgrade.feature.rooms');
    case 'segmentation': return msg('billing.upgrade.feature.segmentation');
    case 'audio_cleanup': return msg('billing.upgrade.feature.audio_cleanup');
    case 'video_enhance': return msg('billing.upgrade.feature.video_enhance');
    case 'social_publishing': return msg('billing.upgrade.feature.social_publishing');
    case 'api': return msg('billing.upgrade.feature.api');
    case 'resolution': return msg('billing.upgrade.feature.resolution');
    default: return msg('billing.upgrade.feature.other');
  }
}
