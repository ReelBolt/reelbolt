'use client';

import { useMemo } from 'react';
import useSWR from 'swr';
import { fetcher } from '../api/client';
import { avatarLibraryItemPath, avatarLibraryPath } from '../api/avatar-library';
import { isLibraryOff, libraryItemsOf, libraryRequirementsOf } from '../avatars/library';
import type { AvatarLibraryItem, AvatarLibraryListResponse } from '../types/avatar-library';

/**
 * The workspace avatar library. With a project, each item says whether that project already holds an import.
 * A 404 means the library is off on this server (`off`), so an error is an answer and never retried.
 * Pass `enabled: false` to ask nothing (a closed picker, a server that has no library).
 */
export function useAvatarLibrary(projectId?: string | null, enabled = true) {
  const { data, error, isLoading, mutate } = useSWR<AvatarLibraryListResponse>(
    enabled ? avatarLibraryPath(projectId) : null, fetcher, { shouldRetryOnError: false },
  );
  const items = useMemo(() => (data ? libraryItemsOf(data) : undefined), [data]);
  const library = useMemo(() => libraryRequirementsOf(data), [data]);
  return { items, library, error, isLoading, mutate, off: !data && isLibraryOff(error) };
}

/** One library item, read fresh (before a destructive action, for the current number of projects that use it). */
export function useAvatarLibraryItem(itemId: string | null | undefined) {
  return useSWR<AvatarLibraryItem>(itemId ? avatarLibraryItemPath(itemId) : null, fetcher, { shouldRetryOnError: false });
}

/**
 * Whether the workspace has an avatar library to show (the navigation asks once). False while loading and on
 * any error: the link then stays hidden instead of leading to a page that cannot load.
 */
export function useAvatarLibraryAvailable(enabled = true): boolean {
  const { data, error } = useSWR<AvatarLibraryListResponse>(enabled ? avatarLibraryPath() : null, fetcher, {
    shouldRetryOnError: false,
    revalidateOnFocus: false,
    revalidateOnReconnect: false,
  });
  return Boolean(data) && !error;
}
