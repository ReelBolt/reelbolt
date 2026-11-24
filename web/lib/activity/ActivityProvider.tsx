'use client';

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import useSWR from 'swr';
import { apiFetch } from '@/lib/api/client';
import { useAuth } from '@/lib/hooks/use-auth';
import { userScopeFromEmail } from '@/lib/editing/sync/user-scope';
import './builtins';
import { loadDismissed, loadSeenAt, saveDismissed, saveSeenAt } from './dismissals';
import { useWorkflowEvents } from './events';
import { FinishNotifier, isActiveState, isFinishedState } from './notifier';
import { getJobType } from './registry';
import type { ActivityItem, ActivityJob, ActivityJobState, ActivityResponse, ActivityType, JobReview } from './types';

export const ACTIVITY_URL = '/api/v1/activity?limit=50';
const FAST_REFRESH_MS = 4_000;
const SLOW_REFRESH_MS = 30_000;
const MUTATE_DEBOUNCE_MS = 500;
/** Finished client jobs stay listed this long (they exist in memory only). */
export const CLIENT_JOB_KEEP_MS = 10 * 60_000;

const EMPTY: ActivityResponse = { items: [], serverTime: '', activeCount: 0 };

/** The list is a convenience: a 404 (older server), a network error or a bad body is an empty list. */
async function fetchActivity(): Promise<ActivityResponse> {
  try {
    const res = await apiFetch<ActivityResponse>(ACTIVITY_URL);
    return res && Array.isArray(res.items) ? res : EMPTY;
  } catch {
    return EMPTY;
  }
}

/** Describes work the browser itself is doing (no server row): uploads, proposals, picture operations. */
export interface TrackJobMeta {
  type: ActivityType;
  title?: string | null;
  projectId?: string;
  href?: string | null;
  creditsEstimate?: number | null;
  /**
   * The server run this work mirrors (e.g. the editor's magic blur). The provider lists a run once: the server
   * item wins while it runs, the client job wins while it needs the user's OK.
   */
  executionId?: string | null;
  /**
   * A stable id (e.g. derived from an editor result's id). Tracking the same id again re-attaches to the existing
   * job instead of listing a second row.
   */
  id?: string;
  /** The title may hold the user's own text: show it in the tray row only, never in a toast or OS notification. */
  quietTitle?: boolean;
  /** Stops the work; the signal handed to `work` is aborted first. */
  cancel?: () => Promise<void>;
  /** Start in the cost-estimate phase; move on with `updateTrackedJob(id, { state: 'running' })`. */
  estimating?: boolean;
}

export interface TrackedJobPatch {
  state?: ActivityJobState;
  progress?: number | null;
  liveStage?: string | null;
  title?: string | null;
  href?: string | null;
}

export type TrackWork<T> = Promise<T> | ((signal: AbortSignal, jobId: string) => Promise<T>);

export interface ActivityContextValue {
  /** Visible jobs: running, estimating, waiting, then finished newest first (dismissed hidden). */
  jobs: ActivityJob[];
  /** Every job including dismissed ones, for in-place status selectors. */
  allJobs: ActivityJob[];
  activeCount: number;
  failedUnseenCount: number;
  isLoading: boolean;
  dismiss: (id: string) => void;
  clearFinished: () => void;
  /** Marks today's failures as seen (the tray calls it when opened). */
  markFailuresSeen: () => void;
  /**
   * Tracks client-side work. Returns the SAME promise (or the one `work` returns), so callers keep
   * their own result and error handling. Client jobs live in memory only and cannot outlive the page.
   */
  trackJob: <T>(meta: TrackJobMeta, work: TrackWork<T>) => Promise<T>;
  updateTrackedJob: (id: string, patch: TrackedJobPatch) => void;
  /** Moves a client job to `needsReview`: it stays until `resolveTrackedJob`, never expires, cannot be dismissed. */
  markNeedsReview: (id: string, review: JobReview) => void;
  /** Resolves a needsReview job: runs `onApply`/`onDiscard` (a throw leaves it in needsReview), then marks it done / cancelled. */
  resolveTrackedJob: (id: string, outcome: 'applied' | 'discarded') => Promise<void>;
  cancel: (job: ActivityJob) => Promise<void>;
}

const noopAsync = async (): Promise<void> => undefined;

const DEFAULT_VALUE: ActivityContextValue = {
  jobs: [],
  allJobs: [],
  activeCount: 0,
  failedUnseenCount: 0,
  isLoading: false,
  dismiss: () => undefined,
  clearFinished: () => undefined,
  markFailuresSeen: () => undefined,
  trackJob: (_meta, work) => (typeof work === 'function' ? work(new AbortController().signal, 'client:none') : work),
  updateTrackedJob: () => undefined,
  markNeedsReview: () => undefined,
  resolveTrackedJob: noopAsync,
  cancel: noopAsync,
};

const ActivityContext = createContext<ActivityContextValue>(DEFAULT_VALUE);

const ORDER: Record<string, number> = { running: 0, needsReview: 1, estimating: 2, waiting: 3 };
const endedMs = (j: ActivityJob): number => Date.parse(j.endedAt ?? j.createdAt) || 0;

/** running, estimating, waiting, then finished newest first. */
export function sortJobs(jobs: ActivityJob[]): ActivityJob[] {
  return [...jobs].sort((a, b) => {
    const ra = ORDER[a.state] ?? 4;
    const rb = ORDER[b.state] ?? 4;
    if (ra !== rb) return ra - rb;
    return ra < 4 ? (Date.parse(a.createdAt) || 0) - (Date.parse(b.createdAt) || 0) : endedMs(b) - endedMs(a);
  });
}

interface Live {
  progress: number | null;
  stage: string | null;
}

const IMPLIED_STATE: Record<string, string> = {
  'execution.running': 'running',
  'execution.completed': 'done',
  'execution.failed': 'failed',
};

const STREAM_TYPES = ['step.progress', 'execution.running', 'execution.completed', 'execution.failed', 'media.derivative.progress'];

export function ActivityProvider({ children }: { children: ReactNode }) {
  const { activeOrgId, user } = useAuth();
  const orgRef = useRef(activeOrgId);
  useEffect(() => {
    orgRef.current = activeOrgId;
  }, [activeOrgId]);
  // Dismissals and the seen mark are per signed-in user AND workspace (a shared browser never leaks them).
  const storeScope = activeOrgId ? `${userScopeFromEmail(user?.email)}.${activeOrgId}` : null;
  const swrKey = activeOrgId ? ['activity', activeOrgId] : null;

  const { data, isLoading, mutate } = useSWR<ActivityResponse>(swrKey, fetchActivity, {
    refreshInterval: (latest) =>
      latest && latest.items.some((i) => i.state === 'waiting' || i.state === 'running') ? FAST_REFRESH_MS : SLOW_REFRESH_MS,
    refreshWhenHidden: false,
    revalidateOnFocus: true,
  });

  // Live progress per execution, from the shared stream.
  const [live, setLive] = useState<Record<string, Live>>({});
  const itemsRef = useRef<ActivityItem[]>([]);
  useEffect(() => {
    itemsRef.current = data?.items ?? [];
  }, [data]);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const debouncedMutate = useCallback(() => {
    if (timer.current) clearTimeout(timer.current);
    const scheduledFor = orgRef.current;
    timer.current = setTimeout(() => {
      timer.current = null;
      // The workspace changed since this was scheduled: a refresh now would cache the new workspace's items under the old key.
      if (orgRef.current !== scheduledFor) return;
      void mutate();
    }, MUTATE_DEBOUNCE_MS);
  }, [mutate]);
  useEffect(
    () => () => {
      if (timer.current) clearTimeout(timer.current);
    },
    [activeOrgId],
  );

  useWorkflowEvents(STREAM_TYPES, (type, frame) => {
    if (type === 'media.derivative.progress') {
      debouncedMutate();
      return;
    }
    const id = frame.executionId;
    if (!id) return;
    if (type === 'step.progress') {
      const pct = typeof frame.data.percentComplete === 'number' ? Math.max(0, Math.min(100, frame.data.percentComplete)) / 100 : null;
      const stage =
        typeof frame.data.stage === 'string' && frame.data.stage
          ? frame.data.stage
          : typeof frame.data.stepLabel === 'string' && frame.data.stepLabel
            ? frame.data.stepLabel
            : null;
      setLive((cur) => ({ ...cur, [id]: { progress: pct ?? cur[id]?.progress ?? null, stage: stage ?? cur[id]?.stage ?? null } }));
      return;
    }
    const implied = IMPLIED_STATE[type];
    if (!implied) return;
    if (implied !== 'running') {
      setLive((cur) => {
        if (!(id in cur)) return cur;
        const next = { ...cur };
        delete next[id];
        return next;
      });
    }
    const item = itemsRef.current.find((i) => i.executionId === id);
    const settled = item && (item.state === implied || (implied === 'failed' && item.state === 'cancelled'));
    if (!settled) debouncedMutate();
  });

  // Client-tracked jobs (memory only).
  const [clientJobs, setClientJobs] = useState<ActivityJob[]>([]);
  const clientJobsRef = useRef<ActivityJob[]>([]);
  useEffect(() => {
    clientJobsRef.current = clientJobs;
  }, [clientJobs]);
  const counter = useRef(0);
  const controllers = useRef(new Map<string, AbortController>());
  const keepTimers = useRef(new Set<ReturnType<typeof setTimeout>>());
  useEffect(() => {
    const timers = keepTimers.current;
    return () => timers.forEach(clearTimeout);
  }, []);

  const updateTrackedJob = useCallback((id: string, patch: TrackedJobPatch) => {
    setClientJobs((cur) =>
      cur.map((j) => {
        if (j.id !== id) return j;
        const next: ActivityJob = { ...j };
        if (patch.state !== undefined) next.state = patch.state;
        if (patch.progress !== undefined) next.progress = patch.progress;
        if (patch.liveStage !== undefined) next.liveStage = patch.liveStage;
        if (patch.title !== undefined) next.title = patch.title;
        if (patch.href !== undefined) next.href = patch.href;
        return next;
      }),
    );
  }, []);

  /** Finished client jobs expire after 10 min; a needsReview job never does. */
  const scheduleKeep = useCallback((id: string) => {
    const t = setTimeout(() => {
      keepTimers.current.delete(t);
      setClientJobs((cur) => cur.filter((j) => j.id !== id || j.state === 'needsReview'));
    }, CLIENT_JOB_KEEP_MS);
    keepTimers.current.add(t);
  }, []);

  const markNeedsReview = useCallback((id: string, review: JobReview) => {
    setClientJobs((cur) =>
      cur.map((j) => (j.id === id ? { ...j, state: 'needsReview', review, cancellable: false, endedAt: new Date().toISOString() } : j)),
    );
  }, []);

  const resolveTrackedJob = useCallback(
    async (id: string, outcome: 'applied' | 'discarded') => {
      const job = clientJobsRef.current.find((j) => j.id === id);
      if (!job || job.state !== 'needsReview') return;
      try {
        await (outcome === 'applied' ? job.review?.onApply?.() : job.review?.onDiscard?.());
      } catch {
        // The callback could not do it (e.g. the editor is read-only): the job keeps waiting for the user's OK.
        return;
      }
      setClientJobs((cur) =>
        cur.map((j) =>
          j.id === id ? { ...j, state: outcome === 'applied' ? 'done' : 'cancelled', review: undefined, endedAt: new Date().toISOString() } : j,
        ),
      );
      scheduleKeep(id);
    },
    [scheduleKeep],
  );

  const trackJob = useCallback(
    <T,>(meta: TrackJobMeta, work: TrackWork<T>): Promise<T> => {
      counter.current += 1;
      const id = meta.id ?? `client:${counter.current}`;
      const controller = new AbortController();
      controllers.current.set(id, controller);
      const nowIso = new Date().toISOString();
      const job: ActivityJob = {
        id,
        type: meta.type,
        state: meta.estimating ? 'estimating' : 'running',
        projectId: meta.projectId ?? '',
        projectName: '',
        title: meta.title ?? null,
        executionId: meta.executionId ?? null,
        workflowId: null,
        subject: {},
        step: null,
        progress: null,
        credits: { heldCredits: meta.creditsEstimate ?? null, chargedCredits: null },
        failure: null,
        startedByYou: true,
        cancellable: !!meta.cancel,
        createdAt: nowIso,
        startedAt: nowIso,
        endedAt: null,
        origin: 'client',
        orgId: orgRef.current,
        liveProgress: null,
        liveStage: null,
        href: meta.href,
        cancelClient: meta.cancel,
        quietTitle: meta.quietTitle,
      };
      // Same stable id again: keep the row (and a pending needsReview), take the new title / cancel / href.
      setClientJobs((cur) =>
        cur.some((j) => j.id === id)
          ? cur.map((j) => (j.id === id ? { ...j, title: job.title, href: job.href, cancelClient: job.cancelClient, cancellable: j.state === 'needsReview' ? false : job.cancellable, quietTitle: job.quietTitle, state: j.state === 'needsReview' ? j.state : job.state, endedAt: j.state === 'needsReview' ? j.endedAt : null } : j))
          : [...cur, job],
      );

      const promise = typeof work === 'function' ? new Promise<T>((resolve) => resolve(work(controller.signal, id))) : work;
      const finish = (state: 'done' | 'failed' | 'cancelled') => {
        controllers.current.delete(id);
        setClientJobs((cur) =>
          cur.map((j) => {
            if (j.id === id && j.state === 'needsReview') return j;
            return j.id === id
              ? {
                  ...j,
                  state,
                  endedAt: new Date().toISOString(),
                  cancellable: false,
                  progress: state === 'done' ? 1 : j.progress,
                  failure: state === 'failed' ? { code: null, refunded: null } : null,
                }
              : j;
          }),
        );
        scheduleKeep(id);
      };
      promise.then(
        () => finish('done'),
        () => finish(controller.signal.aborted ? 'cancelled' : 'failed'),
      );
      return promise;
    },
    [scheduleKeep],
  );

  // Dismissals and the "seen failures" mark, per workspace.
  const [stored, setStored] = useState(() => ({ org: storeScope, dismissed: loadDismissed(storeScope), seenAt: loadSeenAt(storeScope) }));
  let current = stored;
  if (stored.org !== storeScope) {
    current = { org: storeScope, dismissed: loadDismissed(storeScope), seenAt: loadSeenAt(storeScope) };
    setStored(current);
  }
  const dismissedSet = useMemo(() => new Set(current.dismissed), [current.dismissed]);

  const allJobs = useMemo<ActivityJob[]>(() => {
    const server: ActivityJob[] = (data?.items ?? []).map((item) => {
      const l = item.executionId && item.state === 'running' ? live[item.executionId] : undefined;
      return {
        ...item,
        origin: 'server',
        liveProgress: l?.progress ?? item.progress,
        liveStage: l?.stage ?? null,
      };
    });
    // Client jobs belong to the workspace they were started in; another workspace never lists them.
    // One run, one row: a client job that mirrors a server run (same executionId) yields to the server item
    // (fresher progress) unless it needs the user's OK, in which case the client job wins.
    const reviewing = new Set(clientJobs.filter((j) => j.executionId && j.state === 'needsReview').map((j) => j.executionId));
    const serverIds = new Set(server.map((j) => j.executionId).filter((id): id is string => !!id && !reviewing.has(id)));
    return sortJobs([
      ...server.filter((j) => !(j.executionId && reviewing.has(j.executionId))),
      ...clientJobs.filter((j) => j.orgId === activeOrgId).filter((j) => !(j.executionId && j.state !== 'needsReview' && serverIds.has(j.executionId))),
    ]);
  }, [data, live, clientJobs, activeOrgId]);

  const jobs = useMemo(() => allJobs.filter((j) => !(isFinishedState(j.state) && dismissedSet.has(j.id))), [allJobs, dismissedSet]);
  const activeCount = useMemo(() => jobs.filter((j) => isActiveState(j.state)).length, [jobs]);
  const failedUnseenCount = useMemo(
    () => jobs.filter((j) => j.state === 'failed' && j.startedByYou && endedMs(j) > current.seenAt).length,
    [jobs, current.seenAt],
  );

  const persist = useCallback(
    (ids: string[]) => {
      const capped = ids.slice(-200);
      saveDismissed(storeScope, capped);
      setStored((s) => ({ ...s, org: storeScope, dismissed: capped }));
    },
    [storeScope],
  );
  const dismiss = useCallback(
    (id: string) => {
      const job = allJobs.find((j) => j.id === id);
      if (!job || !isFinishedState(job.state) || dismissedSet.has(id)) return;
      persist([...current.dismissed, id]);
    },
    [allJobs, dismissedSet, current.dismissed, persist],
  );
  const clearFinished = useCallback(() => {
    const ids = allJobs.filter((j) => isFinishedState(j.state) && !dismissedSet.has(j.id)).map((j) => j.id);
    if (ids.length) persist([...current.dismissed, ...ids]);
  }, [allJobs, dismissedSet, current.dismissed, persist]);
  const markFailuresSeen = useCallback(() => {
    const at = Date.now();
    saveSeenAt(storeScope, at);
    setStored((s) => ({ ...s, seenAt: at }));
  }, [storeScope]);

  const cancel = useCallback(
    async (job: ActivityJob) => {
      if (job.origin === 'client') {
        controllers.current.get(job.id)?.abort();
        await job.cancelClient?.();
        return;
      }
      await getJobType(job.type).cancel?.(job);
      await mutate();
    },
    [mutate],
  );

  const value = useMemo<ActivityContextValue>(
    () => ({
      jobs,
      allJobs,
      activeCount,
      failedUnseenCount,
      isLoading,
      dismiss,
      clearFinished,
      markFailuresSeen,
      trackJob,
      updateTrackedJob,
      markNeedsReview,
      resolveTrackedJob,
      cancel,
    }),
    [jobs, allJobs, activeCount, failedUnseenCount, isLoading, dismiss, clearFinished, markFailuresSeen, trackJob, updateTrackedJob, markNeedsReview, resolveTrackedJob, cancel],
  );

  return (
    <ActivityContext.Provider value={value}>
      <FinishNotifier key={activeOrgId ?? 'none'} jobs={allJobs} loaded={!!data} />
      {children}
    </ActivityContext.Provider>
  );
}

/** Everything the tray and the status chips need. Safe outside a provider (empty, no-op). */
export function useActivity(): ActivityContextValue {
  return useContext(ActivityContext);
}

/** First job (including dismissed) matching `predicate`, e.g. `j => j.executionId === id`. */
export function useJobFor(predicate: (job: ActivityJob) => boolean): ActivityJob | undefined {
  return useContext(ActivityContext).allJobs.find(predicate);
}

/** Every job matching `predicate`, e.g. `j => j.subject?.timelineId === t`. */
export function useJobsWhere(predicate: (job: ActivityJob) => boolean): ActivityJob[] {
  const { allJobs } = useContext(ActivityContext);
  return allJobs.filter(predicate);
}

/** Convenience for feature code: `const { track, update } = useTrackedJob()`. */
export function useTrackedJob(): { track: ActivityContextValue['trackJob']; update: ActivityContextValue['updateTrackedJob'] } {
  const { trackJob, updateTrackedJob } = useContext(ActivityContext);
  return { track: trackJob, update: updateTrackedJob };
}
