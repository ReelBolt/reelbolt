// @vitest-environment jsdom
/**
 * The Build flow (P2-22, SPEC §9/§9.4/§17). The design-system API is mocked exactly like the page
 * tests mock their hooks; the 402 is a real `ApiError`, so the shared shortfall parser runs for real.
 */
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { WorkflowExecution } from '@/lib/types/workflow';

vi.mock('next/link', () => ({
  default: ({ children, href }: { children: React.ReactNode; href: string }) => (
    <a href={href}>{children}</a>
  ),
}));
vi.mock('@/lib/api/design-system', () => ({
  buildProfile: vi.fn(),
  getDesignSystemsSummary: vi.fn(),
  ingestDesignSystem: vi.fn(),
}));
vi.mock('@/lib/api/executions', () => ({ getExecution: vi.fn() }));

import { ApiError } from '@/lib/api/client';
import { buildProfile, getDesignSystemsSummary, ingestDesignSystem } from '@/lib/api/design-system';
import type { DesignSystemsSummary, DesignVersionSummary } from '@/lib/types/design-system';
import { getExecution } from '@/lib/api/executions';
import { BuildProfileModal } from './BuildProfileModal';

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

function renderModal(onBuilt = vi.fn(), onImport = vi.fn()) {
  render(
    <MantineProvider>
      <BuildProfileModal projectId="p1" opened onClose={vi.fn()} onBuilt={onBuilt} onImport={onImport} />
    </MantineProvider>,
  );
  return { onBuilt, onImport };
}

const queuedExecution = { id: 'e1', status: 'Queued' } as WorkflowExecution;

describe('BuildProfileModal', () => {
  it('starts the build and links to the run it created', async () => {
    vi.mocked(buildProfile).mockResolvedValue({
      executionId: 'e1',
      workflowId: 'w1',
      estimate: { low: 12, high: 60 },
    });
    vi.mocked(getExecution).mockResolvedValue(queuedExecution);
    renderModal();

    fireEvent.click(screen.getByTestId('build-start'));

    await waitFor(() => expect(buildProfile).toHaveBeenCalledWith('p1', {}));
    const link = await screen.findByRole('link');
    expect(link.getAttribute('href')).toBe('/projects/p1/workflows/w1/executions/e1');
  });

  it('asks for the estimate, then resubmits the same body with acceptEstimateCredits', async () => {
    vi.mocked(buildProfile)
      .mockRejectedValueOnce(
        new ApiError('needs acceptance', 409, [], {
          code: 'ESTIMATE_NOT_ACCEPTED',
          estimate: { low: 12, high: 60 },
          capCredits: 300,
        }),
      )
      .mockResolvedValueOnce({ executionId: 'e1', workflowId: 'w1', estimate: { low: 12, high: 60 } });
    vi.mocked(getExecution).mockResolvedValue(queuedExecution);
    renderModal();

    fireEvent.click(screen.getByTestId('build-start'));
    await screen.findByText('Up to 60 credits if we need AI for missing parts');

    fireEvent.click(screen.getByTestId('build-confirm'));
    await waitFor(() =>
      expect(buildProfile).toHaveBeenNthCalledWith(2, 'p1', { acceptEstimateCredits: 60 }),
    );
  });

  it('renders the shared credits shortfall state on a 402', async () => {
    vi.mocked(buildProfile).mockRejectedValue(
      new ApiError('not enough credits', 402, [], {
        code: 'INSUFFICIENT_CREDITS',
        required: 100,
        balance: 20,
        low: null,
        high: null,
        hasHistory: false,
      }),
    );
    renderModal();

    fireEvent.click(screen.getByTestId('build-start'));
    expect(await screen.findByTestId('insufficient-credits')).toBeTruthy();
  });

  // Rewritten (WEB-5): this test used to pin the dead end -- the raw server sentence and nothing
  // else. It still asserts there is no confirm over the cap, but now also requires the numbers and
  // the free way forward.
  it('over the cap: shows estimate against limit, offers no confirm, and points to the free import', async () => {
    vi.mocked(buildProfile).mockRejectedValue(
      new ApiError('The AI fallback is estimated at up to 999 credits, above the 300-credit cap.', 409, [], {
        code: 'ESTIMATE_OVER_CAP',
        message: 'The AI fallback is estimated at up to 999 credits, above the 300-credit cap.',
        estimate: { low: 400, high: 999 },
        capCredits: 300,
      }),
    );
    const { onImport } = renderModal();

    fireEvent.click(screen.getByTestId('build-start'));
    const alert = await screen.findByTestId('build-over-cap');
    expect(alert.textContent).toContain('999');
    expect(alert.textContent).toContain('300');
    expect(alert.textContent).toContain('400');
    expect(screen.queryByTestId('build-confirm')).toBeNull();
    expect(screen.queryByTestId('build-start')).toBeNull();

    // The free path names the four things a person can bring, and opens the import modal.
    expect(alert.textContent).toMatch(/token file/i);
    expect(alert.textContent).toMatch(/DESIGN\.md/);
    expect(alert.textContent).toMatch(/CSS/);
    expect(alert.textContent).toMatch(/zip/i);
    fireEvent.click(screen.getByRole('button', { name: 'Import design files instead' }));
    expect(onImport).toHaveBeenCalledTimes(1);
  });

  describe('D-6 honesty', () => {
    it('says up front, before any click, that the AI build uses credits and that reading files is free', () => {
      renderModal();
      const text = document.body.textContent ?? '';
      expect(text).toMatch(/uses credits/i);
      expect(text).toMatch(/free/i);
      expect(text).toMatch(/before anything is charged/i);
    });

    it('shows the whole range, not only the high end, when asking for acceptance', async () => {
      vi.mocked(buildProfile).mockRejectedValueOnce(
        new ApiError('needs acceptance', 409, [], {
          code: 'ESTIMATE_NOT_ACCEPTED',
          estimate: { low: 12, high: 60 },
          capCredits: 300,
        }),
      );
      renderModal();

      fireEvent.click(screen.getByTestId('build-start'));
      const estimate = await screen.findByTestId('build-estimate');
      expect(estimate.textContent).toContain('12');
      expect(estimate.textContent).toContain('60');
      expect(estimate.textContent).toMatch(/between 12 and 60 credits/i);
    });

    it('adds a typical figure when the server sends one', async () => {
      vi.mocked(buildProfile).mockRejectedValueOnce(
        new ApiError('needs acceptance', 409, [], {
          code: 'ESTIMATE_NOT_ACCEPTED',
          estimate: { low: 12, typical: 30, high: 60 },
          capCredits: 300,
        }),
      );
      renderModal();

      fireEvent.click(screen.getByTestId('build-start'));
      const estimate = await screen.findByTestId('build-estimate');
      expect(estimate.textContent).toMatch(/typically about 30/i);
    });
  });

  describe('a finished run (WEB-7): the status line and Save as drafts', () => {
    const started = { executionId: 'e1', workflowId: 'w1', estimate: { low: 0, high: 0 } };
    const draftFromRun = (executionId: string | null): DesignVersionSummary => ({
      id: 'draft-1',
      designSystemId: 'ds-1',
      versionNumber: 1,
      state: 'Draft',
      origin: 'StaticExtract',
      documentDigest: 'd',
      baseVersionId: null,
      workflowExecutionId: executionId,
      createdByUserId: 'u-1',
      approvedByUserId: null,
      approvedAt: null,
      createdAt: '2026-01-01T00:00:00Z',
    });
    const summaryWith = (latestDraft: DesignVersionSummary | null): DesignSystemsSummary => ({
      design: { id: 'ds-1', kind: 'design', name: 'Acme', currentVersion: null, latestDraft, pendingProposals: 0, parentLibrary: null },
      productMap: null,
    });

    it('a Passed run never reads "Building… (Passed)"', async () => {
      vi.mocked(buildProfile).mockResolvedValue(started);
      vi.mocked(getExecution).mockResolvedValue({ id: 'e1', status: 'Passed' } as WorkflowExecution);
      vi.mocked(getDesignSystemsSummary).mockResolvedValue(summaryWith(draftFromRun('e1')));
      renderModal();

      fireEvent.click(screen.getByTestId('build-start'));
      await screen.findByText(/build finished/i);
      expect(document.body.textContent).not.toMatch(/Building.*\(Passed\)/);
    });

    it('a Passed run whose draft is in place offers review, not Save as drafts', async () => {
      vi.mocked(buildProfile).mockResolvedValue(started);
      vi.mocked(getExecution).mockResolvedValue({ id: 'e1', status: 'Passed' } as WorkflowExecution);
      vi.mocked(getDesignSystemsSummary).mockResolvedValue(summaryWith(draftFromRun('e1')));
      renderModal();

      fireEvent.click(screen.getByTestId('build-start'));
      await screen.findByText(/draft is ready to review/i);
      expect(screen.queryByRole('button', { name: 'Save as drafts' })).toBeNull();
    });

    it('a Passed run with no draft (ingest refused or pending) offers Save as drafts and saves', async () => {
      vi.mocked(buildProfile).mockResolvedValue(started);
      vi.mocked(getExecution).mockResolvedValue({ id: 'e1', status: 'Passed' } as WorkflowExecution);
      // The real state after a refused ingest: the summary has no draft from this run.
      vi.mocked(getDesignSystemsSummary).mockResolvedValue(summaryWith(draftFromRun('some-other-run')));
      vi.mocked(ingestDesignSystem).mockResolvedValue({ versionIds: ['draft-2'], versions: [draftFromRun('e1')] });
      const { onBuilt } = renderModal();

      fireEvent.click(screen.getByTestId('build-start'));
      fireEvent.click(await screen.findByRole('button', { name: 'Save as drafts' }));

      await waitFor(() => expect(ingestDesignSystem).toHaveBeenCalledWith('p1', 'e1', 9));
      await screen.findByText(/saved as drafts/i);
      expect(onBuilt).toHaveBeenCalled();
    });

    it('shows the refusal when saving fails and keeps the button', async () => {
      vi.mocked(buildProfile).mockResolvedValue(started);
      vi.mocked(getExecution).mockResolvedValue({ id: 'e1', status: 'Passed' } as WorkflowExecution);
      vi.mocked(getDesignSystemsSummary).mockResolvedValue(summaryWith(null));
      vi.mocked(ingestDesignSystem).mockRejectedValue(new ApiError('the artifact is not valid', 400, [], { code: 'INVALID_DOCUMENT' }));
      renderModal();

      fireEvent.click(screen.getByTestId('build-start'));
      fireEvent.click(await screen.findByRole('button', { name: 'Save as drafts' }));

      expect((await screen.findByTestId('build-save-error')).textContent).toContain('the artifact is not valid');
      expect(screen.getByRole('button', { name: 'Save as drafts' })).toBeTruthy();
    });

    it('a Failed run says so and offers no Save as drafts', async () => {
      vi.mocked(buildProfile).mockResolvedValue(started);
      vi.mocked(getExecution).mockResolvedValue({ id: 'e1', status: 'Failed' } as WorkflowExecution);
      renderModal();

      fireEvent.click(screen.getByTestId('build-start'));
      await screen.findByText(/build didn.t finish/i);
      expect(screen.queryByRole('button', { name: 'Save as drafts' })).toBeNull();
    });
  });
});
