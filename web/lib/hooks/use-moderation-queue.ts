'use client';

import useSWR, { mutate } from 'swr';
import { fetcher } from '../api/client';
import {
  MODERATION_REVIEW_QUEUE_DEFAULT_LIMIT,
  MODERATION_SETTINGS_PATH,
  acknowledgeModerationItem,
  getModerationReviewQueueUrl,
} from '../api/moderation';
import type {
  ModerationAcknowledgeResponse,
  ModerationReviewQueue,
  ModerationSettings,
} from '../types/moderation';

/**
 * The one review-queue key the page reads and the acknowledgement revalidates.
 *
 * It is the fully-built URL, so the SWR cache entry and the revalidation agree
 * even though the default filter is written down here rather than at each call
 * site.
 */
export const MODERATION_REVIEW_QUEUE_KEY = getModerationReviewQueueUrl({
  state: 'pending',
  limit: MODERATION_REVIEW_QUEUE_DEFAULT_LIMIT,
});

/** Pending mid-band decisions, refreshed through SWR. `enabled=false` makes no request. */
export function useModerationQueue(enabled = true) {
  return useSWR<ModerationReviewQueue>(enabled ? MODERATION_REVIEW_QUEUE_KEY : null, fetcher);
}

/** The effective moderation settings, for the page's read-only strip. `enabled=false` makes no request. */
export function useModerationSettings(enabled = true) {
  return useSWR<ModerationSettings>(enabled ? MODERATION_SETTINGS_PATH : null, fetcher);
}

/**
 * Acknowledges one item and then revalidates the review queue, so the row's
 * state comes from the server rather than from local optimism. Returns the
 * server's acknowledgement unchanged; throws `ApiError` when the call fails.
 */
export async function acknowledgeModerationItemAndRevalidate(
  id: string,
): Promise<ModerationAcknowledgeResponse> {
  const result = await acknowledgeModerationItem(id);
  await mutate(MODERATION_REVIEW_QUEUE_KEY);
  return result;
}
