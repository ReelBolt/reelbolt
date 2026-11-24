import { useCallback } from 'react';
import { notifications } from '@mantine/notifications';
import { type UiState } from '@/lib/editing/store';
import { type EditOp } from '@/lib/editing/engine';
import { useT } from '@/i18n';
import type { PreviewParityCtx } from './usePreviewParity';

/** One way to change the document: dispatch with a refusal the user can read, and the In/Out marks. */
export function useEditDispatch(ctx: PreviewParityCtx) {
  const { store, readOnly, readOnlyMessage, showRefusal } = ctx;
  const tx = useT('editor.editorWorkspace');
  // ── dispatch with user-facing refusal ─────────────────────────────────────
  const dispatch = useCallback(
    (op: EditOp, label?: string) => {
      if (readOnly) {
        notifications.show({ color: 'orange', title: tx('readOnly'), message: readOnlyMessage });
        return false;
      }
      try {
        store.dispatch(op, label);
        return true;
      } catch (error) {
        showRefusal(error, store.getState().doc, op);
        return false;
      }
    },
    [store, readOnly, readOnlyMessage, tx],
  );

  // W0.6: in/out marks persist to the document (one `setInOut` op once both are set); read-only keeps them on screen only.
  const setMarks = useCallback((marks: UiState['inOut']) => store.setInOutMarks(marks, { persist: !readOnly }), [store, readOnly]);

  return { dispatch, setMarks };
}

export type EditDispatchCtx = PreviewParityCtx & ReturnType<typeof useEditDispatch>;
