/**
 * P9 editor API client: per-user preferences (keymap, saved layouts), project presets
 * (transition / effect / mastering) and timecoded review comments. The only module that knows these
 * wire shapes (mirrors of `EditTimelineDto.cs`, camelCase); every call goes through `apiFetch`.
 */

import { apiFetch } from '../../api/client';
import { editTimelineKey, editTimelinesKey } from '../sync/api';

// ── preferences ──────────────────────────────────────────────────────────────

/** A saved panel layout: which workspace, the three panel sizes and the open tabs. */
export interface SavedLayout {
  name: string;
  workspace: string;
  /** Pixel sizes when saved (older layouts, and a fallback). */
  sizes: { left: number; right: number; timeline: number };
  /** R12.7: the same sizes as fractions of the window (left/right of width, timeline of height); preferred when present. */
  fractions?: { left: number; right: number; timeline: number };
  leftTab?: string | null;
  rightTab?: string | null;
}

export interface EditorPreferences {
  /** Action id → key chord; `""` unbinds the action. Absent actions keep their default. */
  keymap: Record<string, string>;
  layouts: SavedLayout[];
  updatedAt: string | null;
}

export const editorPreferencesKey = '/api/v1/editor/preferences';

export const getEditorPreferences = (): Promise<EditorPreferences> => apiFetch<EditorPreferences>(editorPreferencesKey);

export const saveEditorPreferences = (preferences: Pick<EditorPreferences, 'keymap' | 'layouts'>): Promise<EditorPreferences> =>
  apiFetch<EditorPreferences>(editorPreferencesKey, { method: 'PUT', body: JSON.stringify({ keymap: preferences.keymap, layouts: preferences.layouts }) });

// ── presets ──────────────────────────────────────────────────────────────────

export type PresetKind = 'transition' | 'effect' | 'mastering';

export interface TransitionPresetPayload {
  transitionType: string;
  easing?: string;
  framesBefore: number;
  framesAfter: number;
}

export interface EffectPresetPayload {
  effects: { type: string; amount?: number; enabled?: boolean }[];
}

export interface MasteringPresetPayload {
  targetLufs: number;
  truePeakDbtp: number;
  loudnessRange?: number;
}

export interface EditorPreset<P = unknown> {
  id: string;
  kind: PresetKind;
  name: string;
  payload: P;
  createdByUserId: string;
  createdAt: string;
  updatedAt: string;
}

export const editorPresetsKey = (projectId: string, kind?: PresetKind): string =>
  `${editTimelinesKey(projectId)}/presets${kind ? `?kind=${kind}` : ''}`;

export const listEditorPresets = <P = unknown>(projectId: string, kind?: PresetKind): Promise<EditorPreset<P>[]> =>
  apiFetch<EditorPreset<P>[]>(editorPresetsKey(projectId, kind));

export const createEditorPreset = <P>(projectId: string, kind: PresetKind, name: string, payload: P): Promise<EditorPreset<P>> =>
  apiFetch<EditorPreset<P>>(`${editTimelinesKey(projectId)}/presets`, { method: 'POST', body: JSON.stringify({ kind, name, payload }) });

export const deleteEditorPreset = (projectId: string, presetId: string): Promise<void> =>
  apiFetch<void>(`${editTimelinesKey(projectId)}/presets/${presetId}`, { method: 'DELETE' });

// ── review comments ──────────────────────────────────────────────────────────

export interface ReviewComment {
  id: string;
  timelineId: string;
  parentId: string | null;
  authorUserId: string;
  authorName: string;
  frame: number;
  durationFrames: number | null;
  body: string;
  resolved: boolean;
  createdAt: string;
  updatedAt: string;
  /** A comment left by a guest through a review link; `authorName` holds the guest's own typed name. */
  guest?: boolean;
  guestName?: string | null;
  /** `apiKey` when a public-API key wrote the comment; absent means a person. */
  actorKind?: 'person' | 'apiKey';
}

export const reviewCommentsKey = (projectId: string, timelineId: string): string => `${editTimelineKey(projectId, timelineId)}/comments`;

export const listReviewComments = (projectId: string, timelineId: string): Promise<ReviewComment[]> =>
  apiFetch<ReviewComment[]>(reviewCommentsKey(projectId, timelineId));

export const createReviewComment = (projectId: string, timelineId: string, body: { body: string; frame: number; durationFrames?: number | null; parentId?: string | null }): Promise<ReviewComment> =>
  apiFetch<ReviewComment>(reviewCommentsKey(projectId, timelineId), { method: 'POST', body: JSON.stringify(body) });

export const updateReviewComment = (projectId: string, timelineId: string, commentId: string, patch: { body?: string; resolved?: boolean }): Promise<ReviewComment> =>
  apiFetch<ReviewComment>(`${reviewCommentsKey(projectId, timelineId)}/${commentId}`, { method: 'PATCH', body: JSON.stringify(patch) });

export const deleteReviewComment = (projectId: string, timelineId: string, commentId: string): Promise<void> =>
  apiFetch<void>(`${reviewCommentsKey(projectId, timelineId)}/${commentId}`, { method: 'DELETE' });

/** Threads: roots by frame, each with its replies oldest first. */
export function threadComments(comments: readonly ReviewComment[]): { root: ReviewComment; replies: ReviewComment[] }[] {
  const roots = comments.filter((c) => c.parentId === null).sort((a, b) => a.frame - b.frame || a.createdAt.localeCompare(b.createdAt));
  return roots.map((root) => ({ root, replies: comments.filter((c) => c.parentId === root.id).sort((a, b) => a.createdAt.localeCompare(b.createdAt)) }));
}
