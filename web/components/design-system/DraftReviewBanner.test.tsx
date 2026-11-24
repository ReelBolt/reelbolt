// @vitest-environment jsdom
/**
 * The draft-review banner (P2-22, SPEC §17): only a draft triggers it, and only an editor can act.
 */
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import type { DesignSystemSummary, DesignVersionSummary } from '@/lib/types/design-system';

vi.mock('@/lib/api/design-system', () => ({ approveVersion: vi.fn(), rejectVersion: vi.fn() }));
vi.mock('@/lib/hooks/use-design-system', () => ({ useVersionDiff: vi.fn() }));
const revalidate = vi.hoisted(() => vi.fn());
vi.mock('@/lib/hooks/use-revalidate-design-system', () => ({ useRevalidateDesignSystem: () => revalidate }));

import { approveVersion, rejectVersion } from '@/lib/api/design-system';
import { useVersionDiff } from '@/lib/hooks/use-design-system';
import type { DesignDiff } from '@/lib/types/design-system';
import { DraftReviewBanner } from './DraftReviewBanner';
import { EDIT_ONLY_TOOLTIP } from './copy';
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
});

/** What the diff route returns for draft v2 against approved v1 (the stored `diff_json` shape). */
const diff: DesignDiff = {
  counts: { added: 1, removed: 1, changed: 2 },
  added: [{ path: 'color.accent', after: { hex: '#ff0000' } }],
  removed: [{ path: 'color.legacy', before: { hex: '#000000' } }],
  changed: [
    { path: 'color.brand.primary', before: { hex: '#111111' }, after: { hex: '#222222' } },
    { path: 'typography.family.heading', before: ['Inter'], after: ['Lora'] },
  ],
};

function swr(data: unknown, error: unknown = undefined, isLoading = false): never {
  return { data, error, isLoading, mutate: vi.fn() } as never;
}

function draft(over: Partial<DesignVersionSummary> = {}): DesignVersionSummary {
  return {
    id: 'draft-1',
    designSystemId: 'ds-1',
    versionNumber: 2,
    state: 'Draft',
    origin: 'Import',
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

function summary(over: Partial<DesignSystemSummary> = {}): DesignSystemSummary {
  return {
    id: 'ds-1',
    kind: 'design',
    name: 'Acme',
    currentVersion: null,
    latestDraft: draft(),
    pendingProposals: 0,
    parentLibrary: null,
    ...over,
  };
}

const current = { id: 'cur-1', versionNumber: 1, digest: 'd', approvedAt: '2026-01-01T00:00:00Z' };

function renderBanner(props: Partial<Parameters<typeof DraftReviewBanner>[0]> = {}) {
  const onChanged = vi.fn();
  render(
    <MantineProvider>
      <Notifications />
      <DraftReviewBanner
        projectId="p1"
        summary={summary()}
        kind="design"
        canEdit
        onChanged={onChanged}
        {...props}
      />
    </MantineProvider>,
  );
  return onChanged;
}

describe('DraftReviewBanner', () => {
  beforeEach(() => {
    vi.mocked(useVersionDiff).mockReturnValue(swr(undefined));
  });

  it('names the draft waiting for review', () => {
    renderBanner();
    expect(screen.getByText('Draft v2 ready for review')).toBeTruthy();
  });

  it('approves the draft and refreshes', async () => {
    vi.mocked(approveVersion).mockResolvedValue(undefined);
    const onChanged = renderBanner();

    fireEvent.click(screen.getByRole('button', { name: 'Approve' }));
    await waitFor(() => expect(approveVersion).toHaveBeenCalledWith('p1', 'draft-1'));
    expect(onChanged).toHaveBeenCalled();
  });

  it('approving also revalidates History, the tokens and the editor defaults (WEB-11)', async () => {
    vi.mocked(approveVersion).mockResolvedValue(undefined);
    renderBanner();

    fireEvent.click(screen.getByRole('button', { name: 'Approve' }));
    await waitFor(() => expect(revalidate).toHaveBeenCalled());
  });

  it('rejects the draft', async () => {
    vi.mocked(rejectVersion).mockResolvedValue(undefined);
    renderBanner();

    fireEvent.click(screen.getByRole('button', { name: 'Reject' }));
    await waitFor(() => expect(rejectVersion).toHaveBeenCalledWith('p1', 'draft-1'));
  });

  it('disables both actions and explains why without edit access', async () => {
    renderBanner({ canEdit: false });

    const approve = screen.getByRole('button', { name: 'Approve' }) as HTMLButtonElement;
    const reject = screen.getByRole('button', { name: 'Reject' }) as HTMLButtonElement;
    expect(approve.disabled).toBe(true);
    expect(reject.disabled).toBe(true);

    fireEvent.mouseEnter(approve.parentElement as HTMLElement);
    expect(await screen.findByText(en(EDIT_ONLY_TOOLTIP))).toBeTruthy();
  });

  it('renders nothing when there is no draft', () => {
    renderBanner({ summary: summary({ latestDraft: null }) });
    expect(screen.queryByTestId('draft-review-banner')).toBeNull();
  });

  describe('what approving will change (WEB-6)', () => {
    const over = () => summary({ currentVersion: current });

    it('asks the diff route for draft against the current version', () => {
      vi.mocked(useVersionDiff).mockReturnValue(swr(diff));
      renderBanner({ summary: over() });
      expect(useVersionDiff).toHaveBeenCalledWith('p1', 'draft-1', 'cur-1');
    });

    it('shows added / changed / removed counts and the first token names before Approve', () => {
      vi.mocked(useVersionDiff).mockReturnValue(swr(diff));
      renderBanner({ summary: over() });

      const banner = screen.getByTestId('draft-review-banner');
      expect(banner.textContent).toContain('1 added');
      expect(banner.textContent).toContain('2 changed');
      expect(banner.textContent).toContain('1 removed');
      expect(banner.textContent).toContain('color.brand.primary');
      expect(banner.textContent).toContain('color.accent');
    });

    it('lists at most five names and says how many more there are', () => {
      const many: DesignDiff = {
        counts: { added: 8, removed: 0, changed: 0 },
        added: Array.from({ length: 8 }, (_, i) => ({ path: `color.c${i}`, after: { hex: '#ffffff' } })),
        removed: [],
        changed: [],
      };
      vi.mocked(useVersionDiff).mockReturnValue(swr(many));
      renderBanner({ summary: over() });

      const banner = screen.getByTestId('draft-review-banner');
      expect(banner.textContent).toContain('color.c4');
      expect(banner.textContent).not.toContain('color.c5');
      expect(banner.textContent).toContain('and 3 more');
    });

    it('asks for a confirm step before approving over a current version', async () => {
      vi.mocked(useVersionDiff).mockReturnValue(swr(diff));
      vi.mocked(approveVersion).mockResolvedValue(undefined);
      const onChanged = renderBanner({ summary: over() });

      fireEvent.click(screen.getByRole('button', { name: 'Approve' }));
      expect(approveVersion).not.toHaveBeenCalled();
      expect(screen.getByText(/replaces version 1/i)).toBeTruthy();

      fireEvent.click(screen.getByRole('button', { name: 'Yes, approve version 2' }));
      await waitFor(() => expect(approveVersion).toHaveBeenCalledWith('p1', 'draft-1'));
      expect(onChanged).toHaveBeenCalled();
    });

    it('lets the reviewer back out of the confirm step', () => {
      vi.mocked(useVersionDiff).mockReturnValue(swr(diff));
      renderBanner({ summary: over() });

      fireEvent.click(screen.getByRole('button', { name: 'Approve' }));
      fireEvent.click(screen.getByRole('button', { name: 'Keep reviewing' }));
      expect(approveVersion).not.toHaveBeenCalled();
      expect(screen.getByRole('button', { name: 'Approve' })).toBeTruthy();
    });

    it('still lets the reviewer approve, with a generic warning, when the summary cannot load', () => {
      vi.mocked(useVersionDiff).mockReturnValue(swr(undefined, new Error('boom')));
      renderBanner({ summary: over() });

      expect(screen.getByText(/couldn.t load a summary of the changes/i)).toBeTruthy();
      fireEvent.click(screen.getByRole('button', { name: 'Approve' }));
      expect(screen.getByText(/replaces version 1/i)).toBeTruthy();
    });

    it('does not ask for a confirm step for the very first draft (no current version)', async () => {
      vi.mocked(approveVersion).mockResolvedValue(undefined);
      renderBanner({ summary: summary({ currentVersion: null }) });

      expect(useVersionDiff).toHaveBeenCalledWith('p1', 'draft-1', null);
      fireEvent.click(screen.getByRole('button', { name: 'Approve' }));
      await waitFor(() => expect(approveVersion).toHaveBeenCalledWith('p1', 'draft-1'));
    });
  });

  describe('summary shapes (Q4-1)', () => {
    const over = () => summary({ currentVersion: current });
    // What GET .../diff answers for a product map: ProductMapDiff.ToJson() wrapped as { product }.
    // directories/entryPoints/patterns/fields only appear when non-empty.
    const productDiff = {
      product: {
        routes: { added: ['/pricing', '/about'], removed: ['/old'], changed: [{ name: '/home', before: { title: 'a' }, after: { title: 'b' } }] },
        components: { added: ['Hero'], removed: [], changed: [] },
        dependencies: { added: ['zod'], removed: [], changed: [{ name: 'react', before: '18.0.0', after: '19.0.0' }] },
        directories: { added: ['src/new'], removed: [], changed: [] },
        fields: [{ name: 'architecture', before: 'mvc', after: 'layered' }],
      },
    };

    it('summarises a product-map diff without throwing', () => {
      vi.mocked(useVersionDiff).mockReturnValue(swr(productDiff));
      renderBanner({ kind: 'product_map', summary: over() });

      expect(screen.getByText('Draft v2 ready for review')).toBeTruthy();
      expect(screen.getByText('Routes: 2 added · 1 changed · 1 removed')).toBeTruthy();
      expect(screen.getByText('Components: 1 added · 0 changed · 0 removed')).toBeTruthy();
      expect(screen.getByText('Dependencies: 1 added · 1 changed · 0 removed')).toBeTruthy();
      expect(screen.getByText(/Also changed: directories, architecture/)).toBeTruthy();
      expect(screen.getByText(/\/pricing, \/about/)).toBeTruthy();
    });

    it('shows the empty-diff message for a product map with no changes', () => {
      const none = { added: [], removed: [], changed: [] };
      vi.mocked(useVersionDiff).mockReturnValue(swr({ product: { routes: none, components: none, dependencies: none } }));
      renderBanner({ kind: 'product_map', summary: over() });

      expect(screen.getByText('No token changes compared with the current version.')).toBeTruthy();
    });

    it('summarises a design diff with counts and names', () => {
      vi.mocked(useVersionDiff).mockReturnValue(swr(diff));
      renderBanner({ summary: over() });

      expect(screen.getByText('1 added · 2 changed · 1 removed')).toBeTruthy();
      expect(screen.getByText(/color\.brand\.primary/)).toBeTruthy();
    });

    it('shows the empty message for a design diff with no changes', () => {
      vi.mocked(useVersionDiff).mockReturnValue(swr({ counts: { added: 0, removed: 0, changed: 0 }, added: [], removed: [], changed: [] }));
      renderBanner({ summary: over() });

      expect(screen.getByText('No token changes compared with the current version.')).toBeTruthy();
    });

    it('does not throw while the diff is still loading', () => {
      vi.mocked(useVersionDiff).mockReturnValue(swr(undefined, undefined, true));
      renderBanner({ kind: 'product_map', summary: over() });

      expect(screen.getByRole('button', { name: 'Approve' })).toBeTruthy();
      expect(screen.queryByTestId('draft-summary')).toBeNull();
    });

    it('treats an unknown shape as summary unavailable and keeps approve with the generic confirm', () => {
      vi.mocked(useVersionDiff).mockReturnValue(swr({ something: 'else' }));
      renderBanner({ summary: over() });

      expect(screen.getByText(/couldn.t load a summary of the changes/i)).toBeTruthy();
      fireEvent.click(screen.getByRole('button', { name: 'Approve' }));
      expect(screen.getByText(/replaces version 1/i)).toBeTruthy();
    });

    it('survives a partial product diff (missing collections)', () => {
      vi.mocked(useVersionDiff).mockReturnValue(swr({ product: { routes: { added: ['/x'] } } }));
      renderBanner({ kind: 'product_map', summary: over() });

      expect(screen.getByText('Routes: 1 added · 0 changed · 0 removed')).toBeTruthy();
    });

    it('survives an error from the hook for a product map', () => {
      vi.mocked(useVersionDiff).mockReturnValue(swr(undefined, new Error('boom')));
      renderBanner({ kind: 'product_map', summary: over() });

      expect(screen.getByText(/couldn.t load a summary of the changes/i)).toBeTruthy();
      expect(screen.getByRole('button', { name: 'Reject' })).toBeTruthy();
    });
  });
});
