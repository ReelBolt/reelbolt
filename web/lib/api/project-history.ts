import { apiFetch } from './client';
import type {
  ProjectHistoryChangesResponse,
  ProjectHistoryItemDiff,
  ProjectHistoryRestorePreview,
  ProjectHistoryRestoreRequest,
  ProjectHistoryRestoreResult,
  ProjectHistoryVersion,
  ProjectHistoryVersionDetail,
  ProjectHistoryVersionsResponse,
} from '../types/project-history';

/**
 * The project-history client (contract sections 5 and 7).
 *
 * Every id that goes into a path is encoded: project and version ids are GUIDs and item keys are
 * caller-supplied strings, so a malformed one must never change the shape of the URL.
 */
const base = (projectId: string) =>
  `/api/v1/projects/${encodeURIComponent(projectId)}/history`;

const versionBase = (projectId: string, versionId: string) =>
  `${base(projectId)}/versions/${encodeURIComponent(versionId)}`;

const itemBase = (projectId: string, versionId: string, kind: string, key: string) =>
  `${versionBase(projectId, versionId)}/items/${encodeURIComponent(kind)}/${encodeURIComponent(key)}`;

/** `to` is a version id or the literal `current`; absent means the live project. */
function withTarget(url: string, to?: string | null): string {
  const target = to?.trim() || 'current';
  return `${url}?to=${encodeURIComponent(target)}`;
}

/** The SWR key for the version list. `limit` is clamped server-side to 1..200. */
export function projectHistoryVersionsUrl(
  projectId: string,
  options: { limit?: number; before?: number } = {},
): string {
  const params = new URLSearchParams();
  if (options.limit !== undefined) params.set('limit', String(options.limit));
  if (options.before !== undefined) params.set('before', String(options.before));
  const query = params.toString();
  return `${base(projectId)}/versions${query ? `?${query}` : ''}`;
}

/** The SWR key for one version and its items. */
export function projectHistoryVersionUrl(projectId: string, versionId: string): string {
  return versionBase(projectId, versionId);
}

/** The SWR key for the changes between a version and `to` (`current` by default). */
export function projectHistoryChangesUrl(projectId: string, versionId: string, to?: string | null): string {
  return withTarget(`${versionBase(projectId, versionId)}/changes`, to);
}

/** The SWR key for one item's diff against `to` (`current` by default). */
export function projectHistoryItemDiffUrl(
  projectId: string,
  versionId: string,
  kind: string,
  key: string,
  to?: string | null,
): string {
  return withTarget(`${itemBase(projectId, versionId, kind, key)}/diff`, to);
}

/** The download URL of a file item's bytes at that version (opened directly, not through SWR). */
export function projectHistoryItemContentUrl(
  projectId: string,
  versionId: string,
  kind: string,
  key: string,
): string {
  return `${itemBase(projectId, versionId, kind, key)}/content`;
}

/** The project's versions, newest first, with the retained bytes and retention window. */
export function listProjectHistoryVersions(
  projectId: string,
  options: { limit?: number; before?: number } = {},
): Promise<ProjectHistoryVersionsResponse> {
  return apiFetch<ProjectHistoryVersionsResponse>(projectHistoryVersionsUrl(projectId, options));
}

/** Saves the project's current state as a named version (cause `named`). */
export function createProjectHistoryVersion(
  projectId: string,
  name: string,
): Promise<ProjectHistoryVersion> {
  return apiFetch<ProjectHistoryVersion>(`${base(projectId)}/versions`, {
    method: 'POST',
    body: JSON.stringify({ name }),
  });
}

/** One version with its items. */
export function getProjectHistoryVersion(
  projectId: string,
  versionId: string,
): Promise<ProjectHistoryVersionDetail> {
  return apiFetch<ProjectHistoryVersionDetail>(projectHistoryVersionUrl(projectId, versionId));
}

/** Names a version, or un-names it (`null`/`''`) so it expires again. */
export function renameProjectHistoryVersion(
  projectId: string,
  versionId: string,
  name: string | null,
): Promise<ProjectHistoryVersion> {
  return apiFetch<ProjectHistoryVersion>(`${projectHistoryVersionUrl(projectId, versionId)}/name`, {
    method: 'PUT',
    body: JSON.stringify({ name }),
  });
}

/** Deletes a version; the retention sweep frees its bytes afterwards. */
export function deleteProjectHistoryVersion(projectId: string, versionId: string): Promise<void> {
  return apiFetch<void>(projectHistoryVersionUrl(projectId, versionId), { method: 'DELETE' });
}

/** "Delete forever": erases a file's content from every version of the project. */
export function eraseProjectHistoryFile(projectId: string, fileId: string): Promise<void> {
  return apiFetch<void>(`${base(projectId)}/files/${encodeURIComponent(fileId)}`, { method: 'DELETE' });
}

/** The items that changed between the version and another version, or the live project. */
export function getProjectHistoryChanges(
  projectId: string,
  versionId: string,
  to?: string | null,
): Promise<ProjectHistoryChangesResponse> {
  return apiFetch<ProjectHistoryChangesResponse>(
    projectHistoryChangesUrl(projectId, versionId, to),
  );
}

/** One item's diff: text hunks, a binary size change, or a kind-specific document diff. */
export function getProjectHistoryItemDiff(
  projectId: string,
  versionId: string,
  kind: string,
  key: string,
  to?: string | null,
): Promise<ProjectHistoryItemDiff> {
  return apiFetch<ProjectHistoryItemDiff>(
    projectHistoryItemDiffUrl(projectId, versionId, kind, key, to),
  );
}

/** What a restore would change and what it must skip. Writes nothing. */
export function previewProjectHistoryRestore(
  projectId: string,
  versionId: string,
  request: ProjectHistoryRestoreRequest,
): Promise<ProjectHistoryRestorePreview> {
  return apiFetch<ProjectHistoryRestorePreview>(
    `${versionBase(projectId, versionId)}/restore/preview`,
    { method: 'POST', body: JSON.stringify(request) },
  );
}

/** Applies a restore; its safety version is saved first so the restore can be undone. */
export function restoreProjectHistory(
  projectId: string,
  versionId: string,
  request: ProjectHistoryRestoreRequest,
): Promise<ProjectHistoryRestoreResult> {
  return apiFetch<ProjectHistoryRestoreResult>(
    `${versionBase(projectId, versionId)}/restore`,
    { method: 'POST', body: JSON.stringify(request) },
  );
}
