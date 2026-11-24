'use client';

import useSWR from 'swr';
import { listReviewLinks, reviewLinksKey, type ReviewLinkSummary } from '../editing/review/guest-links';

/** The share links of one timeline (owner side). Refreshes every 30 s so a guest's note shows up as unread. */
export function useReviewLinks(projectId: string | null | undefined, timelineId: string | null | undefined) {
  const res = useSWR<ReviewLinkSummary[]>(projectId && timelineId ? reviewLinksKey(projectId, timelineId) : null, () => listReviewLinks(projectId as string, timelineId as string), {
    refreshInterval: 30000,
  });
  // Only a list is a list of links: anything else (a stale cache entry, a wrong shape) reads as none.
  return { ...res, data: Array.isArray(res.data) ? res.data : undefined };
}
