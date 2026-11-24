/**
 * Editor v2 remediation (T-013): every op hands the sync client a document the server accepts.
 *
 * Each block pins one audit defect (eng D1-D7, D13-D15, m3 N3) against a fixture whose ids are real
 * Guids, so the strict `assertInvariants` (the TS half of `EditTimelineValidator`) runs on every
 * result and every inverse step. A block fails on the engine as it was at origin/master and passes
 * once the defect is fixed.
 */

import { afterEach, describe, expect, it, vi } from 'vitest';

import { apply, applyAll, assertInvariants, EditEngineError, MAX_REVERSE_FRAMES, type EditOp } from '../index';
import type { ColorGroup, EditTimeline, Item, MediaClip, TitleClip, Track, Transition } from '../../types';
import { isGuid, newGuid } from '../../ids';
import { newMarkerId } from '../../markers/markers';
import { compileCreateCompound } from '../../structure/compound';
import { compileLatchPass } from '../../audio/automation';

// ─────────────────────────────────────────────────────────────────────────────
// Fixtures — every id a Guid, as the server stores them
// ─────────────────────────────────────────────────────────────────────────────

/** A deterministic, readable Guid: `g(1)` = `00000000-0000-4000-8000-000000000001`. */
const g = (n: number): string => `00000000-0000-4000-8000-${n.toString(16).padStart(12, '0')}`;

const ID = {
  doc: g(9001),
  project: g(9002),
  file: g(9003),
  v: g(100),
  a: g(200),
  A: g(1),
  B: g(2),
  C: g(3),
  T: g(4),
  title: g(5),
  fresh: g(77),
  group: g(300),
};

const media = (id: string, recordIn: number, recordOut: number, extra: Partial<MediaClip> = {}): MediaClip => ({
  id,
  type: 'mediaClip',
  name: 'clip',
  recordIn,
  recordOut,
  assetRef: { kind: 'projectFile', projectFileId: ID.file },
  sourceIn: 100,
  playbackRate: 1,
  enabled: true,
  provenance: null,
  ...extra,
});

const title = (id: string, recordIn: number, recordOut: number): TitleClip => ({
  id,
  type: 'titleClip',
  name: 'title',
  recordIn,
  recordOut,
  text: 'Hello',
  fontSize: 32,
  fontFamily: 'Inter',
  color: '#ffffff',
  enabled: true,
  provenance: null,
});

const transition = (id: string, recordIn: number, recordOut: number): Transition => ({
  id,
  type: 'transition',
  name: 'Dissolve',
  recordIn,
  recordOut,
  transitionType: 'dissolve',
  easing: 'linear',
  enabled: true,
  provenance: null,
});

const track = (id: string, kind: Track['kind'], items: Item[]): Track => ({
  id,
  kind,
  name: kind === 'video' ? 'V1' : 'A1',
  height: 64,
  locked: false,
  enabled: true,
  mute: false,
  solo: false,
  items,
});

function timeline(video: Item[], audio: Item[] = [], extra: Partial<EditTimeline> = {}): EditTimeline {
  return {
    schemaVersion: 2,
    id: ID.doc,
    projectId: ID.project,
    name: 'Contract fixture',
    settings: { width: 1920, height: 1080, rate: { num: 30, den: 1 }, sampleRate: 48000, pixelAspect: { num: 1, den: 1 }, aspectPolicy: 'fit', startTimecodeFrames: 0 },
    tracks: [track(ID.v, 'video', video), track(ID.a, 'audio', audio)],
    markers: [],
    inOut: null,
    seed: null,
    ...extra,
  };
}

/** The op succeeds, every document on the way (result and each inverse step) is server-valid, and undo restores exactly. */
function roundTrip(doc: EditTimeline, op: EditOp): { doc: EditTimeline; inverse: EditOp[] } {
  assertInvariants(doc);
  const before = structuredClone(doc);
  const applied = apply(doc, op);
  expect(doc).toEqual(before);
  assertInvariants(applied.doc);
  let stepwise = applied.doc;
  for (const inverseOp of applied.inverse) {
    stepwise = apply(stepwise, inverseOp).doc;
    assertInvariants(stepwise);
  }
  expect(stepwise).toEqual(before);
  expect(applyAll(applied.doc, applied.inverse)).toEqual(before);
  return applied;
}

const itemById = (doc: EditTimeline, id: string): Item => {
  for (const t of doc.tracks) for (const i of t.items) if (i.id === id) return i;
  throw new Error(`no item ${id}`);
};
const media$ = (doc: EditTimeline, id: string): MediaClip => itemById(doc, id) as MediaClip;
const spans = (doc: EditTimeline, trackIndex = 0) => doc.tracks[trackIndex].items.map((i) => [i.id, i.recordIn, i.recordOut]);

function rejects(doc: EditTimeline, path: string): void {
  expect(() => assertInvariants(doc)).toThrow(EditEngineError);
  expect(() => assertInvariants(doc)).toThrow(path);
}

// ─────────────────────────────────────────────────────────────────────────────
// D1 / D2 / D3 — ids are Guids everywhere, even without crypto.randomUUID
// ─────────────────────────────────────────────────────────────────────────────

describe('eng D2: one v4 Guid generator that needs no crypto.randomUUID', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('mints canonical v4 UUIDs', () => {
    const seen = new Set<string>();
    for (let i = 0; i < 200; i++) {
      const id = newGuid();
      expect(id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
      seen.add(id);
    }
    expect(seen.size).toBe(200);
  });

  it('works on an insecure origin where randomUUID is missing (getRandomValues only)', () => {
    const getRandomValues = vi.fn((bytes: Uint8Array) => {
      bytes.fill(0xab);
      return bytes;
    });
    vi.stubGlobal('crypto', { getRandomValues });
    expect(newGuid()).toBe('abababab-abab-4bab-abab-abababababab');
    expect(isGuid(newMarkerId())).toBe(true);
    expect(getRandomValues).toHaveBeenCalled();
  });

  it('falls back to Math.random only when getRandomValues is absent', () => {
    vi.stubGlobal('crypto', undefined);
    expect(isGuid(newGuid())).toBe(true);
    expect(isGuid(newMarkerId())).toBe(true);
  });
});

describe('eng D3: Make compound mints Guid sequence-track ids', () => {
  it('the created sequence passes the strict invariants', () => {
    const doc = timeline([media(ID.A, 0, 30), media(ID.B, 30, 60)], [media(ID.C, 0, 60)]);
    const ops = compileCreateCompound(doc, [ID.A, ID.B, ID.C], { sequenceId: g(500), compoundId: g(501) });
    const next = applyAll(doc, ops);
    assertInvariants(next);
    const sequence = next.sequences?.[0];
    expect(sequence?.tracks).toHaveLength(2);
    for (const t of sequence?.tracks ?? []) {
      expect(isGuid(t.id)).toBe(true);
      expect(t.id.includes(':')).toBe(false);
    }
  });
});

// The document-level lockstep cases (track/sequence-track/marker ids, a null static value, fades,
// reverse, reverse+hold, a transition off its cut, a title on an audio track, schemaVersion,
// negative recordIn) live in the shared contract corpus (`__fixtures__/contract/*.json`, run by
// `contract.test.ts` AND the xUnit runner): one source of truth for both sides. Only the cases the
// corpus does not carry stay here.
describe('TS/C# lockstep: assertInvariants refuses what model binding refuses (beyond the corpus)', () => {
  it('item, asset, colour-group and bus ids must be Guids too', () => {
    const item = timeline([media('clip-1', 0, 30)]);
    rejects(item, 'tracks[0].items[0].id');
    const asset = timeline([media(ID.A, 0, 30, { assetRef: { kind: 'projectFile', projectFileId: 'file-1' } })]);
    rejects(asset, 'tracks[0].items[0].assetRef.projectFileId');
    const group = timeline([media(ID.A, 0, 30)], [], { colorGroups: [{ id: 'grp', name: 'G', clipIds: [ID.A], grade: null }] });
    rejects(group, 'colorGroups[0].id');
    const bus = timeline([media(ID.A, 0, 30)], [], { buses: [{ id: 'bus-1', name: 'Dialogue' }] });
    rejects(bus, 'buses[0].id');
  });

  it('eng D4: a null static value is refused on a transform channel and a track fader too', () => {
    const doc = timeline([media(ID.A, 0, 30, { transform: { opacity: { value: null, keys: [{ frame: 5, value: 0.5, interp: 'linear' }] } } })]);
    rejects(doc, 'tracks[0].items[0].transform.opacity.value');
    const trackGain = timeline([media(ID.A, 0, 30)]);
    trackGain.tracks[1].gainDb = { value: null, keys: [{ frame: 61, value: -36.5, interp: 'linear' }] };
    rejects(trackGain, 'tracks[1].gainDb.value');
  });
});

describe('d1 N5: the engine never puts a picture-only item on an audio track', () => {
  it('insert / placeOnTop / move of a title onto the audio track are refused', () => {
    const doc = timeline([title(ID.title, 0, 30)]);
    expect(() => apply(doc, { type: 'placeOnTop', trackId: ID.a, item: title(ID.fresh, 40, 50) })).toThrow(/audio track/);
    expect(() => apply(doc, { type: 'insert', trackId: ID.a, index: 0, item: title(ID.fresh, 40, 50) })).toThrow(/audio track/);
    expect(() => apply(doc, { type: 'move', itemId: ID.title, toTrackId: ID.a, toRecordIn: 0 })).toThrow(/audio track/);
    // A media clip may still go to the audio track (its sound plays there).
    roundTrip(timeline([media(ID.A, 0, 30)]), { type: 'move', itemId: ID.A, toTrackId: ID.a, toRecordIn: 0 });
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// D4 / m3 N3 — the first keyframe writes a numeric static value
// ─────────────────────────────────────────────────────────────────────────────

describe('eng D4 / m3 N3: the first keyframe on a channel never writes value:null', () => {
  it('an untouched opacity channel gets its identity (1) as the static value', () => {
    const { doc } = roundTrip(timeline([media(ID.A, 0, 30)]), { type: 'setKeyframe', target: { itemId: ID.A }, param: 'opacity', frame: 5, value: 0.5, interp: 'linear' });
    expect(media$(doc, ID.A).transform?.opacity).toEqual({ value: 1, keys: [{ frame: 5, value: 0.5, interp: 'linear' }] });
  });

  it('scale (1), position (0), rotation (0) and clip gain (0 dB) use their identities', () => {
    let doc = timeline([media(ID.A, 0, 30)]);
    for (const [param, identity] of [['scale', 1], ['positionX', 0], ['rotation', 0], ['gainDb', 0]] as const) {
      doc = roundTrip(doc, { type: 'setKeyframe', target: { itemId: ID.A }, param, frame: 2, value: 0.5, interp: 'linear' }).doc;
      const clip = media$(doc, ID.A);
      const channel = param === 'gainDb' ? clip.gainDb : clip.transform?.[param];
      expect(channel?.value).toBe(identity);
    }
  });

  it('an existing static value is kept, and a null one is replaced by the identity', () => {
    const kept = roundTrip(timeline([media(ID.A, 0, 30, { gainDb: { value: -6 } })]), { type: 'setKeyframe', target: { itemId: ID.A }, param: 'gainDb', frame: 3, value: -12, interp: 'linear' });
    expect(media$(kept.doc, ID.A).gainDb).toEqual({ value: -6, keys: [{ frame: 3, value: -12, interp: 'linear' }] });
    const nulled = apply(timeline([media(ID.A, 0, 30, { gainDb: { value: null } })]), { type: 'setKeyframe', target: { itemId: ID.A }, param: 'gainDb', frame: 3, value: -12, interp: 'linear' });
    expect(media$(nulled.doc, ID.A).gainDb?.value).toBe(0);
    assertInvariants(nulled.doc);
  });

  it('a Latch pass on a track with no fader writes a valid keyed gain', () => {
    const doc = timeline([], [media(ID.A, 0, 300)]);
    const ops = compileLatchPass({ trackId: ID.a }, doc.tracks[1].gainDb, [
      { frame: 61, value: -36.5 },
      { frame: 90, value: -20 },
    ]);
    const next = applyAll(doc, ops);
    assertInvariants(next);
    expect(next.tracks[1].gainDb?.value).toBe(0);
    expect(next.tracks[1].gainDb?.keys?.map((k) => k.frame)).toEqual([61, 90]);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// D5 — blade / trim / slip respect playbackRate, reverse and hold
// ─────────────────────────────────────────────────────────────────────────────

describe('eng D5: source arithmetic follows playbackRate, reverse and hold', () => {
  it('blade on a 2x clip starts the right half twice as far into the source', () => {
    const { doc } = roundTrip(timeline([media(ID.A, 0, 100, { playbackRate: 2 })]), { type: 'blade', itemId: ID.A, atFrame: 20, newItemId: ID.fresh });
    expect(media$(doc, ID.A).sourceIn).toBe(100);
    expect(media$(doc, ID.fresh).sourceIn).toBe(140);
  });

  it('blade on a reversed clip keeps each half showing the frames it showed before', () => {
    const { doc } = roundTrip(timeline([media(ID.A, 0, 100, { reverse: true, playbackRate: 1.5 })]), { type: 'blade', itemId: ID.A, atFrame: 20, newItemId: ID.fresh });
    // reversed: frame f reads sourceIn + (recordOut - f) * rate; the right half touches the lowest frames.
    expect(media$(doc, ID.A).sourceIn).toBe(100 + 120);
    expect(media$(doc, ID.fresh).sourceIn).toBe(100);
  });

  it('blade on a held clip freezes both halves on the same frame', () => {
    const { doc } = roundTrip(timeline([media(ID.A, 0, 100, { hold: true })]), { type: 'blade', itemId: ID.A, atFrame: 40, newItemId: ID.fresh });
    expect(media$(doc, ID.A).sourceIn).toBe(100);
    expect(media$(doc, ID.fresh).sourceIn).toBe(100);
  });

  it('blade does not clone fades across the cut and clamps them to each half', () => {
    const { doc } = roundTrip(timeline([media(ID.A, 0, 100, { fadeInFrames: 15, fadeOutFrames: 15 })]), { type: 'blade', itemId: ID.A, atFrame: 20, newItemId: ID.fresh });
    expect(media$(doc, ID.A)).toMatchObject({ fadeInFrames: 15, fadeOutFrames: 0 });
    expect(media$(doc, ID.fresh)).toMatchObject({ fadeInFrames: 0, fadeOutFrames: 15 });
    const near = roundTrip(timeline([media(ID.A, 0, 100, { fadeInFrames: 30, fadeOutFrames: 15 })]), { type: 'blade', itemId: ID.A, atFrame: 10, newItemId: ID.fresh }).doc;
    expect(media$(near, ID.A).fadeInFrames).toBe(10);
  });

  it('ripple-trimming the head of a 2x clip advances sourceIn by twice the frames', () => {
    const { doc } = roundTrip(timeline([media(ID.A, 0, 100, { playbackRate: 2 })]), { type: 'trim', itemId: ID.A, edge: 'in', mode: 'ripple', deltaFrames: 10 });
    expect(media$(doc, ID.A)).toMatchObject({ recordIn: 0, recordOut: 90, sourceIn: 120 });
  });

  it('trimming the tail of a reversed clip removes its lowest source frames (the tail), not the head', () => {
    const { doc } = roundTrip(timeline([media(ID.A, 0, 100, { reverse: true })]), { type: 'trim', itemId: ID.A, edge: 'out', mode: 'ripple', deltaFrames: -10 });
    expect(media$(doc, ID.A)).toMatchObject({ recordOut: 90, sourceIn: 110 });
    const head = roundTrip(timeline([media(ID.A, 0, 100, { reverse: true })]), { type: 'trim', itemId: ID.A, edge: 'in', mode: 'ripple', deltaFrames: 10 }).doc;
    expect(media$(head, ID.A)).toMatchObject({ recordIn: 0, recordOut: 90, sourceIn: 100 });
  });

  it('slip moves the source by playbackRate × frames; a held clip has nothing to trim from', () => {
    const { doc } = roundTrip(timeline([media(ID.A, 0, 100, { playbackRate: 2 })]), { type: 'trim', itemId: ID.A, edge: 'in', mode: 'slip', deltaFrames: 5 });
    expect(media$(doc, ID.A).sourceIn).toBe(110);
    const held = roundTrip(timeline([media(ID.A, 0, 100, { hold: true })]), { type: 'trim', itemId: ID.A, edge: 'in', mode: 'ripple', deltaFrames: 10 }).doc;
    expect(media$(held, ID.A).sourceIn).toBe(100);
  });

  it('roll moves the neighbour\'s source by its own rate', () => {
    const { doc } = roundTrip(timeline([media(ID.A, 0, 50), media(ID.B, 50, 100, { playbackRate: 2 })]), { type: 'trim', itemId: ID.A, edge: 'out', mode: 'roll', deltaFrames: 5 });
    expect(media$(doc, ID.B)).toMatchObject({ recordIn: 55, sourceIn: 110 });
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// D6 — no op produces a document the validator refuses
// ─────────────────────────────────────────────────────────────────────────────

describe('eng D6: ops never produce fades > duration, reverse > 1800 f, or reverse+hold', () => {
  it('a ripple trim stops where the fades still fit', () => {
    const { doc } = roundTrip(timeline([media(ID.A, 0, 100, { fadeInFrames: 15, fadeOutFrames: 15 })]), { type: 'trim', itemId: ID.A, edge: 'out', mode: 'ripple', deltaFrames: -95 });
    expect(media$(doc, ID.A).recordOut).toBe(30);
  });

  it('retime and fit-to-fill refuse to shrink a clip below its fades', () => {
    const doc = timeline([media(ID.A, 0, 100, { fadeInFrames: 15, fadeOutFrames: 15 })]);
    expect(() => apply(doc, { type: 'retime', itemId: ID.A, playbackRate: 4, recordOut: 25 })).toThrow(/fades/);
    expect(() => apply(doc, { type: 'fitToFill', itemId: ID.A, recordIn: 0, recordOut: 20 })).toThrow(/fades/);
  });

  it('setProperty refuses fades longer than the clip, reverse on a clip past 1800 frames, reverse with hold', () => {
    const doc = timeline([media(ID.A, 0, 100, { fadeOutFrames: 50 }), media(ID.B, 100, 100 + MAX_REVERSE_FRAMES + 1)], [media(ID.C, 0, 30, { hold: true })]);
    expect(() => apply(doc, { type: 'setProperty', itemId: ID.A, key: 'fadeInFrames', value: 60 })).toThrow(/fades/);
    expect(() => apply(doc, { type: 'setProperty', itemId: ID.B, key: 'reverse', value: true })).toThrow(/1800/);
    expect(() => apply(doc, { type: 'setProperty', itemId: ID.C, key: 'reverse', value: true })).toThrow(/reversed and held/);
    roundTrip(doc, { type: 'setProperty', itemId: ID.A, key: 'fadeInFrames', value: 50 });
  });

  it('a reversed clip cannot be trimmed past 1800 frames', () => {
    const { doc } = roundTrip(timeline([media(ID.A, 0, MAX_REVERSE_FRAMES - 10, { reverse: true, sourceIn: 500 })]), { type: 'trim', itemId: ID.A, edge: 'out', mode: 'ripple', deltaFrames: 100 });
    expect(media$(doc, ID.A).recordOut).toBe(MAX_REVERSE_FRAMES);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// D7 — transitions stay seated, or go
// ─────────────────────────────────────────────────────────────────────────────

describe('eng D7: an edit next to a transition removes or re-seats it', () => {
  const withTransition = () => timeline([media(ID.A, 0, 30), transition(ID.T, 30, 40), media(ID.B, 40, 60), media(ID.C, 60, 90)]);

  it('Delete (lift) of the incoming clip removes the transition', () => {
    const { doc } = roundTrip(withTransition(), { type: 'lift', itemId: ID.B });
    expect(spans(doc)).toEqual([[ID.A, 0, 30], [ID.C, 60, 90]]);
  });

  it('lift of the outgoing clip leaves the transition flush on the clip it still touches (valid: no gap pair)', () => {
    // The validator's rule is about neighbour PAIRS: with nothing before it, the transition opens
    // no gap, so it stays seated on the incoming clip and undo is a single re-insert.
    const { doc } = roundTrip(withTransition(), { type: 'lift', itemId: ID.A });
    expect(spans(doc)).toEqual([[ID.T, 30, 40], [ID.B, 40, 60], [ID.C, 60, 90]]);
  });

  it('nudging a neighbour off the boundary removes the transition', () => {
    const doc0 = timeline([media(ID.A, 10, 30), transition(ID.T, 30, 40), media(ID.B, 40, 60), media(ID.C, 60, 90)]);
    const { doc } = roundTrip(doc0, { type: 'nudge', itemId: ID.A, deltaFrames: -5 });
    expect(spans(doc)).toEqual([[ID.A, 5, 25], [ID.B, 40, 60], [ID.C, 60, 90]]);
  });

  it('ripple delete (extract) of the incoming clip re-seats the transition on the next clip', () => {
    const { doc } = roundTrip(withTransition(), { type: 'extract', itemId: ID.B });
    expect(spans(doc)).toEqual([[ID.A, 0, 30], [ID.T, 30, 40], [ID.C, 40, 70]]);
  });

  it('moving a neighbour to another track removes the transition', () => {
    const { doc } = roundTrip(withTransition(), { type: 'move', itemId: ID.B, toTrackId: ID.a, toRecordIn: 0 });
    expect(doc.tracks[0].items.some((i) => i.id === ID.T)).toBe(false);
  });

  it('sliding a transition flush on both sides is a no-op, not a transition off its cut', () => {
    const { doc, inverse } = roundTrip(withTransition(), { type: 'trim', itemId: ID.T, edge: 'out', mode: 'slide', deltaFrames: 3 });
    expect(doc).toEqual(withTransition());
    expect(inverse[0]).toMatchObject({ deltaFrames: 0 });
  });

  it('lifting the middle of back-to-back transitions undoes exactly (the track goes back whole)', () => {
    const doc0 = timeline([media(ID.A, 0, 30), transition(ID.T, 30, 40), transition(g(6), 40, 50), media(ID.B, 50, 60)]);
    const { doc, inverse } = roundTrip(doc0, { type: 'lift', itemId: ID.T });
    expect(spans(doc)).toEqual([[ID.A, 0, 30], [ID.B, 50, 60]]);
    expect(inverse.map((step) => step.type)).toEqual(['removeTrack', 'addTrack']);
  });

  it('a head trim (ripple in-edge) of the incoming clip keeps the transition seated', () => {
    const { doc } = roundTrip(withTransition(), { type: 'trim', itemId: ID.B, edge: 'in', mode: 'ripple', deltaFrames: 5 });
    expect(spans(doc)).toEqual([[ID.A, 0, 30], [ID.T, 30, 40], [ID.B, 40, 55], [ID.C, 55, 85]]);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// D13 / D14 / D15
// ─────────────────────────────────────────────────────────────────────────────

describe('eng D13: ripple in-edge trim keeps the clip start and pulls the followers', () => {
  it('shortening the head closes up: followers move left by the same frames', () => {
    const { doc } = roundTrip(timeline([media(ID.A, 0, 30), media(ID.B, 30, 60), media(ID.C, 60, 90)]), { type: 'trim', itemId: ID.B, edge: 'in', mode: 'ripple', deltaFrames: 10 });
    expect(spans(doc)).toEqual([[ID.A, 0, 30], [ID.B, 30, 50], [ID.C, 50, 80]]);
    expect(media$(doc, ID.B).sourceIn).toBe(110);
  });

  it('extending the head pushes the followers right even with no gap after it', () => {
    const { doc } = roundTrip(timeline([media(ID.A, 0, 30), media(ID.B, 30, 60), media(ID.C, 60, 90)]), { type: 'trim', itemId: ID.B, edge: 'in', mode: 'ripple', deltaFrames: -10 });
    expect(spans(doc)).toEqual([[ID.A, 0, 30], [ID.B, 30, 70], [ID.C, 70, 100]]);
    expect(media$(doc, ID.B).sourceIn).toBe(90);
  });
});

describe('eng D14: keyframes follow their clip', () => {
  const keyed = (recordIn: number) => ({ gainDb: { value: 0, keys: [{ frame: recordIn + 5, value: -6, interp: 'linear' as const }] }, transform: { opacity: { value: 1, keys: [{ frame: recordIn + 10, value: 0, interp: 'linear' as const }] } } });

  it('nudge and move shift the clip\'s keys with it', () => {
    const nudged = roundTrip(timeline([media(ID.A, 10, 40, keyed(10))]), { type: 'nudge', itemId: ID.A, deltaFrames: 7 }).doc;
    expect(media$(nudged, ID.A).gainDb?.keys?.[0].frame).toBe(22);
    expect(media$(nudged, ID.A).transform?.opacity?.keys?.[0].frame).toBe(27);
    const moved = roundTrip(timeline([media(ID.A, 10, 40, keyed(10))]), { type: 'move', itemId: ID.A, toTrackId: ID.v, toRecordIn: 100 }).doc;
    expect(media$(moved, ID.A).gainDb?.keys?.[0].frame).toBe(105);
  });

  it('a ripple (extract of the clip before) shifts the follower\'s keys', () => {
    const { doc } = roundTrip(timeline([media(ID.A, 0, 10), media(ID.B, 10, 40, keyed(10))]), { type: 'extract', itemId: ID.A });
    expect(media$(doc, ID.B).gainDb?.keys?.[0].frame).toBe(5);
  });

  it('closeGaps shifts the moved clip\'s keys', () => {
    const { doc } = roundTrip(timeline([media(ID.A, 0, 10), media(ID.B, 20, 50, keyed(20))]), { type: 'closeGaps', trackId: ID.v });
    expect(media$(doc, ID.B).gainDb?.keys?.[0].frame).toBe(15);
  });
});

describe('eng D15: a bladed grouped clip keeps its colour group', () => {
  it('the right half joins the left half\'s group, and undo restores the group', () => {
    const group: ColorGroup = { id: ID.group, name: 'Look A', clipIds: [ID.A, ID.C], grade: null };
    const doc = timeline([media(ID.A, 0, 100), media(ID.C, 100, 130)], [], { colorGroups: [group] });
    const { doc: after } = roundTrip(doc, { type: 'blade', itemId: ID.A, atFrame: 40, newItemId: ID.fresh });
    expect(after.colorGroups?.[0].clipIds).toEqual([ID.A, ID.fresh, ID.C]);
  });

  it('join of the two halves takes the right half back out of the group', () => {
    const group: ColorGroup = { id: ID.group, name: 'Look A', clipIds: [ID.A], grade: null };
    const bladed = apply(timeline([media(ID.A, 0, 100, { fadeInFrames: 5, fadeOutFrames: 5, playbackRate: 2 })], [], { colorGroups: [group] }), { type: 'blade', itemId: ID.A, atFrame: 40, newItemId: ID.fresh }).doc;
    const { doc } = roundTrip(bladed, { type: 'join', itemId: ID.A });
    expect(doc.colorGroups?.[0].clipIds).toEqual([ID.A]);
    expect(media$(doc, ID.A)).toMatchObject({ recordIn: 0, recordOut: 100, sourceIn: 100, fadeInFrames: 5, fadeOutFrames: 5 });
  });
});
