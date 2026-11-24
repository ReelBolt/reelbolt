/**
 * Validates an untrusted "return to" path for a post-login redirect (open-redirect guard).
 *
 * The value arrives in a URL a user can be tricked into clicking (the OAuth authorize endpoint sends
 * a signed-out user to /login?next=<the consent URL>), so it is treated as hostile input. Only a
 * same-origin, absolute *path* is honoured:
 *
 *   - it must start with EXACTLY one "/" — never "//" (a protocol-relative URL that would navigate
 *     to another host) and never a scheme or host ("http://…", "https:…", "javascript:…", "data:…");
 *   - it must carry no scheme and no host anywhere in the value;
 *   - it must carry no whitespace or control characters — the classic "\t//evil" / "\n//evil"
 *     vectors, where a browser strips the leading control character and resolves the rest as
 *     protocol-relative, are rejected.
 *
 * Anything that fails a check returns null, and the caller falls back to its normal destination. A
 * non-string (a repeated `?next=`, an empty value, an undefined) is rejected the same way.
 */
export function safeNextRedirect(next: unknown): string | null {
  if (typeof next !== 'string') return null;
  const v = next.trim();
  // Same-origin, absolute path: a single leading "/" (so not "//…"), and not just "/".
  if (v.length < 2 || !v.startsWith('/') || v.startsWith('//')) return null;
  // No whitespace anywhere: kills the "\t//evil" / embedded-control-char vectors. A legitimate path
  // placed in a query string would already be percent-encoded.
  if (/\s/.test(v)) return null;
  // No backslash: a browser treats '\' as '/' in a special-scheme URL, so "/\evil.com" is
  // resolved as "//evil.com" and leaves the origin (new URL('/\\evil.com', 'https://x') is
  // 'https://evil.com/'). router.push used to mask this by prefixing the basePath; the
  // full-document navigation below does not.
  if (v.includes('\\')) return null;
  // No scheme/protocol marker anywhere in the value.
  if (v.includes('://')) return null;
  return v;
}

/** The dashboard's basePath (web/next.config.ts). */
const APP_BASE_PATH = '/app';

/**
 * Where a successful sign-in must leave for a validated `next` path.
 *
 * `next` is a domain-root path — the Go API's only producer hands back `r.URL.RequestURI()` —
 * while the dashboard is served under the '/app' basePath and next/navigation's router
 * re-applies it. So:
 *
 *   - a path under the basePath ("/app/projects/123") is a dashboard destination: the router
 *     takes it with the basePath stripped, or it would be doubled;
 *   - any other path is served outside the dashboard — the OAuth consent URL
 *     ("/oauth/authorize?…"), or the marketing site — and the browser must request it
 *     verbatim. Handing it to the router produced "/app/oauth/authorize", a 404 (prod,
 *     2026-10-20).
 */
export function loginDestination(next: string): { href: string; fullDocument: boolean } {
  if (next === APP_BASE_PATH || next.startsWith(`${APP_BASE_PATH}/`)) {
    return { href: next.slice(APP_BASE_PATH.length) || '/', fullDocument: false };
  }
  return { href: next, fullDocument: true };
}

/**
 * Navigate to a validated `next` path after an auth step: a destination the dashboard does not
 * serve is requested verbatim by the browser, one it does goes through the router. The single
 * place the branch lives, so the sign-in page and the forced password change leave the same way.
 */
export function navigateToNext(next: string, router: { push(href: string): void }): void {
  const destination = loginDestination(next);
  if (destination.fullDocument) window.location.assign(destination.href);
  else router.push(destination.href);
}
