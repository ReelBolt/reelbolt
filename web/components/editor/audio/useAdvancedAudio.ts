'use client';

import { useCallback, useSyncExternalStore } from 'react';

const KEY = 'rb.editor.advancedAudio';
const EVENT = 'rb-advanced-audio';

const read = (): boolean => {
  try {
    return typeof window !== 'undefined' && window.localStorage.getItem(KEY) === '1';
  } catch {
    return false;
  }
};

const subscribe = (cb: () => void) => {
  window.addEventListener(EVENT, cb);
  window.addEventListener('storage', cb);
  return () => {
    window.removeEventListener(EVENT, cb);
    window.removeEventListener('storage', cb);
  };
};

/** D3: the "Advanced audio" switch (buses, compressor/gate/limiter/de-esser, numeric EQ), remembered per browser and shared by every audio panel. */
export function useAdvancedAudio(): [boolean, (on: boolean) => void] {
  const on = useSyncExternalStore(subscribe, read, () => false);
  const set = useCallback((next: boolean) => {
    try {
      window.localStorage.setItem(KEY, next ? '1' : '0');
    } catch {
      /* private mode */
    }
    window.dispatchEvent(new Event(EVENT));
  }, []);
  return [on, set];
}
