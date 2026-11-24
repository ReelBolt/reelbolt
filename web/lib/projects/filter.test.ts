import { describe, expect, it } from 'vitest';
import type { ProjectOverview } from '../types/dashboard';
import type { Project } from '../types/project';
import { filterAndSortProjects } from './filter';

const p = (id: string, name: string, updatedAt: string): Project => ({ id, name, description: '', status: 'Draft', userId: 'u', createdAt: updatedAt, updatedAt });
const ov = (id: string, over: Partial<ProjectOverview>): ProjectOverview => ({
  projectId: id, sourceFileCount: 0, videoCount: 0, audioCount: 0, imageCount: 0, totalBytes: 0, workflowCount: 0, renderCount: 0,
  activeExecutionCount: 0, failedExecutionCount: 0, lastActivityAt: '2026-01-01T00:00:00Z', latestOutput: null, ...over,
});

const projects = [p('a', 'Alpha', '2026-01-01T00:00:00Z'), p('b', 'Beta', '2026-01-02T00:00:00Z'), p('c', 'gamma', '2026-01-03T00:00:00Z')];
const map = new Map([
  ['a', ov('a', { sourceFileCount: 5, renderCount: 2, lastActivityAt: '2026-03-01T00:00:00Z' })],
  ['b', ov('b', { activeExecutionCount: 1, sourceFileCount: 1, lastActivityAt: '2026-02-01T00:00:00Z' })],
  ['c', ov('c', { lastActivityAt: '2026-01-15T00:00:00Z' })],
]);
const ids = (r: Project[]) => r.map((x) => x.id);

describe('filterAndSortProjects', () => {
  it('sorts by recent activity using overview data, not project updatedAt', () => {
    expect(ids(filterAndSortProjects(projects, map, { query: '', filter: 'all', sort: 'activity' }))).toEqual(['a', 'b', 'c']);
  });
  it('filters running, rendered and empty projects', () => {
    expect(ids(filterAndSortProjects(projects, map, { query: '', filter: 'running', sort: 'name' }))).toEqual(['b']);
    expect(ids(filterAndSortProjects(projects, map, { query: '', filter: 'renders', sort: 'name' }))).toEqual(['a']);
    expect(ids(filterAndSortProjects(projects, map, { query: '', filter: 'empty', sort: 'name' }))).toEqual(['c']);
  });
  it('searches case-insensitively and sorts by name and file count', () => {
    expect(ids(filterAndSortProjects(projects, map, { query: 'GAM', filter: 'all', sort: 'name' }))).toEqual(['c']);
    expect(ids(filterAndSortProjects(projects, map, { query: '', filter: 'all', sort: 'files' }))).toEqual(['a', 'b', 'c']);
  });
  describe('folders and tags', () => {
    const tag = (id: string) => ({ id, name: id, color: 'gray' });
    const filed = [
      { ...projects[0], folderId: 'f1', tags: [tag('t1'), tag('t2')] },
      { ...projects[1], folderId: 'f2', tags: [tag('t1')] },
      { ...projects[2], folderId: null, tags: [] },
    ];
    const run = (extra: object) => ids(filterAndSortProjects(filed, map, { query: '', filter: 'all', sort: 'name', ...extra }));
    it('is unchanged when folder and tags are unset', () => {
      expect(run({})).toEqual(run({ folder: 'all', tagIds: [] }));
      expect(run({})).toEqual(['a', 'b', 'c']);
    });
    it('selects a folder or unfiled projects', () => {
      expect(run({ folder: 'f2' })).toEqual(['b']);
      expect(run({ folder: 'unfiled' })).toEqual(['c']);
    });
    it('requires every selected tag (AND) and combines with folder', () => {
      expect(run({ tagIds: ['t1'] })).toEqual(['a', 'b']);
      expect(run({ tagIds: ['t1', 't2'] })).toEqual(['a']);
      expect(run({ tagIds: ['t1'], folder: 'f2' })).toEqual(['b']);
      expect(run({ tagIds: ['zz'] })).toEqual([]);
    });
  });
});
