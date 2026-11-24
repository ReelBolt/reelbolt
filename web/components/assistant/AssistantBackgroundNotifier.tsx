'use client';

import { useEffect, useRef } from 'react';
import { useT } from '@/i18n';
import { shouldNotify, showAssistantNotification } from '@/lib/assistant/browser-notify';
import { useAssistant } from './AssistantShellProvider';
import { useLatest } from './useLatest';

/**
 * Raises a browser notification for a reply that finished, or a request needing an OK, in a turn
 * running in the background. Renders nothing. The words are fixed; the only thing taken from the
 * conversation is its title - never a message.
 */
export function AssistantBackgroundNotifier(): null {
  const { activityEvents, opened, openThread, revealPendingApproval } = useAssistant();
  const t = useT();
  const tRef = useLatest(t);
  const handledRef = useRef(0);
  const openedRef = useRef(opened);

  useEffect(() => {
    openedRef.current = opened;
  }, [opened]);

  useEffect(() => {
    for (const event of activityEvents) {
      if (event.id <= handledRef.current) continue;
      handledRef.current = event.id;
      if (event.kind === 'writing') continue;
      if (!shouldNotify({ drawerOpen: openedRef.current })) continue;
      const approval = event.kind === 'approval';
      showAssistantNotification({
        title: tRef.current(approval ? 'assistant.notify.needsOk' : 'assistant.notify.replied'),
        body: event.title,
        threadId: event.threadId,
        onClick: () => {
          openThread(event.threadId);
          if (approval) revealPendingApproval();
        },
      });
    }
  }, [activityEvents, openThread, revealPendingApproval, tRef]);

  return null;
}
