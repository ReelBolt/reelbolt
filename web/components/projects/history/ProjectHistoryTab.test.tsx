// @vitest-environment jsdom
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

const H = vi.hoisted(() => ({
  pages: {
    data: [] as unknown[] | undefined,
    error: undefined as unknown,
    isLoading: false,
    isValidating: false,
    mutate: vi.fn(),
    size: 1,
    setSize: vi.fn().mockResolvedValue(undefined),
  },
  create: vi.fn(),
}));

vi.mock('@/lib/hooks/use-project-history', () => ({
  HISTORY_PAGE_SIZE: 50,
  useProjectHistoryVersionPages: () => H.pages,
}));
vi.mock('@/lib/api/project-history', () => ({
  createProjectHistoryVersion: (...args: unknown[]) => H.create(...args),
}));

import { ProjectHistoryTab } from './ProjectHistoryTab';

const page = (versions: unknown[], canEdit = true, canDelete = true) => ({
  versions,
  historyBytes: 0,
  retentionDays: 30,
  canEdit,
  canDelete,
});

const version = (number: number) => ({
  id: `v${number}`,
  number,
  name: `Version ${number}`,
  cause: 'named',
  createdAt: '2026-01-01T00:00:00Z',
  expiresAt: null,
  createdByName: 'Ada',
  itemCount: 1,
  totalBytes: 10,
});

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }),
  });
});
beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  H.pages.data = [page([version(2)])];
  H.pages.error = undefined;
  H.pages.isLoading = false;
  H.pages.isValidating = false;
  H.pages.size = 1;
  H.pages.mutate.mockReset().mockResolvedValue(undefined);
  H.pages.setSize.mockReset().mockResolvedValue(undefined);
  H.create.mockReset();
});
afterEach(cleanup);

const renderTab = () => render(<MantineProvider><ProjectHistoryTab projectId="p1" /></MantineProvider>);

describe('History tab gating', () => {
  it('shows Save version when the server says the caller can edit', () => {
    H.pages.data = [page([version(1)], true, false)];
    renderTab();
    expect(screen.getByRole('button', { name: 'Save version' })).toBeTruthy();
    expect(screen.getByText('Version 1')).toBeTruthy();
  });

  it('hides Save version when the server says the caller cannot edit', () => {
    H.pages.data = [page([version(1)], false, false)];
    renderTab();
    expect(screen.queryByRole('button', { name: 'Save version' })).toBeNull();
  });

  it('loads the next page with Load older when the last page is full', () => {
    const versions = Array.from({ length: 50 }, (_, i) => version(50 - i));
    H.pages.data = [page(versions)];
    renderTab();
    fireEvent.click(screen.getByRole('button', { name: 'Load older versions' }));
    expect(H.pages.setSize).toHaveBeenCalledWith(2);
  });

  it('shows no Load older button on a short page', () => {
    renderTab();
    expect(screen.queryByRole('button', { name: 'Load older versions' })).toBeNull();
  });

  it('shows the name rule, not "cannot be restored", for an invalid version name', async () => {
    H.create.mockRejectedValue({ status: 400, code: 'INVALID_NAME' });
    renderTab();
    fireEvent.click(screen.getByRole('button', { name: 'Save version' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'Bad‍name' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save version' }));
    await waitFor(() => expect(screen.getByRole('alert').textContent).toMatch(/control or format characters/));
    expect(screen.getByRole('alert').textContent).not.toMatch(/cannot be restored/);
  });
});
