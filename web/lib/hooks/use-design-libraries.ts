'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import type { DesignLibrarySummary, DesignVersionSummary } from '../types/design-system';

/** SPEC §16.1/P4: the active organization's brand libraries (`/api/v1/org/design-libraries`). */
const BASE = '/api/v1/org/design-libraries';

/** Every library of the active organization, by name. */
export function useDesignLibraries() {
  return useSWR<DesignLibrarySummary[]>(BASE, fetcher);
}

/** One library's versions, newest first. A null `libraryId` leaves the key null, so nothing is fetched. */
export function useDesignLibraryVersions(libraryId: string | null | undefined) {
  return useSWR<DesignVersionSummary[]>(libraryId ? `${BASE}/${libraryId}/versions` : null, fetcher);
}
