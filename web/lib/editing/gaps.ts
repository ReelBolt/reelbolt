import type { EditTimeline } from './types';

/**
 * The gaps around a selection, for the Clip menu's "Close gap" actions. A gap is the empty span
 * between two neighbouring items on one track, or between frame 0 and a first item that starts
 * later (the engine's own definition: `deleteGap` removes any of them, `closeGaps` only the
 * interior ones and keeps the first item where it is).
 */
export interface GapInfo {
  trackId: string;
  trackName: string;
  /** Start frame of the gap directly before the first selected item, or null when it butts up to its neighbour. */
  beforeAtFrame: number | null;
  /** Length of that gap in frames (0 when there is none). */
  beforeLength: number;
  /** Interior gaps on the track (what "close all gaps" would remove). */
  interiorGaps: number;
}

export function gapInfoFor(doc: EditTimeline, selection: readonly string[]): GapInfo | null {
  for (const id of selection) {
    for (const track of doc.tracks) {
      const index = track.items.findIndex((item) => item.id === id);
      if (index < 0) continue;
      const item = track.items[index];
      const previousEnd = index === 0 ? 0 : track.items[index - 1].recordOut;
      let interiorGaps = 0;
      for (let i = 0; i + 1 < track.items.length; i++) if (track.items[i + 1].recordIn > track.items[i].recordOut) interiorGaps++;
      const length = item.recordIn - previousEnd;
      return {
        trackId: track.id,
        trackName: track.name,
        beforeAtFrame: length > 0 ? previousEnd : null,
        beforeLength: Math.max(0, length),
        interiorGaps,
      };
    }
  }
  return null;
}
