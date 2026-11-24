'use client';

import useSWR, { useSWRConfig } from 'swr';
import { fetcher } from '../api/client';
import {
  createProjectTag,
  deleteProjectTag,
  organizeProjects,
  setProjectOrganization,
  updateProjectTag,
} from '../api/project-organization';
import type { OrganizeProjectsBody, ProjectOrganizationBody, ProjectTag, ProjectTagBody } from '../types/project-organization';

export const TAGS_KEY = '/api/v1/project-tags';
export const PROJECTS_KEY = '/api/v1/projects';
export const OVERVIEW_KEY = '/api/v1/dashboard/overview';

export function useProjectTags() {
  return useSWR<ProjectTag[]>(TAGS_KEY, fetcher);
}

/** Revalidate everything a tag/folder change can touch (project rows, counts, overview, activity). */
export function useRevalidateOrganization() {
  const { mutate } = useSWRConfig();
  return () =>
    mutate(
      (key) =>
        typeof key === 'string' &&
        (key === PROJECTS_KEY ||
          key === OVERVIEW_KEY ||
          key.startsWith(`${TAGS_KEY}`) ||
          key.startsWith('/api/v1/project-folders')),
    );
}

export function useProjectTagMutations() {
  const revalidate = useRevalidateOrganization();
  const after = async <T,>(p: Promise<T>): Promise<T> => {
    const r = await p;
    await revalidate();
    return r;
  };
  return {
    createTag: (data: ProjectTagBody) => after(createProjectTag(data)),
    updateTag: (id: string, data: Partial<ProjectTagBody>) => after(updateProjectTag(id, data)),
    deleteTag: (id: string) => after(deleteProjectTag(id)),
    setOrganization: (projectId: string, data: ProjectOrganizationBody) => after(setProjectOrganization(projectId, data)),
    organize: (data: OrganizeProjectsBody) => after(organizeProjects(data)),
  };
}
