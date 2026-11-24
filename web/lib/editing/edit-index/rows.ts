/**
 * The edit index — an EDL-style, read-only projection of every item in the document.
 *
 * `buildEditIndex` is pure and TOTAL over the items: one row per item, in document order (track
 * order, then `recordIn`), with the row count ALWAYS equal to the item count. A projection that
 * silently dropped an event is the failure mode this list exists to prevent, so a property test
 * pins the count.
 */

import type { EditTimeline, Item } from '../types';

export interface EditIndexRow {
  /** 1-based position in the whole list. */
  index: number;
  trackId: string;
  trackName: string;
  trackKind: string;
  itemId: string;
  itemType: Item['type'];
  name: string;
  recordIn: number;
  recordOut: number;
  durationFrames: number;
  /** `mediaClip` only, else null. */
  sourceIn: number | null;
  enabled: boolean;
  /** From the track. */
  locked: boolean;
  hasProvenance: boolean;
  rationale: string | null;
}

export type EditIndexSortKey = 'index' | 'recordIn' | 'durationFrames' | 'trackName' | 'name' | 'itemType';

export function buildEditIndex(doc: EditTimeline): EditIndexRow[] {
  const rows: EditIndexRow[] = [];
  let index = 1;
  for (const track of doc.tracks) {
    const items = [...track.items].sort((a, b) => a.recordIn - b.recordIn);
    for (const item of items) {
      const fields = item as unknown as Record<string, unknown>;
      rows.push({
        index: index++,
        trackId: track.id,
        trackName: track.name,
        trackKind: track.kind,
        itemId: item.id,
        itemType: item.type,
        name: item.name,
        recordIn: item.recordIn,
        recordOut: item.recordOut,
        durationFrames: item.recordOut - item.recordIn,
        sourceIn: item.type === 'mediaClip' && typeof fields.sourceIn === 'number' ? (fields.sourceIn as number) : null,
        enabled: typeof fields.enabled === 'boolean' ? (fields.enabled as boolean) : true,
        locked: track.locked,
        hasProvenance: item.provenance != null,
        rationale: item.provenance?.rationale ?? null,
      });
    }
  }
  return rows;
}

export function sortEditIndex(rows: readonly EditIndexRow[], key: EditIndexSortKey, descending = false): EditIndexRow[] {
  const sorted = [...rows].sort((a, b) => {
    const va = a[key];
    const vb = b[key];
    if (typeof va === 'number' && typeof vb === 'number') return va - vb || a.index - b.index;
    const sa = String(va);
    const sb = String(vb);
    return sa < sb ? -1 : sa > sb ? 1 : a.index - b.index;
  });
  return descending ? sorted.reverse() : sorted;
}

export function filterEditIndex(rows: readonly EditIndexRow[], query: string): EditIndexRow[] {
  const needle = query.trim().toLowerCase();
  if (needle.length === 0) return [...rows];
  return rows.filter(
    (row) =>
      row.name.toLowerCase().includes(needle) ||
      row.trackName.toLowerCase().includes(needle) ||
      row.itemType.toLowerCase().includes(needle) ||
      (row.rationale ?? '').toLowerCase().includes(needle),
  );
}
