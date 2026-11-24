'use client';

import useSWR from 'swr';
import { getProjectGitHubLink } from '../api/github';
import type { ProjectGitHubLink } from '../types/github';

/**
 * The project's linked repository (docs/github-app.md). `data` is null when the project has no
 * link — the endpoint's 404 `not_linked` is read as an answer, not as an error, so "not linked yet"
 * and "the request failed" stay distinguishable on screen.
 */
export function useProjectGitHub(projectId: string | null | undefined) {
  return useSWR<ProjectGitHubLink | null>(
    projectId ? `/api/v1/projects/${projectId}/github` : null,
    () => getProjectGitHubLink(projectId as string),
  );
}
