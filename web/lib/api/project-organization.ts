import { msg, type Msg } from '@/i18n/msg';
import type { T } from '@/i18n/translator';
import { enT } from '../i18n-default';
import { apiFetch } from './client';
import { friendlyMessage } from './error-messages';
import type {
  OrganizeProjectsBody,
  ProjectFolder,
  ProjectFolderBody,
  ProjectOrganizationBody,
  ProjectTag,
  ProjectTagBody,
  TagActivityResponse,
  TagDetailActivityResponse,
} from '../types/project-organization';
import type { Project } from '../types/project';

const json = (method: string, body: unknown): RequestInit => ({ method, body: JSON.stringify(body) });

export const getProjectTags = () => apiFetch<ProjectTag[]>('/api/v1/project-tags');
export const createProjectTag = (data: ProjectTagBody) => apiFetch<ProjectTag>('/api/v1/project-tags', json('POST', data));
export const updateProjectTag = (id: string, data: Partial<ProjectTagBody>) =>
  apiFetch<ProjectTag>(`/api/v1/project-tags/${encodeURIComponent(id)}`, json('PATCH', data));
export const deleteProjectTag = (id: string) => apiFetch<void>(`/api/v1/project-tags/${encodeURIComponent(id)}`, { method: 'DELETE' });

export const getProjectFolders = () => apiFetch<ProjectFolder[]>('/api/v1/project-folders');
export const createProjectFolder = (data: ProjectFolderBody) =>
  apiFetch<ProjectFolder>('/api/v1/project-folders', json('POST', data));
export const updateProjectFolder = (id: string, data: ProjectFolderBody) =>
  apiFetch<ProjectFolder>(`/api/v1/project-folders/${encodeURIComponent(id)}`, json('PATCH', data));
export const deleteProjectFolder = (id: string) => apiFetch<void>(`/api/v1/project-folders/${encodeURIComponent(id)}`, { method: 'DELETE' });

export const setProjectOrganization = (projectId: string, data: ProjectOrganizationBody) =>
  apiFetch<Project>(`/api/v1/projects/${encodeURIComponent(projectId)}/organization`, json('PUT', data));

/** All-or-nothing bulk change; the server answers 204. */
export const organizeProjects = (data: OrganizeProjectsBody) =>
  apiFetch<void>('/api/v1/projects/organize', json('POST', data));

export const getTagActivityUrl = (days: number) => `/api/v1/project-tags/activity?days=${days}`;
export const getTagDetailActivityUrl = (id: string, days: number) => `/api/v1/project-tags/${encodeURIComponent(id)}/activity?days=${days}`;
export const getTagActivity = (days: number) => apiFetch<TagActivityResponse>(getTagActivityUrl(days));
export const getTagDetailActivity = (id: string, days: number) =>
  apiFetch<TagDetailActivityResponse>(getTagDetailActivityUrl(id, days));

const CODE_MESSAGES: Record<string, Msg> = {
  TAG_NAME_TAKEN: msg('errors.projectOrg.TAG_NAME_TAKEN'),
  TAG_LIMIT_REACHED: msg('errors.projectOrg.TAG_LIMIT_REACHED'),
  FOLDER_NAME_TAKEN: msg('errors.projectOrg.FOLDER_NAME_TAKEN'),
  FOLDER_LIMIT_REACHED: msg('errors.projectOrg.FOLDER_LIMIT_REACHED'),
  PROJECT_TAG_LIMIT_REACHED: msg('errors.projectOrg.PROJECT_TAG_LIMIT_REACHED'),
};

/** Plain sentence for a tag/folder/organize failure; unknown errors fall back to the shared friendly mapper. */
export function projectOrganizationErrorMessage(error: unknown, t: T = enT): string {
  const code =
    error && typeof error === 'object' && 'code' in error ? (error as { code: unknown }).code : null;
  const known = typeof code === 'string' ? CODE_MESSAGES[code] : undefined;
  return known ? t.msg(known) : friendlyMessage(error, undefined, t);
}
