'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import type { SocialAccount, SocialMetricsSummary, SocialPostList, SocialProvider } from '../types/social';

/** Only the platforms this deployment can connect. An empty list means publishing is not available yet. */
export function useSocialProviders() {
  return useSWR<SocialProvider[]>('/api/v1/social/providers', fetcher, { shouldRetryOnError: false });
}

export function useSocialAccounts(enabled = true) {
  return useSWR<SocialAccount[]>(enabled ? '/api/v1/social/accounts' : null, fetcher, { shouldRetryOnError: false });
}

export function useSocialPosts(status?: string) {
  const query = new URLSearchParams({ take: '100' });
  if (status) query.set('status', status);
  return useSWR<SocialPostList>(`/api/v1/social/posts?${query}`, fetcher, { shouldRetryOnError: false, refreshInterval: 30_000 });
}

export function useSocialMetricsSummary(days = 30) {
  return useSWR<SocialMetricsSummary>(`/api/v1/social/metrics/summary?days=${days}`, fetcher, { shouldRetryOnError: false });
}
