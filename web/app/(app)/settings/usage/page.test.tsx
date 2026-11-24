// @vitest-environment jsdom

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, cleanup, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import UsagePage from './page';
import { useBillingSummary, useBillingUsage } from '@/lib/hooks/use-billing';
import type { BillingSummary, BillingUsage } from '@/lib/types/billing';

vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);
Element.prototype.scrollIntoView = vi.fn();

vi.mock('@/lib/hooks/use-billing', () => ({ useBillingSummary: vi.fn(), useBillingUsage: vi.fn() }));
const summaryMock = vi.mocked(useBillingSummary);
const usageMock = vi.mocked(useBillingUsage);
type SummaryResult = ReturnType<typeof useBillingSummary>;
type UsageResult = ReturnType<typeof useBillingUsage>;

const summaryResult = (over: Partial<SummaryResult>) =>
  ({ data: undefined, error: undefined, isLoading: false, isValidating: false, mutate: vi.fn(), ...over }) as unknown as SummaryResult;
const usageResult = (over: Partial<UsageResult>) =>
  ({ data: undefined, error: undefined, isLoading: false, isValidating: false, mutate: vi.fn(), ...over }) as unknown as UsageResult;

const summary = (over: Partial<BillingSummary> = {}): BillingSummary => ({
  billingProvider: 'paddle',
  paddle: null,
  canManageBilling: true,
  subscription: {
    planKey: 'creator', planName: 'Creator', status: 'Active', seats: 1,
    periodStart: '2026-09-10T00:00:00Z', periodEnd: '2026-10-10T00:00:00Z',
    cancelAtPeriodEnd: false, hasBillingCustomer: true,
  },
  credits: { balance: 120, reserved: 20, available: 100 },
  // The summary carries the plan catalogue and the top-up packs too (WP B10a-UI reads them); this
  // page renders neither, but the type is the whole payload, so the fixture has to be complete.
  topUp: { available: true, packs: [500, 1000, 3000] },
  plans: [],
  ...over,
});

const usage = (over: Partial<BillingUsage> = {}): BillingUsage => ({
  from: '2026-09-10T00:00:00Z',
  to: '2026-10-10T00:00:00Z',
  groupBy: 'day',
  totals: { credits: 12.5, events: 9, costUsd: null },
  byBillingMode: [
    { mode: 'Platform', credits: 10, events: 7, costUsd: null },
    { mode: 'Byo', credits: 2.5, events: 2, costUsd: null },
  ],
  groups: [
    { key: '2026-10-01', label: '2026-10-01', credits: 8, events: 5, costUsd: null, units: [{ kind: 'LlmInputTokens', quantity: 12_000 }] },
    { key: '2026-10-02', label: '2026-10-02', credits: 4.5, events: 4, costUsd: null, units: [{ kind: 'RenderOutputSeconds', quantity: 90 }] },
  ],
  ...over,
});

const renderPage = () =>
  render(
    <MantineProvider>
      <UsagePage />
    </MantineProvider>,
  );

describe('UsagePage', () => {
  beforeEach(() => {
    global.matchMedia = ((q: string) => ({
      matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
      addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
    })) as unknown as typeof matchMedia;
    summaryMock.mockReturnValue(summaryResult({ data: summary() }));
    usageMock.mockReturnValue(usageResult({ data: usage() }));
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it('renders the balance, the window totals and the who-pays split', () => {
    renderPage();

    expect(screen.getByTestId('credits-card').textContent).toContain('100');
    expect(screen.getByTestId('credits-card').textContent).toContain('Creator');
    expect(screen.getByTestId('totals-card').textContent).toContain('12.50');
    expect(screen.getByTestId('totals-card').textContent).toContain('9 metered calls in the last 30 days');
    expect(screen.getByTestId('who-pays-card').textContent).toContain('ReelBolt providers');
    expect(screen.getByTestId('who-pays-card').textContent).toContain('Your own providers');
  });

  it('renders one row per group, with the units each one consumed', () => {
    renderPage();

    expect(screen.getByText('2026-10-01')).toBeTruthy();
    expect(screen.getByText('12.0k input tokens')).toBeTruthy();
    expect(screen.getByText('90 rendered seconds')).toBeTruthy();
    expect(screen.getByText('Day (UTC)')).toBeTruthy();
  });

  it('turns the raw enum member into a readable label on the step-kind axis', () => {
    usageMock.mockReturnValue(
      usageResult({
        data: usage({
          groupBy: 'kind',
          groups: [{ key: 'LlmInputTokens', label: 'LlmInputTokens', credits: 1, events: 1, costUsd: null, units: [] }],
        }),
      }),
    );
    renderPage();

    fireEvent.click(screen.getByText('By step kind'));

    expect(screen.getByText('Step kind')).toBeTruthy();
    expect(screen.getByText('Input tokens')).toBeTruthy();
    expect(screen.queryByText('LlmInputTokens')).toBeNull();
  });

  it('hides our own provider cost from everyone who is not a platform admin', () => {
    renderPage();
    expect(screen.queryByText(/Provider cost/)).toBeNull();
    expect(screen.queryByText(/Our provider cost/)).toBeNull();

    cleanup();
    summaryMock.mockReturnValue(summaryResult({ data: summary() }));
    usageMock.mockReturnValue(
      usageResult({
        data: usage({
          totals: { credits: 12.5, events: 9, costUsd: 0.3 },
          groups: [{ key: '2026-10-01', label: '2026-10-01', credits: 8, events: 5, costUsd: 0.3, units: [] }],
        }),
      }),
    );
    renderPage();
    expect(screen.getByText('Provider cost')).toBeTruthy();
    expect(screen.getByText(/Our provider cost: \$0.3000/)).toBeTruthy();
  });

  it('re-queries with the chosen axis rather than re-filtering cached data', () => {
    renderPage();

    fireEvent.click(screen.getByText('By project'));

    const lastCall = usageMock.mock.calls[usageMock.mock.calls.length - 1][0];
    expect(lastCall).toMatchObject({ groupBy: 'project' });
  });

  it('re-queries with an earlier window when the period changes', () => {
    renderPage();
    const firstFrom = usageMock.mock.calls[0][0]?.from;

    fireEvent.click(screen.getByText('90 days'));

    const lastFrom = usageMock.mock.calls[usageMock.mock.calls.length - 1][0]?.from;
    expect(new Date(lastFrom!).getTime()).toBeLessThan(new Date(firstFrom!).getTime());
  });

  it('explains an empty window instead of drawing an empty table', () => {
    usageMock.mockReturnValue(usageResult({ data: usage({ groups: [], byBillingMode: [], totals: { credits: 0, events: 0, costUsd: null } }) }));
    renderPage();

    expect(screen.getByText(/No metered usage in the last 30 days/)).toBeTruthy();
    expect(screen.getByTestId('who-pays-card').textContent).toContain('Nothing metered in this period.');
  });

  it('explains a deployment without billing instead of showing a failure', () => {
    summaryMock.mockReturnValue(summaryResult({ error: new ApiError('Not Found', 404) }));
    usageMock.mockReturnValue(usageResult({ error: new ApiError('Not Found', 404) }));
    renderPage();

    expect(screen.getByText('Billing is not enabled on this deployment')).toBeTruthy();
    expect(screen.queryByText('Could not load your usage')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
  });

  it('offers a retry for a failure that is not a missing feature', () => {
    usageMock.mockReturnValue(usageResult({ error: new Error('Network unreachable') }));
    renderPage();

    expect(screen.getByText('Could not load your usage')).toBeTruthy();
    expect(screen.getByText('Network unreachable')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(usageMock.mock.results[0].value.mutate).toHaveBeenCalled();
  });

  it('shows a loader, and claims no figures, until the first roll-up arrives', () => {
    usageMock.mockReturnValue(usageResult({ isLoading: true }));
    summaryMock.mockReturnValue(summaryResult({ isLoading: true }));
    const { container } = renderPage();

    expect(container.querySelector('.mantine-Loader-root')).not.toBeNull();
    expect(screen.queryByTestId('totals-card')).toBeNull();
  });

  it('groups by source with plain labels for every source', () => {
    const sources: Array<[string, string]> = [
      ['Workflow', 'Workflow runs'],
      ['Assistant', 'Assistant'],
      ['Mcp', 'Claude Code and MCP'],
      ['Editor', 'Picture editor'],
      ['TimelineAssist', 'Timeline suggestions'],
      ['FileSummary', 'File summaries'],
      ['Indexing', 'Search indexing'],
      ['DocsIndex', 'Help search'],
      ['Storage', 'Storage'],
      ['Unattributed', 'Other'],
    ];
    usageMock.mockReturnValue(
      usageResult({
        data: usage({
          groupBy: 'source',
          groups: sources.map(([key]) => ({ key, label: key, credits: 1, events: 1, costUsd: null, units: [] })),
        }),
      }),
    );
    renderPage();

    fireEvent.click(screen.getByText('By source'));

    const lastCall = usageMock.mock.calls[usageMock.mock.calls.length - 1][0];
    expect(lastCall).toMatchObject({ groupBy: 'source' });
    expect(screen.getByRole('columnheader', { name: 'Source' })).toBeTruthy();
    const rowText = screen.getAllByRole('row').slice(1).map((r) => r.querySelector('td')?.querySelector('p')?.textContent);
    expect(rowText).toEqual(sources.map(([, label]) => label));
    expect(screen.queryByText('Mcp')).toBeNull();
    expect(screen.queryByText('TimelineAssist')).toBeNull();
  });

  it('shows the assistant allowance as used of included, with a bar and the period end', () => {
    summaryMock.mockReturnValue(
      summaryResult({
        data: summary({
          assistantAllowance: {
            includedCredits: 200, usedCredits: 50,
            periodStart: '2026-09-10T00:00:00Z', periodEnd: '2026-10-10T00:00:00Z',
          },
        }),
      }),
    );
    renderPage();

    const card = screen.getByTestId('assistant-allowance-card');
    expect(card.textContent).toContain('Assistant allowance');
    expect(card.textContent).toContain('50 of 200 included credits used this period');
    expect(card.textContent).toContain(new Date('2026-10-10T00:00:00Z').toLocaleDateString());
    expect(card.textContent).toContain('After the allowance, assistant and Claude Code use is charged from your credits.');
    const bar = screen.getByRole('progressbar', { name: 'Share of the assistant allowance used' });
    expect(bar.getAttribute('aria-valuenow')).toBe('25');
  });

  it('says Unlimited when the plan includes no cap on assistant use', () => {
    summaryMock.mockReturnValue(
      summaryResult({
        data: summary({
          assistantAllowance: {
            includedCredits: null, usedCredits: 12,
            periodStart: '2026-09-10T00:00:00Z', periodEnd: '2026-10-10T00:00:00Z',
          },
        }),
      }),
    );
    renderPage();

    const card = screen.getByTestId('assistant-allowance-card');
    expect(card.textContent).toContain('Unlimited');
    expect(card.textContent).not.toContain('included credits used');
    expect(screen.queryByRole('progressbar', { name: 'Share of the assistant allowance used' })).toBeNull();
  });

  it('hides the allowance when billing is unavailable or the backend sends none', () => {
    summaryMock.mockReturnValue(summaryResult({ error: new ApiError('Not Found', 404) }));
    renderPage();
    expect(screen.queryByTestId('assistant-allowance-card')).toBeNull();
    expect(screen.queryByText('Assistant allowance')).toBeNull();

    cleanup();
    summaryMock.mockReturnValue(summaryResult({ data: summary() }));
    renderPage();
    expect(screen.queryByTestId('assistant-allowance-card')).toBeNull();
  });
});
