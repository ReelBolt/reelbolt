/**
 * Timeline compare (P6): an EVENT diff of two documents — which items were added, removed, moved,
 * re-trimmed or otherwise changed, by id — rather than a text diff of the JSON. Items are matched
 * by id, which `duplicate` and the revision snapshots preserve, so comparing a timeline with its
 * duplicate or an earlier revision lists exactly the edits made in between.
 */

import type { EditTimeline, Item, Track } from '../types';

export type ItemChangeKind = 'added' | 'removed' | 'moved' | 'trimmed' | 'changed';

export interface ItemChange {
  kind: ItemChangeKind;
  itemId: string;
  itemType: Item['type'];
  name: string;
  /** The track name in each document (null when the item is absent there). */
  trackBefore: string | null;
  trackAfter: string | null;
  before: { recordIn: number; recordOut: number } | null;
  after: { recordIn: number; recordOut: number } | null;
  /** The property keys whose values differ, for a `changed` entry (or alongside a span change). */
  changedKeys: string[];
}

export interface TrackChange {
  kind: 'added' | 'removed' | 'renamed' | 'changed';
  trackId: string;
  nameBefore: string | null;
  nameAfter: string | null;
  changedKeys: string[];
}

export interface TimelineDiff {
  items: ItemChange[];
  tracks: TrackChange[];
  settingsChanged: string[];
  markers: { added: number; removed: number; changed: number };
  /** Frames of material in each document (the last item's end). */
  lengthBefore: number;
  lengthAfter: number;
  counts: Record<ItemChangeKind, number>;
}

const stable = (value: unknown): string => JSON.stringify(value, (_k, v) => (v && typeof v === 'object' && !Array.isArray(v) ? Object.fromEntries(Object.entries(v as Record<string, unknown>).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))) : v));

const SPAN_KEYS = new Set(['recordIn', 'recordOut']);

function changedKeysOf(a: Record<string, unknown>, b: Record<string, unknown>, skip: ReadonlySet<string>): string[] {
  const keys = new Set([...Object.keys(a), ...Object.keys(b)]);
  const out: string[] = [];
  for (const key of keys) {
    if (skip.has(key)) continue;
    const va = a[key];
    const vb = b[key];
    const absentA = va === undefined || va === null;
    const absentB = vb === undefined || vb === null;
    if (absentA && absentB) continue;
    if (stable(va) !== stable(vb)) out.push(key);
  }
  return out.sort();
}

const endOf = (doc: EditTimeline): number => doc.tracks.reduce((end, t) => t.items.reduce((e, i) => Math.max(e, i.recordOut), end), 0);

/** The event diff from `before` to `after`. Pure. */
export function diffTimelines(before: EditTimeline, after: EditTimeline): TimelineDiff {
  const locate = (doc: EditTimeline) => {
    const map = new Map<string, { item: Item; track: Track }>();
    for (const track of doc.tracks) for (const item of track.items) map.set(item.id, { item, track });
    return map;
  };
  const a = locate(before);
  const b = locate(after);

  const items: ItemChange[] = [];
  for (const [id, { item, track }] of a) {
    const other = b.get(id);
    if (!other) {
      items.push({ kind: 'removed', itemId: id, itemType: item.type, name: item.name, trackBefore: track.name, trackAfter: null, before: { recordIn: item.recordIn, recordOut: item.recordOut }, after: null, changedKeys: [] });
      continue;
    }
    const spanBefore = { recordIn: item.recordIn, recordOut: item.recordOut };
    const spanAfter = { recordIn: other.item.recordIn, recordOut: other.item.recordOut };
    const changedKeys = changedKeysOf(item as unknown as Record<string, unknown>, other.item as unknown as Record<string, unknown>, SPAN_KEYS);
    const trackMoved = track.id !== other.track.id;
    const sameDuration = spanBefore.recordOut - spanBefore.recordIn === spanAfter.recordOut - spanAfter.recordIn;
    const spanChanged = spanBefore.recordIn !== spanAfter.recordIn || spanBefore.recordOut !== spanAfter.recordOut;
    let kind: ItemChangeKind | null = null;
    if (trackMoved || (spanChanged && sameDuration)) kind = 'moved';
    else if (spanChanged) kind = 'trimmed';
    else if (changedKeys.length > 0) kind = 'changed';
    if (kind === null) continue;
    items.push({ kind, itemId: id, itemType: other.item.type, name: other.item.name, trackBefore: track.name, trackAfter: other.track.name, before: spanBefore, after: spanAfter, changedKeys });
  }
  for (const [id, { item, track }] of b) {
    if (a.has(id)) continue;
    items.push({ kind: 'added', itemId: id, itemType: item.type, name: item.name, trackBefore: null, trackAfter: track.name, before: null, after: { recordIn: item.recordIn, recordOut: item.recordOut }, changedKeys: [] });
  }
  items.sort((x, y) => (x.after?.recordIn ?? x.before?.recordIn ?? 0) - (y.after?.recordIn ?? y.before?.recordIn ?? 0) || x.itemId.localeCompare(y.itemId));

  const tracks: TrackChange[] = [];
  const tracksB = new Map(after.tracks.map((t) => [t.id, t] as const));
  for (const track of before.tracks) {
    const other = tracksB.get(track.id);
    if (!other) {
      tracks.push({ kind: 'removed', trackId: track.id, nameBefore: track.name, nameAfter: null, changedKeys: [] });
      continue;
    }
    const changedKeys = changedKeysOf(track as unknown as Record<string, unknown>, other as unknown as Record<string, unknown>, new Set(['items']));
    if (changedKeys.length === 0) continue;
    tracks.push({ kind: changedKeys.length === 1 && changedKeys[0] === 'name' ? 'renamed' : 'changed', trackId: track.id, nameBefore: track.name, nameAfter: other.name, changedKeys });
  }
  const tracksA = new Set(before.tracks.map((t) => t.id));
  for (const track of after.tracks) if (!tracksA.has(track.id)) tracks.push({ kind: 'added', trackId: track.id, nameBefore: null, nameAfter: track.name, changedKeys: [] });

  const settingsChanged = changedKeysOf(before.settings as unknown as Record<string, unknown>, after.settings as unknown as Record<string, unknown>, new Set());

  const markersA = new Map(before.markers.map((m) => [m.id, m] as const));
  const markersB = new Map(after.markers.map((m) => [m.id, m] as const));
  let added = 0;
  let removed = 0;
  let changed = 0;
  for (const [id, m] of markersA) {
    const other = markersB.get(id);
    if (!other) removed++;
    else if (stable(m) !== stable(other)) changed++;
  }
  for (const id of markersB.keys()) if (!markersA.has(id)) added++;

  const counts: Record<ItemChangeKind, number> = { added: 0, removed: 0, moved: 0, trimmed: 0, changed: 0 };
  for (const change of items) counts[change.kind]++;

  return { items, tracks, settingsChanged, markers: { added, removed, changed }, lengthBefore: endOf(before), lengthAfter: endOf(after), counts };
}

/** A one-line summary ("3 added, 1 moved, 2 trimmed"), or "No differences". */
export function summarizeDiff(diff: TimelineDiff): string {
  const parts = (Object.keys(diff.counts) as ItemChangeKind[]).filter((k) => diff.counts[k] > 0).map((k) => `${diff.counts[k]} ${k}`);
  if (diff.tracks.length > 0) parts.push(`${diff.tracks.length} track change${diff.tracks.length === 1 ? '' : 's'}`);
  if (diff.settingsChanged.length > 0) parts.push('settings changed');
  const markerTotal = diff.markers.added + diff.markers.removed + diff.markers.changed;
  if (markerTotal > 0) parts.push(`${markerTotal} marker change${markerTotal === 1 ? '' : 's'}`);
  return parts.length === 0 ? 'No differences' : parts.join(', ');
}
