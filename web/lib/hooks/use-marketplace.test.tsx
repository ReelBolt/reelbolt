// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { SWRConfig } from 'swr';
import type { ReactNode } from 'react';
import { DEFAULT_FILTERS } from '@/components/marketplace/marketplace-model';
import type { MarketplaceRecipeCard } from '../types/marketplace';

const fetcherMock = vi.hoisted(() => vi.fn());
vi.mock('../api/client', () => ({ fetcher: fetcherMock, apiFetch: vi.fn() }));
const api = vi.hoisted(() => ({ rate: vi.fn(), report: vi.fn(), copy: vi.fn() }));
vi.mock('../api/marketplace', async (orig) => {
  const actual = await orig<typeof import('../api/marketplace')>();
  return { ...actual, rateMarketplaceRecipe: api.rate, reportMarketplaceRecipe: api.report, copyMarketplaceRecipe: api.copy };
});

import { useMarketplaceActions, useMarketplaceMe, useMarketplaceRecipes } from './use-marketplace';

const wrapper = ({ children }: { children: ReactNode }) => (
  <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>{children}</SWRConfig>
);

const card = (id: string): MarketplaceRecipeCard => ({
  id, name: id, description: '', category: 'promo', language: 'en', costClass: 'No credits', stepCount: 1, stepTypes: [],
  authorLabel: 'A', copyCount: 0, ratingAverage: null, ratingCount: 0, previewSeconds: 5, listedAt: '2026-11-01T00:00:00Z', version: 1,
});

beforeEach(() => {
  fetcherMock.mockReset();
  api.rate.mockReset();
  api.report.mockReset();
});

describe('useMarketplaceRecipes', () => {
  it('loads more by nextCursor and stops when there is none', async () => {
    fetcherMock.mockImplementation(async (url: string) =>
      url.includes('cursor=c1') ? { items: [card('b')] } : { items: [card('a')], nextCursor: 'c1' },
    );
    const { result } = renderHook(() => useMarketplaceRecipes(DEFAULT_FILTERS), { wrapper });
    await waitFor(() => expect(result.current.items.map((i) => i.id)).toEqual(['a']));
    expect(result.current.hasMore).toBe(true);
    act(() => result.current.loadMore());
    await waitFor(() => expect(result.current.items.map((i) => i.id)).toEqual(['a', 'b']));
    expect(result.current.hasMore).toBe(false);
    expect(fetcherMock.mock.calls.map((c) => c[0])).toEqual([
      '/api/v1/public/marketplace/recipes?limit=24',
      '/api/v1/public/marketplace/recipes?cursor=c1&limit=24',
    ]);
  });

  it('starts again from the first page when a filter changes', async () => {
    fetcherMock.mockResolvedValue({ items: [card('a')] });
    const { result, rerender } = renderHook(({ q }) => useMarketplaceRecipes({ ...DEFAULT_FILTERS, q }), {
      wrapper,
      initialProps: { q: '' },
    });
    await waitFor(() => expect(result.current.loaded).toBe(true));
    rerender({ q: 'reel' });
    await waitFor(() => expect(fetcherMock).toHaveBeenCalledWith('/api/v1/public/marketplace/recipes?q=reel&limit=24'));
  });
});

describe('useMarketplaceMe', () => {
  it('asks nothing for a visitor and reads the signed-in route for an account', async () => {
    renderHook(() => useMarketplaceMe('r1', false), { wrapper });
    expect(fetcherMock).not.toHaveBeenCalled();
    fetcherMock.mockResolvedValue({ copied: true, canRate: true, myStars: null, reported: false });
    const { result } = renderHook(() => useMarketplaceMe('r1', true), { wrapper });
    await waitFor(() => expect(result.current.data?.canRate).toBe(true));
    expect(fetcherMock).toHaveBeenCalledWith('/api/v1/marketplace/recipes/r1/me');
  });
});

describe('useMarketplaceActions', () => {
  it('rates through the signed-in route helper', async () => {
    api.rate.mockResolvedValue(undefined);
    fetcherMock.mockResolvedValue({});
    const { result } = renderHook(() => useMarketplaceActions('r1'), { wrapper });
    await act(async () => {
      await result.current.rate(4);
    });
    expect(api.rate).toHaveBeenCalledWith('r1', 4);
  });
});
