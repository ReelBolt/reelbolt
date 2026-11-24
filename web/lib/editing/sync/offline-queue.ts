/**
 * P9: the offline op queue and its reconciliation.
 *
 * When the network goes away the editor keeps editing locally; the sync client persists ONE queue
 * entry per timeline — the revision the edits were made against (`baseRevision`), the document at
 * that revision (`baseDoc`), the ops applied since, and the local result. On reconnect the entry is
 * reconciled against what the server now holds:
 *
 *   - **fast-forward** — the server is still at `baseRevision`: the queued PATCH is sent as it is.
 *   - **rebased** — the server moved on, and the queued ops can be replayed on top of it. "Can" is
 *     PROVEN, never assumed: (1) every op on both sides is an engine op (a server-side `restore`
 *     replaced the document wholesale and is never rebased over); (2) replaying the local ops on
 *     `baseDoc` reproduces the queued local document, and replaying the server's ops on `baseDoc`
 *     reproduces the server's document — so both logs are exactly what they claim; (3) the two logs
 *     touch disjoint items/tracks/document parts (a cheap, conservative pre-filter); (4) the two
 *     orders COMMUTE — `server ∘ local` and `local ∘ server` from `baseDoc` produce the same document
 *     — and the result satisfies `assertInvariants`. Only then is the rebased document saved, against
 *     the server's revision.
 *   - **refused** — anything else, with the reason. Nothing is applied; the queued edits stay in
 *     storage and the editor goes read-only with a way to download them.
 *
 * This is the guard against the failure the engine exists to prevent: a queued op replayed blind
 * against a newer revision would apply an inverse program computed for a document that no longer
 * exists. Undo history does not survive a rebase (its inverse programs belong to the old base).
 */

import { applyAll, assertInvariants, EditEngineError, type EditOp } from '../engine';
import type { EditTimeline } from '../types';
import type { WireOp } from './api';
import { QUEUE_PREFIX } from './user-scope';

export const QUEUE_VERSION = 1;

export interface QueueEntry {
  version: typeof QUEUE_VERSION;
  projectId: string;
  timelineId: string;
  baseRevision: number;
  baseDoc: EditTimeline;
  localDoc: EditTimeline;
  ops: WireOp[];
  savedAt: number;
  /** Whose edits these are (the opaque per-user scope, never the email). Absent on entries from older versions: treated as another user's. */
  userId?: string;
}

export interface QueueStorage {
  get(key: string): string | null;
  set(key: string, value: string): void;
  remove(key: string): void;
  /** Every key starting with `prefix` (the journal's expiry sweep). Absent = the store cannot be listed. */
  keys?(prefix: string): string[];
}

/** Per user: `reelbolt:editor:offline-queue:{scope}:{timelineId}`; another user never reaches this key. */
export const queueKey = (userId: string, timelineId: string): string => `${QUEUE_PREFIX}${userId}:${timelineId}`;
/** The pre-per-user key (no user in it): never read, deleted on sight. */
const legacyQueueKey = (timelineId: string): string => `${QUEUE_PREFIX}${timelineId}`;

/** `localStorage`, every access wrapped: a private window or blocked storage degrades to "no queue". */
export const browserQueueStorage: QueueStorage = {
  get: (key) => {
    try {
      return typeof window === 'undefined' ? null : window.localStorage.getItem(key);
    } catch {
      return null;
    }
  },
  set: (key, value) => {
    try {
      if (typeof window !== 'undefined') window.localStorage.setItem(key, value);
    } catch {
      // Storage full or blocked: the queue lives only in memory for this tab.
    }
  },
  remove: (key) => {
    try {
      if (typeof window !== 'undefined') window.localStorage.removeItem(key);
    } catch {
      // ignore
    }
  },
  keys: (prefix) => {
    const out: string[] = [];
    try {
      if (typeof window === 'undefined') return out;
      for (let i = 0; i < window.localStorage.length; i++) {
        const key = window.localStorage.key(i);
        if (key && key.startsWith(prefix)) out.push(key);
      }
    } catch {
      // blocked storage: nothing to list
    }
    return out;
  },
};

export function readQueue(storage: QueueStorage, userId: string, timelineId: string): QueueEntry | null {
  storage.remove(legacyQueueKey(timelineId));
  const raw = storage.get(queueKey(userId, timelineId));
  if (!raw) return null;
  try {
    const parsed = JSON.parse(raw) as QueueEntry;
    return parsed?.version === QUEUE_VERSION && parsed.userId === userId && parsed.timelineId === timelineId && Array.isArray(parsed.ops) ? parsed : null;
  } catch {
    return null;
  }
}

export type Reconciliation =
  | { kind: 'fastForward' }
  | { kind: 'rebased'; doc: EditTimeline; ops: EditOp[] }
  | { kind: 'refused'; reason: string };

const isEngineOp = (op: WireOp): op is EditOp => op.type !== 'restore';

/** Order-insensitive structural equality (key order differs between equal documents after edits). */
export function sameDocument(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (a === null || b === null || typeof a !== 'object' || typeof b !== 'object') {
    // `undefined` keys are treated as absent by the object branch below.
    return false;
  }
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  if (Array.isArray(a)) {
    const bb = b as unknown[];
    return a.length === bb.length && a.every((v, i) => sameDocument(v, bb[i]));
  }
  const ao = a as Record<string, unknown>;
  const bo = b as Record<string, unknown>;
  const keys = new Set([...Object.keys(ao).filter((k) => ao[k] !== undefined), ...Object.keys(bo).filter((k) => bo[k] !== undefined)]);
  for (const key of keys) if (!sameDocument(ao[key], bo[key])) return false;
  return true;
}

/**
 * The parts of a document an op touches, conservatively: an op that can move other items (ripple,
 * structure) claims its whole TRACK; one that only changes an item's own fields claims that ITEM;
 * markers, settings, in/out, groups, sequences and buses are claimed by name. An item looked up in
 * none of `docs` makes the op claim everything (`*`).
 */
export function touchedParts(op: EditOp, docs: readonly EditTimeline[]): Set<string> {
  const parts = new Set<string>();
  const trackOf = (itemId: string): string | null => {
    for (const doc of docs) for (const track of doc.tracks) if (track.items.some((i) => i.id === itemId)) return track.id;
    return null;
  };
  const claimTrackOfItem = (itemId: string) => {
    const track = trackOf(itemId);
    if (track === null) parts.add('*');
    else parts.add(`track:${track}`);
    parts.add(`item:${itemId}`);
  };
  switch (op.type) {
    case 'setProperty':
      parts.add(`item:${op.itemId}`);
      break;
    case 'setKeyframe':
    case 'removeKeyframe':
    case 'moveKeyframe':
      if ('trackId' in op.target) parts.add(`trackfader:${op.target.trackId}`);
      else parts.add(`item:${op.target.itemId}`);
      break;
    case 'insert':
    case 'append':
    case 'placeOnTop':
    case 'deleteGap':
    case 'closeGaps':
      parts.add(`track:${op.trackId}`);
      break;
    case 'move':
      claimTrackOfItem(op.itemId);
      parts.add(`track:${op.toTrackId}`);
      break;
    case 'addTransition':
      claimTrackOfItem(op.afterItemId);
      break;
    case 'removeTransition':
      claimTrackOfItem(op.transitionId);
      break;
    case 'overwrite':
    case 'lift':
    case 'extract':
    case 'trim':
    case 'blade':
    case 'replace':
    case 'fitToFill':
    case 'join':
    case 'nudge':
    case 'retime':
      claimTrackOfItem(op.itemId);
      break;
    case 'addTrack':
    case 'removeTrack':
    case 'moveTrack':
      // Track ORDER is shared by every track op that names an index.
      parts.add('tracks');
      if (op.type === 'addTrack') parts.add(`track:${op.track.id}`);
      else parts.add(`track:${op.trackId}`);
      break;
    case 'setTrackProperty':
      parts.add(`trackprop:${op.trackId}:${op.key}`);
      break;
    case 'addMarker':
    case 'setMarker':
    case 'removeMarker':
      parts.add('markers');
      break;
    case 'setTimelineSettings':
      parts.add('settings');
      break;
    case 'setInOut':
      parts.add('inOut');
      break;
    case 'setColorGroup':
      parts.add(`colorGroup:${op.groupId}`);
      parts.add('colorGroups');
      break;
    case 'setSequence':
      parts.add(`sequence:${op.sequenceId}`);
      parts.add('sequences');
      break;
    case 'setBus':
      parts.add(`bus:${op.busId}`);
      parts.add('buses');
      break;
    default:
      parts.add('*');
  }
  return parts;
}

function partsOf(ops: readonly EditOp[], docs: readonly EditTimeline[]): Set<string> {
  const all = new Set<string>();
  for (const op of ops) for (const part of touchedParts(op, docs)) all.add(part);
  return all;
}

function tryApply(doc: EditTimeline, ops: readonly EditOp[]): EditTimeline | null {
  try {
    return applyAll(doc, ops);
  } catch {
    // Any failure — an EditEngineError refusal, and equally a TypeError from a malformed op — is a
    // refusal here. Reconciliation must never let an op throw through it.
    return null;
  }
}

/**
 * Reconciles a queued entry against the server's current state. `serverOps` are the server's op
 * logs for revisions `baseRevision + 1 … server.revision`, in order — or `null` when they could not
 * all be read (then a moved server is always refused).
 */
export function reconcileQueue(entry: Pick<QueueEntry, 'baseRevision' | 'baseDoc' | 'localDoc' | 'ops'>, server: { revision: number; doc: EditTimeline }, serverOps: readonly WireOp[] | null): Reconciliation {
  if (server.revision === entry.baseRevision) return { kind: 'fastForward' };
  if (server.revision < entry.baseRevision) return { kind: 'refused', reason: `the server is at revision ${server.revision}, older than the ${entry.baseRevision} these edits were made on` };
  if (serverOps === null) return { kind: 'refused', reason: 'the changes made elsewhere could not be read in full' };
  if (!entry.ops.every(isEngineOp)) return { kind: 'refused', reason: 'the queued edits include a restore, which replaces the whole document' };
  if (!serverOps.every(isEngineOp)) return { kind: 'refused', reason: 'a revision restored elsewhere replaced the whole document' };
  const local = entry.ops as EditOp[];
  const remote = serverOps as EditOp[];

  const localReplay = tryApply(entry.baseDoc, local);
  if (localReplay === null || !sameDocument(localReplay, entry.localDoc)) return { kind: 'refused', reason: 'the queued edits no longer reproduce the queued document' };
  const remoteReplay = tryApply(entry.baseDoc, remote);
  if (remoteReplay === null || !sameDocument(remoteReplay, server.doc)) return { kind: 'refused', reason: 'the changes made elsewhere do not reproduce the server document' };

  const docs = [entry.baseDoc, entry.localDoc, server.doc];
  const localParts = partsOf(local, docs);
  const remoteParts = partsOf(remote, docs);
  const overlap = [...localParts].filter((p) => remoteParts.has(p) || p === '*' || remoteParts.has('*'));
  if (overlap.length > 0) return { kind: 'refused', reason: `your offline edits and the changes made elsewhere touch the same ${describePart(overlap[0])}` };

  const rebased = tryApply(server.doc, local);
  if (rebased === null) return { kind: 'refused', reason: 'your offline edits cannot be applied on top of the newer revision' };
  const otherOrder = tryApply(entry.localDoc, remote);
  if (otherOrder === null || !sameDocument(rebased, otherOrder)) return { kind: 'refused', reason: 'your offline edits and the changes made elsewhere do not commute' };
  try {
    assertInvariants(rebased);
  } catch (error) {
    return { kind: 'refused', reason: error instanceof Error ? error.message : String(error) };
  }
  return { kind: 'rebased', doc: rebased, ops: local };
}

function describePart(part: string): string {
  if (part === '*') return 'part of the timeline';
  const [kind] = part.split(':');
  switch (kind) {
    case 'track':
      return 'track';
    case 'item':
      return 'clip';
    case 'trackprop':
    case 'trackfader':
      return 'track setting';
    default:
      return part.replace(/:.*/, '');
  }
}
