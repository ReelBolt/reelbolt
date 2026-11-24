// @vitest-environment jsdom
/* eslint @typescript-eslint/no-unused-vars: ["warn", { "argsIgnorePattern": "^_" }] */
/** RM.8: the Add media sheet. Tile actions produce the right call or the plain refusal; chips, upload wording, bins. */
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import userEvent from '@testing-library/user-event';
import { BINS, DERIVATIVES_ALL_KINDS, FILMSTRIP_RESPONSE, POOL } from '@/lib/editing/media/fixtures';
import type { UploadEntry } from '@/lib/hooks/use-upload-queue';
import { AddMediaSheet, type AddMediaSheetProps } from '../AddMediaSheet';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
Element.prototype.scrollIntoView = vi.fn();

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

const state = vi.hoisted(() => ({
  pool: { data: [] as unknown[] | undefined, error: undefined as unknown, isLoading: false, mutate: undefined as unknown },
  bins: { data: [] as unknown[] | undefined, error: undefined as unknown, isLoading: false, mutate: undefined as unknown },
  uploads: [] as unknown[],
  enqueue: undefined as unknown,
  retry: undefined as unknown,
}));
vi.mock('@/lib/hooks/use-media', () => {
  const annotations = { data: [] as unknown[], mutate: async () => undefined };
  return { useMediaPool: () => state.pool, useMediaBins: () => state.bins, useMediaAnnotations: () => annotations };
});
vi.mock('@/lib/hooks/use-upload-queue', () => ({
  useUploadQueue: () => ({ entries: state.uploads, enqueue: state.enqueue, retry: state.retry, clearFinished: vi.fn() }),
}));
vi.mock('@/lib/hooks/use-media-derivatives', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/hooks/use-media-derivatives')>();
  return { ...actual, useMediaDerivativeProgress: vi.fn() };
});
vi.mock('@/lib/editing/api/media', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/editing/api/media')>();
  return { ...actual, fetchMediaDerivatives: vi.fn(), fetchMediaFilmstrip: vi.fn(), addBinItem: vi.fn(), removeBinItem: vi.fn(), createBin: vi.fn(), upsertMediaAnnotation: vi.fn() };
});
import { addBinItem, createBin, fetchMediaDerivatives, fetchMediaFilmstrip, removeBinItem, upsertMediaAnnotation } from '@/lib/editing/api/media';
import { resetTestViewport, setTestViewport } from '@/lib/editing/__tests__/viewport';

const PROJECT = '00000000-0000-4000-8000-000000000009';
const SMART_BIN = { id: 'smart-1', name: 'Favourites', parentBinId: null, sortOrder: 9, itemCount: 0, isSmart: true, smartFilter: { favourite: true } };
const VIDEO = POOL[0];

function mount(props: Partial<AddMediaSheetProps> = {}) {
  const onPlace = vi.fn<AddMediaSheetProps['onPlace']>(() => null);
  const onPlaced = vi.fn();
  const utils = render(
    <MantineProvider>
      <AddMediaSheet projectId={PROJECT} readOnly={false} onPlace={onPlace} onPlaced={onPlaced} {...props} />
    </MantineProvider>,
  );
  return { onPlace, onPlaced, ...utils };
}
const menuFor = async (mediaRef: string) => {
  const trigger = screen.getAllByTestId('media-tile-menu').find((el) => el.getAttribute('data-for-media-ref') === mediaRef)!;
  await act(async () => fireEvent.click(trigger));
};
const tileOf = (mediaRef: string) => screen.getAllByTestId('add-media-tile').find((el) => el.getAttribute('data-media-ref') === mediaRef)!;

beforeEach(() => {
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
  state.pool = { data: POOL, error: undefined, isLoading: false, mutate: vi.fn((u: unknown) => Promise.resolve(typeof u === 'function' ? (u as (c: unknown) => unknown)(POOL) : u)) };
  state.bins = { data: [...BINS, SMART_BIN], error: undefined, isLoading: false, mutate: vi.fn() };
  state.uploads = [];
  state.enqueue = vi.fn();
  state.retry = vi.fn();
  vi.mocked(fetchMediaDerivatives).mockReset().mockResolvedValue(DERIVATIVES_ALL_KINDS);
  vi.mocked(fetchMediaFilmstrip).mockReset().mockResolvedValue(FILMSTRIP_RESPONSE);
  vi.mocked(addBinItem).mockReset().mockResolvedValue({ id: 'item-9', binId: BINS[2].id, mediaRef: VIDEO.mediaRef, sourceKind: 'ProjectFile', sortOrder: 0, createdAt: '2026-10-03T08:00:00Z' });
  vi.mocked(removeBinItem).mockReset().mockResolvedValue(undefined);
  vi.mocked(createBin).mockReset().mockResolvedValue({ id: 'new-bin', name: 'Interview day', parentBinId: null, sortOrder: 0, itemCount: 0 });
});
afterEach(() => {
  cleanup();
  resetTestViewport();
});

describe('RM.8 tile menu: put it on the timeline', () => {
  it('Insert at playhead places with the insert mode and closes the sheet to a peek', async () => {
    const { onPlace, onPlaced } = mount();
    await menuFor(VIDEO.mediaRef);
    await act(async () => fireEvent.click(await screen.findByTestId('tile-place-insert')));
    expect(onPlace).toHaveBeenCalledWith(VIDEO, 'insert', null);
    expect(onPlaced).toHaveBeenCalledWith(VIDEO);
  });

  it('Add to end places with the append mode', async () => {
    const { onPlace, onPlaced } = mount();
    await menuFor(VIDEO.mediaRef);
    await act(async () => fireEvent.click(await screen.findByTestId('tile-place-append')));
    expect(onPlace).toHaveBeenCalledWith(VIDEO, 'append', null);
    expect(onPlaced).toHaveBeenCalledTimes(1);
  });

  it('a refusal is shown in plain words and the sheet stays open', async () => {
    const { onPlaced } = mount({ onPlace: () => 'Add an unlocked video track first.' });
    await menuFor(VIDEO.mediaRef);
    await act(async () => fireEvent.click(await screen.findByTestId('tile-place-insert')));
    expect(screen.getByTestId('add-media-problem').textContent).toBe('Add an unlocked video track first.');
    expect(onPlaced).not.toHaveBeenCalled();
  });

  it('read-only disables both placements', async () => {
    mount({ readOnly: true });
    await menuFor(VIDEO.mediaRef);
    expect((await screen.findByTestId('tile-place-insert')).getAttribute('data-disabled')).toBe('true');
    expect(screen.getByTestId('tile-place-append').getAttribute('data-disabled')).toBe('true');
  });
});

describe('RM.8 bins', () => {
  it('Add to bin pins the clip with one request and says so', async () => {
    mount();
    await menuFor(VIDEO.mediaRef);
    await act(async () => fireEvent.click(await screen.findByTestId(`add-to-bin-${BINS[2].id}`)));
    expect(addBinItem).toHaveBeenCalledWith(PROJECT, BINS[2].id, VIDEO.mediaRef);
    await waitFor(() => expect(screen.getByTestId('add-media-message').textContent).toBe(`Added to "${BINS[2].name}".`));
  });

  it('a smart bin refuses with the existing plain message and sends nothing (P6-BIN-03)', async () => {
    mount();
    await menuFor(VIDEO.mediaRef);
    await act(async () => fireEvent.click(await screen.findByTestId(`add-to-bin-${SMART_BIN.id}`)));
    expect(addBinItem).not.toHaveBeenCalled();
    expect(screen.getByTestId('add-media-problem').textContent).toBe('"Favourites" is a smart bin: it shows every source matching its filter. Tag the clip instead, or add it to an ordinary bin.');
  });

  it('Remove from bin deletes that pin', async () => {
    mount();
    await menuFor(VIDEO.mediaRef);
    const pin = VIDEO.bins[0];
    await act(async () => fireEvent.click(await screen.findByTestId(`remove-from-bin-${pin.binId}`)));
    expect(removeBinItem).toHaveBeenCalledWith(PROJECT, pin.binId, pin.itemId);
  });

  it('a bin chip filters the tiles to what the bin holds', async () => {
    mount();
    expect(screen.getAllByTestId('add-media-tile')).toHaveLength(POOL.length);
    await act(async () => fireEvent.click(screen.getByTestId(`add-media-bin-${VIDEO.bins[0].binId}`)));
    const refs = screen.getAllByTestId('add-media-tile').map((t) => t.getAttribute('data-media-ref'));
    expect(refs).toEqual(POOL.filter((p) => p.bins.some((b) => b.binId === VIDEO.bins[0].binId)).map((p) => p.mediaRef));
  });

  it('makes a bin from a name field, and asks for a name first', async () => {
    mount();
    await act(async () => fireEvent.click(screen.getByTestId('add-media-new-bin')));
    await act(async () => fireEvent.click(screen.getByTestId('add-media-bin-create')));
    expect(createBin).not.toHaveBeenCalled();
    expect(screen.getByTestId('add-media-problem').textContent).toBe('Give the bin a name first.');
    fireEvent.change(screen.getByTestId('add-media-bin-name'), { target: { value: '  Interview day ' } });
    await act(async () => fireEvent.click(screen.getByTestId('add-media-bin-create')));
    expect(createBin).toHaveBeenCalledWith(PROJECT, { name: 'Interview day' });
  });
});

describe('RM.8 readiness chips', () => {
  it('Ready with a preview copy; the not-ready line still allows adding; unprobed is Processing preview', async () => {
    vi.mocked(fetchMediaDerivatives).mockImplementation(async (_p, ref) => (ref === VIDEO.mediaRef ? DERIVATIVES_ALL_KINDS : []));
    mount();
    await waitFor(() => expect(tileOf(VIDEO.mediaRef).getAttribute('data-readiness')).toBe('ready'));
    const chipOf = (ref: string) => tileOf(ref).querySelector('[data-testid="add-media-chip"]')!.textContent;
    expect(chipOf(VIDEO.mediaRef)).toBe('Ready');
    const audio = POOL.find((p) => p.kind === 'audio')!;
    await waitFor(() => expect(tileOf(audio.mediaRef).getAttribute('data-readiness')).toBe(audio.probe ? 'notReady' : 'processing'));
    if (audio.probe) expect(chipOf(audio.mediaRef)).toBe('Preview not ready - you can still add it');
  });

  it('a source with no probe says Processing preview', async () => {
    state.pool = { ...state.pool, data: [{ ...VIDEO, probe: null }] };
    mount();
    expect(tileOf(VIDEO.mediaRef).querySelector('[data-testid="add-media-chip"]')!.textContent).toBe('Processing preview');
  });
});

describe('RM.8 upload', () => {
  const entry = (patch: Partial<UploadEntry>): UploadEntry => ({ id: 'u1', name: 'clip.mp4', destination: '', size: 10, progress: 0, status: 'queued', ...patch });

  it('the picker takes video, sound and pictures, several at once, and queues what was chosen', () => {
    mount();
    const input = screen.getByTestId('add-media-input') as HTMLInputElement;
    expect(input.accept).toBe('video/*,audio/*,image/*');
    expect(input.multiple).toBe(true);
    expect(screen.getByTestId('add-media-capture-input').getAttribute('capture')).toBe('environment');
    const file = new File([new Uint8Array([1, 2, 3])], 'clip.mp4', { type: 'video/mp4' });
    fireEvent.change(input, { target: { files: [file] } });
    expect(state.enqueue).toHaveBeenCalledWith([{ file, relativePath: 'clip.mp4' }], '');
  });

  it('shows progress in plain words', () => {
    state.uploads = [entry({ status: 'uploading', progress: 42 })];
    mount();
    expect(screen.getByTestId('add-media-upload-text').textContent).toBe('Uploading 42%');
  });

  it('a failed upload says so with Try again', async () => {
    state.uploads = [entry({ status: 'error', error: 'The connection dropped.' })];
    mount();
    expect(screen.getByTestId('add-media-upload-text').textContent).toBe('Did not upload');
    expect(screen.getByText('The connection dropped.')).toBeTruthy();
    await act(async () => fireEvent.click(screen.getByTestId('add-media-retry')));
    expect(state.retry).toHaveBeenCalledWith('u1');
  });
});

describe('RM.8 preview and notes, and the touch scrub', () => {
  it('Keywords and notes opens the editor and Back returns to the tiles', async () => {
    mount();
    await menuFor(VIDEO.mediaRef);
    await act(async () => fireEvent.click(await screen.findByTestId('tile-annotate')));
    expect(screen.getByTestId('add-media-annotate')).toBeTruthy();
    await act(async () => fireEvent.click(screen.getByTestId('add-media-back')));
    expect(screen.getByTestId('add-media-grid')).toBeTruthy();
  });

  it('Keywords, note and flag are saved on change with one call carrying only what changed, and the editor is finger-sized', async () => {
    setTestViewport('phone');
    vi.mocked(upsertMediaAnnotation).mockResolvedValue({ mediaRef: VIDEO.mediaRef, keywords: ['b-roll'], note: 'Use the second take', colorLabel: null, favourite: false, flag: true, updatedAt: '2026-10-01T10:00:00Z' } as never);
    mount();
    await menuFor(VIDEO.mediaRef);
    await act(async () => fireEvent.click(await screen.findByTestId('tile-annotate')));
    const keywords = screen.getByRole('combobox', { name: 'Keywords' });
    await userEvent.setup().type(keywords, 'B-Roll{Enter}');
    await act(async () => fireEvent.change(screen.getByTestId('annotation-note'), { target: { value: 'Use the second take' } }));
    await act(async () => fireEvent.click(screen.getByTestId('annotation-flag')));
    // Wave 2: the annotation saves on change (no Save button); the fields stay finger-sized.
    expect(screen.queryByTestId('annotation-save')).toBeNull();
    expect(keywords.closest('[data-testid="annotation-keywords"], div')).toBeTruthy();
    await waitFor(() => expect(upsertMediaAnnotation).toHaveBeenCalledTimes(1), { timeout: 3000 });
    expect(upsertMediaAnnotation).toHaveBeenCalledWith(PROJECT, VIDEO.mediaRef, { keywords: ['b-roll'], note: 'Use the second take', flag: true });
  });

  it('the preview mark and subclip buttons are 44 px on a phone', async () => {
    setTestViewport('phone');
    vi.mocked(fetchMediaDerivatives).mockResolvedValue(DERIVATIVES_ALL_KINDS as never);
    mount();
    await menuFor(VIDEO.mediaRef);
    await act(async () => fireEvent.click(await screen.findByTestId('tile-preview')));
    for (const id of ['mark-in', 'mark-out', 'mark-clear', 'create-subclip']) {
      expect((await screen.findByTestId(id)).style.minHeight).toBe('44px');
    }
  });

  it('Preview offers Insert at playhead and Add to end for the open clip', async () => {
    const { onPlace, onPlaced } = mount();
    await menuFor(VIDEO.mediaRef);
    await act(async () => fireEvent.click(await screen.findByTestId('tile-preview')));
    expect(screen.getByTestId('add-media-preview')).toBeTruthy();
    await act(async () => fireEvent.click(screen.getByTestId('preview-append')));
    expect(onPlace).toHaveBeenCalledWith(VIDEO, 'append', null);
    expect(onPlaced).toHaveBeenCalledTimes(1);
  });

  it('a sideways touch drag over a tile shows the position label', async () => {
    mount();
    const picture = tileOf(VIDEO.mediaRef).querySelector('[data-testid="media-clip-tile"]') as HTMLElement;
    picture.getBoundingClientRect = () => ({ left: 0, top: 0, right: 200, bottom: 100, width: 200, height: 100, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect;
    const touch = (x: number, y: number) => ({ clientX: x, clientY: y, pointerId: 3, pointerType: 'touch' });
    fireEvent.pointerDown(picture, touch(20, 50));
    await act(async () => {
      fireEvent.pointerMove(picture, touch(60, 52));
    });
    await act(async () => {
      fireEvent.pointerMove(picture, touch(180, 52));
    });
    const label = await screen.findByTestId('media-tile-scrub-label');
    expect(label.textContent).toMatch(/of|Preview loading/);
    await act(async () => {
      fireEvent.pointerUp(picture, touch(180, 52));
    });
    expect(screen.queryByTestId('media-tile-scrub-label')).toBeNull();
  });
});
