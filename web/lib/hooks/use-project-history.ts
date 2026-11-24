'use client';

import useSWR from 'swr';
import useSWRInfinite from 'swr/infinite';
import { fetcher } from '../api/client';
import {
  projectHistoryChangesUrl,
  projectHistoryItemDiffUrl,
  projectHistoryVersionUrl,
  projectHistoryVersionsUrl,
} from '../api/project-history';
import type {
  ProjectHistoryChangesResponse,
  ProjectHistoryItemDiff,
  ProjectHistoryVersionDetail,
  ProjectHistoryVersionsResponse,
} from '../types/project-history';

/** One page of versions as the list requests them (the API's default is 50; the contract allows 1..200). */
export const HISTORY_PAGE_SIZE = 50;

/**
 * The project's saved versions, newest first, paged through the `before={number}` cursor.
 *
 * `data` is the array of loaded pages; call `setSize(size + 1)` to load older versions. The cursor is
 * the number of the oldest version already loaded, so pages never overlap and every version can be
 * reached. A page shorter than `HISTORY_PAGE_SIZE` means there is nothing older.
 */
export function useProjectHistoryVersionPages(projectId: string | null | undefined) {
  return useSWRInfinite<ProjectHistoryVersionsResponse>(
    (index, previous) => {
      if (!projectId) return null;
      if (index === 0) return projectHistoryVersionsUrl(projectId, { limit: HISTORY_PAGE_SIZE });
      if (!previous || previous.versions.length === 0) return null;
      const oldest = previous.versions[previous.versions.length - 1];
      return projectHistoryVersionsUrl(projectId, { limit: HISTORY_PAGE_SIZE, before: oldest.number });
    },
    fetcher,
  );
}

/** The project's saved versions, newest first, plus the history storage figures. */
export function useProjectHistoryVersions(projectId: string | null | undefined) {
  return useSWR<ProjectHistoryVersionsResponse>(
    projectId ? projectHistoryVersionsUrl(projectId) : null,
    fetcher,
  );
}

/** One version and its items. */
export function useProjectHistoryVersion(
  projectId: string | null | undefined,
  versionId: string | null | undefined,
) {
  return useSWR<ProjectHistoryVersionDetail>(
    projectId && versionId ? projectHistoryVersionUrl(projectId, versionId) : null,
    fetcher,
  );
}

/** The items that changed between the version and `to` (`current` means the live project). */
export function useProjectHistoryChanges(
  projectId: string | null | undefined,
  versionId: string | null | undefined,
  to: string,
) {
  return useSWR<ProjectHistoryChangesResponse>(
    projectId && versionId ? projectHistoryChangesUrl(projectId, versionId, to) : null,
    fetcher,
  );
}

/** One item's diff against `to`; disabled while no item is selected. */
export function useProjectHistoryItemDiff(
  projectId: string | null | undefined,
  versionId: string | null | undefined,
  kind: string | null | undefined,
  key: string | null | undefined,
  to: string,
) {
  return useSWR<ProjectHistoryItemDiff>(
    projectId && versionId && kind && key
      ? projectHistoryItemDiffUrl(projectId, versionId, kind, key, to)
      : null,
    fetcher,
  );
}
