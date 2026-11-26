import { beforeEach, describe, expect, it, vi } from 'vitest';
import { firstVideoGrantOffered, resetFirstVideoCache } from './first-video-server';

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status });

describe('firstVideoGrantOffered', () => {
  beforeEach(resetFirstVideoCache);

  it('is true only for a 200 with enabled true', async () => {
    const fetcher = vi.fn(async (_url: string, _init?: RequestInit) => json(200, { enabled: true }));
    expect(await firstVideoGrantOffered({ fetcher, env: { INFERENCE_URL: 'http://inf:1/' } })).toBe(true);
    expect(fetcher.mock.calls[0][0]).toBe('http://inf:1/api/v1/billing/promotions/first-video');
  });

  it('is false for a 200 with enabled false', async () => {
    expect(await firstVideoGrantOffered({ fetcher: async () => json(200, { enabled: false }) })).toBe(false);
  });

  it('is false for a 404 (billing off), even with an enabled body', async () => {
    expect(await firstVideoGrantOffered({ fetcher: async () => json(404, { enabled: true }) })).toBe(false);
  });

  it('is false on a network error', async () => {
    expect(await firstVideoGrantOffered({ fetcher: async () => Promise.reject(new Error('down')) })).toBe(false);
  });

  it('is false on bad JSON and on a non-boolean enabled', async () => {
    expect(await firstVideoGrantOffered({ fetcher: async () => new Response('nope', { status: 200 }) })).toBe(false);
    resetFirstVideoCache();
    expect(await firstVideoGrantOffered({ fetcher: async () => json(200, { enabled: 'true' }) })).toBe(false);
  });

  it('is false when the API does not answer within the timeout', async () => {
    const fetcher = (_url: string, init?: RequestInit) =>
      new Promise<Response>((_resolve, reject) => {
        init?.signal?.addEventListener('abort', () => reject(new Error('aborted')));
      });
    expect(await firstVideoGrantOffered({ fetcher, timeoutMs: 20 })).toBe(false);
  });

  it('reuses an answer within the TTL and re-reads after it', async () => {
    let at = 1000;
    const fetcher = vi.fn(async () => json(200, { enabled: true }));
    const opts = { fetcher, now: () => at, ttlMs: 100 };
    await firstVideoGrantOffered(opts);
    at = 1050;
    await firstVideoGrantOffered(opts);
    expect(fetcher).toHaveBeenCalledTimes(1);
    at = 1200;
    await firstVideoGrantOffered(opts);
    expect(fetcher).toHaveBeenCalledTimes(2);
  });
});
