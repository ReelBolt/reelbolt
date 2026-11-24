import { apiFetch } from './client';
import type {
  ApiKeyAuditPage,
  ApiKeyCreated,
  ApiKeyItem,
  ApiKeyListResult,
  CreateApiKeyRequest,
  UpdateApiKeyRequest,
} from '../types/api-key';

/**
 * The workspace API keys client. Every route is under the active workspace's session
 * (`/api/v1/org/api-keys`, singular `org` — the contract the Inference API serves), and the server
 * answers 404 when `PUBLIC_API_ENABLED` is off, 402 `PLAN_NOT_ENTITLED` on Trial and 409
 * `api_key_limit` when the workspace is full.
 */
const BASE = '/api/v1/org/api-keys';

/** Both the legacy bare array and the `{items, capsEnforced}` envelope normalise to one shape. */
interface RawListEnvelope {
  items?: ApiKeyItem[];
  capsEnforced?: boolean;
  defaultDailyCapCredits?: number;
}

function normaliseList(raw: ApiKeyItem[] | RawListEnvelope): ApiKeyListResult {
  if (Array.isArray(raw)) return { items: raw, capsEnforced: null, defaultDailyCapCredits: null };
  return {
    items: raw.items ?? [],
    capsEnforced: typeof raw.capsEnforced === 'boolean' ? raw.capsEnforced : null,
    defaultDailyCapCredits: typeof raw.defaultDailyCapCredits === 'number' ? raw.defaultDailyCapCredits : null,
  };
}

/** `GET /api/v1/org/api-keys`: the workspace's keys, newest first, and whether caps are enforced. */
export async function listApiKeys(): Promise<ApiKeyListResult> {
  return normaliseList(await apiFetch<ApiKeyItem[] | RawListEnvelope>(BASE));
}

/** `POST /api/v1/org/api-keys`: 201 `{key, secret}`. The secret is returned by this call only. */
export function createApiKey(request: CreateApiKeyRequest): Promise<ApiKeyCreated> {
  return apiFetch<ApiKeyCreated>(BASE, { method: 'POST', body: JSON.stringify(request) });
}

/** `PATCH /api/v1/org/api-keys/{id}`: name and caps only. */
export function updateApiKey(id: string, request: UpdateApiKeyRequest): Promise<ApiKeyItem> {
  return apiFetch<ApiKeyItem>(`${BASE}/${encodeURIComponent(id)}`, {
    method: 'PATCH',
    body: JSON.stringify(request),
  });
}

/** `DELETE /api/v1/org/api-keys/{id}`: revokes immediately (204). */
export async function revokeApiKey(id: string): Promise<void> {
  await apiFetch<void>(`${BASE}/${encodeURIComponent(id)}`, { method: 'DELETE' });
}

/** `GET /api/v1/org/api-keys/{id}/audit`: one cursor page of the key's requests. */
export function getApiKeyAudit(
  id: string,
  options: { limit?: number; cursor?: string | null } = {},
): Promise<ApiKeyAuditPage> {
  const params = new URLSearchParams();
  if (options.limit) params.set('limit', String(options.limit));
  if (options.cursor) params.set('cursor', options.cursor);
  const query = params.toString();
  return apiFetch<ApiKeyAuditPage>(`${BASE}/${encodeURIComponent(id)}/audit${query ? `?${query}` : ''}`);
}
