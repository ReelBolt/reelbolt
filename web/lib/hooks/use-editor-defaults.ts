'use client';

import useSWR from 'swr';
import { getEditorDefaults } from '../api/design-system';
import type { EditorDefaults } from '../types/design-system';

/** The design-system URL the editor's defaults hang off, so `mutate` can refresh it by key. */
export function editorDefaultsUrl(projectId: string): string {
  return `/api/v1/projects/${projectId}/design-system/editor-defaults`;
}

/**
 * SPEC §16.3: the current approved design system's text-layer defaults, fetched once per project.
 * A project with no usable design member (or none selected) resolves to null and the editor keeps
 * exactly its own defaults. Never revalidated on focus or refetch: the defaults follow the approved
 * design version, so the editor picks them up when it mounts, not on every window focus.
 */
export function useEditorDefaults(projectId: string | null | undefined) {
  return useSWR<EditorDefaults | null>(
    projectId ? editorDefaultsUrl(projectId) : null,
    () => getEditorDefaults(projectId as string),
    { revalidateOnFocus: false, revalidateIfStale: false, shouldRetryOnError: false },
  );
}
