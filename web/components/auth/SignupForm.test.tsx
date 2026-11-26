// @vitest-environment jsdom

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SignupForm } from './SignupForm';
import { ApiError } from '@/lib/api/client';
import { runSignupCapture } from '@/components/analytics/SignupTracker';
import { encodeFirstTouch } from '@/lib/attribution';
import { SIGNUP_INTENT_KEY, readSignupIntent } from '@/lib/auth/signup-intent';
import { mirrorStorageKey, parseMirror } from '@/lib/onboarding/state';
import { userKeyFromEmail } from '@/lib/telemetry/identity';

const push = vi.fn();
vi.mock('next/navigation', () => ({ useRouter: () => ({ push }) }));
vi.mock('@/lib/hooks/use-auth', () => ({ notifyAuthChange: vi.fn() }));

const signup = vi.fn();
const resend = vi.fn();
vi.mock('@/lib/api/auth', () => ({
  signup: (data: unknown) => signup(data),
  resendVerification: (data: unknown) => resend(data),
}));

// The registry's current wording (GET /api/v1/marketing/wording), stubbed so the form never fetches.
const WORDING = {
  version: '2026-10-marketing-v1',
  text: 'Email me ReelBolt product news, tips and offers. You can unsubscribe at any time from any email or in Settings. See our Privacy Policy.',
};
const M = vi.hoisted(() => ({
  wording: { data: undefined as { version: string; text: string } | undefined, mutate: vi.fn() },
}));
vi.mock('@/lib/hooks/use-marketing', () => ({ useMarketingWording: () => M.wording }));

vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);
Element.prototype.scrollIntoView = vi.fn();

const renderForm = (props: Partial<React.ComponentProps<typeof SignupForm>> = {}) =>
  render(
    <MantineProvider>
      <SignupForm
        turnstileSiteKey=""
        termsVersion="2026-10"
        privacyVersion="2026-10"
        {...props}
      />
    </MantineProvider>,
  );

// The whole field set a visitor types into: there is no "type it again" box.
const fill = (over: Partial<Record<'email' | 'password', string>> = {}) => {
  fireEvent.change(screen.getByLabelText('Email'), { target: { value: over.email ?? 'ada@example.com' } });
  fireEvent.change(screen.getByLabelText('Password'), { target: { value: over.password ?? 'correct horse battery' } });
};

const acceptTerms = () =>
  fireEvent.click(screen.getByRole('checkbox', { name: /accept the/i }));

const submit = () => fireEvent.click(screen.getByRole('button', { name: 'Create account' }));

describe('SignupForm', () => {
  beforeEach(() => {
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
    signup.mockReset();
    push.mockReset();
    window.localStorage.clear();
    window.history.replaceState({}, '', '/');
    M.wording = { data: WORDING, mutate: vi.fn() };
    // The script tag is never fetched in jsdom; a stub is enough for the widget to render and
    // stays absent for the "no challenge configured" cases.
    delete (window as unknown as { turnstile?: unknown }).turnstile;
  });

  afterEach(cleanup);

  it('sends every field the Go API validates, with the configured document versions', async () => {
    signup.mockResolvedValue({ message: 'If this address can be used, an email is on its way.' });
    renderForm();
    fill();
    acceptTerms();
    submit();

    await waitFor(() => expect(signup).toHaveBeenCalledTimes(1));
    expect(signup.mock.calls[0][0]).toEqual({
      email: 'ada@example.com',
      password: 'correct horse battery',
      acceptTerms: true,
      termsVersion: '2026-10',
      privacyVersion: '2026-10',
      // The marketing box was left alone: no opt-in, and no wording version to vouch for.
      marketingOptIn: false,
    });
  });

  it('offers the marketing box unticked, separate from the terms box, with the registry text', () => {
    renderForm();
    const box = screen.getByTestId('marketing-optin-checkbox') as HTMLInputElement;
    expect(box.checked).toBe(false);
    expect(box.required).toBe(false);
    expect(screen.getByRole('checkbox', { name: /Email me ReelBolt product news/ })).toBe(box);
    expect(screen.getByRole('checkbox', { name: /accept the/i })).not.toBe(box);
    // The Privacy Policy mention in the wording is a live link.
    expect(screen.getAllByRole('link', { name: 'Privacy Policy' }).length).toBeGreaterThanOrEqual(2);
  });

  it('sends the opt-in with the wording version it showed when the box is ticked', async () => {
    signup.mockResolvedValue({ message: 'ok' });
    renderForm();
    fill();
    acceptTerms();
    fireEvent.click(screen.getByTestId('marketing-optin-checkbox'));
    submit();

    await waitFor(() => expect(signup).toHaveBeenCalledTimes(1));
    expect(signup.mock.calls[0][0]).toMatchObject({
      acceptTerms: true,
      marketingOptIn: true,
      marketingWordingVersion: '2026-10-marketing-v1',
    });
  });

  it('never makes the marketing box a condition of signing up', async () => {
    signup.mockResolvedValue({ message: 'ok' });
    renderForm();
    fill();
    acceptTerms();
    submit();

    await waitFor(() => expect(signup).toHaveBeenCalledTimes(1));
    expect(signup.mock.calls[0][0]).toMatchObject({ marketingOptIn: false });
    expect(signup.mock.calls[0][0]).not.toHaveProperty('marketingWordingVersion');
  });

  it('hides the box and still signs up when the wording cannot be read', async () => {
    M.wording = { data: undefined, mutate: vi.fn() };
    signup.mockResolvedValue({ message: 'ok' });
    renderForm();
    expect(screen.queryByTestId('marketing-optin-checkbox')).toBeNull();
    fill();
    acceptTerms();
    submit();

    await waitFor(() => expect(signup).toHaveBeenCalledTimes(1));
    expect(signup.mock.calls[0][0]).toMatchObject({ marketingOptIn: false });
  });

  it('clears the box and refetches the wording when the API says it went stale', async () => {
    signup.mockRejectedValueOnce(new ApiError('invalid_marketing_wording', 400, [], { error: 'invalid_marketing_wording' }));
    renderForm();
    fill();
    acceptTerms();
    fireEvent.click(screen.getByTestId('marketing-optin-checkbox'));
    submit();

    await waitFor(() => expect(screen.getByText(/marketing email option has been updated/)).toBeTruthy());
    expect((screen.getByTestId('marketing-optin-checkbox') as HTMLInputElement).checked).toBe(false);
    expect(M.wording.mutate).toHaveBeenCalled();
  });

  it('sends no turnstileToken when no site key is configured', async () => {
    signup.mockResolvedValue({ message: 'ok' });
    renderForm({ turnstileSiteKey: '' });
    expect(screen.queryByTestId('turnstile-widget')).toBeNull();
    fill();
    acceptTerms();
    submit();

    await waitFor(() => expect(signup).toHaveBeenCalledTimes(1));
    expect(signup.mock.calls[0][0]).not.toHaveProperty('turnstileToken');
  });

  it('sends the turnstileToken the widget produced when a site key is configured', async () => {
    signup.mockResolvedValue({ message: 'ok' });
    const challengeRendered = vi.fn();
    (window as unknown as { turnstile?: unknown }).turnstile = {
      render: (_el: HTMLElement, options: { callback: (token: string) => void }) => {
        challengeRendered();
        options.callback('turnstile-token');
        return 'widget-1';
      },
      remove: () => {},
    };
    renderForm({ turnstileSiteKey: 'site-key' });
    // The widget hands its token over in a promise callback, so wait for the challenge to render
    // before submitting — otherwise the click happens with the token still unset.
    await waitFor(() => expect(challengeRendered).toHaveBeenCalled());
    fill();
    acceptTerms();
    submit();

    await waitFor(() => expect(signup).toHaveBeenCalledTimes(1));
    expect(signup.mock.calls[0][0]).toMatchObject({ turnstileToken: 'turnstile-token' });
  });

  it('refuses to submit a configured challenge that produced no token', async () => {
    // A rendered-but-unsolved challenge: every callback that could hand over a token expires or
    // errors instead, which must leave the form unable to submit (fail closed, like the server).
    (window as unknown as { turnstile?: unknown }).turnstile = {
      render: (_el: HTMLElement, options: { 'error-callback': () => void }) => {
        options['error-callback']();
        return 'widget-1';
      },
      remove: () => {},
    };
    renderForm({ turnstileSiteKey: 'site-key' });
    fill();
    acceptTerms();
    submit();

    await waitFor(() => expect(screen.getByText('Please complete the challenge below.')).toBeTruthy());
    expect(signup).not.toHaveBeenCalled();
  });

  it('shows the API message verbatim and never claims the account was created', async () => {
    signup.mockResolvedValue({ message: 'If this address can be used, an email is on its way.' });
    renderForm();
    fill();
    acceptTerms();
    submit();

    await waitFor(() => expect(screen.getByRole('status').textContent).toContain('an email is on its way'));
    // Account enumeration: the page must not distinguish "created" from "already registered".
    expect(document.body.textContent).not.toMatch(/already (exists|registered)/i);
    expect(document.body.textContent).not.toMatch(/account (created|was created)/i);
    expect(push).not.toHaveBeenCalled();
  });

  it('blocks a password the API would reject', async () => {
    renderForm();
    fill({ password: 'short' });
    acceptTerms();
    submit();

    await waitFor(() => expect(screen.getByText('Password must be at least 10 characters')).toBeTruthy());
    expect(signup).not.toHaveBeenCalled();
  });

  it('blocks submission until the terms are accepted', async () => {
    renderForm();
    fill();
    submit();

    await waitFor(() => expect(screen.getByText('Please accept the terms and the privacy policy')).toBeTruthy());
    expect(signup).not.toHaveBeenCalled();
  });

  // F-7. The API refuses a submission that does not name the version the deployment serves, and
  // answers with the versions that ARE current. The form adopts them and clears the consent box,
  // so the retry is a real re-acceptance of the new document rather than a replay.
  it('adopts the versions a 409 refusal carries and requires a fresh acceptance', async () => {
    signup.mockRejectedValueOnce(
      new ApiError('the terms have been updated since this page was loaded', 409, [], {
        currentTermsVersion: '2026-11',
        currentPrivacyVersion: '2026-11',
      }),
    );
    renderForm();
    fill();
    acceptTerms();
    submit();

    await waitFor(() => expect(screen.getByText(/Please review version 2026-11/)).toBeTruthy());
    expect((screen.getByRole('checkbox', { name: /accept the/i }) as HTMLInputElement).checked).toBe(false);

    signup.mockResolvedValue({ message: 'If this address can be used, an email is on its way.' });
    acceptTerms();
    submit();

    await waitFor(() => expect(signup).toHaveBeenCalledTimes(2));
    expect(signup.mock.calls[1][0]).toMatchObject({ termsVersion: '2026-11', privacyVersion: '2026-11' });
  });

  it('turns the 429 from the per-IP signup cap into friendly copy with the retry time and support contact', async () => {
    const err = new ApiError('too many sign-ups from this network; try again later', 429);
    err.retryAfterSeconds = 3600;
    signup.mockRejectedValue(err);
    renderForm();
    fill();
    acceptTerms();
    submit();

    await waitFor(() => expect(screen.getByText(/try again in about 1 hour/)).toBeTruthy());
    expect(document.body.textContent).toContain('support@reelbolt.ai');
  });

  it('Resend 429 shows the actual wait from Retry-After', async () => {
    signup.mockResolvedValue({ message: 'ok' });
    const err = new ApiError('slow down', 429);
    err.retryAfterSeconds = 1200;
    resend.mockRejectedValue(err);
    renderForm();
    fill();
    acceptTerms();
    submit();
    await waitFor(() => expect(screen.getByTestId('check-email-panel')).toBeTruthy());
    fireEvent.click(screen.getByRole('button', { name: 'Resend email' }));
    await waitFor(() => expect(screen.getByText(/try again in about 20 minutes/)).toBeTruthy());
  });

  it('replaces the form with a check-your-email panel that resends and changes the address', async () => {
    signup.mockResolvedValue({ message: 'If this address can be used, an email is on its way.' });
    resend.mockResolvedValue({ message: 'ok' });
    renderForm();
    fill();
    acceptTerms();
    submit();

    await waitFor(() => expect(screen.getByTestId('check-email-panel')).toBeTruthy());
    expect(screen.queryByRole('button', { name: 'Create account' })).toBeNull();
    expect(document.body.textContent).toContain('ada@example.com');
    expect(document.body.textContent).toMatch(/spam/i);
    expect(screen.getByRole('link', { name: 'Sign in' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Resend email' }));
    await waitFor(() => expect(resend).toHaveBeenCalledWith({ email: 'ada@example.com' }));
    await waitFor(() => expect((screen.getByRole('button', { name: 'Resend email' }) as HTMLButtonElement).disabled).toBe(true));

    fireEvent.click(screen.getByRole('button', { name: 'Change address' }));
    expect((screen.getByLabelText('Email') as HTMLInputElement).value).toBe('ada@example.com');
  });

  it('sends the Turnstile token with Resend when a site key is configured, and waits for one', async () => {
    signup.mockResolvedValue({ message: 'ok' });
    resend.mockResolvedValue({ message: 'ok' });
    // A minimal widget stub: render() hands back a token straight away on demand.
    let give: ((t: string) => void) | null = null;
    (window as unknown as { turnstile: unknown }).turnstile = {
      render: (_c: unknown, o: { callback: (t: string) => void }) => {
        give = o.callback;
        return 'w1';
      },
      remove: () => {},
    };
    renderForm({ turnstileSiteKey: 'site-key' });
    fill();
    acceptTerms();
    await waitFor(() => expect(give).not.toBeNull());
    act(() => give!('signup-token'));
    submit();
    await waitFor(() => expect(screen.getByTestId('check-email-panel')).toBeTruthy());
    await waitFor(() => expect((screen.getByRole('button', { name: 'Resend email' }) as HTMLButtonElement).disabled).toBe(true));
    await waitFor(() => expect(give).not.toBeNull());
    act(() => give!('resend-token'));
    await waitFor(() => expect((screen.getByRole('button', { name: 'Resend email' }) as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(screen.getByRole('button', { name: 'Resend email' }));
    await waitFor(() => expect(resend).toHaveBeenCalledWith({ email: 'ada@example.com', turnstileToken: 'resend-token' }));
  });

  it('signs the visitor straight in when the signup carried an invite token', async () => {
    signup.mockResolvedValue({ accessToken: 'jwt', mustChangePassword: false });
    renderForm({ inviteToken: 'invite-token', initialEmail: 'ada@example.com' });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'correct horse battery' } });
    acceptTerms();
    submit();

    await waitFor(() => expect(push).toHaveBeenCalledWith('/dashboard'));
    expect(signup.mock.calls[0][0]).toMatchObject({ inviteToken: 'invite-token' });
  });

  it('prefills the invited address', () => {
    renderForm({ initialEmail: 'invited@example.com' });
    expect((screen.getByLabelText('Email') as HTMLInputElement).value).toBe('invited@example.com');
  });
});

/**
 * Friction: one password field with a show/hide eye, no second "type it again" box. The
 * 10-character rule and the terms box are unchanged (pinned above).
 */
describe('SignupForm password field', () => {
  beforeEach(() => {
    signup.mockReset();
    M.wording = { data: WORDING, mutate: vi.fn() };
  });

  it('asks for the password once: email, one password, and nothing to retype', () => {
    renderForm();
    expect(screen.queryByLabelText('Confirm password')).toBeNull();
    expect(screen.queryByLabelText('Name')).toBeNull();
    expect(screen.queryByLabelText(/repeat|again|confirm/i)).toBeNull();
    const inputs = Array.from(document.querySelectorAll('input:not([type="checkbox"])')) as HTMLInputElement[];
    expect(inputs.map((i) => i.getAttribute('autocomplete'))).toEqual(['email', 'new-password']);
  });

  it('lets the visitor check what they typed with a keyboard-reachable show/hide toggle', () => {
    renderForm();
    const field = screen.getByLabelText('Password') as HTMLInputElement;
    expect(field.type).toBe('password');
    const toggle = screen.getByRole('button', { name: 'Show password' });
    expect(toggle.getAttribute('tabindex')).toBe('0');
    expect(toggle.getAttribute('aria-pressed')).toBe('false');
    fireEvent.mouseDown(toggle);
    expect((screen.getByLabelText('Password') as HTMLInputElement).type).toBe('text');
    expect(screen.getByRole('button', { name: 'Show password' }).getAttribute('aria-pressed')).toBe('true');
  });

  it('submits with the one password and no confirmation value', async () => {
    signup.mockResolvedValue({ message: 'ok' });
    renderForm();
    fill();
    acceptTerms();
    submit();
    await waitFor(() => expect(signup).toHaveBeenCalledTimes(1));
    expect(signup.mock.calls[0][0]).not.toHaveProperty('confirmPassword');
  });
});

/**
 * The persona and the campaign parameters of the visitor's own link travel in the signup request
 * (docs/design/new-user-funnel.md section 6): no cookie, so they survive the email round trip.
 */
describe('SignupForm carries the link through to the request and the sign-in links', () => {
  const CARRY = {
    persona: 'creator' as const,
    campaign: { source: 'hackernews', medium: 'community', campaign: 'launch-2026-11', content: 'hn-launch' },
    plan: 'creator',
    next: '/app/projects/123',
  };

  beforeEach(() => {
    signup.mockReset();
    push.mockReset();
    window.localStorage.clear();
    M.wording = { data: WORDING, mutate: vi.fn() };
    window.history.replaceState({}, '', '/signup?for=creator&utm_source=hackernews');
  });

  it('sends persona and attribution exactly as the contract names them', async () => {
    signup.mockResolvedValue({ message: 'ok' });
    renderForm({ carry: CARRY });
    fill();
    acceptTerms();
    submit();

    await waitFor(() => expect(signup).toHaveBeenCalledTimes(1));
    expect(signup.mock.calls[0][0]).toEqual({
      email: 'ada@example.com',
      password: 'correct horse battery',
      acceptTerms: true,
      termsVersion: '2026-10',
      privacyVersion: '2026-10',
      marketingOptIn: false,
      persona: 'creator',
      plan: 'creator',
      attribution: {
        source: 'hackernews',
        medium: 'community',
        campaign: 'launch-2026-11',
        content: 'hn-launch',
        // jsdom has no referrer: this document is where the visitor landed.
        landingPath: '/signup',
      },
    });
    // The return path is link state, not a signup field.
    expect(signup.mock.calls[0][0]).not.toHaveProperty('next');
  });

  it('sends the persona alone when the link had no campaign parameters, with no empty attribution', async () => {
    signup.mockResolvedValue({ message: 'ok' });
    renderForm({ carry: { persona: 'educator', campaign: {} } });
    fill();
    acceptTerms();
    submit();

    await waitFor(() => expect(signup).toHaveBeenCalledTimes(1));
    expect(signup.mock.calls[0][0]).toMatchObject({ persona: 'educator' });
    expect(signup.mock.calls[0][0]).not.toHaveProperty('attribution');
  });

  it('sends the sanitized plan, and drops a malformed one', async () => {
    signup.mockResolvedValue({ message: 'ok' });
    renderForm({ carry: { campaign: {}, plan: ' Pro ' } });
    fill();
    acceptTerms();
    submit();
    await waitFor(() => expect(signup).toHaveBeenCalledTimes(1));
    expect(signup.mock.calls[0][0]).toMatchObject({ plan: 'pro' });
    cleanup();
    signup.mockClear();
    renderForm({ carry: { campaign: {}, plan: '<script>' } });
    fill();
    acceptTerms();
    submit();
    await waitFor(() => expect(signup).toHaveBeenCalledTimes(1));
    expect(signup.mock.calls[0][0]).not.toHaveProperty('plan');
  });

  it('sends neither object for a plain link', async () => {
    signup.mockResolvedValue({ message: 'ok' });
    renderForm();
    fill();
    acceptTerms();
    submit();

    await waitFor(() => expect(signup).toHaveBeenCalledTimes(1));
    expect(signup.mock.calls[0][0]).not.toHaveProperty('persona');
    expect(signup.mock.calls[0][0]).not.toHaveProperty('plan');
    expect(signup.mock.calls[0][0]).not.toHaveProperty('attribution');
  });

  it('keeps for, utm, plan and next on the sign-in link, on the form and on the check-email panel', async () => {
    signup.mockResolvedValue({ message: 'ok' });
    renderForm({ carry: CARRY });
    const expected =
      '/login?for=creator&utm_source=hackernews&utm_medium=community&utm_campaign=launch-2026-11' +
      '&utm_content=hn-launch&plan=creator&next=%2Fapp%2Fprojects%2F123';
    expect(screen.getByRole('link', { name: 'Sign in' }).getAttribute('href')).toBe(expected);

    fill();
    acceptTerms();
    submit();
    await waitFor(() => expect(screen.getByTestId('check-email-panel')).toBeTruthy());
    expect(screen.getByRole('link', { name: 'Sign in' }).getAttribute('href')).toBe(expected);
  });

  it('links to the plain sign-in page when the link carried nothing', () => {
    renderForm();
    expect(screen.getByRole('link', { name: 'Sign in' }).getAttribute('href')).toBe('/login');
  });

  it('honours a validated return path after an invite signup, the way sign-in does', async () => {
    signup.mockResolvedValue({ accessToken: 'jwt', mustChangePassword: false });
    renderForm({ inviteToken: 'invite-token', initialEmail: 'ada@example.com', carry: CARRY });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'correct horse battery' } });
    acceptTerms();
    submit();

    // An app path goes through the router with the '/app' basePath stripped.
    await waitFor(() => expect(push).toHaveBeenCalledWith('/projects/123'));
    expect(push).not.toHaveBeenCalledWith('/dashboard');
  });
});

/**
 * An account set up from a team invitation opens in the Personal workspace every account gets,
 * which is empty. The dashboard's rule for the welcome questions counts projects there, so an
 * invited teammate was sent through "what are you here to make" and into a personal first project.
 */
describe('SignupForm: an invited teammate is not a welcome-flow candidate', () => {
  beforeEach(() => {
    signup.mockReset();
    push.mockReset();
    window.localStorage.clear();
    M.wording = { data: WORDING, mutate: vi.fn() };
  });
  afterEach(cleanup);

  const mirrorOf = (email: string) => parseMirror(window.localStorage.getItem(mirrorStorageKey(userKeyFromEmail(email))) ?? '');

  it('tells this browser so before the dashboard is reached', async () => {
    signup.mockResolvedValue({ accessToken: 'jwt', mustChangePassword: false });
    renderForm({ inviteToken: 'invite-token', initialEmail: 'Invited@Example.com' });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'correct horse battery' } });
    acceptTerms();
    submit();
    await waitFor(() => expect(push).toHaveBeenCalledWith('/dashboard'));
    // The mark that stops the redirect, under the same per-person key the dashboard reads.
    expect(mirrorOf('invited@example.com').welcome).toBe('offered');
  });

  it('leaves an ordinary sign-up alone: that person is who the questions are for', async () => {
    signup.mockResolvedValue({ message: 'If this address can be used, an email is on its way.' });
    renderForm();
    fill();
    acceptTerms();
    submit();
    await screen.findByTestId('check-email-panel');
    expect(mirrorOf('ada@example.com').welcome).toBeUndefined();
  });
});

/**
 * The plan a pricing card started and the page the visitor was on their way to. The emailed link
 * carries only its token, so both wait in this browser, bound to the address the email was sent to.
 */
describe('SignupForm keeps the plan and the return path for the emailed link', () => {
  beforeEach(() => {
    signup.mockReset();
    window.localStorage.clear();
    M.wording = { data: WORDING, mutate: vi.fn() };
  });
  afterEach(cleanup);

  it('remembers them once the sign-up was accepted, for that address only', async () => {
    signup.mockResolvedValue({ message: 'If this address can be used, an email is on its way.' });
    renderForm({ carry: { campaign: {}, plan: 'pro', next: '/app/marketplace/recipes/abc' } });
    fill();
    acceptTerms();
    expect(window.localStorage.getItem(SIGNUP_INTENT_KEY)).toBeNull();
    submit();
    await screen.findByTestId('check-email-panel');
    expect(readSignupIntent(userKeyFromEmail('ada@example.com'))).toEqual({ plan: 'pro', next: '/app/marketplace/recipes/abc' });
    // The plan goes to the API (kept on the server); the return path never does.
    expect(signup.mock.calls[0][0]).toMatchObject({ plan: 'pro' });
    expect(signup.mock.calls[0][0]).not.toHaveProperty('next');
  });

  it('remembers nothing for a refused sign-up or a plain link', async () => {
    signup.mockRejectedValue(new ApiError('nope', 400));
    renderForm({ carry: { campaign: {}, plan: 'pro' } });
    fill();
    acceptTerms();
    submit();
    await waitFor(() => expect(signup).toHaveBeenCalledTimes(1));
    expect(window.localStorage.getItem(SIGNUP_INTENT_KEY)).toBeNull();
    cleanup();
    signup.mockReset();
    signup.mockResolvedValue({ message: 'ok' });
    renderForm();
    fill();
    acceptTerms();
    submit();
    await screen.findByTestId('check-email-panel');
    expect(window.localStorage.getItem(SIGNUP_INTENT_KEY)).toBeNull();
  });
});

/**
 * "Change address" submits a second time from the same page. The API uses the first-touch cookie
 * once (the request that carries it clears it), so the corrected address, the one the person will
 * really use, went out with no first touch while the mistyped one kept it.
 */
describe('SignupForm: a corrected address keeps the first touch', () => {
  const FT = 'rb_ft';
  const cookie = () => document.cookie.split('; ').find((c) => c.startsWith(`${FT}=`))?.slice(FT.length + 1);
  const clearCookie = () => {
    document.cookie = `${FT}=; Max-Age=0; Path=/`;
  };
  const touch = encodeFirstTouch({ source: 'newsletter', content: 'nl-nov', visitorId: '2f0d5b7c-1a34-4c2e-9f10-8b6a1d2e3f40', ts: 1 })!;

  beforeEach(() => {
    signup.mockReset();
    signup.mockResolvedValue({ message: 'If this address can be used, an email is on its way.' });
    window.localStorage.clear();
    clearCookie();
    M.wording = { data: WORDING, mutate: vi.fn() };
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(null, { status: 204 }))));
  });
  afterEach(() => {
    cleanup();
    clearCookie();
    vi.unstubAllGlobals();
  });

  const submitThenChangeAddress = async () => {
    renderForm();
    fill({ email: 'ada@exmaple.com' });
    acceptTerms();
    submit();
    await screen.findByTestId('check-email-panel');
    // What the API's response does: the cookie it used is cleared.
    clearCookie();
    fireEvent.click(screen.getByRole('button', { name: 'Change address' }));
  };

  it('puts the cookie the page opened with back when the visitor goes back to the form', async () => {
    window.localStorage.setItem('rf-cookie-consent', 'granted');
    document.cookie = `${FT}=${touch}; Path=/`;
    runSignupCapture();
    await submitThenChangeAddress();
    expect(cookie()).toBe(touch);
  });

  it('never overwrites a cookie that is still there', async () => {
    window.localStorage.setItem('rf-cookie-consent', 'granted');
    document.cookie = `${FT}=${touch}; Path=/`;
    runSignupCapture();
    renderForm();
    fill();
    acceptTerms();
    submit();
    await screen.findByTestId('check-email-panel');
    document.cookie = `${FT}=someone-elses; Path=/`;
    fireEvent.click(screen.getByRole('button', { name: 'Change address' }));
    expect(cookie()).toBe('someone-elses');
  });

  it('writes nothing once consent is withdrawn', async () => {
    window.localStorage.setItem('rf-cookie-consent', 'granted');
    document.cookie = `${FT}=${touch}; Path=/`;
    runSignupCapture();
    window.localStorage.setItem('rf-cookie-consent', 'denied');
    await submitThenChangeAddress();
    expect(cookie()).toBeUndefined();
  });
});

/**
 * The check-email panel says what happens next, and still never says whether an account exists:
 * the same sentences are shown for a new address and for a known one.
 */
describe('SignupForm check-email panel', () => {
  beforeEach(() => {
    signup.mockReset();
    M.wording = { data: WORDING, mutate: vi.fn() };
  });

  it('says what happens after the link without claiming an account was created', async () => {
    signup.mockResolvedValue({ message: 'If this address can be used, an email is on its way.' });
    renderForm();
    fill();
    acceptTerms();
    submit();

    const panel = await screen.findByTestId('check-email-panel');
    expect(panel.textContent).toContain('once you confirm, two quick questions set up your first project');
    // Still conditional on the address being usable, still the API's own uniform sentence.
    expect(panel.textContent).toContain('If ada@example.com can be used');
    expect(screen.getByRole('status').textContent).toContain('an email is on its way');
    expect(panel.textContent).not.toMatch(/account (is |was |has been )?(created|ready)/i);
    expect(panel.textContent).not.toMatch(/already (exists|registered|have)/i);
    expect(panel.textContent).not.toMatch(/welcome/i);
  });

  // WELCOME_FLOW_ENABLED=false: nobody is asked the two questions, so the panel does not promise them.
  it('does not promise the welcome questions on a deployment where they are switched off', async () => {
    signup.mockResolvedValue({ message: 'If this address can be used, an email is on its way.' });
    renderForm({ welcomeEnabled: false });
    fill();
    acceptTerms();
    submit();

    const panel = await screen.findByTestId('check-email-panel');
    expect(panel.textContent).not.toMatch(/two quick questions/i);
    expect(panel.textContent).toContain('If ada@example.com can be used');
  });
});

/**
 * `signup.submitted` is a consented visitor event: a first-party beacon to the visit endpoint,
 * sent when the form passes its own checks. Nothing is read, stored or sent without the cookie
 * consent the marketing site records, and it never carries a form value.
 */
describe('SignupForm signup.submitted beacon', () => {
  const fetchMock = vi.fn(() => Promise.resolve(new Response(null)));

  beforeEach(() => {
    signup.mockReset();
    fetchMock.mockClear();
    vi.stubGlobal('fetch', fetchMock);
    window.localStorage.clear();
    document.cookie = 'rb_ft=; Max-Age=0; Path=/';
    M.wording = { data: WORDING, mutate: vi.fn() };
    window.history.replaceState({}, '', '/signup?utm_source=Newsletter');
  });

  const beacons = () =>
    fetchMock.mock.calls
      .filter((c) => (c as unknown[])[0] === '/api/v1/telemetry/visit')
      .map((c) => JSON.parse(((c as unknown[])[1] as RequestInit).body as string));

  it('sends nothing, and stores nothing, without consent', async () => {
    for (const consent of [null, 'denied']) {
      if (consent) window.localStorage.setItem('rf-cookie-consent', consent);
      signup.mockResolvedValue({ message: 'ok' });
      const view = renderForm();
      fill();
      acceptTerms();
      submit();
      await waitFor(() => expect(signup).toHaveBeenCalled());
      expect(fetchMock).not.toHaveBeenCalled();
      expect(window.localStorage.getItem('rf-visitor-id')).toBeNull();
      view.unmount();
      signup.mockReset();
    }
  });

  it('sends signup.submitted with the visitor id and no form value once consent is granted', async () => {
    window.localStorage.setItem('rf-cookie-consent', 'granted');
    window.localStorage.setItem('rf-visitor-id', '11111111-1111-4111-8111-111111111111');
    signup.mockResolvedValue({ message: 'ok' });
    renderForm();
    fill();
    acceptTerms();
    submit();

    await waitFor(() => expect(signup).toHaveBeenCalledTimes(1));
    expect(beacons()).toEqual([
      {
        event: 'signup.submitted',
        visitorId: '11111111-1111-4111-8111-111111111111',
        source: 'newsletter',
        // The page the browser opened with the campaign link: no stored first touch says otherwise.
        landingPath: '/signup',
      },
    ]);
    const sent = JSON.stringify(beacons());
    expect(sent).not.toContain('ada@example.com');
    expect(sent).not.toContain('Ada');
    expect(sent).not.toContain('correct horse');
  });

  it('does not fire when the form is refused by its own checks', async () => {
    window.localStorage.setItem('rf-cookie-consent', 'granted');
    renderForm();
    fill({ password: 'short' });
    acceptTerms();
    submit();
    await waitFor(() => expect(screen.getByText('Password must be at least 10 characters')).toBeTruthy());

    cleanup();
    renderForm();
    fill();
    submit(); // terms not accepted
    await waitFor(() => expect(screen.getByText('Please accept the terms and the privacy policy')).toBeTruthy());

    expect(signup).not.toHaveBeenCalled();
    expect(beacons()).toEqual([]);
  });

  it('does not fire for an unsolved challenge (the form fails closed before sending)', async () => {
    window.localStorage.setItem('rf-cookie-consent', 'granted');
    (window as unknown as { turnstile?: unknown }).turnstile = {
      render: (_el: HTMLElement, options: { 'error-callback': () => void }) => {
        options['error-callback']();
        return 'widget-1';
      },
      remove: () => {},
    };
    renderForm({ turnstileSiteKey: 'site-key' });
    fill();
    acceptTerms();
    submit();
    await waitFor(() => expect(screen.getByText('Please complete the challenge below.')).toBeTruthy());
    expect(beacons()).toEqual([]);
    delete (window as unknown as { turnstile?: unknown }).turnstile;
  });

  it('still signs up when the beacon cannot be sent', async () => {
    window.localStorage.setItem('rf-cookie-consent', 'granted');
    vi.stubGlobal('fetch', vi.fn(() => { throw new Error('blocked'); }));
    signup.mockResolvedValue({ message: 'ok' });
    renderForm();
    fill();
    acceptTerms();
    submit();
    await waitFor(() => expect(screen.getByTestId('check-email-panel')).toBeTruthy());
  });
});
