import { apiFetch } from './client';
import type { EditingStyle, SaveEditingStyleRequest, StylePreset } from '../types/styles';

/** The built-in editing styles everyone can pick (see docs/editing-styles.md). */
export function listStylePresets(): Promise<StylePreset[]> {
  return apiFetch<StylePreset[]>('/api/v1/styles/presets');
}

/** The caller's saved styles; with a project, the ones usable in it (global + pinned to it). */
export function listEditingStyles(projectId?: string | null): Promise<EditingStyle[]> {
  return apiFetch<EditingStyle[]>(editingStylesUrl(projectId));
}

export function editingStylesUrl(projectId?: string | null): string {
  return projectId ? `/api/v1/styles?projectId=${encodeURIComponent(projectId)}` : '/api/v1/styles';
}

export function createEditingStyle(request: SaveEditingStyleRequest): Promise<EditingStyle> {
  return apiFetch<EditingStyle>('/api/v1/styles', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
}

export function updateEditingStyle(id: string, request: SaveEditingStyleRequest): Promise<EditingStyle> {
  return apiFetch<EditingStyle>(`/api/v1/styles/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
}

/** Deletes a saved style. Past runs keep their own copy of it. */
export function deleteEditingStyle(id: string): Promise<void> {
  return apiFetch<void>(`/api/v1/styles/${id}`, { method: 'DELETE' });
}
