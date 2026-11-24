import { describe, expect, it } from 'vitest';
import type { AdjustmentClip, MediaClip, ShapeMask } from '@/lib/editing/types';
import { createEditorStore } from '@/lib/editing/store';
import { leafPlayheadFrame } from '@/lib/editing/store/coarse-ui';
import { PANEL_PLAYHEAD_QUANTUM_SEC } from '@/components/editor/shell/LivePlayhead';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import {
  BOX_HANDLES,
  TRACKED_WINDOW_NOTE,
  applyFrameDraft,
  boxToFrame,
  createFrameTargetMemo,
  featherFromPointer,
  featherHandlePoint,
  featherInsetPx,
  frameToBox,
  maskOps,
  moveFrameBy,
  nudgeFrame,
  pixelToPoint,
  reframeOps,
  reframeWindow,
  resizeFrame,
  resolveFrameTargets,
  selectedFrameItem,
  toggleInvertOps,
  windowOps,
  type Frame,
} from '..';

const canvas = { width: 1000, height: 500 };
const handle = (id: string) => BOX_HANDLES.find((h) => h.id === id)!;
const rect: Frame = { cx: 500, cy: 250, w: 200, h: 100, rotation: 0 };
const mask = { kind: 'rect' as const, x: 0.5, y: 0.5, w: 0.2, h: 0.2, feather: 0.05, invert: false };
const clipWith = (extra: Partial<MediaClip> = {}) => media('c1', 0, 100, 0, extra);

describe('box / frame conversion', () => {
  it('round-trips canvas fractions through pixels, rotation 0', () => {
    const f = boxToFrame({ x: 0.25, y: 0.5, w: 0.5, h: 0.2 }, canvas);
    expect(f).toEqual({ cx: 250, cy: 250, w: 500, h: 100, rotation: 0 });
    expect(frameToBox(f, canvas)).toEqual({ x: 0.25, y: 0.5, w: 0.5, h: 0.2 });
  });
});

describe('moveFrameBy', () => {
  it('moves and clamps the whole frame inside the canvas', () => {
    expect(moveFrameBy(rect, 50, -20, canvas)).toMatchObject({ cx: 550, cy: 230 });
    const far = moveFrameBy(rect, 5000, 5000, canvas);
    expect(far.cx).toBe(900);
    expect(far.cy).toBe(450);
    expect(moveFrameBy(rect, -5000, -5000, canvas)).toMatchObject({ cx: 100, cy: 50 });
  });
});

describe('resizeFrame', () => {
  it('a corner keeps the opposite corner; an edge changes one axis only', () => {
    const br = resizeFrame(rect, handle('br'), { x: 700, y: 350 }, canvas);
    expect([br.cx - br.w / 2, br.cy - br.h / 2]).toEqual([400, 200]);
    expect([br.cx + br.w / 2, br.cy + br.h / 2]).toEqual([700, 350]);
    const r = resizeFrame(rect, handle('r'), { x: 800, y: 10 }, canvas);
    expect(r).toMatchObject({ cx: 600, cy: 250, w: 400, h: 100 });
    const t = resizeFrame(rect, handle('t'), { x: 0, y: 100 }, canvas);
    expect(t).toMatchObject({ cx: 500, cy: 200, w: 200, h: 200 });
  });
  it('clamps to the canvas and holds a minimum size', () => {
    const big = resizeFrame(rect, handle('br'), { x: 9999, y: 9999 }, canvas);
    expect(big.cx + big.w / 2).toBe(1000);
    expect(big.cy + big.h / 2).toBe(500);
    const tiny = resizeFrame(rect, handle('br'), { x: 0, y: 0 }, canvas);
    expect(tiny.w).toBeCloseTo(20);
    expect(tiny.h).toBeCloseTo(10);
  });
  it('Shift on a corner keeps the aspect; Alt grows from the centre', () => {
    const k = resizeFrame(rect, handle('br'), { x: 800, y: 260 }, canvas, { keepAspect: true });
    expect(k.w / k.h).toBeCloseTo(2);
    const c = resizeFrame(rect, handle('br'), { x: 600, y: 300 }, canvas, { fromCentre: true });
    expect(c).toMatchObject({ cx: 500, cy: 250, w: 200, h: 100 });
    const c2 = resizeFrame(rect, handle('br'), { x: 650, y: 290 }, canvas, { fromCentre: true });
    expect(c2).toMatchObject({ cx: 500, cy: 250, w: 300, h: 80 });
  });
  it('Shift never grows past the canvas', () => {
    const k = resizeFrame(rect, handle('br'), { x: 9999, y: 300 }, canvas, { keepAspect: true });
    expect(k.cx + k.w / 2).toBeLessThanOrEqual(1000 + 1e-6);
    expect(k.cy + k.h / 2).toBeLessThanOrEqual(500 + 1e-6);
    expect(k.w / k.h).toBeCloseTo(2);
  });
  it('has eight handles, and an ellipse uses the same bounding-box handles', () => {
    expect(BOX_HANDLES.map((h) => h.id).sort()).toEqual(['b', 'bl', 'br', 'l', 'r', 't', 'tl', 'tr']);
  });
  it('rotation is always 0', () => {
    expect(resizeFrame(rect, handle('tl'), { x: 300, y: 150 }, canvas).rotation).toBe(0);
  });
});

describe('feather', () => {
  it('the inset follows the feather and never exceeds half the shape', () => {
    expect(featherInsetPx(rect, canvas, 0.05)).toBe(25);
    expect(featherInsetPx(rect, canvas, 0.5)).toBe(50);
    expect(featherInsetPx(rect, canvas, 0)).toBe(0);
  });
  it('the handle round-trips through the pointer', () => {
    for (const kind of ['rect', 'ellipse'] as const) {
      const at = featherHandlePoint(rect, kind, canvas, 0.08);
      expect(featherFromPointer(rect, kind, canvas, at)).toBeCloseTo(0.08, 3);
    }
  });
  it('dragging out to the edge is zero, in past the middle is capped', () => {
    expect(featherFromPointer(rect, 'rect', canvas, { x: 900, y: 250 })).toBe(0);
    expect(featherFromPointer(rect, 'rect', canvas, { x: 500, y: 250 })).toBeLessThanOrEqual(0.5);
    expect(featherFromPointer(rect, 'rect', canvas, { x: 500, y: 250 })).toBeCloseTo(0.1, 3);
  });
});

describe('nudgeFrame', () => {
  it('1 percent and 10 percent of the canvas, clamped', () => {
    expect(nudgeFrame(rect, 0.01, 0, canvas)).toMatchObject({ cx: 510, cy: 250 });
    expect(nudgeFrame(rect, 0, 0.1, canvas)).toMatchObject({ cx: 500, cy: 300 });
    expect(nudgeFrame(rect, 1, 0, canvas).cx).toBe(900);
  });
});

describe('reframe', () => {
  it('the kept window has the canvas aspect, is centred on the point and stays inside the picture', () => {
    const picture = { left: 0, top: 0, right: 1000, bottom: 500 };
    expect(reframeWindow(picture, canvas, { x: 0.5, y: 0.5 })).toMatchObject({ cx: 500, cy: 250, w: 1000, h: 500 });
    // A square picture inside the wide canvas: the window is the widest wide strip.
    const square = { left: 250, top: 0, right: 750, bottom: 500 };
    const w = reframeWindow(square, canvas, { x: 0.1, y: 0.1 });
    expect(w.w / w.h).toBeCloseTo(2);
    expect(w.cx - w.w / 2).toBeGreaterThanOrEqual(250);
    expect(w.cy - w.h / 2).toBeGreaterThanOrEqual(0);
  });
  it('pixels become a point clamped to [0, 1]', () => {
    expect(pixelToPoint({ x: 250, y: 125 }, canvas)).toEqual({ x: 0.25, y: 0.25 });
    expect(pixelToPoint({ x: -50, y: 9999 }, canvas)).toEqual({ x: 0, y: 1 });
  });
});

describe('draft to ops', () => {
  it('a mask change is ONE setProperty mask; an unchanged mask writes nothing', () => {
    const c = clipWith({ mask });
    expect(maskOps(c, { ...mask })).toEqual([]);
    const ops = maskOps(c, { ...mask, x: 0.3, w: 0.4 });
    expect(ops).toEqual([{ type: 'setProperty', itemId: 'c1', key: 'mask', value: { ...mask, x: 0.3, w: 0.4 } }]);
  });
  it('a feather-only change is one op; invert toggles one op', () => {
    const c = clipWith({ mask });
    expect(maskOps(c, { ...mask, feather: 0.1 })).toHaveLength(1);
    expect(toggleInvertOps(c)).toEqual([{ type: 'setProperty', itemId: 'c1', key: 'mask', value: { ...mask, invert: true } }]);
    expect(toggleInvertOps(clipWith())).toEqual([]);
  });
  it('a reframe point writes setProperty reframe x,y, none when unchanged', () => {
    const c = clipWith({ reframe: { x: 0.5, y: 0.5 } });
    expect(reframeOps(c, { x: 0.5, y: 0.5 })).toEqual([]);
    expect(reframeOps(c, { x: 0.2, y: 0.8 })).toEqual([{ type: 'setProperty', itemId: 'c1', key: 'reframe', value: { x: 0.2, y: 0.8 } }]);
  });
  it('a window change rewrites only the shape: offsets and tracking stay', () => {
    const tracked = { shape: { kind: 'ellipse' as const, x: 0.5, y: 0.5, w: 0.3, h: 0.3 }, offsetX: { value: 0, keys: [{ frame: 0, value: 0.1, interp: 'linear' as const }] }, offsetY: null };
    const c = clipWith({ grade: { nodes: [{ id: 'n1', name: 'n', enabled: true, window: tracked }, { id: 'n2', name: 'm', enabled: true }] } });
    const ops = windowOps(c, 'n1', { ...tracked.shape, x: 0.4 });
    expect(ops).toHaveLength(1);
    const value = (ops[0] as { value: { nodes: { id: string; window?: typeof tracked }[] } }).value;
    expect(value.nodes[0].window?.offsetX).toEqual(tracked.offsetX);
    expect(value.nodes[0].window?.shape.x).toBe(0.4);
    expect(value.nodes[1]).toEqual(c.grade?.nodes?.[1]);
    expect(windowOps(c, 'n1', tracked.shape)).toEqual([]);
    expect(windowOps(c, 'missing', tracked.shape)).toEqual([]);
  });
  it('applyFrameDraft shows the draft in the preview document only', () => {
    const d = doc([track(TRACKS.v1, 'video', [clipWith({ mask, reframe: { x: 0.5, y: 0.5 } })])]);
    const m = applyFrameDraft(d, { kind: 'mask', itemId: 'c1', shape: { ...mask, x: 0.9 } });
    expect(((m.tracks[0].items[0] as MediaClip).mask as ShapeMask).x).toBe(0.9);
    expect(((d.tracks[0].items[0] as MediaClip).mask as ShapeMask).x).toBe(0.5);
    const r = applyFrameDraft(d, { kind: 'reframe', itemId: 'c1', point: { x: 0.1, y: 0.2 } });
    expect((r.tracks[0].items[0] as MediaClip).reframe).toEqual({ x: 0.1, y: 0.2 });
    expect(applyFrameDraft(d, null)).toBe(d);
  });
});

describe('what the viewer shows', () => {
  const win = { shape: { kind: 'rect' as const, x: 0.5, y: 0.5, w: 0.3, h: 0.3 }, offsetX: { value: 0.1, keys: null }, offsetY: null };
  const graded = clipWith({ grade: { nodes: [{ id: 'n1', name: 'n', enabled: true, window: win }] } });
  it('mask and reframe show for the selected clip; a window only when the Colour panel asks', () => {
    const c = clipWith({ mask, reframe: { x: 0.3, y: 0.7 } });
    expect(resolveFrameTargets(c, 0, null).map((t) => t.id)).toEqual(['mask', 'reframe']);
    expect(resolveFrameTargets(clipWith(), 0, null)).toEqual([]);
    expect(resolveFrameTargets(graded, 0, null)).toEqual([]);
    const t = resolveFrameTargets(graded, 0, { itemId: 'c1', nodeId: 'n1' });
    expect(t).toHaveLength(1);
    expect(t[0]).toMatchObject({ id: 'window', tracked: false, offset: { x: 0.1, y: 0 } });
  });
  it('a window with keyed offsets is tracked (read-only), and the note is the plain line', () => {
    const tracked = clipWith({ grade: { nodes: [{ id: 'n1', name: 'n', enabled: true, window: { ...win, offsetX: { value: 0, keys: [{ frame: 0, value: 0, interp: 'linear' }, { frame: 10, value: 0.2, interp: 'linear' }] } } }] } });
    const [t] = resolveFrameTargets(tracked, 5, { itemId: 'c1', nodeId: 'n1' });
    expect(t).toMatchObject({ id: 'window', tracked: true });
    expect((t as { offset: { x: number } }).offset.x).toBeCloseTo(0.1);
    expect(TRACKED_WINDOW_NOTE).toBe('Tracked windows follow the picture. Turn off tracking to move this by hand.');
  });
  it('selection: one media or adjustment clip on a picture track at the playhead', () => {
    const adj = { id: 'a1', type: 'adjustmentClip', name: 'Adj', recordIn: 0, recordOut: 50, enabled: true, grade: null, effects: null, provenance: null } as AdjustmentClip;
    const d = doc([track(TRACKS.v1, 'video', [clipWith()]), track(TRACKS.v2, 'video', [adj]), track(TRACKS.a1, 'audio', [media('au', 0, 100)])]);
    expect(selectedFrameItem(d, ['c1'], 10)?.id).toBe('c1');
    expect(selectedFrameItem(d, ['a1'], 10)?.id).toBe('a1');
    expect(selectedFrameItem(d, ['c1'], 100)).toBeNull();
    expect(selectedFrameItem(d, ['au'], 10)).toBeNull();
    expect(selectedFrameItem(d, ['c1', 'a1'], 10)).toBeNull();
  });
});

describe('the target memo and the 4 Hz leaf quantum (§7.3)', () => {
  const d = doc([track(TRACKS.v1, 'video', [clipWith({ mask, reframe: { x: 0.3, y: 0.7 } })])]);

  it('reuses the same answer until the document, selection, frame or window target changes', () => {
    const memo = createFrameTargetMemo();
    const a = memo.resolve(d, ['c1'], 8, null);
    expect(a?.targets.map((t) => t.id)).toEqual(['mask', 'reframe']);
    expect(memo.resolve(d, ['c1'], 8, null)).toBe(a); // same inputs: the same object
    const later = memo.resolve(d, ['c1'], 16, null);
    expect(later).not.toBe(a); // the next sampled frame recomputes
    expect(memo.resolve(d, ['c2'], 16, null)).toBeNull();
    const withWindow = memo.resolve(d, ['c1'], 16, { itemId: 'c1', nodeId: 'n1' });
    expect(withWindow?.targets.map((t) => t.id)).toEqual(['mask', 'reframe']);
    expect(memo.resolve(d, ['c1'], 16, null)).not.toBe(withWindow); // the published target changed
  });

  it('the sampled playhead only moves at the 0.25 s quantum (30 fps -> 8 frames)', () => {
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: d, revision: 1 });
    store.setUi({ playbackSpeed: 1 });
    const at = (frame: number) => {
      store.setUi({ playheadFrame: frame });
      return leafPlayheadFrame(store.getState(), PANEL_PLAYHEAD_QUANTUM_SEC);
    };
    expect([at(10), at(11), at(15)]).toEqual([8, 8, 8]);
    expect([at(16), at(23)]).toEqual([16, 16]);
    expect(at(24)).toBe(24);
    store.setUi({ playbackSpeed: 0 });
    expect(at(11)).toBe(11); // exact when paused
  });
});
