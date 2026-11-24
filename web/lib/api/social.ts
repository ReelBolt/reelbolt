import { ApiError, apiFetch } from './client';
import type {
  CreateSocialPostBody, SocialOutputKind, SocialPost, SocialPostContent, SocialPreflight, UpdateSocialPostBody,
} from '../types/social';

export type * from '../types/social';

const send = (method: string, body?: unknown): RequestInit => ({ method, body: body === undefined ? undefined : JSON.stringify(body) });

/** Navigates the browser to this URL: the platform's consent screen. `returnTo` is an `/app/…` path. */
export async function startSocialConnect(provider: string, returnTo: string): Promise<string> {
  const r = await apiFetch<{ authorizeUrl: string }>(`/api/v1/social/${encodeURIComponent(provider)}/connect`, send('POST', { returnTo }));
  return r.authorizeUrl;
}

export function disconnectSocialAccount(id: string): Promise<void> {
  return apiFetch<void>(`/api/v1/social/accounts/${id}`, send('DELETE'));
}

export interface PreflightTarget extends SocialPostContent {
  projectId: string;
  outputKind: SocialOutputKind;
  outputId: string;
  accountId: string;
}
export function preflightSocialPost(body: PreflightTarget): Promise<SocialPreflight> {
  return apiFetch<SocialPreflight>('/api/v1/social/posts/preflight', send('POST', body));
}

export function createSocialPost(body: CreateSocialPostBody): Promise<SocialPost> {
  return apiFetch<SocialPost>('/api/v1/social/posts', send('POST', body));
}

export function updateSocialPost(id: string, body: UpdateSocialPostBody): Promise<SocialPost> {
  return apiFetch<SocialPost>(`/api/v1/social/posts/${id}`, send('PATCH', body));
}

export function cancelSocialPost(id: string): Promise<SocialPost> {
  return apiFetch<SocialPost>(`/api/v1/social/posts/${id}/cancel`, send('POST'));
}

/** An `unknown` post is retried only after the person confirmed it is not on the channel. */
export function retrySocialPost(id: string, confirmNotPosted: boolean): Promise<SocialPost> {
  return apiFetch<SocialPost>(`/api/v1/social/posts/${id}/retry`, send('POST', { confirmNotPosted }));
}

/** The plan does not include publishing (402). */
export function isPlanGated(error: unknown): boolean {
  return error instanceof ApiError && error.status === 402;
}

/** A sentence from the server's refusal, or null so the caller shows its own. */
export function socialErrorMessage(error: unknown): string | null {
  return error instanceof ApiError && error.message ? error.message : null;
}
