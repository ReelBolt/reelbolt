import { apply, type EditOp } from '../engine';
import type { EditTimeline, Item, TrackKind } from '../types';

/**
 * R03.2: copy and paste of clips. The clipboard is plain JSON (`ClipboardPayload`): the copied
 * items keep their offsets from the earliest one and their track offsets from the first track, so
 * pasting a selection of several clips reproduces its shape. Pasting regenerates every item id,
 * and every `linkId` is remapped consistently (clips that shared a link keep sharing a fresh one).
 */
export const CLIPBOARD_VERSION = 1;

export interface ClipboardEntry {
  /** Track offset from the first copied track, in document order. */
  trackOffset: number;
  trackKind: TrackKind;
  /** Frames from the earliest copied item's `recordIn`. */
  frameOffset: number;
  item: Item;
}

export interface ClipboardPayload {
  app: 'reelforge-edit-clips';
  version: typeof CLIPBOARD_VERSION;
  entries: ClipboardEntry[];
}

export type BuildResult<T> = ({ ok: true } & T) | { ok: false; reason: string };

const asRecord = (item: Item): Record<string, unknown> => item as unknown as Record<string, unknown>;

/** The payload for a selection, or null when nothing selected exists in the document. */
export function copyClips(doc: EditTimeline, selection: readonly string[]): ClipboardPayload | null {
  const wanted = new Set(selection);
  const rows: { trackIndex: number; kind: TrackKind; item: Item }[] = [];
  doc.tracks.forEach((track, trackIndex) => {
    for (const item of track.items) if (wanted.has(item.id)) rows.push({ trackIndex, kind: track.kind, item });
  });
  if (rows.length === 0) return null;
  const firstTrack = Math.min(...rows.map((r) => r.trackIndex));
  const firstFrame = Math.min(...rows.map((r) => r.item.recordIn));
  return {
    app: 'reelforge-edit-clips',
    version: CLIPBOARD_VERSION,
    entries: rows.map((r) => ({
      trackOffset: r.trackIndex - firstTrack,
      trackKind: r.kind,
      frameOffset: r.item.recordIn - firstFrame,
      item: structuredClone(r.item),
    })),
  };
}

export function serializeClipboard(payload: ClipboardPayload): string {
  return JSON.stringify(payload);
}

/** Parses clipboard text; null for anything that is not our payload (plain text, other JSON, bad shape). */
export function parseClipboard(text: string | null | undefined): ClipboardPayload | null {
  if (!text) return null;
  try {
    const value = JSON.parse(text) as Partial<ClipboardPayload> | null;
    if (!value || value.app !== 'reelforge-edit-clips' || value.version !== CLIPBOARD_VERSION || !Array.isArray(value.entries) || value.entries.length === 0) return null;
    for (const e of value.entries) {
      if (!e || typeof e.trackOffset !== 'number' || typeof e.frameOffset !== 'number' || !e.item || typeof e.item.recordIn !== 'number' || typeof e.item.recordOut !== 'number') return null;
    }
    return value as ClipboardPayload;
  } catch {
    return null;
  }
}

/** A fresh copy of an item at a new place, with its id (and link id, via `links`) regenerated. */
function reissue(item: Item, recordIn: number, newId: () => string, links: Map<string, string>): Item {
  const copy = structuredClone(item);
  const duration = item.recordOut - item.recordIn;
  const delta = recordIn - item.recordIn;
  copy.id = newId();
  copy.recordIn = recordIn;
  copy.recordOut = recordIn + duration;
  const record = asRecord(copy);
  const link = record.linkId;
  if (typeof link === 'string') {
    if (!links.has(link)) links.set(link, newId());
    record.linkId = links.get(link);
  }
  // Keyframes are absolute timeline frames: they travel with the clip.
  if (delta !== 0) shiftKeys(record, delta);
  return copy;
}

function shiftKeys(value: unknown, delta: number): void {
  if (!value || typeof value !== 'object') return;
  if (Array.isArray(value)) {
    value.forEach((v) => shiftKeys(v, delta));
    return;
  }
  const record = value as Record<string, unknown>;
  if (Array.isArray(record.keys)) {
    for (const key of record.keys as Record<string, unknown>[]) if (key && typeof key.frame === 'number') key.frame += delta;
  }
  for (const [name, child] of Object.entries(record)) if (name !== 'keys') shiftKeys(child, delta);
}

export interface PasteTarget {
  /** The track the first copied track lands on (a track id in `doc`). */
  trackId: string;
  atFrame: number;
}

/**
 * Paste = place each copy on its track at `atFrame + frameOffset`, never rippling and never
 * overwriting: a span that is not free refuses the whole paste with a plain sentence (so a paste
 * can never destroy what is there). One batch = one undo step.
 */
export function buildPasteOps(doc: EditTimeline, payload: ClipboardPayload, target: PasteTarget, newId: () => string, isLocked: (trackId: string) => boolean = () => false): BuildResult<{ ops: EditOp[]; newItemIds: string[] }> {
  const baseIndex = doc.tracks.findIndex((t) => t.id === target.trackId);
  if (baseIndex < 0) return { ok: false, reason: 'Pick a track to paste onto first.' };
  const links = new Map<string, string>();
  const ops: EditOp[] = [];
  const newItemIds: string[] = [];
  const placed: { trackId: string; recordIn: number; recordOut: number }[] = [];
  for (const entry of [...payload.entries].sort((a, b) => a.trackOffset - b.trackOffset || a.frameOffset - b.frameOffset)) {
    const track = doc.tracks[baseIndex + entry.trackOffset];
    if (!track || track.kind !== entry.trackKind) {
      return { ok: false, reason: `There is no ${entry.trackKind} track there to paste onto. Select a track with room for every copied clip.` };
    }
    if (isLocked(track.id)) return { ok: false, reason: `${track.name} is locked. Unlock it to paste there.` };
    const item = reissue(entry.item, Math.max(0, target.atFrame + entry.frameOffset), newId, links);
    const busy = track.items.some((other) => other.recordIn < item.recordOut && item.recordIn < other.recordOut) || placed.some((p) => p.trackId === track.id && p.recordIn < item.recordOut && item.recordIn < p.recordOut);
    if (busy) {
      return { ok: false, reason: `There is no room on ${track.name} at that point. Move the playhead to empty space, or use Duplicate to make room.` };
    }
    placed.push({ trackId: track.id, recordIn: item.recordIn, recordOut: item.recordOut });
    ops.push({ type: 'placeOnTop', trackId: track.id, item });
    newItemIds.push(item.id);
  }
  return { ok: true, ops, newItemIds };
}

/**
 * Duplicate = copy placed directly after the selection (same tracks), pushing what follows on each
 * track to the right so nothing is overwritten. One batch = one undo step.
 */
export function buildDuplicateOps(doc: EditTimeline, selection: readonly string[], newId: () => string, isLocked: (trackId: string) => boolean = () => false): BuildResult<{ ops: EditOp[]; newItemIds: string[] }> {
  const payload = copyClips(doc, selection);
  if (!payload) return { ok: false, reason: 'Select a clip first.' };
  const wanted = new Set(selection);
  const located = doc.tracks.flatMap((t, trackIndex) => t.items.filter((i) => wanted.has(i.id)).map((item) => ({ item, track: t, trackIndex })));
  const firstTrack = Math.min(...located.map((l) => l.trackIndex));
  const groupStart = Math.min(...located.map((l) => l.item.recordIn));
  const groupEnd = Math.max(...located.map((l) => l.item.recordOut));
  const links = new Map<string, string>();
  const ops: EditOp[] = [];
  const newItemIds: string[] = [];
  // Per track, the copies go right-most first so each insert's index stays canonical.
  const byTrack = new Map<string, Item[]>();
  for (const entry of payload.entries) {
    const track = doc.tracks[firstTrack + entry.trackOffset];
    if (isLocked(track.id)) return { ok: false, reason: `${track.name} is locked. Unlock it to duplicate there.` };
    const item = reissue(entry.item, groupEnd + (entry.item.recordIn - groupStart), newId, links);
    byTrack.set(track.id, [...(byTrack.get(track.id) ?? []), item]);
    newItemIds.push(item.id);
  }
  // Each copy is inserted flush after the previous run; the engine ripples what follows by exactly its
  // length. Simulated here so a span that cannot be made (a partial gap) is a plain sentence, not an engine error.
  let working = doc;
  for (const [trackId, items] of byTrack) {
    for (const item of [...items].sort((a, b) => a.recordIn - b.recordIn)) {
      const track = working.tracks.find((t) => t.id === trackId)!;
      const op: EditOp = { type: 'insert', trackId, index: track.items.filter((i) => i.recordIn < item.recordIn).length, item };
      try {
        working = apply(working, op).doc;
      } catch {
        return { ok: false, reason: `There is not enough room after the clip on ${track.name} to duplicate it. Close the gap or move the next clip first.` };
      }
      ops.push(op);
    }
  }
  return { ok: true, ops, newItemIds };
}

/** Selection → ops that cut: the copy is taken first (by the caller), then the clips are ripple-deleted by the delete builder. */
export const clipboardItemCount = (payload: ClipboardPayload): number => payload.entries.length;
