'use client';

import { useEditorController } from './workspace/useEditorController';
import { useEditorView } from './workspace/view/useEditorView';
import type { EditorWorkspaceProps } from './workspace/types';

export type { EditorWorkspaceProps } from './workspace/types';

/**
 * The editable workspace: one store and one sync client behind the shell (workspace bar, resizable panels, status
 * bar), the canvas timeline with its track headers, the program viewer that plays THE TIMELINE, the transport, and
 * every panel around them.
 *
 * This file only joins the two halves. `workspace/useEditorController` is everything the workspace knows and can do
 * (domain hooks, no markup); `workspace/view/*` builds the markup from it (view hooks that return elements and keep no state).
 */
export function EditorWorkspace({ store, sync, onReload, seedStepResultId = null }: EditorWorkspaceProps) {
  const controller = useEditorController({ store, sync, onReload, seedStepResultId });
  return useEditorView(controller);
}
