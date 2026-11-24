'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import { VIDEO_SPEND_LIMIT_PATH, type VideoSpendLimit } from '../api/video-spend-limit';

/** The caller's own daily video limit, with what they used today. */
export function useVideoSpendLimit() {
  return useSWR<VideoSpendLimit>(VIDEO_SPEND_LIMIT_PATH, fetcher);
}
