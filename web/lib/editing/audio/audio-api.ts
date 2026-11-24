/**
 * The audio jobs (T-011): loudness measurement of the real mix and the ~10 s audition render. Both
 * are ordinary render executions the server tags with a job kind and keeps out of Deliver; this
 * module is the only place that knows their wire shape.
 */

import type { RenderCheck } from './render-checks';
import { apiFetch } from '../../api/client';
import { editTimelineKey, type EditTimelineRenderEstimate } from '../sync/api';

export type AudioJobKind = 'loudness' | 'audition';

export interface AudioJobStart {
  executionId: string;
  timelineId: string;
  revision: number;
  kind: AudioJobKind;
}

export interface AudioJobLoudness {
  integratedLufs: number;
  truePeakDbtp: number;
  loudnessRange: number;
  normalised: boolean;
  checks?: RenderCheck[] | null;
}

export interface AudioJob {
  executionId: string;
  kind: AudioJobKind;
  status: string;
  revision: number;
  errorMessage?: string | null;
  loudness?: AudioJobLoudness | null;
  durationSec?: number | null;
  stepResultId?: string | null;
}

const base = (projectId: string, timelineId: string) => editTimelineKey(projectId, timelineId);

export const startLoudness = (projectId: string, timelineId: string, revision?: number): Promise<AudioJobStart> =>
  apiFetch<AudioJobStart>(`${base(projectId, timelineId)}/loudness`, { method: 'POST', body: JSON.stringify(revision === undefined ? {} : { revision }) });

export const estimateLoudness = (projectId: string, timelineId: string, revision?: number): Promise<EditTimelineRenderEstimate> =>
  apiFetch<EditTimelineRenderEstimate>(`${base(projectId, timelineId)}/loudness/estimate`, { method: 'POST', body: JSON.stringify(revision === undefined ? {} : { revision }) });

export const startAudition = (projectId: string, timelineId: string, range: { inFrame: number; outFrame: number }, revision?: number): Promise<AudioJobStart> =>
  apiFetch<AudioJobStart>(`${base(projectId, timelineId)}/audition`, { method: 'POST', body: JSON.stringify({ range, ...(revision === undefined ? {} : { revision }) }) });

export const getAudioJob = (projectId: string, timelineId: string, executionId: string): Promise<AudioJob> =>
  apiFetch<AudioJob>(`${base(projectId, timelineId)}/audio-jobs/${executionId}`);

export const auditionUrl = (projectId: string, stepResultId: string): string => `/api/v1/projects/${projectId}/outputs/${stepResultId}/download`;

const TERMINAL_FAIL = ['failed', 'cancelled', 'canceled', 'timedout', 'timed_out'];
/** The server reports a finished job as `Passed` (execution status); `Completed` is accepted too. */
export const isJobDone = (job: AudioJob): boolean => ['passed', 'completed', 'succeeded'].includes(job.status.toLowerCase());
export const isJobFailed = (job: AudioJob): boolean => TERMINAL_FAIL.includes(job.status.toLowerCase());

/** The ~10 s window around the playhead an audition renders, clamped to the film. */
export function auditionRange(playheadFrame: number, endFrame: number, fps: number, seconds = 10): { inFrame: number; outFrame: number } | null {
  const total = Math.max(0, Math.round(endFrame));
  if (total <= 0) return null;
  const span = Math.max(1, Math.round(seconds * fps));
  let inFrame = Math.max(0, Math.round(playheadFrame) - Math.floor(span / 2));
  let outFrame = inFrame + span;
  if (outFrame > total) {
    outFrame = total;
    inFrame = Math.max(0, outFrame - span);
  }
  return outFrame > inFrame ? { inFrame, outFrame } : null;
}

/** Polls a job until it is completed or failed (or `signal` aborts); `intervalMs` is injectable for tests. */
export async function waitForAudioJob(
  projectId: string,
  timelineId: string,
  executionId: string,
  opts: { signal?: AbortSignal; intervalMs?: number; timeoutMs?: number; get?: typeof getAudioJob } = {},
): Promise<AudioJob> {
  const { signal, intervalMs = 1500, timeoutMs = 10 * 60_000, get = getAudioJob } = opts;
  const started = Date.now();
  for (;;) {
    if (signal?.aborted) throw new DOMException('aborted', 'AbortError');
    const job = await get(projectId, timelineId, executionId);
    if (isJobDone(job)) return job;
    if (isJobFailed(job)) throw new Error(job.errorMessage?.trim() || 'The audio job did not finish.');
    if (Date.now() - started > timeoutMs) throw new Error('The audio job took too long. Try again.');
    await new Promise((r) => setTimeout(r, intervalMs));
  }
}
