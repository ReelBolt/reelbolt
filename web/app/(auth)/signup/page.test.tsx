// @vitest-environment jsdom

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { authPageConfig } from '@/lib/auth/config';
import { resetOfferedOAuthCache } from '@/lib/auth/oauth-server';
import AuthLayout from '../layout';
import SignupPage from './page';

vi.mock('next/navigation', () => ({ useRouter: () => ({ push: vi.fn() }) }));
// The page reads the visitor's user agent from the request headers (the in-app browser check).
const requestHeaders = vi.hoisted(() => ({ userAgent: '' }));
vi.mock('next/headers', () => ({
  headers: async () => ({ get: (name: string) => (name.toLowerCase() === 'user-agent' ? requestHeaders.userAgent : null) }),
}));
vi.mock('@/lib/hooks/use-auth', () => ({ notifyAuthChange: vi.fn() }));
vi.mock('@/lib/api/auth', () => ({ signup: vi.fn() }));
const firstVideo = vi.hoisted(() => ({ offered: false }));
vi.mock('@/lib/auth/first-video-server', () => ({ firstVideoGrantOffered: async () => firstVideo.offered }));

vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);
Element.prototype.scrollIntoView = vi.fn();

// The one pinned alpha sentence (em dash U+2014), the same on every surface.
const PINNED_NOTICE =
  'ReelBolt 0.4.0-alpha — public alpha. Expect rough edges; things may change.';

const renderPage = async (params: Record<string, string | string[] | undefined> = {}) => {
  const page = await SignupPage({ searchParams: Promise.resolve(params) });
  return render(<MantineProvider>{page}</MantineProvider>);
};

// Render through the real auth layout, which is what a visitor to /app/signup actually gets.
const renderPageInLayout = async (params: Record<string, string | string[] | undefined> = {}) => {
  const page = await SignupPage({ searchParams: Promise.resolve(params) });
  return render(
    <MantineProvider>
      <AuthLayout>{page}</AuthLayout>
    </MantineProvider>,
  );
};

describe('SignupPage', () => {
  beforeEach(() => {
    delete process.env.SIGNUP_ENABLED;
    delete process.env.NEXT_PUBLIC_SIGNUP_ENABLED;
    window.sessionStorage.clear();
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
    delete process.env.NEXT_PUBLIC_SIGNUP_ENABLED;
  });

  // Review Focus 1: with signup disabled the page must render the documented refusal state, never a
  // form that posts into the API's 404. The page already does this (page.tsx:63-87) off the flag in
  // lib/auth/config.ts:36 — this pins it so a future edit cannot quietly bring the form back.
  it('offers no signup form on an invite-only instance (the API answers 404 there)', async () => {
    await renderPage();

    expect(screen.queryByRole('button', { name: 'Create account' })).toBeNull();
    expect(screen.getByText(/does not take self-serve sign-ups/i)).toBeTruthy();
  });

  it('renders the form once the instance enables signups', async () => {
    process.env.SIGNUP_ENABLED = 'true';
    await renderPage();

    expect(screen.getByRole('button', { name: 'Create account' })).toBeTruthy();
  });

  it('prefills the invited address and carries the invite token through', async () => {
    process.env.SIGNUP_ENABLED = 'true';
    await renderPage({ email: 'invited@example.com', invite: 'invite-token' });

    expect((screen.getByLabelText('Email') as HTMLInputElement).value).toBe('invited@example.com');
    expect(screen.getByText(/join the team/i)).toBeTruthy();
  });
});

describe('SignupPage carries the alpha notice (through the auth layout)', () => {
  beforeEach(() => {
    window.sessionStorage.clear();
  });
  afterEach(() => {
    cleanup();
    delete process.env.SIGNUP_ENABLED;
    delete process.env.NEXT_PUBLIC_SIGNUP_ENABLED;
  });

  it('shows the pinned notice on the signup page', async () => {
    process.env.SIGNUP_ENABLED = 'true';
    await renderPageInLayout();
    expect(screen.getByTestId('alpha-banner').textContent).toContain(PINNED_NOTICE);
  });
});

/**
 * The new-user funnel's signup surface (docs/design/new-user-funnel.md section 3): a value panel
 * beside the form, written for the audience the visitor's link named, with the form first in the
 * document so it is first on a phone.
 */
describe('SignupPage value panel and layout', () => {
  beforeEach(() => {
    requestHeaders.userAgent = '';
    delete process.env.WELCOME_FLOW_ENABLED;
    process.env.SIGNUP_ENABLED = 'true';
    delete process.env.OAUTH_GOOGLE_ENABLED;
    delete process.env.OAUTH_GITHUB_ENABLED;
    resetOfferedOAuthCache();
    window.sessionStorage.clear();
    window.localStorage.clear();
  });
  afterEach(() => {
    cleanup();
    requestHeaders.userAgent = '';
    delete process.env.WELCOME_FLOW_ENABLED;
    delete process.env.SIGNUP_ENABLED;
    delete process.env.OAUTH_GOOGLE_ENABLED;
    delete process.env.OAUTH_GITHUB_ENABLED;
    resetOfferedOAuthCache();
  });

  const panel = () => screen.getByTestId('signup-value-panel');

  it('makes no first-video promise unless the grant is offered', async () => {
    firstVideo.offered = false;
    await renderPage();
    expect(screen.getByText('Create your free account. No card required.')).toBeTruthy();
    expect(document.body.textContent).not.toMatch(/first video is on us/i);
  });

  it('promises the first video in the subtitle and the trust list when the grant is offered', async () => {
    firstVideo.offered = true;
    try {
      await renderPage();
      expect(screen.getByText('Create your free account. No card required. Your first video is on us.')).toBeTruthy();
      expect(within(screen.getByTestId('signup-value-trust')).getAllByRole('listitem')).toHaveLength(3);
    } finally {
      firstVideo.offered = false;
    }
  });

  it('leads with the outcome, not with "create an account"', async () => {
    await renderPage();
    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Your story, made into a video');
    expect(screen.getByText('Create your free account. No card required.')).toBeTruthy();
    expect(screen.queryByText('Start with a free workspace')).toBeNull();
  });

  it('shows the general story when the link names no audience', async () => {
    await renderPage();
    expect(panel().getAttribute('data-audience')).toBe('general');
    expect(within(panel()).getByRole('heading', { level: 2 }).textContent).toBe(
      'You have a story. ReelBolt makes the video.',
    );
  });

  it('speaks to the audience the link named with ?for=', async () => {
    await renderPage({ for: 'educator' });
    expect(panel().getAttribute('data-audience')).toBe('educator');
    expect(within(panel()).getByRole('heading', { level: 2 }).textContent).toBe(
      'Short clips made from the lessons you already teach',
    );
    expect(screen.getByTestId('signup-value-before').textContent).toContain('long recording');
    expect(screen.getByTestId('signup-value-after').textContent).toContain('subtitles');
  });

  // The public educator page says a one-hour lecture cannot be turned into short clips on its own
  // and that nothing about who finishes a video is measured. The panel must not promise either.
  it('does not promise educators a long recording turned into clips, or clips people finish', async () => {
    await renderPage({ for: 'educator' });
    const text = panel().textContent ?? '';
    expect(text).not.toMatch(/finish/i);
    expect(text).not.toMatch(/captioned/i);
    expect(text).not.toMatch(/hour-long/i);
    expect(screen.getByTestId('signup-value-after').textContent).toContain('A few short takes');
  });

  it('ignores an unknown, repeated or "other" persona and shows the general story', async () => {
    for (const value of ['astronaut', '<script>', 'other', ['creator', 'agency']]) {
      await renderPage({ for: value });
      expect(panel().getAttribute('data-audience')).toBe('general');
      cleanup();
    }
  });

  it('says what happens next in three steps, in order', async () => {
    await renderPage();
    const steps = within(screen.getByTestId('signup-value-next')).getAllByRole('listitem');
    expect(steps).toHaveLength(3);
    expect(steps[0].textContent).toContain('Confirm your email');
    expect(steps[1].textContent).toContain('Tell us what you are making');
    expect(steps[2].textContent).toContain('first cut');
    // A provider sign-up sends no email, and the emailed link does not last for ever: both are said.
    expect(steps[0].textContent).toContain('If you sign up with email');
    expect(steps[0].textContent).toContain('within a day');
  });

  // WELCOME_FLOW_ENABLED=false: nobody is asked the two questions, so the page does not promise them.
  it('does not promise the welcome questions on a deployment where they are switched off', async () => {
    process.env.WELCOME_FLOW_ENABLED = 'false';
    await renderPage();
    const steps = within(screen.getByTestId('signup-value-next')).getAllByRole('listitem');
    expect(steps).toHaveLength(2);
    expect(steps[0].textContent).toContain('Confirm your email');
    expect(steps[1].textContent).toContain('first cut');
    expect(panel().textContent).not.toMatch(/two quick questions/i);
  });

  it('puts the form before the panel in the document, so a phone shows the form first', async () => {
    await renderPage();
    const form = document.querySelector('form')!;
    expect(form.compareDocumentPosition(panel()) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    // One h1 (the card), then the panel's h2: the heading order follows the document.
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1);
  });

  it('keeps the pinned alpha notice with the panel on the page', async () => {
    await renderPageInLayout({ for: 'creator' });
    expect(screen.getByTestId('alpha-banner').textContent).toContain(PINNED_NOTICE);
    expect(panel()).toBeTruthy();
  });

  it('shows no panel to an invited teammate, whose next steps are different', async () => {
    await renderPage({ email: 'invited@example.com', invite: 'invite-token', for: 'creator' });
    expect(screen.queryByTestId('signup-value-panel')).toBeNull();
    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Create your account');
  });

  it('shows no panel on an instance that takes no sign-ups', async () => {
    delete process.env.SIGNUP_ENABLED;
    await renderPage({ for: 'creator' });
    expect(screen.queryByTestId('signup-value-panel')).toBeNull();
    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Create your account');
  });

  // The page offers provider sign-in first, above the form, and its links carry what the
  // visitor's link carried (the API being unreachable here, the env flags decide the buttons).
  it('offers provider sign-in above the form, carrying the persona and the campaign parameters', async () => {
    process.env.OAUTH_GOOGLE_ENABLED = 'true';
    resetOfferedOAuthCache();
    await renderPage({ for: 'creator', utm_source: 'HackerNews', utm_content: 'hn-launch', plan: 'creator' });

    const google = screen.getByRole('link', { name: 'Continue with Google' });
    expect(google.getAttribute('href')).toBe(
      '/api/v1/auth/oauth/google/start?for=creator&utm_source=hackernews&utm_content=hn-launch',
    );
    const form = document.querySelector('form')!;
    expect(google.compareDocumentPosition(form) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(screen.getByText('or sign up with email')).toBeTruthy();
  });

  // Google refuses to sign anyone in from an embedded webview, which is where the Instagram,
  // TikTok, Facebook and LinkedIn apps open a posted link. There the form leads, the Google button
  // gives way to a sentence, and GitHub (which works there) stays.
  it('inside an in-app browser: the email form first, no Google button, a note instead', async () => {
    process.env.OAUTH_GOOGLE_ENABLED = 'true';
    process.env.OAUTH_GITHUB_ENABLED = 'true';
    resetOfferedOAuthCache();
    requestHeaders.userAgent =
      'Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 Instagram 355.0.0.31.105 (iPhone15,2; iOS 18_1; en_US; en; scale=3.00; 1179x2556; 654321)';
    await renderPage({ for: 'creator' });

    expect(screen.queryByRole('link', { name: 'Continue with Google' })).toBeNull();
    const note = screen.getByTestId('oauth-in-app-note');
    expect(note.textContent).toContain('Open this page in your usual browser');
    const github = screen.getByRole('link', { name: 'Continue with GitHub' });
    const form = document.querySelector('form')!;
    expect(form.compareDocumentPosition(github) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(form.compareDocumentPosition(note) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(screen.queryByText('or sign up with email')).toBeNull();
  });

  it('in an ordinary mobile browser the provider buttons stay first, Google included', async () => {
    process.env.OAUTH_GOOGLE_ENABLED = 'true';
    resetOfferedOAuthCache();
    requestHeaders.userAgent =
      'Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Mobile/15E148 Safari/604.1';
    await renderPage();
    const google = screen.getByRole('link', { name: 'Continue with Google' });
    const form = document.querySelector('form')!;
    expect(google.compareDocumentPosition(form) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(screen.queryByTestId('oauth-in-app-note')).toBeNull();
  });

  it('keeps for, utm and plan on the link to the sign-in page', async () => {
    await renderPage({ for: 'creator', utm_source: 'HackerNews', plan: 'creator', next: '/app/projects/9' });
    expect(screen.getByRole('link', { name: 'Sign in' }).getAttribute('href')).toBe(
      '/login?for=creator&utm_source=hackernews&plan=creator&next=%2Fapp%2Fprojects%2F9',
    );
  });
});

/**
 * Review Focus 1, the invariant half: the site and the API must read the signup switch from names
 * that match — the site's build-time switch is the `NEXT_PUBLIC_`-prefixed form of the API's — and
 * the web runtime flag must read both spellings. Read from the three sources directly (never from
 * `process.cwd()`), so a rename on any one surface fails here rather than shipping a CTA the API
 * 404s. This asserts the NAMES only: the values are the alpha environment's `.env` (gate row A6),
 * not something this ticket sets.
 */
describe('the signup switch is read from matching names on every surface', () => {
  const source = (rel: string): string =>
    readFileSync(fileURLToPath(new URL(rel, import.meta.url)), 'utf8');

  const apiConfig = source('../../../../api/config/config.go');
  const siteConfig = source('../../../../site/lib/site-config.ts');
  const webConfig = source('../../../../web/lib/auth/config.ts');

  it('the API switch and the site build switch are the same name, one public', () => {
    const apiName = apiConfig.match(/SignupEnabled:\s*getBoolEnv\(\s*"([A-Z0-9_]+)"/)?.[1];
    const siteName = siteConfig.match(/process\.env\.(NEXT_PUBLIC_[A-Z0-9_]+)/)?.[1];

    // Both must be found, or the comparisons below would pass vacuously.
    expect(apiName).toBeTruthy();
    expect(siteName).toBeTruthy();
    expect(apiName).toBe('SIGNUP_ENABLED');
    expect(siteName).toBe(`NEXT_PUBLIC_${apiName}`);
  });

  it('the web runtime flag reads both spellings the other two surfaces use', () => {
    expect(webConfig).toContain("env.SIGNUP_ENABLED === 'true'");
    expect(webConfig).toContain("env.NEXT_PUBLIC_SIGNUP_ENABLED === 'true'");
  });

  it('either spelling flips the web signup flag, and neither is set here', () => {
    expect(authPageConfig({ SIGNUP_ENABLED: 'true' }).signupEnabled).toBe(true);
    expect(authPageConfig({ NEXT_PUBLIC_SIGNUP_ENABLED: 'true' }).signupEnabled).toBe(true);
    // Nothing set means closed: the default is unchanged by this ticket.
    expect(authPageConfig({}).signupEnabled).toBe(false);
  });
});
