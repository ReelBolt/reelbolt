'use client';

/**
 * Magic blur: find an object the customer NAMES in a project video, then put a region effect on it.
 * Speaks to the object-analysis routes (`.../media/{mediaRef}/objects`) and the project-level
 * egress consent (`.../object-effects/consent`). This module is the one place the editor does so.
 */

import { useCallback, useEffect, useState } from 'react';
import useSWR from 'swr';
import { ApiError, apiFetch, fetcher } from '@/lib/api/client';
import type { RegionEffectType } from '../types';
import type { SeedBody } from '../selection/seed';

export type ObjectRequestStatus = 'queued' | 'running' | 'succeeded' | 'failed';

export const REGION_EFFECTS: readonly RegionEffectType[] = ['blur', 'pixelate', 'highlight', 'spotlight', 'colorpop', 'outline'];
/** A clip carries at most this many region effects (the contract's cap). */
export const MAX_REGION_EFFECTS = 4;
export const DEFAULT_OBJECT_STRENGTH = 0.7;
export const PROMPT_MAX = 80;

export interface ObjectEstimate {
  billingEnabled: boolean;
  credits: number;
  frames: number;
  seconds: number;
  balanceCredits: number;
  affordable: boolean;
  cached: boolean;
  consented: boolean;
  unavailableReason: string | null;
  /** True when the video length is unknown, so `credits` is a maximum. */
  upperBound?: boolean;
}

export interface ObjectRequest {
  requestId: string;
  executionId: string;
  status: ObjectRequestStatus;
  prompt: string;
  matchScope: string | null;
  effect: string;
  strength: number;
  invert: boolean;
  featherPx: number;
  analysisStepResultId: string | null;
  trackIds: string[] | null;
  coverageEndSec: number | null;
  failureCode: string | null;
  message: string | null;
  createdAt: string;
}

export interface ObjectQueued {
  requestId: string;
  executionId: string;
  /** The workflow that runs the job; lets the editor offer "Stop the job". Absent on older servers. */
  workflowId?: string | null;
  status: ObjectRequestStatus;
}

export interface ObjectConsent {
  consented: boolean;
  consentedAt: string | null;
}

export interface ObjectRunBody {
  /** The words; optional when `seed` names the object. */
  prompt?: string;
  /** The object the user pointed at: `{atSec, point, box?}` (source seconds, source fractions). */
  seed?: SeedBody;
  effect: RegionEffectType;
  strength: number;
  invert: boolean;
  featherPx: number;
}

const objectsBase = (projectId: string, mediaRef: string) => `/api/v1/projects/${encodeURIComponent(projectId)}/media/${encodeURIComponent(mediaRef)}/objects`;
const consentUrl = (projectId: string) => `/api/v1/projects/${encodeURIComponent(projectId)}/object-effects/consent`;

export const estimateObject = (projectId: string, mediaRef: string, what: string | { prompt?: string; seed?: SeedBody }): Promise<ObjectEstimate> =>
  apiFetch<ObjectEstimate>(`${objectsBase(projectId, mediaRef)}/estimate`, { method: 'POST', body: JSON.stringify(typeof what === 'string' ? { prompt: what } : what) });

export const startObject = (projectId: string, mediaRef: string, body: ObjectRunBody): Promise<ObjectQueued> =>
  apiFetch<ObjectQueued>(objectsBase(projectId, mediaRef), { method: 'POST', body: JSON.stringify(body) });

export const fetchObject = (projectId: string, mediaRef: string, requestId: string): Promise<ObjectRequest> =>
  fetcher<ObjectRequest>(`${objectsBase(projectId, mediaRef)}/${encodeURIComponent(requestId)}`);

export const grantObjectConsent = (projectId: string): Promise<ObjectConsent> => apiFetch<ObjectConsent>(consentUrl(projectId), { method: 'PUT' });
export const withdrawObjectConsent = (projectId: string): Promise<ObjectConsent> => apiFetch<ObjectConsent>(consentUrl(projectId), { method: 'DELETE' });

export const isTerminal = (status: ObjectRequestStatus | undefined): boolean => status === 'succeeded' || status === 'failed';

const POLL_MS = 1500;
const POLL_MAX_MS = 12000;
/** After this many errors in a row the panel stops asking and offers a retry. */
export const POLL_ERROR_LIMIT = 4;

/** A refusal that asking again cannot fix (404, 403, other 4xx but not 408/429). */
const isFatalPoll = (error: unknown): boolean => error instanceof ApiError && error.status >= 400 && error.status < 500 && error.status !== 408 && error.status !== 429;

/**
 * One request, polled every 1.5 s until it is terminal. A null id watches nothing. A fatal refusal (404, 403, ...)
 * stops polling at once; a 5xx or network error backs off and, after {@link POLL_ERROR_LIMIT} in a row, stops with
 * `stalled` true so the panel can say so and offer `retry`.
 */
export function useObjectRequest(projectId: string | null, mediaRef: string | null, requestId: string | null) {
  const key = projectId && mediaRef && requestId ? ['object-request', projectId, mediaRef, requestId] : null;
  const keyId = key ? key.join('|') : '';
  const [recorded, setRecorded] = useState({ keyId: '', count: 0, fatal: false });
  const errors = recorded.keyId === keyId ? recorded : { keyId, count: 0, fatal: false };
  const swr = useSWR<ObjectRequest>(
    key,
    ([, p, m, r]: readonly [string, string, string, string]) => fetchObject(p, m, r),
    {
      // Polling is ours, not SWR's: SWR skips an interval tick while its cache holds an error, and restarts the
      // timer whenever the host re-renders with a new config object (the inspector does, during playback).
      refreshInterval: 0,
      revalidateOnFocus: false,
      shouldRetryOnError: false,
      onSuccess: () => setRecorded((e) => (e.count === 0 && !e.fatal ? e : { keyId, count: 0, fatal: false })),
      onError: (error) => setRecorded((e) => ({ keyId, count: (e.keyId === keyId ? e.count : 0) + 1, fatal: isFatalPoll(error) })),
    },
  );
  const { mutate, isValidating } = swr;
  const terminal = isTerminal(swr.data?.status);
  const halted = errors.fatal || errors.count >= POLL_ERROR_LIMIT;
  const delay = Math.min(POLL_MAX_MS, POLL_MS * 2 ** errors.count);
  useEffect(() => {
    if (!keyId || terminal || halted || isValidating) return;
    const id = window.setTimeout(() => void mutate(), delay);
    return () => window.clearTimeout(id);
  }, [keyId, terminal, halted, isValidating, delay, mutate]);
  const retry = useCallback(() => {
    setRecorded({ keyId, count: 0, fatal: false });
    void mutate();
  }, [keyId, mutate]);
  const stalled = !terminal && halted;
  return { ...swr, stalled, retry };
}

export const fetchObjectConsent = (projectId: string): Promise<ObjectConsent> => fetcher<ObjectConsent>(consentUrl(projectId));

/** The project's consent, read on its own so "Withdraw" does not wait for a price. */
export function useObjectConsent(projectId: string | null) {
  return useSWR<ObjectConsent>(projectId ? ['object-consent', projectId] : null, ([, p]: readonly [string, string]) => fetchObjectConsent(p), { revalidateOnFocus: false });
}

/** The requests made for this source, newest first (used to name an existing effect's object). */
export function useObjectList(projectId: string | null, mediaRef: string | null) {
  return useSWR<ObjectRequest[]>(
    projectId && mediaRef ? ['object-list', projectId, mediaRef] : null,
    ([, p, m]: readonly [string, string, string]) => fetcher<ObjectRequest[]>(objectsBase(p, m)),
    { revalidateOnFocus: false },
  );
}

/** `analysisStepResultId` -> the words the customer typed. */
export function promptsByAnalysis(list: readonly ObjectRequest[] | undefined): Map<string, string> {
  const map = new Map<string, string>();
  for (const r of list ?? []) if (r.analysisStepResultId && !map.has(r.analysisStepResultId)) map.set(r.analysisStepResultId, r.prompt);
  return map;
}

export type ObjectErrorKind = 'plan' | 'credits' | 'tooLong' | 'consent' | 'notFoundAtPoint' | 'other';

/** The failure code for a tap that landed on no object. */
export const OBJECT_NOT_FOUND_AT_POINT = 'OBJECT_NOT_FOUND_AT_POINT';

/** What a refusal means for the panel; `message` is the API's own plain sentence, when it sent one. */
export function classifyObjectError(error: unknown): { kind: ObjectErrorKind; message: string | null } {
  if (!(error instanceof ApiError)) return { kind: 'other', message: null };
  const message = error.message && error.message.trim() ? error.message : null;
  if (error.status === 402 && error.code === 'PLAN_NOT_ENTITLED') return { kind: 'plan', message };
  if (error.status === 402) return { kind: 'credits', message };
  if (error.status === 422 && error.code === 'OBJECT_SOURCE_TOO_LONG') return { kind: 'tooLong', message };
  if (error.code === OBJECT_NOT_FOUND_AT_POINT) return { kind: 'notFoundAtPoint', message };
  if (error.status === 409 && error.code === 'OBJECT_CONSENT_REQUIRED') return { kind: 'consent', message };
  return { kind: 'other', message };
}
