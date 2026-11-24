// @vitest-environment jsdom
/**
 * The timeline route flushes on route change (eng D10): leaving the page (the route component
 * unmounts) sends the pending edit at once instead of dropping it with the debounce timer.
 */
import { Suspense } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { EditorStore } from '@/lib/editing/store';
import { standardDoc } from '@/lib/editing/__tests__/fixtures';

vi.mock('next/link', () => ({
  default: ({ children, href, ...rest }: { children: React.ReactNode; href: string }) => <a href={href} {...rest}>{children}</a>,
}));
// `standardDoc` names things `c1`/`timeline-1`, and the sync client checks a document with the strict
// engine invariants before it saves. Only the Guid id shape is relaxed here (it is pinned in
// `engine/__tests__/apply.contract.test.ts`); every structural invariant still runs. The loose cast keeps
// this compiling before the engine's options argument exists (the extra argument is then ignored).
vi.mock('@/lib/editing/engine', async (importOriginal) => {
  const engine = await importOriginal<typeof import('@/lib/editing/engine')>();
  const strict = engine.assertInvariants as (...args: unknown[]) => unknown;
  return { ...engine, assertInvariants: (doc: Parameters<typeof engine.assertInvariants>[0]) => strict(doc, { ids: 'opaque' }) };
});
// The sync client only sends while a signed-in session is known (the account cookie), so the route test signs one in.
vi.mock('@/lib/hooks/use-auth', async (importOriginal) => ({ ...(await importOriginal<typeof import('@/lib/hooks/use-auth')>()), getUserFromCookie: () => ({ email: 'me@x.test' }) }));
const mounted = { store: null as EditorStore | null };
vi.mock('@/components/editor/EditorWorkspace', () => ({
  EditorWorkspace: ({ store }: { store: EditorStore }) => {
    mounted.store = store;
    return <div data-testid="workspace" />;
  },
}));

import TimelineEditorPage from './page';

const resolved = <T,>(value: T) => Object.assign(Promise.resolve(value), { status: 'fulfilled' as const, value });

const fetchMock = vi.fn(async (_url: string, init?: RequestInit) => {
  const body = init?.method === 'PATCH' ? { revision: 4 } : { revision: 3, document: standardDoc(), seedStepResultId: null };
  return new Response(JSON.stringify({ id: 't', projectId: 'p', name: 'n', createdAt: '', updatedAt: '', ...body }), { status: 200 });
});

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  vi.stubGlobal('fetch', fetchMock);
  fetchMock.mockClear();
  mounted.store = null;
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('the timeline route', () => {
  it('flushes a pending edit when the route unmounts', async () => {
    const view = render(
      <MantineProvider>
        <Suspense fallback={null}>
          <TimelineEditorPage params={resolved({ id: 'p', timelineId: 't' })} />
        </Suspense>
      </MantineProvider>,
    );
    await screen.findByTestId('workspace');
    const store = mounted.store;
    expect(store).not.toBeNull();
    act(() => store?.dispatch({ type: 'setProperty', itemId: 'c1', key: 'name', value: 'renamed' }));

    view.unmount(); // a route change, well inside the 500 ms debounce
    await act(async () => {
      await Promise.resolve();
    });

    const patches = fetchMock.mock.calls.filter(([, init]) => init?.method === 'PATCH');
    expect(patches).toHaveLength(1);
    expect(String(patches[0][1]?.body)).toContain('"renamed"');
  });
});
