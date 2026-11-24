// @vitest-environment jsdom

/**
 * The assistant's per-call context on the picture editor, captured from the REAL client stack.
 *
 * The floating assistant is mounted once for the whole app and learns which picture is open from
 * two places: the context the page registers, and the route the shell derives from
 * \`usePathname()\` plus \`window.location.search\`. On the picture editor the ids are split — the
 * document is in the path and its project is only in the \`?project=\` query — so a body that
 * carries the document without the project is a body the server drops entirely (it validates the
 * picture against the project in context), which is the user's report: "he doesn't see any open
 * picture even though I'm on the image editor page with an open picture".
 *
 * These captures go through the real provider stack with a stubbed \`fetch\` — the same way
 * \`assistantSeamCapture.tsx\` drives it — because a body assembled by hand cannot show what the app
 * puts on the wire.
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { AssistantShellProvider } from './AssistantShellProvider';
import { AssistantPageContext } from './AssistantPageContext';
import { AssistantTrigger } from './AssistantTrigger';

const DOCUMENT_ID = '2fe24ae5-b075-4cd6-bde7-de84c3f20cad';
const PROJECT_ID = 'a09cda31-cf29-42da-bede-48f4f79d14b3';
const THREAD_ID = '5e2c8b71-3a4d-4f90-9c6e-1b7d0a84f2c3';

/** The route the browser is on. A test may move it, as a client-side navigation does. */
let pathname = `/editor/thumbnails/${DOCUMENT_ID}`;

vi.mock('next/navigation', () => ({
  usePathname: () => pathname,
  useRouter: () => ({ push: vi.fn(), replace: vi.fn(), refresh: vi.fn(), prefetch: vi.fn() }),
}));

class CaptureResizeObserver implements ResizeObserver {
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

let captured: Record<string, unknown> | null = null;
let originalFetch: typeof fetch;
let originalResizeObserver: typeof ResizeObserver;
let originalMatchMedia: typeof globalThis.matchMedia;
let originalScrollTo: typeof Element.prototype.scrollTo;

beforeEach(() => {
  captured = null;
  pathname = `/editor/thumbnails/${DOCUMENT_ID}`;
  originalFetch = globalThis.fetch;
  originalResizeObserver = global.ResizeObserver;
  originalMatchMedia = global.matchMedia;
  originalScrollTo = Element.prototype.scrollTo;

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
    if (href.includes('/assistant/threads') && (init?.method ?? 'GET').toUpperCase() === 'POST') {
      return Promise.resolve(
        new Response(JSON.stringify({ id: THREAD_ID }), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        }),
      );
    }
    if (href.includes('/assistant/threads')) {
      return Promise.resolve(
        new Response(
          JSON.stringify({ id: THREAD_ID, title: 'New chat', projectId: PROJECT_ID, messages: [] }),
          { status: 200, headers: { 'content-type': 'application/json' } },
        ),
      );
    }
    if (href.includes('/assistant/chat')) {
      captured = JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>;
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
    return Promise.resolve(new Response('{}', { status: 200 }));
  }) as typeof fetch;
});

afterEach(() => {
  cleanup();
  globalThis.fetch = originalFetch;
  global.ResizeObserver = originalResizeObserver;
  global.matchMedia = originalMatchMedia;
  Element.prototype.scrollTo = originalScrollTo;
});

/** Renders the shell around \`children\`, sends one turn, and returns the body it posted. */
async function captureTurn(children: React.ReactNode): Promise<Record<string, unknown>> {
  const user = userEvent.setup();
  render(
    <MantineProvider>
      <AssistantShellProvider>
        <AssistantTrigger />
        {children}
      </AssistantShellProvider>
    </MantineProvider>,
  );

  await user.click(screen.getByRole('button', { name: /open assistant/i }));
  await waitFor(
    () => {
      expect(screen.getAllByPlaceholderText(/message the assistant/i).length).toBeGreaterThan(0);
    },
    { timeout: 5000 },
  );

  const inputs = screen.getAllByPlaceholderText(/message the assistant/i);
  await user.type(inputs[inputs.length - 1], 'add a headline');
  const sendButtons = screen.getAllByRole('button', { name: /send/i });
  await user.click(sendButtons[sendButtons.length - 1]);
  await waitFor(() => expect(captured).not.toBeNull(), { timeout: 5000 });
  return captured as unknown as Record<string, unknown>;
}

describe('the assistant on the picture editor route', () => {
  it('sends the document from the path and the project from the query', async () => {
    window.history.replaceState({}, '', `/app${pathname}?project=${PROJECT_ID}`);

    const body = await captureTurn(null);
    const page = body.pageContext as Record<string, unknown> | undefined;

    expect(page?.kind).toBe('picture-editor');
    expect(page?.entityIds).toMatchObject({ documentId: DOCUMENT_ID, projectId: PROJECT_ID });
    expect(body.projectId).toBe(PROJECT_ID);
    expect(body.imageDocumentId).toBe(DOCUMENT_ID);
  }, 20000);

  it('picks the query up again once the router has written the URL', async () => {
    // A client-side navigation renders with the new pathname BEFORE the router applies the new URL
    // (it writes it in a useInsertionEffect). This is that render.
    window.history.replaceState({}, '', '/app/editor?view=pictures');

    const user = userEvent.setup();
    render(
      <MantineProvider>
        <AssistantShellProvider>
          <AssistantTrigger />
        </AssistantShellProvider>
      </MantineProvider>,
    );
    await waitFor(() => expect(screen.getByRole('button', { name: /open assistant/i })).toBeTruthy());

    // The router's own write, with no render of its own.
    window.history.replaceState({}, '', `/app${pathname}?project=${PROJECT_ID}`);

    await user.click(screen.getByRole('button', { name: /open assistant/i }));
    await waitFor(
      () => {
        expect(screen.getAllByPlaceholderText(/message the assistant/i).length).toBeGreaterThan(0);
      },
      { timeout: 5000 },
    );
    const inputs = screen.getAllByPlaceholderText(/message the assistant/i);
    await user.type(inputs[inputs.length - 1], 'add a headline');
    const sendButtons = screen.getAllByRole('button', { name: /send/i });
    await user.click(sendButtons[sendButtons.length - 1]);
    await waitFor(() => expect(captured).not.toBeNull(), { timeout: 5000 });

    const body = captured as unknown as Record<string, unknown>;
    expect((body.pageContext as Record<string, unknown>).entityIds).toMatchObject({
      documentId: DOCUMENT_ID,
      projectId: PROJECT_ID,
    });
    expect(body.projectId).toBe(PROJECT_ID);
  }, 20000);

  it('sends what the picture editor page itself registers', async () => {
    // The page knows both ids already (it loaded the document with them); nothing here depends on
    // the route being parsed correctly.
    pathname = '/editor/thumbnails/whatever';
    window.history.replaceState({}, '', '/app/editor/thumbnails/whatever');

    const body = await captureTurn(
      <AssistantPageContext projectId={PROJECT_ID} imageDocumentId={DOCUMENT_ID} />,
    );

    expect(body.projectId).toBe(PROJECT_ID);
    expect(body.imageDocumentId).toBe(DOCUMENT_ID);
  }, 20000);
});
