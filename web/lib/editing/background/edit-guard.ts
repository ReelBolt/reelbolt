/**
 * Which edits touch a clip a background job is working on. Only the ones that change WHICH part of the video the
 * clip plays, or whether it exists, matter: delete, trim, move, split, slip, replace the video, change the speed.
 * A property such as the region effect the job itself writes never counts.
 */

import type { EditOp } from '../engine';
import type { EditTimeline, MediaClip } from '../types';
import { allMediaClips, rebaseAnchor } from './anchor';
import { canTakeRegionEffect } from './object-effect';
import { heldKeysFor, holdsStructuralEdits, SOURCE_KEYS } from './job-protection';
import type { JobMark } from './job-marks';
import type { BackgroundEntry } from './results-store';


/** Media clip ids inside the compound `itemId` (nested compounds included); empty when it is not a compound. */
export function nestedClipIds(doc: EditTimeline, itemId: string): string[] {
  const compound = doc.tracks.flatMap((t) => t.items).find((i) => i.id === itemId);
  return compound?.type === 'compoundClip' ? clipsOfSequence(doc, compound.sequenceId, new Set()) : [];
}

function clipsOfSequence(doc: EditTimeline, sequenceId: string, seen: Set<string>): string[] {
  if (seen.has(sequenceId)) return [];
  seen.add(sequenceId);
  const out: string[] = [];
  for (const item of (doc.sequences ?? []).find((q) => q.id === sequenceId)?.tracks.flatMap((t) => t.items) ?? []) {
    if (item.type === 'mediaClip') out.push(item.id);
    else if (item.type === 'compoundClip') out.push(...clipsOfSequence(doc, item.sequenceId, seen));
  }
  return out;
}

/** Ids of the clips an op deletes, trims, moves, splits, slips, replaces or retimes. */
export function affectedClipIds(op: EditOp, doc: EditTimeline, heldKeys: ReadonlySet<string> = SOURCE_KEYS): string[] {
  const own = ownAffected(op, doc, heldKeys);
  // Lifting, moving or replacing a compound touches every clip nested in it.
  return [...own, ...own.flatMap((id) => nestedClipIds(doc, id))];
}

function ownAffected(op: EditOp, doc: EditTimeline, heldKeys: ReadonlySet<string>): string[] {
  switch (op.type) {
    case 'setSequence':
      // Replacing or removing a nested sequence changes every clip it holds.
      return clipsOfSequence(doc, op.sequenceId, new Set());
    case 'lift':
    case 'extract':
    case 'trim':
    case 'blade':
    case 'move':
    case 'nudge':
    case 'overwrite':
    case 'replace':
    case 'fitToFill':
    case 'join':
    case 'retime':
      return [op.itemId];
    case 'setProperty':
      return heldKeys.has(op.key) ? [op.itemId] : [];
    case 'removeTrack':
    case 'deleteGap':
    case 'closeGaps': {
      const track = [...doc.tracks, ...(doc.sequences ?? []).flatMap((s) => s.tracks)].find((t) => t.id === op.trackId);
      return track ? track.items.map((i) => i.id) : [];
    }
    default:
      return [];
  }
}

/** The capacity check of the entry's own tool: only magic blur has an effect cap; other tools take any clip. */
export const canApplyFor = (entry: BackgroundEntry): ((clip: MediaClip) => boolean) | undefined => (entry.tool === 'objectEffect' ? canTakeRegionEffect : undefined);

/** The running entries whose target clips `ops` would affect (entries in `allowed` were already waved through). */
export function entriesAffectedBy(ops: readonly EditOp[], doc: EditTimeline, entries: readonly BackgroundEntry[], allowed: ReadonlySet<string>): BackgroundEntry[] {
  // What an edit touches depends on the job: each kind holds the properties it reads (job-protection.ts).
  const touchedFor = new Map<string, Set<string>>();
  const touchedBy = (tool: string): Set<string> => {
    let set = touchedFor.get(tool);
    if (!set) {
      const keys = heldKeysFor(tool);
      set = new Set(ops.flatMap((op) => affectedClipIds(op, doc, keys)));
      touchedFor.set(tool, set);
    }
    return set;
  };
  const present = new Set(allMediaClips(doc).map((c) => c.id));
  return entries.filter((entry) => {
    if (entry.state !== 'running' || allowed.has(entry.id)) return false;
    const outcome = rebaseAnchor(doc, entry.anchor, canApplyFor(entry));
    const ids = new Set(outcome.kind === 'apply' ? outcome.clipIds : outcome.applicable);
    for (const id of entry.anchor.clipIds) if (present.has(id)) ids.add(id);
    const touched = touchedBy(entry.tool);
    return [...ids].some((id) => touched.has(id));
  });
}

/** The running marks (jobs outside the results store, e.g. follow a subject) whose clips `ops` would affect. */
export function marksAffectedBy(ops: readonly EditOp[], doc: EditTimeline, marks: readonly JobMark[], allowed: ReadonlySet<string>): JobMark[] {
  return marks.filter((mark) => {
    if (mark.state !== 'running' || allowed.has(mark.id)) return false;
    const keys = heldKeysFor(mark.jobKind);
    const considered = holdsStructuralEdits(mark.jobKind) ? ops : ops.filter((op) => op.type === 'setProperty');
    const touched = new Set(considered.flatMap((op) => affectedClipIds(op, doc, keys)));
    return mark.clipIds.some((id) => touched.has(id));
  });
}

/**
 * Everything running that would hold `ops`: results-store entries and marks outside the store. `exemptJobId` is the job
 * whose own RESULT the ops are (a job's result is never held by that job's own mark); "go on" ids in `allowed` are waved through.
 */
export function jobsHolding(
  ops: readonly EditOp[],
  doc: EditTimeline,
  entries: readonly BackgroundEntry[],
  marks: readonly JobMark[],
  allowed: ReadonlySet<string>,
  exemptJobId?: string,
): { entries: BackgroundEntry[]; marks: JobMark[] } {
  const waved = exemptJobId ? new Set([...allowed, exemptJobId]) : allowed;
  return { entries: entriesAffectedBy(ops, doc, entries, waved), marks: marksAffectedBy(ops, doc, marks, waved) };
}
