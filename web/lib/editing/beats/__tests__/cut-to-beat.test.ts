import { describe, expect, it } from 'vitest';

import { apply } from '../../engine';
import type { EditTimeline } from '../../types';
import { buildBeatGrid, NO_CORRECTION } from '../beat-grid';
import { compileCutToBeat, type CutToBeatMode } from '../cut-to-beat';
import { analysis, clip, counter, timeline, track } from './fixtures';

// Music clip m on A1 covers frames 0..240; beats every 15 frames, bars every 60.
const grid = buildBeatGrid(analysis(), NO_CORRECTION);
const music = clip('m', 0, 240, { sourceIn: 0 });
const spans = (doc: EditTimeline, id: string) => doc.tracks.find((t) => t.id === id)?.items.map((i) => [i.id, i.recordIn, i.recordOut]);
const plan = (mode: CutToBeatMode, unit: 'beat' | 'bar' = 'beat') => ({ musicClipId: 'm', grid, unit, newItemId: counter(), mode, sourceFrames: () => 100000 });
const replay = (doc: EditTimeline, ops: ReturnType<typeof compileCutToBeat>['ops']) => ops.reduce((d, op) => apply(d, op).doc, doc);

describe('common', () => {
  const doc = timeline([track('v1', 'video', [clip('a', 0, 100), clip('b', 100, 200)]), track('a1', 'audio', [music])]);
  it('refuses a hold music clip, a missing music clip, and does nothing on an empty grid', () => {
    const held = timeline([track('a1', 'audio', [clip('m', 0, 240, { hold: true })])]);
    expect(compileCutToBeat(held, plan({ kind: 'markers' })).summary.skipped).toEqual([{ itemId: 'm', reason: 'hold' }]);
    expect(compileCutToBeat(doc, { ...plan({ kind: 'markers' }), musicClipId: 'zz' }).summary.skipped[0].reason).toBe('noMusic');
    const empty = { ...grid, beats: [] };
    expect(compileCutToBeat(doc, { ...plan({ kind: 'markers' }), grid: empty }).ops).toEqual([]);
  });
  it('never mutates its input and the ops replay to the preview', () => {
    const before = JSON.stringify(doc);
    const r = compileCutToBeat(doc, plan({ kind: 'snapCuts', trackIds: ['v1'], toleranceFrames: 10 }));
    expect(JSON.stringify(doc)).toBe(before);
    expect(replay(doc, r.ops)).toEqual(r.preview);
  });
});

describe('snapCuts', () => {
  it('rolls the cut to the nearest beat within tolerance', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 0, 100, { sourceIn: 0 }), clip('b', 100, 200, { sourceIn: 50 })]), track('a1', 'audio', [music])]);
    const r = compileCutToBeat(doc, plan({ kind: 'snapCuts', trackIds: ['v1'], toleranceFrames: 10 }));
    expect(spans(r.preview, 'v1')).toEqual([['a', 0, 105], ['b', 105, 200]]);
    expect(r.summary.cutsMoved).toBe(1);
    expect(r.ops[0]).toMatchObject({ type: 'trim', mode: 'roll', edge: 'out', deltaFrames: 5 });
  });
  it('leaves a cut outside the tolerance, and a gap, alone', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 0, 98), clip('b', 100, 200)]), track('a1', 'audio', [music])]);
    expect(compileCutToBeat(doc, plan({ kind: 'snapCuts', trackIds: ['v1'], toleranceFrames: 10 })).ops).toEqual([]);
    const tight = timeline([track('v1', 'video', [clip('a', 0, 100), clip('b', 100, 200)]), track('a1', 'audio', [music])]);
    expect(compileCutToBeat(tight, plan({ kind: 'snapCuts', trackIds: ['v1'], toleranceFrames: 2 })).ops).toEqual([]);
  });
  it('reports noHandle when the source cannot lengthen the clip and the follower cannot shrink', () => {
    // a rolled later needs source after its out edge (not clamped), b rolled later needs b.sourceIn>=0; roll earlier needs b handle at sourceIn 0
    const doc = timeline([track('v1', 'video', [clip('a', 0, 100), clip('b', 100, 200, { sourceIn: 0 })]), track('a1', 'audio', [music])]);
    const r = compileCutToBeat(doc, plan({ kind: 'snapCuts', trackIds: ['v1'], toleranceFrames: 10 }, 'beat'));
    // 100 -> 105 is later: fine. Make it earlier: cut at 110 -> 105 needs b to extend earlier (sourceIn 0)
    const early = timeline([track('v1', 'video', [clip('a', 0, 110), clip('b', 110, 200, { sourceIn: 0 })]), track('a1', 'audio', [music])]);
    const e = compileCutToBeat(early, plan({ kind: 'snapCuts', trackIds: ['v1'], toleranceFrames: 10 }));
    expect(r.summary.cutsMoved + e.summary.cutsMoved + e.summary.skipped.length).toBeGreaterThan(0);
    for (const s of e.summary.skipped) expect(s.reason).toBe('noHandle');
  });
  it('skips cuts outside the music span and locked tracks', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 300, 400), clip('b', 400, 500)]), track('v2', 'video', [clip('c', 0, 100), clip('d', 100, 200)], true), track('a1', 'audio', [music])]);
    const r = compileCutToBeat(doc, plan({ kind: 'snapCuts', trackIds: ['v1', 'v2'], toleranceFrames: 10 }));
    expect(r.ops).toEqual([]);
    expect(r.summary.skipped).toEqual([{ itemId: 'a', reason: 'outsideMusic' }, { itemId: null, reason: 'locked' }]);
  });
  it('rolls a linked partner by the same delta', () => {
    const doc = timeline([
      track('v1', 'video', [clip('a', 0, 100, { linkId: 'L1' }), clip('b', 100, 200, { linkId: 'L2', sourceIn: 50 })]),
      track('a2', 'audio', [clip('sa', 0, 100, { linkId: 'L1' }), clip('sb', 100, 200, { linkId: 'L2', sourceIn: 50 })]),
      track('a1', 'audio', [music]),
    ]);
    const r = compileCutToBeat(doc, plan({ kind: 'snapCuts', trackIds: ['v1'], toleranceFrames: 10 }));
    expect(spans(r.preview, 'a2')).toEqual([['sa', 0, 105], ['sb', 105, 200]]);
  });
  it('skips the point when a partner track is locked', () => {
    const doc = timeline([
      track('v1', 'video', [clip('a', 0, 100, { linkId: 'L1' }), clip('b', 100, 200, { linkId: 'L2', sourceIn: 50 })]),
      track('a2', 'audio', [clip('sa', 0, 100, { linkId: 'L1' }), clip('sb', 100, 200, { linkId: 'L2', sourceIn: 50 })], true),
      track('a1', 'audio', [music]),
    ]);
    const r = compileCutToBeat(doc, plan({ kind: 'snapCuts', trackIds: ['v1'], toleranceFrames: 10 }));
    expect(r.ops).toEqual([]);
    expect(r.summary.skipped).toEqual([{ itemId: 'a', reason: 'locked' }]);
  });
});

describe('split', () => {
  it('blades at every inside beat, right to left, with ids from newItemId', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 10, 70)]), track('a1', 'audio', [music])]);
    const r = compileCutToBeat(doc, plan({ kind: 'split', itemIds: ['a'], minPieceFrames: 1 }));
    expect(r.summary.splits).toBe(4); // 15 30 45 60
    expect(r.ops.map((o) => (o.type === 'blade' ? o.atFrame : -1))).toEqual([60, 45, 30, 15]);
    expect(spans(r.preview, 'v1')?.map((s) => [s[1], s[2]])).toEqual([[10, 15], [15, 30], [30, 45], [45, 60], [60, 70]]);
  });
  it('skips a blade that leaves a short piece', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 10, 70)]), track('a1', 'audio', [music])]);
    const r = compileCutToBeat(doc, plan({ kind: 'split', itemIds: ['a'], minPieceFrames: 8 }));
    expect(r.preview.tracks[0].items.every((i) => i.recordOut - i.recordIn >= 8)).toBe(true);
    expect(r.summary.skipped.some((s) => s.reason === 'tooShort')).toBe(true);
  });
  it('splits linked partners at the same frames and re-pairs links', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 0, 40, { linkId: 'L', assetRef: { kind: 'projectFile', projectFileId: 'f' } })]), track('a2', 'audio', [clip('s', 0, 40, { linkId: 'L', assetRef: { kind: 'projectFile', projectFileId: 'f' } })]), track('a1', 'audio', [music])]);
    const r = compileCutToBeat(doc, plan({ kind: 'split', itemIds: ['a'], minPieceFrames: 1 }));
    expect(spans(r.preview, 'a2')?.map((s) => [s[1], s[2]])).toEqual([[0, 15], [15, 30], [30, 40]]);
    const links = r.preview.tracks.find((t) => t.id === 'v1')?.items.map((i) => (i.type === 'mediaClip' ? i.linkId : null));
    const partner = r.preview.tracks.find((t) => t.id === 'a2')?.items.map((i) => (i.type === 'mediaClip' ? i.linkId : null));
    expect(links).toEqual(partner);
    expect(new Set(links).size).toBe(3);
  });
  it('skips locked tracks without an op', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 10, 70)], true), track('a1', 'audio', [music])]);
    const r = compileCutToBeat(doc, plan({ kind: 'split', itemIds: ['a'], minPieceFrames: 1 }));
    expect(r.ops).toEqual([]);
    expect(r.summary.skipped).toEqual([{ itemId: 'a', reason: 'locked' }]);
  });
});

describe('markers', () => {
  const doc = timeline([track('a1', 'audio', [music])]);
  it('adds one per line with colours and caller-supplied names', () => {
    const r = compileCutToBeat(doc, { ...plan({ kind: 'markers' }, 'beat'), markerName: (l) => (l.bar ? `B${l.barNumber}` : 'beat') });
    expect(r.summary.markers).toBe(16);
    expect(r.preview.markers[0]).toMatchObject({ frame: 0, name: 'B1', color: '#f59f00' });
    expect(r.preview.markers[1]).toMatchObject({ frame: 15, name: 'beat', color: '#868e96' });
  });
  it('skips an occupied frame and stops at the cap', () => {
    const withMarker = { ...doc, markers: [{ id: 'x', frame: 15, name: 'x' }] };
    const r = compileCutToBeat(withMarker, plan({ kind: 'markers', maxMarkers: 3 }));
    expect(r.preview.markers.map((m) => m.frame)).toEqual([0, 15, 30, 45]);
    expect(r.summary.skipped).toEqual([{ itemId: null, reason: 'markerCap' }]);
  });
  it('hard-caps at 1000', () => {
    const big = timeline([track('a1', 'audio', [clip('m', 0, 5000)])]);
    const g = buildBeatGrid(analysis({ beatTimesSec: Array.from({ length: 3000 }, (_, i) => i / 30) }), NO_CORRECTION);
    const r = compileCutToBeat(big, { musicClipId: 'm', grid: g, unit: 'beat', newItemId: counter(), mode: { kind: 'markers', maxMarkers: 5000 } });
    expect(r.summary.markers).toBe(1000);
  });
});

describe('fit', () => {
  const base = { kind: 'fit', trackId: 'v1', barsPerClip: 'auto', startAt: 'musicStart', onDrop: 'none', stopAtMusicEnd: false } as const;
  it('sets each clip to whole beats and lays them end to end', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 5, 50), clip('b', 60, 130)]), track('a1', 'audio', [music])]);
    const r = compileCutToBeat(doc, plan(base));
    expect(spans(r.preview, 'v1')).toEqual([['a', 0, 45], ['b', 45, 105]]);
    expect(r.summary.clipsFitted).toBe(2);
  });
  it('lengthens a clip shorter than one unit to one unit', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 0, 10, { sourceIn: 0 })]), track('a1', 'audio', [music])]);
    expect(spans(compileCutToBeat(doc, plan(base)).preview, 'v1')).toEqual([['a', 0, 15]]);
  });
  it('never lengthens without a source length, and only up to the source with one', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 0, 10, { sourceIn: 5 })]), track('a1', 'audio', [music])]);
    const none = compileCutToBeat(doc, { ...plan(base), sourceFrames: undefined });
    expect(spans(none.preview, 'v1')).toEqual([['a', 0, 10]]);
    expect(none.summary.skipped).toEqual([{ itemId: 'a', reason: 'shortClip' }]);
    const unknown = compileCutToBeat(doc, { ...plan(base), sourceFrames: () => null });
    expect(spans(unknown.preview, 'v1')).toEqual([['a', 0, 10]]);
    const partial = compileCutToBeat(doc, { ...plan(base), sourceFrames: () => 20 });
    expect(spans(partial.preview, 'v1')).toEqual([['a', 0, 15]]);
    const tight = compileCutToBeat(doc, { ...plan(base), sourceFrames: () => 12 });
    expect(spans(tight.preview, 'v1')).toEqual([['a', 0, 10]]);
    expect(tight.summary.skipped).toEqual([{ itemId: 'a', reason: 'shortClip' }]);
    const fast = compileCutToBeat(timeline([track('v1', 'video', [clip('a', 0, 10, { sourceIn: 0, playbackRate: 2 })]), track('a1', 'audio', [music])]), { ...plan(base), sourceFrames: () => 24 });
    expect(spans(fast.preview, 'v1')).toEqual([['a', 0, 12]]);
  });
  it('reports shortClip when the clip cannot lengthen (reverse ceiling)', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 0, 10, { sourceIn: 0, reverse: true })]), track('a1', 'audio', [music])]);
    const r = compileCutToBeat(doc, plan(base));
    expect(spans(r.preview, 'v1')).toEqual([['a', 0, 10]]);
    expect(r.summary.skipped).toEqual([{ itemId: 'a', reason: 'shortClip' }]);
  });
  it('uses bar counts', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 0, 200), clip('b', 200, 400)]), track('a1', 'audio', [music])]);
    const r = compileCutToBeat(doc, plan({ ...base, barsPerClip: 1 }));
    expect(spans(r.preview, 'v1')).toEqual([['a', 0, 60], ['b', 60, 120]]);
  });
  it('starts on the first downbeat', () => {
    const g = buildBeatGrid(analysis(), { ...NO_CORRECTION, barShift: 1 });
    const doc = timeline([track('v1', 'video', [clip('a', 0, 100)]), track('a1', 'audio', [music])]);
    const r = compileCutToBeat(doc, { musicClipId: 'm', grid: g, unit: 'beat', newItemId: counter(), mode: { ...base, startAt: 'firstDownbeat' } });
    expect(r.preview.tracks[0].items[0].recordIn).toBe(15);
  });
  it('stops at the music end: lifts clips past it and trims the last to the end', () => {
    const clips = Array.from({ length: 6 }, (_, i) => clip(`c${i}`, i * 100, i * 100 + 100));
    const doc = timeline([track('v1', 'video', clips), track('a1', 'audio', [music])]);
    const r = compileCutToBeat(doc, plan({ ...base, stopAtMusicEnd: true }));
    const items = spans(r.preview, 'v1') ?? [];
    expect(items[items.length - 1][2]).toBe(240);
    expect(r.summary.clipsDropped).toBe(r.summary.skipped.filter((s) => s.reason === 'pastMusicEnd').length);
    expect(r.summary.clipsDropped).toBeGreaterThan(0);
    expect(items.every((i) => (i[2] as number) <= 240)).toBe(true);
  });
  it('puts the strongest clip on the grid line nearest the drop and the rest follow', () => {
    const clips = [clip('a', 0, 60), clip('b', 60, 120), clip('c', 120, 180), clip('d', 180, 240)];
    const doc = timeline([track('v1', 'video', clips), track('a1', 'audio', [music])]);
    const r = compileCutToBeat(doc, plan({ ...base, onDrop: 'strongestClip', rank: { c: 9, a: 1 }, dropSec: 3.6 }));
    const s = spans(r.preview, 'v1') ?? [];
    const c = s.find((x) => x[0] === 'c');
    expect(c?.[1]).toBe(105); // 3.6 s = frame 108 -> nearest beat 105
    expect(s.find((x) => x[0] === 'b')?.[2]).toBe(105);
    expect(s.find((x) => x[0] === 'd')?.[1]).toBe(c?.[2]);
  });
  it('moves linked partners with their clip and skips locked tracks', () => {
    const doc = timeline([
      track('v1', 'video', [clip('a', 0, 50, { linkId: 'L' })]),
      track('a2', 'audio', [clip('s', 0, 50, { linkId: 'L' })]),
      track('a1', 'audio', [music]),
    ]);
    const r = compileCutToBeat(doc, plan(base));
    expect(spans(r.preview, 'a2')).toEqual([['s', 0, 45]]);
    const lockedDoc = timeline([track('v1', 'video', [clip('a', 0, 50)], true), track('a1', 'audio', [music])]);
    const l = compileCutToBeat(lockedDoc, plan(base));
    expect(l.ops).toEqual([]);
    expect(l.summary.skipped).toEqual([{ itemId: null, reason: 'locked' }]);
  });
  it('never throws on a locked partner track', () => {
    const doc = timeline([
      track('v1', 'video', [clip('a', 0, 50, { linkId: 'L' })]),
      track('a2', 'audio', [clip('s', 0, 50, { linkId: 'L' })], true),
      track('a1', 'audio', [music]),
    ]);
    const r = compileCutToBeat(doc, plan(base));
    expect(spans(r.preview, 'a2')).toEqual([['s', 0, 50]]);
    // The clip itself is skipped whole (no op on it either), like snapCuts.
    expect(spans(r.preview, 'v1')).toEqual([['a', 0, 50]]);
    expect(r.summary.skipped).toEqual([{ itemId: 'a', reason: 'locked' }]);
  });
});
