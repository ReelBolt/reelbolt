/**
 * The workspace API-keys client: list shape normalisation and the exact URL/method/body of every
 * route. `fetch` is stubbed, so these assert the contract the Inference API serves.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createApiKey, getApiKeyAudit, listApiKeys, revokeApiKey, updateApiKey } from './api-keys';
import { ApiError } from './client';
import type { ApiKeyItem } from '../types/api-key';

const KEY: ApiKeyItem = {
  id: 'k1',
  name: 'CI pipeline',
  keyPrefix: 'rbk_abc',
  lastFour: '9f2c',
  createdBy: { id: 'u1', name: 'Ada' },
  scopes: ['projects.read'],
  projectIds: null,
  projects: null,
  caps: { dailyCredits: null, weeklyCredits: null, monthlyCredits: null },
  expiresAt: null,
  neverExpires: true,
  createdAt: '2026-10-01T00:00:00Z',
  lastUsedAt: null,
  status: 'active',
  revokedAt: null,
};

function respond(body: unknown, status = 200): void {
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>(async () =>
      new Response(status === 204 ? null : JSON.stringify(body), {
        status,
        headers: { 'content-type': 'application/json' },
      }),
    ),
  );
}

afterEach(() => vi.unstubAllGlobals());

describe('listApiKeys', () => {
  it('accepts the legacy bare array', async () => {
    respond([KEY]);
    await expect(listApiKeys()).resolves.toEqual({ items: [KEY], capsEnforced: null, defaultDailyCapCredits: null });
    expect(vi.mocked(fetch).mock.calls[0][0]).toBe('/api/v1/org/api-keys');
  });

  it('accepts the {items, capsEnforced} envelope', async () => {
    respond({ items: [KEY], capsEnforced: false, defaultDailyCapCredits: null });
    await expect(listApiKeys()).resolves.toEqual({ items: [KEY], capsEnforced: false, defaultDailyCapCredits: null });
  });
});

describe('mutations', () => {
  it('POSTs a create request and returns the secret once', async () => {
    respond({ key: KEY, secret: 'rbk_abc_secret' }, 201);
    await expect(
      createApiKey({ name: 'CI pipeline', scopes: ['projects.read'], caps: { dailyCredits: 100 } }),
    ).resolves.toEqual({ key: KEY, secret: 'rbk_abc_secret' });
    const [, init] = vi.mocked(fetch).mock.calls[0];
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({
      name: 'CI pipeline',
      scopes: ['projects.read'],
      caps: { dailyCredits: 100 },
    });
  });

  it('PATCHes name and caps on one key', async () => {
    respond(KEY);
    await updateApiKey('k/1', { name: 'Renamed', caps: { weeklyCredits: 50 } });
    expect(vi.mocked(fetch).mock.calls[0][0]).toBe('/api/v1/org/api-keys/k%2F1');
    expect(vi.mocked(fetch).mock.calls[0][1]?.method).toBe('PATCH');
  });

  it('DELETEs a key and accepts the 204', async () => {
    respond(null, 204);
    await expect(revokeApiKey('k1')).resolves.toBeUndefined();
    expect(vi.mocked(fetch).mock.calls[0][1]?.method).toBe('DELETE');
  });
});

describe('getApiKeyAudit', () => {
  it('adds limit and cursor to the audit URL', async () => {
    respond({ data: [], nextCursor: 'c2' });
    await expect(getApiKeyAudit('k1', { limit: 25, cursor: 'c1' })).resolves.toEqual({ data: [], nextCursor: 'c2' });
    expect(vi.mocked(fetch).mock.calls[0][0]).toBe('/api/v1/org/api-keys/k1/audit?limit=25&cursor=c1');
  });

  it('surfaces the plan gate as an ApiError with the server code', async () => {
    respond({ code: 'PLAN_NOT_ENTITLED', feature: 'api', message: 'The API is available on paid plans.' }, 402);
    await expect(createApiKey({ name: 'x', scopes: ['projects.read'] })).rejects.toMatchObject({
      status: 402,
      code: 'PLAN_NOT_ENTITLED',
    } satisfies Partial<ApiError>);
  });
});
