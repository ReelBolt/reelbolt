'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import { postTextsPath } from '../api/post-text';
import type { SavedPostTextList } from '../types/post-text';

/** The post texts saved for one output. Pass `null` to hold the request (dialog closed). */
export function useSavedPostTexts(projectId: string, outputId: string | null) {
  return useSWR<SavedPostTextList>(
    projectId && outputId ? postTextsPath(projectId, outputId) : null,
    fetcher,
    { shouldRetryOnError: false },
  );
}
