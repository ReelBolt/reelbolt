import type { EditOp } from './engine';
import type { EditTimeline, Item } from './types';

/**
 * The ops for "Delete" (ripple): every selected clip is extracted from its own track, and so is its
 * PAIRED SOUND/PICTURE — the clip on another unlocked track made from the same source with the same
 * `sourceIn` and the same span, which is exactly what a media-pool insert (picture plus "Camera
 * audio") and "Extract audio" produce. Without it, deleting the picture closed the gap on V1 only
 * and everything after it slipped out of sync with its sound.
 *
 * There is no link or group in the document, so the pair is recognised by what it is: same asset,
 * same source offset, same record span. A companion on a locked track is left alone (a locked track
 * accepts no edit), and `skippedLocked` says so, so the UI can tell the user.
 *
 * Right-most first, so each close-up is independent of the others (an `extract` shifts only what
 * follows it on its own track).
 */
export interface RippleDeletePlan {
  ops: EditOp[];
  /** Selected clips plus their companions. */
  itemCount: number;
  /** Companions found on locked tracks and therefore not removed. */
  skippedLocked: number;
}

const sameAsset = (a: Item, b: Item): boolean => {
  const x = (a as { assetRef?: { kind?: string; projectFileId?: string | null; stepResultId?: string | null } }).assetRef;
  const y = (b as { assetRef?: { kind?: string; projectFileId?: string | null; stepResultId?: string | null } }).assetRef;
  if (!x || !y) return false;
  return x.kind === y.kind && (x.projectFileId ?? null) === (y.projectFileId ?? null) && (x.stepResultId ?? null) === (y.stepResultId ?? null);
};

export function rippleDeletePlan(doc: EditTimeline, selection: readonly string[], isLocked: (trackId: string) => boolean): RippleDeletePlan {
  const located = doc.tracks.flatMap((track) => track.items.map((item) => ({ item, track })));
  const chosen = new Map<string, Item>();
  for (const id of selection) {
    const hit = located.find((l) => l.item.id === id);
    if (hit) chosen.set(id, hit.item);
  }
  let skippedLocked = 0;
  for (const { item: picked, track: pickedTrack } of located.filter((l) => chosen.has(l.item.id))) {
    for (const { item, track } of located) {
      if (track.id === pickedTrack.id || chosen.has(item.id)) continue;
      if (item.type !== 'mediaClip' || picked.type !== 'mediaClip') continue;
      if (item.recordIn !== picked.recordIn || item.recordOut !== picked.recordOut) continue;
      if ((item.sourceIn ?? 0) !== (picked.sourceIn ?? 0) || !sameAsset(item, picked)) continue;
      if (isLocked(track.id)) {
        skippedLocked++;
        continue;
      }
      chosen.set(item.id, item);
    }
  }
  const items = [...chosen.values()].sort((a, b) => b.recordIn - a.recordIn);
  return { ops: items.map((item) => ({ type: 'extract', itemId: item.id }) as EditOp), itemCount: items.length, skippedLocked };
}
