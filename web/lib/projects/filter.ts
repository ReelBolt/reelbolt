import type { ProjectOverview } from '../types/dashboard';
import type { Project } from '../types/project';

export type ProjectFilter = 'all' | 'running' | 'renders' | 'empty';
export type ProjectSort = 'activity' | 'name' | 'created' | 'files';
/** `all` = no folder filter, `unfiled` = projects in no folder, otherwise a folder id. */
export type FolderSelection = 'all' | 'unfiled' | (string & {});

export function filterAndSortProjects(
  projects: Project[],
  overview: Map<string, ProjectOverview>,
  { query, filter, sort, folder = 'all', tagIds = [] }: {
    query: string;
    filter: ProjectFilter;
    sort: ProjectSort;
    folder?: FolderSelection;
    /** Selected tags; a project must carry every one (AND). */
    tagIds?: readonly string[];
  },
): Project[] {
  const q = query.trim().toLowerCase();
  const activity = (p: Project) => Date.parse(overview.get(p.id)?.lastActivityAt ?? p.updatedAt);
  return projects
    .filter((p) => {
      if (folder === 'unfiled' && p.folderId) return false;
      if (folder !== 'all' && folder !== 'unfiled' && p.folderId !== folder) return false;
      if (tagIds.length > 0) {
        const have = new Set((p.tags ?? []).map((t) => t.id));
        if (!tagIds.every((id) => have.has(id))) return false;
      }
      if (q && !`${p.name} ${p.description ?? ''}`.toLowerCase().includes(q)) return false;
      const o = overview.get(p.id);
      if (filter === 'running') return (o?.activeExecutionCount ?? 0) > 0;
      if (filter === 'renders') return (o?.renderCount ?? 0) > 0;
      if (filter === 'empty') return (o?.sourceFileCount ?? 0) === 0;
      return true;
    })
    .sort((a, b) => {
      if (sort === 'name') return a.name.localeCompare(b.name, undefined, { sensitivity: 'base', numeric: true });
      if (sort === 'created') return Date.parse(b.createdAt) - Date.parse(a.createdAt);
      if (sort === 'files') return (overview.get(b.id)?.sourceFileCount ?? 0) - (overview.get(a.id)?.sourceFileCount ?? 0);
      return activity(b) - activity(a);
    });
}
