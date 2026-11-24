// @vitest-environment jsdom

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ChangePasswordForm } from './ChangePasswordForm';

const push = vi.fn();
vi.mock('next/navigation', () => ({ useRouter: () => ({ push }) }));
vi.mock('@/lib/hooks/use-auth', () => ({ notifyAuthChange: vi.fn() }));

const changePassword = vi.fn();
vi.mock('@/lib/api/auth', () => ({ changePassword: (data: unknown) => changePassword(data) }));

vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);
Element.prototype.scrollIntoView = vi.fn();

const renderForm = (props: { redirectTo?: string; next?: string } = {}) =>
  render(
    <MantineProvider>
      <ChangePasswordForm {...props} />
    </MantineProvider>,
  );

const submit = async () => {
  fireEvent.change(screen.getByLabelText('Current password'), {
    target: { value: 'old password' },
  });
  fireEvent.change(screen.getByLabelText('New password'), {
    target: { value: 'correct horse battery staple' },
  });
  fireEvent.change(screen.getByLabelText('Confirm new password'), {
    target: { value: 'correct horse battery staple' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Change password' }));
};

describe('ChangePasswordForm', () => {
  beforeEach(() => {
    changePassword.mockReset();
    changePassword.mockResolvedValue(undefined);
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

  it('reaches a root-level ?next with a full-document navigation, not the basePath-aware router', async () => {
    renderForm({ next: '/oauth/authorize?client_id=app' });
    await submit();

    await waitFor(() =>
      expect(window.location.assign).toHaveBeenCalledWith('/oauth/authorize?client_id=app'),
    );
    expect(push).not.toHaveBeenCalled();
  });

  it('sends an app-relative ?next through the router, with the basePath stripped', async () => {
    renderForm({ next: '/app/projects/123' });
    await submit();

    await waitFor(() => expect(push).toHaveBeenCalledWith('/projects/123'));
    expect(window.location.assign).not.toHaveBeenCalled();
  });

  it('falls back to the dashboard after a forced change with no ?next', async () => {
    renderForm();
    await submit();

    await waitFor(() => expect(push).toHaveBeenCalledWith('/dashboard'));
    expect(window.location.assign).not.toHaveBeenCalled();
  });

  it('keeps the caller redirectTo for a voluntary change (the settings page)', async () => {
    renderForm({ redirectTo: '/settings' });
    await submit();

    await waitFor(() => expect(push).toHaveBeenCalledWith('/settings'));
    expect(window.location.assign).not.toHaveBeenCalled();
  });

  it('puts a wrong current password on its own field, in a plain sentence, and clears it on edit', async () => {
    changePassword.mockRejectedValue(new Error('current password is incorrect'));
    renderForm();
    await submit();

    expect(await screen.findByText('Your current password is not correct. Check it and try again.')).toBeTruthy();
    expect(push).not.toHaveBeenCalled();
    fireEvent.change(screen.getByLabelText('Current password'), { target: { value: 'other' } });
    await waitFor(() => expect(screen.queryByText(/current password is not correct/i)).toBeNull());
  });
});
