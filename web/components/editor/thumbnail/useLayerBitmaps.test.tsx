// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, renderHook } from '@testing-library/react';
import type { EditorLayer } from '@/lib/editor/model';
import { BITMAP_LOAD_BACKOFF_MS, useLayerBitmaps } from './useLayerBitmaps';

const load = vi.hoisted(() => ({ results: new Map<string, Array<'ok' | 'fail'>>(), calls: [] as string[], matte: [] as string[] }));
vi.mock('@/lib/editor/canvas-dom', async () => {
  const { fakeBitmap } = await import('@/lib/editor/__tests__/recording-context');
  return {
    loadBitmap: async (url: string, options?: { matte?: boolean }) => {
      load.calls.push(url);
      if (options?.matte) load.matte.push(url);
      const queue = load.results.get(url);
      const next = queue && queue.length > 1 ? queue.shift() : queue?.[0];
      if (next === 'fail') throw new Error('A layer image could not be loaded.');
      return fakeBitmap(10, 10);
    },
  };
});

const fetchMock = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>();

const layer = (id: string, imageUrl: string): EditorLayer =>
  ({ id, kind: 'image', label: `Layer ${id}`, imageUrl, visible: true }) as unknown as EditorLayer;

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const settle = () => act(async () => {
  await vi.advanceTimersByTimeAsync(0);
});
const waitOutBackoff = () => act(async () => {
  await vi.advanceTimersByTimeAsync(BITMAP_LOAD_BACKOFF_MS.reduce((a, b) => a + b, 0) + 10);
});

beforeEach(() => {
  vi.useFakeTimers();
  load.results.clear();
  load.calls = [];
  load.matte = [];
  fetchMock.mockReset();
  vi.stubGlobal('fetch', fetchMock);
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('useLayerBitmaps: masks', () => {
  it('loads and hands over the matte of ANY bitmap kind, decoded as a matte', async () => {
    const layers = [{ ...layer('s', '/img/s'), kind: 'source', matteUrl: '/matte/s' } as unknown as EditorLayer];
    const { result } = renderHook(() => useLayerBitmaps(layers));
    await settle();
    expect(load.matte).toEqual(['/matte/s']);
    expect(result.current.bitmaps.get('s')?.matte).toBeDefined();
    expect(load.calls).toContain('/img/s');
  });

  it('a seeded mask is used without being fetched', async () => {
    let layers = [layer('s', '/img/s')];
    const { result, rerender } = renderHook(() => useLayerBitmaps(layers));
    const { fakeBitmap } = await import('@/lib/editor/__tests__/recording-context');
    act(() => result.current.seed('/matte/s?v=2', fakeBitmap(10, 10)));
    layers = [{ ...layer('s', '/img/s'), kind: 'source', matteUrl: '/matte/s?v=2' } as unknown as EditorLayer];
    rerender();
    await settle();
    expect(load.calls).not.toContain('/matte/s?v=2');
    expect(result.current.bitmaps.get('s')?.matte).toBeDefined();
  });
});

describe('useLayerBitmaps', () => {
  it('counts a layer as missing when the server answers 404 LAYER_IMAGE_MISSING, without retrying', async () => {
    load.results.set('/img/a', ['fail']);
    fetchMock.mockResolvedValue(json(404, { code: 'LAYER_IMAGE_MISSING', message: 'gone' }));
    const layers = [layer('a', '/img/a')];
    const { result } = renderHook(() => useLayerBitmaps(layers));
    await settle();
    expect(result.current.missing.has('a')).toBe(true);
    expect(result.current.unavailable.size).toBe(0);
    await waitOutBackoff();
    expect(load.calls).toEqual(['/img/a']);
  });

  it('retries after a 5xx, and loads the layer once the server recovers', async () => {
    load.results.set('/img/a', ['fail', 'ok']);
    fetchMock.mockResolvedValue(json(503, { message: 'busy' }));
    const layers = [layer('a', '/img/a')];
    const { result } = renderHook(() => useLayerBitmaps(layers));
    await settle();
    expect(result.current.missing.size).toBe(0);
    expect(result.current.bitmaps.has('a')).toBe(false);
    await waitOutBackoff();
    expect(load.calls).toEqual(['/img/a', '/img/a']);
    expect(result.current.bitmaps.has('a')).toBe(true);
    expect(result.current.missing.size).toBe(0);
    expect(result.current.unavailable.size).toBe(0);
  });

  it('a plain 404 without the missing-image code is not counted as missing', async () => {
    load.results.set('/img/a', ['fail']);
    fetchMock.mockResolvedValue(new Response('not found', { status: 404 }));
    const layers = [layer('a', '/img/a')];
    const { result } = renderHook(() => useLayerBitmaps(layers));
    await waitOutBackoff();
    expect(result.current.missing.size).toBe(0);
    expect(result.current.unavailable.has('a')).toBe(true);
  });

  it('after a bounded number of network errors reports the layer as unavailable, never missing', async () => {
    load.results.set('/img/a', ['fail']);
    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'));
    const layers = [layer('a', '/img/a')];
    const { result } = renderHook(() => useLayerBitmaps(layers));
    await waitOutBackoff();
    await waitOutBackoff();
    expect(load.calls.length).toBe(BITMAP_LOAD_BACKOFF_MS.length + 1);
    expect(result.current.missing.size).toBe(0);
    expect(result.current.unavailable.has('a')).toBe(true);
  });

  it('retry() tries an unavailable layer again and clears it once it loads', async () => {
    load.results.set('/img/a', ['fail', 'fail', 'fail', 'ok']);
    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'));
    const layers = [layer('a', '/img/a')];
    const { result } = renderHook(() => useLayerBitmaps(layers));
    await waitOutBackoff();
    expect(result.current.unavailable.has('a')).toBe(true);
    act(() => result.current.retry());
    expect(result.current.unavailable.size).toBe(0);
    await settle();
    expect(result.current.bitmaps.has('a')).toBe(true);
  });
});
