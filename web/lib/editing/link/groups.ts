/**
 * R08.1 linked clips: reading the link groups out of a document. Pure; no op is built here.
 *
 * A group is the set of media clips sharing a `linkId` (typically a picture clip and its camera-audio
 * companion). Groups live in the root timeline's tracks; a compound clip's inner sequence has its own.
 */

import type { EditTimeline, Item, MediaClip, Track } from '../types';

/** An item located in the document. */
export interface Located {
  item: Item;
  track: Track;
  index: number;
}

export function locate(doc: EditTimeline, itemId: string): Located | null {
  for (const track of doc.tracks) {
    const index = track.items.findIndex((item) => item.id === itemId);
    if (index >= 0) return { item: track.items[index], track, index };
  }
  return null;
}

/** The link id of an item, or null (absent, null and non-media items are all "not linked"). */
export function linkIdOf(item: Item): string | null {
  if (item.type !== 'mediaClip') return null;
  const linkId = (item as MediaClip).linkId;
  return typeof linkId === 'string' && linkId.length > 0 ? linkId : null;
}

/** Every member of the group `itemId` belongs to, itself included, in track then time order. An unlinked item is a group of one. */
export function groupOf(doc: EditTimeline, itemId: string): Located[] {
  const own = locate(doc, itemId);
  if (!own) return [];
  const linkId = linkIdOf(own.item);
  if (linkId === null) return [own];
  const members: Located[] = [];
  for (const track of doc.tracks) {
    track.items.forEach((item, index) => {
      if (linkIdOf(item) === linkId) members.push({ item, track, index });
    });
  }
  return members;
}

/**
 * The selection a link-aware command acts on: the given ids first (in order, unknown ids dropped),
 * then every other member of their groups. Selecting one half of a linked pair selects both.
 */
export function expandSelection(doc: EditTimeline, ids: readonly string[]): string[] {
  const out: string[] = [];
  const seen = new Set<string>();
  const add = (id: string): void => {
    if (!seen.has(id)) {
      seen.add(id);
      out.push(id);
    }
  };
  for (const id of ids) if (locate(doc, id)) add(id);
  for (const id of ids) for (const member of groupOf(doc, id)) add(member.item.id);
  return out;
}

/** True when the item has at least one partner. */
export function isLinked(doc: EditTimeline, itemId: string): boolean {
  return groupOf(doc, itemId).length > 1;
}

/** The members other than `itemId`. */
export function partnersOf(doc: EditTimeline, itemId: string): Located[] {
  return groupOf(doc, itemId).filter((member) => member.item.id !== itemId);
}

/** Options every builder takes. */
export interface LinkOptions {
  /** Tracks that accept no edit: a partner on one is left alone (the primary is the caller's business). */
  isLocked?: (trackId: string) => boolean;
}

/** The group members a command may touch: the primary plus every unlocked partner. */
export function editableGroup(doc: EditTimeline, itemId: string, options?: LinkOptions): Located[] {
  const isLocked = options?.isLocked;
  return groupOf(doc, itemId).filter((m) => m.item.id === itemId || !isLocked?.(m.track.id));
}

/**
 * Group-aware selection for the store: `next` is what a gesture asked for, `prev` what was selected.
 * Every member of a group touched by `next` is selected too, except that a group that was entirely
 * selected and is now missing a member (the user deselected one half) is dropped as a whole.
 */
export function reconcileLinkedSelection(doc: EditTimeline, prev: readonly string[], next: readonly string[]): string[] {
  const nextSet = new Set(next);
  const prevSet = new Set(prev);
  const out = new Set<string>();
  const dropped = new Set<string>();
  for (const id of next) {
    const group = groupOf(doc, id);
    if (group.length > 1 && group.every((m) => prevSet.has(m.item.id)) && !group.every((m) => nextSet.has(m.item.id))) {
      for (const m of group) dropped.add(m.item.id);
    }
  }
  for (const id of next) {
    if (dropped.has(id)) continue;
    for (const m of groupOf(doc, id)) out.add(m.item.id);
    out.add(id);
  }
  return [...out];
}
