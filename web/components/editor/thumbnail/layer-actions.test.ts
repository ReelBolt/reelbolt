import { describe, expect, it } from 'vitest';
import { nudgeStepPx, nudgeTransform, reorderTarget } from './layer-actions';

const T = { x: 100, y: 50, scale: 1, rotation: 0, flipX: false, flipY: false };

describe('layer menu targets (1-based from the back)', () => {
  it('bring forward and send back move one step', () => {
    expect(reorderTarget('forward', 1, 4)).toBe(3);
    expect(reorderTarget('back', 2, 4)).toBe(2);
  });
  it('front and back go to the ends', () => {
    expect(reorderTarget('front', 0, 4)).toBe(4);
    expect(reorderTarget('backmost', 3, 4)).toBe(1);
  });
  it('are disabled at the ends', () => {
    expect(reorderTarget('forward', 3, 4)).toBeNull();
    expect(reorderTarget('front', 3, 4)).toBeNull();
    expect(reorderTarget('back', 0, 4)).toBeNull();
    expect(reorderTarget('backmost', 0, 4)).toBeNull();
  });
});

describe('nudge pad math', () => {
  it('moves 1 px or 10 px per press', () => {
    expect(nudgeStepPx(false)).toBe(1);
    expect(nudgeStepPx(true)).toBe(10);
    expect(nudgeTransform(T, 'ArrowRight', false)).toMatchObject({ x: 101, y: 50 });
    expect(nudgeTransform(T, 'ArrowLeft', true)).toMatchObject({ x: 90, y: 50 });
    expect(nudgeTransform(T, 'ArrowUp', true)).toMatchObject({ x: 100, y: 40 });
    expect(nudgeTransform(T, 'ArrowDown', false)).toMatchObject({ x: 100, y: 51 });
  });
});
