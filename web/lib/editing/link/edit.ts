/**
 * R08.1 link-aware editing WITHOUT a new op (the catalogue is closed): every builder here returns a
 * plain list of existing ops covering ALL members of the group, to be dispatched as ONE batch
 * (`store.dispatchBatch`), so it is one undo step. Pure functions of the document.
 *
 * A builder throws the engine's own `EditEngineError` when a member cannot take the edit (a trim
 * that has no neighbour to roll, say): the whole batch is refused, never half a pair.
 */

import { apply, applyAll, EditEngineError, type EditOp } from '../engine';
import { newGuid } from '../ids';
import { rippleDeletePlan, type RippleDeletePlan } from '../ripple-delete';
import type { EditTimeline, MediaClip } from '../types';
import type { TrimEdge, TrimMode } from '../engine';
import { editableGroup, expandSelection, groupOf, linkIdOf, locate, type LinkOptions, type Located } from './groups';

const rateOf = (clip: MediaClip): number => (typeof clip.playbackRate === 'number' && clip.playbackRate > 0 ? clip.playbackRate : 1);

// ─────────────────────────────────────────────────────────────────────────────
// Move / nudge
// ─────────────────────────────────────────────────────────────────────────────

export interface LinkedMove {
  itemId: string;
  toTrackId: string;
  toRecordIn: number;
}

/**
 * Moves the given clips and, by the same time delta, every partner on its OWN track. A partner that
 * is itself in `moves` is not moved twice. The primary may change track; partners never do.
 */
export function linkedOpsForMove(doc: EditTimeline, moves: readonly LinkedMove[], options?: LinkOptions): EditOp[] {
  const explicit = new Set(moves.map((m) => m.itemId));
  const planned: Array<{ itemId: string; trackId: string; to: number; delta: number; start: number }> = [];
  const taken = new Set<string>();
  for (const move of moves) {
    const own = locate(doc, move.itemId);
    if (!own) continue;
    const delta = move.toRecordIn - own.item.recordIn;
    planned.push({ itemId: move.itemId, trackId: move.toTrackId, to: move.toRecordIn, delta, start: own.item.recordIn });
    taken.add(move.itemId);
    for (const partner of editableGroup(doc, move.itemId, options)) {
      if (partner.item.id === move.itemId || explicit.has(partner.item.id) || taken.has(partner.item.id)) continue;
      taken.add(partner.item.id);
      planned.push({ itemId: partner.item.id, trackId: partner.track.id, to: partner.item.recordIn + delta, delta, start: partner.item.recordIn });
    }
  }
  // Moving right: right-most first; moving left: left-most first, so no step runs over a neighbour that has not moved yet.
  const right = planned.filter((p) => p.delta >= 0).sort((a, b) => b.start - a.start);
  const left = planned.filter((p) => p.delta < 0).sort((a, b) => a.start - b.start);
  return [...right, ...left].map((p): EditOp => ({ type: 'move', itemId: p.itemId, toTrackId: p.trackId, toRecordIn: p.to }));
}

/** Nudges the given clips and their partners by `deltaFrames` (same direction ordering as a move). */
export function linkedOpsForNudge(doc: EditTimeline, ids: readonly string[], deltaFrames: number, options?: LinkOptions): EditOp[] {
  if (deltaFrames === 0) return [];
  const chosen = collectEditable(doc, ids, options);
  const sorted = [...chosen].sort((a, b) => (deltaFrames > 0 ? b.item.recordIn - a.item.recordIn : a.item.recordIn - b.item.recordIn));
  return sorted.map((m): EditOp => ({ type: 'nudge', itemId: m.item.id, deltaFrames }));
}

/** The ids plus their partners (unlocked partners only), each once. */
function collectEditable(doc: EditTimeline, ids: readonly string[], options?: LinkOptions): Located[] {
  const seen = new Set<string>();
  const out: Located[] = [];
  for (const id of ids) {
    for (const member of editableGroup(doc, id, options)) {
      if (seen.has(member.item.id)) continue;
      seen.add(member.item.id);
      out.push(member);
    }
  }
  return out;
}

// ─────────────────────────────────────────────────────────────────────────────
// Trim
// ─────────────────────────────────────────────────────────────────────────────

/** How far a trim really moved its edge, in record frames (the engine clamps; this reads the result). */
function edgeDisplacement(before: Located, doc: EditTimeline, itemId: string, mode: TrimMode, edge: TrimEdge): number {
  const after = locate(doc, itemId)?.item;
  if (!after) return 0;
  const b = before.item;
  switch (mode) {
    case 'slip':
      return Math.round(((((after as MediaClip).sourceIn ?? 0) - ((b as MediaClip).sourceIn ?? 0))) / (b.type === 'mediaClip' ? rateOf(b) : 1));
    case 'slide':
      return after.recordIn - b.recordIn;
    case 'roll':
      return edge === 'out' ? after.recordOut - b.recordOut : after.recordIn - b.recordIn;
    case 'ripple':
      return edge === 'out' ? after.recordOut - b.recordOut : b.recordOut - b.recordIn - (after.recordOut - after.recordIn);
  }
}

/**
 * Trims the same edge of every member by the same delta, and the delta is the largest one EVERY
 * member can take (the engine clamps each trim on its own; one clamped member must not leave the
 * pair out of step). `[]` when some member cannot move at all.
 */
export function linkedOpsForTrim(doc: EditTimeline, itemId: string, edge: TrimEdge, mode: TrimMode, deltaFrames: number, options?: LinkOptions): EditOp[] {
  const group = editableGroup(doc, itemId, options);
  if (group.length === 0 || deltaFrames === 0) return [];
  let common = deltaFrames;
  if (group.length > 1) {
    for (const member of group) {
      const probe = apply(doc, { type: 'trim', itemId: member.item.id, edge, mode, deltaFrames }).doc;
      const moved = edgeDisplacement(member, probe, member.item.id, mode, edge);
      if (Math.sign(moved) !== Math.sign(deltaFrames) || moved === 0) return [];
      if (Math.abs(moved) < Math.abs(common)) common = moved;
    }
  }
  return group.map((m): EditOp => ({ type: 'trim', itemId: m.item.id, edge, mode, deltaFrames: common }));
}

/**
 * The J/L-cut helper (the Alt/Option override): moves ONE clip's edge in place, leaving its partners
 * alone and the link intact, so the sound can lead or trail the picture. Unlike a bare ripple trim it
 * does not disturb later clips on that track: the clip is trimmed, (for the in edge) put back at its
 * start line, and every following clip on the track is nudged back to where it was.
 * `deltaFrames` < 0 moves the edge earlier. Empty when the clip cannot take the edit at all.
 */
export function jlCutOps(doc: EditTimeline, itemId: string, edge: TrimEdge, deltaFrames: number): EditOp[] {
  const own = locate(doc, itemId);
  if (!own || deltaFrames === 0) return [];
  const trim: EditOp = { type: 'trim', itemId, edge, mode: 'ripple', deltaFrames };
  const after = apply(doc, trim).doc;
  const next = locate(after, itemId);
  if (!next) return [];
  const effective = edgeDisplacement(own, after, itemId, 'ripple', edge);
  if (effective === 0) return [];
  // After a ripple trim: out edge, followers moved by +effective; in edge, the head stays put and followers moved by -effective.
  const followersShift = edge === 'out' ? effective : -effective;
  const back = -followersShift;
  const nudges = [...own.track.items.slice(own.index + 1)]
    .sort((a, b) => (back > 0 ? b.recordIn - a.recordIn : a.recordIn - b.recordIn))
    .map((follower): EditOp => ({ type: 'nudge', itemId: follower.id, deltaFrames: back }));
  const put: EditOp[] = edge === 'in' ? [{ type: 'move', itemId, toTrackId: own.track.id, toRecordIn: own.item.recordIn + effective }] : [];
  // Extending the head earlier: the clip steps back first, then the followers close up; shortening it: the followers step out first.
  const ops: EditOp[] = effective < 0 && edge === 'in' ? [trim, ...put, ...nudges] : [trim, ...nudges, ...put];
  try {
    applyAll(doc, ops);
  } catch (error) {
    if (error instanceof EditEngineError) return [];
    throw error;
  }
  return ops;
}

// ─────────────────────────────────────────────────────────────────────────────
// Blade
// ─────────────────────────────────────────────────────────────────────────────

export interface LinkedBlade {
  ops: EditOp[];
  /** For every blade: the clip that was cut -> the id of its new right half. */
  rightHalves: Record<string, string>;
}

/**
 * Cuts the clip and its partners at `atFrame`. The left halves keep the link; the right halves get a
 * fresh shared `linkId` (so the two halves are two separate pairs), or none when only one member
 * actually straddles the frame. `newItemId` names the primary's right half; partners' ids are minted.
 */
export function linkedOpsForBlade(doc: EditTimeline, itemId: string, atFrame: number, newItemId: string, options?: LinkOptions): LinkedBlade {
  const group = editableGroup(doc, itemId, options).filter((m) => m.item.recordIn < atFrame && atFrame < m.item.recordOut);
  const ops: EditOp[] = [];
  const rightHalves: Record<string, string> = {};
  for (const member of group) {
    const id = member.item.id === itemId ? newItemId : newGuid();
    rightHalves[member.item.id] = id;
    ops.push({ type: 'blade', itemId: member.item.id, atFrame, newItemId: id });
  }
  const primary = locate(doc, itemId);
  const wasLinked = primary !== null && linkIdOf(primary.item) !== null;
  if (wasLinked) {
    const fresh = group.length > 1 ? newGuid() : null;
    for (const member of group) ops.push({ type: 'setProperty', itemId: rightHalves[member.item.id], key: 'linkId', value: fresh });
  }
  return { ops, rightHalves };
}

// ─────────────────────────────────────────────────────────────────────────────
// Delete
// ─────────────────────────────────────────────────────────────────────────────

/**
 * "Delete" (ripple, D7) or "Shift+Delete" (lift) of the selection plus every partner. Ripple closes
 * the gap on every track a member sits on (it delegates to {@link rippleDeletePlan}, which also finds
 * un-linked picture/sound pairs); partners on a locked track are left alone and counted.
 */
export function linkedOpsForDelete(doc: EditTimeline, ids: readonly string[], mode: 'ripple' | 'lift', isLocked: (trackId: string) => boolean = () => false): RippleDeletePlan {
  const picked = new Set(ids);
  let skippedLocked = 0;
  const chosen: string[] = [];
  for (const id of expandSelection(doc, ids)) {
    const own = locate(doc, id);
    if (!own) continue;
    if (!picked.has(id) && isLocked(own.track.id)) {
      skippedLocked++;
      continue;
    }
    chosen.push(id);
  }
  if (mode === 'ripple') {
    const plan = rippleDeletePlan(doc, chosen, isLocked);
    return { ...plan, skippedLocked: plan.skippedLocked + skippedLocked };
  }
  return { ops: chosen.map((id): EditOp => ({ type: 'lift', itemId: id })), itemCount: chosen.length, skippedLocked };
}

// ─────────────────────────────────────────────────────────────────────────────
// Retime / properties
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Retimes the clip to `playbackRate` ending at `recordOut` and every partner by the same speed and the
 * same proportional length, so picture and sound stay the same length and in step.
 */
export function linkedOpsForRetime(doc: EditTimeline, itemId: string, playbackRate: number, recordOut: number, options?: LinkOptions): EditOp[] {
  const own = locate(doc, itemId);
  if (!own) return [];
  const oldLength = own.item.recordOut - own.item.recordIn;
  const ratio = (recordOut - own.item.recordIn) / oldLength;
  return editableGroup(doc, itemId, options).map((m): EditOp => {
    if (m.item.id === itemId) return { type: 'retime', itemId, playbackRate, recordOut };
    const length = Math.max(1, Math.round((m.item.recordOut - m.item.recordIn) * ratio));
    return { type: 'retime', itemId: m.item.id, playbackRate, recordOut: m.item.recordIn + length };
  });
}

/** Property keys that apply to the whole group; every other key (gain, transform, crop...) is per clip. */
export const GROUP_WIDE_KEYS: readonly string[] = ['enabled'];

/** setProperty on the clip; for a group-wide key (see {@link GROUP_WIDE_KEYS}) on every member. */
export function linkedOpsForSetProperty(doc: EditTimeline, itemId: string, key: Extract<EditOp, { type: 'setProperty' }>['key'], value: Extract<EditOp, { type: 'setProperty' }>['value'], options?: LinkOptions): EditOp[] {
  const members = GROUP_WIDE_KEYS.includes(key) ? editableGroup(doc, itemId, options) : groupOf(doc, itemId).filter((m) => m.item.id === itemId);
  return members.map((m): EditOp => ({ type: 'setProperty', itemId: m.item.id, key, value }));
}

/** Enable or disable the clip and its partners together. */
export function linkedOpsForEnable(doc: EditTimeline, itemId: string, enabled: boolean, options?: LinkOptions): EditOp[] {
  return linkedOpsForSetProperty(doc, itemId, 'enabled', enabled, options);
}

// ─────────────────────────────────────────────────────────────────────────────
// Link / unlink
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Links the given clips into one new group. They must be media clips from the same source on
 * different tracks (the document rules); returns `[]` otherwise rather than an invalid group.
 */
export function linkOps(doc: EditTimeline, ids: readonly string[]): EditOp[] {
  const members = ids.map((id) => locate(doc, id)).filter((m): m is Located => m !== null && m.item.type === 'mediaClip');
  if (members.length < 2 || members.length > 8) return [];
  if (new Set(members.map((m) => m.track.id)).size !== members.length) return [];
  const first = (members[0].item as MediaClip).assetRef;
  const same = members.every((m) => {
    const ref = (m.item as MediaClip).assetRef;
    return ref.kind === first.kind && (ref.projectFileId ?? null) === (first.projectFileId ?? null) && (ref.stepResultId ?? null) === (first.stepResultId ?? null);
  });
  if (!same) return [];
  const linkId = newGuid();
  return members.map((m): EditOp => ({ type: 'setProperty', itemId: m.item.id, key: 'linkId', value: linkId }));
}

/** Unlinks the whole group of each given clip (default), or only the given clips when `only` is set. */
export function unlinkOps(doc: EditTimeline, ids: readonly string[], only = false): EditOp[] {
  const targets = only ? ids.filter((id) => locate(doc, id)) : expandSelection(doc, ids);
  return targets.filter((id) => linkIdOf(locate(doc, id)!.item) !== null).map((id): EditOp => ({ type: 'setProperty', itemId: id, key: 'linkId', value: null }));
}
