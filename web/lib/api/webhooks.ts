import { apiFetch } from './client';
import type {
  CreateWebhookRequest,
  UpdateWebhookRequest,
  WebhookCreated,
  WebhookDeliveryPage,
  WebhookEndpoint,
} from '../types/webhook';

/** Workspace webhook endpoints. 404 means the public API is off; 409 is the 10-endpoint limit. */
const BASE = '/api/v1/org/webhooks';
const path = (id: string) => `${BASE}/${encodeURIComponent(id)}`;

export async function listWebhooks(): Promise<WebhookEndpoint[]> {
  const raw = await apiFetch<{ items?: WebhookEndpoint[] }>(BASE);
  return raw.items ?? [];
}

/** 201 with the endpoint including `secret`, returned by this call only. */
export function createWebhook(request: CreateWebhookRequest): Promise<WebhookCreated> {
  return apiFetch<WebhookCreated>(BASE, { method: 'POST', body: JSON.stringify(request) });
}

export function updateWebhook(id: string, request: UpdateWebhookRequest): Promise<WebhookEndpoint> {
  return apiFetch<WebhookEndpoint>(path(id), { method: 'PATCH', body: JSON.stringify(request) });
}

export async function deleteWebhook(id: string): Promise<void> {
  await apiFetch<void>(path(id), { method: 'DELETE' });
}

/** The old secret keeps signing for 24 hours; the new one is returned once. */
export function rotateWebhookSecret(id: string): Promise<{ secret: string }> {
  return apiFetch<{ secret: string }>(`${path(id)}/rotate-secret`, { method: 'POST' });
}

export async function sendWebhookTest(id: string): Promise<void> {
  await apiFetch<void>(`${path(id)}/test`, { method: 'POST' });
}

export function listWebhookDeliveries(
  id: string,
  options: { limit?: number; cursor?: string | null } = {},
): Promise<WebhookDeliveryPage> {
  const params = new URLSearchParams();
  if (options.limit) params.set('limit', String(options.limit));
  if (options.cursor) params.set('cursor', options.cursor);
  const query = params.toString();
  return apiFetch<WebhookDeliveryPage>(`${path(id)}/deliveries${query ? `?${query}` : ''}`);
}

export async function redeliverWebhook(id: string, deliveryId: string): Promise<void> {
  await apiFetch<void>(`${path(id)}/deliveries/${encodeURIComponent(deliveryId)}/redeliver`, { method: 'POST' });
}
