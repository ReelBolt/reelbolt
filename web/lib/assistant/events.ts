/**
 * The assistant tells the rest of the app what it just changed.
 *
 * When one of the assistant's actions reaches `done` the chat stream says what to reload (a
 * `refresh` payload of ids). The assistant drawer and the page it changed are far apart in the tree —
 * the drawer is mounted once for the whole dashboard — so the news travels as a window event, like
 * the picture editor's (`lib/editor/assistant-events.ts`): the drawer announces, and whoever shows
 * the changed thing listens.
 *
 * Each action is announced ONCE per page, by its id. Reopening the drawer re-renders every message,
 * and a change the app already brought in must not be brought in again.
 *
 * No React here; the window is the only global touched.
 */

import type { AssistantAction, AssistantActionKind, AssistantActionRefresh } from '../types/assistant';

/** The window event's name. */
export const ASSISTANT_ACTION_DONE_EVENT = 'reelbolt:assistant-action-done';

/** What a listener is told. The title is the plain-language one; ids are only in `refresh`. */
export interface AssistantActionDone {
  id: string;
  kind: AssistantActionKind;
  title: string;
  /** What to reload, as the server sent it. Absent when the server named nothing in particular. */
  refresh?: AssistantActionRefresh;
}

/** Every action announced on this page so far. Module-level on purpose: it outlives the drawer. */
const announced = new Set<string>();

/**
 * Tells the page that an action finished, once per action id. Answers true when it was announced
 * now; false when it was not a finished action, had been announced already, or there is no window.
 */
export function announceAssistantActionDone(action: AssistantAction): boolean {
  if (action.state !== 'done' || !action.id || announced.has(action.id)) return false;
  if (typeof window === 'undefined') return false;
  announced.add(action.id);
  const detail: AssistantActionDone = { id: action.id, kind: action.kind, title: action.title };
  if (action.refresh) detail.refresh = action.refresh;
  window.dispatchEvent(new CustomEvent<AssistantActionDone>(ASSISTANT_ACTION_DONE_EVENT, { detail }));
  return true;
}

/** Listens for finished actions. Answers the function that stops listening. */
export function onAssistantActionDone(handler: (done: AssistantActionDone) => void): () => void {
  if (typeof window === 'undefined') return () => {};
  const listener = (event: Event) => {
    const detail = (event as CustomEvent<AssistantActionDone>).detail;
    if (detail) handler(detail);
  };
  window.addEventListener(ASSISTANT_ACTION_DONE_EVENT, listener);
  return () => window.removeEventListener(ASSISTANT_ACTION_DONE_EVENT, listener);
}

/** Forgets every announcement. For tests only. */
export function resetAnnouncedAssistantActions(): void {
  announced.clear();
}
