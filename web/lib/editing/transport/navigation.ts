/**
 * Transport navigation — pure functions over the document for "jump to previous/next edit,
 * marker, clip edge" and for frame stepping. All frames are timeline frames.
 */

import type { EditTimeline } from '../types';

/** Every edit point (an item's start or end) on the given tracks, ascending and unique. */
export function editPoints(doc: EditTimeline, trackIds?: readonly string[]): number[] {
  const set = new Set<number>();
  for (const track of doc.tracks) {
    if (trackIds && !trackIds.includes(track.id)) continue;
    for (const item of track.items) {
      set.add(item.recordIn);
      set.add(item.recordOut);
    }
  }
  return [...set].sort((a, b) => a - b);
}

/** Every marker frame, ascending and unique. */
export function markerFrames(doc: EditTimeline): number[] {
  return [...new Set(doc.markers.map((m) => m.frame))].sort((a, b) => a - b);
}

/** The last frame any item reaches (exclusive), or 0 for an empty timeline. */
export function timelineEndFrame(doc: EditTimeline): number {
  let end = 0;
  for (const track of doc.tracks) {
    for (const item of track.items) end = Math.max(end, item.recordOut);
  }
  return end;
}

/** The smallest candidate strictly greater than `from`, or `null`. */
export function nextAfter(candidates: readonly number[], from: number): number | null {
  for (const candidate of candidates) {
    if (candidate > from) return candidate;
  }
  return null;
}

/** The largest candidate strictly less than `from`, or `null`. */
export function previousBefore(candidates: readonly number[], from: number): number | null {
  for (let i = candidates.length - 1; i >= 0; i--) {
    if (candidates[i] < from) return candidates[i];
  }
  return null;
}

export function nextEdit(doc: EditTimeline, from: number, trackIds?: readonly string[]): number | null {
  return nextAfter(editPoints(doc, trackIds), from);
}

export function previousEdit(doc: EditTimeline, from: number, trackIds?: readonly string[]): number | null {
  return previousBefore(editPoints(doc, trackIds), from);
}

export function nextMarker(doc: EditTimeline, from: number): number | null {
  return nextAfter(markerFrames(doc), from);
}

export function previousMarker(doc: EditTimeline, from: number): number | null {
  return previousBefore(markerFrames(doc), from);
}

/** `frame + delta`, clamped to `[0, maxFrame]`. */
export function stepFrame(frame: number, delta: number, maxFrame: number): number {
  return Math.max(0, Math.min(Math.max(0, maxFrame), frame + delta));
}
