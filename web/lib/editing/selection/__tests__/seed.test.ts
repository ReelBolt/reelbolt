import { describe, expect, it } from 'vitest';
import type { MediaClip } from '../../types';
import { invertMapper, moveSeedTo, resizeSeedCorner } from '../seed';
import { canvasPointToSourcePoint, clampBox, defaultBoxAround, isValidSeed, nudgeSeed, seedBody, seedFromBox, seedFromTap } from '../seed';

const clip = (over: Partial<MediaClip> = {}): MediaClip =>
  ({ id: 'c1', type: 'mediaClip', recordIn: 0, recordOut: 300, sourceIn: 0, playbackRate: 1, assetRef: { kind: 'projectFile', projectFileId: 'f1' }, ...over }) as unknown as MediaClip;
const id = { positionX: 0, positionY: 0, scale: 1, rotation: 0 };
const canvas = { width: 1920, height: 1080 };
const source = { width: 1920, height: 1080 };

describe('canvasPointToSourcePoint', () => {
  it('maps the middle and a quarter point of an untransformed picture', () => {
    expect(canvasPointToSourcePoint(clip(), id, { x: 960, y: 540 }, canvas, source, 'fit')).toEqual({ x: 0.5, y: 0.5 });
    const q = canvasPointToSourcePoint(clip(), id, { x: 480, y: 270 }, canvas, source, 'fit')!;
    expect(q.x).toBeCloseTo(0.25);
    expect(q.y).toBeCloseTo(0.25);
  });
  it('accounts for scale', () => {
    const p = canvasPointToSourcePoint(clip(), { ...id, scale: 2 }, { x: 1440, y: 810 }, canvas, source, 'fit')!;
    expect(p.x).toBeCloseTo(0.625);
    expect(p.y).toBeCloseTo(0.625);
  });
  it('accounts for the clip crop', () => {
    const p = canvasPointToSourcePoint(clip({ crop: { left: 0.5, top: 0, right: 0, bottom: 0 } } as Partial<MediaClip>), id, { x: 960, y: 540 }, canvas, { width: 960, height: 1080 }, 'fit');
    expect(p).not.toBeNull();
    expect(p!.x).toBeGreaterThan(0.5);
    expect(p!.x).toBeLessThanOrEqual(1);
  });
  it('accounts for a rotated clip (90 degrees swaps the axes)', () => {
    const p = canvasPointToSourcePoint(clip(), { ...id, rotation: 90 }, { x: 960 + 100, y: 540 }, canvas, { width: 1080, height: 1920 }, 'fit');
    expect(p).not.toBeNull();
    expect(Math.abs(p!.x - 0.5)).toBeLessThan(0.2);
    expect(p!.y).not.toBeCloseTo(0.5, 2);
  });
  it('is null when the tap misses the picture', () => {
    expect(canvasPointToSourcePoint(clip(), { ...id, scale: 0.5 }, { x: 10, y: 10 }, canvas, source, 'fit')).toBeNull();
  });
});

describe('default box, clamp, validate', () => {
  it('a box around a tap slides back inside the frame at an edge', () => {
    expect(defaultBoxAround({ x: 0.5, y: 0.5 })).toEqual({ x: 0.4, y: 0.4, w: 0.2, h: 0.2 });
    const b = defaultBoxAround({ x: 0.99, y: 0.01 });
    expect(b.x + b.w).toBeCloseTo(1);
    expect(b.y).toBe(0);
  });
  it('clamps and refuses a box under 1%', () => {
    const c = clampBox({ x: -0.1, y: 0.5, w: 0.3, h: 0.7 })!;
    expect([c.x, c.y]).toEqual([0, 0.5]);
    expect(c.w).toBeCloseTo(0.2);
    expect(c.h).toBeCloseTo(0.5);
    expect(clampBox({ x: 0.5, y: 0.5, w: 0.005, h: 0.3 })).toBeNull();
    expect(clampBox({ x: Number.NaN, y: 0, w: 0.5, h: 0.5 })).toBeNull();
    expect(seedFromBox('file:a', 1, { x: 0.5, y: 0.5, w: 0.005, h: 0.3 })).toBeNull();
  });
  it('a tap seed and a box seed are valid; the box centre is the point', () => {
    const tap = seedFromTap('file:a', 2.5, { x: 0.3, y: 0.4 });
    expect(isValidSeed(tap)).toBe(true);
    const drawn = seedFromBox('file:a', 2.5, { x: 0.2, y: 0.2, w: 0.2, h: 0.4 })!;
    expect(drawn.point.x).toBeCloseTo(0.3);
    expect(drawn.point.y).toBeCloseTo(0.4);
  });
  it('rejects non-finite, out-of-range and empty seeds, and gives no body for them', () => {
    const ok = seedFromTap('file:a', 1, { x: 0.5, y: 0.5 });
    expect(isValidSeed({ ...ok, atSec: Number.NaN })).toBe(false);
    expect(isValidSeed({ ...ok, point: { x: 1.2, y: 0.5 } })).toBe(false);
    expect(isValidSeed({ ...ok, mediaRef: '' })).toBe(false);
    expect(isValidSeed({ ...ok, box: { x: 0.9, y: 0.1, w: 0.3, h: 0.1 } })).toBe(false);
    expect(seedBody({ ...ok, point: { x: 2, y: 0 } })).toBeNull();
    // A tap sends the point alone; a resized box is sent.
    expect(seedBody(ok)).toEqual({ atSec: 1, point: { x: 0.5, y: 0.5 } });
    const resized = resizeSeedCorner(ok, 'se', { x: 0.8, y: 0.9 });
    expect(seedBody(resized)).toEqual({ atSec: 1, point: resized.point, box: resized.box });
    expect(seedBody(seedFromBox('file:a', 1, { x: 0.2, y: 0.2, w: 0.2, h: 0.4 })!)!.box).toBeDefined();
  });
});

describe('nudgeSeed', () => {
  it('moves the point and its box by 1% and stays inside the frame', () => {
    const s = seedFromTap('file:a', 1, { x: 0.5, y: 0.5 });
    const n = nudgeSeed(s, 1, -1);
    expect(n.point.x).toBeCloseTo(0.51);
    expect(n.point.y).toBeCloseTo(0.49);
    expect(n.box!.x).toBeCloseTo(0.41);
    expect(n.box!.w).toBe(0.2);
    const edge = nudgeSeed(seedFromTap('file:a', 1, { x: 1, y: 0.5 }), 1, 0);
    expect(edge.point.x).toBe(1);
    expect(edge.box!.x + edge.box!.w).toBeLessThanOrEqual(1);
  });
});

import { canvasToSourceMapper } from '../../follow/map';

describe('move, resize, invert', () => {
  it('moves the marker with its box and keeps the box inside', () => {
    const s = moveSeedTo(seedFromTap('file:a', 1, { x: 0.5, y: 0.5 }), { x: 0.95, y: 0.5 });
    expect(s.point.x).toBeCloseTo(0.95);
    expect(s.box!.x + s.box!.w).toBeLessThanOrEqual(1);
  });
  it('resizes from a corner and refuses a collapse', () => {
    const s = seedFromTap('file:a', 1, { x: 0.5, y: 0.5 });
    const r = resizeSeedCorner(s, 'se', { x: 0.8, y: 0.9 });
    expect(r.box).toEqual({ x: 0.4, y: 0.4, w: expect.closeTo(0.4), h: expect.closeTo(0.5) });
    expect(resizeSeedCorner(s, 'se', { x: 0.4, y: 0.9 })).toBe(s);
  });
  it('inverts the rotated mapper', () => {
    const f = canvasToSourceMapper(clip(), { ...id, rotation: 90, scale: 1.5 }, canvas, { width: 1080, height: 1920 }, 'fit')!;
    const g = invertMapper(f)!;
    const back = g(f({ x: 700, y: 400 }));
    expect(back.x).toBeCloseTo(700);
    expect(back.y).toBeCloseTo(400);
  });
});
