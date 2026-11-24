/**
 * "Act on this event": the one-click choices on an Events lane tick, each expressed with the EXISTING document
 * ops only (the catalogue stays at 34): `setKeyframe` on the clip's transform channels (zoom, pan, shake),
 * `blade` + `retime` (speed ramp, via the link-aware builders), `blade` (cut), `placeOnTop` / `addTrack` (title,
 * flash). Each plan is ONE program the caller dispatches as one undo step.
 *
 * Every time comes from the event's own record frame (resolved from the analysis `frame`/`timeSec` through the
 * clip's source-in and rate, see `events-data.ts`), never from text a model wrote. No random numbers: the shake
 * is a seeded noise set, so the same event always writes the same keys.
 *
 * Not offered, because no existing op can express it: `glow` (a glow needs a region effect with a time window,
 * which the document does not carry). It is absent from the menu, not disabled.
 */

import { applyAll, type EditOp, type KeyframeParam } from '../engine';
import { newGuid } from '../ids';
import { linkedOpsForBlade, linkedOpsForRetime } from '../link/edit';
import type { LinkOptions } from '../link/groups';
import { addAtPlayhead } from '../structure/add-at-playhead';
import type { EditTimeline, GeneratorClip, MediaClip, Track } from '../types';
import { transformAt } from '../viewer-transform/draft';
import type { LaneEvent } from './events-data';

export type EventActionId = 'addSound' | 'addTitle' | 'zoom' | 'pan' | 'shake' | 'flash' | 'speedRamp' | 'cut';

/** The menu order. */
export const EVENT_ACTION_ORDER: readonly EventActionId[] = ['addSound', 'addTitle', 'zoom', 'pan', 'shake', 'flash', 'speedRamp', 'cut'];

/** Actions the design lists but the document cannot express yet; they are hidden, never shown disabled. */
export const HIDDEN_EVENT_ACTIONS: readonly string[] = ['glow'];

export type EventActionPlan =
  | { kind: 'ops'; id: EventActionId; ops: EditOp[]; /** The item to select afterwards, when the action made one. */ select: string | null }
  | { kind: 'sound'; id: 'addSound'; /** Where the playhead goes before the sound picker opens. */ frame: number }
  | { kind: 'none'; id: EventActionId; reason: 'noClip' | 'locked' | 'tooShort' | 'refused' };

export interface EventActionContext {
  doc: EditTimeline;
  event: LaneEvent;
  newId: () => string;
  isLocked?: (trackId: string) => boolean;
}

const fpsOf = (doc: EditTimeline): number => (doc.settings.rate.den > 0 && doc.settings.rate.num > 0 ? doc.settings.rate.num / doc.settings.rate.den : 30);
const secFrames = (fps: number, seconds: number): number => Math.max(1, Math.round(fps * seconds));

/** The clip the event plays through, when it is still on the timeline and still covers the event's frame. */
export function clipOfEvent(doc: EditTimeline, event: LaneEvent): { clip: MediaClip; track: Track } | null {
  for (const track of doc.tracks) {
    for (const item of track.items) {
      if (item.id === event.clipId && item.type === 'mediaClip' && event.frame >= item.recordIn && event.frame < item.recordOut) return { clip: item, track };
    }
  }
  return null;
}

/** The actions that apply to this event right now, in menu order. Read-only documents get none that edit. */
export function availableEventActions(doc: EditTimeline, event: LaneEvent, options: { readOnly: boolean; isLocked?: (trackId: string) => boolean; canOpenSounds: boolean }): EventActionId[] {
  if (options.readOnly) return [];
  const found = clipOfEvent(doc, event);
  const locked = found ? (options.isLocked?.(found.track.id) ?? found.track.locked) : false;
  return EVENT_ACTION_ORDER.filter((id) => {
    if (id === 'addSound') return options.canOpenSounds;
    if (id === 'addTitle' || id === 'flash') return true;
    if (!found || locked) return false;
    if (id === 'cut') return event.frame > found.clip.recordIn && event.frame < found.clip.recordOut;
    if (id === 'speedRamp') return found.clip.hold !== true && found.clip.recordOut - found.clip.recordIn >= 4;
    return found.clip.recordOut - found.clip.recordIn >= 3;
  });
}

// ── keyframe helpers ────────────────────────────────────────────────────────────────────────────────

interface Key { frame: number; value: number; interp: 'linear' | 'easeOutCubic' | 'easeInOutQuad' | 'easeInOutCubic' }

/** Keys clipped to the clip's frames, strictly increasing: a later key at the same frame wins, one past the end is dropped. */
function tidy(keys: readonly Key[], clip: MediaClip): Key[] {
  const last = clip.recordOut - 1;
  const byFrame = new Map<number, Key>();
  for (const k of keys) {
    const frame = Math.max(clip.recordIn, Math.min(last, Math.round(k.frame)));
    byFrame.set(frame, { ...k, frame });
  }
  return [...byFrame.values()].sort((a, b) => a.frame - b.frame);
}

const keyOps = (clip: MediaClip, param: KeyframeParam, keys: readonly Key[]): EditOp[] =>
  keys.map((k) => ({ type: 'setKeyframe', target: { itemId: clip.id }, param, frame: k.frame, value: k.value, interp: k.interp }));

const clampTo = (v: number, lo: number, hi: number): number => Math.min(hi, Math.max(lo, v));
const round4 = (v: number): number => Math.round(v * 10000) / 10000;

/** Centres the event's region (picture fractions) at zoom `scale`, kept inside the picture so no edge shows. */
function centreOn(region: LaneEvent['region'], scale: number, axis: 'x' | 'y', fallback: number): number {
  if (!region) return fallback;
  const c = axis === 'x' ? region.x + region.w / 2 : region.y + region.h / 2;
  const limit = Math.max(0, (scale - 1) / 2);
  return round4(clampTo(-(c - 0.5) * scale, -limit, limit));
}

/** A small deterministic generator, seeded by the event (the shake never uses `Math.random`). */
export function seededNoise(seed: string): () => number {
  let h = 1779033703 ^ seed.length;
  for (let i = 0; i < seed.length; i++) {
    h = Math.imul(h ^ seed.charCodeAt(i), 3432918353);
    h = (h << 13) | (h >>> 19);
  }
  let a = h >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function zoomPlan(ctx: EventActionContext, clip: MediaClip, fps: number): EditOp[] {
  const at = ctx.event.frame;
  const cur = transformAt(clip, at);
  const lead = secFrames(fps, 0.1);
  const hold = secFrames(fps, 0.8);
  const settle = secFrames(fps, 0.4);
  const target = round4(Math.min(8, cur.scale * 1.25));
  const px = centreOn(ctx.event.region, target, 'x', cur.positionX);
  const py = centreOn(ctx.event.region, target, 'y', cur.positionY);
  const span = (param: 'scale' | 'positionX' | 'positionY', from: number, to: number): EditOp[] =>
    keyOps(clip, param, tidy([
      { frame: at - lead, value: from, interp: 'linear' },
      { frame: at, value: to, interp: 'easeOutCubic' },
      { frame: at + hold, value: to, interp: 'easeInOutCubic' },
      { frame: at + hold + settle, value: from, interp: 'linear' },
    ], clip));
  return [...span('scale', cur.scale, target), ...(ctx.event.region ? [...span('positionX', cur.positionX, px), ...span('positionY', cur.positionY, py)] : [])];
}

function panPlan(ctx: EventActionContext, clip: MediaClip, fps: number): EditOp[] {
  const at = ctx.event.frame;
  const cur = transformAt(clip, at);
  const travel = secFrames(fps, 1);
  const scale = round4(Math.max(cur.scale, 1.2));
  const limit = (scale - 1) / 2;
  // Towards the object when the event has a region; else right to left, the way a camera usually drifts.
  const px = ctx.event.region ? centreOn(ctx.event.region, scale, 'x', cur.positionX) : round4(-limit * 0.8);
  const py = ctx.event.region ? centreOn(ctx.event.region, scale, 'y', cur.positionY) : cur.positionY;
  const move = (param: 'scale' | 'positionX' | 'positionY', from: number, to: number): EditOp[] =>
    from === to ? [] : keyOps(clip, param, tidy([{ frame: at, value: from, interp: 'easeInOutQuad' }, { frame: at + travel, value: to, interp: 'linear' }], clip));
  return [...move('scale', cur.scale, scale), ...move('positionX', cur.positionX, px), ...move('positionY', cur.positionY, py)];
}

function shakePlan(ctx: EventActionContext, clip: MediaClip, fps: number): EditOp[] {
  const at = ctx.event.frame;
  const cur = transformAt(clip, at);
  const noise = seededNoise(ctx.event.key);
  const step = Math.max(1, Math.round(fps / 15));
  const count = 8;
  const amp = 0.012 + 0.018 * ctx.event.strength;
  const headroom = round4(Math.max(cur.scale, 1 + 2 * (amp + 0.01)));
  const wobble = (base: number): Key[] => {
    const keys: Key[] = [];
    for (let i = 0; i < count; i++) {
      const decay = 1 - i / count;
      const sign = i % 2 === 0 ? 1 : -1;
      keys.push({ frame: at + i * step, value: round4(clampTo(base + sign * amp * decay * (0.6 + 0.4 * noise()), -2, 2)), interp: 'linear' });
    }
    keys.push({ frame: at + count * step, value: base, interp: 'linear' });
    return keys;
  };
  return [
    ...keyOps(clip, 'scale', tidy([{ frame: at, value: headroom, interp: 'linear' }, { frame: at + count * step, value: headroom, interp: 'linear' }, { frame: at + count * step + 1, value: cur.scale, interp: 'linear' }], clip)),
    ...keyOps(clip, 'positionX', tidy(wobble(cur.positionX), clip)),
    ...keyOps(clip, 'positionY', tidy(wobble(cur.positionY), clip)),
  ];
}

/** Puts `item` on the topmost free unlocked video track over its span, else on a new video track above (the same rule as "Add title"). */
function placeOnTopOps(doc: EditTimeline, item: GeneratorClip, newId: () => string): EditOp[] {
  const free = (t: Track) => t.items.every((i) => i.recordOut <= item.recordIn || i.recordIn >= item.recordOut);
  const videos = doc.tracks.map((t, i) => ({ t, i })).filter(({ t }) => t.kind === 'video');
  for (let k = videos.length - 1; k >= 0; k--) {
    const { t } = videos[k];
    if (!t.locked && free(t)) return [{ type: 'placeOnTop', trackId: t.id, item }];
  }
  const index = videos.length > 0 ? videos[videos.length - 1].i + 1 : 0;
  return [{ type: 'addTrack', index, track: { id: newId(), kind: 'video', name: `V${videos.length + 1}`, height: 64, locked: false, enabled: true, mute: false, solo: false, items: [item] } }];
}

function flashPlan(ctx: EventActionContext, fps: number): { ops: EditOp[]; select: string } {
  const id = ctx.newId();
  const item: GeneratorClip = {
    id, type: 'generatorClip', name: 'Flash', recordIn: ctx.event.frame, recordOut: ctx.event.frame + secFrames(fps, 0.1),
    generatorType: 'color', generatorConfig: { color: '#ffffff' }, enabled: true,
  };
  return { ops: placeOnTopOps(ctx.doc, item, ctx.newId), select: id };
}

function speedRampPlan(ctx: EventActionContext, clip: MediaClip, fps: number): EditOp[] | null {
  const link: LinkOptions | undefined = ctx.isLocked ? { isLocked: ctx.isLocked } : undefined;
  const a = ctx.event.frame;
  const b = Math.min(clip.recordOut, a + secFrames(fps, 0.5));
  if (b - a < 2) return null;
  const ops: EditOp[] = [];
  let doc = ctx.doc;
  let middle = clip.id;
  if (a > clip.recordIn) {
    const cut = linkedOpsForBlade(doc, clip.id, a, ctx.newId(), link);
    ops.push(...cut.ops);
    doc = applyAll(doc, cut.ops);
    middle = cut.rightHalves[clip.id];
  }
  if (b < clip.recordOut) {
    const cut = linkedOpsForBlade(doc, middle, b, ctx.newId(), link);
    ops.push(...cut.ops);
    doc = applyAll(doc, cut.ops);
  }
  const slow = doc.tracks.flatMap((t) => t.items).find((i): i is MediaClip => i.id === middle && i.type === 'mediaClip');
  if (!slow) return null;
  const rate = typeof slow.playbackRate === 'number' && slow.playbackRate > 0 ? slow.playbackRate : 1;
  // Half speed: the same source takes twice the record frames, and what follows ripples later.
  ops.push(...linkedOpsForRetime(doc, middle, Math.max(0.1, rate / 2), slow.recordIn + (slow.recordOut - slow.recordIn) * 2, link));
  return ops;
}

/** Builds the one program for `id` on this event. Refusals (a missing or locked clip) come back as `none`, never a throw. */
export function planEventAction(id: EventActionId, ctx: EventActionContext): EventActionPlan {
  const fps = fpsOf(ctx.doc);
  if (id === 'addSound') return { kind: 'sound', id, frame: ctx.event.frame };
  if (id === 'addTitle') {
    const { ops, itemId } = addAtPlayhead(ctx.doc, 'title', ctx.event.frame, ctx.newId);
    return { kind: 'ops', id, ops, select: itemId };
  }
  if (id === 'flash') {
    const { ops, select } = flashPlan(ctx, fps);
    return { kind: 'ops', id, ops, select };
  }
  const found = clipOfEvent(ctx.doc, ctx.event);
  if (!found) return { kind: 'none', id, reason: 'noClip' };
  if (ctx.isLocked?.(found.track.id) ?? found.track.locked) return { kind: 'none', id, reason: 'locked' };
  const { clip } = found;
  if (id === 'cut') {
    if (ctx.event.frame <= clip.recordIn || ctx.event.frame >= clip.recordOut) return { kind: 'none', id, reason: 'tooShort' };
    const cut = linkedOpsForBlade(ctx.doc, clip.id, ctx.event.frame, ctx.newId(), ctx.isLocked ? { isLocked: ctx.isLocked } : undefined);
    return { kind: 'ops', id, ops: cut.ops, select: null };
  }
  if (id === 'speedRamp') {
    const ops = speedRampPlan(ctx, clip, fps);
    return ops ? { kind: 'ops', id, ops, select: null } : { kind: 'none', id, reason: 'tooShort' };
  }
  if (clip.recordOut - clip.recordIn < 3) return { kind: 'none', id, reason: 'tooShort' };
  const ops = id === 'zoom' ? zoomPlan(ctx, clip, fps) : id === 'pan' ? panPlan(ctx, clip, fps) : shakePlan(ctx, clip, fps);
  return ops.length > 0 ? { kind: 'ops', id, ops, select: null } : { kind: 'none', id, reason: 'tooShort' };
}

/** A fresh id for the items an action makes (the editor's one id generator). */
export const newEventActionId = (): string => newGuid();
