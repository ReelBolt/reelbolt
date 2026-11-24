// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { SWRConfig } from 'swr';
import type { ReactNode } from 'react';

const fetcherMock = vi.hoisted(() => vi.fn());
vi.mock('../api/client', () => ({ fetcher: fetcherMock }));

import {
  useProjectHistoryChanges,
  useProjectHistoryItemDiff,
  useProjectHistoryVersion,
  useProjectHistoryVersionPages,
  useProjectHistoryVersions,
} from './use-project-history';

let cache = new Map();
const wrapper = ({ children }: { children: ReactNode }) => (
  <SWRConfig value={{ provider: () => cache, dedupingInterval: 0 }}>{children}</SWRConfig>
);

beforeEach(() => {
  vi.clearAllMocks();
  cache = new Map();
  fetcherMock.mockResolvedValue({});
});

describe('project history hooks', () => {
  it('reads the version list from the history endpoint', async () => {
    renderHook(() => useProjectHistoryVersions('p1'), { wrapper });
    await waitFor(() => expect(fetcherMock).toHaveBeenCalledWith('/api/v1/projects/p1/history/versions'));
  });

  it('reads one version and encodes its id', async () => {
    renderHook(() => useProjectHistoryVersion('p1', 'v/1'), { wrapper });
    await waitFor(() => expect(fetcherMock).toHaveBeenCalledWith('/api/v1/projects/p1/history/versions/v%2F1'));
  });

  it('compares against the live project by default and against another version on request', async () => {
    const { rerender } = renderHook(({ to }) => useProjectHistoryChanges('p1', 'v1', to), {
      wrapper,
      initialProps: { to: 'current' },
    });
    await waitFor(() => expect(fetcherMock).toHaveBeenCalledWith(
      '/api/v1/projects/p1/history/versions/v1/changes?to=current',
    ));

    rerender({ to: 'v2' });
    await waitFor(() => expect(fetcherMock).toHaveBeenCalledWith(
      '/api/v1/projects/p1/history/versions/v1/changes?to=v2',
    ));
  });

  it('encodes the kind and key of an item diff', async () => {
    renderHook(() => useProjectHistoryItemDiff('p1', 'v1', 'File', 'f/1', 'current'), { wrapper });
    await waitFor(() => expect(fetcherMock).toHaveBeenCalledWith(
      '/api/v1/projects/p1/history/versions/v1/items/File/f%2F1/diff?to=current',
    ));
  });

  it('sends no request while the selected item is unknown', async () => {
    renderHook(() => useProjectHistoryItemDiff('p1', 'v1', null, null, 'current'), { wrapper });
    renderHook(() => useProjectHistoryVersion(undefined, 'v1'), { wrapper });
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(fetcherMock).not.toHaveBeenCalled();
  });

  it('pages the version list with limit 50 and before=<oldest loaded number>', async () => {
    fetcherMock.mockImplementation((url: string) => Promise.resolve(
      url.includes('before=100')
        ? { versions: [{ id: 'v2', number: 99 }], historyBytes: 0, retentionDays: 30, canEdit: true, canDelete: true }
        : { versions: [{ id: 'v1', number: 100 }], historyBytes: 0, retentionDays: 30, canEdit: true, canDelete: true },
    ));
    const { result } = renderHook(() => useProjectHistoryVersionPages('p1'), { wrapper });
    await waitFor(() => expect(fetcherMock).toHaveBeenCalledWith(
      '/api/v1/projects/p1/history/versions?limit=50',
    ));

    await act(async () => { await result.current.setSize(2); });
    await waitFor(() => expect(fetcherMock).toHaveBeenCalledWith(
      '/api/v1/projects/p1/history/versions?limit=50&before=100',
    ));
  });
});
