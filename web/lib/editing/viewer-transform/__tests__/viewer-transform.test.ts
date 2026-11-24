import { describe, expect, it } from 'vitest';
import type { EditTimeline, MediaClip } from '@/lib/editing/types';
import {
  CORNER_HANDLES,
  applyDraft,
  baseSize,
  bounds,
  cornerPresetValues,
  fillValues,
  fitValues,
  frameFor,
  keyedChannels,
  mirrorOf,
  moveBy,
  padValues,
  rotateTo,
  scaleFromCorner,
  snapMove,
  snapTargets,
  transformAt,
  transformOps,
  wrapDegrees,
  localToCanvas,
  type TransformValues,
} from '..';

const canvas = { width: 1920, height: 1080 };
const id: TransformValues = { positionX: 0, positionY: 0, scale: 1, rotation: 0 };
const square = baseSize(canvas, { width: 1000, height: 1000 }, 'fit');

const clip = (over: Partial<MediaClip> = {}): MediaClip =>
  ({ id: 'c1', type: 'mediaClip', recordIn: 0, recordOut: 100, sourceIn: 0, assetRef: { kind: 'projectFile', projectFileId: 'f' }, ...over }) as MediaClip;

describe('baseSize', () => {
  it('fit letterboxes a square source, fill covers, stretch is the canvas', () => {
    expect(baseSize(canvas, { width: 1000, height: 1000 }, 'fit')).toEqual({ w: 1080, h: 1080 });
    expect(baseSize(canvas, { width: 1000, height: 1000 }, 'fill')).toEqual({ w: 1920, h: 1920 });
    expect(baseSize(canvas, { width: 1000, height: 1000 }, 'stretch')).toEqual({ w: 1920, h: 1080 });
    expect(baseSize(canvas, null, 'fit')).toEqual({ w: 1920, h: 1080 });
  });
  it('subtracts the crop', () => {
    expect(baseSize(canvas, null, 'fit', { left: 0.25, right: 0.25 })).toEqual({ w: 960, h: 1080 });
  });
});

describe('move and snap', () => {
  it('moves by a pixel delta as canvas fractions', () => {
    const m = moveBy(id, 192, -108, canvas);
    expect(m.positionX).toBeCloseTo(0.1);
    expect(m.positionY).toBeCloseTo(-0.1);
  });
  it('clamps the position to the document range', () => {
    expect(moveBy(id, 1e6, 0, canvas).positionX).toBe(2);
  });
  it('snaps the centre to the canvas centre within the threshold and reports the guide', () => {
    const f = frameFor({ ...id, positionX: 5 / 1920 }, square, canvas);
    const s = snapMove(f, snapTargets(canvas, []), 8);
    expect(s.dx).toBeCloseTo(-5);
    expect(s.guides.some((g) => g.axis === 'x' && g.kind === 'centre')).toBe(true);
  });
  it('does not snap beyond the threshold', () => {
    const f = frameFor({ ...id, positionX: 0.013, positionY: 0.013 }, baseSize(canvas, null, 'fit'), canvas);
    const s = snapMove(f, snapTargets(canvas, []).filter((t) => t.kind === 'centre'), 8);
    expect(s.dx).toBe(0);
    expect(s.dy).toBe(0);
    expect(s.guides).toEqual([]);
  });
  it('snaps to thirds, safe margins and other clips', () => {
    const small = baseSize(canvas, null, 'fit');
    const third = frameFor({ ...id, scale: 0.2, positionX: 1 / 3 - 0.5 + 0.002 }, small, canvas);
    expect(snapMove(third, snapTargets(canvas, []), 6).guides.some((g) => g.kind === 'third' || g.kind === 'centre' || g.kind === 'safe')).toBe(true);
    const other = frameFor({ ...id, scale: 0.3, positionX: 0.25 }, small, canvas);
    const mover = frameFor({ ...id, scale: 0.3, positionX: -0.25 + 0.002 }, small, canvas);
    const hit = snapMove(mover, snapTargets(canvas, [other]).filter((t) => t.kind === 'clip'), 6);
    expect(hit.guides.length).toBeGreaterThan(0);
  });
});

describe('scale', () => {
  const start = frameFor(id, square, canvas);
  const br = CORNER_HANDLES.find((h) => h.id === 'br')!;
  it('dragging the corner anchors the opposite corner and keeps the ratio', () => {
    const tl0 = localToCanvas(start, { x: -0.5, y: -0.5 });
    const p = localToCanvas(start, { x: 1.5, y: 1.5 });
    const next = scaleFromCorner(id, start, br, p, canvas);
    expect(next.scale).toBeCloseTo(2);
    const after = frameFor(next, square, canvas);
    const tl1 = localToCanvas(after, { x: -0.5, y: -0.5 });
    expect(tl1.x).toBeCloseTo(tl0.x);
    expect(tl1.y).toBeCloseTo(tl0.y);
  });
  it('Alt scales from the centre', () => {
    const p = localToCanvas(start, { x: 1, y: 1 });
    const next = scaleFromCorner(id, start, br, p, canvas, { fromCentre: true });
    expect(next.scale).toBeCloseTo(2);
    expect(next.positionX).toBeCloseTo(0);
    expect(next.positionY).toBeCloseTo(0);
  });
  it('works on a rotated clip along its own axes', () => {
    const rotated = { ...id, rotation: 30 };
    const f = frameFor(rotated, square, canvas);
    const p = localToCanvas(f, { x: 1.5, y: 1.5 });
    expect(scaleFromCorner(rotated, f, br, p, canvas).scale).toBeCloseTo(2);
  });
  it('never goes below the minimum or above the maximum', () => {
    expect(scaleFromCorner(id, start, br, { x: 0, y: 0 }, canvas).scale).toBeGreaterThan(0);
    expect(scaleFromCorner(id, start, br, { x: 1e7, y: 1e7 }, canvas).scale).toBe(8);
  });
});

describe('rotate', () => {
  it('points the clip up at the pointer, clockwise positive', () => {
    expect(rotateTo({ x: 0, y: 0 }, { x: 10, y: 0 })).toBeCloseTo(90);
    expect(rotateTo({ x: 0, y: 0 }, { x: 0, y: -10 })).toBeCloseTo(0);
    expect(rotateTo({ x: 0, y: 0 }, { x: -10, y: 0 })).toBeCloseTo(-90);
  });
  it('snaps to 15 degrees with a step and wraps into (-180, 180]', () => {
    expect(rotateTo({ x: 0, y: 0 }, { x: 3, y: -10 }, 15)).toBe(15);
    expect(wrapDegrees(270)).toBe(-90);
    expect(wrapDegrees(-180)).toBe(180);
  });
});

describe('presets store exact values', () => {
  it('fit and fill', () => {
    expect(fitValues(square, canvas)).toEqual({ positionX: 0, positionY: 0, rotation: 0, scale: 1 });
    expect(fillValues(square, canvas).scale).toBeCloseTo(1920 / 1080, 5);
    const wide = baseSize(canvas, { width: 2000, height: 1000 }, 'fit');
    expect(fitValues(wide, canvas).scale).toBeCloseTo(1, 5);
  });
  it('the 3x3 pad puts the edges flush with the canvas', () => {
    const half = { ...id, scale: 0.5 };
    const tl = padValues(half, square, canvas, -1, -1);
    const b = bounds(frameFor(tl, square, canvas));
    expect(b.left).toBeCloseTo(0);
    expect(b.top).toBeCloseTo(0);
    const c = padValues(half, square, canvas, 0, 0);
    expect(c.positionX).toBe(0);
    expect(c.positionY).toBe(0);
    const br = bounds(frameFor(padValues(half, square, canvas, 1, 1), square, canvas));
    expect(br.right).toBeCloseTo(1920);
    expect(br.bottom).toBeCloseTo(1080);
  });
  it('picture-in-corner is small, upright and inside the margin', () => {
    const v = cornerPresetValues(square, canvas, 1, 1);
    expect(v.rotation).toBe(0);
    expect(v.scale).toBeCloseTo(0.3, 5);
    const b = bounds(frameFor(v, square, canvas));
    expect(b.right).toBeCloseTo(1920 * 0.96);
    expect(b.bottom).toBeCloseTo(1080 * 0.96);
  });
});

describe('writing the transform', () => {
  it('writes static channels in one setProperty and only what changed', () => {
    const ops = transformOps(clip(), { ...id, positionX: 0.25 }, 10);
    expect(ops).toHaveLength(1);
    expect(ops[0]).toMatchObject({ type: 'setProperty', key: 'transform' });
    const value = (ops[0] as { value: { positionX: { value: number } } }).value;
    expect(value.positionX.value).toBe(0.25);
  });
  it('writes a key at the playhead for a channel that is already keyframed', () => {
    const c = clip({ transform: { positionX: { value: 0, keys: [{ frame: 0, value: 0, interp: 'linear' }, { frame: 50, value: 0.5, interp: 'linear' }] } } });
    const ops = transformOps(c, { ...id, positionX: 0.2, scale: 2 }, 25);
    expect(ops.map((o) => o.type)).toEqual(['setProperty', 'setKeyframe']);
    expect(ops[1]).toMatchObject({ param: 'positionX', frame: 25, value: 0.2 });
    expect(keyedChannels(c).positionX).toBe(true);
    expect(keyedChannels(c).scale).toBe(false);
  });
  it('writes nothing when nothing changed', () => {
    expect(transformOps(clip(), id, 0)).toEqual([]);
  });
  it('reads keyframed values at a frame', () => {
    const c = clip({ transform: { positionX: { value: 0, keys: [{ frame: 0, value: 0, interp: 'linear' }, { frame: 100, value: 1, interp: 'linear' }] } } });
    expect(transformAt(c, 50).positionX).toBeCloseTo(0.5);
  });
});

describe('draft and mirror', () => {
  const doc = { tracks: [{ id: 't', kind: 'video', items: [clip()] }] } as unknown as EditTimeline;
  it('applyDraft shows the draft in a copy and leaves the document alone', () => {
    const out = applyDraft(doc, { itemId: 'c1', values: { ...id, positionX: 0.3 } }, 5);
    expect(((out.tracks[0].items[0] as MediaClip).transform?.positionX?.value)).toBe(0.3);
    expect((doc.tracks[0].items[0] as MediaClip).transform).toBeUndefined();
  });
  it('the mirror follows the draft while dragging', () => {
    expect(mirrorOf(doc, 'c1', 0, null)?.live).toBe(false);
    const m = mirrorOf(doc, 'c1', 0, { itemId: 'c1', values: { ...id, rotation: 45 } });
    expect(m?.live).toBe(true);
    expect(m?.values.rotation).toBe(45);
    expect(mirrorOf(doc, 'nope', 0, null)).toBeNull();
  });
});
