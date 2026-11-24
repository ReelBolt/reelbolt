'use client';

import useSWR from 'swr';
import { listGitHubBranches, listGitHubFiles, listGitHubRepositories } from '../api/github';
import type { GitHubBranchList, GitHubFileList, GitHubRepositoryList } from '../types/github';

/**
 * The repositories the connected person may link to this project, straight from GitHub's own answer.
 *
 * Keyed on the project because the endpoint is: it is the project that supplies the workspace whose
 * connection is used, and the project whose access check guards the call.
 */
export function useGitHubRepositories(projectId: string | null | undefined, enabled = true) {
  return useSWR<GitHubRepositoryList>(
    projectId && enabled ? `/api/v1/projects/${projectId}/github/repositories` : null,
    () => listGitHubRepositories(projectId as string),
  );
}

/**
 * Every file and folder of one branch. `repositoryId` null means the repository already linked to the
 * project; `branch` null means the linked branch (or the default branch for a new pick).
 */
export function useGitHubFiles(
  projectId: string | null | undefined,
  repositoryId: number | null,
  branch: string | null,
  enabled = true,
) {
  return useSWR<GitHubFileList>(
    projectId && enabled ? `/api/v1/projects/${projectId}/github/files?${repositoryId ?? 'linked'}:${branch ?? ''}` : null,
    () => listGitHubFiles(projectId as string, repositoryId, branch),
    // A tree is large and changes only on a push; one read per opening is plenty.
    { revalidateOnFocus: false },
  );
}

/** The branches of a repository (null = the linked one), default branch first. */
export function useGitHubBranches(projectId: string | null | undefined, repositoryId: number | null, enabled = true) {
  return useSWR<GitHubBranchList>(
    projectId && enabled ? `/api/v1/projects/${projectId}/github/branches?${repositoryId ?? 'linked'}` : null,
    () => listGitHubBranches(projectId as string, repositoryId),
    { revalidateOnFocus: false },
  );
}
