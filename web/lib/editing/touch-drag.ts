'use client';

import { useSyncExternalStore } from 'react';

/**
 * W3-D3: dragging a clip to move it by finger is behind a per-device flag, off by default (touch
 * drags are emulation-verified only). With it off, a long press opens the clip action menu instead.
 */
export const TOUCH_DRAG_KEY = 'reelbolt:editor:touch-drag';
const CHANGE_EVENT = 'reelbolt:touch-drag-change';

export function readTouchDragEnabled(): boolean {
  try {
    return typeof localStorage !== 'undefined' && localStorage.getItem(TOUCH_DRAG_KEY) === 'on';
  } catch {
    return false;
  }
}

export function setTouchDragEnabled(enabled: boolean): void {
  try {
    if (enabled) localStorage.setItem(TOUCH_DRAG_KEY, 'on');
    else localStorage.removeItem(TOUCH_DRAG_KEY);
  } catch {
    // Storage blocked: the flag stays off.
  }
  if (typeof window !== 'undefined') window.dispatchEvent(new Event(CHANGE_EVENT));
}

function subscribe(onChange: () => void): () => void {
  window.addEventListener(CHANGE_EVENT, onChange);
  window.addEventListener('storage', onChange);
  return () => {
    window.removeEventListener(CHANGE_EVENT, onChange);
    window.removeEventListener('storage', onChange);
  };
}

/** Whether this device lets a long press pick a clip up to move it. */
export function useTouchDragEnabled(): boolean {
  return useSyncExternalStore(subscribe, readTouchDragEnabled, () => false);
}
