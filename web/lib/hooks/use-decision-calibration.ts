'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import { getDecisionCalibrationUrl } from '../api/decision-calibration';
import type {
  DecisionCalibrationParams,
  DecisionCalibrationReport,
} from '../types/decision-calibration';

/**
 * Fetches the decision-model calibration report.
 *
 * The SWR key is the fully-built URL, so it covers every supplied param: changing
 * `acceptAt` (or any other threshold/filter) produces a different key and re-queries
 * rather than serving the previous thresholds' cached report.
 */
export function useDecisionCalibration(params: DecisionCalibrationParams = {}) {
  return useSWR<DecisionCalibrationReport>(getDecisionCalibrationUrl(params), fetcher);
}
