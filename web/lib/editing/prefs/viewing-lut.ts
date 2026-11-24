'use client';

/**
 * The viewing-LUT setting: per device and per timeline, in localStorage. It is a way of LOOKING at the picture, so it is
 * deliberately NOT in the editor-preferences API and NOT in the document - nothing that leaves this browser (a render,
 * a still, a review link, a hand-off) can carry it. Phone surface: the viewing LUT is ignored (off).
 */

import { useCallback, useMemo, useSyncExternalStore } from 'react';
import { isCameraProfileId } from '../color/camera-profiles';
import { useDeviceClass } from '../use-device-class';

export type ViewingLutChoice = { kind: 'file'; fileId: string } | { kind: 'profile'; id: string } | null;

export const VIEWING_LUT_KEY_PREFIX = 'reelbolt:editor:viewing-lut:';
export const viewingLutKey = (timelineId: string): string => `${VIEWING_LUT_KEY_PREFIX}${timelineId}`;

const PROJECT_FILE_ID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** A project file id is a GUID; anything else in localStorage is ignored, never put in a URL. */
export function isProjectFileId(value: string): boolean {
  return PROJECT_FILE_ID_RE.test(value);
}

/** Anything that is not a well-formed choice reads as "off". */
export function parseViewingLut(raw: unknown): ViewingLutChoice {
  if (!raw || typeof raw !== 'object') return null;
  const v = raw as { kind?: unknown; fileId?: unknown; id?: unknown };
  if (v.kind === 'file' && typeof v.fileId === 'string' && isProjectFileId(v.fileId)) {
    // A GUID needs no encoding, but the value is a path segment: encode it anyway so a future wiring cannot pass it raw.
    return { kind: 'file', fileId: encodeURIComponent(v.fileId) };
  }
  if (v.kind === 'profile' && typeof v.id === 'string' && isCameraProfileId(v.id)) return { kind: 'profile', id: v.id };
  return null;
}

type StorageLike = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;

function defaultStorage(): StorageLike | null {
  try {
    return typeof window !== 'undefined' ? window.localStorage : null;
  } catch {
    return null;
  }
}

export function readViewingLut(timelineId: string, storage: StorageLike | null = defaultStorage()): ViewingLutChoice {
  if (!storage) return null;
  try {
    const text = storage.getItem(viewingLutKey(timelineId));
    return text ? parseViewingLut(JSON.parse(text)) : null;
  } catch {
    return null;
  }
}

export function writeViewingLut(timelineId: string, choice: ViewingLutChoice, storage: StorageLike | null = defaultStorage()): void {
  if (!storage) return;
  try {
    if (choice === null) storage.removeItem(viewingLutKey(timelineId));
    else storage.setItem(viewingLutKey(timelineId), JSON.stringify(choice));
  } catch {
    // Storage full or blocked: the view just does not persist.
  }
  changed.forEach((fn) => fn());
}

const changed = new Set<() => void>();

function subscribe(listener: () => void): () => void {
  changed.add(listener);
  const onStorage = (event: StorageEvent) => {
    if (event.key === null || event.key.startsWith(VIEWING_LUT_KEY_PREFIX)) listener();
  };
  if (typeof window !== 'undefined') window.addEventListener('storage', onStorage);
  return () => {
    changed.delete(listener);
    if (typeof window !== 'undefined') window.removeEventListener('storage', onStorage);
  };
}

/**
 * The timeline's viewing LUT and its setter. On the phone surface the choice is ignored: `choice` is null and the
 * stored value is left alone for the device's other surfaces.
 */
export function useViewingLut(timelineId: string): { choice: ViewingLutChoice; setChoice: (next: ViewingLutChoice) => void } {
  const phone = useDeviceClass() === 'phone';
  // The snapshot is the raw string so useSyncExternalStore compares by value.
  const raw = useSyncExternalStore(
    subscribe,
    () => {
      const storage = defaultStorage();
      try {
        return storage?.getItem(viewingLutKey(timelineId)) ?? null;
      } catch {
        return null;
      }
    },
    () => null,
  );
  const choice = useMemo<ViewingLutChoice>(() => {
    if (phone || !raw) return null;
    try {
      return parseViewingLut(JSON.parse(raw));
    } catch {
      return null;
    }
  }, [phone, raw]);
  const setChoice = useCallback((next: ViewingLutChoice) => writeViewingLut(timelineId, next), [timelineId]);
  return { choice, setChoice };
}
