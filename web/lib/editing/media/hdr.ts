/**
 * R10.7: phone HDR footage (HLG, PQ). The render converts such a source to standard (SDR BT.709) colours
 * BEFORE the grade. A proxy is now tone-mapped when it is made, but the viewer itself does not tone-map (no shader
 * operator: it would be a second, unverified implementation of the renderer's), so a proxy built before that change
 * still shows HDR as decoded. The honest minimum stays: say so, in the Inspector and in the parity badge.
 */
import type { MediaProbe } from './types';

export type HdrKind = 'hlg' | 'pq';

/** Looks up a media clip's HDR kind from the media pool (null = standard colours or not known). */
export type HdrKindLookup = (clip: { assetRef: { kind: string; projectFileId?: string | null; stepResultId?: string | null } }) => HdrKind | null;

/** The kind a stored probe states. An old probe without the field, or any other value, is not HDR. */
export function hdrKindOfProbe(probe: Pick<MediaProbe, 'hdr'> | null | undefined): HdrKind | null {
  const hdr = probe?.hdr;
  return hdr === 'hlg' || hdr === 'pq' ? hdr : null;
}

/** The parity reason (plain words; the badge shows it as is). */
export const HDR_PREVIEW_REASON = 'Preview shows this HDR clip approximately; the delivered file is converted to standard colours before grading.';

/** The small note the Inspector shows on an HDR clip. */
export const HDR_CLIP_NOTE = 'HDR clip';
