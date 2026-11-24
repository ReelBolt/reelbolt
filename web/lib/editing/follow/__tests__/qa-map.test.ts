/**
 * QA (follow-subject, PR #956): edge cases of the source-time -> timeline-frame mapping and of the box drawn on
 * the viewer, checked against the preview's own evaluation (scheduler.ts) and fit (gl-compositor.ts fitFor).
 */
import { describe, expect, it } from 'vitest';
import type { MediaClip } from '../../types';
import { buildFollowTransform, canvasDragToSourceBox } from '../map';
import { evaluateKeyframed } from '../../preview/scheduler';

const rate = { num: 30, den: 1 };
// recordIn 100 .. recordOut 400 (10 s), sourceIn 60 (2 s): the clip shows source 2 s .. 12 s.
const clip = (over: Partial<MediaClip> = {}): MediaClip =>
  ({ id: 'c1', type: 'mediaClip', recordIn: 100, recordOut: 400, sourceIn: 60, playbackRate: 1, assetRef: { kind: 'projectFile', projectFileId: 'f1' }, ...over }) as unknown as MediaClip;
const key = (sourceSec: number, x: number) => ({ sourceSec, positionX: x, positionY: 0, scale: 2 });

describe('QA: the server path covers [fromSec, toSec]; its end key lands on recordOut', () => {
  it('a two-key linear pan (start and end of the used range) still moves the picture forward', () => {
    // The server's range is sourceIn/fps .. (sourceIn + len*speed)/fps = 2 .. 12; decimation keeps both ends.
    const r = buildFollowTransform(clip(), [key(2, 0), key(12, 1)], rate);
    const px = r.transform?.positionX ?? null;
    // At the last frame shown (399) the server's path is at ~0.9967.
    expect(evaluateKeyframed(px, 399, 0)).toBeCloseTo(0.9967, 2);
  });

  it('a two-key linear pan still moves the picture on a reversed clip', () => {
    // Reverse: source 12 s is shown at frame 100, source 2 s at frame 400 (dropped).
    const r = buildFollowTransform(clip({ reverse: true }), [key(2, 0), key(12, 1)], rate);
    const px = r.transform?.positionX ?? null;
    expect(evaluateKeyframed(px, 399, 0)).toBeCloseTo(0.0033, 2);
  });
});

describe('QA: the drawn box uses the fit the preview draws', () => {
  it('a transformed clip on a fill timeline maps the box through the contain fit (preview fitFor uses fit once transformed)', () => {
    const transformed = clip({ transform: { scale: { value: 1.5 } } as unknown as MediaClip['transform'] });
    const values = { positionX: 0, positionY: 0, scale: 1.5, rotation: 0 };
    const canvas = { width: 1080, height: 1920 };
    const source = { width: 1920, height: 1080 };
    const a = { x: 440, y: 900 };
    const b = { x: 640, y: 1020 };
    const asPreviewDraws = canvasDragToSourceBox(transformed, values, a, b, canvas, source, 'fit');
    const asSectionMaps = canvasDragToSourceBox(transformed, values, a, b, canvas, source, 'fill');
    expect(asSectionMaps).toEqual(asPreviewDraws);
  });
});
