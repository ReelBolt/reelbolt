import { describe, expect, it } from 'vitest';
import { motionRankForClips, sourceFramesFromProbe } from '../feeds';
import { clip, timeline, track } from './fixtures';

describe('sourceFramesFromProbe', () => {
  it('turns a probed duration into source frames at the document rate', () => {
    const frames = sourceFramesFromProbe({ 'file-a': 12.5 }, 30);
    expect(frames(clip('a', 0, 100))).toBe(375);
  });

  it('is null for an unknown file, an unprobed duration, or an unusable rate', () => {
    expect(sourceFramesFromProbe({ 'file-a': 12.5 }, 30)(clip('b', 0, 100))).toBeNull();
    expect(sourceFramesFromProbe({ 'file-a': null }, 30)(clip('a', 0, 100))).toBeNull();
    expect(sourceFramesFromProbe({ 'file-a': 0 }, 30)(clip('a', 0, 100))).toBeNull();
    expect(sourceFramesFromProbe({ 'file-a': 12.5 }, 0)(clip('a', 0, 100))).toBeNull();
  });

  it('is null for a clip that is not a project file (a step-result render)', () => {
    const step = clip('s', 0, 100, { assetRef: { kind: 'stepResult', stepResultId: 'sr-1' } });
    expect(sourceFramesFromProbe({ 'file-s': 10 }, 30)(step)).toBeNull();
  });
});

describe('motionRankForClips', () => {
  // 30 fps: a 0..100-frame clip shows source seconds 0..3.333; b starts at source frame 60 (2 s) and
  // runs 100 frames (to 5.333 s); c is twice as fast, so its 30 record frames cover 2 s of source.
  const doc = timeline([
    track('v1', 'video', [clip('a', 0, 100), clip('b', 100, 200, { sourceIn: 60 }), clip('c', 200, 230, { playbackRate: 2 })]),
    track('v2', 'video', [clip('d', 0, 100, { assetRef: { kind: 'projectFile', projectFileId: 'file-z' } })]),
  ]);
  const motion = {
    'file-a': [
      { startSec: 0, endSec: 2, mean: 0.1, motionClass: 'low' },
      { startSec: 2, endSec: 4, mean: 0.3, motionClass: 'high' },
    ],
    'file-b': [
      { startSec: 0, endSec: 3, mean: 0.5, motionClass: 'high' },
      { startSec: 3, endSec: 7, mean: 0.9, motionClass: 'high' },
    ],
    // file-c has no motion analysis at all.
  };

  it('weights the windows by the overlap with each clip source window', () => {
    const rank = motionRankForClips(doc, 'v1', motion);
    // a: 0..3.333 -> (2*0.1 + 1.333*0.3) / 3.333
    expect(rank.a).toBeCloseTo((2 * 0.1 + (4 / 3) * 0.3) / (10 / 3), 6);
    // b: 2..5.333 -> (1*0.5 + 2.333*0.9) / 3.333
    expect(rank.b).toBeCloseTo((1 * 0.5 + (7 / 3) * 0.9) / (10 / 3), 6);
  });

  it('covers the whole source window of a retimed clip, and skips files with no motion and unknown tracks', () => {
    const withC = { ...motion, 'file-c': [{ startSec: 0, endSec: 2, mean: 0.4, motionClass: 'high' as const }] };
    const rank = motionRankForClips(doc, 'v1', withC);
    expect(rank.c).toBeCloseTo(0.4, 6);
    expect(rank).not.toHaveProperty('d');
    expect(motionRankForClips(doc, 'nope', withC)).toEqual({});
  });

  it('omits a clip whose source window no window overlaps', () => {
    const far = { 'file-a': [{ startSec: 90, endSec: 95, mean: 0.7, motionClass: 'high' as const }] };
    expect(motionRankForClips(doc, 'v1', far)).toEqual({});
  });
});
