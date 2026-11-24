// @vitest-environment jsdom

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { AssistantProvider } from './AssistantProvider';
import { AssistantChatPanel } from './AssistantChatPanel';
import type { AssistantContext } from './AssistantShellProvider';

/**
 * The `threadId` the client actually puts on the wire — asserted on the SERIALIZED body the REAL
 * adapter posts, not on the provider's return value.
 *
 * WHY THIS FILE EXISTS
 *
 * `AssistantProvider`'s `body` callback is spread LAST by `DataStreamRuntimeAdapter`, so it can
 * override keys the adapter contributes itself. The adapter contributes `threadId` from its own
 * `unstable_threadId` argument, and before a thread is bound that argument is assistant-ui's
 * CLIENT-SIDE PLACEHOLDER — the literal string `__LOCALID_…` (see
 * `@assistant-ui/core/.../remote-thread-state.js`). The endpoint (see
 * `AssistantChatController.ChatStream` and `AssistantChatRequest`) binds `threadId` as a
 * `Guid`, so `__LOCALID_…` is rejected by the JSON input formatter and the whole turn answers
 * **HTTP 400**. Worse, the server's thread auto-allocation keys on "no `threadId` key AT ALL", so
 * the placeholder is not merely wrong — it is the one shape that defeats the allocation path.
 *
 * The consequence is that the assistant is unusable for the entire pre-thread window (the composer
 * is not disabled while `POST /assistant/threads` is in flight) and after any transient
 * `createThread` failure.
 *
 * WHAT IS PINNED HERE
 *
 *   - an UNBOUND turn carries NO `threadId` key at all, on the serialized string as well as on the
 *     parsed object, and no `__LOCALID_` placeholder anywhere; and
 *   - a BOUND turn (the GUID the shell holds once `createThread` resolves) carries exactly that
 *     GUID.
 *
 * Both are asserted against the body captured off the real component stack — real
 * `AssistantProvider`, real `useDataStreamRuntime`, real `DataStreamRuntimeAdapter` — so this
 * cannot pass by testing a hand-built `adapter.run({...})` argument list that never produces the
 * placeholder in the first place.
 */

const GUID = '3f2b1c44-9d6e-4a71-8f0b-2c5e7a19d384';

/** The placeholder the adapter contributes and which must never reach the wire. */
const LOCAL_ID_PREFIX = '__LOCALID_';

class MockResizeObserver implements ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

const STUB_SSE = [
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
].join('\n');

/**
 * Renders the real provider + panel, types one turn, submits it, and returns the exact raw string
 * body the client posted — plus a parsed view of it.
 */
async function capturedTurnBody(
  threadId: string | null,
  context: AssistantContext = { projectId: '12345678-1234-1234-1234-123456789012' },
): Promise<{
  raw: string;
  body: Record<string, unknown>;
}> {
  const posts: string[] = [];

  global.fetch = vi.fn<typeof fetch>((_url, init) => {
    posts.push(String((init as RequestInit | undefined)?.body ?? ''));
    return Promise.resolve(
      new Response(STUB_SSE, {
        status: 200,
        headers: {
          'content-type': 'text/event-stream',
          'x-vercel-ai-ui-message-stream': 'v1',
        },
      }),
    );
  }) as unknown as typeof fetch;

  const user = userEvent.setup();
  render(
    <MantineProvider>
      <AssistantProvider
        threadId={threadId}
        context={context}
      >
        <AssistantChatPanel />
      </AssistantProvider>
    </MantineProvider>,
  );

  const inputs = screen.getAllByPlaceholderText(/message the assistant/i);
  await user.type(inputs[inputs.length - 1], 'hello');
  const sendButtons = screen.getAllByRole('button', { name: /send/i });
  await user.click(sendButtons[sendButtons.length - 1]);

  await waitFor(
    () => {
      expect(posts.some((raw) => raw.includes('"messages"'))).toBe(true);
    },
    { timeout: 5000 },
  );

  const raw = posts.find((candidate) => candidate.includes('"messages"'))!;
  return { raw, body: JSON.parse(raw) as Record<string, unknown> };
}

describe('AssistantProvider — the threadId the real adapter puts on the wire', () => {
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

  it('sends NO threadId key at all before a server thread is bound', async () => {
    const { raw, body } = await capturedTurnBody(null);

    expect(
      raw.includes(LOCAL_ID_PREFIX),
      'The client posted assistant-ui\'s client-side placeholder thread id. The endpoint binds ' +
        '`threadId` as a Guid, so this body is rejected with HTTP 400 by the JSON input formatter, ' +
        `and the server only allocates a thread when the key is ABSENT. Raw body posted:\n${raw}`,
    ).toBe(false);

    expect(
      'threadId' in body,
      'An unbound turn must carry no `threadId` key at all: the endpoint reads an ABSENT key as ' +
        '"allocate me a thread", and reads a present-but-unbindable one as a 400. ' +
        `Body posted: ${JSON.stringify(body)}`,
    ).toBe(false);
  });

  it('sends the bound server GUID once a thread exists', async () => {
    const { body } = await capturedTurnBody(GUID);

    expect(body.threadId).toBe(GUID);
  });

  it('still omits absent page context rather than sending it as null', async () => {
    const { body } = await capturedTurnBody(GUID);

    expect('workflowId' in body).toBe(false);
    expect('imageDocumentId' in body).toBe(false);
    expect(body.projectId).toBe('12345678-1234-1234-1234-123456789012');
  });

  it('sends the open picture as imageDocumentId when the user is in the picture editor', async () => {
    const { body } = await capturedTurnBody(GUID, {
      projectId: '12345678-1234-1234-1234-123456789012',
      imageDocumentId: '04d17aa1-84fe-4694-9b86-db1bf2d3997d',
    });

    expect(body.imageDocumentId).toBe('04d17aa1-84fe-4694-9b86-db1bf2d3997d');
    expect(body.projectId).toBe('12345678-1234-1234-1234-123456789012');
  });
});
