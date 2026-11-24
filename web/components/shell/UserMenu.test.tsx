// @vitest-environment jsdom
/**
 * A15-UI: the account menu states which workspace the caller is in and what they are in it.
 * Both values come from the readable `reelbolt_user` cookie, so a switch-org is reflected
 * without a refetch.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { UserInfo } from '@/lib/types/auth';

vi.mock('./ThemeToggle', () => ({ ThemeToggle: () => null }));

import { UserMenu } from './UserMenu';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
Element.prototype.scrollIntoView = vi.fn();

function signInAs(user: Partial<UserInfo>) {
  const full: UserInfo = {
    email: 'tommaso@example.com', isPlatformAdmin: false, mustChangePassword: false,
    org: 'org-personal', orgName: 'tommaso@example.com', orgKind: 'Personal', orgRole: 'Owner',
    ...user,
  };
  document.cookie = `reelbolt_user=${encodeURIComponent(JSON.stringify(full))}; path=/`;
}

const openMenu = () => fireEvent.click(screen.getByRole('button', { name: /^Account menu/ }));

beforeEach(() => {
  document.cookie = 'reelbolt_user=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
  global.matchMedia = ((q: string) => ({
    matches: true, media: q, onchange: null,
    addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

describe('UserMenu', () => {
  it('has "Take the tour" for phones, where the header Help menu steps aside', async () => {
    signInAs({});
    render(<MantineProvider><UserMenu /></MantineProvider>);
    openMenu();
    expect(await screen.findByRole('menuitem', { name: 'Take the tour' })).toBeTruthy();
  });

  it('links the documentation for phones, where the header Help menu steps aside', async () => {
    signInAs({});
    render(<MantineProvider><UserMenu /></MantineProvider>);
    openMenu();
    const docs = await screen.findByRole('menuitem', { name: 'Documentation' });
    expect(docs.getAttribute('href')).toBe('/docs/');
  });

  it('names the Personal workspace "Personal" and the caller\'s role in it', async () => {
    signInAs({});
    render(<MantineProvider><UserMenu /></MantineProvider>);
    openMenu();
    expect(await screen.findByText('Personal · Owner')).toBeTruthy();
  });

  it('names a Team workspace by its name and shows the role from the re-issued cookie', async () => {
    signInAs({ org: 'org-acme', orgName: 'Acme Team', orgKind: 'Team', orgRole: 'Billing' });
    render(<MantineProvider><UserMenu /></MantineProvider>);
    openMenu();
    expect(await screen.findByText('Acme Team · Billing')).toBeTruthy();
  });

  it('still shows the email and the sign-out action', async () => {
    signInAs({});
    render(<MantineProvider><UserMenu /></MantineProvider>);
    openMenu();
    expect(await screen.findByText('Logout')).toBeTruthy();
    expect(screen.getAllByText('tommaso@example.com').length).toBeGreaterThan(0);
  });

  it('links to Account and Plan & billing next to Logout', async () => {
    signInAs({});
    render(<MantineProvider><UserMenu /></MantineProvider>);
    openMenu();
    expect((await screen.findByText('Account')).closest('a')?.getAttribute('href')).toBe('/settings');
    expect(screen.getByText('Plan & billing').closest('a')?.getAttribute('href')).toBe('/settings/billing');
    expect(screen.getByText('Logout')).toBeTruthy();
  });
});
