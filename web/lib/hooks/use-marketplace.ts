'use client';

import { useCallback, useMemo } from 'react';
import useSWR, { useSWRConfig } from 'swr';
import useSWRInfinite from 'swr/infinite';
import { fetcher } from '../api/client';
import {
  copyMarketplaceRecipe,
  facetsUrl,
  meUrl,
  rateMarketplaceRecipe,
  recipesUrl,
  recipeUrl,
  reportMarketplaceRecipe,
} from '../api/marketplace';
import type {
  MarketplaceFacets,
  MarketplaceFilters,
  MarketplaceMe,
  MarketplaceRecipeCard,
  MarketplaceRecipeDetail,
  MarketplaceRecipePage,
  MarketplaceReportReason,
} from '../types/marketplace';

/** The recipe list with load-more by `nextCursor`. A new filter set starts again from the first page. */
export function useMarketplaceRecipes(filters: MarketplaceFilters) {
  const { q, category, cost, stepType, language, sort } = filters;
  const getKey = useCallback(
    (index: number, previous: MarketplaceRecipePage | null): string | null => {
      if (index === 0) return recipesUrl({ q, category, cost, stepType, language, sort });
      if (!previous?.nextCursor) return null;
      return recipesUrl({ q, category, cost, stepType, language, sort }, previous.nextCursor);
    },
    [q, category, cost, stepType, language, sort],
  );
  const { data, error, isLoading, isValidating, size, setSize, mutate } = useSWRInfinite<MarketplaceRecipePage>(
    getKey,
    (url: string) => fetcher<MarketplaceRecipePage>(url),
    { revalidateFirstPage: false },
  );
  const items = useMemo<MarketplaceRecipeCard[]>(() => (data ?? []).flatMap((p) => p.items), [data]);
  const last = data?.[data.length - 1];
  const hasMore = Boolean(last?.nextCursor);
  const isLoadingMore = size > 1 && isValidating && data?.length !== size;
  const loadMore = useCallback(() => {
    if (hasMore && !isValidating) void setSize((s) => s + 1);
  }, [hasMore, isValidating, setSize]);
  return { items, hasMore, loadMore, isLoading, isLoadingMore, error: error as unknown, loaded: data !== undefined, mutate };
}

export function useMarketplaceRecipe(id: string | undefined) {
  return useSWR<MarketplaceRecipeDetail>(id ? recipeUrl(id) : null, fetcher);
}

export function useMarketplaceFacets() {
  return useSWR<MarketplaceFacets>(facetsUrl(), fetcher);
}

/** What the signed-in account may do with a recipe. Pass `signedIn` false for visitors: nothing is requested. */
export function useMarketplaceMe(id: string | undefined, signedIn: boolean) {
  return useSWR<MarketplaceMe>(id && signedIn ? meUrl(id) : null, fetcher, { shouldRetryOnError: false });
}

/** Copy, rate and report for one recipe. Rate and report refresh what the page shows afterwards. */
export function useMarketplaceActions(id: string) {
  const { mutate } = useSWRConfig();
  const copy = useCallback((projectId: string, name?: string) => copyMarketplaceRecipe(projectId, id, name), [id]);
  const rate = useCallback(
    async (stars: number) => {
      await rateMarketplaceRecipe(id, stars);
      await Promise.all([mutate(meUrl(id)), mutate(recipeUrl(id))]);
    },
    [id, mutate],
  );
  const report = useCallback(
    async (reason: MarketplaceReportReason, note?: string) => {
      await reportMarketplaceRecipe(id, reason, note);
      await mutate(meUrl(id));
    },
    [id, mutate],
  );
  return { copy, rate, report };
}
