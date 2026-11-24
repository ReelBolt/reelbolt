// @vitest-environment jsdom

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { VerifyEmailStatus } from './VerifyEmailStatus';

const push = vi.fn();
vi.mock('next/navigation', () => ({ useRouter: () => ({ push }) }));
const notifyAuthChange = vi.fn();
vi.mock('@/lib/hooks/use-auth', () => ({ notifyAuthChange: () => notifyAuthChange() }));

const verifyEmail = vi.fn();
const resendVerification = vi.fn();
vi.mock('@/lib/api/auth', () => ({
  verifyEmail: (data: unknown) => verifyEmail(data),
  resendVerification: (data: unknown) => resendVerification(data),
}));

vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);
Element.prototype.scrollIntoView = vi.fn();

const renderStatus = (token: string, next?: string) =>
  render(
    <MantineProvider>
      <VerifyEmailStatus token={token} next={next} />
    </MantineProvider>,
  );

describe('VerifyEmailStatus', () => {
  beforeEach(() => {
    verifyEmail.mockReset();
    resendVerification.mockReset();
    push.mockReset();
    notifyAuthChange.mockReset();
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

  it('redeems the token once, then signs the visitor in and continues to the dashboard', async () => {
    verifyEmail.mockResolvedValue({ accessToken: 'jwt' });
    renderStatus('tok-123');

    await waitFor(() => expect(push).toHaveBeenCalledWith('/dashboard'));
    expect(verifyEmail).toHaveBeenCalledTimes(1);
    expect(verifyEmail).toHaveBeenCalledWith({ token: 'tok-123' });
    expect(notifyAuthChange).toHaveBeenCalledTimes(1);
  });

  // The dashboard stays the target for a new account: the app layout sends a new user on to the
  // welcome questions from there. Only a validated return path takes precedence, as at sign-in.
  it('honours a validated app return path through the router, with the basePath stripped', async () => {
    verifyEmail.mockResolvedValue({ accessToken: 'jwt' });
    renderStatus('tok-123', '/app/projects/123');

    await waitFor(() => expect(push).toHaveBeenCalledWith('/projects/123'));
    expect(push).not.toHaveBeenCalledWith('/dashboard');
    expect(notifyAuthChange).toHaveBeenCalledTimes(1);
  });

  it('leaves for a root-level return path with a full-document navigation', async () => {
    const assign = vi.fn();
    Object.defineProperty(window, 'location', {
      configurable: true,
      value: { ...window.location, assign },
    });
    verifyEmail.mockResolvedValue({ accessToken: 'jwt' });
    renderStatus('tok-123', '/oauth/authorize?client_id=app');

    await waitFor(() => expect(assign).toHaveBeenCalledWith('/oauth/authorize?client_id=app'));
    expect(push).not.toHaveBeenCalled();
  });

  it('keeps the return path on the sign-in link when the token cannot be used', async () => {
    verifyEmail.mockRejectedValue(new Error('invalid or expired token'));
    renderStatus('tok-123', '/app/projects/123');

    const link = await screen.findByRole('link', { name: 'Go to sign in' });
    expect(link.getAttribute('href')).toBe('/login?next=%2Fapp%2Fprojects%2F123');
    expect(push).not.toHaveBeenCalled();
  });

  it('shows the generic failure the API returns for a used, expired or unknown token', async () => {
    verifyEmail.mockRejectedValue(new Error('invalid or expired token'));
    renderStatus('tok-123');

    await waitFor(() =>
      expect(
        screen.getByText('This verification link cannot be used: This link has expired or was already used. Send a new link to continue.'),
      ).toBeTruthy(),
    );
    expect(push).not.toHaveBeenCalled();
  });

  it('does not blame the link when the request itself failed, and still offers a new link', async () => {
    verifyEmail.mockRejectedValue(new TypeError('Failed to fetch'));
    renderStatus('tok-123');

    await screen.findByTestId('verify-resend');
    expect(document.body.textContent).not.toMatch(/cannot be used/);
    expect(screen.getByRole('button', { name: 'Send me a new link' })).toBeTruthy();
  });

  it('does not call the API when the link arrived without a token', () => {
    renderStatus('');

    expect(verifyEmail).not.toHaveBeenCalled();
    expect(screen.getByText(/missing its token/i)).toBeTruthy();
  });

  /**
   * A link opened after its 24 hours (or a second time) used to be a dead end: the only way on was
   * to sign in with a password typed once and find the resend in a banner. The page now asks for
   * the address and sends a new link through the public resend endpoint.
   */
  describe('a link that cannot be used', () => {
    const failed = async () => {
      verifyEmail.mockRejectedValue(new Error('invalid or expired token'));
      renderStatus('tok-old');
      return screen.findByTestId('verify-resend');
    };

    it('offers a new link right there: the address, one button', async () => {
      const form = await failed();
      expect(form.textContent).toContain('Need a new link?');
      resendVerification.mockResolvedValue({ message: 'ok' });
      fireEvent.change(screen.getByLabelText('Email'), { target: { value: ' ada@example.com ' } });
      fireEvent.click(screen.getByRole('button', { name: 'Send me a new link' }));
      await waitFor(() => expect(resendVerification).toHaveBeenCalledWith({ email: 'ada@example.com' }));
      // The answer is the same whether or not anything was sent: no account-existence signal.
      const status = await screen.findByText(/If that address is waiting to be confirmed, a new link is on its way/);
      expect(status.textContent).not.toMatch(/account|sent to|registered/i);
      expect(push).not.toHaveBeenCalled();
    });

    it('is offered for a link that arrived without its token too', () => {
      renderStatus('');
      expect(screen.getByTestId('verify-resend')).toBeTruthy();
      expect(screen.getByRole('button', { name: 'Send me a new link' })).toBeTruthy();
    });

    it('asks for an address before sending anything', async () => {
      await failed();
      fireEvent.click(screen.getByRole('button', { name: 'Send me a new link' }));
      expect(screen.getByText('Email is required')).toBeTruthy();
      expect(resendVerification).not.toHaveBeenCalled();
    });

    it('says how long to wait when too many emails were asked for, and plainly when it failed', async () => {
      const { ApiError } = await import('@/lib/api/client');
      await failed();
      const tooMany = new ApiError('slow down', 429);
      tooMany.retryAfterSeconds = 1200;
      resendVerification.mockRejectedValue(tooMany);
      fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'ada@example.com' } });
      fireEvent.click(screen.getByRole('button', { name: 'Send me a new link' }));
      expect(await screen.findByText(/Too many emails were requested\. Please try again in/)).toBeTruthy();
      resendVerification.mockRejectedValue(new Error('offline'));
      fireEvent.click(screen.getByRole('button', { name: 'Send me a new link' }));
      expect(await screen.findByText(/could not be sent again right now/)).toBeTruthy();
    });

    it('waits for the challenge when one is configured, and sends its token', async () => {
      verifyEmail.mockRejectedValue(new Error('invalid or expired token'));
      let solve: (token: string) => void = () => {};
      (window as unknown as { turnstile?: unknown }).turnstile = {
        render: (_el: HTMLElement, options: { callback: (token: string) => void }) => {
          solve = options.callback;
          return 'w1';
        },
        remove: () => {},
      };
      render(
        <MantineProvider>
          <VerifyEmailStatus token="tok-old" turnstileSiteKey="site-key" />
        </MantineProvider>,
      );
      await screen.findByTestId('verify-resend');
      const button = screen.getByRole('button', { name: 'Send me a new link' }) as HTMLButtonElement;
      expect(button.disabled).toBe(true);
      await waitFor(() => expect(solve).not.toBe(undefined));
      await waitFor(() => {
        solve('challenge-token');
        expect(button.disabled).toBe(false);
      });
      resendVerification.mockResolvedValue({ message: 'ok' });
      fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'ada@example.com' } });
      fireEvent.click(button);
      await waitFor(() => expect(resendVerification).toHaveBeenCalledWith({ email: 'ada@example.com', turnstileToken: 'challenge-token' }));
      delete (window as unknown as { turnstile?: unknown }).turnstile;
    });
  });
});
