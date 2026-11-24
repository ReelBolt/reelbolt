import { useMemo } from 'react';
import { useT } from '@/i18n';
import { msg } from '@/i18n/msg';
import { notifications } from '@mantine/notifications';
import type { EditOp } from '@/lib/editing/engine';
import { refusalSentence } from '@/lib/editing/refusal';
import { refusalSentenceMsg } from '@/lib/editing/labels-msg';
import type { EditTimeline } from '@/lib/editing/types';

/**
 * The "Edit refused" toast, with a translated title. `refusalSentence` (lib/editing) is still English until its own
 * lane converts it; a sentence the UI decided itself is passed in already resolved (`t(...)`).
 */
export function useRefusalToast(): { showRefusal: (error: unknown, doc?: EditTimeline | null, op?: EditOp) => void; showRefusalMessage: (message: string) => void } {
  const t = useT();
  return useMemo(() => {
    const title = t.msg(msg('editor-helpers-1.shell.refusalTitle'));
    return {
      showRefusal: (error, doc, op) => notifications.show({ color: 'red', title, message: t.msg(refusalSentenceMsg(refusalSentence(error, doc, op))) }),
      showRefusalMessage: (message) => notifications.show({ color: 'red', title, message }),
    };
  }, [t]);
}
