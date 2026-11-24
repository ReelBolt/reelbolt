// @vitest-environment jsdom
/**
 * The account settings page.
 *
 * What these tests are actually protecting:
 *
 *   - the page is a USER page, so its values come from the session and the Personal workspace keeps
 *     its D1 label whatever the stored name says;
 *   - the settings surface it belongs to is reachable from it (Usage, Plan & billing);
 *   - the GitHub account card is MOUNTED HERE and imported by its own module path. That is the seam
 *     the sibling github-account-link work drops into: if someone moves the mount point, this test
 *     fails rather than the integration silently going missing;
 *   - a voluntary password change lands back on this page, not on the dashboard;
 *   - choosing a colour scheme sticks, and "follow the system" -- which the header toggle can never
 *     restore once it has written an explicit scheme -- is offered here.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

const H = vi.hoisted(() => ({
  user: null as null | Record<string, unknown>,
  isPlatformAdmin: false,
  push: vi.fn(),
  changePassword: vi.fn(),
  notifyAuthChange: vi.fn(),
}));

vi.mock('@/lib/hooks/use-github-account', () => ({
  useGitHubAccount: () => ({
    data: { connected: false, login: null, avatarUrl: null, canConnect: true, revokedReason: null, connectedAt: null, installUrl: null },
    error: undefined,
    mutate: vi.fn(),
  }),
}));
vi.mock('next/navigation', () => ({
  useRouter: () => ({ push: H.push, refresh: vi.fn(), prefetch: vi.fn(), replace: vi.fn() }),
  usePathname: () => '/settings',
  useSearchParams: () => new URLSearchParams(),
}));
// The cloud "Connected accounts" card is mounted here; its data is stubbed (its own tests cover it).
const CLOUD = vi.hoisted(() => ({ configured: false }));
vi.mock('@/components/cloud-storage/hooks', () => ({
  useCloudProviders: () => ({ data: [{ provider: 'dropbox', displayName: 'Dropbox', configured: CLOUD.configured }] }),
  useCloudConnections: () => ({ data: [], error: undefined, mutate: vi.fn() }),
}));
vi.mock('@/lib/hooks/use-auth', () => ({
  useAuth: () => ({
    user: H.user,
    isPlatformAdmin: H.isPlatformAdmin,
    isOrgAdmin: H.user?.orgRole === 'Owner' || H.user?.orgRole === 'Admin',
  }),
  notifyAuthChange: H.notifyAuthChange,
}));
vi.mock('@/lib/api/auth', () => ({
  changePassword: (...args: unknown[]) => H.changePassword(...args),
}));

// The storage card's figures are stubbed here; its own rendering is asserted by the storage tests.
const STORAGE = vi.hoisted(() => ({ result: { data: undefined, error: undefined, isLoading: false, mutate: vi.fn() } as Record<string, unknown> }));
vi.mock('@/lib/hooks/use-storage', () => ({
  useStorageUsage: () => STORAGE.result,
  useProjectFolderLinks: () => ({ data: [], error: undefined, isLoading: false, mutate: vi.fn() }),
}));

// The "Your data" cards' own behaviour is covered by their tests; here they only need to mount.
vi.mock('@/lib/api/account', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api/account')>();
  return {
    ...actual,
    getDataExportLink: () => Promise.resolve({ kind: 'none' }),
    getDataExportStatus: () => Promise.resolve({ kind: 'ok', preparing: false, latest: null, nextRequestAt: null }),
    requestDataExport: vi.fn(),
    requestAccountDeletion: vi.fn(),
  };
});

// The marketing card's behaviour is covered by MarketingEmailsCard.test.tsx; here it only mounts.
vi.mock('@/lib/hooks/use-marketing', () => ({
  useMarketingConsent: () => ({
    data: { status: 'none', wordingVersion: null, consentedAt: null, confirmedAt: null, withdrawnAt: null },
    error: undefined,
    mutate: vi.fn(),
  }),
  useMarketingWording: () => ({ data: { version: 'v1', text: 'Email me news.' }, error: undefined, mutate: vi.fn() }),
}));

import AccountSettingsPage from './page';

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

// Mantine's SegmentedControl positions its floating indicator with a ResizeObserver, which jsdom
// does not implement.
vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);

const renderPage = () =>
  render(
    <MantineProvider defaultColorScheme="auto">
      <AccountSettingsPage />
    </MantineProvider>,
  );

/** The page is tabbed (A20); a test for a card on another tab opens that tab first. */
const openTab = (name: string) => fireEvent.click(screen.getByRole('tab', { name }));

function setUser(over: Record<string, unknown> = {}) {
  H.user = {
    email: 'ada@example.com',
    mustChangePassword: false,
    emailVerified: true,
    org: '11111111-1111-1111-1111-111111111111',
    orgName: 'Acme',
    orgKind: 'Team',
    orgRole: 'Owner',
    ...over,
  };
}

beforeEach(() => {
  CLOUD.configured = false;
  H.isPlatformAdmin = false;
  H.push.mockReset();
  H.changePassword.mockReset();
  window.localStorage.clear();
  window.history.replaceState(null, '', '/');
  setUser();
});

afterEach(cleanup);

describe('account settings: connected cloud accounts', () => {
  it('mounts the Connected accounts card when cloud storage is set up, and leaves it out otherwise', () => {
    renderPage();
    expect(screen.queryByTestId('cloud-accounts-card')).toBeNull();
    cleanup();
    CLOUD.configured = true;
    renderPage();
    expect(screen.getByTestId('cloud-accounts-card')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Connect Dropbox' })).toBeTruthy();
  });
});

describe('account settings: profile', () => {
  it('names the signed-in address and the workspace role', () => {
    renderPage();
    expect(screen.getByText('ada@example.com')).toBeTruthy();
    expect(screen.getByText('Acme \u00b7 Owner')).toBeTruthy();
    expect(screen.getByText('Confirmed')).toBeTruthy();
  });

  it('always reads the Personal workspace as "Personal", whatever it is stored as (D1)', () => {
    setUser({ orgName: 'ada-personal-7f3a', orgKind: 'Personal' });
    renderPage();
    expect(screen.getByText('Personal \u00b7 Owner')).toBeTruthy();
    expect(screen.queryByText(/ada-personal-7f3a/)).toBeNull();
  });

  it('says an unconfirmed address is unconfirmed rather than showing a green badge', () => {
    setUser({ emailVerified: false });
    renderPage();
    expect(screen.getByText('Not confirmed')).toBeTruthy();
  });

  it('offers the Members link only to a role the API would answer', () => {
    renderPage();
    expect(screen.getByRole('link', { name: /Members/ })).toBeTruthy();
  });

  it('does not offer the Members link to a Billing member, whose request would be refused', () => {
    setUser({ orgRole: 'Billing' });
    renderPage();
    expect(screen.queryByRole('link', { name: /Members/ })).toBeNull();
  });

  it('shows the platform-administrator badge only to a platform admin', () => {
    renderPage();
    expect(screen.queryByText('Platform administrator')).toBeNull();
    cleanup();
    H.isPlatformAdmin = true;
    renderPage();
    expect(screen.getByText('Platform administrator')).toBeTruthy();
  });
});

describe('account settings: headings and the settings surface', () => {
  it('gives the page one level-2 heading and its cards level-3 headings', () => {
    const { container } = renderPage();
    expect(screen.getByRole('heading', { level: 1, name: 'Account' })).toBeTruthy();
    const h3 = Array.from(container.querySelectorAll('h3')).map((h) => h.textContent);
    for (const title of ['Profile', 'Appearance', 'GitHub']) {
      expect(h3).toContain(title);
    }
    openTab('Security');
    expect(Array.from(container.querySelectorAll('h3')).map((h) => h.textContent)).toContain('Password');
  });

  it('links on to the workspace pages of the same settings surface', () => {
    renderPage();
    expect(screen.getByRole('link', { name: 'Usage' }).getAttribute('href')).toBe('/settings/usage');
    // Named twice: in the Workspace section, and in the Delete account card (cancel a paid plan first).
    const billing = screen.getAllByRole('link', { name: 'Plan & billing' });
    expect(billing.length).toBeGreaterThan(0);
    for (const link of billing) expect(link.getAttribute('href')).toBe('/settings/billing');
  });
});

describe('account settings: the GitHub account seam', () => {
  it('mounts the GitHub account card, which is where the account-link work lands', () => {
    renderPage();
    expect(screen.getByTestId('github-account-card')).toBeTruthy();
    expect(screen.getByText('GitHub account')).toBeTruthy();
  });

  it('offers the connect action when the server can connect an account', () => {
    renderPage();
    expect(screen.getByRole('button', { name: /connect github account/i })).toBeTruthy();
  });
});

describe('account settings: appearance', () => {
  it('offers the three schemes, including the "follow the system" the header toggle cannot restore', () => {
    renderPage();
    for (const label of ['Follow the system', 'Light', 'Dark']) {
      expect(screen.getByRole('radio', { name: label })).toBeTruthy();
    }
  });

  it('writes the chosen scheme to the browser, so it survives a reload', async () => {
    renderPage();
    fireEvent.click(screen.getByRole('radio', { name: 'Dark' }));
    await waitFor(() => expect(window.localStorage.getItem('mantine-color-scheme-value')).toBe('dark'));
  });
});

describe('account settings: password', () => {
  it('posts the change and returns to this page rather than the dashboard', async () => {
    H.changePassword.mockResolvedValue(undefined);
    renderPage();
    openTab('Security');

    fireEvent.change(screen.getByLabelText(/current password/i), { target: { value: 'old-password' } });
    fireEvent.change(screen.getByLabelText(/^new password/i), { target: { value: 'a-longer-password' } });
    fireEvent.change(screen.getByLabelText(/confirm new password/i), { target: { value: 'a-longer-password' } });
    fireEvent.click(screen.getByRole('button', { name: /change password/i }));

    await waitFor(() => expect(H.changePassword).toHaveBeenCalledTimes(1));
    expect(H.changePassword).toHaveBeenCalledWith({
      currentPassword: 'old-password',
      newPassword: 'a-longer-password',
    });
    await waitFor(() => expect(H.push).toHaveBeenCalledWith('/settings#security'));
  });

  it('refuses a new password the API would refuse, without calling it', async () => {
    renderPage();
    openTab('Security');
    fireEvent.change(screen.getByLabelText(/current password/i), { target: { value: 'old-password' } });
    fireEvent.change(screen.getByLabelText(/^new password/i), { target: { value: 'short' } });
    fireEvent.change(screen.getByLabelText(/confirm new password/i), { target: { value: 'short' } });
    fireEvent.click(screen.getByRole('button', { name: /change password/i }));

    await waitFor(() => expect(screen.getByText('Password must be at least 10 characters')).toBeTruthy());
    expect(H.changePassword).not.toHaveBeenCalled();
  });
});

describe('account settings: your data', () => {
  it('ends with the "Your data" section: a copy of your data, and closing the account', async () => {
    renderPage();
    openTab('Your data');
    expect(screen.getByRole('heading', { level: 3, name: 'Your data' })).toBeTruthy();
    expect(screen.getByTestId('data-export-card')).toBeTruthy();
    expect(screen.getByTestId('delete-account-card')).toBeTruthy();
    expect(await screen.findByRole('button', { name: 'Request a copy' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Delete account…' })).toBeTruthy();
  });
});

describe('account settings: workspace storage', () => {
  it('shows the workspace storage bar with the plan limit and the largest projects', () => {
    STORAGE.result = {
      data: {
        usedBytes: 4_509_715_660,
        limitBytes: 16_106_127_360,
        byProject: [{ projectId: 'p1', name: 'Launch reel', usedBytes: 3_221_225_472 }],
      },
    };
    renderPage();

    const card = screen.getByTestId('storage-card');
    expect(card.textContent).toContain('4.2 GB of 15 GB used');
    expect(card.textContent).toContain('Launch reel');
    expect(within(card).getByRole('link', { name: 'See plans' }).getAttribute('href')).toBe('/settings/billing');
  });

  it('renders nothing rather than a broken card when storage cannot be read', () => {
    STORAGE.result = { data: undefined, error: new Error('boom'), isLoading: false, mutate: vi.fn() };
    renderPage();
    expect(screen.queryByTestId('storage-card')).toBeNull();
  });
});

describe('account settings: tabs and what a hosted user can use (A20)', () => {
  it('offers Profile, Security, Notifications and Your data tabs', () => {
    renderPage();
    for (const name of ['Profile', 'Security', 'Notifications', 'Your data']) {
      expect(screen.getByRole('tab', { name })).toBeTruthy();
    }
  });

  it('says there are no personal API keys and how tools connect instead', () => {
    renderPage();
    openTab('Security');
    expect(screen.getByTestId('connect-tools-card').textContent).toMatch(/does not use personal API keys/);
  });

  it('hides the admin MCP tools from a plain member, and shows them to an Owner', () => {
    setUser({ orgRole: 'Member' });
    renderPage();
    openTab('Security');
    expect(screen.queryByTestId('mcp-admin-tools-card')).toBeNull();
    cleanup();
    setUser({ orgRole: 'Owner' });
    renderPage();
    openTab('Security');
    expect(screen.getByTestId('mcp-admin-tools-card')).toBeTruthy();
  });
});
