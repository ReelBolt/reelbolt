'use client';

import { useCallback } from 'react';
import { useSWRConfig } from 'swr';

/**
 * Revalidate everything the design system hangs off for one project — the summary, version lists,
 * version documents, sources, proposals and the editor defaults — after an action that changes what
 * "current" means (approve, reject, restore). Keys are matched by prefix so a list that is not
 * mounted right now is simply marked stale and refetches when it is next shown.
 */
export function useRevalidateDesignSystem(projectId: string) {
  const { mutate } = useSWRConfig();
  return useCallback(
    () =>
      mutate(
        (key) =>
          typeof key === 'string' && key.startsWith(`/api/v1/projects/${projectId}/design-system`),
      ),
    [mutate, projectId],
  );
}
