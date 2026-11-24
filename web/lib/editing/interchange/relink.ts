/**
 * R12.2 (journey 3): relinking an offline clip after an import.
 *
 * An import whose media this project does not have leaves a hole and a red marker named
 * "Offline: <name>" where each clip was (the document has no offline clip type). Relinking is "place
 * a project file at that marker" with the ops the editor already has: `overwrite` of the chosen file at the
 * marker's frame (cut to the hole's length so a longer file never eats the neighbours) and
 * `removeMarker`, as ONE undo step.
 */

import type { EditOp } from '../engine';
import type { EditTimeline, Marker, Rational } from '../types';
import { buildPlaceOps, type PlaceResult } from '../media/place';
import { framesFromSeconds, rationalOf } from '../media/in-out';
import { poolClipSpan, type IsLocked } from '../media/pool-insert';
import type { MediaPoolItem } from '../media/types';

export const OFFLINE_PREFIX = 'Offline: ';

export interface OfflineMarker {
  markerId: string;
  /** The clip's name as the file called it. */
  name: string;
  frame: number;
  durationFrames: number;
}

/** The offline markers of a document, in timeline order. */
export function offlineMarkers(doc: Pick<EditTimeline, 'markers'>): OfflineMarker[] {
  return (doc.markers ?? [])
    .filter((m) => m.name.startsWith(OFFLINE_PREFIX))
    .map((m) => ({ markerId: m.id, name: m.name.slice(OFFLINE_PREFIX.length), frame: m.frame, durationFrames: Math.max(1, m.durationFrames ?? 1) }))
    .sort((a, b) => a.frame - b.frame);
}

/** The plain sentence listing what is offline. */
export function offlineMessage(items: readonly OfflineMarker[]): string {
  const names = items.map((i) => i.name).join(', ');
  return items.length === 1
    ? `1 clip could not be found in this project: ${names}. Its place is marked in red on the timeline.`
    : `${items.length} clips could not be found in this project: ${names}. Their places are marked in red on the timeline.`;
}

export interface RelinkInput {
  marker: Pick<Marker, 'id' | 'frame' | 'durationFrames'>;
  item: MediaPoolItem;
  trackId: string | null;
  rate: Rational;
  newId: () => string;
  isLocked?: IsLocked;
}

export type RelinkResult = { ok: true; ops: EditOp[]; label: string; place: PlaceResult } | { ok: false; reason: string };

export function buildRelinkOps(doc: EditTimeline, input: RelinkInput): RelinkResult {
  const hole = Math.max(1, input.marker.durationFrames ?? 1);
  const natural = poolClipSpan(input.item, input.rate);
  let marks = null;
  const sourceRate = rationalOf(input.item.probe);
  if (input.item.kind !== 'image' && natural.durationFrames > hole && sourceRate) {
    const outFrame = framesFromSeconds((hole * input.rate.den) / input.rate.num, sourceRate);
    if (outFrame !== null && outFrame > 0) marks = { inFrame: 0, outFrame };
  }
  const placed = buildPlaceOps(doc, { item: input.item, mode: 'overwrite', at: input.marker.frame, trackId: input.trackId, marks, rate: input.rate, newId: input.newId, isLocked: input.isLocked });
  if (!placed.ok) return { ok: false, reason: placed.reason };
  return { ok: true, ops: [...placed.ops, { type: 'removeMarker', markerId: input.marker.id }], label: 'Relink offline clip', place: placed };
}
