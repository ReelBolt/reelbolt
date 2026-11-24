'use client';

import useSWR from 'swr';
import { listApiKeys } from '../api/api-keys';
import type { ApiKeyListResult } from '../types/api-key';

/**
 * The active workspace's API keys. The endpoint needs Owner or Admin — a Member or Billing caller
 * gets 403 — so the page passes `null` for a role that may not read it and no request is made.
 */
export function useApiKeys(orgId: string | null | undefined) {
  return useSWR<ApiKeyListResult>(orgId ? '/api/v1/org/api-keys' : null, () => listApiKeys());
}
