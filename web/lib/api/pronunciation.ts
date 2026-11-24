import { apiFetch } from './client';
import type { PronunciationEntry, PronunciationInput, PronunciationList } from '../types/pronunciation';

const base = (projectId: string) => `/api/v1/projects/${projectId}/pronunciation`;
const json = { 'Content-Type': 'application/json' };

export function listPronunciation(projectId: string): Promise<PronunciationList> {
  return apiFetch<PronunciationList>(base(projectId));
}

export function createPronunciation(projectId: string, input: PronunciationInput): Promise<PronunciationEntry> {
  return apiFetch<PronunciationEntry>(base(projectId), { method: 'POST', headers: json, body: JSON.stringify(input) });
}

export function updatePronunciation(projectId: string, id: string, input: PronunciationInput): Promise<PronunciationEntry> {
  return apiFetch<PronunciationEntry>(`${base(projectId)}/${id}`, { method: 'PUT', headers: json, body: JSON.stringify(input) });
}

export function deletePronunciation(projectId: string, id: string): Promise<void> {
  return apiFetch<void>(`${base(projectId)}/${id}`, { method: 'DELETE' });
}
