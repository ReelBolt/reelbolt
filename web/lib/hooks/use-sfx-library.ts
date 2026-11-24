'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import { sfxLibraryUrl, type SfxLibraryPage, type SfxLibraryQuery } from '../api/sfx-library';

/** One page of the built-in sound-effects library. Platform content, so the key does not depend on the project. */
export function useSfxLibrary(enabled: boolean, query: SfxLibraryQuery) {
  return useSWR<SfxLibraryPage>(enabled ? sfxLibraryUrl(query) : null, fetcher, {
    shouldRetryOnError: false,
    revalidateOnFocus: false,
    keepPreviousData: true,
  });
}
