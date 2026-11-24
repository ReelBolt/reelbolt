'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import type { ProjectFile, ProjectFileContent } from '../types/project';

function needsPolling(files: ProjectFile[] | undefined | null) {
  if (!files) return false;
  return files.some(
    (f) =>
      f.summaryStatus === 'Pending' ||
      f.summaryStatus === 'Processing' ||
      f.indexingStatus === 'Pending' ||
      f.indexingStatus === 'Processing',
  );
}

export function useProjectFiles(projectId: string) {
  return useSWR<ProjectFile[]>(
    projectId ? `/api/v1/projects/${projectId}/files` : null,
    fetcher,
    {
      refreshInterval: (data) => (needsPolling(data) ? 10000 : 0),
    },
  );
}

export function useProjectFileContent(projectId: string, fileId: string | null) {
  return useSWR<ProjectFileContent>(
    projectId && fileId ? `/api/v1/projects/${projectId}/files/${fileId}/content` : null,
    fetcher,
  );
}
