// @vitest-environment jsdom

/**
 * Does the shell's page context survive the server render → hydration boundary with the picture
 * editor's \`?project=\` query?
 *
 * The shell derives page info from \`usePathname()\` plus \`window.location.search\`, memoized on the
 * pathname alone. During the server render there is no \`window\`, so the search reads as empty — the
 * document id (in the path) survives and the project id (in the query) does not. This captures what
 * the shell reports after hydration, which is when a real page sends its first turn.
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { act } from '@testing-library/react';
import { renderToString } from 'react-dom/server';
import { hydrateRoot } from 'react-dom/client';
import { MantineProvider } from '@mantine/core';
import { AssistantShellProvider, useAssistant } from './AssistantShellProvider';

const DOCUMENT_ID = '2fe24ae5-b075-4cd6-bde7-de84c3f20cad';
const PROJECT_ID = 'a09cda31-cf29-42da-bede-48f4f79d14b3';
const PATHNAME = `/editor/thumbnails/${DOCUMENT_ID}`;

vi.mock('next/navigation', () => ({
  usePathname: () => PATHNAME,
  useRouter: () => ({ push: vi.fn(), replace: vi.fn(), refresh: vi.fn(), prefetch: vi.fn() }),
}));

class CaptureResizeObserver implements ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

let originalResizeObserver: typeof ResizeObserver;
let originalMatchMedia: typeof globalThis.matchMedia;

beforeEach(() => {
  originalResizeObserver = global.ResizeObserver;
  originalMatchMedia = global.matchMedia;
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
});

afterEach(() => {
  global.ResizeObserver = originalResizeObserver;
  global.matchMedia = originalMatchMedia;
  document.body.innerHTML = '';
});

describe('page info across the server render → hydration boundary', () => {
  it('reports the query-borne project after hydrating the picture editor page', async () => {
    // The server render cannot see the query (no window); the browser's URL has it.
    window.history.replaceState({}, '', `/app${PATHNAME}`);

    const page = { current: null as ReturnType<typeof useAssistant> | null };
    function Probe() {
      page.current = useAssistant();
      return null;
    }

    const tree = (
      <MantineProvider>
        <AssistantShellProvider>
          <Probe />
        </AssistantShellProvider>
      </MantineProvider>
    );

    const html = renderToString(tree);
    const container = document.createElement('div');
    container.innerHTML = html;
    document.body.appendChild(container);

    // Now the browser URL the page is really on.
    window.history.replaceState({}, '', `/app${PATHNAME}?project=${PROJECT_ID}`);

    await act(async () => {
      hydrateRoot(container, tree);
    });

    // The project id lives only in the query, which the server render cannot see.
    expect(page.current?.pageInfo.entityIds).toEqual({ documentId: DOCUMENT_ID, projectId: PROJECT_ID });
  }, 20000);
});
