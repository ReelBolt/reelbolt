/**
 * P8 advanced structure + audio — the pure modules: the TS flattener (a mirror of the C#
 * `TimelineFlattener`), compound create/open/commit/decompose and multicam create/live-cut/switch as
 * programs of the closed catalogue (each replayed from the original document and undone through the
 * engine's own inverses), waveform cross-correlation sync, latch automation, the VO placement, and
 * applyProposal: atomic, provenance-aware, partially acceptable, always leaving a valid document.
 */

import { describe, expect, it, vi } from 'vitest';
import fc from 'fast-check';
import { apply, assertInvariants, EditEngineError, type EditOp, type TrackPropertyValue } from '../engine';
import { doc, media, title, track, TRACKS } from './fixtures';
import { expandMulticam, flattenTimeline, needsFlattening } from '../structure/flatten';
import { compileCommitSequence, compileCreateCompound, compileDecompose, openSequenceDocument } from '../structure/compound';
import { compileCreateMulticam, compileLiveCut, compileSwitchAngle, findMulticam } from '../structure/multicam';
import { envelope, estimateSyncOffset, isConfidentSync, offsetToFrames } from '../audio/sync';
import { compileLatchPass, thinSamples, valueAt } from '../audio/automation';
import { compilePlaceVoiceover, recorderSupport, voFileName } from '../audio/vo-recorder';
import { ghostDiff, planProposal, programFor, ProposalNeedsAcknowledgement, userOwnedTouchedBy } from '../proposals/apply-proposal';
import type { EditProposalResponse } from '../sync/api';
import type { EditTimeline, MediaClip, MulticamClip } from '../types';

// These fixtures name things `c1`/`timeline-1`, and the production code under test checks documents
// with the strict engine invariants. Only the Guid id shape is relaxed here (it is pinned in
// `engine/__tests__/apply.contract.test.ts`); every structural invariant still runs.
vi.mock('../engine', async (importOriginal) => {
  const engine = await importOriginal<typeof import('../engine')>();
  return { ...engine, assertInvariants: (doc: Parameters<typeof engine.assertInvariants>[0]) => engine.assertInvariants(doc, { ids: 'opaque' }) };
});

/** Applies a program, collecting the inverse; then proves the inverse restores the start exactly. */
function runAndUndo(start: EditTimeline, ops: readonly EditOp[]): EditTimeline {
  let current = start;
  const inverses: EditOp[][] = [];
  for (const op of ops) {
    const r = apply(current, op);
    current = r.doc;
    inverses.push(r.inverse);
  }
  assertInvariants(current);
  let undone = current;
  for (let i = inverses.length - 1; i >= 0; i--) for (const op of inverses[i]) undone = apply(undone, op).doc;
  expect(undone).toStrictEqual(start);
  return current;
}

let counter = 0;
const newId = () => `id-${++counter}`;

const machine = (id: string, extra: Partial<MediaClip> = {}): MediaClip => ({ ...media(id, 0, 1, 0), provenance: { origin: 'generated', editedByUser: false, agentName: 'VideoStoryEditor' }, ...extra });

describe('P8 flattener (TS mirror)', () => {
  it('leaves a document without compound or multicam clips untouched (same object)', () => {
    const d = doc([track(TRACKS.v1, 'video', [media('a', 0, 30)])]);
    expect(needsFlattening(d)).toBe(false);
    expect(flattenTimeline(d)).toBe(d);
  });

  it('expands a compound clip into shifted, clipped nested tracks right after its host, with derived ids', () => {
    const d: EditTimeline = {
      ...doc([track(TRACKS.v1, 'video', [{ id: 'cc', type: 'compoundClip', name: 'Nest', recordIn: 100, recordOut: 140, sequenceId: 'q', sourceIn: 10, enabled: true }]), track(TRACKS.v2, 'video', [])]),
      sequences: [{ id: 'q', name: 'Q', tracks: [track('qt', 'video', [media('n1', 0, 20, 5), media('n2', 20, 60, 0), { id: 'tr', type: 'transition', name: 'x', recordIn: 45, recordOut: 55, transitionType: 'dissolve' }])] }],
    };
    const flat = flattenTimeline(d);
    expect(flat.tracks.map((t) => t.id)).toEqual([TRACKS.v1, 'cc/qt', TRACKS.v2]);
    expect(flat.tracks[0].items).toEqual([]);
    const nested = flat.tracks[1].items as MediaClip[];
    // shift = 100 − 10 = 90: n1 [90,110) clipped to [100,110) with sourceIn 5 + 10; n2 [110,150) → [110,140).
    expect(nested.map((i) => [i.id, i.recordIn, i.recordOut, i.sourceIn])).toEqual([
      ['cc/n1', 100, 110, 15],
      ['cc/n2', 110, 140, 0],
    ]);
    // The transition [135,145) is cut by the clip's end → dropped.
    expect(nested.some((i) => (i as { type: string }).type === 'transition')).toBe(false);
  });

  it('expands a multicam clip into one media clip per angle run', () => {
    const clip: MulticamClip = {
      id: 'mc', type: 'multicamClip', name: 'MC', recordIn: 30, recordOut: 90, sourceIn: 4, activeAngleId: 'a',
      angles: [{ id: 'a', name: 'A', assetRef: media('x', 0, 1).assetRef, offsetFrames: 0 }, { id: 'b', name: 'B', assetRef: media('y', 0, 1).assetRef, offsetFrames: 12 }],
      cuts: [{ atFrame: 50, angleId: 'b' }, { atFrame: 70, angleId: 'a' }],
    };
    expect(expandMulticam(clip).map((m) => [m.recordIn, m.recordOut, m.sourceIn, m.name])).toEqual([
      [30, 50, 4, 'MC · A'],
      [50, 70, 12 + 4 + 20, 'MC · B'],
      [70, 90, 0 + 4 + 40, 'MC · A'],
    ]);
  });

  it('never loops on a cyclic or over-deep document (it drops, like the C# side)', () => {
    const cyc: EditTimeline = {
      ...doc([track(TRACKS.v1, 'video', [{ id: 'c1', type: 'compoundClip', name: 'c1', recordIn: 0, recordOut: 10, sequenceId: 'q1' }])]),
      sequences: [{ id: 'q1', name: 'q1', tracks: [track('t', 'video', [{ id: 'c2', type: 'compoundClip', name: 'c2', recordIn: 0, recordOut: 10, sequenceId: 'q1' }])] }],
    };
    expect(() => flattenTimeline(cyc)).not.toThrow();
  });
});

describe('P8 compound clips as programs', () => {
  const start = () => doc([track(TRACKS.v1, 'video', [media('a', 10, 40, 0), media('b', 40, 70, 100), media('z', 200, 230)]), track(TRACKS.a1, 'audio', [media('m', 20, 60, 0)])]);

  it('creates a compound over the selection (one sequence track per source track, ids kept) and undoes exactly', () => {
    const ops = compileCreateCompound(start(), ['a', 'b', 'm'], { sequenceId: 'seq1', compoundId: 'cmp1' }, 'Interview');
    const after = runAndUndo(start(), ops);
    expect(after.tracks[0].items.map((i) => i.id)).toEqual(['cmp1', 'z']);
    expect(after.tracks[1].items).toEqual([]);
    const seq = after.sequences?.find((q) => q.id === 'seq1');
    expect(seq?.tracks.map((t) => t.items.map((i) => [i.id, i.recordIn, i.recordOut]))).toEqual([
      [['a', 0, 30], ['b', 30, 60]],
      [['m', 10, 50]],
    ]);
    // The flattened picture reproduces the original placement.
    const flat = flattenTimeline(after);
    const placed = flat.tracks.flatMap((t) => t.items).filter((i) => i.type === 'mediaClip').map((i) => [i.recordIn, i.recordOut]);
    expect(placed).toEqual(expect.arrayContaining([[10, 40], [40, 70], [20, 60], [200, 230]]));
  });

  it('refuses a compound whose span is not free on the host track', () => {
    expect(() => compileCreateCompound(start(), ['a', 'z'], { sequenceId: 's', compoundId: 'c' })).toThrow(/not free/);
  });

  it('opens a sequence as a sub-document and commits edits back as ONE setSequence', () => {
    const created = runAndUndo(start(), compileCreateCompound(start(), ['a', 'b'], { sequenceId: 'seq1', compoundId: 'cmp1' }));
    const sub = openSequenceDocument(created, 'seq1');
    const edited = apply(sub, { type: 'blade', itemId: 'a', atFrame: 15, newItemId: 'a2' }).doc;
    const commit = compileCommitSequence(created, 'seq1', edited);
    expect(commit.type).toBe('setSequence');
    const after = runAndUndo(created, [commit]);
    expect(after.sequences?.[0].tracks[0].items.map((i) => i.id)).toEqual(['a', 'a2', 'b']);
  });

  it('refuses a commit that would reuse a root item id inside the sequence', () => {
    const created = runAndUndo(start(), compileCreateCompound(start(), ['a'], { sequenceId: 'seq1', compoundId: 'cmp1' }));
    const sub = openSequenceDocument(created, 'seq1');
    const dup = apply(sub, { type: 'placeOnTop', trackId: sub.tracks[0].id, item: media('z', 100, 110) }).doc;
    expect(() => apply(created, compileCommitSequence(created, 'seq1', dup))).toThrow(EditEngineError);
  });

  it('decomposes a compound back onto fresh tracks (fresh ids, same placement) and removes the unused sequence', () => {
    const created = runAndUndo(start(), compileCreateCompound(start(), ['a', 'b'], { sequenceId: 'seq1', compoundId: 'cmp1' }));
    const after = runAndUndo(created, compileDecompose(created, 'cmp1', newId));
    expect(after.sequences ?? []).toEqual([]);
    const restored = after.tracks[1].items.map((i) => [i.recordIn, i.recordOut, (i as MediaClip).sourceIn]);
    expect(restored).toEqual([[10, 40, 0], [40, 70, 100]]);
  });

  it('refuses a cycle and a nesting deeper than four', () => {
    const created = runAndUndo(start(), compileCreateCompound(start(), ['a'], { sequenceId: 'q1', compoundId: 'c1' }));
    const self = { ...created.sequences![0], tracks: [track('t', 'video', [{ id: 'cx', type: 'compoundClip' as const, name: 'self', recordIn: 0, recordOut: 5, sequenceId: 'q1' }])] };
    expect(() => apply(created, { type: 'setSequence', sequenceId: 'q1', sequence: self })).toThrow(/contains itself/);

    let deep: EditTimeline = doc([track(TRACKS.v1, 'video', [{ id: 'r', type: 'compoundClip', name: 'r', recordIn: 0, recordOut: 5, sequenceId: 'd1' }])]);
    deep = { ...deep, sequences: [1, 2, 3, 4].map((n) => ({ id: `d${n}`, name: `d${n}`, tracks: n < 4 ? [track(`t${n}`, 'video', [{ id: `k${n}`, type: 'compoundClip' as const, name: 'k', recordIn: 0, recordOut: 5, sequenceId: `d${n + 1}` }])] : [] })) };
    expect(() => assertInvariants(deep)).toThrow(/nest at most 4/);
  });

  it('refuses removing a sequence a compound still plays', () => {
    const created = runAndUndo(start(), compileCreateCompound(start(), ['a'], { sequenceId: 'q1', compoundId: 'c1' }));
    expect(() => apply(created, { type: 'setSequence', sequenceId: 'q1', sequence: undefined })).toThrow(/still played/);
  });
});

describe('P8 multicam as programs', () => {
  const base = () => doc([track(TRACKS.v1, 'video', [media('camA', 0, 120, 10)]), track(TRACKS.v2, 'video', [media('camB', 0, 120, 0)])]);

  it('creates a multicam from synced clips (offsets = sourceIn + sync) and undoes exactly', () => {
    const ops = compileCreateMulticam(base(), ['camA', 'camB'], { multicamId: 'mc', angleIds: ['A', 'B'] }, { camB: 7 });
    const after = runAndUndo(base(), ops);
    const mc = findMulticam(after, 'mc')!;
    expect(mc.angles.map((a) => [a.id, a.offsetFrames])).toEqual([['A', 10], ['B', 7]]);
    expect(after.tracks[1].items).toEqual([]);
  });

  it('cuts live (one op per press, a press on the live angle is a no-op) and switches a run later', () => {
    let d = runAndUndo(base(), compileCreateMulticam(base(), ['camA', 'camB'], { multicamId: 'mc', angleIds: ['A', 'B'] }));
    const press = (frame: number, angle: string) => {
      const op = compileLiveCut(findMulticam(d, 'mc')!, frame, angle);
      if (op) d = runAndUndo(d, [op]);
      return op;
    };
    expect(press(10, 'A')).toBeNull();
    press(40, 'B');
    press(80, 'A');
    expect(findMulticam(d, 'mc')!.cuts).toEqual([{ atFrame: 40, angleId: 'B' }, { atFrame: 80, angleId: 'A' }]);
    const sw = compileSwitchAngle(findMulticam(d, 'mc')!, 60, 'A');
    d = runAndUndo(d, [sw!]);
    expect(findMulticam(d, 'mc')!.cuts?.[0]).toEqual({ atFrame: 40, angleId: 'A' });
    const head = compileSwitchAngle(findMulticam(d, 'mc')!, 5, 'B');
    expect(head).toEqual({ type: 'setProperty', itemId: 'mc', key: 'activeAngleId', value: 'B' });
  });

  it('blades a multicam into halves that each keep their own cuts and start on the live angle', () => {
    let d = runAndUndo(base(), compileCreateMulticam(base(), ['camA', 'camB'], { multicamId: 'mc', angleIds: ['A', 'B'] }));
    d = runAndUndo(d, [compileLiveCut(findMulticam(d, 'mc')!, 40, 'B')!, compileLiveCut({ ...findMulticam(d, 'mc')!, cuts: [{ atFrame: 40, angleId: 'B' }] }, 80, 'A')!]);
    const after = runAndUndo(d, [{ type: 'blade', itemId: 'mc', atFrame: 60, newItemId: 'mc2' }]);
    const left = findMulticam(after, 'mc')!;
    const right = findMulticam(after, 'mc2')!;
    expect(left.cuts).toEqual([{ atFrame: 40, angleId: 'B' }]);
    expect(right.activeAngleId).toBe('B');
    expect(right.cuts).toEqual([{ atFrame: 80, angleId: 'A' }]);
    expect(right.sourceIn).toBe(60);
  });

  it('refuses a trim that would strand a cut outside the clip (instead of saving a document the server rejects)', () => {
    let d = runAndUndo(base(), compileCreateMulticam(base(), ['camA', 'camB'], { multicamId: 'mc', angleIds: ['A', 'B'] }));
    d = runAndUndo(d, [compileLiveCut(findMulticam(d, 'mc')!, 100, 'B')!]);
    expect(() => apply(d, { type: 'trim', itemId: 'mc', edge: 'out', mode: 'ripple', deltaFrames: -40 })).toThrow(/strictly inside/);
    expect(() => apply(d, { type: 'setProperty', itemId: 'mc', key: 'cuts', value: [{ atFrame: 100, angleId: 'nope' }] })).toThrow(/not one of the clip's angles/);
  });
});

describe('P8 buses and processing', () => {
  it('adds, routes and removes buses; refuses a cycle and out-of-range processing', () => {
    const d = doc([track(TRACKS.a1, 'audio', [media('m', 0, 30)])]);
    let after = runAndUndo(d, [
      { type: 'setBus', busId: 'dx', bus: { id: 'dx', name: 'Dialogue', dynamics: { compressor: { thresholdDb: -20, ratio: 4 } }, leveler: { kind: 'speech', targetDb: -16 } } },
      { type: 'setBus', busId: 'mx', bus: { id: 'mx', name: 'Mix', outputBusId: null } },
      { type: 'setTrackProperty', trackId: TRACKS.a1, key: 'busId', value: 'dx' },
      { type: 'setTrackProperty', trackId: TRACKS.a1, key: 'denoise', value: { kind: 'fft', strength: 0.4 } },
    ]);
    after = runAndUndo(after, [{ type: 'setBus', busId: 'dx', bus: { ...after.buses![0], outputBusId: 'mx' } }]);
    expect(() => apply(after, { type: 'setBus', busId: 'mx', bus: { id: 'mx', name: 'Mix', outputBusId: 'dx' } })).toThrow(/cycle/);
    expect(() => apply(after, { type: 'setTrackProperty', trackId: TRACKS.a1, key: 'dynamics', value: { limiter: { ceilingDb: 3 } } })).toThrow(EditEngineError);
    expect(() => apply(after, { type: 'setTrackProperty', trackId: TRACKS.a1, key: 'denoise', value: { kind: 'rnn', strength: 0.5 } as unknown as TrackPropertyValue })).toThrow(EditEngineError);
    // Removing a bus a track still names is allowed: the track feeds the master.
    const removed = runAndUndo(after, [{ type: 'setBus', busId: 'dx', bus: undefined }]);
    expect(removed.tracks[0].busId).toBe('dx');
  });

  it('sets a channel map from the closed catalogue only', () => {
    const d = doc([track(TRACKS.a1, 'audio', [media('m', 0, 30)])]);
    runAndUndo(d, [{ type: 'setProperty', itemId: 'm', key: 'channelMap', value: 'mono' }]);
    expect(() => apply(d, { type: 'setProperty', itemId: 'm', key: 'channelMap', value: 'surround' })).toThrow(EditEngineError);
  });
});

describe('P8 audio sync by cross-correlation', () => {
  /** A noise-floor signal with a few transients ("claps") at the given seconds. */
  const signal = (claps: number[], seconds: number, rate = 8000, seed = 1): Float32Array => {
    const out = new Float32Array(seconds * rate);
    let s = seed;
    for (let i = 0; i < out.length; i++) {
      s = (s * 1103515245 + 12345) % 2147483648;
      out[i] = ((s / 2147483648) - 0.5) * 0.02;
    }
    for (const c of claps) for (let i = 0; i < rate / 20; i++) out[Math.round(c * rate) + i] += Math.exp(-i / 80) * (i % 2 ? 0.9 : -0.9);
    return out;
  };

  it('recovers a known offset to the bucket and reports high confidence', () => {
    const ref = envelope(signal([1, 3.2, 5.5, 7], 10), 8000);
    const other = envelope(signal([1.73, 3.93, 6.23, 7.73], 10, 8000, 7), 8000);
    const r = estimateSyncOffset(ref, other, 100, 3);
    expect(r.lagBuckets).toBe(73);
    expect(r.offsetSeconds).toBeCloseTo(0.73, 2);
    expect(isConfidentSync(r)).toBe(true);
    expect(offsetToFrames(r.offsetSeconds, { num: 30, den: 1 })).toBe(22);
  });

  it('finds a negative offset too, and reports an unrelated pair as not confident', () => {
    const ref = envelope(signal([2, 4.5, 6], 9), 8000);
    const other = envelope(signal([1.5, 4, 5.5], 9, 8000, 3), 8000);
    expect(estimateSyncOffset(ref, other, 100, 2).lagBuckets).toBe(-50);
    const flat = envelope(signal([], 9, 8000, 5), 8000);
    expect(isConfidentSync(estimateSyncOffset(ref, flat, 100, 2))).toBe(false);
  });
});

describe('P8 latch automation', () => {
  it('reads keys (linear and step) and thins a held fader to its endpoints', () => {
    const ch = { value: -6, keys: [{ frame: 0, value: -20, interp: 'linear' as const }, { frame: 10, value: 0, interp: 'step' as const }, { frame: 20, value: -10, interp: 'linear' as const }] };
    expect(valueAt(ch, 5)).toBe(-10);
    expect(valueAt(ch, 15)).toBe(0);
    expect(valueAt(null, 3, -3)).toBe(-3);
    const held = Array.from({ length: 50 }, (_, i) => ({ frame: i, value: -12 }));
    expect(thinSamples(held)).toEqual([{ frame: 0, value: -12 }, { frame: 49, value: -12 }]);
  });

  it('a latch pass replaces keys inside the pass window only, as one undoable program', () => {
    const d = doc([track(TRACKS.a1, 'audio', [media('m', 0, 300)], { gainDb: { value: 0, keys: [{ frame: 5, value: -3, interp: 'linear' }, { frame: 50, value: -9, interp: 'linear' }, { frame: 250, value: -1, interp: 'linear' }] } })]);
    const samples = [...Array.from({ length: 30 }, (_, i) => ({ frame: 40 + i, value: -6 })), ...Array.from({ length: 30 }, (_, i) => ({ frame: 70 + i, value: -6 - i / 2 }))];
    const ops = compileLatchPass({ trackId: TRACKS.a1 }, d.tracks[0].gainDb, samples);
    const after = runAndUndo(d, ops);
    const keys = after.tracks[0].gainDb!.keys!;
    expect(keys.map((k) => k.frame)).toContain(5);
    expect(keys.map((k) => k.frame)).toContain(250);
    expect(keys.map((k) => k.frame)).not.toContain(50);
    expect(keys.find((k) => k.frame === 40)?.value).toBe(-6);
  });

  it('property: any pass of in-range samples compiles to a program the engine accepts and undoes', () => {
    fc.assert(
      fc.property(fc.array(fc.record({ frame: fc.integer({ min: 0, max: 400 }), value: fc.double({ min: -80, max: 40, noNaN: true }) }), { minLength: 1, maxLength: 60 }), (samples) => {
        const d = doc([track(TRACKS.a1, 'audio', [media('m', 0, 500)])]);
        runAndUndo(d, compileLatchPass({ trackId: TRACKS.a1 }, null, [...samples].sort((a, b) => a.frame - b.frame)));
      }),
      { numRuns: 60 },
    );
  });
});

describe('P8 browser voiceover', () => {
  it('reports why recording is unavailable, and picks a supported container when it is', () => {
    expect(recorderSupport({ isSecureContext: false })).toEqual({ ok: false, reason: expect.stringContaining('secure') });
    expect(recorderSupport({ isSecureContext: true, mediaDevices: {} })).toMatchObject({ ok: false });
    expect(recorderSupport({ isSecureContext: true, mediaDevices: { getUserMedia: () => null }, MediaRecorder: { isTypeSupported: (t) => t === 'audio/webm' } })).toEqual({ ok: true, mimeType: 'audio/webm' });
    expect(voFileName('My promo!', new Date('2026-10-11T08:09:10Z'), 'audio/webm')).toBe('vo-My-promo-20261011-080910.webm');
  });

  it('places a take on a free audio track at the playhead, or on a new VO track', () => {
    const d = doc([track(TRACKS.v1, 'video', [media('v', 0, 100)]), track(TRACKS.a1, 'audio', [media('m', 0, 60)])]);
    const busy = compilePlaceVoiceover(d, '00000000-0000-4000-8000-000000000099', 30, 45, { itemId: 'vo1', trackId: 'tvo' });
    expect(busy[0].type).toBe('addTrack');
    runAndUndo(d, busy);
    const free = compilePlaceVoiceover(d, '00000000-0000-4000-8000-000000000099', 60, 45, { itemId: 'vo1', trackId: 'tvo' });
    expect(free).toEqual([expect.objectContaining({ type: 'placeOnTop', trackId: TRACKS.a1 })]);
    runAndUndo(d, free);
  });
});

describe('P8 applyProposal', () => {
  /** A document with one agent-made clip, one user clip and a title; mirrors what EditProposalCompiler emits. */
  const proposalDoc = () =>
    doc([
      track(TRACKS.v1, 'video', [machine('gen', { recordIn: 0, recordOut: 60 }), media('mine', 60, 120, 0), title('t', 120, 150)]),
      track(TRACKS.a1, 'audio', [media('music', 0, 150)]),
    ]);
  const proposal: Pick<EditProposalResponse, 'groups'> = {
    groups: [
      { label: 'Trim end of gen', ops: [{ type: 'trim', itemId: 'gen', edge: 'out', mode: 'ripple', deltaFrames: -15 }], touchedItemIds: ['gen'], touchesUserOwned: false, reason: 'slow' },
      { label: 'Remove mine', ops: [{ type: 'extract', itemId: 'mine' }], touchedItemIds: ['mine'], touchesUserOwned: true, reason: 'redundant' },
      {
        label: 'Title A',
        ops: [
          { type: 'addTrack', index: 2, track: { id: 'gfx', kind: 'video', name: 'Graphics', height: 64, locked: false, enabled: true, mute: false, solo: false, items: [] } },
          { type: 'placeOnTop', trackId: 'gfx', item: { ...title('tA', 0, 30, 'Hello'), provenance: { origin: 'generated', editedByUser: false, agentName: 'MotionGraphicsPlanner' } } },
        ],
        touchedItemIds: ['gen', 'tA'], touchesUserOwned: false, reason: null,
      },
      { label: 'Title B', ops: [{ type: 'placeOnTop', trackId: 'gfx', item: title('tB', 90, 110, 'Bye') }], touchedItemIds: ['tB'], touchesUserOwned: false, reason: null },
    ],
  };

  it('recomputes provenance from the document, not the server flag', () => {
    expect(userOwnedTouchedBy(proposalDoc(), proposal.groups[1])).toEqual(['mine']);
    // A lying flag does not help: the group still touches a user clip.
    expect(userOwnedTouchedBy(proposalDoc(), { ...proposal.groups[1], touchesUserOwned: false })).toEqual(['mine']);
    expect(userOwnedTouchedBy(proposalDoc(), proposal.groups[0])).toEqual([]);
  });

  it('refuses to change a user-owned clip without acknowledgement, and applies it once acknowledged', () => {
    expect(() => planProposal(proposalDoc(), proposal, new Set([1]))).toThrow(ProposalNeedsAcknowledgement);
    const plan = planProposal(proposalDoc(), proposal, new Set([1]), new Set(['mine']));
    expect(plan.doc.tracks[0].items.map((i) => i.id)).toEqual(['gen', 't']);
    runAndUndo(proposalDoc(), plan.ops);
  });

  it('a partial accept pulls in the lane a later group needs and leaves a valid document', () => {
    expect(programFor(proposal, new Set([3])).map((o) => o.type)).toEqual(['addTrack', 'placeOnTop']);
    const plan = planProposal(proposalDoc(), proposal, new Set([0, 3]));
    assertInvariants(plan.doc);
    expect(plan.doc.tracks[2].items.map((i) => i.id)).toEqual(['tB']);
    runAndUndo(proposalDoc(), plan.ops);
  });

  it('is atomic: when one accepted group cannot apply, nothing is applied and the group is named', () => {
    const broken = { groups: [...proposal.groups, { label: 'Ghost clip', ops: [{ type: 'lift', itemId: 'gone' } as EditOp], touchedItemIds: [], touchesUserOwned: false, reason: null }] };
    const before = proposalDoc();
    expect(() => planProposal(before, broken, new Set([0, 4]))).toThrow(/Ghost clip/);
    expect(before).toStrictEqual(proposalDoc());
  });

  it('ghost diff: additions outlined, removals hatched, changes carry the old span, user ownership flagged', () => {
    const { ghosts, failed, newTracks } = ghostDiff(proposalDoc(), proposal, new Set([0, 1, 2]));
    expect(failed).toEqual([]);
    expect(newTracks).toEqual([{ id: 'gfx', name: 'Graphics', kind: 'video' }]);
    expect(ghosts.find((g) => g.itemId === 'tA')).toMatchObject({ kind: 'add', trackId: 'gfx', groupIndex: 2 });
    expect(ghosts.find((g) => g.itemId === 'mine' && g.groupIndex === 1)).toMatchObject({ kind: 'remove', userOwned: true });
    expect(ghosts.find((g) => g.itemId === 'gen' && g.groupIndex === 0)).toMatchObject({ kind: 'change', before: { recordIn: 0, recordOut: 60 }, recordOut: 45 });
  });

  it('property: any subset of groups either applies atomically to a valid document or refuses without change', () => {
    fc.assert(
      fc.property(fc.subarray([0, 1, 2, 3]), fc.boolean(), (subset, ack) => {
        const start = proposalDoc();
        try {
          const plan = planProposal(start, proposal, new Set(subset), ack ? new Set(['mine']) : new Set());
          assertInvariants(plan.doc);
          runAndUndo(start, plan.ops);
        } catch (error) {
          expect(error instanceof ProposalNeedsAcknowledgement || error instanceof EditEngineError).toBe(true);
          expect(subset.includes(1) && !ack).toBe(true);
        }
        expect(start).toStrictEqual(proposalDoc());
      }),
    );
  });
});
