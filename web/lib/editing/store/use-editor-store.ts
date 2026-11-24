'use client';

/**
 * React binding for {@link EditorStore}, on `useSyncExternalStore` so a selector re-renders only
 * the components that read what changed. The store itself has no React in it.
 */

import { useSyncExternalStore } from 'react';
import type { EditorState, EditorStore } from './editor-store';

export function useEditorStore<T>(store: EditorStore, selector: (state: EditorState) => T): T {
  return useSyncExternalStore(
    (listener) => store.subscribe(listener),
    () => selector(store.getState()),
    () => selector(store.getState()),
  );
}
