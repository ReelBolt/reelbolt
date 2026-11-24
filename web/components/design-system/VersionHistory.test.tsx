// @vitest-environment jsdom
/**
 * Two-version compare in History (P3-04, SPEC §9/§13.3): picking an older and a newer version calls
 * `getVersionDiff(projectId, newerId, olderId)` and renders the side-by-side `VersionDiff`.
 */
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
vi.mock('@/lib/api/design-system', () => ({
  getVersionDiff: vi.fn(),
  restoreVersion: vi.fn(),
}));
vi.mock('@/lib/hooks/use-design-system', () => ({ useDesignVersions: vi.fn() }));
const revalidate = vi.hoisted(() => vi.fn());
vi.mock('@/lib/hooks/use-revalidate-design-system', () => ({ useRevalidateDesignSystem: () => revalidate }));

import { getVersionDiff, restoreVersion } from '@/lib/api/design-system';
import { useDesignVersions } from '@/lib/hooks/use-design-system';
import type { DesignVersionSummary } from '@/lib/types/design-system';
import { VersionHistory } from './VersionHistory';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }),
  });
  global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as never;
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function swrResult(data: unknown, error: unknown = undefined, isLoading = false): never {
  return { data, error, isLoading, mutate: vi.fn() } as never;
}

function versionSummary(over: Partial<DesignVersionSummary> = {}): DesignVersionSummary {
  return {
    id: 'v-1',
    designSystemId: 'ds-1',
    versionNumber: 1,
    state: 'Approved',
    origin: 'Import',
    documentDigest: 'digest',
    baseVersionId: null,
    workflowExecutionId: null,
    createdByUserId: 'u-1',
    approvedByUserId: 'u-1',
    approvedAt: '2026-01-01T00:00:00Z',
    createdAt: '2026-01-01T00:00:00Z',
    ...over,
  };
}

beforeEach(() => {
  vi.mocked(useDesignVersions).mockReturnValue(
    swrResult([
      versionSummary({ id: 'v-2', versionNumber: 2, state: 'Draft', origin: 'Manual' }),
      versionSummary({ id: 'v-1', versionNumber: 1 }),
    ]),
  );
});

describe('VersionHistory compare', () => {
  it('compares the older and newer version and renders the diff', async () => {
    vi.mocked(getVersionDiff).mockResolvedValue({
      counts: { added: 0, removed: 0, changed: 1 },
      added: [],
      removed: [],
      changed: [{ path: 'color.primary', before: { hex: '#6d28d9' }, after: { hex: '#7c3aed' } }],
    });

    render(
      <MantineProvider>
        <VersionHistory projectId="p1" canEdit />
      </MantineProvider>,
    );

    fireEvent.click(screen.getByLabelText('Select Version 1 to compare'));
    fireEvent.click(screen.getByLabelText('Select Version 2 to compare'));

    await waitFor(() => expect(getVersionDiff).toHaveBeenCalledWith('p1', 'v-2', 'v-1'));
    expect(await screen.findByTestId('version-diff')).toBeTruthy();
    expect(screen.getByText('#6d28d9')).toBeTruthy();
    expect(screen.getByText('#7c3aed')).toBeTruthy();
  });
});

describe('VersionHistory failure and freshness', () => {
  it('says the history could not be loaded, with a retry, instead of "No versions yet"', () => {
    const mutate = vi.fn();
    vi.mocked(useDesignVersions).mockReturnValue({ data: undefined, error: new Error('boom'), isLoading: false, mutate } as never);
    render(<MantineProvider><VersionHistory projectId="p1" canEdit /></MantineProvider>);

    expect(screen.queryByText('No versions yet.')).toBeNull();
    expect(screen.getByText(/could not load/i)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(mutate).toHaveBeenCalled();
  });

  it('restoring a version revalidates the draft banner and the rest of the profile', async () => {
    vi.mocked(restoreVersion).mockResolvedValue(undefined);
    render(<MantineProvider><VersionHistory projectId="p1" canEdit /></MantineProvider>);

    fireEvent.click(screen.getAllByRole('button', { name: 'Restore' })[0]);

    await waitFor(() => expect(restoreVersion).toHaveBeenCalled());
    await waitFor(() => expect(revalidate).toHaveBeenCalled());
  });

  it('without edit access Restore is disabled and a visible sentence says why', () => {
    render(<MantineProvider><VersionHistory projectId="p1" canEdit={false} /></MantineProvider>);

    const restore = screen.getAllByRole('button', { name: 'Restore' })[0] as HTMLButtonElement;
    expect(restore.disabled).toBe(true);
    const reasonId = restore.getAttribute('aria-describedby');
    expect(reasonId).toBeTruthy();
    expect(document.getElementById(reasonId as string)?.textContent).toContain('edit access');
  });
});
