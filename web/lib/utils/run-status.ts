import type { T } from '@/i18n/translator';
import { enT, labelOf } from '@/lib/i18n-default';
import type { NeedsAttention } from '../types/workflow';

export interface RunStatusInput {
  status: string;
  needsAttention?: NeedsAttention | null;
}

/** True when the run passed but its review stopped below the quality bar. */
export function isNeedsAttention(run: RunStatusInput): boolean {
  return run.status === 'Passed' && !!run.needsAttention;
}

/**
 * The single derivation of a run's badge text and colour; amber "Needs attention" replaces green
 * "Passed". Pass the component's `useT()` for the active language (English without it).
 */
export function runStatusDisplay(run: RunStatusInput, t: T = enT): { label: string; color: string } {
  if (isNeedsAttention(run)) return { label: t('libLabels.runStatus.NeedsAttention'), color: 'yellow' };
  return { label: labelOf(t, 'runStatus', run.status, run.status), color: STATUS_FALLBACK[run.status] ?? 'gray' };
}

const STATUS_FALLBACK: Record<string, string> = {
  Queued: 'gray', Running: 'blue', Passed: 'green', Failed: 'red', Cancelled: 'orange', Stopped: 'orange',
};
