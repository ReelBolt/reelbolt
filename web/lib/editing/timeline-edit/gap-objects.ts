import { applyAll, type EditOp } from '../engine';
import { partnersOf, type LinkOptions } from '../link/groups';
import type { EditTimeline } from '../types';

/**
 * R03.1: a gap as an object the user can click. A gap is the empty span between two neighbouring
 * items on one track, or between frame 0 and a first item that starts later. The empty space after
 * the last item is not a gap (there is nothing to close up).
 */
export interface TimelineGap {
  trackId: string;
  /** First empty frame. */
  start: number;
  /** First frame after the gap (where the next item starts). */
  end: number;
}

/** Every gap on every track, in track then time order. */
export function listGaps(doc: EditTimeline): TimelineGap[] {
  const gaps: TimelineGap[] = [];
  for (const track of doc.tracks) {
    let cursor = 0;
    for (const item of track.items) {
      if (item.recordIn > cursor) gaps.push({ trackId: track.id, start: cursor, end: item.recordIn });
      cursor = Math.max(cursor, item.recordOut);
    }
  }
  return gaps;
}

/** The gap on a track that contains `frame`, or null when that frame is inside an item or past the last one. */
export function gapAt(doc: EditTimeline, trackId: string, frame: number): TimelineGap | null {
  return listGaps(doc).find((g) => g.trackId === trackId && frame >= g.start && frame < g.end) ?? null;
}

/** True when the gap still exists, exactly as selected (an edit may have closed or resized it). */
export function gapStillExists(doc: EditTimeline, gap: TimelineGap): boolean {
  const found = gapAt(doc, gap.trackId, gap.start);
  return found !== null && found.start === gap.start && found.end === gap.end;
}

/** Interior gaps on one track: what "Close all gaps" removes. */
export function interiorGapCount(doc: EditTimeline, trackId: string): number {
  const track = doc.tracks.find((t) => t.id === trackId);
  if (!track) return 0;
  return listGaps(doc).filter((g) => g.trackId === trackId && g.start > 0).length;
}

/**
 * Delete on a selected gap: later items on the track shift left by the gap's length. One op, plus, when a document is
 * given, the same op on every track that holds a linked partner of a shifted clip AND has the very same gap (a camera
 * sound beside its picture), so the pair stays in step. A partner on a locked track is left alone.
 */
export function buildDeleteGapOps(gap: TimelineGap, doc?: EditTimeline, options?: LinkOptions): EditOp[] {
  const ops: EditOp[] = [{ type: 'deleteGap', trackId: gap.trackId, atFrame: gap.start }];
  const track = doc?.tracks.find((t) => t.id === gap.trackId);
  if (!doc || !track) return ops;
  const partnerTracks = new Set<string>();
  for (const item of track.items) {
    if (item.recordIn < gap.end) continue;
    for (const partner of partnersOf(doc, item.id)) if (partner.track.id !== gap.trackId) partnerTracks.add(partner.track.id);
  }
  for (const trackId of partnerTracks) {
    if (options?.isLocked?.(trackId)) continue;
    const other = gapAt(doc, trackId, gap.start);
    if (other && other.start === gap.start && other.end === gap.end) ops.push({ type: 'deleteGap', trackId, atFrame: gap.start });
  }
  return ops;
}

/**
 * "Close all gaps" on a track. Empty when the track has none (so the caller can say so). Linked partners with the very same
 * gaps close with it (see {@link buildDeleteGapOps}); with none to follow it stays the single `closeGaps` op.
 */
export function buildCloseAllGapsOps(doc: EditTimeline, trackId: string, options?: LinkOptions): EditOp[] {
  if (interiorGapCount(doc, trackId) === 0) return [];
  const stepwise: EditOp[] = [];
  let current = doc;
  for (let guard = 0; guard < 500; guard++) {
    const gap = listGaps(current).find((g) => g.trackId === trackId && g.start > 0);
    if (!gap) break;
    const step = buildDeleteGapOps(gap, current, options);
    stepwise.push(...step);
    current = applyAll(current, step);
  }
  return stepwise.every((op) => op.type === 'deleteGap' && op.trackId === trackId) ? [{ type: 'closeGaps', trackId }] : stepwise;
}
