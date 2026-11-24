'use client';

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import type { MutableRefObject, PropsWithChildren } from 'react';
import { AssistantRuntimeProvider, useAui } from '@assistant-ui/react';
import type { ThreadMessageLike } from '@assistant-ui/react';
import { useDataStreamRuntime, type DataStreamProtocol } from '@assistant-ui/react-data-stream';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import type { AssistantContext } from './AssistantShellProvider';
import { readActiveTab, readDocumentTitle } from './pageInfo';
import { rejectTurnRefusal } from './assistantTurnRefusal';
import { AssistantResumeOnReturn } from './AssistantResumeOnReturn';
import { getAssistantChatStreamUrl, getThread, getTurnStatus } from '../../lib/api/assistant';
import type { AssistantThreadMessage, AssistantTurnStatus } from '../../lib/types/assistant';
import { useLatest } from './useLatest';

/** Where the thread's persisted history stands, for whatever chrome wants to say so. */
export type AssistantHistoryStatus = 'idle' | 'loading' | 'ready' | 'error';

export interface AssistantHistoryState {
  status: AssistantHistoryStatus;
  /** Why the history could not be loaded. Non-null only while `status` is 'error'. */
  error: string | null;
  /** Loads the thread again — the error state's retry. */
  reload(): void;
  /**
   * The server said it is still answering this thread's newest message (asked before a reload, or
   * from another tab). Lets the panel say "still working on it" instead of "no reply" — the two
   * look identical in the stored turns. False while unknown.
   */
  replyInProgress: boolean;
  /**
   * How many runs the assistant is waiting on for this thread (it answers by itself when they
   * finish), and — when exactly one — that run's name. Zero when nothing is pending.
   */
  pendingWatches: number;
  pendingWatchLabel: string | null;
}

const NO_RELOAD = () => {};

/** Served before a provider is mounted, and while no thread is bound: no history, nothing to say. */
const IDLE_HISTORY: AssistantHistoryState = { status: 'idle', error: null, reload: NO_RELOAD,
  replyInProgress: false,
  pendingWatches: 0,
  pendingWatchLabel: null,
};

const AssistantHistoryContext = createContext<AssistantHistoryState>(IDLE_HISTORY);

/**
 * The history load's outcome, read by the drawer's chrome. A failed load is reported here rather
 * than thrown, so the chat still renders — and its composer still works — underneath the notice.
 */
export function useAssistantHistory(): AssistantHistoryState {
  return useContext(AssistantHistoryContext);
}

export interface AssistantProviderProps extends PropsWithChildren {
  /**
   * The server thread to restore, or null/absent before one exists. Optional because a bare
   * <AssistantProvider> with no thread is a real state: the drawer mounts the runtime before its
   * first thread has been created, and a thread-less runtime still carries the page's context.
   */
  threadId?: string | null;
  context: AssistantContext;
}

/** What the transport sends alongside every turn. Absent fields are omitted, never null. */
interface AssistantRequestBody {
  /**
   * The server thread the turn is persisted to.
   *
   * Deliberately NOT optional. An optional property would be absent from the object this callback
   * returns, and the data-stream adapter spreads that object LAST over a body it has already
   * built — including a `threadId` of its own, taken from its `unstable_threadId` argument. Before
   * a server thread is bound that argument is assistant-ui's CLIENT-SIDE placeholder, the literal
   * `__LOCALID_…` (`@assistant-ui/core/.../remote-thread-state.js`), which the endpoint cannot
   * bind: `AssistantChatRequest.ThreadId` is a `Guid`, so the JSON input formatter rejects the
   * whole body and the turn answers 400 — and because the server's thread auto-allocation keys on
   * an ABSENT key, that placeholder is precisely the one shape which defeats it.
   *
   * Stating the key is what displaces the adapter's value. Its value is the shell's real server id
   * when one is bound, and `undefined` otherwise; `JSON.stringify` drops `undefined` properties,
   * so the placeholder is overridden on the object while `threadId` still vanishes from the
   * serialized JSON — the shape the endpoint reads as "allocate me a thread".
   */
  threadId: string | undefined;
  projectId?: string;
  workflowId?: string;
  /** The picture editor's open document, when the user is in it. */
  imageDocumentId?: string;
  /** Untrusted page hints; the server bounds and sanitises them. Omitted when the page is unknown. */
  pageContext?: Record<string, unknown>;
}

/**
 * The body of a re-attach: a thread id, the attach flag, and an EMPTY `messages` array — nothing
 * else.
 *
 * A reconnect must never re-send the user's message (T-022/T-023, Review Focus 11). The transport
 * serializes the conversation into `messages` on every run and the adapter spreads this object LAST
 * over the body it built, so stating `messages: []` here is what REPLACES that array: the attach
 * request carries the question not at all, and the server appends nothing and starts no second turn.
 * It is the same mechanism the thread id already relies on (see `AssistantRequestBody`), and it is
 * the only way to drop a key the adapter always sets.
 */
interface AssistantAttachBody {
  threadId: string | undefined;
  attach: true;
  messages: readonly never[];
}

/** Whether a thread's turn is one a reopening client must re-attach to rather than read from history. */
function isLiveTurn(turn: AssistantTurnStatus | null): boolean {
  return turn?.state === 'running' || turn?.state === 'awaiting_approval';
}

/**
 * The dashed-GUID form the server issues and its `Guid` parameter binds. A thread id that is not
 * one of these can never be posted — `System.Text.Json` would reject the entire body over it — so
 * it is treated as "no thread bound" and the turn asks the server to allocate one, rather than
 * turning into a 400 the user cannot act on.
 */
const SERVER_THREAD_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function isServerThreadId(value: string | null | undefined): value is string {
  return typeof value === 'string' && SERVER_THREAD_ID.test(value);
}

interface RuntimeConfig {
  api: string;
  protocol: DataStreamProtocol;
  body: () => Promise<AssistantRequestBody>;
  /** Turns a refusal the server answered before streaming (the 429 turn cap) into the reply's error. */
  onResponse: (response: Response) => Promise<void>;
  initialMessages: readonly ThreadMessageLike[];
}

interface AssistantRuntimeHostProps extends PropsWithChildren {
  /** The thread the transport should address. Always the shell's current selection. */
  threadId: string | null;
  context: AssistantContext;
  /** The history this runtime is BORN with — read once, by `useDataStreamRuntime`. */
  initialMessages: readonly ThreadMessageLike[];
  /** The server still owns a live turn for this thread: subscribe to it rather than read history. */
  attach: boolean;
  /** Runs the assistant is waiting on: while above zero the host polls for a turn the server starts. */
  pendingWatches: number;
  /** Reports the count the turn probe last gave. */
  onWatchCount: (count: number) => void;
  /** Loads the thread's history again (a reply landed while nothing was attached). */
  onReload: () => void;
}

/**
 * What the runtime is bound to: the thread it is displaying, the turns it was born with, and the
 * React key that expresses both. A changed key is what tells React to construct another runtime.
 */
interface Binding {
  generation: number;
  key: string | null;
  threadId: string | null;
  messages: readonly ThreadMessageLike[];
}

/** Shared empty array, so "no messages" keeps one identity and can never move a memo by itself. */
const NO_MESSAGES: readonly ThreadMessageLike[] = [];

/**
 * The runtime starts keyless: it exists before it has a thread, and its `threadId` travels in the
 * request body rather than in its identity.
 */
const INITIAL_BINDING: Binding = { generation: 0, key: null, threadId: null, messages: NO_MESSAGES };

/**
 * Whether a freshly loaded thread needs the runtime to be re-created, and with what.
 *
 * The rule is about what is on SCREEN, not about ids: a runtime is re-created when the
 * conversation it holds has to change — when it moves from one thread to another, when a
 * re-opened thread's stored turns have to be restored, or when a retry finally brings history in.
 * Gaining a FIRST thread does not qualify on its own: an empty, just-created thread is the very
 * conversation already on screen, merely acquiring an id, and re-creating the runtime there would
 * throw away a turn the user typed while the thread was still being created.
 */
function nextBinding(
  current: Binding,
  threadId: string,
  messages: readonly ThreadMessageLike[],
): Binding {
  const rebind = (next: readonly ThreadMessageLike[]): Binding => {
    const generation = current.generation + 1;
    return { generation, key: `${threadId}#${generation}`, threadId, messages: next };
  };

  if (current.threadId === threadId) {
    // The thread already on screen, loaded again — the error state's retry. Only history that
    // actually arrived is worth re-creating a runtime for.
    return messages.length > 0 && messages !== current.messages ? rebind(messages) : current;
  }

  if (current.threadId !== null) {
    // Two different conversations: whatever is on screen belongs to the other one.
    return rebind(messages);
  }

  return messages.length === 0 ? { ...current, threadId } : rebind(messages);
}

/** One load's outcome, tagged with the request that produced it so a stale response is ignored. */
interface HistoryLoad {
  request: string;
  status: 'ready' | 'error';
  error: string | null;
  replyInProgress: boolean;
  /** The server still owns a live turn for this thread: the runtime must re-attach to it. */
  attach: boolean;
  /** Runs the assistant is waiting on, from the thread detail or the turn probe. */
  pendingWatches: number;
  pendingWatchLabel: string | null;
}

/**
 * Owns the assistant's conversation runtime for the subtree under it.
 *
 * The runtime config is memoized so it keeps ONE object identity across renders, and `context` is
 * deliberately absent from that memo: it lives in a ref the stable `body` callback reads when a
 * turn is actually sent. That is what lets the drawer be mounted once, above the router, while the
 * page underneath it changes — a fresh config object on every navigation would have been a reset
 * signal for the whole conversation. The page's project and workflow are per-CALL context now; the
 * thread — and only the thread — is what the runtime is re-created for.
 *
 * `useDataStreamRuntime` reads `initialMessages` exactly once, when it constructs its runtime, so
 * restored history means a runtime that was born with it: the host below carries a key, and a
 * changed key is a new runtime. The thread the runtime displays is COMMITTED, not instantaneous —
 * until the incoming thread's history has arrived, the conversation on screen stays put, because
 * a runtime keyed to an id whose history is not yet known would blank the panel and remount again
 * a moment later.
 */
export function AssistantProvider({ threadId = null, context, children }: AssistantProviderProps) {
  const t = useT();
  const tRef = useLatest(t);
  const [binding, setBinding] = useState<Binding>(INITIAL_BINDING);
  const [load, setLoad] = useState<HistoryLoad | null>(null);
  const [attempt, setAttempt] = useState(0);
  const [polled, setPolled] = useState<{ threadId: string | null; count: number } | null>(null);

  const reload = useCallback(() => setAttempt((count) => count + 1), []);

  // The request this render belongs to. `status` is derived from it rather than written by the
  // effect, so a thread switch reports "loading" the moment it is requested — without an effect
  // that sets state on its way in and re-renders the whole drawer a second time.
  const request = threadId === null ? null : `${threadId}#${attempt}`;

  useEffect(() => {
    if (threadId === null || request === null) return;

    let cancelled = false;

    // The turn state is asked in PARALLEL with the history: it decides whether the runtime must
    // re-attach to a turn the server still owns (T-022 — the turn outlives the connection), and it
    // does not depend on the history load. `getTurnStatus` never rejects: a 404 (a foreign thread,
    // or a hub that forgot the turn after a process restart) is an ordinary "no live turn", not an
    // error — the stored history is the answer.
    Promise.all([getThread(threadId), getTurnStatus(threadId)])
      .then(([detail, turn]) => {
        if (cancelled) return;
        setBinding((current) => nextBinding(current, threadId, toThreadMessages(detail.messages)));
        setLoad({
          request,
          status: 'ready',
          error: null,
          replyInProgress: detail.replyInProgress === true,
          attach: isLiveTurn(turn),
          pendingWatches: Math.max(detail.pendingRunWatches?.length ?? 0, turn?.pendingWatches ?? 0),
          pendingWatchLabel: detail.pendingRunWatches?.[0]?.runLabel ?? null,
        });
      })
      .catch((cause: unknown) => {
        if (cancelled) return;
        // An empty conversation is still a usable one: the failure is reported beside the chat,
        // never instead of it, and never by refusing to render the composer.
        setBinding((current) => nextBinding(current, threadId, NO_MESSAGES));
        setLoad({ request, status: 'error', error: describeError(tRef.current, cause, tRef.current('assistant.provider.threadLoadFailed')), replyInProgress: false,
          attach: false,
          pendingWatches: 0,
          pendingWatchLabel: null,
        });
      });

    return () => {
      cancelled = true;
    };
  }, [threadId, request, tRef]);

  const status: AssistantHistoryStatus =
    request === null ? 'idle' : load?.request === request ? load.status : 'loading';
  const error = status === 'error' ? (load?.error ?? null) : null;
  const replyInProgress = status === 'ready' && load?.replyInProgress === true;
  const attach = status === 'ready' && load?.attach === true;

  // The probe's newer count (from the poller) wins over the one the thread was loaded with.
  const loadedWatches = status === 'ready' ? (load?.pendingWatches ?? 0) : 0;
  const pendingWatches = polled !== null && polled.threadId === threadId ? polled.count : loadedWatches;
  const pendingWatchLabel = pendingWatches === 1 && loadedWatches === 1 ? (load?.pendingWatchLabel ?? null) : null;

  const history = useMemo<AssistantHistoryState>(
    () => ({ status, error, reload, replyInProgress, pendingWatches, pendingWatchLabel }),
    [status, error, reload, replyInProgress, pendingWatches, pendingWatchLabel],
  );

  return (
    <AssistantHistoryContext.Provider value={history}>
      <AssistantRuntimeHost
        key={binding.key ?? 'unbound'}
        threadId={threadId}
        context={context}
        initialMessages={binding.messages}
        attach={attach}
        pendingWatches={pendingWatches}
        onWatchCount={(count) => setPolled({ threadId, count })}
        onReload={reload}
      >
        {children}
      </AssistantRuntimeHost>
    </AssistantHistoryContext.Provider>
  );
}

/**
 * The runtime itself, one instance per conversation. Split out of the provider precisely so that it
 * CAN be re-created: a key on an element the provider returns is what constructs another runtime,
 * and only the runtime, when the bound conversation changes.
 */
function AssistantRuntimeHost({
  threadId,
  context,
  initialMessages,
  attach,
  pendingWatches,
  onWatchCount,
  onReload,
  children,
}: AssistantRuntimeHostProps) {
  const contextRef = useRef({ threadId, context });

  useEffect(() => {
    contextRef.current = { threadId, context };
  }, [threadId, context]);

  // Set by `TurnReattach` just before it starts the re-attach run, read and cleared by the body
  // callback below. A ref, not state: the callback is memoized and must not change identity.
  const attachRef = useRef(false);

  const body = useCallback(async (): Promise<AssistantRequestBody | AssistantAttachBody> => {
    const current = contextRef.current;

    // A re-attach: the thread id and the attach flag, and an EMPTY `messages`. The user's message is
    // never re-sent — the server replays the turn it already owns. Consumed once, so an ordinary
    // turn that follows is built normally.
    if (attachRef.current) {
      attachRef.current = false;
      return {
        threadId: isServerThreadId(current.threadId) ? current.threadId : undefined,
        attach: true,
        messages: [],
      };
    }

    // The adapter's own `threadId` (`unstable_threadId`) is deliberately NOT the source here: it is
    // assistant-ui's `__LOCALID_…` placeholder until a server thread is bound, so the shell's
    // selection is the only value that can name a real thread. Writing the key — with the id or
    // with `undefined` — is what overrides the adapter's contribution; see `AssistantRequestBody`.
    const payload: AssistantRequestBody = {
      threadId: isServerThreadId(current.threadId) ? current.threadId : undefined,
    };
    // Omitted rather than sent as undefined or null: the endpoint distinguishes "no project
    // context" (a platform-wide page) from a context the caller supplied.
    if (current.context.projectId) payload.projectId = current.context.projectId;
    if (current.context.workflowId) payload.workflowId = current.context.workflowId;
    if (current.context.imageDocumentId) payload.imageDocumentId = current.context.imageDocumentId;
    const page = current.context.page;
    if (page) {
      payload.pageContext = {
        path: page.path,
        kind: page.kind,
        title: page.title,
        documentTitle: readDocumentTitle(),
        tab: readActiveTab() ?? page.tab,
        entityIds: page.entityIds,
        ...(current.context.extra ? { extra: current.context.extra } : {}),
      };
    }
    return payload;
  }, []);

  // The newest user message the replayed reply answers — the parent of the live assistant reply, so
  // it hangs off the question it belongs to. `initialMessages` is the history this runtime was born
  // with; a live turn always has a user message behind it, but null is a safe root.
  const parentId = useMemo(() => {
    for (let i = initialMessages.length - 1; i >= 0; i--) {
      if (initialMessages[i].role === 'user') return initialMessages[i].id ?? null;
    }
    return null;
  }, [initialMessages]);

  // `initialMessages` belongs in the dependency list rather than behind a ref: this host is
  // re-created whenever the runtime has to be born with different history, so a changed array is
  // already a mount, and the memo's identity only ever has to survive ordinary re-renders — the
  // ones a page navigation causes, which is exactly the case it exists for.
  const runtimeConfig = useMemo<RuntimeConfig>(
    () => ({
      api: getAssistantChatStreamUrl(),
      protocol: 'ui-message-stream',
      body,
      onResponse: rejectTurnRefusal,
      initialMessages,
    }),
    [initialMessages, body],
  );

  const runtime = useDataStreamRuntime(runtimeConfig);

  return (
    <AssistantRuntimeProvider runtime={runtime}>
      <TurnReattach attach={attach} parentId={parentId} attachRef={attachRef} />
      <AssistantResumeOnReturn onReload={onReload} />
      <PendingRunPoller
        threadId={threadId}
        pendingWatches={pendingWatches}
        parentId={parentId}
        attachRef={attachRef}
        onWatchCount={onWatchCount}
        onReload={onReload}
      />
      {children}
    </AssistantRuntimeProvider>
  );
}

/**
 * Re-attaches a reopened conversation to the turn the server still owns — the client half of T-022.
 *
 * A phone screen switching off drops the connection, but the turn keeps running server-side, and its
 * reply would otherwise be lost to the panel until a reload. When `AssistantProvider`'s turn-state
 * probe says the thread's turn is `running` or `awaiting_approval`, this subscribes the runtime to
 * it by starting a run whose request body is `{threadId, attach: true}` (see `AssistantAttachBody`).
 * The server replays the turn from the start and streams the rest, so the in-progress reply — and,
 * for `awaiting_approval`, the permission card — reappears exactly as it does on a live turn.
 *
 * It appends nothing and starts no second turn, and it never carries the user's message. A thread
 * whose turn has FINISHED never reaches here (`attach` is false): its reply is already in the stored
 * history, and attaching would start an empty second stream over it (Review Focus 11). A thread that
 * is idle, or whose turn this process has forgotten, is likewise read from history.
 */
function TurnReattach({
  attach,
  parentId,
  attachRef,
}: {
  attach: boolean;
  parentId: string | null;
  attachRef: MutableRefObject<boolean>;
}) {
  const aui = useAui();
  const started = useRef(false);

  useEffect(() => {
    if (!attach || started.current) return;
    started.current = true;
    // Read on the body callback's way out: it is what turns this run into an attach rather than a
    // new turn. Set immediately before the run so no other run can observe it.
    attachRef.current = true;
    aui.thread().startRun({ parentId });
  }, [attach, parentId, aui, attachRef]);

  return null;
}

/** How often a thread with a pending run is asked whether the server has started answering it. */
export const WATCH_POLL_MS = 5000;

/**
 * Picks up a reply the SERVER started (the assistant was waiting on a run, the run finished, and the
 * assistant answered with no browser attached). Only runs while the thread has pending runs: it
 * polls the turn endpoint, pauses while the tab is hidden (one poll when it returns), and when the
 * turn is `running` or `awaiting_approval` with no local stream it re-attaches exactly like
 * `TurnReattach` — so the reply streams in and an approval card shows.
 */
function PendingRunPoller({
  threadId,
  pendingWatches,
  parentId,
  attachRef,
  onWatchCount,
  onReload,
}: {
  threadId: string | null;
  pendingWatches: number;
  parentId: string | null;
  attachRef: MutableRefObject<boolean>;
  onWatchCount: (count: number) => void;
  onReload: () => void;
}) {
  const aui = useAui();
  const watching = threadId !== null && isServerThreadId(threadId) && pendingWatches > 0;
  const latest = useRef({ parentId, onWatchCount, onReload, count: pendingWatches });
  useEffect(() => {
    latest.current = { ...latest.current, parentId, onWatchCount, onReload };
  }, [parentId, onWatchCount, onReload]);

  useEffect(() => {
    if (!watching || threadId === null) return;
    let stopped = false;
    let inFlight = false;
    let failures = 0;
    let skipUntil = 0;
    latest.current.count = pendingWatches;

    const tick = async () => {
      if (stopped || inFlight || document.hidden || Date.now() < skipUntil) return;
      inFlight = true;
      try {
        const turn = await getTurnStatus(threadId);
        if (stopped || turn === null) return;
        failures = 0;
        const live = turn.state === 'running' || turn.state === 'awaiting_approval';
        const running = aui.thread().getState().isRunning;
        // One stream at a time: a local run (a reply being typed, or an earlier attach) owns the thread.
        if (live && !running) {
          attachRef.current = true;
          aui.thread().startRun({ parentId: latest.current.parentId });
        }
        if (typeof turn.pendingWatches === 'number') {
          const dropped = turn.pendingWatches < latest.current.count;
          latest.current.count = turn.pendingWatches;
          latest.current.onWatchCount(turn.pendingWatches);
          // A watched run finished and its reply came and went between two looks (or while the tab
          // was hidden): nothing was streamed here, so read the stored history once to show it.
          if (dropped && !live && !running) latest.current.onReload();
        }
      } catch {
        // Offline or a failed start: wait longer before the next look, never an unhandled rejection.
        failures += 1;
        skipUntil = Date.now() + Math.min(WATCH_POLL_MS * 2 ** failures, 60000);
      } finally {
        inFlight = false;
      }
    };

    const timer = setInterval(() => void tick(), WATCH_POLL_MS);
    const onVisibility = () => {
      if (!document.hidden) void tick();
    };
    document.addEventListener('visibilitychange', onVisibility);
    return () => {
      stopped = true;
      clearInterval(timer);
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, [watching, threadId, aui, attachRef, pendingWatches]);

  return null;
}

/**
 * Persisted turns as chat messages: a plain text part each, and nothing else — the thread stores
 * prose, and a proposal's structured payload only exists on the live turn that produced it.
 */
function toThreadMessages(
  turns: readonly AssistantThreadMessage[] | undefined,
): ThreadMessageLike[] {
  // A response is untrusted input: a payload without the turns it promised restores an empty
  // conversation rather than taking the drawer down with it.
  return (turns ?? []).map((turn) => {
    // The stored position rides along so the panel can tell which replies it is actually showing.
    const message: ThreadMessageLike = {
      id: turn.id,
      role: toThreadRole(turn.role),
      content: [{ type: 'text', text: turn.content }],
      metadata: { custom: { sequence: turn.sequence } },
    };
    // A run-finished note stays in the history the model replays, unchanged; the metadata is only
    // what lets the panel draw it as an event row instead of something the person said.
    if (turn.kind === 'run_wake' && turn.wake) {
      return { ...message, metadata: { custom: { sequence: turn.sequence, runWake: turn.wake } } };
    }
    return message;
  });
}

/**
 * `user`/`system` pass through; everything else — `assistant`, and the `tool` rows the server may
 * keep — is displayed as assistant prose rather than dropped, since a stored turn the UI silently
 * hid would make the restored conversation disagree with the model's own.
 */
function toThreadRole(role: string): ThreadMessageLike['role'] {
  if (role === 'user' || role === 'system') return role;
  return 'assistant';
}

