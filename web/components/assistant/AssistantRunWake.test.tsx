// @vitest-environment jsdom

/**
 * A run the assistant is waiting on: the wake row, the "I'll pick this up" line, and the poll that
 * picks up a reply the SERVER started. Driven through the real provider + panel with a stubbed fetch.
 * Only the interval timers are faked so promises and testing-library's own waits stay real.
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
const SSE_HEADERS = { 'content-type': 'text/event-stream', 'x-vercel-ai-ui-message-stream': 'v1' };
const REPLY_SSE = [
  'data: {"type":"start","messageId":"m9"}',
  '',
  'data: {"type":"text-start","id":"t1"}',
  '',
  'data: {"type":"text-delta","textDelta":"The teaser passed."}',
  '',
  'data: {"type":"text-end"}',
  '',
  'data: {"type":"finish","finishReason":"stop","usage":{"inputTokens":0,"outputTokens":0}}',
  '',
  'data: [DONE]',
  '',
  '',
].join('\n');

const USER = { id: 'm1', role: 'user', content: 'Run the teaser', sequence: 1, createdAt: '' };
const WAKE = {
  id: 'm2',
  role: 'user',
  content: '[Run update] Launch teaser finished: Passed',
  sequence: 2,
  createdAt: '',
  kind: 'run_wake' as const,
  wake: { status: 'Passed' as const, runLabel: 'Launch teaser' },
};

let detail: AssistantThreadDetail;
let turn: AssistantTurnStatus;
let turnCalls = 0;
let chatBodies: Array<Record<string, unknown>> = [];
let chatResponse: () => Response;

const json = (payload: unknown) =>
  new Response(JSON.stringify(payload), { status: 200, headers: { 'content-type': 'application/json' } });

function installFetch() {
  turnCalls = 0;
  chatBodies = [];
  chatResponse = () => new Response(REPLY_SSE, { status: 200, headers: SSE_HEADERS });
  global.fetch = vi.fn<typeof fetch>((input, init) => {
    const url = String(input);
    if (url.includes('/api/v1/assistant/chat')) {
      chatBodies.push(JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as Record<string, unknown>);
      return Promise.resolve(chatResponse());
    }
    if (/\/threads\/[^/]+\/turn$/.test(url)) {
      turnCalls += 1;
      return Promise.resolve(json(turn));
    }
    if (/\/threads\/[^/]+$/.test(url)) return Promise.resolve(json(detail));
    return Promise.resolve(json({}));
  }) as unknown as typeof fetch;
}

function setHidden(hidden: boolean) {
  Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden });
}

function renderPanel(id = THREAD_ID) {
  return render(
    <MantineProvider env="test">
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <AssistantProvider threadId={id} context={{}}>
          <AssistantChatPanel />
        </AssistantProvider>
      </SWRConfig>
    </MantineProvider>,
  );
}

/** Mounts and lets the initial history/turn loads settle, with the poll clock still stopped. */
async function mount(id?: string) {
  const view = renderPanel(id);
  await act(async () => {
    await vi.advanceTimersByTimeAsync(0);
  });
  return view;
}

const tick = (ms = 5000) =>
  act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', MockResizeObserver);
  Element.prototype.scrollTo = vi.fn();
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
  vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
  setHidden(false);
  detail = { id: THREAD_ID, title: 't', projectId: null, messages: [USER, WAKE] };
  turn = { state: 'idle' };
  installFetch();
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  setHidden(false);
});

describe('the run-finished row', () => {
  it('is a neutral event row with the outcome in words, not a user bubble', async () => {
    await mount();
    const row = await screen.findByText(/Launch teaser finished: Passed/);
    expect(row.closest('[data-role="run-wake"]')).not.toBeNull();
    expect(document.querySelector('[data-role="user"]')?.textContent).not.toContain('[Run update]');
    expect(screen.queryByText(/\[Run update\]/)).toBeNull();
  });

  it('says Failed and Cancelled in text too', async () => {
    detail.messages = [USER, { ...WAKE, wake: { status: 'Failed', runLabel: 'Promo' } }];
    await mount();
    expect(await screen.findByText(/Promo finished: Failed/)).toBeTruthy();
  });
});

describe('the pending-run line', () => {
  it('names the run when there is one', async () => {
    detail.pendingRunWatches = [{ runLabel: 'Launch teaser', createdAt: '' }];
    await mount();
    expect((await screen.findByTestId('assistant-pending-watch')).textContent).toBe(
      "I'll pick this up when 'Launch teaser' finishes - you can close this window.",
    );
  });

  it('counts the runs when there are several', async () => {
    detail.pendingRunWatches = [
      { runLabel: 'A', createdAt: '' },
      { runLabel: 'B', createdAt: '' },
    ];
    await mount();
    expect((await screen.findByTestId('assistant-pending-watch')).textContent).toContain('when 2 runs finish');
  });

  it('is absent when nothing is pending', async () => {
    await mount();
    await screen.findByText(/Launch teaser finished/);
    expect(screen.queryByTestId('assistant-pending-watch')).toBeNull();
  });

  it('disappears once the probe reports none left', async () => {
    detail.pendingRunWatches = [{ runLabel: 'Launch teaser', createdAt: '' }];
    await mount();
    await screen.findByTestId('assistant-pending-watch');
    turn = { state: 'idle', pendingWatches: 0 };
    await tick();
    await waitFor(() => expect(screen.queryByTestId('assistant-pending-watch')).toBeNull());
  });
});

describe('polling for a reply the server started', () => {
  beforeEach(() => {
    detail.pendingRunWatches = [{ runLabel: 'Launch teaser', createdAt: '' }];
  });

  it('does not poll a thread with nothing pending', async () => {
    detail.pendingRunWatches = undefined;
    await mount();
    const before = turnCalls;
    await tick(30000);
    expect(turnCalls).toBe(before);
  });

  it('polls every 5 s while pending and stops on unmount', async () => {
    const view = await mount();
    const before = turnCalls;
    await tick(5000);
    expect(turnCalls).toBe(before + 1);
    await tick(5000);
    expect(turnCalls).toBe(before + 2);
    view.unmount();
    await tick(15000);
    expect(turnCalls).toBe(before + 2);
  });

  it('attaches (no new message) when the turn becomes running', async () => {
    await mount();
    expect(chatBodies).toHaveLength(0);
    turn = { state: 'running', pendingWatches: 0 };
    await tick();
    await waitFor(() => expect(chatBodies).toHaveLength(1));
    expect(chatBodies[0]).toMatchObject({ threadId: THREAD_ID, attach: true, messages: [] });
    expect(await screen.findByText('The teaser passed.')).toBeTruthy();
  });

  it('attaches when the turn awaits approval', async () => {
    await mount();
    turn = { state: 'awaiting_approval', pendingWatches: 0 };
    await tick();
    await waitFor(() => expect(chatBodies).toHaveLength(1));
    expect(chatBodies[0]).toMatchObject({ attach: true });
  });

  it('does not attach a second time while a stream is already attached', async () => {
    let controller!: ReadableStreamDefaultController<Uint8Array>;
    const body = new ReadableStream<Uint8Array>({
      start(c) {
        controller = c;
      },
    });
    chatResponse = () => new Response(body, { status: 200, headers: SSE_HEADERS });
    await mount();
    turn = { state: 'running', pendingWatches: 1 };
    await tick();
    await waitFor(() => expect(chatBodies).toHaveLength(1));
    await tick();
    await tick();
    expect(chatBodies).toHaveLength(1);
    controller.enqueue(new TextEncoder().encode(REPLY_SSE));
    controller.close();
    await tick(0);
  });

  it('pauses while the tab is hidden and polls once when it returns', async () => {
    await mount();
    const before = turnCalls;
    setHidden(true);
    await tick(20000);
    expect(turnCalls).toBe(before);
    setHidden(false);
    await act(async () => {
      document.dispatchEvent(new Event('visibilitychange'));
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(turnCalls).toBe(before + 1);
  });

  it('reloads the history once when the reply came and went between two polls', async () => {
    await mount();
    expect(screen.queryByText('Already answered.')).toBeNull();
    // The server started, finished and persisted a wake turn between two looks.
    detail = {
      ...detail,
      pendingRunWatches: [],
      messages: [...detail.messages, { id: 'm3', role: 'assistant', content: 'Already answered.', sequence: 3, createdAt: '' }],
    };
    turn = { state: 'idle', pendingWatches: 0 };
    await tick();
    expect(await screen.findByText('Already answered.')).toBeTruthy();
    expect(chatBodies).toHaveLength(0);
    expect(screen.queryByTestId('assistant-pending-watch')).toBeNull();
  });

  it('does not reload while watches are unchanged, and survives a failing probe', async () => {
    await mount();
    const detailCalls = () => (global.fetch as unknown as { mock: { calls: unknown[][] } }).mock.calls.filter(
      (c) => /\/threads\/[^/]+$/.test(String(c[0])),
    ).length;
    const before = detailCalls();
    turn = { state: 'completed', pendingWatches: 1 };
    await tick();
    await tick();
    expect(detailCalls()).toBe(before);
    const real = global.fetch;
    global.fetch = vi.fn(() => Promise.reject(new TypeError('offline'))) as unknown as typeof fetch;
    await tick();
    await tick();
    global.fetch = real;
  });
});
