import { apiFetch } from './client';
import type {
  CloneVoiceRequest,
  CreateLibraryVoiceRequest,
  DeleteVoiceResponse,
  ProjectVoice,
  VoiceLibraryPage,
} from '../types/voices';

/** Lists the project's voice library (see docs/voiceover.md "Voices and consent"). */
export function listProjectVoices(projectId: string): Promise<ProjectVoice[]> {
  return apiFetch<ProjectVoice[]>(`/api/v1/projects/${projectId}/voices`);
}

/** Registers a provider-library voice by its vendor id. No consent is captured: no person in the project is its subject. */
export function createLibraryVoice(projectId: string, request: CreateLibraryVoiceRequest): Promise<ProjectVoice> {
  return apiFetch<ProjectVoice>(`/api/v1/projects/${projectId}/voices`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
}

/** Clones a voice from the project's own recordings. The consent statement must match verbatim or the API creates nothing. */
export function cloneVoice(projectId: string, request: CloneVoiceRequest): Promise<ProjectVoice> {
  return apiFetch<ProjectVoice>(`/api/v1/projects/${projectId}/voices/clone`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
}

/** Revokes consent: the voice stays on record but is unselectable everywhere from now on. */
export function revokeVoiceConsent(projectId: string, voiceId: string): Promise<ProjectVoice> {
  return apiFetch<ProjectVoice>(`/api/v1/projects/${projectId}/voices/${voiceId}/revoke-consent`, { method: 'POST' });
}

/** Deletes the voice: vendor model, reference recordings and row — the response says what was actually removed. */
export function deleteVoice(projectId: string, voiceId: string): Promise<DeleteVoiceResponse> {
  return apiFetch<DeleteVoiceResponse>(`/api/v1/projects/${projectId}/voices/${voiceId}`, { method: 'DELETE' });
}

/** The URL of one library page; empty search/language are omitted so equal searches share a cache key. */
export function voiceLibraryUrl(projectId: string, search: string, language: string | null, page: number): string {
  const params = new URLSearchParams();
  if (search.trim()) params.set('q', search.trim());
  if (language) params.set('language', language);
  params.set('page', String(page));
  return `/api/v1/projects/${projectId}/voices/library?${params.toString()}`;
}

/** Browses the speech provider's public voice library (see docs/voiceover.md "Browsing the Fish voice library"). */
export function listVoiceLibrary(projectId: string, search: string, language: string | null, page: number): Promise<VoiceLibraryPage> {
  return apiFetch<VoiceLibraryPage>(voiceLibraryUrl(projectId, search, language, page));
}
