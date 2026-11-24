/**
 * R03.6 — "Place from media": the four ways a pool source (or its marked range) lands on the
 * timeline, as one pure op batch the store applies as ONE undo step.
 *
 *   - `insert`      at the cut nearest `at`; later clips ripple to make room.
 *   - `overwrite`   exactly at `at`; whatever is under the new clip on the target track is cut away
 *                   (blade + lift, no ripple). The only mode that may remove existing footage.
 *   - `append`      after the last clip of the target track.
 *   - `placeOnTop`  exactly at `at`, on the topmost video (or audio) track that is free there, or on
 *                   a new track above when none is — never touches existing clips.
 *
 * The three-point edit comes from `marks` (the source viewer's In/Out, giving `sourceIn` and length)
 * plus `at` (the timeline playhead). A video with sound brings its sound on a paired audio track,
 * linked with the picture (R08.1) — `lib/editing/media/pool-insert` owns that rule and is reused.
 * Every mode returns where the clip landed in plain language for the "Placed on Video 1 at ..." line.
 */

import type { EditOp } from '../engine';
import type { EditTimeline, MediaClip, Rational, Track } from '../types';
import { framesToTimecode } from '../time';
import { withDetectedProfile } from '../color/auto-profile';
import { clipFromPoolItem, companionAudioOp, hasAudioStream, type IsLocked } from './pool-insert';
import type { Marks } from './in-out';
import type { MediaPoolItem } from './types';

export type PlaceMode = 'insert' | 'overwrite' | 'append' | 'placeOnTop';

export interface PlaceInput {
  /** The pool source; carries `mediaRef` and its `probe`. */
  item: MediaPoolItem;
  mode: PlaceMode;
  /** The timeline frame to place at (the playhead). */
  at: number;
  /** The target track (honouring track targeting); null/absent picks the first unlocked track of the right kind. */
  trackId?: string | null;
  /** The source viewer's In/Out marks. */
  marks?: Marks | null;
  rate: Rational;
  newId: () => string;
  isLocked?: IsLocked;
  /** Overrides the clip's name (Create subclip names it after the marked range). */
  name?: string;
}

export interface LandedAt {
  trackName: string;
  frame: number;
  tc: string;
}

export interface PlaceResult {
  ok: true;
  ops: EditOp[];
  clipId: string;
  companionId: string | null;
  landedAt: LandedAt;
  durationFrames: number;
  lengthKnown: boolean;
  label: string;
}

export interface PlaceFailure {
  ok: false;
  /** Plain-language reason. */
  reason: string;
}

const MODE_LABEL: Record<PlaceMode, string> = { insert: 'Insert', overwrite: 'Overwrite', append: 'Add to end', placeOnTop: 'Place on top' };

/** `V1` reads "Video 1", `A2` "Audio 2"; any other name is kept. */
export function trackDisplayName(track: Pick<Track, 'name' | 'kind'>): string {
  const match = /^([VA])(\d+)$/i.exec(track.name.trim());
  if (!match) return track.name;
  return `${match[1].toUpperCase() === 'V' ? 'Video' : 'Audio'} ${match[2]}`;
}

/** "Placed on Video 1 at 00:00:12:04" */
export function landedAtLine(landed: LandedAt, verb = 'Placed'): string {
  return `${verb} on ${landed.trackName} at ${landed.tc}`;
}

/** Ops that clear `[from, to)` on `track` without moving anything: blade the edges, lift the middle. */
export function clearRangeOps(track: Track, from: number, to: number, newId: () => string): EditOp[] {
  const ops: EditOp[] = [];
  for (const item of track.items) {
    if (item.recordOut <= from || item.recordIn >= to) continue;
    const leftOverhang = item.recordIn < from;
    const rightOverhang = item.recordOut > to;
    if (!leftOverhang && !rightOverhang) {
      ops.push({ type: 'lift', itemId: item.id });
    } else if (leftOverhang && rightOverhang) {
      const middleId = newId();
      ops.push({ type: 'blade', itemId: item.id, atFrame: to, newItemId: newId() });
      ops.push({ type: 'blade', itemId: item.id, atFrame: from, newItemId: middleId });
      ops.push({ type: 'lift', itemId: middleId });
    } else if (leftOverhang) {
      const tailId = newId();
      ops.push({ type: 'blade', itemId: item.id, atFrame: from, newItemId: tailId });
      ops.push({ type: 'lift', itemId: tailId });
    } else {
      ops.push({ type: 'blade', itemId: item.id, atFrame: to, newItemId: newId() });
      ops.push({ type: 'lift', itemId: item.id });
    }
  }
  return ops;
}

/**
 * The cut nearest `at` where a clip of `duration` frames can go in: flush before a following clip
 * (which then ripples) or into a gap that is long enough. The engine refuses an insert that would
 * partly overlap its neighbour, so a cut in front of a short gap is skipped.
 */
function insertPoint(track: Track, at: number, duration: number): number {
  const points = new Set<number>([0]);
  for (const i of track.items) {
    points.add(i.recordIn);
    points.add(i.recordOut);
  }
  let best = -1;
  for (const p of points) {
    const index = track.items.filter((i) => i.recordIn < p).length;
    const previous = index > 0 ? track.items[index - 1] : undefined;
    const following = track.items[index];
    if (previous && previous.recordOut > p) continue;
    if (following && following.recordIn !== p && following.recordIn < p + duration) continue;
    if (best < 0 || Math.abs(p - at) < Math.abs(best - at)) best = p;
  }
  return best < 0 ? (track.items[track.items.length - 1]?.recordOut ?? 0) : best;
}

const isFree = (track: Track, from: number, to: number): boolean => track.items.every((i) => i.recordOut <= from || i.recordIn >= to);

/** The op batch for `mode`; a failure carries a sentence the UI shows as is. */
export function buildPlaceOps(doc: EditTimeline, input: PlaceInput): PlaceResult | PlaceFailure {
  const isLocked: IsLocked = input.isLocked ?? ((t) => t.locked);
  const built = clipFromPoolItem(input.item, input.rate, input.newId, input.marks);
  if (!built) return { ok: false, reason: 'This source cannot be put on the timeline.' };
  const { clip, span } = built;
  if (input.name) clip.name = input.name;

  const kind: Track['kind'] = input.item.kind === 'audio' ? 'audio' : 'video';
  const at = Math.max(0, Math.trunc(input.at));
  const duration = span.durationFrames;

  const requested = input.trackId ? doc.tracks.find((t) => t.id === input.trackId && t.kind === kind) : undefined;
  const target = requested ?? doc.tracks.find((t) => t.kind === kind && !isLocked(t));
  if (input.mode !== 'placeOnTop') {
    if (!target) return { ok: false, reason: `Add an unlocked ${kind} track first.` };
    // A locked target is the engine's refusal to word ("Edit refused"), not ours: the store knows the live flags.
  }

  const wantsCompanion = kind === 'video' && input.item.kind === 'video' && hasAudioStream(input.item) === true;
  const linkId = wantsCompanion ? input.newId() : null;
  if (linkId !== null) clip.linkId = linkId;

  const ops: EditOp[] = [];
  let trackName: string;
  let frame: number;
  let ripple = false;

  if (input.mode === 'append') {
    const track = target as Track;
    const last = track.items[track.items.length - 1];
    frame = last ? last.recordOut : 0;
    trackName = trackDisplayName(track);
    ops.push({ type: 'append', trackId: track.id, item: clip });
  } else if (input.mode === 'insert') {
    const track = target as Track;
    frame = insertPoint(track, at, duration);
    trackName = trackDisplayName(track);
    ripple = true;
    clip.recordIn = frame;
    clip.recordOut = frame + duration;
    ops.push({ type: 'insert', trackId: track.id, index: track.items.filter((i) => i.recordIn < frame).length, item: clip });
  } else if (input.mode === 'overwrite') {
    const track = target as Track;
    frame = at;
    trackName = trackDisplayName(track);
    ops.push(...clearRangeOps(track, frame, frame + duration, input.newId));
    clip.recordIn = frame;
    clip.recordOut = frame + duration;
    // The range is empty after the clears, so the clip drops in with no index and no ripple.
    ops.push({ type: 'placeOnTop', trackId: track.id, item: clip });
  } else {
    frame = at;
    clip.recordIn = frame;
    clip.recordOut = frame + duration;
    const sameKind = doc.tracks.filter((t) => t.kind === kind);
    const free = [...sameKind].reverse().find((t) => !isLocked(t) && isFree(t, frame, frame + duration));
    if (free) {
      trackName = trackDisplayName(free);
      ops.push({ type: 'placeOnTop', trackId: free.id, item: clip });
    } else {
      const last = sameKind[sameKind.length - 1];
      const name = `${kind === 'video' ? 'V' : 'A'}${sameKind.length + 1}`;
      const index = last ? doc.tracks.indexOf(last) + 1 : kind === 'video' ? 0 : doc.tracks.length;
      const fresh: Track = { id: input.newId(), kind, name, height: kind === 'video' ? 64 : 56, locked: false, enabled: true, mute: false, solo: false, items: [clip] };
      trackName = trackDisplayName(fresh);
      ops.push({ type: 'addTrack', index, track: fresh });
    }
  }

  let companionId: string | null = null;
  if (wantsCompanion) {
    const companion: MediaClip = { ...clip, id: input.newId(), recordIn: frame, recordOut: frame + duration };
    ops.push(companionAudioOp(doc, companion, input.newId, { ripple, isLocked: input.isLocked }));
    companionId = companion.id;
  }

  const label = `${MODE_LABEL[input.mode]} ${clip.name}${companionId ? ' with its sound' : ''}`;
  return {
    ok: true,
    ops: withDetectedProfile(ops, input.item.probe, clip.id, input.item.kind),
    clipId: clip.id,
    companionId,
    landedAt: { trackName, frame, tc: framesToTimecode(frame, input.rate) },
    durationFrames: duration,
    lengthKnown: span.lengthKnown,
    label,
  };
}
