import { msg, type Msg } from '@/i18n/msg';
import { enT } from '@/lib/i18n-default';
import { safeNextRedirect } from './redirect';

/** The sentence every expired-session surface uses (login banner, and the message of a 401 ApiError). */
export const SESSION_ENDED_MESSAGE = enT('libLabels.auth.sessionEnded');

/** Translatable form of SESSION_ENDED_MESSAGE: resolve with `useT().msg(SESSION_ENDED_MSG)`. */
export const SESSION_ENDED_MSG: Msg = msg('libLabels.auth.sessionEnded');

/** The `reason` query value the login page turns into SESSION_ENDED_MESSAGE. */
export const SESSION_EXPIRED_REASON = 'expired';

const APP_BASE_PATH = '/app';

/**
 * Pages a signed-out visitor is expected to be on (bare paths, basePath stripped). A 401 here is
 * not an expiry (a wrong password answers 401 on /login) and redirecting would loop.
 */
const PUBLIC_AUTH_PATHS = new Set([
  '/login',
  '/signup',
  '/verify-email',
  '/forgot-password',
  '/reset-password',
  '/unsubscribe',
  '/marketing/confirm',
  '/offline',
]);

function barePath(pathname: string): string {
  return pathname === APP_BASE_PATH ? '/' : pathname.startsWith(`${APP_BASE_PATH}/`) ? pathname.slice(APP_BASE_PATH.length) : pathname;
}

/** True for pages where a 401 must not trigger the expiry redirect (auth pages, invite and guest links). */
/** The public gallery only: `/community` and `/community/<guid>`. `/community/mine` is signed-in and still redirects on a 401. */
const PUBLIC_COMMUNITY_PATH = /^\/community(?:\/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})?$/;

export function isSessionExemptPath(pathname: string): boolean {
  const bare = barePath(pathname);
  return (
    PUBLIC_AUTH_PATHS.has(bare) ||
    bare.startsWith('/invite/') ||
    bare.startsWith('/review/g/') ||
    PUBLIC_COMMUNITY_PATH.test(bare)
  );
}

/** The anonymous marketplace reads: a 401 here is never a session expiry (the visitor may have no session at all). */
export function isPublicApiUrl(url: string): boolean {
  return url.startsWith('/api/v1/public/');
}

/**
 * Calls whose 401 means "these credentials are wrong" (or "no session is expected yet"), not "your
 * session ended": the unauthenticated auth endpoints, plus change-password, which answers 401 for a
 * wrong CURRENT password (handleChangePassword) and account/delete, which answers 401
 * "password is incorrect" (account.go). The only other 401s under /api/v1/auth/ are plain "unauthorized". Everything else under
 * /api/v1/auth/ (accept-terms, switch-org, account and settings calls...) is session-backed and
 * goes through the expiry handler.
 */
const UNAUTHENTICATED_AUTH_ENDPOINT =
  /^\/api\/v1\/auth\/(?:(?:token|signup|verify-email|resend-verification|forgot-password|reset-password|change-password|account\/delete|oauth)(?:[/?#]|$)|invitations\/[^/?#]+(?:[?#]|$))/;

export function isAuthEndpoint(url: string): boolean {
  return UNAUTHENTICATED_AUTH_ENDPOINT.test(url);
}

/**
 * The login URL for an expired session: `/app/login?next=<path>&reason=expired`. `next` is only
 * attached when the current location is itself a valid same-origin absolute path (safeNextRedirect);
 * otherwise the user simply signs in to the dashboard.
 */
export function sessionExpiredLoginUrl(pathname: string, search = ''): string {
  const params = new URLSearchParams();
  const next = safeNextRedirect(`${pathname}${search}`);
  if (next && !isSessionExemptPath(pathname)) params.set('next', next);
  params.set('reason', SESSION_EXPIRED_REASON);
  return `${APP_BASE_PATH}/login?${params.toString()}`;
}

let redirecting = false;

/**
 * The one place a 401 on an authenticated call goes. Navigates (once, however many calls fail at
 * the same time) to the login page with the return path. It does not touch client state, so
 * locally saved drafts survive the round trip. No-op on the server, on auth pages, and for the
 * auth endpoints' own 401s.
 */
export function handleUnauthorized(status: number, url = ''): void {
  if (status !== 401 || typeof window === 'undefined') return;
  if (isAuthEndpoint(url) || isPublicApiUrl(url)) return;
  const { pathname, search } = window.location;
  if (isSessionExemptPath(pathname) || redirecting) return;
  redirecting = true;
  window.location.assign(sessionExpiredLoginUrl(pathname, search));
}

/** Test hook: re-arm the once-only guard. */
export function resetSessionExpiryForTests(): void {
  redirecting = false;
}

/** The message to show for a 401 that was handled centrally (never the raw server string). */
export function unauthorizedMessage(status: number, url = ''): string | null {
  return status === 401 && !isAuthEndpoint(url) && !isPublicApiUrl(url) ? SESSION_ENDED_MESSAGE : null;
}
