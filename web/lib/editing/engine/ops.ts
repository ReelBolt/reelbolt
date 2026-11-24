/**
 * The **closed** edit-op catalogue of the EditTimeline v2 engine.
 *
 * This module is types plus contract text; every op is *executed* by `./apply`. The union below
 * is closed on purpose: an op that is not a member cannot reach the engine at all (TypeScript
 * rejects it), so "which edits exist" is answered in exactly one place. The thirteen P1 ops —
 * `insert`, `overwrite`, `lift`, `extract`, `trim`, `blade`, `deleteGap`, `closeGaps`, `move`,
 * `setProperty`, `addMarker`, `setMarker`, `removeMarker` — plus the twelve P4 ops — `append`,
 * `replace`, `fitToFill`, `placeOnTop`, `join`, `nudge`, `addTrack`, `removeTrack`, `moveTrack`,
 * `setTrackProperty`, `setTimelineSettings`, `setInOut` — plus the six P5 ops — `setKeyframe`,
 * `removeKeyframe`, `moveKeyframe`, `retime`, `addTransition`, `removeTransition` — plus the P7
 * `setColorGroup` and the two P8 ops `setSequence` and `setBus` — and nothing else (thirty-four).
 * Link/group stays deferred. Applying an AI-assist proposal is deliberately NOT an op: a proposal
 * arrives as groups of ops drawn from this same catalogue and `lib/editing/proposals` applies the
 * accepted groups as one atomic batch, so its inverse is still a program of named ops.
 *
 * ## The P8 ops and their inverses
 *
 * `setSequence` adds, replaces or removes ONE nested sequence (the timeline a compound clip
 * plays); its inverse is the same op with the previous sequence (or `undefined`). Removing a
 * sequence a compound clip still plays is refused, as is a sequence that would contain itself or
 * nest deeper than {@link MAX_NESTING_DEPTH}. `setBus` is the same shape for a submix bus; a bus
 * routed into a cycle is refused, and removing a bus tracks still name is allowed (they feed the
 * master, exactly as the validator tolerates).
 *
 * ## The P5 ops and their inverses
 *
 * `setKeyframe` writes one key of one keyframed channel (a clip's `gainDb` or a transform channel,
 * or a track's `gainDb`); its inverse re-sets the key that was there, `removeKeyframe`s the key it
 * added, or — when the op had to CREATE the channel container — `setProperty`/`setTrackProperty`
 * restores the whole previous container (absent included), so a container's `null`-vs-`[]`
 * ambiguity never reaches the inverse. `removeKeyframe` never deletes a container and inverts to
 * `setKeyframe`; `moveKeyframe` moves back. `retime` carries BOTH the new rate and the new
 * `recordOut` (the rounding of `duration × rate` is not invertible by itself) and ripples the
 * following items; its inverse is the retime with the old pair. `addTransition` trims the two
 * neighbours into the transition span and advances the incoming clip's `sourceIn`; its inverse is
 * `removeTransition` carrying the cut frame, and vice versa.
 *
 * ## The P4 ops and their inverses
 *
 * Every P4 op inverts to a program drawn from the same named set, exactly like the P1 ops:
 * `append`/`placeOnTop` invert to `lift` (they only ever place an item into free space);
 * `replace` and `fitToFill` invert to `overwrite` with the original item (both keep the id, and
 * the original carries every field the op changed); `join` inverts to the one `blade` that
 * reproduces the two halves — which is why `join` **refuses** a neighbour that is not an exact
 * continuation (same kind, every field equal, `sourceIn` advanced by the left half's length),
 * because a `blade` cannot resurrect a right half whose fields differed; `nudge` inverts to the
 * opposite `nudge`; `addTrack`/`removeTrack` are each other's inverse carrying the whole track
 * (items included, so removing a populated track is one undoable op); `moveTrack` moves back;
 * `setTrackProperty`, `setTimelineSettings` and `setInOut` re-set the old values.
 *
 * ## Why `inverse` is a program (`EditOp[]`) rather than a single op
 *
 * Ten of the thirteen ops are exactly self-inverse under one op (`overwrite` re-applied with the
 * old item, `move` moved back, `setProperty`/`setMarker` re-set to the old value, …), but three
 * cannot be expressed as one op in a catalogue whose compound *forward* ops are deferred and
 * which has no merge/shift primitive: `blade` must both drop the right half and restore the left
 * half's original span, and `deleteGap`/`closeGaps` translate an unbounded number of items.
 * Those three therefore invert to a short program drawn **only from the same named set** — two
 * ops for `blade` (`lift` the right half, then `overwrite` the left half back to its original
 * span), one `move` per affected item for `deleteGap`/`closeGaps` (rightmost first, so every
 * destination is already free when the move runs). Nothing outside this file's union may appear
 * in either direction.
 *
 * ## The one deviation from the ticket's op semantics — and why it is forced
 *
 * The ticket specifies `insert`'s ripple as `delta = max(0, item.recordOut - nextRecordIn)` and
 * `extract`'s close-up as `max(0, duration - gapAfter)`, with a length-1 inverse for each. Those
 * two cannot both hold, and the reason is information-theoretic rather than a coding slip:
 * **a rippling `insert` destroys `nextRecordIn`**, and no function of the post-insert document
 * can recover it again. A partially-overlapping insert (the inserted item runs `0 < d < duration`
 * past where the following item starts) produces a document whose local shape — item ending
 * exactly where the next one begins — is byte-identical to one where the item merely *filled* a
 * gap of that size. The two need different undo shifts, so one single-op inverse cannot serve
 * both, and an inverse that guesses silently corrupts the document. The same ambiguity makes the
 * ticket's `extract` formula (`max(0, duration - gapAfter)`) non-invertible in the middle band
 * `0 < gapAfter < duration`.
 *
 * The engine therefore uses the ticket's *prose* rule for `extract` ("adjacent → full ripple;
 * already gapped → no shift") and accepts exactly the two `insert` positions that are
 * recoverable, which is what keeps `insert`, `lift` and `extract` mutually exact inverses:
 *
 *   - `insert` **fits in the gap** (`recordOut <= nextRecordIn`) — ripples nothing; its inverse
 *     is `lift`.
 *   - `insert` lands **flush at the following item's start** (`nextRecordIn === recordIn`) — the
 *     classic ripple insert, shifting everything at/after that point right by the item's own
 *     `duration`; its inverse is `extract`.
 *   - anything in between (a *partial* overlap with the following item) throws `EditEngineError`
 *     instead of returning an inverse that cannot restore the document. To place an item over an
 *     existing one, use `overwrite`; to push content aside from an arbitrary point, insert flush
 *     at the next item's start.
 *
 * `extract` in turn shifts the following items left by the removed item's `duration` when it was
 * adjacent (`gapAfter === 0`) and leaves them alone when it was already gapped (`gapAfter > 0`),
 * which is exactly the shift that re-inserting the item restores — so `extract` is also the
 * inverse of the flush `insert`, and its own inverse is `insert` in both branches.
 *
 * Two smaller decisions follow from the same rule — *an op may not destroy information its own
 * inverse needs*:
 *
 *   - **`trim`'s `slide` translates the neighbour's facing edge by the same delta; it never snaps
 *     it onto the moved item's edge.** Snapping looks equivalent (the two coincide when the items
 *     are adjacent) but silently swallows a pre-existing gap, and the gap's size is not
 *     recoverable from the post-op document — a slide over a gap would then undo to a different
 *     document. A translation leaves every gap exactly as wide as it was.
 *   - **`addMarker` accepts an optional, validated `index`** (see {@link AddMarker}), because the
 *     contract orders markers only *by frame*: markers sharing a frame have no canonical order, so
 *     `removeMarker`'s inverse carries the slot it took the marker out of instead of guessing.
 *
 * Record spans are half-open `[recordIn, recordOut)` integer frames on the output clock. Within
 * a track items are sorted by `recordIn` and never overlap (`prev.recordOut <= next.recordIn`);
 * gaps are legal and meaningful. Item ids are opaque and unique document-wide. A `locked` track
 * rejects every op that names it — directly (`trackId`) or through an item that lives on it.
 * Every op is pure: it clones on write and never touches its input document.
 */

import type { AudioBus, AudioDenoise, AudioDynamics, AudioLeveler, EditSequence, MulticamAngle, MulticamCut, AssetRef, ChromaKeyer, ClipCrop, ClipEffect, ClipTransform, ColorGroup, EditRange, EditTimelineSettings, GradeStack, Item, Keyframed, Marker, QuadCorners, ReframeTarget, RegionClipEffect, ShapeMask, Track, TrackEq, Transition } from '../types';

// ─────────────────────────────────────────────────────────────────────────────
// Small unions used by the ops
// ─────────────────────────────────────────────────────────────────────────────

/** Which end of an item a {@link Trim} moves. */
export type TrimEdge = 'in' | 'out';

/**
 * How a {@link Trim} absorbs the move.
 *
 * - `ripple` — move the named edge and shift every following item by the same delta.
 * - `roll` — move the shared boundary with the adjacent neighbour on that edge, both edges
 *   together (the neighbour's own `sourceIn` follows when its head moved).
 * - `slip` — change only the item's `sourceIn`; the timeline geometry is untouched.
 * - `slide` — move the item's whole span and let the neighbour on the named edge absorb it.
 */
export type TrimMode = 'ripple' | 'roll' | 'slip' | 'slide';

/** Every property {@link SetProperty} may address. See {@link SET_PROPERTY_ALLOWLIST}. */
export type PropertyKey =
  | 'name'
  | 'enabled'
  | 'text'
  | 'fontSize'
  | 'fontFamily'
  | 'color'
  | 'language'
  | 'playbackRate'
  | 'sourceIn'
  | 'gainDb'
  | 'transform'
  | 'crop'
  | 'reverse'
  | 'sourceRotation'
  | 'hold'
  | 'fadeInFrames'
  | 'fadeOutFrames'
  | 'transitionType'
  | 'easing'
  | 'generatorType'
  | 'generatorConfig'
  | 'assetRef'
  | 'grade'
  | 'mask'
  | 'keyer'
  | 'effects'
  | 'regionEffects'
  | 'stabilize'
  | 'reframe'
  | 'retimeQuality'
  | 'cornerPin'
  | 'matte'
  | 'quad'
  | 'template'
  | 'look'
  | 'position'
  | 'channelMap'
  | 'inputProfile'
  | 'blendMode'
  | 'sequenceId'
  | 'activeAngleId'
  | 'cuts'
  | 'angles'
  | 'linkId'
  | 'audioSource';

/**
 * A settable property's value. `undefined` is meaningful and not a gap in the type: it **removes**
 * the key, which is what lets `setProperty`'s inverse restore a property that was absent before
 * (a `Keyframed` gain, say) without a dedicated delete op in the catalogue.
 */
export type PropertyValue =
  | string
  | number
  | boolean
  | Keyframed<number>
  | ClipTransform
  | ClipCrop
  | AssetRef
  | GradeStack
  | ShapeMask
  | ChromaKeyer
  | ClipEffect[]
  | RegionClipEffect[]
  | ReframeTarget
  | QuadCorners
  | Keyframed<QuadCorners>
  | MulticamCut[]
  | MulticamAngle[]
  | Record<string, unknown>
  | null
  | undefined;

/** P5: the keyframed channels `setKeyframe`/`removeKeyframe`/`moveKeyframe` may address. */
export type KeyframeParam = 'gainDb' | 'positionX' | 'positionY' | 'scale' | 'scaleY' | 'rotation' | 'opacity';

/** The channels that live on a media clip's `transform` (every other `KeyframeParam` is a top-level field). */
export const TRANSFORM_PARAMS: readonly KeyframeParam[] = ['positionX', 'positionY', 'scale', 'scaleY', 'rotation', 'opacity'];

/** P5: what a keyframe op addresses — one item's channel or one track's fader. */
export type KeyframeTarget = { itemId: string } | { trackId: string };

/**
 * P5: the closed `xfade` transition catalogue — mirrors `EditTimelineValidator.TransitionTypes`
 * byte for byte (a word outside it is refused by the engine AND by the server).
 */
export const TRANSITION_TYPES: readonly string[] = [
  'fade', 'dissolve', 'fadeblack', 'fadewhite', 'fadegrays', 'distance', 'pixelize',
  'wipeleft', 'wiperight', 'wipeup', 'wipedown', 'wipetl', 'wipetr', 'wipebl', 'wipebr',
  'slideleft', 'slideright', 'slideup', 'slidedown',
  'smoothleft', 'smoothright', 'smoothup', 'smoothdown',
  'circlecrop', 'rectcrop', 'circleopen', 'circleclose',
  'vertopen', 'vertclose', 'horzopen', 'horzclose',
  'diagtl', 'diagtr', 'diagbl', 'diagbr',
  'hlslice', 'hrslice', 'vuslice', 'vdslice',
  'radial', 'zoomin', 'squeezev', 'squeezeh', 'hblur',
];

/** P5: the closed generator catalogue — mirrors `EditTimelineValidator.GeneratorTypes`. */
export const GENERATOR_TYPES: readonly string[] = ['color', 'bars', 'testsrc'];

/** P7: the closed effects catalogue — mirrors `EditTimelineValidator.EffectTypes`. */
export const EFFECT_TYPES: readonly string[] = ['blur', 'sharpen', 'vignette', 'grain', 'pixelate', 'mono'];

/** The closed region-effect words (`EditTimelineValidator.RegionEffectWords`). */
export const REGION_EFFECT_TYPES: readonly string[] = ['blur', 'pixelate', 'highlight', 'spotlight', 'colorpop', 'outline'];

/** P7: the closed title-template catalogue — mirrors `EditTimelineValidator.TitleTemplates`. */
export const TITLE_TEMPLATES: readonly string[] = ['plain', 'lowerThird', 'boxed', 'outline', 'card'];

/** R09.6: the closed caption-look catalogue — mirrors `EditTimelineValidator.CaptionLooks`. */
export const CAPTION_LOOKS: readonly string[] = ['clean', 'bold', 'boxed', 'outline', 'spotlight'];

/** R09.6/R10.5: the 3x3 text-position pad — mirrors `EditTimelineValidator.TextPositions`. */
export const TEXT_POSITIONS: readonly string[] = ['topLeft', 'topCenter', 'topRight', 'middleLeft', 'center', 'middleRight', 'bottomLeft', 'bottomCenter', 'bottomRight'];

/** P7: how a slowed clip makes its frames — mirrors `EditTimelineValidator.RetimeQualities`. */
export const RETIME_QUALITIES: readonly string[] = ['frame', 'opticalFlow'];

/** P7: the mask shapes — mirrors `EditTimelineValidator.MaskKinds`. */
export const MASK_KINDS: readonly string[] = ['rect', 'ellipse'];
/** The segmentation mask variant's limits (the server validator is the trusted check). */
export const SEGMENTATION_MASK_MAX_TRACKS = 8;
export const SEGMENTATION_MASK_MAX_FEATHER_PX = 64;

/** P8: the channel maps — mirrors `EditTimelineValidator.ChannelMaps`. */
export const CHANNEL_MAPS: readonly string[] = ['stereo', 'left', 'right', 'mono', 'swap'];

/** T-012: the blend-mode words, in catalogue order — mirrors `BlendModes.Words`. */
export const BLEND_MODES: readonly string[] = ['normal', 'multiply', 'screen', 'overlay', 'softLight', 'hardLight', 'darken', 'lighten', 'add', 'difference'];

/** P8: the denoiser kinds — mirrors `EditTimelineValidator.DenoiseKinds`. */
export const DENOISE_KINDS: readonly string[] = ['fft'];

/** P8: the leveler kinds — mirrors `EditTimelineValidator.LevelerKinds`. */
export const LEVELER_KINDS: readonly string[] = ['speech', 'dynamic'];

/** P8: nesting depth (root = 1) for compound clips and bus routing — mirrors `MaxNestingDepth`. */
export const MAX_NESTING_DEPTH = 4;
/** P8: ceilings mirrored from the validator. */
export const MAX_SEQUENCES = 64;
export const MAX_BUSES = 16;
export const MAX_MULTICAM_ANGLES = 16;
export const MAX_MULTICAM_CUTS = 2000;

/** P8: the closed ranges of the audio processing fields, mirrored from `ValidateAudioChannel`. */
export const AUDIO_RANGES = {
  compressor: { thresholdDb: [-60, 0], ratio: [1, 20], attackMs: [1, 2000], releaseMs: [1, 2000], makeupDb: [0, 24] },
  limiter: { ceilingDb: [-24, 0] },
  gate: { thresholdDb: [-90, 0], ratio: [1, 20], attackMs: [1, 2000], releaseMs: [1, 2000] },
  deesser: { intensity: [0, 1] },
  denoise: { strength: [0, 1] },
  leveler: { targetDb: [-40, 0] },
} as const;

/** P7: the per-stack, per-curve, per-clip and per-timeline ceilings, mirrored from the validator. */
export const MAX_GRADE_NODES = 8;
export const MAX_CURVE_POINTS = 16;
export const MAX_EFFECTS = 8;

/** Most region effects one clip may carry (`EditTimelineValidator.MaxRegionEffects`). */
export const MAX_REGION_EFFECTS = 4;

/** Blur alone ranges to 2 (sigma = amount x 20): mirrors `EditTimelineValidator.MaxBlurAmount`. Every other effect stays in [0, 1]. */
export const MAX_BLUR_AMOUNT = 2;
export const MAX_COLOR_GROUPS = 32;

/** P7: the closed value range of every primaries channel, mirrored from the validator. */
export const GRADE_RANGES: Readonly<Record<'wheel' | 'contrast' | 'pivot' | 'saturation' | 'hue' | 'temperature' | 'tint' | 'exposure', { min: number; max: number; identity: number }>> = {
  wheel: { min: -1, max: 1, identity: 0 },
  contrast: { min: 0.25, max: 3, identity: 1 },
  pivot: { min: 0, max: 1, identity: 0.5 },
  saturation: { min: 0, max: 3, identity: 1 },
  hue: { min: -180, max: 180, identity: 0 },
  temperature: { min: -1, max: 1, identity: 0 },
  tint: { min: -1, max: 1, identity: 0 },
  exposure: { min: -4, max: 4, identity: 0 },
};

/** P5: the interpolation words (`InterpKind` names) a transition's `easing` may take. */
export const INTERP_KINDS = ['linear', 'easeInQuad', 'easeOutQuad', 'easeInOutQuad', 'easeInCubic', 'easeOutCubic', 'easeInOutCubic', 'step'] as const;

/** P5: the longest clip that may be reversed, in frames — mirrors `EditTimelineValidator.MaxReverseFrames`. */
export const MAX_REVERSE_FRAMES = 1800;

/** P5: keyframes per channel — mirrors `EditTimelineValidator.MaxKeyframes`. */
export const MAX_KEYFRAMES = 256;

/** P5: the closed value range of every keyframed channel, mirrored from the validator. */
export const KEYFRAME_PARAM_RANGE: Readonly<Record<KeyframeParam, { min: number; max: number; exclusiveMin?: boolean }>> = {
  gainDb: { min: -60, max: 20 },
  positionX: { min: -2, max: 2 },
  positionY: { min: -2, max: 2 },
  scale: { min: 0, max: 8, exclusiveMin: true },
  scaleY: { min: 0, max: 8, exclusiveMin: true },
  rotation: { min: -360, max: 360 },
  opacity: { min: 0, max: 1 },
};

/**
 * The identity (no-op) static value of each keyframed channel: what an absent channel means. The
 * first `setKeyframe` on an untouched channel writes it as the container's `value`, because the
 * server's `Keyframed<double>.Value` is a non-nullable double and a JSON `null` fails binding.
 */
export const KEYFRAME_IDENTITY: Readonly<Record<KeyframeParam, number>> = {
  gainDb: 0,
  positionX: 0,
  positionY: 0,
  scale: 1,
  scaleY: 1,
  rotation: 0,
  opacity: 1,
};

/**
 * Item kinds that are picture only and so may not sit on an audio track (engine and validator
 * agree). A `mediaClip` may (its sound plays there), as may a transition between two of them, a
 * compound clip (a selection of audio clips makes one) and a multicam clip.
 */
export const PICTURE_ONLY_ITEM_TYPES: readonly Item['type'][] = ['titleClip', 'caption', 'generatorClip', 'insertClip', 'adjustmentClip'];

// ─────────────────────────────────────────────────────────────────────────────
// The ops
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Splice `item` into a track at its canonical array `index`, then ripple.
 *
 * `index` must be the canonical position for `item.recordIn` (the number of existing items
 * starting before it) and must be a legal position: the preceding item, if any, must already end
 * at or before `item.recordIn`. See the module note for which following-item positions are
 * accepted and why the rest throw.
 */
export type Insert = {
  type: 'insert';
  trackId: string;
  index: number;
  item: Item;
};

/**
 * Replace the *content* of the addressed item. `item.id` must equal `itemId`, and any span is
 * allowed as long as it overlaps no other item on the track. Keeping the id fixed is what makes
 * this exactly self-inverse.
 */
export type Overwrite = {
  type: 'overwrite';
  itemId: string;
  item: Item;
};

/** Remove the item and leave the gap. The inverse is `insert` back into that gap. */
export type Lift = {
  type: 'lift';
  itemId: string;
};

/**
 * Ripple-delete: remove the item, then close up only what it was adjacent to — the following
 * items shift left by the removed item's `duration` when `gapAfter === 0`, and not at all when
 * it was already followed by a gap.
 */
export type Extract = {
  type: 'extract';
  itemId: string;
};

/**
 * Move one edge of an item, clamped to the invariants.
 *
 * `deltaFrames` is a request, not a promise: it is clamped so that `duration >= 1`, `sourceIn >= 0`
 * and no item overlaps another. **The inverse is this same op with `deltaFrames` = minus the
 * EFFECTIVE (post-clamp) delta**, which is why a clamped trim still round-trips exactly.
 */
export type Trim = {
  type: 'trim';
  itemId: string;
  edge: TrimEdge;
  mode: TrimMode;
  deltaFrames: number;
};

/**
 * Split an item strictly inside its span. The left half keeps the item's id and ends at
 * `atFrame`; the right half takes `newItemId`, starts at `atFrame`, keeps the original end and
 * advances `sourceIn` by the split offset when the kind carries one. Every other property is
 * copied to both halves.
 */
export type Blade = {
  type: 'blade';
  itemId: string;
  atFrame: number;
  newItemId: string;
};

/**
 * Delete the gap containing `atFrame` by shifting every later item left by the gap's length.
 * Throws when `atFrame` is not inside a gap.
 */
export type DeleteGap = {
  type: 'deleteGap';
  trackId: string;
  atFrame: number;
};

/**
 * Remove every interior gap on a track, preserving item order and the first item's own
 * `recordIn`. The trailing space after the last item is not an interior gap and is untouched.
 */
export type CloseGaps = {
  type: 'closeGaps';
  trackId: string;
};

/** Move an item (keeping its duration) to another position and/or track, leaving a gap behind. */
export type Move = {
  type: 'move';
  itemId: string;
  toTrackId: string;
  toRecordIn: number;
};

/** Set one allowlisted property on one item. Throws on an unknown key or a wrongly-typed value. */
export type SetProperty = {
  type: 'setProperty';
  itemId: string;
  key: PropertyKey;
  value: PropertyValue;
};

/**
 * Add a marker. Its id must be fresh, and it is spliced in so the marker list stays frame-ordered.
 *
 * `index` is an optional, validated slot. Omitting it — the normal case for a hand-authored op —
 * inserts after every marker whose frame is `<=` the new one. It exists because the contract only
 * orders markers *by frame*, so markers sharing a frame have no canonical order: restoring such a
 * marker by frame alone would land it at the end of its tie group rather than in its old slot,
 * and `removeMarker`'s inverse would no longer deep-equal the document it undid. The engine's own
 * inverse therefore carries the slot it removed the marker from. When present it must itself keep
 * the list frame-ordered, so it cannot be used to break the invariant.
 */
export type AddMarker = {
  type: 'addMarker';
  marker: Marker;
  index?: number;
};

/** Patch a marker's fields. `frame` may only move within the slot its neighbours allow. */
export type SetMarker = {
  type: 'setMarker';
  markerId: string;
  patch: Partial<Marker>;
};

/** Remove a marker by id. */
export type RemoveMarker = {
  type: 'removeMarker';
  markerId: string;
};

// ─────────────────────────────────────────────────────────────────────────────
// The P4 ops (Wave C): placement conveniences, track structure, settings
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Place `item` at the very end of a track: its `recordIn` becomes the last item's `recordOut`
 * (or 0 on an empty track) and only its duration is read from the span it arrives with. Nothing
 * else moves, so the inverse is `lift`.
 */
export type Append = {
  type: 'append';
  trackId: string;
  item: Item;
};

/**
 * Swap the content of an item for another item's content while keeping the addressed item's id
 * **and span** (the P4 "replace" edit: same slot, different source). `item.id`, `recordIn` and
 * `recordOut` on the supplied item are ignored in favour of the existing ones. Inverse: `overwrite`
 * with the original.
 */
export type Replace = {
  type: 'replace';
  itemId: string;
  item: Item;
};

/**
 * Re-span an item to `[recordIn, recordOut)` and, on a `mediaClip`, scale `playbackRate` so the
 * same source range still fills the new span (the four-point "fit to fill" edit). Refuses a span
 * that overlaps a neighbour. Inverse: `overwrite` with the original.
 */
export type FitToFill = {
  type: 'fitToFill';
  itemId: string;
  recordIn: number;
  recordOut: number;
};

/**
 * Place `item` on a track at its own `recordIn` when that span is free, with no index to supply
 * and never a ripple — the "drop it on the top track" edit. A span that touches an existing item
 * is refused. Inverse: `lift`.
 */
export type PlaceOnTop = {
  type: 'placeOnTop';
  trackId: string;
  item: Item;
};

/**
 * Merge an item with the item that follows it on the same track. The follower must start exactly
 * where the item ends and must be an **exact continuation**: identical in every field except id
 * and span, with `sourceIn` advanced by the item's duration where the kind carries one — i.e. the
 * pair a `blade` produces. Anything else is refused, because the inverse is that one `blade` and
 * a blade cannot restore a right half whose fields differed.
 */
export type Join = {
  type: 'join';
  itemId: string;
};

/**
 * Move an item along its own track by `deltaFrames` (non-zero), refusing a negative start or an
 * overlap. Inverse: the opposite nudge.
 */
export type Nudge = {
  type: 'nudge';
  itemId: string;
  deltaFrames: number;
};

/**
 * Insert a whole track at `index` (0..tracks.length). The track's id must be fresh, its items must
 * already satisfy the per-track invariants and carry fresh ids — so `removeTrack`'s inverse can
 * put a populated track back exactly. Inverse: `removeTrack`.
 */
export type AddTrack = {
  type: 'addTrack';
  index: number;
  track: Track;
};

/** Remove a track and everything on it. Inverse: `addTrack` with the whole track at its old index. */
export type RemoveTrack = {
  type: 'removeTrack';
  trackId: string;
};

/** Move a track to another compositing position (`toIndex` must differ). Inverse: move it back. */
export type MoveTrack = {
  type: 'moveTrack';
  trackId: string;
  toIndex: number;
};

/**
 * P7: add, replace or remove ONE colour group. `group === undefined` removes the group with
 * `groupId`; otherwise `group.id` must equal `groupId` and every `clipIds` entry must be a media
 * clip of the document that no OTHER group holds. The inverse is this same op with the previous
 * group (or `undefined` when there was none).
 *
 * `restore` is NOT a field of this op. The T-112 exemption — a group outliving its clips is a legal,
 * dangling state (see `assertColorGroups`), and the undo stack replays a group's restore before the
 * inverse that brings the clip back — is granted by the apply path's `{ inverse }` option, which only
 * an entry's inverse program sets. A fresh edit therefore still refuses a group naming a clip that
 * does not exist, and no wire, proposal or queued op can forge the exemption.
 */
export type SetColorGroup = {
  type: 'setColorGroup';
  groupId: string;
  group: ColorGroup | undefined;
};

/** Every track field {@link SetTrackProperty} may address. See {@link TRACK_PROPERTY_ALLOWLIST}. */
export type TrackPropertyKey = 'name' | 'color' | 'height' | 'locked' | 'enabled' | 'mute' | 'solo' | 'busId' | 'gainDb' | 'pan' | 'eq' | 'dynamics' | 'denoise' | 'leveler';

/**
 * A settable track field's value. `null` is legal only for `color` and `busId`, and so is
 * `undefined`, which **removes** the key — the same convention `setProperty` uses, and what lets
 * the inverse restore a track that never carried the optional key at all.
 */
export type TrackPropertyValue = string | number | boolean | Keyframed<number> | TrackEq | AudioDynamics | AudioDenoise | AudioLeveler | null | undefined;

/**
 * Set one allowlisted track field. The one op a locked track accepts is the one that unlocks it
 * (`key === 'locked'`); every other key on a locked track is refused like any other op.
 */
export type SetTrackProperty = {
  type: 'setTrackProperty';
  trackId: string;
  key: TrackPropertyKey;
  value: TrackPropertyValue;
};

/**
 * Patch the timeline settings (resolution, rate, sample rate, pixel aspect, aspect policy, start
 * timecode). Every key is validated; the inverse is the same op carrying the old values of exactly
 * the keys patched.
 */
export type SetTimelineSettings = {
  type: 'setTimelineSettings';
  patch: Partial<EditTimelineSettings>;
};

/**
 * Set or clear the document's in/out range. `null` clears it to an explicit null; `undefined`
 * removes the key (which is how the inverse restores a document that never had one).
 */
export type SetInOut = {
  type: 'setInOut';
  inOut: EditRange | null | undefined;
};

/**
 * Every edit the engine understands, and the only ops that may appear in an `inverse` program.
 */
// ─────────────────────────────────────────────────────────────────────────────
// P5 ops — keyframes, retime, transitions
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Set (add or replace) one keyframe on one keyframed channel. `target` is a media clip (`gainDb`
 * or a transform channel) or a track (`gainDb`). The channel container is created when absent.
 * `value` must be finite and inside the channel's {@link KEYFRAME_PARAM_RANGE}; `frame` an integer.
 * Inverse: the previous key re-set, `removeKeyframe`, or the previous container restored.
 */
export type SetKeyframe = {
  type: 'setKeyframe';
  target: KeyframeTarget;
  param: KeyframeParam;
  frame: number;
  value: number;
  interp: (typeof INTERP_KINDS)[number];
};

/** Remove the keyframe at `frame`. Refuses a frame with no key. Never deletes the container. Inverse: `setKeyframe`. */
export type RemoveKeyframe = {
  type: 'removeKeyframe';
  target: KeyframeTarget;
  param: KeyframeParam;
  frame: number;
};

/** Move the keyframe at `frame` to `toFrame` (free, integer, different). Inverse: move it back. */
export type MoveKeyframe = {
  type: 'moveKeyframe';
  target: KeyframeTarget;
  param: KeyframeParam;
  frame: number;
  toFrame: number;
};

/**
 * Change a media clip's playback rate AND its `recordOut` together, rippling every following
 * item by the change in duration. Both are explicit because `round(duration × rate)` is not
 * invertible on its own — the inverse is the retime with the old pair. `recordOut` must exceed
 * `recordIn`; the ripple must not run an item before frame 0 (it cannot: items only move right
 * or shrink into free space the op itself created).
 */
export type Retime = {
  type: 'retime';
  itemId: string;
  playbackRate: number;
  recordOut: number;
};

/**
 * Place a transition between a media clip (`afterItemId`) and the media clip that directly
 * follows it (adjacent, no transition yet). The cut at `c = A.recordOut` becomes the transition
 * span `[c - framesBefore, c + framesAfter)`: A is shortened by `framesBefore`, B starts
 * `framesAfter` later with `sourceIn` advanced by `round(framesAfter × rateB)`, and B must own
 * `framesBefore × rateB` frames of source BEFORE its `sourceIn` (the incoming handle — refused as
 * `insufficient_handles` otherwise; A's outgoing handle can only be checked where the asset's
 * duration is known, i.e. by the renderer, which pads with the last frame). The `transition`
 * carries every field but the span. Inverse: `removeTransition` with `cutFrame = c`.
 */
export type AddTransition = {
  type: 'addTransition';
  afterItemId: string;
  transition: Omit<Transition, 'recordIn' | 'recordOut'>;
  framesBefore: number;
  framesAfter: number;
};

/**
 * Remove a transition, restoring a hard cut at `cutFrame` (inside the transition span): the
 * outgoing clip's `recordOut` and the incoming clip's `recordIn` both become `cutFrame`, and the
 * incoming clip's `sourceIn` moves back by `round((T.recordOut - cutFrame) × rateB)`. Inverse:
 * `addTransition` with the split the cut frame implies.
 */
export type RemoveTransition = {
  type: 'removeTransition';
  transitionId: string;
  cutFrame: number;
};

// ─────────────────────────────────────────────────────────────────────────────
// P8 ops — nested sequences and submix buses
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Add, replace or remove ONE nested sequence. `sequence === undefined` removes `sequenceId`
 * (refused while a compound clip plays it); otherwise `sequence.id` must equal `sequenceId`, its
 * tracks must satisfy the per-track invariants, its item ids must be unique document-wide, and
 * the nesting it creates must stay acyclic and at most {@link MAX_NESTING_DEPTH} deep. Inverse:
 * this op with the previous sequence (or `undefined`).
 */
export type SetSequence = {
  type: 'setSequence';
  sequenceId: string;
  sequence: EditSequence | undefined;
  /** Where a NEW sequence is spliced in (default: appended) — what lets removal's inverse restore the slot. */
  index?: number;
};

/**
 * Add, replace or remove ONE submix bus. `bus === undefined` removes it (tracks routed to it then
 * feed the master); otherwise `bus.id` must equal `busId`, every processing value must be in range
 * and the routing must stay acyclic and at most {@link MAX_NESTING_DEPTH} deep. Inverse: this op
 * with the previous bus (or `undefined`).
 */
export type SetBus = {
  type: 'setBus';
  busId: string;
  bus: AudioBus | undefined;
  /** Where a NEW bus is spliced in (default: appended) — what lets removal's inverse restore the slot. */
  index?: number;
};

export type EditOp =
  | Insert
  | Overwrite
  | Lift
  | Extract
  | Trim
  | Blade
  | DeleteGap
  | CloseGaps
  | Move
  | SetProperty
  | AddMarker
  | SetMarker
  | RemoveMarker
  | Append
  | Replace
  | FitToFill
  | PlaceOnTop
  | Join
  | Nudge
  | AddTrack
  | RemoveTrack
  | MoveTrack
  | SetTrackProperty
  | SetTimelineSettings
  | SetInOut
  | SetKeyframe
  | RemoveKeyframe
  | MoveKeyframe
  | Retime
  | AddTransition
  | RemoveTransition
  | SetColorGroup
  | SetSequence
  | SetBus;

/**
 * The closed `setTrackProperty` allowlist with each key's value shape. `name` must stay a string
 * (a track always has a name), `height` a positive number, the four flags booleans, and the two
 * nullable strings may be cleared to `null`.
 */
export const TRACK_PROPERTY_ALLOWLIST: Readonly<
  Record<TrackPropertyKey, 'string' | 'nullableString' | 'positiveNumber' | 'boolean' | 'keyframedNumber' | 'pan' | 'eq' | 'dynamics' | 'denoise' | 'leveler'>
> = {
  name: 'string',
  color: 'nullableString',
  height: 'positiveNumber',
  locked: 'boolean',
  enabled: 'boolean',
  mute: 'boolean',
  solo: 'boolean',
  busId: 'nullableString',
  gainDb: 'keyframedNumber',
  pan: 'pan',
  eq: 'eq',
  dynamics: 'dynamics',
  denoise: 'denoise',
  leveler: 'leveler',
};

/** The settings keys {@link SetTimelineSettings} may patch — the whole record, nothing outside it. */
export const TIMELINE_SETTINGS_KEYS: readonly (keyof EditTimelineSettings)[] = [
  'width',
  'height',
  'rate',
  'sampleRate',
  'pixelAspect',
  'aspectPolicy',
  'startTimecodeFrames',
];

/** The aspect policies the schema spells. */
export const ASPECT_POLICIES: readonly EditTimelineSettings['aspectPolicy'][] = ['fit', 'fill', 'stretch'];

/** The discriminator of every op — handy for exhaustive switches and for tests. */
export type EditOpType = EditOp['type'];

/**
 * The closed `setProperty` allowlist, by item kind.
 *
 * `name` is on every kind (`ItemBase`); `enabled` is on every kind that declares it (all of them
 * since P8). Everything else is per-kind: title formatting on `titleClip`, caption text/language on
 * `caption`, the media-only `playbackRate`/`gainDb`/`channelMap` on `mediaClip`, the sequence a
 * `compoundClip` plays, and a `multicamClip`'s angles, active angle and angle cuts (re-checked as a
 * whole after the write, so a cut can never land outside the clip or name an unknown angle).
 */
export const SET_PROPERTY_ALLOWLIST: Readonly<Record<Item['type'], readonly PropertyKey[]>> = {
  mediaClip: ['name', 'enabled', 'playbackRate', 'sourceIn', 'gainDb', 'transform', 'crop', 'reverse', 'sourceRotation', 'hold', 'fadeInFrames', 'fadeOutFrames', 'assetRef', 'grade', 'mask', 'keyer', 'effects', 'regionEffects', 'stabilize', 'reframe', 'retimeQuality', 'cornerPin', 'channelMap', 'linkId', 'inputProfile', 'blendMode'],
  titleClip: ['name', 'enabled', 'text', 'fontSize', 'fontFamily', 'color', 'template', 'position', 'transform'],
  generatorClip: ['name', 'enabled', 'generatorType', 'generatorConfig'],
  insertClip: ['name', 'enabled', 'matte', 'quad'],
  caption: ['name', 'enabled', 'text', 'language', 'look', 'position'],
  transition: ['name', 'enabled', 'transitionType', 'easing'],
  compoundClip: ['name', 'enabled', 'sourceIn', 'sequenceId'],
  multicamClip: ['name', 'enabled', 'sourceIn', 'gainDb', 'activeAngleId', 'cuts', 'angles', 'audioSource'],
  adjustmentClip: ['name', 'enabled', 'grade', 'effects'],
};

/**
 * The allowlisted keys a `setProperty` may **not** clear.
 *
 * {@link PropertyValue} documents `undefined` as "remove the key", and that is load-bearing: it is
 * what lets a `setProperty` that *set* an absent property be undone by the same op, without a
 * dedicated delete op in the catalogue. But the document invariants require some of the very keys
 * this allowlist addresses — `assertItemShape` refuses an item whose `name` is not a string, and
 * `name` is on **every** kind's allowlist — so honouring `undefined` for those keys would hand back
 * a document the engine may no longer edit, contradicting the "an op cannot *introduce* a
 * violation" claim `./apply` makes. `applySetProperty` therefore **refuses** such a clear with an
 * `EditEngineError`, exactly the way `insert` refuses a partial overlap rather than performing
 * something it cannot honestly undo. Deleting the field to make the conflict go away, or loosening
 * the invariant to admit a nameless item, would each trade a loud refusal for a silently corrupt
 * document.
 *
 * `name`, `transitionType` and `generatorType` are the required keys: every other allowlisted key
 * is optional for the invariants, so clearing it stays supported.
 */
export const REQUIRED_PROPERTY_KEYS: ReadonlySet<PropertyKey> = new Set<PropertyKey>(['name', 'transitionType', 'generatorType', 'assetRef', 'sequenceId', 'activeAngleId', 'angles']);

/**
 * Item kinds whose `sourceIn` is a real source offset — the only kinds `trim`'s `slip` mode can
 * act on, and the only ones whose `sourceIn` a `roll`/`slide` moves when their head moves.
 */
export const SOURCE_OFFSET_ITEM_TYPES: readonly Item['type'][] = ['mediaClip', 'compoundClip', 'multicamClip'];
