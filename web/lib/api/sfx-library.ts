import { apiFetch } from './client';

/** One built-in sound as the API shows it (SfxLibraryController). No storage key. */
export interface SfxLibraryItem {
  id: string;
  slug: string;
  title: string;
  category: string;
  tags: string[];
  durationMs: number;
  sizeBytes: number;
  mimeType: string;
  licence: string;
  author: string | null;
  sourceUrl: string | null;
}

export interface SfxLibraryCategoryCount {
  category: string;
  count: number;
}

export interface SfxLibraryPage {
  items: SfxLibraryItem[];
  total: number;
  page: number;
  pageSize: number;
  categories: SfxLibraryCategoryCount[];
}

export interface SfxLibraryAddResult {
  mediaRef: string;
  fileId: string;
  name: string;
}

export interface SfxLibraryStarterSetResult {
  items: SfxLibraryAddResult[];
  added: number;
}

/** Refusal codes of the add calls. */
export const SFX_LIBRARY_PROJECT_CAP = 'SFX_LIBRARY_PROJECT_CAP';
export const SFX_LIBRARY_RATE_LIMIT = 'SFX_LIBRARY_RATE_LIMIT';

export const SFX_PAGE_SIZE = 50;

export interface SfxLibraryQuery {
  search: string;
  category: string | null;
  maxDurationMs: number | null;
  page: number;
}

export function sfxLibraryUrl(q: SfxLibraryQuery): string {
  const params = new URLSearchParams();
  if (q.search.trim()) params.set('query', q.search.trim());
  if (q.category) params.set('category', q.category);
  if (q.maxDurationMs !== null) params.set('maxDurationMs', String(q.maxDurationMs));
  params.set('page', String(q.page));
  params.set('pageSize', String(SFX_PAGE_SIZE));
  return `/api/v1/sfx-library?${params.toString()}`;
}

/** The audio stream of one library sound (an <audio src>; same-origin, cookie-authenticated). */
export function sfxPreviewUrl(id: string): string {
  return `/api/v1/sfx-library/${id}/preview`;
}

/** Copies a library sound into the project (idempotent) and returns its `file:<guid>` reference. */
export function addSfxLibraryItem(projectId: string, id: string): Promise<SfxLibraryAddResult> {
  return apiFetch<SfxLibraryAddResult>(`/api/v1/projects/${projectId}/sfx-library/${id}/add`, { method: 'POST' });
}

export function addSfxStarterSet(projectId: string): Promise<SfxLibraryStarterSetResult> {
  return apiFetch<SfxLibraryStarterSetResult>(`/api/v1/projects/${projectId}/sfx-library/starter-set`, { method: 'POST' });
}
