/**
 * P8: applying an AI-assist proposal — atomic, provenance-aware, partially acceptable.
 *
 * A proposal (`POST …/proposals`) is a list of GROUPS, each a small program of ops from the closed
 * catalogue plus the items it touches. The server compiled it from id-anchored model actions; this
 * module decides what the editor actually does with it, and trusts nothing it can recompute:
 *
 *   - **Provenance is recomputed here.** Whether a group touches a clip the user owns (no
 *     provenance, `origin: 'manual'`, or `editedByUser`) is derived from the CURRENT document, not
 *     from the server's `touchesUserOwned` flag — the document may have changed since the proposal
 *     was made, and a flag cannot be trusted to be honest anyway. Any op that names a user-owned item
 *     counts, not only the ids the server listed.
 *   - **A user-owned clip is never changed without acknowledgement.** `planProposal` refuses
 *     ({@link ProposalNeedsAcknowledgement}) unless every user-owned item the accepted groups touch is
 *     in `acknowledged` — which the panel only fills after the ghost diff has shown that item.
 *   - **Atomic.** The accepted groups' ops are dry-run as one program; if any op refuses, nothing is
 *     applied and the refusal names the group. The store then applies the same program as ONE batch
 *     (one undo step, its inverse the engine's own inverse programs).
 *   - **Partial accept leaves a valid document.** Groups are independent except for the lane a
 *     proposal creates (`addTrack` rides in the FIRST group that needs it); accepting a later group
 *     alone pulls that `addTrack` in, so a partial accept never references a track that does not exist;
 *     a lane an earlier partial accept already created is reused, not added a second time.
 *     The result is run through `assertInvariants` before it is returned.
 *   - **Content cuts are compiled here.** A group with `cuts` (source seconds on an item) carries no
 *     ops: at plan time its cuts are mapped to record frames on the CURRENT document, merged with the
 *     other accepted cut groups per track and compiled once per track through `compileRippleCut`
 *     (blade/blade/extract, linked and twin partners included), before the op groups. A cut whose
 *     item is gone is a refusal that names the group.
 */

import { ApplySession, applyAll, assertInvariants, EditEngineError, type EditOp } from '../engine';
import { newGuid } from '../ids';
import { mapSourceSilenceToRecord, normalizeSpans, type RecordSpan } from '../silence/remove-silence';
import { compileRippleCut } from '../transcript/transcript';
import type { EditTimeline, Item, MediaClip } from '../types';
import type { EditProposalGroupResponse, EditProposalResponse, ProposalCut } from '../sync/api';

/** Thrown when accepted groups would change user-owned clips the user has not acknowledged. */
export class ProposalNeedsAcknowledgement extends Error {
  constructor(readonly itemIds: readonly string[]) {
    super(`This would change ${itemIds.length} clip${itemIds.length === 1 ? '' : 's'} you made yourself — review them in the ghost diff and confirm first.`);
    this.name = 'ProposalNeedsAcknowledgement';
  }
}

/** A clip the user authored or confirmed — the same rule as the server's `EditProposalView.IsUserOwned`. */
export function isUserOwned(item: Item): boolean {
  const p = item.provenance;
  return p === null || p === undefined || p.origin === 'manual' || p.editedByUser === true;
}

function findItem(doc: EditTimeline, id: string): Item | undefined {
  for (const track of doc.tracks) {
    const item = track.items.find((i) => i.id === id);
    if (item) return item;
  }
  return undefined;
}

/** Every item id an op names directly (not ids it creates). */
export function idsNamedBy(op: EditOp): string[] {
  const o = op as Record<string, unknown>;
  const ids: string[] = [];
  for (const key of ['itemId', 'afterItemId', 'transitionId']) if (typeof o[key] === 'string') ids.push(o[key] as string);
  if (op.type === 'setKeyframe' || op.type === 'removeKeyframe' || op.type === 'moveKeyframe') {
    if ('itemId' in op.target) ids.push(op.target.itemId);
  }
  if (op.type === 'setColorGroup') ids.push(...(op.group?.clipIds ?? []));
  return ids;
}

/** The ops a cut-only group compiles to against `doc`, memoised per document and group object. */
const cutOpsMemo = new WeakMap<EditTimeline, WeakMap<object, readonly EditOp[] | null>>();
function cutGroupOps(doc: EditTimeline, group: Partial<EditProposalGroupResponse>): readonly EditOp[] | null {
  if (!group.cuts || group.cuts.length === 0) return null;
  let perDoc = cutOpsMemo.get(doc);
  if (!perDoc) cutOpsMemo.set(doc, (perDoc = new WeakMap()));
  if (perDoc.has(group)) return perDoc.get(group) ?? null;
  let ops: readonly EditOp[] | null;
  try {
    ops = expandCuts(doc, [{ ...group, label: group.label ?? '' }], new Set([0])).ops;
  } catch {
    ops = null;
  }
  perDoc.set(group, ops);
  return ops;
}

/**
 * The existing user-owned items a group's ops would change, recomputed against `doc`. A cut group is
 * judged by its EXPANDED ops, so the linked and twin partners the ripple cut also touches count.
 */
export function userOwnedTouchedBy(doc: EditTimeline, group: Pick<EditProposalGroupResponse, 'ops'> & Partial<EditProposalGroupResponse>): string[] {
  const cutOps = cutGroupOps(doc, group);
  const named = [...group.ops.flatMap(idsNamedBy), ...(cutOps ? cutOps.flatMap(idsNamedBy) : (group.cuts ?? []).map((c) => c.itemId))];
  const changed = new Set(named);
  const out: string[] = [];
  for (const id of changed) {
    const item = findItem(doc, id);
    if (item && isUserOwned(item)) out.push(id);
  }
  return out;
}

export const MAX_CUTS_PER_GROUP = 200;
export const MAX_CUTS_PER_ACCEPT = 300;

/** Thrown when a content-cut group (or the accepted set) has more cuts than the editor applies at once. */
export class ProposalTooManyCuts extends EditEngineError {
  constructor(readonly count: number, readonly label: string) {
    super(`“${label}” cannot be applied to the timeline as it is now: this suggestion has too many cuts to apply at once (${count}). Mark a shorter range and ask again.`);
    this.name = 'ProposalTooManyCuts';
  }
}

export interface ExpandedCuts {
  /** The document after the cut ops (the expansion's own preview), so callers need not replay them. */
  doc?: EditTimeline;
  /** How many leading ops are the (already applied) cut expansion. */
  cutCount?: number;
  /** The ripple-cut program for all accepted cut groups, one `compileRippleCut` per track. */
  ops: EditOp[];
  /** Which proposal group (index into the given groups) each op came from, for naming a refusal. */
  owner: Map<EditOp, number>;
}

const fpsOf = (doc: EditTimeline): number => (doc.settings.rate.den === 0 ? 30 : doc.settings.rate.num / doc.settings.rate.den);

function findClip(doc: EditTimeline, id: string): { clip: MediaClip; trackId: string } | undefined {
  for (const track of doc.tracks) {
    const item = track.items.find((i) => i.id === id);
    if (item) return item.type === 'mediaClip' ? { clip: item, trackId: track.id } : undefined;
  }
  return undefined;
}

/**
 * Expands the cuts of the accepted groups: per track (document order), the cuts' source seconds are
 * mapped to record frames on the CURRENT (already partly cut) document just before that track's ONE
 * `compileRippleCut`. A clip an earlier op of the expansion already split (a linked or twin
 * partner that was cut along) is cut on whichever piece still shows the span; the part already gone is skipped.
 * Refuses, naming the group (EditEngineError), when a cut's clip is gone, is reversed or a freeze
 * frame, or when the span no longer lies wholly inside the clip's source window (stale, or a blade since).
 */
/** Throws ProposalTooManyCuts when the accepted cut groups exceed the caps (cheap; no expansion). */
export function checkCutCaps(groups: CutGroups, accepted: ReadonlySet<number>): void {
  let total = 0;
  let first: string | null = null;
  for (const i of [...accepted].sort((a, b) => a - b)) {
    const n = groups[i]?.cuts?.length ?? 0;
    if (n === 0) continue;
    if (n > MAX_CUTS_PER_GROUP) throw new ProposalTooManyCuts(n, groups[i].label);
    first ??= groups[i].label;
    total += n;
  }
  if (total > MAX_CUTS_PER_ACCEPT) throw new ProposalTooManyCuts(total, first ?? '');
}

type CutGroups = ReadonlyArray<Pick<EditProposalGroupResponse, 'label'> & Partial<EditProposalGroupResponse>>;

/** Per document, the expansions already computed, keyed by the accepted cut groups' content (computed once per accept). */
const expansionMemo = new WeakMap<EditTimeline, Map<string, { result: ExpandedCuts; indices: number[] } | Error>>();
export const expansionStats = { computed: 0 };

export function expandCuts(doc: EditTimeline, groups: CutGroups, accepted: ReadonlySet<number>): ExpandedCuts {
  const indices = [...accepted].filter((i) => (groups[i]?.cuts?.length ?? 0) > 0).sort((a, b) => a - b);
  if (indices.length === 0) return { ops: [], owner: new Map(), doc };
  checkCutCaps(groups, accepted);
  const key = JSON.stringify(indices.map((i) => [groups[i].label, groups[i].cuts]));
  let perDoc = expansionMemo.get(doc);
  if (!perDoc) expansionMemo.set(doc, (perDoc = new Map()));
  let hit = perDoc.get(key);
  if (!hit) {
    try {
      expansionStats.computed++;
      hit = { result: expandCutsUncached(doc, indices.map((i) => groups[i]), new Set(indices.map((_, n) => n))), indices };
    } catch (e) {
      hit = e instanceof Error ? e : new Error(String(e));
    }
    perDoc.set(key, hit);
  }
  if (hit instanceof Error) throw hit;
  // The memo's owners are positions in the accepted-cut-group list; map them back to this caller's indices.
  const owner = new Map<EditOp, number>();
  for (const [op, n] of hit.result.owner) owner.set(op, indices[n]);
  return { ops: hit.result.ops, owner, doc: hit.result.doc };
}

function sameAsset(a: MediaClip, b: MediaClip): boolean {
  return a.assetRef.kind === b.assetRef.kind && a.assetRef.projectFileId === b.assetRef.projectFileId && a.assetRef.stepResultId === b.assetRef.stepResultId;
}

/** True when `piece` shows part of the source window `[from, to)` of `whole`: what a blade leaves of one clip. */
function sameSource(piece: MediaClip, whole: MediaClip, from: number, to: number, rate: number): boolean {
  if (!sameAsset(piece, whole)) return false;
  const pieceRate = typeof piece.playbackRate === 'number' && piece.playbackRate > 0 ? piece.playbackRate : 1;
  if (pieceRate !== rate) return false;
  const start = piece.sourceIn ?? 0;
  return start >= from && start + (piece.recordOut - piece.recordIn) * pieceRate <= to + 1;
}

function expandCutsUncached(doc: EditTimeline, groups: CutGroups, accepted: ReadonlySet<number>): ExpandedCuts {
  const fps = fpsOf(doc);
  const refuse = (group: number, why: string): never => {
    throw new EditEngineError(`“${groups[group].label}” cannot be applied to the timeline as it is now: ${why}`);
  };
  const perTrack = new Map<string, Array<{ group: number; cut: ProposalCut }>>();
  groups.forEach((group, index) => {
    if (!accepted.has(index) || !group.cuts) return;
    for (const cut of group.cuts) {
      const found = findClip(doc, cut.itemId);
      if (!found) refuse(index, 'the clip it cuts is no longer on the timeline.');
      else if (found.clip.reverse === true || found.clip.hold === true) refuse(index, 'the clip it cuts is reversed or a freeze frame.');
      else perTrack.set(found.trackId, [...(perTrack.get(found.trackId) ?? []), { group: index, cut }]);
    }
  });
  const ops: EditOp[] = [];
  const owner = new Map<EditOp, number>();
  let current = doc;
  for (const track of doc.tracks) {
    const entries = perTrack.get(track.id);
    if (!entries) continue;
    const tagged: Array<RecordSpan & { group: number }> = [];
    for (const { group, cut } of entries) {
      const original = findClip(doc, cut.itemId);
      if (!original) {
        refuse(group, 'the clip it cuts is no longer on the timeline.');
        continue;
      }
      // The cut names a span of the clip as it stands now. A span wholly outside it is stale; one only partly inside
      // (a blade made since the proposal was launched) would be half-applied, so it is refused too.
      const rate = typeof original.clip.playbackRate === 'number' && original.clip.playbackRate > 0 ? original.clip.playbackRate : 1;
      const from = original.clip.sourceIn ?? 0;
      const to = from + (original.clip.recordOut - original.clip.recordIn) * rate;
      if (cut.endSec * fps <= from || cut.startSec * fps >= to) refuse(group, 'the part it cuts is no longer in the clip.');
      if (cut.startSec * fps < from - 1 || cut.endSec * fps > to + 1) {
        // Past the clip's own window is fine when that part is trimmed away; it is not when a sibling piece of the same
        // source on the track shows it (a blade split the clip), as only one side would be cut.
        const lo = cut.startSec * fps;
        const hi = cut.endSec * fps;
        const siblings = (doc.tracks.find((t) => t.id === original.trackId)?.items ?? []).filter((i): i is MediaClip => i.type === 'mediaClip' && i.id !== original.clip.id);
        const split = siblings.some((i) => {
          const start = i.sourceIn ?? 0;
          const iRate = typeof i.playbackRate === 'number' && i.playbackRate > 0 ? i.playbackRate : 1;
          return sameAsset(i, original.clip) && iRate === rate && start < hi && start + (i.recordOut - i.recordIn) * iRate > lo && (start >= to - 1 || start + (i.recordOut - i.recordIn) * iRate <= from + 1);
        });
        if (split) refuse(group, 'the part it cuts is no longer all in one clip.');
      }
      // An earlier cut of this program (a picture cut that took its linked sound with it) may have split the clip:
      // the span is looked up on every piece of it still on the track; the part already cut away maps to nothing.
      const track = current.tracks.find((t) => t.id === original.trackId);
      for (const piece of track?.items ?? []) {
        if (piece.type !== 'mediaClip' || !sameSource(piece, original.clip, from, to, rate)) continue;
        const span = mapSourceSilenceToRecord(piece, { id: '', startSec: cut.startSec, endSec: cut.endSec }, fps);
        if (span) tagged.push({ ...span, group });
      }
    }
    if (tagged.length === 0) continue;
    const plan = compileRippleCut(current, { trackId: track.id, spans: normalizeSpans(tagged), newItemId: newGuid });
    let lastGroup = tagged[0].group;
    for (const op of plan.ops) {
      if (op.type === 'blade') lastGroup = tagged.find((t) => t.startFrame <= op.atFrame && op.atFrame <= t.endFrame)?.group ?? lastGroup;
      ops.push(op);
      owner.set(op, lastGroup);
    }
    current = plan.preview;
  }
  return { ops, owner, doc: current };
}

/** The track ids a group's ops create, and the track ids they place into. */
function tracksCreated(ops: readonly EditOp[]): Set<string> {
  return new Set(ops.flatMap((op) => (op.type === 'addTrack' ? [op.track.id] : [])));
}
function tracksUsed(ops: readonly EditOp[]): Set<string> {
  const used = new Set<string>();
  for (const op of ops) if ('trackId' in op && typeof op.trackId === 'string') used.add(op.trackId);
  return used;
}

/**
 * The program for the accepted groups, in proposal order, with any lane-creating `addTrack` a
 * later group depends on pulled in from the (unaccepted) group that carries it. A lane that already
 * exists in `doc` (an earlier partial accept created it) is reused, never added twice (UI2-11).
 */
export function programFor(proposal: Pick<EditProposalResponse, 'groups'>, accepted: ReadonlySet<number>, doc?: EditTimeline): EditOp[] {
  return programWithOwners(proposal, accepted, doc).ops;
}

function programWithOwners(proposal: Pick<EditProposalResponse, 'groups'>, accepted: ReadonlySet<number>, doc?: EditTimeline): ExpandedCuts {
  // Content cuts first (without a document they cannot be expanded and are left out).
  const expanded = doc ? expandCuts(doc, proposal.groups, accepted) : { ops: [], owner: new Map<EditOp, number>() };
  // An op on a clip an accepted cut also cuts would run on a stale id: refuse, naming the op group.
  if (expanded.ops.length > 0) {
    const cutIds = new Set(expanded.ops.flatMap(idsNamedBy).filter((id) => doc && findItem(doc, id)));
    proposal.groups.forEach((group, index) => {
      if (!accepted.has(index)) return;
      const clash = group.ops.flatMap(idsNamedBy).find((id) => cutIds.has(id));
      if (clash) throw new EditEngineError(`“${group.label}” cannot be applied to the timeline as it is now: “${doc ? (findItem(doc, clash)?.name ?? clash) : clash}” is already being cut.`);
    });
  }
  const program: EditOp[] = [...expanded.ops];
  const owner = expanded.owner;
  const created = new Set<string>();
  if (doc) {
    for (const group of proposal.groups) {
      for (const op of group.ops) {
        if (op.type === 'addTrack' && doc.tracks.some((t) => t.id === op.track.id && t.kind === op.track.kind)) created.add(op.track.id);
      }
    }
  }
  proposal.groups.forEach((group, index) => {
    if (!accepted.has(index)) return;
    for (const trackId of tracksUsed(group.ops)) {
      if (created.has(trackId) || tracksCreated(group.ops).has(trackId)) continue;
      const carrier = proposal.groups.find((g) => tracksCreated(g.ops).has(trackId));
      const addTrack = carrier?.ops.find((op) => op.type === 'addTrack' && op.track.id === trackId);
      if (addTrack) {
        program.push(addTrack);
        created.add(trackId);
      }
    }
    for (const op of group.ops) {
      if (op.type === 'addTrack') {
        if (created.has(op.track.id)) continue;
        created.add(op.track.id);
      }
      program.push(op);
      owner.set(op, index);
    }
  });
  return { ops: program, owner, doc: expanded.doc, cutCount: expanded.ops.length };
}

export interface ProposalPlan {
  /** The program the store applies as one batch. */
  ops: EditOp[];
  /** The document after the program, already checked by `assertInvariants`. Shared across calls for the same accept: treat as read-only, like every engine document. */
  doc: EditTimeline;
  /** The user-owned ids the program changes (all acknowledged, or the plan would have thrown). */
  userOwned: string[];
}

/**
 * Plans an accept (all groups, or a subset): refuses unacknowledged user-owned changes, dry-runs the
 * whole program atomically and checks the invariants of the result. Never mutates `doc`. The returned
 * `doc` is shared across calls for the same accept; treat it as read-only, like every engine document.
 */
export function planProposal(doc: EditTimeline, proposal: Pick<EditProposalResponse, 'groups'>, accepted: ReadonlySet<number>, acknowledged: ReadonlySet<string> = new Set()): ProposalPlan {
  const userOwned = [...new Set([...accepted].flatMap((i) => (proposal.groups[i] ? userOwnedTouchedBy(doc, proposal.groups[i]) : [])))];
  const missing = userOwned.filter((id) => !acknowledged.has(id));
  if (missing.length > 0) throw new ProposalNeedsAcknowledgement(missing);
  const { ops, owner, doc: afterCuts, cutCount = 0 } = programWithOwners(proposal, accepted, doc);
  // The cut expansion already produced its result document: only the op groups are replayed on top of it.
  let next = cutCount > 0 && afterCuts ? afterCuts : doc;
  const rest = ops.slice(cutCount);
  // One working copy for the whole program; an op that fails names its group, and the copy is dropped.
  const session = rest.length > 0 ? new ApplySession(next) : null;
  for (const op of rest) {
    try {
      session?.apply(op);
    } catch (error) {
      const label = proposal.groups[owner.get(op) ?? -1]?.label ?? op.type;
      const message = error instanceof Error ? error.message : String(error);
      throw new EditEngineError(`“${label}” cannot be applied to the timeline as it is now: ${message}`, error instanceof EditEngineError ? error.op : undefined);
    }
  }
  if (session) next = session.doc;
  assertInvariants(next);
  return { ops, doc: next, userOwned };
}

/** One ghost on the timeline: what accepting would add, remove or change. */
export interface ProposalGhost {
  itemId: string;
  trackId: string;
  recordIn: number;
  recordOut: number;
  kind: 'add' | 'remove' | 'change';
  /** For a change, the span before (so the canvas can hatch the old span and outline the new). */
  before?: { recordIn: number; recordOut: number; trackId: string };
  userOwned: boolean;
  /** The proposal group that produced it. */
  groupIndex: number;
}

/**
 * The ghost diff for the selected groups, group by group so each ghost knows which checkbox it
 * belongs to. A group that cannot apply on its own (against the current document) yields no ghosts;
 * the panel shows it as unavailable instead.
 */
export function ghostDiff(doc: EditTimeline, proposal: Pick<EditProposalResponse, 'groups'>, selected: ReadonlySet<number>): { ghosts: ProposalGhost[]; failed: number[]; newTracks: { id: string; name: string; kind: 'video' | 'audio' }[] } {
  const ghosts: ProposalGhost[] = [];
  const failed: number[] = [];
  const newTracks = new Map<string, { id: string; name: string; kind: 'video' | 'audio' }>();
  const before = locate(doc);
  for (const index of selected) {
    const group = proposal.groups[index];
    if (!group) continue;
    let after: EditTimeline;
    try {
      const program = programWithOwners(proposal, new Set([index]), doc);
      const cutCount = program.cutCount ?? 0;
      after = applyAll(cutCount > 0 && program.doc ? program.doc : doc, program.ops.slice(cutCount));
    } catch {
      failed.push(index);
      continue;
    }
    for (const track of after.tracks) if (!doc.tracks.some((t) => t.id === track.id)) newTracks.set(track.id, { id: track.id, name: track.name, kind: track.kind });
    const afterMap = locate(after);
    for (const [id, a] of afterMap) {
      const b = before.get(id);
      if (!b) ghosts.push({ itemId: id, trackId: a.trackId, recordIn: a.item.recordIn, recordOut: a.item.recordOut, kind: 'add', userOwned: false, groupIndex: index });
      else if (JSON.stringify(a.item) !== JSON.stringify(b.item) || a.trackId !== b.trackId) {
        ghosts.push({ itemId: id, trackId: a.trackId, recordIn: a.item.recordIn, recordOut: a.item.recordOut, kind: 'change', before: { recordIn: b.item.recordIn, recordOut: b.item.recordOut, trackId: b.trackId }, userOwned: isUserOwned(b.item), groupIndex: index });
      }
    }
    for (const [id, b] of before) {
      if (!afterMap.has(id)) ghosts.push({ itemId: id, trackId: b.trackId, recordIn: b.item.recordIn, recordOut: b.item.recordOut, kind: 'remove', userOwned: isUserOwned(b.item), groupIndex: index });
    }
    // The cut spans themselves: the pieces a ripple cut leaves behind carry fresh ids, so the removed stretch is shown from the cut.
    for (const cut of group.cuts ?? []) {
      const found = findClip(doc, cut.itemId);
      const span = found ? mapSourceSilenceToRecord(found.clip, { id: '', startSec: cut.startSec, endSec: cut.endSec }, fpsOf(doc)) : null;
      if (found && span) ghosts.push({ itemId: cut.itemId, trackId: found.trackId, recordIn: span.startFrame, recordOut: span.endFrame, kind: 'remove', userOwned: isUserOwned(found.clip), groupIndex: index });
    }
  }
  return { ghosts, failed, newTracks: [...newTracks.values()] };
}

function locate(doc: EditTimeline): Map<string, { item: Item; trackId: string }> {
  const map = new Map<string, { item: Item; trackId: string }>();
  for (const track of doc.tracks) for (const item of track.items) map.set(item.id, { item, trackId: track.id });
  return map;
}
