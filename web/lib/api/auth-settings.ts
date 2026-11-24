import { apiFetch } from './client';

// Relative path only: the JWT is an httpOnly cookie that nginx turns into an Authorization header.
// The route is self-only (mcp-oauth.md §11): the caller reads and changes only their OWN setting;
// there is no {userId} in the path.
export const AUTH_SETTINGS_PATH = '/api/v1/auth/settings';

/** The caller's own per-user settings. */
export interface AuthSettings {
  /**
   * Lets an agent connected through MCP see and use the 13 administrative tools. Off by default.
   * Meaningful only for a user who already holds that authority; the bridge reads the live column
   * per call and the `assistant.admin` scope is granted at consent time.
   */
  mcpAdminToolsEnabled: boolean;
  /**
   * Per-user opt-in that lets a tool connected through MCP allow sending this user's media to AI services for a run it
   * starts. Off by default; the server reads the live column on every such call. Optional: an older server omits it.
   */
  mcpMediaEgressEnabled?: boolean;
  /** The name shown in greetings; the server trims it and accepts 1..100 characters. */
  displayName: string;
  /**
   * The saved interface language (a code in i18n/locales.json). Optional: a server that predates the
   * field omits it, and refuses it with a 400 on PATCH, in which case the `rb_locale` cookie still wins.
   */
  locale?: string;
  /** An address change awaiting confirmation from the current mailbox, or null. Older servers omit it. */
  pendingEmailChange?: { newEmail: string; expiresAt: string } | null;
}

/** The fields a caller may change in a single PATCH. */
export type AuthSettingsPatch = Partial<Omit<AuthSettings, 'pendingEmailChange'>>;

export function getAuthSettingsUrl(): string {
  return AUTH_SETTINGS_PATH;
}

export async function fetchAuthSettings(): Promise<AuthSettings> {
  return apiFetch<AuthSettings>(AUTH_SETTINGS_PATH);
}

/**
 * Writes the presented field(s) and returns what the server ACTUALLY stored, so the caller never
 * has to guess. A non-ok response throws ApiError (see client.ts).
 */
export async function updateAuthSettings(
  patch: AuthSettingsPatch,
): Promise<AuthSettings> {
  return apiFetch<AuthSettings>(AUTH_SETTINGS_PATH, {
    method: 'PATCH',
    body: JSON.stringify(patch),
  });
}

/** Withdraws the caller's own pending email change (204). */
export async function cancelOwnEmailChange(): Promise<void> {
  return apiFetch<void>('/api/v1/auth/email-change', { method: 'DELETE' });
}
