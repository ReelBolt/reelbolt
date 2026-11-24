'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import { mediaAnnotationsKey, mediaBinsKey, mediaPoolKey } from '../editing/api/media';
import type { MediaAnnotation, MediaBin, MediaPoolItem } from '../editing/media/types';

/**
 * The project's media pool.
 *
 * The URL is built by `web/lib/editing/api/media.ts` — this hook never spells a wire path, so the
 * media API client stays the only module that knows the shape. No polling: a pool changes when the
 * user uploads or a render finishes, and neither is observable from here (there is no version field
 * to watch), so an interval would be pure noise.
 */
export function useMediaPool(projectId: string | null | undefined) {
  return useSWR<MediaPoolItem[]>(projectId ? mediaPoolKey(projectId) : null, fetcher);
}

/**
 * The project's media bins, flat, each carrying `itemCount`.
 *
 * `itemCount` is the whole membership story the API tells — there is no per-bin item listing — so a
 * caller must render a count rather than a clip list.
 */
export function useMediaBins(projectId: string | null | undefined) {
  return useSWR<MediaBin[]>(projectId ? mediaBinsKey(projectId) : null, fetcher);
}

/**
 * P6: every annotation of the project (keywords, note, colour label, favourite, flag), one row per
 * annotated source. A source with no row is simply unannotated.
 */
export function useMediaAnnotations(projectId: string | null | undefined) {
  return useSWR<MediaAnnotation[]>(projectId ? mediaAnnotationsKey(projectId) : null, fetcher);
}
