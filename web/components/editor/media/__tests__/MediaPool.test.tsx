// @vitest-environment jsdom
/* eslint @typescript-eslint/no-unused-vars: ["warn", { "argsIgnorePattern": "^_" }] */
/**
 * The media workspace, against the committed fixtures.
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
import {
  POOL,
  BINS,
  DERIVATIVES_ALL_KINDS,
  DERIVATIVES_NONE,
  FILMSTRIP_INDEX,
  FILMSTRIP_INDEX_EMPTY,
  FILMSTRIP_RESPONSE,
} from '@/lib/editing/media/fixtures';
import { countsByBinId } from '@/lib/editing/media/bins';
import { expectedDerivativeKinds } from '@/lib/editing/media/derivative-kinds';
import type { MediaDerivativeRow, MediaPoolItem } from '@/lib/editing/media/types';

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

const uploadSpy = vi.hoisted(() => vi.fn());
vi.mock('@/lib/api/files', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/api/files')>()),
  uploadFileWithProgress: (...args: unknown[]) => uploadSpy(...args),
}));

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

/** Every tile currently rendered. */
const tiles = () => screen.queryAllByTestId('media-clip-tile');

/** Trimmed text of a node, or an empty string — never `undefined`, so assertions stay readable. */
const textOf = (node: Element | null): string => (node?.textContent ?? '').trim();

const attrOf = (node: Element | null, name: string): string | null =>
  node === null ? null : node.getAttribute(name);

describe('MediaPool', () => {
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
    addBinItemSpy.mockReset();
    // The created pin's real id, which is what a later removal addresses.
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
    cleanup();
  });

  // ───────────────────────────────────────────────────────────────────────────
  // Both mediaRef prefixes, treated as opaque
  // ───────────────────────────────────────────────────────────────────────────

  it('renders pool items carrying BOTH mediaRef prefixes', () => {
    const { container } = renderPool();

    expect(tiles()).toHaveLength(POOL.length);
    expect(
      container.querySelector('[data-media-ref="file:00000000-0000-4000-8000-000000000101"]'),
    ).not.toBeNull();
    expect(
      container.querySelector(
        '[data-media-ref="step_result:00000000-0000-4000-8000-000000000102"]',
      ),
    ).not.toBeNull();
  });

  it('echoes the mediaRef verbatim and never rewrites it', () => {
    const { container } = renderPool();
    const refs = [...container.querySelectorAll('[data-media-ref]')].map((node) =>
      node.getAttribute('data-media-ref'),
    );

    expect(refs.sort()).toEqual(POOL.map((item) => item.mediaRef).sort());
  });

  // ───────────────────────────────────────────────────────────────────────────
  // The hover-scrub fetch count
  // ───────────────────────────────────────────────────────────────────────────

  it('issues exactly ONE derivatives fetch per mediaRef across 12 hover events', async () => {
    renderPool();
    const rendered = tiles();
    expect(rendered).toHaveLength(4);

    // Twelve hover events over three distinct clips, four events each.
    const cycle = [rendered[0], rendered[1], rendered[2], rendered[0], rendered[1]];
    await act(async () => {
      for (let round = 0; round < 12; round += 1) {
        fireEvent.mouseEnter(cycle[round % cycle.length]);
      }
    });

    // Twelve events, three calls: one per source, not one per event.
    expect(derivativesSpy).toHaveBeenCalledTimes(3);
    expect(new Set(derivativesSpy.mock.calls.map((call) => call[1])).size).toBe(3);
    expect(derivativesSpy.mock.calls.every((call) => call[0] === PROJECT_ID)).toBe(true);
  });

  it('serves later hovers of the same clip entirely from cache', async () => {
    renderPool();
    const [firstTile] = tiles();

    await act(async () => {
      for (let index = 0; index < 10; index += 1) fireEvent.mouseEnter(firstTile);
    });
    expect(derivativesSpy).toHaveBeenCalledTimes(1);

    // Leaving and re-entering does not refetch either.
    await act(async () => {
      fireEvent.mouseLeave(firstTile);
      fireEvent.mouseEnter(firstTile);
    });
    expect(derivativesSpy).toHaveBeenCalledTimes(1);
  });

  it('re-reads a source already read when its derivatives version moves, and nothing else (d2 N4)', async () => {
    let versions: Record<string, number> = {};
    const pool = () => (
      <MantineProvider>
        <MediaPool projectId={PROJECT_ID} derivativesVersionOf={(ref) => versions[ref] ?? 0} />
      </MantineProvider>
    );
    const view = render(pool());
    await act(async () => {
      fireEvent.mouseEnter(tiles()[0]);
    });
    expect(derivativesSpy).toHaveBeenCalledTimes(1);

    // A progress event for the hovered source and one for a source nobody looked at.
    versions = { [POOL[0].mediaRef]: 1, [POOL[1].mediaRef]: 1 };
    await act(async () => {
      view.rerender(pool());
    });
    expect(derivativesSpy).toHaveBeenCalledTimes(2);
    expect(derivativesSpy.mock.calls[1][1]).toBe(POOL[0].mediaRef);

    // The same version again issues nothing.
    await act(async () => {
      view.rerender(pool());
    });
    expect(derivativesSpy).toHaveBeenCalledTimes(2);
  });

  it('fetches each mediaRef once even when the responses are still in flight', async () => {
    let settle: ((rows: MediaDerivativeRow[]) => void) | undefined;
    derivativesSpy.mockImplementation(
      () =>
        new Promise<MediaDerivativeRow[]>((resolve) => {
          settle = resolve;
        }),
    );

    renderPool();
    const rendered = tiles();

    // A synchronous burst before anything settles: the "already fetched" check must be
    // synchronous, or the same clip is requested once per event.
    act(() => {
      for (let round = 0; round < 10; round += 1) fireEvent.mouseEnter(rendered[round % 3]);
    });
    expect(derivativesSpy).toHaveBeenCalledTimes(3);

    await act(async () => {
      settle?.(DERIVATIVES_ALL_KINDS);
    });
  });

  // ───────────────────────────────────────────────────────────────────────────
  // Honest degradation
  // ───────────────────────────────────────────────────────────────────────────

  it('scrubs against the REAL filmstrip the API returns, cell by cell', async () => {
    renderPool();

    await act(async () => {
      fireEvent.mouseEnter(tiles()[0]);
    });

    // The index is fetched for the hovered source, in one request.
    expect(filmstripSpy).toHaveBeenCalledWith(PROJECT_ID, POOL[0].mediaRef);

    const cell = await screen.findByTestId('filmstrip-cell');
    // Sheet 0's own content URL, which is what the wire carries — not a rebuilt storage leaf.
    const background = cell.style.backgroundImage;
    expect(background).toContain(
      FILMSTRIP_INDEX.densities[0].sheets[0].contentUrl,
    );
    // A 10x10 sheet is drawn at 1000% x 1000% (one cell fills the box), and thumbnail 0 sits at
    // the sheet's origin.
    expect(cell.style.backgroundSize).toBe('1000% 1000%');
    expect(cell.style.backgroundPosition).toBe('0% 0%');

    // The labelled position reports the measured density and the sheet's own recorded time.
    const position = textOf(screen.getByTestId('filmstrip-position'));
    expect(position).toContain('Density 1 s');
    expect(position).toContain('thumbnail 0');
    expect(position).toContain('sheet 1');
  });

  it('states the empty index honestly when the API returns zero densities', async () => {
    filmstripSpy.mockResolvedValue({ filmstrip: FILMSTRIP_INDEX_EMPTY });
    renderPool();

    await act(async () => {
      fireEvent.mouseEnter(tiles()[0]);
    });

    const unavailable = await screen.findByTestId('filmstrip-index-unavailable');
    expect(textOf(unavailable)).toContain('No filmstrip index for this source');

    // No cell was rendered, and nothing claims a frame.
    expect(screen.queryByTestId('filmstrip-cell')).toBeNull();
    expect(screen.queryByTestId('filmstrip-no-frame')).toBeNull();
  });

  it('reports a failed index fetch as a failure, not as "this source has none"', async () => {
    filmstripSpy.mockRejectedValue(new Error('Index gateway timeout'));
    renderPool();

    await act(async () => {
      fireEvent.mouseEnter(tiles()[0]);
    });

    const error = await screen.findByTestId('filmstrip-index-error');
    expect(textOf(error)).toContain('Index gateway timeout');
    // The two states must never be confused: a failure is retryable, an empty index is not.
    expect(screen.queryByTestId('filmstrip-index-unavailable')).toBeNull();
  });

  it('scrubs by keyboard, stepping by the density the index declares', async () => {
    // Pointer scrubbing cannot be driven in jsdom (clientX is unavailable), but hover-scrub must not
    // be the ONLY way to reach a position: a keyboard user needs the same control, and this asserts
    // the real one rather than a simulated pointer.
    derivativesSpy.mockResolvedValue(DERIVATIVES_ALL_KINDS);
    renderPool();

    await act(async () => {
      fireEvent.mouseEnter(tiles()[0]);
    });

    const cell = await screen.findByTestId('filmstrip-cell');
    expect(cell.getAttribute('data-thumb-index')).toBe('0');
    // The range is the MEASURED 137 thumbnails, not the clip's 12.5 s probe duration.
    expect(cell.getAttribute('aria-valuemax')).toBe('137');
    expect(cell.getAttribute('aria-valuemax')).not.toBe('12.5');

    await act(async () => {
      fireEvent.keyDown(cell, { key: 'ArrowRight' });
    });
    expect(screen.getByTestId('filmstrip-cell').getAttribute('data-thumb-index')).toBe('1');

    await act(async () => {
      fireEvent.keyDown(screen.getByTestId('filmstrip-cell'), { key: 'End' });
    });
    const end = screen.getByTestId('filmstrip-cell');
    // The far end is the LAST MEASURED thumbnail — sheet 2, index 136 — never a cell past it.
    expect(end.getAttribute('data-thumb-index')).toBe('136');
    expect(textOf(screen.getByTestId('filmstrip-position'))).toContain('sheet 2');

    await act(async () => {
      fireEvent.keyDown(screen.getByTestId('filmstrip-cell'), { key: 'Home' });
    });
    expect(screen.getByTestId('filmstrip-cell').getAttribute('data-thumb-index')).toBe('0');
  });

  it('states the absent case for a source with no derivatives at all', async () => {
    derivativesSpy.mockResolvedValue(DERIVATIVES_NONE);
    filmstripSpy.mockResolvedValue({ filmstrip: FILMSTRIP_INDEX_EMPTY });
    renderPool();

    await act(async () => {
      fireEvent.mouseEnter(tiles()[0]);
    });

    for (const kind of expectedDerivativeKinds(POOL[0])) {
      expect(attrOf(await screen.findByTestId(`derivative-kind-${kind}`), 'data-present')).toBe(
        'false',
      );
    }
    // Kinds the pool pipeline never produces are not advertised (audit d2 N5, RD-15).
    expect(screen.queryByTestId('derivative-kind-thumbnail')).toBeNull();
    expect(screen.queryByTestId('derivative-kind-waveform')).toBeNull();
    // The strip is still honest about the index rather than showing an empty box.
    expect(textOf(await screen.findByTestId('filmstrip-index-unavailable'))).toContain(
      'No filmstrip index for this source',
    );
  });

  it('reports a derivatives failure as a message instead of an unresolvable spinner', async () => {
    derivativesSpy.mockRejectedValue(new Error('Gateway timeout'));
    renderPool();

    await act(async () => {
      fireEvent.mouseEnter(tiles()[0]);
    });

    const error = await screen.findByTestId('filmstrip-error');
    expect(textOf(error)).toContain('Gateway timeout');
    expect(screen.queryByTestId('filmstrip-loading')).toBeNull();
  });

  it('shows the loading line while the derivatives request is in flight', async () => {
    derivativesSpy.mockImplementation(() => new Promise<MediaDerivativeRow[]>(() => {}));
    renderPool();

    act(() => {
      fireEvent.mouseEnter(tiles()[0]);
    });

    expect(textOf(screen.getByTestId('filmstrip-loading'))).toContain('Loading');
  });

  it('renders media elements only as tile posters (R05.2), never an iframe or an audio player', () => {
    const { container } = renderPool();

    for (const el of container.querySelectorAll('video, img')) expect(el.closest('[data-testid="media-tile-poster"]')).not.toBeNull();
    expect(container.querySelectorAll('iframe')).toHaveLength(0);
    expect(container.querySelectorAll('audio')).toHaveLength(0);
  });

  it('R05.8 collapses and resizes the panes for this visit; the legacy key is neither read nor written (T-003)', () => {
    window.localStorage.clear();
    renderPool();
    const grid = screen.getByTestId('media-pool');
    expect(grid.hasAttribute('data-bins-collapsed')).toBe(false);
    fireEvent.click(screen.getByTestId('pool-toggle-bins'));
    expect(grid.hasAttribute('data-bins-collapsed')).toBe(true);
    expect(screen.getByTestId('pool-toggle-bins').textContent).toBe('Show bins');
    fireEvent.keyDown(screen.getByTestId('pool-resizer-detail'), { key: 'ArrowLeft' });
    expect(grid.style.getPropertyValue('--pool-cols-three')).toBe('minmax(0, 1fr) 336px');
    // T-003: the Media page persists its zone widths in the shared layout record; the pool's own
    // panes are per visit and the legacy pool-panes key never comes back.
    expect(window.localStorage.getItem('reelforge.editor.poolPanes.v1')).toBeNull();
    cleanup();

    renderPool();
    expect(screen.getByTestId('media-pool').hasAttribute('data-bins-collapsed')).toBe(false);
    expect(window.localStorage.getItem('reelforge.editor.poolPanes.v1')).toBeNull();
  });

  it('T-003 renders bins, pool and viewer+metadata into the frame’s zones', () => {
    const bins = document.createElement('div');
    const detail = document.createElement('div');
    document.body.append(bins, detail);
    renderPool({
      zoneTargets: { bins, detail },
      detailPane: () => <div data-testid="detail-stub">detail</div>,
    });
    try {
      expect(bins.querySelector('[data-testid="bin-node"]'), 'the bins render in the left zone').not.toBeNull();
      expect(detail.querySelector('[data-testid="media-detail-pane"]')?.textContent, 'the viewer+metadata render in the right zone').toContain('detail');
      expect(screen.getByTestId('media-pool'), 'the pool itself stays in the centre').not.toBeNull();
      // The frame owns the widths: the pool's own pane bar and resizers are not drawn.
      expect(screen.queryByTestId('pool-pane-bar')).toBeNull();
      expect(screen.queryByTestId('pool-resizer-bins')).toBeNull();
    } finally {
      bins.remove();
      detail.remove();
    }
  });

  it('shows an idle instruction, not a blank strip, when nothing is hovered', () => {
    renderPool();
    expect(textOf(screen.getByTestId('filmstrip-idle'))).toContain('Hover a clip');
  });

  // ───────────────────────────────────────────────────────────────────────────
  // Route-level states
  // ───────────────────────────────────────────────────────────────────────────

  it('renders the loading state', () => {
    state.pool = { data: undefined, error: undefined, isLoading: true, mutate: vi.fn() };
    renderPool();

    expect(textOf(screen.getByTestId('media-pool-loading'))).toContain('Loading media');
    expect(screen.queryByTestId('media-pool')).toBeNull();
  });

  it("renders the empty state as 'No media in this project yet'", () => {
    state.pool = { data: [], error: undefined, isLoading: false, mutate: vi.fn() };
    renderPool();

    expect(textOf(screen.getByTestId('media-pool-empty'))).toContain('No media in this project yet');
  });

  it('R-upload: the empty state offers Upload and a drop zone, and a dropped file is uploaded then the pool re-read', async () => {
    const mutate = vi.fn();
    state.pool = { data: [], error: undefined, isLoading: false, mutate };
    uploadSpy.mockReset().mockImplementation(async (_project: string, _file: File, onProgress: (n: number) => void) => {
      onProgress(100);
      return { id: 'f1' };
    });
    renderPool();

    expect(screen.getByTestId('media-upload')).toBeTruthy();
    const zone = screen.getByTestId('media-dropzone');
    const file = new File(['abc'], 'clip.mp4', { type: 'video/mp4' });
    fireEvent.drop(zone, { dataTransfer: { files: [file], items: [], types: ['Files'] } });
    await waitFor(() => expect(uploadSpy).toHaveBeenCalledTimes(1));
    expect(uploadSpy.mock.calls[0][0]).toBe(PROJECT_ID);
    expect((uploadSpy.mock.calls[0][1] as File).name).toBe('clip.mp4');
    await waitFor(() => expect(mutate).toHaveBeenCalled());
  });

  it('R-upload: a source whose ingest probe has not landed yet is re-read every few seconds, so a fresh upload leaves "not analysed" by itself', async () => {
    const mutate = vi.fn();
    state.pool = { data: [{ ...(POOL[0] as object), kind: 'video', probe: null }], error: undefined, isLoading: false, mutate };
    uploadSpy.mockReset().mockResolvedValue({ id: 'f2' });
    renderPool();
    fireEvent.drop(screen.getByTestId('media-pool-dropzone'), { dataTransfer: { files: [new File(['abc'], 'new.mp4', { type: 'video/mp4' })], items: [], types: ['Files'] } });
    await waitFor(() => expect(mutate).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(mutate.mock.calls.length).toBeGreaterThanOrEqual(2), { timeout: 6000 });
  });

  it('R-upload: an upload that fails says so in plain words and offers Try again', async () => {
    state.pool = { data: [], error: undefined, isLoading: false, mutate: vi.fn() };
    uploadSpy.mockReset().mockRejectedValue(new Error('The file is too large.'));
    renderPool();

    const file = new File(['abc'], 'big.mp4', { type: 'video/mp4' });
    fireEvent.drop(screen.getByTestId('media-dropzone'), { dataTransfer: { files: [file], items: [], types: ['Files'] } });
    expect(await screen.findByText('The file is too large.')).toBeTruthy();
    expect(screen.getByTestId('media-upload-retry')).toBeTruthy();
  });

  it('renders the error state with the server message', () => {
    state.pool = {
      data: undefined,
      error: new Error('Media service unavailable'),
      isLoading: false,
      mutate: vi.fn(),
    };
    renderPool();

    expect(textOf(screen.getByTestId('media-pool-error'))).toContain('Media service unavailable');
  });

  it('reports a bins failure without taking the pool down with it', () => {
    state.bins = {
      data: undefined,
      error: new Error('Bins service unavailable'),
      isLoading: false,
      mutate: vi.fn(),
    };
    renderPool();

    expect(textOf(screen.getByTestId('media-bins-error'))).toContain('Bins service unavailable');
    // The pool itself is still usable.
    expect(tiles()).toHaveLength(POOL.length);
  });

  // ───────────────────────────────────────────────────────────────────────────
  // View modes and the T-203 seam
  // ───────────────────────────────────────────────────────────────────────────

  it('switches the layout between grid, list and filmstrip', () => {
    renderPool();

    expect(screen.getByTestId('media-clip-grid')).toBeTruthy();

    fireEvent.click(screen.getByRole('radio', { name: 'List' }));
    expect(screen.getByTestId('media-clip-list')).toBeTruthy();

    fireEvent.click(screen.getByRole('radio', { name: 'Filmstrip' }));
    expect(screen.getByTestId('media-clip-filmstrip')).toBeTruthy();
  });

  it('calls onSelectClip with the item, then with null when it is deselected', () => {
    const onSelectClip = vi.fn();
    renderPool({ onSelectClip });

    fireEvent.click(tiles()[0]);
    expect(onSelectClip).toHaveBeenCalledWith(POOL[0]);

    fireEvent.click(tiles()[0]);
    expect(onSelectClip).toHaveBeenLastCalledWith(null);
  });

  it('renders the detail-pane slot with the current selection', () => {
    renderPool({
      detailPane: (selected: MediaPoolItem | null) => (
        <div data-testid="t203-viewer">{selected ? selected.fileName : 'no selection'}</div>
      ),
    });

    expect(textOf(screen.getByTestId('t203-viewer'))).toBe('no selection');

    fireEvent.click(tiles()[0]);
    expect(textOf(screen.getByTestId('t203-viewer'))).toBe(POOL[0].fileName);
  });

  it('owns view state locally: selecting a clip and switching mode issue no request', () => {
    // The standing rule of this wave: view state never produces a write. Only a HOVER fetches.
    renderPool();

    fireEvent.click(tiles()[1]);
    fireEvent.click(screen.getByRole('radio', { name: 'List' }));
    fireEvent.click(screen.getByRole('radio', { name: 'Filmstrip' }));
    fireEvent.click(tiles()[2]);

    expect(derivativesSpy).not.toHaveBeenCalled();
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// The whole fixture set, scanned for values that must never reach the DOM
// ─────────────────────────────────────────────────────────────────────────────

describe('MediaPool over the whole fixture set', () => {
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
    addBinItemSpy.mockReset();
    // The created pin's real id, which is what a later removal addresses.
    addBinItemSpy.mockResolvedValue({
      id: 'item-1',
      binId: BINS[2].id,
      mediaRef: POOL[0].mediaRef,
      sourceKind: 'ProjectFile',
      sortOrder: 0,
      createdAt: '2026-10-03T08:00:00Z',
    });
  });

  afterEach(() => cleanup());

  it.each(POOL.map((item) => [item.fileName, item] as const))(
    'renders item %s with no undefined, NaN or null anywhere in the DOM',
    async (_name, item) => {
      const { container } = render(
        <MantineProvider>
          <MediaPool projectId={PROJECT_ID} />
        </MantineProvider>,
      );

      expect(container.querySelector(`[data-media-ref="${item.mediaRef}"]`)).not.toBeNull();

      // Hover every tile, so the strip has rendered for the item under test and for each other.
      await act(async () => {
        for (const node of tiles()) fireEvent.mouseEnter(node);
      });
      await waitFor(() => expect(screen.getByTestId('filmstrip-strip')).toBeTruthy());

      const text = container.textContent ?? '';
      expect(text).not.toContain('undefined');
      expect(text).not.toContain('NaN');
      expect(text).not.toContain('null');
      expect(text).not.toContain('Infinity');

      // No rendered element is an unlabelled, textless cell — the "silent empty box" the ticket
      // forbids. Anything without text must carry an accessible name instead.
      const silent = [...container.querySelectorAll('button, [data-testid]')].filter(
        (node) => (node.textContent ?? '').trim().length === 0 && node.getAttribute('aria-label') === null,
      );
      expect(silent.map((node) => node.getAttribute('data-testid') ?? node.tagName)).toEqual([]);
    },
  );

  it('renders the all-null probe as a stated label, never a zero or a blank', () => {
    renderPool();

    const audioTile = screen.getByText('interview-room-tone.wav').closest('button');
    expect(textOf(audioTile)).toContain('Not analysed');
    expect(textOf(audioTile)).not.toContain('NaN');
  });

  it('renders the never-analysed items as a stated label, never an invented duration', () => {
    renderPool();

    expect(textOf(screen.getByText('logo-mark.png').closest('button'))).toContain('Not analysed');
    expect(textOf(screen.getByText('render-pass-1.mp4').closest('button'))).toContain('Not analysed');
  });

  // ───────────────────────────────────────────────────────────────────────────
  // Bin membership: the pool cache is where a derived count comes from
  // ───────────────────────────────────────────────────────────────────────────

  it('writes a new bin pin into the pool cache, so the bin count is real', async () => {
    renderPool();
    const poolMutate = state.pool.mutate as unknown as ReturnType<typeof vi.fn>;
    poolMutate.mockClear();

    // The next hook-visible pool carries the pin the server would return.
    const target = screen.getByTestId(`bin-drop-target-${BINS[2].id}`);
    await act(async () => {
      fireEvent.drop(target, {
        dataTransfer: {
          setData: () => {},
          getData: (format: string) =>
            format === 'application/x-reelforge-media-ref' ? POOL[0].mediaRef : '',
        },
      });
    });

    expect(addBinItemSpy).toHaveBeenCalledWith(PROJECT_ID, BINS[2].id, POOL[0].mediaRef);
    expect(poolMutate).toHaveBeenCalled();

    // The updater is a pure function over the pool: run it and read the result, so the assertion is
    // about the DATA the cache receives rather than about the fact that a call happened.
    const updater = poolMutate.mock.calls[0][0] as (current: MediaPoolItem[]) => MediaPoolItem[];
    const next = updater(POOL);
    const updated = next.find((item) => item.mediaRef === POOL[0].mediaRef);
    // The existing pin is kept and the new one is appended — the same source can be in several bins.
    expect(updated?.bins).toEqual([
      ...POOL[0].bins,
      { binId: BINS[2].id, itemId: 'item-1' },
    ]);
    // Nothing else in the pool was disturbed.
    expect(next.filter((item) => item.mediaRef !== POOL[0].mediaRef)).toEqual(
      POOL.filter((item) => item.mediaRef !== POOL[0].mediaRef),
    );
  });

  it('derives a bin count from the pool pins rather than from the server itemCount', () => {
    // A pure, direct check of the derivation the tree renders — the fixtures' MediaBin.itemCount
    // says 12, the pool's own pins say 1.
    const counts = countsByBinId(POOL);
    // VIDEO_FILE_ITEM is pinned in Rushes; AUDIO_FILE_ITEM is pinned in Music and in Rushes.
    expect(counts[BINS[0].id]).toBe(2);
    expect(counts[BINS[0].id]).not.toBe(BINS[0].itemCount);
    expect(counts[BINS[2].id]).toBeUndefined();
  });
});
