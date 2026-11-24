/**
 * R09.7: how captions are delivered. The render burns in every enabled caption on an ENABLED track;
 * the SRT/WebVTT export writes every enabled caption on ANY track. So the choice is one switch on the
 * tracks that hold captions: on = burned into the picture (and still exportable), off = separate
 * files only. "Both" is the first with the sidecar files offered next to the render.
 */

import type { EditOp } from '../engine';
import type { EditTimeline } from '../types';

export type CaptionDelivery = 'burn' | 'sidecar' | 'both';

export interface CaptionTrackInfo {
  id: string;
  name: string;
  enabled: boolean;
}

/** The tracks that hold at least one enabled caption. */
export function captionTracks(doc: Pick<EditTimeline, 'tracks'>): CaptionTrackInfo[] {
  return doc.tracks
    .filter((t) => t.items.some((i) => i.type === 'caption' && i.enabled !== false))
    .map((t) => ({ id: t.id, name: t.name, enabled: t.enabled !== false }));
}

/** What the document says now: burned in unless every caption track is switched off. */
export function deliveryOf(tracks: readonly CaptionTrackInfo[]): 'burn' | 'sidecar' {
  return tracks.length > 0 && tracks.every((t) => !t.enabled) ? 'sidecar' : 'burn';
}

/** The ops that make the caption tracks match the choice (only the tracks that need to change). */
export function deliveryOps(tracks: readonly CaptionTrackInfo[], choice: CaptionDelivery): EditOp[] {
  const want = choice !== 'sidecar';
  return tracks.filter((t) => t.enabled !== want).map((t): EditOp => ({ type: 'setTrackProperty', trackId: t.id, key: 'enabled', value: want }));
}
