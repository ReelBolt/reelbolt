import { NextResponse } from 'next/server';
import type { NextRequest } from 'next/server';
import { safeNextRedirect } from '@/lib/auth/redirect';
import { parseUserCookie } from '@/lib/auth/user-cookie';
import { legalChangeFor } from '@/lib/auth/legal';
import { servedLegalVersions } from '@/lib/auth/legal-server';
import { videoEditorEnabled } from '@/lib/editor/config';
import { isPlatformAdminClaims, sessionTokenConfig, verifySessionToken } from '@/lib/auth/session-token';

/**
 * Pages and assets reachable without a session, matched EXACTLY, never as a prefix (S-31): a prefix
 * entry also opens every route that merely starts with the same letters (`/loginX`,
 * `/unsubscribe-all`, `/marketing/confirm/anything`), including routes added later. The query string
 * is not part of `pathname`, so an exact match still admits every tokenised link. The matcher sees
 * paths with the '/app' basePath already stripped, which is why these are bare.
 *
 * The five account paths are here because their visitors are by definition signed out: someone
 * who has just created an account, opened a verification or reset link from their inbox, asked
 * for one, or followed a team invitation. Gating them would bounce every one of them to /login.
 * Each is a page only: the API calls it makes go to /api/v1/auth/*, which the '/api/' prefix
 * already covers.
 *
 * Marketing email links (saas-promotions): withdrawal needs no login and must never be behind a
 * sign-in or a re-acceptance; the double-opt-in confirmation is reached from an inbox by someone who
 * may have no account at all. Each page only POSTs its token on a button click.
 *
 * PWA assets: the manifest's <link> tag is emitted on every page including /login, and the service
 * worker precaches /offline at install time, so both need to resolve to real content rather than a
 * 307 to /login for installability/precaching to work.
 *
 * This is navigation convenience only: the API stays the authority on every request.
 */
const publicExactPaths = new Set([
  '/login',
  '/signup',
  '/verify-email',
  '/forgot-password',
  '/reset-password',
  '/email-change',
  '/unsubscribe',
  '/marketing/confirm',
  '/offline',
  '/sw.js',
  '/manifest.webmanifest',
  '/icon.svg',
  '/apple-icon.png',
  '/favicon-192.png',
  '/favicon-512.png',
  '/favicon-512-maskable.png',
]);

/**
 * The only prefixes, each ending in '/' so it covers a whole path segment and nothing beside it:
 * a team invitation link (C8), /invite/<token>, where the token in the path is the credential; and
 * the API routes, which authenticate every request themselves.
 */
const publicPrefixes = ['/invite/', '/api/'];

/**
 * A guest review link, `/review/g/<token>`: the 43-character base64url token in the path is the credential
 * (docs/design/guest-review-links.md). Matched with an anchored pattern, never a prefix: `/review/g/x`,
 * `/review/g/<token>/extra` and `/review/gX...` stay behind the sign-in. The API is still the authority.
 */
const GUEST_REVIEW_PATH = /^\/review\/g\/[A-Za-z0-9_-]{43}$/;

/**
 * The community recipe gallery, browsable without an account (docs/design/recipe-marketplace.md): the list `/community` and one
 * recipe `/community/<guid>`, nothing else beside them (`/community/`, `/community/x`, `/community/<guid>/extra` stay behind the
 * sign-in). Anchored, never a prefix. Copy, rate and report are signed-in API calls, which authenticate themselves.
 */
const COMMUNITY_PATH = /^\/community(?:\/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})?$/;

function isPublicPath(pathname: string): boolean {
  return (
    publicExactPaths.has(pathname) ||
    publicPrefixes.some((p) => pathname.startsWith(p)) ||
    GUEST_REVIEW_PATH.test(pathname) ||
    COMMUNITY_PATH.test(pathname)
  );
}

/**
 * The video-editor paths, as bare paths (the '/app' basePath is stripped before matching, so the
 * hub is '/editor' here, not '/app/editor'). Each shape the video editor owns, exactly:
 *
 *   /editor                                   the hub (timelines, media, pictures)
 *   /editor/preview                           the preview surface
 *   /editor/{projectId}/timelines
 *   /editor/{projectId}/media
 *   /editor/{projectId}/timeline/{timelineId}
 *   /editor/{projectId}/review/{timelineId}
 *   /editor/{projectId}/{stepResultId}        one render opened straight in the editor
 *   /projects/{projectId}/editor/{stepResultId}   the legacy entry point (a render's "Open in editor")
 *
 * Anchored, never a bare prefix: '/editor' as a prefix would also swallow the picture editor.
 */
const videoEditorPatterns: RegExp[] = [
  /^\/editor$/,
  /^\/editor\/preview$/,
  /^\/editor\/[^/]+\/timelines$/,
  /^\/editor\/[^/]+\/media$/,
  /^\/editor\/[^/]+\/timeline\/[^/]+$/,
  /^\/editor\/[^/]+\/review\/[^/]+$/,
  /^\/editor\/[^/]+\/[^/]+$/,
  /^\/projects\/[^/]+\/editor\/[^/]+$/,
];

/** The picture editor: `/editor/thumbnails/{documentId}`, and it is NEVER gated. */
const thumbnailEditorPrefix = '/editor/thumbnails';

/**
 * Whether a bare path is a **video** editor path — the ones the flag hides.
 *
 * The picture editor is checked FIRST and excluded. `/editor/thumbnails/{documentId}` has the same
 * two-segment shape as `/editor/{projectId}/{stepResultId}`, so without this the thumbnail editor
 * would be caught by that rule; it must render and work in BOTH flag states.
 */
function isVideoEditorPath(pathname: string): boolean {
  if (pathname === thumbnailEditorPrefix || pathname.startsWith(`${thumbnailEditorPrefix}/`)) {
    return false;
  }
  return videoEditorPatterns.some((pattern) => pattern.test(pathname));
}

interface CookieUser {
  email: string;
  isAdmin?: boolean;
  isPlatformAdmin?: boolean;
  mustChangePassword: boolean;
  /**
   * The Terms version this session accepted, or absent when the user has accepted none. The Go API
   * writes it from `legal_acceptances` (api/handlers/auth/auth.go, C10), and since F-7 the row it
   * writes was itself pinned to the version `legal_documents` declares — so this is a value the
   * server established, never one the caller asserted.
   */
  termsVersion?: string;
  /**
   * The latest Privacy version the user ACCEPTED (Go `privacyVersion`, omitempty). Absent on a
   * session issued before the Go API carried it: such a session is never gated for Privacy alone.
   */
  privacyVersion?: string;
}

/**
 * Pages the re-acceptance gate never blocks, whatever the session is behind on, matched EXACTLY.
 * /accept-terms itself (no loop), and /settings/data: the page that holds only the data export and
 * account deletion cards, because neither right may be made conditional on accepting a new
 * document. The rest of /settings (password, integrations, billing...) stays gated (S-22): exempting
 * the whole account page let a session behind on the Terms keep using far more than those two
 * rights. Sign-out is a POST to /api/auth/logout, which the '/api/' public entry already covers.
 */
const legalGateExempt = new Set(['/accept-terms', '/settings/data']);

/**
 * The sign-in redirect for a request with no usable session. It carries the requested page as
 * `?next=` so the user returns to it after signing in (a natural expiry looks exactly like a first
 * visit here, so no `reason`). The value is rebuilt as `/app` + path + search of THIS request, never
 * taken from a caller-supplied parameter, and still has to pass safeNextRedirect (a single leading
 * slash, no scheme, host, backslash or whitespace); the dashboard default and the root add nothing.
 *
 * request.nextUrl.clone() preserves the app's basePath ('/app') when re-serialized; building a URL
 * from a bare path plus request.url instead would silently drop it.
 */
function loginRedirectUrl(request: NextRequest): URL {
  const { pathname, search } = request.nextUrl;
  const url = request.nextUrl.clone();
  url.pathname = '/login';
  url.search = '';
  if (pathname !== '/' && pathname !== '/dashboard') {
    const next = safeNextRedirect(`/app${pathname}${search}`);
    if (next) url.searchParams.set('next', next);
  }
  return url;
}

export async function proxy(request: NextRequest) {
  const { pathname } = request.nextUrl;

  // Allow public paths
  if (isPublicPath(pathname)) {
    return NextResponse.next();
  }

  const userCookie = request.cookies.get('reelbolt_user')?.value;
  const tokenCookie = request.cookies.get('reelbolt_token')?.value;

  // No auth → redirect to login
  if (!tokenCookie || !userCookie) {
    return NextResponse.redirect(loginRedirectUrl(request));
  }

  // The signed, httpOnly session token is the authority (platform-admin-access-incident): a missing
  // signing key, a bad signature, a wrong iss/aud/alg or an expired token is a signed-out visitor.
  const claims = verifySessionToken(tokenCookie, sessionTokenConfig());
  if (!claims) {
    return NextResponse.redirect(loginRedirectUrl(request));
  }

  // Next has already decoded the value once; parseUserCookie parses it as given and never throws.
  const user = parseUserCookie(userCookie) as CookieUser | null;
  if (!user) {
    const url = request.nextUrl.clone();
    url.pathname = '/login';
    return NextResponse.redirect(url);
  }

  // Must change password → force to change-password page
  if (user?.mustChangePassword && pathname !== '/change-password') {
    const url = request.nextUrl.clone();
    url.pathname = '/change-password';
    return NextResponse.redirect(url);
  }

  // Platform routes require a platform admin, read from the VERIFIED token. The reelbolt_user cookie
  // is browser-writable and stays display-only (name, email); its isPlatformAdmin is never trusted.
  if (pathname.startsWith('/admin') && !isPlatformAdminClaims(claims)) {
    const url = request.nextUrl.clone();
    url.pathname = '/dashboard';
    // A one-time notice, shown and stripped by components/shell/AdminOnlyNotice.tsx: an unexplained bounce reads as a bug.
    url.search = '';
    url.searchParams.set('notice', 'admin-only');
    return NextResponse.redirect(url);
  }

  // Re-acceptance gate for a new terms version.
  //
  // The version this session accepted is in the cookie because the Go API put it there, read from
  // legal_acceptances (C10). The version it is compared against is NOT a build-time constant: it is
  // what the service that owns legal_documents reports right now (F-7). Comparing the cookie to a
  // NEXT_PUBLIC_TERMS_VERSION build arg meant both sides of the comparison were values the browser
  // could produce, and the API recorded whatever version the caller sent, so neither side was
  // evidence. Now both sides come from the database: the Go API pins the recorded acceptance to the
  // published version, and this gate reads that same version back.
  //
  // An ABSENT field still means "this session predates the gate" and is left alone: treating it as
  // stale would send every pre-existing session to /accept-terms on its next page load. A first
  // login that accepted nothing at all (the OAuth path) is routed there by the API's callback
  // itself, not by this gate.
  //
  // A read that does not answer leaves the session alone rather than redirecting: the gate's job is
  // to move people it KNOWS are behind, and an API outage must not lock every session out of the
  // dashboard. The consequence is only that a publication during an outage takes effect once the
  // API answers again.
  //
  // Privacy: a session whose cookie names an accepted Privacy version behind the served one goes to
  // the same page, which then shows an acknowledgement rather than a consent form (legalChangeFor).
  // A cookie without privacyVersion is left alone for Privacy, exactly like the terms rule above.
  if (!legalGateExempt.has(pathname) && typeof user?.termsVersion === 'string') {
    const served = await servedLegalVersions();
    if (legalChangeFor(user, served) !== null) {
      const url = request.nextUrl.clone();
      url.pathname = '/accept-terms';
      return NextResponse.redirect(url);
    }
  }

  // The video editor is off: hide the surface by sending every video-editor path to the
  // coming-soon page. The picture editor (/editor/thumbnails/{documentId}) is never matched here.
  // Placed after the account gates above so a password change, an admin-only page or a stale
  // terms acceptance keeps its precedence; the coming-soon page is itself behind all of them.
  if (!videoEditorEnabled() && isVideoEditorPath(pathname)) {
    const url = request.nextUrl.clone();
    url.pathname = '/editor-coming-soon';
    return NextResponse.redirect(url);
  }

  return NextResponse.next();
}

export const config = {
  matcher: ['/((?!_next/static|_next/image|favicon.ico).*)'],
};
