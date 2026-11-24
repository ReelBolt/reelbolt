import { mutate as globalMutate } from 'swr';
import { grantAvatarMediaConsent } from '@/lib/api/avatars';
import { mediaPoolKey } from '@/lib/editing/api/media';
import {
  cancelSubjectClip,
  estimateSubjectClip,
  getSubjectClip,
  startSubjectClip,
  SubjectClipFailedError,
  waitForSubjectClip,
  type SubjectClipEstimate,
  type SubjectClipRequest,
  type SubjectClipStart,
  type SubjectClipStatus,
} from '@/lib/editing/subjects/subject-clip-api';

/** The calls a clip job makes, injectable for tests. */
export interface SubjectClipApi {
  estimate: (projectId: string, body: SubjectClipRequest) => Promise<SubjectClipEstimate>;
  start: (projectId: string, body: SubjectClipRequest) => Promise<SubjectClipStart>;
  wait: (projectId: string, executionId: string, signal: AbortSignal, onProgress: (status: SubjectClipStatus) => void) => Promise<SubjectClipStatus>;
  cancel: (projectId: string, executionId: string) => Promise<unknown>;
  /** Records the project's consent to send avatar and object files to AI services (`PUT …/avatars/media-consent`). */
  grantConsent: (projectId: string) => Promise<unknown>;
  /** Re-reads a run (after a cancel found it already finished). */
  getJob?: (projectId: string, executionId: string) => Promise<SubjectClipStatus>;
  /** The new clip is an ordinary project file: re-read the media pool so it shows up. */
  refreshPool: (projectId: string) => void;
}

export const defaultSubjectClipApi: SubjectClipApi = {
  estimate: estimateSubjectClip,
  start: startSubjectClip,
  wait: (projectId, executionId, signal, onProgress) => waitForSubjectClip(projectId, executionId, { signal, onProgress }),
  cancel: cancelSubjectClip,
  grantConsent: grantAvatarMediaConsent,
  getJob: getSubjectClip,
  refreshPool: (projectId) => void globalMutate(mediaPoolKey(projectId)),
};

export type SubjectClipJobState = 'queued' | 'running' | 'ready' | 'failed' | 'cancelled';

/** One clip being made (or just made) in this browser session. */
export interface SubjectClipJob {
  /** Stable for the session; the run's id. */
  executionId: string;
  projectId: string;
  state: SubjectClipJobState;
  /** The names of the people and objects it shows, as chosen. */
  names: readonly string[];
  /** The new project files, one per take, once ready. */
  outputFileIds: readonly string[];
  /** The server's sentence about a failure. */
  message: string | null;
  failureCode: string | null;
  /** What the server said the run holds, in credits, when it said. */
  credits: number | null;
  startedAt: number;
}

export interface StartSubjectClipOptions {
  names: readonly string[];
  api?: SubjectClipApi;
  /**
   * The run exists. `finished` settles when it ends (resolves when ready; rejects when it failed or was
   * cancelled), which is what the header's activity tray tracks.
   */
  onStarted?: (job: SubjectClipJob, finished: Promise<SubjectClipJob>) => void;
  /** The clip is in the project's media. */
  onReady?: (job: SubjectClipJob) => void;
}

interface Running {
  abort: AbortController;
  api: SubjectClipApi;
  /** Rejects `finished` when the person stops the run. */
  stop: (reason: Error) => void;
}

const jobs = new Map<string, SubjectClipJob>();
const running = new Map<string, Running>();
const listeners = new Set<() => void>();
let version = 0;

function notify(): void {
  version += 1;
  for (const l of [...listeners]) l();
}

function put(job: SubjectClipJob): SubjectClipJob {
  jobs.set(job.executionId, job);
  notify();
  return job;
}

export const subscribeSubjectClips = (listener: () => void): (() => void) => {
  listeners.add(listener);
  return () => listeners.delete(listener);
};
export const subjectClipsVersion = (): number => version;

/** The project's clip jobs of this session: running first, then the newest finished. */
export function subjectClipJobs(projectId: string): SubjectClipJob[] {
  const active = (j: SubjectClipJob): number => (j.state === 'queued' || j.state === 'running' ? 0 : 1);
  return [...jobs.values()].filter((j) => j.projectId === projectId).sort((a, b) => active(a) - active(b) || b.startedAt - a.startedAt);
}

const isAbort = (error: unknown): boolean => (error as { name?: string } | null)?.name === 'AbortError';

/**
 * Starts a clip. A refused start rejects with the server's error and lists nothing (the dialog words the refusal,
 * or asks for the one-time consent). Once the server has the run it is listed here and polled outside any
 * component, so closing the dialog, switching the pool's tab or leaving the page's panel does not lose it.
 */
export async function startSubjectClipJob(projectId: string, request: SubjectClipRequest, options: StartSubjectClipOptions): Promise<SubjectClipJob> {
  const api = options.api ?? defaultSubjectClipApi;
  const started = await api.start(projectId, request);
  const id = started.executionId;
  const job = put({
    executionId: id,
    projectId,
    state: started.state === 'running' ? 'running' : 'queued',
    names: [...options.names],
    outputFileIds: [],
    message: null,
    failureCode: null,
    credits: typeof started.credits === 'number' ? started.credits : null,
    startedAt: Date.now(),
  });
  const abort = new AbortController();
  const finished = new Promise<SubjectClipJob>((resolve, reject) => {
    running.set(id, { abort, api, stop: reject });
    api.wait(projectId, id, abort.signal, (status) => {
      const now = jobs.get(id);
      if (now && !abort.signal.aborted && (status.state === 'queued' || status.state === 'running') && now.state !== status.state) put({ ...now, state: status.state });
    }).then(
      (status) => {
        if (abort.signal.aborted) return;
        running.delete(id);
        const done = put({ ...(jobs.get(id) ?? job), state: 'ready', outputFileIds: [...status.outputFileIds], message: null, failureCode: null });
        api.refreshPool(projectId);
        options.onReady?.(done);
        resolve(done);
      },
      (error: unknown) => {
        if (abort.signal.aborted || isAbort(error)) return;
        running.delete(id);
        const status = error instanceof SubjectClipFailedError ? error.status : null;
        const cancelled = status?.state === 'cancelled';
        put({
          ...(jobs.get(id) ?? job),
          state: cancelled ? 'cancelled' : 'failed',
          message: status?.message?.trim() || null,
          failureCode: status?.failureCode ?? (status ? null : 'unreadable'),
        });
        reject(error instanceof Error ? error : new Error('failed'));
      },
    );
  });
  // Nobody may be listening (no activity tray): a failed run must not surface as an unhandled rejection.
  finished.catch(() => undefined);
  options.onStarted?.(job, finished);
  return job;
}

/**
 * Stops a running clip: it is shown as cancelled at once, and the server is told. When the server answers that the
 * run had already finished, the clip it made is shown instead.
 */
export function cancelSubjectClipJob(executionId: string): Promise<void> {
  const job = jobs.get(executionId);
  const run = running.get(executionId);
  if (!job || !run || (job.state !== 'queued' && job.state !== 'running')) return Promise.resolve();
  run.abort.abort();
  running.delete(executionId);
  put({ ...job, state: 'cancelled' });
  run.stop(new DOMException('cancelled', 'AbortError'));
  return run.api
    .cancel(job.projectId, executionId)
    .then(async (answer) => {
      if (answer !== null || !run.api.getJob) return;
      const status = await run.api.getJob(job.projectId, executionId);
      const now = jobs.get(executionId);
      if (status.state === 'ready' && now?.state === 'cancelled') {
        put({ ...now, state: 'ready', outputFileIds: [...status.outputFileIds] });
        run.api.refreshPool(job.projectId);
      }
    })
    .catch(() => undefined);
}

/** Takes a finished job off the list. A running one stays (stop it first). */
export function dismissSubjectClipJob(executionId: string): void {
  const job = jobs.get(executionId);
  if (!job || job.state === 'queued' || job.state === 'running') return;
  jobs.delete(executionId);
  notify();
}

/** Test hook: drop every job (aborting polls). */
export function resetSubjectClipStore(): void {
  for (const run of running.values()) run.abort.abort();
  running.clear();
  jobs.clear();
  version += 1;
}
