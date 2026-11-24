'use client';

import { useCallback, useEffect, useLayoutEffect, useState } from 'react';

/**
 * Which failed-run warnings the viewer has closed, remembered per browser.
 *
 * Both "a run failed" surfaces — the dashboard's "N runs need your attention" panel and the project
 * page's "Last run failed" panel — list executions, and the same execution can appear on both. They
 * share this store, keyed by execution id, so closing a warning in one place closes it in the other,
 * and a NEW failure still surfaces: only the ids the viewer explicitly dismissed stay hidden.
 *
 * `localStorage` is wrapped: a private window or blocked storage degrades to "nothing dismissed"
 * rather than breaking the page. The stored list is capped at {@link MAX_DISMISSED_IDS} so the key
 * cannot grow forever; the oldest dismissals fall off first.
 */

/** `localStorage` key; exported so a test can check the real persisted shape. */
export const DISMISSED_RUN_FAILURES_KEY = 'reelbolt.dismissed-run-failures';
const MAX_DISMISSED_IDS = 200;

const EMPTY: ReadonlySet<string> = new Set();

// One module-level copy so a remount (client-side navigation between the dashboard and a project)
// shows the dismissed panel hidden on its first paint instead of flashing it and then hiding it.
let cached: Set<string> | null = null;
const listeners = new Set<() => void>();

// `useLayoutEffect` on the client so the stored ids are applied before the browser paints;
// `useEffect` on the server, where layout effects do not run (and would warn).
const useIsomorphicLayoutEffect = typeof window === 'undefined' ? useEffect : useLayoutEffect;

function load(): Set<string> {
  try {
    const raw = window.localStorage.getItem(DISMISSED_RUN_FAILURES_KEY);
    if (!raw) return new Set();
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return new Set();
    return new Set(parsed.filter((id): id is string => typeof id === 'string' && id.length > 0));
  } catch {
    return new Set();
  }
}

function current(): Set<string> {
  if (!cached) cached = load();
  return cached;
}

function persist(next: Set<string>) {
  const trimmed = [...next].slice(-MAX_DISMISSED_IDS);
  cached = new Set(trimmed);
  try {
    window.localStorage.setItem(DISMISSED_RUN_FAILURES_KEY, JSON.stringify(trimmed));
  } catch {
    // Storage full or blocked: the dismissal still applies for this page's lifetime.
  }
  for (const listener of [...listeners]) listener();
}

/** Hides every failed run in `ids` from both warning surfaces until the viewer clears storage. */
export function dismissFailedRuns(ids: readonly string[]) {
  const next = new Set(current());
  const sizeBefore = next.size;
  for (const id of ids) if (id) next.add(id);
  if (next.size === sizeBefore) return;
  persist(next);
}

/**
 * Reads the dismissed execution ids and returns a `dismiss(ids)` callback. The first paint after a
 * client-side navigation already sees the stored ids; the first paint after a full page load is the
 * server-rendered "nothing dismissed" one, corrected before the browser paints.
 */
export function useDismissedRunFailures() {
  const [dismissed, setDismissed] = useState<ReadonlySet<string>>(EMPTY);

  useIsomorphicLayoutEffect(() => {
    setDismissed(current());
    const listener = () => setDismissed(current());
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
    };
  }, []);

  const dismiss = useCallback((ids: readonly string[]) => dismissFailedRuns(ids), []);

  return { dismissed, dismiss };
}

/** Test-only: forget the module-level copy (localStorage remains the real store). */
export function __resetDismissedRunFailuresForTests() {
  cached = null;
  listeners.clear();
}
