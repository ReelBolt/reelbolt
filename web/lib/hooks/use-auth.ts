'use client';

import { useCallback, useMemo, useSyncExternalStore } from 'react';
import useSWR from 'swr';
import type { UserInfo } from '../types/auth';
import { readUserCookie, writeUserCookie } from '../auth/user-cookie';
import { wipeEditorLocalData } from '../editing/sync/user-scope';

export function getUserFromCookie(): UserInfo | null {
  return readUserCookie();
}

let cachedUser: UserInfo | null = null;
let listeners: (() => void)[] = [];

function subscribe(listener: () => void) {
  listeners.push(listener);
  return () => {
    listeners = listeners.filter((l) => l !== listener);
  };
}

function getSnapshot(): UserInfo | null {
  const user = getUserFromCookie();
  if (JSON.stringify(user) !== JSON.stringify(cachedUser)) {
    cachedUser = user;
  }
  return cachedUser;
}

function getServerSnapshot(): UserInfo | null {
  return null;
}

export function setUserCookie(user: UserInfo) {
  writeUserCookie(user);
}

export function notifyAuthChange() {
  cachedUser = getUserFromCookie();
  listeners.forEach((l) => l());
}

/**
 * The web app's own route (served under the '/app' basePath; a bare '/api/...' goes to the Go API).
 * It verifies the httpOnly session token, so its answer cannot be forged from the browser.
 */
export const AUTH_ME_URL = '/app/api/auth/me';

async function fetchVerifiedPlatformAdmin([url]: readonly [string, string]): Promise<boolean> {
  const response = await fetch(url, { credentials: 'same-origin', cache: 'no-store' });
  if (!response.ok) return false;
  const body = (await response.json()) as { user?: { isPlatformAdmin?: unknown } | null };
  return body.user?.isPlatformAdmin === true;
}

export function useAuth() {
  const user = useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot);

  const logout = useCallback(async () => {
    // Explicit logout: unsaved editor documents on this device belong to this user and must not outlive the
    // session (a 401 / expiry is NOT a logout and keeps them). Best effort, never blocks the logout.
    const wipe = wipeEditorLocalData().catch(() => undefined);
    try {
      await fetch('/api/auth/logout', { method: 'POST' });
    } finally {
      await Promise.race([wipe, new Promise<void>((resolve) => setTimeout(resolve, 1500))]);
    }
    notifyAuthChange();
    // Raw browser navigation does not get the app's basePath ('/app') prepended
    // automatically, unlike next/link or next/navigation — the path must be absolute.
    window.location.href = '/app/login';
  }, []);

  // platform-admin-access-incident: the reelbolt_user cookie is browser-editable and is used for
  // display only. The admin bit comes from /api/auth/me, which verifies the signed token; until it
  // answers (or if it fails) the user is treated as a non-admin, so no admin UI flashes.
  const { data: verifiedAdmin } = useSWR(
    user ? ([AUTH_ME_URL, user.email ?? ''] as const) : null,
    fetchVerifiedPlatformAdmin,
    { revalidateOnFocus: false },
  );
  const isPlatformAdmin = verifiedAdmin === true;
  const isOrgAdmin = user?.orgRole === 'Owner' || user?.orgRole === 'Admin';

  return useMemo(
    () => ({
      user,
      isAuthenticated: !!user,
      isPlatformAdmin,
      /** @deprecated use isPlatformAdmin */
      isAdmin: isPlatformAdmin,
      isOrgAdmin,
      activeOrgId: user?.org ?? null,
      emailVerified: user?.emailVerified ?? true,
      logout,
    }),
    [user, logout, isPlatformAdmin, isOrgAdmin],
  );
}
