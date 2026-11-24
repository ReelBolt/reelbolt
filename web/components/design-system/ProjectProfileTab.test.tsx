// @vitest-environment jsdom
/**
 * The read-only brand-and-product tab (SPEC §9/§17). The hooks are mocked exactly like the project
 * page mocks `use-files`; the fixtures are the design and product-map documents the server exposes.
 */
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

const H = vi.hoisted(() => ({ canEdit: true, orgRole: undefined as string | undefined }));

vi.mock('@/lib/hooks/use-auth', () => ({
  useAuth: () => ({
    isPlatformAdmin: H.canEdit,
    isOrgAdmin: false,
    user: H.orgRole ? { orgRole: H.orgRole } : undefined,
  }),
}));
vi.mock('@/lib/hooks/use-design-system', () => ({
  useDesignSystem: vi.fn(),
  useDesignVersions: vi.fn(),
  useDesignVersion: vi.fn(),
  useDesignSources: vi.fn(),
  useDesignProposals: vi.fn(),
  useVersionDiff: vi.fn(),
}));
// The overview now hosts the org-library link picker (P4-06); it reads the org library list.
vi.mock('@/lib/hooks/use-design-libraries', () => ({
  useDesignLibraries: () => ({ data: [], isLoading: false }),
  useDesignLibraryVersions: () => ({ data: [], isLoading: false }),
}));

vi.mock('@/lib/api/design-system', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/api/design-system')>()),
  buildProfile: vi.fn(),
}));

import { ApiError } from '@/lib/api/client';
import { buildProfile } from '@/lib/api/design-system';
import {
  useDesignProposals,
  useDesignSources,
  useDesignSystem,
  useDesignVersion,
  useDesignVersions,
  useVersionDiff,
} from '@/lib/hooks/use-design-system';
import type {
  DesignDocument,
  DesignSource,
  DesignSystemSummary,
  DesignVersion,
  DesignVersionSummary,
} from '@/lib/types/design-system';
import { ProjectProfileTab } from './ProjectProfileTab';
import { BUILD_MODAL_BODY, ERROR_403, ERROR_404, IMPORT_HINT } from './copy';
import { en } from './__tests__/en';

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
  H.canEdit = true;
  H.orgRole = undefined;
});

/** A structural stand-in for the SWR response the mocked hooks return. */
function swrResult(data: unknown, error: unknown = undefined, isLoading = false): never {
  return { data, error, isLoading } as never;
}

const designDocument: DesignDocument = {
  schema: 'reelbolt.design-system/1',
  name: 'Acme',
  color: {
    brand: {
      primary: {
        $type: 'color',
        $value: { hex: '#6d28d9' },
        $extensions: {
          'com.reelbolt': {
            conf: 0.95,
            prov: { method: 'static', importer: 'css-theme', loc: 'app/globals.css:12', sourceId: null },
          },
        },
      },
    },
    secondary: { $type: 'color', $value: { hex: '#0ea5e9' } },
  },
  typography: {
    family: { heading: { $type: 'fontFamily', $value: ['Inter'] } },
    weight: { heading: { $type: 'fontWeight', $value: 700 } },
  },
  voice: { summary: 'Friendly and direct.', tone: ['warm', 'plain'] },
};

const productDocument: DesignDocument = {
  schema: 'reelbolt.product-map/1',
  name: 'Web app',
  framework: {
    name: 'next',
    version: '15.1',
    language: 'typescript',
    styling: ['tailwind'],
    packageManager: 'pnpm',
    projectType: 'web app',
  },
  routes: [{ path: '/', file: 'app/page.tsx', kind: 'page', requiresAuth: false, source: 'static' }],
  components: [
    {
      name: 'PriceCard',
      path: 'components/PriceCard.tsx',
      responsibility: 'Shows one plan.',
      keyProps: ['plan'],
      core: false,
      source: 'static',
    },
  ],
};

function summary(over: Partial<DesignSystemSummary> = {}): DesignSystemSummary {
  return {
    id: 'ds-1',
    kind: 'design',
    name: 'Acme',
    currentVersion: { id: 'v-design', versionNumber: 1, digest: 'digest', approvedAt: '2026-01-01T00:00:00Z' },
    latestDraft: null,
    pendingProposals: 0,
    parentLibrary: null,
    ...over,
  };
}

const designSummary = summary();
const productSummary = summary({
  id: 'ds-2',
  kind: 'product_map',
  name: 'Web app',
  currentVersion: { id: 'v-product', versionNumber: 1, digest: 'digest', approvedAt: '2026-01-01T00:00:00Z' },
});

function fullVersion(over: Partial<DesignVersion> = {}): DesignVersion {
  return {
    id: 'v-design',
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
    document: designDocument,
    coverage: null,
    sources: [],
    ...over,
  };
}

const designVersion = fullVersion();
const productVersion = fullVersion({ id: 'v-product', designSystemId: 'ds-2', document: productDocument });

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

function mockFixtures() {
  vi.mocked(useDesignSystem).mockReturnValue(swrResult({ design: designSummary, productMap: productSummary }));
  vi.mocked(useDesignVersion).mockImplementation(((_projectId: string, versionId: string | null) => {
    if (versionId === 'v-design') return swrResult(designVersion);
    if (versionId === 'v-product') return swrResult(productVersion);
    return swrResult(undefined);
  }) as never);
  vi.mocked(useDesignVersions).mockReturnValue(swrResult([versionSummary()]));
  vi.mocked(useDesignSources).mockReturnValue(swrResult([]));
  vi.mocked(useDesignProposals).mockReturnValue(swrResult([]));
}

function renderTab() {
  render(
    <MantineProvider>
      <ProjectProfileTab projectId="p1" />
    </MantineProvider>,
  );
}

beforeEach(() => {
  vi.mocked(useDesignSystem).mockReturnValue(swrResult({ design: null, productMap: null }));
  vi.mocked(useDesignVersion).mockReturnValue(swrResult(undefined));
  vi.mocked(useDesignVersions).mockReturnValue(swrResult([]));
  vi.mocked(useDesignSources).mockReturnValue(swrResult([]));
  vi.mocked(useDesignProposals).mockReturnValue(swrResult([]));
  vi.mocked(useVersionDiff).mockReturnValue(swrResult(undefined));
});

describe('ProjectProfileTab', () => {
  it('renders_overview_from_fixture_data', () => {
    mockFixtures();
    renderTab();

    expect(screen.getAllByText('#6d28d9').length).toBeGreaterThan(0);
    expect(screen.getAllByText('high').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Inter').length).toBeGreaterThan(0);
    expect(screen.getByText('Friendly and direct.')).toBeTruthy();
    expect(screen.getByText('1 route')).toBeTruthy();
    expect(screen.getByText('1 component')).toBeTruthy();
    expect(screen.getByText('PriceCard')).toBeTruthy();
  });

  it('renders_tokens_sub_tab', async () => {
    mockFixtures();
    renderTab();
    fireEvent.click(screen.getByRole('tab', { name: 'Tokens' }));

    expect(screen.getAllByText('color').length).toBeGreaterThan(0);
    expect(screen.getAllByText('primary').length).toBeGreaterThan(0);
    expect(screen.getAllByText('#6d28d9').length).toBeGreaterThan(0);

    fireEvent.click(screen.getByRole('button', { name: 'Provenance for color.brand.primary' }));
    const popover = await screen.findByTestId('token-provenance-color.brand.primary');
    expect(within(popover).getByText('static')).toBeTruthy();
    expect(within(popover).getByText('css-theme')).toBeTruthy();
    expect(within(popover).getByText('app/globals.css:12')).toBeTruthy();
    expect(within(popover).getByText('high')).toBeTruthy();

    expect(screen.queryByText(/lock/i)).toBeNull();
    expect(screen.queryByRole('checkbox')).toBeNull();
  });

  it('renders_sources_sub_tab', () => {
    mockFixtures();
    const source: DesignSource = {
      id: 's-1',
      sourceKind: 'file',
      projectFileId: null,
      path: 'app/globals.css',
      importer: 'css-theme',
      contentSha256: '6d28d9ab1234567890',
      sourceRevision: null,
      sizeBytes: 1024,
      status: 'Current',
      lastCheckedAt: '2026-01-01T00:00:00Z',
      lastChangedAt: null,
    };
    vi.mocked(useDesignSources).mockReturnValue(swrResult([source]));
    renderTab();
    fireEvent.click(screen.getByRole('tab', { name: 'Sources' }));

    expect(screen.getByText('css-theme')).toBeTruthy();
    expect(screen.getByText('6d28d9ab')).toBeTruthy();
    expect(screen.getByText('Current')).toBeTruthy();
  });

  it('renders_history_sub_tab', () => {
    mockFixtures();
    vi.mocked(useDesignVersions).mockReturnValue(swrResult([
      versionSummary({ id: 'v-2', versionNumber: 2, state: 'Draft', origin: 'Manual' }),
      versionSummary({ id: 'v-1', versionNumber: 1, state: 'Approved', origin: 'Import' }),
    ]));
    renderTab();
    fireEvent.click(screen.getByRole('tab', { name: 'History' }));

    expect(screen.getByText('Draft')).toBeTruthy();
    expect(screen.getByText('Approved')).toBeTruthy();
    expect(screen.getByText('Version 2')).toBeTruthy();
  });

  it('renders_empty_state_when_no_profiles', () => {
    H.canEdit = false;
    renderTab();

    expect(screen.getByText('No brand or product profile yet')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Import design files' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Build a product profile' })).toBeTruthy();
  });

  it('renders_404_and_403_error_states', () => {
    vi.mocked(useDesignSystem).mockReturnValue(swrResult(undefined, new ApiError('x', 404)));
    renderTab();
    expect(screen.getByText(en(ERROR_404))).toBeTruthy();

    cleanup();
    vi.mocked(useDesignSystem).mockReturnValue(swrResult(undefined, new ApiError('x', 403)));
    renderTab();
    expect(screen.getByText(en(ERROR_403))).toBeTruthy();
  });

  it('sub_tab_list_keyboard_focus_order_smoke', () => {
    mockFixtures();
    renderTab();

    const tabs = screen.getAllByRole('tab');
    expect(tabs).toHaveLength(5);
    const [overview, tokens] = tabs;
    overview.focus();
    fireEvent.keyDown(overview, { key: 'ArrowRight' });

    expect(tokens.getAttribute('aria-selected')).toBe('true');
    expect(document.activeElement).toBe(tokens);
  });

  it('proposals_sub_tab_badge_shows_total_pending', () => {
    mockFixtures();
    vi.mocked(useDesignSystem).mockReturnValue(
      swrResult({ design: summary({ pendingProposals: 2 }), productMap: productSummary }),
    );
    renderTab();

    expect(screen.getByTestId('proposals-badge').textContent).toBe('2');
  });

  it('rebuild_with_ai_opens_the_build_modal', async () => {
    mockFixtures();
    renderTab();

    fireEvent.click(screen.getByRole('button', { name: 'Rebuild with AI' }));

    expect(await screen.findByText(en(BUILD_MODAL_BODY))).toBeTruthy();
  });

  describe('the first draft (WEB-1): a build or import leaves a draft and no current version', () => {
    // The real API answer after an import on a fresh project: a system row with a draft and
    // `currentVersion: null` (DesignSystemSummaryResponse), and no product map at all.
    const firstDraft = versionSummary({ id: 'draft-1', versionNumber: 1, state: 'Draft', approvedAt: null, approvedByUserId: null });
    const draftOnly = () =>
      swrResult({ design: summary({ currentVersion: null, latestDraft: firstDraft }), productMap: null });

    it('shows the review banner with an enabled Approve instead of the empty state', () => {
      vi.mocked(useDesignSystem).mockReturnValue(draftOnly());
      renderTab();

      expect(screen.getByTestId('draft-review-banner')).toBeTruthy();
      const approve = screen.getByRole('button', { name: 'Approve' }) as HTMLButtonElement;
      expect(approve.disabled).toBe(false);
      expect(screen.getByRole('button', { name: 'Reject' })).toBeTruthy();
    });

    it('keeps Import and Build reachable beside the banner', () => {
      vi.mocked(useDesignSystem).mockReturnValue(draftOnly());
      renderTab();

      expect(screen.getByRole('button', { name: 'Import' })).toBeTruthy();
      expect(screen.getByRole('button', { name: 'Rebuild with AI' })).toBeTruthy();
    });

    it('disables Approve for the Billing role and says why in visible text', () => {
      H.canEdit = false;
      H.orgRole = 'Billing';
      vi.mocked(useDesignSystem).mockReturnValue(draftOnly());
      renderTab();

      const approve = screen.getByRole('button', { name: 'Approve' }) as HTMLButtonElement;
      expect(approve.disabled).toBe(true);
      expect(approve.getAttribute('aria-describedby')).toBeTruthy();
      expect(screen.getAllByText('You need edit access to approve or reject changes.').length).toBeGreaterThan(0);
    });

    it('still shows the plain empty state when there is neither a profile nor a draft', () => {
      renderTab();
      expect(screen.getByText('No brand or product profile yet')).toBeTruthy();
      expect(screen.queryByTestId('draft-review-banner')).toBeNull();
    });
  });

  describe('token locks (WEB-4): locking is only possible on a Draft, so the draft is what gets switches', () => {
    const lockedDoc: DesignDocument = {
      color: {
        brand: {
          primary: {
            $type: 'color',
            $value: { hex: '#6d28d9' },
            $extensions: { 'com.reelbolt': { conf: 0.95, locked: true } },
          },
        },
        secondary: { $type: 'color', $value: { hex: '#0ea5e9' } },
      },
    };

    function mockWithDraft() {
      const draftSummary = versionSummary({ id: 'draft-2', versionNumber: 2, state: 'Draft', approvedAt: null, approvedByUserId: null });
      vi.mocked(useDesignSystem).mockReturnValue(
        swrResult({ design: summary({ latestDraft: draftSummary }), productMap: null }),
      );
      vi.mocked(useDesignVersion).mockImplementation(((_p: string, versionId: string | null) => {
        if (versionId === 'v-design') return swrResult(fullVersion({ document: lockedDoc }));
        if (versionId === 'draft-2') {
          return swrResult(fullVersion({ id: 'draft-2', versionNumber: 2, state: 'Draft', approvedAt: null, document: lockedDoc }));
        }
        return swrResult(undefined);
      }) as never);
    }

    it('renders lock switches for the draft that the API actually returns', () => {
      mockWithDraft();
      renderTab();
      fireEvent.click(screen.getByRole('tab', { name: 'Tokens' }));

      expect(screen.getAllByRole('switch').length).toBe(2);
      expect(screen.getByLabelText('Unlock color.brand.primary')).toBeTruthy();
      expect(screen.getByLabelText('Lock color.secondary')).toBeTruthy();
    });

    it('explains which tree is the draft and which is the approved version', () => {
      mockWithDraft();
      renderTab();
      fireEvent.click(screen.getByRole('tab', { name: 'Tokens' }));

      expect(screen.getByText('Draft version 2 (not approved yet)')).toBeTruthy();
      expect(screen.getByText('Approved version 1')).toBeTruthy();
    });

    it('shows the approved version read-only: a Locked badge and no switch of its own', () => {
      vi.mocked(useDesignSystem).mockReturnValue(swrResult({ design: designSummary, productMap: null }));
      vi.mocked(useDesignVersion).mockImplementation(((_p: string, versionId: string | null) =>
        versionId === 'v-design' ? swrResult(fullVersion({ document: lockedDoc })) : swrResult(undefined)) as never);
      renderTab();
      fireEvent.click(screen.getByRole('tab', { name: 'Tokens' }));

      expect(screen.getByTestId('token-locked-color.brand.primary')).toBeTruthy();
      expect(screen.queryByRole('switch')).toBeNull();
    });
  });

  it('over the build limit, the free import button swaps the build dialog for the import dialog', async () => {
    mockFixtures();
    vi.mocked(buildProfile).mockRejectedValue(
      new ApiError('over', 409, [], { code: 'ESTIMATE_OVER_CAP', estimate: { low: 100, high: 999 }, capCredits: 300 }),
    );
    renderTab();

    fireEvent.click(screen.getByRole('button', { name: 'Rebuild with AI' }));
    fireEvent.click(await screen.findByTestId('build-start'));
    fireEvent.click(await screen.findByRole('button', { name: 'Import design files instead' }));

    expect(await screen.findByText(en(IMPORT_HINT))).toBeTruthy();
    await waitFor(() => expect(screen.queryByTestId('build-start')).toBeNull());
  });

  describe('a failed version fetch is not "no profile" (WEB-10)', () => {
    function failingDocument() {
      const mutate = vi.fn();
      vi.mocked(useDesignSystem).mockReturnValue(swrResult({ design: designSummary, productMap: null }));
      vi.mocked(useDesignVersion).mockReturnValue({
        data: undefined,
        error: new ApiError('boom', 500),
        isLoading: false,
        mutate,
      } as never);
      return mutate;
    }

    it('the Overview shows an error with retry instead of "No brand profile yet."', () => {
      const mutate = failingDocument();
      renderTab();

      expect(screen.queryByText('No brand profile yet.')).toBeNull();
      fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
      expect(mutate).toHaveBeenCalled();
    });

    it('the Tokens tab shows an error with retry instead of "No tokens"', () => {
      failingDocument();
      renderTab();
      fireEvent.click(screen.getByRole('tab', { name: 'Tokens' }));

      expect(screen.queryByText('No tokens in the current design profile.')).toBeNull();
      expect(screen.getAllByRole('button', { name: 'Try again' }).length).toBeGreaterThan(0);
    });
  });
});
