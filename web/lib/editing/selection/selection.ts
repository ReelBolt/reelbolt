/**
 * Selection reducers — pure `(doc, selection, input) → selection`.
 *
 * A selection is a SET of item ids: order-insensitive, duplicate-free, and always returned in a
 * stable DOCUMENT order (track order, then `recordIn`) so two selections covering the same items
 * compare equal whichever gesture produced them. It is never a copy of item objects and never a
 * document write.
 */

import type { EditTimeline, Item } from '../types';

export type Selection = readonly string[];

interface Located {
  item: Item;
  trackIndex: number;
  trackId: string;
}

function locate(doc: EditTimeline): Map<string, Located> {
  const map = new Map<string, Located>();
  doc.tracks.forEach((track, trackIndex) => {
    for (const item of track.items) map.set(item.id, { item, trackIndex, trackId: track.id });
  });
  return map;
}

/** Puts a set of ids into the canonical document order, dropping ids the document no longer has. */
export function normalizeSelection(doc: EditTimeline, ids: Iterable<string>): string[] {
  const located = locate(doc);
  const unique = [...new Set(ids)].filter((id) => located.has(id));
  return unique.sort((a, b) => {
    const la = located.get(a)!;
    const lb = located.get(b)!;
    if (la.trackIndex !== lb.trackIndex) return la.trackIndex - lb.trackIndex;
    if (la.item.recordIn !== lb.item.recordIn) return la.item.recordIn - lb.item.recordIn;
    return a < b ? -1 : a > b ? 1 : 0;
  });
}

export function clearSelection(): string[] {
  return [];
}

export function selectItem(doc: EditTimeline, itemId: string): string[] {
  return normalizeSelection(doc, [itemId]);
}

export function toggleItem(doc: EditTimeline, selection: Selection, itemId: string): string[] {
  const set = new Set(selection);
  if (set.has(itemId)) set.delete(itemId);
  else set.add(itemId);
  return normalizeSelection(doc, set);
}

/**
 * Shift-click: every item on the anchor's track and the target's track range whose span intersects
 * the frame range between the two items. A missing anchor degrades to a plain select.
 */
export function selectRange(doc: EditTimeline, anchorId: string | null, targetId: string): string[] {
  const located = locate(doc);
  const target = located.get(targetId);
  if (!target) return [];
  const anchor = anchorId ? located.get(anchorId) : undefined;
  if (!anchor) return [targetId];

  const trackLo = Math.min(anchor.trackIndex, target.trackIndex);
  const trackHi = Math.max(anchor.trackIndex, target.trackIndex);
  const frameLo = Math.min(anchor.item.recordIn, target.item.recordIn);
  const frameHi = Math.max(anchor.item.recordOut, target.item.recordOut);

  return selectMarquee(doc, { trackLo, trackHi, frameLo, frameHi });
}

export interface MarqueeRect {
  trackLo: number;
  trackHi: number;
  /** Inclusive start frame. */
  frameLo: number;
  /** Exclusive end frame. */
  frameHi: number;
}

/** Every item whose span intersects the rectangle (track index range × frame range). */
export function selectMarquee(doc: EditTimeline, rect: MarqueeRect): string[] {
  const ids: string[] = [];
  doc.tracks.forEach((track, trackIndex) => {
    if (trackIndex < rect.trackLo || trackIndex > rect.trackHi) return;
    for (const item of track.items) {
      if (item.recordIn < rect.frameHi && item.recordOut > rect.frameLo) ids.push(item.id);
    }
  });
  return normalizeSelection(doc, ids);
}

/**
 * NLE "select forward": every item that ends after the playhead — so the clip under the playhead is
 * included, as in the track-select-forward tool — on the given tracks or all.
 */
export function selectForwardFromPlayhead(doc: EditTimeline, playhead: number, trackIds?: readonly string[]): string[] {
  const ids: string[] = [];
  for (const track of doc.tracks) {
    if (trackIds && !trackIds.includes(track.id)) continue;
    for (const item of track.items) if (item.recordOut > playhead) ids.push(item.id);
  }
  return normalizeSelection(doc, ids);
}

/** NLE "select backward": every item that starts before the playhead (the clip under it included). */
export function selectBackwardFromPlayhead(doc: EditTimeline, playhead: number, trackIds?: readonly string[]): string[] {
  const ids: string[] = [];
  for (const track of doc.tracks) {
    if (trackIds && !trackIds.includes(track.id)) continue;
    for (const item of track.items) if (item.recordIn < playhead) ids.push(item.id);
  }
  return normalizeSelection(doc, ids);
}

export function selectAllOnTrack(doc: EditTimeline, trackId: string): string[] {
  const track = doc.tracks.find((t) => t.id === trackId);
  return track ? normalizeSelection(doc, track.items.map((i) => i.id)) : [];
}

export function selectAll(doc: EditTimeline): string[] {
  return normalizeSelection(doc, doc.tracks.flatMap((t) => t.items.map((i) => i.id)));
}

/** Two selections are equal when they hold the same ids, whatever order they came in. */
export function selectionEquals(a: Selection, b: Selection): boolean {
  if (a.length !== b.length) return false;
  const set = new Set(a);
  return b.every((id) => set.has(id));
}

/** The Select menu's commands (and their keymap actions). */
export type SelectCommand = 'all' | 'none' | 'forward' | 'backward' | 'track';

/**
 * W0.6 (P3-SEL-03): the one place the Select menu and the keyboard turn a command into a selection.
 * "All on track" uses the track of the first selected item, else the first targeted track.
 */
export function runSelectCommand(
  doc: EditTimeline,
  ui: { playheadFrame: number; selection: Selection; targetedTrackIds: readonly string[] },
  command: SelectCommand,
): string[] {
  switch (command) {
    case 'all':
      return selectAll(doc);
    case 'none':
      return [];
    case 'forward':
      return selectForwardFromPlayhead(doc, ui.playheadFrame);
    case 'backward':
      return selectBackwardFromPlayhead(doc, ui.playheadFrame);
    case 'track': {
      const located = locate(doc);
      const trackId = (ui.selection.length > 0 ? located.get(ui.selection[0])?.trackId : undefined) ?? ui.targetedTrackIds.find((id) => doc.tracks.some((t) => t.id === id));
      return trackId ? selectAllOnTrack(doc, trackId) : [];
    }
  }
}
