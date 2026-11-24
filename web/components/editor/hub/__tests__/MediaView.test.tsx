// @vitest-environment jsdom
/* eslint @typescript-eslint/no-unused-vars: ["warn", { "argsIgnorePattern": "^_" }] */
/**
 * T-022 (audit top-10 #7, d2 N4): the media pane refreshes itself from the pipeline's own
 * `media.derivative.progress` events — no "Check again" click and no re-hover needed.
 */
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, cleanup, fireEvent, waitFor, act } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

const state = vi.hoisted(() => ({
  pool: { data: [] as unknown[], error: undefined, isLoading: false, mutate: (() => Promise.resolve(undefined)) as (...args: unknown[]) => Promise<unknown> },
}));

vi.mock('@/lib/hooks/use-media', () => ({
  useMediaPool: () => state.pool,
  useMediaBins: () => ({ data: [], error: undefined, isLoading: false, mutate: vi.fn() }),
  useMediaAnnotations: () => ({ data: [], error: undefined, isLoading: false, mutate: vi.fn() }),
}));

vi.mock('@/lib/editing/api/media', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/api/media')>()),
  fetchMediaDerivatives: vi.fn(),
  fetchMediaFilmstrip: vi.fn(),
  requestMediaDerivatives: vi.fn(),
  upsertMediaAnnotation: vi.fn(),
}));

import { MediaView } from '../MediaView';
import { fetchMediaDerivatives, fetchMediaFilmstrip, requestMediaDerivatives } from '@/lib/editing/api/media';
import { DERIVATIVES_ALL_KINDS, FILMSTRIP_RESPONSE, POOL } from '@/lib/editing/media/fixtures';
import { MEDIA_DERIVATIVE_PROGRESS_EVENT } from '@/lib/hooks/use-media-derivatives';

const PROJECT_ID = '00000000-0000-4000-8000-000000000009';
const derivativesSpy = vi.mocked(fetchMediaDerivatives);

class FakeEventSource {
  static instances: FakeEventSource[] = [];
  listeners = new Map<string, ((event: Event) => void)[]>();
  constructor(public url: string) {
    FakeEventSource.instances.push(this);
  }
  addEventListener(type: string, handler: (event: Event) => void) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), handler]);
  }
  removeEventListener() {}
  close() {}
}

const emit = (mediaRef: string, stage: string) => {
  const data = JSON.stringify({ type: MEDIA_DERIVATIVE_PROGRESS_EVENT, projectId: PROJECT_ID, data: { projectId: PROJECT_ID, sourceProjectFileId: mediaRef.replace(/^file:/, ''), stage } });
  for (const source of FakeEventSource.instances)
    for (const handler of source.listeners.get(MEDIA_DERIVATIVE_PROGRESS_EVENT) ?? []) handler(new MessageEvent(MEDIA_DERIVATIVE_PROGRESS_EVENT, { data }));
};

const tiles = () => screen.queryAllByTestId('media-clip-tile');
const fileItem = POOL.find((item) => item.mediaRef.startsWith('file:'))!;
const fileTileIndex = POOL.indexOf(fileItem);

beforeEach(() => {
  FakeEventSource.instances = [];
  vi.stubGlobal('EventSource', FakeEventSource);
  vi.stubGlobal(
    'ResizeObserver',
    class {
      constructor(_cb: ResizeObserverCallback) {}
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
  Element.prototype.scrollIntoView = vi.fn();
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
  state.pool = { data: POOL, error: undefined, isLoading: false, mutate: vi.fn(() => Promise.resolve(undefined)) };
  derivativesSpy.mockReset();
  derivativesSpy.mockResolvedValue(DERIVATIVES_ALL_KINDS);
  vi.mocked(fetchMediaFilmstrip).mockResolvedValue(FILMSTRIP_RESPONSE);
  vi.mocked(requestMediaDerivatives).mockReturnValue(new Promise(() => {}));
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const renderView = (readOnly = false) =>
  render(
    <MantineProvider>
      <MediaView projectId={PROJECT_ID} readOnly={readOnly} />
    </MantineProvider>,
  );

describe('MediaView — the pane refreshes itself', () => {
  it('re-reads a hovered source when its preparation moves on, once per stage', async () => {
    renderView();
    expect(FakeEventSource.instances.map((s) => s.url)).toEqual(['/api/v1/workflows/events']);
    await act(async () => {
      fireEvent.mouseEnter(tiles()[fileTileIndex]);
    });
    expect(derivativesSpy).toHaveBeenCalledTimes(1);

    await act(async () => emit(fileItem.mediaRef, 'Proxy'));
    expect(derivativesSpy).toHaveBeenCalledTimes(2);
    expect(derivativesSpy.mock.calls[1][1]).toBe(fileItem.mediaRef);

    // The same stage again (a percentage tick) does not re-read.
    await act(async () => emit(fileItem.mediaRef, 'Proxy'));
    expect(derivativesSpy).toHaveBeenCalledTimes(2);
  });

  it('re-reads the pool (and so the probe) once a run finishes', async () => {
    renderView();
    await act(async () => emit(fileItem.mediaRef, 'Done'));
    await waitFor(() => expect(state.pool.mutate).toHaveBeenCalled(), { timeout: 2000 });
  });

  it('says where the open clip\'s preparation is, in plain words', async () => {
    renderView();
    await act(async () => {
      fireEvent.click(tiles()[fileTileIndex]);
    });
    await act(async () => emit(fileItem.mediaRef, 'Filmstrip'));
    expect(screen.getByTestId('media-derivative-progress').textContent).toBe('Preparing this file: making the filmstrip.');
  });
});

describe('MediaView — Enhance on a video tile', () => {
  const videoIndex = POOL.findIndex((item) => item.kind === 'video');
  const menuOf = (index: number) => screen.getAllByTestId('media-tile-menu')[index];

  it('offers Enhance... on a video tile for an editor who can change things', async () => {
    renderView(false);
    await act(async () => {
      fireEvent.click(menuOf(videoIndex));
    });
    expect(await screen.findByTestId('tile-enhance')).toBeTruthy();
  });

  it('hides Enhance... for a read-only editor', async () => {
    renderView(true);
    await act(async () => {
      fireEvent.click(menuOf(videoIndex));
    });
    expect(screen.queryByTestId('tile-enhance')).toBeNull();
  });
});
