// @vitest-environment jsdom
/**
 * A15-UI: the workspace switcher. It lists the caller's organizations (the Personal one is
 * always labelled "Personal", decision D1), switches the active one through the real
 * `useSwitchOrg` hook — which re-issues the session cookie and revalidates the client cache —
 * and lands on /dashboard. The SSE reconnect that a switch also triggers is keyed on the active
 * org and is covered by `lib/hooks/sse-org-reconnect.test.tsx`.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import { ApiError } from '@/lib/api/client';
import type { Org } from '@/lib/api/orgs';
import type { UserInfo } from '@/lib/types/auth';

const { fetcher, switchOrgMock, push } = vi.hoisted(() => ({
  fetcher: vi.fn(),
  switchOrgMock: vi.fn(),
  push: vi.fn(),
}));
const notifyShow = vi.spyOn(notifications, 'show').mockImplementation(() => 'notification-id');

// Partial mocks, so `ApiError` stays the real class the app throws.
vi.mock('@/lib/api/client', async (orig) => ({
  ...(await orig<typeof import('@/lib/api/client')>()),
  fetcher: (...a: unknown[]) => fetcher(...a),
}));
vi.mock('@/lib/api/orgs', async (orig) => ({
  ...(await orig<typeof import('@/lib/api/orgs')>()),
  switchOrg: (...a: unknown[]) => switchOrgMock(...a),
}));
vi.mock('next/navigation', () => ({ useRouter: () => ({ push, refresh: vi.fn(), prefetch: vi.fn() }) }));

import { OrgSwitcher } from './OrgSwitcher';

// Neither exists in jsdom, and Mantine's overlay components need both.
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
Element.prototype.scrollIntoView = vi.fn();

const PERSONAL_ORG: Org = {
  id: 'org-personal', name: 'tommaso@example.com', slug: 'personal', kind: 'Personal', role: 'Owner', createdAt: '2026-10-01T00:00:00Z',
};
const TEAM_ORG: Org = {
  id: 'org-acme', name: 'Acme Team', slug: 'acme-team', kind: 'Team', role: 'Admin', createdAt: '2026-10-01T00:00:00Z',
};

/** The readable cookie the Go API writes on login and on every switch-org. */
function signInAs(user: Partial<UserInfo>) {
  const full: UserInfo = {
    email: 'tommaso@example.com', isPlatformAdmin: false, mustChangePassword: false,
    org: PERSONAL_ORG.id, orgName: PERSONAL_ORG.name, orgKind: 'Personal', orgRole: 'Owner',
    ...user,
  };
  document.cookie = `reelbolt_user=${encodeURIComponent(JSON.stringify(full))}; path=/`;
}

const renderSwitcher = (onSelect = vi.fn()) => {
  render(
    <MantineProvider>
      <Notifications />
      <OrgSwitcher onSelect={onSelect} />
    </MantineProvider>,
  );
  return onSelect;
};

/**
 * Opens the menu and returns its items. They are the only menuitems on the page, so they are
 * addressed by role and then by their own label — RTL's `name` option on a role query does not
 * match them here.
 */
async function openMenu() {
  fireEvent.click(screen.getByRole('button', { name: 'Switch workspace' }));
  const items = await screen.findAllByRole('menuitem');
  return {
    labels: items.map((item) => item.textContent),
    item(label: string) {
      const found = items.find((candidate) => candidate.textContent === label);
      if (!found) throw new Error(`No workspace menu item labelled "${label}"`);
      return found;
    },
  };
}

beforeEach(() => {
  document.cookie = 'reelbolt_user=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
  fetcher.mockReset();
  switchOrgMock.mockReset();
  push.mockReset();
  notifyShow.mockClear();
  fetcher.mockResolvedValue([PERSONAL_ORG, TEAM_ORG]);
  global.matchMedia = ((q: string) => ({
    matches: true, media: q, onchange: null,
    addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

describe('OrgSwitcher', () => {
  it('lists the caller\'s workspaces and labels the Personal one "Personal", not its stored name', async () => {
    signInAs({});
    renderSwitcher();
    // The trigger names the active workspace, and the Personal workspace never shows its raw name.
    expect(screen.getByRole('button', { name: 'Switch workspace' }).textContent).toContain('Personal');
    const menu = await openMenu();
    expect(menu.labels).toEqual(['Personal', 'Acme Team']);
    expect(screen.queryByText('tommaso@example.com')).toBeNull();
  });

  it('switches through the hook, revalidates the cache and lands on /dashboard', async () => {
    signInAs({});
    switchOrgMock.mockImplementation(async () => {
      // What POST /api/v1/auth/switch-org does: the browser stores the re-issued readable cookie.
      signInAs({ org: TEAM_ORG.id, orgName: TEAM_ORG.name, orgKind: 'Team', orgRole: 'Admin' });
      return { org: TEAM_ORG.id, orgName: TEAM_ORG.name, orgKind: 'Team', orgRole: 'Admin' };
    });
    const onSelect = renderSwitcher();
    // Opening the menu proves `useOrgs` has data (from the cache or from this mount's fetch).
    const menu = await openMenu();
    const callsBefore = fetcher.mock.calls.length;
    await act(async () => { fireEvent.click(menu.item('Acme Team')); });

    expect(switchOrgMock).toHaveBeenCalledWith({ organizationId: TEAM_ORG.id });
    // Closing the mobile navbar is the caller's job, and it still runs.
    expect(onSelect).toHaveBeenCalled();
    // `useSwitchOrg` calls `mutate(() => true)`, i.e. clears and revalidates every cached key.
    await waitFor(() => expect(fetcher.mock.calls.length).toBeGreaterThan(callsBefore));
    await waitFor(() => expect(push).toHaveBeenCalledWith('/dashboard'));
    // The label follows the switch, so the shell never keeps naming the old workspace.
    expect(screen.getByRole('button', { name: 'Switch workspace' }).textContent).toContain('Acme Team');
  });

  it('reports a refused switch and stays put', async () => {
    signInAs({});
    switchOrgMock.mockRejectedValue(new ApiError('organization not found', 404));
    renderSwitcher();
    const menu = await openMenu();
    await act(async () => { fireEvent.click(menu.item('Acme Team')); });

    expect(notifyShow).toHaveBeenCalledWith({ message: 'organization not found', color: 'red' });
    expect(push).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Switch workspace' }).textContent).toContain('Personal');
  });

  it('does not call the API when the caller picks the workspace they are already in', async () => {
    signInAs({});
    renderSwitcher();
    const menu = await openMenu();
    await act(async () => { fireEvent.click(menu.item('Personal')); });
    expect(switchOrgMock).not.toHaveBeenCalled();
    expect(push).not.toHaveBeenCalled();
  });

  it('paints its trigger with the scheme-adaptive default surface, not a fixed grey palette step', () => {
    signInAs({});
    renderSwitcher();
    const trigger = screen.getByRole('button', { name: 'Switch workspace' });
    // The pill's background must be the semantic "default" surface (white in light mode,
    // dark-6 in dark mode) — the fixed `--mantine-color-gray-0` step stayed near-white in
    // both schemes, which is what kept the switcher light in dark mode.
    expect(trigger.style.background).toBe('var(--mantine-color-default)');
    expect(trigger.style.background).not.toContain('gray-0');
  });
});
