'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import type { OutputVideo } from '../api/outputs';

export interface OutputVideosOptions {
  /** Re-reads the list on this interval (ms) while `keepPolling` says the wanted file is still missing. */
  pollMs?: number;
  keepPolling?: (data: OutputVideo[] | undefined) => boolean;
}

export function useOutputVideos(projectId: string, options?: OutputVideosOptions) {
  const pollMs = options?.pollMs ?? 0;
  const keepPolling = options?.keepPolling;
  return useSWR<OutputVideo[]>(
    projectId ? `/api/v1/projects/${projectId}/outputs` : null,
    fetcher,
    pollMs > 0 && keepPolling ? { refreshInterval: (data) => (keepPolling(data) ? pollMs : 0) } : undefined,
  );
}
