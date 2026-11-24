import { apiFetch } from '../../api/client';
import type { EnhanceConsent, EnhanceEstimate, EnhanceJob, EnhanceRequest, EnhanceStart } from './types';

const base = (projectId: string, mediaRef: string): string => `/api/v1/projects/${encodeURIComponent(projectId)}/media/${encodeURIComponent(mediaRef)}/enhance`;
const consentUrl = (projectId: string): string => `/api/v1/projects/${encodeURIComponent(projectId)}/video-enhance/consent`;

const post = <T>(url: string, body: EnhanceRequest): Promise<T> => apiFetch<T>(url, { method: 'POST', body: JSON.stringify(body) });

export const startEnhance = (projectId: string, mediaRef: string, body: EnhanceRequest): Promise<EnhanceStart> => post<EnhanceStart>(base(projectId, mediaRef), body);

/** Writes nothing; never needs the project consent. */
export const estimateEnhance = (projectId: string, mediaRef: string, body: EnhanceRequest): Promise<EnhanceEstimate> => post<EnhanceEstimate>(`${base(projectId, mediaRef)}/estimate`, body);

export const getEnhanceJob = (projectId: string, mediaRef: string, executionId: string): Promise<EnhanceJob> => apiFetch<EnhanceJob>(`${base(projectId, mediaRef)}/${encodeURIComponent(executionId)}`);

/** 202 when stopping; 409 ENHANCE_ALREADY_FINISHED means the run ended first, which is just as good (resolves). */
export async function cancelEnhance(projectId: string, mediaRef: string, executionId: string): Promise<unknown> {
  try {
    return await apiFetch<unknown>(`${base(projectId, mediaRef)}/${encodeURIComponent(executionId)}/cancel`, { method: 'POST' });
  } catch (error) {
    const e = error as { status?: unknown; body?: unknown } | null;
    const code = typeof e?.body === 'object' && e.body !== null ? (e.body as { code?: unknown }).code : undefined;
    if (e?.status === 409 && code === 'ENHANCE_ALREADY_FINISHED') return null;
    throw error;
  }
}

export const getEnhanceConsent = (projectId: string): Promise<EnhanceConsent> => apiFetch<EnhanceConsent>(consentUrl(projectId));
export const putEnhanceConsent = (projectId: string): Promise<EnhanceConsent> => apiFetch<EnhanceConsent>(consentUrl(projectId), { method: 'PUT' });
export const deleteEnhanceConsent = (projectId: string): Promise<unknown> => apiFetch<unknown>(consentUrl(projectId), { method: 'DELETE' });

export const isEnhanceEnded = (job: EnhanceJob): boolean => job.state === 'failed' || job.state === 'cancelled';

/** A run that ended without a result. The UI words `failureCode` itself, never the raw message. */
export class EnhanceFailedError extends Error {
  readonly job: EnhanceJob;
  constructor(job: EnhanceJob) {
    super(job.message?.trim() || 'The enhancement did not finish.');
    this.name = 'EnhanceFailedError';
    this.job = job;
  }
}

/**
 * Polls a job until it is ready (or fails, or `signal` aborts). A failed poll is retried with backoff while the run is
 * not terminal (the server keeps it, and its credits, going): only a 404 or the timeout ends it. Interval, timeout and
 * fetcher are injectable for tests.
 */
export async function waitForEnhance(
  projectId: string,
  mediaRef: string,
  executionId: string,
  opts: { signal?: AbortSignal; intervalMs?: number; timeoutMs?: number; maxBackoffMs?: number; onProgress?: (job: EnhanceJob) => void; fetchJob?: typeof getEnhanceJob } = {},
): Promise<EnhanceJob> {
  const { signal, intervalMs = 2000, timeoutMs = 60 * 60_000, maxBackoffMs = 15_000, onProgress, fetchJob = getEnhanceJob } = opts;
  const started = Date.now();
  let failures = 0;
  let last: EnhanceJob | null = null;
  for (;;) {
    if (signal?.aborted) throw new DOMException('aborted', 'AbortError');
    let wait = intervalMs;
    try {
      const job = await fetchJob(projectId, mediaRef, executionId);
      if (signal?.aborted) throw new DOMException('aborted', 'AbortError');
      failures = 0;
      last = job;
      if (job.state === 'ready') return job;
      if (isEnhanceEnded(job)) throw new EnhanceFailedError(job);
      onProgress?.(job);
    } catch (error) {
      if (error instanceof EnhanceFailedError || (error as { name?: string } | null)?.name === 'AbortError') throw error;
      if ((error as { status?: unknown } | null)?.status === 404) throw error;
      failures += 1;
      wait = Math.min(maxBackoffMs, intervalMs * 2 ** failures);
    }
    if (Date.now() - started > timeoutMs) {
      throw new EnhanceFailedError({ ...(last ?? ({ executionId, op: 'Upscale', engine: 'Local', outputFileId: null, sourceMediaRef: mediaRef } as EnhanceJob)), state: 'failed', failureCode: 'timeout' });
    }
    await new Promise<void>((resolve) => setTimeout(resolve, wait));
  }
}

export type EnhanceRefusalKind = 'credits' | 'plan' | 'consent' | 'tooLong' | 'tooShort' | 'notMeasured' | 'email' | 'invalid' | 'busy' | 'unavailable' | 'notVideo' | 'notFound' | 'other';
export interface EnhanceRefusal {
  kind: EnhanceRefusalKind;
  /** Credits still missing, for the add-credits refusal. */
  missing?: number;
  /** `video_enhance` (the plan has no enhancement) or `resolution` (the plan caps the output size). */
  feature?: 'video_enhance' | 'resolution';
  maxSeconds?: number;
  /** Extend only: the shortest source the model accepts, in seconds. */
  minSeconds?: number;
}

/**
 * The estimate route answers 200 with `unavailableCode` (and a sentence in `unavailableReason`). The code decides;
 * the English-text match below is only the fallback for an answer that carries no code.
 */
export function describeEstimateUnavailable(code: string | null | undefined, reason: string | null | undefined): EnhanceRefusal {
  switch (code) {
    case 'PLAN_NOT_ENTITLED': return { kind: 'plan', feature: 'video_enhance' };
    case 'RESOLUTION_ABOVE_PLAN': return { kind: 'plan', feature: 'resolution' };
    case 'SOURCE_TOO_LONG': return { kind: 'tooLong' };
    case 'SOURCE_TOO_SHORT': return { kind: 'tooShort' };
    case 'ENHANCE_REQUEST_INVALID': return { kind: 'invalid' };
    case 'SOURCE_NOT_MEASURED': return { kind: 'notMeasured' };
    case 'ENHANCE_UNAVAILABLE': return { kind: 'unavailable' };
    case 'CONSENT_REQUIRED': return { kind: 'consent' };
    default:
  }
  return describeEstimateUnavailableReason(reason ?? '');
}

/** Fallback only: sorts an estimate sentence when no code came with it. */
export function describeEstimateUnavailableReason(reason: string): EnhanceRefusal {
  const text = reason.toLowerCase();
  if (/resolution|output size|up to \d+p/.test(text)) return { kind: 'plan', feature: 'resolution' };
  if (/plan|upgrade/.test(text)) return { kind: 'plan', feature: 'video_enhance' };
  if (/too long|trim the clip|up to \d+ minutes/.test(text)) return { kind: 'tooLong' };
  if (/too short|minimum|at least \d+ seconds/.test(text)) return { kind: 'tooShort' };
  if (/credit/.test(text)) return { kind: 'credits' };
  if (/not a video|isn't a video/.test(text)) return { kind: 'notVideo' };
  if (/not set up|isn't set up|unavailable|not available|isn't available/.test(text)) return { kind: 'unavailable' };
  return { kind: 'other' };
}

/** Sorts a refused start or estimate into the few states the UI words itself. */
export function describeEnhanceRefusal(error: unknown): EnhanceRefusal {
  const e = error as { status?: unknown; body?: unknown } | null;
  const status = typeof e?.status === 'number' ? e.status : 0;
  const body = typeof e?.body === 'object' && e.body !== null ? (e.body as Record<string, unknown>) : {};
  const code = typeof body.code === 'string' ? body.code : '';
  if (status === 402 && code === 'INSUFFICIENT_CREDITS') {
    const m = body.missing;
    return typeof m === 'number' && Number.isFinite(m) && m > 0 ? { kind: 'credits', missing: Math.ceil(m * 10) / 10 } : { kind: 'credits' };
  }
  if (status === 402) return { kind: 'plan', feature: body.feature === 'resolution' ? 'resolution' : 'video_enhance' };
  if (status === 403 && code === 'EMAIL_NOT_VERIFIED') return { kind: 'email' };
  if (status === 409 && code === 'CONSENT_REQUIRED') return { kind: 'consent' };
  if (status === 409 && code === 'SOURCE_NOT_MEASURED') return { kind: 'notMeasured' };
  if (status === 409) return { kind: 'unavailable' };
  if (status === 413) return typeof body.maxSeconds === 'number' ? { kind: 'tooLong', maxSeconds: body.maxSeconds } : { kind: 'tooLong' };
  if (status === 429) return { kind: 'busy' };
  if (status === 422 && code === 'ENHANCE_SOURCE_NOT_VIDEO') return { kind: 'notVideo' };
  if (status === 422 && code === 'SOURCE_TOO_SHORT') return typeof body.minSeconds === 'number' ? { kind: 'tooShort', minSeconds: body.minSeconds } : { kind: 'tooShort' };
  if (status === 400 && code === 'ENHANCE_REQUEST_INVALID') return { kind: 'invalid' };
  if (status === 400 && code === 'NOT_VIDEO') return { kind: 'notVideo' };
  if (status === 404) return { kind: 'notFound' };
  return { kind: 'other' };
}
