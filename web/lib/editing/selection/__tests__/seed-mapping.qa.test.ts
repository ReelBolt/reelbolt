/**
 * QA (click-to-select): exact values for the canvas -> source mapping a tap goes through. The base suite checks crop and
 * rotation only by inequalities; these pin the numbers for crop, rotation, crop + rotation, non-uniform scale and the
 * "a transformed clip is fitted, whatever the policy" rule, plus a round trip through invertMapper for the drawn marker.
 */
import { describe, expect, it } from 'vitest';
import type { MediaClip } from '../../types';
import { canvasToSourceMapper } from '../../follow/map';
import { canvasPointToSourcePoint, invertMapper } from '../seed';

const clip = (over: Partial<MediaClip> = {}): MediaClip =>
  ({ id: 'c1', type: 'mediaClip', recordIn: 0, recordOut: 300, sourceIn: 0, playbackRate: 1, assetRef: { kind: 'projectFile', projectFileId: 'f1' }, ...over }) as unknown as MediaClip;
const id = { positionX: 0, positionY: 0, scale: 1, rotation: 0 };
const canvas = { width: 1920, height: 1080 };
const wide = { width: 1920, height: 1080 };

describe('tap mapping, exact values', () => {
  it('a left crop of half: the visible half fills the frame and its centre is source x 0.75', () => {
    const c = clip({ crop: { left: 0.5, top: 0, right: 0, bottom: 0 } } as Partial<MediaClip>);
    expect(canvasPointToSourcePoint(c, id, { x: 960, y: 540 }, canvas, wide, 'fit')!.x).toBeCloseTo(0.75, 6);
    expect(canvasPointToSourcePoint(c, id, { x: 480, y: 540 }, canvas, wide, 'fit')!.x).toBeCloseTo(0.5, 6);
    expect(canvasPointToSourcePoint(c, id, { x: 1439, y: 540 }, canvas, wide, 'fit')!.x).toBeCloseTo(1 - 0.5 / 960, 3);
  });

  // QA finding (red on 4276e432a): the miss check is 0..1 of the whole source, not the crop window, so a tap on the empty
  // canvas left of a cropped picture becomes a seed in the cropped-away part (x 0.458) the user cannot see.
  it('a tap outside a cropped picture is not a seed (it would land in the cropped-away part)', () => {
    const c = clip({ crop: { left: 0.5, top: 0, right: 0, bottom: 0 } } as Partial<MediaClip>);
    expect(canvasPointToSourcePoint(c, id, { x: 400, y: 540 }, canvas, wide, 'fit')).toBeNull();
  });

  it('90 degrees clockwise: right of centre on the canvas is the upper half of the source', () => {
    const p = canvasPointToSourcePoint(clip(), { ...id, rotation: 90 }, { x: 1060, y: 540 }, canvas, wide, 'fit')!;
    expect(p.x).toBeCloseTo(0.5, 6);
    expect(p.y).toBeCloseTo(0.5 - 100 / 1080, 6);
  });

  it('crop and rotation together', () => {
    const c = clip({ crop: { left: 0, top: 0.5, right: 0, bottom: 0 } } as Partial<MediaClip>);
    // Cropped picture is 1920x540 around the canvas centre; rotated 90, a point 100 px below centre is 100 px to the
    // picture's right in its own axes (clockwise: the source's left edge points up): source x = 0.5 + 100/1920, y stays at
    // the crop's middle (0.75).
    const p = canvasPointToSourcePoint(c, { ...id, rotation: 90 }, { x: 960, y: 640 }, canvas, wide, 'fit')!;
    expect(p.x).toBeCloseTo(0.5 + 100 / 1920, 6);
    expect(p.y).toBeCloseTo(0.75, 6);
  });

  it('a non-uniform scaleY stretches only the vertical axis', () => {
    const p = canvasPointToSourcePoint(clip(), { ...id, scaleY: 2 }, { x: 1440, y: 810 }, canvas, wide, 'fit')!;
    expect(p.x).toBeCloseTo(0.75, 6);
    expect(p.y).toBeCloseTo(0.5 + 270 / 2160, 6);
  });

  it('fill policy crops a portrait source on an untransformed clip; a transformed clip is fitted instead', () => {
    const portrait = { width: 1080, height: 1920 };
    const top = { x: 960, y: 0 };
    const filled = canvasPointToSourcePoint(clip(), id, top, canvas, portrait, 'fill')!;
    // Filled: the picture is 1920 x 3413.3 px, so the canvas top edge is 540/3413.3 above the source centre.
    expect(filled.y).toBeCloseTo(0.5 - 540 / (1920 * (1920 / 1080)), 6);
    const transformed = clip({ transform: { scale: { value: 1 } } } as unknown as Partial<MediaClip>);
    expect(canvasPointToSourcePoint(transformed, id, top, canvas, portrait, 'fill')!.y).toBeCloseTo(0, 6);
  });

  it('the marker is drawn where the tap was: invert(map(p)) == p with crop, rotation, scale and shift', () => {
    const c = clip({ crop: { left: 0.1, top: 0.2, right: 0.05, bottom: 0 } } as Partial<MediaClip>);
    const values = { positionX: 120, positionY: -80, scale: 1.3, scaleY: 0.9, rotation: 33 };
    const f = canvasToSourceMapper(c, values, canvas, wide, 'fit')!;
    const g = invertMapper(f)!;
    for (const p of [{ x: 700, y: 400 }, { x: 1100, y: 620 }, { x: 960, y: 540 }]) {
      const back = g(f(p));
      expect(back.x).toBeCloseTo(p.x, 6);
      expect(back.y).toBeCloseTo(p.y, 6);
    }
  });
});
