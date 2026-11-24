'use client';

import { useCallback } from 'react';
import useSWR, { useSWRConfig } from 'swr';
import {
  getMarketingLinks,
  getMarketingLinksFunnel,
  MARKETING_ADMIN_LINKS_URL,
  marketingLinksFunnelUrl,
} from '../api/marketing-links';
import type { MarketingLinkFunnel, MarketingLinkList } from '../types/marketing-links';

/** Platform admin: every tracked link, newest first. `enabled=false` makes no request. */
export function useMarketingLinks(enabled = true) {
  return useSWR<MarketingLinkList>(enabled ? MARKETING_ADMIN_LINKS_URL : null, getMarketingLinks, {
    revalidateOnFocus: false,
    shouldRetryOnError: false,
  });
}

/**
 * Platform admin: each link's funnel for the last `days` days. Deliberately without
 * `keepPreviousData`: while another period loads there are no numbers, rather than the numbers of
 * the period that was on screen before under the new period's label.
 */
export function useMarketingLinksFunnel(days: number, enabled = true) {
  return useSWR<MarketingLinkFunnel>(enabled ? marketingLinksFunnelUrl(days) : null, getMarketingLinksFunnel, {
    revalidateOnFocus: false,
    shouldRetryOnError: false,
  });
}

/**
 * After a create, an edit or an archive: reload the list and every funnel window already read. It
 * uses the mutate of the SWR cache the hooks above read from, so it reaches whichever cache is mounted.
 */
export function useRevalidateMarketingLinks(): () => Promise<void> {
  const { mutate } = useSWRConfig();
  return useCallback(async () => {
    await mutate((key) => typeof key === 'string' && key.startsWith(MARKETING_ADMIN_LINKS_URL));
  }, [mutate]);
}
