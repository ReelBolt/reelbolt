import { describe, expect, it } from 'vitest';

import { apply } from '../../engine';
import type { EditTimeline, MediaClip } from '../../types';
import { buildBeatGrid, gridLines, NO_CORRECTION, type GridLine } from '../beat-grid';
import { compileCutToBeat, type CutToBeatMode, type CutToBeatPlan } from '../cut-to-beat';
import { beatSecondsToRecordFrames, recordFrameToSourceSec, sourceSecToRecordFrame } from '../map-to-record';
import { analysis, clip, counter, timeline, track } from './fixtures';

const L = (sec: number, bar = false, barNumber = 1): GridLine => ({ sec, bar, barNumber });
const frames = (r: ReturnType<typeof beatSecondsToRecordFrames>) => (Array.isArray(r) ? r.map((x) => x.frame) : r);

describe('QA map-to-record', () => {
  it('sourceIn shifts the mapping; beats before sourceIn and at/after recordOut are dropped', () => {
    const c = clip('m', 100, 200, { sourceIn: 30 });
    expect(frames(beatSecondsToRecordFrames(c, [L(0.5), L(1), L(2), L(4.3), L(4.4)], 30))).toEqual([100, 130, 199]);
  });
  it('playbackRate 0.5 / 1 / 2', () => {
    for (const [rate, want] of [[0.5, 60], [1, 30], [2, 15]] as const) {
      expect(frames(beatSecondsToRecordFrames(clip('m', 0, 300, { playbackRate: rate }), [L(1)], 30))).toEqual([want]);
    }
  });
  it('reverse maps from the source end and stays sorted', () => {
    const c = clip('m', 0, 100, { reverse: true });
    expect(frames(beatSecondsToRecordFrames(c, [L(0.5), L(1), L(2)], 30))).toEqual([40, 70, 85]);
  });
  it('hold is refused', () => {
    expect(beatSecondsToRecordFrames(clip('m', 0, 100, { hold: true }), [L(1)], 30)).toEqual({ refused: 'hold' });
  });
  it('rounds half up (7.5 -> 8, rate 2: 1.5 -> 2), dedups, prefers the bar line on a tie', () => {
    expect(sourceSecToRecordFrame(clip('m', 0, 100), 0.25, 30)).toBe(8);
    expect(sourceSecToRecordFrame(clip('m', 0, 100, { playbackRate: 2 }), 0.1, 30)).toBe(2);
    const r = beatSecondsToRecordFrames(clip('m', 0, 100, { playbackRate: 2 }), [L(1), L(1.01, true, 3)], 30);
    expect(r).toEqual([{ frame: 15, bar: true, barNumber: 3 }]);
  });
  it('NTSC 30000/1001 fps maps beats to the nearest frame', () => {
    const fps = 30000 / 1001;
    expect(sourceSecToRecordFrame(clip('m', 0, 10000), 10, fps)).toBe(300); // 299.7
  });
  it('round trip frame -> sec -> frame for sourceIn, rate 0.5/1/2, reverse', () => {
    for (const rate of [0.5, 1, 2]) {
      for (const reverse of [false, true]) {
        const c = clip('m', 37, 437, { sourceIn: 23, playbackRate: rate, reverse });
        for (let f = 37; f < 437; f++) expect(sourceSecToRecordFrame(c, recordFrameToSourceSec(c, f, 30), 30)).toBe(f);
      }
    }
  });
});

// ---------------------------------------------------------------------------------------------
const grid = buildBeatGrid(analysis(), NO_CORRECTION); // beats every 15 f, bars every 60 f @30
const music = clip('m', 0, 240);
const replay = (doc: EditTimeline, ops: ReturnType<typeof compileCutToBeat>['ops']) => ops.reduce((d, op) => apply(d, op).doc, doc);
const spans = (doc: EditTimeline, id: string) => doc.tracks.find((t) => t.id === id)?.items.map((i) => [i.id, i.recordIn, i.recordOut]);
const mk = (mode: CutToBeatMode, extra: Partial<CutToBeatPlan> = {}): CutToBeatPlan => ({ musicClipId: 'm', grid, unit: 'beat', newItemId: counter('q'), mode, ...extra });

function invariants(doc: EditTimeline, p: CutToBeatPlan) {
  const before = JSON.stringify(doc);
  const r = compileCutToBeat(doc, p);
  expect(JSON.stringify(doc)).toBe(before);
  expect(replay(doc, r.ops)).toEqual(r.preview);
  for (const t of r.preview.tracks) for (const i of t.items) expect(i.recordOut - i.recordIn).toBeGreaterThan(0);
  return r;
}

const linkedDoc = (lockA = false) => timeline([
  track('v1', 'video', [clip('a', 0, 100, { linkId: 'L1' }), clip('b', 100, 200, { sourceIn: 50, linkId: 'L2' }), clip('c', 200, 290, { sourceIn: 50, linkId: 'L3' })]),
  track('a1', 'audio', [clip('aa', 0, 100, { linkId: 'L1' }), clip('ba', 100, 200, { sourceIn: 50, linkId: 'L2' }), clip('ca', 200, 290, { sourceIn: 50, linkId: 'L3' })], lockA),
  track('a2', 'audio', [music]),
]);
const inSync = (d: EditTimeline) => {
  const v = spans(d, 'v1')!.map(([, i, o]) => [i, o]);
  const a = spans(d, 'a1')!.map(([, i, o]) => [i, o]);
  expect(a).toEqual(v);
};

describe('QA compileCutToBeat invariants per mode', () => {
  it('snapCuts: valid, not mutated, linked in sync', () => {
    const r = invariants(linkedDoc(), mk({ kind: 'snapCuts', trackIds: ['v1'], toleranceFrames: 10 }));
    expect(r.summary.cutsMoved).toBeGreaterThan(0);
    inSync(r.preview);
  });
  it('snapCuts never makes a zero-length clip with a huge tolerance', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 0, 101), clip('b', 101, 103, { sourceIn: 50 }), clip('c', 103, 200, { sourceIn: 50 })]), track('a2', 'audio', [music])]);
    invariants(doc, mk({ kind: 'snapCuts', trackIds: ['v1'], toleranceFrames: 100 }));
  });
  it('split: valid, linked in sync', () => {
    const r = invariants(linkedDoc(), mk({ kind: 'split', itemIds: ['a', 'b'], minPieceFrames: 1 }));
    expect(r.summary.splits).toBeGreaterThan(0);
    inSync(r.preview);
  });
  it('markers: valid', () => {
    const r = invariants(linkedDoc(), mk({ kind: 'markers', maxMarkers: 5 }));
    expect(r.summary.markers).toBe(5);
    expect(r.summary.skipped.map((s) => s.reason)).toContain('markerCap');
  });
  it('fit: valid, linked in sync', () => {
    const r = invariants(linkedDoc(), mk({ kind: 'fit', trackId: 'v1', barsPerClip: 'auto', startAt: 'musicStart', onDrop: 'none', stopAtMusicEnd: true }, { unit: 'bar', sourceFrames: () => 100000 }));
    inSync(r.preview);
  });
  it('locked tracks never throw and are reported, every mode', () => {
    const locked = (doc: EditTimeline) => ({ ...doc, tracks: doc.tracks.map((t) => (t.id === 'v1' ? { ...t, locked: true } : t)) });
    const doc = locked(linkedDoc());
    const modes: CutToBeatMode[] = [
      { kind: 'snapCuts', trackIds: ['v1'], toleranceFrames: 10 },
      { kind: 'split', itemIds: ['a'], minPieceFrames: 1 },
      { kind: 'fit', trackId: 'v1', barsPerClip: 'auto', startAt: 'musicStart', onDrop: 'none', stopAtMusicEnd: true },
    ];
    for (const m of modes) {
      const r = invariants(doc, mk(m));
      expect(r.ops.filter((o) => 'itemId' in o && ['a', 'b', 'c'].includes((o as { itemId: string }).itemId))).toEqual([]);
      expect(r.summary.skipped.map((s) => s.reason)).toContain('locked');
    }
  });
  it('snapCuts with a locked partner track skips the edit point (stays in sync)', () => {
    const r = invariants(linkedDoc(true), mk({ kind: 'snapCuts', trackIds: ['v1'], toleranceFrames: 10 }));
    inSync(r.preview);
  });
  it('PROBE fit with a locked linked-partner track keeps A/V in sync', () => {
    const r = invariants(linkedDoc(true), mk({ kind: 'fit', trackId: 'v1', barsPerClip: 'auto', startAt: 'musicStart', onDrop: 'none', stopAtMusicEnd: true }, { unit: 'bar', sourceFrames: () => 100000 }));
    inSync(r.preview);
  });
});

describe('QA fit', () => {
  const fit = (extra: Partial<Extract<CutToBeatMode, { kind: 'fit' }>> = {}): CutToBeatMode => ({ kind: 'fit', trackId: 'v1', barsPerClip: 'auto', startAt: 'musicStart', onDrop: 'none', stopAtMusicEnd: true, ...extra });
  const SRC = 55;
  const withinSource = (d: EditTimeline) => {
    for (const i of d.tracks.find((t) => t.id === 'v1')!.items) {
      const c = i as MediaClip;
      expect((c.sourceIn ?? 0) + (c.recordOut - c.recordIn) * (c.playbackRate ?? 1)).toBeLessThanOrEqual(SRC);
    }
  };
  it('never lengthens past the source: auto, bars 4, rate 2', () => {
    for (const rate of [1, 2]) {
      const doc = timeline([track('v1', 'video', [clip('a', 0, Math.floor(50 / rate), { playbackRate: rate }), clip('b', 30, 40, { sourceIn: 20, playbackRate: rate })]), track('a2', 'audio', [music])]);
      for (const bpc of ['auto', 1, 4] as const) {
        const r = invariants(doc, mk(fit({ barsPerClip: bpc }), { unit: 'bar', sourceFrames: () => SRC }));
        withinSource(r.preview);
        expect(r.summary.skipped.map((s) => s.reason)).toContain('shortClip');
      }
    }
  });
  it('never lengthens past the source when forced to reach the drop', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 0, 50), clip('b', 50, 100)]), track('a2', 'audio', [music])]);
    const r = invariants(doc, mk(fit({ onDrop: 'strongestClip', rank: { a: 0, b: 1 }, dropSec: 6 }), { unit: 'beat', sourceFrames: () => SRC }));
    withinSource(r.preview);
    expect(spans(r.preview, 'v1')).toContainEqual(['b', 180, expect.any(Number)]);
  });
  it('no source length known -> never lengthened', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 0, 50)]), track('a2', 'audio', [music])]);
    const r = invariants(doc, mk(fit({ barsPerClip: 4 }), { unit: 'bar' }));
    expect(spans(r.preview, 'v1')).toEqual([['a', 0, 50]]);
  });
  it('clips past the music end are removed, last kept ends on the music end', () => {
    const doc = timeline([track('v1', 'video', Array.from({ length: 6 }, (_, k) => clip(`c${k}`, k * 100, k * 100 + 100))), track('a2', 'audio', [music])]);
    const r = invariants(doc, mk(fit(), { unit: 'bar', sourceFrames: () => 100000 }));
    const s = spans(r.preview, 'v1')!;
    expect(s.every(([, , o]) => (o as number) <= 240)).toBe(true);
    expect(s[s.length - 1][2]).toBe(240);
    expect(r.summary.clipsDropped).toBe(2);
    expect(r.summary.skipped.filter((k) => k.reason === 'pastMusicEnd')).toHaveLength(2);
  });
  it('drop placement: strongest (index 2) starts on the grid line nearest the drop', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 0, 100), clip('b', 100, 200), clip('c', 200, 300)]), track('a2', 'audio', [music])]);
    const r = invariants(doc, mk(fit({ onDrop: 'strongestClip', rank: { a: 1, b: 2, c: 9 }, dropSec: 4.1 }), { unit: 'bar', sourceFrames: () => 100000 }));
    expect(spans(r.preview, 'v1')!.find(([id]) => id === 'c')![1]).toBe(120);
  });
  it('PROBE drop placement when the strongest clip is the FIRST clip', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 0, 100), clip('b', 100, 200)]), track('a2', 'audio', [music])]);
    const r = invariants(doc, mk(fit({ onDrop: 'strongestClip', rank: { a: 9, b: 1 }, dropSec: 4 }), { unit: 'bar', sourceFrames: () => 100000 }));
    const a = spans(r.preview, 'v1')!.find(([id]) => id === 'a');
    // spec: the strongest clip starts exactly on the grid line nearest the drop (frame 120)
    expect(a?.[1]).toBe(120);
  });
});

describe('QA snapCuts source end', () => {
  it('PROBE snapCuts never rolls a clip past its source end (sourceFrames known = 100)', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 0, 100), clip('b', 100, 200, { sourceIn: 50 })]), track('a2', 'audio', [music])]);
    const r = invariants(doc, mk({ kind: 'snapCuts', trackIds: ['v1'], toleranceFrames: 10 }, { sourceFrames: () => 100 }));
    expect(spans(r.preview, 'v1')![0][2]).toBeLessThanOrEqual(100);
  });
});

describe('QA grid sanity', () => {
  it('gridLines bar units start on downbeats', () => {
    const g = buildBeatGrid(analysis({ firstDownbeatIndex: 1 }), NO_CORRECTION);
    expect(gridLines(g, 'bar')[0]).toMatchObject({ sec: 0.5, bar: true });
  });
});

describe('T-014 fit: linked partners on the evolving document', () => {
  const fitMode = (extra: Partial<Extract<CutToBeatMode, { kind: 'fit' }>> = {}): CutToBeatMode => ({ kind: 'fit', trackId: 'v1', barsPerClip: 'auto', startAt: 'musicStart', onDrop: 'none', stopAtMusicEnd: true, ...extra });
  it('keeps picture and sound in sync over several linked clips (partners found after each ripple)', () => {
    const doc = timeline([
      track('v1', 'video', [clip('a', 0, 60, { linkId: 'L1' }), clip('b', 60, 120, { linkId: 'L2' }), clip('c', 120, 180, { linkId: 'L3' })]),
      track('a1', 'audio', [clip('aa', 0, 60, { linkId: 'L1' }), clip('ba', 60, 120, { linkId: 'L2' }), clip('ca', 120, 180, { linkId: 'L3' })]),
      track('a2', 'audio', [music]),
    ]);
    const r = invariants(doc, mk(fitMode({ barsPerClip: 2 }), { unit: 'bar', sourceFrames: () => 100000 }));
    expect(spans(r.preview, 'v1')!.map(([, i, o]) => [i, o])).toEqual(spans(r.preview, 'a1')!.map(([, i, o]) => [i, o]));
    expect(spans(r.preview, 'v1')![1][1]).toBe(120);
  });
  it('a locked partner track skips the clips entirely and reports locked', () => {
    const r = invariants(linkedDoc(true), mk(fitMode(), { unit: 'bar', sourceFrames: () => 100000 }));
    expect(r.ops.filter((o) => 'itemId' in o && ['a', 'b', 'c'].includes((o as { itemId: string }).itemId))).toEqual([]);
    expect(r.summary.skipped.filter((k) => k.reason === 'locked').map((k) => k.itemId)).toEqual(['a', 'b', 'c']);
  });
  it('drop at index 0: other clips first, the strongest lands on the drop, the gap is reported when it cannot be reached', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 0, 100), clip('b', 100, 200)]), track('a2', 'audio', [music])]);
    const none = invariants(doc, mk(fitMode({ onDrop: 'strongestClip', rank: { a: 9, b: 1 }, dropSec: 4 }), { unit: 'bar', sourceFrames: () => 100 }));
    expect(spans(none.preview, 'v1')!.find(([id]) => id === 'a')![1]).toBe(120);
    expect(none.summary.skipped.map((k) => k.reason)).toContain('gapBeforeDrop');
  });
});
