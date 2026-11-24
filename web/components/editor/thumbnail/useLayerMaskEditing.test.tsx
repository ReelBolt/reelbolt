// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, renderHook } from '@testing-library/react';
import { ApiError } from '@/lib/api/client';
import { LayerMaskBuffer } from '@/lib/editor/layer-mask';
import type { EditorLayer } from '@/lib/editor/model';
import type { BitmapMap } from '@/lib/editor/compose';
import { fakeBitmap } from '@/lib/editor/__tests__/recording-context';

const api = vi.hoisted(() => ({ putImageLayerMatte: vi.fn(), deleteImageLayerMatte: vi.fn() }));
vi.mock('@/lib/api/image-documents', async (orig) => ({ ...(await orig<typeof import('@/lib/api/image-documents')>()), ...api }));

vi.mock('@/lib/editor/canvas-dom', async () => {
  const { LayerMaskBuffer: Buffer } = await import('@/lib/editor/layer-mask');
  class MaskSurface {
    alpha = { width: 4, height: 4, name: 'alpha' };
    gray = { width: 4, height: 4, name: 'gray' };
    constructor(public mask: InstanceType<typeof Buffer>) {}
    sync() {
      this.mask.takeDirty();
    }
  }
  return {
    MaskSurface,
    maskFromMatte: () => new Buffer(4, 4),
    maskToPng: async () => new Blob(['png'], { type: 'image/png' }),
    matteAsGray: (m: unknown) => m,
  };
});

import { useLayerMaskEditing } from './useLayerMaskEditing';

const layer = { id: 'L1', kind: 'source', label: 'Photo', width: 4, height: 4, matteUrl: '/m', locked: false } as unknown as EditorLayer;
const bitmaps: BitmapMap = new Map([['L1', { image: fakeBitmap(4, 4, 'img'), matte: fakeBitmap(4, 4, 'matte') }]]);

function setup(over: { layers?: readonly EditorLayer[]; bitmaps?: BitmapMap } = {}) {
  const seed = vi.fn();
  const onMessage = vi.fn();
  const onActive = vi.fn();
  const writeMatte = vi.fn(async (_id: string, call: (base: number | undefined) => Promise<{ matteUrl: string | null; revision: number | null }>, before?: (u: string | null) => void) => {
    const r = await call(5);
    const url = r.matteUrl ? `${r.matteUrl}?v=${r.revision}` : null;
    before?.(url);
    return url;
  });
  const hook = renderHook(() =>
    useLayerMaskEditing({ projectId: 'p', documentId: 'd', layers: over.layers ?? [layer], bitmaps: over.bitmaps ?? bitmaps, writeMatte, seed, onMessage, onActive }),
  );
  return { ...hook, seed, onMessage, onActive, writeMatte };
}

beforeEach(() => {
  vi.useFakeTimers();
  api.putImageLayerMatte.mockReset();
  api.deleteImageLayerMatte.mockReset();
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

const paint = (result: { current: ReturnType<typeof useLayerMaskEditing> }) => {
  const s = result.current.session!;
  s.buffer.checkpoint();
  s.buffer.stroke({ x: 1, y: 1 }, { x: 1, y: 1 }, { size: 2, hardness: 1, hide: true });
  act(() => result.current.changed());
};

describe('useLayerMaskEditing', () => {
  it('Edit mask opens a session; Done uploads once, preloads the new mask and closes', async () => {
    api.putImageLayerMatte.mockResolvedValue({ layer: { matteUrl: '/m' }, revision: 6 });
    const { result, seed, onActive, writeMatte } = setup();
    act(() => result.current.editMask(layer));
    expect(result.current.session?.layerId).toBe('L1');
    expect(onActive).toHaveBeenCalledWith(true);
    paint(result);
    expect(result.current.unsaved).toBe(true);
    await act(async () => {
      await result.current.done();
    });
    expect(api.putImageLayerMatte).toHaveBeenCalledTimes(1);
    expect(api.putImageLayerMatte.mock.calls[0][3]).toBeInstanceOf(Blob);
    expect(api.putImageLayerMatte.mock.calls[0][4]).toBe(5);
    expect(writeMatte).toHaveBeenCalledTimes(1);
    expect(seed).toHaveBeenCalledWith('/m?v=6', expect.anything());
    expect(result.current.session).toBeNull();
    expect(onActive).toHaveBeenLastCalledWith(false);
  });

  it('a failed save keeps the painting open and says why in plain words; Try again then saves', async () => {
    api.putImageLayerMatte.mockRejectedValue(new ApiError('boom', 503, [], null));
    const { result } = setup();
    act(() => result.current.editMask(layer));
    paint(result);
    const first = act(async () => {
      const p = result.current.done();
      await vi.advanceTimersByTimeAsync(20_000);
      await p;
    });
    await first;
    expect(api.putImageLayerMatte).toHaveBeenCalledTimes(4);
    expect(result.current.session).not.toBeNull();
    expect(result.current.unsaved).toBe(true);
    expect(result.current.saveState).toMatchObject({ phase: 'failed' });
    expect(JSON.stringify(result.current.saveState)).toMatch(/painting is kept/);

    api.putImageLayerMatte.mockResolvedValue({ layer: { matteUrl: '/m' }, revision: 9 });
    await act(async () => {
      await result.current.done();
    });
    expect(result.current.session).toBeNull();
  });

  it('does not retry a refusal such as a full storage plan, and keeps the painting', async () => {
    api.putImageLayerMatte.mockRejectedValue(new ApiError('full', 413, [], null));
    const { result, onMessage } = setup();
    act(() => result.current.editMask(layer));
    paint(result);
    await act(async () => {
      await result.current.done();
    });
    expect(api.putImageLayerMatte).toHaveBeenCalledTimes(1);
    expect(result.current.session).not.toBeNull();
    expect(onMessage).toHaveBeenCalledWith('error', expect.stringMatching(/storage/));
  });

  it('Done with nothing painted closes without a request', async () => {
    const { result } = setup();
    act(() => result.current.editMask(layer));
    await act(async () => {
      await result.current.done();
    });
    expect(api.putImageLayerMatte).not.toHaveBeenCalled();
    expect(result.current.session).toBeNull();
  });

  it('undo of a stroke works inside the session', () => {
    const { result } = setup();
    act(() => result.current.editMask(layer));
    paint(result);
    expect(result.current.canUndo).toBe(true);
    act(() => result.current.undo());
    expect(result.current.session?.buffer.isAllVisible).toBe(true);
    expect(result.current.canRedo).toBe(true);
  });

  it('Add mask uploads an all-visible mask, then opens it for painting', async () => {
    api.putImageLayerMatte.mockResolvedValue({ layer: { matteUrl: '/m' }, revision: 6 });
    const { result } = setup({ layers: [{ ...layer, matteUrl: null }] });
    await act(async () => {
      await result.current.addMask({ ...layer, matteUrl: null });
    });
    expect(api.putImageLayerMatte).toHaveBeenCalledTimes(1);
    expect(result.current.session?.buffer).toBeInstanceOf(LayerMaskBuffer);
    expect(result.current.session?.buffer.isAllVisible).toBe(true);
    expect(result.current.unsaved).toBe(false);
  });

  it('Delete mask calls DELETE and ends a session on that layer', async () => {
    api.deleteImageLayerMatte.mockResolvedValue({ layer: null, revision: 7 });
    const { result } = setup();
    act(() => result.current.editMask(layer));
    await act(async () => {
      await result.current.deleteMask(layer);
    });
    expect(api.deleteImageLayerMatte).toHaveBeenCalledWith('p', 'd', 'L1', 5);
    expect(result.current.session).toBeNull();
  });

  it('Invert mask uploads the inverted pixels without opening a session', async () => {
    api.putImageLayerMatte.mockResolvedValue({ layer: { matteUrl: '/m' }, revision: 6 });
    const { result } = setup();
    await act(async () => {
      await result.current.invertMask(layer);
    });
    expect(api.putImageLayerMatte).toHaveBeenCalledTimes(1);
    expect(result.current.session).toBeNull();
  });

  it('says so, and sends nothing, when the mask has not loaded yet', async () => {
    const { result, onMessage } = setup({ bitmaps: new Map([['L1', { image: fakeBitmap(4, 4, 'img') }]]) });
    act(() => result.current.editMask(layer));
    expect(result.current.session).toBeNull();
    expect(onMessage).toHaveBeenCalledWith('error', expect.stringMatching(/still loading/));
  });
});
