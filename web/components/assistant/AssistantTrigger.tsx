'use client';

import { ActionIcon, Indicator, Tooltip } from '@mantine/core';
import { IconMessageCircle, IconWand } from '@tabler/icons-react';
import { useT } from '@/i18n';
import type { T } from '@/i18n/translator';
import { useAssistant } from './AssistantShellProvider';
import classes from './AssistantTrigger.module.css';

/** The name a sighted reader never sees but a screen-reader user relies on. */
function triggerLabel(t: T, opened: boolean, approvals: number, replies: number, writing: boolean): string {
  // Approvals alone keep their long-standing wording.
  if (approvals > 0 && replies === 0 && !writing) {
    const detail = t('assistant.trigger.approvalsWaiting', { count: approvals });
    return t(opened ? 'assistant.trigger.showWith' : 'assistant.trigger.openWith', { detail });
  }
  const parts: string[] = [];
  if (approvals > 0) parts.push(t('assistant.trigger.needsOk', { count: approvals }));
  if (replies > 0) parts.push(t('assistant.trigger.newReplies', { count: replies }));
  if (writing) parts.push(t('assistant.trigger.writing'));
  if (parts.length === 0) return t(opened ? 'assistant.trigger.close' : 'assistant.trigger.open');
  // While the drawer is open the button cannot close it, so it must not claim to.
  return t(opened ? 'assistant.trigger.showWith' : 'assistant.trigger.openWith', { detail: parts.join(', ') });
}

/**
 * The assistant's only entry point. It lives in `AppShell`'s header — not in `PageHeader` — so
 * every authenticated page reaches it, including the majority that render no page header at all.
 * The button carries its own accessible name; the tooltip is a sighted-pointer affordance and
 * never the sole name.
 *
 * When requests are waiting the button is also the way to them: the badge counts them, and a press
 * opens the drawer on the card instead of toggling it shut — closing the drawer would hide the very
 * thing the number is pointing at.
 */
export function AssistantTrigger() {
  const t = useT();
  const { opened, toggle, pendingApprovals, revealPendingApproval, activity } = useAssistant();
  const approvals = pendingApprovals.length;
  const replies = activity?.unread.length ?? 0;
  const writing = (activity?.writing.length ?? 0) > 0;
  // One number for everything that wants a look; the accessible name says which is which.
  const count = approvals + replies;

  return (
    <Tooltip label={t('assistant.trigger.label')} disabled={opened}>
      <Indicator
        label={count}
        disabled={count === 0}
        size={18}
        withBorder
        // violet-6 in both schemes: 3.14:1 on the dark header (violet-8 was 2.47:1) and 4.95:1
        // against white, so the badge's boundary itself meets non-text contrast, not just its digit.
        styles={{ indicator: { background: 'var(--mantine-color-violet-6)' } }}
      >
        <ActionIcon
          variant="subtle"
          color="violet"
          size="lg"
          data-assistant-trigger
          data-tour="assistant"
          className={writing ? classes.writing : undefined}
          data-writing={writing ? 'true' : undefined}
          aria-label={triggerLabel(t, opened, approvals, replies, writing)}
          aria-expanded={opened}
          // The trigger names no request: with several waiting it reveals the first card, while
          // the toast (which names the newest) passes that request's id.
          onClick={approvals > 0 ? () => revealPendingApproval() : toggle}
        >
          {opened ? <IconWand size={20} /> : <IconMessageCircle size={20} />}
        </ActionIcon>
      </Indicator>
    </Tooltip>
  );
}
