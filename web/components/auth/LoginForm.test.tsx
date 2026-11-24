// @vitest-environment jsdom

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { LoginForm } from './LoginForm';

const push = vi.fn();
vi.mock('next/navigation', () => ({ useRouter: () => ({ push }) }));
vi.mock('@/lib/hooks/use-auth', () => ({ notifyAuthChange: vi.fn() }));

const login = vi.fn();
vi.mock('@/lib/api/auth', () => ({ login: (data: unknown) => login(data) }));

vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);
Element.prototype.scrollIntoView = vi.fn();

const renderForm = (signupEnabled?: boolean, next?: string) =>
  render(
    <MantineProvider>
      <LoginForm signupEnabled={signupEnabled} next={next} />
    </MantineProvider>,
  );

const submit = async () => {
  fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'ada@example.com' } });
  fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'correct horse battery' } });
  fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
};

describe('LoginForm', () => {
  beforeEach(() => {
    login.mockReset();
    push.mockReset();
    // jsdom has no real navigation; the full-document branch calls window.location.assign.
    Object.defineProperty(window, 'location', {
      configurable: true,
      value: { ...window.location, assign: vi.fn(), replace: vi.fn() },
    });
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

  it('always offers a password reset, since admin-created users need one too', () => {
    renderForm(false);
    expect(screen.getByRole('link', { name: 'Forgot password?' })).toBeTruthy();
  });

  it('hides "Create account" when the deployment takes no self-serve signups', () => {
    renderForm(false);
    expect(screen.queryByRole('link', { name: 'Create account' })).toBeNull();
  });

  it('offers "Create account" when signups are enabled', () => {
    renderForm(true);
    expect(screen.getByRole('link', { name: 'Create account' })).toBeTruthy();
  });

  it('links "Create account" to the plain signup page when the link carried nothing', () => {
    renderForm(true);
    expect(screen.getByRole('link', { name: 'Create account' }).getAttribute('href')).toBe('/signup');
  });

  it('keeps for, utm, plan and next on the "Create account" link and for/utm on the provider buttons', () => {
    render(
      <MantineProvider>
        <LoginForm
          signupEnabled
          oauthProviders={['google']}
          next="/app/projects/123"
          carry={{ persona: 'creator', campaign: { source: 'reddit', content: 'r-videoediting' }, plan: 'creator' }}
        />
      </MantineProvider>,
    );
    expect(screen.getByRole('link', { name: 'Create account' }).getAttribute('href')).toBe(
      '/signup?for=creator&utm_source=reddit&utm_content=r-videoediting&plan=creator&next=%2Fapp%2Fprojects%2F123',
    );
    expect(screen.getByRole('link', { name: 'Continue with Google' }).getAttribute('href')).toBe(
      '/api/v1/auth/oauth/google/start?for=creator&utm_source=reddit&utm_content=r-videoediting',
    );
  });

  // A provider sign-in leaves the app and comes back through the API, which hands back no return
  // path. The page's own validated `next` is kept in this browser for the way back.
  it('keeps the return path for the way back when a provider button is pressed', () => {
    window.localStorage.clear();
    render(
      <MantineProvider>
        <LoginForm oauthProviders={['github']} next="/oauth/authorize?client_id=app" />
      </MantineProvider>,
    );
    fireEvent.click(screen.getByRole('link', { name: 'Continue with GitHub' }));
    expect(JSON.parse(window.localStorage.getItem('rb-signup-intent:v1') ?? '{}').next).toBe('/oauth/authorize?client_id=app');
    window.localStorage.clear();
  });

  it('swaps the Google button for a note inside an in-app browser, and keeps the password form', () => {
    render(
      <MantineProvider>
        <LoginForm oauthProviders={['google', 'github']} inAppBrowser />
      </MantineProvider>,
    );
    expect(screen.queryByRole('link', { name: 'Continue with Google' })).toBeNull();
    expect(screen.getByTestId('oauth-in-app-note')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Continue with GitHub' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeTruthy();
  });

  it('sends none of the carried values with a sign-in', async () => {
    login.mockResolvedValue({ accessToken: 'jwt', mustChangePassword: false });
    render(
      <MantineProvider>
        <LoginForm signupEnabled carry={{ persona: 'creator', campaign: { source: 'reddit' }, plan: 'creator' }} />
      </MantineProvider>,
    );
    await submit();
    await waitFor(() => expect(login).toHaveBeenCalledTimes(1));
    expect(login.mock.calls[0][0]).toEqual({ email: 'ada@example.com', password: 'correct horse battery' });
  });

  it('still logs in', async () => {
    login.mockResolvedValue({ accessToken: 'jwt', mustChangePassword: false });
    renderForm(true);
    await submit();

    await waitFor(() => expect(push).toHaveBeenCalledWith('/dashboard'));
    expect(window.location.assign).not.toHaveBeenCalled();
  });

  it('reaches a root-level ?next with a full-document navigation, not the basePath-aware router', async () => {
    login.mockResolvedValue({ accessToken: 'jwt', mustChangePassword: false });
    renderForm(true, '/oauth/authorize?client_id=app&scope=assistant.tools');
    await submit();

    await waitFor(() =>
      expect(window.location.assign).toHaveBeenCalledWith(
        '/oauth/authorize?client_id=app&scope=assistant.tools',
      ),
    );
    // The router would prepend '/app' and 404 on '/app/oauth/authorize'.
    expect(push).not.toHaveBeenCalled();
  });

  it('still sends an app-relative ?next through the router, with the basePath stripped', async () => {
    login.mockResolvedValue({ accessToken: 'jwt', mustChangePassword: false });
    renderForm(true, '/app/projects/123/workflows');
    await submit();

    await waitFor(() => expect(push).toHaveBeenCalledWith('/projects/123/workflows'));
    expect(window.location.assign).not.toHaveBeenCalled();
  });

  it('does not let ?next bypass the forced password change, but carries it to the gate', async () => {
    login.mockResolvedValue({ accessToken: 'jwt', mustChangePassword: true });
    renderForm(true, '/oauth/authorize?client_id=app&scope=assistant.tools');
    await submit();

    // The gate still wins — the change is mandatory — and the validated path rides along so the
    // user reaches the consent screen once the change is done.
    await waitFor(() =>
      expect(push).toHaveBeenCalledWith(
        '/change-password?next=%2Foauth%2Fauthorize%3Fclient_id%3Dapp%26scope%3Dassistant.tools',
      ),
    );
    expect(push).not.toHaveBeenCalledWith('/oauth/authorize?client_id=app&scope=assistant.tools');
    expect(window.location.assign).not.toHaveBeenCalled();
  });

  it('sends a forced password change to the gate with no ?next when none was given', async () => {
    login.mockResolvedValue({ accessToken: 'jwt', mustChangePassword: true });
    renderForm(true);
    await submit();

    await waitFor(() => expect(push).toHaveBeenCalledWith('/change-password'));
    expect(window.location.assign).not.toHaveBeenCalled();
  });

  it('ties invalid credentials to the password field with a reset link, and clears it on edit', async () => {
    login.mockRejectedValue(new Error('invalid credentials'));
    renderForm(false);
    await submit();

    expect(await screen.findByText(/email and password do not match/i)).toBeTruthy();
    expect(screen.getAllByRole('link', { name: 'Forgot password?' }).length).toBe(1);
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'another-try' } });
    await waitFor(() => expect(screen.queryByText(/email and password do not match/i)).toBeNull());
  });
});
