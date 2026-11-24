import { apiFetch } from './client';
import type {
  AvatarBuild, AvatarBuildListResponse, AvatarBuildStarted, StartVariantRequest, VariantEstimate, VariantEstimateRequest,
} from '../types/avatar-builds';

/**
 * Variants made by the picture model and the builds that make them (docs/design/avatar-objects.md "Avatar
 * variants"). A variant made from other files is an ordinary create (`createAvatar` with the three variant fields).
 * A server from before variants answers 404 on every route here.
 */
// The base path of a project's avatars (the same one `lib/api/avatars.ts` builds).
const avatarsPath = (projectId: string): string => `/api/v1/projects/${encodeURIComponent(projectId)}/avatars`;

export function avatarBuildsPath(projectId: string): string {
  return `${avatarsPath(projectId)}/builds`;
}

export function avatarBuildPath(projectId: string, jobId: string): string {
  return `${avatarBuildsPath(projectId)}/${encodeURIComponent(jobId)}`;
}

function variantsPath(projectId: string, avatarId: string): string {
  return `${avatarsPath(projectId)}/${encodeURIComponent(avatarId)}/variants`;
}

const post = (body?: unknown): RequestInit => (body === undefined
  ? { method: 'POST' }
  : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });

/** What making the variant would cost. Writes nothing and starts nothing. */
export function estimateAvatarVariant(projectId: string, avatarId: string, request: VariantEstimateRequest): Promise<VariantEstimate> {
  return apiFetch<VariantEstimate>(`${variantsPath(projectId, avatarId)}/estimate`, post(request));
}

/** Starts the build (`202`). `409 CONSENT_REQUIRED` means the project's permission to send media is not recorded yet. */
export function startAvatarVariant(projectId: string, avatarId: string, request: StartVariantRequest): Promise<AvatarBuildStarted> {
  return apiFetch<AvatarBuildStarted>(variantsPath(projectId, avatarId), post(request));
}

export function listAvatarBuilds(projectId: string): Promise<AvatarBuildListResponse> {
  return apiFetch<AvatarBuildListResponse>(avatarBuildsPath(projectId));
}

export function getAvatarBuild(projectId: string, jobId: string): Promise<AvatarBuild> {
  return apiFetch<AvatarBuild>(avatarBuildPath(projectId, jobId));
}

/**
 * Stops a running build. The answer may or may not carry the build (an accepted cancel with an empty body is
 * fine); callers read the list again.
 */
export async function cancelAvatarBuild(projectId: string, jobId: string): Promise<AvatarBuild | undefined> {
  try {
    return await apiFetch<AvatarBuild | undefined>(`${avatarBuildPath(projectId, jobId)}/cancel`, post());
  } catch (e) {
    if (e instanceof SyntaxError) return undefined;
    throw e;
  }
}
