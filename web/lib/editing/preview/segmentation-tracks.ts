/**
 * The live preview of a segmentation mask: the analysis's tracked BOXES (not the exact outline the
 * render cuts) interpolated in source time. Pure, so the compositor, the session and the parity
 * badge share one reading of the data.
 *
 * Wire shape: `GET /api/v1/projects/{p}/step-results/{id}/segmentation-tracks`.
 */

import type { EditTimeline, MediaClip, Rational, SegmentationMask } from '../types';

/** One keyframe: `t` in SOURCE seconds, the box as source fractions (top-left origin). */
export type BoxKeyframe = { t: number; x: number; y: number; w: number; h: number };

export type SegmentationTrack = {
  trackId: string;
  label: string;
  firstFrame: number;
  lastFrame: number;
  fps: number;
  boxKeyframes: BoxKeyframe[];
  /** Source seconds from 0 that the track's mask video holds (the binding rule's coverage); absent from an older API. */
  coverageEndSec?: number;
  /** T-006: whether a browser-decodable mask video exists (`ready`), is being made (`pending`) or cannot be (`none`); absent from an older API. */
  maskPreview?: { state: 'ready' | 'pending' | 'none' };
};

export type MatteBox = { x: number; y: number; w: number; h: number };

/** What the compositor needs to matte one layer: the union of `boxes`, optionally inverted, edge feathered in source pixels. */
export type Matte = {
  boxes: readonly MatteBox[];
  invert: boolean;
  /** The render's gblur sigma, in SOURCE pixels. */
  featherPx: number;
  /** The source's width in pixels, when known: the compositor scales the feather to the decoded proxy. */
  sourceWidth?: number;
};

/** The mask-preview video of one track (T-006): `200 video/mp4` when ready, `202 {state:"pending"}` while it is made, `404` = none. */
export function maskPreviewUrl(projectId: string, analysisStepResultId: string, trackId: string): string {
  return `/api/v1/projects/${encodeURIComponent(projectId)}/step-results/${encodeURIComponent(analysisStepResultId)}/segmentation-tracks/${encodeURIComponent(trackId)}/mask-preview`;
}

/** How often a pending mask preview is asked for again, and how many times before the preview falls back to boxes for good. */
export const MASK_PREVIEW_POLL_MS = 2000;
export const MASK_PREVIEW_MAX_TRIES = 30;

/** Looks up an analysis's tracks; `undefined` while loading or when the fetch was refused. */
export type SegmentationTracksLookup = (analysisStepResultId: string) => readonly SegmentationTrack[] | undefined;

/** The compositor draws at most this many boxes per layer (a mask names 1-8 tracks). */
export const MAX_MATTE_BOXES = 8;

const finite = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v);

/**
 * The track's box at `seconds` of source time, linearly interpolated between its keyframes, or `null`
 * outside the track's frame coverage `[firstFrame, lastFrame]` (the render refuses past coverage; the
 * preview shows no matte there) or when it has no usable keyframe.
 */
export function boxAt(track: SegmentationTrack, seconds: number): MatteBox | null {
  const fps = track.fps > 0 ? track.fps : 0;
  if (fps === 0 || !finite(seconds)) return null;
  // Half a frame of slack either side: the last frame covers its whole duration.
  if (seconds < track.firstFrame / fps - 0.5 / fps || seconds > track.lastFrame / fps + 0.5 / fps) return null;
  const keys = track.boxKeyframes.filter((k) => finite(k.t) && finite(k.x) && finite(k.y) && finite(k.w) && finite(k.h));
  if (keys.length === 0) return null;
  const sorted = keys.length > 1 && keys.some((k, i) => i > 0 && k.t < keys[i - 1].t) ? [...keys].sort((a, b) => a.t - b.t) : keys;
  const first = sorted[0];
  const last = sorted[sorted.length - 1];
  if (seconds <= first.t) return { x: first.x, y: first.y, w: first.w, h: first.h };
  if (seconds >= last.t) return { x: last.x, y: last.y, w: last.w, h: last.h };
  let hi = 1;
  while (hi < sorted.length - 1 && sorted[hi].t < seconds) hi += 1;
  const a = sorted[hi - 1];
  const b = sorted[hi];
  const u = b.t > a.t ? (seconds - a.t) / (b.t - a.t) : 0;
  const lerp = (p: number, q: number) => p + (q - p) * u;
  return { x: lerp(a.x, b.x), y: lerp(a.y, b.y), w: lerp(a.w, b.w), h: lerp(a.h, b.h) };
}

/** True when at least one of the mask's tracks has box keyframes (otherwise there is nothing to preview). */
export function hasBoxData(mask: SegmentationMask, lookup: SegmentationTracksLookup | undefined): boolean {
  const tracks = lookup?.(mask.analysisStepResultId);
  if (!tracks) return false;
  return tracks.some((t) => mask.trackIds.includes(t.trackId) && t.boxKeyframes.length > 0);
}

/**
 * The matte for a segmentation mask at `seconds` of source time: the union of its named tracks'
 * boxes. When the object is absent at that moment (outside every track's first/last non-empty frame)
 * the matte is EMPTY - zero boxes, so coverage 0, or the whole picture when inverted - because the
 * render's mask video is empty there. `null` (draw unmatted) only when the tracks are not loaded or
 * none of the named tracks has box data at all (the badge then says the preview has no box).
 */
export function matteFor(mask: SegmentationMask, lookup: SegmentationTracksLookup | undefined, seconds: number): Matte | null {
  const tracks = lookup?.(mask.analysisStepResultId);
  if (!tracks) return null;
  const boxes: MatteBox[] = [];
  for (const track of tracks) {
    if (!mask.trackIds.includes(track.trackId)) continue;
    const box = boxAt(track, seconds);
    if (box && boxes.length < MAX_MATTE_BOXES) boxes.push(box);
  }
  if (boxes.length === 0 && !hasBoxData(mask, lookup)) return null;
  return { boxes, invert: mask.invert === true, featherPx: Math.min(64, Math.max(0, finite(mask.featherPx) ? mask.featherPx : 0)) };
}

/**
 * A box of the unrotated source, as it sits in the picture after the clip's `sourceRotation` (clockwise degrees).
 * The render mattes at source geometry and rotates afterwards (RegionFilterGraphBuilder: mask and region effects on
 * the decoded source, then `SourceRotationFilter`); the preview decodes the picture already rotated, so the boxes
 * turn with it. A point (u, v) goes to (1 - v, u) per quarter turn.
 */
export function rotateMatteBox(box: MatteBox, rotation: number | null | undefined): MatteBox {
  switch (((Math.round((rotation ?? 0) / 90) % 4) + 4) % 4) {
    case 1:
      return { x: 1 - (box.y + box.h), y: box.x, w: box.h, h: box.w };
    case 2:
      return { x: 1 - (box.x + box.w), y: 1 - (box.y + box.h), w: box.w, h: box.h };
    case 3:
      return { x: box.y, y: 1 - (box.x + box.w), w: box.h, h: box.w };
    default:
      return box;
  }
}

/** {@link rotateMatteBox} for every box of a matte; the same matte when there is no rotation. */
export function rotateMatte(matte: Matte, rotation: number | null | undefined): Matte {
  if (!rotation || ((Math.round(rotation / 90) % 4) + 4) % 4 === 0) return matte;
  return { ...matte, boxes: matte.boxes.map((b) => rotateMatteBox(b, rotation)) };
}

/** The distinct analyses the document's segmentation masks name (so the editor fetches only those). */
export function segmentationAnalysesOf(doc: EditTimeline): string[] {
  const ids = new Set<string>();
  for (const track of doc.tracks) {
    for (const item of track.items) {
      if (item.type !== 'mediaClip') continue;
      const mask = item.mask;
      if (mask && mask.kind === 'segmentation') ids.add(mask.analysisStepResultId);
      // Region effects (magic blur) follow an object too: their analyses load so the preview has a box (or mask video) for them.
      for (const e of item.regionEffects ?? []) if (e?.mask?.analysisStepResultId) ids.add(e.mask.analysisStepResultId);
    }
  }
  return [...ids].sort();
}

/**
 * Whether the render may bind the mask to the clip, by the ONE rule the API validator and the render share,
 * `SegmentationMaskBinding.Check` (inference/src/ReelBolt.Shared/Editing/SegmentationMaskBinding.cs:73-82):
 * for EVERY named track, the clip's whole source span must end no later than the track's coverage plus one
 * frame. With `fps` the timeline rate, `rate = playbackRate > 0 ? playbackRate : 1`,
 * `start = sourceIn / fps`, `end = start + (hold ? 1 / fps : (recordOut - recordIn) * rate / fps)`, a track
 * refuses the clip when `end > coverageEndSec + 1 / fps` (line 78). The coverage is `FrameCount / Fps` from
 * source 0 (SegmentationMaskBinding.cs:15, `SegmentationTrackFacts.CoverageEndOf`), served as `coverageEndSec`.
 * The other refusals (different source, trimmed analysis, upright) need facts the API does not serve: the
 * render and the validator still apply them.
 *
 * `false`: refused (the render refuses the clip, so the preview draws no picture); `true`: covered;
 * `undefined`: tracks (or a named track's coverage) are not known.
 */
export function maskBindsClip(clip: Pick<MediaClip, 'sourceIn' | 'recordIn' | 'recordOut' | 'playbackRate' | 'hold'>, mask: SegmentationMask, lookup: SegmentationTracksLookup | undefined, rate: Rational): boolean | undefined {
  const tracks = lookup?.(mask.analysisStepResultId);
  const fps = rate.den > 0 ? rate.num / rate.den : 0;
  if (!tracks || !(fps > 0)) return undefined;
  const playbackRate = typeof clip.playbackRate === 'number' && clip.playbackRate > 0 ? clip.playbackRate : 1;
  const start = (clip.sourceIn ?? 0) / fps;
  const end = start + (clip.hold === true ? 1 / fps : ((clip.recordOut - clip.recordIn) * playbackRate) / fps);
  let covered = true;
  for (const id of mask.trackIds) {
    const track = tracks.find((t) => t.trackId === id);
    if (!track || !finite(track.coverageEndSec)) return undefined;
    if (end > track.coverageEndSec + 1 / fps) covered = false;
  }
  return covered;
}
