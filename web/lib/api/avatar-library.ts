import { apiFetch } from './client';
import { avatarsPath } from './avatars';
import type { ProjectAvatar } from '../types/avatars';
import type {
  AvatarLibraryItem,
  AvatarLibraryListResponse,
  DeleteLibraryItemResponse,
  ImportLibraryAvatarRequest,
  PublishToLibraryRequest,
  ReconfirmLibraryItemRequest,
  UpdateLibraryItemRequest,
} from '../types/avatar-library';

/**
 * The workspace avatar library (docs/design/avatar-objects.md "The workspace avatar library"). Every route is 404
 * while neither operator switch (people, objects) is on, and for another workspace's item.
 */
export const AVATAR_LIBRARY_PATH = '/api/v1/avatar-library';

/** The list; with a project, each item also says whether that project already holds an import (`imported`). */
export function avatarLibraryPath(projectId?: string | null): string {
  return projectId ? `${AVATAR_LIBRARY_PATH}?projectId=${encodeURIComponent(projectId)}` : AVATAR_LIBRARY_PATH;
}

export function avatarLibraryItemPath(itemId: string): string {
  return `${AVATAR_LIBRARY_PATH}/${encodeURIComponent(itemId)}`;
}

/** A picture of a library item, streamed by the API (pictures only). */
export function avatarLibraryPictureUrl(itemId: string, referenceId: string): string {
  return `${avatarLibraryItemPath(itemId)}/references/${encodeURIComponent(referenceId)}/content`;
}

export function avatarImportPath(projectId: string): string {
  return `${avatarsPath(projectId)}/import`;
}

const json = (method: string, body: unknown): RequestInit => ({
  method,
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(body),
});

export function listAvatarLibrary(projectId?: string | null): Promise<AvatarLibraryListResponse> {
  return apiFetch<AvatarLibraryListResponse>(avatarLibraryPath(projectId));
}

export function getAvatarLibraryItem(itemId: string): Promise<AvatarLibraryItem> {
  return apiFetch<AvatarLibraryItem>(avatarLibraryItemPath(itemId));
}

/**
 * Saves a project avatar to the library (Owner or Admin). The source avatar then becomes a linked import of the
 * new item. The statement must match the server's workspace text verbatim, or nothing is saved.
 */
export function publishToAvatarLibrary(request: PublishToLibraryRequest): Promise<AvatarLibraryItem> {
  return apiFetch<AvatarLibraryItem>(AVATAR_LIBRARY_PATH, json('POST', request));
}

/** Name and description; every import follows. */
export function updateAvatarLibraryItem(itemId: string, request: UpdateLibraryItemRequest): Promise<AvatarLibraryItem> {
  return apiFetch<AvatarLibraryItem>(avatarLibraryItemPath(itemId), json('PATCH', request));
}

/** Confirms the workspace statement again (a person: the keep-until date starts again). No file changes. */
export function reconfirmAvatarLibraryItem(itemId: string, request: ReconfirmLibraryItemRequest): Promise<AvatarLibraryItem> {
  return apiFetch<AvatarLibraryItem>(`${avatarLibraryItemPath(itemId)}/reconfirm`, json('POST', request));
}

/** Withdraws a person's consent: unusable in every project at once. */
export function revokeAvatarLibraryConsent(itemId: string): Promise<AvatarLibraryItem> {
  return apiFetch<AvatarLibraryItem>(`${avatarLibraryItemPath(itemId)}/revoke-consent`, { method: 'POST' });
}

/** Deletes the item, its imports in every project and its stored files. The response says what was removed. */
export function deleteAvatarLibraryItem(itemId: string): Promise<DeleteLibraryItemResponse> {
  return apiFetch<DeleteLibraryItemResponse>(avatarLibraryItemPath(itemId), { method: 'DELETE' });
}

/** Imports a library item into a project: a linked project avatar (`fromLibrary: true`). */
export function importLibraryAvatar(projectId: string, libraryItemId: string): Promise<ProjectAvatar> {
  const body: ImportLibraryAvatarRequest = { libraryItemId };
  return apiFetch<ProjectAvatar>(avatarImportPath(projectId), json('POST', body));
}
