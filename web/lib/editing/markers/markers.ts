/**
 * Markers — the document's own markers plus the DERIVED agent-rationale markers.
 *
 * A rationale marker is not a document marker: it is projected, read-only, from an item's real
 * `Provenance.rationale` (which the P0 seeder copies verbatim from the compile manifest) and sits at
 * the item's `recordIn`. Its label is the item's name and its note is the rationale text — nothing
 * is synthesized, and an item with no rationale yields no marker. Because it is derived it never
 * produces an op; deleting it is impossible by construction, which is the point: an agent's
 * recorded reason stays visible for as long as the clip it explains exists.
 */

import { newGuid } from '../ids';
import type { EditTimeline, Marker } from '../types';

export type MarkerKind = 'user' | 'rationale';

export interface MarkerRow {
  id: string;
  kind: MarkerKind;
  frame: number;
  name: string;
  note: string | null;
  color: string | null;
  durationFrames: number | null;
  /** For a rationale marker, the item it explains. */
  itemId: string | null;
  /** For a rationale marker, the agent and provider names when the provenance carried them. */
  agentName: string | null;
  providerName: string | null;
}

export const RATIONALE_MARKER_PREFIX = 'rationale:';

export function userMarkerRows(doc: EditTimeline): MarkerRow[] {
  return doc.markers.map((marker: Marker) => ({
    id: marker.id,
    kind: 'user',
    frame: marker.frame,
    name: marker.name,
    note: marker.note ?? null,
    color: marker.color ?? null,
    durationFrames: marker.durationFrames ?? null,
    itemId: null,
    agentName: null,
    providerName: null,
  }));
}

export function rationaleMarkerRows(doc: EditTimeline): MarkerRow[] {
  const rows: MarkerRow[] = [];
  for (const track of doc.tracks) {
    for (const item of track.items) {
      const provenance = item.provenance;
      if (!provenance) continue;
      const rationale = typeof provenance.rationale === 'string' ? provenance.rationale.trim() : '';
      if (rationale.length === 0) continue;
      rows.push({
        id: `${RATIONALE_MARKER_PREFIX}${item.id}`,
        kind: 'rationale',
        frame: item.recordIn,
        name: item.name,
        note: rationale,
        color: null,
        durationFrames: item.recordOut - item.recordIn,
        itemId: item.id,
        agentName: provenance.agentName ?? null,
        providerName: provenance.providerName ?? null,
      });
    }
  }
  return rows;
}

/** User and rationale markers together, by frame then kind (user first) then id. */
export function allMarkerRows(doc: EditTimeline): MarkerRow[] {
  return [...userMarkerRows(doc), ...rationaleMarkerRows(doc)].sort((a, b) => {
    if (a.frame !== b.frame) return a.frame - b.frame;
    if (a.kind !== b.kind) return a.kind === 'user' ? -1 : 1;
    return a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
  });
}

/** Frames of every marker row (user + rationale), ascending and unique — for next/prev navigation. */
export function allMarkerFrames(doc: EditTimeline): number[] {
  return [...new Set(allMarkerRows(doc).map((row) => row.frame))].sort((a, b) => a - b);
}

export function isRationaleMarkerId(id: string): boolean {
  return id.startsWith(RATIONALE_MARKER_PREFIX);
}

/**
 * A fresh document id (marker, item, track, …): a v4 Guid from {@link newGuid}, which needs no
 * `crypto.randomUUID` and so also works on a plain-HTTP origin and on older Safari.
 */
export function newMarkerId(): string {
  return newGuid();
}
