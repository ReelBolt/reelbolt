import { describe, expect, it } from 'vitest';
import {
  ASPECT_PRESETS,
  RESOLUTION_TIERS,
  exceedsPhoneLimit,
  matchAspect,
  matchTier,
  originalCanvasSize,
  ORIGINAL_LONG_SIDE_MAX,
  sizeFor,
} from './canvas-size';
import { CANVAS_MAX, CANVAS_MIN } from './model';

const aspect = (id: string) => ASPECT_PRESETS.find((a) => a.id === id)!;
const tier = (label: string) => RESOLUTION_TIERS.find((t) => t.label === label)!;

describe('sizeFor', () => {
  it('names a size by its short side, the way video people do', () => {
    expect(sizeFor(aspect('16:9'), tier('720p'))).toEqual({ width: 1280, height: 720 });
    expect(sizeFor(aspect('16:9'), tier('1080p'))).toEqual({ width: 1920, height: 1080 });
    expect(sizeFor(aspect('16:9'), tier('1440p'))).toEqual({ width: 2560, height: 1440 });
    expect(sizeFor(aspect('16:9'), tier('4K'))).toEqual({ width: 3840, height: 2160 });
    expect(sizeFor(aspect('9:16'), tier('1080p'))).toEqual({ width: 1080, height: 1920 });
    expect(sizeFor(aspect('1:1'), tier('1080p'))).toEqual({ width: 1080, height: 1080 });
    expect(sizeFor(aspect('4:5'), tier('1080p'))).toEqual({ width: 1080, height: 1350 });
    expect(sizeFor(aspect('21:9'), tier('4K'))).toEqual({ width: 5040, height: 2160 });
  });

  it('stays inside the server limits for every shape and size, with even sides', () => {
    for (const a of ASPECT_PRESETS) {
      for (const t of RESOLUTION_TIERS) {
        const { width, height } = sizeFor(a, t);
        for (const side of [width, height]) {
          expect(side).toBeGreaterThanOrEqual(CANVAS_MIN);
          expect(side).toBeLessThanOrEqual(CANVAS_MAX);
          expect(side % 2).toBe(0);
        }
        expect(matchAspect(width, height)?.id).toBe(a.id);
        expect(matchTier(width, height)?.id).toBe(t.id);
      }
    }
  });

  it('keeps the shape when a very wide ratio would pass the largest side', () => {
    const size = sizeFor({ w: 8, h: 1 }, { shortSide: 2160 });
    expect(size.width).toBe(CANVAS_MAX);
    expect(size.width / size.height).toBeCloseTo(8, 1);
  });
});

describe('matching a size back to its shape and size', () => {
  it('reads the presets back and calls everything else custom', () => {
    expect(matchAspect(1280, 720)?.id).toBe('16:9');
    expect(matchTier(1280, 720)?.label).toBe('720p');
    expect(matchAspect(1000, 500)).toBeNull();
    expect(matchAspect(0, 10)).toBeNull();
    expect(matchTier(1000, 501)).toBeNull();
  });
});

describe('exceedsPhoneLimit', () => {
  it('warns only above what a phone can draw', () => {
    expect(exceedsPhoneLimit(3840, 2160)).toBe(false);
    expect(exceedsPhoneLimit(5040, 4000)).toBe(true);
  });
});

describe('originalCanvasSize', () => {
  it('is the photo\'s own pixels when they fit', () => {
    expect(originalCanvasSize({ width: 1080, height: 1920 })).toEqual({ width: 1080, height: 1920 });
    expect(originalCanvasSize({ width: 600, height: 400 })).toEqual({ width: 600, height: 400 });
  });

  it('caps the long side at 4096 and scales the short side with it, keeping the shape', () => {
    expect(ORIGINAL_LONG_SIDE_MAX).toBe(4096);
    expect(originalCanvasSize({ width: 8000, height: 6000 })).toEqual({ width: 4096, height: 3072 });
    expect(originalCanvasSize({ width: 3000, height: 9000 })).toEqual({ width: 1365, height: 4096 });
  });

  it('never goes below the smallest canvas, and answers null for a photo with no size', () => {
    expect(originalCanvasSize({ width: 4, height: 2 })).toEqual({ width: CANVAS_MIN, height: CANVAS_MIN });
    expect(originalCanvasSize({ width: 0, height: 100 })).toBeNull();
  });
});
