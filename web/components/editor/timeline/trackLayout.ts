/**
 * Pure helpers behind the track header menu (R03.5): the colour palette, the three row heights,
 * where a dragged track lands, and the sentences the menu shows. No React, no store: the header
 * component calls these and dispatches the closed-catalogue ops (`setTrackProperty`, `addTrack`,
 * `removeTrack`, `moveTrack`).
 */

import type { Track } from '@/lib/editing/types';

/** A track colour swatch: an English id (test ids; the shown name is the `trackMenu.swatches.<id>` message) and a 6-digit hex. */
export interface TrackSwatch {
  name: string;
  hex: string;
}

/**
 * Accessible swatches. Each one holds 3:1 (the WCAG non-text minimum) against both the dark and
 * the light lane backgrounds, so the colour bar on a header is visible in either scheme
 * (`__tests__/trackLayout.test.ts` proves it).
 */
export const TRACK_SWATCHES: readonly TrackSwatch[] = [
  { name: 'Blue', hex: '#3b82d6' },
  { name: 'Teal', hex: '#12907a' },
  { name: 'Green', hex: '#2f8f3a' },
  { name: 'Orange', hex: '#c8620a' },
  { name: 'Red', hex: '#d03a3a' },
  { name: 'Pink', hex: '#c0398f' },
  { name: 'Purple', hex: '#8a55d0' },
  { name: 'Grey', hex: '#7a808c' },
];

/** The lane backgrounds the swatches are checked against (see `paint.ts` DARK_THEME / LIGHT_THEME). */
export const LANE_BACKGROUNDS = ['#16171b', '#1a1b20', '#fbfbfc', '#f4f5f7'] as const;

export type TrackHeightPreset = 'small' | 'medium' | 'large';

export const TRACK_HEIGHT_PRESETS: Readonly<Record<TrackHeightPreset, number>> = { small: 40, medium: 64, large: 96 };

/** The preset whose height is nearest to `height` (a stored height need not be exactly a preset). */
export function nearestHeightPreset(height: number): TrackHeightPreset {
  let best: TrackHeightPreset = 'medium';
  let bestDelta = Infinity;
  for (const key of Object.keys(TRACK_HEIGHT_PRESETS) as TrackHeightPreset[]) {
    const delta = Math.abs(TRACK_HEIGHT_PRESETS[key] - height);
    if (delta < bestDelta) {
      best = key;
      bestDelta = delta;
    }
  }
  return best;
}

/** The index a track moves to for "up" / "down", or null when it is already at that end. */
export function moveTarget(index: number, count: number, direction: 'up' | 'down'): number | null {
  const to = direction === 'up' ? index - 1 : index + 1;
  return to < 0 || to >= count ? null : to;
}

/**
 * Where a dragged track lands: the row under `y` (a y inside the headers column, relative to its
 * first row). `rows` carries each row's height. Out-of-range y clamps to the first / last row.
 */
export function dropIndexAtY(rows: readonly number[], y: number): number {
  if (rows.length === 0) return 0;
  let top = 0;
  for (let i = 0; i < rows.length; i++) {
    if (y < top + rows[i]) return i;
    top += rows[i];
  }
  return rows.length - 1;
}

/** The index to add a track at: directly above or below the track at `index`. */
export function addIndex(index: number, where: 'above' | 'below'): number {
  return where === 'above' ? index : index + 1;
}

/** A name for a new track of `kind` that no existing track already uses (V3, A2 ...). */
export function nextTrackName(tracks: readonly Pick<Track, 'kind' | 'name'>[], kind: 'video' | 'audio'): string {
  const letter = kind === 'video' ? 'V' : 'A';
  const names = new Set(tracks.map((t) => t.name));
  let n = tracks.filter((t) => t.kind === kind).length + 1;
  while (names.has(`${letter}${n}`)) n += 1;
  return `${letter}${n}`;
}

/** A trimmed, non-empty track name, or null when the typed name should be refused (empty). */
export function cleanTrackName(raw: string): string | null {
  const name = raw.trim().replace(/\s+/g, ' ');
  return name.length === 0 ? null : name.slice(0, 60);
}

/** The sentence the remove-track confirmation shows, or null when the track is empty (no confirm needed). */
export function removeConfirmation(track: Pick<Track, 'name' | 'items'>): string | null {
  const n = track.items.length;
  if (n === 0) return null;
  return `${track.name} has ${n} ${n === 1 ? 'clip' : 'clips'}. Removing the track removes ${n === 1 ? 'it' : 'them'} too. You can undo this.`;
}

/** The three labelled view-only toggles; they change what the timeline paints, never the document. */
export interface TimelineView {
  waveforms: boolean;
  thumbnails: boolean;
  names: boolean;
}

export const DEFAULT_TIMELINE_VIEW: TimelineView = { waveforms: true, thumbnails: true, names: true };

export const TIMELINE_VIEW_STORAGE_KEY = 'reelforge.editor.timelineView';

/** Parses stored view toggles; anything unreadable falls back to the defaults per key. */
export function parseTimelineView(raw: string | null): TimelineView {
  if (!raw) return DEFAULT_TIMELINE_VIEW;
  try {
    const v = JSON.parse(raw) as Partial<Record<keyof TimelineView, unknown>>;
    return {
      waveforms: typeof v.waveforms === 'boolean' ? v.waveforms : true,
      thumbnails: typeof v.thumbnails === 'boolean' ? v.thumbnails : true,
      names: typeof v.names === 'boolean' ? v.names : true,
    };
  } catch {
    return DEFAULT_TIMELINE_VIEW;
  }
}
