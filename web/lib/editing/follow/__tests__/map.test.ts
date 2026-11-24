import { describe, expect, it } from 'vitest';
import type { MediaClip } from '../../types';
import { buildFollowTransform, canvasDragToSourceBox, isFollowStale, lostSpansInClip, snapshotClip, timelineFrameForSource } from '../map';
import { sourcePositionFor } from '../../preview/scheduler';

const rate = { num: 30, den: 1 };
const canvas = { width: 1920, height: 1080 };
const clip = (over: Partial<MediaClip> = {}): MediaClip =>
  ({ id: 'c1', type: 'mediaClip', recordIn: 100, recordOut: 400, sourceIn: 60, playbackRate: 1, assetRef: { kind: 'projectFile', projectFileId: 'f1' }, ...over }) as unknown as MediaClip;
const key = (sourceSec: number, x = 0.1, y = -0.1, s = 2) => ({ sourceSec, positionX: x, positionY: y, scale: s });

describe('timelineFrameForSource', () => {
  it.each([
    ['speed 1', {}],
    ['speed 2', { playbackRate: 2 }],
    ['speed 0.5', { playbackRate: 0.5 }],
    ['reverse', { reverse: true }],
    ['reverse at 2x', { reverse: true, playbackRate: 2 }],
  ] as const)('inverts sourcePositionFor at %s', (_n, over) => {
    const c = clip(over);
    for (const frame of [100, 150, 250, 399]) {
      const sec = sourcePositionFor(c, frame) / 30;
      expect(timelineFrameForSource(c, sec, rate)).toBeCloseTo(frame, 6);
    }
  });
  it('is null for a held clip', () => expect(timelineFrameForSource(clip({ hold: true }), 3, rate)).toBeNull());
});

describe('buildFollowTransform', () => {
  it('maps source seconds to integer timeline frames with linear keys and the first key as base', () => {
    const r = buildFollowTransform(clip(), [key(2, 0.1), key(3, 0.2), key(4, 0.3)], rate);
    // source frame 60 is the in-point: 2 s = frame 60 -> timeline 100.
    expect(r.keyCount).toBe(3);
    const px = r.transform!.positionX!;
    expect(px.keys!.map((k) => k.frame)).toEqual([100, 130, 160]);
    expect(px.keys!.every((k) => k.interp === 'linear' && Number.isInteger(k.frame))).toBe(true);
    expect(px.value).toBe(0.1);
    expect(r.transform!.scale!.keys![2].value).toBe(2);
  });
  it('speed 2 halves the timeline distance, speed 0.5 doubles it', () => {
    expect(buildFollowTransform(clip({ playbackRate: 2 }), [key(2), key(4)], rate).transform!.positionX!.keys!.map((k) => k.frame)).toEqual([100, 130]);
    expect(buildFollowTransform(clip({ playbackRate: 0.5, recordOut: 600 }), [key(2), key(3)], rate).transform!.positionX!.keys!.map((k) => k.frame)).toEqual([100, 160]);
  });
  it('reverse counts back from the clip end', () => {
    const r = buildFollowTransform(clip({ reverse: true }), [key(3), key(2)], rate);
    // offset 30 frames from the in-point is 30 frames before recordOut (400) -> 370; offset 0 -> 400, the end key lands on the last frame (399).
    expect(r.transform!.positionX!.keys!.map((k) => k.frame)).toEqual([370, 399]);
  });
  it('drops keys outside the clip and keeps the last of keys that share a frame', () => {
    const r = buildFollowTransform(clip(), [key(0.5, 0), key(2, 0.1), key(2.01, 0.9), key(60, 0.5)], rate);
    expect(r.transform!.positionX!.keys!).toEqual([{ frame: 100, value: 0.9, interp: 'linear' }]);
  });
  it('keeps rotation and opacity, and reports nothing when no key lands in the clip', () => {
    const c = clip({ transform: { rotation: { value: 5 }, opacity: { value: 0.5 } } });
    const r = buildFollowTransform(c, [key(2)], rate);
    expect(r.transform!.rotation).toEqual({ value: 5 });
    expect(r.transform!.opacity).toEqual({ value: 0.5 });
    expect(buildFollowTransform(c, [key(100)], rate)).toEqual({ transform: null, keyCount: 0 });
  });
});

describe('isFollowStale', () => {
  const snap = snapshotClip(clip(), canvas);
  const assumed = { canvasWidth: 1920, canvasHeight: 1080, sourceWidth: 1920, sourceHeight: 1080 };
  it('allows trimming and moving the clip in time', () => {
    expect(isFollowStale(snap, clip({ recordIn: 130, recordOut: 300, sourceIn: 90 }), canvas, assumed)).toBe(false);
  });
  it.each([
    ['another source', { assetRef: { kind: 'projectFile', projectFileId: 'f2' } }],
    ['a crop', { crop: { left: 0.1 } }],
    ['a reframe', { reframe: { x: 0.5, y: 0.5 } }],
    ['stabilize', { stabilize: true }],
    ['a speed change', { playbackRate: 2 }],
    ['reverse', { reverse: true }],
    ['hold', { hold: true }],
  ])('is stale after %s', (_n, over) => {
    expect(isFollowStale(snap, clip(over as Partial<MediaClip>), canvas, assumed)).toBe(true);
  });
  it('is stale when the canvas differs from the snapshot or from what the server assumed', () => {
    expect(isFollowStale(snap, clip(), { width: 1080, height: 1920 }, assumed)).toBe(true);
    expect(isFollowStale(snap, clip(), canvas, { ...assumed, canvasWidth: 1280 })).toBe(true);
  });
});

describe('lostSpansInClip', () => {
  it('maps spans to clip frames and clips them to the clip', () => {
    expect(lostSpansInClip(clip(), [{ fromSec: 3, toSec: 4 }, { fromSec: 100, toSec: 110 }], rate)).toEqual([{ fromFrame: 130, toFrame: 160 }]);
  });
});

describe('canvasDragToSourceBox', () => {
  const id = { positionX: 0, positionY: 0, scale: 1, rotation: 0 };
  const source = { width: 1920, height: 1080 };
  it('a drag on an untransformed picture is the same box in source fractions', () => {
    const b = canvasDragToSourceBox(clip(), id, { x: 480, y: 270 }, { x: 960, y: 540 }, canvas, source, 'fit')!;
    expect(b.x).toBeCloseTo(0.25);
    expect(b.y).toBeCloseTo(0.25);
    expect(b.w).toBeCloseTo(0.25);
    expect(b.h).toBeCloseTo(0.25);
  });
  it('accounts for scale and for a click-sized drag', () => {
    const b = canvasDragToSourceBox(clip(), { ...id, scale: 2 }, { x: 960, y: 540 }, { x: 1440, y: 810 }, canvas, source, 'fit')!;
    expect(b.x).toBeCloseTo(0.5);
    expect(b.w).toBeCloseTo(0.125);
    expect(canvasDragToSourceBox(clip(), id, { x: 10, y: 10 }, { x: 11, y: 11 }, canvas, source, 'fit')).toBeNull();
  });
});
