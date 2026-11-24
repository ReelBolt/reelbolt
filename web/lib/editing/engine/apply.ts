/**
 * Execution of the closed {@link EditOp} catalogue against an EditTimeline v2 document.
 *
 * Every op is **pure**: `apply` clones the document up front and never writes to the argument or
 * to anything reachable from it, and the inverse program it returns never aliases the returned
 * document either (removed items are detached from the clone before they are embedded in the
 * inverse). Callers therefore get a new document plus a program that, applied in order, restores
 * the one they passed in.
 *
 * ## The round-trip contract
 *
 * For every op, `applyAll(apply(doc, op).doc, inverse)` deep-equals `doc`, and every intermediate
 * document satisfies {@link assertInvariants}. That is not a nice-to-have: undo/redo and
 * optimistic concurrency both rest on it. The two places where the naively-expected semantics
 * would break it are resolved **in favour of the round trip** and documented on the op types in
 * `./ops` and on the handlers here:
 *
 *   - `insert` accepts only the two positions it can be undone from (fits in a gap → `lift`;
 *     flush at the following item's start → the classic ripple → `extract`). A partial overlap
 *     is rejected rather than performed, because its inverse data is destroyed by the ripple.
 *   - `trim` clamps `deltaFrames` (duration floor, `sourceIn` floor, neighbour limits) and its
 *     inverse carries **minus the effective delta**, so a clamped trim still restores exactly.
 *   - `setProperty` clears a key only when the key is optional: `undefined` means "remove the
 *     key" (that is what makes the op invertible), but a key the document invariants REQUIRE may
 *     not be cleared, so such an op is refused rather than performed. See
 *     {@link REQUIRED_PROPERTY_KEYS} in `./ops` for the rule and the set that drives it.
 *
 * Invariants are enforced by {@link assertInvariants}, which the engine deliberately does not run
 * on every op: it is O(document) and belongs at the call sites that need it (the tests run it
 * after every op and after every inverse). The engine's own op handlers validate precisely the
 * fields they touch, so an op cannot *introduce* a violation.
 */

import type { AudioBus, AudioDenoise, AudioDynamics, AudioLeveler, EditSequence, MulticamClip, AssetRef, ChromaKeyer, ClipEffect, ColorGroup, CurvePoint, EditRange, EditTimeline, EditTimelineSettings, GradeStack, GradeWheel, Item, Keyframe, Keyframed, Marker, MediaClip, QuadCorners, Rational, RegionClipEffect, ReframeTarget, ShapeMask, ClipMask, SegmentationMask, Track, Transition } from '../types';
import {
  AUDIO_RANGES,
  BLEND_MODES,
  CHANNEL_MAPS,
  DENOISE_KINDS,
  LEVELER_KINDS,
  MAX_BUSES,
  MAX_MULTICAM_ANGLES,
  MAX_MULTICAM_CUTS,
  MAX_NESTING_DEPTH,
  MAX_SEQUENCES,
  type SetBus,
  type SetSequence,
  ASPECT_POLICIES,
  EFFECT_TYPES,
  GENERATOR_TYPES,
  GRADE_RANGES,
  INTERP_KINDS,
  KEYFRAME_IDENTITY,
  KEYFRAME_PARAM_RANGE,
  MASK_KINDS,
  SEGMENTATION_MASK_MAX_TRACKS,
  SEGMENTATION_MASK_MAX_FEATHER_PX,
  PICTURE_ONLY_ITEM_TYPES,
  MAX_COLOR_GROUPS,
  MAX_CURVE_POINTS,
  MAX_EFFECTS,
  MAX_REGION_EFFECTS,
  REGION_EFFECT_TYPES,
  MAX_BLUR_AMOUNT,
  MAX_GRADE_NODES,
  MAX_KEYFRAMES,
  MAX_REVERSE_FRAMES,
  RETIME_QUALITIES,
  TITLE_TEMPLATES,
  CAPTION_LOOKS,
  TEXT_POSITIONS,
  type SetColorGroup,
  REQUIRED_PROPERTY_KEYS,
  SET_PROPERTY_ALLOWLIST,
  SOURCE_OFFSET_ITEM_TYPES,
  TIMELINE_SETTINGS_KEYS,
  TRACK_PROPERTY_ALLOWLIST,
  TRANSFORM_PARAMS,
  TRANSITION_TYPES,
  type AddMarker,
  type AddTrack,
  type AddTransition,
  type KeyframeParam,
  type KeyframeTarget,
  type MoveKeyframe,
  type RemoveKeyframe,
  type RemoveTransition,
  type Retime,
  type SetKeyframe,
  type Append,
  type FitToFill,
  type Join,
  type MoveTrack,
  type Nudge,
  type PlaceOnTop,
  type RemoveTrack,
  type Replace,
  type SetInOut,
  type SetTimelineSettings,
  type SetTrackProperty,
  type Blade,
  type CloseGaps,
  type DeleteGap,
  type EditOp,
  type Extract,
  type Insert,
  type Lift,
  type Move,
  type Overwrite,
  type PropertyKey,
  type PropertyValue,
  type RemoveMarker,
  type SetMarker,
  type SetProperty,
  type Trim,
} from './ops';
import { isGuid } from '../ids';
import { isCameraProfileId } from '../color/camera-profiles';

// ─────────────────────────────────────────────────────────────────────────────
// Error type
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Every rejection the engine makes is this error — an unknown id, a locked track, a
 * non-canonical index, a wrongly-typed property, a document that violates an invariant.
 *
 * `op` names the op that was being executed when the engine refused, which is what makes the
 * failure actionable in a UI (the offending edit, not just a message).
 */
export class EditEngineError extends Error {
  /** The `type` of the op that was refused, when the failure came from executing one. */
  readonly op?: EditOp['type'];

  constructor(message: string, op?: EditOp['type']) {
    super(op ? `${op}: ${message}` : message);
    this.name = 'EditEngineError';
    this.op = op;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Small structural helpers
// ─────────────────────────────────────────────────────────────────────────────

/** An item seen as a plain field bag — the engine writes fields the discriminated union types. */
type ItemFields = Record<string, unknown> & {
  id: string;
  name: string;
  recordIn: number;
  recordOut: number;
};

type MarkerFields = Record<string, unknown> & { id: string; frame: number };

const asFields = (item: Item): ItemFields => item as unknown as ItemFields;
const asMarkerFields = (marker: Marker): MarkerFields => marker as unknown as MarkerFields;

const hasOwn = (value: object, key: string): boolean =>
  Object.prototype.hasOwnProperty.call(value, key);

const isPlainObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

const isInteger = (value: unknown): value is number =>
  typeof value === 'number' && Number.isInteger(value);

const clamp = (value: number, low: number, high: number): number =>
  Math.min(Math.max(value, low), high);

const durationOf = (item: Item): number => item.recordOut - item.recordIn;

/** The item's source offset, or `undefined` for a kind that has no such field. */
function sourceOffset(item: Item): number | undefined {
  if (!SOURCE_OFFSET_ITEM_TYPES.includes(item.type)) return undefined;
  const raw = asFields(item).sourceIn;
  return typeof raw === 'number' ? raw : undefined;
}

/** Writes an item's source offset. Only called for kinds that carry one. */
function writeSourceOffset(item: Item, value: number): void {
  asFields(item).sourceIn = value;
}

/** Writes (or deletes, for `undefined`) one field on any object. */
function writeField(target: Record<string, unknown>, key: string, value: unknown): void {
  if (value === undefined) {
    delete target[key];
  } else {
    target[key] = value;
  }
}

/** Shifts every key of one keyframed channel by `delta` frames (keys are timeline-absolute). */
function shiftChannelKeys(channel: unknown, delta: number): void {
  if (!isPlainObject(channel) || !Array.isArray(channel.keys)) return;
  for (const key of channel.keys as unknown[]) {
    if (isPlainObject(key) && typeof key.frame === 'number') key.frame += delta;
  }
}

/**
 * Translates the CONTENT an item carries on the timeline clock by `delta`: its keyframes (clip gain,
 * transform channels, an insert's corner pin) and a multicam clip's cuts. Keys and cuts are stored
 * in timeline frames, so any op that moves a clip moves them with it, or the animation detaches.
 */
function shiftItemContent(item: Item, delta: number): void {
  if (delta === 0) return;
  const fields = asFields(item);
  shiftChannelKeys(fields.gainDb, delta);
  shiftChannelKeys(fields.quad, delta);
  if (isPlainObject(fields.transform)) {
    for (const param of TRANSFORM_PARAMS) shiftChannelKeys(fields.transform[param], delta);
  }
  if (item.type === 'multicamClip' && Array.isArray(item.cuts)) {
    for (const cut of item.cuts) cut.atFrame += delta;
  }
}

/** Translates one item, span and content, by `delta`. */
function shiftItem(item: Item, delta: number): void {
  item.recordIn += delta;
  item.recordOut += delta;
  shiftItemContent(item, delta);
}

/** Translates every item from `from` to the end of the track by `delta`. */
function shiftRange(items: Item[], from: number, delta: number): void {
  for (let i = from; i < items.length; i++) {
    shiftItem(items[i], delta);
  }
}

/**
 * The renderer's playback-rate range (`RegionFilterGraphBuilder.MinPlaybackRate` /
 * `MaxPlaybackRate` in inference TimelineRender): the source arithmetic reads a clip's rate through
 * the same clamp, so a stored 1e-12 or 1e6 moves `sourceIn` like the 0.1 or 10 the render plays.
 */
const MIN_SOURCE_RATE = 0.1;
const MAX_SOURCE_RATE = 10;

/** The playback rate the source arithmetic uses: a media clip's own (clamped to the renderer's range), 1 for every other kind. */
function sourceRate(item: Item): number {
  if (item.type !== 'mediaClip') return 1;
  const rate = item.playbackRate;
  if (typeof rate !== 'number' || !Number.isFinite(rate) || rate <= 0) return 1;
  return Math.min(MAX_SOURCE_RATE, Math.max(MIN_SOURCE_RATE, rate));
}

const isReversed = (item: Item): boolean => item.type === 'mediaClip' && item.reverse === true;
const isHeld = (item: Item): boolean => item.type === 'mediaClip' && item.hold === true;

/**
 * Source frames covered by `frames` timeline frames at `rate`, rounded half away from zero so the
 * result is odd-symmetric (`sourceStep(-n) === -sourceStep(n)`): a trim and its inverse move the
 * source by exactly opposite amounts, which is what keeps the round trip exact at any rate.
 */
function sourceStep(frames: number, rate: number): number {
  const step = Math.sign(frames) * Math.round(Math.abs(frames) * rate);
  return Object.is(step, -0) ? 0 : step;
}

/**
 * The largest frame count whose source step still fits in `budget` source frames. Closed form
 * (`round(n × rate) <= budget` iff `n × rate < budget + 0.5`), then at most a few corrective steps
 * for floating-point error: never a scan, so no rate can make it spin (security OE-1).
 */
function framesWithin(budget: number, rate: number): number {
  if (!Number.isFinite(budget) || budget < 0 || !Number.isFinite(rate) || rate <= 0) return 0;
  let frames = Math.floor((budget + 0.5) / rate) + 1;
  if (!Number.isFinite(frames)) return 0;
  for (let step = 0; step < 4 && frames > 0 && sourceStep(frames, rate) > budget; step++) frames--;
  while (frames > 0 && sourceStep(frames, rate) > budget) frames = Math.floor(frames / 2);
  return Math.max(0, frames);
}

/**
 * How `sourceIn` changes when an edge of the item moves by `delta` timeline frames (positive =
 * later) — the formula `SegmentPlanner.SourceFrameAt` and the preview's `sourcePositionFor` read
 * back: a held clip always shows `sourceIn`; a forward clip's `sourceIn` is the frame at its IN
 * edge; a reversed clip's `sourceIn` is the frame at its OUT edge (the lowest it touches).
 */
function sourceShiftForEdge(item: Item, edge: 'in' | 'out', delta: number): number {
  if (isHeld(item)) return 0;
  const rate = sourceRate(item);
  if (isReversed(item)) return edge === 'out' ? -sourceStep(delta, rate) : 0;
  return edge === 'in' ? sourceStep(delta, rate) : 0;
}

/**
 * The bounds an edge move of `delta` must respect so `sourceIn` stays >= 0: only the direction that
 * reads EARLIER source is bounded (a forward clip's head extended, a reversed clip's tail extended).
 */
function edgeSourceBound(item: Item, edge: 'in' | 'out', atLeast: (v: number) => void, atMost: (v: number) => void): void {
  const sourceIn = sourceOffset(item);
  if (sourceIn === undefined || isHeld(item)) return;
  const rate = sourceRate(item);
  if (isReversed(item)) {
    if (edge === 'out') atMost(framesWithin(sourceIn, rate));
  } else if (edge === 'in') {
    atLeast(-framesWithin(sourceIn, rate));
  }
}

/** Moves one edge of an item by `delta`, keeping the source it shows anchored (rate/reverse/hold aware). */
function moveEdge(item: Item, edge: 'in' | 'out', delta: number): void {
  if (delta === 0) return;
  if (edge === 'in') item.recordIn += delta;
  else item.recordOut += delta;
  const sourceIn = sourceOffset(item);
  const shift = sourceShiftForEdge(item, edge, delta);
  if (sourceIn !== undefined && shift !== 0) writeSourceOffset(item, sourceIn + shift);
}

/** The shortest a clip may become: one frame, and never shorter than its fades together. */
function minDurationOf(item: Item): number {
  if (item.type !== 'mediaClip') return 1;
  const fades = (item.fadeInFrames ?? 0) + (item.fadeOutFrames ?? 0);
  return Math.max(1, fades);
}

/** The longest a clip may become: a reversed clip at most {@link MAX_REVERSE_FRAMES}. */
function maxDurationOf(item: Item): number {
  return isReversed(item) ? MAX_REVERSE_FRAMES : Number.POSITIVE_INFINITY;
}

/** The canonical array index for an item starting at `recordIn`: after every earlier start. */
function canonicalIndex(items: readonly Item[], recordIn: number): number {
  let index = 0;
  while (index < items.length && items[index].recordIn < recordIn) index++;
  return index;
}

function clone<T>(value: T): T {
  return structuredClone(value);
}

// ─────────────────────────────────────────────────────────────────────────────
// Invariants
// ─────────────────────────────────────────────────────────────────────────────

/** R08.1: a link id is 1-64 characters of [A-Za-z0-9_-] (the editor mints them with `newGuid()`). */
const LINK_ID_PATTERN = /^[A-Za-z0-9_-]{1,64}$/;
export function isLinkId(value: unknown): value is string {
  return typeof value === 'string' && LINK_ID_PATTERN.test(value);
}

/**
 * R08.1: the link-group rules, per track set (the root timeline, and each compound sequence on its
 * own): every member of a group is a media clip, no two members sit on one track, and they all play
 * the same source. A group of one is tolerated (its partner was deleted or unlinked elsewhere).
 * The wording is plain language: it reaches the user as a refusal.
 */
export function assertLinkGroups(tracks: readonly Track[], pathPrefix = 'tracks'): void {
  const groups = new Map<string, Array<{ item: MediaClip; trackId: string; path: string }>>();
  tracks.forEach((track, t) => {
    track.items.forEach((item, i) => {
      const linkId = (item as { linkId?: unknown }).linkId;
      if (linkId === undefined || linkId === null) return;
      const path = `${pathPrefix}[${t}].items[${i}].linkId`;
      if (item.type !== 'mediaClip') throw new EditEngineError(`${path}: only video, audio and picture clips can be linked`);
      const key = linkId as string;
      const members = groups.get(key) ?? [];
      members.push({ item, trackId: track.id, path });
      groups.set(key, members);
    });
  });
  for (const members of groups.values()) {
    if (members.length > 8) throw new EditEngineError(`${members[8].path}: a linked group can hold at most 8 clips`);
    const seen = new Set<string>();
    for (const { item, trackId, path } of members) {
      if (seen.has(trackId)) throw new EditEngineError(`${path}: two linked clips cannot sit on the same track`);
      seen.add(trackId);
      if (!sameAssetRef(item, members[0].item)) throw new EditEngineError(`${path}: linked clips must all come from the same source`);
    }
  }
}

function sameAssetRef(a: MediaClip, b: MediaClip): boolean {
  return a.assetRef.kind === b.assetRef.kind && (a.assetRef.projectFileId ?? null) === (b.assetRef.projectFileId ?? null) && (a.assetRef.stepResultId ?? null) === (b.assetRef.stepResultId ?? null);
}

/**
 * Throws {@link EditEngineError} unless `doc` satisfies every invariant the engine maintains:
 *
 *   - `schemaVersion === 2` (the v1 shape is not representable, and a document claiming another
 *     version is not one this engine may edit); a document that omits it reads as 2, as the server
 *     binds it;
 *   - unique item, track and marker ids document-wide;
 *   - per track, items sorted by `recordIn` and non-overlapping (`prev.recordOut <= next.recordIn`);
 *   - `0 <= recordIn < recordOut` (integer frames) on every item;
 *   - `sourceIn >= 0` wherever it is present, and `playbackRate > 0` on a `MediaClip`;
 *   - a `MediaClip` always carries an `assetRef`;
 *   - every marker satisfies {@link assertMarkerShape} — `id`, `frame` and `name` — the same check
 *     `addMarker` runs on the marker it splices in, and markers are sorted by `frame`.
 *
 * and, in lockstep with the server's `EditTimelineValidator` and its model binding (T-013), so a
 * document that passes here is never refused by the save:
 *
 *   - every id the server binds as a `Guid` IS one (8-4-4-4-12 hex) — see {@link assertGuidIds};
 *   - a keyframed number never carries `value: null` (`Keyframed<double>.Value` is a double);
 *   - a media clip's fades fit inside it, a reversed clip spans at most `MAX_REVERSE_FRAMES`, and a
 *     clip is never both reversed and held;
 *   - any neighbour pair involving a transition is flush (`current.recordIn === previous.recordOut`);
 *   - a picture-only item (`PICTURE_ONLY_ITEM_TYPES`) never sits on an audio track.
 *
 * `options.ids: 'opaque'` skips ONLY the Guid shape check, for unit fixtures that name things
 * `v1`/`track-video`; production callers (the sync client, the offline queue, proposals) use the
 * default.
 *
 * The message names the offending path (e.g. `tracks[0].items[2].recordOut`) so a caller can say
 * exactly what to fix.
 */
export function assertInvariants(
  doc: EditTimeline,
  options: { ids?: 'guid' | 'opaque'; sourceDurations?: SourceDurationFrames; segmentationTracks?: SegmentationTrackLookup } = {},
): void {
  // A missing schemaVersion reads as 2 (the server binds the absent field as 2); any other value,
  // null included, is refused.
  const schemaVersion: unknown = (doc as { schemaVersion?: unknown }).schemaVersion;
  if (schemaVersion !== undefined && schemaVersion !== 2) {
    throw new EditEngineError(
      `schemaVersion must be 2, found ${String(schemaVersion)}`,
    );
  }

  const itemIds = new Set<string>();
  const trackIds = new Set<string>();

  for (let t = 0; t < doc.tracks.length; t++) {
    const track = doc.tracks[t];
    const trackPath = `tracks[${t}]`;

    if (typeof track.id !== 'string' || track.id.length === 0) {
      throw new EditEngineError(`${trackPath}.id must be a non-empty string`);
    }
    if (trackIds.has(track.id)) {
      throw new EditEngineError(`${trackPath}.id duplicates track id '${track.id}'`);
    }
    trackIds.add(track.id);
    assertChannelValue(track.gainDb, `${trackPath}.gainDb`);

    let previous: Item | undefined;
    for (let i = 0; i < track.items.length; i++) {
      const item = track.items[i];
      const path = `${trackPath}.items[${i}]`;

      assertItemShape(item, path, options.segmentationTracks);
      assertItemFitsTrack(item, track, undefined, path);
      if (options.sourceDurations !== undefined) assertSourceExtent(item, options.sourceDurations, path);

      if (itemIds.has(item.id)) {
        throw new EditEngineError(`${path}.id duplicates item id '${item.id}'`);
      }
      itemIds.add(item.id);

      if (previous !== undefined) {
        if (item.recordIn < previous.recordIn) {
          throw new EditEngineError(
            `${path}.recordIn (${item.recordIn}) breaks the track's recordIn ordering after ${previous.recordIn}`,
          );
        }
        if (previous.recordOut > item.recordIn) {
          throw new EditEngineError(
            `${path}.recordIn (${item.recordIn}) overlaps the previous item, which ends at ${previous.recordOut}`,
          );
        }
        assertTransitionFlush(previous, item, path);
      }
      previous = item;
    }
  }

  const markerIds = new Set<string>();
  for (let m = 0; m < doc.markers.length; m++) {
    const marker = doc.markers[m];
    const path = `markers[${m}]`;

    // The marker's own field invariants (id, frame, name) live in `assertMarkerShape`, which
    // `addMarker` also runs on the marker it splices in — one definition for both layers, so a
    // marker field the engine requires cannot be checked in one place and missed in the other.
    assertMarkerShape(marker, path);

    if (markerIds.has(marker.id)) {
      throw new EditEngineError(`${path}.id duplicates marker id '${marker.id}'`);
    }
    markerIds.add(marker.id);

    if (m > 0 && doc.markers[m - 1].frame > marker.frame) {
      throw new EditEngineError(
        `${path}.frame (${marker.frame}) breaks the marker frame ordering after ${doc.markers[m - 1].frame}`,
      );
    }
  }

  assertLinkGroups(doc.tracks, 'tracks');
  assertColorGroups(doc);
  assertSequences(doc, itemIds);
  (doc.sequences ?? []).forEach((sequence, q) => assertLinkGroups(sequence.tracks, `sequences[${q}].tracks`));
  assertBuses(doc);
  if (options.ids !== 'opaque') assertGuidIds(doc);
}

/**
 * Segmentation masks: VideoAnalyze step-result id (same project) -> its `g{n}` track ids. Optional and only
 * for callers that know the project's analyses; the server validator is the trusted check. A mask naming an id
 * that is absent (foreign, missing, or not an analysis alike) is refused with one message.
 */
export type SegmentationTrackLookup = Readonly<Record<string, readonly string[]>>;

/**
 * R01.6: the known length, in timeline frames, of a source, keyed by the asset's project-file id or
 * step-result id. A source that is absent is "not known" and never refused (fail open), exactly as the
 * server's validator reads a missing duration.
 */
export type SourceDurationFrames = Readonly<Record<string, number>>;

/**
 * T-023: the media clips (`tracks[i].items[j]` paths) whose source length is NOT known, so the overrun check
 * above could not run for them. A clip is never refused for it (fail open) but it is reported, so the editor can
 * say "length not checked" instead of silently passing. Mirrors `EditTimelineValidator.UncheckedMediaClips`.
 */
export function uncheckedMediaClips(doc: EditTimeline, durations: SourceDurationFrames): string[] {
  const out: string[] = [];
  doc.tracks.forEach((track, t) => {
    track.items.forEach((item, i) => {
      if (item.type !== 'mediaClip' || item.recordOut <= item.recordIn) return;
      const assetRef = asFields(item).assetRef;
      if (!isPlainObject(assetRef)) return;
      const id = typeof assetRef.projectFileId === 'string' ? assetRef.projectFileId : assetRef.stepResultId;
      if (typeof id !== 'string') return;
      if (!hasOwn(durations, id)) out.push(`tracks[${t}].items[${i}]`);
    });
  });
  return out;
}

/**
 * `EditTimelineValidator.ValidateMediaClipSourceExtent`: a media clip whose source in/out lies past the
 * known source length is refused, naming the clip by its label. The source frames a clip consumes are its
 * record extent times its playback rate (one frame for a freeze); only a provable overrun is refused.
 */
function assertSourceExtent(item: Item, durations: SourceDurationFrames, path: string): void {
  if (item.type !== 'mediaClip') return;
  const fields = asFields(item);
  const assetRef = fields.assetRef;
  if (!isPlainObject(assetRef)) return;
  const id = typeof assetRef.projectFileId === 'string' ? assetRef.projectFileId : assetRef.stepResultId;
  if (typeof id !== 'string' || !hasOwn(durations, id)) return;

  const available = durations[id];
  const extent = item.recordOut - item.recordIn;
  const rawRate = fields.playbackRate;
  const rate = typeof rawRate === 'number' && Number.isFinite(rawRate) && rawRate > 0 ? rawRate : 1;
  const consumed = fields.hold === true ? 1 : Math.floor(extent * rate);
  const sourceIn = typeof fields.sourceIn === 'number' ? fields.sourceIn : 0;
  const required = sourceIn + Math.max(1, consumed);
  if (required > available) {
    throw new EditEngineError(
      `${path}.sourceIn: the clip "${item.name}" runs past the end of its source (it needs ${required} frames of the source, which has ${available})`,
    );
  }
}

/**
 * `EditTimelineValidator.ValidateTrackOrdering`: any neighbour pair involving a transition must be
 * flush — a transition sits exactly at the boundary between its neighbours.
 */
function assertTransitionFlush(previous: Item, item: Item, path: string, op?: EditOp['type']): void {
  if ((previous.type === 'transition' || item.type === 'transition') && item.recordIn !== previous.recordOut) {
    throw new EditEngineError(
      `${path}.recordIn: a transition must sit exactly at the boundary between its neighbours (expected ${previous.recordOut}, found ${item.recordIn})`,
      op,
    );
  }
}

/** A keyframed number's static `value` may be absent but never `null` — the server binds it as a `double`. */
function assertChannelValue(channel: unknown, path: string, op?: EditOp['type']): void {
  if (isPlainObject(channel) && hasOwn(channel, 'value') && channel.value === null) {
    throw new EditEngineError(`${path}.value must be a number or absent, not null`, op);
  }
}

/**
 * Every field the server deserializes as a `Guid` must be one, or model binding refuses the whole
 * save before the validator even runs: the header ids, track/bus/marker/sequence/colour-group ids
 * and routes, every item id document-wide, compound and multicam references, grade-node ids, and
 * the ids inside asset references and provenance. Display-only ids the client derives (rationale
 * marker rows, sub-document ids) never reach a document, so they are not checked here.
 */
function assertGuidIds(doc: EditTimeline): void {
  const guid = (value: unknown, path: string, nullable = false): void => {
    if (nullable && (value === undefined || value === null)) return;
    if (!isGuid(value)) throw new EditEngineError(`${path} must be a Guid (8-4-4-4-12 hex digits), found '${String(value)}'`);
  };
  const assetRef = (ref: unknown, path: string): void => {
    if (!isPlainObject(ref)) return;
    guid(ref.projectFileId, `${path}.projectFileId`, true);
    guid(ref.stepResultId, `${path}.stepResultId`, true);
  };
  const grade = (stack: unknown, path: string): void => {
    if (!isPlainObject(stack) || !Array.isArray(stack.nodes)) return;
    stack.nodes.forEach((node, n) => {
      if (isPlainObject(node)) guid(node.id, `${path}.nodes[${n}].id`);
    });
  };
  const item = (value: Item, path: string): void => {
    guid(value.id, `${path}.id`);
    const fields = asFields(value);
    assetRef(fields.assetRef, `${path}.assetRef`);
    grade(fields.grade, `${path}.grade`);
    const provenance = fields.provenance;
    if (isPlainObject(provenance)) {
      guid(provenance.stepResultId, `${path}.provenance.stepResultId`, true);
      guid(provenance.agentDefinitionId, `${path}.provenance.agentDefinitionId`, true);
      guid(provenance.providerId, `${path}.provenance.providerId`, true);
    }
    if (value.type === 'compoundClip') guid(value.sequenceId, `${path}.sequenceId`);
    if (value.type === 'multicamClip') {
      guid(value.activeAngleId, `${path}.activeAngleId`);
      (value.angles ?? []).forEach((angle, a) => {
        guid(angle.id, `${path}.angles[${a}].id`);
        assetRef(angle.assetRef, `${path}.angles[${a}].assetRef`);
      });
      (value.cuts ?? []).forEach((cut, c) => guid(cut.angleId, `${path}.cuts[${c}].angleId`));
    }
  };
  const tracks = (list: readonly Track[], base: string): void => {
    list.forEach((track, t) => {
      guid(track.id, `${base}[${t}].id`);
      guid(track.busId, `${base}[${t}].busId`, true);
      track.items.forEach((value, i) => item(value, `${base}[${t}].items[${i}]`));
    });
  };

  guid(doc.id, 'id');
  guid(doc.projectId, 'projectId');
  if (isPlainObject(doc.seed)) guid(doc.seed.executionId, 'seed.executionId');
  tracks(doc.tracks, 'tracks');
  doc.markers.forEach((marker, m) => guid(marker.id, `markers[${m}].id`));
  (doc.sequences ?? []).forEach((sequence, q) => {
    guid(sequence.id, `sequences[${q}].id`);
    tracks(sequence.tracks, `sequences[${q}].tracks`);
  });
  (doc.colorGroups ?? []).forEach((group, g) => {
    guid(group.id, `colorGroups[${g}].id`);
    group.clipIds.forEach((clipId, c) => guid(clipId, `colorGroups[${g}].clipIds[${c}]`));
    grade(group.grade, `colorGroups[${g}].grade`);
  });
  (doc.buses ?? []).forEach((bus, b) => {
    guid(bus.id, `buses[${b}].id`);
    guid(bus.outputBusId, `buses[${b}].outputBusId`, true);
  });
}

/**
 * P8: the nested-sequence invariants — at most `MAX_SEQUENCES`, unique non-empty ids, a name, each
 * sequence's tracks under the same per-track rules as the root, item ids unique DOCUMENT-WIDE (root
 * and every sequence, so a compound can be decomposed without an id clash), and the nesting rules:
 * every compound clip names an existing sequence, no sequence contains itself, the chain from the
 * root is at most `MAX_NESTING_DEPTH` deep. `rootItemIds` are the root's ids, already collected.
 */
function assertSequences(doc: EditTimeline, rootItemIds: Set<string>, op?: EditOp['type']): void {
  const sequences = doc.sequences;
  if (sequences !== undefined && sequences !== null) {
    if (!Array.isArray(sequences)) throw new EditEngineError('sequences must be an array or null', op);
    if (sequences.length > MAX_SEQUENCES) throw new EditEngineError(`a timeline may carry at most ${MAX_SEQUENCES} sequences, found ${sequences.length}`, op);
    const ids = new Set<string>();
    const itemIds = new Set(rootItemIds);
    sequences.forEach((sequence, q) => {
      const path = `sequences[${q}]`;
      if (!isPlainObject(sequence) || typeof sequence.id !== 'string' || sequence.id.length === 0) throw new EditEngineError(`${path}.id must be a non-empty string`, op);
      if (ids.has(sequence.id)) throw new EditEngineError(`${path}.id duplicates sequence id '${sequence.id}'`, op);
      ids.add(sequence.id);
      if (typeof sequence.name !== 'string' || sequence.name.trim().length === 0) throw new EditEngineError(`${path}.name must be a non-empty string`, op);
      if (!Array.isArray(sequence.tracks)) throw new EditEngineError(`${path}.tracks must be an array`, op);
      const trackIds = new Set<string>();
      sequence.tracks.forEach((track, t) => {
        const trackPath = `${path}.tracks[${t}]`;
        if (!isPlainObject(track) || typeof track.id !== 'string' || track.id.length === 0) throw new EditEngineError(`${trackPath}.id must be a non-empty string`, op);
        if (trackIds.has(track.id)) throw new EditEngineError(`${trackPath}.id duplicates track id '${track.id}'`, op);
        trackIds.add(track.id);
        if (track.kind !== 'video' && track.kind !== 'audio') throw new EditEngineError(`${trackPath}.kind must be 'video' or 'audio'`, op);
        if (!Array.isArray(track.items)) throw new EditEngineError(`${trackPath}.items must be an array`, op);
        assertChannelValue(track.gainDb, `${trackPath}.gainDb`, op);
        let previous: Item | undefined;
        track.items.forEach((item, i) => {
          const itemPath = `${trackPath}.items[${i}]`;
          assertItemShape(item, itemPath);
          assertItemFitsTrack(item, track, op, itemPath);
          if (itemIds.has(item.id)) throw new EditEngineError(`${itemPath}.id duplicates item id '${item.id}' (item ids are unique across the root and every sequence)`, op);
          itemIds.add(item.id);
          if (previous !== undefined && previous.recordOut > item.recordIn) throw new EditEngineError(`${itemPath}.recordIn (${item.recordIn}) overlaps the previous item, which ends at ${previous.recordOut}`, op);
          if (previous !== undefined) assertTransitionFlush(previous, item, itemPath, op);
          previous = item;
        });
      });
    });
  }

  const byId = new Map<string, EditSequence>();
  for (const sequence of doc.sequences ?? []) byId.set(sequence.id, sequence);
  const walk = (tracks: readonly Track[], depth: number, onPath: Set<string>, base: string): void => {
    tracks.forEach((track, t) => {
      track.items.forEach((item, i) => {
        if (item.type !== 'compoundClip') return;
        const path = `${base}[${t}].items[${i}].sequenceId`;
        const found = byId.get(item.sequenceId);
        if (found === undefined) throw new EditEngineError(`${path}: sequence '${item.sequenceId}' does not exist in this timeline`, op);
        if (onPath.has(item.sequenceId)) throw new EditEngineError(`${path}: sequence '${item.sequenceId}' contains itself`, op);
        if (depth >= MAX_NESTING_DEPTH) throw new EditEngineError(`${path}: compound clips may nest at most ${MAX_NESTING_DEPTH} deep`, op);
        onPath.add(item.sequenceId);
        walk(found.tracks, depth + 1, onPath, `sequence '${found.name}'.tracks`);
        onPath.delete(item.sequenceId);
      });
    });
  };
  walk(doc.tracks, 1, new Set(), 'tracks');
}

/** P8: the processing a track or a bus carries — the validator's `ValidateAudioChannel` ranges. */
export function isAudioDynamics(value: unknown): value is AudioDynamics {
  if (!isPlainObject(value)) return false;
  for (const key of Object.keys(value)) if (!['compressor', 'limiter', 'gate', 'deesser'].includes(key)) return false;
  for (const stage of ['compressor', 'limiter', 'gate', 'deesser'] as const) {
    const v = value[stage];
    if (v === undefined || v === null) continue;
    if (!isPlainObject(v)) return false;
    const ranges = AUDIO_RANGES[stage] as Record<string, readonly [number, number]>;
    for (const key of Object.keys(v)) {
      const range = ranges[key];
      if (range === undefined) return false;
      if (v[key] !== undefined && !inRange(v[key], range[0], range[1])) return false;
    }
  }
  return true;
}

export function isAudioDenoise(value: unknown): value is AudioDenoise {
  if (!isPlainObject(value)) return false;
  for (const key of Object.keys(value)) if (!['kind', 'strength'].includes(key)) return false;
  if (value.kind !== undefined && !DENOISE_KINDS.includes(String(value.kind))) return false;
  return value.strength === undefined || inRange(value.strength, AUDIO_RANGES.denoise.strength[0], AUDIO_RANGES.denoise.strength[1]);
}

export function isAudioLeveler(value: unknown): value is AudioLeveler {
  if (!isPlainObject(value)) return false;
  for (const key of Object.keys(value)) if (!['kind', 'targetDb'].includes(key)) return false;
  if (value.kind !== undefined && !LEVELER_KINDS.includes(String(value.kind))) return false;
  return value.targetDb === undefined || inRange(value.targetDb, AUDIO_RANGES.leveler.targetDb[0], AUDIO_RANGES.leveler.targetDb[1]);
}

/** P8: one bus's own fields (routing is checked across all buses by {@link assertBuses}). */
function assertBusShape(bus: AudioBus, path: string, op?: EditOp['type']): void {
  if (!isPlainObject(bus) || typeof bus.id !== 'string' || bus.id.length === 0) throw new EditEngineError(`${path}.id must be a non-empty string`, op);
  if (typeof bus.name !== 'string' || bus.name.trim().length === 0) throw new EditEngineError(`${path}.name must be a non-empty string`, op);
  if (bus.outputBusId === bus.id) throw new EditEngineError(`${path}.outputBusId: a bus cannot feed itself`, op);
  if (bus.outputBusId !== undefined && bus.outputBusId !== null && typeof bus.outputBusId !== 'string') throw new EditEngineError(`${path}.outputBusId must be a string or null`, op);
  if (bus.gainDb !== undefined && bus.gainDb !== null && !isKeyframedNumber(bus.gainDb, 'gainDb')) throw new EditEngineError(`${path}.gainDb is not a valid fader`, op);
  if (bus.pan !== undefined && !inRange(bus.pan, -1, 1)) throw new EditEngineError(`${path}.pan must be in [-1, 1]`, op);
  if (bus.eq !== undefined && bus.eq !== null && !isTrackEq(bus.eq)) throw new EditEngineError(`${path}.eq is not a valid equaliser`, op);
  if (bus.dynamics !== undefined && bus.dynamics !== null && !isAudioDynamics(bus.dynamics)) throw new EditEngineError(`${path}.dynamics is out of range`, op);
  if (bus.denoise !== undefined && bus.denoise !== null && !isAudioDenoise(bus.denoise)) throw new EditEngineError(`${path}.denoise is out of range`, op);
  if (bus.leveler !== undefined && bus.leveler !== null && !isAudioLeveler(bus.leveler)) throw new EditEngineError(`${path}.leveler is out of range`, op);
}

/**
 * P8: the bus invariants — at most `MAX_BUSES`, unique ids, valid fields, and routing that never
 * cycles or chains deeper than `MAX_NESTING_DEPTH`. A route to a bus that does not exist is
 * TOLERATED (it feeds the master), exactly as the server's validator tolerates it.
 */
function assertBuses(doc: EditTimeline, op?: EditOp['type']): void {
  const buses = doc.buses;
  if (buses === undefined || buses === null) return;
  if (!Array.isArray(buses)) throw new EditEngineError('buses must be an array or null', op);
  if (buses.length > MAX_BUSES) throw new EditEngineError(`a timeline may carry at most ${MAX_BUSES} buses, found ${buses.length}`, op);
  const byId = new Map<string, AudioBus>();
  buses.forEach((bus, b) => {
    assertBusShape(bus, `buses[${b}]`, op);
    if (byId.has(bus.id)) throw new EditEngineError(`buses[${b}].id duplicates bus id '${bus.id}'`, op);
    byId.set(bus.id, bus);
  });
  buses.forEach((bus, b) => {
    const seen = new Set<string>([bus.id]);
    let next = bus.outputBusId ?? null;
    let depth = 1;
    while (next !== null && byId.has(next)) {
      if (seen.has(next)) throw new EditEngineError(`buses[${b}].outputBusId: bus routing forms a cycle`, op);
      seen.add(next);
      depth++;
      if (depth > MAX_NESTING_DEPTH) throw new EditEngineError(`buses[${b}].outputBusId: buses may chain at most ${MAX_NESTING_DEPTH} deep`, op);
      next = byId.get(next)?.outputBusId ?? null;
    }
  });
}

/**
 * P8: a multicam clip's own invariants — 1..`MAX_MULTICAM_ANGLES` angles with unique ids and an
 * asset each, an active angle among them, cuts strictly increasing, strictly inside the span and
 * naming known angles. Run on every item shape check, so a trim that would strand a cut outside the
 * clip is refused by the post-op structure check rather than saved and rejected by the server.
 */
function assertMulticamShape(item: MulticamClip, path: string, op?: EditOp['type']): void {
  if (!Array.isArray(item.angles) || item.angles.length === 0) throw new EditEngineError(`${path}.angles: a multicam clip needs at least one angle`, op);
  if (item.angles.length > MAX_MULTICAM_ANGLES) throw new EditEngineError(`${path}.angles: at most ${MAX_MULTICAM_ANGLES} angles, found ${item.angles.length}`, op);
  const ids = new Set<string>();
  item.angles.forEach((angle, a) => {
    const anglePath = `${path}.angles[${a}]`;
    if (!isPlainObject(angle) || typeof angle.id !== 'string' || angle.id.length === 0) throw new EditEngineError(`${anglePath}.id must be a non-empty string`, op);
    if (ids.has(angle.id)) throw new EditEngineError(`${anglePath}.id duplicates angle id '${angle.id}'`, op);
    ids.add(angle.id);
    if (typeof angle.name !== 'string') throw new EditEngineError(`${anglePath}.name must be a string`, op);
    if (!isAssetRef(angle.assetRef)) throw new EditEngineError(`${anglePath}.assetRef must be an asset reference`, op);
    if (angle.offsetFrames !== undefined && !isInteger(angle.offsetFrames)) throw new EditEngineError(`${anglePath}.offsetFrames must be an integer`, op);
    // P3: the angle's camera profile is the same closed word as a media clip's; null means display
    // space, so only a present value is checked.
    if (angle.inputProfile !== undefined && angle.inputProfile !== null && !isCameraProfileId(angle.inputProfile)) {
      throw new EditEngineError(`${anglePath}.inputProfile '${String(angle.inputProfile)}' is not in the camera-profile catalogue`, op);
    }
  });
  if (!ids.has(item.activeAngleId)) throw new EditEngineError(`${path}.activeAngleId '${String(item.activeAngleId)}' is not one of the clip's angles`, op);
  const audioSource = item.audioSource;
  if (audioSource !== undefined && audioSource !== null && audioSource !== 'program' && audioSource !== 'none' && !ids.has(audioSource)) {
    throw new EditEngineError(`${path}.audioSource '${String(audioSource)}' must be 'program', 'none' or one of the clip's angles`, op);
  }
  const cuts = item.cuts;
  if (cuts === undefined || cuts === null) return;
  if (!Array.isArray(cuts)) throw new EditEngineError(`${path}.cuts must be an array or null`, op);
  if (cuts.length > MAX_MULTICAM_CUTS) throw new EditEngineError(`${path}.cuts: at most ${MAX_MULTICAM_CUTS} cuts, found ${cuts.length}`, op);
  let previous = Number.NEGATIVE_INFINITY;
  cuts.forEach((cut, c) => {
    const cutPath = `${path}.cuts[${c}]`;
    if (!isPlainObject(cut) || !isInteger(cut.atFrame)) throw new EditEngineError(`${cutPath}.atFrame must be an integer`, op);
    if (cut.atFrame <= item.recordIn || cut.atFrame >= item.recordOut) throw new EditEngineError(`${cutPath}.atFrame ${cut.atFrame} must fall strictly inside the clip's span (${item.recordIn}, ${item.recordOut})`, op);
    if (cut.atFrame <= previous) throw new EditEngineError(`${cutPath}.atFrame: cuts must be in strictly increasing frame order`, op);
    previous = cut.atFrame;
    if (!ids.has(cut.angleId)) throw new EditEngineError(`${cutPath}.angleId '${String(cut.angleId)}' is not one of the clip's angles`, op);
  });
}

/**
 * P8: the structural checks every op ends with — compound references resolve (no cycle, depth ≤ 4)
 * and every multicam clip's cuts still fall inside its (possibly trimmed) span. Cheap (one pass over
 * the root items), and what keeps an op from handing the sync client a document the server would
 * refuse: the op is refused instead, and its clone discarded.
 */
function assertStructureAfter(doc: EditTimeline, op: EditOp['type']): void {
  let hasCompound = false;
  for (const track of doc.tracks) {
    for (const item of track.items) {
      if (item.type === 'multicamClip') assertMulticamShape(item, `item '${item.id}'`, op);
      else if (item.type === 'compoundClip') hasCompound = true;
    }
  }
  if (hasCompound || (doc.sequences?.length ?? 0) > 0) {
    const rootIds = new Set(doc.tracks.flatMap((t) => t.items.map((i) => i.id)));
    assertSequences(doc, rootIds, op);
  }
}

/**
 * P7: the colour-group invariants — at most `MAX_COLOR_GROUPS`, unique ids, a non-empty name, no
 * clip in two groups, a valid grade. A member id that no longer names a media clip is TOLERATED
 * (the clip was lifted or extracted after it was grouped): it is inert for the renderer and the
 * group panel shows it as gone, which is what lets `lift`/`extract`/`removeTrack` keep their exact
 * single-op inverses instead of also editing groups. `setColorGroup` itself is strict (`strict`) for
 * the group it sets: a member it ADDS must be a media clip; one the group already had may dangle
 * (that is what lets an inverse restore a group whose clip was lifted since).
 */
function assertColorGroups(doc: EditTimeline, strict?: { groupId: string; tolerated: ReadonlySet<string> }): void {
  const groups = doc.colorGroups;
  if (groups === undefined || groups === null) return;
  if (!Array.isArray(groups)) throw new EditEngineError('colorGroups must be an array or null');
  if (groups.length > MAX_COLOR_GROUPS) throw new EditEngineError(`a timeline may carry at most ${MAX_COLOR_GROUPS} colour groups, found ${groups.length}`);
  const mediaIds = new Set(doc.tracks.flatMap((t) => t.items.filter((i) => i.type === 'mediaClip').map((i) => i.id)));
  const groupIds = new Set<string>();
  const members = new Set<string>();
  groups.forEach((group, g) => {
    const path = `colorGroups[${g}]`;
    if (!isPlainObject(group) || typeof group.id !== 'string' || group.id.length === 0) throw new EditEngineError(`${path}.id must be a non-empty string`);
    if (groupIds.has(group.id)) throw new EditEngineError(`${path}.id duplicates colour group id '${group.id}'`);
    groupIds.add(group.id);
    if (typeof group.name !== 'string' || group.name.trim().length === 0) throw new EditEngineError(`${path}.name must be a non-empty string`);
    if (!Array.isArray(group.clipIds)) throw new EditEngineError(`${path}.clipIds must be an array`);
    group.clipIds.forEach((clipId, c) => {
      if (typeof clipId !== 'string' || clipId.length === 0) throw new EditEngineError(`${path}.clipIds[${c}] must be a non-empty string`);
      if (strict !== undefined && group.id === strict.groupId && !mediaIds.has(clipId) && !strict.tolerated.has(clipId)) throw new EditEngineError(`${path}.clipIds[${c}] '${clipId}' is not a media clip of this timeline`, 'setColorGroup');
      if (members.has(clipId)) throw new EditEngineError(`${path}.clipIds[${c}] '${clipId}' is in more than one colour group`);
      members.add(clipId);
    });
    if (group.grade !== undefined && group.grade !== null && !isGradeStack(group.grade)) throw new EditEngineError(`${path}.grade is not a valid grade stack`);
  });
}

/** One item's own field invariants — shared by `assertInvariants` and by the ops that add items. */
function assertItemShape(item: Item, path: string, segmentationTracks?: SegmentationTrackLookup): void {
  if (!isPlainObject(item)) {
    throw new EditEngineError(`${path} must be an object`);
  }
  if (typeof item.id !== 'string' || item.id.length === 0) {
    throw new EditEngineError(`${path}.id must be a non-empty string`);
  }
  if (typeof item.name !== 'string') {
    throw new EditEngineError(`${path}.name must be a string`);
  }
  if (!isInteger(item.recordIn) || !isInteger(item.recordOut)) {
    throw new EditEngineError(
      `${path} must use integer frames, found recordIn ${item.recordIn} / recordOut ${item.recordOut}`,
    );
  }
  if (item.recordIn < 0) {
    throw new EditEngineError(`${path}.recordIn must be >= 0, found ${item.recordIn}`);
  }
  if (item.recordIn >= item.recordOut) {
    throw new EditEngineError(
      `${path}.recordOut (${item.recordOut}) must be greater than recordIn (${item.recordIn})`,
    );
  }

  const fields = asFields(item);
  if (hasOwn(fields, 'sourceIn')) {
    const sourceIn = fields.sourceIn;
    if (sourceIn !== undefined && sourceIn !== null) {
      if (!isInteger(sourceIn) || sourceIn < 0) {
        throw new EditEngineError(`${path}.sourceIn must be a non-negative integer, found ${String(sourceIn)}`);
      }
    }
  }

  if (item.type === 'compoundClip') {
    if (typeof fields.sequenceId !== 'string' || fields.sequenceId.length === 0) {
      throw new EditEngineError(`${path}.sequenceId must be a non-empty string`);
    }
  }
  if (item.type === 'multicamClip') {
    assertMulticamShape(item, path);
  }

  if (item.type === 'titleClip') assertTransformChannels(fields.transform, path);
  if (item.type === 'caption' || item.type === 'titleClip') {
    if (fields.position !== undefined && fields.position !== null && !TEXT_POSITIONS.includes(String(fields.position))) {
      throw new EditEngineError(`${path}.position '${String(fields.position)}' is not on the text-position pad`);
    }
  }
  if (item.type === 'caption' && fields.look !== undefined && fields.look !== null && !CAPTION_LOOKS.includes(String(fields.look))) {
    throw new EditEngineError(`${path}.look '${String(fields.look)}' is not in the caption-look catalogue`);
  }

  if (item.type === 'mediaClip') {
    if (!isPlainObject(fields.assetRef)) {
      throw new EditEngineError(`${path}.assetRef is required on a mediaClip`);
    }
    if (fields.channelMap !== undefined && fields.channelMap !== null && !CHANNEL_MAPS.includes(String(fields.channelMap))) {
      throw new EditEngineError(`${path}.channelMap '${String(fields.channelMap)}' is not in the channel-map catalogue`);
    }
    if (fields.blendMode !== undefined && fields.blendMode !== null && (typeof fields.blendMode !== 'string' || !BLEND_MODES.includes(fields.blendMode))) {
      throw new EditEngineError(`${path}.blendMode '${String(fields.blendMode)}' is not in the blend-mode catalogue`);
    }
    if (fields.linkId !== undefined && fields.linkId !== null && !isLinkId(fields.linkId)) {
      throw new EditEngineError(`${path}.linkId must be 1-64 letters, digits, '-' or '_'`);
    }
    // P3: the camera profile is a closed word. null means no transform, so only a present value is
    // checked; a non-string cannot be a profile id and is refused the same way.
    if (fields.inputProfile !== undefined && fields.inputProfile !== null && !isCameraProfileId(fields.inputProfile)) {
      throw new EditEngineError(`${path}.inputProfile '${String(fields.inputProfile)}' is not in the camera-profile catalogue`);
    }
    if (fields.sourceRotation !== undefined && fields.sourceRotation !== null && (typeof fields.sourceRotation !== 'number' || ![0, 90, 180, 270].includes(fields.sourceRotation))) {
      throw new EditEngineError(`${path}.sourceRotation must be 0, 90, 180 or 270`);
    }
    if (Array.isArray(fields.effects)) {
      fields.effects.forEach((e: { type?: unknown; amount?: unknown }, i: number) => {
        if (isPlainObject(e) && typeof e.amount === 'number' && !inRange(e.amount, 0, e.type === 'blur' ? MAX_BLUR_AMOUNT : 1)) {
          throw new EditEngineError(`${path}.effects[${i}].amount must be in [0, ${e.type === 'blur' ? MAX_BLUR_AMOUNT : 1}] for ${String(e.type)}`);
        }
      });
    }
    if (fields.mask !== undefined && fields.mask !== null) {
      if (!isClipMask(fields.mask)) {
        const problem = (fields.mask as { kind?: unknown }).kind === 'segmentation' ? segmentationMaskProblem(fields.mask) : undefined;
        throw new EditEngineError(`${path}.mask${problem ? '.' + problem : ''} is not a valid shape or segmentation mask`);
      }
      if (isSegmentationMask(fields.mask) && segmentationTracks !== undefined) {
        const known = segmentationTracks[fields.mask.analysisStepResultId];
        // One message for a foreign/missing analysis and an unknown track: no existence oracle.
        if (known === undefined || fields.mask.trackIds.some((id) => !known.includes(id))) {
          throw new EditEngineError(`${path}.mask.analysisStepResultId does not name a video analysis of this project with all of its tracks`);
        }
      }
    }
    if (fields.regionEffects !== undefined && fields.regionEffects !== null) {
      const problem = regionEffectsProblem(fields.regionEffects);
      if (problem) throw new EditEngineError(`${path}.regionEffects${problem}`);
      if (segmentationTracks !== undefined) {
        (fields.regionEffects as RegionClipEffect[]).forEach((e, i) => {
          const known = segmentationTracks[e.mask.analysisStepResultId];
          if (known === undefined || e.mask.trackIds.some((id) => !known.includes(id))) {
            throw new EditEngineError(`${path}.regionEffects[${i}].mask.analysisStepResultId does not name a video analysis of this project with all of its tracks`);
          }
        });
      }
      if ((fields.regionEffects as unknown[]).length > 0 && fields.stabilize === true) {
        throw new EditEngineError(`${path}.stabilize cannot be combined with region effects`);
      }
    }
    if (isSegmentationMask(fields.mask) && fields.stabilize === true) {
      throw new EditEngineError(`${path}.stabilize cannot be combined with a segmentation mask`);
    }
    const playbackRate = fields.playbackRate;
    if (playbackRate !== undefined && playbackRate !== null) {
      if (typeof playbackRate !== 'number' || !Number.isFinite(playbackRate) || playbackRate <= 0) {
        throw new EditEngineError(`${path}.playbackRate must be greater than zero, found ${String(playbackRate)}`);
      }
    }
    assertMediaDepth(item, path);
  }
  if (item.type === 'multicamClip') {
    assertChannelValue(fields.gainDb, `${path}.gainDb`);
  }
}

/**
 * `EditTimelineValidator.ValidateMediaClipDepth`'s structural rules: non-negative integer fades that
 * fit inside the clip together, `reverse` never with `hold`, a reversed clip at most
 * `MAX_REVERSE_FRAMES` long, and no keyframed channel with a `null` static value.
 */
function assertMediaDepth(clip: MediaClip, path: string, op?: EditOp['type']): void {
  const duration = durationOf(clip);
  const fadeIn = clip.fadeInFrames;
  const fadeOut = clip.fadeOutFrames;
  if (fadeIn !== undefined && (!isInteger(fadeIn) || fadeIn < 0)) throw new EditEngineError(`${path}.fadeInFrames must be a non-negative integer`, op);
  if (fadeOut !== undefined && (!isInteger(fadeOut) || fadeOut < 0)) throw new EditEngineError(`${path}.fadeOutFrames must be a non-negative integer`, op);
  if ((fadeIn ?? 0) + (fadeOut ?? 0) > duration) {
    throw new EditEngineError(`${path}.fadeInFrames: the fades (${fadeIn ?? 0} + ${fadeOut ?? 0} frames) are longer than the clip (${duration} frames)`, op);
  }
  if (clip.reverse === true && clip.hold === true) throw new EditEngineError(`${path}.reverse: a clip cannot be both reversed and held`, op);
  if (clip.reverse === true && duration > MAX_REVERSE_FRAMES) {
    throw new EditEngineError(`${path}.reverse: a reversed clip may span at most ${MAX_REVERSE_FRAMES} frames, found ${duration}`, op);
  }
  assertChannelValue(clip.gainDb, `${path}.gainDb`, op);
  const transform: unknown = clip.transform;
  assertTransformChannels(transform, path, op);
}

/** A transform's channels: no `{value:null}`, and a vertical scale that is finite and not negative (zero reads as absent, as for `scale`). */
function assertTransformChannels(transform: unknown, path: string, op?: EditOp['type']): void {
  if (!isPlainObject(transform)) return;
  for (const param of TRANSFORM_PARAMS) assertChannelValue(transform[param], `${path}.transform.${param}`, op);
  // C# is the referee (ValidateKeyframedDouble, exclusiveMin): a static 0 reads as "no value"; otherwise a value and every key
  // must be finite, above 0 and at most 8. Same for `scale` and `scaleY`.
  for (const param of ['scale', 'scaleY'] as const) {
    const channel = transform[param];
    if (!isPlainObject(channel)) continue;
    const bad = (v: unknown): boolean => typeof v !== 'number' || !Number.isFinite(v) || v <= 0 || v > 8;
    if (channel.value !== undefined && channel.value !== null && channel.value !== 0 && bad(channel.value)) {
      throw new EditEngineError(`${path}.transform.${param}.value must be a finite number in (0, 8]`, op);
    }
    if (Array.isArray(channel.keys)) {
      channel.keys.forEach((key: unknown, i: number) => {
        if (isPlainObject(key) && bad(key.value)) throw new EditEngineError(`${path}.transform.${param}.keys[${i}].value must be a finite number in (0, 8]`, op);
      });
    }
  }
}

/**
 * A marker's own field invariants.
 *
 * `op` is the op running the check, so a refusal raised from inside an op still carries the op
 * that was being executed (`assertInvariants` is not an op and omits it).
 */
function assertMarkerShape(marker: Marker, path: string, op?: EditOp['type']): void {
  if (!isPlainObject(marker)) {
    throw new EditEngineError(`${path} must be an object`, op);
  }
  if (typeof marker.id !== 'string' || marker.id.length === 0) {
    throw new EditEngineError(`${path}.id must be a non-empty string`, op);
  }
  if (!isInteger(marker.frame) || marker.frame < 0) {
    throw new EditEngineError(`${path}.frame must be a non-negative integer`, op);
  }
  if (typeof marker.name !== 'string') {
    throw new EditEngineError(`${path}.name must be a string`, op);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Lookups
// ─────────────────────────────────────────────────────────────────────────────

type ItemLocation = { track: Track; index: number; item: Item };

function requireTrack(doc: EditTimeline, trackId: string, op: EditOp['type']): Track {
  const track = doc.tracks.find((candidate) => candidate.id === trackId);
  if (track === undefined) {
    throw new EditEngineError(`unknown track id '${trackId}'`, op);
  }
  if (track.locked) {
    throw new EditEngineError(`track '${trackId}' is locked`, op);
  }
  return track;
}

/** Finds an item anywhere in the document and refuses if its track is locked. */
function requireItem(doc: EditTimeline, itemId: string, op: EditOp['type']): ItemLocation {
  for (const track of doc.tracks) {
    const index = track.items.findIndex((candidate) => candidate.id === itemId);
    if (index >= 0) {
      if (track.locked) {
        throw new EditEngineError(`track '${track.id}' is locked`, op);
      }
      return { track, index, item: track.items[index] };
    }
  }
  throw new EditEngineError(`unknown item id '${itemId}'`, op);
}

/** Refuses a picture-only item (title, caption, generator, insert, adjustment) on an audio track. */
function assertItemFitsTrack(item: Item, track: Track, op?: EditOp['type'], path?: string): void {
  if (track.kind === 'audio' && PICTURE_ONLY_ITEM_TYPES.includes(item.type)) {
    throw new EditEngineError(`${path ?? `item '${item.id}'`}: a ${item.type} is picture only and cannot sit on an audio track`, op);
  }
  if (track.kind === 'audio' && item.type === 'mediaClip' && item.blendMode !== undefined && item.blendMode !== null && item.blendMode !== 'normal') {
    throw new EditEngineError(`${path ?? `item '${item.id}'`}.blendMode '${item.blendMode}' is only allowed on a clip on a video track`, op);
  }
}

/** Refuses an item id already used anywhere in the document. */
function requireFreshItemId(doc: EditTimeline, itemId: string, op: EditOp['type']): void {
  const taken =
    doc.tracks.some((track) => track.items.some((item) => item.id === itemId)) ||
    (doc.sequences ?? []).some((sequence) => sequence.tracks.some((track) => track.items.some((item) => item.id === itemId)));
  if (taken) {
    throw new EditEngineError(`item id '${itemId}' is already in use`, op);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// apply
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Applies one op to a document, returning the new document and the program that undoes it.
 *
 * The returned `inverse` is always non-empty except for `closeGaps` on a track that has no gaps,
 * which genuinely changed nothing and therefore has nothing to undo.
 */
export function apply(
  doc: EditTimeline,
  op: EditOp,
  /** True only when replaying an entry's INVERSE program (the engine's own undo). Never set by a
   *  wire op: `applySetColorGroup` uses it instead of a field on the op, so a caller cannot forge the
   *  restore exemption (Security finding on T-112). */
  options?: { inverse?: boolean },
): { doc: EditTimeline; inverse: EditOp[] } {
  let result = applyOne(doc, op, options?.inverse === true);
  const unseated = transitionsUnseatedBy(doc, result.doc, op);
  if (unseated.length > 0) {
    // eng D7: the validator requires every neighbour pair involving a transition to be flush. An
    // edit that would open a gap next to one (delete, move, nudge, a trim of a neighbour) takes the
    // transition with it: the transition is lifted FIRST and the edit re-run on that document, so
    // the inverse is the edit's own inverse followed by re-inserting the transition — every step a
    // valid document. An edit that leaves it flush (a ripple delete closing up) keeps it seated on
    // its new neighbour. Only the flush rule decides, never "has a neighbour on both sides": a
    // transition at a track edge is valid, and a rule the inverse could trip over (lifting a clip
    // appended after such a transition) would break exact undo.
    // Lifting one can leave another off its cut (back-to-back transitions): repeat until none is.
    let without = doc;
    const restore: EditOp[] = [];
    const lifted: string[] = [];
    let pending = unseated;
    while (pending.length > 0) {
      for (const id of pending) {
        const step = applyLift(clone(without), { type: 'lift', itemId: id });
        without = step.doc;
        restore.unshift(...step.inverse);
        lifted.push(id);
      }
      const rerun = applyOne(without, reindexed(doc, op, lifted), options?.inverse === true);
      // Back-to-back transitions cannot be re-inserted one at a time (the first would sit off its
      // cut until the second is back), so when several went the tracks they sat on are put back whole.
      const putBack = lifted.length === 1 ? restore : doc.tracks.filter((t) => t.items.some((item) => lifted.includes(item.id))).flatMap((t) => restoreTrack(doc, t, clone(t)));
      result = { doc: rerun.doc, inverse: [...rerun.inverse, ...putBack] };
      pending = transitionsUnseatedBy(doc, result.doc, op);
    }
    result = { doc: result.doc, inverse: exactInverse(doc, result.doc, result.inverse, op.type) };
  }
  assertTransitionsFlushAfter(doc, result.doc, op.type);
  assertStructureAfter(result.doc, op.type);
  return result;
}

/**
 * `op` re-addressed to a document without the `lifted` transitions: an `insert`'s index counts the
 * items before it on its track, so every lifted transition before that index is one fewer.
 */
function reindexed(doc: EditTimeline, op: EditOp, lifted: readonly string[]): EditOp {
  if (op.type !== 'insert') return op;
  const track = doc.tracks.find((candidate) => candidate.id === op.trackId);
  if (track === undefined) return op;
  const gone = new Set(lifted);
  const before = track.items.slice(0, op.index).filter((item) => gone.has(item.id)).length;
  return before === 0 ? op : { ...op, index: op.index - before };
}

/** Whether each root transition satisfies the validator's pair rule (flush with each neighbour it has). */
function transitionSeats(doc: EditTimeline): Map<string, { flush: boolean }> {
  const seats = new Map<string, { flush: boolean }>();
  for (const track of doc.tracks) {
    track.items.forEach((item, i) => {
      if (item.type !== 'transition') return;
      const before = track.items[i - 1];
      const after = track.items[i + 1];
      const flushBefore = before === undefined || before.recordOut === item.recordIn;
      const flushAfter = after === undefined || after.recordIn === item.recordOut;
      seats.set(item.id, { flush: flushBefore && flushAfter });
    });
  }
  return seats;
}

/**
 * The transitions an op unseated: present before and after, not the op's own subject, flush before
 * but breaking the validator's pair rule after.
 * `addTransition`/`removeTransition` manage their own transition and are exempt.
 */
function transitionsUnseatedBy(before: EditTimeline, after: EditTimeline, op: EditOp): string[] {
  if (op.type === 'addTransition' || op.type === 'removeTransition') return [];
  const subject = 'itemId' in op ? op.itemId : undefined;
  const was = transitionSeats(before);
  if (was.size === 0) return [];
  const now = transitionSeats(after);
  const unseated: string[] = [];
  for (const [id, seat] of was) {
    if (id === subject) continue;
    const next = now.get(id);
    if (next === undefined) continue;
    if (seat.flush && !next.flush) unseated.push(id);
  }
  return unseated;
}

/**
 * After the sweep, the op may not leave a transition off a cut that it put there itself (an insert
 * or move of a transition item, or moving the transition): refused. A pair that was already off
 * before the op is the document's own problem and is left for the save to report.
 */
function assertTransitionsFlushAfter(before: EditTimeline, after: EditTimeline, op: EditOp['type']): void {
  const now = transitionSeats(after);
  if (now.size === 0) return;
  assertSeatsFlush(transitionSeats(before), now, op);
}

function assertSeatsFlush(was: Map<string, { flush: boolean }>, now: Map<string, { flush: boolean }>, op: EditOp['type']): void {
  for (const [id, seat] of now) {
    if (seat.flush) continue;
    if (was.get(id)?.flush === false) continue;
    throw new EditEngineError(`transition '${id}' would no longer sit exactly at the boundary between its neighbours`, op);
  }
}

function applyOne(doc: EditTimeline, op: EditOp, inverse = false): { doc: EditTimeline; inverse: EditOp[] } {
  return dispatchOp(clone(doc), op, inverse);
}

/** Runs one op on `next`, which it WRITES TO (the caller owns the working copy). */
function dispatchOp(next: EditTimeline, op: EditOp, inverse: boolean): { doc: EditTimeline; inverse: EditOp[] } {
  switch (op.type) {
    case 'insert':
      return applyInsert(next, op);
    case 'overwrite':
      return applyOverwrite(next, op);
    case 'lift':
      return applyLift(next, op);
    case 'extract':
      return applyExtract(next, op);
    case 'trim':
      return applyTrim(next, op);
    case 'blade':
      return applyBlade(next, op);
    case 'deleteGap':
      return applyDeleteGap(next, op);
    case 'closeGaps':
      return applyCloseGaps(next, op);
    case 'move':
      return applyMove(next, op);
    case 'setProperty':
      return applySetProperty(next, op);
    case 'addMarker':
      return applyAddMarker(next, op);
    case 'setMarker':
      return applySetMarker(next, op);
    case 'removeMarker':
      return applyRemoveMarker(next, op);
    case 'append':
      return applyAppend(next, op);
    case 'replace':
      return applyReplace(next, op);
    case 'fitToFill':
      return applyFitToFill(next, op);
    case 'placeOnTop':
      return applyPlaceOnTop(next, op);
    case 'join':
      return applyJoin(next, op);
    case 'nudge':
      return applyNudge(next, op);
    case 'addTrack':
      return applyAddTrack(next, op);
    case 'removeTrack':
      return applyRemoveTrack(next, op);
    case 'moveTrack':
      return applyMoveTrack(next, op);
    case 'setTrackProperty':
      return applySetTrackProperty(next, op);
    case 'setTimelineSettings':
      return applySetTimelineSettings(next, op);
    case 'setInOut':
      return applySetInOut(next, op);
    case 'setKeyframe':
      return applySetKeyframe(next, op);
    case 'removeKeyframe':
      return applyRemoveKeyframe(next, op);
    case 'moveKeyframe':
      return applyMoveKeyframe(next, op);
    case 'retime':
      return applyRetime(next, op);
    case 'addTransition':
      return applyAddTransition(next, op);
    case 'removeTransition':
      return applyRemoveTransition(next, op);
    case 'setColorGroup':
      return applySetColorGroup(next, op, inverse);
    case 'setSequence':
      return applySetSequence(next, op);
    case 'setBus':
      return applySetBus(next, op);
    default: {
      const unreachable: never = op;
      throw new EditEngineError(
        `unsupported edit op ${JSON.stringify((unreachable as { type?: unknown }).type)}`,
      );
    }
  }
}

/** Whether the document has a root transition (the one case a batched op needs its pre-op document for). */
function hasRootTransition(doc: EditTimeline): boolean {
  return doc.tracks.some((track) => track.items.some((item) => item.type === 'transition'));
}

/**
 * A run of ops over ONE working copy of a document: the document is cloned once, not once per op.
 * Behaviour is the same as folding `apply`: each op returns the same inverse program, and an op that
 * throws throws the same error. The input document is never written to and the working copy never
 * shares an object with a returned inverse. A throw leaves the working copy unusable (the caller
 * drops the whole session: all-or-nothing).
 *
 * `doc` is the live working copy: read it between ops, but do not keep references into it across
 * an `apply` (the next op writes to it). While the document holds a transition an op may have to
 * lift and re-run around, each op takes the pure per-op path instead; the result is identical.
 */
export class ApplySession {
  private work: EditTimeline;
  private broken = false;

  constructor(doc: EditTimeline) {
    this.work = clone(doc);
  }

  /** The working copy after the ops so far. Read-only for callers. */
  get doc(): EditTimeline {
    return this.work;
  }

  /** Applies one op to the working copy and returns the program that undoes it. */
  apply(op: EditOp, options?: { inverse?: boolean }): EditOp[] {
    if (this.broken) throw new EditEngineError('the batch already failed and was discarded', op.type);
    try {
      if (hasRootTransition(this.work)) {
        const result = apply(this.work, op, options);
        this.work = result.doc;
        return clone(result.inverse);
      }
      const result = dispatchOp(this.work, op, options?.inverse === true);
      // No transition existed before the op, so none can have been unseated; one the op added must sit flush.
      const now = transitionSeats(result.doc);
      if (now.size > 0) assertSeatsFlush(new Map(), now, op.type);
      assertStructureAfter(result.doc, op.type);
      this.work = result.doc;
      return clone(result.inverse);
    } catch (error) {
      this.broken = true;
      throw error;
    }
  }
}

/**
 * Applies a program of ops left to right on one working copy, returning the final document and each
 * op's inverse program (same programs, same order as folding `apply`). All or nothing.
 */
export function applyBatch(
  doc: EditTimeline,
  ops: readonly EditOp[],
  options?: { inverse?: boolean },
): { doc: EditTimeline; inverses: EditOp[][] } {
  if (ops.length === 0) return { doc, inverses: [] };
  const session = new ApplySession(doc);
  const inverses = ops.map((op) => session.apply(op, options));
  return { doc: session.doc, inverses };
}

/** Folds a program of ops over a document, left to right (on one working copy; see {@link applyBatch}). */
export function applyAll(
  doc: EditTimeline,
  ops: readonly EditOp[],
  options?: { inverse?: boolean },
): EditTimeline {
  if (ops.length === 1) return apply(doc, ops[0], options).doc;
  return applyBatch(doc, ops, options).doc;
}

// ─────────────────────────────────────────────────────────────────────────────
// insert / overwrite / lift / extract
// ─────────────────────────────────────────────────────────────────────────────

function applyInsert(doc: EditTimeline, op: Insert): { doc: EditTimeline; inverse: EditOp[] } {
  const track = requireTrack(doc, op.trackId, 'insert');
  const item = clone(op.item);
  assertItemShape(item, 'insert.item');
  assertItemFitsTrack(item, track, 'insert', 'insert.item');
  requireFreshItemId(doc, item.id, 'insert');

  const items = track.items;
  const canonical = canonicalIndex(items, item.recordIn);
  if (op.index !== canonical) {
    throw new EditEngineError(
      `index ${op.index} is not canonical for recordIn ${item.recordIn} (expected ${canonical})`,
      'insert',
    );
  }

  const previous = canonical > 0 ? items[canonical - 1] : undefined;
  if (previous !== undefined && previous.recordOut > item.recordIn) {
    throw new EditEngineError(
      `the item would overlap the preceding item, which ends at ${previous.recordOut}`,
      'insert',
    );
  }

  const following = items[canonical];
  const duration = durationOf(item);
  const ripple = following === undefined ? 0 : Math.max(0, item.recordOut - following.recordIn);

  if (ripple !== 0 && ripple !== duration) {
    throw new EditEngineError(
      `the item partially overlaps the following item (it runs ${ripple} frame(s) past its start of ` +
        `${following?.recordIn}); insert it flush at that start or into a gap — a partial overlap ` +
        'destroys the information a length-1 inverse would need to undo it',
      'insert',
    );
  }

  items.splice(canonical, 0, item);
  if (ripple !== 0) {
    shiftRange(items, canonical + 1, ripple);
  }

  // A ripple of exactly the item's duration pushes the following items into the shape `extract`
  // closes up again; with no ripple, nothing moved and `lift` restores the gap exactly.
  const inverse: EditOp[] =
    ripple === 0 ? [{ type: 'lift', itemId: item.id }] : [{ type: 'extract', itemId: item.id }];

  return { doc, inverse };
}

function applyOverwrite(doc: EditTimeline, op: Overwrite): { doc: EditTimeline; inverse: EditOp[] } {
  const { track, index, item: original } = requireItem(doc, op.itemId, 'overwrite');
  const replacement = clone(op.item);
  assertItemShape(replacement, 'overwrite.item');
  assertItemFitsTrack(replacement, track, 'overwrite', 'overwrite.item');

  if (replacement.id !== op.itemId) {
    throw new EditEngineError(
      `overwrite must keep the item id: addressed '${op.itemId}', got '${replacement.id}'`,
      'overwrite',
    );
  }

  const previous = index > 0 ? track.items[index - 1] : undefined;
  const following = index + 1 < track.items.length ? track.items[index + 1] : undefined;
  if (previous !== undefined && previous.recordOut > replacement.recordIn) {
    throw new EditEngineError(
      `the new span starts at ${replacement.recordIn}, overlapping the previous item which ends at ${previous.recordOut}`,
      'overwrite',
    );
  }
  if (following !== undefined && replacement.recordOut > following.recordIn) {
    throw new EditEngineError(
      `the new span ends at ${replacement.recordOut}, overlapping the following item which starts at ${following.recordIn}`,
      'overwrite',
    );
  }

  track.items[index] = replacement;

  return { doc, inverse: [{ type: 'overwrite', itemId: op.itemId, item: original }] };
}

function applyLift(doc: EditTimeline, op: Lift): { doc: EditTimeline; inverse: EditOp[] } {
  const { track, index, item } = requireItem(doc, op.itemId, 'lift');
  track.items.splice(index, 1);

  // Nothing else moved, so the canonical position for the removed item's own recordIn is where
  // it came from, and re-inserting it there ripples nothing.
  const insertAt = canonicalIndex(track.items, item.recordIn);
  return {
    doc,
    inverse: [{ type: 'insert', trackId: track.id, index: insertAt, item }],
  };
}

function applyExtract(doc: EditTimeline, op: Extract): { doc: EditTimeline; inverse: EditOp[] } {
  const { track, index, item } = requireItem(doc, op.itemId, 'extract');
  const duration = durationOf(item);
  const following = index + 1 < track.items.length ? track.items[index + 1] : undefined;
  const adjacent = following !== undefined && following.recordIn === item.recordOut;

  track.items.splice(index, 1);
  if (adjacent) {
    shiftRange(track.items, index, -duration);
  }

  const insertAt = canonicalIndex(track.items, item.recordIn);
  return {
    doc,
    inverse: [{ type: 'insert', trackId: track.id, index: insertAt, item }],
  };
}

// ─────────────────────────────────────────────────────────────────────────────
// trim
// ─────────────────────────────────────────────────────────────────────────────

function applyTrim(doc: EditTimeline, op: Trim): { doc: EditTimeline; inverse: EditOp[] } {
  const { track, index, item } = requireItem(doc, op.itemId, 'trim');
  if (!isInteger(op.deltaFrames)) {
    throw new EditEngineError(`deltaFrames must be an integer, found ${String(op.deltaFrames)}`, 'trim');
  }

  const items = track.items;
  const previous = index > 0 ? items[index - 1] : undefined;
  const following = index + 1 < items.length ? items[index + 1] : undefined;
  const sourceIn = sourceOffset(item);

  // Every clamp below exists to keep an invariant true; the effective delta is what the inverse
  // carries, so a clamped trim still restores the document exactly. Durations are bounded by the
  // clip's fades (never shorter than both together) and by the reverse ceiling, and source offsets
  // by frame 0, all at the clip's own playback rate and direction (see `moveEdge`).
  let low = Number.NEGATIVE_INFINITY;
  let high = Number.POSITIVE_INFINITY;
  const atLeast = (value: number): void => {
    low = Math.max(low, value);
  };
  const atMost = (value: number): void => {
    high = Math.min(high, value);
  };
  /** `target`'s duration changes by `sign × delta`: keep it within [minDuration, maxDuration]. */
  const durationBounds = (target: Item, sign: 1 | -1): void => {
    const length = durationOf(target);
    const min = minDurationOf(target) - length;
    const max = maxDurationOf(target) - length;
    if (sign === 1) {
      atLeast(min);
      atMost(max);
    } else {
      atMost(-min);
      atLeast(-max);
    }
  };

  switch (op.mode) {
    case 'ripple': {
      if (op.edge === 'out') {
        durationBounds(item, 1);
        edgeSourceBound(item, 'out', atLeast, atMost);
      } else {
        // The head is trimmed in place: the clip keeps its start, so its duration changes by
        // -delta and everything after it closes up (or opens out) by the same amount.
        durationBounds(item, -1);
        edgeSourceBound(item, 'in', atLeast, atMost);
      }
      break;
    }
    case 'roll': {
      if (op.edge === 'out') {
        if (following === undefined) {
          throw new EditEngineError('roll on the out edge needs a following item', 'trim');
        }
        durationBounds(item, 1);
        edgeSourceBound(item, 'out', atLeast, atMost);
        durationBounds(following, -1);
        edgeSourceBound(following, 'in', atLeast, atMost);
      } else {
        if (previous === undefined) {
          throw new EditEngineError('roll on the in edge needs a preceding item', 'trim');
        }
        durationBounds(item, -1);
        edgeSourceBound(item, 'in', atLeast, atMost);
        durationBounds(previous, 1);
        edgeSourceBound(previous, 'out', atLeast, atMost);
      }
      break;
    }
    case 'slip': {
      if (sourceIn === undefined) {
        throw new EditEngineError(
          'slip changes only sourceIn, and this item carries none (only a mediaClip has a source offset)',
          'trim',
        );
      }
      // The source window moves by `delta` timeline frames' worth of source at the clip's rate.
      atLeast(-framesWithin(sourceIn, sourceRate(item)));
      break;
    }
    case 'slide': {
      if (op.edge === 'out') {
        if (following === undefined) {
          throw new EditEngineError('slide on the out edge needs a following item', 'trim');
        }
        atLeast(-item.recordIn);
        if (previous !== undefined) atLeast(previous.recordOut - item.recordIn);
        durationBounds(following, -1);
        edgeSourceBound(following, 'in', atLeast, atMost);
      } else {
        if (previous === undefined) {
          throw new EditEngineError('slide on the in edge needs a preceding item', 'trim');
        }
        // The neighbour's facing edge TRANSLATES by the same delta (it is never snapped onto the
        // item's edge): snapping would swallow a pre-existing gap, and the gap is not recoverable
        // from the post-op document, which would break the inverse.
        durationBounds(previous, 1);
        edgeSourceBound(previous, 'out', atLeast, atMost);
        if (following !== undefined) atMost(following.recordIn - item.recordOut);
      }
      // eng D7: a transition slid off the neighbour on its untouched side would sit off its cut, so
      // a transition flush there does not slide (the request clamps to a no-op).
      const untouched = op.edge === 'out' ? previous : following;
      const flushSide = op.edge === 'out' ? untouched?.recordOut === item.recordIn : untouched?.recordIn === item.recordOut;
      if (item.type === 'transition' && untouched !== undefined && flushSide) {
        atLeast(0);
        atMost(0);
      }
      break;
    }
    default: {
      const unreachable: never = op.mode;
      throw new EditEngineError(`unsupported trim mode ${String(unreachable)}`, 'trim');
    }
  }

  if (low > high) {
    throw new EditEngineError(
      `no delta satisfies this ${op.mode} trim on the ${op.edge} edge (allowed range is empty)`,
      'trim',
    );
  }
  // `-0` would survive into the inverse and compare unequal to `0`; normalise it away.
  const clamped = clamp(op.deltaFrames, low, high);
  const effective = Object.is(clamped, -0) ? 0 : clamped;

  switch (op.mode) {
    case 'ripple': {
      if (op.edge === 'out') {
        moveEdge(item, 'out', effective);
        shiftRange(items, index + 1, effective);
      } else {
        // Trim the head where it is, then slide the clip (and its keys) back to its own start.
        moveEdge(item, 'in', effective);
        shiftItem(item, -effective);
        shiftRange(items, index + 1, -effective);
      }
      break;
    }
    case 'roll': {
      if (op.edge === 'out') {
        moveEdge(item, 'out', effective);
        moveEdge(following as Item, 'in', effective);
      } else {
        moveEdge(item, 'in', effective);
        moveEdge(previous as Item, 'out', effective);
      }
      break;
    }
    case 'slip': {
      writeSourceOffset(item, (sourceIn as number) + sourceStep(effective, sourceRate(item)));
      break;
    }
    case 'slide': {
      shiftItem(item, effective);
      if (op.edge === 'out') {
        moveEdge(following as Item, 'in', effective);
      } else {
        moveEdge(previous as Item, 'out', effective);
      }
      break;
    }
    default:
      break;
  }

  return {
    doc,
    inverse: [
      {
        type: 'trim',
        itemId: op.itemId,
        edge: op.edge,
        mode: op.mode,
        deltaFrames: effective === 0 ? 0 : -effective,
      },
    ],
  };
}

// ─────────────────────────────────────────────────────────────────────────────
// blade
// ─────────────────────────────────────────────────────────────────────────────

/**
 * The two halves `blade` makes of `item` at `atFrame` (both fresh clones; `item` is untouched).
 *
 * Each half keeps showing exactly the source frames it showed before the cut, at the clip's own
 * rate and direction (see {@link sourceShiftForEdge}): a forward right half starts `head × rate`
 * further into the source, a reversed LEFT half does (its out edge, the lowest frame it touches,
 * moved), a held clip's halves both stay on the frozen frame. A fade belongs to the clip's outer
 * edge, so the cut itself stays hard (left fade-out and right fade-in become 0) and each kept fade
 * is clamped to its half. Absent fields stay absent. `join` reuses this to prove it is undoable.
 */
function bladeHalves(item: Item, atFrame: number, newItemId: string): { left: Item; right: Item } {
  const left = clone(item);
  const right = clone(item);
  const head = atFrame - item.recordIn;
  const tail = item.recordOut - atFrame;
  left.recordOut = atFrame;
  right.id = newItemId;
  right.recordIn = atFrame;

  const sourceIn = sourceOffset(item) ?? (SOURCE_OFFSET_ITEM_TYPES.includes(item.type) ? 0 : undefined);
  if (sourceIn !== undefined && !isHeld(item)) {
    const rate = sourceRate(item);
    if (isReversed(item)) {
      const step = sourceStep(tail, rate);
      if (step !== 0) writeSourceOffset(left, sourceIn + step);
    } else {
      const step = sourceStep(head, rate);
      if (step !== 0 || sourceOffset(item) !== undefined) writeSourceOffset(right, sourceIn + step);
    }
  }

  if (left.type === 'mediaClip' && right.type === 'mediaClip') {
    if (left.fadeOutFrames !== undefined) left.fadeOutFrames = 0;
    if (right.fadeInFrames !== undefined) right.fadeInFrames = 0;
    if (left.fadeInFrames !== undefined) left.fadeInFrames = Math.min(left.fadeInFrames, head);
    if (right.fadeOutFrames !== undefined) right.fadeOutFrames = Math.min(right.fadeOutFrames, tail);
  }

  if (item.type === 'multicamClip' && left.type === 'multicamClip' && right.type === 'multicamClip') {
    // P8: each half keeps the cuts strictly inside its own span; the right half starts on the angle
    // that was live at the blade frame (a cut exactly there becomes its active angle).
    const cuts = item.cuts ?? [];
    let live = item.activeAngleId;
    for (const cut of cuts) if (cut.atFrame <= atFrame) live = cut.angleId;
    if (item.cuts !== undefined && item.cuts !== null) {
      left.cuts = cuts.filter((cut) => cut.atFrame < atFrame).map((cut) => ({ ...cut }));
      right.cuts = cuts.filter((cut) => cut.atFrame > atFrame).map((cut) => ({ ...cut }));
    }
    right.activeAngleId = live;
  }
  return { left, right };
}

/** The colour group a clip belongs to, by index, or -1. */
function colorGroupIndexOf(doc: EditTimeline, clipId: string): number {
  return (doc.colorGroups ?? []).findIndex((group) => isPlainObject(group) && Array.isArray(group.clipIds) && group.clipIds.includes(clipId));
}

function applyBlade(doc: EditTimeline, op: Blade): { doc: EditTimeline; inverse: EditOp[] } {
  const { track, index, item } = requireItem(doc, op.itemId, 'blade');

  if (!isInteger(op.atFrame)) {
    throw new EditEngineError(`atFrame must be an integer, found ${String(op.atFrame)}`, 'blade');
  }
  if (op.atFrame <= item.recordIn || op.atFrame >= item.recordOut) {
    throw new EditEngineError(
      `atFrame ${op.atFrame} is not strictly inside the item's span [${item.recordIn}, ${item.recordOut})`,
      'blade',
    );
  }
  if (typeof op.newItemId !== 'string' || op.newItemId.length === 0) {
    throw new EditEngineError('newItemId must be a non-empty string', 'blade');
  }
  requireFreshItemId(doc, op.newItemId, 'blade');

  const originalLeft = clone(item);
  const followedByTransition = track.items[index + 1]?.type === 'transition';
  const originalTrack = followedByTransition ? clone(track) : undefined;
  const { left, right } = bladeHalves(item, op.atFrame, op.newItemId);
  track.items[index] = left;
  track.items.splice(index + 1, 0, right);

  // Drop the right half first (freeing its span), then widen the left half back to the span it
  // had — two ops, both from the named catalogue.
  let inverse: EditOp[] = [
    { type: 'lift', itemId: op.newItemId },
    { type: 'overwrite', itemId: item.id, item: originalLeft },
  ];
  if (item.type === 'transition') {
    // eng D7: lifting the right half would leave the left one off its cut for a step. A transition
    // carries nothing a cut loses, so the one `join` that merges the halves restores it exactly.
    inverse = [{ type: 'join', itemId: item.id }];
  } else {
    const after = track.items[index + 2];
    if (after !== undefined && after.type === 'transition' && after.recordIn === right.recordOut) {
      // eng D7: a transition flush after the right half would sit off its cut for a step once that
      // half is lifted, so the track is put back whole instead (see `restoreTrack`).
      inverse = restoreTrack(doc, track, originalTrack as Track);
    }
  }

  // P7: the right half is the same shot, so it stays in the left half's colour group (listed right
  // after it); the inverse puts the group back first.
  const groupIndex = item.type === 'mediaClip' ? colorGroupIndexOf(doc, item.id) : -1;
  if (groupIndex >= 0) {
    const group = (doc.colorGroups as ColorGroup[])[groupIndex];
    const previous = clone(group);
    group.clipIds.splice(group.clipIds.indexOf(item.id) + 1, 0, op.newItemId);
    inverse.unshift({ type: 'setColorGroup', groupId: group.id, group: previous });
  }

  return { doc, inverse };
}

// ─────────────────────────────────────────────────────────────────────────────
// deleteGap / closeGaps
// ─────────────────────────────────────────────────────────────────────────────

/** Frame positions of every interior/leading gap on a track, as `[start, end)` pairs. */
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

function applyDeleteGap(doc: EditTimeline, op: DeleteGap): { doc: EditTimeline; inverse: EditOp[] } {
  const track = requireTrack(doc, op.trackId, 'deleteGap');
  if (!isInteger(op.atFrame)) {
    throw new EditEngineError(`atFrame must be an integer, found ${String(op.atFrame)}`, 'deleteGap');
  }

  const items = track.items;
  const gap = gapsOf(items).find(([start, end]) => start <= op.atFrame && op.atFrame < end);
  if (gap === undefined) {
    throw new EditEngineError(`frame ${op.atFrame} is not inside a gap on track '${track.id}'`, 'deleteGap');
  }

  const [start, end] = gap;
  const length = end - start;
  const firstShifted = items.findIndex((item) => item.recordIn >= end);

  const originals: Array<{ itemId: string; toRecordIn: number }> = [];
  const originalTrack = clone(track);
  if (firstShifted >= 0) {
    for (let i = firstShifted; i < items.length; i++) {
      originals.push({ itemId: items[i].id, toRecordIn: items[i].recordIn });
    }
    shiftRange(items, firstShifted, -length);
  }

  return { doc, inverse: restoreShifted(doc, track, originalTrack, originals) };
}

/**
 * The inverse of a gap op: every shifted item back to where it was, one `move` each, rightmost
 * first so each destination is the item's own former slot, already free once the items to its
 * right are back. A transition cannot take part (eng D7): moving it, or a neighbour, on its own
 * leaves it off its cut for a step, so when one shifted the track is put back whole instead.
 */
function restoreShifted(
  doc: EditTimeline,
  track: Track,
  originalTrack: Track,
  originals: ReadonlyArray<{ itemId: string; toRecordIn: number }>,
): EditOp[] {
  const shifted = new Set(originals.map(({ itemId }) => itemId));
  if (track.items.some((item) => item.type === 'transition' && shifted.has(item.id))) {
    return restoreTrack(doc, track, originalTrack);
  }
  return originals
    .slice()
    .reverse()
    .map(({ itemId, toRecordIn }) => ({ type: 'move' as const, itemId, toTrackId: track.id, toRecordIn }));
}

/**
 * Puts a whole track back as it was: `removeTrack` then `addTrack` at the same index. Used where a
 * per-item restore would pass through a document with a transition off its cut (eng D7), which
 * the validator — and so `assertInvariants` after every inverse step — refuses.
 */
function restoreTrack(doc: EditTimeline, track: Track, originalTrack: Track): EditOp[] {
  return [
    { type: 'removeTrack', trackId: track.id },
    { type: 'addTrack', index: doc.tracks.indexOf(track), track: originalTrack },
  ];
}

/** Whether `inverse`, applied step by step to `after`, gives back exactly `before`. */
function undoesExactly(before: EditTimeline, after: EditTimeline, inverse: readonly EditOp[]): boolean {
  try {
    let current = after;
    for (const step of inverse) current = apply(current, step).doc;
    return deepEqual(current, before);
  } catch (error) {
    if (error instanceof EditEngineError) return false;
    throw error;
  }
}

/**
 * The inverse of an edit that also lifted transitions (eng D7), proven before it is returned. The
 * per-item program is tried first; when back-to-back transitions make one of its steps sit off a
 * cut (re-inserting the first before its neighbour is back), every track the edit changed is put
 * back whole instead: all of them removed, then each original re-added at its index (removing
 * first keeps item ids unique when an item moved between two of them).
 */
function exactInverse(before: EditTimeline, after: EditTimeline, inverse: EditOp[], op: EditOp['type']): EditOp[] {
  if (undoesExactly(before, after, inverse)) return inverse;
  const sameTracks = before.tracks.length === after.tracks.length && before.tracks.every((track, i) => after.tracks[i].id === track.id);
  if (sameTracks) {
    const changed = before.tracks.filter((track, i) => !deepEqual(track, after.tracks[i]));
    const wholeTracks: EditOp[] = [
      ...changed.map((track): EditOp => ({ type: 'removeTrack', trackId: track.id })),
      ...changed.map((track): EditOp => ({ type: 'addTrack', index: before.tracks.indexOf(track), track: clone(track) })),
    ];
    if (undoesExactly(before, after, wholeTracks)) return wholeTracks;
  }
  throw new EditEngineError('this edit next to a transition could not be undone exactly, so it was not made', op);
}

function applyCloseGaps(doc: EditTimeline, op: CloseGaps): { doc: EditTimeline; inverse: EditOp[] } {
  const track = requireTrack(doc, op.trackId, 'closeGaps');
  const items = track.items;
  if (items.length <= 1) {
    return { doc, inverse: [] };
  }

  const originals: Array<{ itemId: string; toRecordIn: number }> = [];
  const originalTrack = clone(track);
  let cursor = items[0].recordOut;

  for (let i = 1; i < items.length; i++) {
    const item = items[i];
    if (item.recordIn !== cursor) {
      originals.push({ itemId: item.id, toRecordIn: item.recordIn });
      shiftItem(item, cursor - item.recordIn);
    }
    cursor = item.recordOut;
  }

  return { doc, inverse: restoreShifted(doc, track, originalTrack, originals) };
}

// ─────────────────────────────────────────────────────────────────────────────
// move
// ─────────────────────────────────────────────────────────────────────────────

function applyMove(doc: EditTimeline, op: Move): { doc: EditTimeline; inverse: EditOp[] } {
  const { track: fromTrack, index, item } = requireItem(doc, op.itemId, 'move');
  const toTrack = requireTrack(doc, op.toTrackId, 'move');

  if (!isInteger(op.toRecordIn) || op.toRecordIn < 0) {
    throw new EditEngineError(
      `toRecordIn must be a non-negative integer, found ${String(op.toRecordIn)}`,
      'move',
    );
  }

  const duration = durationOf(item);
  const originalIn = item.recordIn;
  const originalTrackId = fromTrack.id;

  fromTrack.items.splice(index, 1);

  const target = toTrack.items;
  const destination = canonicalIndex(target, op.toRecordIn);
  const previous = destination > 0 ? target[destination - 1] : undefined;
  const following = destination < target.length ? target[destination] : undefined;

  if (previous !== undefined && previous.recordOut > op.toRecordIn) {
    throw new EditEngineError(
      `the destination at ${op.toRecordIn} overlaps the item ending at ${previous.recordOut}`,
      'move',
    );
  }
  if (following !== undefined && op.toRecordIn + duration > following.recordIn) {
    throw new EditEngineError(
      `the destination span [${op.toRecordIn}, ${op.toRecordIn + duration}) overlaps the item starting at ${following.recordIn}`,
      'move',
    );
  }

  assertItemFitsTrack(item, toTrack, 'move');
  shiftItem(item, op.toRecordIn - originalIn);
  target.splice(destination, 0, item);

  return {
    doc,
    inverse: [
      { type: 'move', itemId: op.itemId, toTrackId: originalTrackId, toRecordIn: originalIn },
    ],
  };
}

// ─────────────────────────────────────────────────────────────────────────────
// setProperty
// ─────────────────────────────────────────────────────────────────────────────

/** Throws unless `value` is legal for `key` (`undefined` always means "clear the key"). */
function assertPropertyValue(key: PropertyKey, value: PropertyValue): void {
  if (value === undefined) return;

  const fail = (expected: string): never => {
    throw new EditEngineError(
      `setProperty value for '${key}' must be ${expected}, found ${JSON.stringify(value) ?? String(value)}`,
      'setProperty',
    );
  };

  switch (key) {
    case 'name':
    case 'text':
    case 'fontFamily':
    case 'color':
    case 'language':
      if (typeof value !== 'string') fail('a string');
      return;
    case 'enabled':
      if (typeof value !== 'boolean') fail('a boolean');
      return;
    case 'playbackRate':
      if (typeof value !== 'number' || !Number.isFinite(value) || value <= 0) {
        fail('a number greater than zero');
      }
      return;
    case 'fontSize':
      if (typeof value !== 'number' || !Number.isFinite(value)) fail('a finite number');
      return;
    case 'gainDb': {
      if (value === null) return;
      if (!isKeyframedNumber(value, 'gainDb')) fail('null or a keyframed number within [-60, 20] dB with strictly increasing integer frames');
      return;
    }
    case 'transform': {
      if (value === null) return;
      if (!isPlainObject(value)) fail('null or a transform object');
      const transform = value as Record<string, unknown>;
      for (const key of Object.keys(transform)) {
        if (!TRANSFORM_PARAMS.includes(key as KeyframeParam)) fail(`a transform with only the channels ${TRANSFORM_PARAMS.join(', ')}`);
        const channel = transform[key];
        if (channel === null || channel === undefined) continue;
        if (!isKeyframedNumber(channel, key as KeyframeParam)) fail(`a transform whose '${key}' is a keyframed number inside its range`);
      }
      return;
    }
    case 'crop': {
      if (value === null) return;
      if (!isPlainObject(value)) fail('null or a crop object');
      const crop = value as Record<string, unknown>;
      const edge = (name: string): number => {
        const v = crop[name];
        if (v === undefined) return 0;
        if (typeof v !== 'number' || !Number.isFinite(v) || v < 0 || v >= 1) fail(`a crop whose '${name}' is a fraction in [0, 1)`);
        return v as number;
      };
      for (const name of Object.keys(crop)) {
        if (!['left', 'top', 'right', 'bottom'].includes(name)) fail('a crop with only left/top/right/bottom');
      }
      if (edge('left') + edge('right') >= 1 || edge('top') + edge('bottom') >= 1) fail('a crop that leaves some picture');
      return;
    }
    case 'reverse':
    case 'hold':
      if (typeof value !== 'boolean') fail('a boolean');
      return;
    case 'fadeInFrames':
    case 'fadeOutFrames':
      if (!isInteger(value) || (value as number) < 0) fail('a non-negative integer frame count');
      return;
    case 'sourceIn':
      if (!isInteger(value) || (value as number) < 0) fail('a non-negative integer source frame');
      return;
    case 'transitionType':
      if (typeof value !== 'string' || !TRANSITION_TYPES.includes(value)) fail('a transition type from the catalogue');
      return;
    case 'easing':
      if (typeof value !== 'string' || !(INTERP_KINDS as readonly string[]).includes(value)) fail('an interpolation kind');
      return;
    case 'generatorType':
      if (typeof value !== 'string' || !GENERATOR_TYPES.includes(value)) fail('a generator type from the catalogue');
      return;
    case 'generatorConfig': {
      if (value === null) return;
      if (!isPlainObject(value)) fail('null or a config object');
      const colour = (value as Record<string, unknown>).color;
      if (colour !== undefined && (typeof colour !== 'string' || !/^#[0-9a-fA-F]{6}$/.test(colour))) fail('a config whose color is #RRGGBB');
      return;
    }
    case 'assetRef':
      // P6 relink: the clip's source changes, its span and offsets stay. The shape is checked here;
      // whether the new asset is in the project is the server validator's call (the trust boundary).
      if (!isAssetRef(value)) fail("an asset reference ({ kind: 'projectFile', projectFileId } or { kind: 'stepResult', stepResultId })");
      return;
    // ── P7 ─────────────────────────────────────────────────────────────────
    case 'grade':
      if (value === null) return;
      if (!isGradeStack(value)) fail(`null or a grade stack (≤ ${MAX_GRADE_NODES} nodes, primaries inside their ranges, curves ≤ ${MAX_CURVE_POINTS} increasing points, a LUT asset with intensity in [0, 1])`);
      return;
    case 'mask':
      if (value === null) return;
      if (!isClipMask(value)) fail('null or a mask ({ kind: rect|ellipse, x/y/w/h in [0, 1], feather in [0, 0.5], invert } or { kind: segmentation, analysisStepResultId, trackIds: 1-8 g{n}, invert, featherPx in [0, 64] })');
      return;
    case 'keyer':
    case 'matte':
      if (value === null) return;
      if (!isChromaKeyer(value)) fail('null or a keyer ({ color: #RRGGBB, similarity in (0, 1], blend and spill in [0, 1] })');
      return;
    case 'effects':
      if (value === null) return;
      if (!isEffects(value)) fail(`null or a list of at most ${MAX_EFFECTS} effects from the catalogue with amounts in [0, 1] ([0, ${MAX_BLUR_AMOUNT}] for blur)`);
      return;
    case 'regionEffects':
      if (value === null) return;
      if (!isRegionEffects(value)) fail(`null or a list of at most ${MAX_REGION_EFFECTS} region effects ({ effect: blur|pixelate|highlight|spotlight|colorpop|outline, strength in [0, 1], mask: a segmentation mask, enabled })`);
      return;
    case 'stabilize':
      if (typeof value !== 'boolean') fail('a boolean');
      return;
    case 'reframe':
      if (value === null) return;
      if (!isReframe(value)) fail('null or a subject point ({ x, y } in [0, 1])');
      return;
    case 'sourceRotation':
      if (value === null) return;
      if (typeof value !== 'number' || ![0, 90, 180, 270].includes(value)) fail('null, 0, 90, 180 or 270');
      return;
    case 'retimeQuality':
      if (typeof value !== 'string' || !RETIME_QUALITIES.includes(value)) fail('frame or opticalFlow');
      return;
    case 'cornerPin':
      if (value === null) return;
      if (!isQuadCorners(value)) fail('null or four corners ({ topLeft, topRight, bottomRight, bottomLeft }, each { x, y } in [-1, 2])');
      return;
    case 'quad':
      if (value === null) return;
      if (!isKeyframedQuad(value)) fail(`null or a keyframed quad (≤ ${MAX_KEYFRAMES} keys with strictly increasing integer frames)`);
      return;
    case 'template':
      if (value === null) return;
      if (typeof value !== 'string' || !TITLE_TEMPLATES.includes(value)) fail('null or a title template from the catalogue');
      return;
    case 'look':
      if (value === null) return;
      if (typeof value !== 'string' || !CAPTION_LOOKS.includes(value)) fail('null or a caption look from the catalogue');
      return;
    case 'position':
      if (value === null) return;
      if (typeof value !== 'string' || !TEXT_POSITIONS.includes(value)) fail('null or a spot on the 3x3 text pad');
      return;
    case 'channelMap':
      if (typeof value !== 'string' || !CHANNEL_MAPS.includes(value)) fail('a channel map from the catalogue');
      return;
    case 'blendMode':
      if (typeof value !== 'string' || !BLEND_MODES.includes(value)) fail('a blend mode from the catalogue');
      return;
    case 'linkId':
      if (value === null) return;
      if (!isLinkId(value)) fail("null or 1-64 letters, digits, '-' or '_'");
      return;
    case 'inputProfile':
      if (value === null) return;
      if (!isCameraProfileId(value)) fail('null or a camera profile from the catalogue');
      return;
    case 'audioSource':
      if (value === null) return;
      if (typeof value !== 'string' || value.length === 0) fail("null, 'program', 'none' or an angle id");
      return;
    case 'sequenceId':
    case 'activeAngleId':
      if (typeof value !== 'string' || value.length === 0) fail('a non-empty id');
      return;
    case 'cuts':
      if (value !== null && !Array.isArray(value)) fail('an array of angle cuts or null');
      return;
    case 'angles':
      if (!Array.isArray(value) || value.length === 0) fail('a non-empty array of angles');
      return;
    default:
      return;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// P7 value shapes — the same rules `EditTimelineValidator` applies, so a hostile value is refused
// here before it is sent and again on the server if it arrives anyway.
// ─────────────────────────────────────────────────────────────────────────────

const inRange = (v: unknown, min: number, max: number): v is number => typeof v === 'number' && Number.isFinite(v) && v >= min && v <= max;

function isWheel(value: unknown): value is GradeWheel {
  if (value === null || value === undefined) return true;
  if (!isPlainObject(value)) return false;
  for (const key of Object.keys(value)) {
    if (!['r', 'g', 'b', 'master'].includes(key)) return false;
    const v = (value as Record<string, unknown>)[key];
    if (v !== undefined && !inRange(v, -1, 1)) return false;
  }
  return true;
}

function isCurve(value: unknown): value is CurvePoint[] {
  if (value === null || value === undefined) return true;
  if (!Array.isArray(value) || value.length > MAX_CURVE_POINTS) return false;
  let previous = Number.NEGATIVE_INFINITY;
  for (const point of value) {
    if (!isPlainObject(point) || !inRange(point.x, 0, 1) || !inRange(point.y, 0, 1)) return false;
    if ((point.x as number) <= previous) return false;
    previous = point.x as number;
  }
  return true;
}

/** A well-formed `GradeStack`. */
export function isGradeStack(value: unknown): value is GradeStack {
  if (!isPlainObject(value)) return false;
  const stack = value as Record<string, unknown>;
  if (stack.enabled !== undefined && typeof stack.enabled !== 'boolean') return false;
  if (stack.nodes === undefined || stack.nodes === null) return true;
  if (!Array.isArray(stack.nodes) || stack.nodes.length > MAX_GRADE_NODES) return false;
  const ids = new Set<string>();
  for (const node of stack.nodes) {
    if (!isPlainObject(node) || typeof node.id !== 'string' || node.id.length === 0 || ids.has(node.id)) return false;
    ids.add(node.id);
    if (node.name !== undefined && typeof node.name !== 'string') return false;
    if (node.enabled !== undefined && typeof node.enabled !== 'boolean') return false;
    const p = node.primaries;
    if (p !== undefined && p !== null) {
      if (!isPlainObject(p)) return false;
      const primaries = p as Record<string, unknown>;
      if (!isWheel(primaries.lift) || !isWheel(primaries.gamma) || !isWheel(primaries.gain) || !isWheel(primaries.offset)) return false;
      for (const key of ['contrast', 'pivot', 'saturation', 'hue', 'temperature', 'tint', 'exposure'] as const) {
        const v = primaries[key];
        if (v !== undefined && !inRange(v, GRADE_RANGES[key].min, GRADE_RANGES[key].max)) return false;
      }
    }
    const c = node.curves;
    if (c !== undefined && c !== null) {
      if (!isPlainObject(c)) return false;
      const curves = c as Record<string, unknown>;
      if (!isCurve(curves.y) || !isCurve(curves.r) || !isCurve(curves.g) || !isCurve(curves.b)) return false;
    }
    const l = node.lut;
    if (l !== undefined && l !== null) {
      if (!isPlainObject(l) || !isAssetRef((l as Record<string, unknown>).file)) return false;
      const intensity = (l as Record<string, unknown>).intensity;
      if (intensity !== undefined && !inRange(intensity, 0, 1)) return false;
    }
    if (node.qualifier !== undefined && node.qualifier !== null && !isGradeQualifier(node.qualifier)) return false;
    if (node.window !== undefined && node.window !== null && !isGradeWindow(node.window)) return false;
  }
  return true;
}

/** P9: an HSL qualifier inside the validator's ranges (hue centre [0, 360), width (0, 180], bands ordered). */
export function isGradeQualifier(value: unknown): boolean {
  if (!isPlainObject(value)) return false;
  const q = value as Record<string, unknown>;
  for (const key of Object.keys(q)) if (!['hueCenter', 'hueWidth', 'satMin', 'satMax', 'lumMin', 'lumMax', 'softness', 'invert'].includes(key)) return false;
  if (q.hueCenter !== undefined && !(inRange(q.hueCenter, 0, 360) && (q.hueCenter as number) < 360)) return false;
  if (q.hueWidth !== undefined && !(inRange(q.hueWidth, 0, 180) && (q.hueWidth as number) > 0)) return false;
  for (const key of ['satMin', 'satMax', 'lumMin', 'lumMax']) if (q[key] !== undefined && !inRange(q[key], 0, 1)) return false;
  if (q.softness !== undefined && !inRange(q.softness, 0, 0.5)) return false;
  if (q.invert !== undefined && typeof q.invert !== 'boolean') return false;
  if ((q.satMin as number | undefined ?? 0) > (q.satMax as number | undefined ?? 1)) return false;
  if ((q.lumMin as number | undefined ?? 0) > (q.lumMax as number | undefined ?? 1)) return false;
  return true;
}

/** P9: a power window — a valid shape plus offsets keyframed in [-1, 1]. */
export function isGradeWindow(value: unknown): boolean {
  if (!isPlainObject(value)) return false;
  const w = value as Record<string, unknown>;
  if (!isShapeMask(w.shape)) return false;
  for (const key of ['offsetX', 'offsetY']) {
    const channel = w[key];
    if (channel === undefined || channel === null) continue;
    if (!isPlainObject(channel)) return false;
    const c = channel as Record<string, unknown>;
    if (c.value !== undefined && c.value !== null && !inRange(c.value, -1, 1)) return false;
    if (c.keys !== undefined && c.keys !== null) {
      if (!Array.isArray(c.keys) || c.keys.length > MAX_KEYFRAMES) return false;
      let previous = Number.NEGATIVE_INFINITY;
      for (const k of c.keys) {
        if (!isPlainObject(k) || !isInteger(k.frame) || !inRange(k.value, -1, 1) || (k.frame as number) <= previous) return false;
        if (!(INTERP_KINDS as readonly string[]).includes(String(k.interp))) return false;
        previous = k.frame as number;
      }
    }
  }
  return true;
}

export function isShapeMask(value: unknown): value is ShapeMask {
  if (!isPlainObject(value)) return false;
  const m = value as Record<string, unknown>;
  if (m.kind !== undefined && (typeof m.kind !== 'string' || !MASK_KINDS.includes(m.kind))) return false;
  for (const key of ['x', 'y', 'w', 'h']) if (m[key] !== undefined && !inRange(m[key], 0, 1)) return false;
  if (m.feather !== undefined && !inRange(m.feather, 0, 0.5)) return false;
  if (m.invert !== undefined && typeof m.invert !== 'boolean') return false;
  return true;
}

const SEGMENTATION_TRACK_ID = /^g\d{1,6}$/;

/** The first field of a `kind: 'segmentation'` mask that is not valid, or undefined when it is well formed. */
export function segmentationMaskProblem(value: unknown): string | undefined {
  if (!isPlainObject(value)) return '';
  const m = value as Record<string, unknown>;
  if (m.kind !== 'segmentation') return 'kind';
  if (typeof m.analysisStepResultId !== 'string' || !isGuid(m.analysisStepResultId)) return 'analysisStepResultId';
  if (!Array.isArray(m.trackIds) || m.trackIds.length < 1 || m.trackIds.length > SEGMENTATION_MASK_MAX_TRACKS) return 'trackIds';
  if (!m.trackIds.every((id) => typeof id === 'string' && SEGMENTATION_TRACK_ID.test(id))) return 'trackIds';
  if (new Set(m.trackIds as string[]).size !== m.trackIds.length) return 'trackIds';
  if (typeof m.invert !== 'boolean') return 'invert';
  if (!inRange(m.featherPx, 0, SEGMENTATION_MASK_MAX_FEATHER_PX)) return 'featherPx';
  return undefined;
}

/** A segmentation mask: shape only (whether the analysis and tracks exist is the server's call). */
export function isSegmentationMask(value: unknown): value is SegmentationMask {
  return segmentationMaskProblem(value) === undefined;
}

/** A clip's mask: a shape mask or a segmentation mask. Grade windows stay shape-only (isShapeMask). */
export function isClipMask(value: unknown): value is ClipMask {
  return isSegmentationMask(value) || (isPlainObject(value) && (value as { kind?: unknown }).kind !== 'segmentation' && isShapeMask(value));
}

export function isChromaKeyer(value: unknown): value is ChromaKeyer {
  if (!isPlainObject(value)) return false;
  const k = value as Record<string, unknown>;
  if (k.color !== undefined && (typeof k.color !== 'string' || !/^#[0-9a-fA-F]{6}$/.test(k.color))) return false;
  if (k.similarity !== undefined && !(typeof k.similarity === 'number' && Number.isFinite(k.similarity) && k.similarity > 0 && k.similarity <= 1)) return false;
  if (k.blend !== undefined && !inRange(k.blend, 0, 1)) return false;
  if (k.spill !== undefined && !inRange(k.spill, 0, 1)) return false;
  return true;
}

export function isEffects(value: unknown): value is ClipEffect[] {
  if (!Array.isArray(value) || value.length > MAX_EFFECTS) return false;
  return value.every((e) => isPlainObject(e) && typeof e.type === 'string' && EFFECT_TYPES.includes(e.type) && (e.amount === undefined || inRange(e.amount, 0, e.type === 'blur' ? MAX_BLUR_AMOUNT : 1)) && (e.enabled === undefined || typeof e.enabled === 'boolean'));
}

/** The first problem with a region-effects list as a path suffix (e.g. `[1].strength`), or undefined when well formed. */
export function regionEffectsProblem(value: unknown): string | undefined {
  if (!Array.isArray(value)) return '';
  if (value.length > MAX_REGION_EFFECTS) return ` allows at most ${MAX_REGION_EFFECTS} region effects`;
  for (let i = 0; i < value.length; i++) {
    const e: unknown = value[i];
    if (!isPlainObject(e)) return `[${i}]`;
    const r = e as Record<string, unknown>;
    if (typeof r.effect !== 'string' || !REGION_EFFECT_TYPES.includes(r.effect)) return `[${i}].effect`;
    if (!inRange(r.strength, 0, 1)) return `[${i}].strength`;
    if (typeof r.enabled !== 'boolean') return `[${i}].enabled`;
    if (!isSegmentationMask(r.mask)) return `[${i}].mask`;
  }
  return undefined;
}

export function isRegionEffects(value: unknown): value is RegionClipEffect[] {
  return regionEffectsProblem(value) === undefined;
}

export function isReframe(value: unknown): value is ReframeTarget {
  if (!isPlainObject(value)) return false;
  const r = value as Record<string, unknown>;
  return (r.x === undefined || inRange(r.x, 0, 1)) && (r.y === undefined || inRange(r.y, 0, 1));
}

export function isQuadCorners(value: unknown): value is QuadCorners {
  if (!isPlainObject(value)) return false;
  const q = value as Record<string, unknown>;
  return ['topLeft', 'topRight', 'bottomRight', 'bottomLeft'].every((corner) => {
    const point = q[corner];
    return isPlainObject(point) && inRange(point.x, -1, 2) && inRange(point.y, -1, 2);
  });
}

export function isKeyframedQuad(value: unknown): value is Keyframed<QuadCorners> {
  if (!isPlainObject(value)) return false;
  const k = value as Record<string, unknown>;
  if (k.value !== undefined && k.value !== null && !isQuadCorners(k.value)) return false;
  if (k.keys === undefined || k.keys === null) return true;
  if (!Array.isArray(k.keys) || k.keys.length > MAX_KEYFRAMES) return false;
  let previous = Number.NEGATIVE_INFINITY;
  for (const key of k.keys) {
    if (!isPlainObject(key) || !isInteger(key.frame) || !isQuadCorners(key.value) || !(INTERP_KINDS as readonly string[]).includes(String(key.interp))) return false;
    if ((key.frame as number) <= previous) return false;
    previous = key.frame as number;
  }
  return true;
}

/** A well-formed `AssetRef`: one of the two kinds, carrying exactly the id that kind names. */
export function isAssetRef(value: unknown): value is AssetRef {
  if (!isPlainObject(value)) return false;
  const ref = value as Record<string, unknown>;
  const id = (name: string): boolean => typeof ref[name] === 'string' && (ref[name] as string).trim().length > 0;
  if (ref.kind === 'projectFile') return id('projectFileId') && (ref.stepResultId === undefined || ref.stepResultId === null);
  if (ref.kind === 'stepResult') return id('stepResultId') && (ref.projectFileId === undefined || ref.projectFileId === null);
  return false;
}

/**
 * A `Keyframed<number>` whose static value and every key sit inside the channel's range, with at
 * most {@link MAX_KEYFRAMES} keys in strictly increasing integer frame order and a known
 * interpolation kind — the same rule `EditTimelineValidator.ValidateKeyframedDouble` applies.
 */
function isKeyframedNumber(value: unknown, param: KeyframeParam): value is Keyframed<number> {
  if (!isPlainObject(value)) return false;
  const range = KEYFRAME_PARAM_RANGE[param];
  const inRange = (v: number): boolean => Number.isFinite(v) && (range.exclusiveMin ? v > range.min : v >= range.min) && v <= range.max;
  const keyframed = value as Keyframed<number>;
  // Absent is fine (the identity); an explicit null fails the server's `double` binding.
  if (keyframed.value === null) return false;
  if (keyframed.value !== undefined) {
    if (typeof keyframed.value !== 'number') return false;
    // 0 on a range that excludes it reads as "no static value" (the identity), as on the server.
    if (!(range.exclusiveMin && keyframed.value === 0) && !inRange(keyframed.value)) return false;
  }
  if (keyframed.keys !== undefined && keyframed.keys !== null) {
    if (!Array.isArray(keyframed.keys) || keyframed.keys.length > MAX_KEYFRAMES) return false;
    let previous = Number.NEGATIVE_INFINITY;
    for (const keyframe of keyframed.keys) {
      if (!isPlainObject(keyframe) || !isInteger(keyframe.frame) || typeof keyframe.value !== 'number' || !inRange(keyframe.value)) return false;
      if (!(INTERP_KINDS as readonly string[]).includes(String(keyframe.interp))) return false;
      if ((keyframe.frame as number) <= previous) return false;
      previous = keyframe.frame as number;
    }
  }
  return true;
}

/** The `setProperty` keys {@link assertMediaDepth} re-checks after the write. */
const MEDIA_DEPTH_KEYS: ReadonlySet<string> = new Set(['fadeInFrames', 'fadeOutFrames', 'reverse', 'hold', 'gainDb', 'transform']);

function applySetProperty(doc: EditTimeline, op: SetProperty): { doc: EditTimeline; inverse: EditOp[] } {
  const { item, track: host } = requireItem(doc, op.itemId, 'setProperty');

  const allowed = SET_PROPERTY_ALLOWLIST[item.type] ?? [];
  if (!allowed.includes(op.key)) {
    throw new EditEngineError(
      `key '${op.key}' is not settable on a ${item.type} (allowed: ${allowed.join(', ')})`,
      'setProperty',
    );
  }
  assertPropertyValue(op.key, op.value);

  // `undefined` means "clear the key", which is load-bearing for the inverse — but a key the
  // document invariants require may not be cleared, or the op would hand back a document the
  // engine may no longer edit. Refused BEFORE the write, so nothing is half-applied. The rule and
  // the set that drives it live in `./ops` beside `SET_PROPERTY_ALLOWLIST`.
  if (op.value === undefined && REQUIRED_PROPERTY_KEYS.has(op.key)) {
    throw new EditEngineError(
      `key '${op.key}' is required by the document invariants and cannot be cleared; ` +
        'set a value instead of undefined',
      'setProperty',
    );
  }

  // A blend mode other than normal needs a picture to blend: video tracks only (the server validator says the same).
  if (op.key === 'blendMode' && host.kind === 'audio' && op.value !== undefined && op.value !== 'normal') {
    throw new EditEngineError(`item '${op.itemId}'.blendMode '${String(op.value)}' is only allowed on a clip on a video track`, 'setProperty');
  }

  // A clip cannot be stabilised and carry region effects (the server validator refuses the pair): checked here, before the write.
  if (item.type === 'mediaClip' && ((op.key === 'stabilize' && op.value === true && (item.regionEffects?.length ?? 0) > 0) || (op.key === 'regionEffects' && Array.isArray(op.value) && op.value.length > 0 && item.stabilize === true))) {
    throw new EditEngineError(`item '${op.itemId}' cannot be stabilised and carry region effects`, 'setProperty');
  }

  const fields = asFields(item);
  const hadKey = hasOwn(fields, op.key) && fields[op.key] !== undefined;
  const previous = hadKey ? (fields[op.key] as PropertyValue) : undefined;

  writeField(fields, op.key, op.value);
  // The clip-depth rules the server checks across fields (fades fit, reverse ≤ 1800 f, not both
  // reversed and held, no null static value): refused here, on the cloned document, not at save.
  if (item.type === 'mediaClip' && MEDIA_DEPTH_KEYS.has(op.key)) assertMediaDepth(item, `item '${op.itemId}'`, 'setProperty');

  return {
    doc,
    inverse: [{ type: 'setProperty', itemId: op.itemId, key: op.key, value: previous }],
  };
}

// ─────────────────────────────────────────────────────────────────────────────
// markers
// ─────────────────────────────────────────────────────────────────────────────

/** The index a marker with `frame` goes to: after every marker at or before that frame. */
function markerInsertionIndex(markers: readonly Marker[], frame: number): number {
  let index = 0;
  while (index < markers.length && markers[index].frame <= frame) index++;
  return index;
}

function applyAddMarker(doc: EditTimeline, op: AddMarker): { doc: EditTimeline; inverse: EditOp[] } {
  const marker = clone(op.marker);
  assertMarkerShape(marker, 'addMarker.marker', 'addMarker');

  if (doc.markers.some((existing) => existing.id === marker.id)) {
    throw new EditEngineError(`marker id '${marker.id}' is already in use`, 'addMarker');
  }

  let index = markerInsertionIndex(doc.markers, marker.frame);
  if (op.index !== undefined) {
    if (!isInteger(op.index) || op.index < 0 || op.index > doc.markers.length) {
      throw new EditEngineError(
        `index must be an integer within 0..${doc.markers.length}, found ${String(op.index)}`,
        'addMarker',
      );
    }
    const before = op.index > 0 ? doc.markers[op.index - 1] : undefined;
    const after = op.index < doc.markers.length ? doc.markers[op.index] : undefined;
    if (before !== undefined && before.frame > marker.frame) {
      throw new EditEngineError(
        `index ${op.index} would put frame ${marker.frame} after frame ${before.frame}, breaking the frame ordering`,
        'addMarker',
      );
    }
    if (after !== undefined && marker.frame > after.frame) {
      throw new EditEngineError(
        `index ${op.index} would put frame ${marker.frame} before frame ${after.frame}, breaking the frame ordering`,
        'addMarker',
      );
    }
    index = op.index;
  }

  doc.markers.splice(index, 0, marker);

  return { doc, inverse: [{ type: 'removeMarker', markerId: marker.id }] };
}

function applySetMarker(doc: EditTimeline, op: SetMarker): { doc: EditTimeline; inverse: EditOp[] } {
  const index = doc.markers.findIndex((marker) => marker.id === op.markerId);
  if (index < 0) {
    throw new EditEngineError(`unknown marker id '${op.markerId}'`, 'setMarker');
  }

  const marker = doc.markers[index];
  const patch = op.patch as unknown as Record<string, unknown>;

  if (hasOwn(patch, 'id') && patch.id !== op.markerId) {
    throw new EditEngineError('setMarker must keep the marker id', 'setMarker');
  }
  if (hasOwn(patch, 'frame')) {
    const frame = patch.frame;
    if (!isInteger(frame) || frame < 0) {
      throw new EditEngineError('a marker frame must be a non-negative integer', 'setMarker');
    }
    const previousFrame = index > 0 ? doc.markers[index - 1].frame : Number.NEGATIVE_INFINITY;
    const followingFrame =
      index + 1 < doc.markers.length ? doc.markers[index + 1].frame : Number.POSITIVE_INFINITY;
    if (frame < previousFrame || frame > followingFrame) {
      // Moving a marker past a neighbour would need the marker list re-sorted, and a re-sort
      // cannot be undone by one op (markers sharing a frame have no order in the contract) —
      // removeMarker + addMarker is the path for that.
      throw new EditEngineError(
        `frame ${frame} would break the marker frame ordering (allowed ${previousFrame}..${followingFrame}); ` +
          'removeMarker + addMarker moves a marker past its neighbours',
        'setMarker',
      );
    }
  }

  const fields = asMarkerFields(marker);
  const inversePatch: Record<string, unknown> = {};
  for (const key of Object.keys(patch)) {
    inversePatch[key] = hasOwn(fields, key) ? fields[key] : undefined;
  }
  for (const key of Object.keys(patch)) {
    writeField(fields, key, patch[key]);
  }

  // `setMarker` may patch any field, and `undefined` clears it — but the marker must still be one
  // the engine may edit, so the post-patch marker is held to the same field invariants
  // `assertInvariants` and `addMarker` apply. `apply` cloned the document up front, so a refusal
  // here cannot reach the caller's document.
  assertMarkerShape(marker, `markers[${index}]`, 'setMarker');

  return {
    doc,
    inverse: [{ type: 'setMarker', markerId: op.markerId, patch: inversePatch as Partial<Marker> }],
  };
}

function applyRemoveMarker(
  doc: EditTimeline,
  op: RemoveMarker,
): { doc: EditTimeline; inverse: EditOp[] } {
  const index = doc.markers.findIndex((marker) => marker.id === op.markerId);
  if (index < 0) {
    throw new EditEngineError(`unknown marker id '${op.markerId}'`, 'removeMarker');
  }

  const [removed] = doc.markers.splice(index, 1);

  // The slot is carried explicitly: markers sharing a frame have no order in the contract, so
  // "insert by frame" alone could not put a tied marker back where it was.
  return { doc, inverse: [{ type: 'addMarker', marker: removed, index }] };
}

// ─────────────────────────────────────────────────────────────────────────────
// P4 ops — placement conveniences
// ─────────────────────────────────────────────────────────────────────────────

/** Structural equality over JSON-shaped values (key order ignored, `undefined` and absent equal). */
function deepEqual(left: unknown, right: unknown): boolean {
  if (left === right) return true;
  if (Array.isArray(left) || Array.isArray(right)) {
    if (!Array.isArray(left) || !Array.isArray(right) || left.length !== right.length) return false;
    return left.every((value, i) => deepEqual(value, right[i]));
  }
  if (isPlainObject(left) && isPlainObject(right)) {
    const keys = new Set([...Object.keys(left), ...Object.keys(right)]);
    for (const key of keys) {
      if (!deepEqual(left[key], right[key])) return false;
    }
    return true;
  }
  return false;
}

/** Refuses a span that touches the neighbours around `index` (the slot an item would occupy). */
function requireFreeSpan(
  items: readonly Item[],
  index: number,
  recordIn: number,
  recordOut: number,
  op: EditOp['type'],
  skipIndex?: number,
): void {
  const previous = index > 0 ? items[index - 1] : undefined;
  const following = items.find((_, i) => i >= index && i !== skipIndex);
  if (previous !== undefined && previous.recordOut > recordIn) {
    throw new EditEngineError(
      `the span starts at ${recordIn}, overlapping the previous item which ends at ${previous.recordOut}`,
      op,
    );
  }
  if (following !== undefined && recordOut > following.recordIn) {
    throw new EditEngineError(
      `the span ends at ${recordOut}, overlapping the following item which starts at ${following.recordIn}`,
      op,
    );
  }
}

function applyAppend(doc: EditTimeline, op: Append): { doc: EditTimeline; inverse: EditOp[] } {
  const track = requireTrack(doc, op.trackId, 'append');
  const item = clone(op.item);
  assertItemShape(item, 'append.item');
  assertItemFitsTrack(item, track, 'append', 'append.item');
  requireFreshItemId(doc, item.id, 'append');

  const duration = durationOf(item);
  const last = track.items[track.items.length - 1];
  const start = last === undefined ? 0 : last.recordOut;
  item.recordIn = start;
  item.recordOut = start + duration;
  track.items.push(item);

  return { doc, inverse: [{ type: 'lift', itemId: item.id }] };
}

function applyReplace(doc: EditTimeline, op: Replace): { doc: EditTimeline; inverse: EditOp[] } {
  const { track, index, item: original } = requireItem(doc, op.itemId, 'replace');
  const replacement = clone(op.item);
  if (!isPlainObject(replacement)) {
    throw new EditEngineError('replace.item must be an object', 'replace');
  }
  replacement.id = original.id;
  replacement.recordIn = original.recordIn;
  replacement.recordOut = original.recordOut;
  assertItemShape(replacement, 'replace.item');
  assertItemFitsTrack(replacement, track, 'replace', 'replace.item');

  track.items[index] = replacement;
  return { doc, inverse: [{ type: 'overwrite', itemId: op.itemId, item: original }] };
}

function applyFitToFill(doc: EditTimeline, op: FitToFill): { doc: EditTimeline; inverse: EditOp[] } {
  const { track, index, item } = requireItem(doc, op.itemId, 'fitToFill');
  if (!isInteger(op.recordIn) || !isInteger(op.recordOut)) {
    throw new EditEngineError(
      `the span must use integer frames, found recordIn ${String(op.recordIn)} / recordOut ${String(op.recordOut)}`,
      'fitToFill',
    );
  }
  if (op.recordIn < 0 || op.recordOut <= op.recordIn) {
    throw new EditEngineError(`the span [${op.recordIn}, ${op.recordOut}) is empty or starts before frame 0`, 'fitToFill');
  }
  requireFreeSpan(track.items, index, op.recordIn, op.recordOut, 'fitToFill', index);

  const original = clone(item);
  const oldDuration = durationOf(item);
  const newDuration = op.recordOut - op.recordIn;

  if (item.type === 'mediaClip') {
    const oldRate = typeof item.playbackRate === 'number' && item.playbackRate > 0 ? item.playbackRate : 1;
    const newRate = (oldDuration * oldRate) / newDuration;
    if (!Number.isFinite(newRate) || newRate <= 0) {
      throw new EditEngineError(`the fitted playback rate ${newRate} is not a positive number`, 'fitToFill');
    }
    item.playbackRate = newRate;
  }
  item.recordIn = op.recordIn;
  item.recordOut = op.recordOut;
  if (item.type === 'mediaClip') assertMediaDepth(item, `item '${op.itemId}'`, 'fitToFill');

  return { doc, inverse: [{ type: 'overwrite', itemId: op.itemId, item: original }] };
}

function applyPlaceOnTop(doc: EditTimeline, op: PlaceOnTop): { doc: EditTimeline; inverse: EditOp[] } {
  const track = requireTrack(doc, op.trackId, 'placeOnTop');
  const item = clone(op.item);
  assertItemShape(item, 'placeOnTop.item');
  assertItemFitsTrack(item, track, 'placeOnTop', 'placeOnTop.item');
  requireFreshItemId(doc, item.id, 'placeOnTop');

  const index = canonicalIndex(track.items, item.recordIn);
  requireFreeSpan(track.items, index, item.recordIn, item.recordOut, 'placeOnTop');
  track.items.splice(index, 0, item);

  return { doc, inverse: [{ type: 'lift', itemId: item.id }] };
}

function applyJoin(doc: EditTimeline, op: Join): { doc: EditTimeline; inverse: EditOp[] } {
  const { track, index, item } = requireItem(doc, op.itemId, 'join');
  const right = track.items[index + 1];
  if (right === undefined) {
    throw new EditEngineError(`item '${op.itemId}' is the last item on its track; there is nothing to join it with`, 'join');
  }
  if (right.recordIn !== item.recordOut) {
    throw new EditEngineError(
      `the following item starts at ${right.recordIn}, not at this item's end ${item.recordOut}; close the gap first`,
      'join',
    );
  }

  // The merged item takes the left half's head and the right half's tail (its out fade, a reversed
  // clip's lowest source frame, the cuts after the join). The pair must be exactly what `blade`
  // makes of it, because that single blade IS the inverse; anything else is refused.
  const merged = clone(item);
  merged.recordOut = right.recordOut;
  if (merged.type === 'mediaClip' && right.type === 'mediaClip') {
    if (hasOwn(right, 'fadeOutFrames')) merged.fadeOutFrames = right.fadeOutFrames;
    else delete merged.fadeOutFrames;
    if (isReversed(merged) && !isHeld(merged)) {
      if (hasOwn(right, 'sourceIn')) merged.sourceIn = right.sourceIn;
      else delete merged.sourceIn;
    }
  }
  if (merged.type === 'multicamClip' && right.type === 'multicamClip' && Array.isArray(merged.cuts) && Array.isArray(right.cuts)) {
    const atJoin = right.activeAngleId !== activeAngleAt(merged, item.recordOut) ? [{ atFrame: item.recordOut, angleId: right.activeAngleId }] : [];
    merged.cuts = [...merged.cuts, ...atJoin, ...right.cuts];
  }
  const expected = bladeHalves(merged, item.recordOut, right.id);
  if (!deepEqual(expected.left, item) || !deepEqual(expected.right, right)) {
    throw new EditEngineError(
      `item '${right.id}' is not a continuation of '${op.itemId}' (a field other than the span differs), so joining them could not be undone exactly`,
      'join',
    );
  }
  // P7: a blade puts the right half in the left half's group, right after it; only that shape is
  // what the inverse blade rebuilds.
  const leftGroup = colorGroupIndexOf(doc, item.id);
  const rightGroup = colorGroupIndexOf(doc, right.id);
  const groups = doc.colorGroups ?? [];
  const bladeShaped = item.type === 'mediaClip'
    ? leftGroup === rightGroup && (leftGroup < 0 || groups[leftGroup].clipIds.indexOf(right.id) === groups[leftGroup].clipIds.indexOf(item.id) + 1)
    : rightGroup < 0;
  if (!bladeShaped) {
    throw new EditEngineError(`'${op.itemId}' and '${right.id}' are not in the same colour group, so joining them could not be undone exactly`, 'join');
  }

  const joinFrame = item.recordOut;
  track.items[index] = merged;
  track.items.splice(index + 1, 1);
  if (leftGroup >= 0 && item.type === 'mediaClip') {
    const members = groups[leftGroup].clipIds;
    members.splice(members.indexOf(right.id), 1);
  }

  return { doc, inverse: [{ type: 'blade', itemId: op.itemId, atFrame: joinFrame, newItemId: right.id }] };
}

/** The angle a multicam clip shows at `frame`: its active angle, then each cut at or before it. */
function activeAngleAt(item: MulticamClip, frame: number): string {
  let live = item.activeAngleId;
  for (const cut of item.cuts ?? []) if (cut.atFrame <= frame) live = cut.angleId;
  return live;
}

function applyNudge(doc: EditTimeline, op: Nudge): { doc: EditTimeline; inverse: EditOp[] } {
  const { track, index, item } = requireItem(doc, op.itemId, 'nudge');
  if (!isInteger(op.deltaFrames) || op.deltaFrames === 0) {
    throw new EditEngineError(`deltaFrames must be a non-zero integer, found ${String(op.deltaFrames)}`, 'nudge');
  }
  const recordIn = item.recordIn + op.deltaFrames;
  const recordOut = item.recordOut + op.deltaFrames;
  if (recordIn < 0) {
    throw new EditEngineError(`nudging by ${op.deltaFrames} would start the item at ${recordIn}, before frame 0`, 'nudge');
  }
  requireFreeSpan(track.items, index, recordIn, recordOut, 'nudge', index);

  shiftItem(item, op.deltaFrames);

  return { doc, inverse: [{ type: 'nudge', itemId: op.itemId, deltaFrames: -op.deltaFrames }] };
}

// ─────────────────────────────────────────────────────────────────────────────
// P4 ops — track structure
// ─────────────────────────────────────────────────────────────────────────────

/** Finds a track whatever its lock state — for the one op that may unlock it. */
function findTrack(doc: EditTimeline, trackId: string, op: EditOp['type']): { track: Track; index: number } {
  const index = doc.tracks.findIndex((candidate) => candidate.id === trackId);
  if (index < 0) {
    throw new EditEngineError(`unknown track id '${trackId}'`, op);
  }
  return { track: doc.tracks[index], index };
}

/** A whole track's shape: its own fields, plus its items under the per-track invariants with fresh ids. */
function assertTrackShape(doc: EditTimeline, track: Track, path: string, op: EditOp['type']): void {
  if (!isPlainObject(track)) {
    throw new EditEngineError(`${path} must be an object`, op);
  }
  if (typeof track.id !== 'string' || track.id.length === 0) {
    throw new EditEngineError(`${path}.id must be a non-empty string`, op);
  }
  if (doc.tracks.some((candidate) => candidate.id === track.id)) {
    throw new EditEngineError(`track id '${track.id}' is already in use`, op);
  }
  if (track.kind !== 'video' && track.kind !== 'audio') {
    throw new EditEngineError(`${path}.kind must be 'video' or 'audio', found ${String(track.kind)}`, op);
  }
  if (typeof track.name !== 'string') {
    throw new EditEngineError(`${path}.name must be a string`, op);
  }
  if (typeof track.height !== 'number' || !Number.isFinite(track.height) || track.height <= 0) {
    throw new EditEngineError(`${path}.height must be a positive number`, op);
  }
  for (const flag of ['locked', 'enabled', 'mute', 'solo'] as const) {
    if (typeof track[flag] !== 'boolean') {
      throw new EditEngineError(`${path}.${flag} must be a boolean`, op);
    }
  }
  if (!Array.isArray(track.items)) {
    throw new EditEngineError(`${path}.items must be an array`, op);
  }

  const seen = new Set<string>();
  let previous: Item | undefined;
  track.items.forEach((item, i) => {
    const itemPath = `${path}.items[${i}]`;
    assertItemShape(item, itemPath);
    assertItemFitsTrack(item, track, op, itemPath);
    if (seen.has(item.id)) {
      throw new EditEngineError(`${itemPath}.id duplicates item id '${item.id}'`, op);
    }
    seen.add(item.id);
    requireFreshItemId(doc, item.id, op);
    if (previous !== undefined && previous.recordOut > item.recordIn) {
      throw new EditEngineError(
        `${itemPath}.recordIn (${item.recordIn}) overlaps the previous item, which ends at ${previous.recordOut}`,
        op,
      );
    }
    previous = item;
  });
}

function applyAddTrack(doc: EditTimeline, op: AddTrack): { doc: EditTimeline; inverse: EditOp[] } {
  if (!isInteger(op.index) || op.index < 0 || op.index > doc.tracks.length) {
    throw new EditEngineError(`index must be between 0 and ${doc.tracks.length}, found ${String(op.index)}`, 'addTrack');
  }
  const track = clone(op.track);
  assertTrackShape(doc, track, 'addTrack.track', 'addTrack');
  doc.tracks.splice(op.index, 0, track);

  return { doc, inverse: [{ type: 'removeTrack', trackId: track.id }] };
}

function applyRemoveTrack(doc: EditTimeline, op: RemoveTrack): { doc: EditTimeline; inverse: EditOp[] } {
  const track = requireTrack(doc, op.trackId, 'removeTrack');
  const index = doc.tracks.indexOf(track);
  doc.tracks.splice(index, 1);

  return { doc, inverse: [{ type: 'addTrack', index, track }] };
}

function applyMoveTrack(doc: EditTimeline, op: MoveTrack): { doc: EditTimeline; inverse: EditOp[] } {
  const track = requireTrack(doc, op.trackId, 'moveTrack');
  const from = doc.tracks.indexOf(track);
  if (!isInteger(op.toIndex) || op.toIndex < 0 || op.toIndex >= doc.tracks.length) {
    throw new EditEngineError(`toIndex must be between 0 and ${doc.tracks.length - 1}, found ${String(op.toIndex)}`, 'moveTrack');
  }
  if (op.toIndex === from) {
    throw new EditEngineError(`track '${op.trackId}' is already at index ${from}`, 'moveTrack');
  }
  doc.tracks.splice(from, 1);
  doc.tracks.splice(op.toIndex, 0, track);

  return { doc, inverse: [{ type: 'moveTrack', trackId: op.trackId, toIndex: from }] };
}

function applySetTrackProperty(doc: EditTimeline, op: SetTrackProperty): { doc: EditTimeline; inverse: EditOp[] } {
  const shape = TRACK_PROPERTY_ALLOWLIST[op.key];
  if (shape === undefined) {
    throw new EditEngineError(`'${String(op.key)}' is not a settable track property`, 'setTrackProperty');
  }
  const value = op.value;
  const legal =
    shape === 'string' ? typeof value === 'string'
      : shape === 'nullableString' ? value === null || value === undefined || typeof value === 'string'
        : shape === 'positiveNumber' ? typeof value === 'number' && Number.isFinite(value) && value > 0
          : shape === 'keyframedNumber' ? value === null || value === undefined || isKeyframedNumber(value, 'gainDb')
            : shape === 'pan' ? value === undefined || (typeof value === 'number' && Number.isFinite(value) && value >= -1 && value <= 1)
              : shape === 'eq' ? value === null || value === undefined || isTrackEq(value)
                : shape === 'dynamics' ? value === null || value === undefined || isAudioDynamics(value)
                  : shape === 'denoise' ? value === null || value === undefined || isAudioDenoise(value)
                    : shape === 'leveler' ? value === null || value === undefined || isAudioLeveler(value)
                      : typeof value === 'boolean';
  if (!legal) {
    throw new EditEngineError(`'${String(value)}' is not a legal value for track property '${op.key}' (${shape})`, 'setTrackProperty');
  }

  // Unlocking is the one edit a locked track must accept, or a lock could never be undone.
  const track = op.key === 'locked' ? findTrack(doc, op.trackId, 'setTrackProperty').track : requireTrack(doc, op.trackId, 'setTrackProperty');
  const fields = track as unknown as Record<string, unknown>;
  // An absent optional key is restored as absent (`undefined` removes), never as an explicit null.
  const previous = hasOwn(fields, op.key) ? fields[op.key] : undefined;
  writeField(fields, op.key, value);

  return {
    doc,
    inverse: [{ type: 'setTrackProperty', trackId: op.trackId, key: op.key, value: previous as SetTrackProperty['value'] }],
  };
}

// ─────────────────────────────────────────────────────────────────────────────
// P4 ops — settings and in/out
// ─────────────────────────────────────────────────────────────────────────────

function assertRational(value: unknown, path: string, op: EditOp['type']): asserts value is Rational {
  if (!isPlainObject(value) || !isInteger(value.num) || !isInteger(value.den) || value.num <= 0 || value.den <= 0) {
    throw new EditEngineError(`${path} must be a rational with positive integer num and den`, op);
  }
}

function applySetTimelineSettings(doc: EditTimeline, op: SetTimelineSettings): { doc: EditTimeline; inverse: EditOp[] } {
  if (!isPlainObject(op.patch)) {
    throw new EditEngineError('patch must be an object', 'setTimelineSettings');
  }
  const keys = Object.keys(op.patch) as (keyof EditTimelineSettings)[];
  if (keys.length === 0) {
    throw new EditEngineError('patch names no settings key', 'setTimelineSettings');
  }
  for (const key of keys) {
    if (!TIMELINE_SETTINGS_KEYS.includes(key)) {
      throw new EditEngineError(`'${String(key)}' is not a timeline setting`, 'setTimelineSettings');
    }
    const value = op.patch[key];
    switch (key) {
      case 'width':
      case 'height':
      case 'sampleRate':
        if (!isInteger(value) || value <= 0) {
          throw new EditEngineError(`${key} must be a positive integer, found ${String(value)}`, 'setTimelineSettings');
        }
        break;
      case 'startTimecodeFrames':
        if (!isInteger(value) || value < 0) {
          throw new EditEngineError(`startTimecodeFrames must be a non-negative integer, found ${String(value)}`, 'setTimelineSettings');
        }
        break;
      case 'rate':
      case 'pixelAspect':
        assertRational(value, key, 'setTimelineSettings');
        break;
      case 'aspectPolicy':
        if (!ASPECT_POLICIES.includes(value as EditTimelineSettings['aspectPolicy'])) {
          throw new EditEngineError(`aspectPolicy must be one of ${ASPECT_POLICIES.join(', ')}, found ${String(value)}`, 'setTimelineSettings');
        }
        break;
    }
  }

  const previous: Partial<EditTimelineSettings> = {};
  const settings = doc.settings as unknown as Record<string, unknown>;
  for (const key of keys) {
    (previous as Record<string, unknown>)[key] = clone(settings[key]);
    settings[key] = clone(op.patch[key]);
  }

  return { doc, inverse: [{ type: 'setTimelineSettings', patch: previous }] };
}

/**
 * P7: add, replace or remove one colour group. The group list is kept in its existing order (a
 * replaced group stays where it was; a new one is appended), so the inverse — this op with the
 * previous group, or `undefined` — restores the document exactly.
 */
function applySetColorGroup(doc: EditTimeline, op: SetColorGroup, inverse = false): { doc: EditTimeline; inverse: EditOp[] } {
  if (typeof op.groupId !== 'string' || op.groupId.length === 0) {
    throw new EditEngineError('groupId must be a non-empty string', 'setColorGroup');
  }
  const groups = Array.isArray(doc.colorGroups) ? doc.colorGroups : [];
  const index = groups.findIndex((g) => g.id === op.groupId);
  const previous: ColorGroup | undefined = index >= 0 ? clone(groups[index]) : undefined;

  // T-112: the inverse re-sets `previous`. When that group names a clip the document no longer holds
  // as a media clip, the undo stack replays the restore BEFORE the inverse that brings the clip
  // back — so the exemption is granted by the apply path's `inverse` flag (see below), not by
  // anything carried on this op.
  const inverseOf = (g: ColorGroup | undefined): SetColorGroup =>
    ({ type: 'setColorGroup', groupId: op.groupId, group: g });

  if (op.group === undefined) {
    if (index < 0) throw new EditEngineError(`no colour group '${op.groupId}' to remove`, 'setColorGroup');
    const next = groups.filter((_, i) => i !== index);
    (doc as { colorGroups?: ColorGroup[] | null }).colorGroups = next.length === 0 && doc.colorGroups === undefined ? undefined : next;
    if (next.length === 0) delete (doc as { colorGroups?: unknown }).colorGroups;
    if (previous === undefined) throw new EditEngineError('unreachable', 'setColorGroup');
    return { doc, inverse: [inverseOf(previous)] };
  }

  if (!isPlainObject(op.group) || op.group.id !== op.groupId || !Array.isArray(op.group.clipIds)) {
    throw new EditEngineError(`group.id must equal groupId '${op.groupId}' and clipIds must be an array`, 'setColorGroup');
  }
  const next = index >= 0 ? groups.map((g, i) => (i === index ? clone(op.group as ColorGroup) : g)) : [...groups, clone(op.group)];
  (doc as { colorGroups?: ColorGroup[] | null }).colorGroups = next;
  // Refused BEFORE the write is observable: the cloned document is discarded on throw. Strict: a
  // group being SET must name clips that exist, except the ids it is putting back — those already in
  // the group it replaces, or (only when replaying the engine's own inverse) the dangling members of
  // a restored group. `inverse` is a parameter of the apply path, never a field on the op, so a wire
  // op, a proposal or a queued op cannot forge the exemption. Only a group outliving its clips may
  // dangle.
  const tolerated = inverse
    ? new Set((op.group as ColorGroup).clipIds)
    : new Set(previous?.clipIds ?? []);
  assertColorGroups(doc, { groupId: op.groupId, tolerated });
  return { doc, inverse: [inverseOf(previous)] };
}

/**
 * P8: add, replace or remove one nested sequence, keeping the list order (a replaced sequence stays
 * where it was; a new one is appended), so the inverse — the previous sequence or `undefined` —
 * restores the document exactly. The whole structure is re-checked after the write (ids unique
 * document-wide, nesting acyclic and shallow enough), and a sequence still played by a compound clip
 * cannot be removed.
 */
function applySetSequence(doc: EditTimeline, op: SetSequence): { doc: EditTimeline; inverse: EditOp[] } {
  if (typeof op.sequenceId !== 'string' || op.sequenceId.length === 0) throw new EditEngineError('sequenceId must be a non-empty string', 'setSequence');
  const sequences = Array.isArray(doc.sequences) ? doc.sequences : [];
  const index = sequences.findIndex((q) => q.id === op.sequenceId);
  const previous: EditSequence | undefined = index >= 0 ? clone(sequences[index]) : undefined;

  if (op.sequence === undefined) {
    if (index < 0) throw new EditEngineError(`no sequence '${op.sequenceId}' to remove`, 'setSequence');
    const playedBy = [doc.tracks, ...sequences.filter((_, i) => i !== index).map((q) => q.tracks)]
      .some((tracks) => tracks.some((t) => t.items.some((i) => i.type === 'compoundClip' && i.sequenceId === op.sequenceId)));
    if (playedBy) throw new EditEngineError(`sequence '${op.sequenceId}' is still played by a compound clip`, 'setSequence');
    const next = sequences.filter((_, i) => i !== index);
    if (next.length === 0) delete (doc as { sequences?: unknown }).sequences;
    else doc.sequences = next;
    return { doc, inverse: [{ type: 'setSequence', sequenceId: op.sequenceId, sequence: previous, index }] };
  }

  if (!isPlainObject(op.sequence) || op.sequence.id !== op.sequenceId) throw new EditEngineError(`sequence.id must equal sequenceId '${op.sequenceId}'`, 'setSequence');
  doc.sequences = index >= 0 ? sequences.map((q, i) => (i === index ? clone(op.sequence as EditSequence) : q)) : spliceAt(sequences, clone(op.sequence), op.index, 'setSequence');
  const rootIds = new Set(doc.tracks.flatMap((t) => t.items.map((i) => i.id)));
  assertSequences(doc, rootIds, 'setSequence');
  return { doc, inverse: [{ type: 'setSequence', sequenceId: op.sequenceId, sequence: previous }] };
}

/** Splices a new entry at a validated `index` (0..length), or appends when it is absent. */
function spliceAt<T>(list: readonly T[], entry: T, index: number | undefined, op: EditOp['type']): T[] {
  if (index === undefined) return [...list, entry];
  if (!isInteger(index) || index < 0 || index > list.length) throw new EditEngineError(`index must be between 0 and ${list.length}, found ${String(index)}`, op);
  return [...list.slice(0, index), entry, ...list.slice(index)];
}

/** P8: add, replace or remove one submix bus — the same shape as `setColorGroup`/`setSequence`. */
function applySetBus(doc: EditTimeline, op: SetBus): { doc: EditTimeline; inverse: EditOp[] } {
  if (typeof op.busId !== 'string' || op.busId.length === 0) throw new EditEngineError('busId must be a non-empty string', 'setBus');
  const buses = Array.isArray(doc.buses) ? doc.buses : [];
  const index = buses.findIndex((b) => b.id === op.busId);
  const previous: AudioBus | undefined = index >= 0 ? clone(buses[index]) : undefined;

  if (op.bus === undefined) {
    if (index < 0) throw new EditEngineError(`no bus '${op.busId}' to remove`, 'setBus');
    const next = buses.filter((_, i) => i !== index);
    if (next.length === 0) delete (doc as { buses?: unknown }).buses;
    else doc.buses = next;
    return { doc, inverse: [{ type: 'setBus', busId: op.busId, bus: previous, index }] };
  }

  if (!isPlainObject(op.bus) || op.bus.id !== op.busId) throw new EditEngineError(`bus.id must equal busId '${op.busId}'`, 'setBus');
  doc.buses = index >= 0 ? buses.map((b, i) => (i === index ? clone(op.bus as AudioBus) : b)) : spliceAt(buses, clone(op.bus), op.index, 'setBus');
  assertBuses(doc, 'setBus');
  return { doc, inverse: [{ type: 'setBus', busId: op.busId, bus: previous }] };
}

function applySetInOut(doc: EditTimeline, op: SetInOut): { doc: EditTimeline; inverse: EditOp[] } {
  const next = op.inOut;
  if (next !== null && next !== undefined) {
    if (!isPlainObject(next) || !isInteger(next.inFrame) || !isInteger(next.outFrame) || next.inFrame < 0 || next.outFrame <= next.inFrame) {
      throw new EditEngineError('inOut must be null or { inFrame >= 0, outFrame > inFrame } in integer frames', 'setInOut');
    }
  }

  const fields = doc as unknown as Record<string, unknown>;
  const previous = hasOwn(fields, 'inOut') ? (clone(fields.inOut) as EditRange | null) : undefined;
  writeField(fields, 'inOut', next === undefined ? undefined : clone(next));

  return { doc, inverse: [{ type: 'setInOut', inOut: previous }] };
}

// ─────────────────────────────────────────────────────────────────────────────
// P5 ops — keyframes
// ─────────────────────────────────────────────────────────────────────────────

/** A track EQ in the validator's ranges: cuts in [20, 20000] Hz (low below high), at most four bands. */
function isTrackEq(value: unknown): boolean {
  if (!isPlainObject(value)) return false;
  const hz = (v: unknown): boolean => v === null || v === undefined || (typeof v === 'number' && Number.isFinite(v) && v >= 20 && v <= 20000);
  if (!hz(value.lowCutHz) || !hz(value.highCutHz)) return false;
  if (typeof value.lowCutHz === 'number' && typeof value.highCutHz === 'number' && value.lowCutHz >= value.highCutHz) return false;
  if (value.bands !== undefined && value.bands !== null) {
    if (!Array.isArray(value.bands) || value.bands.length > 4) return false;
    for (const band of value.bands) {
      if (!isPlainObject(band)) return false;
      if (typeof band.frequencyHz !== 'number' || band.frequencyHz < 20 || band.frequencyHz > 20000) return false;
      if (typeof band.gainDb !== 'number' || !Number.isFinite(band.gainDb) || band.gainDb < -24 || band.gainDb > 24) return false;
      if (band.q !== undefined && (typeof band.q !== 'number' || !(band.q > 0) || band.q > 10)) return false;
    }
  }
  for (const key of Object.keys(value)) {
    if (!['lowCutHz', 'highCutHz', 'bands'].includes(key)) return false;
  }
  return true;
}

/**
 * Where a keyframed channel lives: the holder object and the key on it, resolved from a target.
 * A clip's `gainDb` and a track's `gainDb` are top-level fields; a transform channel lives on the
 * clip's `transform` object (created on demand by `setKeyframe`, never by a read).
 */
type ChannelSite = {
  holder: Record<string, unknown>;
  key: string;
  /** How to restore the whole container: a `setProperty` on the item or a `setTrackProperty` on the track. */
  restore: (previous: unknown) => EditOp;
  /** For a transform channel, the clip and whether `transform` existed before the write. */
  transformOwner?: { fields: Record<string, unknown>; hadTransform: boolean; previousTransform: unknown };
};

function resolveChannel(doc: EditTimeline, target: KeyframeTarget, param: KeyframeParam, op: EditOp['type'], create: boolean): ChannelSite {
  if (!isPlainObject(target)) {
    throw new EditEngineError('target must name an itemId or a trackId', op);
  }
  if ('trackId' in target) {
    if (param !== 'gainDb') {
      throw new EditEngineError(`a track carries only the 'gainDb' channel, not '${param}'`, op);
    }
    const track = requireTrack(doc, target.trackId, op);
    const fields = track as unknown as Record<string, unknown>;
    return {
      holder: fields,
      key: 'gainDb',
      restore: (previous) => ({ type: 'setTrackProperty', trackId: target.trackId, key: 'gainDb', value: previous as Keyframed<number> | null | undefined }),
    };
  }
  const { item } = requireItem(doc, target.itemId, op);
  if (item.type !== 'mediaClip') {
    throw new EditEngineError(`only a mediaClip carries keyframed channels; '${target.itemId}' is a ${item.type}`, op);
  }
  const fields = asFields(item);
  if (param === 'gainDb') {
    return {
      holder: fields,
      key: 'gainDb',
      restore: (previous) => ({ type: 'setProperty', itemId: target.itemId, key: 'gainDb', value: previous as PropertyValue }),
    };
  }
  if (!TRANSFORM_PARAMS.includes(param)) {
    throw new EditEngineError(`'${param}' is not a keyframed channel`, op);
  }
  const hadTransform = isPlainObject(fields.transform);
  const previousTransform = clone(fields.transform);
  if (!hadTransform) {
    if (!create) {
      throw new EditEngineError(`item '${target.itemId}' has no transform, so no '${param}' keyframes`, op);
    }
    fields.transform = {};
  }
  return {
    holder: fields.transform as Record<string, unknown>,
    key: param,
    restore: (previous) => ({ type: 'setProperty', itemId: target.itemId, key: 'transform', value: previous as PropertyValue }),
    transformOwner: { fields, hadTransform, previousTransform },
  };
}

function keysOf(holder: Record<string, unknown>, key: string): Keyframe<number>[] | null {
  const container = holder[key];
  if (!isPlainObject(container)) return null;
  const keys = (container as Keyframed<number>).keys;
  return Array.isArray(keys) ? (keys as Keyframe<number>[]) : null;
}

function applySetKeyframe(doc: EditTimeline, op: SetKeyframe): { doc: EditTimeline; inverse: EditOp[] } {
  if (!isInteger(op.frame) || op.frame < 0) {
    throw new EditEngineError(`frame must be a non-negative integer, found ${String(op.frame)}`, 'setKeyframe');
  }
  const range = KEYFRAME_PARAM_RANGE[op.param];
  if (range === undefined) {
    throw new EditEngineError(`'${String(op.param)}' is not a keyframed channel`, 'setKeyframe');
  }
  const inRange = Number.isFinite(op.value) && (range.exclusiveMin ? op.value > range.min : op.value >= range.min) && op.value <= range.max;
  if (!inRange) {
    throw new EditEngineError(`value ${String(op.value)} is outside the '${op.param}' range ${range.exclusiveMin ? '(' : '['}${range.min}, ${range.max}]`, 'setKeyframe');
  }
  if (!(INTERP_KINDS as readonly string[]).includes(String(op.interp))) {
    throw new EditEngineError(`interp must be one of ${INTERP_KINDS.join(', ')}`, 'setKeyframe');
  }

  const site = resolveChannel(doc, op.target, op.param, 'setKeyframe', true);
  const previousContainer = clone(site.holder[site.key]);
  const hadContainer = isPlainObject(site.holder[site.key]);
  let keys = keysOf(site.holder, site.key);

  const key: Keyframe<number> = { frame: op.frame, value: op.value, interp: op.interp };
  if (keys === null) {
    if (keys === null && (keys = null, true) && (previousContainer !== undefined && previousContainer !== null && !isPlainObject(previousContainer))) {
      throw new EditEngineError(`'${site.key}' holds something that is not a keyframed value`, 'setKeyframe');
    }
    // The static value is what the channel showed before it was animated: kept when it is a
    // number, otherwise the channel's identity. Never `null` — the server's `Keyframed<double>.Value`
    // is a double, so a null fails binding and the whole save with it (eng D4, m3 N3).
    const existing = hadContainer ? (site.holder[site.key] as Keyframed<number>) : undefined;
    const staticValue = typeof existing?.value === 'number' ? existing.value : KEYFRAME_IDENTITY[op.param];
    const container: Keyframed<number> = { ...(existing ?? {}), value: staticValue, keys: [key] };
    site.holder[site.key] = container;
    // The container (or its keys array) did not exist: the exact inverse restores the previous
    // container — including its absence, and including an absent `transform` object.
    // A transform channel is restored through the WHOLE `transform` object (that is what its
    // `setProperty` writes), so the inverse carries the transform as it was, sibling channels included.
    const previous = site.transformOwner ? site.transformOwner.previousTransform : previousContainer;
    return { doc, inverse: [site.restore(previous)] };
  }

  if (keys.length >= MAX_KEYFRAMES && !keys.some((k) => k.frame === op.frame)) {
    throw new EditEngineError(`a channel carries at most ${MAX_KEYFRAMES} keyframes`, 'setKeyframe');
  }

  const existing = keys.findIndex((k) => k.frame === op.frame);
  if (existing >= 0) {
    const old = keys[existing];
    keys[existing] = key;
    return { doc, inverse: [{ type: 'setKeyframe', target: op.target, param: op.param, frame: old.frame, value: old.value, interp: old.interp }] };
  }

  let index = 0;
  while (index < keys.length && keys[index].frame < op.frame) index++;
  keys.splice(index, 0, key);
  return { doc, inverse: [{ type: 'removeKeyframe', target: op.target, param: op.param, frame: op.frame }] };
}

function applyRemoveKeyframe(doc: EditTimeline, op: RemoveKeyframe): { doc: EditTimeline; inverse: EditOp[] } {
  const site = resolveChannel(doc, op.target, op.param, 'removeKeyframe', false);
  const keys = keysOf(site.holder, site.key);
  const index = keys === null ? -1 : keys.findIndex((k) => k.frame === op.frame);
  if (keys === null || index < 0) {
    throw new EditEngineError(`'${op.param}' has no keyframe at frame ${String(op.frame)}`, 'removeKeyframe');
  }
  const [removed] = keys.splice(index, 1);
  return { doc, inverse: [{ type: 'setKeyframe', target: op.target, param: op.param, frame: removed.frame, value: removed.value, interp: removed.interp }] };
}

function applyMoveKeyframe(doc: EditTimeline, op: MoveKeyframe): { doc: EditTimeline; inverse: EditOp[] } {
  if (!isInteger(op.toFrame) || op.toFrame < 0) {
    throw new EditEngineError(`toFrame must be a non-negative integer, found ${String(op.toFrame)}`, 'moveKeyframe');
  }
  if (op.toFrame === op.frame) {
    throw new EditEngineError('toFrame must differ from frame', 'moveKeyframe');
  }
  const site = resolveChannel(doc, op.target, op.param, 'moveKeyframe', false);
  const keys = keysOf(site.holder, site.key);
  const index = keys === null ? -1 : keys.findIndex((k) => k.frame === op.frame);
  if (keys === null || index < 0) {
    throw new EditEngineError(`'${op.param}' has no keyframe at frame ${String(op.frame)}`, 'moveKeyframe');
  }
  if (keys.some((k) => k.frame === op.toFrame)) {
    throw new EditEngineError(`'${op.param}' already has a keyframe at frame ${op.toFrame}`, 'moveKeyframe');
  }
  const [moved] = keys.splice(index, 1);
  const key: Keyframe<number> = { ...moved, frame: op.toFrame };
  let at = 0;
  while (at < keys.length && keys[at].frame < op.toFrame) at++;
  keys.splice(at, 0, key);
  return { doc, inverse: [{ type: 'moveKeyframe', target: op.target, param: op.param, frame: op.toFrame, toFrame: op.frame }] };
}

// ─────────────────────────────────────────────────────────────────────────────
// P5 ops — retime
// ─────────────────────────────────────────────────────────────────────────────

function applyRetime(doc: EditTimeline, op: Retime): { doc: EditTimeline; inverse: EditOp[] } {
  const { track, index, item } = requireItem(doc, op.itemId, 'retime');
  if (item.type !== 'mediaClip') {
    throw new EditEngineError(`only a mediaClip can be retimed; '${op.itemId}' is a ${item.type}`, 'retime');
  }
  if (typeof op.playbackRate !== 'number' || !Number.isFinite(op.playbackRate) || op.playbackRate <= 0) {
    throw new EditEngineError(`playbackRate must be a number greater than zero, found ${String(op.playbackRate)}`, 'retime');
  }
  if (!isInteger(op.recordOut) || op.recordOut <= item.recordIn) {
    throw new EditEngineError(`recordOut must be an integer after recordIn ${item.recordIn}, found ${String(op.recordOut)}`, 'retime');
  }
  const clip = item as MediaClip;
  const previousRate = typeof clip.playbackRate === 'number' ? clip.playbackRate : 1;
  const previousOut = item.recordOut;
  const delta = op.recordOut - previousOut;

  // A longer clip ripples the following items right; a shorter one closes up like a ripple trim.
  // Either way the geometry after the clip translates by `delta`, which is exactly what the
  // inverse (the old pair) undoes.
  if (delta > 0 && clip.reverse === true && (clip.recordOut - clip.recordIn) + delta > MAX_REVERSE_FRAMES) {
    throw new EditEngineError(`a reversed clip may span at most ${MAX_REVERSE_FRAMES} frames`, 'retime');
  }
  item.recordOut = op.recordOut;
  asFields(item).playbackRate = op.playbackRate;
  // A shorter clip must still hold its fades (refused, not clamped: the inverse is the old pair).
  assertMediaDepth(clip, `item '${op.itemId}'`, 'retime');
  shiftRange(track.items, index + 1, delta);

  return { doc, inverse: [{ type: 'retime', itemId: op.itemId, playbackRate: previousRate, recordOut: previousOut }] };
}

// ─────────────────────────────────────────────────────────────────────────────
// P5 ops — transitions
// ─────────────────────────────────────────────────────────────────────────────

function rateOf(clip: MediaClip): number {
  return typeof clip.playbackRate === 'number' && clip.playbackRate > 0 ? clip.playbackRate : 1;
}

function applyAddTransition(doc: EditTimeline, op: AddTransition): { doc: EditTimeline; inverse: EditOp[] } {
  const { track, index, item: outgoing } = requireItem(doc, op.afterItemId, 'addTransition');
  if (outgoing.type !== 'mediaClip') {
    throw new EditEngineError(`a transition joins two media clips; '${op.afterItemId}' is a ${outgoing.type}`, 'addTransition');
  }
  const incoming = track.items[index + 1];
  if (incoming === undefined || incoming.type !== 'mediaClip') {
    throw new EditEngineError(`'${op.afterItemId}' is not followed by a media clip`, 'addTransition');
  }
  if (incoming.recordIn !== outgoing.recordOut) {
    throw new EditEngineError(`the two clips are not adjacent (gap or transition between ${outgoing.recordOut} and ${incoming.recordIn})`, 'addTransition');
  }
  if (!isInteger(op.framesBefore) || !isInteger(op.framesAfter) || op.framesBefore < 0 || op.framesAfter < 0 || op.framesBefore + op.framesAfter < 1) {
    throw new EditEngineError('framesBefore and framesAfter must be non-negative integers summing to at least 1', 'addTransition');
  }
  if (op.framesBefore >= durationOf(outgoing)) {
    throw new EditEngineError(`framesBefore ${op.framesBefore} would consume the whole outgoing clip (${durationOf(outgoing)} frames)`, 'addTransition');
  }
  if (op.framesAfter >= durationOf(incoming)) {
    throw new EditEngineError(`framesAfter ${op.framesAfter} would consume the whole incoming clip (${durationOf(incoming)} frames)`, 'addTransition');
  }
  const transition = op.transition;
  if (!isPlainObject(transition) || typeof transition.id !== 'string' || transition.id.length === 0) {
    throw new EditEngineError('transition must carry a non-empty id', 'addTransition');
  }
  if (typeof transition.name !== 'string') {
    throw new EditEngineError('transition.name must be a string', 'addTransition');
  }
  if (typeof transition.transitionType !== 'string' || !TRANSITION_TYPES.includes(transition.transitionType)) {
    throw new EditEngineError(`transitionType '${String(transition.transitionType)}' is not in the catalogue`, 'addTransition');
  }
  if (transition.easing !== undefined && !(INTERP_KINDS as readonly string[]).includes(String(transition.easing))) {
    throw new EditEngineError(`easing '${String(transition.easing)}' is not an interpolation kind`, 'addTransition');
  }
  requireFreshItemId(doc, transition.id, 'addTransition');

  const incomingClip = incoming as MediaClip;
  const rate = rateOf(incomingClip);
  const hadSourceIn = sourceOffset(incomingClip) !== undefined;
  const incomingSourceIn = sourceOffset(incomingClip) ?? 0;
  // The incoming clip is shown from `framesBefore` frames before its own start during the
  // transition, so it needs that much source before its sourceIn.
  const handleNeeded = Math.round(op.framesBefore * rate);
  if (incomingClip.reverse !== true && incomingSourceIn < handleNeeded) {
    throw new EditEngineError(
      `insufficient_handles: the incoming clip has ${incomingSourceIn} source frames before its start, the transition needs ${handleNeeded}`,
      'addTransition',
    );
  }

  const cut = outgoing.recordOut;
  outgoing.recordOut = cut - op.framesBefore;
  incoming.recordIn = cut + op.framesAfter;
  const advance = Math.round(op.framesAfter * rate);
  if (incomingClip.reverse !== true && advance !== 0) {
    writeSourceOffset(incomingClip, incomingSourceIn + advance);
  }
  const placed: Transition = {
    ...(clone(transition) as Omit<Transition, 'recordIn' | 'recordOut'>),
    type: 'transition',
    recordIn: cut - op.framesBefore,
    recordOut: cut + op.framesAfter,
  };
  track.items.splice(index + 1, 0, placed);

  const inverse: EditOp[] = [{ type: 'removeTransition', transitionId: placed.id, cutFrame: cut }];
  // An incoming clip that carried no `sourceIn` key gained one here (absent and 0 are distinct
  // shapes — `trim`'s slip tells them apart), so the inverse clears it again after the cut is back.
  if (!hadSourceIn && incomingClip.reverse !== true && advance !== 0) {
    inverse.push({ type: 'setProperty', itemId: incoming.id, key: 'sourceIn', value: undefined });
  }
  return { doc, inverse };
}

function applyRemoveTransition(doc: EditTimeline, op: RemoveTransition): { doc: EditTimeline; inverse: EditOp[] } {
  const { track, index, item } = requireItem(doc, op.transitionId, 'removeTransition');
  if (item.type !== 'transition') {
    throw new EditEngineError(`'${op.transitionId}' is a ${item.type}, not a transition`, 'removeTransition');
  }
  const outgoing = track.items[index - 1];
  const incoming = track.items[index + 1];
  if (outgoing === undefined || incoming === undefined || outgoing.type !== 'mediaClip' || incoming.type !== 'mediaClip') {
    throw new EditEngineError('the transition is not between two media clips', 'removeTransition');
  }
  if (outgoing.recordOut !== item.recordIn || incoming.recordIn !== item.recordOut) {
    throw new EditEngineError('the transition does not sit exactly between its neighbours', 'removeTransition');
  }
  if (!isInteger(op.cutFrame) || op.cutFrame < item.recordIn || op.cutFrame > item.recordOut) {
    throw new EditEngineError(`cutFrame must be an integer inside the transition span [${item.recordIn}, ${item.recordOut}]`, 'removeTransition');
  }
  const incomingClip = incoming as MediaClip;
  const rate = rateOf(incomingClip);
  const framesAfter = item.recordOut - op.cutFrame;
  const framesBefore = op.cutFrame - item.recordIn;
  const incomingSourceIn = sourceOffset(incomingClip) ?? 0;
  const pullBack = Math.round(framesAfter * rate);
  if (incomingClip.reverse !== true && incomingSourceIn - pullBack < 0) {
    throw new EditEngineError('removing the transition at this cut would move the incoming clip before source frame 0', 'removeTransition');
  }

  const transition = clone(item) as Transition;
  track.items.splice(index, 1);
  outgoing.recordOut = op.cutFrame;
  incoming.recordIn = op.cutFrame;
  if (incomingClip.reverse !== true && pullBack !== 0) {
    writeSourceOffset(incomingClip, incomingSourceIn - pullBack);
  }

  const { recordIn: _in, recordOut: _out, ...rest } = transition;
  void _in;
  void _out;
  return {
    doc,
    inverse: [{ type: 'addTransition', afterItemId: outgoing.id, transition: rest, framesBefore, framesAfter }],
  };
}
