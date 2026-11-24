// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { MarketplaceFilters, MarketplaceRecipeCard } from '@/lib/types/marketplace';

const state = vi.hoisted(() => ({
  list: { items: [] as unknown[], hasMore: false, loadMore: vi.fn(), isLoading: false, isLoadingMore: false, error: undefined as unknown, loaded: true, mutate: vi.fn() },
  facets: undefined as unknown,
  calls: [] as MarketplaceFilters[],
}));
vi.mock('@/lib/hooks/use-marketplace', () => ({
  useMarketplaceRecipes: (f: MarketplaceFilters) => {
    state.calls.push(f);
    return state.list;
  },
  useMarketplaceFacets: () => ({ data: state.facets }),
}));

import { CommunityGallery } from './CommunityGallery';

const card = (id: string, name: string): MarketplaceRecipeCard => ({
  id, name, description: 'd', category: 'promo', language: 'en', costClass: 'No credits', stepCount: 2, stepTypes: [], authorLabel: 'Ada',
  copyCount: 0, ratingAverage: null, ratingCount: 0, previewSeconds: 5, listedAt: '2026-11-01T00:00:00Z', version: 1,
});

const last = () => state.calls[state.calls.length - 1];
const mount = () => render(<MantineProvider><CommunityGallery /></MantineProvider>);

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  state.calls = [];
  state.facets = { categories: [{ key: 'promo', count: 2 }, { key: 'footage', count: 1 }], stepTypes: [{ key: 'Narration', count: 2 }], languages: [{ key: 'it', count: 1 }] };
  state.list = { items: [card('a', 'Alpha reel'), card('b', 'Beta cut')], hasMore: false, loadMore: vi.fn(), isLoading: false, isLoadingMore: false, error: undefined, loaded: true, mutate: vi.fn() };
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe('CommunityGallery', () => {
  it('lists the recipes as cards', () => {
    mount();
    expect(screen.getByRole('link', { name: 'Alpha reel' })).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Beta cut' })).toBeTruthy();
    expect(last()).toEqual({ q: '', category: '', cost: '', stepType: '', language: '', sort: 'new' });
  });

  it('debounces the search text before it reaches the request', () => {
    vi.useFakeTimers();
    mount();
    fireEvent.change(screen.getByLabelText('Search recipes'), { target: { value: 'reel' } });
    expect(last().q).toBe('');
    act(() => {
      vi.advanceTimersByTime(400);
    });
    expect(last().q).toBe('reel');
  });

  it('maps each chip and the sort to the request, and clears a chip by pressing it again', () => {
    mount();
    fireEvent.click(screen.getByRole('button', { name: 'Footage' }));
    expect(last().category).toBe('footage');
    fireEvent.click(screen.getByRole('button', { name: 'Uses credits and a voice' }));
    expect(last().cost).toBe('Uses credits and a voice');
    fireEvent.click(screen.getByRole('button', { name: 'Narration' }));
    expect(last().stepType).toBe('Narration');
    fireEvent.click(screen.getByRole('button', { name: 'Italian' }));
    expect(last().language).toBe('it');
    fireEvent.change(screen.getByLabelText('Sort by'), { target: { value: 'rated' } });
    expect(last().sort).toBe('rated');
    fireEvent.click(screen.getByRole('button', { name: 'Footage' }));
    expect(last().category).toBe('');
    fireEvent.click(screen.getAllByRole('button', { name: 'Clear filters' })[0]);
    expect(last()).toEqual({ q: '', category: '', cost: '', stepType: '', language: '', sort: 'rated' });
  });

  it('shows loading, error, empty and filtered-empty states', () => {
    state.list = { ...state.list, items: [], isLoading: true, loaded: false };
    const { unmount } = mount();
    expect(screen.getByRole('status', { name: 'Loading recipes' })).toBeTruthy();
    unmount();
    state.list = { ...state.list, isLoading: false, loaded: false, error: new Error('x') };
    const second = mount();
    expect(screen.getByRole('alert').textContent).toContain('We could not load the recipes');
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(state.list.mutate).toHaveBeenCalled();
    second.unmount();
    state.list = { ...state.list, error: undefined, loaded: true };
    const third = mount();
    expect(screen.getByText('No recipes here yet')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Promo' }));
    expect(screen.getByText('No recipe matches')).toBeTruthy();
    third.unmount();
  });

  it('offers load more only while there is a next page', () => {
    state.list = { ...state.list, hasMore: true };
    mount();
    fireEvent.click(screen.getByRole('button', { name: 'Show more recipes' }));
    expect(state.list.loadMore).toHaveBeenCalled();
  });
});
