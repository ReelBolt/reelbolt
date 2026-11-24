import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { SWRConfig } from 'swr';
import { AUTH_ME_URL, notifyAuthChange, useAuth } from './use-auth';

// platform-admin-access-incident: an edited reelbolt_user cookie must not turn on admin UI.
const wrapper = ({ children }: { children: ReactNode }) => (
  <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>{children}</SWRConfig>
);

function setCookie(user: Record<string, unknown>) {
  document.cookie = `reelbolt_user=${encodeURIComponent(JSON.stringify(user))}; path=/`;
  notifyAuthChange();
}

function meAnswers(isPlatformAdmin: boolean) {
  const fetchMock = vi.fn(async () => new Response(JSON.stringify({ user: { isPlatformAdmin } }), { status: 200 }));
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

beforeEach(() => setCookie({ email: 'a@b.co', isPlatformAdmin: true, isAdmin: true }));
afterEach(() => {
  vi.unstubAllGlobals();
  document.cookie = 'reelbolt_user=; Max-Age=0; path=/';
  notifyAuthChange();
});

describe('useAuth platform-admin bit', () => {
  it('ignores an edited cookie: the verified answer says non-admin', async () => {
    const fetchMock = meAnswers(false);
    const { result } = renderHook(() => useAuth(), { wrapper });
    expect(result.current.isPlatformAdmin).toBe(false);
    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith(AUTH_ME_URL, expect.anything()));
    expect(result.current.isPlatformAdmin).toBe(false);
    expect(result.current.user?.email).toBe('a@b.co');
  });

  it('turns admin UI on only once /api/auth/me confirms it', async () => {
    meAnswers(true);
    const { result } = renderHook(() => useAuth(), { wrapper });
    expect(result.current.isPlatformAdmin).toBe(false);
    await waitFor(() => expect(result.current.isPlatformAdmin).toBe(true));
  });

  it('stays non-admin when the verified answer fails', async () => {
    const fetchMock = vi.fn(async () => new Response('{}', { status: 401 }));
    vi.stubGlobal('fetch', fetchMock);
    const { result } = renderHook(() => useAuth(), { wrapper });
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    expect(result.current.isPlatformAdmin).toBe(false);
  });
});
