// @vitest-environment jsdom

/**
 * The project's Cloud storage tab: one card per provider the server has set up; connect → account →
 * drive → folder → (optional) files → link; then the linked card — Sync now, Change files, Unlink — and
 * the background-sync follow-up. The network functions are stubbed; the sentences are the real ones.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import { SWRConfig } from 'swr';
import { ApiError } from '@/lib/api/client';
import type { CloudConnection, CloudDrive, CloudFolderList, CloudProvider, ProjectCloudLink } from '@/lib/types/cloudStorage';

const h = vi.hoisted(() => ({
  search: '',
  replace: vi.fn(),
  providers: [] as CloudProvider[] | undefined,
  connections: [] as CloudConnection[] | undefined,
  links: [] as ProjectCloudLink[],
  drives: [] as CloudDrive[] | undefined,
  folders: {} as Record<string, CloudFolderList>,
  mutateConnections: vi.fn(),
  mutateLinks: vi.fn(),
  start: vi.fn(),
  create: vi.fn(),
  update: vi.fn(),
  remove: vi.fn(),
  sync: vi.fn(),
}));

vi.mock('next/navigation', () => ({
  useRouter: () => ({ replace: h.replace, push: vi.fn() }),
  usePathname: () => '/projects/p1',
  useSearchParams: () => new URLSearchParams(h.search),
}));

vi.mock('./hooks', () => ({
  useCloudProviders: () => ({ data: h.providers, error: undefined }),
  useCloudConnections: () => ({ data: h.connections, error: undefined, mutate: h.mutateConnections }),
  useProjectCloudLinks: () => ({ data: h.links, error: undefined, isLoading: false, mutate: h.mutateLinks }),
  useCloudDrives: (id: string | null) => ({ data: id ? h.drives : undefined, error: undefined }),
}));

vi.mock('@/lib/api/cloudStorage', async (orig) => ({
  ...(await orig<typeof import('@/lib/api/cloudStorage')>()),
  startCloudConnect: (...a: unknown[]) => h.start(...a),
  createProjectCloudLink: (...a: unknown[]) => h.create(...a),
  updateProjectCloudLink: (...a: unknown[]) => h.update(...a),
  deleteProjectCloudLink: (...a: unknown[]) => h.remove(...a),
  syncProjectCloudLink: (...a: unknown[]) => h.sync(...a),
  listCloudFolder: (_c: string, _d: string | null, folderId: string | null) => {
    const list = h.folders[folderId ?? ''];
    return list ? Promise.resolve(list) : Promise.reject(new ApiError('folder_not_found', 404, [], { error: 'folder_not_found' }));
  },
}));

import { CLOUD_SYNC_POLL_MS, ProjectCloudStorageTab, cloudSyncFinishedNotice } from './ProjectCloudStorageTab';
import { en } from '@/components/design-system/__tests__/en';

const providers = (configured: Partial<Record<CloudProvider['provider'], boolean>>): CloudProvider[] => [
  { provider: 'google-drive', displayName: 'Google Drive', configured: configured['google-drive'] ?? false },
  { provider: 'onedrive', displayName: 'OneDrive', configured: configured.onedrive ?? false },
  { provider: 'dropbox', displayName: 'Dropbox', configured: configured.dropbox ?? false },
];

const connection = (over: Partial<CloudConnection> = {}): CloudConnection => ({
  id: 'c1', provider: 'google-drive', accountEmail: 'ana@example.com', accountDisplayName: 'Ana', createdAt: '2026-10-01T10:00:00Z', lastError: null, ...over,
});

const linked = (over: Partial<ProjectCloudLink> = {}): ProjectCloudLink => ({
  id: 'l1', provider: 'google-drive', connectionId: 'c1', accountEmail: 'ana@example.com', driveId: null, folderId: 'f1',
  folderDisplayPath: '/Brand', includePaths: null, lastSyncedAt: '2026-10-10T10:00:00Z', lastSyncedFileCount: 12,
  lastError: null, syncing: false, ...over,
});

const refusal = (status: number, body: Record<string, unknown>) => new ApiError(String(body.error), status, [], body);

beforeEach(() => {
  notifications.clean();
  h.search = 'tab=cloud';
  h.replace.mockReset();
  h.providers = providers({ 'google-drive': true });
  h.connections = [];
  h.links = [];
  h.drives = [{ driveId: null, name: 'My Drive', kind: 'personal' }];
  h.folders = {
    '': { folder: { folderId: null, name: 'My Drive', displayPath: '/' }, items: [{ id: 'f1', name: 'Brand', isFolder: true, size: null, modifiedAt: null }], truncated: false },
    f1: {
      folder: { folderId: 'f1', name: 'Brand', displayPath: '/Brand' },
      items: [
        { id: 'f2', name: 'fonts', isFolder: true, size: null, modifiedAt: null },
        { id: 'i1', name: 'logo.svg', isFolder: false, size: 10, modifiedAt: null },
      ],
      truncated: false,
    },
  };
  h.mutateConnections.mockReset().mockResolvedValue(undefined);
  h.mutateLinks.mockReset().mockImplementation(async (update?: unknown) => {
    if (typeof update === 'function') h.links = (update as (l: ProjectCloudLink[]) => ProjectCloudLink[])(h.links);
    return h.links;
  });
  for (const fn of [h.start, h.create, h.update, h.remove, h.sync]) fn.mockReset();
  Object.defineProperty(window, 'location', { configurable: true, value: { ...window.location, assign: vi.fn() } });
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
Element.prototype.scrollIntoView = vi.fn();

function renderTab() {
  return render(
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <MantineProvider>
        <Notifications />
        <ProjectCloudStorageTab projectId="p1" />
      </MantineProvider>
    </SWRConfig>,
  );
}

/** Makes each re-read of the links answer the next list (the last repeats). */
const linkReads = (...answers: ProjectCloudLink[][]) => {
  let i = 0;
  h.mutateLinks.mockImplementation(async () => {
    h.links = answers[Math.min(i, answers.length - 1)];
    i += 1;
    return h.links;
  });
};
const nextPoll = () => act(async () => { await vi.advanceTimersByTimeAsync(CLOUD_SYNC_POLL_MS); });

describe('which providers are shown', () => {
  it('shows a card only for the providers this server has set up', () => {
    h.providers = providers({ dropbox: true });
    renderTab();

    expect(screen.getByRole('button', { name: 'Connect Dropbox' })).toBeTruthy();
    expect(screen.queryByText('Google Drive')).toBeNull();
    expect(screen.queryByText('OneDrive')).toBeNull();
  });

  it('says plainly when none is set up, and offers nothing that cannot work', () => {
    h.providers = providers({});
    renderTab();

    expect(screen.getByText(/not set up on this server/)).toBeTruthy();
    expect(screen.queryByRole('button')).toBeNull();
  });
});

describe('connecting and linking', () => {
  it('Connect sends the browser to the provider, coming back to this tab', async () => {
    h.start.mockResolvedValue('https://accounts.google.com/o/oauth2/v2/auth?state=x');
    renderTab();

    fireEvent.click(screen.getByRole('button', { name: 'Connect Google Drive' }));

    await waitFor(() => expect(h.start).toHaveBeenCalledWith('google-drive', '/app/projects/p1?tab=cloud'));
    await waitFor(() => expect(window.location.assign).toHaveBeenCalledWith('https://accounts.google.com/o/oauth2/v2/auth?state=x'));
  });

  it('a refused connect says why in plain words', async () => {
    h.start.mockRejectedValue(refusal(503, { error: 'public_base_url_missing' }));
    renderTab();

    fireEvent.click(screen.getByRole('button', { name: 'Connect Google Drive' }));

    expect((await screen.findByRole('alert')).textContent).toMatch(/not set up on this server yet/);
  });

  it('account → folder → every file → Link sends exactly that and follows the first sync', async () => {
    h.connections = [connection()];
    h.create.mockResolvedValue(linked({ syncing: true, lastSyncedAt: null, lastSyncedFileCount: 0 }));
    renderTab();

    expect(screen.getByText(/ana@example.com/)).toBeTruthy();
    fireEvent.click(await screen.findByRole('button', { name: 'Open folder Brand' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Use “Brand”' }));
    expect(screen.getByRole('radio', { name: /Every file in this folder/ })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /Link and sync \/Brand/ }));

    await waitFor(() => expect(h.create).toHaveBeenCalledWith('p1', { connectionId: 'c1', driveId: null, folderId: 'f1', includePaths: [] }));
    expect(await screen.findByText(/Copying its files now/)).toBeTruthy();
    // The new link is in the list and shown as syncing.
    expect(await screen.findByText('/Brand')).toBeTruthy();
  });

  it('only the ticked files are sent when the person chooses some', async () => {
    h.connections = [connection()];
    h.create.mockResolvedValue(linked({ includePaths: ['logo.svg'], syncing: true }));
    renderTab();

    fireEvent.click(await screen.findByRole('button', { name: 'Open folder Brand' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Use “Brand”' }));
    fireEvent.click(screen.getByRole('radio', { name: /Only the files and folders I tick/ }));

    const link = screen.getByRole('button', { name: /Link and sync/ }) as HTMLButtonElement;
    expect(link.disabled).toBe(true);
    fireEvent.click(await screen.findByRole('checkbox', { name: 'logo.svg' }));
    expect(link.disabled).toBe(false);
    fireEvent.click(link);

    await waitFor(() => expect(h.create).toHaveBeenCalledWith('p1', expect.objectContaining({ includePaths: ['logo.svg'] })));
  });

  it('an account that needs reconnecting offers Reconnect instead of the folder picker', async () => {
    h.connections = [connection({ lastError: 'Google Drive no longer accepts ReelBolt’s access to this account.' })];
    h.start.mockResolvedValue('https://accounts.google.com/x');
    renderTab();

    expect(screen.getByText('This account needs reconnecting')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Reconnect' }));
    await waitFor(() => expect(h.start).toHaveBeenCalledWith('google-drive', '/app/projects/p1?tab=cloud'));
  });
});

describe('a linked folder', () => {
  it('shows the folder, the account, when it last synced and how many files it brought', () => {
    h.connections = [connection()];
    h.links = [linked({ includePaths: ['logo.svg'] })];
    renderTab();

    expect(screen.getByText('/Brand')).toBeTruthy();
    expect(screen.getByText('ana@example.com')).toBeTruthy();
    expect(screen.getByText('12 files')).toBeTruthy();
    expect(screen.getByText('logo.svg')).toBeTruthy();
    expect(screen.getByRole('status', { name: /Google Drive sync status/ }).textContent).toMatch(/Done/);
  });

  it('Sync now shows Syncing…, re-reads every 3 s while syncing, announces the result and stops', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    h.connections = [connection()];
    h.links = [linked()];
    h.sync.mockResolvedValue({ status: 'queued' });
    linkReads([linked({ syncing: true })], [linked({ syncing: false, lastSyncedFileCount: 40 })]);
    renderTab();

    fireEvent.click(screen.getByRole('button', { name: /Sync now/ }));
    await waitFor(() => expect(h.sync).toHaveBeenCalledWith('p1', 'l1'));
    expect(await screen.findByRole('button', { name: /Syncing…/ })).toBeTruthy();
    expect(screen.getByRole('status', { name: /sync status/ }).textContent).toMatch(/Running/);
    expect(h.mutateLinks).not.toHaveBeenCalled();

    await nextPoll();
    expect(h.mutateLinks).toHaveBeenCalledTimes(1);
    expect(screen.queryByText(/sync finished/)).toBeNull();

    await nextPoll();
    expect(h.mutateLinks).toHaveBeenCalledTimes(2);
    expect(await screen.findByText('Google Drive: sync finished')).toBeTruthy();
    expect(screen.getByText(/40 files from \/Brand are in this project/)).toBeTruthy();

    await nextPoll();
    await nextPoll();
    expect(h.mutateLinks).toHaveBeenCalledTimes(2);
  });

  it('follows a sync that was already running when the page opened', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    h.connections = [connection()];
    h.links = [linked({ syncing: true })];
    linkReads([linked({ syncing: false, lastError: 'Too many files are chosen to sync: 3995, and the limit is 2000.' })]);
    renderTab();

    await nextPoll();
    expect(await screen.findByText('Google Drive: the sync did not finish')).toBeTruthy();
    expect(screen.getAllByText(/Too many files are chosen to sync/).length).toBeGreaterThan(0);
    // The fix for this failure is offered next to the reason, as well as in the card's own buttons.
    expect(screen.getAllByRole('button', { name: 'Change files' })).toHaveLength(2);
  });

  it('does not re-read anything while nothing is syncing', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    h.connections = [connection()];
    h.links = [linked()];
    renderTab();

    await nextPoll();
    await nextPoll();
    expect(h.mutateLinks).not.toHaveBeenCalled();
  });

  it('Sync now asked too soon says how long to wait', async () => {
    h.connections = [connection()];
    h.links = [linked()];
    h.sync.mockRejectedValue(refusal(429, { error: 'sync_too_soon', message: 'A sync was started a moment ago. Wait a few seconds, then try again.', retryAfterSeconds: 12 }));
    renderTab();

    fireEvent.click(screen.getByRole('button', { name: /Sync now/ }));

    expect((await screen.findByRole('alert')).textContent).toMatch(/Try again in 12 seconds/);
  });

  it('a link whose account lost access offers Reconnect', async () => {
    h.connections = [connection({ lastError: 'Google Drive no longer accepts ReelBolt’s access to this account. Reconnect it in Settings.' })];
    h.links = [linked({ lastError: 'Google Drive no longer accepts ReelBolt’s access to this account. Reconnect it in Settings.' })];
    h.start.mockResolvedValue('https://accounts.google.com/x');
    renderTab();

    expect(screen.getByText('The last sync did not finish')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Reconnect' }));
    await waitFor(() => expect(h.start).toHaveBeenCalledWith('google-drive', '/app/projects/p1?tab=cloud'));
  });

  it('Change files opens on the saved choice and saves the new one, then follows the re-sync', async () => {
    h.connections = [connection()];
    h.links = [linked({ includePaths: ['logo.svg'] })];
    h.update.mockResolvedValue(linked({ includePaths: [], syncing: true }));
    renderTab();

    fireEvent.click(screen.getByRole('button', { name: 'Change files' }));
    expect((await screen.findByRole('checkbox', { name: 'logo.svg' }) as HTMLInputElement).checked).toBe(true);
    fireEvent.click(screen.getByRole('radio', { name: /Every file in this folder/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Save and sync' }));

    await waitFor(() => expect(h.update).toHaveBeenCalledWith('p1', 'l1', { includePaths: [] }));
    expect(await screen.findByText(/Syncing the new choice of files now/)).toBeTruthy();
  });

  it('Unlink asks first, says the files stay, then unlinks', async () => {
    h.connections = [connection()];
    h.links = [linked()];
    h.remove.mockResolvedValue(undefined);
    renderTab();

    fireEvent.click(screen.getByRole('button', { name: /Unlink folder/ }));
    expect(await screen.findByText('Stop syncing this folder?')).toBeTruthy();
    expect(screen.getByText(/already brought in stay in the project/)).toBeTruthy();
    expect(h.remove).not.toHaveBeenCalled();
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Unlink' }));

    await waitFor(() => expect(h.remove).toHaveBeenCalledWith('p1', 'l1'));
  });
});

describe('coming back from connecting an account', () => {
  it('announces a connected account once, re-reads the accounts and strips the query', async () => {
    h.providers = providers({ dropbox: true });
    h.search = 'tab=cloud&cloud=connected&provider=dropbox';
    renderTab();

    expect(await screen.findByText('Dropbox account connected. Now choose a folder.')).toBeTruthy();
    expect(h.mutateConnections).toHaveBeenCalled();
    expect(h.replace).toHaveBeenCalledWith('/projects/p1?tab=cloud', { scroll: false });
  });

  it('explains a failed connection in plain words', async () => {
    h.search = 'tab=cloud&cloud=error&provider=google-drive&reason=denied';
    renderTab();

    expect(await screen.findByText('You did not approve ReelBolt on Google Drive, so no account was connected.')).toBeTruthy();
    expect(h.replace).toHaveBeenCalledWith('/projects/p1?tab=cloud', { scroll: false });
  });
});

describe('cloudSyncFinishedNotice', () => {
  it('reads a finished sync as the file count, or as the reason it failed', () => {
    const done = cloudSyncFinishedNotice(linked({ lastSyncedFileCount: 1 }), 'Dropbox');
    expect(done.color).toBe('green');
    expect(typeof done.message === 'string' ? done.message : en(done.message)).toBe('1 file from /Brand is in this project.');
    expect(cloudSyncFinishedNotice(linked({ lastError: 'Nope.' }), 'Dropbox')).toMatchObject({ color: 'red', message: 'Nope.' });
  });
});
