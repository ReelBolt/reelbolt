'use client';

import useSWR from 'swr';
import { listWebhooks } from '../api/webhooks';
import type { WebhookEndpoint } from '../types/webhook';

/** The workspace's webhook endpoints (Owner or Admin only; pass `null` to skip the request). */
export function useWebhooks(orgId: string | null | undefined) {
  return useSWR<WebhookEndpoint[]>(orgId ? '/api/v1/org/webhooks' : null, () => listWebhooks());
}
