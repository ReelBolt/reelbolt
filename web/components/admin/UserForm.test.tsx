// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { AdminUser } from '@/lib/types/admin';

const H = vi.hoisted(() => ({
  update: vi.fn(),
  request: vi.fn(),
  cancel: vi.fn(),
}));

vi.mock('@/lib/api/admin', () => ({
  createUser: vi.fn(),
  updateUser: (...a: unknown[]) => H.update(...a),
  requestEmailChange: (...a: unknown[]) => H.request(...a),
  cancelEmailChange: (...a: unknown[]) => H.cancel(...a),
}));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));

import { UserForm } from './UserForm';

vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);
Element.prototype.scrollIntoView = vi.fn();

const base: AdminUser = {
  id: 'u1',
  email: 'old@example.com',
  displayName: 'Old',
  isAdmin: false,
  mustChangePassword: false,
  createdAt: '2026-01-01T00:00:00Z',
  emailVerified: true,
  pendingEmailChange: null,
};

const renderForm = (user: AdminUser) =>
  render(
    <MantineProvider>
      <UserForm opened onClose={() => {}} onSuccess={() => {}} user={user} />
    </MantineProvider>,
  );

beforeEach(() => {
  H.update.mockReset().mockResolvedValue(base);
  H.request.mockReset();
  H.cancel.mockReset().mockResolvedValue(undefined);
  window.matchMedia = ((q: string) => ({
    matches: false,
    media: q,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
});
afterEach(cleanup);

describe('UserForm edit mode', () => {
  it('shows the email read-only and sends the stored value unchanged', async () => {
    renderForm(base);
    const input = screen.getByTestId('user-email-readonly') as HTMLInputElement;
    expect(input.readOnly).toBe(true);
    expect(input.value).toBe('old@example.com');
    expect(screen.getByText('Only the user can confirm an email change')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Update' }));
    await waitFor(() => expect(H.update).toHaveBeenCalled());
    expect(H.update.mock.calls[0][1]).toMatchObject({ email: 'old@example.com' });
  });

  it('shows a pending alert and cancels it with DELETE', async () => {
    renderForm({
      ...base,
      pendingEmailChange: { newEmail: 'next@example.com', requestedAt: '2030-01-01T00:00:00Z', expiresAt: '2030-01-02T00:00:00Z' },
    });
    expect(screen.getByTestId('pending-email-alert').textContent).toContain('next@example.com');
    fireEvent.click(screen.getByTestId('cancel-email-change'));
    await waitFor(() => expect(H.cancel).toHaveBeenCalledWith('u1'));
    await waitFor(() => expect(screen.queryByTestId('pending-email-alert')).toBeNull());
    expect(screen.getByTestId('open-change-email')).toBeTruthy();
  });

  it('the change modal posts the new address and shows it pending', async () => {
    H.request.mockResolvedValue({ pendingEmail: 'new@example.com', expiresAt: '2030-01-02T00:00:00Z' });
    renderForm(base);
    fireEvent.click(screen.getByTestId('open-change-email'));
    const input = await screen.findByTestId('change-email-input');
    fireEvent.change(input, { target: { value: 'new@example.com' } });
    fireEvent.click(screen.getByTestId('change-email-submit'));
    await waitFor(() => expect(H.request).toHaveBeenCalledWith('u1', 'new@example.com'));
    await waitFor(() => expect(screen.getByTestId('pending-email-alert').textContent).toContain('new@example.com'));
  });

  it('follows a new user prop (refresh, expiry, confirm)', () => {
    const pendingUser: AdminUser = {
      ...base,
      pendingEmailChange: { newEmail: 'next@example.com', requestedAt: '2030-01-01T00:00:00Z', expiresAt: '2030-01-02T00:00:00Z' },
    };
    const { rerender } = renderForm(base);
    expect(screen.queryByTestId('pending-email-alert')).toBeNull();
    const wrap = (u: AdminUser) => (
      <MantineProvider>
        <UserForm opened onClose={() => {}} onSuccess={() => {}} user={u} />
      </MantineProvider>
    );
    rerender(wrap(pendingUser));
    expect(screen.getByTestId('pending-email-alert').textContent).toContain('next@example.com');
    rerender(wrap({ ...base, pendingEmailChange: null }));
    expect(screen.queryByTestId('pending-email-alert')).toBeNull();
  });
});
