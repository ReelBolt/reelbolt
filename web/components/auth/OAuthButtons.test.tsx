// @vitest-environment jsdom

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { CONSENT_STORAGE_KEY } from '@/lib/analytics/consent';
import { SIGNUP_INTENT_KEY } from '@/lib/auth/signup-intent';
import { OAuthButtons } from './OAuthButtons';
import { oauthErrorNotice, oauthProviderLabel } from '@/lib/auth/oauth';

vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);

const renderButtons = (providers: string[]) =>
  render(
    <MantineProvider>
      <OAuthButtons providers={providers} />
    </MantineProvider>,
  );

describe('OAuthButtons', () => {
  beforeEach(() => {
    // Mantine's color-scheme provider reads matchMedia, which jsdom does not implement.
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

  afterEach(cleanup);

  it('renders nothing when no provider is configured', () => {
    const { container } = renderButtons([]);
    expect(container.querySelectorAll('a')).toHaveLength(0);
  });

  it('links each provider at the Go API start endpoint', () => {
    renderButtons(['google', 'github']);
    const links = screen.getAllByRole('link');
    expect(links.map((a) => a.getAttribute('href'))).toEqual([
      '/api/v1/auth/oauth/google/start',
      '/api/v1/auth/oauth/github/start',
    ]);
    // A plain root-relative anchor: a next/link href would get the '/app' basePath prepended.
    expect(screen.getByText('Continue with Google')).toBeTruthy();
    expect(screen.getByText('Continue with GitHub')).toBeTruthy();
  });

  // The persona and the campaign parameters have no request body to travel in on this path: the
  // Go start endpoint reads them from the query and packs them into the OAuth state.
  it('carries the persona and the campaign parameters as for and utm_* on the start endpoint', () => {
    render(
      <MantineProvider>
        <OAuthButtons
          providers={['google', 'github']}
          carry={{
            persona: 'software',
            campaign: { source: 'hackernews', medium: 'community', campaign: 'launch-2026-11', content: 'hn-launch', term: 'ai+video' },
            plan: 'pro',
            next: '/app/projects/1',
          }}
        />
      </MantineProvider>,
    );
    const query =
      '?for=software&utm_source=hackernews&utm_medium=community&utm_campaign=launch-2026-11&utm_content=hn-launch&utm_term=ai%2Bvideo';
    expect(screen.getAllByRole('link').map((a) => a.getAttribute('href'))).toEqual([
      `/api/v1/auth/oauth/google/start${query}`,
      `/api/v1/auth/oauth/github/start${query}`,
    ]);
  });

  it('adds no query when the link carried nothing', () => {
    render(
      <MantineProvider>
        <OAuthButtons providers={['google']} carry={{ campaign: {} }} />
      </MantineProvider>,
    );
    expect(screen.getByRole('link').getAttribute('href')).toBe('/api/v1/auth/oauth/google/start');
  });

  /**
   * A provider sign-up has no form to submit: pressing the button IS asking for an account. Without
   * a beacon the visitor step "Submitted signup" counted only people who chose a password, and
   * everyone who signed up with Google or GitHub read as having left the signup page.
   */
  describe('the signup.submitted visitor event', () => {
    const beacons = () =>
      (fetchMock.mock.calls as [string, RequestInit][])
        .filter(([url]) => url === '/api/v1/telemetry/visit')
        .map(([, init]) => ({ body: JSON.parse(String(init.body)), keepalive: init.keepalive }));
    let fetchMock: ReturnType<typeof vi.fn>;

    beforeEach(() => {
      window.localStorage.clear();
      fetchMock = vi.fn(() => Promise.resolve(new Response(null, { status: 204 })));
      vi.stubGlobal('fetch', fetchMock);
    });
    afterEach(() => {
      vi.unstubAllGlobals();
      window.localStorage.clear();
    });

    const renderSignup = (props: { signup?: boolean } = { signup: true }) =>
      render(
        <MantineProvider>
          <OAuthButtons providers={['google', 'github']} carry={{ campaign: { source: 'newsletter', content: 'nl-nov' } }} {...props} />
        </MantineProvider>,
      );

    it('is sent, with keepalive, when a consenting visitor presses a provider button on the signup page', () => {
      window.localStorage.setItem(CONSENT_STORAGE_KEY, 'granted');
      renderSignup();
      fireEvent.click(screen.getByRole('link', { name: 'Continue with Google' }));
      expect(beacons()).toHaveLength(1);
      expect(beacons()[0].body.event).toBe('signup.submitted');
      expect(beacons()[0].body.visitorId).toMatch(/^[0-9a-f-]{36}$/);
      // The navigation to the provider follows at once: the request must outlive the page.
      expect(beacons()[0].keepalive).toBe(true);
      // The press does not swallow the navigation.
      expect(screen.getByRole('link', { name: 'Continue with Google' }).getAttribute('href')).toContain('/api/v1/auth/oauth/google/start');
    });

    it('is sent for GitHub too', () => {
      window.localStorage.setItem(CONSENT_STORAGE_KEY, 'granted');
      renderSignup();
      fireEvent.click(screen.getByRole('link', { name: 'Continue with GitHub' }));
      expect(beacons().map((b) => b.body.event)).toEqual(['signup.submitted']);
    });

    it('is never sent without cookie consent: nothing is read, stored or sent', () => {
      renderSignup();
      fireEvent.click(screen.getByRole('link', { name: 'Continue with Google' }));
      window.localStorage.setItem(CONSENT_STORAGE_KEY, 'denied');
      fireEvent.click(screen.getByRole('link', { name: 'Continue with GitHub' }));
      expect(beacons()).toEqual([]);
      expect(window.localStorage.getItem('rf-visitor-id')).toBeNull();
    });

    it('is not sent from the sign-in page, where a press is mostly an existing account signing in', () => {
      window.localStorage.setItem(CONSENT_STORAGE_KEY, 'granted');
      renderSignup({ signup: false });
      fireEvent.click(screen.getByRole('link', { name: 'Continue with Google' }));
      expect(beacons()).toEqual([]);
    });
  });

  // The provider round trip cannot carry the plan a pricing card started or the page the visitor
  // was on their way to: a press keeps them in this browser for the way back.
  describe('the plan and the return path', () => {
    beforeEach(() => window.localStorage.clear());
    afterEach(() => window.localStorage.clear());

    it('are remembered when a provider button is pressed', () => {
      render(
        <MantineProvider>
          <OAuthButtons providers={['google']} carry={{ campaign: {}, plan: 'pro', next: '/app/marketplace/recipes/abc' }} />
        </MantineProvider>,
      );
      expect(window.localStorage.getItem(SIGNUP_INTENT_KEY)).toBeNull();
      fireEvent.click(screen.getByRole('link', { name: 'Continue with Google' }));
      expect(JSON.parse(window.localStorage.getItem(SIGNUP_INTENT_KEY) ?? '{}')).toMatchObject({
        plan: 'pro',
        next: '/app/marketplace/recipes/abc',
      });
    });

    it('write nothing when the link carried neither', () => {
      render(
        <MantineProvider>
          <OAuthButtons providers={['google']} carry={{ persona: 'creator', campaign: { source: 'hn' } }} />
        </MantineProvider>,
      );
      fireEvent.click(screen.getByRole('link', { name: 'Continue with Google' }));
      expect(window.localStorage.getItem(SIGNUP_INTENT_KEY)).toBeNull();
    });
  });

  // Google answers an error page to an embedded webview (the Instagram, TikTok, Facebook and
  // LinkedIn apps open posted links in one): no button that leads there, a sentence instead.
  describe('inside an in-app browser', () => {
    it('replaces the Google button with how to open the page in the real browser, and keeps GitHub', () => {
      render(
        <MantineProvider>
          <OAuthButtons providers={['google', 'github']} inAppBrowser />
        </MantineProvider>,
      );
      expect(screen.queryByRole('link', { name: 'Continue with Google' })).toBeNull();
      expect(screen.getByRole('link', { name: 'Continue with GitHub' })).toBeTruthy();
      const note = screen.getByTestId('oauth-in-app-note');
      expect(note.textContent).toContain('Google sign-in does not work inside this app');
      expect(note.textContent).toContain('or use your email here');
    });

    it('says nothing about Google where Google is not configured', () => {
      render(
        <MantineProvider>
          <OAuthButtons providers={['github']} inAppBrowser />
        </MantineProvider>,
      );
      expect(screen.queryByTestId('oauth-in-app-note')).toBeNull();
      expect(screen.getByRole('link', { name: 'Continue with GitHub' })).toBeTruthy();
    });

    it('offers every provider in an ordinary browser', () => {
      renderButtons(['google', 'github']);
      expect(screen.getAllByRole('link')).toHaveLength(2);
      expect(screen.queryByTestId('oauth-in-app-note')).toBeNull();
    });
  });

  it('falls back to a humanised label for an unknown provider id', () => {
    expect(oauthProviderLabel('gitlab')).toBe('Continue with gitlab');
  });
});

describe('oauthErrorNotice', () => {
  it('explains the codes the callback sends back', () => {
    expect(oauthErrorNotice('oauth_email_unverified')).toContain('verified email');
    expect(oauthErrorNotice('oauth_rate_limited')).toContain('Too many accounts');
    expect(oauthErrorNotice('oauth_failed')).toContain('could not be completed');
  });

  it('never renders an empty notice for an unrecognised code', () => {
    expect(oauthErrorNotice('something-new').length).toBeGreaterThan(0);
  });
});
