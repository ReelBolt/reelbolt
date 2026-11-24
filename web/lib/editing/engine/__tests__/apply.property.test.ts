/**
 * Property-based half of the engine's contract: the round trip and the invariants, asserted over
 * *arbitrary* documents rather than over the hand-built fixtures in `apply.test.ts`.
 *
 * `apply.test.ts` pins 85 worked examples — one named case per behaviour a human thought of. This
 * file asks the complementary question: is `applyAll(apply(doc, op).doc, inverse)` deep-equal to
 * `doc`, and does `assertInvariants` still hold, when `doc` is a randomly generated but *valid*
 * timeline and `op` names real generated ids? Anything the worked examples miss — an unusual gap
 * layout, a track that happens to be one item long, a clamp that binds on the last frame — shows
 * up here as a shrunk counterexample instead of as a production undo that corrupts a document.
 *
 * Three things are asserted, and nothing here weakens any of them:
 *
 *   1. **Round trip per op.** One `fc.assert(fc.property(...))` per op of the closed thirteen-op
 *      catalogue, each generating documents *and* op parameters from the generated document (real
 *      track/item/marker ids, never a fixed fixture). Restoration is asserted twice: stepwise
 *      through the inverse program, and once through `applyAll(after, inverse)` — the ticket's
 *      exact wording.
 *   2. **Refusals are rejections.** A deliberately *unfiltered* op generator (partial overlaps,
 *      non-canonical indices, out-of-allowlist keys, missing neighbours, stale ids) asserts the
 *      either/or contract: the op either succeeds and round-trips, or it is refused with an
 *      `EditEngineError` and the document is left byte-identical. A refusal is never a crash and
 *      never a silent mutation.
 *   3. **Invariants across a run.** 1-20 randomly generated *valid* ops are folded over one
 *      document, `assertInvariants` is asserted after **every** op, and the concatenated inverse
 *      program — applied in reverse order, the way an undo stack runs it — restores the starting
 *      document exactly.
 *
 * Four seeded (non-random) cases then pin the boundaries the ticket names, each asserting the
 * round trip exactly rather than only that nothing threw: an op on a track's FIRST item, an op on
 * a track's LAST item, ops that EMPTY a track, and a `Trim` whose requested delta is CLAMPED by
 * the media-handle (`sourceIn`) floor.
 *
 * Every generator builds its documents through the same invariants the engine maintains
 * (`recordIn >= 0`, `recordOut > recordIn`, sorted and non-overlapping per track, unique ids
 * document-wide, `sourceIn >= 0`, `assetRef` on a `mediaClip`), because a property whose input is
 * already invalid proves nothing about `apply`.
 *
 * The final block pins a real defect this suite found and that the engine has since closed: for one
 * round `setProperty` could clear a field `assertInvariants` requires, so the *result* of a
 * successful op was not a document the engine may edit. The engine now refuses that op (and the
 * marker path was closed in the same round), so the block asserts the refusal — alongside the
 * optional-key clear the fix must not break — instead of the coverage being dropped.
 */

import { describe, expect, it } from 'vitest';
import fc from 'fast-check';

import {
  apply,
  applyAll,
  assertInvariants as assertEngineInvariants,
  EditEngineError,
  CHANNEL_MAPS,
  EFFECT_TYPES,
  INTERP_KINDS,
  KEYFRAME_PARAM_RANGE,
  PICTURE_ONLY_ITEM_TYPES,
  SET_PROPERTY_ALLOWLIST,
  TITLE_TEMPLATES,
  TRANSITION_TYPES,
  type EditOp,
  type KeyframeParam,
  type KeyframeTarget,
  type PropertyKey,
  type PropertyValue,
  type TrimEdge,
  type TrimMode,
} from '../index';
import type {
  AssetRef,
  Caption,
  ClipEffect,
  EditTimeline,
  Item,
  Keyframed,
  Marker,
  MediaClip,
  TitleClip,
  Track,
} from '../../types';

/** These fixtures name things `v1`/`track-video`: every invariant except the Guid id shape (pinned in `apply.contract.test.ts`). */
const assertInvariants = (doc: Parameters<typeof assertEngineInvariants>[0]): void => assertEngineInvariants(doc, { ids: 'opaque' });

// ─────────────────────────────────────────────────────────────────────────────
// Compile-time pins
//
// Type-checked by `npx tsc --noEmit` rather than by the runner: the closed catalogue gains a
// fourteenth member and this file stops compiling until it has a property of its own.
// ─────────────────────────────────────────────────────────────────────────────

/** The twenty-five ops this file asserts a round-trip property for, in the catalogue's own order. */
const COVERED_OPS = [
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

type UncoveredOpType = Exclude<EditOp['type'], (typeof COVERED_OPS)[number]>;
const everyOpHasAProperty: UncoveredOpType extends never ? true : false = true;

/** `apply`'s `inverse` is a program (`EditOp[]`), not a single op — the ticket's `inverse: EditOp[]`. */
type InverseIsOpArray = ReturnType<typeof apply>['inverse'] extends EditOp[] ? true : false;
const inverseIsOpArray: InverseIsOpArray = true;

// ─────────────────────────────────────────────────────────────────────────────
// Minimal, valid document construction
// ─────────────────────────────────────────────────────────────────────────────

const SETTINGS: EditTimeline['settings'] = {
  width: 1920,
  height: 1080,
  rate: { num: 30, den: 1 },
  sampleRate: 48000,
  pixelAspect: { num: 1, den: 1 },
  aspectPolicy: 'fit',
  startTimecodeFrames: 0,
};

const projectFile = (id: string): AssetRef => ({ kind: 'projectFile', projectFileId: id });

/** The nine item kinds — all of them, so `setProperty`'s per-kind allowlist is fully exercised. */
const ITEM_KINDS = [
  'mediaClip',
  'titleClip',
  'caption',
  'generatorClip',
  'insertClip',
  'transition',
  'compoundClip',
  'multicamClip',
  'adjustmentClip',
] as const;

type GeneratedKind = (typeof ITEM_KINDS)[number];

/**
 * A kind a builder may put on `track` as a NEW item: never a transition (one only means something
 * flush between two clips, which `addTransition` builds), and never a picture-only kind on an audio
 * track (d1 N5: refused by the engine and the validator alike).
 */
function placeableKind(e: Entropy, trackKind: Track['kind']): GeneratedKind {
  const kinds = ITEM_KINDS.filter((kind) => kind !== 'transition' && !(trackKind === 'audio' && PICTURE_ONLY_ITEM_TYPES.includes(kind)));
  return e.pick(kinds) ?? 'mediaClip';
}

/** P8: every generated document carries one empty sequence, which every generated compound clip plays. */
const PROP_SEQUENCE_ID = 'seq-prop';

/** P8: the two angles every generated multicam clip carries (angle ids are only unique per clip). */
const PROP_ANGLES = [
  { id: 'angle-a', name: 'Cam A' },
  { id: 'angle-b', name: 'Cam B', offsetFrames: 12 },
] as const;

const itemBase = (id: string, recordIn: number, recordOut: number) => ({
  id,
  name: id,
  recordIn,
  recordOut,
  provenance: null,
});

/**
 * One item of `kind`, carrying exactly the fields that kind requires. `sourceIn` is added only
 * when asked for — an *absent* `sourceIn` is a different (and legal) shape from `sourceIn: 0`, and
 * the engine's `slip` mode distinguishes the two.
 */
function makeItem(
  id: string,
  kind: GeneratedKind,
  recordIn: number,
  recordOut: number,
  sourceIn: number | undefined,
): Item {
  const base = itemBase(id, recordIn, recordOut);
  switch (kind) {
    case 'mediaClip':
      return {
        ...base,
        type: 'mediaClip',
        assetRef: projectFile(`file-${id}`),
        playbackRate: 1,
        enabled: true,
        ...(sourceIn === undefined ? {} : { sourceIn }),
      } satisfies MediaClip;
    case 'titleClip':
      return {
        ...base,
        type: 'titleClip',
        text: `t-${id}`,
        fontSize: 24,
        fontFamily: 'Inter',
        color: '#ffffff',
        enabled: true,
      } satisfies TitleClip;
    case 'caption':
      return { ...base, type: 'caption', text: `c-${id}`, language: 'en', enabled: true } satisfies Caption;
    case 'generatorClip':
      return { ...base, type: 'generatorClip', generatorType: 'bars', enabled: true };
    case 'insertClip':
      return { ...base, type: 'insertClip', assetRef: projectFile(`ins-${id}`), enabled: true };
    case 'transition':
      return { ...base, type: 'transition', transitionType: 'dissolve', easing: 'linear', enabled: true };
    case 'compoundClip':
      return { ...base, type: 'compoundClip', sequenceId: PROP_SEQUENCE_ID };
    case 'multicamClip':
      return { ...base, type: 'multicamClip', angles: PROP_ANGLES.map((a) => ({ ...a, assetRef: projectFile(`${a.id}-${id}`) })), activeAngleId: PROP_ANGLES[0].id };
    case 'adjustmentClip':
      return { ...base, type: 'adjustmentClip' };
  }
}

/** A copy of `item` whose span is `[recordIn, recordOut)`, every other field untouched. */
function withSpan(item: Item, recordIn: number, recordOut: number): Item {
  const copy = structuredClone(item) as Item & { recordIn: number; recordOut: number };
  copy.recordIn = recordIn;
  copy.recordOut = recordOut;
  return copy;
}

/** The source offset the engine would see: only a `mediaClip` has one. */
function sourceInOf(item: Item): number | undefined {
  if (item.type !== 'mediaClip') return undefined;
  const raw = (item as unknown as Record<string, unknown>).sourceIn;
  return typeof raw === 'number' ? raw : undefined;
}

type Located = { track: Track; index: number; item: Item };

function allItems(doc: EditTimeline): Located[] {
  const found: Located[] = [];
  for (const track of doc.tracks) {
    track.items.forEach((item, index) => found.push({ track, index, item }));
  }
  return found;
}

/** How many transitions (other than `subjectId`) an op removed from the document (eng D7's sweep). */
function transitionsSweptBy(before: EditTimeline, after: EditTimeline, subjectId: string): number {
  const remaining = new Set(allItems(after).map((located) => located.item.id));
  return allItems(before).filter((located) => located.item.type === 'transition' && located.item.id !== subjectId && !remaining.has(located.item.id)).length;
}

/** How many tracks the swept transitions sat on (a move can sweep on its source AND destination). */
function tracksSweptBy(before: EditTimeline, after: EditTimeline, subjectId: string): number {
  const remaining = new Set(allItems(after).map((located) => located.item.id));
  const tracks = new Set(
    allItems(before)
      .filter((located) => located.item.type === 'transition' && located.item.id !== subjectId && !remaining.has(located.item.id))
      .map((located) => located.track.id),
  );
  return tracks.size;
}

/**
 * The ops that put swept transitions back: one `insert` for a lone one; several go back with their
 * whole tracks (`removeTrack` + `addTrack` per track they sat on).
 */
function sweepRestoreLength(swept: number, tracks = 1): number {
  return swept === 0 ? 0 : swept === 1 ? 1 : 2 * tracks;
}

/** The canonical array index for an item starting at `recordIn` — recomputed here, never imported. */
function canonicalIndex(items: readonly Item[], recordIn: number): number {
  let index = 0;
  while (index < items.length && items[index].recordIn < recordIn) index++;
  return index;
}

/** Frame positions of every leading/interior gap on a track, as `[start, end)` pairs. */
function gapsOf(items: readonly Item[]): Array<[number, number]> {
  const gaps: Array<[number, number]> = [];
  if (items.length === 0) return gaps;
  if (items[0].recordIn > 0) gaps.push([0, items[0].recordIn]);
  for (let i = 0; i + 1 < items.length; i++) {
    if (items[i + 1].recordIn > items[i].recordOut) {
      gaps.push([items[i].recordOut, items[i + 1].recordIn]);
    }
  }
  return gaps;
}

// ─────────────────────────────────────────────────────────────────────────────
// Arbitrary documents
// ─────────────────────────────────────────────────────────────────────────────

type Slot = {
  gapBefore: number;
  length: number;
  kind: GeneratedKind;
  hasSourceIn: boolean;
  sourceIn: number;
};

type MarkerSpec = { frame: number; name: string };

const slotArb = (minGap: number, minLength: number): fc.Arbitrary<Slot> =>
  fc.record({
    gapBefore: fc.integer({ min: minGap, max: minGap + 6 }),
    length: fc.integer({ min: minLength, max: Math.max(minLength, 8) }),
    kind: fc.constantFrom(...ITEM_KINDS),
    hasSourceIn: fc.boolean(),
    sourceIn: fc.integer({ min: 0, max: 4 }),
  });

const markerSpecArb: fc.Arbitrary<MarkerSpec> = fc.record({
  frame: fc.integer({ min: 0, max: 40 }),
  name: fc.string({ maxLength: 6 }),
});

type DocOptions = {
  /** Force at least one marker, for ops that address one. */
  minMarkers?: number;
  /** Force a leading gap (and therefore at least one item) on every track, for `deleteGap`. */
  guaranteeGap?: boolean;
  /** Force at least this many items on the first track, for ops that need an item at all. */
  minItems?: number;
  /** Force every item of the first track to be at least this long, for `blade`. */
  minItemLength?: number;
};

/**
 * Arbitrary **valid** timelines: 1-3 tracks, 0-4 items each, random gap/length layouts, random
 * kinds, with or without a source offset. Ids are derived from the (track, position) pair, so they
 * are unique document-wide by construction.
 */
function documentArbitrary(options: DocOptions = {}): fc.Arbitrary<EditTimeline> {
  const minGap = options.guaranteeGap === true ? 1 : 0;
  const minItems = Math.max(options.guaranteeGap === true ? 1 : 0, options.minItems ?? 0);
  const minLength = options.minItemLength ?? 1;

  return fc
    .tuple(
      // The first track carries whatever the caller needs to be guaranteed to exist; the rest are
      // free to be empty, one-item, or anything in between.
      fc.array(slotArb(minGap, minLength), { minLength: minItems, maxLength: 4 }),
      fc.array(fc.array(slotArb(minGap, 1), { minLength: 0, maxLength: 4 }), {
        minLength: 0,
        maxLength: 2,
      }),
      fc.array(markerSpecArb, { minLength: options.minMarkers ?? 0, maxLength: 4 }),
    )
    .map(([first, rest, markerSpecs]) => buildTimeline([first, ...rest], markerSpecs));
}

function buildTimeline(trackSlots: readonly (readonly Slot[])[], markerSpecs: readonly MarkerSpec[]): EditTimeline {
  const tracks: Track[] = trackSlots.map((slots, t) => {
    const items: Item[] = [];
    const audio = t % 2 !== 0;
    let cursor = 0;
    slots.forEach((slot, i) => {
      // Valid by construction (d1 N5, eng D7): a picture-only kind on an audio track becomes a media
      // clip, and any pair involving a transition is flush.
      const kind: GeneratedKind = audio && PICTURE_ONLY_ITEM_TYPES.includes(slot.kind) ? 'mediaClip' : slot.kind;
      const flush = (kind === 'transition' && items.length > 0) || items[items.length - 1]?.type === 'transition';
      const recordIn = cursor + (flush ? 0 : slot.gapBefore);
      const recordOut = recordIn + slot.length;
      items.push(
        makeItem(
          `d${t}-${i}`,
          kind,
          recordIn,
          recordOut,
          slot.hasSourceIn ? slot.sourceIn : undefined,
        ),
      );
      cursor = recordOut;
    });
    return {
      id: `track-${t}`,
      kind: t % 2 === 0 ? 'video' : 'audio',
      name: `track-${t}`,
      height: 64,
      locked: false,
      enabled: true,
      mute: false,
      solo: false,
      items,
    };
  });

  const markers: Marker[] = markerSpecs
    .map((spec, i) => ({
      id: `m${i}`,
      frame: spec.frame,
      name: spec.name,
      color: null,
      note: null,
      durationFrames: null,
    }))
    .sort((left, right) => left.frame - right.frame);

  return {
    schemaVersion: 2,
    id: 'timeline-prop',
    projectId: 'project-prop',
    name: 'Property fixture',
    settings: SETTINGS,
    tracks,
    markers,
    inOut: null,
    seed: null,
    sequences: [{ id: PROP_SEQUENCE_ID, name: 'Nested', tracks: [] }],
  };
}

/** A document generator whose result is walked with `f`, so op parameters can name real ids. */
function withDocument<T>(
  options: DocOptions,
  f: (doc: EditTimeline) => fc.Arbitrary<T>,
): fc.Arbitrary<T> {
  return documentArbitrary(options).chain((doc) => f(doc));
}

// ─────────────────────────────────────────────────────────────────────────────
// Entropy: raw integers interpreted against the *current* document
//
// Op parameters have to name ids that exist right now, so they cannot come from a free-standing
// arbitrary. Instead a vector of plain integers is drawn first and then interpreted against the
// document (or against each intermediate document of a sequence) — which keeps the generated ops
// genuinely random while every id they mention is real.
// ─────────────────────────────────────────────────────────────────────────────

class Entropy {
  private cursor = 0;

  constructor(private readonly values: readonly number[]) {}

  next(): number {
    const value = this.values[this.cursor % Math.max(1, this.values.length)] ?? 0;
    this.cursor++;
    return Math.abs(Math.trunc(value));
  }

  /** A value in `[0, bound)`, or `0` when there is no room to choose. */
  below(bound: number): number {
    return bound <= 0 ? 0 : this.next() % bound;
  }

  /** An inclusive integer range; `low` when the range is empty. */
  between(low: number, high: number): number {
    return high <= low ? low : low + this.below(high - low + 1);
  }

  bool(): boolean {
    return this.below(2) === 1;
  }

  pick<T>(values: readonly T[]): T | undefined {
    return values.length === 0 ? undefined : values[this.below(values.length)];
  }
}

const entropyArb: fc.Arbitrary<number[]> = fc.array(fc.nat({ max: 1_000_000 }), {
  minLength: 8,
  maxLength: 24,
});

/** One entropy vector per step, and 1-20 steps: the ticket's 1-20 op sequence, exactly. */
const planArb: fc.Arbitrary<number[][]> = fc.array(entropyArb, { minLength: 1, maxLength: 20 });

// ─────────────────────────────────────────────────────────────────────────────
// Valid-by-construction op builders
// ─────────────────────────────────────────────────────────────────────────────

const TRIM_MODES: readonly TrimMode[] = ['ripple', 'roll', 'slip', 'slide'];
const TRIM_EDGES: readonly TrimEdge[] = ['in', 'out'];
const PROPERTY_KEYS: readonly PropertyKey[] = [
  'name',
  'enabled',
  'text',
  'fontSize',
  'fontFamily',
  'color',
  'language',
  'playbackRate',
  'sourceIn',
  'gainDb',
  'transform',
  'crop',
  'reverse',
  'hold',
  'fadeInFrames',
  'fadeOutFrames',
  'transitionType',
  'easing',
  'generatorType',
  'generatorConfig',
  'assetRef',
  'grade',
  'mask',
  'keyer',
  'effects',
  'stabilize',
  'reframe',
  'retimeQuality',
  'cornerPin',
  'matte',
  'quad',
  'template',
];

/** Every position at which `insert` is *accepted* — fits-in-gap, flush-at-next-start, trailing. */
function insertSites(doc: EditTimeline): Array<{ trackId: string; recordIn: number; duration: number }> {
  const sites: Array<{ trackId: string; recordIn: number; duration: number }> = [];
  for (const track of doc.tracks) {
    const items = track.items;
    if (items.length === 0) {
      for (let d = 1; d <= 4; d++) sites.push({ trackId: track.id, recordIn: d - 1, duration: d });
      continue;
    }
    for (const [start, end] of gapsOf(items)) {
      for (let d = 1; d <= 4; d++) {
        for (let pos = start; pos + d <= end; pos++) {
          sites.push({ trackId: track.id, recordIn: pos, duration: d });
        }
      }
    }
    for (const item of items) {
      for (let d = 1; d <= 4; d++) sites.push({ trackId: track.id, recordIn: item.recordIn, duration: d });
    }
    const last = items[items.length - 1];
    for (let d = 1; d <= 4; d++) sites.push({ trackId: track.id, recordIn: last.recordOut + d - 1, duration: d });
  }
  return sites;
}

/** Spans an `overwrite` may legally give the item at `index`, keeping it clear of its neighbours. */
function overwriteSpans(track: Track, index: number): Array<{ recordIn: number; recordOut: number }> {
  const previous = index > 0 ? track.items[index - 1] : undefined;
  const following = index + 1 < track.items.length ? track.items[index + 1] : undefined;
  const item = track.items[index];
  const low = previous === undefined ? 0 : previous.recordOut;
  const high = following === undefined ? Math.max(item.recordOut, low + 8) + 4 : following.recordIn;

  const spans: Array<{ recordIn: number; recordOut: number }> = [];
  for (let start = low; start < high; start++) {
    for (let end = start + 1; end <= high && end <= start + 8; end++) {
      spans.push({ recordIn: start, recordOut: end });
    }
  }
  return spans;
}

/** Positions an item of `duration` frames may legally be moved to, on every track. */
function moveDestinations(doc: EditTimeline, item: Item): Array<{ toTrackId: string; toRecordIn: number }> {
  const duration = item.recordOut - item.recordIn;
  const destinations: Array<{ toTrackId: string; toRecordIn: number }> = [];

  for (const track of doc.tracks) {
    if (track.kind === 'audio' && PICTURE_ONLY_ITEM_TYPES.includes(item.type)) continue; // d1 N5
    // `move` removes the item before it looks for a destination, so the source slot is already
    // free when this track is the item's own.
    const rest = track.items.filter((candidate) => candidate.id !== item.id);
    if (rest.length === 0) {
      for (let pos = 0; pos <= 4; pos++) destinations.push({ toTrackId: track.id, toRecordIn: pos });
      continue;
    }
    for (let pos = 0; pos + duration <= rest[0].recordIn; pos++) {
      destinations.push({ toTrackId: track.id, toRecordIn: pos });
    }
    for (const [start, end] of gapsOf(rest)) {
      for (let pos = start; pos + duration <= end; pos++) {
        destinations.push({ toTrackId: track.id, toRecordIn: pos });
      }
    }
    const last = rest[rest.length - 1];
    for (let pos = last.recordOut; pos <= last.recordOut + 4; pos++) {
      destinations.push({ toTrackId: track.id, toRecordIn: pos });
    }
  }
  return destinations;
}

/** Modes that succeed on `located` — the ones whose required neighbour (or source offset) exists. */
function trimModes(located: Located, track: Track): Array<{ edge: TrimEdge; mode: TrimMode }> {
  const { index, item } = located;
  const hasPrevious = index > 0;
  const hasFollowing = index + 1 < track.items.length;

  const modes: Array<{ edge: TrimEdge; mode: TrimMode }> = [
    { edge: 'out', mode: 'ripple' },
    { edge: 'in', mode: 'ripple' },
  ];
  if (hasFollowing) modes.push({ edge: 'out', mode: 'roll' }, { edge: 'out', mode: 'slide' });
  if (hasPrevious) modes.push({ edge: 'in', mode: 'roll' }, { edge: 'in', mode: 'slide' });
  if (sourceInOf(item) !== undefined) modes.push({ edge: 'out', mode: 'slip' }, { edge: 'in', mode: 'slip' });
  return modes;
}

/**
 * A `deltaFrames` drawn from a pool that is mostly *out of range* on purpose: the op documents
 * `deltaFrames` as a request rather than a promise, and the property that matters is that a
 * clamped request still round-trips on the effective delta.
 */
function deltaFrames(e: Entropy): number {
  const pool = [-40, -12, -3, -1, 0, 1, 3, 12, 40];
  return e.bool() ? (e.pick(pool) ?? 0) : e.between(-20, 20);
}

/** A legal value for `key`: the shape `assertPropertyValue` accepts. */
function legalValue(key: PropertyKey, e: Entropy): PropertyValue {
  switch (key) {
    case 'name':
      // Never `undefined`: `name` is required by `assertInvariants` (see the known-issue property).
      return `n${e.below(1000)}`;
    case 'text':
    case 'fontFamily':
    case 'color':
    case 'language':
      return e.below(4) === 0 ? undefined : `s${e.below(100)}`;
    case 'enabled':
      return e.below(4) === 0 ? undefined : e.bool();
    case 'playbackRate':
      return 0.25 + e.below(40) / 10;
    case 'sourceIn':
      return e.below(4) === 0 ? undefined : e.below(30);
    case 'fontSize':
      return e.below(200) - 50;
    case 'gainDb': {
      // `gainDb` is a `Keyframed<number>`: a bare number is NOT a legal value for it (the engine
      // refuses it), so every branch here is an object, `null` or `undefined`.
      const variant = e.below(5);
      if (variant === 0) return null;
      if (variant === 1) return undefined;
      if (variant === 2) return { value: e.below(41) - 20, keys: null } satisfies Keyframed<number>;
      // Never `value: null` (eng D4): the server binds `Keyframed<double>.Value` as a double.
      if (variant === 3) return { keys: [] } satisfies Keyframed<number>;
      return {
        keys: [{ frame: e.below(20), value: e.below(41) - 20, interp: 'linear' }],
      } satisfies Keyframed<number>;
    }
    // ── P5 keys ───────────────────────────────────────────────────────────
    case 'transform': {
      const variant = e.below(4);
      if (variant === 0) return null;
      if (variant === 1) return undefined;
      if (variant === 2) return { scale: { value: 0.5 + e.below(30) / 10, keys: null }, opacity: { value: e.below(11) / 10 } };
      return { positionX: { keys: [{ frame: e.below(20), value: e.below(41) / 20 - 1, interp: 'linear' }, { frame: 20 + e.below(20), value: 0, interp: 'step' }] } };
    }
    case 'crop': {
      const variant = e.below(3);
      if (variant === 0) return null;
      if (variant === 1) return undefined;
      return { left: e.below(4) / 10, top: e.below(4) / 10, right: e.below(4) / 10, bottom: e.below(4) / 10 };
    }
    case 'reverse':
    case 'hold':
      return e.below(4) === 0 ? undefined : e.bool();
    case 'fadeInFrames':
    case 'fadeOutFrames':
      return e.below(4) === 0 ? undefined : e.below(10);
    case 'transitionType':
      // Required: never `undefined`.
      return TRANSITION_TYPES[e.below(TRANSITION_TYPES.length)];
    case 'easing':
      return e.below(4) === 0 ? undefined : INTERP_KINDS[e.below(INTERP_KINDS.length)];
    case 'generatorType':
      // Required: never `undefined`.
      return ['color', 'bars', 'testsrc'][e.below(3)];
    case 'generatorConfig': {
      const variant = e.below(3);
      if (variant === 0) return null;
      if (variant === 1) return undefined;
      return { color: `#${(e.below(0xffffff) + 0x1000000).toString(16).slice(1).padStart(6, '0')}` };
    }
    case 'assetRef':
      // Required: never `undefined`. P6 relink — either kind, with the id that kind names.
      return e.bool()
        ? { kind: 'projectFile', projectFileId: `00000000-0000-4000-8000-${String(e.below(1000)).padStart(12, '0')}` }
        : { kind: 'stepResult', stepResultId: `00000000-0000-4000-8000-${String(1000 + e.below(1000)).padStart(12, '0')}` };
    // ── P7 keys ───────────────────────────────────────────────────────────
    case 'grade': {
      const variant = e.below(4);
      if (variant === 0) return null;
      if (variant === 1) return undefined;
      if (variant === 2) return { enabled: e.bool(), nodes: [] };
      return {
        enabled: true,
        nodes: [
          { id: `n${e.below(100)}`, name: 'Primary', enabled: true, primaries: { lift: { r: e.below(11) / 10 - 0.5 }, contrast: 0.5 + e.below(10) / 10, saturation: e.below(21) / 10, exposure: e.below(9) - 4 } },
          { id: `m${e.below(100)}`, curves: { y: [{ x: 0.25, y: e.below(11) / 10 }, { x: 0.75, y: e.below(11) / 10 }] }, lut: { file: { kind: 'projectFile', projectFileId: '00000000-0000-4000-8000-000000000777' }, intensity: e.below(11) / 10 } },
        ],
      };
    }
    case 'mask': {
      const variant = e.below(3);
      if (variant === 0) return null;
      if (variant === 1) return undefined;
      return { kind: e.bool() ? 'rect' : 'ellipse', x: e.below(11) / 10, y: e.below(11) / 10, w: e.below(11) / 10, h: e.below(11) / 10, feather: e.below(6) / 10, invert: e.bool() };
    }
    case 'keyer':
    case 'matte': {
      const variant = e.below(3);
      if (variant === 0) return null;
      if (variant === 1) return undefined;
      return { color: e.bool() ? '#00FF00' : '#1a2B3c', similarity: (1 + e.below(10)) / 10, blend: e.below(11) / 10, spill: e.below(11) / 10 };
    }
    case 'effects': {
      const variant = e.below(3);
      if (variant === 0) return null;
      if (variant === 1) return undefined;
      return [{ type: EFFECT_TYPES[e.below(EFFECT_TYPES.length)] as ClipEffect['type'], amount: e.below(11) / 10, enabled: e.bool() }];
    }
    case 'stabilize':
      return e.below(4) === 0 ? undefined : e.bool();
    case 'reframe':
      return e.below(3) === 0 ? null : e.below(3) === 1 ? undefined : { x: e.below(11) / 10, y: e.below(11) / 10 };
    case 'retimeQuality':
      return e.below(4) === 0 ? undefined : e.bool() ? 'frame' : 'opticalFlow';
    case 'cornerPin': {
      const variant = e.below(3);
      if (variant === 0) return null;
      if (variant === 1) return undefined;
      const p = () => ({ x: e.below(31) / 10 - 1, y: e.below(31) / 10 - 1 });
      return { topLeft: p(), topRight: p(), bottomRight: p(), bottomLeft: p() };
    }
    case 'quad': {
      const variant = e.below(3);
      if (variant === 0) return null;
      if (variant === 1) return undefined;
      const corners = { topLeft: { x: 0, y: 0 }, topRight: { x: 1, y: 0 }, bottomRight: { x: 1, y: 1 }, bottomLeft: { x: 0, y: 1 } };
      return { value: corners, keys: [{ frame: e.below(20), value: corners, interp: 'linear' }, { frame: 20 + e.below(20), value: corners, interp: 'step' }] };
    }
    case 'template':
      return e.below(3) === 0 ? null : e.below(3) === 1 ? undefined : TITLE_TEMPLATES[e.below(TITLE_TEMPLATES.length)];
    // ── P8 keys ───────────────────────────────────────────────────────────
    case 'channelMap':
      return e.below(4) === 0 ? undefined : CHANNEL_MAPS[e.below(CHANNEL_MAPS.length)];
    case 'sequenceId':
      // Required, and must name a sequence of the document: every generated document has one.
      return PROP_SEQUENCE_ID;
    case 'activeAngleId':
      return PROP_ANGLES[e.below(PROP_ANGLES.length)].id;
    case 'cuts':
      // A cut needs a frame inside the clip; the span-aware cut cases live in the multicam unit tests.
      return e.below(3) === 0 ? null : e.below(3) === 1 ? undefined : [];
    case 'angles':
      return (e.bool() ? [...PROP_ANGLES] : [...PROP_ANGLES].reverse()).map((a) => ({ ...a, assetRef: projectFile(`${a.id}-x`) }));
  }
}

/** A deliberately wrong-typed value for `key`, to exercise the rejection path. */
/**
 * {@link legalValue}, kept legal for THIS item across fields (eng D6, the validator's clip-depth
 * rules): a fade never longer than the room the other fade leaves, never reverse with hold.
 */
function legalValueFor(item: Item, key: PropertyKey, e: Entropy): PropertyValue {
  const value = legalValue(key, e);
  if (item.type !== 'mediaClip') return value;
  if ((key === 'fadeInFrames' || key === 'fadeOutFrames') && typeof value === 'number') {
    const other = (key === 'fadeInFrames' ? item.fadeOutFrames : item.fadeInFrames) ?? 0;
    return Math.min(value, Math.max(0, item.recordOut - item.recordIn - other));
  }
  if ((key === 'reverse' && value === true && item.hold === true) || (key === 'hold' && value === true && item.reverse === true)) return false;
  return value;
}

function illegalValue(key: PropertyKey, e: Entropy): PropertyValue {
  switch (key) {
    case 'enabled':
      return `not-a-boolean-${e.below(10)}`;
    case 'playbackRate':
      return e.bool() ? 0 : -3;
    case 'fontSize':
      return `not-a-number-${e.below(10)}`;
    case 'gainDb':
      return e.bool() ? 'not-a-keyframed-number' : ({ value: 'x' } as unknown as Keyframed<number>);
    case 'transform':
      return e.bool() ? { opacity: { value: 7 } } : { wobble: { value: 1 } };
    case 'crop':
      return e.bool() ? { left: 0.7, right: 0.6 } : { left: -1 };
    case 'reverse':
    case 'hold':
      return 'yes';
    case 'fadeInFrames':
    case 'fadeOutFrames':
    case 'sourceIn':
      return e.bool() ? -3 : 1.5;
    case 'transitionType':
    case 'generatorType':
      return 'sparkle';
    case 'easing':
      return 'bouncy';
    case 'generatorConfig':
      return { color: 'red' };
    case 'assetRef':
      return e.bool() ? { kind: 'projectFile' } : { kind: 'url', projectFileId: 'x' };
    case 'grade':
      return e.bool() ? { nodes: [{ id: 'n', primaries: { contrast: 9 } }] } : { nodes: [{ id: 'n', curves: { y: [{ x: 0.5, y: 0.5 }, { x: 0.5, y: 0.6 }] } }] };
    case 'mask':
      return e.bool() ? { kind: 'star' } : { x: 2 };
    case 'keyer':
    case 'matte':
      return e.bool() ? { color: 'green' } : { similarity: 0 };
    case 'effects':
      return e.bool() ? ([{ type: 'sparkle' }] as unknown as ClipEffect[]) : [{ type: 'blur', amount: 3 }];
    case 'stabilize':
      return 'yes';
    case 'reframe':
      return { x: 1.5 };
    case 'retimeQuality':
      return 'magic';
    case 'cornerPin':
      return { topLeft: { x: 0, y: 0 } };
    case 'quad':
      return { keys: [{ frame: 1.5, value: {}, interp: 'linear' }] };
    case 'template':
      return 'neon';
    case 'channelMap':
      return 'surround';
    case 'sequenceId':
    case 'activeAngleId':
      return e.bool() ? '' : 7;
    case 'cuts':
      return 'cut';
    case 'angles':
      return [];
    default:
      return e.below(1000);
  }
}

/** A fresh item id no generated document can already hold. */
const freshItemId = (seq: number): string => `new-item-${seq}`;
const freshMarkerId = (seq: number): string => `new-marker-${seq}`;

/**
 * Builds one op that is **valid by construction** against `doc`: every id it names exists (or is
 * fresh on purpose), every position it names is one the engine accepts. Returns `undefined` when
 * the document offers no site for that op, in which case the caller falls back to a universally
 * legal op rather than drawing a different one (which would bias the distribution).
 */
function buildValidOp(
  doc: EditTimeline,
  e: Entropy,
  seq: number,
  opName: EditOp['type'],
): EditOp | undefined {
  switch (opName) {
    case 'insert': {
      const site = e.pick(insertSites(doc));
      if (site === undefined) return undefined;
      const track = doc.tracks.find((candidate) => candidate.id === site.trackId);
      if (track === undefined) return undefined;
      return {
        type: 'insert',
        trackId: site.trackId,
        index: canonicalIndex(track.items, site.recordIn),
        item: makeItem(freshItemId(seq), placeableKind(e, track.kind), site.recordIn, site.recordIn + site.duration, undefined),
      };
    }

    case 'overwrite': {
      const located = e.pick(allItems(doc));
      if (located === undefined) return undefined;
      // A transition keeps its span (it sits exactly on its cut); any other item may re-span.
      const span = located.item.type === 'transition' ? located.item : e.pick(overwriteSpans(located.track, located.index));
      if (span === undefined) return undefined;
      return { type: 'overwrite', itemId: located.item.id, item: withSpan(located.item, span.recordIn, span.recordOut) };
    }

    case 'lift': {
      const located = e.pick(allItems(doc));
      return located === undefined ? undefined : { type: 'lift', itemId: located.item.id };
    }

    case 'extract': {
      const located = e.pick(allItems(doc));
      return located === undefined ? undefined : { type: 'extract', itemId: located.item.id };
    }

    case 'trim': {
      const located = e.pick(allItems(doc));
      if (located === undefined) return undefined;
      const mode = e.pick(trimModes(located, located.track));
      if (mode === undefined) return undefined;
      return {
        type: 'trim',
        itemId: located.item.id,
        edge: mode.edge,
        mode: mode.mode,
        deltaFrames: deltaFrames(e),
      };
    }

    case 'blade': {
      const candidates = allItems(doc).filter((located) => located.item.recordOut - located.item.recordIn >= 2);
      const located = e.pick(candidates);
      if (located === undefined) return undefined;
      return {
        type: 'blade',
        itemId: located.item.id,
        atFrame: e.between(located.item.recordIn + 1, located.item.recordOut - 1),
        newItemId: freshItemId(seq),
      };
    }

    case 'deleteGap': {
      const sites: Array<{ trackId: string; atFrame: number }> = [];
      for (const track of doc.tracks) {
        for (const [start, end] of gapsOf(track.items)) {
          for (let frame = start; frame < end; frame++) sites.push({ trackId: track.id, atFrame: frame });
        }
      }
      const site = e.pick(sites);
      return site === undefined ? undefined : { type: 'deleteGap', trackId: site.trackId, atFrame: site.atFrame };
    }

    case 'closeGaps': {
      const track = e.pick(doc.tracks);
      return track === undefined ? undefined : { type: 'closeGaps', trackId: track.id };
    }

    case 'move': {
      const located = e.pick(allItems(doc).filter((candidate) => candidate.item.type !== 'transition'));
      if (located === undefined) return undefined;
      const destination = e.pick(moveDestinations(doc, located.item));
      if (destination === undefined) return undefined;
      return {
        type: 'move',
        itemId: located.item.id,
        toTrackId: destination.toTrackId,
        toRecordIn: destination.toRecordIn,
      };
    }

    case 'setProperty': {
      const located = e.pick(allItems(doc));
      if (located === undefined) return undefined;
      const key = e.pick(SET_PROPERTY_ALLOWLIST[located.item.type]);
      if (key === undefined) return undefined;
      return { type: 'setProperty', itemId: located.item.id, key, value: legalValueFor(located.item, key, e) };
    }

    case 'addMarker': {
      const frame = e.between(0, 40);
      const marker: Marker = {
        id: freshMarkerId(seq),
        frame,
        name: `generated-${seq}`,
        color: null,
        note: null,
        durationFrames: null,
      };
      // Both branches of the optional slot: omitted (insert-after-equals) and explicit-canonical.
      if (e.bool()) return { type: 'addMarker', marker };
      let index = 0;
      while (index < doc.markers.length && doc.markers[index].frame <= frame) index++;
      return { type: 'addMarker', marker, index };
    }

    case 'setMarker': {
      const index = e.below(doc.markers.length);
      if (doc.markers.length === 0) return undefined;
      const marker = doc.markers[index];
      const patch: Partial<Marker> = {};
      if (e.bool()) patch.name = `renamed-${e.below(100)}`;
      if (e.bool()) patch.color = e.bool() ? `#${e.below(1000)}` : null;
      if (e.bool()) patch.note = e.bool() ? undefined : `note-${e.below(100)}`;
      if (e.bool()) patch.durationFrames = e.bool() ? null : e.below(20);
      if (e.bool()) {
        // A frame is only legal inside the slot its neighbours leave; and never negative.
        const low = index > 0 ? doc.markers[index - 1].frame : 0;
        const high = index + 1 < doc.markers.length ? doc.markers[index + 1].frame : low + 5;
        patch.frame = e.between(low, high);
      }
      if (Object.keys(patch).length === 0) patch.name = `renamed-${e.below(100)}`;
      return { type: 'setMarker', markerId: marker.id, patch };
    }

    case 'removeMarker': {
      const marker = e.pick(doc.markers);
      return marker === undefined ? undefined : { type: 'removeMarker', markerId: marker.id };
    }

    // ── P4 ops ──────────────────────────────────────────────────────────────

    case 'append': {
      const track = e.pick(doc.tracks);
      if (track === undefined) return undefined;
      const duration = e.between(1, 8);
      // The span the item arrives with is deliberately NOT where it lands: only its duration counts.
      const arrivesAt = e.between(0, 50);
      return { type: 'append', trackId: track.id, item: makeItem(freshItemId(seq), placeableKind(e, track.kind), arrivesAt, arrivesAt + duration, undefined) };
    }

    case 'replace': {
      const located = e.pick(allItems(doc));
      if (located === undefined) return undefined;
      // A different kind, a different id and a different span: the engine must keep the original id and span.
      const kind = located.item.type === 'transition' ? 'transition' : placeableKind(e, located.track.kind);
      const replacement = makeItem(`ignored-${seq}`, kind, 100, 200, e.bool() ? 3 : undefined);
      return { type: 'replace', itemId: located.item.id, item: replacement };
    }

    case 'fitToFill': {
      const located = e.pick(allItems(doc).filter((candidate) => candidate.item.type !== 'transition'));
      if (located === undefined) return undefined;
      const span = e.pick(overwriteSpans(located.track, located.index));
      if (span === undefined) return undefined;
      return { type: 'fitToFill', itemId: located.item.id, recordIn: span.recordIn, recordOut: span.recordOut };
    }

    case 'placeOnTop': {
      // The free sites are exactly the gap-fitting insert sites (never the flush ripple position).
      const site = e.pick(insertSites(doc).filter((candidate) => {
        const track = doc.tracks.find((t) => t.id === candidate.trackId);
        if (track === undefined) return false;
        const idx = canonicalIndex(track.items, candidate.recordIn);
        const following = track.items[idx];
        return following === undefined || candidate.recordIn + candidate.duration <= following.recordIn;
      }));
      if (site === undefined) return undefined;
      const track = doc.tracks.find((t) => t.id === site.trackId) as Track;
      return { type: 'placeOnTop', trackId: site.trackId, item: makeItem(freshItemId(seq), placeableKind(e, track.kind), site.recordIn, site.recordIn + site.duration, undefined) };
    }

    case 'join': {
      // Only a blade-shaped pair joins, which an arbitrary document rarely has by chance: the
      // builder names the blade it wants first and `materializeJoin` performs it, so the join is
      // still generated against a document the engine itself produced.
      const candidates = allItems(doc).filter((located) => located.item.recordOut - located.item.recordIn >= 2);
      const located = e.pick(candidates);
      if (located === undefined) return undefined;
      const pending: Pending = {
        type: 'join',
        itemId: located.item.id,
        __first: [{ type: 'blade', itemId: located.item.id, atFrame: e.between(located.item.recordIn + 1, located.item.recordOut - 1), newItemId: freshItemId(seq) }],
      };
      return pending;
    }

    // ── P5 ────────────────────────────────────────────────────────────────

    case 'setKeyframe': {
      const draw = keyframeDraw(doc, e);
      if (draw === undefined) return undefined;
      return { type: 'setKeyframe', ...draw, frame: e.between(0, 60), value: keyframeValue(e, draw.param), interp: e.pick(INTERP_KINDS) ?? 'linear' };
    }

    case 'removeKeyframe':
    case 'moveKeyframe': {
      // A generated document carries no keyframes: the builder sets the key it wants first.
      const draw = keyframeDraw(doc, e);
      if (draw === undefined) return undefined;
      const frame = e.between(0, 60);
      const first: EditOp = { type: 'setKeyframe', ...draw, frame, value: keyframeValue(e, draw.param), interp: e.pick(INTERP_KINDS) ?? 'linear' };
      const op: EditOp = opName === 'removeKeyframe'
        ? { type: 'removeKeyframe', ...draw, frame }
        : { type: 'moveKeyframe', ...draw, frame, toFrame: frame + e.between(1, 9) };
      const pending: Pending = { ...op, __first: [first] };
      return pending;
    }

    case 'retime': {
      const clips = allItems(doc).filter((located) => located.item.type === 'mediaClip');
      const located = e.pick(clips);
      if (located === undefined) return undefined;
      const rate = e.pick([0.5, 0.8, 1, 1.25, 2, 4]) ?? 1;
      // The retimed clip must stay at least as long as its fades (engine rule, eng D6): a fade-in set earlier in the run
      // can otherwise outlast a 1-frame retime, which `apply` refuses by design.
      const fades = located.item.type === 'mediaClip' ? (located.item.fadeInFrames ?? 0) + (located.item.fadeOutFrames ?? 0) : 0;
      return { type: 'retime', itemId: located.item.id, playbackRate: rate, recordOut: located.item.recordIn + Math.max(e.between(1, 12), fades) };
    }

    case 'addTransition':
    case 'removeTransition': {
      // Adjacent media pairs are rare by chance: blade a clip of 4+ frames so the pair is an exact
      // continuation whose incoming half owns `leftLength` frames of handle (when it has a source offset).
      const clips = allItems(doc).filter((located) => located.item.type === 'mediaClip' && located.item.recordOut - located.item.recordIn >= 4);
      const located = e.pick(clips);
      if (located === undefined) return undefined;
      const atFrame = e.between(located.item.recordIn + 2, located.item.recordOut - 2);
      const leftLength = atFrame - located.item.recordIn;
      const rightLength = located.item.recordOut - atFrame;
      const sourceIn = sourceInOf(located.item);
      const handle = sourceIn === undefined ? 0 : sourceIn + leftLength;
      const framesBefore = e.between(0, Math.min(handle, leftLength - 1));
      const framesAfter = e.between(framesBefore === 0 ? 1 : 0, rightLength - 1);
      const newItemId = freshItemId(seq);
      const transitionId = `${newItemId}-x`;
      const first: EditOp[] = [
        { type: 'blade', itemId: located.item.id, atFrame, newItemId },
        {
          type: 'addTransition',
          afterItemId: located.item.id,
          transition: { id: transitionId, type: 'transition', name: 'T', transitionType: e.pick(TRANSITION_TYPES) ?? 'fade', easing: e.pick(INTERP_KINDS) ?? 'linear', enabled: true, provenance: null },
          framesBefore,
          framesAfter,
        },
      ];
      if (opName === 'addTransition') {
        const pending: Pending = { ...(first[1] as EditOp), __first: [first[0]] };
        return pending;
      }
      // Any cut inside the span the incoming clip can afford: its sourceIn is `handle + framesAfter`.
      const span = framesBefore + framesAfter;
      const pullBack = e.between(0, Math.min(span, handle + framesAfter));
      const cutFrame = atFrame + framesAfter - pullBack;
      const pending: Pending = { type: 'removeTransition', transitionId, cutFrame, __first: first };
      return pending;
    }

    case 'nudge': {
      // Only an item with room on at least one side can be nudged; a boxed-in item is skipped.
      const sites: Array<{ itemId: string; deltaFrames: number }> = [];
      for (const located of allItems(doc)) {
        if (located.item.type === 'transition') continue; // it sits exactly on its cut
        const items = located.track.items;
        const previous = located.index > 0 ? items[located.index - 1] : undefined;
        const following = located.index + 1 < items.length ? items[located.index + 1] : undefined;
        const low = (previous === undefined ? 0 : previous.recordOut) - located.item.recordIn;
        const high = following === undefined ? 6 : following.recordIn - located.item.recordOut;
        for (let d = low; d <= high; d++) if (d !== 0) sites.push({ itemId: located.item.id, deltaFrames: d });
      }
      const site = e.pick(sites);
      return site === undefined ? undefined : { type: 'nudge', itemId: site.itemId, deltaFrames: site.deltaFrames };
    }

    case 'addTrack': {
      const items: Item[] = [];
      const kind: Track['kind'] = e.bool() ? 'video' : 'audio';
      if (e.bool()) {
        let cursor = e.between(0, 3);
        const count = e.between(1, 3);
        for (let i = 0; i < count; i++) {
          const length = e.between(1, 5);
          items.push(makeItem(`${freshItemId(seq)}-${i}`, placeableKind(e, kind), cursor, cursor + length, undefined));
          cursor += length + e.between(0, 2);
        }
      }
      return {
        type: 'addTrack',
        index: e.between(0, doc.tracks.length),
        track: { id: `generated-track-${seq}`, kind, name: `generated ${seq}`, color: null, height: 48, locked: false, enabled: true, mute: false, solo: false, busId: null, items },
      };
    }

    case 'removeTrack': {
      const track = e.pick(doc.tracks);
      return track === undefined ? undefined : { type: 'removeTrack', trackId: track.id };
    }

    case 'moveTrack': {
      if (doc.tracks.length < 2) return undefined;
      const from = e.below(doc.tracks.length);
      let to = e.below(doc.tracks.length);
      if (to === from) to = (to + 1) % doc.tracks.length;
      return { type: 'moveTrack', trackId: doc.tracks[from].id, toIndex: to };
    }

    case 'setTrackProperty': {
      const track = e.pick(doc.tracks);
      if (track === undefined) return undefined;
      const keys = ['name', 'color', 'height', 'locked', 'enabled', 'mute', 'solo', 'busId'] as const;
      const key = e.pick(keys) ?? 'name';
      const value =
        key === 'name' ? `renamed-${e.below(100)}`
          : key === 'color' || key === 'busId' ? (e.bool() ? null : `v-${e.below(100)}`)
            : key === 'height' ? e.between(8, 200)
              : e.bool();
      return { type: 'setTrackProperty', trackId: track.id, key, value };
    }

    case 'setTimelineSettings': {
      const patch: Record<string, unknown> = {};
      if (e.bool()) patch.width = e.between(16, 4096);
      if (e.bool()) patch.height = e.between(16, 2160);
      if (e.bool()) patch.rate = { num: e.between(1, 60000), den: e.between(1, 1001) };
      if (e.bool()) patch.sampleRate = e.pick([44100, 48000, 96000]) ?? 48000;
      if (e.bool()) patch.pixelAspect = { num: e.between(1, 4), den: e.between(1, 4) };
      if (e.bool()) patch.aspectPolicy = e.pick(['fit', 'fill', 'stretch'] as const) ?? 'fit';
      if (e.bool()) patch.startTimecodeFrames = e.between(0, 100000);
      if (Object.keys(patch).length === 0) patch.width = e.between(16, 4096);
      return { type: 'setTimelineSettings', patch };
    }

    case 'setInOut': {
      const choice = e.below(3);
      if (choice === 0) return { type: 'setInOut', inOut: null };
      if (choice === 1) return { type: 'setInOut', inOut: undefined };
      const inFrame = e.between(0, 40);
      return { type: 'setInOut', inOut: { inFrame, outFrame: inFrame + e.between(1, 20) } };
    }

    case 'setColorGroup': {
      // Add a group over some media clips, replace an existing one, or remove one that exists.
      const existing = doc.colorGroups ?? [];
      // `setColorGroup` is strict on write, and its check covers **every** group in the document —
      // so a single member left dangling by an earlier `lift`/`extract`/`removeTrack` of this same
      // run makes all three branches illegal: an add or a replace is refused outright, and a remove
      // is accepted but its inverse (which restores the dangling group) is only honoured when the
      // undo path replays it with `{ inverse: true }`. This generator emits forward ops only, so there
      // is no legal site to offer here at all.
      const mediaIds = new Set(
        allItems(doc)
          .filter((located) => located.item.type === 'mediaClip')
          .map((located) => located.item.id),
      );
      if (existing.some((group) => group.clipIds.some((clipId) => !mediaIds.has(clipId)))) return undefined;
      const clips = allItems(doc).filter((located) => located.item.type === 'mediaClip' && !existing.some((g) => g.clipIds.includes(located.item.id)));
      const roll = e.below(3);
      if (roll === 0 && existing.length > 0) {
        const group = e.pick(existing);
        if (group) return { type: 'setColorGroup', groupId: group.id, group: undefined };
      }
      if (roll === 1 && existing.length > 0) {
        const group = e.pick(existing);
        if (group) return { type: 'setColorGroup', groupId: group.id, group: { ...group, name: `${group.name}'`, grade: { enabled: e.bool(), nodes: [{ id: 'n0', primaries: { saturation: e.below(21) / 10 } }] } } };
      }
      const members = clips.slice(0, 1 + e.below(2)).map((located) => located.item.id);
      const groupId = `group-${seq}`;
      return { type: 'setColorGroup', groupId, group: { id: groupId, name: `Group ${seq}`, clipIds: members, grade: e.bool() ? null : { nodes: [{ id: `${groupId}-n`, primaries: { exposure: e.below(5) - 2 } }] }, lookGroupId: e.bool() ? null : `k${e.below(9)}` } };
    }
    case 'setSequence': {
      // Add a sequence holding a fresh clip, replace an unplayed one, or remove an unplayed one.
      const played = new Set(allItems(doc).flatMap((l) => (l.item.type === 'compoundClip' ? [l.item.sequenceId] : [])));
      // The shared generator sequence stays: later builders draw compound clips that play it.
      const unplayed = (doc.sequences ?? []).filter((q) => !played.has(q.id) && q.id !== PROP_SEQUENCE_ID);
      const roll = e.below(3);
      if (roll === 0 && unplayed.length > 0) {
        const sequence = e.pick(unplayed);
        if (sequence) return { type: 'setSequence', sequenceId: sequence.id, sequence: undefined };
      }
      if (roll === 1 && (doc.sequences ?? []).length > 0) {
        const sequence = e.pick(doc.sequences ?? []);
        if (sequence) return { type: 'setSequence', sequenceId: sequence.id, sequence: { ...sequence, name: `${sequence.name}'` } };
      }
      const sequenceId = `seq-${seq}`;
      const length = e.between(1, 30);
      return {
        type: 'setSequence',
        sequenceId,
        sequence: { id: sequenceId, name: `Sequence ${seq}`, tracks: [{ id: `${sequenceId}-t`, kind: 'video', name: 'V1', height: 64, locked: false, enabled: true, mute: false, solo: false, items: [makeItem(`${sequenceId}-i`, 'titleClip', 0, length, undefined)] }] },
      };
    }
    case 'setBus': {
      const existing = doc.buses ?? [];
      const roll = e.below(3);
      if (roll === 0 && existing.length > 0) {
        const bus = e.pick(existing);
        if (bus) return { type: 'setBus', busId: bus.id, bus: undefined };
      }
      if (roll === 1 && existing.length > 0) {
        const bus = e.pick(existing);
        if (bus) return { type: 'setBus', busId: bus.id, bus: { ...bus, pan: (e.below(21) - 10) / 10, dynamics: { limiter: { ceilingDb: -e.below(24) } } } };
      }
      const busId = `bus-${seq}`;
      // Routing into an existing bus is fine as long as it terminates (a new bus has no inbound route).
      const output = e.bool() ? (e.pick(existing)?.id ?? null) : null;
      return { type: 'setBus', busId, bus: { id: busId, name: `Bus ${seq}`, outputBusId: output, leveler: e.bool() ? { kind: 'speech', targetDb: -e.below(40) } : null } };
    }
    default:
      return undefined;
  }
}

/**
 * An op the builder could not yet place on the generated document: the ops it needs first ride
 * along until `materializePending` runs them (a `join` wants its blade; a `removeKeyframe` wants
 * its key; a transition op wants the adjacent pair). Every property still generates its op against
 * a document the engine itself produced.
 */
type Pending = EditOp & { __first: EditOp[] };

function isPending(op: EditOp): op is Pending {
  return '__first' in op;
}

function materializePending(doc: EditTimeline, op: EditOp): { document: EditTimeline; op: EditOp } {
  if (!isPending(op)) return { document: doc, op };
  const { __first, ...real } = op;
  return { document: applyAll(doc, __first), op: real as EditOp };
}

/** The keyframe target and channel a builder draws: a media clip's channel when one exists, else a track fader. */
function keyframeDraw(doc: EditTimeline, e: Entropy): { target: KeyframeTarget; param: KeyframeParam } | undefined {
  const clips = allItems(doc).filter((located) => located.item.type === 'mediaClip');
  const clip = e.pick(clips);
  if (clip !== undefined && e.below(4) !== 0) {
    const params: KeyframeParam[] = ['gainDb', 'positionX', 'positionY', 'scale', 'rotation', 'opacity'];
    return { target: { itemId: clip.item.id }, param: e.pick(params) ?? 'gainDb' };
  }
  const track = e.pick(doc.tracks);
  if (track === undefined) return undefined;
  return { target: { trackId: track.id }, param: 'gainDb' };
}

/** A value strictly inside the channel's range (the range edges have their own refusal property). */
function keyframeValue(e: Entropy, param: KeyframeParam): number {
  const range = KEYFRAME_PARAM_RANGE[param];
  const steps = 8;
  const step = e.between(1, steps - 1);
  return range.min + ((range.max - range.min) * step) / steps;
}

/**
 * A universally legal op — the fallback when a site is missing. `closeGaps` on the first track, or,
 * once a run has removed every track (a P4 `removeTrack` may), the always-legal `setInOut`.
 */
function fallbackOp(doc: EditTimeline): EditOp {
  const track = doc.tracks[0];
  return track === undefined ? { type: 'setInOut', inOut: null } : { type: 'closeGaps', trackId: track.id };
}

function buildValidOpOrFallback(doc: EditTimeline, e: Entropy, seq: number): EditOp {
  const opName = COVERED_OPS[e.below(COVERED_OPS.length)];
  const op = buildValidOp(doc, e, seq, opName) ?? fallbackOp(doc);
  // The plan runner applies ops to the document it holds and cannot run a preamble first, so a
  // pending op is replaced by the first op it wanted (a blade, a setKeyframe): that op's own inverse
  // is exercised instead, and the pending op keeps its dedicated property.
  if (isPending(op)) {
    return op.__first[0];
  }
  // A run never locks a track: every later builder draws ids from the whole document and a locked
  // track would turn its legal-by-construction op into a refusal. Locking keeps its own single-op
  // property (where the generated value is random) and the seeded unlock case.
  if (op.type === 'setTrackProperty' && op.key === 'locked') {
    return { ...op, value: false };
  }
  return op;
}

/**
 * The **unfiltered** builder: the same thirteen ops, but with positions, ids, keys and neighbours
 * chosen without regard for whether the engine accepts them. Used to assert the either/or
 * contract — a legal op round-trips, an illegal one is *refused* — rather than to assert success.
 */
function buildUnfilteredOp(
  doc: EditTimeline,
  e: Entropy,
  seq: number,
  opName: EditOp['type'],
): EditOp {
  const items = allItems(doc);
  const anyTrack = () => e.pick(doc.tracks);
  const anyItem = () => e.pick(items);
  const anyMarker = () => e.pick(doc.markers);

  switch (opName) {
    case 'insert': {
      const track = anyTrack();
      if (track === undefined) return fallbackOp(doc);
      const recordIn = e.between(0, 60);
      const duration = e.between(1, 6);
      const index = e.bool() ? e.below(track.items.length + 1) : canonicalIndex(track.items, recordIn);
      return {
        type: 'insert',
        trackId: track.id,
        index,
        item: makeItem(
          freshItemId(seq),
          e.pick(ITEM_KINDS) ?? 'caption',
          recordIn,
          recordIn + duration,
          undefined,
        ),
      };
    }

    case 'overwrite': {
      const located = anyItem();
      if (located === undefined) return fallbackOp(doc);
      const recordIn = e.between(0, 60);
      const replacement = withSpan(located.item, recordIn, recordIn + e.between(1, 8));
      // A changed id must be refused; a legal span must round-trip.
      if (e.below(5) === 0) replacement.id = freshItemId(seq);
      return { type: 'overwrite', itemId: located.item.id, item: replacement };
    }

    case 'lift':
    case 'extract': {
      const located = anyItem();
      if (located === undefined) return fallbackOp(doc);
      return { type: opName, itemId: located.item.id };
    }

    case 'trim': {
      const located = anyItem();
      if (located === undefined) return fallbackOp(doc);
      return {
        type: 'trim',
        itemId: located.item.id,
        edge: e.pick(TRIM_EDGES) ?? 'out',
        mode: e.pick(TRIM_MODES) ?? 'ripple',
        // A non-integer delta must be refused; a clamped one must round-trip on its effective
        // delta, so most of these are deliberately far outside the legal range.
        deltaFrames: e.bool() ? e.between(-80, 80) : e.between(-80, 80) + 0.5,
      };
    }

    case 'blade': {
      const located = anyItem();
      if (located === undefined) return fallbackOp(doc);
      return {
        type: 'blade',
        itemId: located.item.id,
        atFrame: e.between(0, 60),
        newItemId: e.below(6) === 0 ? located.item.id : freshItemId(seq),
      };
    }

    case 'deleteGap': {
      const track = anyTrack();
      return track === undefined
        ? fallbackOp(doc)
        : { type: 'deleteGap', trackId: track.id, atFrame: e.between(0, 60) };
    }

    case 'closeGaps': {
      const track = anyTrack();
      return track === undefined ? fallbackOp(doc) : { type: 'closeGaps', trackId: track.id };
    }

    case 'move': {
      const located = anyItem();
      const track = anyTrack();
      if (located === undefined || track === undefined) return fallbackOp(doc);
      return {
        type: 'move',
        itemId: located.item.id,
        toTrackId: track.id,
        toRecordIn: e.between(0, 60),
      };
    }

    case 'setProperty': {
      const located = anyItem();
      if (located === undefined) return fallbackOp(doc);
      // Half the time an allowlisted key (which must succeed), half the time any key at all
      // (which is refused for most kinds); and half of each with a wrongly-typed value.
      const key = e.bool()
        ? (e.pick(SET_PROPERTY_ALLOWLIST[located.item.type]) ?? 'name')
        : (e.pick(PROPERTY_KEYS) ?? 'name');
      const value = e.bool() ? illegalValue(key, e) : legalValue(key, e);
      return { type: 'setProperty', itemId: located.item.id, key, value };
    }

    case 'addMarker': {
      // A duplicate id must be refused.
      const duplicate = e.below(5) === 0 ? anyMarker() : undefined;
      const marker: Marker = {
        id: duplicate?.id ?? freshMarkerId(seq),
        frame: e.between(0, 40),
        name: `unfiltered-${seq}`,
        color: null,
        note: null,
        durationFrames: null,
      };
      return e.bool()
        ? { type: 'addMarker', marker }
        : { type: 'addMarker', marker, index: e.below(doc.markers.length + 2) };
    }

    case 'setMarker': {
      const marker = anyMarker();
      if (marker === undefined) return fallbackOp(doc);
      return {
        type: 'setMarker',
        markerId: marker.id,
        // A frame that crosses a neighbour must be refused; a name patch must round-trip.
        patch: e.bool() ? { frame: e.between(0, 60) } : { name: `unfiltered-${e.below(100)}` },
      };
    }

    case 'removeMarker': {
      const marker = anyMarker();
      return { type: 'removeMarker', markerId: marker?.id ?? freshMarkerId(seq) };
    }

    // ── P4 ops, unfiltered: stale ids, out-of-range positions and illegal values are all drawn ─

    case 'append': {
      const track = anyTrack();
      return { type: 'append', trackId: e.bool() ? (track?.id ?? 'stale') : 'stale-track', item: makeItem(e.bool() ? freshItemId(seq) : (anyItem()?.item.id ?? 'dup'), 'titleClip', 0, e.between(0, 3), undefined) };
    }
    case 'replace': {
      const item = anyItem();
      return { type: 'replace', itemId: e.bool() ? (item?.item.id ?? 'stale') : 'stale-item', item: makeItem('x', e.pick(ITEM_KINDS) ?? 'caption', 0, 1, e.bool() ? -1 : undefined) };
    }
    case 'fitToFill': {
      const item = anyItem();
      const recordIn = e.between(-2, 40);
      return { type: 'fitToFill', itemId: item?.item.id ?? 'stale', recordIn, recordOut: recordIn + e.between(-1, 12) };
    }
    case 'placeOnTop': {
      const track = anyTrack();
      const recordIn = e.between(0, 40);
      return { type: 'placeOnTop', trackId: track?.id ?? 'stale', item: makeItem(freshItemId(seq), 'generatorClip', recordIn, recordIn + e.between(1, 6), undefined) };
    }
    case 'join': {
      const item = anyItem();
      return { type: 'join', itemId: e.bool() ? (item?.item.id ?? 'stale') : 'stale-item' };
    }
    case 'nudge': {
      const item = anyItem();
      return { type: 'nudge', itemId: item?.item.id ?? 'stale', deltaFrames: e.between(-10, 10) };
    }
    case 'addTrack': {
      const existing = anyTrack();
      return {
        type: 'addTrack',
        index: e.between(-1, doc.tracks.length + 1),
        track: {
          id: e.bool() ? `fresh-${seq}-${e.below(1000)}` : (existing?.id ?? 'dup'),
          kind: e.bool() ? 'video' : ('weird' as unknown as 'audio'),
          name: 'unfiltered',
          height: e.bool() ? 48 : 0,
          locked: false,
          enabled: true,
          mute: false,
          solo: false,
          items: e.bool() ? [] : [makeItem(anyItem()?.item.id ?? 'dup', 'caption', 0, 2, undefined), makeItem('u2', 'caption', 1, 3, undefined)],
        },
      };
    }
    case 'removeTrack':
      return { type: 'removeTrack', trackId: e.bool() ? (anyTrack()?.id ?? 'stale') : 'stale-track' };
    case 'moveTrack':
      return { type: 'moveTrack', trackId: anyTrack()?.id ?? 'stale', toIndex: e.between(-1, doc.tracks.length) };
    case 'setTrackProperty': {
      const keys = ['name', 'color', 'height', 'locked', 'enabled', 'mute', 'solo', 'busId', 'items'] as const;
      const key = e.pick(keys) ?? 'name';
      const value = e.pick([null, 'text', 0, -3, 12, true, false] as const) ?? null;
      return { type: 'setTrackProperty', trackId: anyTrack()?.id ?? 'stale', key: key as 'name', value };
    }
    case 'setTimelineSettings': {
      const patches: Record<string, unknown>[] = [
        {},
        { width: 0 },
        { height: -1 },
        { rate: { num: 0, den: 1 } },
        { aspectPolicy: 'bogus' },
        { startTimecodeFrames: -5 },
        { bogus: 1 },
        { width: e.between(16, 4096), sampleRate: 48000 },
      ];
      return { type: 'setTimelineSettings', patch: e.pick(patches) ?? {} };
    }
    case 'setInOut': {
      const choices: Array<{ inFrame: number; outFrame: number } | null | undefined> = [null, undefined, { inFrame: -1, outFrame: 3 }, { inFrame: 5, outFrame: 5 }, { inFrame: 2, outFrame: 9 }];
      return { type: 'setInOut', inOut: e.pick(choices) ?? null };
    }
    case 'setKeyframe': {
      const target: KeyframeTarget = e.bool() ? { itemId: anyItem()?.item.id ?? 'stale' } : { trackId: anyTrack()?.id ?? 'stale' };
      const param = e.pick(['gainDb', 'scale', 'opacity', 'bogus'] as const) ?? 'gainDb';
      return { type: 'setKeyframe', target, param: param as KeyframeParam, frame: e.between(-2, 40), value: e.pick([-100, -6, 0, 0.5, 1, 9, Number.NaN]) ?? 0, interp: e.pick([...INTERP_KINDS, 'bogus' as const]) ?? 'linear' } as EditOp;
    }
    case 'removeKeyframe': {
      const target: KeyframeTarget = e.bool() ? { itemId: anyItem()?.item.id ?? 'stale' } : { trackId: anyTrack()?.id ?? 'stale' };
      return { type: 'removeKeyframe', target, param: e.pick(['gainDb', 'rotation'] as const) ?? 'gainDb', frame: e.between(0, 40) };
    }
    case 'moveKeyframe': {
      const target: KeyframeTarget = e.bool() ? { itemId: anyItem()?.item.id ?? 'stale' } : { trackId: anyTrack()?.id ?? 'stale' };
      const frame = e.between(0, 40);
      return { type: 'moveKeyframe', target, param: 'gainDb', frame, toFrame: e.bool() ? frame : e.between(-1, 40) };
    }
    case 'retime': {
      const located = anyItem();
      return { type: 'retime', itemId: located?.item.id ?? 'stale', playbackRate: e.pick([0, -1, 0.5, 2, Number.NaN]) ?? 2, recordOut: e.between(-5, 60) };
    }
    case 'addTransition': {
      const located = anyItem();
      return {
        type: 'addTransition',
        afterItemId: located?.item.id ?? 'stale',
        transition: { id: e.bool() ? freshItemId(seq) : (located?.item.id ?? 'dup'), type: 'transition', name: 'T', transitionType: e.pick([...TRANSITION_TYPES, 'sparkle']) ?? 'fade', enabled: true },
        framesBefore: e.between(-1, 6),
        framesAfter: e.between(-1, 6),
      };
    }
    case 'removeTransition': {
      const located = anyItem();
      return { type: 'removeTransition', transitionId: located?.item.id ?? 'stale', cutFrame: e.between(-1, 60) };
    }
    case 'setColorGroup': {
      const located = anyItem();
      const variant = e.below(3);
      if (variant === 0) return { type: 'setColorGroup', groupId: 'g-missing', group: undefined };
      if (variant === 1) return { type: 'setColorGroup', groupId: 'g1', group: { id: 'g2', name: 'mismatch', clipIds: [] } };
      return { type: 'setColorGroup', groupId: 'g1', group: { id: 'g1', name: 'x', clipIds: [located?.item.type === 'mediaClip' ? 'not-a-clip' : (located?.item.id ?? 'stale')] } };
    }
    case 'setSequence': {
      const located = anyItem();
      const variant = e.below(4);
      // Remove a sequence a compound may still play, remove a missing one, mismatched ids, or a
      // sequence that reuses a root item id (ids are unique document-wide).
      if (variant === 0) return { type: 'setSequence', sequenceId: PROP_SEQUENCE_ID, sequence: undefined };
      if (variant === 1) return { type: 'setSequence', sequenceId: 'seq-missing', sequence: undefined };
      if (variant === 2) return { type: 'setSequence', sequenceId: 's1', sequence: { id: 's2', name: 'mismatch', tracks: [] } };
      return { type: 'setSequence', sequenceId: 's-dup', sequence: { id: 's-dup', name: 'dup', tracks: [{ id: 'st', kind: 'video', name: 'V', height: 64, locked: false, enabled: true, mute: false, solo: false, items: [makeItem(located?.item.id ?? 'x', 'titleClip', 0, 5, undefined)] }] } };
    }
    case 'setBus': {
      const variant = e.below(3);
      if (variant === 0) return { type: 'setBus', busId: 'bus-missing', bus: undefined };
      if (variant === 1) return { type: 'setBus', busId: 'b1', bus: { id: 'b1', name: 'self', outputBusId: 'b1' } };
      return { type: 'setBus', busId: 'b2', bus: { id: 'b2', name: 'hot', dynamics: { compressor: { ratio: e.between(-5, 40) } } } };
    }
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Assertion helpers
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Every clause of the round-trip contract, for one op:
 *
 *   - the input document is never written to (purity);
 *   - the document after the op satisfies every invariant;
 *   - the inverse is a program drawn only from the closed catalogue;
 *   - applying it **step by step** restores the document, with the invariants asserted after each
 *     intermediate step as well;
 *   - `applyAll(after, inverse)` restores it — the ticket's exact formulation.
 */
function assertRoundTrip(doc: EditTimeline, op: EditOp): { doc: EditTimeline; inverse: EditOp[] } {
  const before = structuredClone(doc);
  assertInvariants(doc);

  const applied = apply(doc, op);
  expect(doc).toStrictEqual(before);
  assertInvariants(applied.doc);

  for (const inverseOp of applied.inverse) {
    expect(COVERED_OPS).toContain(inverseOp.type);
  }

  let stepwise = applied.doc;
  for (const inverseOp of applied.inverse) {
    stepwise = apply(stepwise, inverseOp, { inverse: true }).doc;
    assertInvariants(stepwise);
  }
  expect(stepwise).toStrictEqual(before);

  const restored = applyAll(applied.doc, applied.inverse, { inverse: true });
  assertInvariants(restored);
  expect(restored).toStrictEqual(before);

  return applied;
}

type Attempt =
  | { ok: true; doc: EditTimeline; inverse: EditOp[] }
  | { ok: false; error: unknown };

function tryApply(doc: EditTimeline, op: EditOp): Attempt {
  try {
    const applied = apply(doc, op);
    return { ok: true, doc: applied.doc, inverse: applied.inverse };
  } catch (error) {
    return { ok: false, error };
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// The catalogue itself
// ─────────────────────────────────────────────────────────────────────────────

describe('the property suite covers the catalogue', () => {
  it('has a round-trip property for each of the thirty-four ops, pinned at compile time', () => {
    expect(everyOpHasAProperty).toBe(true);
    expect(inverseIsOpArray).toBe(true);
    expect(COVERED_OPS).toHaveLength(34);
    expect(new Set(COVERED_OPS).size).toBe(34);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// Property 1 — the round trip, one property per op
// ─────────────────────────────────────────────────────────────────────────────

describe('property: applyAll(apply(doc, op).doc, inverse) restores doc — every op', () => {
  /**
   * A document arbitrary plus an entropy vector, reduced to one op of `opName` built against that
   * very document. Every id the op names is therefore a real generated id, and every position is
   * one the engine accepts — a generator bug surfaces as "offers no legal site", not as a red
   * round-trip property.
   */
  const opProperty = (options: DocOptions, opName: EditOp['type'], mayHaveNoSite: boolean) =>
    withDocument(options, (doc) =>
      fc.tuple(fc.constant(doc), entropyArb).map(([document, values]) => {
        const op = buildValidOp(document, new Entropy(values), 0, opName);
        if (op === undefined && mayHaveNoSite) return { document, op: undefined };
        expect(op, `${opName} offers no legal site on this generated document`).toBeDefined();
        return materializePending(document, op as EditOp);
      }),
    );

  /**
   * Runs one op-name against arbitrary documents and asserts the full round-trip contract.
   * `mayHaveNoSite`: the op never applies to a transition (it sits exactly on its cut), so a
   * document whose every item is a transition, or boxed in by one, offers no site; such draws are
   * discarded rather than failing as a generator bug.
   */
  const propertyFor = (opName: EditOp['type'], options: DocOptions = {}, mayHaveNoSite = false) =>
    fc.assert(
      fc.property(opProperty(options, opName, mayHaveNoSite), ({ document, op }) => {
        fc.pre(op !== undefined);
        expect(op.type).toBe(opName);
        assertRoundTrip(document, op);
      }),
    );

  // ── P4 ops ────────────────────────────────────────────────────────────────

  it('append (lands at the track end, only the duration is read, undoes by lift)', () => {
    propertyFor('append');
  });

  it('replace (keeps the id and span, swaps every other field, undoes by overwrite)', () => {
    propertyFor('replace', { minItems: 1 });
  });

  it('fitToFill (re-spans within the neighbours, scales a media clip rate, undoes by overwrite)', () => {
    propertyFor('fitToFill', { minItems: 1 }, true);
  });

  it('placeOnTop (drops into free space with no index, undoes by lift)', () => {
    propertyFor('placeOnTop');
  });

  it('join (merges a blade-shaped pair, undoes by the one blade that made it)', () => {
    propertyFor('join', { minItems: 1, minItemLength: 2 });
  });

  it('nudge (slides within the neighbours, undoes by the opposite nudge)', () => {
    propertyFor('nudge', { minItems: 1 }, true);
  });

  it('addTrack (empty or populated, at any index, undoes by removeTrack)', () => {
    propertyFor('addTrack');
  });

  it('removeTrack (takes the whole track with its items, undoes by addTrack at the old index)', () => {
    propertyFor('removeTrack');
  });

  it('moveTrack (reorders compositing, undoes by moving back)', () => {
    fc.assert(
      fc.property(
        withDocument({}, (doc) =>
          fc.tuple(fc.constant(doc), entropyArb).map(([generated, values]) => {
            let document = generated;
            if (document.tracks.length < 2) {
              const extra: Track = { ...document.tracks[0], id: 'track-extra', name: 'extra', items: [] };
              document = { ...document, tracks: [...document.tracks, extra] };
            }
            const op = buildValidOp(document, new Entropy(values), 0, 'moveTrack');
            expect(op, 'moveTrack offers no legal site on this generated document').toBeDefined();
            return { document, op: op as EditOp };
          }),
        ),
        ({ document, op }) => {
          expect(op.type).toBe('moveTrack');
          assertRoundTrip(document, op);
        },
      ),
    );
  });

  it('setTrackProperty (every allowlisted key, undoes by re-setting the old value)', () => {
    propertyFor('setTrackProperty');
  });

  it('setTimelineSettings (any non-empty valid patch, undoes by re-setting exactly the patched keys)', () => {
    propertyFor('setTimelineSettings');
  });

  it('setInOut (a range, an explicit null or a removal, undoes to the previous shape exactly)', () => {
    propertyFor('setInOut');
  });

  // ── P5 ops ────────────────────────────────────────────────────────────────

  /** A document with at least one 8-frame media clip on its first track (appended after the last item). */
  const withMediaClip = (options: DocOptions, opName: EditOp['type']) =>
    withDocument(options, (doc) =>
      fc.tuple(fc.constant(doc), entropyArb).map(([generated, values]) => {
        let document = generated;
        if (!allItems(document).some((located) => located.item.type === 'mediaClip' && located.item.recordOut - located.item.recordIn >= 4)) {
          const track = document.tracks[0];
          const last = track.items[track.items.length - 1];
          const end = last === undefined ? 0 : last.recordOut;
          // Flush after a trailing transition (eng D7: it must sit on a cut), one frame clear otherwise.
          const start = last?.type === 'transition' ? end : end + 1;
          const clip = makeItem('seeded-media', 'mediaClip', start, start + 8, 3);
          document = { ...document, tracks: [{ ...track, items: [...track.items, clip] }, ...document.tracks.slice(1)] };
        }
        const op = buildValidOp(document, new Entropy(values), 0, opName);
        expect(op, `${opName} offers no legal site on this generated document`).toBeDefined();
        return materializePending(document, op as EditOp);
      }),
    );

  const propertyWithMedia = (opName: EditOp['type']) =>
    fc.assert(
      fc.property(withMediaClip({}, opName), ({ document, op }) => {
        expect(op.type).toBe(opName);
        assertRoundTrip(document, op);
      }),
    );

  it('setKeyframe (creates, adds or replaces a key on a clip channel or a track fader; undoes exactly)', () => {
    propertyFor('setKeyframe');
  });

  it('removeKeyframe (undoes by re-setting the removed key, never deleting the container)', () => {
    propertyFor('removeKeyframe');
  });

  it('moveKeyframe (keeps frame order, undoes by moving back)', () => {
    propertyFor('moveKeyframe');
  });

  it('retime (new rate and recordOut together, ripples the followers, undoes with the old pair)', () => {
    propertyWithMedia('retime');
  });

  it('addTransition (trims and pulls the neighbours, undoes by removeTransition at the cut)', () => {
    propertyWithMedia('addTransition');
  });

  it('removeTransition (restores a hard cut anywhere in the span, undoes by the implied addTransition)', () => {
    propertyWithMedia('removeTransition');
  });

  it('setColorGroup (adds, replaces or removes one colour group; undoes with the previous group or its absence)', () => {
    propertyFor('setColorGroup');
  });

  it('setSequence (adds, replaces or removes one nested sequence; undoes with the previous sequence or its absence)', () => {
    propertyFor('setSequence');
  });

  it('setBus (adds, replaces or removes one submix bus; undoes with the previous bus or its absence)', () => {
    propertyFor('setBus');
  });

  it('insert (fits a gap, or lands flush at the next item start, and ripples)', () => {
    propertyFor('insert');
  });

  it('overwrite (replaces the content of an item, keeping its id and span legality)', () => {
    propertyFor('overwrite', { minItems: 1 });
  });

  it('lift (removes the item, leaves the gap, undoes by putting it back where it was)', () => {
    fc.assert(
      fc.property(
        withDocument({ minItems: 1 }, (doc) =>
          fc.tuple(fc.constant(doc), entropyArb).map(([document, values]) => {
            const located = new Entropy(values).pick(allItems(document));
            expect(located, 'the generated document has no items').toBeDefined();
            return { document, located: located! };
          }),
        ),
        ({ document, located }) => {
          const op: EditOp = { type: 'lift', itemId: located.item.id };
          const applied = assertRoundTrip(document, op);

          // The item really left the track, and the inverse names the index the track's own
          // recordIn makes canonical — which is what makes re-inserting ripple nothing.
          const after = applied.doc.tracks[document.tracks.indexOf(located.track)];
          expect(after.items.some((item) => item.id === located.item.id)).toBe(false);
          // eng D7: a transition that lost this neighbour is lifted with it; the inverse puts the
          // item back first, then each such transition (one insert per transition).
          const swept = transitionsSweptBy(document, applied.doc, located.item.id);
          if (swept > 0 && applied.inverse[0].type === 'removeTrack') {
            // When re-inserting the item first would itself sit off a cut (a lifted transition
            // between back-to-back transitions), the track is put back whole: proven exact above.
            expect(applied.inverse).toEqual([
              { type: 'removeTrack', trackId: after.id },
              { type: 'addTrack', index: document.tracks.indexOf(located.track), track: located.track },
            ]);
            return;
          }
          expect(applied.inverse).toHaveLength(1 + sweepRestoreLength(swept));
          expect(applied.inverse[0]).toMatchObject({ type: 'insert', trackId: after.id });
          expect((applied.inverse[0] as Extract<EditOp, { type: 'insert' }>).index).toBe(
            canonicalIndex(after.items, located.item.recordIn),
          );
        },
      ),
    );
  });

  it('extract (ripple-deletes, closing up only what the item was adjacent to)', () => {
    fc.assert(
      fc.property(
        withDocument({ minItems: 1 }, (doc) =>
          fc.tuple(fc.constant(doc), entropyArb).map(([document, values]) => {
            const located = new Entropy(values).pick(allItems(document));
            expect(located, 'the generated document has no items').toBeDefined();
            return { document, located: located! };
          }),
        ),
        ({ document, located }) => {
          const applied = assertRoundTrip(document, { type: 'extract', itemId: located.item.id });

          // The documented adjacency rule, checked against the PRE-op document: the following
          // items shift left by exactly the removed item's duration when it was flush against
          // them, and not at all when it was already gapped.
          const track = document.tracks[document.tracks.indexOf(located.track)];
          const following = track.items[located.index + 1];
          if (following === undefined) return;
          const adjacent = following.recordIn === located.item.recordOut;
          const expectedShift = adjacent ? -(located.item.recordOut - located.item.recordIn) : 0;
          const moved = applied.doc.tracks[document.tracks.indexOf(located.track)].items.find(
            (item) => item.id === following.id,
          );
          // eng D7: a following transition that lost its incoming clip is lifted (or re-seated).
          if (moved === undefined && following.type === 'transition') return;
          expect(moved?.recordIn).toBe(following.recordIn + expectedShift);
        },
      ),
    );
  });

  it('trim (every mode and edge the item allows, on requested deltas that are mostly clamped)', () => {
    propertyFor('trim', { minItems: 1 });
  });

  it('blade (splits strictly inside the span and undoes with a two-op program)', () => {
    fc.assert(
      fc.property(
        withDocument({ minItems: 1, minItemLength: 2 }, (doc) =>
          fc.tuple(fc.constant(doc), entropyArb).map(([document, values]) => {
            const e = new Entropy(values);
            const located = e.pick(
              allItems(document).filter(
                (candidate) => candidate.item.recordOut - candidate.item.recordIn >= 2,
              ),
            );
            expect(located, 'the generated document has no item long enough to blade').toBeDefined();
            const op: EditOp = {
              type: 'blade',
              itemId: located!.item.id,
              atFrame: e.between(located!.item.recordIn + 1, located!.item.recordOut - 1),
              newItemId: freshItemId(0),
            };
            return { document, op };
          }),
        ),
        ({ document, op }) => {
          expect(op.type).toBe('blade');
          const applied = assertRoundTrip(document, op);
          // Two ops (lift the right half, widen the left); a bladed transition undoes by one
          // `join`; a transition flush after the cut steps aside around the two (eng D7).
          // (A flush transition after the cut makes it `removeTrack` + `addTrack`: still two ops.)
          const bladed = allItems(document).find((l) => l.item.id === op.itemId)!;
          expect(applied.inverse).toHaveLength(bladed.item.type === 'transition' ? 1 : 2);
          // The left half keeps the id and the right half takes the fresh one.
          const splits = apply(document, op).doc.tracks.flatMap((track) => track.items);
          expect(splits.some((item) => item.id === op.itemId)).toBe(true);
          expect(splits.some((item) => item.id === freshItemId(0))).toBe(true);
        },
      ),
    );
  });

  it('deleteGap (deletes a leading or interior gap and undoes with one move per shifted item)', () => {
    fc.assert(
      fc.property(
        withDocument({ guaranteeGap: true }, (doc) =>
          fc.tuple(fc.constant(doc), entropyArb).map(([document, values]) => {
            const e = new Entropy(values);
            const withGaps = document.tracks.filter((candidate) => gapsOf(candidate.items).length > 0);
            expect(withGaps.length, 'the generated document has no gap on any track').toBeGreaterThan(0);
            const track = e.pick(withGaps);
            const gap = e.pick(gapsOf(track!.items));
            expect(gap, 'the picked track has no gap').toBeDefined();
            const op: EditOp = {
              type: 'deleteGap',
              trackId: track!.id,
              atFrame: e.between(gap![0], gap![1] - 1),
            };
            return { document, op };
          }),
        ),
        ({ document, op }) => {
          expect(op.type).toBe('deleteGap');
          assertRoundTrip(document, op);
        },
      ),
    );
  });

  it('closeGaps (removes every interior gap, or is a no-op with an empty inverse)', () => {
    fc.assert(
      fc.property(
        withDocument({}, (doc) =>
          fc.tuple(fc.constant(doc), entropyArb).map(([document, values]) => {
            const track = new Entropy(values).pick(document.tracks);
            expect(track, 'the generated document has no tracks').toBeDefined();
            return { document, op: { type: 'closeGaps', trackId: track!.id } as EditOp };
          }),
        ),
        ({ document, op }) => {
          expect(op.type).toBe('closeGaps');
          assertRoundTrip(document, op);
        },
      ),
    );
  });

  it('move (to another track or within the same one, always to a free destination)', () => {
    fc.assert(
      fc.property(
        withDocument({ minItems: 1 }, (doc) =>
          fc.tuple(fc.constant(doc), entropyArb).map(([document, values]) => {
            const e = new Entropy(values);
            // A transition never moves on its own: it sits exactly on its cut.
            const located = e.pick(allItems(document).filter((candidate) => candidate.item.type !== 'transition'));
            if (located === undefined) return { document, op: undefined };
            const destination = e.pick(moveDestinations(document, located.item));
            expect(destination, 'the item has no free destination').toBeDefined();
            const op: Extract<EditOp, { type: 'move' }> = {
              type: 'move',
              itemId: located.item.id,
              toTrackId: destination!.toTrackId,
              toRecordIn: destination!.toRecordIn,
            };
            return { document, op };
          }),
        ),
        ({ document, op }) => {
          fc.pre(op !== undefined);
          const applied = assertRoundTrip(document, op);
          expect(applied.inverse).toHaveLength(1 + sweepRestoreLength(transitionsSweptBy(document, applied.doc, op.itemId), tracksSweptBy(document, applied.doc, op.itemId)));
        },
      ),
    );
  });

  it('setProperty (every allowlisted key of every item kind, values present, absent or cleared)', () => {
    fc.assert(
      fc.property(
        withDocument({ minItems: 1 }, (doc) =>
          fc.tuple(fc.constant(doc), entropyArb).map(([document, values]) => {
            const e = new Entropy(values);
            const located = e.pick(allItems(document));
            expect(located, 'the generated document has no items').toBeDefined();
            const key = e.pick(SET_PROPERTY_ALLOWLIST[located!.item.type]);
            expect(key, `this item kind (${located!.item.type}) has an empty allowlist`).toBeDefined();
            const op: EditOp = {
              type: 'setProperty',
              itemId: located!.item.id,
              key: key!,
              value: legalValueFor(located!.item, key!, e),
            };
            return { document, op };
          }),
        ),
        ({ document, op }) => {
          expect(op.type).toBe('setProperty');
          assertRoundTrip(document, op);
        },
      ),
    );
  });

  it('setProperty on every item kind of a hand-built document, one key at a time', () => {
    const doc = kindZoo();
    let cases = 0;
    for (const track of doc.tracks) {
      for (const item of track.items) {
        for (const key of SET_PROPERTY_ALLOWLIST[item.type]) {
          assertRoundTrip(doc, {
            type: 'setProperty',
            itemId: item.id,
            key,
            value: legalValueFor(item, key, new Entropy([3, 7, 11, 19])),
          });
          cases++;
        }
      }
    }
    // Nine kinds, every allowlisted key of each: the per-kind allowlist is a contract, not a hint.
    expect(cases).toBeGreaterThanOrEqual(20);
  });

  it('addMarker (splices in frame order, with and without an explicit slot)', () => {
    fc.assert(
      fc.property(
        withDocument({}, (doc) =>
          fc.tuple(fc.constant(doc), entropyArb).map(([document, values]) => {
            const e = new Entropy(values);
            const frame = e.between(0, 40);
            const marker: Marker = {
              id: freshMarkerId(0),
              frame,
              name: 'generated',
              color: null,
              note: null,
              durationFrames: null,
            };
            const op: EditOp = e.bool()
              ? { type: 'addMarker', marker }
              : { type: 'addMarker', marker, index: markerIndexFor(document.markers, frame) };
            return { document, op };
          }),
        ),
        ({ document, op }) => {
          expect(op.type).toBe('addMarker');
          const applied = assertRoundTrip(document, op);
          expect(applied.inverse).toEqual([{ type: 'removeMarker', markerId: freshMarkerId(0) }]);
        },
      ),
    );
  });

  it('setMarker (patches fields and moves a frame inside the slot its neighbours leave)', () => {
    fc.assert(
      fc.property(
        withDocument({ minMarkers: 1 }, (doc) =>
          fc.tuple(fc.constant(doc), entropyArb).map(([document, values]) => {
            const e = new Entropy(values);
            const index = e.below(document.markers.length);
            const low = index > 0 ? document.markers[index - 1].frame : 0;
            const high =
              index + 1 < document.markers.length ? document.markers[index + 1].frame : low + 5;
            const op: EditOp = {
              type: 'setMarker',
              markerId: document.markers[index].id,
              patch: {
                name: `renamed-${e.below(100)}`,
                frame: e.between(low, high),
                ...(e.bool() ? { note: e.bool() ? null : `note-${e.below(100)}` } : {}),
              },
            };
            return { document, op };
          }),
        ),
        ({ document, op }) => {
          expect(op.type).toBe('setMarker');
          const applied = assertRoundTrip(document, op);
          expect(applied.inverse).toHaveLength(1);
          expect(applied.inverse[0]).toMatchObject({ type: 'setMarker' });
        },
      ),
    );
  });

  it('removeMarker (puts a tied marker back in its own slot, not at the end of its tie group)', () => {
    fc.assert(
      fc.property(
        withDocument({ minMarkers: 1 }, (doc) =>
          fc.tuple(fc.constant(doc), entropyArb).map(([document, values]) => {
            const marker = new Entropy(values).pick(document.markers);
            expect(marker, 'the generated document has no markers').toBeDefined();
            return { document, op: { type: 'removeMarker', markerId: marker!.id } as EditOp };
          }),
        ),
        ({ document, op }) => {
          expect(op.type).toBe('removeMarker');
          const applied = assertRoundTrip(document, op);
          expect(applied.inverse).toHaveLength(1);
          expect(applied.inverse[0]).toMatchObject({ type: 'addMarker' });
        },
      ),
    );
  });
});

/** The index a marker with `frame` takes: after every marker at or before that frame. */
function markerIndexFor(markers: readonly Marker[], frame: number): number {
  let index = 0;
  while (index < markers.length && markers[index].frame <= frame) index++;
  return index;
}

// ─────────────────────────────────────────────────────────────────────────────
// Property 1b — inverses are programs drawn only from the named catalogue
// ─────────────────────────────────────────────────────────────────────────────

describe('property: inverse programs stay inside the closed catalogue', () => {
  it('every op of the catalogue, legal or not, yields an in-catalogue program when it succeeds', () => {
    fc.assert(
      fc.property(
        documentArbitrary({ guaranteeGap: true, minMarkers: 1 }),
        fc.array(fc.nat({ max: 1000 }), { minLength: 12, maxLength: 12 }),
        (document, values) => {
          for (let index = 0; index < COVERED_OPS.length; index++) {
            const op = buildUnfilteredOp(document, new Entropy([index * 7919, ...values]), index, COVERED_OPS[index]);
            expect(op.type).toBe(COVERED_OPS[index]);

            const attempt = tryApply(document, op);
            if (!attempt.ok) {
              expect(attempt.error).toBeInstanceOf(EditEngineError);
              continue;
            }
            for (const inverseOp of attempt.inverse) {
              expect(COVERED_OPS).toContain(inverseOp.type);
            }
          }
        },
      ),
    );
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// Property 1c — a refused op is a rejection, never a crash and never a mutation
// ─────────────────────────────────────────────────────────────────────────────

describe('property: every op either round-trips or is refused as a rejection', () => {
  it('an unfiltered op (partial overlap, non-canonical index, bad key, stale id) is refused, not corrupting', () => {
    fc.assert(
      fc.property(
        withDocument({}, (doc) =>
          fc.tuple(fc.constant(doc), entropyArb).map(([document, values]) => {
            const e = new Entropy(values);
            const opName = COVERED_OPS[e.below(COVERED_OPS.length)];
            return { document, op: buildUnfilteredOp(document, e, 0, opName) };
          }),
        ),
        ({ document, op }) => {
          const before = structuredClone(document);
          const attempt = tryApply(document, op);

          // Either way, the document the caller handed in is untouched.
          expect(document).toStrictEqual(before);

          if (attempt.ok) {
            assertRoundTrip(document, op);
          } else {
            expect(attempt.error, `refusal of ${op.type} must be an EditEngineError`).toBeInstanceOf(
              EditEngineError,
            );
            expect((attempt.error as EditEngineError).name).toBe('EditEngineError');
          }
        },
      ),
    );
  });

  it('insert into a partial overlap is refused by design, with the document left alone', () => {
    // The accepted deviation in `docs/editor.md`: only fits-in-gap and flush-at-next-start are
    // recoverable, so anything in between must be a rejection rather than a corrupting ripple.
    const doc = twoItemTrack();
    const before = structuredClone(doc);
    // Track `[10, 20)` then `[30, 40)`: a 5-frame item at 27 runs 2 frames past the next start.
    const partial: EditOp = {
      type: 'insert',
      trackId: 't0',
      index: 1,
      item: makeItem('partial', 'titleClip', 27, 32, undefined),
    };
    expect(() => apply(doc, partial)).toThrow(EditEngineError);
    expect(doc).toStrictEqual(before);

    // And the two positions the engine does accept, from the same document, both round-trip.
    assertRoundTrip(doc, {
      type: 'insert',
      trackId: 't0',
      index: 1,
      item: makeItem('fits', 'titleClip', 22, 27, undefined),
    });
    assertRoundTrip(doc, {
      type: 'insert',
      trackId: 't0',
      index: 1,
      item: makeItem('flush', 'titleClip', 30, 35, undefined),
    });
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// Property 2 — a run of 1-20 valid ops keeps every invariant, at every step
// ─────────────────────────────────────────────────────────────────────────────

describe('property: 1-20 random valid ops keep the invariants and undo cleanly', () => {
  /**
   * Folds `plan` over `document`, asserting the whole contract at **every** intermediate step —
   * not only at the end — and then replays the collected programs as an undo stack.
   */
  function runPlan(document: EditTimeline, plan: readonly number[][]): EditTimeline {
    assertInvariants(document);

    let current = document;
    const programs: EditOp[][] = [];
    const appliedOps: EditOp[] = [];

    for (let step = 0; step < plan.length; step++) {
      const op = buildValidOpOrFallback(current, new Entropy(plan[step]), step);
      const applied = apply(current, op);

      // The intermediate state after *every* op must itself be a document the engine may edit.
      assertInvariants(applied.doc);
      expect(applied.doc).not.toBe(current);

      for (const inverseOp of applied.inverse) {
        expect(COVERED_OPS).toContain(inverseOp.type);
      }

      programs.push(applied.inverse);
      appliedOps.push(op);
      current = applied.doc;
    }

    // The whole run applied as one program of ops.
    expect(applyAll(document, appliedOps)).toStrictEqual(current);

    // The undo stack: each step's inverse program, replayed newest-first, with the invariants
    // asserted after every inverse op as well.
    let undone = current;
    for (let step = programs.length - 1; step >= 0; step--) {
      for (const inverseOp of programs[step]) {
        undone = apply(undone, inverseOp).doc;
        assertInvariants(undone);
      }
    }
    expect(undone).toStrictEqual(document);
    return current;
  }

  it('holds assertInvariants after every op of the run, and restores the start through the undo stack', () => {
    fc.assert(
      fc.property(
        documentArbitrary({ minMarkers: 1, guaranteeGap: true }),
        planArb,
        (document, plan) => {
          runPlan(document, plan);
        },
      ),
    );
  });

  /**
   * A real regression, pinned by its seed. On `-1332402414` this property used to fail with
   * `setColorGroup: colorGroups[0].clipIds[0] 'd0-0' is not a media clip of this timeline`: an
   * earlier `lift` in the run had taken out a clip a colour group still named, and the builder then
   * offered a `setColorGroup` over that document — an op `apply` refuses by design, because the op
   * is strict on write while only the *document* invariant tolerates a dangling member. The builder
   * now offers no colour-group site at all while any group dangles, so the seed must stay green.
   */
  it('holds on the pinned regression seed -1332402414', () => {
    fc.assert(
      fc.property(
        documentArbitrary({ minMarkers: 1, guaranteeGap: true }),
        planArb,
        (document, plan) => {
          runPlan(document, plan);
        },
      ),
      { seed: -1332402414 },
    );
  });

  it('holds for runs of exactly every length from 1 to 20 ops', () => {
    const doc = documentArbitrary({ minMarkers: 1, guaranteeGap: true });
    const vectors = fc.sample(entropyArb, { numRuns: 200, seed: 20240101 });
    for (let length = 1; length <= 20; length++) {
      const plan = vectors.slice(0, length);
      expect(plan).toHaveLength(length);
      fc.assert(
        fc.property(doc, (document) => {
          const after = runPlan(document, plan);
          // A run of this length really ran, and really moved the document along.
          expect(after).not.toBe(document);
        }),
        { seed: 20240101 + length },
      );
    }
  }, 300_000);

  it('never draws a plan outside the 1-20 bound the ticket states', () => {
    const lengths = fc.sample(planArb, { numRuns: 300, seed: 7 }).map((plan) => plan.length);
    expect(Math.min(...lengths)).toBeGreaterThanOrEqual(1);
    expect(Math.max(...lengths)).toBeLessThanOrEqual(20);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// Seeded (non-random) boundary cases
// ─────────────────────────────────────────────────────────────────────────────

// ── Fixtures ────────────────────────────────────────────────────────────────

function timelineOf(tracks: Track[], markers: Marker[] = []): EditTimeline {
  return {
    schemaVersion: 2,
    id: 'timeline-seeded',
    projectId: 'project-seeded',
    name: 'Seeded fixture',
    settings: SETTINGS,
    tracks,
    markers,
    inOut: null,
    seed: null,
    sequences: [{ id: PROP_SEQUENCE_ID, name: 'Nested', tracks: [] }],
  };
}

function trackOf(id: string, items: Item[]): Track {
  return {
    id,
    kind: 'video',
    name: id,
    height: 64,
    locked: false,
    enabled: true,
    mute: false,
    solo: false,
    items,
  };
}

/** Two items with a ten-frame gap between them, on one track. */
function twoItemTrack(): EditTimeline {
  return timelineOf([
    trackOf('t0', [
      makeItem('i0', 'titleClip', 10, 20, undefined),
      makeItem('i1', 'mediaClip', 30, 40, 4),
    ]),
  ]);
}

/** Three items — a contiguous pair, then a gap — with neighbours on both sides of the middle. */
function threeItemTrack(): EditTimeline {
  return timelineOf([
    trackOf('t0', [
      makeItem('a', 'mediaClip', 0, 10, 3),
      makeItem('b', 'mediaClip', 10, 20, 5),
      makeItem('c', 'titleClip', 28, 36, undefined),
    ]),
  ]);
}

/** One item per kind, so the per-kind allowlists are all exercised deterministically. */
function kindZoo(): EditTimeline {
  let zooCursor = 0;
  return timelineOf([
    trackOf(
      't0',
      // 12-frame items 8 frames apart, except that the transition sits flush between its
      // neighbours (eng D7: it must sit exactly on a cut).
      ITEM_KINDS.map((kind, i) => {
        const touchesTransition = kind === 'transition' || ITEM_KINDS[i - 1] === 'transition';
        const start = i === 0 ? 0 : zooCursor + (touchesTransition ? 0 : 8);
        zooCursor = start + 12;
        return makeItem(`kind-${kind}`, kind, start, start + 12, kind === 'mediaClip' ? 2 : undefined);
      }),
    ),
  ]);
}

/** A lone media clip whose handle is the only thing a negative trim can spend. */
function soloMediaClip(): EditTimeline {
  return timelineOf([trackOf('t0', [makeItem('solo', 'mediaClip', 50, 60, 2)])]);
}

/** A three-frame leader, a gap, and a lone media clip — for the neighbour-duration clamp. */
function leaderAndSolo(): EditTimeline {
  return timelineOf([
    trackOf('t0', [
      makeItem('leader', 'titleClip', 0, 3, undefined),
      makeItem('solo', 'mediaClip', 60, 70, 2),
    ]),
  ]);
}

describe('seeded: an op at a track FIRST item', () => {
  it('round-trips every op that can name the first item of a track, exactly', () => {
    const doc = threeItemTrack();
    const first = doc.tracks[0].items[0];

    assertRoundTrip(doc, { type: 'lift', itemId: first.id });
    assertRoundTrip(doc, { type: 'extract', itemId: first.id });
    assertRoundTrip(doc, { type: 'blade', itemId: first.id, atFrame: 4, newItemId: 'first-right' });
    assertRoundTrip(doc, { type: 'overwrite', itemId: first.id, item: withSpan(first, 0, 6) });
    assertRoundTrip(doc, { type: 'move', itemId: first.id, toTrackId: 't0', toRecordIn: 40 });
    assertRoundTrip(doc, { type: 'trim', itemId: first.id, edge: 'out', mode: 'ripple', deltaFrames: 3 });
    // The in edge of the FIRST item has no preceding neighbour to clamp against — the delta is
    // bounded only by `recordIn >= 0` and by its own source offset.
    assertRoundTrip(doc, { type: 'trim', itemId: first.id, edge: 'in', mode: 'ripple', deltaFrames: -1 });
    assertRoundTrip(doc, { type: 'trim', itemId: first.id, edge: 'in', mode: 'ripple', deltaFrames: -4 });
    assertRoundTrip(doc, { type: 'trim', itemId: first.id, edge: 'out', mode: 'roll', deltaFrames: 2 });
    assertRoundTrip(doc, { type: 'trim', itemId: first.id, edge: 'out', mode: 'slide', deltaFrames: 2 });
    assertRoundTrip(doc, { type: 'setProperty', itemId: first.id, key: 'name', value: 'renamed' });

    // eng D13: an in-edge ripple keeps the clip's start and changes its length, so it is clamped
    // only by `a`'s own source handle (3 frames): −4 extends the head by 3, and the followers move
    // right by the same 3.
    const clamped = apply(doc, { type: 'trim', itemId: first.id, edge: 'in', mode: 'ripple', deltaFrames: -4 });
    expect(clamped.doc.tracks[0].items[0]).toMatchObject({ recordIn: 0, recordOut: 13, sourceIn: 0 });
    expect(clamped.doc.tracks[0].items[1]).toMatchObject({ recordIn: 13, recordOut: 23 });
    expect(clamped.inverse[0]).toMatchObject({ type: 'trim', deltaFrames: 3 });
  });

  it('round-trips an insert immediately before the first item and an overwrite of index 0', () => {
    const doc = timelineOf([trackOf('t0', [makeItem('only', 'caption', 6, 16, undefined)])]);
    // A leading gap `[0, 6)` — five frames of it fit without rippling.
    assertRoundTrip(doc, {
      type: 'insert',
      trackId: 't0',
      index: 0,
      item: makeItem('lead', 'titleClip', 0, 5, undefined),
    });
    // Flush at the first item's own start ripples the whole track right.
    const rippled = assertRoundTrip(doc, {
      type: 'insert',
      trackId: 't0',
      index: 0,
      item: makeItem('flush', 'titleClip', 6, 12, undefined),
    });
    expect(rippled.doc.tracks[0].items.map((item) => item.recordIn)).toEqual([6, 12]);
    expect(rippled.inverse).toEqual([{ type: 'extract', itemId: 'flush' }]);
  });
});

describe('seeded: an op at a track LAST item', () => {
  it('round-trips every op that can name the last item of a track, exactly', () => {
    const doc = threeItemTrack();
    const last = doc.tracks[0].items[2];

    assertRoundTrip(doc, { type: 'lift', itemId: last.id });
    assertRoundTrip(doc, { type: 'extract', itemId: last.id });
    assertRoundTrip(doc, { type: 'blade', itemId: last.id, atFrame: 32, newItemId: 'last-right' });
    assertRoundTrip(doc, { type: 'overwrite', itemId: last.id, item: withSpan(last, 26, 40) });
    // A free destination: the gap the removed `c` leaves behind (`b` ends at 20).
    assertRoundTrip(doc, { type: 'move', itemId: last.id, toTrackId: 't0', toRecordIn: 20 });
    // The out edge of the LAST item has no following item to ripple — only the duration floor
    // (and its own source offset) can clamp it.
    assertRoundTrip(doc, { type: 'trim', itemId: last.id, edge: 'out', mode: 'ripple', deltaFrames: 12 });
    assertRoundTrip(doc, { type: 'trim', itemId: last.id, edge: 'out', mode: 'ripple', deltaFrames: -40 });
    assertRoundTrip(doc, { type: 'trim', itemId: last.id, edge: 'in', mode: 'ripple', deltaFrames: -2 });
    assertRoundTrip(doc, { type: 'trim', itemId: last.id, edge: 'in', mode: 'roll', deltaFrames: -3 });
    assertRoundTrip(doc, { type: 'trim', itemId: last.id, edge: 'in', mode: 'slide', deltaFrames: 4 });
    assertRoundTrip(doc, { type: 'setProperty', itemId: last.id, key: 'text', value: 'last' });

    // `extract` on the last item is the plain-removal branch: it was gapped, so nothing shifts.
    const extracted = apply(doc, { type: 'extract', itemId: last.id });
    expect(extracted.doc.tracks[0].items.map((item) => item.recordIn)).toEqual([0, 10]);
  });

  it('round-trips an insert into the trailing space after the last item', () => {
    const doc = twoItemTrack();
    assertRoundTrip(doc, {
      type: 'insert',
      trackId: 't0',
      index: 2,
      item: makeItem('tail', 'caption', 40, 48, undefined),
    });
  });

  it('round-trips a blade and a move on a single-item track (both neighbours absent)', () => {
    const doc = soloMediaClip();
    const applied = assertRoundTrip(doc, { type: 'blade', itemId: 'solo', atFrame: 55, newItemId: 'solo-right' });
    expect(applied.inverse).toEqual([
      { type: 'lift', itemId: 'solo-right' },
      { type: 'overwrite', itemId: 'solo', item: expect.objectContaining({ recordIn: 50, recordOut: 60 }) },
    ]);
  });
});

describe('seeded: an op that EMPTIES a track', () => {
  it('lift of the only item leaves the track empty and restores it exactly', () => {
    const doc = soloMediaClip();
    const applied = assertRoundTrip(doc, { type: 'lift', itemId: 'solo' });
    expect(applied.doc.tracks[0].items).toHaveLength(0);
    expect(applied.inverse).toEqual([
      { type: 'insert', trackId: 't0', index: 0, item: expect.objectContaining({ id: 'solo', recordIn: 50, recordOut: 60 }) },
    ]);
  });

  it('extract of the only item empties the track and restores it exactly', () => {
    const doc = soloMediaClip();
    const applied = assertRoundTrip(doc, { type: 'extract', itemId: 'solo' });
    expect(applied.doc.tracks[0].items).toHaveLength(0);
  });

  it('move of the only item to another track empties the source track and restores it exactly', () => {
    const doc = timelineOf([
      trackOf('t0', [makeItem('solo', 'mediaClip', 50, 60, 2)]),
      trackOf('t1', []),
    ]);
    const applied = assertRoundTrip(doc, { type: 'move', itemId: 'solo', toTrackId: 't1', toRecordIn: 0 });
    expect(applied.doc.tracks[0].items).toHaveLength(0);
    expect(applied.doc.tracks[1].items.map((item) => item.id)).toEqual(['solo']);
    expect(applied.inverse).toEqual([{ type: 'move', itemId: 'solo', toTrackId: 't0', toRecordIn: 50 }]);
  });

  it('lifts both items of a two-item track and restores the track exactly', () => {
    const doc = twoItemTrack();
    const first = apply(doc, { type: 'lift', itemId: 'i0' });
    const second = apply(first.doc, { type: 'lift', itemId: 'i1' });
    expect(second.doc.tracks[0].items).toHaveLength(0);
    // The undo stack replays each step's program newest-first (each program itself in order).
    const restored = applyAll(second.doc, [...second.inverse, ...first.inverse]);
    expect(restored).toStrictEqual(doc);
  });
});

describe('seeded: a Trim clamped by the media-handle (sourceIn) floor', () => {
  it('clamps a ripple in-edge delta to −sourceIn and still restores the document exactly', () => {
    const doc = soloMediaClip();
    const item = doc.tracks[0].items[0] as MediaClip;
    expect(item.sourceIn).toBe(2);

    // Asked for −10; only 2 frames of handle exist before the source offset would go negative.
    const applied = assertRoundTrip(doc, {
      type: 'trim',
      itemId: 'solo',
      edge: 'in',
      mode: 'ripple',
      deltaFrames: -10,
    });

    // eng D13: the clip keeps its start; the two frames of handle lengthen it at the head.
    const after = applied.doc.tracks[0].items[0] as MediaClip;
    expect(after.recordIn).toBe(50);
    expect(after.recordOut).toBe(62);
    expect(after.sourceIn).toBe(0);
    // The inverse carries the EFFECTIVE delta, which is what makes the clamped trim invertible.
    expect(applied.inverse).toEqual([
      { type: 'trim', itemId: 'solo', edge: 'in', mode: 'ripple', deltaFrames: 2 },
    ]);
  });

  it('clamps a slip delta to −sourceIn, changing nothing but the source offset', () => {
    const doc = soloMediaClip();
    const applied = assertRoundTrip(doc, {
      type: 'trim',
      itemId: 'solo',
      edge: 'in',
      mode: 'slip',
      deltaFrames: -100,
    });

    const after = applied.doc.tracks[0].items[0] as MediaClip;
    expect(after.recordIn).toBe(50);
    expect(after.recordOut).toBe(60);
    expect(after.sourceIn).toBe(0);
    expect(applied.inverse).toEqual([
      { type: 'trim', itemId: 'solo', edge: 'in', mode: 'slip', deltaFrames: 2 },
    ]);
  });

  it('clamps a roll in-edge delta to the trimmed clip’s own handle, neighbour still a frame long', () => {
    const doc = threeItemTrack();
    // `a` [0,10) sourceIn 3, `b` [10,20) sourceIn 5, `c` [28,36). Rolling `b`'s in edge left can
    // spend at most 5 frames of `b`'s own handle before `sourceIn` would go negative (−30 asked).
    const applied = assertRoundTrip(doc, {
      type: 'trim',
      itemId: 'b',
      edge: 'in',
      mode: 'roll',
      deltaFrames: -30,
    });
    const items = applied.doc.tracks[0].items;
    expect(items[0].recordOut).toBe(5);
    expect((items[1] as MediaClip).recordIn).toBe(5);
    expect((items[1] as MediaClip).recordOut).toBe(20);
    expect((items[1] as MediaClip).sourceIn).toBe(0);
    expect(applied.inverse).toEqual([
      { type: 'trim', itemId: 'b', edge: 'in', mode: 'roll', deltaFrames: 5 },
    ]);
  });

  it('clamps a slide in-edge delta to the neighbour’s own duration floor', () => {
    const doc = leaderAndSolo();
    // Sliding `solo` left by −40 would shrink the 3-frame leader past zero; the floor is one
    // frame (delta −2), and the item comes to rest 58 frames in rather than at 20.
    const applied = assertRoundTrip(doc, {
      type: 'trim',
      itemId: 'solo',
      edge: 'in',
      mode: 'slide',
      deltaFrames: -40,
    });
    const items = applied.doc.tracks[0].items;
    expect(items[0].recordOut).toBe(1);
    expect(items[1].recordIn).toBe(58);
    expect(items[1].recordOut).toBe(68);
    expect(applied.inverse[0]).toMatchObject({
      type: 'trim',
      edge: 'in',
      mode: 'slide',
      deltaFrames: 2,
    });
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// A real defect this suite found — closed by the engine, and pinned as a refusal
// ─────────────────────────────────────────────────────────────────────────────

describe('setProperty cannot clear a field assertInvariants requires', () => {
  /**
   * This block used to record a real defect as an `it.fails` property plus its minimal
   * reproduction. The defect: `PropertyValue` documents `undefined` as "remove the key", and `name`
   * is on every item kind's allowlist — so `setProperty { key: 'name', value: undefined }` was
   * ACCEPTED, deleted `name`, and handed back a document `assertInvariants` then rejected
   * (`name must be a string`). That contradicted `apply.ts`'s own claim that "an op cannot
   * *introduce* a violation", in the direction that matters most: the *result* of a successful op
   * was not a document the engine may edit.
   *
   * The engine now REFUSES that op rather than granting it — see `REQUIRED_PROPERTY_KEYS` in
   * `./ops` and `applySetProperty` in `apply.ts` — and the marker path was closed in the same
   * round, so the item/marker asymmetry this block also used to record no longer exists.
   *
   * The same coverage is inverted below rather than deleted: the defect is pinned as a refusal so
   * it cannot return silently, the refusal is checked to be a refusal of the RIGHT thing (the op is
   * named, the caller's document is untouched, the invariants still hold), and the optional-key
   * clear the fix must not break is pinned beside it.
   *
   * Minimal reproduction, now asserting the refusal:
   *
   *   const doc = <any valid timeline>;               // e.g. one titleClip on one track
   *   apply(doc, { type: 'setProperty', itemId: '<id>', key: 'name', value: undefined });
   *   // throws EditEngineError: "setProperty: key 'name' is required by the document invariants
   *   //                          and cannot be cleared; set a value instead"
   */
  it('refuses to clear a required key, naming the key and carrying the op', () => {
    const doc = twoItemTrack();
    const clearing: EditOp = {
      type: 'setProperty',
      itemId: 'i0',
      key: 'name',
      value: undefined,
    };

    expect(() => apply(doc, clearing)).toThrow(EditEngineError);
    // The message names the offending key, so the failure is actionable without a stack trace.
    expect(() => apply(doc, clearing)).toThrow(
      /^setProperty: key 'name' is required by the document invariants/,
    );

    try {
      apply(doc, clearing);
      expect.unreachable('apply should have refused to clear a required key');
    } catch (error) {
      expect(error).toBeInstanceOf(EditEngineError);
      // `op` is what a UI uses to point at the edit that failed — it must not be dropped.
      expect((error as EditEngineError).op).toBe('setProperty');
    }
  });

  it('refuses it on every item kind, and never half-applies it', () => {
    const doc = kindZoo();
    const before = JSON.parse(JSON.stringify(doc)) as EditTimeline;

    for (const item of doc.tracks[0].items) {
      expect(() =>
        apply(doc, { type: 'setProperty', itemId: item.id, key: 'name', value: undefined }),
      ).toThrow(EditEngineError);
    }

    // A refusal is not a mutation: `apply` cloned up front, so the caller's document is untouched
    // and still satisfies the invariants it satisfied before the refused ops.
    expect(doc).toStrictEqual(before);
    expect(() => assertInvariants(doc)).not.toThrow();
  });

  it('still clears an OPTIONAL key, and that op still round-trips', () => {
    const doc = twoItemTrack();
    const { doc: after, inverse } = apply(doc, {
      type: 'setProperty',
      itemId: 'i0',
      key: 'color',
      value: undefined,
    });

    expect(Object.prototype.hasOwnProperty.call(after.tracks[0].items[0], 'color')).toBe(false);
    assertInvariants(after);
    // The clear is what makes `setProperty` invertible, so it must survive the narrowing.
    expect(applyAll(after, inverse)).toStrictEqual(doc);
  });

  it('setMarker is held to the same shape: clearing the required name is refused', () => {
    const withMarker = timelineOf([trackOf('t0', [])], [
      { id: 'mk', frame: 3, name: 'mk', color: null, note: null, durationFrames: null },
    ]);
    const before = JSON.parse(JSON.stringify(withMarker)) as EditTimeline;
    const clearing = {
      type: 'setMarker',
      markerId: 'mk',
      patch: { name: undefined },
    } as const;

    expect(() => apply(withMarker, clearing)).toThrow(EditEngineError);

    try {
      apply(withMarker, clearing);
      expect.unreachable('setMarker should have refused to clear a required field');
    } catch (error) {
      expect(error).toBeInstanceOf(EditEngineError);
      expect((error as EditEngineError).op).toBe('setMarker');
      expect((error as Error).message).toMatch(/markers\[0\]\.name must be a string/);
    }

    // The write happens before the shape check inside `applySetMarker`, so this assertion is the
    // real test of the "the clone protects the caller" claim rather than a restatement of it.
    expect(withMarker).toStrictEqual(before);
    expect(() => assertInvariants(withMarker)).not.toThrow();
  });

  it('a marker rename still round-trips, so the new check is not broader than the hole', () => {
    const withMarker = timelineOf([trackOf('t0', [])], [
      { id: 'mk', frame: 3, name: 'mk', color: null, note: null, durationFrames: null },
    ]);
    const renamed = { type: 'setMarker', markerId: 'mk', patch: { name: 'renamed' } } as const;
    const { doc: after, inverse } = apply(withMarker, renamed);

    expect(after.markers[0].name).toBe('renamed');
    assertInvariants(after);
    expect(applyAll(after, inverse)).toStrictEqual(withMarker);
  });
});
