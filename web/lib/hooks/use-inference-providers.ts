'use client';

import { useMemo } from 'react';
import useSWR from 'swr';
import { fetcher } from '../api/client';
import {
  CONSOLIDATION_REPORT_PATH,
  getSelectableProvidersUrl,
  providersUrl,
  providerUrl,
  type ProviderScope,
} from '../api/inference-providers';
import {
  flattenProviders,
  type ConsolidationReport,
  type InferenceProvider,
  type InferenceProviderCapability,
  type ModelCapabilityRow,
  type SelectableProvider,
} from '../types/inference-provider';

/**
 * The provider TREE of a scope: accounts with their models and capabilities (and, on the platform
 * scope only, each capability's prices).
 *
 * `platform` is platform-admin only and `org` is the workspace's Owner/Admin only — both answer 403
 * to anyone else, so callers pass `enabled` rather than firing a request known to be refused. A null
 * SWR key is the documented way to pause a subscription.
 */
export function useProviders(scope: ProviderScope = 'platform', enabled: boolean = true) {
  return useSWR<InferenceProvider[]>(enabled ? providersUrl(scope) : null, fetcher);
}

/** One provider account with its models, capabilities and (platform only) prices. */
export function useProvider(id: string, scope: ProviderScope = 'platform', enabled: boolean = true) {
  return useSWR<InferenceProvider>(id && enabled ? providerUrl(id, scope) : null, fetcher);
}

/**
 * The PLATFORM model capabilities, flattened with their model and provider — for the screens that
 * only ask "which row serves capability X, and is it the default" (the admin dashboard, the setup
 * checklist). Platform admins only; step editors and pickers want {@link useSelectableProviders}.
 *
 * It shares the platform tree's SWR entry, so it costs no second request.
 */
export function useInferenceProviders(enabled: boolean = true) {
  const { data, error, isLoading, isValidating, mutate } = useProviders('platform', enabled);
  const rows = useMemo<ModelCapabilityRow[] | undefined>(
    () => (data ? flattenProviders(data) : undefined),
    [data],
  );
  return { data: rows, error, isLoading, isValidating, mutate };
}

/**
 * The model capabilities a picker may offer: every enabled platform one plus the workspace's own,
 * while its plan allows bring-your-own. Every member except Billing may read this, and the rows
 * carry nothing secret — see {@link SelectableProvider}.
 *
 * `enabled` is how a caller that already knows the role cannot read this (Billing) avoids a request
 * that would only 403. `capability` is part of the URL, so each capability gets its own cache entry.
 */
export function useSelectableProviders(capability?: InferenceProviderCapability | null, enabled: boolean = true) {
  return useSWR<SelectableProvider[]>(enabled ? getSelectableProvidersUrl(capability) : null, fetcher);
}

/** Which providers the catalog upgrade merged. Platform admins only. */
export function useConsolidationReport(enabled: boolean = true) {
  return useSWR<ConsolidationReport>(enabled ? CONSOLIDATION_REPORT_PATH : null, fetcher);
}
