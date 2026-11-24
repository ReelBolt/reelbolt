import { apiFetch } from './client';
import type {
  DecisionCalibrationParams,
  DecisionCalibrationReport,
} from '../types/decision-calibration';

/** Relative path only: the JWT is an httpOnly cookie that nginx turns into an Authorization header. */
export const DECISION_CALIBRATION_PATH = '/api/v1/workflow-engine/decision-calibration';

/** The order the params are appended in. Asserted by the tests, so it stays stable. */
const PARAM_ORDER = ['acceptAt', 'minMargin', 'providerId', 'site'] as const;

/**
 * Appends `key=value` only when the caller actually supplied a value.
 *
 * Deliberately `=== undefined`/`null` rather than a falsy check: `acceptAt = 0` and
 * `minMargin = 0` are legitimate thresholds, and a falsy check would silently drop them
 * and let the backend's defaults apply instead. Numbers go through `String` (never
 * `toFixed`/rounding), so `0.855` stays `0.855`.
 *
 * An empty or whitespace-only string is treated as "not supplied" — it would otherwise
 * send a meaningless empty filter (`?site=`).
 */
function appendParam(
  parts: string[],
  key: (typeof PARAM_ORDER)[number],
  value: string | number | undefined | null,
): void {
  if (value === undefined || value === null) return;
  if (typeof value === 'string' && value.trim() === '') return;
  parts.push(`${key}=${encodeURIComponent(String(value))}`);
}

/**
 * Builds the (relative) request URL from only the supplied params — the backend's own
 * defaults for `acceptAt`/`minMargin` are never sent from here.
 */
export function getDecisionCalibrationUrl(params: DecisionCalibrationParams = {}): string {
  const parts: string[] = [];
  for (const key of PARAM_ORDER) appendParam(parts, key, params[key]);
  return parts.length > 0 ? `${DECISION_CALIBRATION_PATH}?${parts.join('&')}` : DECISION_CALIBRATION_PATH;
}

/** Fetches the decision-model calibration report. Admin-only on the backend (403 otherwise). */
export function getDecisionCalibration(
  params: DecisionCalibrationParams = {},
): Promise<DecisionCalibrationReport> {
  return apiFetch<DecisionCalibrationReport>(getDecisionCalibrationUrl(params));
}
