import { describe, expect, it } from 'vitest';
import { assetRef, doc, media, track, TRACKS } from '../../__tests__/fixtures';
import type { EditTimeline, MediaClip } from '../../types';
import { anchorForClips, rebaseAnchor, type ResultAnchor } from '../anchor';

const KEY = 'file:00000000-0000-4000-8000-000000000001';
const one = (clips: MediaClip[]): EditTimeline => doc([track(TRACKS.v1, 'video', clips)]);
/** Launched on clip `x` (0..60 frames = source 0..2 s at 30 fps); the result covers source 0..10 s. */
const launch = (): ResultAnchor => anchorForClips(one([media('x', 0, 60, 0)]), ['x'], { startSec: 0, endSec: 10 });

describe('anchorForClips', () => {
  it('names the asset, the targets, every clip playing the asset, and the launch window', () => {
    const d = doc([track(TRACKS.v1, 'video', [media('x', 0, 60, 0), media('y', 60, 120, 60), media('o', 120, 180, 0, { assetRef: assetRef(2) })])]);
    const a = anchorForClips(d, ['x'], { startSec: 0, endSec: null });
    expect(a.assetKey).toBe(KEY);
    expect(a.clipIds).toEqual(['x']);
    expect(a.assetClipIdsAtLaunch).toEqual(['x', 'y']);
    expect(a.coverage).toEqual({ startSec: 0, endSec: null });
    expect(a.launchWindows?.x).toEqual({ startSec: 0, endSec: 2 });
  });
});

describe('rebaseAnchor', () => {
  it('unchanged: applies to the clip', () => {
    expect(rebaseAnchor(one([media('x', 0, 60, 0)]), launch())).toEqual({ kind: 'apply', clipIds: ['x'] });
  });
  it('trimmed inside the covered range: applies', () => {
    expect(rebaseAnchor(one([media('x', 0, 30, 15)]), launch())).toEqual({ kind: 'apply', clipIds: ['x'] });
  });
  it('trimmed (slipped) outside the covered range: needs review, nothing applicable', () => {
    const out = rebaseAnchor(one([media('x', 0, 60, 400)]), launch());
    expect(out).toEqual({ kind: 'needsReview', applicable: [], reasons: ['outsideCoverage'], lostCount: 1 });
  });
  it('ending exactly at the covered end applies; a few frames past it does not', () => {
    expect(rebaseAnchor(one([media('x', 0, 60, 240)]), launch()).kind).toBe('apply');
    expect(rebaseAnchor(one([media('x', 0, 60, 245)]), launch()).kind).toBe('needsReview');
  });
  it('moved on the timeline: applies (source time is what counts)', () => {
    expect(rebaseAnchor(one([media('x', 200, 260, 0)]), launch())).toEqual({ kind: 'apply', clipIds: ['x'] });
  });
  it('split in two: applies to both pieces', () => {
    const d = one([media('x', 0, 30, 0), media('y', 30, 60, 30)]);
    expect(rebaseAnchor(d, launch())).toEqual({ kind: 'apply', clipIds: ['x', 'y'] });
  });
  it('split in three: applies to every piece', () => {
    const d = one([media('x', 0, 20, 0), media('y', 20, 40, 20), media('z', 40, 60, 40)]);
    expect(rebaseAnchor(d, launch())).toEqual({ kind: 'apply', clipIds: ['x', 'y', 'z'] });
  });
  it('the original half deleted but its other piece left: applies to what is left', () => {
    const d = one([media('y', 30, 60, 30)]);
    expect(rebaseAnchor(d, launch())).toEqual({ kind: 'apply', clipIds: ['y'] });
  });
  it('deleted: needs review, targetGone, nothing applicable', () => {
    expect(rebaseAnchor(one([]), launch())).toEqual({ kind: 'needsReview', applicable: [], reasons: ['targetGone'], lostCount: 1 });
  });
  it('a clip of another video that appeared later is not a target', () => {
    const d = one([media('x', 0, 60, 0), media('n', 60, 120, 0, { assetRef: assetRef(2) })]);
    expect(rebaseAnchor(d, launch())).toEqual({ kind: 'apply', clipIds: ['x'] });
  });
  it('a new clip of the same video far away in source time is not a target', () => {
    const d = one([media('x', 0, 60, 0), media('n', 60, 120, 600)]);
    expect(rebaseAnchor(d, launch())).toEqual({ kind: 'apply', clipIds: ['x'] });
  });
  it('asset replaced: needs review, assetChanged', () => {
    const out = rebaseAnchor(one([media('x', 0, 60, 0, { assetRef: assetRef(2) })]), launch());
    expect(out).toEqual({ kind: 'needsReview', applicable: [], reasons: ['assetChanged'], lostCount: 1 });
  });
  it('nested in a compound: still found and applied', () => {
    const base = one([]);
    const nested: EditTimeline = { ...base, sequences: [{ id: 's1', name: 's', tracks: [track('seq-v', 'video', [media('x', 0, 60, 0)])] }] };
    expect(rebaseAnchor(nested, launch())).toEqual({ kind: 'apply', clipIds: ['x'] });
  });
  it('reversed: same source window, applies', () => {
    expect(rebaseAnchor(one([media('x', 0, 60, 0, { reverse: true })]), launch())).toEqual({ kind: 'apply', clipIds: ['x'] });
  });
  it('speed change: the window follows the rate', () => {
    // 60 frames at 8x = 16 s of source, past the 10 s covered.
    expect(rebaseAnchor(one([media('x', 0, 60, 0, { playbackRate: 8 })]), launch()).kind).toBe('needsReview');
    expect(rebaseAnchor(one([media('x', 0, 60, 0, { playbackRate: 2 })]), launch()).kind).toBe('apply');
  });
  it('a target that already holds the limit is not applicable', () => {
    const out = rebaseAnchor(one([media('x', 0, 60, 0)]), launch(), () => false);
    expect(out).toEqual({ kind: 'needsReview', applicable: [], reasons: ['limitReached'], lostCount: 1 });
  });
  it('part inside, part outside: applies to what is inside and says so', () => {
    const a = anchorForClips(one([media('x', 0, 60, 0), media('w', 60, 120, 0)]), ['x', 'w'], { startSec: 0, endSec: 10 });
    const out = rebaseAnchor(one([media('x', 0, 60, 0), media('w', 60, 120, 600)]), a);
    expect(out).toEqual({ kind: 'needsReview', applicable: ['x'], reasons: ['outsideCoverage'], lostCount: 1 });
  });
  it('an unknown end (null) covers the whole video', () => {
    const a = anchorForClips(one([media('x', 0, 60, 0)]), ['x'], { startSec: 0, endSec: null });
    expect(rebaseAnchor(one([media('x', 0, 60, 9000)]), a).kind).toBe('apply');
  });
});
