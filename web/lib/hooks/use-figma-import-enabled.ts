'use client';

import useSWR from 'swr';
import { ApiError } from '../api/client';
import { importFigmaVariables } from '../api/design-system';

/** One global key: the switch is a server setting, the same for every project. */
export const FIGMA_IMPORT_KEY = 'design-system:figma-import-enabled';

/**
 * Whether this server has the Figma variables import switched on. The route answers
 * `404 { code: "feature_disabled" }` when it is off and rejects an empty file key with a `400` when
 * it is on — in both cases before any token is read — so one call with empty values tells the two
 * apart without spending a token. Asked once, then remembered for the page's lifetime; any answer
 * other than "disabled" (including a network error) counts as available, since the server stays the
 * real guard.
 */
export function useFigmaImportEnabled(projectId: string, active: boolean): boolean {
  const { data } = useSWR<boolean>(
    active ? FIGMA_IMPORT_KEY : null,
    async () => {
      try {
        await importFigmaVariables(projectId, '', '');
        return true;
      } catch (e) {
        return !(e instanceof ApiError && e.status === 404 && e.code === 'feature_disabled');
      }
    },
    {
      revalidateOnFocus: false,
      revalidateOnReconnect: false,
      revalidateIfStale: false,
      shouldRetryOnError: false,
      dedupingInterval: 60 * 60 * 1000,
    },
  );
  return data !== false;
}
