'use client';

import useSWR from 'swr';
import {
  listCloudConnections,
  listCloudDrives,
  listCloudProviders,
  listProjectCloudLinks,
} from '@/lib/api/cloudStorage';
import type { CloudConnection, CloudDrive, CloudProvider, ProjectCloudLink } from '@/lib/types/cloudStorage';

/** All three providers and whether this server has each set up. Changes only with a redeploy. */
export function useCloudProviders() {
  return useSWR<CloudProvider[]>('/api/v1/cloud-storage/providers', listCloudProviders, { revalidateOnFocus: false });
}

/** The caller's connected accounts in the active workspace. */
export function useCloudConnections(enabled = true) {
  return useSWR<CloudConnection[]>(enabled ? '/api/v1/cloud-storage/connections' : null, listCloudConnections);
}

/** A project's linked cloud folders. */
export function useProjectCloudLinks(projectId: string | null | undefined) {
  return useSWR<ProjectCloudLink[]>(
    projectId ? `/api/v1/projects/${projectId}/cloud-links` : null,
    () => listProjectCloudLinks(projectId as string),
  );
}

/** The drives one connection can choose a folder from. */
export function useCloudDrives(connectionId: string | null | undefined) {
  return useSWR<CloudDrive[]>(
    connectionId ? `/api/v1/cloud-storage/connections/${connectionId}/drives` : null,
    () => listCloudDrives(connectionId as string),
    { revalidateOnFocus: false },
  );
}
