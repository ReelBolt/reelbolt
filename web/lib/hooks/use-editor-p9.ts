'use client';

import useSWR from 'swr';
import {
  editorPreferencesKey,
  editorPresetsKey,
  getEditorPreferences,
  listEditorPresets,
  listReviewComments,
  reviewCommentsKey,
  type EditorPreferences,
  type EditorPreset,
  type PresetKind,
  type ReviewComment,
} from '../editing/api/editor-p9';

/** P9: the caller's editor preferences (keymap, layouts); empty defaults until the first save. */
export function useEditorPreferences() {
  return useSWR<EditorPreferences>(editorPreferencesKey, getEditorPreferences, { revalidateOnFocus: false });
}

/** P9: a project's presets of one kind. */
export function useEditorPresets<P = unknown>(projectId: string | null | undefined, kind: PresetKind) {
  return useSWR<EditorPreset<P>[]>(projectId ? editorPresetsKey(projectId, kind) : null, () => listEditorPresets<P>(projectId as string, kind));
}

/** P9: a timeline's review comments, refreshed every 15 s so a reviewer on another device shows up. */
export function useReviewComments(projectId: string | null | undefined, timelineId: string | null | undefined) {
  return useSWR<ReviewComment[]>(projectId && timelineId ? reviewCommentsKey(projectId, timelineId) : null, () => listReviewComments(projectId as string, timelineId as string), { refreshInterval: 15000 });
}
