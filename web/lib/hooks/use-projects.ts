'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import type { Project } from '../types/project';

/** The active workspace's projects. Pass `enabled: false` to ask nothing. */
export function useProjects(enabled = true) {
  return useSWR<Project[]>(enabled ? '/api/v1/projects' : null, fetcher);
}

export function useProject(id: string) {
  return useSWR<Project>(id ? `/api/v1/projects/${id}` : null, fetcher);
}
