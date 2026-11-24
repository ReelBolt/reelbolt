'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import type { ProjectTimelineSummary } from '../types/timeline';

/** The project's workflow renders that carry a timeline manifest (each can seed an editable timeline). */
export function useProjectTimelines(projectId: string | null | undefined) {
  return useSWR<ProjectTimelineSummary[]>(
    projectId ? `/api/v1/projects/${projectId}/timelines` : null,
    fetcher
  );
}
