/**
 * Snapping — candidate frames and the resolution of a proposed frame against them.
 *
 * Candidates are every edit point (item start/end), every marker, the playhead and the in/out
 * marks. `snapFrame` returns the nearest candidate within `thresholdFrames`, or the frame unchanged.
 * With snapping disabled (the modifier toggle) it is the identity, so the two code paths differ only
 * in whether the candidate search runs.
 */

import type { EditTimeline } from '../types';
import { editPoints, markerFrames } from '../transport/navigation';

export interface SnapContext {
  playheadFrame: number;
  inFrame: number | null;
  outFrame: number | null;
  /** Items being dragged are excluded, or an item would snap to its own edges. */
  excludeItemIds?: readonly string[];
  /** Extra candidate frames (for example beat-grid lines), unioned in. */
  extraFrames?: readonly number[];
}

export function snapCandidates(doc: EditTimeline, context: SnapContext): number[] {
  const excluded = new Set(context.excludeItemIds ?? []);
  const set = new Set<number>();

  if (excluded.size === 0) {
    for (const frame of editPoints(doc)) set.add(frame);
  } else {
    for (const track of doc.tracks) {
      for (const item of track.items) {
        if (excluded.has(item.id)) continue;
        set.add(item.recordIn);
        set.add(item.recordOut);
      }
    }
  }

  for (const frame of markerFrames(doc)) set.add(frame);
  set.add(context.playheadFrame);
  if (context.inFrame !== null) set.add(context.inFrame);
  if (context.outFrame !== null) set.add(context.outFrame);
  set.add(0);
  for (const frame of context.extraFrames ?? []) if (Number.isFinite(frame)) set.add(frame);

  return [...set].sort((a, b) => a - b);
}

export interface SnapResult {
  frame: number;
  snapped: boolean;
  /** The candidate that won, when one did. */
  candidate: number | null;
}

/** Binary search for the nearest candidate; ties resolve to the earlier frame. */
export function snapFrame(
  frame: number,
  candidates: readonly number[],
  thresholdFrames: number,
  enabled: boolean,
): SnapResult {
  if (!enabled || candidates.length === 0 || thresholdFrames < 0) {
    return { frame, snapped: false, candidate: null };
  }

  let lo = 0;
  let hi = candidates.length - 1;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (candidates[mid] < frame) lo = mid + 1;
    else hi = mid;
  }

  let best = candidates[lo];
  if (lo > 0 && Math.abs(candidates[lo - 1] - frame) <= Math.abs(best - frame)) best = candidates[lo - 1];

  if (Math.abs(best - frame) <= thresholdFrames) {
    return { frame: best, snapped: best !== frame, candidate: best };
  }
  return { frame, snapped: false, candidate: null };
}

/** The snap threshold in frames for a given zoom: ~8 CSS px, at least 1 frame. */
export function snapThresholdFrames(pixelsPerFrame: number, thresholdPx = 8): number {
  if (!Number.isFinite(pixelsPerFrame) || pixelsPerFrame <= 0) return 1;
  return Math.max(1, Math.round(thresholdPx / pixelsPerFrame));
}

/** T-020: what a snap caught, in words a label can say. Pure; the component turns it into copy. */
export type SnapSource =
  | { kind: 'playhead' }
  | { kind: 'marker'; name: string }
  | { kind: 'in' }
  | { kind: 'out' }
  | { kind: 'clipEdge'; trackName: string }
  | { kind: 'start' };

/**
 * Names the thing at `frame` that a snap caught. Looked up only when the guide's frame changes (never per
 * pointer move), so the drag's candidate search stays kind-free and fast. Order: playhead, marker, in, out,
 * clip edge, timeline start.
 */
export function snapSourceAt(doc: EditTimeline, context: SnapContext, frame: number): SnapSource | null {
  if (context.playheadFrame === frame) return { kind: 'playhead' };
  const marker = doc.markers.find((m) => m.frame === frame);
  if (marker) return { kind: 'marker', name: marker.name };
  if (context.inFrame === frame) return { kind: 'in' };
  if (context.outFrame === frame) return { kind: 'out' };
  const excluded = new Set(context.excludeItemIds ?? []);
  for (const track of doc.tracks) {
    for (const item of track.items) {
      if (excluded.has(item.id)) continue;
      if (item.recordIn === frame || item.recordOut === frame) return { kind: 'clipEdge', trackName: track.name || track.id };
    }
  }
  return frame === 0 ? { kind: 'start' } : null;
}
