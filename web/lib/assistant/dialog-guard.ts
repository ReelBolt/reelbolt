'use client';

import { useSyncExternalStore } from 'react';

/**
 * Whether a dialog opened FROM the assistant's drawer is on screen.
 *
 * Mantine closes every modal that is open when Escape is pressed — each listens on the window, and
 * only a `Modal.Stack` makes them take turns. The assistant's confirmation sits on top of its own
 * drawer, so one Escape meant for the confirmation ("keep asking") would also shut the whole
 * assistant. The drawer reads this and ignores Escape while a dialog of ours is above it.
 *
 * A count rather than a flag, so two dialogs overlapping (a request's "Always allow" and the header
 * switch) cannot release each other's hold.
 */
let open = 0;
const listeners = new Set<() => void>();

function emit() {
  listeners.forEach((listener) => listener());
}

/** Registers one open dialog. Answers the function that releases it; releasing twice is harmless. */
export function holdAssistantDialog(): () => void {
  open += 1;
  emit();
  let released = false;
  return () => {
    if (released) return;
    released = true;
    open -= 1;
    emit();
  };
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** True while any dialog opened from the assistant is on screen. */
export function useAssistantDialogOpen(): boolean {
  return useSyncExternalStore(
    subscribe,
    () => open > 0,
    () => false,
  );
}
