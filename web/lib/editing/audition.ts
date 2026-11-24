/**
 * R09.3: audition support. The editor can play the RESULT of a proposed cut without writing it, by
 * handing the viewer a preview document. This finds where that preview first differs from the
 * real document so playback can start a couple of seconds before the first cut.
 */

import type { EditTimeline, Item } from './types';

const same = (a: Item, b: Item): boolean => a.id === b.id && a.recordIn === b.recordIn && a.recordOut === b.recordOut;

/** The first record frame at which `after` differs from `before` (0 when nothing differs). */
export function firstDifferenceFrame(before: EditTimeline, after: EditTimeline): number {
  let first = Number.POSITIVE_INFINITY;
  const trackIds = new Set([...before.tracks.map((t) => t.id), ...after.tracks.map((t) => t.id)]);
  for (const id of trackIds) {
    const a = [...(before.tracks.find((t) => t.id === id)?.items ?? [])].sort((x, y) => x.recordIn - y.recordIn);
    const b = [...(after.tracks.find((t) => t.id === id)?.items ?? [])].sort((x, y) => x.recordIn - y.recordIn);
    const byIdA = new Map(a.map((i) => [i.id, i] as const));
    const byIdB = new Map(b.map((i) => [i.id, i] as const));
    for (const item of a) {
      const other = byIdB.get(item.id);
      if (!other) first = Math.min(first, item.recordIn);
      else if (!same(item, other)) first = Math.min(first, item.recordIn !== other.recordIn ? Math.min(item.recordIn, other.recordIn) : Math.min(item.recordOut, other.recordOut));
    }
    for (const item of b) if (!byIdA.has(item.id)) first = Math.min(first, item.recordIn);
  }
  return Number.isFinite(first) ? first : 0;
}
