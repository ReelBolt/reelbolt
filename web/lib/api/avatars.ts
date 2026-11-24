import { apiFetch } from './client';
import type {
  AvatarMediaConsent,
  AvatarUsage,
  CreateAvatarRequest,
  CreateObjectAvatarRequest,
  DeleteAvatarResponse,
  ProjectAvatar,
  ReplaceAvatarReferencesRequest,
  ReplaceObjectReferencesRequest,
  UpdateAvatarRequest,
} from '../types/avatars';

/**
 * Base path of a project's avatars (docs/design/avatars.md "REST", docs/design/avatar-objects.md). Every route is
 * 404 while neither operator switch (people, objects) is on.
 */
export function avatarsPath(projectId: string): string {
  return `/api/v1/projects/${encodeURIComponent(projectId)}/avatars`;
}

export function avatarRequirementsPath(projectId: string): string {
  return `${avatarsPath(projectId)}/requirements`;
}

export function avatarUsagePath(projectId: string, avatarId: string): string {
  return `${avatarsPath(projectId)}/${encodeURIComponent(avatarId)}/usage`;
}

export function avatarMediaConsentPath(projectId: string): string {
  return `${avatarsPath(projectId)}/media-consent`;
}

const json = (method: string, body: unknown): RequestInit => ({
  method,
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(body),
});

/**
 * Creates an avatar of a person, or an object (`subjectKind: 'Object'`). The statement must match the server's
 * verbatim (the consent statement for a person, the object statement for an object), or nothing is created.
 */
export function createAvatar(projectId: string, request: CreateAvatarRequest | CreateObjectAvatarRequest): Promise<ProjectAvatar> {
  return apiFetch<ProjectAvatar>(avatarsPath(projectId), json('POST', request));
}

/** Name, description, voice link and disclosure label. */
export function updateAvatar(projectId: string, avatarId: string, request: UpdateAvatarRequest): Promise<ProjectAvatar> {
  return apiFetch<ProjectAvatar>(`${avatarsPath(projectId)}/${encodeURIComponent(avatarId)}`, json('PATCH', request));
}

/** Has the AI read the object's colour, make and model, marks and scale from its pictures. Without `overwrite` it fills only empty fields. */
export function deriveAvatarIdentityFacts(projectId: string, avatarId: string, overwrite = false): Promise<ProjectAvatar> {
  return apiFetch<ProjectAvatar>(
    `${avatarsPath(projectId)}/${encodeURIComponent(avatarId)}/identity-facts/derive`, json('POST', { overwrite }),
  );
}

/**
 * Changes the reference files: a re-attestation, the statement is sent again. For a person the retention
 * restarts; an object sends its own statement and no retention.
 */
export function replaceAvatarReferences(
  projectId: string, avatarId: string, request: ReplaceAvatarReferencesRequest | ReplaceObjectReferencesRequest,
): Promise<ProjectAvatar> {
  return apiFetch<ProjectAvatar>(`${avatarsPath(projectId)}/${encodeURIComponent(avatarId)}/references`, json('PUT', request));
}

/** Withdraws a person's consent: unusable everywhere from now on; deleted automatically after the grace period. An object has none (`409 AVATAR_NOT_A_PERSON`). */
export function revokeAvatarConsent(projectId: string, avatarId: string): Promise<ProjectAvatar> {
  return apiFetch<ProjectAvatar>(`${avatarsPath(projectId)}/${encodeURIComponent(avatarId)}/revoke-consent`, { method: 'POST' });
}

/** Deletes the avatar, its vendor copies and (for a person) its reference files; an object's files are kept. The response says what was actually removed. */
export function deleteAvatar(projectId: string, avatarId: string): Promise<DeleteAvatarResponse> {
  return apiFetch<DeleteAvatarResponse>(`${avatarsPath(projectId)}/${encodeURIComponent(avatarId)}`, { method: 'DELETE' });
}

export function getAvatarUsage(projectId: string, avatarId: string): Promise<AvatarUsage> {
  return apiFetch<AvatarUsage>(avatarUsagePath(projectId, avatarId));
}

/**
 * The project's consent to send avatar and object files to AI services from the editors and the assistant
 * (docs/design/avatar-objects.md "Avatar REST contract with objects"). Given and withdrawn only by a person in
 * the app: never a field of another request.
 */
export function getAvatarMediaConsent(projectId: string): Promise<AvatarMediaConsent> {
  return apiFetch<AvatarMediaConsent>(avatarMediaConsentPath(projectId));
}

export function grantAvatarMediaConsent(projectId: string): Promise<AvatarMediaConsent> {
  return apiFetch<AvatarMediaConsent>(avatarMediaConsentPath(projectId), { method: 'PUT' });
}

export function withdrawAvatarMediaConsent(projectId: string): Promise<AvatarMediaConsent> {
  return apiFetch<AvatarMediaConsent>(avatarMediaConsentPath(projectId), { method: 'DELETE' });
}
