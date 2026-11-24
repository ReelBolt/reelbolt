/**
 * The permission requests a conversation is still waiting on.
 *
 * The cards live inside the assistant reply, which means they are invisible while the drawer is
 * shut. The header badge and the toast need the same list without rendering the conversation, so
 * this module derives it from the runtime's messages — the existing `data-assistantAction` frames,
 * never a copy the UI keeps of its own.
 */

import { isAbandoned, reduceAssistantActions } from './actions';
import type { AssistantActionKind } from '../types/assistant';

/** One request still waiting for an answer, in the words the card will use. */
export interface PendingApproval {
  id: string;
  title: string;
  kind: AssistantActionKind;
}

/**
 * The shape of a chat message this module reads. Deliberately narrow: the runtime's messages carry
 * far more, and only the role, the turn status (which decides whether a turn was abandoned) and the
 * frames appended to it matter here.
 */
export interface AssistantMessageLike {
  id?: string;
  role?: string;
  status?: { type?: string; reason?: string } | null;
  metadata?: { unstable_data?: unknown } | null;
}

/** How long the toast names a request before it hides itself. The one source of that duration. */
export const ASSISTANT_APPROVAL_TOAST_MS = 8000;

/**
 * Every request still `pending` across the conversation, in the order each id first appeared.
 *
 * A request stops counting the moment the server says what became of it (allowed, declined,
 * expired, done, failed) — or when the turn it belonged to stopped without answering, which is the
 * same thing the card renders as expired. Nothing else removes one: no timer, no local dismissal.
 */
export function pendingApprovalsOf(
  messages: readonly AssistantMessageLike[] | null | undefined,
): PendingApproval[] {
  if (!Array.isArray(messages)) return [];

  // A Map keeps the position of an id's first appearance while collapsing repeated frames.
  const byId = new Map<string, PendingApproval>();
  for (const message of messages) {
    if (message?.role !== 'assistant') continue;
    const running = message.status?.type === 'running';
    for (const action of reduceAssistantActions(message.metadata?.unstable_data)) {
      if (action.state !== 'pending') continue;
      if (isAbandoned(action, running)) continue;
      if (byId.has(action.id)) continue;
      byId.set(action.id, { id: action.id, title: action.title, kind: action.kind });
    }
  }
  return [...byId.values()];
}
