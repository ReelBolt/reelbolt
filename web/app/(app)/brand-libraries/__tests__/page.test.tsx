// @vitest-environment jsdom
/**
 * P4-06: the organization brand-library page (SPEC §16.1). The library hooks are mocked like the
 * project profile tab, and the mutating API functions are mocks, so the tests pin the page's own
 * behaviour: list, create, rename, delete-with-confirm, approve gated to Owner/Admin, and Billing
 * told the surface is not available.
 */
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import type { DesignLibrarySummary, DesignVersionSummary } from '@/lib/types/design-system';

const H = vi.hoisted(() => ({
  role: 'Owner' as string | null,
  libraries: [] as unknown[],
  versions: [] as unknown[],
  create: vi.fn(),
  rename: vi.fn(),
  remove: vi.fn(),
  approve: vi.fn(),
  mutateLibraries: vi.fn(),
  mutateVersions: vi.fn(),
}));

vi.mock('@/lib/hooks/use-auth', () => ({
  useAuth: () => ({
    user: { orgRole: H.role },
    isOrgAdmin: H.role === 'Owner' || H.role === 'Admin',
    isPlatformAdmin: false,
  }),
}));
vi.mock('@/lib/hooks/use-design-libraries', () => ({
  useDesignLibraries: () => ({
    data: H.libraries,
    error: undefined,
    isLoading: false,
    mutate: H.mutateLibraries,
  }),
  useDesignLibraryVersions: () => ({
    data: H.versions,
    error: undefined,
    isLoading: false,
    mutate: H.mutateVersions,
  }),
}));
vi.mock('@/lib/api/design-system', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api/design-system')>();
  return {
    ...actual,
    createDesignLibrary: (...args: unknown[]) => H.create(...args),
    renameDesignLibrary: (...args: unknown[]) => H.rename(...args),
    deleteDesignLibrary: (...args: unknown[]) => H.remove(...args),
    approveDesignLibraryVersion: (...args: unknown[]) => H.approve(...args),
  };
});

import BrandLibrariesPage from '../page';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({
      matches: false,
      addListener() {},
      removeListener() {},
      addEventListener() {},
      removeEventListener() {},
    }),
  });
  global.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as never;
  Element.prototype.scrollIntoView = vi.fn();
});

beforeEach(() => {
  H.role = 'Owner';
  H.libraries = [];
  H.versions = [];
  H.create.mockReset().mockResolvedValue(library());
  H.rename.mockReset().mockResolvedValue(library());
  H.remove.mockReset().mockResolvedValue(undefined);
  H.approve.mockReset().mockResolvedValue(undefined);
  H.mutateLibraries.mockReset().mockResolvedValue(undefined);
  H.mutateVersions.mockReset().mockResolvedValue(undefined);
});

afterEach(cleanup);

function library(over: Partial<DesignLibrarySummary> = {}): DesignLibrarySummary {
  return {
    id: 'lib-1',
    organizationId: 'org-1',
    name: 'Acme',
    designSystemId: 'ds-1',
    designSystem: {
      id: 'ds-1',
      kind: 'design',
      name: 'Acme',
      currentVersion: {
        id: 'v-1',
        versionNumber: 3,
        digest: 'digest',
        approvedAt: '2026-01-01T00:00:00Z',
      },
      latestDraft: null,
      pendingProposals: 0,
      parentLibrary: null,
    },
    ...over,
  };
}

function version(over: Partial<DesignVersionSummary> = {}): DesignVersionSummary {
  return {
    id: 'v-4',
    designSystemId: 'ds-1',
    versionNumber: 4,
    state: 'Draft',
    origin: 'Manual',
    documentDigest: 'digest',
    baseVersionId: null,
    workflowExecutionId: null,
    createdByUserId: 'u-1',
    approvedByUserId: null,
    approvedAt: null,
    createdAt: '2026-01-01T00:00:00Z',
    ...over,
  };
}

function renderPage() {
  render(
    <MantineProvider>
      <Notifications />
      <BrandLibrariesPage />
    </MantineProvider>,
  );
}

describe('BrandLibrariesPage', () => {
  it('lists libraries with their current version', () => {
    H.libraries = [library()];
    renderPage();

    expect(screen.getByText('Acme')).toBeTruthy();
    expect(screen.getByText('v3')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'New library' })).toBeTruthy();
  });

  it('creates a library from the modal', async () => {
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'New library' }));
    fireEvent.change(await screen.findByLabelText('Name'), { target: { value: 'Acme Two' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create library' }));

    await waitFor(() => expect(H.create).toHaveBeenCalledWith('Acme Two', undefined));
    await waitFor(() => expect(H.mutateLibraries).toHaveBeenCalled());
  });

  it('renames a library', async () => {
    H.libraries = [library()];
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'Rename' }));
    fireEvent.change(await screen.findByLabelText('Name'), { target: { value: 'Acme Two' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(H.rename).toHaveBeenCalledWith('lib-1', 'Acme Two'));
  });

  it('deletes only after the confirm dialog is accepted', async () => {
    H.libraries = [library()];
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'Delete' }));
    expect(H.remove).not.toHaveBeenCalled();

    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete library' }));

    await waitFor(() => expect(H.remove).toHaveBeenCalledWith('lib-1'));
  });

  // Rewritten (WEB-6): this test used to expect a single click on Approve to approve straight away.
  // Approving a library version changes the brand of every linked project, so it now asks first.
  it('lets an Owner approve a Draft version, only after a confirm that warns about linked projects', async () => {
    H.role = 'Owner';
    H.libraries = [library()];
    H.versions = [version({ state: 'Draft', versionNumber: 4 })];
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'Versions' }));
    expect(await screen.findByText('Version 4 · Draft')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Approve' }));
    expect(H.approve).not.toHaveBeenCalled();

    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toMatch(/every project linked to this library/i);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Approve version 4' }));

    await waitFor(() => expect(H.approve).toHaveBeenCalledWith('lib-1', 'v-4'));
    await waitFor(() => expect(H.mutateVersions).toHaveBeenCalled());
  });

  it('backs out of the approve confirm without approving', async () => {
    H.role = 'Owner';
    H.libraries = [library()];
    H.versions = [version({ state: 'Draft', versionNumber: 4 })];
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'Versions' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Approve' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));

    expect(H.approve).not.toHaveBeenCalled();
  });

  it('hides every write control from a Member but keeps the history readable', async () => {
    H.role = 'Member';
    H.libraries = [library()];
    H.versions = [version()];
    renderPage();

    expect(screen.queryByRole('button', { name: 'New library' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Rename' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Delete' })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Versions' }));
    expect(await screen.findByText('Version 4 · Draft')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Approve' })).toBeNull();
    expect(screen.getByText('Only an Owner or Admin can approve a version.')).toBeTruthy();
  });

  it('tells a Billing caller the surface is not available', () => {
    H.role = 'Billing';
    renderPage();

    expect(screen.getByTestId('brand-libraries-billing-unavailable')).toBeTruthy();
    expect(screen.getByText('Brand libraries are not available for the Billing role.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'New library' })).toBeNull();
  });
});
