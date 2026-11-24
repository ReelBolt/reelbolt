import { afterEach, describe, expect, it, vi } from 'vitest';
import { goApiBaseUrl, offeredOAuth, resetOfferedOAuthCache } from './oauth-server';

const ok = (body: unknown) => vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve(body) });

afterEach(() => resetOfferedOAuthCache());

describe('offeredOAuth', () => {
  it('offers exactly the providers the API has credentials for', async () => {
    const fetcher = ok({ providers: ['google', 'github'], createsAccount: false });
    expect(await offeredOAuth({ fetcher, env: {} })).toEqual({ providers: ['google', 'github'], createsAccount: false });
    expect(fetcher).toHaveBeenCalledWith('http://go-api:8080/api/v1/auth/oauth/providers', expect.anything());
  });

  it('ignores a provider this page has no button for', async () => {
    const fetcher = ok({ providers: ['google', 'myspace'], createsAccount: true });
    expect(await offeredOAuth({ fetcher, env: {} })).toEqual({ providers: ['google'], createsAccount: true });
  });

  it('answers null when the API cannot be asked, so the page falls back to its flags', async () => {
    expect(await offeredOAuth({ fetcher: vi.fn().mockRejectedValue(new Error('down')), env: {} })).toBeNull();
    resetOfferedOAuthCache();
    expect(await offeredOAuth({ fetcher: vi.fn().mockResolvedValue({ ok: false }), env: {} })).toBeNull();
  });

  it('reuses an answer for a while instead of asking on every page view', async () => {
    const fetcher = ok({ providers: ['github'], createsAccount: true });
    let t = 1_000;
    await offeredOAuth({ fetcher, env: {}, now: () => t });
    t += 10_000;
    await offeredOAuth({ fetcher, env: {}, now: () => t });
    expect(fetcher).toHaveBeenCalledTimes(1);
  });

  it('reads the API address from GO_API_URL', () => {
    expect(goApiBaseUrl({ GO_API_URL: 'http://api.internal:9000/' })).toBe('http://api.internal:9000');
  });
});
