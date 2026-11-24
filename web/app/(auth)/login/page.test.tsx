// @vitest-environment jsdom
/**
 * The sign-in page's account-deletion notices: where a deletion request lands once the session is
 * gone (from the account page with the date, from a Google/GitHub confirmation possibly without),
 * and the refusal a Google/GitHub confirmation can come back with.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.mock('next/navigation', () => ({
  useRouter: () => ({ push: vi.fn(), refresh: vi.fn(), prefetch: vi.fn(), replace: vi.fn() }),
  usePathname: () => '/login',
  useSearchParams: () => new URLSearchParams(),
}));

// The page's half of the post-sign-in return-path contract is the prop it hands the form; the
// form's own navigation is LoginForm.test.tsx's job. Stubbing the form exposes exactly that prop,
// so a page that forwarded the raw `?next=` (instead of the validated one) would render "//evil.com".
vi.mock('@/components/auth/LoginForm', () => ({
  LoginForm: ({ next, carry, inAppBrowser }: { next?: string; carry?: unknown; inAppBrowser?: boolean }) => (
    <>
      <div data-testid="login-form-next">{next ?? '(none)'}</div>
      <div data-testid="login-form-carry">{JSON.stringify(carry ?? null)}</div>
      <div data-testid="login-form-in-app">{String(inAppBrowser)}</div>
    </>
  ),
}));

// The page reads the visitor's user agent from the request headers (the in-app browser check).
const requestHeaders = vi.hoisted(() => ({ userAgent: '' }));
vi.mock('next/headers', () => ({
  headers: async () => ({ get: (name: string) => (name.toLowerCase() === 'user-agent' ? requestHeaders.userAgent : null) }),
}));

import AuthLayout from '../layout';
import LoginPage from './page';

Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (q: string) => ({
    matches: false,
    media: q,
    addEventListener() {},
    removeEventListener() {},
    addListener() {},
    removeListener() {},
    dispatchEvent: () => false,
    onchange: null,
  }),
});

async function renderLogin(params: Record<string, string>) {
  const ui = await LoginPage({ searchParams: Promise.resolve(params) });
  return render(<MantineProvider>{ui}</MantineProvider>);
}

// The same page as a visitor gets it: through the auth layout that carries the alpha notice.
async function renderLoginInLayout(params: Record<string, string>) {
  const ui = await LoginPage({ searchParams: Promise.resolve(params) });
  return render(
    <MantineProvider>
      <AuthLayout>{ui}</AuthLayout>
    </MantineProvider>,
  );
}

afterEach(cleanup);

describe('login page: account deletion', () => {
  it('names the deletion date and how to cancel it', async () => {
    await renderLogin({ deletion: 'scheduled', date: '2026-11-10T12:00:00Z' });
    const notice = screen.getByTestId('deletion-scheduled-notice');
    expect(notice.textContent).toContain('scheduled for deletion');
    expect(notice.textContent).toContain('10 November 2026');
    expect(notice.textContent).toContain('sign in before then');
  });

  it('still explains the grace period when no date came with it', async () => {
    await renderLogin({ deletion: 'scheduled' });
    expect(screen.getByTestId('deletion-scheduled-notice').textContent).toContain('in 30 days');
  });

  it('never shows a value that is not a date', async () => {
    await renderLogin({ deletion: 'scheduled', date: '<script>' });
    const notice = screen.getByTestId('deletion-scheduled-notice');
    expect(notice.textContent).not.toContain('<script>');
    expect(notice.textContent).toContain('in 30 days');
  });

  it('shows nothing about a deletion on an ordinary visit', async () => {
    await renderLogin({});
    expect(screen.queryByTestId('deletion-scheduled-notice')).toBeNull();
  });

  it('explains a Google/GitHub deletion the sole-owner rule refused', async () => {
    await renderLogin({ error: 'oauth_delete_blocked' });
    expect(screen.getByRole('alert').textContent).toContain('was not deleted');
  });
});

describe('login page carries the alpha notice (through the auth layout)', () => {
  beforeEach(() => window.sessionStorage.clear());

  it('shows the pinned notice on the sign-in page', async () => {
    await renderLoginInLayout({});
    expect(screen.getByTestId('alpha-banner').textContent).toContain(
      'ReelBolt 0.4.0-alpha — public alpha. Expect rough edges; things may change.',
    );
  });
});

/**
 * The page validates the untrusted `?next=` with safeNextRedirect and hands the form only the
 * surviving value. A root-level path (the OAuth consent URL) must reach the form unchanged; a
 * hostile one must be dropped, never passed on as-is.
 */
describe('login page: the return path', () => {
  it('passes a validated root-level ?next through to the form unchanged', async () => {
    await renderLogin({ next: '/oauth/authorize?client_id=app&scope=assistant.tools' });
    expect(screen.getByTestId('login-form-next').textContent).toBe(
      '/oauth/authorize?client_id=app&scope=assistant.tools',
    );
  });

  it('drops a hostile ?next rather than handing it to the form', async () => {
    await renderLogin({ next: '//evil.com' });
    expect(screen.getByTestId('login-form-next').textContent).toBe('(none)');
  });
});

/**
 * The page reads what the visitor's own link carried (`for`, `plan`, `utm_*`) and hands the form
 * only sanitized values, so the "Create account" link and the provider buttons can keep them.
 */
describe('login page: what the link carried', () => {
  const carry = () => JSON.parse(screen.getByTestId('login-form-carry').textContent ?? 'null');

  it('hands the sanitized persona, campaign parameters, plan and return path to the form', async () => {
    await renderLogin({
      for: 'Agency',
      plan: 'team',
      next: '/app/projects/1',
      utm_source: 'LinkedIn',
      utm_campaign: 'launch-2026-11',
      utm_content: 'li-post',
    });
    expect(carry()).toEqual({
      persona: 'agency',
      plan: 'team',
      next: '/app/projects/1',
      campaign: { source: 'linkedin', campaign: 'launch-2026-11', content: 'li-post' },
    });
  });

  it('drops an unknown persona, a bad campaign value and a hostile return path', async () => {
    await renderLogin({ for: 'hacker', utm_source: 'bad value!', next: '//evil.com', plan: '<b>' });
    expect(carry()).toEqual({ campaign: {} });
    expect(screen.getByTestId('login-form-next').textContent).toBe('(none)');
  });
});

// Google refuses to sign anyone in from an embedded webview (a link opened inside the Instagram,
// TikTok, Facebook or LinkedIn app). The page tells the form, which swaps the Google button for a
// sentence; an ordinary browser is never treated that way.
describe('login page: inside an in-app browser', () => {
  afterEach(() => {
    requestHeaders.userAgent = '';
  });

  it('tells the form when the visitor is inside another app, and only then', async () => {
    await renderLogin({});
    expect(screen.getByTestId('login-form-in-app').textContent).toBe('false');
    cleanup();
    requestHeaders.userAgent =
      'Mozilla/5.0 (Linux; Android 14; Pixel 8 Build/UQ1A.240205.002; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/130.0.6723.58 Mobile Safari/537.36 [FB_IAB/FB4A;FBAV/487.0.0.44.110;]';
    await renderLogin({});
    expect(screen.getByTestId('login-form-in-app').textContent).toBe('true');
    cleanup();
    requestHeaders.userAgent =
      'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.6723.58 Mobile Safari/537.36';
    await renderLogin({});
    expect(screen.getByTestId('login-form-in-app').textContent).toBe('false');
  });
});
