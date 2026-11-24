// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { SWRConfig } from 'swr';
import type { ReactNode } from 'react';

const api = vi.hoisted(() => ({
  createProjectTag: vi.fn(),
  updateProjectTag: vi.fn(),
  deleteProjectTag: vi.fn(),
  organizeProjects: vi.fn(),
  setProjectOrganization: vi.fn(),
  createProjectFolder: vi.fn(),
  updateProjectFolder: vi.fn(),
  deleteProjectFolder: vi.fn(),
  getTagActivityUrl: (d: number) => `/api/v1/project-tags/activity?days=${d}`,
  getTagDetailActivityUrl: (id: string, d: number) => `/api/v1/project-tags/${id}/activity?days=${d}`,
}));
vi.mock('../api/project-organization', () => api);
const fetcherMock = vi.hoisted(() => vi.fn());
vi.mock('../api/client', () => ({ fetcher: fetcherMock }));

import { useProjectTagMutations, useProjectTags } from './use-project-tags';
import { useProjectFolderMutations } from './use-project-folders';
import { useProjects } from './use-projects';
import { useTagActivity } from './use-tag-activity';

const wrapper = ({ children }: { children: ReactNode }) => (
  <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>{children}</SWRConfig>
);

beforeEach(() => {
  vi.clearAllMocks();
  fetcherMock.mockImplementation(async (url: string) => (url.includes('activity') ? { tags: [] } : []));
});

describe('project organization hooks', () => {
  it('a tag mutation revalidates the projects list and the tag list', async () => {
    api.organizeProjects.mockResolvedValue(undefined);
    const { result } = renderHook(() => ({ tags: useProjectTags(), projects: useProjects(), m: useProjectTagMutations() }), { wrapper });
    await waitFor(() => expect(fetcherMock).toHaveBeenCalledWith('/api/v1/projects'));
    const before = fetcherMock.mock.calls.filter((c) => c[0] === '/api/v1/projects').length;
    await act(async () => {
      await result.current.m.organize({ projectIds: ['p'], addTagIds: ['t'], removeTagIds: [] });
    });
    await waitFor(() => expect(fetcherMock.mock.calls.filter((c) => c[0] === '/api/v1/projects').length).toBeGreaterThan(before));
    expect(fetcherMock.mock.calls.filter((c) => c[0] === '/api/v1/project-tags').length).toBeGreaterThan(1);
  });
  it('a folder mutation revalidates the projects list', async () => {
    api.deleteProjectFolder.mockResolvedValue(undefined);
    const { result } = renderHook(() => ({ projects: useProjects(), m: useProjectFolderMutations() }), { wrapper });
    await waitFor(() => expect(fetcherMock).toHaveBeenCalledWith('/api/v1/projects'));
    const before = fetcherMock.mock.calls.length;
    await act(async () => {
      await result.current.m.deleteFolder('f1');
    });
    await waitFor(() => expect(fetcherMock.mock.calls.length).toBeGreaterThan(before));
  });
  it('does not revalidate when the call fails', async () => {
    api.createProjectTag.mockRejectedValue(new Error('boom'));
    const { result } = renderHook(() => useProjectTagMutations(), { wrapper });
    await expect(result.current.createTag({ name: 'x', color: 'gray' })).rejects.toThrow('boom');
    expect(fetcherMock).not.toHaveBeenCalled();
  });
  it('loads activity for a window', async () => {
    const { result } = renderHook(() => useTagActivity(30), { wrapper });
    await waitFor(() => expect(result.current.data).toBeDefined());
    expect(fetcherMock).toHaveBeenCalledWith('/api/v1/project-tags/activity?days=30');
  });
});
