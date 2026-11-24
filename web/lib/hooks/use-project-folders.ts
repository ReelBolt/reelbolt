'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import { createProjectFolder, deleteProjectFolder, updateProjectFolder } from '../api/project-organization';
import type { ProjectFolder, ProjectFolderBody } from '../types/project-organization';
import { useRevalidateOrganization } from './use-project-tags';

export const FOLDERS_KEY = '/api/v1/project-folders';

export function useProjectFolders() {
  return useSWR<ProjectFolder[]>(FOLDERS_KEY, fetcher);
}

export function useProjectFolderMutations() {
  const revalidate = useRevalidateOrganization();
  const after = async <T,>(p: Promise<T>): Promise<T> => {
    const r = await p;
    await revalidate();
    return r;
  };
  return {
    createFolder: (data: ProjectFolderBody) => after(createProjectFolder(data)),
    renameFolder: (id: string, data: ProjectFolderBody) => after(updateProjectFolder(id, data)),
    deleteFolder: (id: string) => after(deleteProjectFolder(id)),
  };
}
