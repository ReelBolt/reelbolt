import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, apiFetch } from './client';

function respond(status: number, headers: Record<string, string>) {
  vi.stubGlobal('fetch', vi.fn(async () => new Response('{"message":"slow down"}', { status, headers })));
}

describe('apiFetch Retry-After', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('exposes a numeric Retry-After on the error', async () => {
    respond(429, { 'Retry-After': '3600' });
    const err = (await apiFetch('/x').catch((e: unknown) => e)) as ApiError;
    expect(err.retryAfterSeconds).toBe(3600);
  });

  it('leaves it undefined when absent or not a number', async () => {
    respond(429, {});
    expect(((await apiFetch('/x').catch((e: unknown) => e)) as ApiError).retryAfterSeconds).toBeUndefined();
    respond(429, { 'Retry-After': 'Wed, 21 Oct 2026 07:28:00 GMT' });
    expect(((await apiFetch('/x').catch((e: unknown) => e)) as ApiError).retryAfterSeconds).toBeUndefined();
  });
});
