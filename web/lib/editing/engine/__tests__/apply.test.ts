/**
 * Worked examples for every op in the closed edit catalogue — the executable half of the
 * engine's contract.
 *
 * Three things are proven for each op, not just "it ran":
 *
 *   1. **The round trip.** `applyAll(apply(doc, op).doc, inverse)` deep-equals the document the op
 *      started from, with {@link assertInvariants} run after the op, after every intermediate
 *      inverse op, and at the end. This is what undo/redo and optimistic concurrency rest on, so
 *      it is asserted for every op rather than spot-checked.
 *   2. **The inverse stays inside the catalogue.** Every op in every returned program is one of
 *      the thirteen named ops, and its length obeys the published rule (1 for everything except
 *      `blade`'s 2, `deleteGap`/`closeGaps`' one `move` per affected item).
 *   3. **Purity.** The engine never writes to the document it was handed — proven both by a
 *      frozen document (a write would throw under ESM's strict mode) and by a snapshot comparison.
 *
 * The boundary and rejection cases the ticket names are covered explicitly at the bottom of each
 * op's block, including the hand-built invariant violators that keep `assertInvariants`
 * load-bearing rather than a function that always passes.
 */

import { describe, expect, it } from 'vitest';

import {
  apply,
  applyAll,
  assertInvariants as assertEngineInvariants,
  EditEngineError,
  SET_PROPERTY_ALLOWLIST,
  type EditOp,
} from '../index';
import type { Caption, EditTimeline, Item, Marker, MediaClip, TitleClip, Track } from '../../types';

/** These fixtures name things `v1`/`track-video`: every invariant except the Guid id shape (pinned in `apply.contract.test.ts`). */
const assertInvariants = (doc: Parameters<typeof assertEngineInvariants>[0]): void => assertEngineInvariants(doc, { ids: 'opaque' });

// ─────────────────────────────────────────────────────────────────────────────
// Compile-time pins on the catalogue's shape
//
// These are type-checked by `npx tsc --noEmit`, not by the runner: a deferred op smuggled into
// the union, or a field added to `Trim`, fails the build rather than a test.
// ─────────────────────────────────────────────────────────────────────────────

/** Every member the catalogue is allowed to have, written down exactly once. */
const NAMED_OPS = [
  'insert',
  'overwrite',
  'lift',
  'extract',
  'trim',
  'blade',
  'deleteGap',
  'closeGaps',
  'move',
  'setProperty',
  'addMarker',
  'setMarker',
  'removeMarker',
  'append',
  'replace',
  'fitToFill',
  'placeOnTop',
  'join',
  'nudge',
  'addTrack',
  'removeTrack',
  'moveTrack',
  'setTrackProperty',
  'setTimelineSettings',
  'setInOut',
  'setKeyframe',
  'removeKeyframe',
  'moveKeyframe',
  'retime',
  'addTransition',
  'removeTransition',
  'setColorGroup',
  'setSequence',
  'setBus',
] as const;

const namedOpTypes: readonly EditOp['type'][] = NAMED_OPS;

/** `never` only when the union has no member beyond {@link NAMED_OPS}. */
type UncoveredOpType = Exclude<EditOp['type'], (typeof NAMED_OPS)[number]>;
const everyOpIsNamed: UncoveredOpType extends never ? true : false = true;

/** `Trim`'s payload is exactly `{ itemId, edge, mode, deltaFrames }` — no more, no less. */
type TrimPayloadKeys = Exclude<keyof Extract<EditOp, { type: 'trim' }>, 'type'>;
type ExtraTrimKey = Exclude<TrimPayloadKeys, 'itemId' | 'edge' | 'mode' | 'deltaFrames'>;
type MissingTrimKey = Exclude<'itemId' | 'edge' | 'mode' | 'deltaFrames', TrimPayloadKeys>;
const trimPayloadIsExact: [ExtraTrimKey extends never ? true : false, MissingTrimKey extends never ? true : false] = [
  true,
  true,
];

// ─────────────────────────────────────────────────────────────────────────────
// Fixtures
// ─────────────────────────────────────────────────────────────────────────────

const TRACKS = { video: 'track-video', audio: 'track-audio', solo: 'track-solo', empty: 'track-empty' };

const assetRef = (n: number) => ({ kind: 'projectFile' as const, projectFileId: `file-${n}` });

const title = (id: string, recordIn: number, recordOut: number, text = id): TitleClip => ({
  id,
  type: 'titleClip',
  name: id,
  recordIn,
  recordOut,
  text,
  fontSize: 32,
  fontFamily: 'Inter',
  color: '#ffffff',
  enabled: true,
  provenance: null,
});

const media = (
  id: string,
  recordIn: number,
  recordOut: number,
  sourceIn?: number,
  extra: Partial<MediaClip> = {},
): MediaClip => ({
  id,
  type: 'mediaClip',
  name: id,
  recordIn,
  recordOut,
  assetRef: assetRef(1),
  ...(sourceIn === undefined ? {} : { sourceIn }),
  playbackRate: 1,
  enabled: true,
  provenance: null,
  ...extra,
});

const caption = (id: string, recordIn: number, recordOut: number, text = id): Caption => ({
  id,
  type: 'caption',
  name: id,
  recordIn,
  recordOut,
  text,
  language: 'en',
  enabled: true,
  provenance: null,
});

const track = (id: string, kind: Track['kind'], items: Item[], extra: Partial<Track> = {}): Track => ({
  id,
  kind,
  name: id,
  height: 64,
  locked: false,
  enabled: true,
  mute: false,
  solo: false,
  items,
  ...extra,
});

const marker = (id: string, frame: number, extra: Partial<Marker> = {}): Marker => ({
  id,
  frame,
  name: id,
  color: null,
  note: null,
  durationFrames: null,
  ...extra,
});

const settings: EditTimeline['settings'] = {
  width: 1920,
  height: 1080,
  rate: { num: 30, den: 1 },
  sampleRate: 48000,
  pixelAspect: { num: 1, den: 1 },
  aspectPolicy: 'fit',
  startTimecodeFrames: 0,
};

function timeline(tracks: Track[], markers: Marker[] = []): EditTimeline {
  return {
    schemaVersion: 2,
    id: 'timeline-1',
    projectId: 'project-1',
    name: 'Engine fixture',
    settings,
    tracks,
    markers,
    inOut: null,
    seed: null,
  };
}

/**
 * The main fixture: a video track with a contiguous pair, a gap, and a trailing caption; an audio
 * track of two adjacent media clips (both with a source offset, so roll/slide/slip have something
 * to move); and two markers.
 */
function baseDoc(): EditTimeline {
  return timeline(
    [
      track(TRACKS.video, 'video', [
        title('v1', 0, 10),
        media('v2', 10, 20, 5),
        caption('v3', 30, 40),
      ]),
      track(TRACKS.audio, 'audio', [media('a1', 0, 20, 2), media('a2', 20, 40, 3)]),
    ],
    [marker('m1', 5), marker('m2', 25)],
  );
}

/** A one-item track whose source offset is small — used for clamps that bind. */
function soloDoc(): EditTimeline {
  return timeline([track(TRACKS.solo, 'video', [media('s1', 50, 60, 2)])]);
}

/** An empty track, for the insert-into-nothing boundary. */
function emptyDoc(): EditTimeline {
  return timeline([track(TRACKS.empty, 'video', [])]);
}

// ─────────────────────────────────────────────────────────────────────────────
// Assertion helpers
// ─────────────────────────────────────────────────────────────────────────────

type Applied = { doc: EditTimeline; inverse: EditOp[] };

/**
 * Applies `op` to `doc` and proves the whole contract for it: invariants after the op, an
 * in-catalogue inverse program of the expected length, invariants after every inverse op, and
 * exact restoration — both step by step and through `applyAll`, which is the form the ticket
 * states. `doc` itself must be untouched.
 */
function roundTrip(doc: EditTimeline, op: EditOp, expectedInverseLength?: number): Applied {
  const before = structuredClone(doc);
  const applied = apply(doc, op);

  expect(doc).toEqual(before);

  assertInvariants(applied.doc);
  expect(applied.doc).not.toBe(doc);

  for (const inverseOp of applied.inverse) {
    expect(NAMED_OPS).toContain(inverseOp.type);
  }
  if (expectedInverseLength !== undefined) {
    expect(applied.inverse).toHaveLength(expectedInverseLength);
  }

  let stepwise = applied.doc;
  for (const inverseOp of applied.inverse) {
    stepwise = apply(stepwise, inverseOp).doc;
    assertInvariants(stepwise);
  }
  expect(stepwise).toEqual(before);

  const restored = applyAll(applied.doc, applied.inverse);
  assertInvariants(restored);
  expect(restored).toEqual(before);

  return applied;
}

/** Asserts that the op is refused, and that the document is left alone. */
function expectRefused(doc: EditTimeline, op: EditOp): EditEngineError {
  const before = structuredClone(doc);
  let caught: unknown;
  try {
    apply(doc, op);
  } catch (error) {
    caught = error;
  }
  expect(caught).toBeInstanceOf(EditEngineError);
  expect(doc).toEqual(before);
  return caught as EditEngineError;
}

function itemOn(doc: EditTimeline, trackId: string, itemId: string): Item {
  const found = doc.tracks.find((candidate) => candidate.id === trackId)?.items.find((item) => item.id === itemId);
  if (found === undefined) throw new Error(`fixture is missing ${trackId}/${itemId}`);
  return found;
}

// ─────────────────────────────────────────────────────────────────────────────
// The catalogue itself
// ─────────────────────────────────────────────────────────────────────────────

describe('the closed op catalogue', () => {
  it('has exactly the thirty-four named members (thirteen P1 + twelve P4 + six P5 + one P7 + two P8) and nothing else', () => {
    expect(everyOpIsNamed).toBe(true);
    expect(trimPayloadIsExact).toEqual([true, true]);
    expect(namedOpTypes).toHaveLength(34);
    expect([...namedOpTypes].sort()).toEqual([...NAMED_OPS].sort());
  });

  it('rejects an op type that is not a member, at runtime as well as at compile time', () => {
    const smuggled = { type: 'applyProposal' } as unknown as EditOp;
    expect(() => apply(baseDoc(), smuggled)).toThrow(EditEngineError);
  });

  it('reports refusals as EditEngineError carrying the offending op', () => {
    const error = expectRefused(baseDoc(), { type: 'lift', itemId: 'nope' });
    expect(error.name).toBe('EditEngineError');
    expect(error.op).toBe('lift');
    expect(error.message).toContain('nope');
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// Purity
// ─────────────────────────────────────────────────────────────────────────────

describe('purity', () => {
  const deepFreeze = <T,>(value: T): T => {
    if (value !== null && typeof value === 'object') {
      Object.values(value as Record<string, unknown>).forEach(deepFreeze);
      Object.freeze(value);
    }
    return value;
  };

  it('never writes to the input document, nor to anything reachable from it', () => {
    const doc = deepFreeze(baseDoc());
    const snapshot = structuredClone(doc);

    // A write to a frozen object throws in strict mode, so a successful pass over a broad set of
    // ops is itself the assertion; the snapshot then covers what freezing cannot.
    expect(() => apply(doc, { type: 'insert', trackId: TRACKS.video, index: 2, item: title('vx', 22, 27) })).not.toThrow();
    expect(() => apply(doc, { type: 'overwrite', itemId: 'v3', item: caption('v3', 30, 36) })).not.toThrow();
    expect(() => apply(doc, { type: 'lift', itemId: 'v2' })).not.toThrow();
    expect(() => apply(doc, { type: 'extract', itemId: 'v1' })).not.toThrow();
    expect(() => apply(doc, { type: 'trim', itemId: 'v1', edge: 'out', mode: 'ripple', deltaFrames: 4 })).not.toThrow();
    expect(() => apply(doc, { type: 'blade', itemId: 'v2', atFrame: 14, newItemId: 'v2b' })).not.toThrow();
    expect(() => apply(doc, { type: 'deleteGap', trackId: TRACKS.video, atFrame: 25 })).not.toThrow();
    expect(() => apply(doc, { type: 'closeGaps', trackId: TRACKS.video })).not.toThrow();
    expect(() => apply(doc, { type: 'move', itemId: 'v3', toTrackId: TRACKS.video, toRecordIn: 22 })).not.toThrow();
    expect(() => apply(doc, { type: 'setProperty', itemId: 'v1', key: 'text', value: 'hi' })).not.toThrow();
    expect(() => apply(doc, { type: 'addMarker', marker: marker('m3', 15) })).not.toThrow();
    expect(() => apply(doc, { type: 'setMarker', markerId: 'm1', patch: { name: 'renamed' } })).not.toThrow();
    expect(() => apply(doc, { type: 'removeMarker', markerId: 'm1' })).not.toThrow();

    expect(doc).toEqual(snapshot);
  });

  it('returns objects that share no reference with the input', () => {
    const doc = baseDoc();
    const applied = apply(doc, { type: 'setProperty', itemId: 'v1', key: 'name', value: 'renamed' });

    expect(applied.doc).not.toBe(doc);
    expect(applied.doc.tracks).not.toBe(doc.tracks);
    expect(itemOn(applied.doc, TRACKS.video, 'v1')).not.toBe(itemOn(doc, TRACKS.video, 'v1'));
    expect(applied.doc.settings).not.toBe(doc.settings);
  });

  it('does not alias an inverse payload to the caller-supplied op payload', () => {
    const payload = title('vx', 22, 27);
    const applied = apply(baseDoc(), { type: 'insert', trackId: TRACKS.video, index: 2, item: payload });
    expect(itemOn(applied.doc, TRACKS.video, 'vx')).not.toBe(payload);
  });

  it('applyAll with an empty program returns a deep-equal document', () => {
    const doc = baseDoc();
    expect(applyAll(doc, [])).toEqual(doc);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// insert
// ─────────────────────────────────────────────────────────────────────────────

describe('insert', () => {
  it('fits into a gap, ripples nothing, and undoes with a single lift', () => {
    const { doc, inverse } = roundTrip(
      baseDoc(),
      { type: 'insert', trackId: TRACKS.video, index: 2, item: title('vx', 22, 27) },
      1,
    );

    expect(inverse[0]).toEqual({ type: 'lift', itemId: 'vx' });
    const items = doc.tracks[0].items;
    expect(items.map((item) => [item.id, item.recordIn, item.recordOut])).toEqual([
      ['v1', 0, 10],
      ['v2', 10, 20],
      ['vx', 22, 27],
      ['v3', 30, 40],
    ]);
  });

  it('landing flush at the following item ripples by the item duration and undoes with extract', () => {
    const { doc, inverse } = roundTrip(
      baseDoc(),
      { type: 'insert', trackId: TRACKS.video, index: 2, item: title('vx', 30, 35) },
      1,
    );

    expect(inverse[0]).toEqual({ type: 'extract', itemId: 'vx' });
    expect(doc.tracks[0].items.map((item) => [item.id, item.recordIn, item.recordOut])).toEqual([
      ['v1', 0, 10],
      ['v2', 10, 20],
      ['vx', 30, 35],
      ['v3', 35, 45],
    ]);
  });

  it('ripples the whole track when inserted flush at index 0', () => {
    const { doc, inverse } = roundTrip(
      baseDoc(),
      { type: 'insert', trackId: TRACKS.video, index: 0, item: title('v0', 0, 5) },
      1,
    );

    expect(inverse[0]).toEqual({ type: 'extract', itemId: 'v0' });
    expect(doc.tracks[0].items.map((item) => [item.id, item.recordIn, item.recordOut])).toEqual([
      ['v0', 0, 5],
      ['v1', 5, 15],
      ['v2', 15, 25],
      ['v3', 35, 45],
    ]);
  });

  it('refuses a partial overlap with the following item rather than destroying its own inverse', () => {
    const error = expectRefused(baseDoc(), {
      type: 'insert',
      trackId: TRACKS.video,
      index: 2,
      item: title('vx', 25, 35),
    });
    expect(error.message).toContain('partially overlaps');
  });

  it('inserts at index 0 and at items.length, and into an empty track', () => {
    const atHead = roundTrip(emptyDoc(), {
      type: 'insert',
      trackId: TRACKS.empty,
      index: 0,
      item: title('e1', 0, 5),
    });
    expect(atHead.doc.tracks[0].items.map((item) => item.id)).toEqual(['e1']);

    const atTail = roundTrip(baseDoc(), {
      type: 'insert',
      trackId: TRACKS.video,
      index: 3,
      item: title('vx', 40, 45),
    });
    expect(atTail.doc.tracks[0].items.map((item) => item.id)).toEqual(['v1', 'v2', 'v3', 'vx']);
    expect(atTail.inverse[0]).toEqual({ type: 'lift', itemId: 'vx' });

    const intoSingleItemTrack = roundTrip(soloDoc(), {
      type: 'insert',
      trackId: TRACKS.solo,
      index: 0,
      item: media('s0', 40, 50, 0),
    });
    expect(intoSingleItemTrack.doc.tracks[0].items.map((item) => item.id)).toEqual(['s0', 's1']);
  });

  it('refuses a non-canonical index, an unknown track, a locked track and a duplicate id', () => {
    const op = (index: number): EditOp => ({
      type: 'insert',
      trackId: TRACKS.video,
      index,
      item: title('vx', 22, 27),
    });

    expect(expectRefused(baseDoc(), op(0)).message).toContain('not canonical');
    expect(expectRefused(baseDoc(), op(3)).message).toContain('not canonical');
    expect(
      expectRefused(baseDoc(), {
        type: 'insert',
        trackId: 'nope',
        index: 2,
        item: title('vx', 22, 27),
      }).message,
    ).toContain('unknown track');
    expect(
      expectRefused(
        timeline([track(TRACKS.video, 'video', [title('v1', 0, 10)], { locked: true })]),
        { type: 'insert', trackId: TRACKS.video, index: 1, item: title('vx', 10, 15) },
      ).message,
    ).toContain('locked');
    expect(
      expectRefused(baseDoc(), {
        type: 'insert',
        trackId: TRACKS.video,
        index: 0,
        item: title('v1', 0, 5),
      }).message,
    ).toContain('already in use');
  });

  it('refuses an item that would overlap the preceding item, and a malformed item', () => {
    expect(
      expectRefused(baseDoc(), {
        type: 'insert',
        trackId: TRACKS.video,
        index: 2,
        item: title('vx', 18, 25),
      }).message,
    ).toContain('overlap the preceding item');

    expect(
      expectRefused(emptyDoc(), {
        type: 'insert',
        trackId: TRACKS.empty,
        index: 0,
        item: { ...title('ex', 0, 5), recordOut: 0 } as Item,
      }).message,
    ).toContain('recordOut');

    expect(
      expectRefused(emptyDoc(), {
        type: 'insert',
        trackId: TRACKS.empty,
        index: 0,
        item: { id: 'ex', type: 'mediaClip', name: 'ex', recordIn: 0, recordOut: 5 } as Item,
      }).message,
    ).toContain('assetRef');
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// overwrite
// ─────────────────────────────────────────────────────────────────────────────

describe('overwrite', () => {
  it('replaces content and is exactly self-inverse', () => {
    const { doc, inverse } = roundTrip(baseDoc(), {
      type: 'overwrite',
      itemId: 'v2',
      item: media('v2', 12, 20, 9, { name: 'B retimed' }),
    });

    expect(inverse).toHaveLength(1);
    expect(inverse[0].type).toBe('overwrite');
    expect(itemOn(doc, TRACKS.video, 'v2')).toMatchObject({ recordIn: 12, recordOut: 20, sourceIn: 9 });
  });

  it('works on the first and the last item of a track', () => {
    roundTrip(baseDoc(), { type: 'overwrite', itemId: 'v1', item: title('v1', 0, 6, 'shorter') });
    roundTrip(baseDoc(), { type: 'overwrite', itemId: 'v3', item: caption('v3', 32, 40, 'moved') });
  });

  it('refuses a changed id, an unknown id, a locked track and an overlapping span', () => {
    expect(
      expectRefused(baseDoc(), { type: 'overwrite', itemId: 'v2', item: media('other', 10, 20, 5) }).message,
    ).toContain('must keep the item id');

    expect(expectRefused(baseDoc(), { type: 'overwrite', itemId: 'nope', item: media('nope', 0, 5, 0) }).message).toContain(
      'unknown item id',
    );

    expect(
      expectRefused(
        timeline([track(TRACKS.video, 'video', [media('v2', 10, 20, 5)], { locked: true })]),
        { type: 'overwrite', itemId: 'v2', item: media('v2', 10, 18, 5) },
      ).message,
    ).toContain('locked');

    expect(
      expectRefused(baseDoc(), { type: 'overwrite', itemId: 'v1', item: title('v1', 0, 15) }).message,
    ).toContain('overlapping the following item');
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// lift
// ─────────────────────────────────────────────────────────────────────────────

describe('lift', () => {
  it('removes an item, leaves the gap, and undoes with a single insert', () => {
    const { doc, inverse } = roundTrip(baseDoc(), { type: 'lift', itemId: 'v2' }, 1);

    expect(inverse).toEqual([
      { type: 'insert', trackId: TRACKS.video, index: 1, item: expect.objectContaining({ id: 'v2' }) },
    ]);
    expect(doc.tracks[0].items.map((item) => item.id)).toEqual(['v1', 'v3']);
    expect(itemOn(doc, TRACKS.video, 'v3').recordIn).toBe(30);
  });

  it('lifts the first and the last item of a track, and the only item of a single-item track', () => {
    roundTrip(baseDoc(), { type: 'lift', itemId: 'v1' });
    roundTrip(baseDoc(), { type: 'lift', itemId: 'v3' });
    const emptied = roundTrip(soloDoc(), { type: 'lift', itemId: 's1' });
    expect(emptied.doc.tracks[0].items).toEqual([]);
  });

  it('refuses an unknown id and a locked track', () => {
    expect(expectRefused(baseDoc(), { type: 'lift', itemId: 'nope' }).message).toContain('unknown item id');
    expect(
      expectRefused(timeline([track(TRACKS.video, 'video', [media('v2', 10, 20, 5)], { locked: true })]), {
        type: 'lift',
        itemId: 'v2',
      }).message,
    ).toContain('locked');
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// extract
// ─────────────────────────────────────────────────────────────────────────────

describe('extract', () => {
  it('ripples the following items left when the removed item was adjacent', () => {
    const { doc, inverse } = roundTrip(baseDoc(), { type: 'extract', itemId: 'v1' }, 1);

    expect(inverse[0].type).toBe('insert');
    expect(doc.tracks[0].items.map((item) => [item.id, item.recordIn, item.recordOut])).toEqual([
      ['v2', 0, 10],
      ['v3', 20, 30],
    ]);
  });

  it('leaves the following items alone when the removed item was already gapped', () => {
    const { doc } = roundTrip(baseDoc(), { type: 'extract', itemId: 'v2' }, 1);

    expect(doc.tracks[0].items.map((item) => [item.id, item.recordIn, item.recordOut])).toEqual([
      ['v1', 0, 10],
      ['v3', 30, 40],
    ]);
  });

  it('extracts the last item and the only item of a single-item track', () => {
    roundTrip(baseDoc(), { type: 'extract', itemId: 'v3' });
    const emptied = roundTrip(soloDoc(), { type: 'extract', itemId: 's1' });
    expect(emptied.doc.tracks[0].items).toEqual([]);
  });

  it('refuses an unknown id and a locked track', () => {
    expect(expectRefused(baseDoc(), { type: 'extract', itemId: 'nope' }).message).toContain('unknown item id');
    expect(
      expectRefused(timeline([track(TRACKS.video, 'video', [media('v2', 10, 20, 5)], { locked: true })]), {
        type: 'extract',
        itemId: 'v2',
      }).message,
    ).toContain('locked');
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// trim — the four modes
// ─────────────────────────────────────────────────────────────────────────────

describe('trim', () => {
  it('ripple moves the out edge and shifts every following item by the same delta', () => {
    const { doc, inverse } = roundTrip(baseDoc(), {
      type: 'trim',
      itemId: 'v1',
      edge: 'out',
      mode: 'ripple',
      deltaFrames: 5,
    });

    expect(inverse[0]).toEqual({
      type: 'trim',
      itemId: 'v1',
      edge: 'out',
      mode: 'ripple',
      deltaFrames: -5,
    });
    expect(doc.tracks[0].items.map((item) => [item.id, item.recordIn, item.recordOut])).toEqual([
      ['v1', 0, 15],
      ['v2', 15, 25],
      ['v3', 35, 45],
    ]);
  });

  it('ripple on the in edge trims the head in place (sourceIn follows) and pulls the following items EARLIER (eng D3)', () => {
    const { doc } = roundTrip(baseDoc(), {
      type: 'trim',
      itemId: 'v2',
      edge: 'in',
      mode: 'ripple',
      deltaFrames: 3,
    });

    // The clip keeps its start; 3 frames of head are gone, so everything after it closes up by 3.
    expect(itemOn(doc, TRACKS.video, 'v2')).toMatchObject({ recordIn: 10, recordOut: 17, sourceIn: 8 });
    expect(itemOn(doc, TRACKS.video, 'v3').recordIn).toBe(27);
  });

  it('roll moves the shared boundary with the neighbour, whose source offset follows', () => {
    const out = roundTrip(baseDoc(), {
      type: 'trim',
      itemId: 'a1',
      edge: 'out',
      mode: 'roll',
      deltaFrames: 4,
    });
    expect(itemOn(out.doc, TRACKS.audio, 'a1').recordOut).toBe(24);
    expect(itemOn(out.doc, TRACKS.audio, 'a2')).toMatchObject({ recordIn: 24, recordOut: 40, sourceIn: 7 });

    const inbound = roundTrip(baseDoc(), {
      type: 'trim',
      itemId: 'a2',
      edge: 'in',
      mode: 'roll',
      deltaFrames: 5,
    });
    expect(itemOn(inbound.doc, TRACKS.audio, 'a2')).toMatchObject({ recordIn: 25, recordOut: 40, sourceIn: 8 });
    expect(itemOn(inbound.doc, TRACKS.audio, 'a1').recordOut).toBe(25);
  });

  it('roll throws when there is no neighbour on the named edge', () => {
    expect(
      expectRefused(baseDoc(), { type: 'trim', itemId: 'v1', edge: 'in', mode: 'roll', deltaFrames: 2 }).message,
    ).toContain('needs a preceding item');
    expect(
      expectRefused(baseDoc(), { type: 'trim', itemId: 'v3', edge: 'out', mode: 'roll', deltaFrames: 2 }).message,
    ).toContain('needs a following item');
  });

  it('slip changes only sourceIn, and refuses an item that has no source offset', () => {
    const { doc } = roundTrip(baseDoc(), {
      type: 'trim',
      itemId: 'a2',
      edge: 'in',
      mode: 'slip',
      deltaFrames: 6,
    });

    expect(itemOn(doc, TRACKS.audio, 'a2')).toMatchObject({ recordIn: 20, recordOut: 40, sourceIn: 9 });

    expect(
      expectRefused(baseDoc(), { type: 'trim', itemId: 'v1', edge: 'in', mode: 'slip', deltaFrames: 2 }).message,
    ).toContain('carries none');
  });

  it('slide moves the whole span and the named neighbour absorbs it', () => {
    const out = roundTrip(baseDoc(), {
      type: 'trim',
      itemId: 'a1',
      edge: 'out',
      mode: 'slide',
      deltaFrames: 5,
    });
    expect(itemOn(out.doc, TRACKS.audio, 'a1')).toMatchObject({ recordIn: 5, recordOut: 25, sourceIn: 2 });
    expect(itemOn(out.doc, TRACKS.audio, 'a2')).toMatchObject({ recordIn: 25, recordOut: 40, sourceIn: 8 });

    const inbound = roundTrip(baseDoc(), {
      type: 'trim',
      itemId: 'a2',
      edge: 'in',
      mode: 'slide',
      deltaFrames: -5,
    });
    expect(itemOn(inbound.doc, TRACKS.audio, 'a2')).toMatchObject({ recordIn: 15, recordOut: 35 });
    expect(itemOn(inbound.doc, TRACKS.audio, 'a1').recordOut).toBe(15);
  });

  it('slide TRANSLATES the neighbour edge rather than snapping it, so a gap survives the round trip', () => {
    // v2 ends at 20 and v3 starts at 30, so the slide happens across a ten-frame gap.
    const { doc } = roundTrip(baseDoc(), {
      type: 'trim',
      itemId: 'v3',
      edge: 'in',
      mode: 'slide',
      deltaFrames: -5,
    });

    expect(itemOn(doc, TRACKS.video, 'v3')).toMatchObject({ recordIn: 25, recordOut: 35 });
    // snapped would have been 25, swallowing the gap and losing the information undo needs
    expect(itemOn(doc, TRACKS.video, 'v2').recordOut).toBe(15);
  });

  it('slide throws when there is no neighbour on the named edge', () => {
    expect(
      expectRefused(baseDoc(), { type: 'trim', itemId: 'v3', edge: 'out', mode: 'slide', deltaFrames: 2 }).message,
    ).toContain('needs a following item');
    expect(
      expectRefused(baseDoc(), { type: 'trim', itemId: 'v1', edge: 'in', mode: 'slide', deltaFrames: 2 }).message,
    ).toContain('needs a preceding item');
  });

  it('clamps to the duration floor and still round-trips on the effective delta', () => {
    const { doc, inverse } = roundTrip(baseDoc(), {
      type: 'trim',
      itemId: 'v1',
      edge: 'out',
      mode: 'ripple',
      deltaFrames: -100,
    });

    // duration would have gone negative; it lands on the floor of one frame instead.
    expect(itemOn(doc, TRACKS.video, 'v1')).toMatchObject({ recordIn: 0, recordOut: 1 });
    expect(itemOn(doc, TRACKS.video, 'v2').recordIn).toBe(1);
    expect(inverse[0]).toEqual({
      type: 'trim',
      itemId: 'v1',
      edge: 'out',
      mode: 'ripple',
      deltaFrames: 9,
    });
  });

  it('clamps to the sourceIn floor and still round-trips', () => {
    const { doc, inverse } = roundTrip(soloDoc(), {
      type: 'trim',
      itemId: 's1',
      edge: 'in',
      mode: 'ripple',
      deltaFrames: -5,
    });

    // Extending the head of a ripple-in keeps the start and grows the clip by what the source allows.
    expect(itemOn(doc, TRACKS.solo, 's1')).toMatchObject({ recordIn: 50, recordOut: 62, sourceIn: 0 });
    expect(inverse[0]).toEqual({
      type: 'trim',
      itemId: 's1',
      edge: 'in',
      mode: 'ripple',
      deltaFrames: 2,
    });
  });

  it('clamps a roll/slide request to what the neighbour can give, and a slip to its own source', () => {
    const roll = roundTrip(baseDoc(), {
      type: 'trim',
      itemId: 'a1',
      edge: 'out',
      mode: 'roll',
      deltaFrames: -100,
    });
    // a2 may give back at most its own source offset (3) before sourceIn would go negative.
    expect(itemOn(roll.doc, TRACKS.audio, 'a2')).toMatchObject({ sourceIn: 0, recordIn: 17 });
    expect(roll.inverse[0]).toMatchObject({ deltaFrames: 3 });

    const slide = roundTrip(baseDoc(), {
      type: 'trim',
      itemId: 'a1',
      edge: 'out',
      mode: 'slide',
      deltaFrames: -100,
    });
    expect(itemOn(slide.doc, TRACKS.audio, 'a1').recordIn).toBe(0);
    expect(slide.inverse[0]).toMatchObject({ deltaFrames: 0 });

    const slip = roundTrip(baseDoc(), {
      type: 'trim',
      itemId: 'a2',
      edge: 'in',
      mode: 'slip',
      deltaFrames: -100,
    });
    expect((itemOn(slip.doc, TRACKS.audio, 'a2') as MediaClip).sourceIn).toBe(0);
    expect(slip.inverse[0]).toMatchObject({ deltaFrames: 3 });
  });

  it('treats a fully-clamped delta as a no-op that still round-trips', () => {
    const atSourceStart = (): EditTimeline => timeline([track(TRACKS.solo, 'video', [media('s1', 50, 60, 0)])]);
    const { doc, inverse } = roundTrip(atSourceStart(), {
      type: 'trim',
      itemId: 's1',
      edge: 'in',
      mode: 'ripple',
      deltaFrames: -5,
    });

    expect(doc).toEqual(atSourceStart());
    expect(inverse[0]).toMatchObject({ deltaFrames: 0 });
  });

  it('works on the first and the last item of a track', () => {
    roundTrip(baseDoc(), { type: 'trim', itemId: 'v1', edge: 'out', mode: 'ripple', deltaFrames: 3 });
    roundTrip(baseDoc(), { type: 'trim', itemId: 'v3', edge: 'out', mode: 'ripple', deltaFrames: 3 });
  });

  it('refuses an unknown item, a locked track and a non-integer delta', () => {
    expect(expectRefused(baseDoc(), { type: 'trim', itemId: 'nope', edge: 'out', mode: 'ripple', deltaFrames: 1 }).message).toContain(
      'unknown item id',
    );
    expect(
      expectRefused(timeline([track(TRACKS.video, 'video', [media('v2', 10, 20, 5)], { locked: true })]), {
        type: 'trim',
        itemId: 'v2',
        edge: 'out',
        mode: 'ripple',
        deltaFrames: 1,
      }).message,
    ).toContain('locked');
    expect(
      expectRefused(baseDoc(), { type: 'trim', itemId: 'v1', edge: 'out', mode: 'ripple', deltaFrames: 1.5 }).message,
    ).toContain('integer');
  });
});

describe('source arithmetic at extreme playback rates (security OE-1)', () => {
  // The frame arithmetic clamps the rate to the renderer's [0.1, 10]
  // (RegionFilterGraphBuilder.MinPlaybackRate / MaxPlaybackRate); a rate the invariants refuse
  // (0, NaN, Infinity) reads as 1. A tiny positive rate used to spin `framesWithin` forever.
  const RATES = [1e-12, 5e-324, 0, Number.NaN, Number.POSITIVE_INFINITY, 1e6];
  const soloAt = (rate: number, sourceIn: number, extra: Partial<MediaClip> = {}): EditTimeline =>
    timeline([track(TRACKS.solo, 'video', [media('s1', 50, 60, sourceIn, { playbackRate: rate, ...extra })])]);
  const isValidRate = (rate: number): boolean => Number.isFinite(rate) && rate > 0;
  const run = (doc: EditTimeline, op: EditOp, rate: number): Applied => (isValidRate(rate) ? roundTrip(doc, op) : apply(doc, op));
  const sourceInOf = (doc: EditTimeline): number => (itemOn(doc, TRACKS.solo, 's1') as MediaClip).sourceIn ?? 0;

  it.each(RATES)('slip, head trim and reversed tail trim terminate with a sane sourceIn at rate %s', (rate) => {
    const ops: Array<[EditTimeline, EditOp]> = [
      [soloAt(rate, 2), { type: 'trim', itemId: 's1', edge: 'in', mode: 'slip', deltaFrames: -100 }],
      [soloAt(rate, 2), { type: 'trim', itemId: 's1', edge: 'in', mode: 'ripple', deltaFrames: -100 }],
      [soloAt(rate, 2, { reverse: true }), { type: 'trim', itemId: 's1', edge: 'out', mode: 'ripple', deltaFrames: 100 }],
    ];
    for (const [doc, op] of ops) {
      const sourceIn = sourceInOf(run(doc, op, rate).doc);
      expect(Number.isInteger(sourceIn)).toBe(true);
      expect(sourceIn).toBeGreaterThanOrEqual(0);
    }
  });

  it('reads a tiny rate as 0.1 and a huge one as 10, like the renderer', () => {
    // 0.1: 24 frames are round(2.4) = 2 source frames, the whole offset.
    const tiny = roundTrip(soloAt(1e-12, 2), { type: 'trim', itemId: 's1', edge: 'in', mode: 'slip', deltaFrames: -100 });
    expect(sourceInOf(tiny.doc)).toBe(0);
    expect(tiny.inverse[0]).toMatchObject({ deltaFrames: 24 });

    // 10: 2 frames are 20 source frames; a third would need 30 of the 25 there are.
    const huge = roundTrip(soloAt(1e6, 25), { type: 'trim', itemId: 's1', edge: 'in', mode: 'slip', deltaFrames: -100 });
    expect(sourceInOf(huge.doc)).toBe(5);
    expect(huge.inverse[0]).toMatchObject({ deltaFrames: 2 });
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// blade
// ─────────────────────────────────────────────────────────────────────────────

describe('blade', () => {
  it('splits strictly inside the span and undoes with a two-op program', () => {
    const { doc, inverse } = roundTrip(
      baseDoc(),
      { type: 'blade', itemId: 'v2', atFrame: 14, newItemId: 'v2b' },
      2,
    );

    expect(inverse.map((op) => op.type)).toEqual(['lift', 'overwrite']);
    expect(inverse[0]).toEqual({ type: 'lift', itemId: 'v2b' });
    expect(itemOn(doc, TRACKS.video, 'v2')).toMatchObject({ recordIn: 10, recordOut: 14, sourceIn: 5 });
    expect(itemOn(doc, TRACKS.video, 'v2b')).toMatchObject({ recordIn: 14, recordOut: 20, sourceIn: 9 });
    // every other property is copied to the right half
    expect(itemOn(doc, TRACKS.video, 'v2b').name).toBe(itemOn(doc, TRACKS.video, 'v2').name);
    expect(doc.tracks[0].items.map((item) => item.id)).toEqual(['v1', 'v2', 'v2b', 'v3']);
  });

  it('blades the first item and the last item of a track', () => {
    roundTrip(baseDoc(), { type: 'blade', itemId: 'v1', atFrame: 4, newItemId: 'v1b' });
    roundTrip(baseDoc(), { type: 'blade', itemId: 'v3', atFrame: 35, newItemId: 'v3b' });
  });

  it('blades an item that has no source offset without inventing one', () => {
    const { doc } = roundTrip(baseDoc(), { type: 'blade', itemId: 'v1', atFrame: 4, newItemId: 'v1b' });
    expect(itemOn(doc, TRACKS.video, 'v1b')).not.toHaveProperty('sourceIn');
  });

  it('refuses a split outside the span and a duplicate new id', () => {
    expect(
      expectRefused(baseDoc(), { type: 'blade', itemId: 'v2', atFrame: 10, newItemId: 'x' }).message,
    ).toContain('strictly inside');
    expect(
      expectRefused(baseDoc(), { type: 'blade', itemId: 'v2', atFrame: 20, newItemId: 'x' }).message,
    ).toContain('strictly inside');
    expect(
      expectRefused(baseDoc(), { type: 'blade', itemId: 'v2', atFrame: 14, newItemId: 'v1' }).message,
    ).toContain('already in use');
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// deleteGap / closeGaps
// ─────────────────────────────────────────────────────────────────────────────

describe('deleteGap', () => {
  it('deletes the gap between two items, shifting the later ones left, and undoes with moves', () => {
    const { doc, inverse } = roundTrip(
      baseDoc(),
      { type: 'deleteGap', trackId: TRACKS.video, atFrame: 25 },
      1,
    );

    expect(inverse).toEqual([
      { type: 'move', itemId: 'v3', toTrackId: TRACKS.video, toRecordIn: 30 },
    ]);
    expect(itemOn(doc, TRACKS.video, 'v3').recordIn).toBe(20);
    expect(itemOn(doc, TRACKS.video, 'v3').recordOut).toBe(30);
  });

  it('deletes the gap before the first item', () => {
    const doc = timeline([track(TRACKS.video, 'video', [title('v1', 10, 20)])]);
    const { doc: applied } = roundTrip(doc, { type: 'deleteGap', trackId: TRACKS.video, atFrame: 5 }, 1);
    expect(itemOn(applied, TRACKS.video, 'v1').recordIn).toBe(0);
  });

  it('returns one move per shifted item, rightmost first', () => {
    const doc = timeline([
      track(TRACKS.video, 'video', [title('v1', 0, 10), title('v2', 20, 30), title('v3', 50, 60)]),
    ]);
    const { doc: applied, inverse } = roundTrip(
      doc,
      { type: 'deleteGap', trackId: TRACKS.video, atFrame: 12 },
      2,
    );

    expect(inverse).toEqual([
      { type: 'move', itemId: 'v3', toTrackId: TRACKS.video, toRecordIn: 50 },
      { type: 'move', itemId: 'v2', toTrackId: TRACKS.video, toRecordIn: 20 },
    ]);
    expect(applied.tracks[0].items.map((item) => [item.recordIn, item.recordOut])).toEqual([
      [0, 10],
      [10, 20],
      [40, 50],
    ]);
  });

  it('refuses a frame that is not inside a gap, an unknown track and a locked track', () => {
    expect(
      expectRefused(baseDoc(), { type: 'deleteGap', trackId: TRACKS.video, atFrame: 5 }).message,
    ).toContain('not inside a gap');
    // the trailing space after the last item is unbounded and therefore not a gap
    expect(
      expectRefused(baseDoc(), { type: 'deleteGap', trackId: TRACKS.video, atFrame: 40 }).message,
    ).toContain('not inside a gap');
    expect(
      expectRefused(baseDoc(), { type: 'deleteGap', trackId: 'nope', atFrame: 25 }).message,
    ).toContain('unknown track');
    expect(
      expectRefused(
        timeline([track(TRACKS.video, 'video', [title('v1', 0, 10), title('v2', 20, 30)], { locked: true })]),
        { type: 'deleteGap', trackId: TRACKS.video, atFrame: 15 },
      ).message,
    ).toContain('locked');
  });
});

describe('closeGaps', () => {
  it('closes every interior gap, keeping the first item where it is', () => {
    const doc = timeline([
      track(TRACKS.video, 'video', [title('v1', 10, 20), title('v2', 30, 40), title('v3', 60, 70)]),
    ]);
    const { doc: applied, inverse } = roundTrip(doc, { type: 'closeGaps', trackId: TRACKS.video }, 2);

    expect(applied.tracks[0].items.map((item) => [item.recordIn, item.recordOut])).toEqual([
      [10, 20],
      [20, 30],
      [30, 40],
    ]);
    expect(inverse.map((op) => op.type)).toEqual(['move', 'move']);
    expect(inverse[0]).toMatchObject({ itemId: 'v3', toRecordIn: 60 });
  });

  it('is a no-op on a track with no gaps, with an empty inverse', () => {
    const doc = baseDoc();
    const { doc: applied, inverse } = roundTrip(doc, { type: 'closeGaps', trackId: TRACKS.audio });

    expect(inverse).toEqual([]);
    expect(applied).toEqual(doc);
  });

  it('is a no-op on a single-item track and on an empty track', () => {
    expect(roundTrip(soloDoc(), { type: 'closeGaps', trackId: TRACKS.solo }).inverse).toEqual([]);
    expect(roundTrip(emptyDoc(), { type: 'closeGaps', trackId: TRACKS.empty }).inverse).toEqual([]);
  });

  it('refuses an unknown track and a locked track', () => {
    expect(expectRefused(baseDoc(), { type: 'closeGaps', trackId: 'nope' }).message).toContain('unknown track');
    expect(
      expectRefused(
        timeline([track(TRACKS.video, 'video', [title('v1', 0, 10), title('v2', 20, 30)], { locked: true })]),
        { type: 'closeGaps', trackId: TRACKS.video },
      ).message,
    ).toContain('locked');
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// move
// ─────────────────────────────────────────────────────────────────────────────

describe('move', () => {
  it('moves an item to another track, leaving a gap behind, and is self-inverse', () => {
    const { doc, inverse } = roundTrip(baseDoc(), {
      type: 'move',
      itemId: 'v2',
      toTrackId: TRACKS.audio,
      toRecordIn: 50,
    });

    expect(inverse).toEqual([
      { type: 'move', itemId: 'v2', toTrackId: TRACKS.video, toRecordIn: 10 },
    ]);
    expect(doc.tracks[0].items.map((item) => item.id)).toEqual(['v1', 'v3']);
    expect(itemOn(doc, TRACKS.audio, 'v2')).toMatchObject({ recordIn: 50, recordOut: 60 });
  });

  it('moves within a track and moves the first and last item', () => {
    const within = roundTrip(baseDoc(), {
      type: 'move',
      itemId: 'v3',
      toTrackId: TRACKS.video,
      toRecordIn: 22,
    });
    expect(itemOn(within.doc, TRACKS.video, 'v3')).toMatchObject({ recordIn: 22, recordOut: 32 });

    roundTrip(baseDoc(), { type: 'move', itemId: 'v1', toTrackId: TRACKS.video, toRecordIn: 45 });
    roundTrip(baseDoc(), { type: 'move', itemId: 'v3', toTrackId: TRACKS.video, toRecordIn: 20 });
  });

  it('refuses a destination that is occupied, an unknown id and a locked destination track', () => {
    expect(
      expectRefused(baseDoc(), { type: 'move', itemId: 'v3', toTrackId: TRACKS.video, toRecordIn: 12 }).message,
    ).toContain('overlaps');

    expect(
      expectRefused(baseDoc(), { type: 'move', itemId: 'nope', toTrackId: TRACKS.audio, toRecordIn: 50 }).message,
    ).toContain('unknown item id');

    expect(
      expectRefused(
        timeline([
          track(TRACKS.video, 'video', [media('v2', 10, 20, 5)]),
          track(TRACKS.audio, 'audio', [], { locked: true }),
        ]),
        { type: 'move', itemId: 'v2', toTrackId: TRACKS.audio, toRecordIn: 0 },
      ).message,
    ).toContain('locked');
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// setProperty
// ─────────────────────────────────────────────────────────────────────────────

describe('setProperty', () => {
  it('sets an allowlisted property on every kind and is exactly self-inverse', () => {
    const cases: EditOp[] = [
      { type: 'setProperty', itemId: 'v1', key: 'name', value: 'Renamed' },
      { type: 'setProperty', itemId: 'v1', key: 'enabled', value: false },
      { type: 'setProperty', itemId: 'v1', key: 'text', value: 'Hello' },
      { type: 'setProperty', itemId: 'v1', key: 'fontSize', value: 48 },
      { type: 'setProperty', itemId: 'v1', key: 'fontFamily', value: 'Chakra Petch' },
      { type: 'setProperty', itemId: 'v1', key: 'color', value: '#ff0000' },
      { type: 'setProperty', itemId: 'v3', key: 'text', value: 'Subtitle' },
      { type: 'setProperty', itemId: 'v3', key: 'language', value: 'it' },
      { type: 'setProperty', itemId: 'v2', key: 'playbackRate', value: 1.5 },
      { type: 'setProperty', itemId: 'v2', key: 'gainDb', value: { value: -4, keys: null } },
    ];

    for (const op of cases) {
      const { inverse } = roundTrip(baseDoc(), op);
      expect(inverse).toHaveLength(1);
      expect(inverse[0].type).toBe('setProperty');
    }

    const applied = apply(baseDoc(), cases[3]);
    expect((itemOn(applied.doc, TRACKS.video, 'v1') as TitleClip).fontSize).toBe(48);
  });

  it('restores an ABSENT key by clearing it again, which is what makes it invertible', () => {
    const doc = baseDoc();
    expect(itemOn(doc, TRACKS.video, 'v2')).not.toHaveProperty('gainDb');

    const { doc: applied, inverse } = roundTrip(doc, {
      type: 'setProperty',
      itemId: 'v2',
      key: 'gainDb',
      value: { value: -6, keys: null },
    });

    expect(inverse).toEqual([
      { type: 'setProperty', itemId: 'v2', key: 'gainDb', value: undefined },
    ]);
    expect((itemOn(applied, TRACKS.video, 'v2') as MediaClip).gainDb).toEqual({ value: -6, keys: null });
  });

  it('restores a PRESENT key to its previous value, null included', () => {
    const doc = baseDoc();
    (itemOn(doc, TRACKS.video, 'v2') as MediaClip).gainDb = null;

    const { inverse } = roundTrip(doc, {
      type: 'setProperty',
      itemId: 'v2',
      key: 'gainDb',
      value: { value: -2, keys: null },
    });
    expect(inverse[0]).toMatchObject({ value: null });
  });

  it('refuses to clear a key the document invariants require, naming the key', () => {
    // `name` is on every kind's allowlist AND `assertItemShape` requires it to be a string, so
    // `undefined` ("clear the key") is the one value that would hand back a document the engine
    // may no longer edit. Three different kinds, so the rule is not per-kind special-casing.
    for (const itemId of ['v1', 'v2', 'v3']) {
      const error = expectRefused(baseDoc(), {
        type: 'setProperty',
        itemId,
        key: 'name',
        value: undefined,
      });

      expect(error).toBeInstanceOf(EditEngineError);
      expect(error.op).toBe('setProperty');
      expect(error.message).toContain("'name'");
      expect(error.message).toContain('required');
    }
  });

  it('refuses the clear before it writes anything, leaving input and item untouched', () => {
    const doc = baseDoc();
    const before = structuredClone(doc);
    const beforeKeys = Object.keys(itemOn(doc, TRACKS.video, 'v1'));

    expectRefused(doc, { type: 'setProperty', itemId: 'v1', key: 'name', value: undefined });

    expect(doc).toEqual(before);
    expect(itemOn(doc, TRACKS.video, 'v1')).toHaveProperty('name', 'v1');
    // Not half-applied: the refusal is a decision taken about the op, so no key was removed from
    // the clone either (and `apply` clones up front, so it could never reach the caller's doc).
    expect(Object.keys(itemOn(doc, TRACKS.video, 'v1'))).toEqual(beforeKeys);
  });

  it('still clears an OPTIONAL key, and still round-trips it', () => {
    // Clearing a key the invariants do NOT require stays supported: it is what makes a
    // `setProperty` that set one invertible without a dedicated delete op in the catalogue.
    const audio = baseDoc();
    (itemOn(audio, TRACKS.video, 'v2') as MediaClip).gainDb = { value: -3, keys: null };

    const { doc: clearedGain, inverse: gainInverse } = roundTrip(audio, {
      type: 'setProperty',
      itemId: 'v2',
      key: 'gainDb',
      value: undefined,
    });
    expect(itemOn(clearedGain, TRACKS.video, 'v2')).not.toHaveProperty('gainDb');
    expect(gainInverse[0]).toMatchObject({ key: 'gainDb', value: { value: -3, keys: null } });

    const { doc: clearedColor, inverse: colorInverse } = roundTrip(baseDoc(), {
      type: 'setProperty',
      itemId: 'v1',
      key: 'color',
      value: undefined,
    });
    expect(itemOn(clearedColor, TRACKS.video, 'v1')).not.toHaveProperty('color');
    expect(colorInverse[0]).toMatchObject({ key: 'color', value: '#ffffff' });
  });

  it('refuses a key outside the per-kind allowlist', () => {
    expect(
      expectRefused(baseDoc(), { type: 'setProperty', itemId: 'v2', key: 'text', value: 'no' }).message,
    ).toContain('not settable on a mediaClip');
    expect(
      expectRefused(baseDoc(), { type: 'setProperty', itemId: 'v1', key: 'playbackRate', value: 2 }).message,
    ).toContain('not settable on a titleClip');
    expect(
      expectRefused(baseDoc(), { type: 'setProperty', itemId: 'v3', key: 'fontSize', value: 12 }).message,
    ).toContain('not settable on a caption');
  });

  it('refuses a wrongly-typed value', () => {
    expect(
      expectRefused(baseDoc(), { type: 'setProperty', itemId: 'v1', key: 'name', value: 42 as unknown as string }).message,
    ).toContain('must be a string');
    expect(
      expectRefused(baseDoc(), { type: 'setProperty', itemId: 'v1', key: 'enabled', value: 'yes' as unknown as boolean }).message,
    ).toContain('must be a boolean');
    expect(
      expectRefused(baseDoc(), { type: 'setProperty', itemId: 'v2', key: 'playbackRate', value: 0 }).message,
    ).toContain('greater than zero');
    expect(
      expectRefused(baseDoc(), { type: 'setProperty', itemId: 'v2', key: 'gainDb', value: 'loud' as never }).message,
    ).toContain('keyframed');
  });

  it('refuses an unknown item and a locked track', () => {
    expect(
      expectRefused(baseDoc(), { type: 'setProperty', itemId: 'nope', key: 'name', value: 'x' }).message,
    ).toContain('unknown item id');
    expect(
      expectRefused(timeline([track(TRACKS.video, 'video', [title('v1', 0, 10)], { locked: true })]), {
        type: 'setProperty',
        itemId: 'v1',
        key: 'name',
        value: 'x',
      }).message,
    ).toContain('locked');
  });

  it('advertises an allowlist for every item kind', () => {
    expect(Object.keys(SET_PROPERTY_ALLOWLIST).sort()).toEqual([
      'adjustmentClip',
      'caption',
      'compoundClip',
      'generatorClip',
      'insertClip',
      'mediaClip',
      'multicamClip',
      'titleClip',
      'transition',
    ]);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// markers
// ─────────────────────────────────────────────────────────────────────────────

describe('addMarker', () => {
  it('inserts a fresh marker in frame order and undoes with a single remove', () => {
    const { doc, inverse } = roundTrip(baseDoc(), { type: 'addMarker', marker: marker('m3', 15) }, 1);

    expect(inverse).toEqual([{ type: 'removeMarker', markerId: 'm3' }]);
    expect(doc.markers.map((entry) => entry.frame)).toEqual([5, 15, 25]);
  });

  it('inserts before the first marker and after the last', () => {
    expect(roundTrip(baseDoc(), { type: 'addMarker', marker: marker('m0', 0) }).doc.markers[0].id).toBe('m0');
    expect(roundTrip(baseDoc(), { type: 'addMarker', marker: marker('m9', 90) }).doc.markers.at(-1)?.id).toBe('m9');
  });

  it('refuses a duplicate marker id and a malformed marker', () => {
    expect(expectRefused(baseDoc(), { type: 'addMarker', marker: marker('m1', 15) }).message).toContain(
      'already in use',
    );
    expect(
      expectRefused(baseDoc(), { type: 'addMarker', marker: { ...marker('m3', 15), frame: -1 } }).message,
    ).toContain('non-negative integer');
  });
});

describe('setMarker', () => {
  it('patches fields and is exactly self-inverse', () => {
    const { doc, inverse } = roundTrip(baseDoc(), {
      type: 'setMarker',
      markerId: 'm1',
      patch: { name: 'Intro', color: '#00ff00' },
    });

    expect(inverse[0].type).toBe('setMarker');
    expect(doc.markers[0]).toMatchObject({ id: 'm1', name: 'Intro', color: '#00ff00' });
  });

  it('moves a frame within the slot its neighbours leave, and restores it', () => {
    const { doc } = roundTrip(baseDoc(), { type: 'setMarker', markerId: 'm1', patch: { frame: 8 } });
    expect(doc.markers.map((entry) => entry.frame)).toEqual([8, 25]);
  });

  it('refuses a patch that would leave the marker violating its own shape', () => {
    // `setMarker` may patch any field, and `undefined` clears it — but a marker with no `name`
    // is not one this engine may edit, so the patch is refused rather than performed.
    const error = expectRefused(baseDoc(), {
      type: 'setMarker',
      markerId: 'm1',
      patch: { name: undefined },
    });

    expect(error).toBeInstanceOf(EditEngineError);
    expect(error.op).toBe('setMarker');
    expect(error.message).toContain('markers[0].name');
  });

  it('still renames a marker, and the marker field patch still round-trips', () => {
    const { doc, inverse } = roundTrip(baseDoc(), {
      type: 'setMarker',
      markerId: 'm1',
      patch: { name: 'renamed' },
    });

    expect(doc.markers[0].name).toBe('renamed');
    expect(inverse[0]).toMatchObject({ type: 'setMarker', patch: { name: 'm1' } });
  });

  it('refuses a frame that would cross a neighbour, a changed id and an unknown marker', () => {
    expect(
      expectRefused(baseDoc(), { type: 'setMarker', markerId: 'm1', patch: { frame: 30 } }).message,
    ).toContain('break the marker frame ordering');
    expect(
      expectRefused(baseDoc(), { type: 'setMarker', markerId: 'm1', patch: { id: 'other' } }).message,
    ).toContain('must keep the marker id');
    expect(
      expectRefused(baseDoc(), { type: 'setMarker', markerId: 'nope', patch: { name: 'x' } }).message,
    ).toContain('unknown marker id');
  });
});

describe('removeMarker', () => {
  it('removes a marker and undoes by adding it back', () => {
    const { doc, inverse } = roundTrip(baseDoc(), { type: 'removeMarker', markerId: 'm1' }, 1);

    expect(inverse[0]).toMatchObject({ type: 'addMarker' });
    expect(doc.markers.map((entry) => entry.id)).toEqual(['m2']);
  });

  it('puts a marker back in its own slot even when it shares a frame with another', () => {
    // Markers that share a frame have no order in the contract, so the inverse must carry the
    // slot rather than re-derive it from the frame.
    const doc = timeline([track(TRACKS.video, 'video', [])], [marker('m0', 5), marker('m1', 5), marker('m2', 9)]);
    const { inverse } = roundTrip(doc, { type: 'removeMarker', markerId: 'm0' }, 1);

    expect(inverse[0]).toMatchObject({ type: 'addMarker', index: 0 });
  });

  it('refuses an unknown marker id', () => {
    expect(expectRefused(baseDoc(), { type: 'removeMarker', markerId: 'nope' }).message).toContain(
      'unknown marker id',
    );
  });
});

describe('addMarker slots', () => {
  it('refuses an explicit index that would break the frame ordering', () => {
    expect(
      expectRefused(baseDoc(), { type: 'addMarker', marker: marker('m3', 99), index: 0 }).message,
    ).toContain('breaking the frame ordering');
    expect(
      expectRefused(baseDoc(), { type: 'addMarker', marker: marker('m3', 0), index: 2 }).message,
    ).toContain('breaking the frame ordering');
    expect(
      expectRefused(baseDoc(), { type: 'addMarker', marker: marker('m3', 15), index: 9 }).message,
    ).toContain('0..2');
  });

  it('accepts an explicit index that keeps the ordering, and undoes it', () => {
    const doc = timeline([track(TRACKS.video, 'video', [])], [marker('m0', 5), marker('m1', 5)]);
    const { doc: applied } = roundTrip(doc, {
      type: 'addMarker',
      marker: marker('m2', 5),
      index: 0,
    });
    expect(applied.markers.map((entry) => entry.id)).toEqual(['m2', 'm0', 'm1']);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// assertInvariants — hand-built violators, so the guard is load-bearing
// ─────────────────────────────────────────────────────────────────────────────

describe('assertInvariants', () => {
  it('accepts the fixtures', () => {
    expect(() => assertInvariants(baseDoc())).not.toThrow();
    expect(() => assertInvariants(emptyDoc())).not.toThrow();
    expect(() => assertInvariants(timeline([], []))).not.toThrow();
  });

  const violators: Array<[string, (doc: EditTimeline) => EditTimeline]> = [
    [
      'schemaVersion other than 2',
      (doc) => ({ ...doc, schemaVersion: 1 as unknown as 2 }),
    ],
    [
      'a duplicated item id across tracks',
      (doc) => {
        doc.tracks[1].items[0].id = 'v1';
        return doc;
      },
    ],
    [
      'a duplicated track id',
      (doc) => {
        doc.tracks[1].id = doc.tracks[0].id;
        return doc;
      },
    ],
    [
      'a duplicated marker id',
      (doc) => {
        doc.markers[1].id = doc.markers[0].id;
        return doc;
      },
    ],
    [
      'items out of recordIn order',
      (doc) => {
        doc.tracks[0].items[1].recordIn = 40;
        doc.tracks[0].items[1].recordOut = 50;
        return doc;
      },
    ],
    [
      'overlapping items',
      (doc) => {
        doc.tracks[0].items[2].recordIn = 15;
        doc.tracks[0].items[2].recordOut = 40;
        return doc;
      },
    ],
    [
      'recordOut <= recordIn',
      (doc) => {
        doc.tracks[0].items[0].recordOut = 0;
        return doc;
      },
    ],
    [
      'a negative recordIn',
      (doc) => {
        doc.tracks[0].items[0].recordIn = -1;
        return doc;
      },
    ],
    [
      'a negative sourceIn',
      (doc) => {
        (doc.tracks[0].items[1] as MediaClip).sourceIn = -3;
        return doc;
      },
    ],
    [
      'a non-positive playbackRate',
      (doc) => {
        (doc.tracks[0].items[1] as MediaClip).playbackRate = 0;
        return doc;
      },
    ],
    [
      'a mediaClip without an assetRef',
      (doc) => {
        delete (doc.tracks[0].items[1] as Partial<MediaClip>).assetRef;
        return doc;
      },
    ],
    [
      'a marker with no name',
      (doc) => {
        delete (doc.markers[0] as Partial<Marker>).name;
        return doc;
      },
    ],
    [
      'a marker whose name is not a string',
      (doc) => {
        (doc.markers[0] as unknown as Record<string, unknown>).name = 42;
        return doc;
      },
    ],
    [
      'markers out of frame order',
      (doc) => {
        doc.markers[1].frame = 1;
        return doc;
      },
    ],
  ];

  it.each(violators)('rejects %s', (_label, breakIt) => {
    const doc = breakIt(baseDoc());
    expect(() => assertInvariants(doc)).toThrow(EditEngineError);
  });

  it('names the offending marker field in the message', () => {
    const doc = baseDoc();
    delete (doc.markers[0] as Partial<Marker>).name;
    expect(() => assertInvariants(doc)).toThrow(/markers\[0\]\.name/);
  });

  it('names the offending path in the message', () => {
    const doc = baseDoc();
    doc.tracks[0].items[2].recordIn = 15;
    expect(() => assertInvariants(doc)).toThrow(/tracks\[0\]\.items\[2\]/);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// P5 — keyframes
// ─────────────────────────────────────────────────────────────────────────────

describe('setKeyframe / removeKeyframe / moveKeyframe', () => {
  const target = { itemId: 'v2' };

  it('creates the gain container on first use with the identity static value (never null: eng D4) and undoes by restoring the ABSENT property', () => {
    const { doc, inverse } = roundTrip(baseDoc(), { type: 'setKeyframe', target, param: 'gainDb', frame: 12, value: -6, interp: 'linear' }, 1);
    expect((itemOn(doc, TRACKS.video, 'v2') as MediaClip).gainDb).toEqual({ value: 0, keys: [{ frame: 12, value: -6, interp: 'linear' }] });
    expect(inverse).toEqual([{ type: 'setProperty', itemId: 'v2', key: 'gainDb', value: undefined }]);
  });

  it('creates the transform object for a transform channel and undoes by restoring the absent transform', () => {
    const { doc, inverse } = roundTrip(baseDoc(), { type: 'setKeyframe', target, param: 'scale', frame: 10, value: 1.5, interp: 'easeInOutQuad' }, 1);
    expect((itemOn(doc, TRACKS.video, 'v2') as MediaClip).transform).toEqual({ scale: { value: 1, keys: [{ frame: 10, value: 1.5, interp: 'easeInOutQuad' }] } });
    expect(inverse).toEqual([{ type: 'setProperty', itemId: 'v2', key: 'transform', value: undefined }]);
  });

  it('undoes a key on a second transform channel by restoring the whole transform, never clearing it', () => {
    // Regression: a clip that already carried one transform channel and gained a key on a
    // *different* channel used to undo by re-setting `transform` to the previous value of that
    // channel — which is absent — so the undo wiped every channel already on the clip and the
    // next inverse op (`removeKeyframe` on the first channel) was refused with "has no transform".
    const seeded = apply(baseDoc(), { type: 'setKeyframe', target, param: 'opacity', frame: 3, value: 0.5, interp: 'linear' }).doc;
    const { doc, inverse } = roundTrip(seeded, { type: 'setKeyframe', target, param: 'positionX', frame: 7, value: 1.25, interp: 'linear' }, 1);
    const backup = inverse[0] as { type: string; itemId: string; key: string; value: Record<string, { keys: { frame: number }[] }> };
    expect(inverse).toHaveLength(1);
    expect(backup.type).toBe('setProperty');
    expect(backup.itemId).toBe('v2');
    expect(backup.key).toBe('transform');
    // The inverse carries the WHOLE previous transform (the opacity channel only), never just the
    // absent positionX channel. The channel's seed `value` is master's identity default, not this
    // test's subject.
    expect(Object.keys(backup.value)).toEqual(['opacity']);
    expect(backup.value.opacity.keys.map((k) => k.frame)).toEqual([3]);
    const restored = (itemOn(doc, TRACKS.video, 'v2') as MediaClip).transform!;
    expect(Object.keys(restored)).toEqual(['opacity', 'positionX']);
    expect(restored.opacity!.keys!.map((k) => k.frame)).toEqual([3]);
    expect(restored.positionX!.keys!.map((k) => k.frame)).toEqual([7]);
  });

  it('adds a second key in frame order (undo = removeKeyframe), replaces a key in place (undo = the old key)', () => {
    const seeded = apply(baseDoc(), { type: 'setKeyframe', target, param: 'gainDb', frame: 12, value: -6, interp: 'linear' }).doc;
    const added = roundTrip(seeded, { type: 'setKeyframe', target, param: 'gainDb', frame: 10, value: 0, interp: 'step' }, 1);
    expect((itemOn(added.doc, TRACKS.video, 'v2') as MediaClip).gainDb!.keys!.map((k) => k.frame)).toEqual([10, 12]);
    expect(added.inverse).toEqual([{ type: 'removeKeyframe', target, param: 'gainDb', frame: 10 }]);

    const replaced = roundTrip(seeded, { type: 'setKeyframe', target, param: 'gainDb', frame: 12, value: 3, interp: 'easeInCubic' }, 1);
    expect(replaced.inverse).toEqual([{ type: 'setKeyframe', target, param: 'gainDb', frame: 12, value: -6, interp: 'linear' }]);
  });

  it('works on a track fader, undoing through setTrackProperty when it created the container', () => {
    const { doc, inverse } = roundTrip(baseDoc(), { type: 'setKeyframe', target: { trackId: TRACKS.audio }, param: 'gainDb', frame: 0, value: -12, interp: 'linear' }, 1);
    expect(doc.tracks[1].gainDb).toEqual({ value: 0, keys: [{ frame: 0, value: -12, interp: 'linear' }] });
    expect(inverse).toEqual([{ type: 'setTrackProperty', trackId: TRACKS.audio, key: 'gainDb', value: undefined }]);
  });

  it('removes and moves keys with exact inverses, never deleting the container', () => {
    const seeded = applyAll(baseDoc(), [
      { type: 'setKeyframe', target, param: 'gainDb', frame: 12, value: -6, interp: 'linear' },
      { type: 'setKeyframe', target, param: 'gainDb', frame: 18, value: 0, interp: 'linear' },
    ]);
    const removed = roundTrip(seeded, { type: 'removeKeyframe', target, param: 'gainDb', frame: 12 }, 1);
    expect(removed.inverse).toEqual([{ type: 'setKeyframe', target, param: 'gainDb', frame: 12, value: -6, interp: 'linear' }]);
    const onlyOne = apply(removed.doc, { type: 'removeKeyframe', target, param: 'gainDb', frame: 18 }).doc;
    expect((itemOn(onlyOne, TRACKS.video, 'v2') as MediaClip).gainDb).toEqual({ value: 0, keys: [] });

    const moved = roundTrip(seeded, { type: 'moveKeyframe', target, param: 'gainDb', frame: 18, toFrame: 11 }, 1);
    expect((itemOn(moved.doc, TRACKS.video, 'v2') as MediaClip).gainDb!.keys!.map((k) => k.frame)).toEqual([11, 12]);
    expect(moved.inverse).toEqual([{ type: 'moveKeyframe', target, param: 'gainDb', frame: 11, toFrame: 18 }]);
  });

  it('refuses a value outside the channel range, a non-media item, a track transform, a missing key and an occupied move', () => {
    expect(expectRefused(baseDoc(), { type: 'setKeyframe', target, param: 'opacity', frame: 1, value: 1.5, interp: 'linear' }).message).toContain('range');
    expect(expectRefused(baseDoc(), { type: 'setKeyframe', target, param: 'scale', frame: 1, value: 0, interp: 'linear' }).message).toContain('range');
    expect(expectRefused(baseDoc(), { type: 'setKeyframe', target: { itemId: 'v1' }, param: 'gainDb', frame: 1, value: 0, interp: 'linear' }).message).toContain('mediaClip');
    expect(expectRefused(baseDoc(), { type: 'setKeyframe', target: { trackId: TRACKS.audio }, param: 'scale', frame: 1, value: 1, interp: 'linear' }).message).toContain("'gainDb'");
    expect(expectRefused(baseDoc(), { type: 'removeKeyframe', target, param: 'gainDb', frame: 3 }).message).toContain('no keyframe');
    const seeded = applyAll(baseDoc(), [
      { type: 'setKeyframe', target, param: 'gainDb', frame: 12, value: -6, interp: 'linear' },
      { type: 'setKeyframe', target, param: 'gainDb', frame: 18, value: 0, interp: 'linear' },
    ]);
    expect(expectRefused(seeded, { type: 'moveKeyframe', target, param: 'gainDb', frame: 12, toFrame: 18 }).message).toContain('already');
    expect(expectRefused(seeded, { type: 'setKeyframe', target, param: 'gainDb', frame: 1.5, value: 0, interp: 'linear' }).message).toContain('integer');
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// P5 — retime
// ─────────────────────────────────────────────────────────────────────────────

describe('retime', () => {
  it('changes rate and recordOut together, ripples the followers, and undoes with the old pair', () => {
    const { doc, inverse } = roundTrip(baseDoc(), { type: 'retime', itemId: 'a1', playbackRate: 2, recordOut: 10 }, 1);
    expect(itemOn(doc, TRACKS.audio, 'a1')).toMatchObject({ recordOut: 10, playbackRate: 2 });
    expect(itemOn(doc, TRACKS.audio, 'a2')).toMatchObject({ recordIn: 10, recordOut: 30 });
    expect(inverse).toEqual([{ type: 'retime', itemId: 'a1', playbackRate: 1, recordOut: 20 }]);

    const slower = roundTrip(baseDoc(), { type: 'retime', itemId: 'a1', playbackRate: 0.5, recordOut: 40 }, 1);
    expect(itemOn(slower.doc, TRACKS.audio, 'a2')).toMatchObject({ recordIn: 40, recordOut: 60 });
  });

  it('refuses a non-media item, a non-positive rate and a recordOut at or before recordIn', () => {
    expect(expectRefused(baseDoc(), { type: 'retime', itemId: 'v1', playbackRate: 2, recordOut: 5 }).message).toContain('mediaClip');
    expect(expectRefused(baseDoc(), { type: 'retime', itemId: 'a1', playbackRate: 0, recordOut: 10 }).message).toContain('greater than zero');
    expect(expectRefused(baseDoc(), { type: 'retime', itemId: 'a1', playbackRate: 2, recordOut: 0 }).message).toContain('after recordIn');
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// P5 — transitions
// ─────────────────────────────────────────────────────────────────────────────

describe('addTransition / removeTransition', () => {
  const transition = { id: 'x1', type: 'transition' as const, name: 'Dissolve', transitionType: 'dissolve', easing: 'linear', enabled: true, provenance: null };

  it('centres a transition on the cut: trims the outgoing clip, pulls the incoming one, advances its sourceIn, undoes by removeTransition', () => {
    // a1 [0,20) sourceIn 2 · a2 [20,40) sourceIn 3 → 2 frames before the cut, 2 after.
    const { doc, inverse } = roundTrip(baseDoc(), { type: 'addTransition', afterItemId: 'a1', transition, framesBefore: 2, framesAfter: 2 }, 1);
    const items = doc.tracks[1].items;
    expect(items.map((i) => [i.id, i.recordIn, i.recordOut])).toEqual([['a1', 0, 18], ['x1', 18, 22], ['a2', 22, 40]]);
    expect((items[2] as MediaClip).sourceIn).toBe(5);
    expect(items[1]).toMatchObject({ type: 'transition', transitionType: 'dissolve', recordIn: 18, recordOut: 22 });
    expect(inverse).toEqual([{ type: 'removeTransition', transitionId: 'x1', cutFrame: 20 }]);
  });

  it('removeTransition restores a hard cut anywhere inside the span and undoes by the implied addTransition', () => {
    const withTransition = apply(baseDoc(), { type: 'addTransition', afterItemId: 'a1', transition, framesBefore: 2, framesAfter: 2 }).doc;
    const { doc, inverse } = roundTrip(withTransition, { type: 'removeTransition', transitionId: 'x1', cutFrame: 19 }, 1);
    expect(doc.tracks[1].items.map((i) => [i.id, i.recordIn, i.recordOut])).toEqual([['a1', 0, 19], ['a2', 19, 40]]);
    expect((doc.tracks[1].items[1] as MediaClip).sourceIn).toBe(2); // 5 - round(3 × 1)
    expect(inverse).toEqual([{ type: 'addTransition', afterItemId: 'a1', transition, framesBefore: 1, framesAfter: 3 }]);
  });

  it('refuses insufficient handles, non-adjacent or non-media neighbours, a catalogue miss and a span that eats a clip', () => {
    // a2 has only 3 source frames before its sourceIn: 4 frames before the cut are not available.
    expect(expectRefused(baseDoc(), { type: 'addTransition', afterItemId: 'a1', transition, framesBefore: 4, framesAfter: 0 }).message).toContain('insufficient_handles');
    expect(expectRefused(baseDoc(), { type: 'addTransition', afterItemId: 'v2', transition, framesBefore: 1, framesAfter: 1 }).message).toContain('not followed by a media clip');
    expect(expectRefused(baseDoc(), { type: 'addTransition', afterItemId: 'a1', transition: { ...transition, transitionType: 'sparkle' }, framesBefore: 1, framesAfter: 1 }).message).toContain('catalogue');
    expect(expectRefused(baseDoc(), { type: 'addTransition', afterItemId: 'a1', transition, framesBefore: 20, framesAfter: 0 }).message).toContain('whole outgoing clip');
    expect(expectRefused(baseDoc(), { type: 'addTransition', afterItemId: 'a1', transition, framesBefore: 0, framesAfter: 0 }).message).toContain('at least 1');
    expect(expectRefused(baseDoc(), { type: 'removeTransition', transitionId: 'a1', cutFrame: 10 }).message).toContain('not a transition');
  });

  it('an incoming clip with no sourceIn key gains one and the inverse clears it again', () => {
    const doc = timeline([track(TRACKS.audio, 'audio', [media('a1', 0, 20, 2), media('a2', 20, 40)])]);
    const applied = roundTrip(doc, { type: 'addTransition', afterItemId: 'a1', transition, framesBefore: 0, framesAfter: 3 }, 2);
    expect((applied.doc.tracks[0].items[2] as MediaClip).sourceIn).toBe(3);
    expect(applied.inverse[1]).toEqual({ type: 'setProperty', itemId: 'a2', key: 'sourceIn', value: undefined });
  });

  it('a reversed incoming clip needs no handle: its sourceIn is its lowest frame already', () => {
    const doc = timeline([track(TRACKS.audio, 'audio', [media('a1', 0, 20, 2), media('a2', 20, 40, 0, { reverse: true })])]);
    const applied = roundTrip(doc, { type: 'addTransition', afterItemId: 'a1', transition, framesBefore: 3, framesAfter: 0 }, 1);
    expect((applied.doc.tracks[0].items[2] as MediaClip).sourceIn).toBe(0);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// P5 — the widened allowlists
// ─────────────────────────────────────────────────────────────────────────────

describe('P5 properties through setProperty / setTrackProperty', () => {
  it('sets a transform, a crop, fades, reverse/hold and the catalogue words, each exactly self-inverse', () => {
    roundTrip(baseDoc(), { type: 'setProperty', itemId: 'v2', key: 'transform', value: { scale: { value: 1.2, keys: null }, opacity: { value: 0.5 } } }, 1);
    roundTrip(baseDoc(), { type: 'setProperty', itemId: 'v2', key: 'crop', value: { left: 0.1, right: 0.2 } }, 1);
    roundTrip(baseDoc(), { type: 'setProperty', itemId: 'v2', key: 'fadeInFrames', value: 3 }, 1);
    roundTrip(baseDoc(), { type: 'setProperty', itemId: 'v2', key: 'reverse', value: true }, 1);
    roundTrip(baseDoc(), { type: 'setProperty', itemId: 'v2', key: 'hold', value: true }, 1);
    const withTransition = apply(baseDoc(), {
      type: 'addTransition', afterItemId: 'a1', framesBefore: 1, framesAfter: 1,
      transition: { id: 'x1', type: 'transition', name: 'T', transitionType: 'fade', easing: 'linear', enabled: true, provenance: null },
    }).doc;
    roundTrip(withTransition, { type: 'setProperty', itemId: 'x1', key: 'transitionType', value: 'wipeleft' }, 1);
    roundTrip(withTransition, { type: 'setProperty', itemId: 'x1', key: 'easing', value: 'easeInOutCubic' }, 1);
    roundTrip(baseDoc(), { type: 'setTrackProperty', trackId: TRACKS.audio, key: 'pan', value: -0.5 }, 1);
    roundTrip(baseDoc(), { type: 'setTrackProperty', trackId: TRACKS.audio, key: 'gainDb', value: { value: -3, keys: null } }, 1);
    roundTrip(baseDoc(), { type: 'setTrackProperty', trackId: TRACKS.audio, key: 'eq', value: { lowCutHz: 80, bands: [{ frequencyHz: 3000, gainDb: 2, q: 1.4 }] } }, 1);
  });

  it('refuses out-of-range values the validator would also refuse', () => {
    expect(expectRefused(baseDoc(), { type: 'setProperty', itemId: 'v2', key: 'transform', value: { opacity: { value: 2 } } }).message).toContain('opacity');
    expect(expectRefused(baseDoc(), { type: 'setProperty', itemId: 'v2', key: 'transform', value: { wobble: { value: 1 } } }).message).toContain('channels');
    expect(expectRefused(baseDoc(), { type: 'setProperty', itemId: 'v2', key: 'crop', value: { left: 0.6, right: 0.5 } }).message).toContain('leaves some picture');
    expect(expectRefused(baseDoc(), { type: 'setProperty', itemId: 'v2', key: 'fadeOutFrames', value: -1 }).message).toContain('non-negative');
    expect(expectRefused(baseDoc(), { type: 'setProperty', itemId: 'v2', key: 'gainDb', value: { value: 40 } }).message).toContain('dB');
    expect(expectRefused(baseDoc(), { type: 'setTrackProperty', trackId: TRACKS.audio, key: 'pan', value: 2 }).message).toContain('pan');
    expect(expectRefused(baseDoc(), { type: 'setTrackProperty', trackId: TRACKS.audio, key: 'eq', value: { lowCutHz: 5 } }).message).toContain('eq');
    expect(expectRefused(baseDoc(), { type: 'setTrackProperty', trackId: TRACKS.audio, key: 'eq', value: { bands: [1, 2, 3, 4, 5].map((n) => ({ frequencyHz: 100 * n, gainDb: 1 })) } }).message).toContain('eq');
  });

  it('transitionType is a required key: clearing it is refused', () => {
    const withTransition = apply(baseDoc(), {
      type: 'addTransition', afterItemId: 'a1', framesBefore: 1, framesAfter: 1,
      transition: { id: 'x1', type: 'transition', name: 'T', transitionType: 'fade', easing: 'linear', enabled: true, provenance: null },
    }).doc;
    expect(expectRefused(withTransition, { type: 'setProperty', itemId: 'x1', key: 'transitionType', value: undefined }).message).toContain('required');
  });
});
