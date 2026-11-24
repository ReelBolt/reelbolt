import { describe, it, expect, vi } from 'vitest';

vi.mock('next/navigation', () => ({ usePathname: () => '/' }));

import { isGaBlockedRoute } from './GoogleAnalytics';

// WEB-5: token-bearing routes never load gtag.js.
describe('isGaBlockedRoute', () => {
  it.each([
    '/review/g/abc123token',
    '/app/review/g/abc123token',
    '/runners/pair',
    '/app/runners/pair?code=ABCD-1234',
    '/runners/pair/',
    '/invite/tok',
    '/reset-password?token=x',
  ])('blocks %s', (path) => {
    expect(isGaBlockedRoute(path)).toBe(true);
  });

  it.each(['/', '/runners', '/runners/123', '/editor/42/review', '/projects/1/edit', null, undefined])(
    'does not block %s',
    (path) => {
      expect(isGaBlockedRoute(path)).toBe(false);
    },
  );
});
