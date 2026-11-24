// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import { ApiError } from '@/lib/api/client';
import type { EditImageDocument, EditImageLayer } from '@/lib/types/image-document';

const api = vi.hoisted(() => ({
  getImageDocument: vi.fn(),
  updateImageDocument: vi.fn(),
  updateImageLayer: vi.fn(),
  deleteImageLayer: vi.fn(),
}));
vi.mock('@/lib/api/image-documents', async (orig) => ({ ...(await orig<typeof import('@/lib/api/image-documents')>()), ...api }));

import { useEditorDocument } from './useEditorDocument';

const layer = (id: string, over: Partial<EditImageLayer> = {}): EditImageLayer => ({
  id,
  zOrder: 1,
  role: 'overlay',
  kind: 'image',
  label: id,
  sourceProjectFileId: null,
  imageUrl: `/img/${id}`,
  matteUrl: null,
  mimeType: 'image/png',
  width: 100,
  height: 100,
  opacity: 1,
  transform: { x: 0, y: 0, scale: 1, rotation: 0 },
  segmentationModel: null,
  segmentationPrompt: null,
  createdAt: '',
  visible: true,
  ...over,
});

const docDto = (layers: EditImageLayer[], revision: number): EditImageDocument => ({
  id: 'd',
  projectId: 'p',
  name: 'Cover',
  sourceProjectFileId: 'f',
  revision,
  createdAt: '',
  updatedAt: '',
  layers,
  canvasWidth: 1280,
  canvasHeight: 720,
  backgroundColor: null,
});

const A = layer('a', { zOrder: 1 });
const B = layer('b', { zOrder: 2 });

async function opened() {
  const hook = renderHook(() => useEditorDocument('p', 'd'));
  await waitFor(() => expect(hook.result.current.doc).not.toBeNull());
  return hook;
}

beforeEach(() => {
  for (const fn of Object.values(api)) fn.mockReset();
  api.getImageDocument.mockResolvedValue(docDto([A], 3));
});
afterEach(cleanup);

describe('syncFromServer', () => {
  it('brings in layers the server added as one undo step that takes them out', async () => {
    const { result } = await opened();
    api.getImageDocument.mockResolvedValue(docDto([A, B], 4));

    let outcome: Awaited<ReturnType<typeof result.current.syncFromServer>> | undefined;
    await act(async () => {
      outcome = await result.current.syncFromServer({ kind: 'layersAdded', layerIds: ['b', 'gone'], label: 'Design a graphic' });
    });

    expect(outcome).toEqual({ synced: true, present: ['b'] });
    expect(result.current.doc?.layers.map((l) => l.id)).toEqual(['a', 'b']);
    expect(result.current.revision).toBe(4);
    expect(result.current.undoLabel).toBe('Design a graphic');
    expect(result.current.saveStatus).toBe('saved');

    act(() => result.current.undo());
    expect(result.current.doc?.layers.map((l) => l.id)).toEqual(['a']);
    act(() => result.current.redo());
    expect(result.current.doc?.layers.map((l) => l.id)).toEqual(['a', 'b']);
  });

  it('adopts any other change and clears the history, since local steps may no longer apply', async () => {
    const { result } = await opened();
    api.updateImageLayer.mockResolvedValue(A);
    act(() => result.current.commit(() => ({ label: 'Rename', do: (s) => ({ ...s, layers: s.layers.map((l) => ({ ...l, label: 'Renamed' })) }), undo: (s) => s })));
    expect(result.current.canUndo).toBe(true);

    api.getImageDocument.mockResolvedValue(docDto([{ ...A, label: 'Renamed', opacity: 0.4 }], 6));
    let outcome: Awaited<ReturnType<typeof result.current.syncFromServer>> | undefined;
    await act(async () => {
      outcome = await result.current.syncFromServer({ kind: 'layersChanged', layerIds: ['a'], label: 'Assistant change' });
    });

    // The pending rename went out first, with the revision this editor had.
    expect(api.updateImageLayer).toHaveBeenCalledWith('p', 'd', 'a', { label: 'Renamed', baseRevision: 3 });
    expect(outcome).toEqual({ synced: true, present: ['a'] });
    expect(result.current.doc?.layers[0].opacity).toBe(0.4);
    expect(result.current.canUndo).toBe(false);
    expect(result.current.revision).toBe(6);
  });

  it('stops after a conflict: the reload already adopted the server state, with a notice', async () => {
    const { result } = await opened();
    api.updateImageLayer.mockRejectedValue(new ApiError('Changed elsewhere.', 409, [], { code: 'REVISION_CONFLICT' }));
    act(() => result.current.commit(() => ({ label: 'Rename', do: (s) => ({ ...s, layers: s.layers.map((l) => ({ ...l, label: 'Mine' })) }), undo: (s) => s })));
    api.getImageDocument.mockResolvedValue(docDto([A, B], 9));

    let outcome: Awaited<ReturnType<typeof result.current.syncFromServer>> | undefined;
    await act(async () => {
      outcome = await result.current.syncFromServer({ kind: 'layersAdded', layerIds: ['b'], label: 'Design a graphic' });
    });

    expect(outcome).toEqual({ synced: false, present: [] });
    expect(result.current.notice).toMatch(/changed somewhere else/);
    expect(result.current.doc?.layers.map((l) => l.id)).toEqual(['a', 'b']);
    // Only the reload read the document: syncFromServer did not read it a second time.
    expect(api.getImageDocument).toHaveBeenCalledTimes(2);
  });

  it('a deleted layer is simply gone afterwards, and the history is cleared', async () => {
    api.getImageDocument.mockResolvedValue(docDto([A, B], 3));
    const { result } = await opened();
    api.getImageDocument.mockResolvedValue(docDto([A], 4));
    let outcome: Awaited<ReturnType<typeof result.current.syncFromServer>> | undefined;
    await act(async () => {
      outcome = await result.current.syncFromServer({ kind: 'layersDeleted', layerIds: ['b'], label: 'x' });
    });
    expect(outcome).toEqual({ synced: true, present: [] });
    expect(result.current.doc?.layers.map((l) => l.id)).toEqual(['a']);
    expect(result.current.canUndo).toBe(false);
  });
});

describe('leaving the editor keeps the last edit (T8)', () => {
  const rename = (label: string) => ({
    label: 'Rename',
    do: (s: Parameters<Parameters<ReturnType<typeof useEditorDocument>['commit']>[0]>[0]) => ({
      ...s,
      layers: s.layers.map((l) => ({ ...l, label })),
    }),
    undo: (s: Parameters<Parameters<ReturnType<typeof useEditorDocument>['commit']>[0]>[0]) => s,
  });

  it('sends a pending edit at once when the editor goes away within the save delay', async () => {
    const { result, unmount } = await opened();
    api.updateImageLayer.mockResolvedValue(A);
    act(() => result.current.commit(() => rename('Mine')));
    expect(api.updateImageLayer).not.toHaveBeenCalled();
    unmount();
    await waitFor(() => expect(api.updateImageLayer).toHaveBeenCalledTimes(1));
    expect(api.updateImageLayer).toHaveBeenCalledWith('p', 'd', 'a', { label: 'Mine', baseRevision: 3 }, { keepalive: true });
  });

  it('sends a pending edit with keepalive when the page is hidden or closed', async () => {
    const { result } = await opened();
    api.updateImageLayer.mockResolvedValue(A);
    act(() => result.current.commit(() => rename('Closing')));
    act(() => {
      window.dispatchEvent(new Event('pagehide'));
    });
    expect(api.updateImageLayer).toHaveBeenCalledWith('p', 'd', 'a', { label: 'Closing', baseRevision: 3 }, { keepalive: true });
  });

  it('sends nothing on leaving when everything is saved', async () => {
    const { unmount } = await opened();
    act(() => {
      window.dispatchEvent(new Event('pagehide'));
    });
    unmount();
    await new Promise((r) => setTimeout(r, 20));
    expect(api.updateImageLayer).not.toHaveBeenCalled();
    expect(api.updateImageDocument).not.toHaveBeenCalled();
  });
});

describe('undo leaves no ghost layers (R13.5)', () => {
  const rename = (id: string): Parameters<Awaited<ReturnType<typeof opened>>['result']['current']['commit']>[0] => (d) => ({
    label: 'Rename',
    do: (s) => ({ ...s, layers: s.layers.map((l) => (l.id === id ? { ...l, label: 'X' } : l)) }),
    undo: (s) => ({ ...s, layers: s.layers.map((l) => (l.id === id ? { ...l, label: d.layers.find((o) => o.id === id)!.label } : l)) }),
  });

  async function withAdded() {
    const hook = await opened();
    api.getImageDocument.mockResolvedValue(docDto([A, B], 4));
    await act(async () => {
      await hook.result.current.addLayers('Cut out', async () => [B]);
    });
    return hook;
  }

  it('hides the undone layer on the server while a redo is possible, and never deletes it yet', async () => {
    const { result } = await withAdded();
    api.updateImageLayer.mockResolvedValue(B);
    act(() => result.current.undo());
    expect(result.current.doc?.layers.map((l) => l.id)).toEqual(['a']);
    await act(async () => {
      await result.current.flush();
    });
    expect(api.updateImageLayer).toHaveBeenCalledWith('p', 'd', 'b', expect.objectContaining({ visible: false }));
    expect(api.deleteImageLayer).not.toHaveBeenCalled();
  });

  it('deletes the undone layer on the server once a new edit drops the redo', async () => {
    const { result } = await withAdded();
    api.deleteImageLayer.mockResolvedValue(undefined);
    api.updateImageLayer.mockResolvedValue(A);
    act(() => result.current.undo());
    act(() => result.current.commit(rename('a')));
    api.getImageDocument.mockResolvedValue(docDto([A], 6));
    await act(async () => {
      await result.current.flush();
    });
    expect(api.deleteImageLayer).toHaveBeenCalledWith('p', 'd', 'b');
    expect(result.current.doc?.layers.map((l) => l.id)).toEqual(['a']);
    expect(result.current.canRedo).toBe(false);
  });

  it('Delete is one undo step: undo brings the layer back and the server row is kept', async () => {
    const { result } = await opened();
    api.getImageDocument.mockResolvedValue(docDto([A], 3));
    act(() => result.current.removeLayers(['a'], 'Delete layer'));
    expect(result.current.doc?.layers).toEqual([]);
    act(() => result.current.undo());
    expect(result.current.doc?.layers.map((l) => l.id)).toEqual(['a']);
    await act(async () => {
      await result.current.flush();
    });
    expect(api.deleteImageLayer).not.toHaveBeenCalled();
  });
});
