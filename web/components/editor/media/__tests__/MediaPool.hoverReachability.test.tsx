// @vitest-environment jsdom
/* eslint @typescript-eslint/no-unused-vars: ["warn", { "argsIgnorePattern": "^_" }] */
/**
 * P2-F1 — the filmstrip strip must be REACHABLE, with a pointer and with a keyboard.
 *
 * ## Why this file exists
 *
 * `MediaPool.test.tsx` was green, `tsc` was green, lint was green and the production build was
 * green — while the strip was impossible to use. Every one of those suites drove the control the
 * same way: fire `mouseenter` on a tile, then read the strip. That works, because hovering a tile
 * is what puts the strip on screen. It never drove the ONE event the defect lived on: the pointer
 * LEAVING that tile.
 *
 * The strip's subject used to be the tile's hover, and the tile cleared it in `onMouseLeave`. The
 * strip sits below the grid, so every path to it begins by leaving the tile — the cell unmounted
 * one event before the pointer could arrive, and QA's browser probe measured it exactly:
 *
 *     cell present after tile hover = yes | after gradual move -> cell count=0, idle count=1
 *
 * jsdom fires no such event by itself: there is no pointer, so nothing implicitly produces a
 * `mouseleave`. The event has to be DISPATCHED, which is what this file does, and which is why the
 * whole wave's suite was blind to the bug rather than wrong about it.
 *
 * ## The three claims
 *
 *   1. **The journey survives.** A `mouseout`/`mouseleave` on the tile — the raw event the defect
 *      keyed on, and the real event a browser produces when the pointer crosses from the tile to
 *      the strip — must leave the cell mounted.
 *   2. **The journey then WORKS.** The strip is a control, not a readout: after the crossing, a
 *      pointer move over the cell must actually scrub to a different real thumbnail. jsdom supplies
 *      no layout, so the cell's box is measured by stubbing `getBoundingClientRect` — the component
 *      reads a real rectangle rather than a zero one.
 *   3. **The keyboard needs no pointer at all.** Focus adopts a clip and focus keeps it, so tabbing
 *      from a tile to the slider and pressing an arrow key scrubs. A scrubber that only works with
 *      a mouse is not finished.
 *
 * It also asserts the inverse, so the guard cannot pass by simply never releasing: when the pointer
 * AND the focus have both left the region, the strip returns to its stated idle state.
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
// jsdom polyfills — the same set the repo's other UI tests install, plus one more
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

/**
 * jsdom 26 ships no `PointerEvent`. Without this line `fireEvent.pointerMove` resolves
 * `window['PointerEvent']` to `undefined`, falls back to `window.Event`, and **silently drops
 * `clientX`** — the one coordinate the scrub is computed from, which would turn a real pointer
 * assertion into a test of nothing. `MouseEvent` accepts `clientX` in its init, and React attaches
 * its `pointermove` listener by event NAME, so it does not care which constructor produced it.
 */
if (typeof window.PointerEvent === 'undefined') {
  (window as unknown as { PointerEvent: typeof MouseEvent }).PointerEvent = window.MouseEvent;
}

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

// ─────────────────────────────────────────────────────────────────────────────
// The hooks and the client are mocked exactly as `MediaPool.test.tsx` mocks them,
// so this file exercises the SAME component wiring against the SAME fixtures.
// ─────────────────────────────────────────────────────────────────────────────

const state = vi.hoisted(() => ({
  pool: { data: [] as unknown[] | undefined, error: undefined as unknown, isLoading: false, mutate: undefined as unknown },
  bins: { data: [] as unknown[] | undefined, error: undefined as unknown, isLoading: false, mutate: undefined as unknown },
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

import { fetchMediaDerivatives, fetchMediaFilmstrip } from '@/lib/editing/api/media';

const derivativesSpy = vi.mocked(fetchMediaDerivatives);
const filmstripSpy = vi.mocked(fetchMediaFilmstrip);

const PROJECT_ID = '00000000-0000-4000-8000-000000000009';

function renderPool() {
  return render(
    <MantineProvider>
      <MediaPool projectId={PROJECT_ID} />
    </MantineProvider>,
  );
}

const tiles = () => screen.queryAllByTestId('media-clip-tile');
const textOf = (node: Element | null): string => (node?.textContent ?? '').trim();

/** Stubs the cell's box so a pointer move has a real rectangle to be a fraction OF. */
function stubBox(element: Element, width: number, height: number) {
  element.getBoundingClientRect = () =>
    ({
      left: 0,
      top: 0,
      right: width,
      bottom: height,
      width,
      height,
      x: 0,
      y: 0,
      toJSON: () => ({}),
    }) as DOMRect;
}

describe('MediaPool filmstrip reachability (P2-F1)', () => {
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
    state.bins = { data: BINS, error: undefined, isLoading: false, mutate: vi.fn() };
    derivativesSpy.mockReset();
    derivativesSpy.mockResolvedValue(DERIVATIVES_ALL_KINDS);
    filmstripSpy.mockReset();
    filmstripSpy.mockResolvedValue(FILMSTRIP_RESPONSE);
  });

  afterEach(() => cleanup());

  /**
   * Claim 1 — the cell survives the pointer leaving the tile.
   *
   * This is the exact event the defect lived on, dispatched by hand because jsdom will not produce
   * it. Against the old wiring (`onMouseLeave={() => onHover(null)}` on the tile) the cell is gone
   * by the second assertion and this test fails, which is the whole point of it existing.
   */
  it('keeps the cell mounted while the pointer travels from the tile to the strip', async () => {
    renderPool();

    const tile = tiles()[0];
    expect(textOf(screen.getByTestId('filmstrip-idle'))).toContain('Hover a clip');

    // Enter the tile the way a browser does: the pointer arriving produces `mouseover`, which is
    // the event React synthesises `onMouseEnter` from.
    await act(async () => {
      fireEvent.mouseOver(tile, { relatedTarget: document.body });
    });

    const cell = await screen.findByTestId('filmstrip-cell');
    expect(cell.getAttribute('data-thumb-index')).toBe('0');

    // The journey itself, dispatched by hand because jsdom has no pointer and therefore produces no
    // boundary event of its own.
    //
    // `mouseout` carrying the strip as its `relatedTarget` is what a browser actually emits when the
    // pointer crosses from the tile to the strip: the TILE is left, the REGION is not. React
    // synthesises `onMouseEnter`/`onMouseLeave` from exactly this pair, comparing `relatedTarget`
    // against each ancestor — so this is the faithful model, and it is the assertion that fails
    // against the old wiring, where the tile cleared the subject in its own `onMouseLeave`.
    //
    // Deliberately NOT `fireEvent.mouseLeave(tile)`. Measured, not assumed: RTL dispatches a raw
    // non-bubbling `mouseleave` on the tile, which reaches React's root capture listener and makes
    // React fire `onMouseLeave` on every ancestor WITHOUT a `relatedTarget` check — including the
    // region. That is a jsdom/RTL artefact, not browser behaviour (`mouseleave` is boundary-based:
    // it does not fire on an element the pointer never left). Asserting it here would force the
    // region's release handler to be deleted, i.e. it would demand a wrong fix.
    await act(async () => {
      fireEvent.mouseOut(tile, { relatedTarget: cell });
    });

    expect(screen.queryByTestId('filmstrip-cell')).not.toBeNull();
    expect(screen.queryByTestId('filmstrip-idle')).toBeNull();
    expect(screen.queryByTestId('filmstrip-subject')).not.toBeNull();
  });

  /** Claim 2 — and the pointer can then actually scrub, which is what the control is FOR. */
  it('scrubs to a different real thumbnail after the pointer has crossed to the strip', async () => {
    renderPool();

    const tile = tiles()[0];
    await act(async () => {
      fireEvent.mouseOver(tile, { relatedTarget: document.body });
    });

    const cell = await screen.findByTestId('filmstrip-cell');
    expect(cell.getAttribute('data-thumb-index')).toBe('0');
    // The range is the fixture index's MEASURED 137 thumbnails (100 + 37), never a probe duration.
    expect(cell.getAttribute('aria-valuemax')).toBe('137');
    const origin = (cell as HTMLElement).style.backgroundPosition;

    // Cross to the strip…
    await act(async () => {
      fireEvent.mouseOut(tile, { relatedTarget: cell });
    });

    // …and scrub along it. The cell's box is stubbed because jsdom lays nothing out: a real width is
    // what turns `clientX` into a fraction of the strip.
    stubBox(cell, 200, 120);
    await act(async () => {
      fireEvent.pointerMove(cell, { clientX: 180 });
    });

    const scrubbed = screen.getByTestId('filmstrip-cell');
    // 180/200 = 0.9 of a 137 s range -> 123.3 s -> the 124th measured thumbnail.
    expect(scrubbed.getAttribute('data-thumb-index')).toBe('123');
    expect(scrubbed.getAttribute('data-thumb-index')).not.toBe('0');
    expect((scrubbed as HTMLElement).style.backgroundPosition).not.toBe(origin);
    // The position readout describes the SAME thumbnail the cell is drawing.
    expect(textOf(screen.getByTestId('filmstrip-position'))).toContain('thumbnail 123');
  });

  /**
   * Claim 3 — the keyboard path, with no pointer event anywhere in the test.
   *
   * Focus alone adopts the clip, and focus moving from the tile to the slider does not release it,
   * so the control is reachable by tabbing and operable by the arrow keys.
   */
  it('reaches and operates the scrubber with the keyboard alone', async () => {
    renderPool();

    expect(screen.getByTestId('filmstrip-idle')).toBeTruthy();
    expect(screen.queryByTestId('filmstrip-cell')).toBeNull();

    // No mouse event in this test: focus is the only thing that puts the strip on screen.
    await act(async () => {
      fireEvent.focusIn(tiles()[0]);
    });

    const cell = await screen.findByTestId('filmstrip-cell');
    expect(cell.getAttribute('data-thumb-index')).toBe('0');
    expect(screen.queryByTestId('filmstrip-idle')).toBeNull();

    // Tab onward to the slider itself: focus stays inside the region, so the subject is held.
    await act(async () => {
      fireEvent.focusOut(tiles()[0], { relatedTarget: cell });
      fireEvent.focusIn(cell);
    });
    expect(screen.queryByTestId('filmstrip-cell')).not.toBeNull();

    await act(async () => {
      fireEvent.keyDown(screen.getByTestId('filmstrip-cell'), { key: 'ArrowRight' });
    });
    expect(screen.getByTestId('filmstrip-cell').getAttribute('data-thumb-index')).toBe('1');

    await act(async () => {
      fireEvent.keyDown(screen.getByTestId('filmstrip-cell'), { key: 'End' });
    });
    // The far end is the last MEASURED thumbnail, never a cell past it.
    expect(screen.getByTestId('filmstrip-cell').getAttribute('data-thumb-index')).toBe('136');

    await act(async () => {
      fireEvent.keyDown(screen.getByTestId('filmstrip-cell'), { key: 'Home' });
    });
    expect(screen.getByTestId('filmstrip-cell').getAttribute('data-thumb-index')).toBe('0');
  });

  /**
   * The inverse, so the two tests above cannot pass by the subject simply never being released: a
   * strip that described a clip forever would be as wrong as one that could not be reached.
   */
  it('releases the subject once the pointer and the focus have both left the region', async () => {
    renderPool();

    const region = screen.getByTestId('media-hover-region');
    await act(async () => {
      fireEvent.mouseOver(tiles()[0], { relatedTarget: document.body });
    });
    await screen.findByTestId('filmstrip-cell');

    await act(async () => {
      fireEvent.mouseOut(region, { relatedTarget: document.body });
    });

    await waitFor(() => expect(screen.queryByTestId('filmstrip-cell')).toBeNull());
    expect(textOf(screen.getByTestId('filmstrip-idle'))).toContain('Hover a clip');
  });

  /**
   * The phone layout stacks the detail pane BELOW the strip: if leaving the grid emptied the strip,
   * everything under it moved up between the press and the release of a tap on the pane's own
   * buttons, and "Set in" never got its click. The open clip stays on the strip instead.
   */
  it('keeps the open clip on the strip after the pointer leaves the region', async () => {
    renderPool();

    const region = screen.getByTestId('media-hover-region');
    await act(async () => {
      fireEvent.mouseOver(tiles()[0], { relatedTarget: document.body });
    });
    await screen.findByTestId('filmstrip-cell');
    await act(async () => {
      fireEvent.click(tiles()[0]);
    });

    await act(async () => {
      fireEvent.mouseOut(region, { relatedTarget: document.body });
    });

    expect(screen.queryByTestId('filmstrip-idle')).toBeNull();
    expect(screen.getByTestId('filmstrip-cell')).toBeTruthy();
  });

  /** The region is the seam, so it has to actually contain both halves of the journey. */
  it('contains the grid AND the strip inside the one hover region', () => {
    renderPool();

    const region = screen.getByTestId('media-hover-region');
    expect(region.contains(screen.getByTestId('media-clip-grid'))).toBe(true);
    expect(region.contains(screen.getByTestId('filmstrip-strip'))).toBe(true);
    expect(region.contains(tiles()[0])).toBe(true);
  });
});

/** Guards the fixture assumption the scrub arithmetic above depends on. */
describe('the reachability fixtures', () => {
  it('offers a video clip whose index declares 137 measured thumbnails', () => {
    const item: MediaPoolItem = POOL[0];
    expect(item.kind).toBe('video');
    const densities = FILMSTRIP_RESPONSE.filmstrip.densities;
    const usable = densities[0].sheets;
    const last = usable[usable.length - 1];
    expect((last.firstThumbIndex + last.thumbCount) * densities[0].densitySec).toBe(137);
  });
});
