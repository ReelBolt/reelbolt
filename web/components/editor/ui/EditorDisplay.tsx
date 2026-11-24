'use client';

import React, { useEffect, useRef, useSyncExternalStore } from 'react';
import { useMantineColorScheme, type MantineColorScheme } from '@mantine/core';
import {
  effectiveDensity,
  getDisplayPrefsSnapshot,
  getServerDisplayPrefs,
  subscribeDisplayPrefs,
  updateDisplayPrefs,
  type EditorDensity,
  type EditorSurround,
} from '@/lib/editing/display/display-prefs';

const subscribeNever = (): (() => void) => () => undefined;
const COARSE = '(pointer: coarse)';
const subscribeCoarse = (onChange: () => void): (() => void) => {
  const mq = window.matchMedia(COARSE);
  mq.addEventListener('change', onChange);
  return () => mq.removeEventListener('change', onChange);
};
const coarseNow = (): boolean => window.matchMedia(COARSE).matches;

/** Where the person's own app scheme waits while the editor surround is forced dark. */
export const SAVED_SCHEME_KEY = 'reelbolt:editor:scheme-before-dark';
const isScheme = (v: unknown): v is MantineColorScheme => v === 'light' || v === 'dark' || v === 'auto';
function readSavedScheme(): MantineColorScheme | null {
  try {
    const v = window.localStorage.getItem(SAVED_SCHEME_KEY);
    return isScheme(v) ? v : null;
  } catch {
    return null;
  }
}
function writeSavedScheme(value: MantineColorScheme | null): void {
  try {
    if (value === null) window.localStorage.removeItem(SAVED_SCHEME_KEY);
    else window.localStorage.setItem(SAVED_SCHEME_KEY, value);
  } catch {
    /* storage unavailable: the scheme is restored for this visit only */
  }
}

export interface EditorDisplay {
  /** What is applied now (a finger always gets `comfortable`). */
  density: EditorDensity;
  /** What the person chose. */
  preferredDensity: EditorDensity;
  surround: EditorSurround;
  coarsePointer: boolean;
  setDensity: (density: EditorDensity) => void;
  setSurround: (surround: EditorSurround) => void;
}

/** The editor's display settings on this device: density (comfortable / compact) and the surround (match the app / always dark). */
export function useEditorDisplay(): EditorDisplay {
  const prefs = useSyncExternalStore(subscribeDisplayPrefs, getDisplayPrefsSnapshot, getServerDisplayPrefs);
  const coarsePointer = useSyncExternalStore(subscribeCoarse, coarseNow, () => false);
  return {
    density: effectiveDensity(prefs.density, coarsePointer),
    preferredDensity: prefs.density,
    surround: prefs.surround,
    coarsePointer,
    setDensity: (density) => void updateDisplayPrefs({ density }),
    setSurround: (surround) => void updateDisplayPrefs({ surround }),
  };
}

/**
 * The editor's root element. It carries the display state as attributes (`data-ed-density`, `data-ed-touch`,
 * `data-ed-surround`) that app/global.css turns into the control and row sizes, and, while the surround is
 * "always dark", puts the whole app into the dark scheme for as long as the editor is open (so menus, dialogs, the
 * header and the timeline canvas all agree) and puts the person's own scheme back when they leave.
 */
export function EditorSurface({ children }: { children: React.ReactNode }) {
  const { density, surround, coarsePointer } = useEditorDisplay();
  const { colorScheme, setColorScheme } = useMantineColorScheme();
  /** The person's own scheme while the surround forces dark (null: nothing is forced by this mount). */
  const saved = useRef<MantineColorScheme | null>(null);
  const pendingRestore = useRef<ReturnType<typeof setTimeout> | null>(null);
  // The unmount cleanup must run once, whatever the identity of the setter on each render.
  const setScheme = useRef(setColorScheme);
  useEffect(() => {
    setScheme.current = setColorScheme;
  });

  // The first render after hydration still holds the server's default surround; act only once the stored one has been read.
  const ready = useSyncExternalStore(subscribeNever, () => true, () => false);

  useEffect(() => {
    if (!ready) return;
    if (pendingRestore.current !== null) {
      clearTimeout(pendingRestore.current);
      pendingRestore.current = null;
    }
    const stored = readSavedScheme();
    if (surround === 'dark') {
      // Mantine remembers whatever scheme it is given, so the person's own choice is kept under our key: it survives a reload
      // (and a crash) and is what "Match the app" or leaving the editor puts back.
      if (saved.current === null) {
        saved.current = stored ?? colorScheme;
        writeSavedScheme(saved.current);
      }
      if (colorScheme !== 'dark') setColorScheme('dark');
    } else {
      const back = saved.current ?? stored;
      if (back !== null) {
        saved.current = null;
        writeSavedScheme(null);
        setColorScheme(back);
      }
    }
  }, [ready, surround, colorScheme, setColorScheme]);
  // Leaving the editor gives the scheme back. The restore is deferred one tick so that React's development-mode "unmount and
  // remount" probe (which runs this cleanup and then the effect again at once) cancels it instead of resetting the scheme.
  useEffect(() => {
    if (pendingRestore.current !== null) {
      clearTimeout(pendingRestore.current);
      pendingRestore.current = null;
    }
    return () => {
      pendingRestore.current = setTimeout(() => {
        pendingRestore.current = null;
        if (saved.current !== null) {
          const back = saved.current;
          saved.current = null;
          writeSavedScheme(null);
          setScheme.current(back);
        }
      }, 0);
    };
  }, []);

  return (
    <div data-editor-surface data-ed-density={density} data-ed-touch={coarsePointer ? 'true' : 'false'} data-ed-surround={surround}>
      {children}
    </div>
  );
}
