'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import { revisionsPath } from '../api/revisions';
import type { RevisionHistory } from '../types/revisions';

/** How often the history refreshes while a change request is still waiting or working. */
export const REVISION_POLL_MS = 5000;

/** True while any change request in the history is still waiting to start or working. */
export function hasActiveRevision(history: RevisionHistory | undefined | null): boolean {
  return (history?.revisions ?? []).some((r) => r.status === 'Queued' || r.status === 'Running');
}

/**
 * The versions and change requests of a run's video. Pass `null` for `executionId` to hold the
 * request (e.g. until the execution itself has loaded). Polls only while a request is in flight;
 * call `mutate()` after starting one.
 */
export function useRevisions(projectId: string, executionId: string | null) {
  return useSWR<RevisionHistory>(
    projectId && executionId ? revisionsPath(projectId, executionId) : null,
    fetcher,
    {
      refreshInterval: (data) => (hasActiveRevision(data) ? REVISION_POLL_MS : 0),
      // A 404 means "this run has no video to change": there is nothing to retry.
      shouldRetryOnError: false,
    },
  );
}
