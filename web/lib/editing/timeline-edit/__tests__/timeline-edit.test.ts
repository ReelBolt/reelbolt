import { beforeEach, describe, expect, it, vi } from 'vitest';

const who = vi.hoisted(() => ({ email: 'a@x.com' as string | null }));
vi.mock('../../../hooks/use-auth', () => ({ getUserFromCookie: () => (who.email ? { email: who.email } : null) }));
import { applyAll, type EditOp } from '../../engine';
import { doc, media, track } from '../../__tests__/fixtures';
import type { EditTimeline } from '../../types';
import {
  buildCloseAllGapsOps,
  buildDeleteGapOps,
  buildDeleteOps,
  buildDuplicateOps,
  buildFitToFillOps,
  buildJoinOps,
  buildLiftOps,
  buildMoveToTrackOps,
  buildNumericTrimOps,
  buildPasteOps,
  buildToggleEnabledOps,
  clearClipClipboardForTests,
  copyClips,
  gapAt,
  gapStillExists,
  listGaps,
  parseClipboard,
  readClipClipboard,
  selectFromPlayhead,
  serializeClipboard,
  writeClipClipboard,
} from '..';

let counter = 0;
const newId = () => `00000000-0000-4000-8000-${String(++counter).padStart(12, '0')}`;
const run = (d: EditTimeline, ops: readonly EditOp[]) => applyAll(d, ops);
const spans = (d: EditTimeline, trackId: string) => d.tracks.find((t) => t.id === trackId)!.items.map((i) => [i.id, i.recordIn, i.recordOut]);

const base = () =>
  doc([
    track('v1', 'video', [media('a', 0, 30), media('b', 30, 60), media('c', 60, 90)]),
    track('v2', 'video', []),
    track('a1', 'audio', [media('s', 0, 30, 5)]),
  ]);

beforeEach(() => {
  counter = 0;
  who.email = 'a@x.com';
  clearClipClipboardForTests();
});

describe('gaps as objects', () => {
  const gappy = () => doc([track('v1', 'video', [media('a', 10, 20), media('b', 30, 40), media('c', 40, 50)])]);
  it('lists the leading gap and interior gaps, not the empty tail', () => {
    expect(listGaps(gappy())).toEqual([
      { trackId: 'v1', start: 0, end: 10 },
      { trackId: 'v1', start: 20, end: 30 },
    ]);
  });
  it('finds a gap by frame and notices when it is gone', () => {
    const d = gappy();
    const gap = gapAt(d, 'v1', 25)!;
    expect(gap).toEqual({ trackId: 'v1', start: 20, end: 30 });
    expect(gapAt(d, 'v1', 15)).toBeNull();
    expect(gapStillExists(d, gap)).toBe(true);
    expect(gapStillExists(run(d, buildDeleteGapOps(gap)), gap)).toBe(false);
  });
  it('Delete on a gap shifts later clips left by the gap length', () => {
    const d = gappy();
    const after = run(d, buildDeleteGapOps({ trackId: 'v1', start: 20, end: 30 }));
    expect(spans(after, 'v1')).toEqual([['a', 10, 20], ['b', 20, 30], ['c', 30, 40]]);
  });
  it('Close all gaps removes interior gaps and keeps the first clip where it is; none = no ops', () => {
    const d = gappy();
    const after = run(d, buildCloseAllGapsOps(d, 'v1'));
    expect(spans(after, 'v1')).toEqual([['a', 10, 20], ['b', 20, 30], ['c', 30, 40]]);
    expect(buildCloseAllGapsOps(after, 'v1')).toEqual([]);
  });
});

describe('closing a gap keeps linked pictures and sounds in step', () => {
  const linked = (id: string, from: number, to: number, link: string) => ({ ...media(id, from, to), linkId: link });
  const pair = () =>
    doc([
      track('v1', 'video', [linked('pa', 0, 30, 'la'), linked('pc', 50, 80, 'lc')]),
      track('a1', 'audio', [linked('sa', 0, 30, 'la'), linked('sc', 50, 80, 'lc')]),
    ]);
  const lockedA1 = { isLocked: (id: string) => id === 'a1' };

  it('Delete on the picture gap closes the same gap on the camera sound, in one batch', () => {
    const d = pair();
    const ops = buildDeleteGapOps({ trackId: 'v1', start: 30, end: 50 }, d);
    expect(ops.map((o) => (o.type === 'deleteGap' ? o.trackId : o.type))).toEqual(['v1', 'a1']);
    const after = run(d, ops);
    expect(spans(after, 'v1')).toEqual([['pa', 0, 30], ['pc', 30, 60]]);
    expect(spans(after, 'a1')).toEqual([['sa', 0, 30], ['sc', 30, 60]]);
  });

  it('leaves a partner on a locked track alone, and an unlinked track alone', () => {
    const d = pair();
    expect(buildDeleteGapOps({ trackId: 'v1', start: 30, end: 50 }, d, lockedA1)).toHaveLength(1);
    expect(buildDeleteGapOps({ trackId: 'v1', start: 30, end: 50 })).toHaveLength(1);
    const plain = doc([track('v1', 'video', [media('a', 0, 30), media('c', 50, 80)]), track('a1', 'audio', [media('s', 0, 30), media('t', 50, 80)])]);
    expect(buildDeleteGapOps({ trackId: 'v1', start: 30, end: 50 }, plain)).toHaveLength(1);
  });

  it('Close all gaps closes every gap of the pair; with no partner it stays the single closeGaps op', () => {
    const d = doc([
      track('v1', 'video', [linked('pa', 0, 30, 'la'), linked('pb', 40, 70, 'lb'), linked('pc', 90, 120, 'lc')]),
      track('a1', 'audio', [linked('sa', 0, 30, 'la'), linked('sb', 40, 70, 'lb'), linked('sc', 90, 120, 'lc')]),
    ]);
    const after = run(d, buildCloseAllGapsOps(d, 'v1'));
    expect(spans(after, 'v1')).toEqual([['pa', 0, 30], ['pb', 30, 60], ['pc', 60, 90]]);
    expect(spans(after, 'a1')).toEqual([['sa', 0, 30], ['sb', 30, 60], ['sc', 60, 90]]);
    const solo = doc([track('v1', 'video', [media('a', 0, 30), media('b', 40, 70)])]);
    expect(buildCloseAllGapsOps(solo, 'v1')).toEqual([{ type: 'closeGaps', trackId: 'v1' }]);
  });
});

describe('delete and lift', () => {
  it('Delete ripples: following clips shift left and no gap is left', () => {
    const d = base();
    const plan = buildDeleteOps(d, ['b'], () => false);
    expect(spans(run(d, plan.ops), 'v1')).toEqual([['a', 0, 30], ['c', 30, 60]]);
  });
  it('Shift+Delete lifts: a gap stays', () => {
    const d = base();
    expect(spans(run(d, buildLiftOps(d, ['b'])), 'v1')).toEqual([['a', 0, 30], ['c', 60, 90]]);
  });
  it('ignores ids that are not in the document', () => {
    expect(buildLiftOps(base(), ['nope'])).toEqual([]);
  });
});

describe('copy, paste, duplicate', () => {
  it('round-trips through JSON and refuses other text', () => {
    const payload = copyClips(base(), ['a'])!;
    expect(parseClipboard(serializeClipboard(payload))).toEqual(payload);
    expect(parseClipboard('hello')).toBeNull();
    expect(parseClipboard('{"app":"other"}')).toBeNull();
    expect(parseClipboard(null)).toBeNull();
  });
  it('the internal clipboard returns what was copied', () => {
    const payload = copyClips(base(), ['a'])!;
    writeClipClipboard(payload);
    expect(readClipClipboard()).toEqual(payload);
  });
  it('pastes onto another track at the playhead with fresh ids', () => {
    const d = base();
    const payload = copyClips(d, ['b'])!;
    const r = buildPasteOps(d, payload, { trackId: 'v2', atFrame: 100 }, newId);
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    const after = run(d, r.ops);
    expect(spans(after, 'v2').map(([, i, o]) => [i, o])).toEqual([[100, 130]]);
    expect(r.newItemIds[0]).not.toBe('b');
    expect(after.tracks[0].items).toHaveLength(3);
  });
  it('keeps relative offsets between several clips and across tracks', () => {
    const d = base();
    const payload = copyClips(d, ['a', 'c', 's'])!;
    const target = doc([track('v1', 'video', []), track('v2', 'video', []), track('a1', 'audio', []), track('a2', 'audio', [])]);
    // s sits two tracks below a/c in the source, so it lands two tracks below the paste track.
    const r = buildPasteOps(target, payload, { trackId: 'v1', atFrame: 200 }, newId);
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    const after = run(target, r.ops);
    expect(spans(after, 'v1').map(([, i, o]) => [i, o])).toEqual([[200, 230], [260, 290]]);
    expect(spans(after, 'a1').map(([, i, o]) => [i, o])).toEqual([[200, 230]]);
  });
  it('multi-clip paste lands each clip at its offset', () => {
    const d = base();
    const payload = copyClips(d, ['a', 'c'])!;
    const target = doc([track('x', 'video', [])]);
    const r = buildPasteOps(target, payload, { trackId: 'x', atFrame: 10 }, newId);
    expect(r.ok).toBe(true);
    if (r.ok) expect(spans(run(target, r.ops), 'x').map(([, i, o]) => [i, o])).toEqual([[10, 40], [70, 100]]);
  });
  it('refuses when the span is taken, the track kind differs or the track is locked, with a sentence', () => {
    const d = base();
    const payload = copyClips(d, ['a'])!;
    const busy = buildPasteOps(d, payload, { trackId: 'v1', atFrame: 10 }, newId);
    expect(busy.ok).toBe(false);
    if (!busy.ok) expect(busy.reason).toMatch(/no room/i);
    const wrong = buildPasteOps(d, payload, { trackId: 'a1', atFrame: 500 }, newId);
    expect(wrong.ok).toBe(false);
    const locked = buildPasteOps(d, payload, { trackId: 'v2', atFrame: 0 }, newId, (id) => id === 'v2');
    expect(locked.ok).toBe(false);
  });
  it('remaps link ids consistently', () => {
    const linked = (id: string, a: number, b: number, link: string) => ({ ...media(id, a, b), linkId: link }) as never;
    const d = doc([track('v1', 'video', [linked('p', 0, 30, 'L1')]), track('a1', 'audio', [linked('q', 0, 30, 'L1')]), track('v2', 'video', []), track('a2', 'audio', [])]);
    const payload = copyClips(d, ['p', 'q'])!;
    const pair = doc([track('v2', 'video', []), track('a2', 'audio', [])]);
    const ok = buildPasteOps(pair, payload, { trackId: 'v2', atFrame: 5 }, newId);
    expect(ok.ok).toBe(true);
    if (!ok.ok) return;
    const ids = ok.ops.map((o) => (o as unknown as { item: { linkId: string } }).item.linkId);
    expect(new Set(ids).size).toBe(1);
    expect(ids[0]).not.toBe('L1');
  });
  it('Duplicate inserts right after the clip and pushes the followers, in one program', () => {
    const d = base();
    const r = buildDuplicateOps(d, ['b'], newId);
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    expect(spans(run(d, r.ops), 'v1').map(([, i, o]) => [i, o])).toEqual([[0, 30], [30, 60], [60, 90], [90, 120]]);
  });
  it('Duplicate of two clips keeps them together', () => {
    const d = base();
    const r = buildDuplicateOps(d, ['a', 'b'], newId);
    expect(r.ok).toBe(true);
    if (r.ok) expect(spans(run(d, r.ops), 'v1').map(([, i, o]) => [i, o])).toEqual([[0, 30], [30, 60], [60, 90], [90, 120], [120, 150]]);
  });
});

describe('move to track, join, fit to fill, disable, select', () => {
  it('moves to the next track of the same kind at the same time', () => {
    const d = base();
    const r = buildMoveToTrackOps(d, ['b'], 1, () => false);
    expect(r.ok).toBe(true);
    if (r.ok) expect(spans(run(d, r.ops), 'v2')).toEqual([['b', 30, 60]]);
  });
  it('says so at the top and the bottom, and when the spot is taken', () => {
    const d = base();
    const up = buildMoveToTrackOps(d, ['a'], -1);
    expect(up.ok === false && up.reason).toMatch(/no track above/);
    const down = buildMoveToTrackOps(d, ['s'], 1);
    expect(down.ok === false && down.reason).toMatch(/no track below/);
    const taken = doc([track('v1', 'video', [media('a', 0, 30)]), track('v2', 'video', [media('z', 10, 20)])]);
    expect(buildMoveToTrackOps(taken, ['a'], 1).ok).toBe(false);
  });
  it('join refuses clips that were not split from each other', () => {
    expect(buildJoinOps(base(), 'a').ok).toBe(false);
    const split = run(doc([track('v1', 'video', [media('a', 0, 60)])]), [{ type: 'blade', itemId: 'a', atFrame: 30, newItemId: newId() }]);
    const r = buildJoinOps(split, 'a');
    expect(r.ok).toBe(true);
    if (r.ok) expect(spans(run(split, r.ops), 'v1')).toEqual([['a', 0, 60]]);
  });
  it('fit to fill stretches to the next clip, refuses with no gap', () => {
    const d = doc([track('v1', 'video', [media('a', 0, 30), media('b', 60, 90)])]);
    const r = buildFitToFillOps(d, 'a');
    expect(r.ok).toBe(true);
    if (r.ok) expect(spans(run(d, r.ops), 'v1')[0]).toEqual(['a', 0, 60]);
    expect(buildFitToFillOps(base(), 'a').ok).toBe(false);
  });
  it('disable then enable', () => {
    const d = base();
    const off = buildToggleEnabledOps(d, ['a']);
    expect(off.ok && off.enabling).toBe(false);
    if (!off.ok) return;
    const disabled = run(d, off.ops);
    const on = buildToggleEnabledOps(disabled, ['a']);
    expect(on.ok && on.enabling).toBe(true);
  });
  it('selects forward and backward from the playhead on one track or all', () => {
    const d = base();
    expect(selectFromPlayhead(d, 45, 'forward', 'v1')).toEqual(['b', 'c']);
    expect(selectFromPlayhead(d, 45, 'backward', 'v1')).toEqual(['a', 'b']);
    expect(selectFromPlayhead(d, 20, 'forward', null).sort()).toEqual(['a', 'b', 'c', 's']);
  });
});

describe('numeric trim', () => {
  const one = () => doc([track('v1', 'video', [media('a', 0, 60, 10), media('b', 60, 90)])]);
  it('sets the source in-point exactly (ripple head trim keeps the start)', () => {
    const d = one();
    const r = buildNumericTrimOps(d, 'a', 'sourceIn', 20, 1000);
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    const a = run(d, r.ops).tracks[0].items[0] as unknown as { sourceIn: number; recordIn: number; recordOut: number };
    expect([a.sourceIn, a.recordIn, a.recordOut]).toEqual([20, 0, 50]);
  });
  it('sets duration / out and ripples the followers', () => {
    const d = one();
    const r = buildNumericTrimOps(d, 'a', 'recordOut', 40, 1000);
    expect(r.ok).toBe(true);
    if (r.ok) expect(spans(run(d, r.ops), 'v1')).toEqual([['a', 0, 40], ['b', 40, 70]]);
    const dur = buildNumericTrimOps(d, 'a', 'duration', 80, 1000);
    expect(dur.ok).toBe(true);
    if (dur.ok) expect(spans(run(d, dur.ops), 'v1')).toEqual([['a', 0, 80], ['b', 80, 110]]);
  });
  it('refuses past the source length with a plain sentence', () => {
    const d = one();
    const r = buildNumericTrimOps(d, 'a', 'duration', 500, 100);
    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.reason).toMatch(/at most 90 frames/);
    const i = buildNumericTrimOps(d, 'a', 'sourceIn', 100, 100);
    expect(i.ok).toBe(false);
  });
  it('refuses zero length, negative in-point and fractions', () => {
    const d = one();
    expect(buildNumericTrimOps(d, 'a', 'duration', 0, null).ok).toBe(false);
    expect(buildNumericTrimOps(d, 'a', 'sourceIn', -3, null).ok).toBe(false);
    expect(buildNumericTrimOps(d, 'a', 'duration', 10.5, null).ok).toBe(false);
    expect(buildNumericTrimOps(d, 'a', 'duration', 60, null).ok).toBe(false);
  });
});
