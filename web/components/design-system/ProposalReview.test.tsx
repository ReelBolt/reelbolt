// @vitest-environment jsdom
/**
 * The Proposals sub-tab (P3-04, SPEC §9/§13.4/§17): counts + score, before/after colour swatches
 * with hex, provenance `file:line`, the conflict badge, and the Approve / Reject-with-note pair.
 * The API module and the proposals hook are mocked exactly like the other design-system tests.
 */
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
vi.mock('@/lib/api/design-system', () => ({
  approveProposal: vi.fn(),
  rejectProposal: vi.fn(),
}));
vi.mock('@/lib/hooks/use-design-system', () => ({ useDesignProposals: vi.fn() }));
const revalidate = vi.hoisted(() => vi.fn());
vi.mock('@/lib/hooks/use-revalidate-design-system', () => ({ useRevalidateDesignSystem: () => revalidate }));

import { approveProposal, rejectProposal } from '@/lib/api/design-system';
import { useDesignProposals } from '@/lib/hooks/use-design-system';
import type { DesignProposal } from '@/lib/types/design-system';
import { ProposalReview } from './ProposalReview';

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

beforeEach(() => {
  vi.mocked(useDesignProposals).mockReturnValue(swrResult([]));
});

/** A structural stand-in for the SWR response the mocked hook returns. */
function swrResult(data: unknown, error: unknown = undefined, isLoading = false): never {
  return { data, error, isLoading, mutate: vi.fn() } as never;
}

const changedColor = {
  path: 'color.brand.primary',
  before: { hex: '#6d28d9' },
  after: { hex: '#7c3aed' },
  conflict: false,
  locked: false,
  prov: { method: 'static' as const, importer: 'css-theme', sourceId: null, loc: 'app/globals.css:12' },
};

function proposal(over: Partial<DesignProposal> = {}): DesignProposal {
  return {
    id: 'prop-1',
    designSystemId: 'ds-1',
    baseVersionId: 'v-base',
    proposedVersionId: 'v-2',
    trigger: 'SourceChanged',
    driftScore: 0.42,
    diff: { counts: { added: 1, removed: 0, changed: 1 }, added: [], removed: [], changed: [] },
    status: 'Pending',
    proposedDigest: 'digest',
    decidedByUserId: null,
    decidedAt: null,
    decisionNote: null,
    createdAt: '2026-01-01T00:00:00Z',
    ...over,
  };
}

function renderReview(canEdit = true, onChanged = vi.fn()) {
  render(
    <MantineProvider>
      <ProposalReview projectId="p1" kind="design" canEdit={canEdit} onChanged={onChanged} />
    </MantineProvider>,
  );
  return onChanged;
}

describe('ProposalReview', () => {
  it('renders the counts and the change score', () => {
    vi.mocked(useDesignProposals).mockReturnValue(swrResult([proposal()]));
    renderReview();

    expect(screen.getByTestId('proposal-added-prop-1').textContent).toContain('1');
    expect(screen.getByTestId('proposal-changed-prop-1').textContent).toContain('1');
    expect(screen.getByTestId('proposal-score-prop-1').textContent).toContain('0.42');
  });

  it('renders a changed colour row with before/after hex and the provenance location', () => {
    vi.mocked(useDesignProposals).mockReturnValue(
      swrResult([proposal({ diff: { counts: { added: 0, removed: 0, changed: 1 }, added: [], removed: [], changed: [changedColor] } })]),
    );
    renderReview();

    expect(screen.getByText('#6d28d9')).toBeTruthy();
    expect(screen.getByText('#7c3aed')).toBeTruthy();
    expect(screen.getByText('app/globals.css:12')).toBeTruthy();
  });

  it('shows a font sample for a typography.family change, joining a $value array into one stack', () => {
    const fontChange = {
      path: 'typography.family.heading',
      before: ['Inter', 'sans-serif'],
      after: ['Comic Neue'],
      conflict: false,
      locked: false,
    };
    vi.mocked(useDesignProposals).mockReturnValue(
      swrResult([proposal({ diff: { counts: { added: 0, removed: 0, changed: 1 }, added: [], removed: [], changed: [fontChange] } })]),
    );
    renderReview();

    const statuses = screen.getAllByTestId('type-specimen-sample').map((node) => node.getAttribute('data-font-status'));
    expect(statuses).toEqual(['available', 'unavailable']);
  });

  it('shows no font sample for a path that is not a font family', () => {
    vi.mocked(useDesignProposals).mockReturnValue(
      swrResult([proposal({ diff: { counts: { added: 0, removed: 0, changed: 1 }, added: [], removed: [], changed: [changedColor] } })]),
    );
    renderReview();

    expect(screen.queryByTestId('type-specimen-sample')).toBeNull();
  });

  it('shows the conflict badge on a conflicting row', () => {
    vi.mocked(useDesignProposals).mockReturnValue(
      swrResult([
        proposal({
          diff: {
            counts: { added: 0, removed: 0, changed: 1 },
            added: [],
            removed: [],
            changed: [{ ...changedColor, conflict: true }],
          },
        }),
      ]),
    );
    renderReview();

    expect(screen.getByTestId('proposal-conflict')).toBeTruthy();
  });

  it('approves a proposal then notifies the parent', async () => {
    vi.mocked(useDesignProposals).mockReturnValue(swrResult([proposal()]));
    vi.mocked(approveProposal).mockResolvedValue({} as never);
    const onChanged = renderReview();

    fireEvent.click(screen.getByRole('button', { name: 'Approve' }));

    await waitFor(() => expect(approveProposal).toHaveBeenCalledWith('p1', 'prop-1'));
    expect(onChanged).toHaveBeenCalled();
  });

  it('rejects a proposal with the typed note', async () => {
    vi.mocked(useDesignProposals).mockReturnValue(swrResult([proposal()]));
    vi.mocked(rejectProposal).mockResolvedValue({} as never);
    renderReview();

    fireEvent.change(screen.getByLabelText('Note (optional)'), { target: { value: 'note' } });
    fireEvent.click(screen.getByRole('button', { name: 'Reject' }));

    await waitFor(() => expect(rejectProposal).toHaveBeenCalledWith('p1', 'prop-1', 'note'));
  });

  it('disables both buttons without edit access', () => {
    vi.mocked(useDesignProposals).mockReturnValue(swrResult([proposal()]));
    renderReview(false);

    expect((screen.getByRole('button', { name: 'Approve' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Reject' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('shows the empty state with no pending proposals', () => {
    renderReview();

    expect(screen.getByText('No pending changes')).toBeTruthy();
  });

  it('sends the typed note with Approve as well as Reject', async () => {
    vi.mocked(useDesignProposals).mockReturnValue(swrResult([proposal()]));
    vi.mocked(approveProposal).mockResolvedValue({} as never);
    renderReview();

    fireEvent.change(screen.getByLabelText('Note (optional)'), { target: { value: 'matches the new logo' } });
    fireEvent.click(screen.getByRole('button', { name: 'Approve' }));

    await waitFor(() => expect(approveProposal).toHaveBeenCalledWith('p1', 'prop-1', 'matches the new logo'));
  });

  it('an approved proposal changes what is current, so the whole profile is revalidated', async () => {
    vi.mocked(useDesignProposals).mockReturnValue(swrResult([proposal()]));
    vi.mocked(approveProposal).mockResolvedValue({} as never);
    renderReview();

    fireEvent.click(screen.getByRole('button', { name: 'Approve' }));
    await waitFor(() => expect(revalidate).toHaveBeenCalled());
  });

  it('says the changes could not be loaded, with a retry, instead of "No pending changes"', () => {
    const mutate = vi.fn();
    vi.mocked(useDesignProposals).mockReturnValue({ data: undefined, error: new Error('x'), isLoading: false, mutate } as never);
    renderReview();

    expect(screen.queryByText('No pending changes')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(mutate).toHaveBeenCalled();
  });

  it('without edit access the buttons are disabled and a visible sentence says why', () => {
    vi.mocked(useDesignProposals).mockReturnValue(swrResult([proposal()]));
    renderReview(false);

    const approve = screen.getByRole('button', { name: 'Approve' });
    const id = approve.getAttribute('aria-describedby');
    expect(document.getElementById(id as string)?.textContent).toContain('edit access');
  });
});
