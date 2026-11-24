/**
 * Captures the assistant request body from the REAL client component stack.
 *
 * WHY THIS EXISTS
 *
 * The seam fixture shipped by the round before last was hand-written:
 * `{messages, tools, runConfig, projectId}`. The body the production stack actually posts is
 * `{messages, tools, unstable_assistantMessageId, threadId, parentId, runConfig, …}` — and the e2e
 * half of that guard built the adapter's `run()` arguments BY HAND, so it could never produce the
 * `threadId` that broke. The guard therefore passed while the defect it was meant to catch stayed
 * live: the same class of defect one level down.
 *
 * So the fixture is regenerated from THIS: the real
 * `AssistantShellProvider → AssistantDrawer → AssistantProvider → AssistantChatPanel` stack,
 * driven through a real DOM with a stub `fetch`.
 *
 * THE SCENARIO, AND WHY IT IS NOW THE BOUND ONE
 *
 * This used to leave `POST /assistant/threads` PENDING and send a turn inside the pre-thread
 * window, because that window was where the defect lived: the shell held `threadId === null`, the
 * adapter contributed its `__LOCALID_…` placeholder, and the turn either 400'd or was allocated a
 * thread the client never learned about.
 *
 * THAT WINDOW NO LONGER EXISTS IN THE SHIPPED UI, and that is the fix, not a regression in this
 * harness. The composer is now inert until a server thread is bound — every send path with it (the
 * Send control, the suggestion chips, the composer's `<form>`, and the attach control, which sends
 * its own turn once an upload lands) — so a pre-thread turn is no longer something the app can
 * produce, and a harness that insisted on one would only be pinning a body nothing sends. The
 * pre-thread window is guarded where it belongs, on the LOCAL thread-identity contract, by
 * `AssistantThreadIdentity.test.tsx` — which is the guard that would have caught this defect, since
 * the body it produced was correct by construction.
 *
 * So the capture binds first — the stubbed `createThread` resolves the way the server answers — and
 * then sends. What it pins is the body the shipped client actually puts on the wire, `threadId`
 * included, and `CAPTURE_THREAD_ID` is the exact value the e2e spec asserts against the fixture.
 *
 * WHAT THIS STILL DOES NOT DO
 *
 * It does not compare itself to the fixture: a capture that also decides whether it matches cannot
 * go red when the client changes, which is the whole point of moving this out of a hand-written
 * `adapter.run({...})` call. The e2e spec does the comparing.
 *
 * VOLATILE KEYS
 *
 * assistant-ui mints `unstable_assistantMessageId` and `parentId` fresh on every turn, so the
 * captured body cannot be compared literally. Those two values are replaced with the fixed,
 * obviously-synthetic placeholders in `VOLATILE_KEYS` — and ONLY their values: a key that stops
 * being posted is left absent, so the comparison still notices it. Every other byte is what the
 * client actually put on the wire.
 */

import { useEffect } from 'react';
import { expect } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { AssistantShellProvider, useAssistant } from './AssistantShellProvider';
import { AssistantTrigger } from './AssistantTrigger';

/** The page context the capture opens the assistant from — a fixed GUID so the fixture is stable. */
export const CAPTURE_PROJECT_ID = '11111111-1111-1111-1111-111111111111';

/**
 * The thread the stubbed `POST /assistant/threads` hands back. A fixed GUID rather than a generated
 * one, because the e2e spec asserts the captured body against the shared fixture, and a bound turn
 * carries this id: a random one would make the fixture unstable for a reason that has nothing to do
 * with the seam.
 */
export const CAPTURE_THREAD_ID = '5e2c8b71-3a4d-4f90-9c6e-1b7d0a84f2c3';

/** The turn text the capture sends; the endpoint resolves this out of the AI-SDK `messages[]`. */
export const CAPTURE_TURN_TEXT = 'hello';

/**
 * The keys whose VALUES are minted per turn by assistant-ui rather than derived from app state.
 * Replace the value, never the key — see the module remarks.
 */
export const VOLATILE_KEYS: Readonly<Record<string, string>> = {
  unstable_assistantMessageId: '<client-message-id>',
  parentId: '<client-parent-id>',
};

/** Replaces each present volatile key's value with its fixed placeholder. */
export function normalizeAssistantRequestBody(
  body: Record<string, unknown>,
): Record<string, unknown> {
  const normalized: Record<string, unknown> = { ...body };
  for (const [key, placeholder] of Object.entries(VOLATILE_KEYS)) {
    if (key in normalized) normalized[key] = placeholder;
  }
  return normalized;
}

/** The localized SSE reply, so the turn completes and the panel settles. */
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

class CaptureResizeObserver implements ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

/** Stands in for a page: puts the assistant's per-call project context into the shell. */
function CapturePageContext({ projectId }: { projectId: string }) {
  const { setContext } = useAssistant();
  useEffect(() => {
    setContext({ projectId });
  }, [setContext, projectId]);
  return null;
}

export interface AssistantSeamCapture {
  /** The exact body the client posted once its thread was bound — the only turn it can send. */
  boundTurn: Record<string, unknown>;
  /** The same body with the two per-turn ids replaced by their placeholders. */
  boundTurnNormalized: Record<string, unknown>;
  /** Every request URL the stack issued, in order — makes the scenario self-describing. */
  requestUrls: string[];
}

/**
 * Renders the real drawer stack, opens the assistant, waits for the server thread to bind, sends
 * one turn, and returns the body that went on the wire.
 *
 * Must run under jsdom (the web suite's default environment).
 */
export async function captureAssistantSeamBody(): Promise<AssistantSeamCapture> {
  const originalResizeObserver = global.ResizeObserver;
  const originalMatchMedia = global.matchMedia;
  const originalFetch = globalThis.fetch;
  const originalScrollTo = Element.prototype.scrollTo;

  const requestUrls: string[] = [];
  let boundTurn: Record<string, unknown> | null = null;

  global.ResizeObserver = CaptureResizeObserver as unknown as typeof ResizeObserver;
  global.matchMedia = ((query: string) =>
    ({
      matches: false,
      media: query,
      onchange: null,
      addListener: () => {},
      removeListener: () => {},
      addEventListener: () => {},
      removeEventListener: () => {},
      dispatchEvent: () => true,
    }) as MediaQueryList) as typeof global.matchMedia;
  Element.prototype.scrollTo = (() => {}) as typeof Element.prototype.scrollTo;

  globalThis.fetch = ((url: string | URL | Request, init?: RequestInit) => {
    const href = String(url);
    requestUrls.push(href);

    // The thread the shell binds to first. The capture cannot send a turn until this has resolved —
    // that is the fix — so it answers the way the server does rather than hanging.
    if (href.includes('/assistant/threads') && (init?.method ?? 'GET').toUpperCase() === 'POST') {
      return Promise.resolve(
        new Response(JSON.stringify({ id: CAPTURE_THREAD_ID }), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        }),
      );
    }

    // The history load that follows the bind. An empty conversation, so the panel is the one the
    // turn below is typed into.
    if (href.includes('/assistant/threads/')) {
      return Promise.resolve(
        new Response(
          JSON.stringify({
            id: CAPTURE_THREAD_ID,
            title: 'New chat',
            projectId: CAPTURE_PROJECT_ID,
            messages: [],
          }),
          { status: 200, headers: { 'content-type': 'application/json' } },
        ),
      );
    }

    if (href.includes('/assistant/chat')) {
      boundTurn = JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>;
      return Promise.resolve(
        new Response(STUB_SSE, {
          status: 200,
          headers: {
            'content-type': 'text/event-stream',
            'x-vercel-ai-ui-message-stream': 'v1',
          },
        }),
      );
    }

    // Nothing else should be reached by this scenario; answer plainly rather than hanging.
    return Promise.resolve(new Response('{}', { status: 200 }));
  }) as typeof fetch;

  try {
    const user = userEvent.setup();
    render(
      <MantineProvider>
        <AssistantShellProvider>
          <CapturePageContext projectId={CAPTURE_PROJECT_ID} />
          <AssistantTrigger />
        </AssistantShellProvider>
      </MantineProvider>,
    );

    await user.click(screen.getByRole('button', { name: /open assistant/i }));

    // Waiting for the ready placeholder IS waiting for the bind: while no thread is bound the
    // composer shows the "waiting" placeholder and is disabled, so there is nothing to type into.
    await waitFor(
      () => {
        expect(screen.getAllByPlaceholderText(/message the assistant/i).length).toBeGreaterThan(0);
      },
      { timeout: 5000 },
    );

    const inputs = screen.getAllByPlaceholderText(/message the assistant/i);
    const input = inputs[inputs.length - 1];
    expect(
      (input as HTMLTextAreaElement).disabled,
      'The harness reached the composer before a thread was bound. The shipped UI blocks every send ' +
        'path until one is, so a capture taken here would describe a body the app can no longer ' +
        'produce — see AssistantThreadIdentity.test.tsx for the guard on that window.',
    ).toBe(false);

    await user.type(input, CAPTURE_TURN_TEXT);

    const sendButtons = screen.getAllByRole('button', { name: /send/i });
    await user.click(sendButtons[sendButtons.length - 1]);

    await waitFor(
      () => {
        expect(boundTurn).not.toBeNull();
      },
      { timeout: 5000 },
    );
  } finally {
    cleanup();
    global.ResizeObserver = originalResizeObserver;
    global.matchMedia = originalMatchMedia;
    globalThis.fetch = originalFetch;
    Element.prototype.scrollTo = originalScrollTo;
  }

  const captured = boundTurn as Record<string, unknown> | null;
  if (captured === null) {
    throw new Error(
      'The real drawer stack never posted a chat turn, so there is no body to report. This is a ' +
        'capture failure, not a seam failure: check that the composer still submits once a server ' +
        'thread is bound.',
    );
  }

  return {
    boundTurn: captured,
    boundTurnNormalized: normalizeAssistantRequestBody(captured),
    requestUrls,
  };
}
