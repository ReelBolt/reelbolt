'use client';

import { useEffect, useRef } from 'react';
import useSWR from 'swr';
import { fetcher } from '../api/client';
import { useWorkflowEvents as useSharedWorkflowEvents } from '../activity/events';
import type { DashboardOverview } from '../types/dashboard';

const KEY = '/api/v1/dashboard/overview';
const DASHBOARD_EVENT_TYPES = ['execution.running', 'execution.completed', 'execution.failed', 'step.started', 'step.completed', 'step.progress'];

/**
 * Owner-scoped aggregates for the home page and the projects list. Polls only while something is
 * active; a workflow event on the SSE stream (see `useWorkflowEvents`) revalidates it immediately.
 */
export function useDashboardOverview() {
  return useSWR<DashboardOverview>(KEY, fetcher, {
    refreshInterval: (data) => (data && data.activeExecutions.length > 0 ? 8000 : 30000),
    revalidateOnFocus: true,
  });
}

export interface LiveExecutionState {
  executionId: string;
  stepLabel: string | null;
  percent: number | null;
  lastEventAt: number;
}

/**
 * Subscribes to the existing workflow SSE feed (`/api/v1/workflows/events`) and reports the latest
 * step/progress per execution. Terminal events call `onTerminal` so the caller can revalidate.
 * Unlike `useWorkflowMonitor` this needs no admin endpoints, so it is safe for every user.
 */
export function useWorkflowEvents(onChange: (live: Record<string, LiveExecutionState>) => void, onTerminal: () => void) {
  // Rides the ONE shared global stream (lib/activity/events.ts), which re-opens when the active
  // workspace changes (A15-UI); /dashboard is exactly where a switch lands.
  const liveRef = useRef<Record<string, LiveExecutionState>>({});
  const changeRef = useRef(onChange);
  const terminalRef = useRef(onTerminal);
  useEffect(() => {
    changeRef.current = onChange;
    terminalRef.current = onTerminal;
  });

  useSharedWorkflowEvents(DASHBOARD_EVENT_TYPES, (type, msg) => {
    const id = msg.executionId;
    if (!id) return;
    if (type === 'execution.completed' || type === 'execution.failed') {
      delete liveRef.current[id];
      changeRef.current({ ...liveRef.current });
      terminalRef.current();
      return;
    }
    const d = msg.data;
    const prev = liveRef.current[id];
    const step = typeof d.stepLabel === 'string' && d.stepLabel ? d.stepLabel : null;
    const stage = typeof d.stage === 'string' && d.stage ? d.stage : null;
    const label = step && stage ? `${step}: ${stage}` : (step ?? stage ?? prev?.stepLabel ?? null);
    const pct = typeof d.percentComplete === 'number' ? Math.max(0, Math.min(100, d.percentComplete)) : (prev?.percent ?? null);
    liveRef.current[id] = { executionId: id, stepLabel: label, percent: pct, lastEventAt: Date.now() };
    changeRef.current({ ...liveRef.current });
    if (type === 'execution.running' || type === 'step.completed') terminalRef.current();
  });
}
