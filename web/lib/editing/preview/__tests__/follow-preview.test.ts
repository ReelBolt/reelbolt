import { describe, expect, it } from 'vitest';
import { buildFollowTransform, timelineFrameForSource } from '../../follow/map';
import { activeLayersAt, layerTransformAt } from '../scheduler';
import type { MediaClip } from '../../types';
import { doc, media, track, TRACKS } from '../../__tests__/fixtures';

/**
 * Follow subject -> the drawn frame. Trace (no defect found): FollowSection applies `setProperty transform` to the store
 * (positionX/positionY/scale linear keyframes on TIMELINE frames, canvas fractions and a uniform scale), the viewer's
 * `[doc]` effect calls `session.invalidate()`, the session reads `getDoc()` on every present, `layerTransformAt` samples the
 * keys at the timeline frame, and the compositor puts x * 2 / -y * 2 into clip space. These tests pin the units and the
 * source-to-timeline mapping the apply step relies on, so a regression in any link shows up here.
 */
const rate = { num: 30, den: 1 };
const clipAt = (over: Partial<MediaClip> = {}) => ({ ...media('c1', 100, 160), sourceIn: 30, ...over }) as unknown as MediaClip;

describe('follow keys land on the timeline frames the viewer samples', () => {
  const keys = [
    { sourceSec: 1, positionX: 0.2, positionY: -0.1, scale: 1 },
    { sourceSec: 2, positionX: -0.2, positionY: 0.3, scale: 2 },
  ];

  it('maps source seconds through sourceIn (a clip that starts 1 s into its source)', () => {
    expect(timelineFrameForSource(clipAt(), 1, rate)).toBe(100); // source frame 30 = the clip's first frame
    expect(timelineFrameForSource(clipAt(), 2, rate)).toBe(130);
    expect(timelineFrameForSource(clipAt({ playbackRate: 2 }), 2, rate)).toBe(115);
  });

  it('the applied transform is what layerTransformAt returns frame by frame (position in canvas fractions, uniform scale)', () => {
    const clip = clipAt();
    const built = buildFollowTransform(clip, keys, rate);
    expect(built.keyCount).toBe(2);
    const applied = { ...clip, transform: built.transform } as MediaClip;
    const at = (frame: number) => layerTransformAt(applied, frame);
    expect(at(100)).toMatchObject({ x: 0.2, y: -0.1, scale: 1 });
    expect(at(115).x).toBeCloseTo(0, 9);
    expect(at(115).y).toBeCloseTo(0.1, 9);
    expect(at(115).scale).toBeCloseTo(1.5, 9);
    expect(at(130)).toMatchObject({ x: -0.2, y: 0.3, scale: 2 });
    expect(at(159).x).toBe(-0.2); // after the last key the last value holds
  });

  it('the document the session reads on its next present carries the keys (activeLayersAt hands out the edited clip)', () => {
    const clip = clipAt();
    const built = buildFollowTransform(clip, keys, rate);
    const edited = { ...clip, transform: built.transform } as MediaClip;
    const d = doc([track(TRACKS.v1, 'video', [edited])]);
    const layers = activeLayersAt(d, 115);
    expect(layers).toHaveLength(1);
    expect(layerTransformAt(layers[0].item as MediaClip, 115).scale).toBeCloseTo(1.5, 9);
  });

  it('keeps the clip\'s other transform channels and drops keys outside the clip', () => {
    const clip = clipAt({ transform: { rotation: { value: 12 } } } as Partial<MediaClip>);
    const built = buildFollowTransform(clip, [...keys, { sourceSec: 9, positionX: 1, positionY: 1, scale: 3 }], rate);
    expect(built.transform?.rotation).toEqual({ value: 12 });
    expect(built.transform?.positionX?.keys).toHaveLength(2);
  });
});
