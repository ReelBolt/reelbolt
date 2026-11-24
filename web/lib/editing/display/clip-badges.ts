/**
 * The adjustment badges a clip shows on the timeline: one small mark per kind of adjustment the clip carries
 * (motion, crop, speed, colour, mask, keyer, effects, ...). Pure: the painter, the hit test and the menus all read
 * this one model, so what is drawn, what a click opens and what "Turn off" / "Remove" do can never disagree.
 *
 * Every action is built from the existing op catalogue (`setProperty`, `retime`); there is no new op. A key is
 * cleared with `value: undefined`, which is how the engine removes an optional field and what its inverse restores.
 * Fades are deliberately not badges (they are drawn as ramps in the clip corners).
 */

import { planSpeed } from '../clip-depth/units';
import { effectiveGrade } from '../color/grade';
import type { ComposeEntryId } from '../compose/entries';
import type { EditOp } from '../engine';
import type { ParityClass, ParityFeature } from '../preview/parity';
import type { ClipTransform, EditTimeline, GradeStack, Item, MediaClip } from '../types';

export type ClipBadgeKind =
  | 'motion'
  | 'follow'
  | 'crop'
  | 'speed'
  | 'colour'
  | 'camera'
  | 'mask'
  | 'regionEffects'
  | 'keyer'
  | 'effects'
  | 'stabilize'
  | 'reframe'
  | 'cornerPin';

/** Left-to-right order on the clip. */
export const CLIP_BADGE_ORDER: readonly ClipBadgeKind[] = [
  'motion', 'follow', 'crop', 'speed', 'colour', 'camera', 'mask', 'regionEffects', 'keyer', 'effects', 'stabilize', 'reframe', 'cornerPin',
];

/** The parity features each badge answers for (the join key with `ItemParity.findings`). */
export const BADGE_FEATURES: Record<ClipBadgeKind, readonly ParityFeature[]> = {
  motion: ['transform'],
  follow: ['transform'],
  crop: ['crop'],
  speed: ['speed', 'retime'],
  colour: ['curvesLut', 'gradeNodes', 'secondaries', 'hdr'],
  camera: ['inputProfile'],
  mask: ['shapeMask', 'segmentationMask'],
  regionEffects: ['regionEffects'],
  keyer: ['keyer'],
  effects: ['effects'],
  stabilize: ['stabilize'],
  reframe: ['reframe'],
  cornerPin: ['cornerPin'],
};

/**
 * Where a badge opens: the workspace, the Compose list entry when that workspace needs one, and the element id of the
 * section (the id is set on the panel's own section; `revealElement` scrolls to it and focuses it).
 */
export const BADGE_SECTION: Record<ClipBadgeKind, { workspace: 'edit' | 'compose' | 'color'; composeEntry?: ComposeEntryId; anchor: string }> = {
  motion: { workspace: 'edit', anchor: 'inspector-section-transform' },
  follow: { workspace: 'edit', anchor: 'follow-section' },
  crop: { workspace: 'edit', anchor: 'inspector-section-crop' },
  speed: { workspace: 'edit', anchor: 'inspector-speed' },
  colour: { workspace: 'color', anchor: 'inspector-section-colour' },
  camera: { workspace: 'color', anchor: 'inspector-section-camera' },
  mask: { workspace: 'compose', composeEntry: 'mask', anchor: 'inspector-section-mask' },
  regionEffects: { workspace: 'edit', anchor: 'object-effects-section' },
  keyer: { workspace: 'compose', composeEntry: 'keyer', anchor: 'inspector-section-keyer' },
  effects: { workspace: 'compose', composeEntry: 'effects', anchor: 'inspector-section-effects' },
  stabilize: { workspace: 'compose', composeEntry: 'motion', anchor: 'inspector-section-stabilize' },
  reframe: { workspace: 'compose', composeEntry: 'reframe', anchor: 'inspector-section-reframe' },
  cornerPin: { workspace: 'compose', composeEntry: 'pin', anchor: 'inspector-section-corner-pin' },
};

/**
 * The phone clip sheet's section that has the badge's controls, or null when the sheet has none (colour, camera,
 * mask, keyer, effects, stabilise, reframe, corner pin and crop are desktop and tablet tools): the phone then shows the
 * "needs a larger screen" note, never a dead end.
 */
export const BADGE_PHONE_SECTION: Record<ClipBadgeKind, 'picture' | 'speed' | 'objects' | null> = {
  motion: 'picture', follow: 'objects', crop: null, speed: 'speed', colour: null, camera: null, mask: null, regionEffects: 'objects',
  keyer: null, effects: null, stabilize: null, reframe: null, cornerPin: null,
};

export interface ClipBadge {
  kind: ClipBadgeKind;
  /** The catalogue key of the label, under `editor-timeline.badges`. */
  labelKey: string;
  /** False = the adjustment is present but switched off (painted hollow). */
  enabled: boolean;
  /** The ops that switch it off (or back on); null when the adjustment has no on/off state. */
  toggle: EditOp[] | null;
  /** The ops that take the adjustment off the clip; empty when it cannot be removed from here (e.g. a colour group). */
  remove: EditOp[];
  /** Overrides the menu words for Remove (`remove` key of the catalogue): speed says "Reset to normal speed". */
  removeKey?: string;
  /** A one-line note under the Remove entry (a catalogue key). */
  removeNoteKey?: string;
  features: readonly ParityFeature[];
}

/** What the painter needs per badge. */
export interface ClipBadgePaint {
  kind: ClipBadgeKind;
  enabled: boolean;
  parity: ParityClass;
}

const RANK: Record<ParityClass, number> = { faithful: 0, approximate: 1, renderRequired: 2 };

const clear = (itemId: string, key: Extract<EditOp, { type: 'setProperty' }>['key']): EditOp => ({ type: 'setProperty', itemId, key, value: undefined });

/** The channels that move or reshape the picture. Opacity is not "motion": it has its own control and no badge. */
const MOTION_CHANNELS = ['positionX', 'positionY', 'scale', 'rotation'] as const;
const hasMotion = (t: ClipTransform | null | undefined): boolean => !!t && MOTION_CHANNELS.some((k) => t[k] !== null && t[k] !== undefined);
/** The transform without its motion channels (opacity stays); undefined when nothing is left. */
const withoutMotion = (t: ClipTransform): ClipTransform | undefined => (t.opacity !== null && t.opacity !== undefined ? { opacity: t.opacity } : undefined);
const withoutFollowed = (t: ClipTransform): ClipTransform | undefined => {
  const rest: ClipTransform = { ...t };
  delete rest.positionX;
  delete rest.positionY;
  delete rest.scale;
  return Object.values(rest).some((c) => c !== null && c !== undefined) ? rest : undefined;
};
const keysOf = (c: unknown): { frame: number; interp?: string }[] => {
  const keys = (c as { keys?: unknown } | null | undefined)?.keys;
  return Array.isArray(keys) ? (keys as { frame: number; interp?: string }[]) : [];
};

/**
 * True for the transform the "follow a subject" tool writes: position X, position Y and scale all keyed on the same
 * frames (at least two), every key linear (`lib/editing/follow/map.ts` buildFollowTransform).
 */
export function looksFollowed(t: ClipTransform | null | undefined): boolean {
  if (!t || !t.positionX || !t.positionY || !t.scale) return false;
  const x = keysOf(t.positionX);
  const y = keysOf(t.positionY);
  const s = keysOf(t.scale);
  if (x.length < 2 || x.length !== y.length || x.length !== s.length) return false;
  return x.every((k, i) => k.frame === y[i]!.frame && k.frame === s[i]!.frame && k.interp === 'linear' && y[i]!.interp === 'linear' && s[i]!.interp === 'linear');
}

/**
 * Speed. Remove is exactly the inspector's "Reset speed": ONE `retime` back to 1x that keeps the same footage (the clip
 * gets longer or shorter and the clips after it on this track move), with the fades shrunk first when they would no longer
 * fit, as the inspector does. A reverse, freeze or optical-flow setting on the clip goes with it.
 */
function speedBadge(item: MediaClip): ClipBadge | null {
  const rate = item.playbackRate;
  const retimed = typeof rate === 'number' && rate !== 1;
  if (!retimed && item.reverse !== true && item.hold !== true) return null;
  const ops: EditOp[] = [];
  if (retimed && typeof rate === 'number' && rate > 0) {
    const plan = planSpeed(1, item);
    if (plan.ok) {
      if (plan.value.fadesShrunk) {
        ops.push({ type: 'setProperty', itemId: item.id, key: 'fadeInFrames', value: plan.value.fadeInFrames > 0 ? plan.value.fadeInFrames : undefined });
        ops.push({ type: 'setProperty', itemId: item.id, key: 'fadeOutFrames', value: plan.value.fadeOutFrames > 0 ? plan.value.fadeOutFrames : undefined });
      }
      ops.push({ type: 'retime', itemId: item.id, playbackRate: 1, recordOut: item.recordIn + plan.value.durationFrames });
    }
  }
  if (item.reverse === true) ops.push(clear(item.id, 'reverse'));
  if (item.hold === true) ops.push(clear(item.id, 'hold'));
  if (item.retimeQuality !== undefined) ops.push(clear(item.id, 'retimeQuality'));
  return {
    kind: 'speed', labelKey: 'speed', enabled: true, toggle: null, remove: ops, features: BADGE_FEATURES.speed,
    ...(retimed ? { removeKey: 'resetSpeed', removeNoteKey: 'resetSpeedNote' } : {}),
  };
}

/** Colour is the clip's grade (its own stack and its colour group's). The camera conversion is its own badge. */
function colourBadge(item: MediaClip, doc: EditTimeline): ClipBadge | null {
  const stacks: GradeStack[] = effectiveGrade(doc, item);
  if (!stacks.some((s) => (s.nodes?.length ?? 0) > 0)) return null;
  const enabled = stacks.some((s) => s.enabled !== false && (s.nodes?.length ?? 0) > 0);
  const own = item.grade;
  const hasOwn = !!own && (own.nodes?.length ?? 0) > 0;
  // On/off is the clip's OWN stack; a colour group's stack is switched in the colour page.
  const toggle: EditOp[] | null = hasOwn ? [{ type: 'setProperty', itemId: item.id, key: 'grade', value: { ...own, enabled: own.enabled === false } }] : null;
  const remove: EditOp[] = own ? [clear(item.id, 'grade')] : [];
  return { kind: 'colour', labelKey: 'colour', enabled: hasOwn ? own.enabled !== false : enabled, toggle, remove, features: BADGE_FEATURES.colour };
}

/** The badges of one item, in `CLIP_BADGE_ORDER`. Only media clips carry adjustments; every other item has none. */
export function clipBadges(item: Item, doc: EditTimeline): ClipBadge[] {
  if (item.type !== 'mediaClip') return [];
  const out: ClipBadge[] = [];
  const id = item.id;

  if (hasMotion(item.transform)) {
    const followed = looksFollowed(item.transform);
    const rest = followed ? withoutFollowed(item.transform as ClipTransform) : withoutMotion(item.transform as ClipTransform);
    // Remove never touches opacity (and a follow removal keeps rotation too, like the follow panel's own remove).
    out.push({ kind: followed ? 'follow' : 'motion', labelKey: followed ? 'follow' : 'motion', enabled: true, toggle: null, remove: [{ type: 'setProperty', itemId: id, key: 'transform', value: rest }], features: BADGE_FEATURES.motion });
  }
  const crop = item.crop;
  if (crop && ((crop.left ?? 0) > 0 || (crop.top ?? 0) > 0 || (crop.right ?? 0) > 0 || (crop.bottom ?? 0) > 0)) {
    out.push({ kind: 'crop', labelKey: 'crop', enabled: true, toggle: null, remove: [clear(id, 'crop')], features: BADGE_FEATURES.crop });
  }
  const speed = speedBadge(item);
  if (speed) out.push(speed);
  const colour = colourBadge(item, doc);
  if (colour) out.push(colour);
  if (item.inputProfile) out.push({ kind: 'camera', labelKey: 'camera', enabled: true, toggle: null, remove: [clear(id, 'inputProfile')], features: BADGE_FEATURES.camera });
  if (item.mask) out.push({ kind: 'mask', labelKey: 'mask', enabled: true, toggle: null, remove: [clear(id, 'mask')], features: BADGE_FEATURES.mask });
  const region = item.regionEffects;
  if (region && region.length > 0) {
    const on = region.some((e) => e.enabled);
    out.push({
      kind: 'regionEffects', labelKey: 'regionEffects', enabled: on,
      toggle: [{ type: 'setProperty', itemId: id, key: 'regionEffects', value: region.map((e) => ({ ...e, enabled: !on })) }],
      remove: [clear(id, 'regionEffects')], features: BADGE_FEATURES.regionEffects,
    });
  }
  if (item.keyer) out.push({ kind: 'keyer', labelKey: 'keyer', enabled: true, toggle: null, remove: [clear(id, 'keyer')], features: BADGE_FEATURES.keyer });
  const fx = item.effects;
  if (fx && fx.length > 0) {
    const on = fx.some((e) => e.enabled !== false);
    out.push({
      kind: 'effects', labelKey: 'effects', enabled: on,
      toggle: [{ type: 'setProperty', itemId: id, key: 'effects', value: fx.map((e) => ({ ...e, enabled: !on })) }],
      remove: [clear(id, 'effects')], features: BADGE_FEATURES.effects,
    });
  }
  if (item.stabilize) out.push({ kind: 'stabilize', labelKey: 'stabilize', enabled: true, toggle: null, remove: [clear(id, 'stabilize')], features: BADGE_FEATURES.stabilize });
  if (item.reframe) out.push({ kind: 'reframe', labelKey: 'reframe', enabled: true, toggle: null, remove: [clear(id, 'reframe')], features: BADGE_FEATURES.reframe });
  if (item.cornerPin) out.push({ kind: 'cornerPin', labelKey: 'cornerPin', enabled: true, toggle: null, remove: [clear(id, 'cornerPin')], features: BADGE_FEATURES.cornerPin });

  return out.sort((a, b) => CLIP_BADGE_ORDER.indexOf(a.kind) - CLIP_BADGE_ORDER.indexOf(b.kind));
}

/** The worst parity among the findings a badge answers for (and the reasons, worst first). */
export function badgeParity(badge: Pick<ClipBadge, 'features'>, findings: readonly { feature: ParityFeature; parity: ParityClass; reason: string }[]): { parity: ParityClass; reasons: string[] } {
  const mine = findings.filter((f) => badge.features.includes(f.feature) && f.parity !== 'faithful').sort((a, b) => RANK[b.parity] - RANK[a.parity]);
  return { parity: mine[0]?.parity ?? 'faithful', reasons: mine.map((f) => f.reason) };
}

/** Badges with their parity, ready for the painter. */
export function clipBadgePaints(item: Item, doc: EditTimeline, findings: readonly { feature: ParityFeature; parity: ParityClass; reason: string }[]): ClipBadgePaint[] {
  return clipBadges(item, doc).map((b) => ({ kind: b.kind, enabled: b.enabled, parity: badgeParity(b, findings).parity }));
}

// ── layout (shared by the painter and the hit test) ──────────────────────────────

export const BADGE_SIZE = 14;
export const BADGE_GAP = 2;
export const BADGE_PAD = 3;
/** The "+N" badge is wider: its text is 12 px. */
export const BADGE_MORE_WIDTH = 24;
/** The label band above the badge row (matches `LABEL_BAND` in clipPaint). */
const LABEL_BAND_HEIGHT = 18;
/** The least body height that shows anything: the label band plus the "+N" chip. A collapsed track (20 px, body 12) shows nothing. */
const MIN_ROW_BODY = 20;

export interface BadgeRect {
  kind: ClipBadgeKind | 'more';
  x: number;
  y: number;
  w: number;
  h: number;
  /** For 'more': how many badges it stands for. */
  count?: number;
}

/**
 * Where the badge row sits on a clip: right-aligned under the label band. Too narrow for all of them: a single "+N"
 * badge stands for the lot (one click opens the clip's adjustment list). No room for even that, or a body too short
 * for a row (a collapsed track): nothing.
 */
export function layoutBadges(kinds: readonly ClipBadgeKind[], clipX0: number, clipW: number, bodyY: number, bodyH: number): BadgeRect[] {
  if (kinds.length === 0 || bodyH < MIN_ROW_BODY) return [];
  const right = clipX0 + clipW - BADGE_PAD;
  const avail = clipW - 2 * BADGE_PAD;
  // A short body (the small track preset) has no room under the label band: one "+N" chip sits in the band's right end.
  if (bodyH < LABEL_BAND_HEIGHT + BADGE_SIZE + 4) {
    if (avail < BADGE_MORE_WIDTH) return [];
    return [{ kind: 'more', x: right - BADGE_MORE_WIDTH, y: bodyY + 2, w: BADGE_MORE_WIDTH, h: BADGE_SIZE, count: kinds.length }];
  }
  const y = bodyY + LABEL_BAND_HEIGHT + 2;
  const needed = kinds.length * BADGE_SIZE + (kinds.length - 1) * BADGE_GAP;
  if (needed <= avail) {
    return kinds.map((kind, i) => ({ kind, x: right - needed + i * (BADGE_SIZE + BADGE_GAP), y, w: BADGE_SIZE, h: BADGE_SIZE }));
  }
  if (avail < BADGE_MORE_WIDTH) return [];
  return [{ kind: 'more', x: right - BADGE_MORE_WIDTH, y, w: BADGE_MORE_WIDTH, h: BADGE_SIZE, count: kinds.length }];
}

/** The badge under a point, or null. */
export function badgeAt(rects: readonly BadgeRect[], x: number, y: number): BadgeRect | null {
  return rects.find((r) => x >= r.x && x < r.x + r.w && y >= r.y && y < r.y + r.h) ?? null;
}

/** Tiny vector glyphs on a 10 x 10 grid, as polylines (no emoji, crisp at any scale). */
export const BADGE_GLYPHS: Record<ClipBadgeKind, readonly (readonly number[])[]> = {
  motion: [[1, 8, 4, 3, 6, 6, 9, 1]],
  follow: [[1, 5, 9, 5], [5, 1, 5, 9], [3, 3, 7, 3, 7, 7, 3, 7, 3, 3]],
  crop: [[3, 0, 3, 7, 10, 7], [0, 3, 7, 3, 7, 10]],
  speed: [[1, 1, 5, 5, 1, 9], [5, 1, 9, 5, 5, 9]],
  colour: [[5, 0, 9, 5, 5, 10, 1, 5, 5, 0]],
  camera: [[1, 3, 9, 3, 9, 8, 1, 8, 1, 3], [4, 3, 5, 1, 7, 1, 8, 3]],
  mask: [[1, 1, 9, 1, 9, 9, 1, 9, 1, 1], [3, 3, 7, 3, 7, 7, 3, 7, 3, 3]],
  regionEffects: [[1, 3, 9, 3], [1, 5, 9, 5], [1, 7, 9, 7], [5, 0, 5, 10]],
  keyer: [[1, 1, 9, 9], [9, 1, 1, 9]],
  effects: [[5, 0, 6, 4, 10, 5, 6, 6, 5, 10, 4, 6, 0, 5, 4, 4, 5, 0]],
  stabilize: [[0, 5, 3, 3, 6, 7, 10, 5]],
  reframe: [[0, 3, 0, 0, 3, 0], [7, 0, 10, 0, 10, 3], [10, 7, 10, 10, 7, 10], [3, 10, 0, 10, 0, 7]],
  cornerPin: [[1, 9, 5, 1, 9, 9, 1, 9]],
};
