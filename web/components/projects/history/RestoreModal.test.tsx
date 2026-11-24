// @vitest-environment jsdom
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

const H = vi.hoisted(() => ({
  mutate: vi.fn(),
  refreshVersions: vi.fn(),
  preview: vi.fn(),
  restore: vi.fn(),
}));

vi.mock('swr', () => ({ useSWRConfig: () => ({ mutate: H.mutate }) }));
vi.mock('@/lib/hooks/use-project-history', () => ({
  useProjectHistoryVersions: () => ({ mutate: H.refreshVersions }),
}));
vi.mock('@/lib/api/project-history', () => ({
  previewProjectHistoryRestore: (...args: unknown[]) => H.preview(...args),
  restoreProjectHistory: (...args: unknown[]) => H.restore(...args),
}));

import { RestoreModal, RestorePreviewList, RestoreResultView, projectRestoreRevalidationKeys } from './RestoreModal';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }),
  });
});
beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  H.mutate.mockReset().mockResolvedValue(undefined);
  H.refreshVersions.mockReset().mockResolvedValue({ versions: [], historyBytes: 0, retentionDays: 30, canEdit: true, canDelete: true });
  H.preview.mockReset();
  H.restore.mockReset();
});
afterEach(cleanup);

const wrap = (node: React.ReactNode) => render(<MantineProvider>{node}</MantineProvider>);

describe('restore preview and result', () => {
  it('lists changes, removals and blocked items with plain reasons', () => {
    wrap(
      <RestorePreviewList
        preview={{
          scope: 'project',
          estimateBytes: 0,
          changes: [
            { kind: 'File', key: '1', name: 'intro.mp4', change: 'changed' },
            { kind: 'File', key: '2', name: 'extra.mp4', change: 'removed' },
          ],
          blocked: [
            { kind: 'File', key: '3', name: 'gone.txt', reason: 'PATH_TAKEN' },
            { kind: 'Workflow', key: '4', name: 'Flow', reason: 'WORKFLOW_INVALID' },
          ],
        }}
      />,
    );
    expect(screen.getByText('Will change')).toBeTruthy();
    expect(screen.getByText('Will be removed')).toBeTruthy();
    expect(screen.getByText(/Another file already uses its path/)).toBeTruthy();
    expect(screen.getByText(/no longer passes today's checks/)).toBeTruthy();
  });

  it('shows restored, removed and skipped items with reasons and the safety version', () => {
    wrap(
      <RestoreResultView
        safetyName="Before a restore"
        result={{
          safetyVersionId: 'x',
          restored: [{ kind: 'File', key: '1', name: 'a.txt' }],
          removed: [{ kind: 'File', key: '2', name: 'b.txt' }],
          skipped: [{ kind: 'File', key: '3', name: 'c.txt', reason: 'STORAGE_QUOTA_EXCEEDED' }],
          notices: [{ kind: 'Workflow', key: '4', code: 'EGRESS_CONSENT_RESET' }],
        }}
      />,
    );
    expect(screen.getByText(/Before a restore, was saved first|A safety version, Before a restore/)).toBeTruthy();
    expect(screen.getByText(/not enough storage left/)).toBeTruthy();
    expect(screen.getByText(/turned off until you allow it again/)).toBeTruthy();
  });

  it('lists every revalidated sibling-tab key for a project', () => {
    expect(projectRestoreRevalidationKeys('p1')).toEqual([
      '/api/v1/projects',
      '/api/v1/projects/p1',
      '/api/v1/projects/p1/files',
      '/api/v1/projects/p1/workflows',
      '/api/v1/projects/p1/edit-timelines',
      '/api/v1/projects/p1/pronunciation',
    ]);
  });

  it('revalidates files, workflows, timelines, pronunciation, the project and the design system after a restore', async () => {
    H.preview.mockResolvedValue({
      scope: 'project',
      changes: [{ kind: 'File', key: '1', name: 'intro.mp4', change: 'changed' }],
      blocked: [],
      estimateBytes: 0,
    });
    H.restore.mockResolvedValue({ safetyVersionId: 's1', restored: [], removed: [], skipped: [], notices: [] });
    wrap(
      <RestoreModal
        opened
        onClose={() => {}}
        projectId="p1"
        versionId="v1"
        request={{ scope: 'project' }}
        onRestored={() => {}}
      />,
    );
    await screen.findByTestId('restore-preview');
    fireEvent.click(screen.getByRole('button', { name: 'Restore' }));

    await waitFor(() => expect(H.restore).toHaveBeenCalled());
    for (const key of projectRestoreRevalidationKeys('p1')) {
      expect(H.mutate).toHaveBeenCalledWith(key);
    }
    const matcher = H.mutate.mock.calls.map((call) => call[0]).find((key) => typeof key === 'function') as (key: string) => boolean;
    expect(matcher('/api/v1/projects/p1/design-system')).toBe(true);
    expect(matcher('/api/v1/projects/p1/design-system/versions?kind=design')).toBe(true);
    expect(matcher('/api/v1/projects/p2/design-system')).toBe(false);
    expect(H.refreshVersions).toHaveBeenCalled();
  });
});
