/** @vitest-environment node */
import { describe, expect, it } from 'vitest';
import fc from 'fast-check';
import {
  normalizeSelection,
  selectAll,
  selectAllOnTrack,
  selectBackwardFromPlayhead,
  selectForwardFromPlayhead,
  selectItem,
  selectMarquee,
  selectRange,
  selectionEquals,
  toggleItem,
} from '../selection';
import { snapCandidates, snapFrame, snapSourceAt, snapThresholdFrames } from '../snapping';
import { standardDoc, TRACKS } from '../../__tests__/fixtures';

const d = standardDoc();

describe('selection reducers', () => {
  it('return unique ids in a stable document order', () => {
    expect(selectItem(d, 'c2')).toEqual(['c2']);
    expect(toggleItem(d, ['c2'], 'c1')).toEqual(['c1', 'c2']);
    expect(toggleItem(d, ['c1', 'c2'], 'c2')).toEqual(['c1']);
    expect(normalizeSelection(d, ['a1', 'c3', 'c3', 'ghost', 't1'])).toEqual(['c3', 't1', 'a1']);
  });

  it('shift-range and marquee covering the same items compare equal', () => {
    const range = selectRange(d, 'c1', 't1');
    const marquee = selectMarquee(d, { trackLo: 0, trackHi: 1, frameLo: 0, frameHi: 90 });
    expect(selectionEquals(range, marquee)).toBe(true);
    expect(range).toEqual(['c1', 'c2', 't1']);
  });

  it('select forward/backward from the playhead and all on track', () => {
    expect(selectForwardFromPlayhead(d, 100, [TRACKS.v1])).toEqual(['c2', 'c3']);
    expect(selectBackwardFromPlayhead(d, 100, [TRACKS.v1])).toEqual(['c1', 'c2']);
    expect(selectAllOnTrack(d, TRACKS.v1)).toEqual(['c1', 'c2', 'c3']);
    expect(selectAll(d)).toHaveLength(6);
    expect(selectRange(d, null, 'c3')).toEqual(['c3']);
    expect(selectRange(d, 'c1', 'ghost')).toEqual([]);
  });

  it('every reducer result is duplicate-free and sorted in document order (property)', () => {
    const ids = d.tracks.flatMap((t) => t.items.map((i) => i.id));
    fc.assert(
      fc.property(fc.array(fc.constantFrom(...ids, 'ghost'), { maxLength: 12 }), (chosen) => {
        const result = normalizeSelection(d, chosen);
        expect(new Set(result).size).toBe(result.length);
        expect(result).toEqual(normalizeSelection(d, [...result].reverse()));
      }),
    );
  });
});

describe('snapping', () => {
  it('collects edits, markers, playhead, in/out and zero as candidates', () => {
    const c = snapCandidates(d, { playheadFrame: 77, inFrame: 10, outFrame: 200 });
    expect(c).toEqual([0, 10, 30, 45, 60, 77, 90, 120, 150, 200, 240]);
    const excluding = snapCandidates(d, { playheadFrame: 77, inFrame: null, outFrame: null, excludeItemIds: ['c1'] });
    // c1's edges are 0 and 60; 60 survives because c2 starts there, 0 survives because zero is
    // always a candidate — so excluding c1 removes nothing here, and a lone clip's edges would go.
    expect(excluding).toContain(60);
    expect(excluding).toContain(0);
    const lone = snapCandidates(d, { playheadFrame: 0, inFrame: null, outFrame: null, excludeItemIds: ['c3', 'a1'] }); // a1 also ends at 240
    expect(lone).not.toContain(240);
  });

  it('snaps to the nearest candidate within the threshold, ties to the earlier frame, identity when disabled', () => {
    const c = [0, 30, 60];
    expect(snapFrame(32, c, 4, true)).toEqual({ frame: 30, snapped: true, candidate: 30 });
    expect(snapFrame(45, c, 20, true)).toEqual({ frame: 30, snapped: true, candidate: 30 });
    expect(snapFrame(40, c, 4, true)).toEqual({ frame: 40, snapped: false, candidate: null });
    expect(snapFrame(32, c, 4, false)).toEqual({ frame: 32, snapped: false, candidate: null });
    expect(snapFrame(30, c, 4, true)).toEqual({ frame: 30, snapped: false, candidate: 30 });
  });

  it('threshold is ~8 px in frames, at least one frame', () => {
    expect(snapThresholdFrames(2)).toBe(4);
    expect(snapThresholdFrames(64)).toBe(1);
    expect(snapThresholdFrames(0.1)).toBe(80);
    expect(snapThresholdFrames(0)).toBe(1);
  });
});

describe('snap source', () => {
  it('names what a snap caught, playhead first, and null where nothing is', () => {
    const ctx = { playheadFrame: 77, inFrame: 10, outFrame: 200 };
    expect(snapSourceAt(d, ctx, 77)).toEqual({ kind: 'playhead' });
    expect(snapSourceAt(d, ctx, 10)).toEqual({ kind: 'in' });
    expect(snapSourceAt(d, ctx, 200)).toEqual({ kind: 'out' });
    expect(snapSourceAt(d, ctx, 120)?.kind).toBe('clipEdge');
    expect(snapSourceAt(d, ctx, 0)).not.toBeNull();
    expect(snapSourceAt(d, ctx, 12345)).toBeNull();
    const marker = d.markers[0];
    if (marker) expect(snapSourceAt(d, { ...ctx, playheadFrame: -1 }, marker.frame)).toMatchObject({ kind: 'marker', name: marker.name });
  });
});
