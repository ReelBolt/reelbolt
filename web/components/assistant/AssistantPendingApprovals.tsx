'use client';

import { useEffect, useMemo, useRef } from 'react';
import { useAuiState } from '@assistant-ui/react';
import { pendingApprovalsOf } from '@/lib/assistant/approvals';
import { useAssistant } from './AssistantShellProvider';

/**
 * The card may be a portal/frame behind the reveal, so look again rather than give up on frame one.
 * A cold open also creates the thread and fetches its history first, so the budget is generous
 * (20 s); every message update restarts the effect and looks again immediately.
 */
const REVEAL_INTERVAL_MS = 100;
const REVEAL_ATTEMPTS = 200;

/**
 * Publishes the conversation's waiting requests to the shell, and brings one into view on request.
 *
 * It renders nothing and lives beside `AssistantActionSync` — that is, inside the runtime provider
 * and OUTSIDE the drawer's body — because the whole point is to know about a request while the
 * drawer is closed. The header badge and the toast read the published list; the reveal effect is
 * what turns "1 approval waiting" into the card actually being on screen and focused.
 */
export function AssistantPendingApprovals(): null {
  // The runtime's own array, a stable reference until a frame arrives — never a filtered copy.
  const messages = useAuiState((s) => s.thread.messages);
  const approvals = useMemo(() => pendingApprovalsOf(messages), [messages]);
  const { setPendingApprovals, approvalFocusToken, approvalFocusId, opened } = useAssistant();
  // The reveal that already landed. Streaming frames keep changing `messages`, and without this
  // the effect would re-focus the card on every token, pulling focus away from the person.
  const revealedTokenRef = useRef(0);

  useEffect(() => {
    setPendingApprovals(approvals);
  }, [approvals, setPendingApprovals]);

  // A reveal is a request to focus, not a value to render: the drawer mounts its content a frame
  // (or a portal) later, so the card is polled for rather than read from this render.
  useEffect(() => {
    // Nothing to reveal before the first request, and a closed drawer has no card to find yet.
    if (approvalFocusToken === 0 || !opened) return;
    // This reveal already landed; later message frames must not steal focus again.
    if (revealedTokenRef.current === approvalFocusToken) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let attempts = 0;

    const reveal = () => {
      if (cancelled) return;
      // Named by id when the caller named one: the toast shows the NEWEST request, and landing on
      // the oldest pending card instead would answer a question the person was never shown.
      const cards = document.querySelectorAll<HTMLElement>(
        '[data-testid="assistant-action"][data-state="pending"]',
      );
      const card =
        approvalFocusId === null
          ? cards[0]
          : [...cards].find((element) => element.dataset.actionId === approvalFocusId);
      if (card) {
        card.scrollIntoView?.({ block: 'center' });
        card.focus?.({ preventScroll: true });
        revealedTokenRef.current = approvalFocusToken;
        return;
      }
      attempts += 1;
      if (attempts < REVEAL_ATTEMPTS) timer = setTimeout(reveal, REVEAL_INTERVAL_MS);
    };

    reveal();
    return () => {
      cancelled = true;
      if (timer !== undefined) clearTimeout(timer);
    };
  }, [approvalFocusToken, approvalFocusId, opened, messages]);

  return null;
}
