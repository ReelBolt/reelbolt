// @vitest-environment jsdom

/**
 * THE RE-ATTACH CONTRACT (Review Focus 11): a conversation reopened after the connection dropped
 * must pick up the turn the server still owns, or read the reply that already finished — and it
 * must NEVER start a second turn or re-send the user's message.
 *
 * WHY THIS FILE EXISTS
 *
 * A phone screen switching off drops the SSE connection. T-022 made the turn outlive the connection
 * (a process-wide hub owns it; the request is only a subscriber) and added the two endpoints the
 * client half needs:
 *
 *   GET  /api/v1/assistant/threads/{threadId}/turn  -> { state, turnId?, messageId?, pendingApprovalId? }
 *   POST /api/v1/assistant/chat  { threadId, attach: true }  -> text/event-stream, same frames as a
 *                                                     new turn, replayed then live, appends nothing
 *
 * The defect this pins is the client half: reopening a thread used to ignore the live turn (the
 * reply was lost until a reload) and, if it were driven naively through the ordinary turn path, it
 * would re-post the user's message and start a SECOND turn. Review Focus 11 names the worst case —
 * a turn that has ALREADY finished: the client must read the finished reply from the stored history
 * rather than start a second (empty) turn over it.
 *
 * HOW IT IS DRIVEN
 *
 * Through the REAL stack — `AssistantProvider → AssistantChatPanel` — with a stubbed `fetch`, the
 * way `AssistantUnansweredThreads.test.tsx` and `AssistantActionCards.integration.test.tsx` do. The
 * observable is what the transport PUTS ON THE WIRE and what the panel SHOWS: the three states the
 * server can report (a live turn, a finished turn, an idle/forgotten thread) each drive one of the
 * three acceptance cases below.
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import type { AssistantThreadDetail, AssistantTurnStatus } from '@/lib/types/assistant';
import { AssistantProvider } from './AssistantProvider';
import { AssistantChatPanel } from './AssistantChatPanel';

class MockResizeObserver implements ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

const THREAD_ID = '0f0f0f0f-1111-2222-3333-444444444444';
const USER_MESSAGE = { id: 'm1', role: 'user', content: 'Make me a reel', sequence: 1, createdAt: '' };
const STORED_REPLY = { id: 'm2', role: 'assistant', content: 'The reel is ready.', sequence: 2, createdAt: '' };

const encoder = new TextEncoder();
const frame = (payload: unknown) => encoder.encode(`data: ${JSON.stringify(payload)}\n\n`);
const action = (data: Record<string, unknown>) => frame({ type: 'data-assistantAction', data });

const PENDING = {
  id: 'act-1',
  state: 'pending',
  mode: 'ask',
  kind: 'file',
  risk: 'destructive',
  title: 'Delete the file “notes.txt”',
  summary: 'It will be removed from this project.',
  changes: [{ kind: 'remove', text: 'notes.txt' }],
};

const SSE_HEADERS = { 'content-type': 'text/event-stream', 'x-vercel-ai-ui-message-stream': 'v1' };

/** The replayed/live frames of a turn that answers, in the same shape a fresh turn would stream. */
const REPLY_SSE = [
  'data: {"type":"start","messageId":"m2"}',
  '',
  'data: {"type":"text-start","id":"t1"}',
  '',
  'data: {"type":"text-delta","textDelta":"Here is your reel plan."}',
  '',
  'data: {"type":"text-end"}',
  '',
  'data: {"type":"finish","finishReason":"stop","usage":{"inputTokens":0,"outputTokens":0}}',
  '',
  'data: [DONE]',
  '',
  '',
].join('\n');

/**
 * The same reply, but the replay the hub sent was CAPPED: T-022 puts a `data-turnTruncated` frame
 * first so the client knows it is seeing only the tail of a long turn.
 */
const TRUNCATED_REPLY_SSE =
  'data: {"type":"data-turnTruncated","data":{"truncated":true,"note":"partial replay"}}\n\n' +
  REPLY_SSE;

interface Recorded {
  url: string;
  method: string;
  body: string | null;
}

let calls: Recorded[] = [];
/** The stored history `GET …/threads/{id}` answers with. */
let detail: AssistantThreadDetail;
/** The state `GET …/threads/{id}/turn` answers with — a status, or the HTTP code it refuses with. */
let turn: AssistantTurnStatus | number;
/** What the attach POST answers. Defaults to a completed reply; a test may hold a stream open. */
let chatResponse: () => Response;

function json(payload: unknown, status = 200): Response {
  return new Response(JSON.stringify(payload), { status, headers: { 'content-type': 'application/json' } });
}

function installFetch() {
  calls = [];
  chatResponse = () => new Response(REPLY_SSE, { status: 200, headers: SSE_HEADERS });

  global.fetch = vi.fn<typeof fetch>((input, init) => {
    const url = String(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    calls.push({ url, method, body: typeof init?.body === 'string' ? init.body : null });

    if (url.includes('/api/v1/assistant/chat')) return Promise.resolve(chatResponse());
    if (/\/api\/v1\/assistant\/threads\/[^/]+\/turn$/.test(url)) {
      return Promise.resolve(typeof turn === 'number' ? json({ error: 'no turn' }, turn) : json(turn));
    }
    if (/\/api\/v1\/assistant\/threads\/[^/]+$/.test(url)) return Promise.resolve(json(detail));
    return Promise.resolve(json({}));
  }) as unknown as typeof fetch;
}

/** The attach POSTs the stack issued, parsed. */
const chatBodies = (): Array<Record<string, unknown>> =>
  calls
    .filter((call) => call.url.includes('/api/v1/assistant/chat'))
    .map((call) => JSON.parse(call.body ?? '{}') as Record<string, unknown>);

/** A chat response whose stream stays open, so a permission card can be held `pending`. */
function holdChatStreamOpen(): { push(chunk: Uint8Array): void; close(): void } {
  let controller!: ReadableStreamDefaultController<Uint8Array>;
  const body = new ReadableStream<Uint8Array>({
    start(c) {
      controller = c;
    },
  });
  chatResponse = () => new Response(body, { status: 200, headers: SSE_HEADERS });
  return { push: (chunk) => controller.enqueue(chunk), close: () => controller.close() };
}

function renderPanel() {
  return render(
    <MantineProvider env="test">
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <AssistantProvider threadId={THREAD_ID} context={{}}>
          <AssistantChatPanel />
        </AssistantProvider>
      </SWRConfig>
    </MantineProvider>,
  );
}

beforeEach(() => {
  global.ResizeObserver = MockResizeObserver;
  global.matchMedia = ((query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => true,
  })) as unknown as typeof window.matchMedia;
  Element.prototype.scrollTo = vi.fn();

  detail = { id: THREAD_ID, title: 'Make me a reel', projectId: null, messages: [USER_MESSAGE] };
  turn = { state: 'idle' };
  installFetch();
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe('reopening a conversation re-attaches to the turn the server still owns', () => {
  it('a RUNNING turn attaches and streams it, and never re-sends the user’s message', async () => {
    detail = { ...detail, replyInProgress: true };
    turn = { state: 'running', turnId: 'turn-1', messageId: 'm2' };

    renderPanel();

    // The replayed reply reaches the panel.
    await screen.findByText('Here is your reel plan.');

    const bodies = chatBodies();
    expect(bodies).toHaveLength(1);
    expect(bodies[0].attach).toBe(true);
    expect(bodies[0].threadId).toBe(THREAD_ID);
    // The question is NOT re-sent: the attach carries no message at all.
    expect(bodies[0].messages).toEqual([]);
    // ...and it is not duplicated on screen either — the stored user turn is the only one.
    expect(screen.getAllByText('Make me a reel')).toHaveLength(1);
    // A whole replay says nothing about trimming.
    expect(screen.queryByTestId('assistant-turn-truncated')).toBeNull();
  });

  it('a CAPPED replay (data-turnTruncated first) says the earlier output was trimmed', async () => {
    detail = { ...detail, replyInProgress: true };
    turn = { state: 'running', turnId: 'turn-1', messageId: 'm2' };
    // The hub capped this turn's frame log, so the replay opens with the marker frame.
    chatResponse = () => new Response(TRUNCATED_REPLY_SSE, { status: 200, headers: SSE_HEADERS });

    renderPanel();

    await screen.findByText('Here is your reel plan.');
    const note = await screen.findByTestId('assistant-turn-truncated');
    expect(note.textContent).toMatch(/trimmed/i);
  });

  it('a FINISHED turn reads the stored reply — no second turn, no empty stream (Review Focus 11)', async () => {
    detail = { ...detail, messages: [USER_MESSAGE, STORED_REPLY] };
    turn = { state: 'completed', messageId: 'm2' };

    const { container } = renderPanel();

    await screen.findByText('The reel is ready.');
    // The worst case: attaching to a finished turn would start a second, empty turn over the reply.
    expect(chatBodies()).toEqual([]);
    // Exactly one assistant message is drawn, and it carries the stored reply — no empty bubble.
    const assistantRows = container.querySelectorAll('[data-role="assistant"]');
    expect(assistantRows).toHaveLength(1);
    expect(assistantRows[0].textContent).toContain('The reel is ready.');
    expect(screen.queryByTestId('assistant-reply-in-progress')).toBeNull();
  });

  it('an IDLE thread behaves exactly as today — no attach, no second turn', async () => {
    turn = { state: 'idle' };

    renderPanel();

    await screen.findByText('Make me a reel');
    expect(chatBodies()).toEqual([]);
    // Today's answer for a stored user turn with nothing behind it: the "No reply yet" notice.
    expect(await screen.findByTestId('assistant-no-reply')).toBeTruthy();
  });

  it('a 404 from the state endpoint falls back to the stored history, not to an error state', async () => {
    // A foreign thread, or a hub that forgot the turn after a process restart (T-022: turns are
    // process-local, so this is reachable in normal operation).
    detail = { ...detail, messages: [USER_MESSAGE, STORED_REPLY] };
    turn = 404;

    renderPanel();

    await screen.findByText('The reel is ready.');
    expect(chatBodies()).toEqual([]);
    expect(screen.queryByTestId('assistant-thread-error')).toBeNull();
    expect(screen.queryByTestId('assistant-reply-in-progress')).toBeNull();
  });

  it('an AWAITING_APPROVAL turn re-attaches and shows the same permission card', async () => {
    detail = { ...detail, replyInProgress: true };
    turn = { state: 'awaiting_approval', turnId: 'turn-1', messageId: 'm2', pendingApprovalId: 'act-1' };
    const stream = holdChatStreamOpen();

    renderPanel();

    await waitFor(() => expect(chatBodies()).toHaveLength(1));
    expect(chatBodies()[0].attach).toBe(true);

    // The replayed frames bring the card back, still pending, with the same arguments.
    await act(async () => {
      stream.push(frame({ type: 'start', messageId: 'm2' }));
      stream.push(action(PENDING));
    });

    const card = await screen.findByRole('group', { name: 'Delete the file “notes.txt”' });
    expect(card.textContent).toContain('notes.txt');
    expect(card.getAttribute('data-state')).toBe('pending');
  });
});

describe('a screen that lost its stream while a card was waiting comes back to it', () => {
  it('re-attaches when the page returns, shows the same card pending, and never calls it timed out', async () => {
    detail = { ...detail, replyInProgress: true };
    turn = { state: 'awaiting_approval', turnId: 'turn-1', messageId: 'm2', pendingApprovalId: 'act-1' };
    const first = holdChatStreamOpen();

    renderPanel();
    await waitFor(() => expect(chatBodies()).toHaveLength(1));
    await act(async () => {
      first.push(frame({ type: 'start', messageId: 'm2' }));
      first.push(action(PENDING));
    });
    await screen.findByRole('button', { name: 'Allow' });

    // The phone switches screens: the stream ends with the card still waiting (closed with [DONE], as a proxy
    // that ends the response cleanly would; an abrupt end makes the runtime reject unhandled, which the
    // suite rightly fails on). This screen can no longer answer it and must say the connection dropped —
    // not that the card timed out.
    const second = holdChatStreamOpen();
    await act(async () => {
      first.push(encoder.encode('data: [DONE]\n\n'));
      first.close();
    });
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Allow' })).toBeNull());
    expect(screen.getByTestId('assistant-action').textContent).toContain('The connection dropped');
    expect(screen.getByTestId('assistant-action').textContent).not.toMatch(/timed out|ran out of time/i);

    // The page comes back to the front: the server still holds the turn, so the screen re-attaches
    // (a second attach POST, still without the user's message) and the same card is answerable again.
    await act(async () => {
      window.dispatchEvent(new Event('pageshow'));
    });
    await waitFor(() => expect(chatBodies()).toHaveLength(2), { timeout: 4000 });
    expect(chatBodies()[1].attach).toBe(true);
    expect(chatBodies()[1].messages).toEqual([]);

    await act(async () => {
      second.push(frame({ type: 'start', messageId: 'm2' }));
      second.push(action(PENDING));
    });
    const card = await screen.findByRole('group', { name: 'Delete the file “notes.txt”' });
    expect(card.getAttribute('data-state')).toBe('pending');
    expect(await screen.findByRole('button', { name: 'Allow' })).toBeTruthy();
  });
});
