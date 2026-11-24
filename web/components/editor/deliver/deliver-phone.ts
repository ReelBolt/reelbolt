'use client';

import { useEffect, useRef, useState } from 'react';
import type { EditTimelineRenderSummary } from '@/lib/editing/sync/api';
import type { TimelineRenderPreset } from '@/lib/types/workflow';

/** The phone's format order: the vertical film first, then the other shapes, then the rest. */
const PHONE_ORDER: readonly TimelineRenderPreset[] = ['Vertical9x16', 'Square1x1', 'Portrait4x5', 'Hd1080p16x9', 'Uhd4k16x9', 'Master', 'AudioOnly'];

export function phoneFormatOrder<T extends { value: TimelineRenderPreset }>(options: readonly T[]): T[] {
  const rank = (p: TimelineRenderPreset): number => {
    const i = PHONE_ORDER.indexOf(p);
    return i < 0 ? PHONE_ORDER.length : i;
  };
  return [...options].sort((a, b) => rank(a.value) - rank(b.value));
}

/** What the Share button can do on this device: share the file itself, share its link, or nothing (then no button). */
export type ShareMode = 'files' | 'link' | 'none';

export interface ShareNavigator {
  share?: (data: ShareData) => Promise<void>;
  canShare?: (data?: ShareData) => boolean;
}

export function shareModeOf(nav: ShareNavigator | undefined, probe: File | null, hasLink: boolean): ShareMode {
  if (!nav || typeof nav.share !== 'function') return 'none';
  if (probe && typeof nav.canShare === 'function') {
    try {
      if (nav.canShare({ files: [probe] })) return 'files';
    } catch {
      /* fall through to the link */
    }
  }
  return hasLink ? 'link' : 'none';
}

/** The finished renders in `next` that were not finished in `known` (the ids already seen). */
export function newlyFinished(known: ReadonlySet<string>, next: readonly EditTimelineRenderSummary[]): EditTimelineRenderSummary[] {
  return next.filter((r) => r.status === 'Passed' && r.hasOutput && !known.has(r.executionId));
}

/**
 * Phone tabs are suspended in the background, so a render that finished while away is not seen until the tab
 * returns. Calls `onForeground` each time the page becomes visible again.
 */
export function useForegroundRefresh(onForeground: () => void): void {
  const ref = useRef(onForeground);
  useEffect(() => {
    ref.current = onForeground;
  }, [onForeground]);
  useEffect(() => {
    const onChange = () => {
      if (document.visibilityState === 'visible') ref.current();
    };
    document.addEventListener('visibilitychange', onChange);
    return () => document.removeEventListener('visibilitychange', onChange);
  }, []);
}

/** Bumps each time the page returns to the foreground: a key that makes the event stream reconnect. */
export function useForegroundEpoch(): number {
  const [epoch, setEpoch] = useState(0);
  useForegroundRefresh(() => setEpoch((n) => n + 1));
  return epoch;
}

/**
 * A completion signal: calls `onFinished` once for every render that finished with a film after the first list was
 * seen. The first list only records the baseline, so opening the editor never announces old films.
 */
export function useRenderCompletion(renders: readonly EditTimelineRenderSummary[] | undefined, onFinished: (finished: EditTimelineRenderSummary[]) => void): void {
  const seen = useRef<Set<string> | null>(null);
  const cb = useRef(onFinished);
  useEffect(() => {
    cb.current = onFinished;
  }, [onFinished]);
  useEffect(() => {
    if (!renders) return;
    if (seen.current === null) {
      seen.current = new Set(renders.filter((r) => r.status === 'Passed').map((r) => r.executionId));
      return;
    }
    const done = newlyFinished(seen.current, renders);
    if (done.length === 0) return;
    done.forEach((r) => seen.current?.add(r.executionId));
    cb.current(done);
  }, [renders]);
}
