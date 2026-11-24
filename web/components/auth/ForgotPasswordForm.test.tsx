// @vitest-environment jsdom

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ForgotPasswordForm } from './ForgotPasswordForm';

const forgotPassword = vi.fn();
vi.mock('@/lib/api/auth', () => ({ forgotPassword: (data: unknown) => forgotPassword(data) }));

vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);
Element.prototype.scrollIntoView = vi.fn();

const renderForm = (turnstileSiteKey = '') =>
  render(
    <MantineProvider>
      <ForgotPasswordForm turnstileSiteKey={turnstileSiteKey} />
    </MantineProvider>,
  );

const submit = (email = 'ada@example.com') => {
  fireEvent.change(screen.getByLabelText('Email'), { target: { value: email } });
  fireEvent.click(screen.getByRole('button', { name: 'Send reset link' }));
};

describe('ForgotPasswordForm', () => {
  beforeEach(() => {
    forgotPassword.mockReset();
    delete (window as unknown as { turnstile?: unknown }).turnstile;
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

  it('shows the uniform 202 message and never confirms that the address exists', async () => {
    forgotPassword.mockResolvedValue({ message: 'If this address can be used, an email is on its way.' });
    renderForm();
    submit();

    await waitFor(() => expect(screen.getByRole('status').textContent).toContain('an email is on its way'));
    expect(document.body.textContent).not.toMatch(/no account|unknown email|not registered|does not exist/i);
  });

  it('sends the turnstile token when a challenge is configured', async () => {
    forgotPassword.mockResolvedValue({ message: 'ok' });
    const challengeRendered = vi.fn();
    (window as unknown as { turnstile?: unknown }).turnstile = {
      render: (_el: HTMLElement, options: { callback: (token: string) => void }) => {
        challengeRendered();
        options.callback('turnstile-token');
        return 'widget-1';
      },
      remove: () => {},
    };
    renderForm('site-key');
    await waitFor(() => expect(challengeRendered).toHaveBeenCalled());
    submit();

    await waitFor(() => expect(forgotPassword).toHaveBeenCalledTimes(1));
    expect(forgotPassword.mock.calls[0][0]).toEqual({
      email: 'ada@example.com',
      turnstileToken: 'turnstile-token',
    });
  });

  it('does not submit a configured challenge without a token', async () => {
    (window as unknown as { turnstile?: unknown }).turnstile = { render: () => 'widget-1', remove: () => {} };
    renderForm('site-key');
    submit();

    await waitFor(() => expect(screen.getByText('Please complete the challenge below.')).toBeTruthy());
    expect(forgotPassword).not.toHaveBeenCalled();
  });
});
