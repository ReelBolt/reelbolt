// @vitest-environment jsdom
/**
 * An import never replaces a good design by accident: the modal compares the new draft with the
 * project's approved version and asks before approving, and an import that found nothing (or lost
 * tokens) says so and asks for a confirm even when there is nothing to compare with.
 */
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';

vi.mock('@/lib/api/design-system', () => ({
  importDesign: vi.fn(),
  importFigmaVariables: vi.fn(),
  importBrandPdf: vi.fn(),
  importZip: vi.fn(),
  approveVersion: vi.fn(),
  rejectVersion: vi.fn(),
}));
vi.mock('@/lib/hooks/use-files', () => ({
  useProjectFiles: () => ({
    data: [{ id: 'f1', originalFileName: 'brief.txt' }],
    isLoading: false,
    error: null,
  }),
}));
vi.mock('@/lib/hooks/use-design-system', () => ({ useVersionDiff: vi.fn() }));
vi.mock('@/lib/hooks/use-revalidate-design-system', () => ({ useRevalidateDesignSystem: () => vi.fn() }));

import { approveVersion, importDesign } from '@/lib/api/design-system';
import { useVersionDiff } from '@/lib/hooks/use-design-system';
import type {
  DesignSystemsSummary,
  DesignVersionSummary,
  ImportDiagnostic,
} from '@/lib/types/design-system';
import { ImportProfileModal } from '../ImportProfileModal';

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

const draft: DesignVersionSummary = {
  id: 'draft-4',
  designSystemId: 'ds-1',
  versionNumber: 4,
  state: 'Draft',
  origin: 'Import',
  documentDigest: 'abc',
  baseVersionId: null,
  workflowExecutionId: null,
  createdByUserId: 'u1',
  approvedByUserId: null,
  approvedAt: null,
  createdAt: '2026-01-01T00:00:00Z',
};

const current: DesignSystemsSummary = {
  design: {
    id: 'ds-1',
    kind: 'design',
    name: 'Brand',
    currentVersion: { id: 'v3', versionNumber: 3, digest: 'd3', approvedAt: '2026-01-01T00:00:00Z' },
    latestDraft: draft,
    pendingProposals: 0,
    parentLibrary: null,
  },
  productMap: null,
};

const zeroTokens: ImportDiagnostic = {
  severity: 'Warn',
  code: 'IMPORT_ZERO_TOKENS',
  path: '',
  message: 'The import found no colours, fonts or other design tokens in the files you chose.',
};

async function runImport(props: { current?: DesignSystemsSummary | null }, diagnostics: ImportDiagnostic[]) {
  vi.mocked(importDesign).mockResolvedValue({ versions: [draft], diagnostics });
  render(
    <MantineProvider>
      <Notifications />
      <ImportProfileModal projectId="p1" opened onClose={vi.fn()} canEdit {...props} />
    </MantineProvider>,
  );
  fireEvent.click(screen.getByRole('combobox'));
  fireEvent.click(await screen.findByRole('option', { name: 'brief.txt' }));
  fireEvent.click(screen.getByTestId('import-submit'));
  await screen.findByTestId('draft-review-banner');
}

describe('ImportProfileModal against the current design', () => {
  it('asks for the real current version and shows the diff against it', async () => {
    vi.mocked(useVersionDiff).mockReturnValue({
      data: { counts: { added: 0, removed: 5, changed: 0 }, added: [], removed: [{ path: 'color.brand' }], changed: [] },
      error: undefined,
    } as never);

    await runImport({ current }, []);

    // The diff is requested between the new draft and the approved version, not against nothing.
    expect(vi.mocked(useVersionDiff)).toHaveBeenCalledWith('p1', 'draft-4', 'v3');
    expect(screen.getByTestId('draft-summary').textContent).toContain('Compared with version 3');
    expect(screen.getByTestId('draft-summary').textContent).toContain('5 removed');
  });

  it('does not approve on the first click: it asks to confirm replacing the current version', async () => {
    vi.mocked(useVersionDiff).mockReturnValue({ data: undefined, error: undefined } as never);
    await runImport({ current }, []);

    fireEvent.click(screen.getByRole('button', { name: /^approve/i }));

    expect(approveVersion).not.toHaveBeenCalled();
    expect(screen.getByTestId('draft-confirm').textContent).toContain('replaces version 3');
  });

  it('shows the zero-token warning and still asks to confirm when there is no current version', async () => {
    vi.mocked(useVersionDiff).mockReturnValue({ data: undefined, error: undefined } as never);
    await runImport({ current: null }, [zeroTokens]);

    expect(screen.getByTestId('import-warning-title')).toBeTruthy();
    expect(screen.getByTestId('draft-warning').textContent).toContain('found no colours');

    fireEvent.click(screen.getByRole('button', { name: /^approve/i }));
    expect(approveVersion).not.toHaveBeenCalled();
    expect(screen.getByTestId('draft-confirm')).toBeTruthy();

    vi.mocked(approveVersion).mockResolvedValue({} as never);
    fireEvent.click(within(screen.getByTestId('draft-confirm')).getAllByRole('button')[0]);
    await waitFor(() => expect(approveVersion).toHaveBeenCalledWith('p1', 'draft-4'));
  });

  it('keeps the old one-click approve for a clean first import', async () => {
    vi.mocked(approveVersion).mockResolvedValue({} as never);
    vi.mocked(useVersionDiff).mockReturnValue({ data: undefined, error: undefined } as never);
    await runImport({ current: null }, []);

    expect(screen.queryByTestId('import-warning-title')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: /^approve/i }));
    await waitFor(() => expect(approveVersion).toHaveBeenCalledWith('p1', 'draft-4'));
  });
});

