'use client';

import { useEffect, useSyncExternalStore } from 'react';
import { deviceClassOf, type DeviceClass, type ViewportFacts } from './device-class';

/**
 * The retired phone escape hatch (W3-D1, AC 11). "Open full view" used to store this flag and load the tablet
 * layout on a phone; it is no longer offered, a phone ignores the flag, and `useRetireFullViewOnPhone` clears a
 * stored value. Storage can throw (private mode, blocked cookies), so every access is guarded.
 */
export const FULL_VIEW_STORAGE_KEY = 'reelbolt:editor:full-view';

let fullView: boolean | null = null;
const listeners = new Set<() => void>();

function readFullView(): boolean {
  if (fullView !== null) return fullView;
  let stored = false;
  try {
    stored = typeof window !== 'undefined' && window.localStorage.getItem(FULL_VIEW_STORAGE_KEY) === '1';
  } catch {
    stored = false;
  }
  fullView = stored;
  return stored;
}

export function getFullViewPreference(): boolean {
  return readFullView();
}

export function setFullViewPreference(on: boolean): void {
  fullView = on;
  try {
    if (on) window.localStorage.setItem(FULL_VIEW_STORAGE_KEY, '1');
    else window.localStorage.removeItem(FULL_VIEW_STORAGE_KEY);
  } catch {
    // Remembered for this page only.
  }
  listeners.forEach((l) => l());
}

/** Test seam: forget the cached preference so the next read goes to storage again. */
export function resetFullViewPreferenceCache(): void {
  fullView = null;
  listeners.forEach((l) => l());
}

export function readViewportFacts(): ViewportFacts {
  return {
    width: window.innerWidth,
    height: window.innerHeight,
    coarsePointer: typeof window.matchMedia === 'function' && window.matchMedia('(pointer: coarse)').matches,
  };
}

function rawClass(): DeviceClass {
  return deviceClassOf(readViewportFacts());
}

function subscribe(onChange: () => void): () => void {
  listeners.add(onChange);
  window.addEventListener('resize', onChange);
  window.addEventListener('orientationchange', onChange);
  const pointer = typeof window.matchMedia === 'function' ? window.matchMedia('(pointer: coarse)') : null;
  pointer?.addEventListener?.('change', onChange);
  return () => {
    listeners.delete(onChange);
    window.removeEventListener('resize', onChange);
    window.removeEventListener('orientationchange', onChange);
    pointer?.removeEventListener?.('change', onChange);
  };
}

/**
 * A phone never gets the tablet layout: it overflowed the screen, drew a 0x0 picture in portrait and left no way
 * back (the owner's stuck phone). The remembered "full view" choice is therefore ignored on a phone viewport.
 */
function snapshot(): DeviceClass {
  return rawClass();
}

/** What the server and the first hydrating paint assume. */
const serverSnapshot = (): DeviceClass => 'desktop';

/**
 * The editor's one device-class source (RM.2): `deviceClassOf` over the live viewport, re-evaluated on
 * resize, rotation and a change of primary pointer. A phone is always `phone`, whatever was remembered.
 * On the server (and the hydration pass) it is `desktop`, then corrects on the client.
 */
export function useDeviceClass(): DeviceClass {
  return useSyncExternalStore(subscribe, snapshot, serverSnapshot);
}

/** The viewport rule alone, ignoring the escape hatch: does this device NEED the phone surface? */
export function useRawDeviceClass(): DeviceClass {
  return useSyncExternalStore(subscribe, rawClass, serverSnapshot);
}

/** True where the editor shows the phone surface. */
export function useIsPhoneClass(): boolean {
  return useDeviceClass() === 'phone';
}

/**
 * True where a finger is the likely pointer: the phone class (one rule, W3-D1) or a coarse primary pointer
 * (a tablet). Dialogs use it for 44 px targets and full-screen sheets.
 */
export function useIsTouchSurface(): boolean {
  const phone = useIsPhoneClass();
  const coarse = useSyncExternalStore(subscribe, coarsePointerNow, () => false);
  return phone || coarse;
}

function coarsePointerNow(): boolean {
  return typeof window !== 'undefined' && typeof window.matchMedia === 'function' && window.matchMedia('(pointer: coarse)').matches;
}

/**
 * Retires a stale "full view" choice: a phone that stored '1' (the old "Open full view" button) is back on the phone
 * surface with nothing to do, and the stored value is cleared so it cannot come back on a wider screen by surprise.
 */
export function useRetireFullViewOnPhone(): void {
  const raw = useRawDeviceClass();
  useEffect(() => {
    if (raw === 'phone' && getFullViewPreference()) setFullViewPreference(false);
  }, [raw]);
}
