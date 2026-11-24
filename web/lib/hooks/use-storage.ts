'use client';

import useSWR from 'swr';
import { getStorageUsage, STORAGE_USAGE_PATH } from '@/lib/api/storage';
import { listProjectFolderLinks } from '@/lib/api/folderLinks';
import type { StorageUsage } from '@/lib/types/storage';
import type { ProjectFolderLink } from '@/lib/types/folderLink';

/**
 * Workspace storage (docs/design/local-folder-sync-and-storage.md Part 2). Readable by any member —
 * it is the reason the person who hit the quota can see why.
 */
export function useStorageUsage() {
  return useSWR<StorageUsage>(STORAGE_USAGE_PATH, getStorageUsage);
}

/**
 * A project's linked computer folders. Read on mount and re-read whenever the window is focused
 * (`revalidateOnFocus`); the component also re-reads after each action. Deliberately NOT on an
 * interval: the desktop app is the thing that changes while this page sits open, and a poll would
 * only make the page noisier.
 */
export function useProjectFolderLinks(projectId: string | null | undefined) {
  const key = projectId ? `/api/v1/projects/${projectId}/folder-links` : null;
  return useSWR<ProjectFolderLink[]>(key, () => listProjectFolderLinks(projectId as string), {
    revalidateOnFocus: true,
    focusThrottleInterval: 5000,
  });
}
