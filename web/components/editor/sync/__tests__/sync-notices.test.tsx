// @vitest-environment jsdom
/**
 * The sync notices (Wave 1 R02.1/R02.2/R02.5): every sentence a person reads, and every button reaches the
 * sync client. The client is a stub: its own behaviour is tested in lib/editing/sync/__tests__.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { SaveState, SyncClient } from '@/lib/editing/sync';
import { SyncNotices } from '../SyncNotices';

const login = vi.fn();
vi.mock('@/lib/api/auth', () => ({ login: (...args: unknown[]) => login(...args) }));

(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = class {
  observe() {}
  unobserve() {}
  disconnect() {}
};
beforeEach(() => {
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => false }) as MediaQueryList;
  login.mockReset();
});
afterEach(cleanup);

const base: SaveState = { status: 'saved', revision: 5, lastSavedAt: null, conflictRevision: null, violations: [], message: null, attempt: 0, reasons: [] };

function stub() {
  return {
    restoreRecovered: vi.fn(async () => 'sent'),
    discardRecovered: vi.fn(),
    resumeAfterSignIn: vi.fn(async () => undefined),
    saveAsCopy: vi.fn(async () => ({ id: 'copy-1', name: 'Film (my edits)' })),
    getLocalEntry: vi.fn(() => ({ version: 1, localDoc: {} })),
    discardLocalEdits: vi.fn(),
    discardQueue: vi.fn(),
    getQueuedEntry: vi.fn(() => null),
    takeOver: vi.fn(async () => undefined),
  };
}

function mount(save: SaveState, sync = stub(), isPhone = false, onReload = vi.fn()) {
  render(
    <MantineProvider>
      <SyncNotices sync={sync as unknown as SyncClient} save={save} isPhone={isPhone} projectId="p" docName="Film" onReload={onReload} />
    </MantineProvider>,
  );
  return { sync, onReload };
}

describe('SyncNotices', () => {
  it('view-only says it plainly', () => {
    mount({ ...base, viewOnly: true });
    expect(screen.getByTestId('view-only-banner').textContent).toContain('You can look but not change this timeline.');
  });

  it('recovery offers Restore and Discard, and each reaches the client', () => {
    const { sync } = mount({ ...base, recovery: { savedAt: 1 } });
    expect(screen.getByTestId('recovery-banner').textContent).toContain('We recovered edits from your last visit');
    fireEvent.click(screen.getByTestId('recovery-restore'));
    expect(sync.restoreRecovered).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByTestId('recovery-discard'));
    expect(sync.discardRecovered).toHaveBeenCalledTimes(1);
  });

  it('another tab: the notice, and Edit here instead takes over then reloads', async () => {
    const { sync, onReload } = mount({ ...base, status: 'otherTab' });
    expect(screen.getByTestId('other-tab-banner').textContent).toContain('Open in another tab');
    fireEvent.click(screen.getByTestId('take-over'));
    await waitFor(() => expect(onReload).toHaveBeenCalledTimes(1));
    expect(sync.takeOver).toHaveBeenCalledTimes(1);
  });

  it('a conflict opens the choice dialog; Keep my copy saves a copy and offers to open it', async () => {
    const { sync } = mount({ ...base, status: 'conflict', conflictRevision: 9, rebaseReason: 'same clip' });
    expect(screen.getByTestId('conflict-dialog')).toBeTruthy();
    expect(document.body.textContent).not.toMatch(/revision|rev \d/i);
    fireEvent.click(screen.getByTestId('conflict-keep-copy'));
    await waitFor(() => expect(screen.getByTestId('conflict-copy-done').textContent).toContain('Film (my edits)'));
    expect(sync.saveAsCopy).toHaveBeenCalledTimes(1);
    expect(screen.getByTestId('conflict-open-copy').getAttribute('href')).toMatch(/timeline\/copy-1/);
  });

  it('Use the latest hands over a recovery file, forgets the local edits and reloads', () => {
    URL.createObjectURL = vi.fn(() => 'blob:x');
    URL.revokeObjectURL = vi.fn();
    const { sync, onReload } = mount({ ...base, status: 'conflict', conflictRevision: 9, rebaseReason: 'same clip' });
    fireEvent.click(screen.getByTestId('conflict-use-latest'));
    expect(sync.getLocalEntry).toHaveBeenCalled();
    expect(sync.discardLocalEdits).toHaveBeenCalled();
    expect(onReload).toHaveBeenCalled();
  });

  it('an expired session opens the sign-in dialog; signing in resumes saving without a reload', async () => {
    login.mockResolvedValue({ mustChangePassword: false });
    const { sync, onReload } = mount({ ...base, status: 'authExpired', signInRequestedAt: 1 });
    expect(screen.getByTestId('sign-in-dialog')).toBeTruthy();
    fireEvent.change(screen.getByTestId('sign-in-email'), { target: { value: 'me@example.com' } });
    fireEvent.change(screen.getByTestId('sign-in-password'), { target: { value: 'pw' } });
    fireEvent.click(screen.getByTestId('sign-in-submit'));
    await waitFor(() => expect(sync.resumeAfterSignIn).toHaveBeenCalledTimes(1));
    expect(login).toHaveBeenCalledWith({ email: 'me@example.com', password: 'pw' });
    expect(onReload).not.toHaveBeenCalled();
  });

  it('a different account: says whose edits were not sent; Download and Discard both drop them and reload', () => {
    const first = mount({ ...base, status: 'authExpired', signInRequestedAt: 1, accountChanged: { previous: 'me@example.com' } });
    expect(screen.getByTestId('account-changed-message').textContent).toBe('You signed in as a different account. The unsaved edits belong to me@example.com and were not sent.');
    expect(screen.queryByTestId('sign-in-dialog')).toBeNull();
    fireEvent.click(screen.getByTestId('account-changed-discard'));
    expect(first.sync.discardLocalEdits).toHaveBeenCalled();
    expect(first.onReload).toHaveBeenCalled();
    cleanup();
    const second = mount({ ...base, status: 'authExpired', signInRequestedAt: 1, accountChanged: { previous: 'me@example.com' } });
    URL.createObjectURL = vi.fn(() => 'blob:x');
    URL.revokeObjectURL = vi.fn();
    fireEvent.click(screen.getByTestId('account-changed-download'));
    expect(second.sync.getLocalEntry).toHaveBeenCalled();
    expect(second.sync.discardLocalEdits).toHaveBeenCalled();
    expect(second.onReload).toHaveBeenCalled();
  });

  it('a wrong password is said in words and nothing resumes', async () => {
    const { ApiError } = await import('@/lib/api/client');
    login.mockRejectedValue(new ApiError('unauthorized', 401));
    const { sync } = mount({ ...base, status: 'authExpired', signInRequestedAt: 1 });
    fireEvent.change(screen.getByTestId('sign-in-email'), { target: { value: 'me@example.com' } });
    fireEvent.change(screen.getByTestId('sign-in-password'), { target: { value: 'bad' } });
    fireEvent.click(screen.getByTestId('sign-in-submit'));
    await waitFor(() => expect(screen.getByTestId('sign-in-error').textContent).toBe('That email or password is not right.'));
    expect(sync.resumeAfterSignIn).not.toHaveBeenCalled();
  });

  it('phone: the sheet buttons are at least 44 px tall', () => {
    mount({ ...base, status: 'conflict', conflictRevision: 9, rebaseReason: 'same clip' }, stub(), true);
    for (const id of ['conflict-keep-copy', 'conflict-use-latest']) {
      const h = (screen.getByTestId(id) as HTMLElement).style.height || getComputedStyle(screen.getByTestId(id)).height;
      expect(h).toMatch(/44px|2\.75rem/);
    }
  });

  it('RM.7 phone: the conflict is a compact bar (no modal) and Keep my copy works', async () => {
    const { sync } = mount({ ...base, status: 'conflict', conflictRevision: 9, rebaseReason: 'same clip' }, stub(), true);
    const bar = screen.getByTestId('conflict-dialog');
    expect(bar.getAttribute('role')).toBe('alert');
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(bar.textContent).not.toMatch(/revision|GUID|op /i);
    fireEvent.click(screen.getByTestId('conflict-keep-copy'));
    await waitFor(() => expect(screen.getByTestId('conflict-copy-done').textContent).toContain('Film (my edits)'));
    expect(sync).toBeTruthy();
  });
});
