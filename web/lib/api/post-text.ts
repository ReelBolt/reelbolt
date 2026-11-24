import { ApiError, apiFetch } from './client';
import type {
  PostTextEstimate,
  PostTextPreparing,
  PostTextRequest,
  PostTextResult,
  SavedPostText,
  SavePostTextBody,
} from '../types/post-text';

export type * from '../types/post-text';

export function postTextsPath(projectId: string, outputId: string): string {
  return `/api/v1/projects/${projectId}/outputs/${outputId}/post-texts`;
}

const json = (body: unknown): RequestInit => ({ method: 'POST', body: JSON.stringify(body) });

/** What the request would cost, line by line. Writes and holds nothing. */
export function estimatePostText(projectId: string, outputId: string, request: PostTextRequest): Promise<PostTextEstimate> {
  return apiFetch<PostTextEstimate>(`${postTextsPath(projectId, outputId)}/estimate`, json(request));
}

export function isPreparing(r: PostTextResult | PostTextPreparing): r is PostTextPreparing {
  return (r as PostTextPreparing).status === 'preparing';
}

/** Writes the text. Answers `{ status: 'preparing' }` (202) while the video's frames or transcript are not ready. */
export function writePostText(projectId: string, outputId: string, request: PostTextRequest): Promise<PostTextResult | PostTextPreparing> {
  return apiFetch<PostTextResult | PostTextPreparing>(postTextsPath(projectId, outputId), json(request));
}

export function savePostText(projectId: string, outputId: string, platform: string, body: SavePostTextBody): Promise<SavedPostText> {
  return apiFetch<SavedPostText>(`${postTextsPath(projectId, outputId)}/${encodeURIComponent(platform)}`, {
    method: 'PUT',
    body: JSON.stringify(body),
  });
}

/** Starts the transcript on the media analysis route the estimate named. */
export function startTranscript(path: string, kinds: readonly string[]): Promise<unknown> {
  return apiFetch<unknown>(path, json({ kinds, language: null }));
}

export type PostTextRefusal = 'credits' | 'held' | 'other';

/** 402 INSUFFICIENT_CREDITS and 409 (quarantine / unpriced model) are told apart; the server sentence is kept. */
export function refusalOf(error: unknown): PostTextRefusal {
  if (!(error instanceof ApiError)) return 'other';
  if (error.status === 402 || error.code === 'INSUFFICIENT_CREDITS') return 'credits';
  if (error.status === 409) return 'held';
  return 'other';
}
