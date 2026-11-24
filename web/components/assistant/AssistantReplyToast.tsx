'use client';

import { useEffect, useRef, useState } from 'react';
import type { CSSProperties, FocusEvent } from 'react';
import { ActionIcon, Button, Paper, Text, VisuallyHidden } from '@mantine/core';
import { IconX } from '@tabler/icons-react';
import { useT } from '@/i18n';
import type { T } from '@/i18n/translator';
import { ASSISTANT_APPROVAL_TOAST_MS } from '@/lib/assistant/approvals';
import type { AssistantActivityEvent } from '@/lib/assistant/activity';
import { useAssistant } from './AssistantShellProvider';
import classes from './AssistantReplyToast.module.css';

const NO_EVENTS: readonly AssistantActivityEvent[] = [];

function headline(t: T, event: AssistantActivityEvent): string {
  const values = { title: event.title };
  if (event.kind === 'writing') {
    return event.wake ? t('assistant.toast.wakeHeadline', values) : t('assistant.toast.writingHeadline', values);
  }
  if (event.kind === 'approval') return t('assistant.toast.approvalHeadline', values);
  return t('assistant.toast.repliedHeadline', values);
}

/**
 * Tells the person, outside the chat, that the assistant is answering - or has answered - in a
 * conversation they are not looking at. Sibling of the approval toast and behaves the same: it
 * hides itself after a few seconds, pauses while hovered or focused, and a dismissed notice stays
 * dismissed. It is not shown while the drawer is open on that conversation, and never for a reply
 * this tab is streaming itself (the shell does not raise those).
 */
export function AssistantReplyToast() {
  const t = useT();
  const { activityEvents = NO_EVENTS, opened, threadId, openThread, markThreadRead } = useAssistant();
  const [shown, setShown] = useState<AssistantActivityEvent | null>(null);
  const dismissedRef = useRef(new Set<number>());
  const [hovered, setHovered] = useState(false);
  const [focused, setFocused] = useState(false);

  const newest = activityEvents.length > 0 ? activityEvents[activityEvents.length - 1] : null;
  const hiddenByDrawer = newest !== null && opened && newest.threadId === threadId;

  useEffect(() => {
    if (!newest || hiddenByDrawer || dismissedRef.current.has(newest.id)) {
      setShown(null);
      return;
    }
    setShown((current) => (current?.id === newest.id ? current : newest));
  }, [newest, hiddenByDrawer]);

  useEffect(() => {
    if (shown) return;
    if (focused) {
      document.querySelector<HTMLElement>('[data-assistant-trigger]')?.focus({ preventScroll: true });
    }
    // eslint-disable-next-line react-hooks/set-state-in-effect -- reset pause state after the toast unmounts
    setHovered(false);
    setFocused(false);
  }, [shown, focused]);

  const remainingRef = useRef(ASSISTANT_APPROVAL_TOAST_MS);
  const startedAtRef = useRef(0);

  useEffect(() => {
    if (!shown) return;
    remainingRef.current = ASSISTANT_APPROVAL_TOAST_MS;
    startedAtRef.current = Date.now();
  }, [shown]);

  useEffect(() => {
    if (!shown || hovered || focused) return;
    const id = shown.id;
    const remaining = remainingRef.current;
    startedAtRef.current = Date.now();
    const timer = setTimeout(() => {
      dismissedRef.current.add(id);
      setShown((current) => (current?.id === id ? null : current));
    }, remaining);
    return () => {
      clearTimeout(timer);
      remainingRef.current = Math.max(0, remaining - (Date.now() - startedAtRef.current));
    };
  }, [shown, hovered, focused]);

  const hide = () => {
    if (!shown) return;
    dismissedRef.current.add(shown.id);
    setShown(null);
  };

  const read = () => {
    if (!shown) return;
    const { threadId: id, sequence } = shown;
    hide();
    openThread(id);
    markThreadRead(id, sequence);
  };

  const handleBlur = (event: FocusEvent<HTMLDivElement>) => {
    if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setFocused(false);
  };

  const action = shown?.kind === 'replied' ? t('assistant.toast.read') : shown?.kind === 'approval' ? t('assistant.toast.review') : null;

  return (
    <>
      {/* Mounted empty for the component's whole life, so the announcement is reliable. */}
      <VisuallyHidden>
        <div role="status" aria-live="polite" aria-atomic="true">
          {shown ? headline(t, shown) : ''}
        </div>
      </VisuallyHidden>

      {shown && (
        <Paper
          data-testid="assistant-reply-toast"
          className={classes.toast}
          shadow="md"
          radius="md"
          p="sm"
          withBorder
          onMouseEnter={() => setHovered(true)}
          onMouseLeave={() => setHovered(false)}
          onFocus={() => setFocused(true)}
          onBlur={handleBlur}
        >
          <div className={classes.text}>
            <Text size="xs" fw={700} c="violet">
              {shown.kind === 'writing' ? t('assistant.toast.background') : t('assistant.toast.assistant')}
            </Text>
            <Text size="sm" fw={600} lineClamp={2}>
              {headline(t, shown)}
            </Text>
          </div>
          <div className={classes.actions}>
            {action && (
              <Button size="md" variant="light" color="violet" onClick={read}>
                {action}
              </Button>
            )}
            <ActionIcon variant="subtle" color="gray" size={44} aria-label={t('assistant.toast.dismiss')} onClick={hide}>
              <IconX size={18} />
            </ActionIcon>
          </div>
          <div
            key={shown.id}
            aria-hidden
            data-testid="assistant-reply-countdown"
            data-paused={hovered || focused ? 'true' : undefined}
            className={classes.countdown}
            style={{ '--toast-ms': `${ASSISTANT_APPROVAL_TOAST_MS}ms` } as CSSProperties}
          />
        </Paper>
      )}
    </>
  );
}
