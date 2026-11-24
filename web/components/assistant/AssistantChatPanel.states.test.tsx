// @vitest-environment jsdom

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, waitFor, cleanup, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { AssistantProvider } from './AssistantProvider';
import { AssistantChatPanel, ASSISTANT_SUGGESTIONS } from './AssistantChatPanel';

class MockResizeObserver implements ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

const sse = (text: string) =>
  [
    'data: {"type":"start","messageId":"msg_1"}',
    '',
    'data: {"type":"text-start","id":"txt_1"}',
    '',
    `data: ${JSON.stringify({ type: 'text-delta', textDelta: text })}`,
    '',
    'data: {"type":"text-end"}',
    '',
    'data: {"type":"finish","finishReason":"stop","usage":{"inputTokens":0,"outputTokens":0}}',
    '',
    'data: [DONE]',
    '',
    '', // an SSE event is dispatched on a blank line, so the stream must end with "\n\n"
  ].join('\n');

const sseResponse = (body: string) =>
  new Response(body, {
    status: 200,
    headers: { 'content-type': 'text/event-stream', 'x-vercel-ai-ui-message-stream': 'v1' },
  });

function renderPanel() {
  return render(
    <MantineProvider>
      <AssistantProvider
        context={{
          projectId: '12345678-1234-1234-1234-123456789012',
          workflowId: '87654321-4321-4321-4321-210987654321',
        }}
      >
        <AssistantChatPanel />
      </AssistantProvider>
    </MantineProvider>,
  );
}

async function send(text: string) {
  const user = userEvent.setup();
  await user.type(screen.getByRole('textbox', { name: /message the workflow assistant/i }), text);
  await user.keyboard('{Enter}');
}

describe('AssistantChatPanel states', () => {
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

  it('shows an empty state with example prompts that send when clicked', async () => {
    const fetchMock = vi.fn<typeof fetch>(() => Promise.resolve(sseResponse(sse('Sure.'))));
    global.fetch = fetchMock as unknown as typeof fetch;
    renderPanel();

    const thread = screen.getByTestId('assistant-thread');
    expect(within(thread).getByText(/how can i help with this workflow/i)).toBeDefined();
    for (const s of ASSISTANT_SUGGESTIONS) {
      expect(within(thread).getByRole('button', { name: new RegExp(s.title, 'i') })).toBeDefined();
    }

    await userEvent.setup().click(screen.getByRole('button', { name: /explain this workflow/i }));
    await waitFor(() => expect(screen.getByText('Sure.')).toBeDefined());
    expect(fetchMock).toHaveBeenCalledTimes(1);
    // The empty state goes away once there's a conversation.
    expect(screen.queryByText(/how can i help with this workflow/i)).toBeNull();
  });

  it('sends on Enter and renders the reply as markdown', async () => {
    global.fetch = vi.fn<typeof fetch>(() =>
      Promise.resolve(sseResponse(sse('Here is the plan:\n\n- **Remove** step 1\n- Add `Review`'))),
    ) as unknown as typeof fetch;
    renderPanel();

    await send('Tidy this up');

    await waitFor(() => expect(screen.getByText('Remove')).toBeDefined());
    expect(screen.getByText('Remove').tagName).toBe('STRONG');
    expect(screen.getByText('Review').tagName).toBe('CODE');
    expect(screen.getAllByRole('listitem').length).toBe(2);
    // The user's own message is shown as sent.
    expect(screen.getByText('Tidy this up')).toBeDefined();
  });

  it('shows a thinking state and a stop control while the reply is in flight', async () => {
    let finish: (() => void) | null = null;
    global.fetch = vi.fn<typeof fetch>(
      (_input, init) =>
        new Promise<Response>((resolve, reject) => {
          finish = () => resolve(sseResponse(sse('Done.')));
          init?.signal?.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')));
        }),
    ) as unknown as typeof fetch;
    renderPanel();

    await send('Add a review step');

    await waitFor(() => expect(screen.getByRole('status').textContent).toMatch(/thinking/i));
    expect(screen.getByRole('button', { name: /stop generating/i })).toBeDefined();
    expect(screen.queryByRole('button', { name: /^send$/i })).toBeNull();

    (finish as unknown as () => void)();
    await waitFor(() => expect(screen.getByText('Done.')).toBeDefined());
    expect(screen.queryByText(/thinking/i)).toBeNull();
    expect(screen.getByRole('button', { name: /^send$/i })).toBeDefined();
  });

  it('shows an error with a retry that asks again', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockImplementationOnce(() => Promise.resolve(new Response('boom', { status: 500 })))
      .mockImplementationOnce(() => Promise.resolve(sseResponse(sse('Recovered.'))));
    global.fetch = fetchMock as unknown as typeof fetch;
    vi.spyOn(console, 'error').mockImplementation(() => {});
    renderPanel();

    await send('Hello');

    const alert = await screen.findByRole('alert');
    expect(within(alert).getByText(/couldn.t reply/i)).toBeDefined();

    await userEvent.setup().click(within(alert).getByRole('button', { name: /try again/i }));
    await waitFor(() => expect(screen.getByText('Recovered.')).toBeDefined());
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('shows the server\'s sentence for a 429 turn cap, and the composer still sends', async () => {
    const busy =
      'Several assistant requests are already running in this workspace. Wait for one of them to finish, then try again.';
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockImplementationOnce(() =>
        Promise.resolve(
          new Response(JSON.stringify({ code: 'TURN_CONCURRENCY_LIMIT', message: busy }), {
            status: 429,
            headers: { 'content-type': 'application/json' },
          }),
        ),
      )
      .mockImplementationOnce(() => Promise.resolve(sseResponse(sse('Here it is.'))));
    global.fetch = fetchMock as unknown as typeof fetch;
    vi.spyOn(console, 'error').mockImplementation(() => {});
    renderPanel();

    await send('Hello');

    const alert = await screen.findByRole('alert');
    expect(within(alert).getByText(busy)).toBeDefined();
    expect(within(alert).getByText(/busy right now/i)).toBeDefined();
    // Not the transport's generic failure.
    expect(within(alert).queryByText(/couldn.t reply/i)).toBeNull();
    expect(alert.textContent).not.toMatch(/Status 429|TURN_CONCURRENCY_LIMIT/);

    // The composer is usable again: a new message goes out and is answered.
    await waitFor(() => expect(screen.getByRole('button', { name: /^send$/i })).toBeDefined());
    await send('Hello again');
    await waitFor(() => expect(screen.getByText('Here it is.')).toBeDefined());
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('keeps the generic error for a 429 that is not the turn cap', async () => {
    global.fetch = vi.fn<typeof fetch>(() =>
      Promise.resolve(new Response(JSON.stringify({ code: 'SOMETHING_ELSE', message: 'x' }), { status: 429 })),
    ) as unknown as typeof fetch;
    vi.spyOn(console, 'error').mockImplementation(() => {});
    renderPanel();

    await send('Hello');

    const alert = await screen.findByRole('alert');
    expect(within(alert).getByText(/couldn.t reply/i)).toBeDefined();
  });
});
