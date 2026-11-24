// @vitest-environment jsdom
/**
 * After a failed provider sign-in the Go callback sends the browser to
 * `/app/login?error=<reason>&for=…&utm_*=…&lp=…`: the reason, next to everything the visitor's own
 * link carried (api/handlers/auth/oauth_state.go). The reason must not cost the visitor their link:
 * the "Create account" link, the provider buttons and, one page on, the signup page's "Sign in"
 * link all keep `for`, `utm_*` and `lp`. The error itself is shown once and is never forwarded.
 *
 * These render the real pages with the real forms (page.test.tsx stubs the form).
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.mock('next/navigation', () => ({
  useRouter: () => ({ push: vi.fn(), refresh: vi.fn(), prefetch: vi.fn(), replace: vi.fn() }),
  usePathname: () => '/login',
  useSearchParams: () => new URLSearchParams(),
}));
vi.mock('next/headers', () => ({ headers: async () => ({ get: () => null }) }));
vi.mock('@/lib/hooks/use-auth', () => ({ notifyAuthChange: vi.fn() }));
vi.mock('@/lib/api/auth', () => ({ login: vi.fn(), signup: vi.fn() }));

import { resetOfferedOAuthCache } from '@/lib/auth/oauth-server';
import SignupPage from '../signup/page';
import LoginPage from './page';

vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);
Element.prototype.scrollIntoView = vi.fn();

/** What the Go callback puts on the address after a provider sign-in that found no account. */
const AFTER_FAILED_PROVIDER = {
  error: 'oauth_no_account',
  for: 'creator',
  utm_source: 'youtube',
  utm_medium: 'paid',
  utm_campaign: 'creators-q4',
  utm_content: 'yt-sponsor',
  utm_term: 'editing',
  lp: '/for/creators',
};
const CARRIED =
  'for=creator&utm_source=youtube&utm_medium=paid&utm_campaign=creators-q4&utm_content=yt-sponsor&utm_term=editing&lp=%2Ffor%2Fcreators';

const renderPage = async (
  Page: typeof LoginPage | typeof SignupPage,
  params: Record<string, string | string[] | undefined>,
) => render(<MantineProvider>{await Page({ searchParams: Promise.resolve(params) })}</MantineProvider>);

const href = (name: string) => screen.getByRole('link', { name }).getAttribute('href');

describe('a failed provider sign-in keeps what the link carried', () => {
  beforeEach(() => {
    process.env.SIGNUP_ENABLED = 'true';
    // The API cannot be asked here, so the env flags decide the provider buttons.
    process.env.OAUTH_GOOGLE_ENABLED = 'true';
    resetOfferedOAuthCache();
    window.sessionStorage.clear();
    window.localStorage.clear();
    global.matchMedia = (query: string): MediaQueryList =>
      ({
        matches: false,
        media: query,
        onchange: null,
        addListener: () => {},
        removeListener: () => {},
        addEventListener: () => {},
        removeEventListener: () => {},
        dispatchEvent: () => true,
      }) as MediaQueryList;
  });
  afterEach(() => {
    cleanup();
    delete process.env.SIGNUP_ENABLED;
    delete process.env.OAUTH_GOOGLE_ENABLED;
    resetOfferedOAuthCache();
  });

  it('login page: shows the reason, and "Create account" keeps for, utm_* and lp', async () => {
    await renderPage(LoginPage, AFTER_FAILED_PROVIDER);
    expect(screen.getByRole('alert').textContent).not.toBe('');
    expect(href('Create account')).toBe(`/signup?${CARRIED}`);
    // A second try with the provider carries them too.
    expect(href('Continue with Google')).toBe(`/api/v1/auth/oauth/google/start?${CARRIED}`);
  });

  it('login page: the reason is never forwarded, whichever one it is', async () => {
    for (const error of ['oauth_no_account', 'oauth_email_unverified', 'oauth_failed', 'something_new']) {
      await renderPage(LoginPage, { ...AFTER_FAILED_PROVIDER, error });
      expect(href('Create account'), error).toBe(`/signup?${CARRIED}`);
      expect(href('Create account'), error).not.toContain('error');
      cleanup();
    }
  });

  it('login page: keeps plan and a safe return path next to them', async () => {
    await renderPage(LoginPage, { ...AFTER_FAILED_PROVIDER, plan: 'creator', next: '/app/projects/9' });
    expect(href('Create account')).toBe(`/signup?${CARRIED}&plan=creator&next=%2Fapp%2Fprojects%2F9`);
  });

  it('login page: the audience alone survives an error (no campaign, so no landing page either)', async () => {
    await renderPage(LoginPage, { error: 'oauth_failed', for: 'educator', lp: '/for/educators' });
    expect(href('Create account')).toBe('/signup?for=educator');
  });

  it('signup page: "Sign in" keeps for, utm_* and lp when an error parameter is on the address', async () => {
    await renderPage(SignupPage, AFTER_FAILED_PROVIDER);
    const links = screen.getAllByRole('link', { name: 'Sign in' });
    expect(links.length).toBeGreaterThan(0);
    for (const link of links) expect(link.getAttribute('href')).toBe(`/login?${CARRIED}`);
    expect(href('Continue with Google')).toBe(`/api/v1/auth/oauth/google/start?${CARRIED}`);
  });

  it('round trip: login with an error -> signup -> back to login loses nothing', async () => {
    await renderPage(LoginPage, AFTER_FAILED_PROVIDER);
    const toSignup = new URL(href('Create account')!, 'https://app.example');
    cleanup();
    await renderPage(SignupPage, Object.fromEntries(toSignup.searchParams));
    const toLogin = new URL(screen.getAllByRole('link', { name: 'Sign in' })[0].getAttribute('href')!, 'https://app.example');
    cleanup();
    await renderPage(LoginPage, Object.fromEntries(toLogin.searchParams));
    expect(href('Create account')).toBe(`/signup?${CARRIED}`);
  });
});
