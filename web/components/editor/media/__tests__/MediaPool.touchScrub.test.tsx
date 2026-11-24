// @vitest-environment jsdom
/**
 * R04.6: a finger pressed on a media tile and dragged sideways scrubs the filmstrip, with a position
 * label on the tile. A mostly vertical drag is left to the page scroll, and a scrub does not select
 * the tile. No hover wording is shown to a touch user.
 */
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, cleanup, fireEvent, act } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MediaPool } from '../MediaPool';
import { POOL, BINS, DERIVATIVES_ALL_KINDS, FILMSTRIP_RESPONSE } from '@/lib/editing/media/fixtures';

vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);
Element.prototype.scrollIntoView = vi.fn();

// jsdom has no PointerEvent: this one keeps clientX and pointerType.
class PointerEventPolyfill extends MouseEvent {
  pointerId: number;
  pointerType: string;
  constructor(type: string, init: PointerEventInit = {}) {
    super(type, init);
    this.pointerId = init.pointerId ?? 0;
    this.pointerType = init.pointerType ?? 'mouse';
  }
}
(window as unknown as { PointerEvent: unknown }).PointerEvent = PointerEventPolyfill;

let coarse = true;
function installMatchMedia() {
  global.matchMedia = (query: string): MediaQueryList =>
    ({
      matches: coarse && query.includes('pointer: coarse'),
      media: query,
      onchange: null,
      addListener: () => {},
      removeListener: () => {},
      addEventListener: () => {},
      removeEventListener: () => {},
      dispatchEvent: () => true,
    }) as MediaQueryList;
}

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
  return { ...actual, fetchMediaDerivatives: vi.fn(), fetchMediaFilmstrip: vi.fn(), addBinItem: vi.fn(), removeBinItem: vi.fn() };
});
import { fetchMediaDerivatives, fetchMediaFilmstrip } from '@/lib/editing/api/media';

const touch = (x: number, y: number) => ({ clientX: x, clientY: y, pointerId: 3, pointerType: 'touch' });
const stubBox = (el: Element, width: number) => {
  el.getBoundingClientRect = () => ({ left: 0, top: 0, right: width, bottom: 100, width, height: 100, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect;
};

describe('R04.6 touch scrub on media tiles', () => {
  beforeEach(() => {
    coarse = true;
    installMatchMedia();
    state.pool = { data: POOL, error: undefined, isLoading: false, mutate: vi.fn() };
    state.bins = { data: BINS, error: undefined, isLoading: false, mutate: vi.fn() };
    vi.mocked(fetchMediaDerivatives).mockReset().mockResolvedValue(DERIVATIVES_ALL_KINDS);
    vi.mocked(fetchMediaFilmstrip).mockReset().mockResolvedValue(FILMSTRIP_RESPONSE);
  });
  afterEach(cleanup);

  const renderPool = () =>
    render(
      <MantineProvider>
        <MediaPool projectId="00000000-0000-4000-8000-000000000009" />
      </MantineProvider>,
    );

  it('a sideways drag scrubs the strip and shows a position label; lifting does not select the tile', async () => {
    renderPool();
    const tile = screen.getAllByTestId('media-clip-tile')[0];
    stubBox(tile, 200);
    fireEvent.pointerDown(tile, touch(20, 50));
    await act(async () => {
      fireEvent.pointerMove(tile, touch(60, 52));
    });
    await act(async () => {
      fireEvent.pointerMove(tile, touch(180, 52));
    });
    const label = await screen.findByTestId('media-tile-scrub-label');
    expect(label.textContent).toMatch(/of/);
    // 180/200 of the measured 137 thumbnails.
    expect((await screen.findByTestId('filmstrip-cell')).getAttribute('data-thumb-index')).toBe('123');
    await act(async () => {
      fireEvent.pointerUp(tile, touch(180, 52));
    });
    expect(screen.queryByTestId('media-tile-scrub-label')).toBeNull();
    fireEvent.click(tile);
    expect(tile.getAttribute('aria-pressed')).toBe('false');
  });

  it('a mostly vertical drag does not scrub (the page scrolls)', async () => {
    renderPool();
    const tile = screen.getAllByTestId('media-clip-tile')[0];
    stubBox(tile, 200);
    fireEvent.pointerDown(tile, touch(100, 20));
    await act(async () => {
      fireEvent.pointerMove(tile, touch(104, 80));
      fireEvent.pointerMove(tile, touch(160, 90));
    });
    expect(screen.queryByTestId('media-tile-scrub-label')).toBeNull();
    fireEvent.pointerUp(tile, touch(160, 90));
  });

  it('tiles allow vertical page scroll (touch-action pan-y)', () => {
    renderPool();
    expect(screen.getAllByTestId('media-clip-tile')[0].style.touchAction).toBe('pan-y');
  });

  it('shows no hover wording on a touch device', () => {
    renderPool();
    expect(document.body.textContent ?? '').not.toMatch(/hover/i);
    expect(document.body.textContent ?? '').toContain('drag across a clip to scrub it');
  });
});
