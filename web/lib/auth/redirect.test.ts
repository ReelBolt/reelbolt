import { beforeEach, describe, expect, it, vi } from 'vitest';
import { loginDestination, navigateToNext, safeNextRedirect } from './redirect';

/**
 * The open-redirect guard, validated directly. The value is an untrusted ?next= from a URL a user
 * can be tricked into clicking, so every non-same-origin shape must be rejected, and every same-origin
 * absolute path must be honoured.
 */
describe('safeNextRedirect', () => {
  it('honours a same-origin, absolute path (the consent URL the authorize endpoint sends)', () => {
    expect(safeNextRedirect('/oauth/authorize?client_id=app&scope=assistant.tools')).toBe(
      '/oauth/authorize?client_id=app&scope=assistant.tools',
    );
    expect(safeNextRedirect('/dashboard')).toBe('/dashboard');
    expect(safeNextRedirect('/app/projects/123/workflows')).toBe('/app/projects/123/workflows');
  });

  it('rejects a protocol-relative URL ("//host")', () => {
    expect(safeNextRedirect('//evil.com')).toBeNull();
    expect(safeNextRedirect('//evil.com/path?q=1')).toBeNull();
    expect(safeNextRedirect('//evil')).toBeNull();
  });

  it('rejects a URL with a scheme or a host', () => {
    expect(safeNextRedirect('http://evil.com')).toBeNull();
    expect(safeNextRedirect('https://evil.com')).toBeNull();
    expect(safeNextRedirect('https:evil.com')).toBeNull();
    expect(safeNextRedirect('evil.com')).toBeNull();
    expect(safeNextRedirect('evil.com/path')).toBeNull();
  });

  it('rejects script/data payloads', () => {
    expect(safeNextRedirect('javascript:alert(1)')).toBeNull();
    expect(safeNextRedirect('data:text/html,<script>')).toBeNull();
  });

  it('rejects the control-character open-redirect vectors', () => {
    expect(safeNextRedirect('\t//evil.com')).toBeNull();
    expect(safeNextRedirect('\n//evil.com')).toBeNull();
    expect(safeNextRedirect('  //evil.com')).toBeNull();
    expect(safeNextRedirect('/oauth\n/authorize')).toBeNull();
  });

  it('rejects a backslash, which the browser resolves as an authority ("/\\evil.com")', () => {
    expect(safeNextRedirect('/\\evil.com')).toBeNull();
    expect(safeNextRedirect('/\\/evil.com')).toBeNull();
    expect(safeNextRedirect('/oauth\\authorize')).toBeNull();
  });

  it('rejects a bare "/", an empty value, a non-string, and a repeated param', () => {
    expect(safeNextRedirect('/')).toBeNull();
    expect(safeNextRedirect('')).toBeNull();
    expect(safeNextRedirect('   ')).toBeNull();
    expect(safeNextRedirect(undefined)).toBeNull();
    expect(safeNextRedirect(null)).toBeNull();
    expect(safeNextRedirect(['next', 'value'])).toBeNull();
  });

  it('trims surrounding whitespace from a legitimate path', () => {
    expect(safeNextRedirect('  /dashboard  ')).toBe('/dashboard');
  });
});

/**
 * The post-sign-in navigation decision. `next` is a domain-root path (safeNextRedirect has
 * already vetted it); the dashboard is served under '/app' and next/navigation re-applies that
 * basePath, so only a path the dashboard itself serves may go through the router.
 */
describe('loginDestination', () => {
  it('requests a domain-root path the dashboard does not serve verbatim (the OAuth consent URL)', () => {
    expect(loginDestination('/oauth/authorize?client_id=app&scope=assistant.tools')).toEqual({
      href: '/oauth/authorize?client_id=app&scope=assistant.tools',
      fullDocument: true,
    });
  });

  it('routes a path under the dashboard basePath, with the basePath stripped (the router re-applies it)', () => {
    expect(loginDestination('/app/projects/123/workflows')).toEqual({
      href: '/projects/123/workflows',
      fullDocument: false,
    });
    expect(loginDestination('/app')).toEqual({ href: '/', fullDocument: false });
  });
});

/**
 * The single place the assign-vs-push branch lives: both auth surfaces (the sign-in page and the
 * forced password change) leave through it, so a domain-root destination is requested by the
 * browser and a dashboard one goes through the router.
 */
describe('navigateToNext', () => {
  const push = vi.fn();

  beforeEach(() => {
    push.mockReset();
    // jsdom has no real navigation; the full-document branch calls window.location.assign.
    Object.defineProperty(window, 'location', {
      configurable: true,
      value: { ...window.location, assign: vi.fn(), replace: vi.fn() },
    });
  });

  it('full-document-navigates a domain-root path the dashboard does not serve', () => {
    navigateToNext('/oauth/authorize?client_id=app', { push });
    expect(window.location.assign).toHaveBeenCalledWith('/oauth/authorize?client_id=app');
    expect(push).not.toHaveBeenCalled();
  });

  it('routes an app path with the basePath stripped', () => {
    navigateToNext('/app/projects/123', { push });
    expect(push).toHaveBeenCalledWith('/projects/123');
    expect(window.location.assign).not.toHaveBeenCalled();
  });
});
