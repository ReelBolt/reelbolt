import type { EditorStore } from './editor-store';

/** What an on-screen or keyboard Undo / Redo did: nothing to step, a step taken, or a step the engine refused. */
export type StepResult = { status: 'nothing' } | { status: 'done' } | { status: 'refused'; title: string; message: string };

/**
 * Undo one step and say what happened. `EditorStore.undo()` already refuses safely (the document,
 * cursor and history stay byte-identical); this adds the sentence the user needs, because a refused
 * undo used to look exactly like a button that does nothing.
 */
export function undoStep(store: EditorStore): StepResult {
  if (!store.canUndo()) return { status: 'nothing' };
  if (store.undo()) return { status: 'done' };
  return {
    status: 'refused',
    title: 'Could not undo',
    message: 'That change cannot be taken back because the timeline changed since. Nothing was altered.',
  };
}

export function redoStep(store: EditorStore): StepResult {
  if (!store.canRedo()) return { status: 'nothing' };
  if (store.redo()) return { status: 'done' };
  return {
    status: 'refused',
    title: 'Could not redo',
    message: 'That change cannot be applied again because the timeline changed since. Nothing was altered.',
  };
}
