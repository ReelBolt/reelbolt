'use client';

import useSWR from 'swr';
import { getImageGraphics, imageGraphicsUrl } from '../api/image-documents';
import { isGraphicJobActive } from '../editor/graphics';
import type { ImageGraphicsResponse } from '../types/image-document';

/** How often the list is read while a design is waiting or running. */
export const GRAPHICS_POLL_MS = 4000;

/**
 * A picture's "Design a graphic" jobs, the cost of another one and whether this installation can
 * design at all. Polls every few seconds while any job is waiting or running — reading the list is
 * also what settles a finished job into the document on the server — and not at all otherwise.
 *
 * Keyed on the route's own URL, so `mutate(imageGraphicsUrl(p, d))` from anywhere (the assistant
 * queued a graphic) refreshes it.
 */
export function useImageGraphics(projectId: string | null | undefined, documentId: string | null | undefined) {
  return useSWR<ImageGraphicsResponse>(
    projectId && documentId ? imageGraphicsUrl(projectId, documentId) : null,
    () => getImageGraphics(projectId as string, documentId as string),
    {
      refreshInterval: (latest) => (latest?.graphics.some(isGraphicJobActive) ? GRAPHICS_POLL_MS : 0),
      revalidateOnFocus: true,
    },
  );
}
