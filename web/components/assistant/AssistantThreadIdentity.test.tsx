// @vitest-environment jsdom

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { useEffect } from 'react';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';

const mocks = vi.hoisted(() => ({ uploadFile: vi.fn() }));

// The attach control's own upload, stubbed rather than exercised: it is the step BEFORE its send,
// and no test may need the backend to reach the send.
vi.mock('@/lib/api/files', () => ({ uploadFile: mocks.uploadFile }));

import { AssistantShellProvider, useAssistant } from './AssistantShellProvider';
import { AssistantTrigger } from './AssistantTrigger';
import { ATTACH_LABEL } from './AssistantAttachControl';
import type { AssistantThreadSummary } from '../../lib/types/assistant';

/**
 * THE LOCAL THREAD-IDENTITY CONTRACT: no chat request is issued while the shell holds no bound
 * thread.
 *
 * WHY THIS FILE EXISTS
 *
 * `AssistantProvider.threadIdBody.test.tsx` and `e2e/tests/assistant-seam.contract.spec.ts` both pin
 * the request BODY — the former that an unbound turn carries no `threadId` key, the latter that the
 * real stack's body deep-equals the shared fixture. Both stayed green through the round in which
 * this defect shipped, and that is not an accident: they pin what the client puts ON the wire, and
 * the defect was upstream of the wire. The server allocates a thread for any body that omits
 * `threadId`, so a body that is CORRECT BY CONSTRUCTION still lands the turn on a thread the client
 * never learns about.
 *
 * QA demonstrated the consequence live:
 *
 *   [A] client's createThread  -> thread A = 430b7428-…
 *   [B] pre-thread turn        -> HTTP 200, server allocated B = f3726c14-…
 *   [C] messages in A = 0   (what the client addresses from turn 2 on)
 *   [C] messages in B = 1   (where turn 1 landed, orphaned)   thread rows = 2
 *
 * From turn 2 on the model's history comes from thread A, which is empty — so the model has no
 * memory of turn 1 while the user still sees it on screen.
 *
 * WHAT IS PINNED HERE, AND WHY IT IS A DIFFERENT CONTRACT
 *
 * The invariant is about LOCAL identity, not about the body: the shipped UI must never put the user
 * in the pre-thread window at all. Two triggers reached it, and both are exercised below:
 *
 *   1. the composer was not disabled while `POST /assistant/threads` was in flight, so a fast user
 *      sent a turn inside that window; and
 *   2. a FAILED `createThread` was swallowed (`AssistantDrawer.tsx`'s
 *      `.catch(() => { createdForVisitRef.current = false; })`) and the effect never re-ran, so
 *      `threadId` stayed `null` for the WHOLE visit and EVERY turn allocated a fresh thread — a
 *      silently stateless assistant, with no error shown and no way back.
 *
 * HOW IT IS DRIVEN
 *
 * Through the REAL stack — `AssistantShellProvider → AssistantDrawer → AssistantProvider →
 * AssistantChatPanel` — with a stubbed `fetch`, exactly the way `assistantSeamCapture.tsx` drives
 * it and exactly the way QA reproduced the defect. A guard on any single component would not have
 * seen the wiring that produced it.
 *
 * The send attempts are deliberately four different gestures, because "the button looks grey" is
 * not the invariant: the disabled Send control, a disabled suggestion chip, a raw `Enter` keydown on
 * the composer input (the primitive's own keyboard-submit path), and a raw `submit` event on the
 * composer's `<form>` (the primitive's `onSubmit` path, which calls `send()` regardless of any
 * button's state). All four must be dead while no thread is bound.
 */

/** The thread the stubbed `POST /assistant/threads` hands back once it is allowed to succeed. */
const THREAD_ID = '7c9f0d2a-4b13-4e77-9a51-0e6d3f8b21c4';

/** The page context the assistant is opened from — a real GUID so the request body is realistic. */
const PROJECT_ID = '11111111-1111-1111-1111-111111111111';

/** What the stubbed `POST /assistant/threads` does on the nth ATTEMPT (index 0 is the first). */
type ThreadAttempt = 'pending' | 'ok' | 'fail';

interface FetchControl {
  /** Every body the stack posted to the chat endpoint. The invariant is that this stays empty. */
  chatBodies: Record<string, unknown>[];
  /** How many times `POST /assistant/threads` was attempted. */
  threadAttempts: number;
  /**
   * Every thread the stack has asked the server to restore history for, in order.
   *
   * This is the BINDING made observable, and it is what the send precondition is synchronised on —
   * see `waitForBound`.
   */
  getThreads: string[];
}

const sseResponse = () =>
  new Response(
    [
      'data: {"type":"start","messageId":"m1"}',
      '',
      'data: {"type":"text-start","id":"t1"}',
      '',
      'data: {"type":"text-delta","textDelta":"ok"}',
      '',
      'data: {"type":"text-end"}',
      '',
      'data: {"type":"finish","finishReason":"stop","usage":{"inputTokens":0,"outputTokens":0}}',
      '',
      'data: [DONE]',
      '',
      '',
    ].join('\n'),
    {
      status: 200,
      headers: { 'content-type': 'text/event-stream', 'x-vercel-ai-ui-message-stream': 'v1' },
    },
  );

const jsonResponse = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });

/**
 * Stubs `fetch` for the whole stack and returns what went out. The thread-creation endpoint follows
 * `plan` attempt by attempt; everything past its end stays pending, so a scenario that runs out of
 * plan cannot silently start succeeding.
 */
function installFetch(plan: readonly ThreadAttempt[]): FetchControl {
  const control: FetchControl = { chatBodies: [], threadAttempts: 0, getThreads: [] };

  globalThis.fetch = ((url: string | URL | Request, init?: RequestInit) => {
    const href = String(url);
    const method = (init?.method ?? 'GET').toUpperCase();

    if (href.endsWith('/api/v1/assistant/threads') && method === 'POST') {
      const attempt = plan[control.threadAttempts] ?? 'pending';
      control.threadAttempts += 1;

      if (attempt === 'pending') return new Promise<Response>(() => {});
      if (attempt === 'fail') {
        return Promise.resolve(jsonResponse({ message: 'The server is busy.' }, 503));
      }
      return Promise.resolve(jsonResponse({ id: THREAD_ID }));
    }

    // `getThread` — the history load that follows a successful bind.
    if (href.includes('/api/v1/assistant/threads/')) {
      control.getThreads.push(threadIdIn(href));
      return Promise.resolve(
        jsonResponse({ id: THREAD_ID, title: 'New chat', projectId: PROJECT_ID, messages: [] }),
      );
    }

    if (href.includes('/api/v1/assistant/chat')) {
      control.chatBodies.push(JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>);
      return Promise.resolve(sseResponse());
    }

    return Promise.resolve(jsonResponse({}));
  }) as typeof fetch;

  return control;
}

class MockResizeObserver implements ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

/** Stands in for a page: puts the assistant's per-call project context into the shell. */
function PageContext() {
  const { setContext } = useAssistant();
  useEffect(() => {
    setContext({ projectId: PROJECT_ID });
  }, [setContext]);
  return null;
}

/** The real stack, driven through the real DOM. */
function renderStack() {
  return render(
    <MantineProvider>
      <AssistantShellProvider>
        <PageContext />
        <AssistantTrigger />
      </AssistantShellProvider>
    </MantineProvider>,
  );
}

async function openAssistant(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole('button', { name: /open assistant/i }));
  return screen.findByRole('textbox', { name: /message the workflow assistant/i });
}

/** The thread a `GET /api/v1/assistant/threads/{id}` addresses. */
function threadIdIn(href: string): string {
  return href.slice(href.lastIndexOf('/') + 1);
}

/**
 * Waits until the shell's selection is bound FOR THE TRANSPORT — not until the composer merely looks
 * ready.
 *
 * A test must synchronise on the observation the code under test reads, and the request body does
 * not read the DOM: it reads `AssistantRuntimeHost`'s `contextRef`, which is written by an EFFECT.
 * So "the textarea lost its `disabled` attribute" and "the next turn will name the thread" are two
 * different observations of one state, and waiting on the first is only waiting on them COINCIDING.
 * They do coincide here — measured, with a temporary probe: at the moment `waitFor(disabled ===
 * false)` resolves, `contextRef` is already written and `getThread` has already gone out — but that
 * is a fact about React's passive-effect flush landing inside the macrotask `waitFor` drains before
 * resolving. It is an implementation detail of the scheduler, not a guarantee the code offers, and
 * the two failure signatures this file was re-opened for (`__LOCALID_…` reaching the wire on a bound
 * shell, and a turn going out on an unbound one) are precisely what a broken version of that
 * coincidence looks like. A guard may not rest on an accident that currently holds.
 *
 * The history load for `id` having gone out is PROOF instead, and it is a proof from ORDER rather
 * than from timing: `AssistantProvider`'s history effect and `AssistantRuntimeHost`'s `contextRef`
 * effect are both dirty in the commit that changes `threadId`, and React flushes one commit's
 * passive effects children-first — so by the time the PARENT's `getThread` reaches `fetch`, the
 * child's ref has already been updated. The wait therefore cannot resolve early.
 */
async function waitForBound(
  control: { getThreads: string[] },
  threadId: string,
): Promise<void> {
  await waitFor(() => expect(control.getThreads).toContain(threadId));
}

/**
 * Every gesture that could send a turn, none of which may produce a chat request while the shell
 * holds no bound thread. `input` is the composer's textarea.
 *
 * The text is put in FIRST, and put in on purpose: a `change` event dispatched directly reaches the
 * primitive's handler even on a control the DOM would refuse to type into, so every gesture below
 * is a genuine attempt that WOULD send if its guard were missing, rather than a no-op against an
 * empty composer. An attempt that cannot fire proves nothing.
 */
async function attemptEverySend(
  user: ReturnType<typeof userEvent.setup>,
  input: HTMLElement,
): Promise<void> {
  fireEvent.change(input, { target: { value: 'hello' } });

  // 1. The Send control, clicked the way a user clicks it.
  const send = screen.queryByRole('button', { name: /^send$/i });
  if (send) await user.click(send);

  // 2/3. The empty state's suggestion chips — a send path of their own that needs no typed text at
  // all, and the likeliest way into the pre-thread window, since the empty state IS that window.
  for (const suggestion of screen.queryAllByRole('button', { name: /explain this workflow/i })) {
    if (!(suggestion as HTMLButtonElement).disabled) await user.click(suggestion);
  }

  // 4. The primitive's own keyboard-submit path, dispatched directly: on an enabled input this
  // calls `requestSubmit()` on the composer's form, which sends.
  fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' });

  // 5. The composer's `<form>` submit, which the primitive answers with `send()` on its own. A
  // guard that only greys buttons leaves this open.
  const form = input.closest('form');
  if (form) fireEvent.submit(form);

  // 6. The attach control. An attachment is not passive: once the upload lands it composes a turn
  // and sends it, which makes it a send path like the rest — and the one that would stay open for
  // a WHOLE visit after a failed `createThread`, since the file picker needs no thread to open.
  const attach = screen.queryByLabelText(ATTACH_LABEL);
  if (attach) {
    fireEvent.change(attach, {
      target: { files: [new File(['hello'], 'brief.md', { type: 'text/markdown' })] },
    });
  }
}

/**
 * Lets every attempt's asynchronous tail finish before the negative is asserted. The attach path
 * uploads FIRST and sends one microtask later, so asserting the moment the gestures return would
 * race it and could report an empty request list for a run that had in fact sent.
 */
async function settleAttempts(): Promise<void> {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 0));
  });
}

describe('assistant thread identity — no turn without a bound thread', () => {
  beforeEach(() => {
    global.ResizeObserver = MockResizeObserver;
    global.matchMedia = (query: string): MediaQueryList =>
      ({
        matches: false,
        media: query,
        onchange: null,
        addListener: () => {},
        removeListener: () => {},
        addEventListener: () => {},
        removeEventListener: () => {},
        dispatchEvent: () => true,
      }) as MediaQueryList;
    Element.prototype.scrollTo = vi.fn();
    mocks.uploadFile.mockReset();
    // Resolved so that the ONLY thing standing between an attach gesture and a sent turn is the
    // composer's own gate: a rejection here would make the attach attempts pass for the wrong
    // reason.
    mocks.uploadFile.mockResolvedValue({ id: '9f8e7d6c-5b4a-4938-8271-60594a3b2c1d' });
  });

  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  it('sends NOTHING while POST /assistant/threads is still in flight, and says so', async () => {
    const control = installFetch(['pending']);
    const user = userEvent.setup();
    renderStack();

    const input = await openAssistant(user);
    await waitFor(() => expect(control.threadAttempts).toBe(1));

    await attemptEverySend(user, input);
    await settleAttempts();

    // The invariant. Anything here is a turn the server would allocate its own thread for.
    expect(
      control.chatBodies,
      'A chat turn went out while the shell held no bound thread. The server allocates a thread ' +
        'for a body that omits `threadId`, so this turn lands on a thread the client never learns ' +
        'about: every later turn addresses the empty one, and the model silently loses this turn ' +
        'while the user still sees it on screen.',
    ).toEqual([]);
    // The attach path uploads before it sends, so it is checked both ways: nothing reached the
    // upload either, which is the guard that actually stops it.
    expect(mocks.uploadFile).not.toHaveBeenCalled();

    // ...and the attempt was HANDLED, not silently dropped: the composer is inert and the drawer
    // says why, in a live region, rather than leaving a dead input with no explanation.
    expect((input as HTMLTextAreaElement).disabled).toBe(true);
    const pending = screen.getByTestId('assistant-thread-pending');
    expect(pending.getAttribute('role')).toBe('status');
    expect(pending.textContent ?? '').toMatch(/conversation/i);
  });

  it('surfaces a failed thread creation, and a retry puts the assistant back in business', async () => {
    const control = installFetch(['fail', 'ok']);
    const user = userEvent.setup();
    renderStack();

    await openAssistant(user);
    await waitFor(() => expect(control.threadAttempts).toBe(1));

    // The failure is SHOWN. Swallowing it is what made the assistant silently stateless for a
    // whole visit: `threadId` stayed null, the effect never re-ran, and every turn allocated a
    // fresh thread with no error anywhere the user could see.
    const alert = await screen.findByTestId('assistant-thread-error');
    expect(alert.getAttribute('role')).toBe('alert');
    expect(alert.textContent ?? '').toMatch(/busy/i);

    // And it is RECOVERABLE, without closing and reopening the drawer.
    await user.click(screen.getByRole('button', { name: /try again/i }));
    await waitFor(() => expect(control.threadAttempts).toBe(2));

    // The retry must land on the transport's precondition too, not merely on the composer's
    // affordance — this is the path that stays open for a whole visit when it is missed.
    await waitForBound(control, THREAD_ID);
    await waitFor(() =>
      expect(
        (screen.getByRole('textbox', { name: /message the workflow assistant/i }) as HTMLTextAreaElement)
          .disabled,
      ).toBe(false),
    );

    const repaired = screen.getByRole('textbox', { name: /message the workflow assistant/i });
    await user.type(repaired, 'are you there?');
    await user.keyboard('{Enter}');

    await waitFor(() => expect(control.chatBodies.length).toBe(1));
    // The recovered thread is the one the turn must be written to, not merely *a* thread.
    expect(control.chatBodies[0].threadId).toBe(THREAD_ID);
    expect(screen.queryByTestId('assistant-thread-error')).toBeNull();
  });

  it('sends the turn WITH the bound thread once the thread exists', async () => {
    const control = installFetch(['ok']);
    const user = userEvent.setup();
    renderStack();

    const input = await openAssistant(user);
    await waitFor(() => expect(control.threadAttempts).toBe(1));
    // Synchronised on the BINDING, not on the composer's `disabled` attribute: the attribute is a
    // second read of the same state (see `waitForBound`), and the assertion below is about what the
    // request body — which reads neither the DOM nor the attribute — will contain.
    await waitForBound(control, THREAD_ID);
    expect((input as HTMLTextAreaElement).disabled).toBe(false);

    await user.type(input, 'hello');
    await user.keyboard('{Enter}');

    await waitFor(() => expect(control.chatBodies.length).toBe(1));
    expect(
      control.chatBodies[0].threadId,
      'The turn must name the thread the shell holds. Omitting it makes the server allocate a ' +
        'second thread and orphan the turn — the exact defect this file exists to prevent.',
    ).toBe(THREAD_ID);
    expect(screen.queryByTestId('assistant-thread-pending')).toBeNull();
    expect(screen.queryByTestId('assistant-thread-error')).toBeNull();
  });
});

/**
 * THE SELECTION-OWNERSHIP CONTRACT: an explicit user selection is never replaced by a background
 * `createThread` that was already in flight.
 *
 * `threadId` has more than one writer. Two of them are the user speaking:
 *
 *   * `AssistantThreadList`'s thread row (`setThreadId(thread.id)`), and
 *   * `AssistantThreadList`'s "New chat" (`setThreadId(id)` after its own create),
 *
 * and one is the drawer's visit-create effect, which opens a thread on the way into the assistant so
 * that the first turn has somewhere to be persisted to. The visit-create is a FALLBACK: it exists
 * only because nothing was bound when it started. When it resolves it used to write its result
 * unconditionally, so a create still in flight silently overwrote whatever the user had chosen in
 * the meantime.
 *
 * QA demonstrated that live, twice:
 *
 *   QA-PROBE E   user picks conversation bbbb…; after the visit-create resolves the last turn goes
 *                out with threadId="aaaa…" — B's history is on screen and the turn is written to A.
 *   QA-PROBE F   thread creations ["visit","newchat"]; the turn goes out with threadId="aaaa…"
 *                even though the user asked for cccc… — reachable in ONE click, because "New chat"
 *                sits in the drawer's always-visible header, outside the `Collapse`.
 *
 * That is silent data misplacement, not a cosmetic glitch: the user reads one conversation and
 * their next message is filed under a different, empty one.
 *
 * THE THIRD WRITER, AND WHY THE FIRST TWO DID NOT COVER IT
 *
 * E and F both pin the VISIT-CREATE as the overwriter, and the conditional write above closes both.
 * Neither of them is about the other asynchronous writer: "New chat" is itself an `await
 * createThread(...)` followed by an unconditional `setThreadId(id)`, so a "New chat" the user
 * clicked and then thought better of lands AFTER the conversation they went on to pick and replaces
 * it. QA isolated that writer exactly:
 *
 *   QA-PROBE J   history panel already open → "New chat" (create in flight) → the user clicks the
 *                conversation they want. Its history loads, and THEN the create lands:
 *                turnThreadId="cccc…", userAskedFor="bbbb…" — the pick is overwritten.
 *   QA-PROBE J2  the same, with the visit-create held pending FOREVER and only "New chat"
 *                released → still "cccc…" ⇒ `startNewChat` alone is the overwriter, and the code
 *                the earlier round touched is not involved.
 *   QA-PROBE J3  control: wait for "New chat" to resolve, then pick → the user wins. Order-dependent,
 *                not a bad harness.
 *
 * A conditional write cannot close this one, because at the moment "New chat" LANDS it has no way to
 * tell "nothing has been selected since I started" from "the user has since picked something else" —
 * it only ever sees the current value, and the current value is non-null in both cases. Closing it
 * takes a token captured BEFORE the request and compared AT landing.
 *
 * WHAT IS PINNED HERE
 *
 * Not which id the shell happens to hold, but which id the NEXT TURN NAMES — the same observable
 * the rest of this file uses, and the only one that matters: it is what the server persists to.
 * Every scenario is driven through the real stack with a `fetch` stub, and in every one an
 * asynchronous writer is still in flight when the user acts, then resolves with a DIFFERENT thread.
 */

/** The thread the visit's own create hands back — the one that must NOT win. */
const VISIT_THREAD_ID = 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa';

/** The conversation the user picks out of history — the one that must win. */
const PICKED_THREAD_ID = 'bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb';

/** The thread the user's own "New chat" creates — the one that must win there. */
const NEW_CHAT_THREAD_ID = 'cccccccc-3333-4333-8333-cccccccccccc';

const PICKED_THREAD: AssistantThreadSummary = {
  id: PICKED_THREAD_ID,
  title: 'Release checklist',
  projectId: PROJECT_ID,
  updatedAt: '2026-01-02T03:04:05Z',
};

interface OwnershipControl {
  /** Every body the stack posted to the chat endpoint. */
  chatBodies: Record<string, unknown>[];
  /** Every thread the stack asked the server to restore history for, in order. */
  getThreads: string[];
  /** How many times the drawer's visit-create has been asked for. */
  visitCreates: number;
  /** Lets the held visit-create resolve. Deliberately manual: the race IS the subject. */
  releaseVisitCreate(): void;
}

/**
 * `fetch` for the ownership scenarios. The visit-create is HELD — it is answered by
 * `releaseVisitCreate` — so the user always acts while it is genuinely in flight. The user's own
 * "New chat" resolves immediately, which is what makes the two writers race in a fixed order, or
 * fails when `newChatFails` asks it to.
 */
function installOwnershipFetch(
  visitThreadId: string,
  options: { newChatFails?: boolean } = {},
): OwnershipControl {
  let release: (() => void) | null = null;
  const control: OwnershipControl = {
    chatBodies: [],
    getThreads: [],
    visitCreates: 0,
    releaseVisitCreate: () => {
      const resume = release;
      release = null;
      resume?.();
    },
  };

  const held = new Promise<void>((resolve) => {
    release = resolve;
  });

  globalThis.fetch = ((url: string | URL | Request, init?: RequestInit) => {
    const href = String(url);
    const method = (init?.method ?? 'GET').toUpperCase();

    if (href.endsWith('/api/v1/assistant/threads') && method === 'POST') {
      if (control.visitCreates++ === 0) {
        // The visit's own create: in flight until the test says otherwise.
        return held.then(() => jsonResponse({ id: visitThreadId }));
      }
      if (options.newChatFails) {
        return Promise.resolve(jsonResponse({ message: 'The server is busy.' }, 503));
      }
      return Promise.resolve(jsonResponse({ id: NEW_CHAT_THREAD_ID }));
    }

    if (href.endsWith('/api/v1/assistant/threads')) {
      return Promise.resolve(jsonResponse([PICKED_THREAD]));
    }

    if (href.includes('/api/v1/assistant/threads/')) {
      control.getThreads.push(threadIdIn(href));
      return Promise.resolve(
        jsonResponse({ id: null, title: 'Conversation', projectId: PROJECT_ID, messages: [] }),
      );
    }

    if (href.includes('/api/v1/assistant/chat')) {
      control.chatBodies.push(JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>);
      return Promise.resolve(sseResponse());
    }

    return Promise.resolve(jsonResponse({}));
  }) as typeof fetch;

  return control;
}

interface NewChatRaceControl {
  /** Every body the stack posted to the chat endpoint. */
  chatBodies: Record<string, unknown>[];
  /** Every thread the stack asked the server to restore history for, in order. */
  getThreads: string[];
  /** How many times the drawer's `POST /assistant/threads` has been asked for. */
  threadCreates: number;
  /** Lets the held "New chat" create land. Deliberately manual: the race IS the subject. */
  releaseNewChat(): void;
}

/**
 * `fetch` for the "New chat" race (QA-PROBE J).
 *
 * The visit-create is held pending FOREVER — it never resolves and never binds — so that the only
 * writer that can possibly overwrite the user's pick is `startNewChat`. That is what makes this a
 * guard on the third writer rather than a second copy of the E/F guard. The user's own "New chat"
 * create is held until `releaseNewChat`, so the pick is guaranteed to land first.
 */
function installNewChatRaceFetch(): NewChatRaceControl {
  let release: (() => void) | null = null;
  const control: NewChatRaceControl = {
    chatBodies: [],
    getThreads: [],
    threadCreates: 0,
    releaseNewChat: () => {
      const resume = release;
      release = null;
      resume?.();
    },
  };

  const held = new Promise<void>((resolve) => {
    release = resolve;
  });

  globalThis.fetch = ((url: string | URL | Request, init?: RequestInit) => {
    const href = String(url);
    const method = (init?.method ?? 'GET').toUpperCase();

    if (href.endsWith('/api/v1/assistant/threads') && method === 'POST') {
      // The visit's own create: never answered, so it can never be the writer under test.
      if (control.threadCreates++ === 0) return new Promise<Response>(() => {});
      // The user's "New chat": in flight until the test says otherwise.
      return held.then(() => jsonResponse({ id: NEW_CHAT_THREAD_ID }));
    }

    if (href.endsWith('/api/v1/assistant/threads')) {
      return Promise.resolve(jsonResponse([PICKED_THREAD]));
    }

    if (href.includes('/api/v1/assistant/threads/')) {
      control.getThreads.push(threadIdIn(href));
      return Promise.resolve(
        jsonResponse({ id: null, title: 'Conversation', projectId: PROJECT_ID, messages: [] }),
      );
    }

    if (href.includes('/api/v1/assistant/chat')) {
      control.chatBodies.push(JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>);
      return Promise.resolve(sseResponse());
    }

    return Promise.resolve(jsonResponse({}));
  }) as typeof fetch;

  return control;
}

/**
 * Types a turn and sends it, re-reading the composer: a rebind remounts it.
 */
async function sendTurn(user: ReturnType<typeof userEvent.setup>, text: string): Promise<void> {
  const composer = screen.getByRole('textbox', { name: /message the workflow assistant/i });
  await user.type(composer, text);
  await user.keyboard('{Enter}');
}

describe('assistant thread identity — a user selection is never replaced', () => {
  beforeEach(() => {
    global.ResizeObserver = MockResizeObserver;
    global.matchMedia = (query: string): MediaQueryList =>
      ({
        matches: false,
        media: query,
        onchange: null,
        addListener: () => {},
        removeListener: () => {},
        addEventListener: () => {},
        removeEventListener: () => {},
        dispatchEvent: () => true,
      }) as MediaQueryList;
    Element.prototype.scrollTo = vi.fn();
  });

  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  it('keeps the conversation the user picked while the visit-create was still in flight', async () => {
    const control = installOwnershipFetch(VISIT_THREAD_ID);
    const user = userEvent.setup();
    renderStack();

    await openAssistant(user);
    await waitFor(() => expect(control.visitCreates).toBe(1));

    // The user picks a conversation out of history — the visit-create is STILL in flight.
    await user.click(screen.getByRole('button', { name: 'History' }));
    await user.click(
      await screen.findByRole('button', { name: `Open conversation ${PICKED_THREAD.title}` }),
    );
    await waitForBound(control, PICKED_THREAD_ID);

    // ...and only now does the background create land, naming a different thread.
    control.releaseVisitCreate();
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });

    await sendTurn(user, 'what did we decide?');
    await waitFor(() => expect(control.chatBodies.length).toBeGreaterThan(0));
    expect(
      control.chatBodies.at(-1)?.threadId,
      'The turn must be written to the conversation the user opened. Naming the visit-create\'s ' +
        'thread instead files this message under an empty thread while the picked one stays on ' +
        'screen — silent misplacement of the user\'s own words.',
    ).toBe(PICKED_THREAD_ID);
  });

  it('keeps the conversation "New chat" created while the visit-create was still in flight', async () => {
    const control = installOwnershipFetch(VISIT_THREAD_ID);
    const user = userEvent.setup();
    renderStack();

    await openAssistant(user);
    await waitFor(() => expect(control.visitCreates).toBe(1));

    // One click: "New chat" is in the drawer header, outside the history `Collapse`.
    await user.click(screen.getByRole('button', { name: 'New chat' }));
    await waitForBound(control, NEW_CHAT_THREAD_ID);

    control.releaseVisitCreate();
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });

    await sendTurn(user, 'start here');
    await waitFor(() => expect(control.chatBodies.length).toBeGreaterThan(0));
    expect(
      control.chatBodies.at(-1)?.threadId,
      'The turn must be written to the thread "New chat" created, not to the one the visit opened ' +
        'behind the user\'s back.',
    ).toBe(NEW_CHAT_THREAD_ID);
  });

  /**
   * A CHARACTERISATION of the fallback's other half, not a guard on the J defect: it is green both
   * before and after the token was introduced. It is here because the token check now sits ON the
   * fallback's write path, and the first thing a token check can get wrong is being advanced by
   * something that claimed nothing — which would strand the visit with no thread at all.
   */
  it('falls back to the visit\'s own thread when the user\'s "New chat" fails', async () => {
    const control = installOwnershipFetch(VISIT_THREAD_ID, { newChatFails: true });
    const user = userEvent.setup();
    renderStack();

    await openAssistant(user);
    await waitFor(() => expect(control.visitCreates).toBe(1));

    // The panel is opened FIRST, because the list reports its own action failures inside it: this
    // is the surface the user actually reads the failure off, so the test waits on it rather than
    // inventing a second, test-only observation of the same state.
    await user.click(screen.getByRole('button', { name: 'History' }));

    // The user asks for a new chat, and that create FAILS. A failure binds nothing, so it must
    // claim nothing either...
    await user.click(screen.getByRole('button', { name: 'New chat' }));
    const alert = await screen.findByRole('alert');
    expect(alert.textContent ?? '').toMatch(/busy/i);

    // ...which leaves the visit-create free to fill the still-empty selection when it lands.
    control.releaseVisitCreate();
    await waitForBound(control, VISIT_THREAD_ID);

    await sendTurn(user, 'still there?');
    await waitFor(() => expect(control.chatBodies.length).toBeGreaterThan(0));
    expect(
      control.chatBodies.at(-1)?.threadId,
      'A failed "New chat" must not strand the assistant: nothing is bound, and the visit-create ' +
        'is the fallback that exists for exactly that state.',
    ).toBe(VISIT_THREAD_ID);
  });

  it('keeps the conversation the user picked while "New chat" was still in flight', async () => {
    const control = installNewChatRaceFetch();
    const user = userEvent.setup();
    renderStack();

    await openAssistant(user);
    await waitFor(() => expect(control.threadCreates).toBe(1));

    // The user asks for a new conversation. That create is in flight...
    await user.click(screen.getByRole('button', { name: 'New chat' }));
    await waitFor(() => expect(control.threadCreates).toBe(2));

    // ...and then changes their mind and opens the conversation they actually want. The pick is
    // synchronised on its own history load, exactly as the rest of this file does — and that load
    // is proof the pick reached the shell BEFORE the create below is allowed to land.
    await user.click(screen.getByRole('button', { name: 'History' }));
    await user.click(
      await screen.findByRole('button', { name: `Open conversation ${PICKED_THREAD.title}` }),
    );
    await waitForBound(control, PICKED_THREAD_ID);

    // Only NOW does "New chat"'s own create land, naming a different thread.
    control.releaseNewChat();
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });

    await sendTurn(user, 'this one, not that one');
    await waitFor(() => expect(control.chatBodies.length).toBeGreaterThan(0));
    expect(
      control.chatBodies.at(-1)?.threadId,
      'The turn must be written to the conversation the user opened LAST. A create that was still ' +
        'in flight when a later selection landed is a stale result: binding it files the message ' +
        'under a thread the user has already navigated away from, while the conversation on screen ' +
        'stays empty.',
    ).toBe(PICKED_THREAD_ID);
  });
});
