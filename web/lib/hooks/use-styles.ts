'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import { editingStylesUrl } from '../api/styles';
import type { EditingStyle, StylePreset } from '../types/styles';

/** The built-in editing styles. They change only with a deploy, so they are never revalidated on focus. */
export function useStylePresets() {
  return useSWR<StylePreset[]>('/api/v1/styles/presets', fetcher, { revalidateOnFocus: false });
}

/** The caller's saved styles — every one, or (with a project) the ones usable in it. */
export function useEditingStyles(projectId?: string | null) {
  return useSWR<EditingStyle[]>(editingStylesUrl(projectId), fetcher);
}
