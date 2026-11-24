/**
 * When a screen that has been away should ask the server what became of its assistant turn.
 *
 * A phone that switches screens drops the chat connection, but the server keeps the turn (and any open
 * permission card) and the person can still answer it. Nothing in the browser says the connection died: the
 * stream may end quietly, end with an error, or just stop delivering while JavaScript was suspended. This
 * module decides, from what the screen holds, whether it is worth asking. The ask is `onReload`, which reads
 * the stored thread and the turn's state and re-attaches to a turn that is still live.
 *
 * It is deliberately conservative about what counts as "something may have been missed", because a reload
 * replaces the runtime: a healthy, finished conversation must never be reloaded.
 */

import { isSettled, reduceAssistantActions } from './actions';
import type { AssistantMessageLike } from './approvals';

/** How long the page must have been hidden before a still-running stream is doubted. */
export const RESUME_HIDDEN_MS = 3000;

/** The least time between two resumes, so a flapping connection cannot reload in a loop. */
export const RESUME_MIN_INTERVAL_MS = 4000;

export interface ResumeInput {
  messages: readonly AssistantMessageLike[] | null | undefined;
  /** Whether the local runtime believes a reply is streaming right now. */
  isRunning: boolean;
  /** How long the page was hidden before this look; 0 when it was not. */
  hiddenMs: number;
}

/** True when the newest reply cannot be trusted to be the whole story. */
export function needsResume({ messages, isRunning, hiddenMs }: ResumeInput): boolean {
  if (!Array.isArray(messages)) return false;

  const last = [...messages].reverse().find((message) => message?.role === 'assistant');
  if (!last) return false;

  // A stream that still claims to run after the page was away for a while is probably a dead one.
  if (isRunning) return hiddenMs >= RESUME_HIDDEN_MS;

  // The person pressed Stop: that is an answer, not a dropped connection. Never bring the turn back.
  if (last.status?.type === 'incomplete' && last.status.reason === 'cancelled') return false;

  const broke = last.status?.type === 'incomplete' && last.status.reason === 'error';
  const waiting = reduceAssistantActions(last.metadata?.unstable_data).some((action) => !isSettled(action.state));
  return broke || waiting;
}

/**
 * The permission requests a person left open by pressing Stop: the newest assistant reply ended `cancelled`
 * and still has cards in the `pending` state. Stop only aborts the connection, and the server keeps the card
 * (and the turn, with its slot and credit hold) until it is answered or times out — so the screen answers
 * "don't allow" for them, which ends the turn. Ids only; nothing is allowed on anyone's behalf.
 */
export function pendingCardsLeftByStop(messages: readonly AssistantMessageLike[] | null | undefined): string[] {
  if (!Array.isArray(messages)) return [];
  const last = [...messages].reverse().find((message) => message?.role === 'assistant');
  if (!last || last.status?.type !== 'incomplete' || last.status.reason !== 'cancelled') return [];
  return reduceAssistantActions(last.metadata?.unstable_data)
    .filter((action) => action.state === 'pending')
    .map((action) => action.id);
}
