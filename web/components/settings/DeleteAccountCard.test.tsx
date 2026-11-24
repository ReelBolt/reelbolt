// @vitest-environment jsdom
/**
 * "Delete account".
 *
 * What these tests protect:
 *   - the red button stays disabled until the password is entered AND DELETE is typed;
 *   - a scheduled deletion leaves for the sign-in page, carrying the date it names;
 *   - each refusal is said in plain words: a wrong password, the sole-owner rule (naming the team and
 *     linking to Members), and the Google/GitHub confirmation, which hands over a button to continue;
 *   - nothing is sent while the button is not armed.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

const H = vi.hoisted(() => ({ del: vi.fn(), real: undefined as undefined | ((password: string) => Promise<unknown>) }));

vi.mock('@/lib/api/account', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api/account')>();
  H.real = actual.requestAccountDeletion;
  return { ...actual, requestAccountDeletion: (...args: unknown[]) => H.del(...args) };
});

import { DeleteAccountCard } from './DeleteAccountCard';
import { resetSessionExpiryForTests } from '@/lib/auth/session-expiry';

Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (q: string) => ({
    matches: false,
    media: q,
    addEventListener() {},
    removeEventListener() {},
    addListener() {},
    removeListener() {},
    dispatchEvent: () => false,
    onchange: null,
  }),
});
vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);

const renderCard = () =>
  render(
    <MantineProvider>
      <DeleteAccountCard />
    </MantineProvider>,
  );

function openModal() {
  fireEvent.click(screen.getByRole('button', { name: 'Delete account…' }));
}

function deleteButton() {
  return screen.getByRole('button', { name: 'Delete my account' }) as HTMLButtonElement;
}

function fillAndConfirm(password: string) {
  fireEvent.change(screen.getByLabelText(/Your password/), { target: { value: password } });
  fireEvent.change(screen.getByLabelText('Type DELETE to confirm'), { target: { value: 'DELETE' } });
}

beforeEach(() => {
  H.del.mockReset();
  Object.defineProperty(window, 'location', { configurable: true, value: { ...window.location, assign: vi.fn(), replace: vi.fn() } });
});

afterEach(cleanup);

describe('DeleteAccountCard', () => {
  it('explains the grace period and the billing step, with a link to Plan & billing', () => {
    renderCard();
    const card = screen.getByTestId('delete-account-card');
    expect(card.textContent).toContain('30 days');
    expect(screen.getByRole('link', { name: 'Plan & billing' }).getAttribute('href')).toBe('/settings/billing');
  });

  it('keeps the delete button disabled until the password is entered and DELETE is typed', async () => {
    renderCard();
    openModal();
    await screen.findByText('Delete your account?');
    expect(deleteButton().disabled).toBe(true);

    fireEvent.change(screen.getByLabelText(/Your password/), { target: { value: 'pw' } });
    expect(deleteButton().disabled).toBe(true);
    fireEvent.change(screen.getByLabelText('Type DELETE to confirm'), { target: { value: 'delete' } });
    expect(deleteButton().disabled).toBe(true);
    fireEvent.change(screen.getByLabelText('Type DELETE to confirm'), { target: { value: 'DELETE' } });
    expect(deleteButton().disabled).toBe(false);
    expect(H.del).not.toHaveBeenCalled();
  });

  it('goes to the sign-in page with the date once the deletion is scheduled', async () => {
    H.del.mockResolvedValue({ kind: 'scheduled', deletionDate: '2026-11-10T12:00:00Z', graceDays: 30 });
    renderCard();
    openModal();
    await screen.findByText('Delete your account?');
    fillAndConfirm('correct horse');
    fireEvent.click(deleteButton());

    await waitFor(() =>
      expect(window.location.replace).toHaveBeenCalledWith(
        '/app/login?deletion=scheduled&date=2026-11-10T12%3A00%3A00Z',
      ),
    );
    expect(H.del).toHaveBeenCalledWith('correct horse');
  });

  it('says so when the password is wrong, and stays open', async () => {
    H.del.mockResolvedValue({ kind: 'wrong_password' });
    renderCard();
    openModal();
    await screen.findByText('Delete your account?');
    fillAndConfirm('nope');
    fireEvent.click(deleteButton());

    expect((await screen.findByTestId('delete-account-error')).textContent).toContain("password isn't right");
    expect(window.location.replace).not.toHaveBeenCalled();
  });

  it('a 401 "password is incorrect" shows the inline error and is not treated as an expired session', async () => {
    resetSessionExpiryForTests();
    Object.defineProperty(window, 'location', {
      configurable: true,
      value: { pathname: '/app/settings', search: '', assign: vi.fn(), replace: vi.fn() },
    });
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(JSON.stringify({ error: 'password is incorrect' }), {
          status: 401,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    );
    H.del.mockImplementation((password: string) => H.real!(password));
    renderCard();
    openModal();
    await screen.findByText('Delete your account?');
    fillAndConfirm('nope');
    fireEvent.click(deleteButton());

    expect((await screen.findByTestId('delete-account-error')).textContent).toContain("password isn't right");
    expect(window.location.assign).not.toHaveBeenCalled();
    expect(window.location.replace).not.toHaveBeenCalled();
    vi.unstubAllGlobals();
  });

  it('names the team that blocks the deletion and links to its members', async () => {
    H.del.mockResolvedValue({ kind: 'sole_owner', organization: 'Acme Films' });
    renderCard();
    openModal();
    await screen.findByText('Delete your account?');
    fillAndConfirm('pw');
    fireEvent.click(deleteButton());

    const alert = await screen.findByTestId('delete-account-error');
    expect(alert.textContent).toContain('only owner of “Acme Films”');
    expect(screen.getByRole('link', { name: /Members/ }).getAttribute('href')).toBe('/org/members');
  });

  it('confirms a Google/GitHub account with that service: no password, then a button to continue', async () => {
    H.del.mockResolvedValue({
      kind: 'provider_confirmation',
      provider: 'google',
      confirmUrl: 'https://accounts.google.com/o/oauth2/v2/auth?state=x',
    });
    renderCard();
    openModal();
    await screen.findByText('Delete your account?');

    fireEvent.click(screen.getByRole('button', { name: 'I sign in with Google or GitHub' }));
    expect(screen.queryByLabelText(/Your password/)).toBeNull();
    const cont = screen.getByRole('button', { name: 'Continue' }) as HTMLButtonElement;
    expect(cont.disabled).toBe(true);
    fireEvent.change(screen.getByLabelText('Type DELETE to confirm'), { target: { value: 'DELETE' } });
    fireEvent.click(cont);

    await waitFor(() => expect(H.del).toHaveBeenCalledWith(''));
    expect((await screen.findByTestId('delete-account-provider')).textContent).toContain('Nothing has been deleted yet');
    fireEvent.click(screen.getByRole('button', { name: 'Continue with Google' }));
    expect(window.location.assign).toHaveBeenCalledWith('https://accounts.google.com/o/oauth2/v2/auth?state=x');
  });

  it('sends a password account that chose the Google/GitHub path back to its password', async () => {
    H.del.mockResolvedValue({ kind: 'password_required' });
    renderCard();
    openModal();
    await screen.findByText('Delete your account?');

    fireEvent.click(screen.getByRole('button', { name: 'I sign in with Google or GitHub' }));
    fireEvent.change(screen.getByLabelText('Type DELETE to confirm'), { target: { value: 'DELETE' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));

    expect((await screen.findByTestId('delete-account-error')).textContent).toContain('signs in with a password');
    expect(screen.getByLabelText(/Your password/)).toBeTruthy();
  });

  it('reports an unexpected failure without claiming anything was deleted', async () => {
    H.del.mockRejectedValue(new Error('boom'));
    renderCard();
    openModal();
    await screen.findByText('Delete your account?');
    fillAndConfirm('pw');
    fireEvent.click(deleteButton());

    expect((await screen.findByTestId('delete-account-error')).textContent).toContain('was not deleted');
  });
});
