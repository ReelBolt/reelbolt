import { apiFetch } from '../../api/client';
import type { AssetRef } from '../types';
import type { CleanupEstimate, CleanupJob, CleanupRequest, CleanupStart } from './types';

/** `file:<guid>` / `step_result:<guid>` for a clip's asset reference, or null when it names neither. */
export function mediaRefOf(ref: AssetRef): string | null {
  if (ref.kind === 'projectFile' && ref.projectFileId) return `file:${ref.projectFileId}`;
  if (ref.kind === 'stepResult' && ref.stepResultId) return `step_result:${ref.stepResultId}`;
  return null;
}

/** Every path segment is percent-encoded; the server decodes each segment back (so `file:<guid>` arrives intact). */
const base = (projectId: string, mediaRef: string): string => `/api/v1/projects/${encodeURIComponent(projectId)}/media/${encodeURIComponent(mediaRef)}/cleanup`;

const post = <T>(url: string, body: CleanupRequest): Promise<T> => apiFetch<T>(url, { method: 'POST', body: JSON.stringify(body) });

/** Starts a cleanup run. 202 with the execution id; refusals are `ApiError`s (402, 400, 404, 429). */
export const startCleanup = (projectId: string, mediaRef: string, body: CleanupRequest): Promise<CleanupStart> => post<CleanupStart>(base(projectId, mediaRef), body);

/** What the run would cost; never needs the egress flag. */
export const estimateCleanup = (projectId: string, mediaRef: string, body: CleanupRequest): Promise<CleanupEstimate> => post<CleanupEstimate>(`${base(projectId, mediaRef)}/estimate`, body);

export const getCleanupJob = (projectId: string, mediaRef: string, executionId: string): Promise<CleanupJob> => apiFetch<CleanupJob>(`${base(projectId, mediaRef)}/${encodeURIComponent(executionId)}`);

export const isCleanupFailed = (job: CleanupJob): boolean => job.state === 'failed' || job.state === 'cancelled';

/** Polls a job until it is ready (or fails, or `signal` aborts). `intervalMs` and `timeoutMs` are injectable for tests. */
export async function waitForCleanup(
  projectId: string,
  mediaRef: string,
  executionId: string,
  opts: { signal?: AbortSignal; intervalMs?: number; timeoutMs?: number; fetchJob?: typeof getCleanupJob } = {},
): Promise<CleanupJob> {
  const { signal, intervalMs = 1500, timeoutMs = 10 * 60_000, fetchJob = getCleanupJob } = opts;
  const started = Date.now();
  for (;;) {
    if (signal?.aborted) throw new DOMException('aborted', 'AbortError');
    const job = await fetchJob(projectId, mediaRef, executionId);
    if (job.state === 'ready') return job;
    if (isCleanupFailed(job)) throw new CleanupFailedError(job);
    if (Date.now() - started > timeoutMs) throw new CleanupFailedError({ ...job, state: 'failed', failureCode: 'timeout' });
    await new Promise<void>((resolve) => setTimeout(resolve, intervalMs));
  }
}

/** A run that ended without a result. The panel shows its own sentence for `failureCode`, never a raw message. */
export class CleanupFailedError extends Error {
  readonly job: CleanupJob;
  constructor(job: CleanupJob) {
    super(job.message?.trim() || 'The cleanup did not finish.');
    this.name = 'CleanupFailedError';
    this.job = job;
  }
}

export type CleanupRefusalKind = 'credits' | 'plan' | 'egress' | 'busy' | 'tooLong' | 'unavailable' | 'notFound' | 'other';
export interface CleanupRefusal {
  kind: CleanupRefusalKind;
  /** Credits still missing, for the add-credits refusal. */
  missing?: number;
}

/**
 * The estimate route answers 200 with a plain sentence in `unavailableReason` (no status or code):
 * sort it into the same kinds the start path returns by HTTP status, so the panel words a plan,
 * too-long or unavailable estimate like the matching refusal.
 */
export function describeEstimateUnavailableReason(reason: string): CleanupRefusal {
  const text = reason.toLowerCase();
  if (/plan|upgrade/.test(text)) return { kind: 'plan' };
  if (/too long|up to \d+ minutes|trim the clip/.test(text)) return { kind: 'tooLong' };
  if (/isn't set up|is not set up|not set up|isn't available|not available|unavailable|can't be cleaned|cannot be cleaned|haven't measured|not measured/.test(text)) {
    return { kind: 'unavailable' };
  }
  if (/credit/.test(text)) return { kind: 'credits' };
  return { kind: 'other' };
}

/** Sorts a refused start or estimate into the few states the panel words itself. */
export function describeCleanupRefusal(error: unknown): CleanupRefusal {
  const e = error as { status?: unknown; body?: unknown } | null;
  const status = typeof e?.status === 'number' ? e.status : 0;
  const body = typeof e?.body === 'object' && e.body !== null ? (e.body as Record<string, unknown>) : {};
  const code = typeof body.code === 'string' ? body.code : '';
  if (status === 402 && code === 'INSUFFICIENT_CREDITS') {
    const m = body.missing;
    return typeof m === 'number' && Number.isFinite(m) && m > 0 ? { kind: 'credits', missing: Math.ceil(m * 10) / 10 } : { kind: 'credits' };
  }
  if (status === 402) return { kind: 'plan' };
  if (status === 400 && code === 'EGRESS_REFUSED') return { kind: 'egress' };
  if (status === 429) return { kind: 'busy' };
  if (status === 413) return { kind: 'tooLong' };
  if (status === 409) return { kind: 'unavailable' };
  if (status === 404) return { kind: 'notFound' };
  return { kind: 'other' };
}
