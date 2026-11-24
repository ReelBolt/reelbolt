// @vitest-environment jsdom
import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHook, waitFor, cleanup } from '@testing-library/react';
import { SWRConfig } from 'swr';
import { useSfxLibrary } from './use-sfx-library';

const fetchMock = vi.fn();
vi.mock('../api/client', () => ({ fetcher: (url: string) => fetchMock(url) }));

const wrapper = ({ children }: { children: React.ReactNode }) => (
  <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>{children}</SWRConfig>
);
afterEach(() => { cleanup(); fetchMock.mockReset(); });

describe('useSfxLibrary', () => {
  it('fetches the page for the query', async () => {
    fetchMock.mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 50, categories: [] });
    const { result } = renderHook(() => useSfxLibrary(true, { search: 'a', category: null, maxDurationMs: null, page: 1 }), { wrapper });
    await waitFor(() => expect(result.current.data?.total).toBe(0));
    expect(fetchMock).toHaveBeenCalledWith('/api/v1/sfx-library?query=a&page=1&pageSize=50');
  });
  it('does not fetch when disabled', () => {
    renderHook(() => useSfxLibrary(false, { search: '', category: null, maxDurationMs: null, page: 1 }), { wrapper });
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
