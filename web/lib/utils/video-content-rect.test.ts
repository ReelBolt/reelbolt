import { describe, expect, it } from 'vitest';
import { containedContentRect } from './video-content-rect';

describe('containedContentRect', () => {
  it('pillarboxes a portrait video in a landscape box', () => {
    const r = containedContentRect(1600, 900, 1080, 1920);
    expect(r.height).toBeCloseTo(900);
    expect(r.width).toBeCloseTo(506.25);
    expect(r.x).toBeCloseTo((1600 - 506.25) / 2);
    expect(r.y).toBeCloseTo(0);
  });

  it('letterboxes a landscape video in a portrait box', () => {
    const r = containedContentRect(400, 800, 1920, 1080);
    expect(r.width).toBeCloseTo(400);
    expect(r.height).toBeCloseTo(225);
    expect(r.y).toBeCloseTo((800 - 225) / 2);
  });

  it('falls back to the whole box until the media size is known', () => {
    expect(containedContentRect(640, 360, 0, 0)).toEqual({ x: 0, y: 0, width: 640, height: 360 });
  });
});
