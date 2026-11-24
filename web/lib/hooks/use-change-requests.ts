'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import { changeCapabilitiesPath, changeRequestsPath } from '../api/change-requests';
import type { StepChangeCapabilities, StepChangeRequestList } from '../types/change-requests';

export const CHANGE_REQUEST_POLL_MS = 5000;

export function hasActiveChangeRequest(list: StepChangeRequestList | undefined | null): boolean {
  return (list?.requests ?? []).some((r) => r.status === 'Queued' || r.status === 'Running' || r.status === 'Pending');
}

/** What each finished step of a run accepts. Pass `null` to hold the request. */
export function useChangeCapabilities(projectId: string, executionId: string | null) {
  return useSWR<StepChangeCapabilities>(
    projectId && executionId ? changeCapabilitiesPath(projectId, executionId) : null,
    fetcher,
    { shouldRetryOnError: false },
  );
}

/** Change requests made against a run, or that are the run itself. Polls while one is in flight; call `mutate()` after creating one. */
export function useChangeRequests(projectId: string, executionId: string | null) {
  return useSWR<StepChangeRequestList>(
    projectId && executionId ? changeRequestsPath(projectId, executionId) : null,
    fetcher,
    {
      refreshInterval: (data) => (hasActiveChangeRequest(data) ? CHANGE_REQUEST_POLL_MS : 0),
      shouldRetryOnError: false,
    },
  );
}
