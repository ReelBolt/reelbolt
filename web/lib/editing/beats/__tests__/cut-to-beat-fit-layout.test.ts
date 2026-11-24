import { describe, expect, it } from 'vitest';
import { apply } from '../../engine';
import type { EditTimeline, MediaClip } from '../../types';
import { buildBeatGrid, NO_CORRECTION } from '../beat-grid';
import { compileCutToBeat, type CutToBeatMode, type CutToBeatPlan } from '../cut-to-beat';
import { analysis, clip, counter, timeline, track } from './fixtures';

const grid = buildBeatGrid(analysis({ beatTimesSec: Array.from({ length: 81 }, (_, i) => i * 0.5), durationSec: 40, analysedSec: 40 }), NO_CORRECTION);
const music = clip('m', 0, 1200);
const replay = (doc: EditTimeline, ops: ReturnType<typeof compileCutToBeat>['ops']) => ops.reduce((d, op) => apply(d, op).doc, doc);
const mk = (mode: CutToBeatMode, extra: Partial<CutToBeatPlan> = {}): CutToBeatPlan => ({ musicClipId: 'm', grid, unit: 'bar', newItemId: counter('q'), sourceFrames: () => 100000, mode, ...extra });
const fit = (extra: Partial<Extract<CutToBeatMode, { kind: 'fit' }>> = {}): CutToBeatMode => ({ kind: 'fit', trackId: 'v1', barsPerClip: 'auto', startAt: 'musicStart', onDrop: 'none', stopAtMusicEnd: true, ...extra });
function run(doc: EditTimeline, p: CutToBeatPlan) {
  const before = JSON.stringify(doc);
  const r = compileCutToBeat(doc, p);
  expect(JSON.stringify(doc)).toBe(before);
  expect(replay(doc, r.ops)).toEqual(r.preview);
  for (const t of r.preview.tracks) {
    const items = [...t.items].sort((a, b) => a.recordIn - b.recordIn);
    for (let i = 0; i < items.length; i++) {
      expect(items[i].recordOut).toBeGreaterThan(items[i].recordIn);
      if (i > 0) expect(items[i].recordIn).toBeGreaterThanOrEqual(items[i - 1].recordOut);
    }
  }
  return r;
}
const all = (d: EditTimeline) => d.tracks.flatMap((t) => t.items.map((i) => ({ ...(i as MediaClip), trackId: t.id })));
// every pair of items sharing a linkId whose spans matched in the input must still match.
function syncedPairs(input: EditTimeline, out: EditTimeline) {
  const inn = all(input);
  for (const a of inn) for (const b of inn) {
    if (a.trackId >= b.trackId || !a.linkId || a.linkId !== b.linkId) continue;
    const oa = all(out).find((x) => x.id === a.id), ob = all(out).find((x) => x.id === b.id);
    if (!oa || !ob) { expect(!!oa).toBe(!!ob); continue; }
    if (a.recordIn === b.recordIn && a.recordOut === b.recordOut) expect([oa.id, oa.recordIn, oa.recordOut]).toEqual([oa.id, ob.recordIn, ob.recordOut]);
    else expect([oa.id, oa.recordIn - a.recordIn]).toEqual([oa.id, ob.recordIn - b.recordIn]); // unequal span: same shift
  }
}

describe('fit: linked A/V stays in sync', () => {
  it('original HIGH: video [0,60],[60,120] vs audio [0,60],[60,160]', () => {
    const doc = timeline([
      track('v1', 'video', [clip('a', 0, 60, { linkId: 'L1' }), clip('b', 60, 120, { linkId: 'L2' })]),
      track('a1', 'audio', [clip('aa', 0, 60, { linkId: 'L1' }), clip('ba', 60, 160, { linkId: 'L2' })]),
      track('a2', 'audio', [music]),
    ]);
    for (const bpc of ['auto', 1, 2] as const) {
      const r = run(doc, mk(fit({ barsPerClip: bpc })));
      syncedPairs(doc, r.preview);
    }
  });
  it('many linked clips of uneven length, bars 1/2/auto, with drop', () => {
    const lens = [37, 91, 45, 130, 22, 77];
    let t = 0; const v: MediaClip[] = []; const a: MediaClip[] = [];
    lens.forEach((L, k) => { v.push(clip('v' + k, t, t + L, { linkId: 'K' + k, sourceIn: 10 })); a.push(clip('s' + k, t, t + L, { linkId: 'K' + k, sourceIn: 10 })); t += L; });
    const doc = timeline([track('v1', 'video', v), track('a1', 'audio', a), track('a2', 'audio', [music])]);
    for (const bpc of ['auto', 1, 2] as const) for (const rank of [undefined, { v0: 9 }, { v3: 9 }] as (Record<string, number> | undefined)[]) {
      const r = run(doc, mk(fit({ barsPerClip: bpc, ...(rank ? { onDrop: 'strongestClip' as const, rank, dropSec: 12 } : {}) })));
      syncedPairs(doc, r.preview);
    }
  });
  it('locked partner on the MIDDLE clip only: that clip and its locked partner stay in sync', () => {
    const doc = timeline([
      track('v1', 'video', [clip('a', 0, 100, { linkId: 'L1' }), clip('b', 100, 200, { linkId: 'L2' }), clip('c', 200, 300, { linkId: 'L3' })]),
      track('a1', 'audio', [clip('aa', 0, 100, { linkId: 'L1' }), clip('ca', 200, 300, { linkId: 'L3' })]),
      track('a3', 'audio', [clip('bb', 100, 200, { linkId: 'L2' })], true),
      track('a2', 'audio', [music]),
    ]);
    for (const bpc of ['auto', 1, 2] as const) {
      const r = run(doc, mk(fit({ barsPerClip: bpc })));
      expect(r.summary.skipped.some((s) => s.itemId === 'b' && s.reason === 'locked')).toBe(true);
      const at = (id: string) => all(r.preview).find((x) => x.id === id);
      expect([at('b')?.recordIn, at('b')?.recordOut, at('bb')?.recordIn, at('bb')?.recordOut]).toEqual([100, 200, 100, 200]);
      expect(at('c')).toBeDefined();
      expect(at('c')!.recordOut).toBeLessThanOrEqual(1200);
      expect(r.summary.skipped.map((k) => k.reason)).not.toContain('invalid');
      syncedPairs(doc, r.preview);
    }
  });
});

describe('fit: strongest clip first, several clips before the drop', () => {
  const drop0 = (lens: number[], dropSec: number, src = 100000) => {
    let t = 0; const v = lens.map((L, k) => { const c = clip('c' + k, t, t + L); t += L; return c; });
    const doc = timeline([track('v1', 'video', v), track('a2', 'audio', [music])]);
    const r = run(doc, mk(fit({ onDrop: 'strongestClip', rank: { c0: 9 }, dropSec, barsPerClip: 'auto' }), { unit: 'beat', sourceFrames: () => src }));
    const s = r.preview.tracks[0].items.map((i) => [i.id, i.recordIn, i.recordOut]);
    return { r, s };
  };
  it('three short clips fill before the drop; strongest starts exactly on the drop', () => {
    const { s, r } = drop0([60, 40, 40, 40], 4);
    expect(s.find(([id]) => id === 'c0')![1]).toBe(120);
    expect(s.map(([id]) => id)).toEqual(['c1', 'c2', 'c3', 'c0']);
    expect(r.summary.skipped.map((k) => k.reason)).not.toContain('invalid');
  });
  it('drop on the very first frame: strongest at the start, nothing reported missing', () => {
    const { s, r } = drop0([60, 40, 40], 0);
    expect(s.find(([id]) => id === 'c0')![1]).toBe(0);
    expect(r.summary.skipped.map((k) => k.reason)).not.toContain('gapBeforeDrop');
  });
  it('drop late with one long pre clip that can stretch', () => {
    const { s } = drop0([60, 50, 30], 10);
    expect(s.find(([id]) => id === 'c0')![1]).toBe(300);
  });
  it('linked partners follow the reorder', () => {
    const doc = timeline([
      track('v1', 'video', [clip('a', 0, 60, { linkId: 'L1' }), clip('b', 60, 100, { linkId: 'L2' }), clip('c', 100, 140, { linkId: 'L3' })]),
      track('a1', 'audio', [clip('aa', 0, 60, { linkId: 'L1' }), clip('ba', 60, 100, { linkId: 'L2' }), clip('ca', 100, 140, { linkId: 'L3' })]),
      track('a2', 'audio', [music]),
    ]);
    const r = run(doc, mk(fit({ onDrop: 'strongestClip', rank: { a: 9 }, dropSec: 4 }), { unit: 'beat' }));
    expect(r.preview.tracks[0].items.find((i) => i.id === 'a')!.recordIn).toBe(120);
    expect(r.summary.skipped.map((k) => k.reason)).not.toContain('invalid');
    syncedPairs(doc, r.preview);
  });
});

describe('snapCuts: source bound', () => {
  it('rate 2 and sourceIn: never past the source end', () => {
    const doc = timeline([track('v1', 'video', [clip('a', 0, 40, { sourceIn: 20, playbackRate: 2 }), clip('b', 40, 200, { sourceIn: 50 })]), track('a2', 'audio', [music])]);
    const r = run(doc, mk({ kind: 'snapCuts', trackIds: ['v1'], toleranceFrames: 10 }, { unit: 'beat', sourceFrames: () => 100 }));
    const a = r.preview.tracks[0].items[0] as MediaClip;
    expect(20 + (a.recordOut - a.recordIn) * 2).toBeLessThanOrEqual(100);
  });
  it('linked partner rolled by the clamped delta', () => {
    const doc = timeline([
      track('v1', 'video', [clip('a', 0, 40, { linkId: 'L1' }), clip('b', 40, 200, { sourceIn: 50, linkId: 'L2' })]),
      track('a1', 'audio', [clip('aa', 0, 40, { linkId: 'L1' }), clip('ba', 40, 200, { sourceIn: 50, linkId: 'L2' })]),
      track('a2', 'audio', [music]),
    ]);
    const r = run(doc, mk({ kind: 'snapCuts', trackIds: ['v1'], toleranceFrames: 10 }, { unit: 'beat', sourceFrames: () => 43 }));
    expect((r.preview.tracks[0].items[0] as MediaClip).recordOut).toBeLessThanOrEqual(43);
    syncedPairs(doc, r.preview);
  });
});

// Small deterministic PRNG so failures reproduce.
function rng(seed: number) {
  let x = seed >>> 0 || 1;
  return () => {
    x ^= x << 13; x >>>= 0; x ^= x >>> 17; x ^= x << 5; x >>>= 0;
    return x / 4294967296;
  };
}

describe('fit: property, random clip sets', () => {
  it('no overlaps, nothing past the music end, linked items in sync, locked pairs untouched', () => {
    for (let seed = 1; seed <= 150; seed++) {
      const r0 = rng(seed);
      const n = 2 + Math.floor(r0() * 6);
      const v: MediaClip[] = [];
      const a: MediaClip[] = [];
      const locked: MediaClip[] = [];
      let t = Math.floor(r0() * 30);
      for (let k = 0; k < n; k++) {
        const L = 10 + Math.floor(r0() * 140);
        const linked = r0() < 0.7;
        const lock = linked && r0() < 0.2;
        v.push(clip('v' + k, t, t + L, linked ? { linkId: 'K' + k } : {}));
        if (linked) (lock ? locked : a).push(clip('s' + k, t, t + L, { linkId: 'K' + k }));
        t += L + (r0() < 0.3 ? Math.floor(r0() * 40) : 0);
      }
      const doc = timeline([track('v1', 'video', v), track('a1', 'audio', a), track('a3', 'audio', locked, true), track('a2', 'audio', [music])]);
      const bpc = (['auto', 1, 2] as const)[Math.floor(r0() * 3)];
      const withDrop = r0() < 0.5;
      const rank = { ['v' + Math.floor(r0() * n)]: 9 };
      const r = run(doc, mk(fit({ barsPerClip: bpc, ...(withDrop ? { onDrop: 'strongestClip' as const, rank, dropSec: Math.floor(r0() * 30) } : {}) }), { unit: r0() < 0.5 ? 'beat' : 'bar', sourceFrames: () => 400 }));
      for (const it of r.preview.tracks[0].items) expect(it.recordOut).toBeLessThanOrEqual(1200);
      expect(r.summary.skipped.map((k) => k.reason)).not.toContain('invalid');
      syncedPairs(doc, r.preview);
      for (const lc of locked) {
        const id = 'v' + lc.id.slice(1);
        const vin = v.find((c) => c.id === id)!;
        const out = all(r.preview).find((x) => x.id === id);
        expect([seed, out?.recordIn, out?.recordOut]).toEqual([seed, vin.recordIn, vin.recordOut]);
      }
    }
  });
});
