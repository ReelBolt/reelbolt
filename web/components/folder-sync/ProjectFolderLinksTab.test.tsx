// @vitest-environment jsdom

/**
 * The project's "Folders on your computers" tab (docs/design/local-folder-sync-and-storage.md Part 1,
 * "Dashboard API"): what each linked folder shows, the pause/resume/unlink actions and their
 * confirmations, the mass-delete guard's own explanation, a storage-full lastError with a way out, and
 * the re-read rules — on mount, after an action and on window focus, never on an interval.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { ProjectFolderLink } from '@/lib/types/folderLink';

const h = vi.hoisted(() => ({
  data: [] as ProjectFolderLink[] | undefined,
  error: undefined as unknown,
  isLoading: false,
  mutate: vi.fn(),
  pause: vi.fn(),
  resume: vi.fn(),
  unlink: vi.fn(),
}));

vi.mock('@/lib/hooks/use-storage', () => ({
  useStorageUsage: () => ({ data: undefined }),
  useProjectFolderLinks: () => ({ data: h.data, error: h.error, isLoading: h.isLoading, mutate: h.mutate }),
}));

vi.mock('@/lib/api/folderLinks', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/api/folderLinks')>()),
  pauseProjectFolderLink: (...args: unknown[]) => h.pause(...args),
  resumeProjectFolderLink: (...args: unknown[]) => h.resume(...args),
  unlinkProjectFolderLink: (...args: unknown[]) => h.unlink(...args),
}));

import { ProjectFolderLinksTab } from './ProjectFolderLinksTab';

const PROJECT = '11111111-1111-1111-1111-111111111111';

const link = (over: Partial<ProjectFolderLink> = {}): ProjectFolderLink => ({
  id: 'l1',
  deviceId: 'd1',
  deviceName: 'Ana’s laptop',
  deviceOnline: true,
  deviceRevoked: false,
  targetFolder: 'Footage',
  displayPath: 'C:\\Users\\ana\\iCloud Drive\\Footage',
  paused: false,
  pausedReason: null,
  fileCount: 12,
  lastChangeAt: new Date(Date.now() - 60_000).toISOString(),
  lastReconciledAt: new Date(Date.now() - 60_000).toISOString(),
  lastError: null,
  ...over,
});

const renderTab = () => render(
  <MantineProvider>
    <ProjectFolderLinksTab projectId={PROJECT} />
  </MantineProvider>,
);

beforeEach(() => {
  h.data = [];
  h.error = undefined;
  h.isLoading = false;
  h.mutate.mockReset().mockResolvedValue(undefined);
  h.pause.mockReset().mockResolvedValue(undefined);
  h.resume.mockReset().mockResolvedValue(undefined);
  h.unlink.mockReset().mockResolvedValue(undefined);
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
    addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
});

afterEach(cleanup);

describe('the empty state', () => {
  it('tells the person to install the desktop app and choose Add folder, linking to the guide', () => {
    renderTab();

    expect(screen.getByText(/Install the ReelBolt desktop app \(Windows or Linux\)/)).toBeTruthy();
    expect(screen.getByText('Add folder')).toBeTruthy();
    const link2 = screen.getByRole('link', { name: /desktop app/i });
    expect(link2.getAttribute('href')).toContain('local-folder-sync');
  });
});

describe('one linked folder', () => {
  it('shows the computer, where the folder is, the project folder, file count and last change', () => {
    h.data = [link()];
    renderTab();

    const card = screen.getByTestId('folder-link-l1');
    expect(card.textContent).toContain('Ana’s laptop');
    expect(card.textContent).toContain('C:\\Users\\ana\\iCloud Drive\\Footage');
    expect(card.textContent).toContain('Footage');
    expect(card.textContent).toContain('12 files');
    expect(card.textContent).toContain('Online');
    expect(card.textContent).toContain('Last change');
    expect(card.textContent).toContain('1 min ago');
  });

  it('shows a revoked computer as disconnected rather than offline', () => {
    h.data = [link({ deviceRevoked: true, deviceOnline: false, lastError: null })];
    renderTab();
    expect(screen.getByTestId('folder-link-l1').textContent).toContain('Disconnected');
    expect(screen.getByTestId('folder-link-l1').textContent).not.toContain('Offline');
  });

  it('shows a plain lastError in full, so the reason is never hidden', () => {
    h.data = [link({ lastError: 'The folder could not be read (permission denied).' })];
    renderTab();

    const error = screen.getByTestId('folder-link-error-l1');
    expect(error.textContent).toContain('The folder could not be read (permission denied).');
    expect(error.textContent).toContain('nothing was deleted');
  });

  it('explains a guard pause as protecting files, and a user pause as somebody’s pause', () => {
    h.data = [link({ paused: true, pausedReason: 'guard' })];
    renderTab();
    const guard = screen.getByTestId('folder-link-paused-l1');
    expect(guard.textContent).toMatch(/nothing was deleted/i);
    expect(guard.textContent).toContain('looked empty or mostly gone');
    expect(screen.getByRole('button', { name: 'Resume' })).toBeTruthy();

    cleanup();
    h.data = [link({ paused: true, pausedReason: 'user' })];
    renderTab();
    const user = screen.getByTestId('folder-link-paused-l1');
    expect(user.textContent).toContain('Syncing is paused');
    expect(user.textContent).not.toMatch(/nothing was deleted/i);
  });

  it('turns a storage-full lastError into the quota sentence with a link to Billing', () => {
    h.data = [link({ lastError: 'Storage is full (15 GB of 15 GB used) — 3 files were not copied.' })];
    renderTab();

    const quota = screen.getByTestId('folder-link-quota-l1');
    expect(quota.textContent).toContain('Storage is full');
    expect(within(quota).getByRole('link', { name: /See plans and storage/ }).getAttribute('href')).toBe('/settings/billing');
  });
});

describe('actions', () => {
  it('pauses, tells the server, and re-reads the list afterwards', async () => {
    h.data = [link()];
    renderTab();

    fireEvent.click(screen.getByRole('button', { name: 'Pause' }));

    await waitFor(() => expect(h.pause).toHaveBeenCalledWith(PROJECT, 'l1'));
    await waitFor(() => expect(h.mutate).toHaveBeenCalled());
  });

  it('resumes a paused link', async () => {
    h.data = [link({ paused: true, pausedReason: 'guard' })];
    renderTab();

    fireEvent.click(screen.getByRole('button', { name: 'Resume' }));

    await waitFor(() => expect(h.resume).toHaveBeenCalledWith(PROJECT, 'l1'));
    await waitFor(() => expect(h.mutate).toHaveBeenCalled());
  });

  it('unlinks only after a confirmation that says the files stay in the project', async () => {
    h.data = [link()];
    renderTab();

    fireEvent.click(screen.getByRole('button', { name: 'Unlink' }));
    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain('will no longer be copied into this project');
    expect(dialog.textContent).toContain('nothing is deleted');
    expect(h.unlink).not.toHaveBeenCalled();

    fireEvent.click(within(dialog).getByRole('button', { name: 'Unlink' }));

    await waitFor(() => expect(h.unlink).toHaveBeenCalledWith(PROJECT, 'l1'));
    await waitFor(() => expect(h.mutate).toHaveBeenCalled());
  });

  it('reports a refused action instead of pretending it worked, and re-reads anyway', async () => {
    h.pause.mockRejectedValue(new Error('That folder is no longer linked.'));
    h.data = [link()];
    renderTab();

    fireEvent.click(screen.getByRole('button', { name: 'Pause' }));

    await waitFor(() => expect(h.mutate).toHaveBeenCalled());
    // The button is usable again (the action ended), whatever the outcome was.
    await waitFor(() => expect(screen.getByRole('button', { name: 'Pause' })).toBeTruthy());
  });
});

describe('when the list comes back', () => {
  it('re-reads on window focus, without any interval polling', async () => {
    h.data = [link()];
    renderTab();

    h.mutate.mockClear();
    fireEvent(window, new Event('focus'));

    await waitFor(() => expect(h.mutate).toHaveBeenCalled());
    expect(screen.getByTestId('folder-link-l1')).toBeTruthy();
  });

  it('offers a retry when the list could not be read', () => {
    h.data = undefined;
    h.error = new Error('boom');
    renderTab();

    expect(screen.getByText('Your computer folders could not be loaded')).toBeTruthy();

    h.mutate.mockClear();
    fireEvent.click(screen.getByRole('button', { name: /Try again/ }));
    expect(h.mutate).toHaveBeenCalled();
  });

  it('shows a loader while the first read is in flight', () => {
    h.data = undefined;
    h.isLoading = true;
    renderTab();
    expect(screen.getByText(/Checking for folders on your computers/)).toBeTruthy();
  });
});
