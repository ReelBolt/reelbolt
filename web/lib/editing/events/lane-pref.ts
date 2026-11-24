'use client';

/**
 * The events lane's view settings: shown or hidden, and whether the playhead and clip edges snap to its ticks.
 * Per device and per timeline in localStorage (like the viewing LUT): a way of LOOKING at the timeline, so it is
 * not in the document and not in the editor-preferences API. The lane starts hidden; snapping starts on.
 */

import { useCallback, useMemo, useSyncExternalStore } from 'react';

export interface EventsLanePref {
  /** The lane is shown under the ruler. */
  lane: boolean;
  /** Ticks are snap targets while the lane is shown (the editor's own snapping switch still decides). */
  snap: boolean;
}

export const DEFAULT_EVENTS_LANE_PREF: EventsLanePref = { lane: false, snap: true };
export const EVENTS_LANE_KEY_PREFIX = 'reelbolt:editor:events-lane:';
export const eventsLaneKey = (timelineId: string): string => `${EVENTS_LANE_KEY_PREFIX}${timelineId}`;

type StorageLike = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;

function defaultStorage(): StorageLike | null {
  try {
    return typeof window !== 'undefined' ? window.localStorage : null;
  } catch {
    return null;
  }
}

/** Anything unreadable falls back to the default per key. */
export function parseEventsLanePref(raw: string | null): EventsLanePref {
  if (!raw) return DEFAULT_EVENTS_LANE_PREF;
  try {
    const v = JSON.parse(raw) as Partial<Record<keyof EventsLanePref, unknown>>;
    return { lane: typeof v.lane === 'boolean' ? v.lane : DEFAULT_EVENTS_LANE_PREF.lane, snap: typeof v.snap === 'boolean' ? v.snap : DEFAULT_EVENTS_LANE_PREF.snap };
  } catch {
    return DEFAULT_EVENTS_LANE_PREF;
  }
}

export function readEventsLanePref(timelineId: string, storage: StorageLike | null = defaultStorage()): EventsLanePref {
  if (!storage) return DEFAULT_EVENTS_LANE_PREF;
  try {
    return parseEventsLanePref(storage.getItem(eventsLaneKey(timelineId)));
  } catch {
    return DEFAULT_EVENTS_LANE_PREF;
  }
}

const listeners = new Set<() => void>();

export function writeEventsLanePref(timelineId: string, pref: EventsLanePref, storage: StorageLike | null = defaultStorage()): void {
  if (storage) {
    try {
      storage.setItem(eventsLaneKey(timelineId), JSON.stringify(pref));
    } catch {
      // Storage full or blocked: the setting just does not persist.
    }
  }
  listeners.forEach((fn) => fn());
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  const onStorage = (event: StorageEvent) => {
    if (event.key === null || event.key.startsWith(EVENTS_LANE_KEY_PREFIX)) listener();
  };
  if (typeof window !== 'undefined') window.addEventListener('storage', onStorage);
  return () => {
    listeners.delete(listener);
    if (typeof window !== 'undefined') window.removeEventListener('storage', onStorage);
  };
}

/** The timeline's lane setting and its setters. */
export function useEventsLanePref(timelineId: string): { pref: EventsLanePref; setLane: (on: boolean) => void; setSnap: (on: boolean) => void } {
  // The snapshot is the raw string so useSyncExternalStore compares by value.
  const raw = useSyncExternalStore(
    subscribe,
    () => {
      try {
        return defaultStorage()?.getItem(eventsLaneKey(timelineId)) ?? '';
      } catch {
        return '';
      }
    },
    () => '',
  );
  const pref = useMemo(() => parseEventsLanePref(raw === '' ? null : raw), [raw]);
  const setLane = useCallback((lane: boolean) => writeEventsLanePref(timelineId, { ...readEventsLanePref(timelineId), lane }), [timelineId]);
  const setSnap = useCallback((snap: boolean) => writeEventsLanePref(timelineId, { ...readEventsLanePref(timelineId), snap }), [timelineId]);
  return { pref, setLane, setSnap };
}
