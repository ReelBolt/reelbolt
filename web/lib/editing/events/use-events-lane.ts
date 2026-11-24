'use client';

/**
 * The events lane's data hook. For the files the timeline's video clips play it reads each file's `events`
 * analysis status, fetches the data of the ones that are ready, and maps them onto record frames. Nothing is
 * requested without a click: when a file has no events yet, `start()` runs the free analysis for the files that
 * lack it, and `estimate` carries the up-front price (a free kind reads "free").
 */

import { useCallback, useMemo, useState } from 'react';
import useSWR from 'swr';
import {
  describeAnalysisError,
  estimateMediaAnalysis,
  fetchAnalysisData,
  fetchMediaAnalysisStatus,
  fileMediaRef,
  startMediaAnalysis,
  type AnalysisState,
} from '../api/media-analysis';
import type { EditTimeline } from '../types';
import { laneEvents, laneFileIds, needsEvents, readEventsResult, type LaneEvent, type SourceEvents } from './events-data';

export interface FileEventsState {
  fileId: string;
  state: AnalysisState;
  reason: string | null;
  events: SourceEvents | null;
  /** The stored result is from an earlier detector, or incomplete: the lane shows it, and `start()` finds the events again. */
  outdated?: boolean;
}

async function loadFile(projectId: string, fileId: string): Promise<FileEventsState> {
  let state: AnalysisState = 'none';
  let reason: string | null = null;
  let outdated = false;
  try {
    const status = await fetchMediaAnalysisStatus(projectId, fileMediaRef(fileId));
    const kind = status.kinds.events;
    state = kind?.state ?? 'none';
    reason = kind?.reason ?? null;
    outdated = kind?.outdated === true;
  } catch {
    return { fileId, state: 'none', reason: null, events: null };
  }
  if (state !== 'ready') return { fileId, state, reason, events: null };
  try {
    const events = readEventsResult(await fetchAnalysisData<unknown>(projectId, fileMediaRef(fileId), 'events'));
    return outdated ? { fileId, state, reason, events, outdated } : { fileId, state, reason, events };
  } catch {
    return { fileId, state: 'failed', reason: null, events: null };
  }
}

const isWorkingState = (s: AnalysisState): boolean => s === 'queued' || s === 'running';

/** What running the analysis would cost: free, or this many credits. */
export interface EventsEstimate {
  free: boolean;
  credits: number;
}

export interface UseEventsLane {
  /** The events on record frames, ascending. Empty until a file is ready. */
  events: readonly LaneEvent[];
  /** Files the lane reads and their states. */
  files: readonly FileEventsState[];
  /** Files with no events yet (not run, or failed), or with events from an earlier detector, that `start()` would analyse. */
  missing: readonly string[];
  /** Some file is queued or running. */
  working: boolean;
  /** At least one file is ready and read. */
  ready: boolean;
  estimate: EventsEstimate | null;
  start: () => Promise<void>;
  starting: boolean;
  /** A plain sentence about a failed start, or null. */
  error: string | null;
}

/** Reads the lane's data. `enabled` false (lane hidden, or no project) makes no request at all. */
export function useEventsLane(projectId: string, doc: EditTimeline, enabled: boolean): UseEventsLane {
  const ids = useMemo(() => laneFileIds(doc), [doc]);
  const idKey = ids.join(',');
  const active = enabled && projectId.length > 0 && ids.length > 0;

  const { data: files, mutate } = useSWR<FileEventsState[]>(
    active ? ['file-events', projectId, idKey] : null,
    ([, p, joined]: readonly [string, string, string]) => Promise.all(joined.split(',').map((id) => loadFile(p, id))),
    { revalidateOnFocus: false, refreshInterval: (latest) => (latest?.some((f) => isWorkingState(f.state)) ? 2000 : 0) },
  );

  const list = useMemo(() => files ?? [], [files]);
  const missing = useMemo(() => list.filter(needsEvents).map((f) => f.fileId), [list]);
  const missingKey = missing.join(',');

  // The price is read once per set of missing files, and only while the lane is shown.
  const { data: estimate } = useSWR<EventsEstimate>(
    active && missing.length > 0 ? ['file-events-estimate', projectId, missingKey] : null,
    async ([, p, joined]: readonly [string, string, string]) => {
      const all = await Promise.all(joined.split(',').map((id) => estimateMediaAnalysis(p, fileMediaRef(id), ['events'])));
      const credits = all.reduce((sum, e) => sum + (e.credits ?? 0), 0);
      return { free: credits === 0, credits };
    },
    { revalidateOnFocus: false },
  );

  const bySource = useMemo(() => {
    const map = new Map<string, SourceEvents>();
    for (const f of list) if (f.events) map.set(f.fileId, f.events);
    return map;
  }, [list]);
  const events = useMemo(() => laneEvents(doc, bySource), [doc, bySource]);

  const [starting, setStarting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const start = useCallback(async () => {
    if (!projectId || missing.length === 0) return;
    setStarting(true);
    setError(null);
    try {
      await Promise.all(missing.map((id) => startMediaAnalysis(projectId, fileMediaRef(id), ['events'])));
      await mutate();
    } catch (e) {
      setError(describeAnalysisError(e));
    } finally {
      setStarting(false);
    }
  }, [projectId, missing, mutate]);

  return {
    events,
    files: list,
    missing,
    working: list.some((f) => isWorkingState(f.state)),
    ready: list.some((f) => f.events !== null),
    estimate: estimate ?? null,
    start,
    starting,
    error,
  };
}
