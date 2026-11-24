// @vitest-environment jsdom

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { AssistantProvider } from './AssistantProvider';
import { AssistantChatPanel } from './AssistantChatPanel';
import { creditsRefusalOf } from './AssistantCreditsNotice';

/**
 * A turn refused for lack of credits is a normal reply plus a `data-credits` part: the reply's words
 * still show, and a notice under them says how to fix it, with a link to add credits.
 */

class MockResizeObserver implements ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

const sse = (frames: Array<{ type: string; data: unknown }>) =>
  [
    'data: {"type":"start","messageId":"msg_1"}',
    '',
    'data: {"type":"text-start","id":"txt_1"}',
    '',
    'data: {"type":"text-delta","textDelta":"You have used this month\'s assistant allowance."}',
    '',
    'data: {"type":"text-end"}',
    '',
    ...frames.flatMap((frame) => [`data: ${JSON.stringify(frame)}`, '']),
    'data: {"type":"finish","finishReason":"stop","usage":{"inputTokens":0,"outputTokens":0}}',
    '',
    'data: [DONE]',
    '',
    '',
  ].join('\n');

function mockStream(frames: Array<{ type: string; data: unknown }>) {
  global.fetch = vi.fn<typeof fetch>(() =>
    Promise.resolve(
      new Response(sse(frames), {
        status: 200,
        headers: { 'content-type': 'text/event-stream', 'x-vercel-ai-ui-message-stream': 'v1' },
      }),
    ),
  ) as unknown as typeof fetch;
}

async function sendMessage() {
  const user = userEvent.setup();
  render(
    <MantineProvider>
      <AssistantProvider context={{ projectId: '12345678-1234-1234-1234-123456789012' }}>
        <AssistantChatPanel />
      </AssistantProvider>
    </MantineProvider>,
  );
  const inputs = screen.getAllByPlaceholderText(/message the assistant/i);
  await user.type(inputs[inputs.length - 1], 'Make it shorter');
  const sendButtons = screen.getAllByRole('button', { name: /send/i });
  await user.click(sendButtons[sendButtons.length - 1]);
}

describe('the assistant credits notice', () => {
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

  it('renders under the reply with an Add credits link, and the reply still shows', { timeout: 30000 }, async () => {
    mockStream([{ type: 'data-credits', data: { code: 'INSUFFICIENT_CREDITS', balance: 0 } }]);
    await sendMessage();

    const notice = await screen.findByTestId('assistant-credits-notice', {}, { timeout: 10000 });
    expect(notice.textContent).toContain('Not enough credits');
    expect(notice.textContent).toContain('you have 0 credits');
    const link = screen.getByRole('link', { name: 'Add credits' });
    expect(link.getAttribute('href')).toBe('/settings/billing');
    expect(screen.getByText(/You have used this month's assistant allowance\./)).toBeTruthy();
    // A normal reply, not a broken stream: the error state never shows.
    expect(screen.queryByText(/The assistant couldn.t reply/)).toBeNull();
  });

  it('shows the streamed reply and a plain turn-limit notice for a TURN_LIMIT part', { timeout: 30000 }, async () => {
    mockStream([{ type: 'data-credits', data: { code: 'TURN_LIMIT' } }]);
    await sendMessage();

    const notice = await screen.findByTestId('assistant-turn-limit-notice', {}, { timeout: 10000 });
    expect(notice.textContent).toContain('This reply hit the spending limit for one message. Ask again to continue.');
    expect(notice.textContent).not.toContain('TURN_LIMIT');
    expect(screen.getByText(/You have used this month's assistant allowance\./)).toBeTruthy();
    // Not the out-of-credits notice, and not a broken stream.
    expect(screen.queryByTestId('assistant-credits-notice')).toBeNull();
    expect(screen.queryByRole('link', { name: 'Add credits' })).toBeNull();
    expect(screen.queryByText(/The assistant couldn.t reply/)).toBeNull();
  });

  it('shows nothing for a reply without a credits part', { timeout: 30000 }, async () => {
    mockStream([]);
    await sendMessage();

    await screen.findByText(/You have used this month's assistant allowance\./, {}, { timeout: 10000 });
    expect(screen.queryByTestId('assistant-credits-notice')).toBeNull();
  });
});

describe('creditsRefusalOf', () => {
  it('reads the credits part and ignores other codes and malformed frames', () => {
    expect(creditsRefusalOf([{ name: 'credits', data: { code: 'INSUFFICIENT_CREDITS', balance: 3.5 } }])).toEqual({
      code: 'INSUFFICIENT_CREDITS',
      balance: 3.5,
    });
    expect(creditsRefusalOf([{ name: 'credits', data: { code: 'INSUFFICIENT_CREDITS' } }])).toEqual({
      code: 'INSUFFICIENT_CREDITS',
      balance: null,
    });
    expect(creditsRefusalOf([{ name: 'credits', data: { code: 'TURN_LIMIT' } }])).toEqual({ code: 'TURN_LIMIT' });
    // A 429-only code never arrives as a part; if one did, it is not a notice.
    expect(creditsRefusalOf([{ name: 'credits', data: { code: 'TURN_CONCURRENCY_LIMIT' } }])).toBeNull();
    expect(creditsRefusalOf([{ name: 'credits', data: { code: 'SOMETHING_ELSE', balance: 1 } }])).toBeNull();
    expect(creditsRefusalOf([{ name: 'toolStatus', data: { caption: 'x' } }, null, 'junk'])).toBeNull();
    expect(creditsRefusalOf(null)).toBeNull();
  });
});
