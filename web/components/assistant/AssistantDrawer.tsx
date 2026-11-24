'use client';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AssistantProvider } from './AssistantProvider';
import { AssistantDrawerShell } from './AssistantDrawer.shell';
import { AssistantActionRefresh, AssistantActionSync } from './AssistantActionSync';
import { AssistantPendingApprovals } from './AssistantPendingApprovals';
import { AssistantActivityPublisher } from './AssistantActivityPublisher';
import { AssistantThreadList } from './AssistantThreadList';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { useAssistant } from './AssistantShellProvider';
import { welcomeFor } from './pageInfo';
import { createThread } from '../../lib/api/assistant';
import { useLatest } from './useLatest';

/** No props: the drawer is mounted once for the whole platform, so everything it draws — whether
 * it is open, how it closes, which project context to send, and what the current page registered —
 * comes from the shell's context rather than from a page that may not be the one on screen. */
export type AssistantDrawerProps = Record<string, never>;

/**
 * Where the visit's conversation stands, for the chrome that has to say so.
 *
 * A turn may only be sent once a server thread is BOUND, because `POST /assistant/chat` allocates a
 * thread for any body that omits `threadId`. That allocation is correct server behaviour and is
 * kept — but a client that sends into it never learns which thread the turn landed on, so the turn
 * is orphaned: the thread the shell holds stays empty, and from the next turn on the model's history
 * comes from THAT thread. The model therefore has no memory of the turn the user can still see on
 * screen.
 *
 * The fix is not to adopt the allocated id from `X-Assistant-Thread-Id`: doing so means calling
 * back into `AssistantShellProvider.setThreadId` mid-stream, which re-renders `AssistantProvider`,
 * fires `getThread`, and REBINDS the runtime underneath the very turn that allocated it. The fix is
 * that the shipped UI never puts the user in the window at all — which is what this state, and the
 * `ready` flag every send path is gated on, exists for.
 */
export interface AssistantThreadSetup {
  /** True only once the shell holds a bound server thread. Nothing may be sent until it is. */
  ready: boolean;
  /** Why no thread is bound, when the attempt failed. Null while it is still being created. */
  error: string | null;
  /** Asks for a thread again — the failure state's way back, without reopening the drawer. */
  retry(): void;
}

/**
 * The platform's single assistant drawer. The runtime provider is the OUTERMOST element of what it
 * returns, so it is an ancestor of the `Drawer` and never a child of it: Mantine unmounts drawer
 * content on close, and a provider underneath that unmount would throw the conversation away every
 * time the drawer was closed to look at the canvas.
 *
 * It also owns the thread's LIFECYCLE: opening the assistant with no thread selected opens one, so
 * that the very first turn has somewhere to be persisted to — and reports the outcome, because a
 * creation that failed quietly is indistinguishable from an assistant that simply forgot.
 */
export function AssistantDrawer() {
  const t = useT();
  const tRef = useLatest(t);
  const {
    opened,
    close,
    context: pageContext,
    page,
    pageInfo,
    extraContext,
    pendingPrompt,
    consumePrompt,
    bumpThreads,
    threadId,
    fillThreadIdIfEmpty,
    selectionToken,
  } = useAssistant();

  // The project id falls back to the route's, so a page that never mounted `AssistantPageContext`
  // (the project overview, files) still gives the server a project to scope to.
  const context = useMemo(
    () => ({
      ...pageContext,
      projectId: pageContext.projectId ?? pageInfo.entityIds.projectId,
      workflowId: pageContext.workflowId ?? pageInfo.entityIds.workflowId,
      imageDocumentId: pageContext.imageDocumentId ?? pageInfo.entityIds.documentId,
      page: pageInfo,
      extra: extraContext,
    }),
    [pageContext, pageInfo, extraContext],
  );

  // Guards the create below against React 18/19's double-invoked effects: a ref survives the
  // StrictMode remount a dependency array does not, and two threads for one open would leave one
  // of them orphaned.
  const createdForVisitRef = useRef(false);

  // A manual retry, and the failure it is retrying. The failure carries the attempt that produced
  // it rather than being cleared by hand, which is what lets the panel report the CURRENT attempt
  // without a `setState` inside the effect below: a retry starts attempt n+1, and the failure from
  // attempt n stops matching immediately, so the notice says "starting" for as long as that is the
  // truth. A failure left over from an earlier OPEN is still reported until the new attempt
  // resolves — it is not stale, only old: no thread is bound either way, and the new attempt
  // replaces it the moment it settles.
  const [attempt, setAttempt] = useState(0);
  const [failure, setFailure] = useState<{ attempt: number; message: string } | null>(null);

  const retry = useCallback(() => setAttempt((count) => count + 1), []);

  useEffect(() => {
    if (!opened) {
      // The next open is a new visit, and may need a thread again.
      createdForVisitRef.current = false;
      return;
    }

    if (threadId !== null || createdForVisitRef.current) return;
    createdForVisitRef.current = true;

    // The project id is read once, at creation: the thread records the project it was opened on, and
    // a later navigation must not rewrite that. Only the id is ever re-read.
    const projectId = context.projectId;

    // The selection token, stamped BEFORE the request — this create is asynchronous too, and the
    // window it has to survive is the same one the user's "New chat" has. Read through the shell's
    // accessor rather than captured in this render: the comparison below has to reflect what
    // happened while the create was in flight, and a value from this closure cannot.
    const stamp = selectionToken();

    createThread(projectId ? { projectId } : undefined)
      // The write is CONDITIONAL, twice over, and both halves are the ownership rule.
      //
      // 1. The TOKEN. If it moved, the user bound a thread of their own while this was in flight —
      //    they picked a conversation, or asked for a new chat — and this create is stale. It is a
      //    fallback: it exists only because nothing was bound when it STARTED, so it may never
      //    land on top of a selection the user made in the meantime.
      //
      // 2. The EMPTY SLOT. What remains is a bind that fills a hole and claims nothing, which is
      //    why it goes through `fillThreadIdIfEmpty` rather than `setThreadId`: the claim form
      //    would advance the token, and a visit-create that landed first — legitimately, into an
      //    empty selection — would then be able to make a "New chat" the user had ALREADY clicked
      //    resolve into nothing. The updater form is what keeps the test and the write one atomic
      //    step, so no other writer can interleave between them.
      //
      // Together: FIRST BINDING WINS, with an explicit selection always allowed to be the first —
      // and, now, with an explicit selection always allowed to be the LAST as well, however long an
      // earlier asynchronous writer takes to land. The two other writers are the user speaking: a
      // thread row and "New chat", both of which claim the selection (and bump the token) rather
      // than filling it.
      //
      // The cost is that losing this race leaves the created thread unreferenced: it is a real,
      // empty thread the user will see in history. That is the right trade — the alternative is
      // writing the user's next message into a thread whose history is not the one on their screen,
      // which is silent data misplacement rather than one spare row. Returning the id to the server
      // would mean adopting it from a create the user did not ask for.
      .then(({ id }) => {
        if (selectionToken() !== stamp) return;
        fillThreadIdIfEmpty(id);
      })
      .catch((cause: unknown) => {
        // NOT swallowed, and the door is left open rather than latched: the failure is reported to
        // the panel, which blocks every send path and offers `retry` above. Swallowing it — which
        // is what this did — left `threadId` null for the whole visit with the effect never
        // re-running, so every turn asked the server to allocate its own thread and the assistant
        // was silently stateless: no error, no memory, no way back.
        //
        // A failure is deliberately NOT a claim, so it leaves the token alone: a user's own "New
        // chat" that failed must not be able to permanently suppress the fallback that is standing
        // by to bind this visit.
        createdForVisitRef.current = false;
        setFailure({ attempt, message: describeError(tRef.current, cause, tRef.current('assistant.drawer.threadStartFailed')) });
      });
  }, [opened, threadId, fillThreadIdIfEmpty, selectionToken, context.projectId, attempt, tRef]);

  // `ready` is derived from the shell's own thread, never latched separately: the shell is the only
  // thing that can hold a bound thread, so a second copy could only ever disagree with it.
  const thread = useMemo<AssistantThreadSetup>(
    () => ({
      ready: threadId !== null,
      error: threadId === null && failure?.attempt === attempt ? failure.message : null,
      retry,
    }),
    [threadId, failure, attempt, retry],
  );

  return (
    <AssistantProvider threadId={threadId} context={context}>
      {/* Before the shell, and outside it: these keep the app in step with what the assistant did
          even while the drawer is shut, and the order is what makes a finished action reach the
          page before the same reply's proposal does. See `AssistantActionSync`. */}
      <AssistantActionSync />
      <AssistantPendingApprovals />
      <AssistantActivityPublisher />
      <AssistantActionRefresh />
      <AssistantDrawerShell
        opened={opened}
        onClose={close}
        thread={thread}
        review={page.review}
        onWorkflowDiff={page.onWorkflowDiff}
        currentSteps={page.currentSteps}
        history={<AssistantThreadList />}
        welcome={welcomeFor(pageInfo.kind, t)}
        pendingPrompt={pendingPrompt}
        onPromptConsumed={consumePrompt}
        onTurnSettled={bumpThreads}
      />
    </AssistantProvider>
  );
}

