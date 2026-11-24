'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import type { PronunciationList } from '../types/pronunciation';

/** The project's pronunciation glossary (applies to spoken narration only). */
export function useProjectPronunciation(projectId: string | null | undefined) {
  return useSWR<PronunciationList>(projectId ? `/api/v1/projects/${projectId}/pronunciation` : null, fetcher);
}
