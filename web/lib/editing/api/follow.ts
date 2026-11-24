'use client';

/**
 * Follow subject: find the subject the customer boxes in a project video (a free, local run), then ask for the
 * camera path that keeps it centred on one clip. Speaks to `.../media/{mediaRef}/subject-tracks` and
 * `.../edit-timelines/{id}/follow`. This module is the one place the editor does so.
 */

import { useCallback, useEffect, useState } from 'react';
import useSWR from 'swr';
import { ApiError, apiFetch, fetcher } from '@/lib/api/client';

export type SubjectTrackStatus = 'queued' | 'running' | 'succeeded' | 'failed';

/** A box in fractions (0..1) of the source frame; x/y is the top-left corner. */
export interface SubjectBox {
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface SubjectTrackQueued {
  executionId: string;
  status: SubjectTrackStatus;
}

export interface SubjectTrackBox extends SubjectBox {
  /** Source seconds. */
  t: number;
}

export interface SubjectTrackResult {
  trackId: string;
  startSec: number;
  endSec: number;
  boxes: SubjectTrackBox[];
}

export interface SubjectTrackState {
  executionId: string;
  status: SubjectTrackStatus;
  failureCode: string | null;
  message: string | null;
  analysisStepResultId: string | null;
  track: SubjectTrackResult | null;
}

export type FollowTightness = 'Loose' | 'Medium' | 'Tight';
export type FollowSmoothness = 'Gentle' | 'Normal' | 'Snappy';
export type FollowOnLost = 'Hold' | 'Widen';

export interface FollowRequest {
  itemId: string;
  revision: number;
  track: { analysisStepResultId: string; trackId: string | null };
  tightness: FollowTightness;
  smoothness: FollowSmoothness;
  onLost: FollowOnLost;
  allowSoftZoom: boolean;
}

export interface FollowKey {
  sourceSec: number;
  positionX: number;
  positionY: number;
  scale: number;
}

export interface FollowAssumed {
  canvasWidth: number;
  canvasHeight: number;
  sourceWidth: number;
  sourceHeight: number;
}

export interface FollowResult {
  revision: number;
  itemId: string;
  keys: FollowKey[];
  assumed: FollowAssumed;
  zoomCap: number;
  zoomCapLimited: boolean;
  lostSpans: { fromSec: number; toSec: number }[];
  clampCount: number;
  warnings: string[];
}

const subjectBase = (projectId: string, mediaRef: string) => `/api/v1/projects/${encodeURIComponent(projectId)}/media/${encodeURIComponent(mediaRef)}/subject-tracks`;

export const startSubjectTrack = (projectId: string, mediaRef: string, body: { atSec: number; box: SubjectBox }): Promise<SubjectTrackQueued> =>
  apiFetch<SubjectTrackQueued>(subjectBase(projectId, mediaRef), { method: 'POST', body: JSON.stringify(body) });

export const fetchSubjectTrack = (projectId: string, mediaRef: string, executionId: string): Promise<SubjectTrackState> =>
  fetcher<SubjectTrackState>(`${subjectBase(projectId, mediaRef)}/${encodeURIComponent(executionId)}`);

export const requestFollow = (projectId: string, timelineId: string, body: FollowRequest): Promise<FollowResult> =>
  apiFetch<FollowResult>(`/api/v1/projects/${encodeURIComponent(projectId)}/edit-timelines/${encodeURIComponent(timelineId)}/follow`, { method: 'POST', body: JSON.stringify(body) });

export const isSubjectTerminal = (status: SubjectTrackStatus | undefined): boolean => status === 'succeeded' || status === 'failed';

const POLL_MS = 1500;
const POLL_MAX_MS = 12000;
/** After this many errors in a row the section stops asking and offers a retry. */
export const SUBJECT_POLL_ERROR_LIMIT = 4;

/** A refusal that asking again cannot fix (404, 403, other 4xx but not 408/429). */
const isFatalPoll = (error: unknown): boolean => error instanceof ApiError && error.status >= 400 && error.status < 500 && error.status !== 408 && error.status !== 429;

/** One subject-finding run, polled until it is terminal. A null id watches nothing. Same policy as the object search. */
export function useSubjectTrack(projectId: string | null, mediaRef: string | null, executionId: string | null) {
  const key = projectId && mediaRef && executionId ? ['subject-track', projectId, mediaRef, executionId] : null;
  const keyId = key ? key.join('|') : '';
  const [recorded, setRecorded] = useState({ keyId: '', count: 0, fatal: false });
  const errors = recorded.keyId === keyId ? recorded : { keyId, count: 0, fatal: false };
  const swr = useSWR<SubjectTrackState>(key, ([, p, m, e]: readonly [string, string, string, string]) => fetchSubjectTrack(p, m, e), {
    refreshInterval: 0,
    revalidateOnFocus: false,
    shouldRetryOnError: false,
    onSuccess: () => setRecorded((e) => (e.count === 0 && !e.fatal ? e : { keyId, count: 0, fatal: false })),
    onError: (error) => setRecorded((e) => ({ keyId, count: (e.keyId === keyId ? e.count : 0) + 1, fatal: isFatalPoll(error) })),
  });
  const { mutate, isValidating } = swr;
  const terminal = isSubjectTerminal(swr.data?.status);
  const halted = errors.fatal || errors.count >= SUBJECT_POLL_ERROR_LIMIT;
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
  return { ...swr, stalled: !terminal && halted, retry };
}

export type FollowErrorKind = 'credits' | 'other';

/** What a refusal means for the section; `message` is the API's own plain sentence, when it sent one (shown verbatim). */
export function classifyFollowError(error: unknown): { kind: FollowErrorKind; message: string | null } {
  if (!(error instanceof ApiError)) return { kind: 'other', message: null };
  const message = error.message && error.message.trim() ? error.message : null;
  return { kind: error.status === 402 ? 'credits' : 'other', message };
}
