/**
 * Undo for "Delete bin" (R05.3).
 *
 * The server soft-deletes a bin subtree but hard-deletes its pins (`media_bin_items` has no
 * `deleted_at`), so there is nothing to un-delete. Undo therefore re-creates the subtree from a
 * snapshot taken just before the delete: bins parents-first (new ids, same names, order and smart
 * rules), then each pin. The media itself is never touched by either direction.
 */

import type { MediaBin, MediaBinMembership, MediaRef } from './types';

/** One bin of the deleted subtree and the sources it had pinned (empty for a smart bin). */
export interface DeletedBinSnapshot {
  bin: MediaBin;
  mediaRefs: MediaRef[];
}

export interface RestoreDeps {
  createBin: (request: { name: string; parentBinId?: string | null; smartFilter?: MediaBin['smartFilter'] }) => Promise<MediaBin>;
  addBinItem: (binId: string, mediaRef: MediaRef) => Promise<{ id: string }>;
}

export interface RestoreResult {
  bins: MediaBin[];
  memberships: { mediaRef: MediaRef; membership: MediaBinMembership }[];
  /** Pins that could not be put back (their source is gone). */
  skippedPins: number;
}

/** Orders a subtree so every bin comes after its parent, keeping the stored order among siblings. */
export function parentsFirst(snapshots: readonly DeletedBinSnapshot[]): DeletedBinSnapshot[] {
  const ids = new Set(snapshots.map((s) => s.bin.id));
  const ordered: DeletedBinSnapshot[] = [];
  const placed = new Set<string>();
  let pending = [...snapshots].sort((a, b) => a.bin.sortOrder - b.bin.sortOrder);
  while (pending.length > 0) {
    const next = pending.filter((s) => !s.bin.parentBinId || !ids.has(s.bin.parentBinId) || placed.has(s.bin.parentBinId));
    if (next.length === 0) break; // a cycle cannot be stored; never loop on one
    for (const s of next) {
      ordered.push(s);
      placed.add(s.bin.id);
    }
    pending = pending.filter((s) => !placed.has(s.bin.id));
  }
  return ordered;
}

export async function restoreDeletedBins(
  snapshots: readonly DeletedBinSnapshot[],
  deps: RestoreDeps,
): Promise<RestoreResult> {
  const idMap = new Map<string, string>();
  const result: RestoreResult = { bins: [], memberships: [], skippedPins: 0 };

  for (const { bin, mediaRefs } of parentsFirst(snapshots)) {
    const parentBinId = bin.parentBinId ? (idMap.get(bin.parentBinId) ?? bin.parentBinId) : null;
    const created = await deps.createBin({
      name: bin.name,
      ...(parentBinId ? { parentBinId } : {}),
      ...(bin.smartFilter ? { smartFilter: bin.smartFilter } : {}),
    });
    idMap.set(bin.id, created.id);
    let count = 0;
    for (const mediaRef of mediaRefs) {
      try {
        const pin = await deps.addBinItem(created.id, mediaRef);
        result.memberships.push({ mediaRef, membership: { binId: created.id, itemId: pin.id } });
        count += 1;
      } catch {
        result.skippedPins += 1;
      }
    }
    result.bins.push({ ...created, itemCount: bin.smartFilter ? created.itemCount : count });
  }
  return result;
}
