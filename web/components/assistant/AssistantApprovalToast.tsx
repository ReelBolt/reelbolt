'use client';

import { useEffect, useRef, useState } from 'react';
import type { CSSProperties, FocusEvent } from 'react';
import { ActionIcon, Button, Paper, Text, VisuallyHidden } from '@mantine/core';
import { IconX } from '@tabler/icons-react';
import { useT } from '@/i18n';
import { ASSISTANT_APPROVAL_TOAST_MS } from '@/lib/assistant/approvals';
import type { PendingApproval } from '@/lib/assistant/approvals';
import { useAssistant } from './AssistantShellProvider';
import classes from './AssistantApprovalToast.module.css';

/**
 * Names the newest waiting request outside the chat, so an approval is still visible when the
 * drawer is shut.
 *
 * It is a signpost, never a decision: Dismiss hides the toast and changes nothing, Review takes the
 * person to THAT request's card, and only a server frame (or an abandoned turn) removes the request
 * from the badge. A request the person dismissed stays dismissed; a later request gets its own toast.
 *
 * While the drawer is open the toast is not shown: the card is already on screen, and on a phone the
 * bottom sheet occupies the only space a toast could use. The badge stays in the header throughout.
 *
 * The countdown pauses while the toast has focus or the pointer is over it — otherwise it would hide
 * from under a keyboard user who is tabbing to Review.
 */
export function AssistantApprovalToast() {
  const t = useT();
  const { pendingApprovals, revealPendingApproval, opened } = useAssistant();
  const [shown, setShown] = useState<PendingApproval | null>(null);
  const dismissedRef = useRef(new Set<string>());
  // Plain booleans, not the id they paused for: an approval arriving while the pointer is over the
  // toast replaces its contents in place, and the pause must survive that replacement.
  const [hovered, setHovered] = useState(false);
  const [focused, setFocused] = useState(false);

  const newest = pendingApprovals.length > 0 ? pendingApprovals[pendingApprovals.length - 1] : null;

  useEffect(() => {
    if (opened) {
      // eslint-disable-next-line react-hooks/set-state-in-effect -- the drawer already shows the card; the toast would cover it
      setShown(null);
      return;
    }
    if (!newest) {
      setShown(null);
      return;
    }
    if (dismissedRef.current.has(newest.id)) {
      setShown((current) => (current && current.id !== newest.id ? null : current));
      return;
    }
    setShown((current) => (current?.id === newest.id ? current : newest));
  }, [newest, opened]);

  // A hidden toast is a new element when it returns; pause state from the previous one must not
  // survive and silence its countdown. A request that settles while the person is inside the toast
  // would otherwise drop focus to the document body, so hand it back to the header trigger.
  useEffect(() => {
    if (shown) return;
    if (focused) {
      document.querySelector<HTMLElement>('[data-assistant-trigger]')?.focus({ preventScroll: true });
    }
    // eslint-disable-next-line react-hooks/set-state-in-effect -- reset pause state after the toast unmounts
    setHovered(false);
    setFocused(false);
  }, [shown, focused]);

  // The countdown bar and the dismiss timer must empty together. The bar is CSS (`animation`), so
  // the timer banks the time already spent whenever it is paused and schedules only the remainder.
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

  const review = () => {
    if (!shown) return;
    const id = shown.id;
    hide();
    revealPendingApproval(id);
  };

  // Focus moves between the toast's own controls constantly; only leaving it entirely resumes the
  // countdown.
  const handleBlur = (event: FocusEvent<HTMLDivElement>) => {
    if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setFocused(false);
  };

  const more = pendingApprovals.length - 1;

  return (
    <>
      {/* Mounted empty for the component's whole life: a live region inserted together with its
          text is announced unreliably, and the visible Paper comes and goes with each request. */}
      <VisuallyHidden>
        <div role="status" aria-live="polite" aria-atomic="true">
          {shown
            ? more > 0
              ? t('assistant.toast.needsOkTitleMore', { title: shown.title, more })
              : t('assistant.toast.needsOkTitle', { title: shown.title })
            : ''}
        </div>
      </VisuallyHidden>

      {shown && (
        <Paper
          data-testid="assistant-approval-toast"
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
              {t('assistant.toast.needsOk')}
            </Text>
            <Text size="sm" fw={600} lineClamp={2}>
              {shown.title}
            </Text>
            {more > 0 && (
              <Text size="xs" c="dimmed">
                {t('assistant.toast.moreWaiting', { more })}
              </Text>
            )}
          </div>
          <div className={classes.actions}>
            <Button size="md" variant="light" color="violet" onClick={review}>
              {t('assistant.toast.review')}
            </Button>
            <ActionIcon variant="subtle" color="gray" size={44} aria-label={t('assistant.toast.dismiss')} onClick={hide}>
              <IconX size={18} />
            </ActionIcon>
          </div>
          {/* The dismissal, made visible: it drains over the same 8 s the timer is counting, and
              pauses with it. Keyed by request so a newer approval starts a fresh bar. */}
          <div
            key={shown.id}
            aria-hidden
            data-testid="assistant-approval-countdown"
            data-paused={hovered || focused ? 'true' : undefined}
            className={classes.countdown}
            style={{ '--toast-ms': `${ASSISTANT_APPROVAL_TOAST_MS}ms` } as CSSProperties}
          />
        </Paper>
      )}
    </>
  );
}
