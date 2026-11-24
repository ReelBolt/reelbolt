/**
 * The stabilised proxy: when a clip has `stabilize` on, the viewer decodes the service's stabilised copy of the
 * SOURCE (same duration, same picture geometry, audio dropped) instead of the plain proxy, and the clip carries a
 * `stabilize` job mark while it is being made. Pure helpers; the resolver (`proxy-urls.ts`) and the workspace use them.
 *
 * Parity wording for `parity.ts` (owned by T-001; the program lead applies it): while the proxy is being made the clip
 * is `renderRequired` (the picture shown is still the unstabilised one) with {@link stabilizePreparingReason}; a failed
 * run is `renderRequired` with {@link STABILIZE_FAILED_REASON}; once ready it is `approximate` with {@link STABILIZE_READY_REASON}.
 */

import type { EditTimeline, Item, MediaClip } from '../types';
import type { MediaDerivativeRow } from '../media/types';
import { mediaRefOf } from './scheduler';

/** The derivative kind name the service uses (`kinds: ['stabilizedProxy']`). */
export const STABILIZED_PROXY_KIND = 'stabilizedProxy';
/** The progress stage the service reports while it encodes (`media.derivative.progress`). */
export const STABILIZE_STAGE = 'Stabilize';
/** The job kind of its mark (the key of `JOB_READS`). */
export const STABILIZE_JOB_KIND = 'stabilize';

export const STABILIZE_READY_REASON = "Stabilised preview of the whole source; the export stabilises just this clip's span, so the first and last second can differ slightly.";
/** The stabilised preview could not be made: the clip shows the unstabilised picture, and the export still stabilises it. */
export const STABILIZE_FAILED_REASON = 'The stabilised preview is not available; the preview shows the unstabilised picture. The export still stabilises the clip.';
export const stabilizePreparingReason = (percent: number | null): string =>
  percent === null ? 'Stabilised preview is being prepared.' : `Stabilised preview is being prepared (${Math.round(percent)}%).`;

/** The stabilised proxy row to play: the largest picture, the later row on a tie. Null when none is listed. */
export function pickStabilizedRow<T extends Pick<MediaDerivativeRow, 'kind' | 'width' | 'height'>>(rows: readonly (T | null | undefined)[] | null | undefined): T | null {
  if (!Array.isArray(rows)) return null;
  let best: T | null = null;
  let bestArea = -1;
  for (const row of rows) {
    if (!row || row.kind !== STABILIZED_PROXY_KIND) continue;
    const area = (row.width ?? 0) * (row.height ?? 0);
    if (area >= bestArea) {
      best = row;
      bestArea = area;
    }
  }
  return best;
}

/** True for a media clip whose stabilisation is on. */
export const isStabilized = (item: Item): item is MediaClip => item.type === 'mediaClip' && item.stabilize === true;

/** Source -> the ids of the clips that use it with stabilisation on (tracks and sequences). */
export function stabilizedSourcesIn(doc: EditTimeline): Map<string, string[]> {
  const out = new Map<string, string[]>();
  const tracks = [...doc.tracks, ...(doc.sequences ?? []).flatMap((s) => s.tracks)];
  for (const track of tracks) {
    for (const item of track.items) {
      if (!isStabilized(item)) continue;
      const ref = mediaRefOf(item.assetRef);
      if (!ref) continue;
      out.set(ref, [...(out.get(ref) ?? []), item.id]);
    }
  }
  return out;
}
