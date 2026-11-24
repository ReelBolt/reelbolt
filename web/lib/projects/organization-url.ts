import type { FolderSelection } from './filter';

export interface OrganizationUrlState {
  folder: FolderSelection;
  tagIds: string[];
}

/** Reads `?folder=` (`unfiled` or an id) and repeatable `?tag=` from a query string. Unset = all, no tags. */
export function parseOrganizationParams(params: URLSearchParams): OrganizationUrlState {
  const folder = params.get('folder')?.trim();
  const tagIds = [...new Set(params.getAll('tag').map((t) => t.trim()).filter(Boolean))];
  return { folder: folder && folder !== 'all' ? folder : 'all', tagIds };
}

/** Returns `params` with folder/tag replaced by `state`; other params are preserved, defaults are omitted. */
export function withOrganizationParams(params: URLSearchParams, state: OrganizationUrlState): URLSearchParams {
  const next = new URLSearchParams(params);
  next.delete('folder');
  next.delete('tag');
  if (state.folder !== 'all') next.set('folder', state.folder);
  for (const id of new Set(state.tagIds)) next.append('tag', id);
  return next;
}

export function toggleTag(tagIds: readonly string[], id: string): string[] {
  return tagIds.includes(id) ? tagIds.filter((t) => t !== id) : [...tagIds, id];
}
