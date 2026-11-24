// @vitest-environment jsdom

/**
 * The permission card, end to end through the real chat runtime: frames arrive on a stream that
 * stays open, the card is drawn under the reply, answering it posts to the approvals endpoint, and
 * the frames that follow move the same card on. Only `fetch` is replaced.
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { useAssistantPreferences } from '@/lib/hooks/use-assistant-preferences';
import { AssistantProvider } from './AssistantProvider';
import { AssistantChatPanel } from './AssistantChatPanel';
import { AssistantActionSync } from './AssistantActionSync';
import {
  onAssistantActionDone,
  resetAnnouncedAssistantActions,
  type AssistantActionDone,
} from '@/lib/assistant/events';

class MockResizeObserver implements ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

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
  projectName: 'Launch Teaser',
};

interface Stream {
  push(chunk: Uint8Array): void;
  /** Ends the reply the way the server does: a finish frame, then the end of the stream. */
  finish(): void;
}

let stream: Stream;
let approvals: Array<{ url: string; body: unknown }>;
let approvalStatus: number;
/** What the server holds for the person's ask/auto setting; an "always allow" answer flips it. */
let serverMode: 'ask' | 'auto';

function installFetch() {
  approvals = [];
  approvalStatus = 204;
  serverMode = 'ask';
  global.fetch = vi.fn<typeof fetch>((input, init) => {
    const url = String(input);
    if (url.endsWith('/api/v1/assistant/preferences')) {
      return Promise.resolve(
        new Response(JSON.stringify({ approvalMode: serverMode }), { status: 200, headers: { 'content-type': 'application/json' } }),
      );
    }
    if (url.includes('/api/v1/assistant/approvals/')) {
      const body = JSON.parse(String(init?.body)) as { alwaysAllow?: boolean };
      approvals.push({ url, body });
      if (approvalStatus === 204 && body.alwaysAllow) serverMode = 'auto';
      return Promise.resolve(
        approvalStatus === 204
          ? new Response(null, { status: 204 })
          : new Response(JSON.stringify({ message: 'nope' }), { status: approvalStatus, headers: { 'content-type': 'application/json' } }),
      );
    }
    let controller!: ReadableStreamDefaultController<Uint8Array>;
    const body = new ReadableStream<Uint8Array>({
      start(c) {
        controller = c;
      },
    });
    stream = {
      push: (chunk) => controller.enqueue(chunk),
      finish: () => {
        controller.enqueue(frame({ type: 'finish', finishReason: 'stop', usage: { inputTokens: 0, outputTokens: 0 } }));
        controller.enqueue(encoder.encode('data: [DONE]\n\n'));
        controller.close();
      },
    };
    stream.push(frame({ type: 'start', messageId: 'msg_1' }));
    return Promise.resolve(
      new Response(body, { status: 200, headers: { 'content-type': 'text/event-stream', 'x-vercel-ai-ui-message-stream': 'v1' } }),
    );
  }) as unknown as typeof fetch;
}

/** Shows the setting the rest of the app would show (the drawer's switch reads the same hook). */
function ModeProbe() {
  const { mode, loaded } = useAssistantPreferences();
  return <span data-testid="mode">{loaded ? mode : 'loading'}</span>;
}

function renderPanel() {
  return render(
    <MantineProvider env="test">
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <AssistantProvider context={{ projectId: '12345678-1234-1234-1234-123456789012' }}>
          <AssistantActionSync />
          <ModeProbe />
          <AssistantChatPanel />
        </AssistantProvider>
      </SWRConfig>
    </MantineProvider>,
  );
}

async function ask(text: string) {
  const user = userEvent.setup();
  await user.type(screen.getByRole('textbox', { name: /message the workflow assistant/i }), text);
  await user.keyboard('{Enter}');
  // The reply is in flight once the stop control shows.
  await screen.findByRole('button', { name: /stop generating/i });
}

const cards = () => screen.queryAllByTestId('assistant-action');

describe('the permission card inside a reply', () => {
  let heard: AssistantActionDone[];
  let stopListening: () => void;

  beforeEach(() => {
    resetAnnouncedAssistantActions();
    heard = [];
    stopListening = onAssistantActionDone((done) => heard.push(done));
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
    installFetch();
  });

  afterEach(() => {
    stopListening();
    cleanup();
    vi.restoreAllMocks();
  });

  it('appears while the reply has no text yet, above the thinking indicator, and waits for an answer', async () => {
    renderPanel();
    await ask('Delete notes.txt');

    await act(async () => {
      stream.push(frame({ type: 'data-toolStatus', data: { caption: 'Waiting for your OK' } }));
      stream.push(action(PENDING));
    });

    const card = await screen.findByRole('group', { name: 'Delete the file “notes.txt”' });
    expect(card.textContent).toContain("This can't be undone.");
    expect(within(card).getByRole('button', { name: 'Allow' })).toBeTruthy();
    // No text has arrived, so the thinking indicator is still up — and the card sits above it.
    const thinking = await screen.findByText(/Waiting for your OK/);
    expect(card.compareDocumentPosition(thinking) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(approvals).toEqual([]);
  });

  it('stops saying it is waiting for an OK once the request has been answered', async () => {
    renderPanel();
    await ask('Delete notes.txt');
    await act(async () => {
      stream.push(frame({ type: 'data-toolStatus', data: { caption: 'Waiting for your OK' } }));
      stream.push(action(PENDING));
    });
    await screen.findByText(/Waiting for your OK/);

    await userEvent.setup().click(await screen.findByRole('button', { name: 'Allow' }));
    await act(async () => stream.push(action({ id: 'act-1', state: 'allowed' })));

    await waitFor(() => expect(screen.queryByText(/Waiting for your OK/)).toBeNull());
    expect(screen.getByRole('status').textContent).toMatch(/thinking/i);
  });

  it('Allow posts the decision, and the frames that follow move the same card on to its outcome', async () => {
    renderPanel();
    await ask('Delete notes.txt');
    await act(async () => stream.push(action(PENDING)));

    await userEvent.setup().click(await screen.findByRole('button', { name: 'Allow' }));

    await waitFor(() => expect(approvals).toHaveLength(1));
    expect(approvals[0].url).toBe('/api/v1/assistant/approvals/act-1');
    expect(approvals[0].body).toEqual({ decision: 'allow' });

    await act(async () => stream.push(action({ id: 'act-1', state: 'allowed' })));
    await waitFor(() => expect(cards()[0].textContent).toContain('Working on it…'));

    await act(async () => {
      stream.push(
        action({
          id: 'act-1',
          state: 'done',
          outcome: 'Deleted “notes.txt”.',
          refresh: { projectId: '12345678-1234-1234-1234-123456789012' },
        }),
      );
      stream.push(frame({ type: 'text-start', id: 't1' }));
      stream.push(frame({ type: 'text-delta', id: 't1', delta: 'Done — it is gone.', textDelta: 'Done — it is gone.' }));
      stream.push(frame({ type: 'text-end', id: 't1' }));
      stream.finish();
    });

    await screen.findByText('Done — it is gone.');
    // One card for the whole life of the request, now a single line with the outcome.
    expect(cards()).toHaveLength(1);
    expect(cards()[0].getAttribute('data-state')).toBe('done');
    expect(cards()[0].textContent).toContain('Deleted “notes.txt”.');
    expect(screen.queryByRole('button', { name: 'Allow' })).toBeNull();
    // And the rest of the app is told, exactly once.
    await waitFor(() => expect(heard.map((h) => h.id)).toEqual(['act-1']));
    expect(heard[0]).toMatchObject({ kind: 'file', refresh: { projectId: '12345678-1234-1234-1234-123456789012' } });
  });

  it('"Always allow" asks first, then allows this request AND puts the app in auto mode', async () => {
    renderPanel();
    await waitFor(() => expect(screen.getByTestId('mode').textContent).toBe('ask'));
    await ask('Delete notes.txt');
    await act(async () => stream.push(action(PENDING)));

    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Always allow' }));
    expect(approvals).toEqual([]);
    await user.click(await screen.findByRole('button', { name: 'Turn on auto mode' }));

    await waitFor(() => expect(approvals).toHaveLength(1));
    expect(approvals[0].body).toEqual({ decision: 'allow', alwaysAllow: true });
    // The switch and the tagline follow the server without a reload.
    await waitFor(() => expect(screen.getByTestId('mode').textContent).toBe('auto'));
  });

  it("Don't allow posts deny, and a declined frame closes the card with nothing changed", async () => {
    renderPanel();
    await ask('Delete notes.txt');
    await act(async () => stream.push(action(PENDING)));

    await userEvent.setup().click(await screen.findByRole('button', { name: "Don't allow" }));
    await waitFor(() => expect(approvals[0]?.body).toEqual({ decision: 'deny' }));

    await act(async () => {
      stream.push(action({ id: 'act-1', state: 'declined' }));
      stream.finish();
    });

    await waitFor(() => expect(cards()[0].textContent).toContain('You chose not to allow this. Nothing was changed.'));
    expect(heard).toEqual([]);
  });

  it('a request answered elsewhere (409) says so on the card, and leaves it for the stream to settle', async () => {
    approvalStatus = 409;
    renderPanel();
    await ask('Delete notes.txt');
    await act(async () => stream.push(action(PENDING)));

    await userEvent.setup().click(await screen.findByRole('button', { name: 'Allow' }));

    expect((await screen.findByRole('alert')).textContent).toBe('This request was already answered or has run out of time.');
  });

  it('shows an auto-mode action that only ever arrives finished, as a quiet line', async () => {
    renderPanel();
    await ask('Rename it');

    await act(async () => {
      stream.push(action({ id: 'auto-1', state: 'done', mode: 'auto', kind: 'project', title: 'Rename the project', outcome: 'Renamed to “Spring Reel”.' }));
      stream.finish();
    });

    await waitFor(() => expect(cards()).toHaveLength(1));
    expect(cards()[0].textContent).toContain('Renamed to “Spring Reel”.');
    expect(screen.getByText('Done automatically')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Allow' })).toBeNull();
  });

  it('says the connection dropped (not "timed out") on a request left unanswered when the reply stops', async () => {
    renderPanel();
    await ask('Delete notes.txt');
    await act(async () => stream.push(action(PENDING)));
    await screen.findByRole('button', { name: 'Allow' });

    // The reply ends (the person pressed Stop, or the connection dropped) with the card unanswered.
    await act(async () => stream.finish());

    await waitFor(() => expect(cards()[0].getAttribute('data-state')).toBe('expired'));
    expect(cards()[0].textContent).toContain('The connection dropped before this was answered');
    expect(cards()[0].textContent).not.toMatch(/timed out|ran out of time/i);
    expect(screen.queryByRole('button', { name: 'Allow' })).toBeNull();
  });

  it('draws two requests in one reply as two cards, in the order they first appeared', async () => {
    renderPanel();
    await ask('Do two things');

    await act(async () => {
      stream.push(action({ ...PENDING, id: 'first', title: 'First thing', risk: 'normal' }));
      stream.push(action({ ...PENDING, id: 'second', title: 'Second thing', risk: 'normal' }));
      stream.push(action({ ...PENDING, id: 'first', title: 'First thing', risk: 'normal', state: 'allowed' }));
    });

    await waitFor(() => expect(cards()).toHaveLength(2));
    expect(cards().map((c) => c.getAttribute('data-state'))).toEqual(['allowed', 'pending']);
    expect(cards()[0].textContent).toContain('First thing');
  });
});
