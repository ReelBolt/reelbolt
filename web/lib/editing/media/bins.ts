/**
 * Pure bin-tree arithmetic: building the tree from the flat list the API returns, deriving real
 * per-bin counts from the pool's own membership lists, and deciding what a drag-and-drop actually
 * means before any call is issued.
 *
 * Nothing here touches the network or React, so the decisions that matter most — whether a drop is a
 * legitimate write and whether it would create a cycle — are unit-testable without a browser.
 *
 * ## What the API can and cannot do (read this before changing a return value)
 *
 * `GET .../media/bins` returns a FLAT list with `parentBinId` and an `itemCount` that counts DIRECT
 * members. Membership is readable two further ways, and both are used here:
 *
 *   - **`MediaPoolItem.bins[]`** — every pin of every source the pool already holds. That is what
 *     `countsByBinId` counts, so a bin's displayed count is derived from the sources a user can
 *     actually see, rather than from an optimistic increment nothing can contradict.
 *   - **`GET .../bins/{binId}/items`** — the bin-scoped listing, which is the only source that also
 *     returns DANGLING pins (a pin whose source row is gone). Those appear in no pool item, so the
 *     two counts can legitimately differ; neither is wrong.
 *
 * Both ids a pin needs are now reachable — `binId` and `itemId` from either list — so a clip can be
 * ADDED to a bin (`POST .../bins/{binId}/items`, which takes a mediaRef) **and REMOVED** from one
 * (`DELETE .../bins/{binId}/items/{itemId}`). The earlier refusal to support removal existed only
 * because an `itemId` was unobtainable, and it is no longer.
 */

import type { BinNode, MediaBin, MediaBinMembership, MediaPoolItem, MediaRef } from './types';
import { isMediaRef } from './types';

/**
 * The server's nesting bound (`MediaBinsController`, T-102: "bounded at 16"). Mirrored here so the
 * UI refuses a drop that the server would reject instead of issuing a call that comes back 400.
 */
export const MAX_BIN_NESTING_DEPTH = 16;

// ─────────────────────────────────────────────────────────────────────────────
// buildBinTree
// ─────────────────────────────────────────────────────────────────────────────

const bySortOrder = (a: MediaBin, b: MediaBin): number =>
  a.sortOrder - b.sortOrder || a.name.localeCompare(b.name) || a.id.localeCompare(b.id);

/**
 * Resolves the bin a node should actually hang from, defending against three malformed inputs:
 * a self-parent, a parent id that is not in the list (an orphan), and a parent chain that loops
 * back to the node itself.
 *
 * All three degrade to "top level" rather than throwing or dropping the bin: a bin the server sent
 * must stay visible somewhere, and a cycle must never be reproduced in the rendered tree — a
 * recursive render over a cycle is an infinite loop, which is why this check is not optional.
 */
function safeParentId(bin: MediaBin, byId: Map<string, MediaBin>): string | null {
  const parentId = bin.parentBinId;
  if (!parentId) return null;
  if (parentId === bin.id) return null;
  if (!byId.has(parentId)) return null;

  // Walk up from the proposed parent. `bins.length` is a hard cap as well as a correctness net:
  // a chain longer than the number of bins can only be a cycle.
  const visited = new Set<string>([bin.id]);
  let cursor: string | null = parentId;
  let steps = 0;
  while (cursor && steps <= byId.size) {
    if (visited.has(cursor)) return null;
    visited.add(cursor);
    cursor = byId.get(cursor)?.parentBinId ?? null;
    steps += 1;
  }
  return steps > byId.size ? null : parentId;
}

/**
 * Builds the nested tree from the API's flat bin list.
 *
 * Deterministic and total: siblings are ordered by `sortOrder`, then name, then id, so two runs over
 * the same input render in the same order. A bin referring to a missing, self, or looping parent is
 * attached at the top level rather than being lost or looping forever.
 *
 * `depth` is 0 for a top-level bin and is used for indentation only.
 */
export function buildBinTree(bins: readonly MediaBin[]): BinNode[] {
  const byId = new Map<string, MediaBin>();
  for (const bin of bins) byId.set(bin.id, bin);

  const nodes = new Map<string, BinNode>();
  for (const bin of bins) nodes.set(bin.id, { ...bin, children: [], depth: 0 });

  const roots: BinNode[] = [];
  for (const bin of bins) {
    const node = nodes.get(bin.id);
    if (!node) continue;
    const parentId = safeParentId(bin, byId);
    const parent = parentId ? nodes.get(parentId) : undefined;
    if (parent) parent.children.push(node);
    else roots.push(node);
  }

  const sortRecursively = (siblings: BinNode[], depth: number) => {
    siblings.sort(bySortOrder);
    for (const node of siblings) {
      node.depth = depth;
      sortRecursively(node.children, depth + 1);
    }
  };
  sortRecursively(roots, 0);

  return roots;
}

/** Depth of every bin, keyed by id — used to bound a proposed move without walking twice. */
export function binDepthsById(bins: readonly MediaBin[]): Map<string, number> {
  const depths = new Map<string, number>();
  const visit = (node: BinNode) => {
    depths.set(node.id, node.depth);
    for (const child of node.children) visit(child);
  };
  for (const root of buildBinTree(bins)) visit(root);
  return depths;
}

/** Every descendant id of `binId`, excluding `binId` itself. Cycle-safe. */
export function descendantIdsOf(bins: readonly MediaBin[], binId: string): Set<string> {
  const byParent = new Map<string, string[]>();
  for (const bin of bins) {
    if (!bin.parentBinId || bin.parentBinId === bin.id) continue;
    const list = byParent.get(bin.parentBinId) ?? [];
    list.push(bin.id);
    byParent.set(bin.parentBinId, list);
  }

  const found = new Set<string>();
  const queue = [...(byParent.get(binId) ?? [])];
  while (queue.length > 0) {
    const id = queue.shift() as string;
    if (found.has(id) || id === binId) continue;
    found.add(id);
    queue.push(...(byParent.get(id) ?? []));
  }
  return found;
}

// ─────────────────────────────────────────────────────────────────────────────
// Real membership counts
// ─────────────────────────────────────────────────────────────────────────────

/**
 * How many pool sources each bin holds, derived from the sources' own `bins` lists.
 *
 * This replaces the optimistic "N items" placeholder with a number read off real membership: an item
 * is counted in a bin exactly when its own payload says it is pinned there, so a count moves for
 * exactly the reason the membership moved. A bin holding nothing produces no key at all, which
 * callers read as 0.
 *
 * **It deliberately does not count DANGLING pins.** A pin whose source row is gone appears in no
 * pool item, so it cannot be counted here; `GET .../bins/{binId}/items` still returns it, and the
 * two figures can therefore differ. Counting the server's `itemCount` here instead would be counting
 * entries the user cannot see, which is the placeholder this function exists to replace.
 */
export function countsByBinId(items: readonly MediaPoolItem[]): Record<string, number> {
  const counts: Record<string, number> = {};

  for (const item of items) {
    const pins = Array.isArray(item?.bins) ? item.bins : [];
    for (const pin of pins) {
      if (!pin || typeof pin.binId !== 'string' || pin.binId.length === 0) continue;
      counts[pin.binId] = (counts[pin.binId] ?? 0) + 1;
    }
  }

  return counts;
}

// ─────────────────────────────────────────────────────────────────────────────
// Drag payloads
// ─────────────────────────────────────────────────────────────────────────────

/** Custom MIME types, so a drag from this tree is never confused with text/file drops. */
export const CLIP_DRAG_MIME = 'application/x-reelforge-media-ref';
export const BIN_DRAG_MIME = 'application/x-reelforge-bin';
/** Prefix of the extra type a clip drag carries (`...-video`); the kind is readable while dragging, the data is not. */
export const CLIP_KIND_MIME_PREFIX = 'application/x-reelforge-kind-';

/** What is being dragged: a pool clip, or a bin being reparented. */
export type BinDragPayload =
  | { kind: 'clip'; mediaRef: MediaRef }
  | { kind: 'bin'; binId: string };

/** Minimal structural type for the parts of `DataTransfer` these helpers need. */
export interface DragDataTransfer {
  setData(format: string, data: string): void;
  getData(format: string): string;
}

export function writeDragPayload(dataTransfer: DragDataTransfer, payload: BinDragPayload): void {
  if (payload.kind === 'clip') dataTransfer.setData(CLIP_DRAG_MIME, payload.mediaRef);
  else dataTransfer.setData(BIN_DRAG_MIME, payload.binId);
}

/**
 * Reads a drag payload, returning null for anything malformed.
 *
 * A mediaRef from a drag source is untrusted input like any other, so it goes through
 * `isMediaRef` — which validates the opaque token SHAPE without ever reporting which prefix it
 * carried, because a caller that can ask "is this a file: token?" is one step from branching on it.
 */
export function readDragPayload(dataTransfer: DragDataTransfer | null | undefined): BinDragPayload | null {
  if (!dataTransfer || typeof dataTransfer.getData !== 'function') return null;

  const mediaRef = dataTransfer.getData(CLIP_DRAG_MIME);
  if (isMediaRef(mediaRef)) return { kind: 'clip', mediaRef };

  const binId = dataTransfer.getData(BIN_DRAG_MIME);
  if (typeof binId === 'string' && binId.length > 0) return { kind: 'bin', binId };

  return null;
}

// ─────────────────────────────────────────────────────────────────────────────
// resolveBinDrop
// ─────────────────────────────────────────────────────────────────────────────

/**
 * The write a drop should perform — or an explicit refusal with a reason.
 *
 * The `none` arm is a real outcome, not an error path: it is how "drop a bin on itself", "drop a bin
 * inside its own descendant" and "drop a clip that is already nowhere" are handled **without
 * issuing a call**.
 */
export type BinDropPlan =
  | { kind: 'addItem'; binId: string; mediaRef: MediaRef }
  | { kind: 'removeItems'; mediaRef: MediaRef; items: MediaBinMembership[] }
  | { kind: 'moveBin'; binId: string; parentBinId: string | null }
  | { kind: 'none'; reason: string };

/**
 * Decides what a drop means. `targetBinId === null` is the root ("All media").
 *
 * Pure, and the single place a drop is authorised: the components call this and then execute the
 * plan, so a refusal can never be lost in an event handler.
 *
 * `memberships` is the dragged clip's own pin list, and it is what makes **dropping a clip on the
 * root mean "remove it from every bin"**. It is optional so a caller with no pin list (a test, or a
 * drag from outside the pool) still gets a typed refusal rather than a fabricated `itemId` — an
 * `itemId` is never guessed here, and a plan is only ever built from pins the caller actually read.
 */
export function resolveBinDrop(
  drag: BinDragPayload | null,
  targetBinId: string | null,
  bins: readonly MediaBin[],
  memberships: readonly MediaBinMembership[] = [],
): BinDropPlan {
  if (!drag) return { kind: 'none', reason: 'Nothing to drop.' };

  const byId = new Map<string, MediaBin>();
  for (const bin of bins) byId.set(bin.id, bin);

  if (drag.kind === 'clip') {
    if (targetBinId === null) {
      const pins = memberships.filter(
        (pin) =>
          pin &&
          typeof pin.binId === 'string' &&
          typeof pin.itemId === 'string' &&
          pin.binId.length > 0 &&
          pin.itemId.length > 0,
      );
      if (pins.length === 0) {
        return { kind: 'none', reason: 'That clip is not in any bin.' };
      }
      return { kind: 'removeItems', mediaRef: drag.mediaRef, items: pins };
    }
    if (!byId.has(targetBinId)) return { kind: 'none', reason: 'That bin no longer exists.' };
    if (memberships.some((pin) => pin && pin.binId === targetBinId)) {
      return { kind: 'none', reason: 'That clip is already in this bin.' };
    }
    return { kind: 'addItem', binId: targetBinId, mediaRef: drag.mediaRef };
  }

  const moved = byId.get(drag.binId);
  if (!moved) return { kind: 'none', reason: 'That bin no longer exists.' };

  if (targetBinId === drag.binId) {
    return { kind: 'none', reason: `"${moved.name}" cannot be dropped inside itself.` };
  }

  if (targetBinId === null) {
    if (moved.parentBinId === null) {
      return { kind: 'none', reason: `"${moved.name}" is already at the top level.` };
    }
    return { kind: 'moveBin', binId: moved.id, parentBinId: null };
  }

  if (!byId.has(targetBinId)) return { kind: 'none', reason: 'That bin no longer exists.' };

  if (moved.parentBinId === targetBinId) {
    return { kind: 'none', reason: `"${moved.name}" is already in that bin.` };
  }

  // The cycle check is a client-side refusal, never a request the server has to reject: moving a
  // bin inside its own descendant would detach the whole subtree from the root.
  if (descendantIdsOf(bins, moved.id).has(targetBinId)) {
    return {
      kind: 'none',
      reason: `"${moved.name}" cannot be moved inside its own sub-bin.`,
    };
  }

  // Depth after the move: the target's depth, plus this bin's own subtree height, plus the target
  // itself. Bounded because the server caps nesting at 16 and would answer 400.
  const depths = binDepthsById(bins);
  const targetDepth = depths.get(targetBinId) ?? 0;
  const subtreeHeight = subtreeHeightOf(bins, moved.id);
  if (targetDepth + 1 + subtreeHeight > MAX_BIN_NESTING_DEPTH) {
    return { kind: 'none', reason: 'Bins cannot be nested any deeper.' };
  }

  return { kind: 'moveBin', binId: moved.id, parentBinId: targetBinId };
}

/** Longest downward chain below `binId`, cycle-safe. A leaf is 0. */
function subtreeHeightOf(bins: readonly MediaBin[], binId: string): number {
  const byParent = new Map<string, string[]>();
  for (const bin of bins) {
    if (!bin.parentBinId || bin.parentBinId === bin.id) continue;
    const list = byParent.get(bin.parentBinId) ?? [];
    list.push(bin.id);
    byParent.set(bin.parentBinId, list);
  }

  const seen = new Set<string>([binId]);
  const walk = (id: string): number => {
    let best = 0;
    for (const child of byParent.get(id) ?? []) {
      if (seen.has(child)) continue;
      seen.add(child);
      best = Math.max(best, 1 + walk(child));
    }
    return best;
  };
  return walk(binId);
}

/** Applies a plan's optimistic effect to a bin list, without issuing any request. */
export function applyDropOptimistically(
  bins: readonly MediaBin[],
  plan: BinDropPlan,
): MediaBin[] {
  if (plan.kind === 'addItem') {
    return bins.map((bin) =>
      bin.id === plan.binId ? { ...bin, itemCount: bin.itemCount + 1 } : bin,
    );
  }
  if (plan.kind === 'removeItems') {
    const removals = new Set(plan.items.map((pin) => pin.binId));
    return bins.map((bin) =>
      removals.has(bin.id) ? { ...bin, itemCount: Math.max(0, bin.itemCount - 1) } : bin,
    );
  }
  if (plan.kind === 'moveBin') {
    return bins.map((bin) =>
      bin.id === plan.binId ? { ...bin, parentBinId: plan.parentBinId } : bin,
    );
  }
  return [...bins];
}

/** A smart bin's contents are its filter's answer; nothing can be pinned into it. */
export function isSmartBin(bin: Pick<MediaBin, 'smartFilter' | 'isSmart'>): boolean {
  return bin.isSmart === true || (bin.smartFilter !== undefined && bin.smartFilter !== null);
}

/** The stated reason a clip cannot go into a smart bin (by drop or by "Add to bin"). */
export function smartBinRefusal(bin: Pick<MediaBin, 'name'>): string {
  return `"${bin.name}" is a smart bin: it shows every source matching its filter. Tag the clip instead, or add it to an ordinary bin.`;
}

/**
 * "Add to bin" from a clip tile's menu: the touch path that needs no HTML5 drag. The same plan a
 * drop on the bin would make, plus the smart-bin refusal a drop states.
 */
export function planAddToBin(
  mediaRef: MediaRef,
  binId: string,
  bins: readonly MediaBin[],
  memberships: readonly MediaBinMembership[],
): BinDropPlan {
  const target = bins.find((bin) => bin.id === binId);
  if (target && isSmartBin(target)) return { kind: 'none', reason: smartBinRefusal(target) };
  return resolveBinDrop({ kind: 'clip', mediaRef }, binId, bins, memberships);
}
