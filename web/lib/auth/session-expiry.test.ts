import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  handleUnauthorized,
  isAuthEndpoint,
  resetSessionExpiryForTests,
  sessionExpiredLoginUrl,
  unauthorizedMessage,
} from './session-expiry';
import { safeNextRedirect } from './redirect';

describe('sessionExpiredLoginUrl', () => {
  it('carries the current path and the expired reason', () => {
    expect(sessionExpiredLoginUrl('/app/projects/1', '?tab=files')).toBe(
      '/app/login?next=%2Fapp%2Fprojects%2F1%3Ftab%3Dfiles&reason=expired',
    );
  });
  it('drops next for a hostile or unusable path but keeps the reason', () => {
    for (const bad of ['//evil.com', '/\\evil.com', 'https://evil.com']) {
      expect(sessionExpiredLoginUrl(bad)).toBe('/app/login?reason=expired');
    }
  });
  it('never uses an auth page as the return path', () => {
    expect(sessionExpiredLoginUrl('/app/login')).toBe('/app/login?reason=expired');
  });
});

describe('open-redirect guard on ?next (login side)', () => {
  it.each([
    '//evil',
    'https://evil',
    '/\\evil',
    '/\\/evil',
    '%2F%2Fevil',
    '%252F%252Fevil',
    '/\t/evil',
    '\t//evil',
    'javascript:alert(1)',
    '/app/x://evil',
  ])('rejects %j', (value) => {
    expect(safeNextRedirect(value)).toBeNull();
  });
  it('accepts a same-origin app path', () => {
    expect(safeNextRedirect('/app/projects/1?tab=files')).toBe('/app/projects/1?tab=files');
  });
});

describe('handleUnauthorized', () => {
  const assign = vi.fn();
  const setLocation = (pathname: string, search = '') => {
    Object.defineProperty(window, 'location', { configurable: true, value: { pathname, search, assign } });
  };
  beforeEach(() => {
    assign.mockReset();
    resetSessionExpiryForTests();
  });
  afterEach(() => resetSessionExpiryForTests());

  it('redirects once however many calls fail', () => {
    setLocation('/app/projects/1');
    handleUnauthorized(401, '/api/v1/projects');
    handleUnauthorized(401, '/api/v1/files');
    expect(assign).toHaveBeenCalledTimes(1);
    expect(assign).toHaveBeenCalledWith('/app/login?next=%2Fapp%2Fprojects%2F1&reason=expired');
  });
  it('ignores non-401, auth endpoints and auth pages (no loop)', () => {
    setLocation('/app/projects/1');
    handleUnauthorized(403, '/api/v1/projects');
    handleUnauthorized(401, '/api/v1/auth/token');
    setLocation('/app/login');
    handleUnauthorized(401, '/api/v1/projects');
    setLocation('/app/reset-password');
    handleUnauthorized(401, '/api/v1/projects');
    expect(assign).not.toHaveBeenCalled();
  });
  it('never redirects for the anonymous marketplace routes or from the community pages', () => {
    setLocation('/app/projects/1');
    handleUnauthorized(401, '/api/v1/public/marketplace/recipes?limit=24');
    expect(assign).not.toHaveBeenCalled();
    setLocation('/app/community');
    handleUnauthorized(401, '/api/v1/marketplace/recipes/x/me');
    setLocation('/app/community/3f2b8c1e-9a4d-4e6f-8b7a-1c2d3e4f5a6b');
    handleUnauthorized(401, '/api/v1/marketplace/recipes/x/me');
    expect(assign).not.toHaveBeenCalled();
    expect(unauthorizedMessage(401, '/api/v1/public/marketplace/facets')).toBeNull();
  });
  it('still redirects a 401 from the signed-in /community/mine page', () => {
    setLocation('/app/community/mine');
    handleUnauthorized(401, '/api/v1/marketplace/mine');
    expect(assign).toHaveBeenCalledTimes(1);
  });
  it('replaces the raw server string for a handled 401 only', () => {
    expect(unauthorizedMessage(401, '/api/v1/x')).toBe('Your session ended. Sign in to continue.');
    expect(unauthorizedMessage(401, '/api/v1/auth/token')).toBeNull();
    expect(unauthorizedMessage(500, '/api/v1/x')).toBeNull();
    expect(isAuthEndpoint('/api/v1/auth/change-password')).toBe(true);
    expect(isAuthEndpoint('/api/v1/auth/forgot-password?x=1')).toBe(true);
    expect(isAuthEndpoint('/api/v1/auth/account/delete')).toBe(true);
    expect(isAuthEndpoint('/api/v1/auth/invitations/abc')).toBe(true);
    expect(isAuthEndpoint('/api/v1/auth/invitations/abc/accept')).toBe(false);
    expect(isAuthEndpoint('/api/v1/auth/accept-terms')).toBe(false);
    expect(isAuthEndpoint('/api/v1/auth/switch-org')).toBe(false);
  });
});
