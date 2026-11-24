// @vitest-environment jsdom

/**
 * The approval surface end to end through the real chat runtime: a request arrives on the stream
 * while the drawer is closed, and the header badge and the toast both follow it — and only the
 * server's next frames (or a stopped turn) take it back down.
 *
 * The drawer is replaced with nothing on purpose: everything under test lives outside it, and a
 * second runtime would never be looked at.
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { AssistantProvider } from './AssistantProvider';
import { AssistantChatPanel } from './AssistantChatPanel';
import { AssistantPendingApprovals } from './AssistantPendingApprovals';
import { AssistantApprovalToast } from './AssistantApprovalToast';
import { AssistantTrigger } from './AssistantTrigger';
import { AssistantShellProvider, useAssistant } from './AssistantShellProvider';

vi.mock('./AssistantDrawer', () => ({ AssistantDrawer: () => null }));

vi.mock('next/navigation', () => ({
  usePathname: () => '/workflows',
  useRouter: () => ({ push: vi.fn(), replace: vi.fn(), refresh: vi.fn() }),
}));

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

function installFetch() {
  global.fetch = vi.fn<typeof fetch>((input) => {
    const url = String(input);
    if (url.endsWith('/api/v1/assistant/preferences')) {
      return Promise.resolve(
        new Response(JSON.stringify({ approvalMode: 'ask' }), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        }),
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

/** The count the shell publishes, as the header would see it. */
function PendingProbe() {
  const { pendingApprovals } = useAssistant();
  return <span data-testid="pending-count">{pendingApprovals.length}</span>;
}

const count = () => screen.getByTestId('pending-count').textContent;
const toast = () => screen.queryByTestId('assistant-approval-toast');

function renderHarness() {
  return render(
    <MantineProvider env="test">
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <AssistantShellProvider>
          <PendingProbe />
          <AssistantTrigger />
          <AssistantApprovalToast />
          <AssistantProvider context={{ projectId: '12345678-1234-1234-1234-123456789012' }}>
            <AssistantPendingApprovals />
            <AssistantChatPanel />
          </AssistantProvider>
        </AssistantShellProvider>
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

describe('approvals outside the chat', () => {
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
    installFetch();
  });

  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  it('a pending frame lights the badge and the toast while the drawer is closed', async () => {
    renderHarness();
    await ask('Delete notes.txt');

    await act(async () => {
      stream.push(action(PENDING));
    });

    await waitFor(() => expect(count()).toBe('1'));
    expect(screen.getByRole('button', { name: 'Open assistant, 1 approval waiting' })).toBeTruthy();
    expect((await screen.findByTestId('assistant-approval-toast')).textContent).toContain(PENDING.title);
    // Nothing was answered on the person's behalf.
    expect(within(toast() as HTMLElement).getByRole('button', { name: 'Review' })).toBeTruthy();
  });

  it('an allowed frame brings the badge and the toast down', async () => {
    renderHarness();
    await ask('Delete notes.txt');
    await act(async () => stream.push(action(PENDING)));
    await waitFor(() => expect(count()).toBe('1'));

    await act(async () => stream.push(action({ id: 'act-1', state: 'allowed' })));

    await waitFor(() => expect(count()).toBe('0'));
    await waitFor(() => expect(toast()).toBeNull());
    expect(screen.getByRole('button', { name: 'Open assistant' })).toBeTruthy();
  });

  it('a second request counts too, and a declined frame clears it', async () => {
    renderHarness();
    await ask('Delete notes.txt');

    await act(async () => stream.push(action(PENDING)));
    await waitFor(() => expect(count()).toBe('1'));

    await act(async () => stream.push(action({ ...PENDING, id: 'act-2', title: 'Rename the project' })));
    await waitFor(() => expect(count()).toBe('2'));

    await act(async () => stream.push(action({ id: 'act-2', state: 'declined' })));
    await waitFor(() => expect(count()).toBe('1'));

    // The first is still waiting; only the declined one went away.
    expect(screen.getByRole('button', { name: 'Open assistant, 1 approval waiting' })).toBeTruthy();
  });

  it('Review lands on the request the toast named, not the first pending card', async () => {
    renderHarness();
    await ask('Delete notes.txt');

    await act(async () => {
      stream.push(action(PENDING));
      stream.push(action({ ...PENDING, id: 'act-2', title: 'Rename the project' }));
    });
    await waitFor(() => expect(count()).toBe('2'));

    // The toast names the newest request, so its Review must reveal that card.
    const review = within(toast() as HTMLElement).getByRole('button', { name: 'Review' });
    await act(async () => {
      fireEvent.click(review);
    });

    await waitFor(() => expect(document.activeElement?.getAttribute('data-action-id')).toBe('act-2'));
  });

  it('hands focus back to the header trigger when the request settles under the toast', async () => {
    renderHarness();
    await ask('Delete notes.txt');
    await act(async () => stream.push(action(PENDING)));
    await waitFor(() => expect(count()).toBe('1'));

    const review = within(toast() as HTMLElement).getByRole('button', { name: 'Review' });
    act(() => review.focus());

    // The server expires it while focus is inside the toast; losing the element must not drop
    // focus to the document body.
    await act(async () => stream.push(action({ id: 'act-1', state: 'expired' })));
    await waitFor(() => expect(toast()).toBeNull());
    expect(document.activeElement?.hasAttribute('data-assistant-trigger')).toBe(true);
  });

  it('a pending request on a stopped turn stops counting, matching the card’s expired rendering', async () => {
    renderHarness();
    await ask('Delete notes.txt');
    await act(async () => stream.push(action(PENDING)));
    await waitFor(() => expect(count()).toBe('1'));

    // The reply ends — the person pressed Stop, or the connection dropped.
    await act(async () => stream.finish());

    await waitFor(() => expect(count()).toBe('0'));
    await waitFor(() => expect(toast()).toBeNull());
    // The card is still there, and says what the count implies.
    await waitFor(() => expect(screen.getAllByTestId('assistant-action')[0].getAttribute('data-state')).toBe('expired'));
  });
});
