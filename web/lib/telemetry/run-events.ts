/**
 * Maps a refused run to the closed `alpha.run_blocked` detail token. Pure: no I/O, no message text,
 * nothing user-typed ever reaches the event, only one of the fixed tokens.
 */
import { ApiError } from '@/lib/api/client';
import { insufficientCredits } from '@/lib/api/billing';
import { sourceProblemOf } from '@/lib/api/source-limits';
import type { RunBlockedDetail } from './allowlist';

/** The page's disabled-state key (RunBlock.key) as a token; null when nothing blocks. */
export function runBlockDetailFromKey(key: string | null | undefined): RunBlockedDetail | null {
  if (!key) return null;
  if (key === 'notes:required') return 'notes_required';
  if (key === 'plan') return 'plan';
  if (key === 'credits') return 'credits';
  if (key.startsWith('source:')) return 'source';
  return 'readiness';
}

/** The error a run start threw, as a token. */
export function runBlockDetailOf(err: unknown): RunBlockedDetail {
  if (insufficientCredits(err)) return 'credits';
  if (sourceProblemOf(err)) return 'source';
  if (err instanceof ApiError) {
    if (err.status === 402) return 'plan';
    if (err.status === 409 && (err.code === 'ESTIMATE_NOT_ACCEPTED' || err.code === 'ESTIMATE_OVER_CAP')) return 'estimate_gate';
    if (err.status === 503 && err.code === 'ESTIMATE_UNAVAILABLE') return 'estimate_gate';
    if (err.status === 429) return 'rate_limited';
  }
  return 'other';
}
