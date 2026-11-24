'use client';

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
} from 'react';
import type { Dispatch, ReactNode, SetStateAction } from 'react';
import { usePathname } from 'next/navigation';
import { useDisclosure } from '@mantine/hooks';
import type { WorkflowEditDiff } from '@/lib/types/workflow-assistant';
import type { PendingApproval } from '@/lib/assistant/approvals';
import type { AssistantActivityEvent } from '@/lib/assistant/activity';
import type { AssistantActivity } from '@/lib/types/assistant';
import { useAuth } from '@/lib/hooks/use-auth';
import { useAssistantActivity } from './useAssistantActivity';
import { AssistantBackgroundNotifier } from './AssistantBackgroundNotifier';
import { AssistantDrawer } from './AssistantDrawer';
import type { NamedStep } from './diff-model';
import { resolvePageInfo } from './pageInfo';
import type { AssistantPageInfo } from './pageInfo';

/** What the assistant knows about where the user is, without the page having to say it twice. */
export interface AssistantContext {
  projectId?: string;
  workflowId?: string;
  /** The picture editor's open document, sent so the assistant's picture tools act on it. */
  imageDocumentId?: string;
  /** Filled by the drawer from the router, never by a page. */
  page?: AssistantPageInfo;
  extra?: Record<string, unknown>;
}

/** Free-form, page-supplied hints (e.g. `{ highlightedField: 'steps[2].minScore' }`). Bounded server-side. */
export type AssistantExtraContext = Record<string, unknown>;

/** A prompt a page asked the assistant to show; consumed once by the open composer. */
export interface AssistantPendingPrompt {
  /** Monotonic, so the same text asked twice is still two requests. */
  id: number;
  prompt: string;
  /** Send immediately instead of only prefilling the composer. */
  send: boolean;
}

/**
 * What a single page hands to the shell for as long as it is mounted: the workflow-diff handler
 * the assistant applies proposals through, the steps the diff is rendered against, and any
 * page-owned review UI. Every field is optional — a page registers only what it has.
 */
export interface AssistantPageRegistration {
  onWorkflowDiff?: (diff: WorkflowEditDiff) => void;
  currentSteps?: readonly NamedStep[];
  review?: ReactNode;
}

export interface AssistantShellValue {
  opened: boolean;
  open(): void;
  close(): void;
  toggle(): void;
  threadId: string | null;
  /**
   * Binds a thread to the shell — the selection itself, not the resulting state.
   *
   * Deliberately React's own setter, updater form included, rather than a value-only setter. The
   * shell's thread has more than one writer and exactly one tie-break rule (see `AssistantDrawer`),
   * and only the updater form can express that rule WITHOUT A RACE: a value-only setter forces a
   * caller that has to consult the current selection — "bind, but not over one that arrived while I
   * was waiting" — to read it first, and any read taken before the write can already be stale by the
   * time the write lands. React hands an updater the CURRENT state at the moment of the update, so
   * the decision and the write are one atomic step and a later writer cannot interleave inside them.
   *
   * This is the CLAIMING form: the user asked for this thread, so it applies unconditionally and
   * advances `selectionToken`. The drawer's fallback uses `fillThreadIdIfEmpty` instead.
   */
  setThreadId: Dispatch<SetStateAction<string | null>>;
  /**
   * Fills an EMPTY selection, and only an empty one — the drawer's visit-create fallback.
   *
   * It deliberately does NOT advance `selectionToken`, and that is the whole point of it existing
   * next to `setThreadId`: the visit-create claims nothing (it exists only because nothing was
   * bound when it started), so it must not be able to invalidate a user's own selection that is
   * still in flight. If it did, a visit-create landing first — legitimately, into an empty slot —
   * would make a "New chat" the user had ALREADY clicked silently do nothing.
   */
  fillThreadIdIfEmpty(id: string): void;
  /**
   * The selection token: a monotonic count of the CLAIMS on the selection, read at any moment.
   *
   * A claim is a bind the user asked for (`setThreadId`). An asynchronous writer captures this
   * value BEFORE it starts its request and compares it AFTER the request settles; if it moved, the
   * user chose something else while the request was in flight and the result is stale — binding it
   * would file the user's next message under a thread they have navigated away from.
   *
   * Why a ref, and not state mirrored into one. The comparison has to reflect WHAT ACTUALLY
   * HAPPENED while the request was in flight, and a copy of a value read before the `await` cannot
   * do that by construction. This counter is not a copy of anything: it is the source of truth,
   * advanced synchronously by the very function that performs the bind, and the bind is the only
   * way the selection can ever change. So a writer that reads it after the `await` necessarily sees
   * every claim that landed before it, including one that landed in a different task.
   *
   * It counts claims rather than every write on purpose: `fillThreadIdIfEmpty` is the one binding
   * that is not a claim (see above), and counting it would break the user's own "New chat".
   */
  selectionToken(): number;
  context: AssistantContext;
  setContext(next: AssistantContext): void;
  page: AssistantPageRegistration;
  /** Route-derived description of the page on screen (kind, entity ids). */
  pageInfo: AssistantPageInfo;
  /** Extra context from `openWithPrompt`; cleared when the user navigates elsewhere. */
  extraContext: AssistantExtraContext | undefined;
  /**
   * Opens the assistant with `prompt` prefilled in the composer (not sent unless
   * `options.send`), and attaches `extraContext` to every turn until the user navigates away.
   */
  openWithPrompt(
    prompt: string,
    extraContext?: AssistantExtraContext,
    options?: { send?: boolean },
  ): void;
  pendingPrompt: AssistantPendingPrompt | null;
  /** Bumped when a turn settles so the conversation list reloads (titles change after turn one). */
  threadsVersion: number;
  bumpThreads(): void;
  consumePrompt(id: number): void;
  registerPage(registration: AssistantPageRegistration): () => void;
  /** Requests the conversation is still waiting on, published from the live runtime. */
  pendingApprovals: readonly PendingApproval[];
  /**
   * Publishes the pending list. Stable, and keeps the previous array when the requests are the
   * same — the publisher runs from an effect, and a fresh array on every frame would re-render
   * every consumer (and retrigger that effect) for nothing.
   */
  setPendingApprovals(next: readonly PendingApproval[]): void;
  /** Bumped when the user asks to be shown the waiting card; consumers poll until it renders. */
  approvalFocusToken: number;
  /** Which request the last reveal named, when it named one; null means "the first pending card". */
  approvalFocusId: string | null;
  /** Opens the assistant (if needed) and brings that pending card — or the first one — into view. */
  revealPendingApproval(id?: string): void;
  /** Writing threads, unread replies and watched runs across all threads (empty when signed out). */
  activity: AssistantActivity;
  /** Background changes (a reply started, finished, or needs an OK), oldest first, capped. */
  activityEvents: readonly AssistantActivityEvent[];
  /** Opens the assistant on that thread. */
  openThread(threadId: string): void;
  /** Marks a thread's replies read (best effort, silent on failure). */
  markThreadRead(threadId: string, throughSequence?: number): void;
  /** Which thread this tab is itself streaming a reply for, so it is not announced as background. */
  setLocalStreamingThreadId(threadId: string | null): void;
}

const AssistantShellContext = createContext<AssistantShellValue | null>(null);

/** Whether two pending lists name the same requests, by id and the words shown for each. */
function sameApprovals(previous: readonly PendingApproval[], next: readonly PendingApproval[]): boolean {
  if (previous.length !== next.length) return false;
  return previous.every((item, index) => item.id === next[index].id && item.title === next[index].title);
}

/** Served as `page` before any page registers, and what a disposer restores it to. */
const NO_REGISTRATION: AssistantPageRegistration = {};

/**
 * Owns the assistant's shell-wide state — whether it is open, which thread is on screen, and
 * what context the current page contributes — so the trigger, the drawer and the page all read
 * one copy. Mounted once, in `app/(app)/layout.tsx`, and it renders no UI of its own.
 */
export function AssistantShellProvider({ children }: { children: ReactNode }) {
  const [opened, { open, close, toggle }] = useDisclosure(false);
  const [threadIdValue, setThreadIdValue] = useState<string | null>(null);
  const [context, setContext] = useState<AssistantContext>({});
  const pathname = usePathname();
  // The query is read from the location rather than useSearchParams, which would force a Suspense
  // boundary around the whole app layout; page info is only ever used on the client.
  //
  // It is held in state and re-read AFTER each commit rather than sampled inside the memo below,
  // because the app router applies the new URL in a `useInsertionEffect` — that is, after the render
  // which already reports the new pathname. A memo that read `window.location.search` at that moment
  // stored the PREVIOUS page's query and kept it for the rest of the visit (the memo is keyed on the
  // pathname, and writing the URL re-renders nothing), which is how the picture editor's `?project=`
  // failed to reach the assistant while every api call on the page — which reads the query through
  // the router — had it.
  const [locationSearch, setLocationSearch] = useState('');
  useEffect(() => {
    const current = typeof window === 'undefined' ? '' : window.location.search;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- re-read the URL after each commit (router applies it in a useInsertionEffect)
    setLocationSearch((previous) => (previous === current ? previous : current));
  });
  const pageInfo = useMemo(() => resolvePageInfo(pathname, locationSearch), [pathname, locationSearch]);
  const [extra, setExtra] = useState<{ path: string; value: AssistantExtraContext } | null>(null);
  const [pendingPrompt, setPendingPrompt] = useState<AssistantPendingPrompt | null>(null);
  const promptIdRef = useRef(0);
  const [threadsVersion, setThreadsVersion] = useState(0);
  const bumpThreads = useCallback(() => setThreadsVersion((n) => n + 1), []);
  const [pendingApprovals, setPendingApprovalsValue] = useState<readonly PendingApproval[]>([]);
  const [approvalFocusToken, setApprovalFocusToken] = useState(0);
  const [approvalFocusId, setApprovalFocusId] = useState<string | null>(null);
  const { isAuthenticated } = useAuth();
  const [localStreamingThreadId, setLocalStreamingThreadId] = useState<string | null>(null);
  const { activity, events: activityEvents, markRead: markThreadRead } = useAssistantActivity({
    enabled: isAuthenticated,
    opened,
    localThreadId: localStreamingThreadId,
  });
  // Equality-stable on purpose: see `AssistantShellValue.setPendingApprovals`. A consumer reads this
  // from an effect and writes it back, and without the no-op the write would schedule the effect
  // again on every render.
  const setPendingApprovals = useCallback((next: readonly PendingApproval[]) => {
    setPendingApprovalsValue((previous) => (sameApprovals(previous, next) ? previous : next));
  }, []);

  const revealPendingApproval = useCallback((id?: string) => {
    // The id is half the request: the toast names the NEWEST request, so revealing "the first
    // pending card" would take the person to a different question than the one they were shown.
    setApprovalFocusId(id ?? null);
    setApprovalFocusToken((token) => token + 1);
    open();
  }, [open]);

  const extraContext = extra && extra.path === pageInfo.path ? extra.value : undefined;

  const openWithPrompt = useCallback(
    (prompt: string, extraValue?: AssistantExtraContext, options?: { send?: boolean }) => {
      if (extraValue) setExtra({ path: pageInfo.path, value: extraValue });
      promptIdRef.current += 1;
      setPendingPrompt({ id: promptIdRef.current, prompt, send: options?.send === true });
      open();
    },
    [open, pageInfo.path],
  );

  const consumePrompt = useCallback((id: number) => {
    setPendingPrompt((current) => (current?.id === id ? null : current));
  }, []);

  // The selection token. A ref, not state: a writer reads it after an `await`, and a value taken
  // from a render closure would be the value as of the render that started the request — exactly
  // the stale read this token exists to eliminate. It is bumped synchronously by `setThreadId`
  // below, so it never lags a bind. See `AssistantShellValue.selectionToken` for the full argument.
  const selectionTokenRef = useRef(0);

  const selectionToken = useCallback(() => selectionTokenRef.current, []);

  /**
   * The claiming bind: the user asked for this thread, so it applies unconditionally — last claim
   * wins — and it advances the token so that an in-flight asynchronous writer can see it happened.
   */
  const setThreadId = useCallback<Dispatch<SetStateAction<string | null>>>((next) => {
    selectionTokenRef.current += 1;
    setThreadIdValue(next);
  }, []);

  const openThread = useCallback(
    (id: string) => {
      setThreadId(id);
      open();
    },
    [setThreadId, open],
  );

  /**
   * The fallback bind. Conditional in the updater form, so the test and the write cannot be
   * separated by another writer landing in between, and deliberately silent to the token.
   */
  const fillThreadIdIfEmpty = useCallback((id: string) => {
    setThreadIdValue((current) => current ?? id);
  }, []);

  // The live registration sits in a ref rather than in state so `registerPage` can stay a
  // stable function: a page can register from an effect keyed on it without that effect
  // re-running. Reads go through useSyncExternalStore, so a write still re-renders.
  const registrationRef = useRef<AssistantPageRegistration>(NO_REGISTRATION);
  const subscribersRef = useRef(new Set<() => void>());

  const subscribe = useCallback((notify: () => void) => {
    const subscribers = subscribersRef.current;
    subscribers.add(notify);
    return () => {
      subscribers.delete(notify);
    };
  }, []);

  const getPage = useCallback(() => registrationRef.current, []);

  const page = useSyncExternalStore(subscribe, getPage, getPage);

  const registerPage = useCallback((registration: AssistantPageRegistration) => {
    const previous = registrationRef.current;
    registrationRef.current = registration;
    subscribersRef.current.forEach((notify) => notify());

    let disposed = false;
    return () => {
      // Idempotent, and never a clobber: if a newer registration has already replaced this one,
      // the ref no longer holds it and this disposer leaves the newer value alone.
      if (disposed) return;
      disposed = true;
      if (registrationRef.current !== registration) return;
      registrationRef.current = previous;
      subscribersRef.current.forEach((notify) => notify());
    };
  }, []);

  const value = useMemo<AssistantShellValue>(
    () => ({
      opened,
      open,
      close,
      toggle,
      threadId: threadIdValue,
      setThreadId,
      fillThreadIdIfEmpty,
      selectionToken,
      context,
      setContext,
      page,
      pageInfo,
      extraContext,
      openWithPrompt,
      pendingPrompt,
      threadsVersion,
      bumpThreads,
      consumePrompt,
      registerPage,
      pendingApprovals,
      setPendingApprovals,
      approvalFocusToken,
      approvalFocusId,
      revealPendingApproval,
      activity,
      activityEvents,
      openThread,
      markThreadRead,
      setLocalStreamingThreadId,
    }),
    [
      opened,
      open,
      close,
      toggle,
      threadIdValue,
      setThreadId,
      fillThreadIdIfEmpty,
      selectionToken,
      context,
      page,
      pageInfo,
      extraContext,
      openWithPrompt,
      pendingPrompt,
      threadsVersion,
      bumpThreads,
      consumePrompt,
      registerPage,
      pendingApprovals,
      setPendingApprovals,
      approvalFocusToken,
      approvalFocusId,
      revealPendingApproval,
      activity,
      activityEvents,
      openThread,
      markThreadRead,
    ],
  );

  return (
    <AssistantShellContext.Provider value={value}>
      {children}
      {/* Mounted unconditionally, and never behind `opened &&`: Mantine is what hides a closed
          drawer, and the drawer's own runtime provider has to stay mounted while it is closed —
          that is what keeps the conversation from being discarded every time the sheet is shut.
          It is rendered here, once, so the assistant outlives every page it is opened from. */}
      <AssistantDrawer />
      <AssistantBackgroundNotifier />
    </AssistantShellContext.Provider>
  );
}

export function useAssistant(): AssistantShellValue {
  const value = useContext(AssistantShellContext);
  if (value === null) {
    throw new Error('useAssistant must be used inside an AssistantShellProvider.');
  }
  return value;
}

/** The shell when there is one, else null: for surfaces (the editor) that also render without the app shell. */
export function useOptionalAssistant(): AssistantShellValue | null {
  return useContext(AssistantShellContext);
}

/** Alias of `useAssistant`, the name pages use: `useAssistantShell().openWithPrompt(...)`. */
export const useAssistantShell = useAssistant;
