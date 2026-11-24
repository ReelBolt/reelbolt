import { ApiError, apiFetch } from './client';
import type {
  CloudConnection,
  CloudConnectFailure,
  CloudDrive,
  CloudDriveKind,
  CloudFolderList,
  CloudProvider,
  CloudSyncQueued,
  CreateProjectCloudLinkRequest,
  ProjectCloudLink,
  UpdateProjectCloudLinkRequest,
} from '../types/cloudStorage';

/**
 * Cloud folder sync — Google Drive, OneDrive/SharePoint and Dropbox (docs/design/cloud-drive-sync.md).
 * Mirrors `lib/api/github.ts`: the server is the only authority, every refusal is a JSON body with an
 * `error` code and (usually) a plain `message`, and nothing on screen ever shows a code.
 */

const BASE = '/api/v1/cloud-storage';

const json = (method: string, body: unknown): RequestInit => ({
  method,
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(body),
});

/** `GET /api/v1/cloud-storage/providers` — all three, each with `configured`. */
export function listCloudProviders(): Promise<CloudProvider[]> {
  return apiFetch<CloudProvider[]>(`${BASE}/providers`);
}

/** `GET /api/v1/cloud-storage/connections` — the caller's connected accounts in this workspace. */
export function listCloudConnections(): Promise<CloudConnection[]> {
  return apiFetch<CloudConnection[]>(`${BASE}/connections`);
}

/** `DELETE /api/v1/cloud-storage/connections/{id}` — forgets the account (`204`). */
export function disconnectCloudConnection(connectionId: string): Promise<void> {
  return apiFetch<void>(`${BASE}/connections/${encodeURIComponent(connectionId)}`, { method: 'DELETE' });
}

/**
 * `POST /api/v1/cloud-storage/{provider}/oauth/start` — starts connecting an account and returns the
 * provider's sign-in URL. `returnTo` is an `/app/…` path the browser comes back to (the server ignores
 * anything else and falls back to `/app/settings`).
 */
export async function startCloudConnect(provider: string, returnTo: string): Promise<string> {
  const body = await apiFetch<{ authorizeUrl: string }>(
    `${BASE}/${encodeURIComponent(provider)}/oauth/start`,
    json('POST', { returnTo }),
  );
  return body.authorizeUrl;
}

/** `GET connections/{id}/drives` — the spaces a connection can choose a folder from, its own first. */
export function listCloudDrives(connectionId: string): Promise<CloudDrive[]> {
  return apiFetch<CloudDrive[]>(`${BASE}/connections/${encodeURIComponent(connectionId)}/drives`);
}

/** `GET connections/{id}/folders` — one level of a folder. Null ids: the own space, its root. */
export function listCloudFolder(connectionId: string, driveId: string | null, folderId: string | null): Promise<CloudFolderList> {
  const query = new URLSearchParams();
  if (driveId) query.set('driveId', driveId);
  if (folderId) query.set('folderId', folderId);
  const qs = query.toString();
  return apiFetch<CloudFolderList>(`${BASE}/connections/${encodeURIComponent(connectionId)}/folders${qs ? `?${qs}` : ''}`);
}

const links = (projectId: string) => `/api/v1/projects/${encodeURIComponent(projectId)}/cloud-links`;

/** `GET /api/v1/projects/{projectId}/cloud-links`. */
export function listProjectCloudLinks(projectId: string): Promise<ProjectCloudLink[]> {
  return apiFetch<ProjectCloudLink[]>(links(projectId));
}

/** `POST /api/v1/projects/{projectId}/cloud-links` — links a folder (`201`) and queues its first sync. */
export function createProjectCloudLink(projectId: string, request: CreateProjectCloudLinkRequest): Promise<ProjectCloudLink> {
  return apiFetch<ProjectCloudLink>(links(projectId), json('POST', request));
}

/** `PATCH .../cloud-links/{linkId}` — changes the chosen files; a real change queues a sync. */
export function updateProjectCloudLink(projectId: string, linkId: string, request: UpdateProjectCloudLinkRequest): Promise<ProjectCloudLink> {
  return apiFetch<ProjectCloudLink>(`${links(projectId)}/${encodeURIComponent(linkId)}`, json('PATCH', request));
}

/** `DELETE .../cloud-links/{linkId}` — stops syncing (`204`); the files stay in the project. */
export function deleteProjectCloudLink(projectId: string, linkId: string): Promise<void> {
  return apiFetch<void>(`${links(projectId)}/${encodeURIComponent(linkId)}`, { method: 'DELETE' });
}

/** `POST .../cloud-links/{linkId}/sync` — "Sync now": `202 queued | already_syncing`. */
export function syncProjectCloudLink(projectId: string, linkId: string): Promise<CloudSyncQueued> {
  return apiFetch<CloudSyncQueued>(`${links(projectId)}/${encodeURIComponent(linkId)}/sync`, { method: 'POST' });
}

/** The `/app/…` path to come back to after connecting, for this project's Cloud storage tab. */
export function projectCloudReturnPath(projectId: string): string {
  return `/app/projects/${encodeURIComponent(projectId)}?tab=cloud`;
}

// ---- words ----

/** A space's kind, as people call it. */
export function describeDriveKind(kind: CloudDriveKind): string {
  switch (kind) {
    case 'personal': return 'Your files';
    case 'sharedDrive': return 'Shared drive';
    case 'sharePointLibrary': return 'SharePoint library';
    case 'teamSpace': return 'Team space';
    default: return 'Drive';
  }
}

function bodyOf(error: unknown): Record<string, unknown> | null {
  if (!(error instanceof ApiError)) return null;
  const body = error.body;
  return typeof body === 'object' && body !== null ? (body as Record<string, unknown>) : null;
}

/** The machine-readable `error` code of a refusal, when it has one. */
export function cloudErrorCode(error: unknown): string | null {
  const code = bodyOf(error)?.error;
  return typeof code === 'string' && code !== '' ? code : null;
}

/** Seconds to wait, from a `429` body's `retryAfterSeconds`. */
export function cloudRetryAfterSeconds(error: unknown): number | null {
  const value = bodyOf(error)?.retryAfterSeconds;
  return typeof value === 'number' && value > 0 ? Math.ceil(value) : null;
}

/** True when the fix is to connect the account again. */
export function needsReconnect(error: unknown): boolean {
  return cloudErrorCode(error) === 'reconnect_required';
}

const CODE_SHAPE = /^[a-z0-9]+(_[a-z0-9]+)+$/;

/** The server's own sentence, when it sent prose (never a bare code). */
function serverMessage(error: unknown): string | null {
  const message = bodyOf(error)?.message;
  return typeof message === 'string' && message.trim() && !CODE_SHAPE.test(message.trim()) ? message.trim() : null;
}

/** "Try again in 12 seconds." */
export function tryAgainSentence(error: unknown): string {
  const seconds = cloudRetryAfterSeconds(error);
  return seconds
    ? `A sync was started a moment ago. Try again in ${seconds} second${seconds === 1 ? '' : 's'}.`
    : 'A sync was started a moment ago. Try again in a few seconds.';
}

/**
 * The sentence for any refused cloud-storage request. A `429` always says how long to wait; otherwise
 * the server's own plain `message` wins, and each documented code has a fallback sentence so that a
 * body without one never prints a code at the user.
 */
export function describeCloudError(error: unknown, fallback = 'That did not work. Try again in a moment.'): string {
  const code = cloudErrorCode(error);
  if (code === 'sync_too_soon' || (error instanceof ApiError && error.status === 429)) return tryAgainSentence(error);

  const message = serverMessage(error);
  if (message) return message;

  switch (code) {
    case 'provider_not_found':
      return 'That service is not available here.';
    case 'no_workspace':
      return 'Choose a workspace before connecting an account.';
    case 'link_not_found':
      return 'That linked folder no longer exists. Reload the page.';
    case 'connection_not_found':
      return 'That connected account could not be found. It may have been disconnected — connect it again.';
    case 'folder_not_found':
      return 'That folder could not be found, or this account cannot open it.';
    case 'invalid_id':
      return 'That drive or folder could not be used. Choose it again from the list.';
    case 'invalid_include_paths':
      return 'Some of the chosen files or folders could not be used. Clear the choice and choose again.';
    case 'already_linked':
      return 'This project already has a folder linked from that service. Reload the page to see it.';
    case 'reconnect_required':
      return 'This account no longer gives ReelBolt access. Reconnect it to carry on.';
    case 'provider_error':
      return 'The service did not answer as expected. Wait a moment and try again.';
    case 'provider_not_configured':
    case 'public_base_url_missing':
      return 'Connecting this service is not set up on this server yet. Ask your administrator.';
  }

  if (error instanceof ApiError && error.status === 404) return 'That could not be found. Reload the page.';
  return fallback;
}

/**
 * The sentence for `?cloud=connected|error&provider=…&reason=…`, which the connect callback redirects
 * back with. Null when there is nothing to say.
 */
export function describeCloudConnectOutcome(
  outcome: string | null | undefined,
  providerName: string,
  reason: string | null | undefined,
): { color: 'green' | 'yellow' | 'red'; message: string } | null {
  if (outcome === 'connected') return { color: 'green', message: `${providerName} account connected. Now choose a folder.` };
  if (outcome !== 'error') return null;

  switch (reason as CloudConnectFailure) {
    case 'denied':
      return { color: 'yellow', message: `You did not approve ReelBolt on ${providerName}, so no account was connected.` };
    case 'expired':
      return { color: 'yellow', message: 'That sign-in took too long and expired. Start again.' };
    case 'invalid_state':
    case 'session_mismatch':
      return {
        color: 'red',
        message: 'That sign-in could not be matched to you — it may have been started in another browser, or you ' +
          'signed in as someone else meanwhile. Start again from this page.',
      };
    case 'account_unavailable':
      return { color: 'red', message: `${providerName} approved the request but did not say which account it was for. Try again.` };
    case 'not_configured':
      return { color: 'red', message: `Connecting ${providerName} is not set up on this server yet. Ask your administrator.` };
    case 'exchange_failed':
    default:
      return { color: 'red', message: `Connecting your ${providerName} account did not finish. Try again.` };
  }
}

/** The display name for a slug, from the providers list when it is loaded. */
export function providerName(slug: string | null | undefined, providers?: CloudProvider[] | null): string {
  const known = providers?.find((p) => p.provider === slug)?.displayName;
  if (known) return known;
  switch (slug) {
    case 'google-drive': return 'Google Drive';
    case 'onedrive': return 'OneDrive';
    case 'dropbox': return 'Dropbox';
    default: return 'cloud storage';
  }
}
