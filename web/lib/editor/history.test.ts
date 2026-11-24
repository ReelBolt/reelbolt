import { describe, expect, it } from 'vitest';
import {
  HISTORY_LIMIT,
  MERGE_WINDOW_MS,
  canRedo,
  canUndo,
  clearHistory,
  emptyHistory,
  execute,
  layersAdded,
  layersRemoved,
  layersReplaced,
  patchLayers,
  retainedLayerIds,
  originalSize,
  patchCanvas,
  patchLayer,
  redo,
  redoLabel,
  reorderLayer,
  undo,
  undoLabel,
  type Command,
} from './history';
import type { EditorDocState, EditorLayer } from './model';

function mk(id: string, zOrder: number, over: Partial<EditorLayer> = {}): EditorLayer {
  return {
    id,
    kind: 'image',
    role: 'overlay',
    label: id,
    zOrder,
    visible: true,
    locked: false,
    opacity: 1,
    blendMode: 'normal',
    transform: { x: 0, y: 0, scale: 1, rotation: 0, flipX: false, flipY: false },
    width: 100,
    height: 100,
    imageUrl: `/i/${id}`,
    matteUrl: null,
    content: null,
    segmentationPrompt: null,
    ...over,
  };
}

const base = (): EditorDocState => ({
  canvasWidth: 1280,
  canvasHeight: 720,
  backgroundColor: null,
  layers: [mk('a', 1), mk('b', 2), mk('c', 3)],
});

const ids = (s: EditorDocState) => s.layers.map((l) => l.id);

function run(state: EditorDocState, commands: (Command | null)[], start = 0) {
  let h = emptyHistory;
  let s = state;
  commands.forEach((c, i) => {
    if (!c) return;
    const r = execute(h, s, c, start + i * 10_000);
    h = r.history;
    s = r.state;
  });
  return { h, s };
}

describe('execute / undo / redo', () => {
  it('applies a command, records it, and can undo and redo it exactly', () => {
    const s0 = base();
    const cmd = patchLayer(s0, 'a', { opacity: 0.25 }, 'Opacity')!;
    const { history, state } = execute(emptyHistory, s0, cmd, 0);
    expect(state.layers[0].opacity).toBe(0.25);
    expect(canUndo(history)).toBe(true);
    expect(canRedo(history)).toBe(false);

    const back = undo(history, state);
    expect(back.state).toEqual(s0);
    expect(canRedo(back.history)).toBe(true);

    const forward = redo(back.history, back.state);
    expect(forward.state.layers[0].opacity).toBe(0.25);
    expect(canRedo(forward.history)).toBe(false);
  });

  it('undo and redo with nothing to do are no-ops', () => {
    const s0 = base();
    expect(undo(emptyHistory, s0)).toEqual({ history: emptyHistory, state: s0 });
    expect(redo(emptyHistory, s0)).toEqual({ history: emptyHistory, state: s0 });
  });

  it('a new edit after an undo discards the redo branch', () => {
    const s0 = base();
    const { h, s } = run(s0, [patchLayer(s0, 'a', { opacity: 0.5 }, 'x')]);
    const undone = undo(h, s);
    const next = patchLayer(undone.state, 'b', { locked: true }, 'lock')!;
    const forked = execute(undone.history, undone.state, next, 99_000);
    expect(canRedo(forked.history)).toBe(false);
  });

  it('names the next undo and redo step', () => {
    const s0 = base();
    const { h, s } = run(s0, [patchLayer(s0, 'a', { visible: false }, 'Hide a')]);
    expect(undoLabel(h)).toBe('Hide a');
    const u = undo(h, s);
    expect(undoLabel(u.history)).toBeNull();
    expect(redoLabel(u.history)).toBe('Hide a');
  });

  it('caps the stack at the limit, dropping the oldest', () => {
    let h = emptyHistory;
    let s = base();
    for (let i = 0; i < HISTORY_LIMIT + 5; i += 1) {
      const c = patchLayer(s, 'a', { opacity: i % 2 === 0 ? 0.2 : 0.8 }, `step ${i}`)!;
      const r = execute(h, s, c, i * 10_000);
      h = r.history;
      s = r.state;
    }
    expect(h.past).toHaveLength(HISTORY_LIMIT);
    expect(undoLabel(h)).toBe(`step ${HISTORY_LIMIT + 4}`);
  });

  it('clearHistory forgets everything', () => {
    expect(clearHistory()).toEqual(emptyHistory);
  });
});

describe('merging', () => {
  it('collapses same-key commands inside the window into one step that undoes to the original', () => {
    const s0 = base();
    let h = emptyHistory;
    let s = s0;
    for (const [i, x] of [10, 20, 30].entries()) {
      const c = patchLayer(
        s,
        'a',
        { transform: { ...s.layers[0].transform, x } },
        'Move',
        'move:a',
      )!;
      const r = execute(h, s, c, 1_000 + i * 100);
      h = r.history;
      s = r.state;
    }
    expect(s.layers[0].transform.x).toBe(30);
    expect(h.past).toHaveLength(1);
    expect(undo(h, s).state.layers[0].transform.x).toBe(0);
    const again = redo(undo(h, s).history, undo(h, s).state);
    expect(again.state.layers[0].transform.x).toBe(30);
  });

  it('starts a new step once the window has passed', () => {
    const s0 = base();
    const first = patchLayer(s0, 'a', { opacity: 0.5 }, 'o', 'op:a')!;
    const r1 = execute(emptyHistory, s0, first, 0);
    const second = patchLayer(r1.state, 'a', { opacity: 0.2 }, 'o', 'op:a')!;
    const r2 = execute(r1.history, r1.state, second, MERGE_WINDOW_MS + 1);
    expect(r2.history.past).toHaveLength(2);
  });

  it('never merges across different keys or when a command has no key', () => {
    const s0 = base();
    const first = patchLayer(s0, 'a', { opacity: 0.5 }, 'o', 'op:a')!;
    const r1 = execute(emptyHistory, s0, first, 0);
    const other = patchLayer(r1.state, 'b', { opacity: 0.5 }, 'o', 'op:b')!;
    expect(execute(r1.history, r1.state, other, 10).history.past).toHaveLength(2);
    const keyless = patchLayer(r1.state, 'a', { opacity: 0.1 }, 'o')!;
    expect(execute(r1.history, r1.state, keyless, 10).history.past).toHaveLength(2);
  });
});

describe('patchLayer', () => {
  it('is null for a missing layer and for a patch that changes nothing', () => {
    const s = base();
    expect(patchLayer(s, 'nope', { opacity: 0.1 }, 'x')).toBeNull();
    expect(patchLayer(s, 'a', { opacity: 1 }, 'x')).toBeNull();
    expect(patchLayer(s, 'a', { transform: { ...s.layers[0].transform } }, 'x')).toBeNull();
  });

  it('restores every field it touched on undo, and only those', () => {
    const s0 = base();
    const cmd = patchLayer(s0, 'b', { opacity: 0.3, blendMode: 'multiply', label: 'Renamed' }, 'edit')!;
    const { history, state } = execute(emptyHistory, s0, cmd, 0);
    expect(state.layers[1]).toMatchObject({ opacity: 0.3, blendMode: 'multiply', label: 'Renamed' });
    expect(undo(history, state).state).toEqual(s0);
  });
});

describe('reorderLayer', () => {
  it('moves a layer to a 1-based position and renumbers', () => {
    const s0 = base();
    const { state } = execute(emptyHistory, s0, reorderLayer(s0, 'c', 1)!, 0);
    expect(ids(state)).toEqual(['c', 'a', 'b']);
    expect(state.layers.map((l) => l.zOrder)).toEqual([1, 2, 3]);
  });

  it('undoes back to the original order', () => {
    const s0 = base();
    const r = execute(emptyHistory, s0, reorderLayer(s0, 'a', 3)!, 0);
    expect(ids(r.state)).toEqual(['b', 'c', 'a']);
    expect(ids(undo(r.history, r.state).state)).toEqual(['a', 'b', 'c']);
  });

  it('clamps the position and returns null when nothing would move', () => {
    const s0 = base();
    expect(reorderLayer(s0, 'a', 1)).toBeNull();
    expect(reorderLayer(s0, 'zzz', 2)).toBeNull();
    expect(ids(reorderLayer(s0, 'a', 99)!.do(s0))).toEqual(['b', 'c', 'a']);
  });
});

describe('patchCanvas', () => {
  it('changes size and background, and undoes them', () => {
    const s0 = base();
    const cmd = patchCanvas(s0, { canvasWidth: 1080, canvasHeight: 1080, backgroundColor: '#000000' }, 'preset')!;
    const r = execute(emptyHistory, s0, cmd, 0);
    expect(r.state).toMatchObject({ canvasWidth: 1080, canvasHeight: 1080, backgroundColor: '#000000' });
    expect(undo(r.history, r.state).state).toEqual(s0);
  });

  it('is null when nothing differs', () => {
    expect(patchCanvas(base(), { canvasWidth: 1280, backgroundColor: null }, 'noop')).toBeNull();
  });
});

describe('layersAdded', () => {
  it('takes the new layers out on undo (no ghost) and puts the same layer back on redo', () => {
    const s0: EditorDocState = { ...base(), layers: [...base().layers, mk('new', 4)] };
    const r = execute(emptyHistory, s0, layersAdded(['new'], 'Cut out the person'), 0);
    const u = undo(r.history, r.state);
    expect(ids(u.state)).not.toContain('new');
    expect(u.state.layers.map((l) => l.zOrder)).toEqual([1, 2, 3]);
    const again = redo(u.history, u.state);
    expect(ids(again.state)).toContain('new');
    expect(again.state.layers.find((l) => l.id === 'new')?.visible).toBe(true);
    expect(again.state.layers[again.state.layers.length - 1].id).toBe('new');
  });

  it('keeps the taken-out rows while redo is possible, and lets them go once the redo is dropped', () => {
    const s0: EditorDocState = { ...base(), layers: [...base().layers, mk('new', 4)] };
    const r = execute(emptyHistory, s0, layersAdded(['new'], 'Add'), 0);
    expect(retainedLayerIds(r.history).size).toBe(0);
    const u = undo(r.history, r.state);
    expect([...retainedLayerIds(u.history)]).toEqual(['new']);
    const forked = execute(u.history, u.state, patchLayer(u.state, base().layers[0].id, { opacity: 0.2 }, 'Fade')!, 10_000);
    expect(retainedLayerIds(forked.history).size).toBe(0);
  });
});

describe('layersRemoved', () => {
  it('deletes layers as one step that undo brings back in place', () => {
    const s0 = base();
    const middle = s0.layers[1].id;
    const r = execute(emptyHistory, s0, layersRemoved([middle], 'Delete layer'), 0);
    expect(ids(r.state)).not.toContain(middle);
    expect([...retainedLayerIds(r.history)]).toEqual([middle]);
    const u = undo(r.history, r.state);
    expect(ids(u.state)).toEqual(ids(s0));
    expect(redo(u.history, u.state).state.layers.map((l) => l.id)).not.toContain(middle);
  });
});

describe('layersReplaced and patchLayers', () => {
  it('swaps layers for a merged one as one step, undone together', () => {
    const s0: EditorDocState = { ...base(), layers: [...base().layers, mk('merged', 4)] };
    const [a, b] = [s0.layers[0].id, s0.layers[1].id];
    const r = execute(emptyHistory, s0, layersReplaced([a, b], ['merged'], 'Merge layers'), 0);
    expect(ids(r.state)).toEqual([s0.layers[2].id, 'merged']);
    const u = undo(r.history, r.state);
    expect(ids(u.state)).toEqual([a, b, s0.layers[2].id]);
    expect(canUndo(u.history)).toBe(false);
    expect(ids(redo(u.history, u.state).state)).toEqual([s0.layers[2].id, 'merged']);
  });

  it('moves several layers as one undo step', () => {
    const s0 = base();
    const [a, b] = [s0.layers[0], s0.layers[1]];
    const cmd = patchLayers(s0, [{ id: a.id, patch: { opacity: 0.3 } }, { id: b.id, patch: { opacity: 0.6 } }], 'Align')!;
    const r = execute(emptyHistory, s0, cmd, 0);
    expect(r.state.layers.slice(0, 2).map((l) => l.opacity)).toEqual([0.3, 0.6]);
    expect(undo(r.history, r.state).state.layers.slice(0, 2).map((l) => l.opacity)).toEqual([a.opacity, b.opacity]);
    expect(patchLayers(s0, [{ id: a.id, patch: { opacity: a.opacity } }], 'Align')).toBeNull();
  });
});

describe('originalSize', () => {
  const photo = (over: Partial<EditorLayer> = {}) => mk('src', 1, { kind: 'source', role: 'background', width: 1080, height: 1920, ...over });
  const doc = (layers: EditorLayer[]): EditorDocState => ({ canvasWidth: 1280, canvasHeight: 720, backgroundColor: null, layers });

  it('sets the canvas to the photo and shows the whole photo in it, as one undo step', () => {
    const before = doc([photo({ transform: { x: 40, y: -30, scale: 0.5, rotation: 12, flipX: false, flipY: false } })]);
    const cmd = originalSize(before, 'src', { width: 1080, height: 1920 })!;
    expect(cmd.label).toBe('Original size');
    const after = cmd.do(before);
    expect(after.canvasWidth).toBe(1080);
    expect(after.canvasHeight).toBe(1920);
    expect(after.layers[0].transform).toEqual({ x: 0, y: 0, scale: 1, rotation: 0, flipX: false, flipY: false });
    expect(cmd.undo(after)).toEqual(before);
  });

  it('scales a photo larger than 4096 down to fit the capped canvas', () => {
    const before = doc([photo({ width: 8000, height: 6000 })]);
    const after = originalSize(before, 'src', { width: 8000, height: 6000 })!.do(before);
    expect([after.canvasWidth, after.canvasHeight]).toEqual([4096, 3072]);
    expect(after.layers[0].transform.scale).toBeCloseTo(4096 / 8000);
    expect(after.layers[0].transform.x).toBe(0);
    expect(after.layers[0].transform.y).toBe(0);
  });

  it('is null when the picture is already at its original size', () => {
    const before = { ...doc([photo()]), canvasWidth: 1080, canvasHeight: 1920 };
    expect(originalSize(before, 'src', { width: 1080, height: 1920 })).toBeNull();
  });
});
