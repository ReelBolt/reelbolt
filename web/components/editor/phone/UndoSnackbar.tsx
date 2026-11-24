'use client';

import React, { useMemo, useSyncExternalStore } from 'react';
import { Button } from '@mantine/core';
import classes from './phone.module.css';
import { useT } from '@/i18n';

/** How long the Undo stays on screen (VF9: a destructive action is undone, not confirmed). */
export const UNDO_SNACKBAR_MS = 6000;

export interface UndoSnackbarRequest {
  /** Plain text, past tense: "Clip deleted". */
  message: string;
  /** Runs when the user taps Undo. */
  onUndo: () => void;
  durationMs?: number;
}

interface Entry {
  id: number;
  message: string;
  onUndo: () => void;
}

let current: Entry | null = null;
let seq = 0;
let timer: ReturnType<typeof setTimeout> | null = null;
const listeners = new Set<() => void>();

function emit(): void {
  listeners.forEach((l) => l());
}

function clearTimer(): void {
  if (timer !== null) clearTimeout(timer);
  timer = null;
}

/** Shows the snackbar (replacing any showing one). Returns a function that dismisses this one. */
export function showUndoSnackbar({ message, onUndo, durationMs = UNDO_SNACKBAR_MS }: UndoSnackbarRequest): () => void {
  clearTimer();
  const entry: Entry = { id: ++seq, message, onUndo };
  current = entry;
  timer = setTimeout(() => {
    if (current?.id === entry.id) dismissUndoSnackbar();
  }, durationMs);
  emit();
  return () => {
    if (current?.id === entry.id) dismissUndoSnackbar();
  };
}

export function dismissUndoSnackbar(): void {
  clearTimer();
  current = null;
  emit();
}

const api = { show: showUndoSnackbar, dismiss: dismissUndoSnackbar } as const;

/** The small API every destructive phone action uses: `const undoable = useUndoSnackbar(); undoable.show({ message, onUndo })`. */
export function useUndoSnackbar(): typeof api {
  return useMemo(() => api, []);
}

function subscribe(onChange: () => void): () => void {
  listeners.add(onChange);
  return () => {
    listeners.delete(onChange);
  };
}

const snapshot = (): Entry | null => current;

/** Mount once in the phone shell. */
export function UndoSnackbar() {
  const t = useT('editor.phone.undoSnackbar');
  const entry = useSyncExternalStore(subscribe, snapshot, () => null);
  if (!entry) return null;
  return (
    <div className={classes.snackbar} role="status" aria-live="polite" data-testid="undo-snackbar">
      <span className={classes.snackbarText}>{entry.message}</span>
      <Button
        variant="subtle"
        color="violet"
        h={44}
        miw={64}
        px="md"
        onClick={() => {
          const run = entry.onUndo;
          dismissUndoSnackbar();
          run();
        }}
        data-testid="undo-snackbar-undo"
      >
        {t('undo')}
      </Button>
    </div>
  );
}

/** Test seam. */
export function resetUndoSnackbar(): void {
  dismissUndoSnackbar();
}
