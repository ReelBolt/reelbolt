// @vitest-environment jsdom

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ResetPasswordForm } from './ResetPasswordForm';

const resetPassword = vi.fn();
vi.mock('@/lib/api/auth', () => ({ resetPassword: (data: unknown) => resetPassword(data) }));

vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);
Element.prototype.scrollIntoView = vi.fn();

const TOKEN = 'tok-123-abcdefghijklmnopqrstuvwxyz';
const renderForm = (token = TOKEN) =>
  render(
    <MantineProvider>
      <ResetPasswordForm token={token} />
    </MantineProvider>,
  );

const fill = (password = 'correct horse battery', confirm = password) => {
  fireEvent.change(screen.getByLabelText('New password'), { target: { value: password } });
  fireEvent.change(screen.getByLabelText('Confirm new password'), { target: { value: confirm } });
};

describe('ResetPasswordForm', () => {
  beforeEach(() => {
    resetPassword.mockReset();
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

  it('sends the token and the new password', async () => {
    resetPassword.mockResolvedValue({ message: 'password updated; sign in with your new password' });
    renderForm();
    fill();
    fireEvent.click(screen.getByRole('button', { name: 'Set new password' }));

    await waitFor(() => expect(resetPassword).toHaveBeenCalledTimes(1));
    expect(resetPassword).toHaveBeenCalledWith({ token: TOKEN, newPassword: 'correct horse battery' });
    await waitFor(() => expect(screen.getByRole('status').textContent).toMatch(/password updated/i));
  });

  it('does not sign the visitor in: the API kills every session instead', async () => {
    resetPassword.mockResolvedValue({ message: 'password updated; sign in with your new password' });
    renderForm();
    fill();
    fireEvent.click(screen.getByRole('button', { name: 'Set new password' }));

    await waitFor(() => expect(screen.getByRole('link', { name: 'Sign in' })).toBeTruthy());
  });

  it('reports an expired or already-used token without a form to retry it', async () => {
    resetPassword.mockRejectedValue(new Error('invalid or expired token'));
    renderForm();
    fill();
    fireEvent.click(screen.getByRole('button', { name: 'Set new password' }));

    await waitFor(() => expect(screen.getByText(/link has expired/i)).toBeTruthy());
  });

  it('blocks a password shorter than the API floor instead of burning the token', async () => {
    renderForm();
    fill('short', 'short');
    fireEvent.click(screen.getByRole('button', { name: 'Set new password' }));

    await waitFor(() => expect(screen.getByText('Password must be at least 10 characters')).toBeTruthy());
    expect(resetPassword).not.toHaveBeenCalled();
  });

  it('never renders the form without a token', () => {
    renderForm('');
    expect(screen.queryByRole('button', { name: 'Set new password' })).toBeNull();
    expect(screen.getByText(/link has expired/i)).toBeTruthy();
  });

  it('says up front that an obviously bad token is expired, with a way to send a new link', () => {
    renderForm('bogus');
    expect(screen.queryByRole('button', { name: 'Set new password' })).toBeNull();
    expect(screen.getByText(/link has expired/i)).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Send a new link' })).toBeTruthy();
    expect(resetPassword).not.toHaveBeenCalled();
  });
});
