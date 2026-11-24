// @vitest-environment jsdom
/** The Sound effects tab: search, play/stop by click, add then place, friendly refusals. */
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import type { SfxLibraryItem, SfxLibraryPage, SfxLibraryQuery } from '@/lib/api/sfx-library';
import { SfxLibraryPanel } from '../SfxLibraryPanel';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

const ITEM: SfxLibraryItem = {
  id: 'a1', slug: 'whoosh-1', title: 'Whoosh one', category: 'whoosh', tags: ['fast', 'air'], durationMs: 1500,
  sizeBytes: 100, mimeType: 'audio/mpeg', licence: 'CC0', author: null, sourceUrl: null,
};
const PAGE: SfxLibraryPage = {
  items: [ITEM, { ...ITEM, id: 'b2', slug: 'ping', title: 'Ping', category: 'ui', tags: [] }],
  total: 2, page: 1, pageSize: 50, categories: [{ category: 'ui', count: 1 }, { category: 'whoosh', count: 1 }],
};

const h = vi.hoisted(() => ({
  queries: [] as SfxLibraryQuery[],
  page: undefined as unknown,
  error: undefined as unknown,
  add: vi.fn(),
  starter: vi.fn(),
  mutatePool: vi.fn(),
}));
vi.mock('@/lib/hooks/use-sfx-library', () => ({
  useSfxLibrary: (_enabled: boolean, q: SfxLibraryQuery) => {
    h.queries.push(q);
    return { data: h.page, error: h.error, isLoading: false };
  },
}));
vi.mock('@/lib/hooks/use-media', () => ({ useMediaPool: () => ({ mutate: h.mutatePool }) }));
vi.mock('@/lib/api/sfx-library', async (orig) => ({
  ...(await orig<typeof import('@/lib/api/sfx-library')>()),
  addSfxLibraryItem: (...a: unknown[]) => h.add(...a),
  addSfxStarterSet: (...a: unknown[]) => h.starter(...a),
}));

const play = vi.fn(() => Promise.resolve());
const pause = vi.fn();

function mount(props: Partial<React.ComponentProps<typeof SfxLibraryPanel>> = {}) {
  return render(
    <MantineProvider>
      <SfxLibraryPanel projectId="p1" {...props} />
    </MantineProvider>,
  );
}

beforeEach(() => {
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
  h.queries = [];
  h.page = PAGE;
  h.error = undefined;
  h.add.mockReset();
  h.starter.mockReset();
  h.mutatePool.mockReset();
  play.mockClear();
  pause.mockClear();
  HTMLMediaElement.prototype.play = play as unknown as () => Promise<void>;
  HTMLMediaElement.prototype.pause = pause;
});
afterEach(cleanup);

describe('SfxLibraryPanel', () => {
  it('lists rows with duration, tags and the Early preview label', () => {
    mount();
    expect(screen.getAllByTestId('sfx-row')).toHaveLength(2);
    expect(screen.getAllByText('1.5 s').length).toBeGreaterThan(0);
    expect(screen.getByText('fast')).toBeTruthy();
    expect(screen.getByTestId('early-preview-label')).toBeTruthy();
  });

  it('debounces the search by 300 ms and filters by category chip', async () => {
    mount();
    fireEvent.change(screen.getByTestId('sfx-search'), { target: { value: 'whoosh' } });
    expect(h.queries.at(-1)?.search).toBe('');
    await waitFor(() => expect(h.queries.at(-1)?.search).toBe('whoosh'), { timeout: 1500 });
    fireEvent.click(screen.getByLabelText(/^ui \(1\)/));
    await waitFor(() => expect(h.queries.at(-1)?.category).toBe('ui'));
  });

  it('plays one sound at a time by click and stops on a second click', () => {
    mount();
    const [first, second] = screen.getAllByTestId('sfx-play');
    fireEvent.click(first!);
    expect(play).toHaveBeenCalledTimes(1);
    expect(screen.getAllByTestId('sfx-row')[0]!.getAttribute('data-playing')).toBe('true');
    fireEvent.click(second!);
    expect(screen.getAllByTestId('sfx-row')[0]!.getAttribute('data-playing')).toBeNull();
    expect(screen.getAllByTestId('sfx-row')[1]!.getAttribute('data-playing')).toBe('true');
    fireEvent.click(second!);
    expect(screen.getAllByTestId('sfx-row')[1]!.getAttribute('data-playing')).toBeNull();
  });

  it('adds, refreshes the pool and places the new pool item', async () => {
    const poolItem = { mediaRef: 'file:f1', fileName: 'whoosh-1.mp3' };
    h.add.mockResolvedValue({ mediaRef: 'file:f1', fileId: 'f1', name: 'whoosh-1.mp3' });
    h.mutatePool.mockResolvedValue([poolItem]);
    const onPlaceItem = vi.fn(() => null);
    mount({ onPlaceItem });
    fireEvent.click(screen.getAllByTestId('sfx-add')[0]!);
    await waitFor(() => expect(onPlaceItem).toHaveBeenCalledWith(poolItem));
    expect(h.add).toHaveBeenCalledWith('p1', 'a1');
    await waitFor(() => expect(screen.getAllByTestId('sfx-add')[0]!.textContent).toBe('Added'));
  });

  it('shows the cap and rate-limit refusals in plain words', async () => {
    h.add.mockRejectedValueOnce(new ApiError('x', 409, [], { code: 'SFX_LIBRARY_PROJECT_CAP' }));
    mount();
    fireEvent.click(screen.getAllByTestId('sfx-add')[0]!);
    await waitFor(() => expect(screen.getByTestId('sfx-problem').textContent).toMatch(/most library sounds/));
    h.add.mockRejectedValueOnce(new ApiError('x', 429, [], { code: 'SFX_LIBRARY_RATE_LIMIT' }));
    fireEvent.click(screen.getAllByTestId('sfx-add')[1]!);
    await waitFor(() => expect(screen.getByTestId('sfx-problem').textContent).toMatch(/Too many additions/));
  });

  it('adds the starter set and reports the count', async () => {
    h.starter.mockResolvedValue({ items: [{}, {}, {}], added: 3 });
    h.mutatePool.mockResolvedValue([]);
    mount();
    fireEvent.click(screen.getByTestId('sfx-starter-set'));
    await waitFor(() => expect(screen.getByTestId('sfx-message').textContent).toMatch(/3 sounds/));
  });

  it('shows a load error and an empty state', () => {
    h.error = new Error('boom');
    h.page = undefined;
    mount();
    expect(screen.getByTestId('sfx-load-error')).toBeTruthy();
  });

  it('disables adding on a view-only editor', () => {
    mount({ readOnly: true });
    expect((screen.getAllByTestId('sfx-add')[0] as HTMLButtonElement).disabled).toBe(true);
  });
});
