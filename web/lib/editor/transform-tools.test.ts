import { describe, expect, it } from 'vitest';
import fc from 'fast-check';
import { layerBounds, type PlacedLayer } from './compose';
import {
  SCALE_MAX,
  SCALE_MIN,
  handlePositions,
  hitHandle,
  isNudgeKey,
  moveByDelta,
  normalizeDegrees,
  nudge,
  rotationFromDrag,
  sameTransform,
  scaleFromCornerDrag,
} from './transform-tools';

function layer(t: Partial<PlacedLayer['transform']> = {}): PlacedLayer {
  return {
    id: 'l',
    kind: 'image',
    role: 'overlay',
    label: 'l',
    zOrder: 1,
    visible: true,
    locked: false,
    opacity: 1,
    blendMode: 'normal',
    transform: { x: 100, y: 100, scale: 1, rotation: 0, flipX: false, flipY: false, ...t },
    width: 200,
    height: 100,
    imageUrl: '/l',
    matteUrl: null,
    content: null,
    segmentationPrompt: null,
    size: { w: 200, h: 100 },
  };
}

describe('handles', () => {
  it('places corner handles on the rotated corners and the rotate handle above the top edge', () => {
    const h = handlePositions(layer(), 30);
    expect(h.tl).toEqual({ x: 100, y: 100 });
    expect(h.br).toEqual({ x: 300, y: 200 });
    expect(h.rotate.x).toBeCloseTo(200, 6);
    expect(h.rotate.y).toBeCloseTo(70, 6);
  });

  it('keeps the rotate handle on the layer\'s own "up" when it is turned', () => {
    // Rotated 90° clockwise, the layer's top edge faces right.
    const h = handlePositions(layer({ rotation: 90 }), 30);
    const c = layerBounds(layer({ rotation: 90 })).center;
    // top-edge midpoint is 50 px right of the centre, and the handle floats 30 px beyond it
    expect(h.rotate.x).toBeCloseTo(c.x + 80, 6);
    expect(h.rotate.y).toBeCloseTo(c.y, 6);
  });

  it('hits the nearest handle within tolerance, and nothing outside it', () => {
    const l = layer();
    expect(hitHandle(l, { x: 103, y: 98 }, 10, 30)).toBe('tl');
    expect(hitHandle(l, { x: 200, y: 71 }, 10, 30)).toBe('rotate');
    expect(hitHandle(l, { x: 200, y: 150 }, 10, 30)).toBeNull();
  });
});

describe('scaleFromCornerDrag', () => {
  it('scales uniformly and keeps the opposite corner fixed on the canvas', () => {
    const l = layer();
    // Drag the bottom-right corner so the diagonal doubles.
    const diagonal = Math.hypot(200, 100);
    const pointer = { x: 100 + 2 * 200, y: 100 + 2 * 100 };
    const next = scaleFromCornerDrag(l, 'br', pointer);
    expect(next.scale).toBeCloseTo(2, 6);
    expect(next.x).toBeCloseTo(100, 6);
    expect(next.y).toBeCloseTo(100, 6);
    expect(Math.hypot(pointer.x - 100, pointer.y - 100)).toBeCloseTo(diagonal * 2, 6);
  });

  it('dragging the top-left corner holds the bottom-right corner still', () => {
    const l = layer();
    const next = scaleFromCornerDrag(l, 'tl', { x: 200, y: 150 }); // half the diagonal
    const after = layerBounds({ ...l, transform: next }).corners[2];
    expect(after.x).toBeCloseTo(300, 6);
    expect(after.y).toBeCloseTo(200, 6);
    expect(next.scale).toBeCloseTo(0.5, 6);
  });

  it('holds the opposite corner fixed under rotation too', () => {
    fc.assert(
      fc.property(
        fc.integer({ min: -180, max: 180 }),
        fc.double({ min: 0.3, max: 3, noNaN: true }),
        (rotation, factor) => {
          const l = layer({ rotation });
          const [tl, , br] = layerBounds(l).corners;
          const pointer = { x: br.x + (br.x - tl.x) * (factor - 1), y: br.y + (br.y - tl.y) * (factor - 1) };
          const next = scaleFromCornerDrag(l, 'br', pointer);
          const anchor = layerBounds({ ...l, transform: next }).corners[0];
          expect(anchor.x).toBeCloseTo(tl.x, 4);
          expect(anchor.y).toBeCloseTo(tl.y, 4);
        },
      ),
    );
  });

  it('clamps the scale to a sane range', () => {
    expect(scaleFromCornerDrag(layer(), 'br', { x: 100, y: 100 }).scale).toBe(SCALE_MIN);
    expect(scaleFromCornerDrag(layer(), 'br', { x: 1e9, y: 1e9 }).scale).toBe(SCALE_MAX);
  });

  it('never changes rotation or flips', () => {
    const next = scaleFromCornerDrag(layer({ rotation: 33, flipX: true }), 'br', { x: 400, y: 300 });
    expect(next.rotation).toBe(33);
    expect(next.flipX).toBe(true);
  });
});

describe('rotationFromDrag', () => {
  it('points the layer\'s up at the pointer', () => {
    const l = layer(); // centre (200, 150)
    expect(rotationFromDrag(l, { x: 200, y: 0 }, false)).toBeCloseTo(0, 6);
    expect(rotationFromDrag(l, { x: 400, y: 150 }, false)).toBeCloseTo(90, 6);
    expect(rotationFromDrag(l, { x: 200, y: 400 }, false)).toBeCloseTo(180, 6);
    expect(rotationFromDrag(l, { x: 0, y: 150 }, false)).toBeCloseTo(-90, 6);
  });

  it('snaps to 15 degrees with Shift', () => {
    const l = layer();
    const raw = rotationFromDrag(l, { x: 300, y: 20 }, false);
    expect(raw % 15).not.toBeCloseTo(0, 0);
    expect(rotationFromDrag(l, { x: 300, y: 20 }, true) % 15).toBeCloseTo(0, 6);
  });

  it('round-trips with the handle position', () => {
    for (const rotation of [-170, -45, 0, 30, 90, 135]) {
      const l = layer({ rotation });
      const handle = handlePositions(l, 40).rotate;
      expect(rotationFromDrag(l, handle, false)).toBeCloseTo(rotation, 4);
    }
  });
});

describe('normalizeDegrees', () => {
  it('folds into (-180, 180]', () => {
    expect(normalizeDegrees(190)).toBe(-170);
    expect(normalizeDegrees(-190)).toBe(170);
    expect(normalizeDegrees(180)).toBe(180);
    expect(normalizeDegrees(-180)).toBe(180);
    expect(normalizeDegrees(720)).toBe(0);
  });
});

describe('move and nudge', () => {
  const t = layer().transform;

  it('moves by the drag delta from the transform at pointer-down', () => {
    expect(moveByDelta(t, { x: 12, y: -5 }, false)).toMatchObject({ x: 112, y: 95 });
  });

  it('Shift pins the move to the dominant axis', () => {
    expect(moveByDelta(t, { x: 30, y: 5 }, true)).toMatchObject({ x: 130, y: 100 });
    expect(moveByDelta(t, { x: 4, y: -30 }, true)).toMatchObject({ x: 100, y: 70 });
  });

  it('nudges one pixel, or ten with Shift, in the arrow direction', () => {
    expect(nudge(t, 'ArrowLeft', false).x).toBe(99);
    expect(nudge(t, 'ArrowRight', true).x).toBe(110);
    expect(nudge(t, 'ArrowUp', false).y).toBe(99);
    expect(nudge(t, 'ArrowDown', true).y).toBe(110);
  });

  it('recognises only the four arrow keys', () => {
    expect(isNudgeKey('ArrowUp')).toBe(true);
    expect(isNudgeKey('Enter')).toBe(false);
  });

  it('knows when a drag changed nothing', () => {
    expect(sameTransform(t, { ...t })).toBe(true);
    expect(sameTransform(t, { ...t, rotation: 1 })).toBe(false);
  });
});
