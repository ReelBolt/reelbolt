// @vitest-environment jsdom

/** "Connected accounts" on the settings page: list, the reconnect badge, Disconnect behind a confirm. */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import type { CloudConnection, CloudProvider } from '@/lib/types/cloudStorage';

const h = vi.hoisted(() => ({
  search: '',
  replace: vi.fn(),
  providers: undefined as CloudProvider[] | undefined,
  connections: undefined as CloudConnection[] | undefined,
  mutate: vi.fn(),
  disconnect: vi.fn(),
  start: vi.fn(),
}));

vi.mock('next/navigation', () => ({
  useRouter: () => ({ replace: h.replace, push: vi.fn() }),
  usePathname: () => '/settings',
  useSearchParams: () => new URLSearchParams(h.search),
}));
vi.mock('./hooks', () => ({
  useCloudProviders: () => ({ data: h.providers }),
  useCloudConnections: () => ({ data: h.connections, error: undefined, mutate: h.mutate }),
}));
vi.mock('@/lib/api/cloudStorage', async (orig) => ({
  ...(await orig<typeof import('@/lib/api/cloudStorage')>()),
  disconnectCloudConnection: (...a: unknown[]) => h.disconnect(...a),
  startCloudConnect: (...a: unknown[]) => h.start(...a),
}));

import { CloudAccountsCard } from './CloudAccountsCard';

const providers = (configured: boolean): CloudProvider[] => [
  { provider: 'google-drive', displayName: 'Google Drive', configured },
  { provider: 'onedrive', displayName: 'OneDrive', configured: false },
  { provider: 'dropbox', displayName: 'Dropbox', configured: false },
];

beforeEach(() => {
  notifications.clean();
  h.search = '';
  h.replace.mockReset();
  h.providers = providers(true);
  h.connections = [
    { id: 'c1', provider: 'google-drive', accountEmail: 'ana@example.com', accountDisplayName: null, createdAt: '2026-10-01T10:00:00Z', lastError: null },
    { id: 'c2', provider: 'google-drive', accountEmail: 'team@example.com', accountDisplayName: null, createdAt: '2026-10-02T10:00:00Z', lastError: 'Google Drive no longer accepts ReelBolt’s access to this account.' },
  ];
  h.mutate.mockReset().mockResolvedValue(undefined);
  h.disconnect.mockReset();
  h.start.mockReset();
  Object.defineProperty(window, 'location', { configurable: true, value: { ...window.location, assign: vi.fn() } });
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

function renderCard() {
  render(
    <MantineProvider>
      <Notifications />
      <CloudAccountsCard />
    </MantineProvider>,
  );
}

describe('Connected accounts', () => {
  it('lists the accounts and marks the one that needs reconnecting', () => {
    renderCard();

    const rows = screen.getAllByTestId('cloud-connection');
    expect(rows).toHaveLength(2);
    expect(within(rows[0]).getByText(/ana@example.com/)).toBeTruthy();
    expect(within(rows[0]).queryByText('Needs reconnecting')).toBeNull();
    expect(within(rows[1]).getByText('Needs reconnecting')).toBeTruthy();
    expect(within(rows[1]).getByRole('button', { name: 'Reconnect' })).toBeTruthy();
  });

  it('Disconnect asks first, then forgets the account and re-reads the list', async () => {
    h.disconnect.mockResolvedValue(undefined);
    renderCard();

    fireEvent.click(within(screen.getAllByTestId('cloud-connection')[0]).getByRole('button', { name: 'Disconnect' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/ReelBolt forgets ana@example.com/)).toBeTruthy();
    expect(h.disconnect).not.toHaveBeenCalled();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Disconnect' }));

    await waitFor(() => expect(h.disconnect).toHaveBeenCalledWith('c1'));
    await waitFor(() => expect(h.mutate).toHaveBeenCalled());
    expect(await screen.findByText('Google Drive account disconnected.')).toBeTruthy();
  });

  it('Reconnect starts the sign-in and comes back to Settings', async () => {
    h.start.mockResolvedValue('https://accounts.google.com/x');
    renderCard();

    fireEvent.click(screen.getByRole('button', { name: 'Reconnect' }));

    await waitFor(() => expect(h.start).toHaveBeenCalledWith('google-drive', '/app/settings'));
    await waitFor(() => expect(window.location.assign).toHaveBeenCalledWith('https://accounts.google.com/x'));
  });

  it('is not shown at all when no provider is set up and nothing is connected', () => {
    h.providers = providers(false);
    h.connections = [];
    renderCard();

    expect(screen.queryByTestId('cloud-accounts-card')).toBeNull();
  });

  it('announces the return from connecting and strips it from the address', async () => {
    h.search = 'cloud=error&provider=google-drive&reason=session_mismatch';
    renderCard();

    expect(await screen.findByText(/could not be matched to you/)).toBeTruthy();
    expect(h.replace).toHaveBeenCalledWith('/settings', { scroll: false });
  });
});
