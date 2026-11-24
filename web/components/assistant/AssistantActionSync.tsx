'use client';

import { useEffect } from 'react';
import { useSWRConfig } from 'swr';
import { useAuiState } from '@assistant-ui/react';
import { reduceAssistantActions } from '@/lib/assistant/actions';
import { announceAssistantActionDone, onAssistantActionDone } from '@/lib/assistant/events';
import { revalidationFilter } from '@/lib/assistant/revalidate';

/**
 * Tells the rest of the app, the moment an assistant action finishes, that something changed.
 *
 * It reads the newest assistant reply's request cards straight from the conversation runtime and
 * announces each `done` one on the window (once per action — see `lib/assistant/events.ts`). It is
 * mounted OUTSIDE the drawer on purpose: the drawer unmounts its content when it is closed, and the
 * person who shut it to look at the canvas is exactly the one whose canvas has to catch up.
 *
 * Ordering matters to the page that listens, and is why this must be mounted BEFORE the drawer in
 * the tree: within one commit, effects of an earlier sibling run first, so a finished action is
 * announced before the same reply's `data-workflowDiff` reaches the page — the server only sends a
 * proposal that is still unapplied at the end of the turn, so anything announced here first can
 * never wrongly consume it.
 */
export function AssistantActionSync() {
  // The runtime's own array — a stable reference until a frame arrives — never a filtered copy: a
  // selector that builds a new array on every read would never settle.
  const unstableData = useAuiState((s) => {
    const messages = s.thread.messages;
    for (let i = messages.length - 1; i >= 0; i--) {
      if (messages[i].role === 'assistant') return messages[i].metadata?.unstable_data ?? null;
    }
    return null;
  });

  useEffect(() => {
    for (const action of reduceAssistantActions(unstableData)) announceAssistantActionDone(action);
  }, [unstableData]);

  return null;
}

/**
 * Brings the app's cached lists up to date after an assistant change: the project list, a project's
 * files, workflows and runs, and the few other things a change can reach (see `revalidate.ts`).
 * Needs no conversation, only the app's data cache, so it simply listens.
 */
export function AssistantActionRefresh() {
  const { mutate } = useSWRConfig();

  useEffect(
    () =>
      onAssistantActionDone((done) => {
        void mutate(revalidationFilter(done.kind));
      }),
    [mutate],
  );

  return null;
}
