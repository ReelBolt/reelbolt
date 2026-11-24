'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import { AUTH_SETTINGS_PATH } from '../api/auth-settings';
import type { AuthSettings } from '../api/auth-settings';

/**
 * The caller's own per-user settings (mcp-oauth.md §11). SWR revalidates on remount and on focus,
 * so navigating away from the settings page and back re-reads the stored value from the server.
 */
export function useAuthSettings() {
  return useSWR<AuthSettings>(AUTH_SETTINGS_PATH, fetcher);
}
