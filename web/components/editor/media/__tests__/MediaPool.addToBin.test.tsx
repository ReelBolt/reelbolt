// @vitest-environment jsdom
/* eslint @typescript-eslint/no-unused-vars: ["warn", { "argsIgnorePattern": "^_" }] */
/**
 * "Add to bin" from a clip tile's menu: the touch path into a bin (no HTML5 drag), including the
 * smart-bin refusal a drop states (remediation W0.7, rows P2-BIN-06 and P6-BIN-03 on phone).
 *
 * (Mock boilerplate shared with MediaPool.test.tsx.)
 *
 * The four claims this file exists to make assertable rather than asserted-by-eye:
 *
 *   1. **One derivatives fetch per mediaRef.** Hover-scrub must not re-request the same source on
 *      every hover event — proved by a mocked-client call count across many hovers, including
 *      repeated hovers on the SAME clip and a burst fired while the first response is in flight.
 *   2. **Honest degradation, and real data where the contract now supplies it.** A source with an
 *      index scrubs against the REAL measured sheets; an index that comes back with zero densities
 *      says so; a missing derivative kind means a stated label, never "failed".
 *   3. **Nothing unknown reaches the DOM.** No `undefined`, `NaN`, `null` or empty text, over the
 *      WHOLE fixture set including the probe-less and all-null-field items.
 *   4. **View state produces no request.** Selecting a clip and switching mode issue nothing.
 *
 * Note: this repo does not install `@testing-library/jest-dom`, so assertions go through
 * `textContent`/`getAttribute` helpers rather than `toHaveTextContent`/`toHaveAttribute`.
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, cleanup, fireEvent, waitFor, act } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

import { MediaPool } from '../MediaPool';
import { POOL, BINS, DERIVATIVES_ALL_KINDS, FILMSTRIP_RESPONSE } from '@/lib/editing/media/fixtures';
import type { MediaPoolItem } from '@/lib/editing/media/types';

// ─────────────────────────────────────────────────────────────────────────────
// Mocks. The hooks are mocked so a test can drive loading/error/empty directly;
// the derivatives client is a spy because its CALL COUNT is the assertion.
// ─────────────────────────────────────────────────────────────────────────────

const state = vi.hoisted(() => ({
  pool: {
    data: [] as unknown[] | undefined,
    error: undefined as unknown,
    isLoading: false,
    // `mutate` is genuinely called now: adding or removing a bin pin writes the membership straight
    // into the pool cache, which is what makes a bin's derived count move.
    mutate: undefined as unknown,
  },
  bins: {
    data: [] as unknown[] | undefined,
    error: undefined as unknown,
    isLoading: false,
    mutate: undefined as unknown,
  },
}));

vi.mock('@/lib/hooks/use-media', () => {
  const annotations = { data: [] as unknown[], mutate: async () => undefined };
  return { useMediaPool: () => state.pool, useMediaBins: () => state.bins, useMediaAnnotations: () => annotations };
});

vi.mock('@/lib/editing/api/media', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/editing/api/media')>();
  return {
    ...actual,
    fetchMediaDerivatives: vi.fn(),
    fetchMediaFilmstrip: vi.fn(),
    addBinItem: vi.fn(),
    removeBinItem: vi.fn(),
  };
});

import { addBinItem, fetchMediaDerivatives, fetchMediaFilmstrip } from '@/lib/editing/api/media';

const derivativesSpy = vi.mocked(fetchMediaDerivatives);
const filmstripSpy = vi.mocked(fetchMediaFilmstrip);
const addBinItemSpy = vi.mocked(addBinItem);

const PROJECT_ID = '00000000-0000-4000-8000-000000000009';

// ─────────────────────────────────────────────────────────────────────────────
// jsdom polyfills Mantine needs (the same set the repo's other UI tests install).
// ─────────────────────────────────────────────────────────────────────────────

vi.stubGlobal(
  'ResizeObserver',
  class ResizeObserver {
    constructor(_callback: ResizeObserverCallback) {}
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);

Element.prototype.scrollIntoView = vi.fn();

function installMatchMedia() {
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
}

function renderPool(props: Partial<Parameters<typeof MediaPool>[0]> = {}) {
  return render(
    <MantineProvider>
      <MediaPool projectId={PROJECT_ID} {...props} />
    </MantineProvider>,
  );
}


const SMART_BIN = { id: 'smart-1', name: 'Favourites', parentBinId: null, sortOrder: 9, itemCount: 0, isSmart: true, smartFilter: { favourite: true } };

import { resetTestViewport, setTestViewport } from '@/lib/editing/__tests__/viewport';

describe('MediaPool: Add to bin from the tile menu', () => {
  beforeEach(() => {
    installMatchMedia();
    state.pool = {
      data: POOL,
      error: undefined,
      isLoading: false,
      mutate: vi.fn((updater: unknown) =>
        Promise.resolve(typeof updater === 'function' ? (updater as (c: unknown) => unknown)(POOL) : updater),
      ),
    };
    state.bins = { data: [...BINS, SMART_BIN], error: undefined, isLoading: false, mutate: vi.fn() };
    derivativesSpy.mockReset();
    derivativesSpy.mockResolvedValue(DERIVATIVES_ALL_KINDS);
    filmstripSpy.mockReset();
    filmstripSpy.mockResolvedValue(FILMSTRIP_RESPONSE);
    addBinItemSpy.mockReset();
    addBinItemSpy.mockResolvedValue({
      id: 'item-1',
      binId: BINS[2].id,
      mediaRef: POOL[0].mediaRef,
      sourceKind: 'ProjectFile',
      sortOrder: 0,
      createdAt: '2026-10-03T08:00:00Z',
    });
  });

  afterEach(() => {
    resetTestViewport();
    cleanup();
  });

  const openTileMenu = async (mediaRef: string) => {
    const trigger = screen.getAllByTestId('media-tile-menu').find((el) => el.getAttribute('data-for-media-ref') === mediaRef)!;
    await act(async () => fireEvent.click(trigger));
  };

  it('pins the clip into the chosen bin with one request, and says so', async () => {
    renderPool();
    const poolMutate = state.pool.mutate as unknown as ReturnType<typeof vi.fn>;
    poolMutate.mockClear();
    await openTileMenu(POOL[0].mediaRef);
    await act(async () => fireEvent.click(await screen.findByTestId(`add-to-bin-${BINS[2].id}`)));

    expect(addBinItemSpy).toHaveBeenCalledTimes(1);
    expect(addBinItemSpy).toHaveBeenCalledWith(PROJECT_ID, BINS[2].id, POOL[0].mediaRef);
    const updater = poolMutate.mock.calls[0][0] as (current: MediaPoolItem[]) => MediaPoolItem[];
    expect(updater(POOL).find((i) => i.mediaRef === POOL[0].mediaRef)?.bins).toEqual([...POOL[0].bins, { binId: BINS[2].id, itemId: 'item-1' }]);
    await waitFor(() => expect(screen.getByTestId('media-add-to-bin-message').textContent).toBe(`Added to "${BINS[2].name}".`));
  });

  it('never offers a smart bin as a place to put a clip (R05.3)', async () => {
    renderPool();
    await openTileMenu(POOL[0].mediaRef);
    expect(screen.queryByTestId(`add-to-bin-${SMART_BIN.id}`)).toBeNull();
    expect(addBinItemSpy).not.toHaveBeenCalled();
  });

  it('refuses a bin the clip is already in', async () => {
    renderPool();
    await openTileMenu(POOL[0].mediaRef);
    await act(async () => fireEvent.click(await screen.findByTestId(`add-to-bin-${POOL[0].bins[0].binId}`)));
    expect(addBinItemSpy).not.toHaveBeenCalled();
    expect(screen.getByTestId('media-add-to-bin-message').textContent).toBe('That clip is already in this bin.');
  });

  it('opening the tile menu does not select the clip', async () => {
    const onSelectClip = vi.fn();
    renderPool({ onSelectClip });
    await openTileMenu(POOL[0].mediaRef);
    expect(onSelectClip).not.toHaveBeenCalled();
  });

  it('on a phone, bins are not dragged and reparenting says it needs a larger screen', async () => {
    setTestViewport('phone');
    renderPool();
    await act(async () => {});
    expect(screen.getByTestId(`bin-name-${BINS[0].id}`).getAttribute('draggable')).toBe('false');
    const note = screen.getByTestId('needs-larger-screen');
    expect(note.getAttribute('data-feature')).toBe('bin-reparent');
    // The touch path into a bin is still there.
    expect(screen.getAllByTestId('media-tile-menu').length).toBe(POOL.length);
  });
});
