'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import { adminListUrl, consentUrl, mineUrl, publishPreview } from '../api/marketplace';
import type {
  AdminRecipe,
  AdminReport,
  AdminTab,
  MarketplaceConsent,
  MineRecipe,
  PublishPreview,
} from '../types/marketplace';

/** The consent text is read again every time the dialog opens, so a new version is never skipped. */
export function useMarketplaceConsent(opened: boolean) {
  return useSWR<MarketplaceConsent>(opened ? consentUrl() : null, fetcher, {
    revalidateOnFocus: false,
    dedupingInterval: 0,
    revalidateOnMount: true,
  });
}

/** What publishing this workflow would remove and refuse. Writes nothing. */
export function usePublishPreview(projectId: string, workflowId: string, opened: boolean) {
  return useSWR<PublishPreview>(
    opened && projectId && workflowId ? ['marketplace-publish-preview', projectId, workflowId] : null,
    () => publishPreview(projectId, workflowId),
    { revalidateOnFocus: false, shouldRetryOnError: false },
  );
}

const itemsOf = <T,>(data: T[] | { items: T[] } | undefined): T[] | undefined =>
  data === undefined ? undefined : Array.isArray(data) ? data : data.items ?? [];

/** The caller organization's recipes. Accepts a bare array or `{items}`. */
export function useMyRecipes() {
  const swr = useSWR<MineRecipe[] | { items: MineRecipe[] }>(mineUrl(), fetcher);
  return { ...swr, items: itemsOf(swr.data) };
}

export function useAdminRecipes(tab: Exclude<AdminTab, 'reports'>, enabled: boolean) {
  const swr = useSWR<AdminRecipe[] | { items: AdminRecipe[] }>(enabled ? adminListUrl(tab) : null, fetcher);
  return { ...swr, items: itemsOf(swr.data), key: adminListUrl(tab) };
}

export function useAdminReports(enabled: boolean) {
  const swr = useSWR<AdminReport[] | { items: AdminReport[] }>(enabled ? adminListUrl('reports') : null, fetcher);
  return { ...swr, items: itemsOf(swr.data), key: adminListUrl('reports') };
}
