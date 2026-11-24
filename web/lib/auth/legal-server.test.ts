// @vitest-environment node

import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  SERVED_VERSIONS_TTL_MS,
  legalApiBaseUrl,
  resetServedVersionsCache,
  servedLegalVersions,
} from './legal-server';

// F-7. What the deployment serves is a fact the server owns, so this module may only ever REPORT
// it: every failure path returns null, and no path invents a version.

afterEach(() => {
  resetServedVersionsCache();
  vi.restoreAllMocks();
});

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

describe('legalApiBaseUrl', () => {
  it('uses INFERENCE_URL, the same target the Go API uses', () => {
    expect(legalApiBaseUrl({ INFERENCE_URL: 'http://inference:8080' })).toBe('http://inference:8080');
  });

  it('falls back to the compose service name and strips trailing slashes', () => {
    expect(legalApiBaseUrl({})).toBe('http://inference:8080');
    expect(legalApiBaseUrl({ INFERENCE_URL: 'http://inference:8080///' })).toBe('http://inference:8080');
    expect(legalApiBaseUrl({ INFERENCE_URL: '  http://fake:9999  ' })).toBe('http://fake:9999');
  });
});

describe('servedLegalVersions', () => {
  it('reads the versions from the service that owns legal_documents', async () => {
    const fetcher = vi.fn().mockResolvedValue(
      jsonResponse({ termsVersion: '2026-11', privacyVersion: '2026-12', documents: [] }),
    );

    await expect(servedLegalVersions({ fetcher, env: { INFERENCE_URL: 'http://inference:8080' } })).resolves
      .toEqual({ termsVersion: '2026-11', privacyVersion: '2026-12' });
    expect(fetcher.mock.calls[0][0]).toBe('http://inference:8080/api/v1/legal/documents');
  });

  it('returns null rather than a guess when the API refuses or does not answer', async () => {
    // The 503 the API answers when a document has no row, a dead API, a timeout and a non-JSON body
    // are all the same answer here: there is nothing to compare a session against.
    const refusing = vi.fn().mockResolvedValue(jsonResponse({ message: 'not configured' }, 503));
    expect(await servedLegalVersions({ fetcher: refusing })).toBeNull();

    resetServedVersionsCache();
    const broken = vi.fn().mockRejectedValue(new Error('connect ECONNREFUSED'));
    expect(await servedLegalVersions({ fetcher: broken })).toBeNull();

    resetServedVersionsCache();
    const html = vi.fn().mockResolvedValue(new Response('<html>', { status: 200 }));
    expect(await servedLegalVersions({ fetcher: html })).toBeNull();

    resetServedVersionsCache();
    const half = vi.fn().mockResolvedValue(jsonResponse({ termsVersion: '2026-11' }));
    expect(await servedLegalVersions({ fetcher: half })).toBeNull();
  });

  it('bounds the read so a hung API cannot stall a gated request', async () => {
    const fetcher = vi.fn().mockResolvedValue(jsonResponse({ termsVersion: 'a', privacyVersion: 'b' }));
    await servedLegalVersions({ fetcher });
    const init = fetcher.mock.calls[0][1] as RequestInit;
    expect(init.signal).toBeInstanceOf(AbortSignal);
    expect(init.cache).toBe('no-store');
  });

  it('reuses one answer for the TTL and re-reads after it', async () => {
    const fetcher = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse({ termsVersion: '2026-10', privacyVersion: '2026-10' }))
      .mockResolvedValueOnce(jsonResponse({ termsVersion: '2026-11', privacyVersion: '2026-11' }));

    let now = 1_000;
    const clock = () => now;

    expect(await servedLegalVersions({ fetcher, now: clock })).toEqual({
      termsVersion: '2026-10',
      privacyVersion: '2026-10',
    });
    // A publication is not seen until the TTL expires — and is seen then, without a restart.
    now += SERVED_VERSIONS_TTL_MS - 1;
    expect(await servedLegalVersions({ fetcher, now: clock })).toEqual({
      termsVersion: '2026-10',
      privacyVersion: '2026-10',
    });
    expect(fetcher).toHaveBeenCalledTimes(1);

    now += 1;
    expect(await servedLegalVersions({ fetcher, now: clock })).toEqual({
      termsVersion: '2026-11',
      privacyVersion: '2026-11',
    });
    expect(fetcher).toHaveBeenCalledTimes(2);
  });

  it('caches a failure too, so an API outage is not retried on every request', async () => {
    const fetcher = vi.fn().mockRejectedValue(new Error('down'));
    let now = 1_000;
    const clock = () => now;

    expect(await servedLegalVersions({ fetcher, now: clock })).toBeNull();
    now += 1;
    expect(await servedLegalVersions({ fetcher, now: clock })).toBeNull();
    expect(fetcher).toHaveBeenCalledTimes(1);
  });
});
