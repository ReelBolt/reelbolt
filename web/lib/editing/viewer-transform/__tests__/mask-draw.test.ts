import { describe, expect, it } from 'vitest';
import type { MediaClip } from '@/lib/editing/types';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import { MIN_SHAPE_FRACTION, drawBoxFromDrag, maskFromBox, maskToolState } from '../frame-handles';

const canvas = { width: 1000, height: 500 };

describe('drawBoxFromDrag', () => {
  it('spans press to pointer, in any direction', () => {
    expect(drawBoxFromDrag({ x: 200, y: 100 }, { x: 600, y: 300 }, canvas)).toEqual({ x: 0.4, y: 0.4, w: 0.4, h: 0.4 });
    expect(drawBoxFromDrag({ x: 600, y: 300 }, { x: 200, y: 100 }, canvas)).toEqual({ x: 0.4, y: 0.4, w: 0.4, h: 0.4 });
  });
  it('stays inside the canvas', () => {
    const b = drawBoxFromDrag({ x: 900, y: 400 }, { x: 5000, y: 5000 }, canvas);
    expect(b.x + b.w / 2).toBeLessThanOrEqual(1);
    expect(b.y + b.h / 2).toBeLessThanOrEqual(1);
    expect(b).toEqual({ x: 0.95, y: 0.9, w: 0.1, h: 0.2 });
  });
  it('is never below the minimum, even for a click', () => {
    const b = drawBoxFromDrag({ x: 500, y: 250 }, { x: 500, y: 250 }, canvas);
    expect(b.w).toBeGreaterThanOrEqual(MIN_SHAPE_FRACTION);
    expect(b.h).toBeGreaterThanOrEqual(MIN_SHAPE_FRACTION);
    const edge = drawBoxFromDrag({ x: 1000, y: 500 }, { x: 1000, y: 500 }, canvas);
    expect(edge.x + edge.w / 2).toBeLessThanOrEqual(1);
  });
  it('Shift draws a square picture ratio', () => {
    const b = drawBoxFromDrag({ x: 100, y: 100 }, { x: 400, y: 150 }, canvas, { keepAspect: true });
    expect(b.w * canvas.width).toBeCloseTo(b.h * canvas.height, 0);
  });
  it('Alt draws from the centre', () => {
    expect(drawBoxFromDrag({ x: 500, y: 250 }, { x: 600, y: 300 }, canvas, { fromCentre: true })).toEqual({ x: 0.5, y: 0.5, w: 0.2, h: 0.2 });
    const near = drawBoxFromDrag({ x: 100, y: 250 }, { x: 800, y: 300 }, canvas, { fromCentre: true });
    expect(near.x - near.w / 2).toBeGreaterThanOrEqual(0);
  });
});

describe('maskToolState and maskFromBox', () => {
  const withMask = (mask: unknown) => doc([track(TRACKS.v1, 'video', [{ ...media('c1', 10, 60, 0), mask } as unknown as MediaClip])]);
  it('names each state the tool shows', () => {
    expect(maskToolState(withMask(null), [], 20).kind).toBe('no-clip');
    expect(maskToolState(withMask(null), ['c1'], 5).kind).toBe('off-screen');
    expect(maskToolState(withMask(null), ['c1'], 20).kind).toBe('draw');
    expect(maskToolState(withMask({ kind: 'ellipse', x: 0.5, y: 0.5, w: 0.5, h: 0.5 }), ['c1'], 20).kind).toBe('edit');
    expect(maskToolState(withMask({ kind: 'segmentation', analysisStepResultId: 'a', trackIds: [], invert: false, featherPx: 1 }), ['c1'], 20).kind).toBe('object');
  });
  it('a redraw keeps feather, invert and kind', () => {
    const next = maskFromBox({ x: 0.3, y: 0.3, w: 0.2, h: 0.2 }, { kind: 'ellipse', x: 0.5, y: 0.5, w: 0.5, h: 0.5, feather: 0.2, invert: true });
    expect(next).toEqual({ kind: 'ellipse', x: 0.3, y: 0.3, w: 0.2, h: 0.2, feather: 0.2, invert: true });
  });
});

describe('corner pin (T-013)', () => {
  const pin = { topLeft: { x: 0, y: 0 }, topRight: { x: 1, y: 0 }, bottomRight: { x: 1, y: 1 }, bottomLeft: { x: 0, y: 1 } };
  const document = doc([track(TRACKS.v1, 'video', [{ ...media('c1', 10, 60, 0), cornerPin: pin } as unknown as MediaClip])]);
  it('clamps a corner to the field range [-1, 2]', async () => {
    const { movePinCorner } = await import('../frame-handles');
    expect(movePinCorner(pin, 'topLeft', { x: -5, y: 9 }).topLeft).toEqual({ x: -1, y: 2 });
  });
  it('maps pixels to canvas fractions', async () => {
    const { pinPointFromPixel } = await import('../frame-handles');
    expect(pinPointFromPixel({ x: 250, y: 125 }, canvas)).toEqual({ x: 0.25, y: 0.25 });
  });
  it('states: edit / off-screen / no-clip', async () => {
    const { pinToolState } = await import('../frame-handles');
    expect(pinToolState(document, ['c1'], 20).kind).toBe('edit');
    expect(pinToolState(document, ['c1'], 80).kind).toBe('off-screen');
    expect(pinToolState(document, [], 20).kind).toBe('no-clip');
  });
  it('ops: one setProperty cornerPin, none when unchanged', async () => {
    const { pinOps, movePinCorner } = await import('../frame-handles');
    const clip = document.tracks[0].items[0] as MediaClip;
    expect(pinOps(clip, pin)).toEqual([]);
    const ops = pinOps(clip, movePinCorner(pin, 'bottomRight', { x: 0.5, y: 0.5 }));
    expect(ops).toHaveLength(1);
    expect(ops[0]).toMatchObject({ type: 'setProperty', itemId: 'c1', key: 'cornerPin' });
  });
  it('a draft previews on the document without touching the original', async () => {
    const { applyFrameDraft } = await import('../frame-handles');
    const moved = { ...pin, topLeft: { x: 0.2, y: 0.2 } };
    const out = applyFrameDraft(document, { kind: 'pin', itemId: 'c1', pin: moved });
    expect((out.tracks[0].items[0] as MediaClip).cornerPin?.topLeft.x).toBe(0.2);
    expect((document.tracks[0].items[0] as MediaClip).cornerPin?.topLeft.x).toBe(0);
  });
});
