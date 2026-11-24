/**
 * T-012 — "Place over": a source lands as a picture-in-picture ABOVE the pictures already on the timeline.
 *
 * Every other placement targets the first video track, and a clip on a higher track is full-frame, so it
 * simply hides what is below. "Place over" puts the clip at `at` on the first video track ABOVE the topmost
 * track that has a picture there (a new track on top when none above is free), shrunk into the bottom-right
 * corner inside the 5% safe margin, in one batch (= one undo step). With no picture there at all there is
 * nothing to be over: the clip is placed like "Place on top", full-frame, no preset.
 *
 * Pure: it reuses `buildPlaceOps` for the clip, the companion sound and the new-track op.
 */

import type { EditOp } from '../engine';
import type { ClipTransform, EditTimeline, Item, MediaClip, Track } from '../types';
import { baseSize, padValues, type TransformValues } from '../viewer-transform/geometry';
import { buildPlaceOps, type PlaceFailure, type PlaceInput, type PlaceResult } from './place';

/** The picture-in-picture size relative to the canvas fit, and its margin from the canvas edge (the 5% safe margin). */
export const PIP_SCALE = 0.33;
export const PIP_MARGIN = 0.05;

export interface PlaceOverInput extends Omit<PlaceInput, 'mode' | 'trackId'> {
  canvas: { width: number; height: number };
  aspectPolicy: 'fit' | 'fill' | 'stretch';
}

export interface PlaceOverResult extends PlaceResult {
  /** The id of the track the clip landed on (new or existing). */
  trackId: string;
  /** True when a new video track was added on top. */
  addedTrack: boolean;
  /** True when the corner preset was applied (false when no picture was under the clip). */
  pip: boolean;
}

const overlaps = (item: Item, from: number, to: number): boolean => item.recordIn < to && item.recordOut > from;

/** The bottom-right picture-in-picture values for a picture of `source` size on `canvas`. */
export function pipValues(canvas: { width: number; height: number }, source: { width: number; height: number } | null, aspectPolicy: 'fit' | 'fill' | 'stretch', crop?: MediaClip['crop']): TransformValues {
  const base = baseSize(canvas, source, aspectPolicy, crop ?? null);
  const fit = Math.min(canvas.width / base.w, canvas.height / base.h);
  const upright: TransformValues = { positionX: 0, positionY: 0, rotation: 0, scale: Math.round(PIP_SCALE * fit * 1e6) / 1e6 };
  return padValues(upright, base, canvas, 1, 1, PIP_MARGIN);
}

/** A whole-clip `transform` value for {@link pipValues} (static channels). */
export function pipTransform(values: TransformValues): ClipTransform {
  return {
    positionX: { value: values.positionX, keys: null },
    positionY: { value: values.positionY, keys: null },
    scale: { value: values.scale, keys: null },
  } as ClipTransform;
}

/** The `setProperty` op that makes `clip` a corner picture-in-picture. Keeps any other transform channel (opacity). */
export function pipOps(clip: MediaClip, values: TransformValues): EditOp[] {
  const next = { ...(clip.transform ?? {}), ...pipTransform(values) } as ClipTransform;
  return [{ type: 'setProperty', itemId: clip.id, key: 'transform', value: next }];
}

/**
 * The first video track above every track that has a picture in `[from, to)` and is itself free there and
 * unlocked; `null` when there is none above. `occupied` is false when no video track has a picture in the range.
 */
export function placeOverTrack(doc: EditTimeline, from: number, to: number, isLocked: (t: Track) => boolean = (t) => t.locked): { track: Track | null; occupied: boolean } {
  const videos = doc.tracks.filter((t) => t.kind === 'video');
  let top = -1;
  videos.forEach((t, i) => {
    if (t.items.some((item) => item.type !== 'transition' && overlaps(item, from, to))) top = i;
  });
  if (top < 0) return { track: null, occupied: false };
  const track = videos.slice(top + 1).find((t) => !isLocked(t) && !t.items.some((item) => overlaps(item, from, to))) ?? null;
  return { track, occupied: true };
}

/** True when the clip `clipId` sits on a video track above another video track that shows a picture during the clip. */
export function coversLowerPicture(doc: EditTimeline, clipId: string): boolean {
  const videos = doc.tracks.filter((t) => t.kind === 'video');
  const at = videos.findIndex((t) => t.items.some((i) => i.id === clipId));
  if (at <= 0) return false;
  const clip = videos[at].items.find((i) => i.id === clipId);
  if (!clip || clip.type !== 'mediaClip') return false;
  return videos.slice(0, at).some((t) => t.enabled && t.items.some((item) => item.type !== 'transition' && overlaps(item, clip.recordIn, clip.recordOut)));
}

/** The clip item a batch places (inside a `placeOnTop` or a fresh `addTrack`). */
function clipItemOf(ops: readonly EditOp[], clipId: string): Item | undefined {
  for (const op of ops) {
    if (op.type === 'placeOnTop' && op.item.id === clipId) return op.item;
    if (op.type === 'addTrack') {
      const hit = op.track.items.find((i) => i.id === clipId);
      if (hit) return hit;
    }
  }
  return undefined;
}

/** The track a batch lands `clipId` on ('' when the batch has no placement op). */
function landedTrackId(ops: readonly EditOp[], clipId: string): string {
  for (const op of ops) {
    if (op.type === 'placeOnTop' && op.item.id === clipId) return op.trackId;
    if (op.type === 'addTrack' && op.track.items.some((i) => i.id === clipId)) return op.track.id;
  }
  return '';
}

export function buildPlaceOverOps(doc: EditTimeline, input: PlaceOverInput): PlaceOverResult | PlaceFailure {
  if (input.item.kind === 'audio') return { ok: false, reason: 'Only a picture can be placed over.' };
  const isLocked = input.isLocked ?? ((t: Track) => t.locked);
  const { canvas, aspectPolicy, ...base } = input;
  // The first build only measures the clip (its length on the timeline).
  const measured = buildPlaceOps(doc, { ...base, mode: 'placeOnTop' });
  if (!measured.ok) return measured;
  const at = Math.max(0, Math.trunc(input.at));
  const { track, occupied } = placeOverTrack(doc, at, at + measured.durationFrames, isLocked);

  let placed: PlaceResult | PlaceFailure;
  let addedTrack = false;
  if (!occupied) {
    placed = measured;
  } else if (track) {
    // Free over the range, so the overwrite clears nothing: it is a plain drop onto that track.
    placed = buildPlaceOps(doc, { ...base, mode: 'overwrite', trackId: track.id });
  } else {
    // Every video track above is busy or locked: a new track on top (all existing video tracks read as locked, so "Place on top" adds one).
    placed = buildPlaceOps(doc, { ...base, mode: 'placeOnTop', isLocked: (t) => (t.kind === 'video' ? true : isLocked(t)) });
    addedTrack = true;
  }
  if (!placed.ok) return placed;

  const clipOp = clipItemOf(placed.ops, placed.clipId);
  const ops = [...placed.ops];
  const source = input.item.probe && input.item.probe.width && input.item.probe.height ? { width: input.item.probe.width, height: input.item.probe.height } : null;
  if (occupied && clipOp && clipOp.type === 'mediaClip') ops.push(...pipOps(clipOp, pipValues(canvas, source, aspectPolicy, clipOp.crop)));

  const label = occupied ? placed.label.replace(/^(Overwrite|Place on top)/, 'Place over') : placed.label;
  return { ...placed, ops, label, trackId: landedTrackId(placed.ops, placed.clipId), addedTrack, pip: occupied };
}
