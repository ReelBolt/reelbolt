// @vitest-environment jsdom
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

const H = vi.hoisted(() => ({
  version: { data: undefined as unknown, error: undefined as unknown, isLoading: false, mutate: vi.fn() },
  list: { data: undefined as unknown },
  changes: { data: { changes: [], unchangedCount: 0, truncated: false }, error: undefined, isLoading: false, mutate: vi.fn() },
}));

vi.mock('@/lib/hooks/use-project-history', () => ({
  useProjectHistoryVersion: () => H.version,
  useProjectHistoryVersions: () => H.list,
  useProjectHistoryChanges: () => H.changes,
}));

import { VersionDetail } from './VersionDetail';

const detail = {
  id: 'v1',
  number: 1,
  name: 'Before the review',
  cause: 'named',
  createdAt: '2026-01-01T00:00:00Z',
  expiresAt: null,
  createdByName: 'Ada',
  itemCount: 1,
  totalBytes: 10,
  items: [
    { kind: 'File', key: 'f1', name: 'intro.mp4', category: null, mimeType: 'video/mp4', sizeBytes: 10, restorable: true, notRestorableReason: null },
  ],
};

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }),
  });
});
beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  H.version.data = detail;
  H.version.error = undefined;
  H.version.isLoading = false;
  H.list.data = { versions: [], historyBytes: 0, retentionDays: 30, canEdit: true, canDelete: true };
});
afterEach(cleanup);

const renderDetail = () => render(
  <MantineProvider>
    <VersionDetail projectId="p1" versionId="v1" permissions={{ canEdit: true, canDelete: true }} onBack={() => {}} onChanged={() => {}} />
  </MantineProvider>,
);

describe('Version detail gating', () => {
  it('shows restore-project and delete-version only when the server says the caller can delete', () => {
    renderDetail();
    expect(screen.getByRole('button', { name: 'Restore whole project' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Delete version' })).toBeTruthy();
  });

  it('hides project-level actions and says who can delete when the caller cannot delete', () => {
    H.list.data = { versions: [], historyBytes: 0, retentionDays: 30, canEdit: true, canDelete: false };
    renderDetail();
    expect(screen.queryByRole('button', { name: 'Restore whole project' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Delete version' })).toBeNull();
    expect(screen.getByText(/Only an owner, an administrator/)).toBeTruthy();
  });

  it('disables restoring an item when the caller cannot edit', () => {
    H.list.data = { versions: [], historyBytes: 0, retentionDays: 30, canEdit: false, canDelete: false };
    renderDetail();
    expect((screen.getByRole('button', { name: 'Restore intro.mp4' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('enables restoring a restorable item when the caller can edit', () => {
    renderDetail();
    expect((screen.getByRole('button', { name: 'Restore intro.mp4' }) as HTMLButtonElement).disabled).toBe(false);
  });
});
