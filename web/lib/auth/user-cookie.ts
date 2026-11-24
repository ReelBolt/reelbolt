import type { UserInfo } from '../types/auth';

/**
 * The one place the readable `reelbolt_user` cookie is parsed and written.
 *
 * Two kinds of input reach `parseUserCookie`, and it must accept both without ever throwing:
 *  - a value Next has ALREADY percent-decoded once (`request.cookies.get(...)` in proxy.ts and
 *    `cookies()` in server components). That is plain JSON, so it is parsed as given FIRST. Decoding it
 *    again is wrong: a name containing '%' (e.g. "100%", "%41") would make decodeURIComponent throw or
 *    change the text, which used to lock the user out of every route.
 *  - the raw encoded value from `document.cookie`, which the Go API writes with url.QueryEscape
 *    (api/handlers/auth/auth.go writeSession): a space is '+', everything else is %XX. That is not JSON,
 *    so it falls through to decoding, with '+' read as a space. (A decoded value that parses as JSON
 *    never reaches this branch, so a literal '+' in it is kept.)
 *
 * Writing uses encodeURIComponent (%20 for a space, %2B for a plus), which round-trips through the
 * same reader. The client rewrite sets Path, SameSite=Lax and Max-Age only; it adds Secure on https.
 */
export const USER_COOKIE_NAME = 'reelbolt_user';

function asUser(parsed: unknown): UserInfo | null {
  return typeof parsed === 'object' && parsed !== null && !Array.isArray(parsed) ? (parsed as UserInfo) : null;
}

/** Parses a cookie VALUE, encoded or already decoded. Null (never a throw) when it is not a JSON object. */
export function parseUserCookie(raw: string | undefined | null): UserInfo | null {
  if (!raw) return null;
  try {
    return asUser(JSON.parse(raw));
  } catch {
    // Not plain JSON: treat it as the encoded form.
  }
  try {
    return asUser(JSON.parse(decodeURIComponent(raw.replace(/\+/g, '%20'))));
  } catch {
    return null;
  }
}

/** The encoded cookie VALUE for a user. */
export function serializeUserCookie(user: UserInfo): string {
  return encodeURIComponent(JSON.stringify(user));
}

/** Reads the cookie from `document.cookie`; null on the server or when absent. */
export function readUserCookie(): UserInfo | null {
  if (typeof document === 'undefined') return null;
  const match = document.cookie.match(/(?:^|;\s*)reelbolt_user=([^;]+)/);
  return match ? parseUserCookie(match[1]) : null;
}

/** Writes the cookie with the attributes the API uses for the readable session cookie. */
export function writeUserCookie(user: UserInfo, maxAgeSeconds?: number): void {
  const age = maxAgeSeconds === undefined ? '' : `; Max-Age=${maxAgeSeconds}`;
  const secure = typeof location !== 'undefined' && location.protocol === 'https:' ? '; Secure' : '';
  document.cookie = `${USER_COOKIE_NAME}=${serializeUserCookie(user)}; path=/; SameSite=Lax${age}${secure}`;
}
