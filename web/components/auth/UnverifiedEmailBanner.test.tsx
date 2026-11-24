// @vitest-environment jsdom

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { UnverifiedEmailBanner } from './UnverifiedEmailBanner';

vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);
Element.prototype.scrollIntoView = vi.fn();

const auth = { user: null as null | { email: string }, emailVerified: true };
vi.mock('@/lib/hooks/use-auth', () => ({
  useAuth: () => ({ user: auth.user, emailVerified: auth.emailVerified }),
}));

const resendVerification = vi.fn();
vi.mock('@/lib/api/auth', () => ({ resendVerification: (data: unknown) => resendVerification(data) }));

vi.mock('./TurnstileWidget', () => ({
  TurnstileWidget: ({ siteKey, onToken }: { siteKey: string; onToken: (t: string) => void }) => (
    <button type="button" data-testid="ts-widget" data-site={siteKey} onClick={() => onToken('tok-1')}>
      solve
    </button>
  ),
}));

const renderBanner = (turnstileSiteKey?: string) =>
  render(
    <MantineProvider>
      <UnverifiedEmailBanner turnstileSiteKey={turnstileSiteKey} />
    </MantineProvider>,
  );

describe('UnverifiedEmailBanner', () => {
  beforeEach(() => {
    resendVerification.mockReset();
    auth.user = null;
    auth.emailVerified = true;
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

  it('stays hidden while signed out', () => {
    renderBanner();
    expect(screen.queryByText(/Confirm your email address/i)).toBeNull();
  });

  it('stays hidden for a verified account', () => {
    auth.user = { email: 'ada@example.com' };
    auth.emailVerified = true;
    renderBanner();
    expect(screen.queryByText(/Confirm your email address/i)).toBeNull();
  });

  it('nags an unverified account', () => {
    auth.user = { email: 'ada@example.com' };
    auth.emailVerified = false;
    renderBanner();
    expect(screen.getByText(/Confirm your email address/i)).toBeTruthy();
    expect(screen.getByText(/ada@example.com/)).toBeTruthy();
  });

  it('resends the link for the signed-in address', async () => {
    auth.user = { email: 'ada@example.com' };
    auth.emailVerified = false;
    resendVerification.mockResolvedValue({ message: 'ok' });
    renderBanner();
    fireEvent.click(screen.getByRole('button', { name: /Resend the email/i }));

    await waitFor(() => expect(resendVerification).toHaveBeenCalledWith({ email: 'ada@example.com' }));
    await waitFor(() => expect(screen.getByText(/check your inbox/i)).toBeTruthy());
  });

  it('keeps the nag and explains itself when the resend fails', async () => {
    auth.user = { email: 'ada@example.com' };
    auth.emailVerified = false;
    resendVerification.mockRejectedValue(new Error('service unavailable'));
    renderBanner();
    fireEvent.click(screen.getByRole('button', { name: /Resend the email/i }));

    await waitFor(() => expect(screen.getByText(/could not send it just now/i)).toBeTruthy());
  });

  it('without a site key shows no widget and sends no token', async () => {
    auth.user = { email: 'ada@example.com' };
    auth.emailVerified = false;
    resendVerification.mockResolvedValue({ message: 'ok' });
    renderBanner('');
    expect(screen.queryByTestId('ts-widget')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: /Resend the email/i }));
    await waitFor(() => expect(resendVerification).toHaveBeenCalledWith({ email: 'ada@example.com' }));
  });

  it('with a site key disables resend until solved, then sends the token and resets the widget', async () => {
    auth.user = { email: 'ada@example.com' };
    auth.emailVerified = false;
    resendVerification.mockResolvedValue({ message: 'ok' });
    renderBanner('site-key');
    const resend = screen.getByRole('button', { name: /Resend the email/i }) as HTMLButtonElement;
    expect(resend.disabled).toBe(true);
    expect(screen.getByTestId('ts-widget').getAttribute('data-site')).toBe('site-key');
    fireEvent.click(screen.getByTestId('ts-widget'));
    await waitFor(() => expect(resend.disabled).toBe(false));
    fireEvent.click(resend);
    await waitFor(() =>
      expect(resendVerification).toHaveBeenCalledWith({ email: 'ada@example.com', turnstileToken: 'tok-1' }),
    );
    await waitFor(() => expect(screen.getByText(/check your inbox/i)).toBeTruthy());
    // Token consumed: "Send it again" is disabled until the fresh widget is solved.
    const again = screen.getByRole('button', { name: /Send it again/i }) as HTMLButtonElement;
    expect(again.disabled).toBe(true);
  });
});
