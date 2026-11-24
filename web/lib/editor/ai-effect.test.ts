import { describe, expect, it } from 'vitest';
import {
  REVEAL_BLUR_PX,
  REVEAL_MS,
  WORKING_BLUR_IN_MS,
  WORKING_BLUR_PX,
  layersReady,
  resolveTarget,
  sampleEffect,
  unionRegions,
  type AiEffect,
} from './ai-effect';
import type { PlacedLayer } from './compose';

const placed = (id: string, over: Partial<PlacedLayer> = {}): PlacedLayer =>
  ({
    id,
    kind: 'cutout',
    role: 'subject',
    label: id,
    zOrder: 1,
    visible: true,
    locked: false,
    opacity: 1,
    blendMode: 'normal',
    transform: { x: 100, y: 50, scale: 1, rotation: 0, flipX: false, flipY: false },
    width: 200,
    height: 100,
    imageUrl: '',
    matteUrl: null,
    content: null,
    segmentationPrompt: null,
    size: { w: 200, h: 100 },
    ...over,
  }) as PlacedLayer;

const working: AiEffect = { phase: 'working', target: { kind: 'frame' }, startedAt: 1000 };
const revealing: AiEffect = { phase: 'revealing', target: { kind: 'frame' }, startedAt: 1000 };

describe('while the AI works', () => {
  it('goes soft over a moment, then stays blurred with a moving shimmer and a faint tint', () => {
    const start = sampleEffect(working, 1000);
    expect(start.blurPx).toBe(0);
    const settled = sampleEffect(working, 1000 + WORKING_BLUR_IN_MS);
    expect(settled.blurPx).toBeCloseTo(WORKING_BLUR_PX);
    expect(settled.alpha).toBeCloseTo(1);
    expect(settled.tint).toBeGreaterThan(0);
    expect(settled.done).toBe(false);
    const later = sampleEffect(working, 1000 + WORKING_BLUR_IN_MS + 400);
    expect(later.shimmer).not.toBeNull();
    expect(later.shimmer).not.toBe(settled.shimmer);
    expect(sampleEffect(working, 1_000_000).done).toBe(false); // working never ends on its own
  });

  it('with reduced motion: blurred at once, nothing moving', () => {
    const frame = sampleEffect(working, 1000, true);
    expect(frame.blurPx).toBe(WORKING_BLUR_PX);
    expect(frame.shimmer).toBeNull();
    expect(sampleEffect(working, 5000, true)).toEqual(frame);
  });
});

describe('when the result arrives', () => {
  it('comes out of the blur into focus over the reveal, then is done', () => {
    const first = sampleEffect(revealing, 1000);
    expect(first.blurPx).toBeCloseTo(REVEAL_BLUR_PX);
    expect(first.alpha).toBeCloseTo(1);
    const mid = sampleEffect(revealing, 1000 + REVEAL_MS / 2);
    expect(mid.blurPx).toBeGreaterThan(0);
    expect(mid.blurPx).toBeLessThan(first.blurPx);
    expect(mid.done).toBe(false);
    const end = sampleEffect(revealing, 1000 + REVEAL_MS);
    expect(end.blurPx).toBeCloseTo(0);
    expect(end.alpha).toBeCloseTo(0);
    expect(end.done).toBe(true);
    expect(end.shimmer).toBeNull();
  });

  it('is monotonic: the picture only ever gets sharper', () => {
    let last = Infinity;
    for (let t = 0; t <= REVEAL_MS; t += 25) {
      const { blurPx } = sampleEffect(revealing, 1000 + t);
      expect(blurPx).toBeLessThanOrEqual(last + 1e-9);
      last = blurPx;
    }
  });

  it('with reduced motion: the result is simply there, sharp', () => {
    expect(sampleEffect(revealing, 1000, true)).toMatchObject({ blurPx: 0, alpha: 0, done: true });
  });
});

describe('where the effect goes', () => {
  const frame = { width: 1280, height: 720 };

  it('covers the whole frame for a new picture', () => {
    expect(resolveTarget({ kind: 'frame' }, [], frame)).toEqual({ x: 0, y: 0, w: 1280, h: 720 });
  });

  it('covers the layers it names, together', () => {
    const layers = [placed('a'), placed('b', { transform: { x: 500, y: 400, scale: 0.5, rotation: 0, flipX: false, flipY: false } })];
    expect(resolveTarget({ kind: 'layers', ids: ['a', 'b'] }, layers, frame)).toEqual({ x: 100, y: 50, w: 500, h: 400 });
    expect(resolveTarget({ kind: 'layers', ids: ['missing'] }, layers, frame)).toBeNull();
  });

  it('covers only the painted part of a layer, wherever the layer has been moved and scaled', () => {
    const layer = placed('src', { transform: { x: 10, y: 20, scale: 2, rotation: 0, flipX: false, flipY: false } });
    const region = resolveTarget({ kind: 'mask', layerId: 'src', bounds: { x: 50, y: 10, w: 20, h: 30 } }, [layer], frame);
    expect(region).toEqual({ x: 110, y: 40, w: 40, h: 60 });
  });

  it('covers a region given in document pixels as it is, whatever the layers', () => {
    const bounds = { x: 40, y: 30, w: 400, h: 120 };
    expect(resolveTarget({ kind: 'region', bounds }, [], frame)).toEqual(bounds);
    // It may sit partly outside the frame (a graphic may be placed past the edge).
    expect(resolveTarget({ kind: 'region', bounds: { x: -10, y: 600, w: 300, h: 200 } }, [placed('a')], frame)).toEqual({
      x: -10,
      y: 600,
      w: 300,
      h: 200,
    });
  });

  it('places no effect for an empty or malformed region', () => {
    expect(resolveTarget({ kind: 'region', bounds: { x: 0, y: 0, w: 0, h: 50 } }, [], frame)).toBeNull();
    expect(resolveTarget({ kind: 'region', bounds: { x: Number.NaN, y: 0, w: 10, h: 10 } }, [], frame)).toBeNull();
  });

  it('unions rectangles', () => {
    expect(unionRegions([])).toBeNull();
    expect(unionRegions([{ x: 0, y: 0, w: 10, h: 10 }, { x: 20, y: -5, w: 5, h: 5 }])).toEqual({ x: 0, y: -5, w: 25, h: 15 });
  });

  it('waits until every new layer can be drawn before the reveal starts', () => {
    const layers = [placed('a'), placed('t', { kind: 'text' })];
    expect(layersReady(['a', 't'], layers, () => false)).toBe(false);
    expect(layersReady(['a', 't'], layers, (id) => id === 'a')).toBe(true);
    expect(layersReady(['gone'], layers, () => true)).toBe(false);
  });
});
