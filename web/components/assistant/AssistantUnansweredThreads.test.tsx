// @vitest-environment jsdom

/**
 * Findings: an unanswered question disappeared from view after navigating away, earlier
 * conversations were hard to find, and the panel could not tell a reply still being worked on from
 * a request that had died. `fetch` is stubbed in every test.
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { useEffect } from 'react';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import type { AssistantThreadDetail, AssistantThreadSummary } from '@/lib/types/assistant';
import { AssistantProvider } from './AssistantProvider';
import { AssistantChatPanel } from './AssistantChatPanel';
import { AssistantThreadList } from './AssistantThreadList';
import { AssistantShellProvider, useAssistant } from './AssistantShellProvider';

class MockResizeObserver implements ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

const THREAD_ID = '0f0f0f0f-1111-2222-3333-444444444444';
const SSE = [
  'data: {"type":"start","messageId":"msg_1"}',
  '',
  'data: {"type":"text-start","id":"txt_1"}',
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

interface Recorded {
  url: string;
  method: string;
  body: string | null;
}

let calls: Recorded[] = [];
let threads: AssistantThreadSummary[] = [];
let detail: AssistantThreadDetail | null = null;

function installFetch() {
  calls = [];
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>(async (input, init) => {
      const url = String(input);
      const method = (init?.method ?? 'GET').toUpperCase();
      calls.push({ url, method, body: typeof init?.body === 'string' ? init.body : null });
      const json = (payload: unknown) =>
        new Response(JSON.stringify(payload), { status: 200, headers: { 'content-type': 'application/json' } });

      if (url.endsWith('/api/v1/assistant/threads') && method === 'GET') return json(threads);
      if (url.endsWith('/api/v1/assistant/threads') && method === 'POST') return json({ id: 'fresh-thread' });
      if (/\/api\/v1\/assistant\/threads\/[^/]+$/.test(url)) {
        return json(detail ?? { id: 'fresh-thread', title: 'New chat', projectId: null, messages: [] });
      }
      if (url.includes('/api/v1/assistant/chat')) {
        return new Response(SSE, {
          status: 200,
          headers: { 'content-type': 'text/event-stream', 'x-vercel-ai-ui-message-stream': 'v1' },
        });
      }
      return new Response('unexpected', { status: 500 });
    }),
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
  threads = [];
  detail = null;
  installFetch();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function OpenOnMount() {
  const { open, threadId } = useAssistant();
  useEffect(() => {
    open();
  }, [open]);
  return <span data-testid="active-thread">{threadId ?? 'none'}</span>;
}

describe('waiting conversations stay in sight', () => {
  it('opening the assistant points at an unanswered conversation and opens it on request', async () => {
    threads = [
      { id: 'answered', title: 'Old chat', projectId: null, updatedAt: '2026-10-04T08:00:00Z' },
      { id: 'lost', title: 'Make me a 15 s reel', projectId: null, updatedAt: '2026-10-04T09:00:00Z', awaitingReply: true },
    ];
    render(
      <MantineProvider env="test">
        <AssistantShellProvider>
          {/* The shell mounts the drawer, and the open drawer renders the History bar itself. */}
          <OpenOnMount />
        </AssistantShellProvider>
      </MantineProvider>,
    );

    const hint = await screen.findByTestId('assistant-waiting-threads');
    expect(hint.textContent).toContain('“Make me a 15 s reel” has no reply yet');

    await userEvent.setup().click(
      within(hint).getByRole('button', { name: 'Open conversation Make me a 15 s reel that is waiting for a reply' }),
    );
    expect(screen.getByTestId('active-thread').textContent).toBe('lost');
  });

  it('History is a labelled button and each row says whether a reply is missing or still coming', async () => {
    threads = [
      { id: 'lost', title: 'Lost one', projectId: null, updatedAt: '2026-10-04T09:00:00Z', awaitingReply: true },
      { id: 'busy', title: 'Busy one', projectId: null, updatedAt: '2026-10-04T09:30:00Z', replyInProgress: true },
    ];
    render(
      <MantineProvider env="test">
        <AssistantShellProvider>
          <AssistantThreadList />
        </AssistantShellProvider>
      </MantineProvider>,
    );

    const history = screen.getByRole('button', { name: 'History' });
    expect(history.textContent).toContain('History');
    await userEvent.setup().click(history);

    const panel = await screen.findByRole('region', { name: 'Conversation history' });
    await waitFor(() => expect(within(panel).getByText(/No reply yet/)).toBeTruthy());
    expect(within(panel).getByText(/Still answering…/)).toBeTruthy();
  });
});

describe('a restored conversation says what happened to its last message', () => {
  function renderPanel() {
    return render(
      <MantineProvider env="test">
        <AssistantProvider threadId={THREAD_ID} context={{}}>
          <AssistantChatPanel />
        </AssistantProvider>
      </MantineProvider>,
    );
  }

  it('a died request is reported, and Ask again re-runs the same message without repeating it', async () => {
    detail = {
      id: THREAD_ID,
      title: 'Make me a reel',
      projectId: null,
      messages: [{ id: 'm1', role: 'user', content: 'Make me a reel', sequence: 1, createdAt: '' }],
    };
    renderPanel();

    const notice = await screen.findByTestId('assistant-no-reply');
    expect(notice.textContent).toContain('No reply yet');

    await userEvent.setup().click(within(notice).getByRole('button', { name: 'Ask again' }));

    await waitFor(() => expect(screen.getByText('Here is your reel plan.')).toBeTruthy(), { timeout: 5000 });
    expect(screen.queryByTestId('assistant-no-reply')).toBeNull();

    const chat = calls.filter((call) => call.url.includes('/api/v1/assistant/chat'));
    expect(chat).toHaveLength(1);
    const body = JSON.parse(chat[0].body ?? '{}') as { threadId?: string; messages?: Array<{ role: string }> };
    expect(body.threadId).toBe(THREAD_ID);
    // The question is sent once — as the run's parent — never appended a second time.
    expect((body.messages ?? []).filter((m) => m.role === 'user')).toHaveLength(1);
    expect(screen.getAllByText('Make me a reel')).toHaveLength(1);
  });

  it('a reply the server is still working on is reported as such, not as lost', async () => {
    detail = {
      id: THREAD_ID,
      title: 'Make me a reel',
      projectId: null,
      messages: [{ id: 'm1', role: 'user', content: 'Make me a reel', sequence: 1, createdAt: '' }],
      replyInProgress: true,
    };
    renderPanel();

    const status = await screen.findByTestId('assistant-reply-in-progress');
    expect(status.textContent).toContain('Still working on your last message');
    expect(screen.queryByTestId('assistant-no-reply')).toBeNull();
  });

  it('an answered conversation shows neither notice', async () => {
    detail = {
      id: THREAD_ID,
      title: 'Done',
      projectId: null,
      messages: [
        { id: 'm1', role: 'user', content: 'Hi', sequence: 1, createdAt: '' },
        { id: 'm2', role: 'assistant', content: 'Hello!', sequence: 2, createdAt: '' },
      ],
    };
    renderPanel();

    await waitFor(() => expect(screen.getByText('Hello!')).toBeTruthy());
    expect(screen.queryByTestId('assistant-no-reply')).toBeNull();
    expect(screen.queryByTestId('assistant-reply-in-progress')).toBeNull();
  });
});
