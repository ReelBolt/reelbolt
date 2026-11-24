// @vitest-environment jsdom

import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import AdminBillingPage from './page';
import {
  useBillingAdminOverview,
  usePriceCoverage,
  usePricingSettings,
  useRateMultipliers,
} from '@/lib/hooks/use-admin-billing';
import { useConsolidationReport } from '@/lib/hooks/use-inference-providers';
import { updatePricingSettings } from '@/lib/api/pricing';
import type { PriceCoverageGap, PricingSettings, RateMultiplier } from '@/lib/types/pricing';
import type { ConsolidationReport } from '@/lib/types/inference-provider';
import type { BillingAdminOverview, BillingCollectedCurrency } from '@/lib/types/billing-admin';

vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);
Element.prototype.scrollIntoView = vi.fn();

const auth = { isPlatformAdmin: true };
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ isPlatformAdmin: auth.isPlatformAdmin }) }));
vi.mock('@/lib/hooks/use-admin-billing', () => ({
  useBillingAdminOverview: vi.fn(),
  usePricingSettings: vi.fn(),
  useRateMultipliers: vi.fn(),
  usePriceCoverage: vi.fn(),
}));
vi.mock('@/lib/hooks/use-inference-providers', () => ({ useConsolidationReport: vi.fn() }));
vi.mock('@/lib/api/pricing', () => ({
  updatePricingSettings: vi.fn(),
  createRateMultiplier: vi.fn(),
}));
vi.mock('@/lib/api/billing-admin', () => ({ getBillingAdminOverview: vi.fn() }));

const overviewHook = vi.mocked(useBillingAdminOverview);
const settingsHook = vi.mocked(usePricingSettings);
const multipliersHook = vi.mocked(useRateMultipliers);
const coverageHook = vi.mocked(usePriceCoverage);
const consolidationHook = vi.mocked(useConsolidationReport);
const updateSettingsMock = vi.mocked(updatePricingSettings);

const mutate = vi.fn();

const SETTINGS: PricingSettings = { markup: 1.2, creditUsdValue: 0.006, updatedAt: '2026-10-01T00:00:00.000Z' };

const rateMultiplier = (over: Partial<RateMultiplier> = {}): RateMultiplier => ({
  id: 'mult-1',
  billingMode: 'Platform',
  usageKind: null,
  multiplier: 1,
  effectiveFrom: '2020-01-01T00:00:00.000Z',
  effectiveTo: null,
  ...over,
});

const GAP: PriceCoverageGap = {
  capabilityId: 'cap-asr',
  providerId: 'prov-fish',
  providerName: 'Fish Audio',
  modelDisplayName: 'Transcribe Pro',
  capability: 'Transcription',
  missingUnits: ['AsrAudioSeconds'],
};

const usdRow = (over: Partial<BillingCollectedCurrency> = {}): BillingCollectedCurrency => ({
  currencyCode: 'USD',
  minorUnitExponent: 2,
  payments: 1,
  zeroAmountPayments: 0,
  grossMinor: 2900,
  subscriptionGrossMinor: 2900,
  topUpGrossMinor: 0,
  discountMinor: 0,
  taxMinor: 0,
  feeMinor: null,
  paymentsWithUnknownTax: 0,
  refunds: 0,
  refundedMinor: 0,
  netMinor: 2900,
  ...over,
});

const overview = (over: Partial<BillingAdminOverview> = {}): BillingAdminOverview => ({
  from: '2026-05-02T00:00:00.000Z',
  to: '2026-06-01T00:00:00.000Z',
  windowDays: 30,
  revenue: {
    listPriceMonthlyUsd: 29,
    committedMonthlyUsd: 29,
    windowCommittedUsd: 29,
    payingSubscriptions: 1,
    trialingSubscriptions: 0,
    internalTrialOrganizations: 2,
    discountedSubscriptions: 0,
    fullyDiscountedSubscriptions: 0,
    subscriptionsWithoutPayment: 0,
    activeProvider: 'stripe',
    excluded: [],
    collected: [usdRow()],
    collectedNetUsd: 29,
    otherCurrencyPayments: 0,
    topUpCreditsInWindow: 2000,
    topUpUsdRecorded: true,
    topUpCreditsRecorded: true,
  },
  cost: {
    totalUsd: 4.5,
    totalCreditsMillis: 500_000,
    events: 3,
    unpricedPlatformEvents: 0,
    unattributedEvents: 0,
    byProviderKind: [{ key: 'Anthropic', costUsd: 4, creditsMillis: 500_000, events: 2 }],
    byModel: [],
    byUsageKind: [],
    bySource: [],
    byBillingMode: [],
    byTemplate: [{ key: 'promo', costUsd: 3, creditsMillis: 500_000, events: 1 }],
    topOrganizations: [
      { organizationId: 'org-a', name: 'Acme', costUsd: 4, creditsMillis: 500_000, events: 2 },
    ],
    topOrganizationsTruncated: false,
  },
  plans: [
    {
      planKey: 'creator',
      planName: 'Creator',
      organizations: 1,
      committedMonthlyUsd: 29,
      listPriceMonthlyUsd: 29,
      windowCommittedUsd: 29,
      costUsd: 4,
      marginUsd: 25,
      marginPercent: 86.21,
    },
  ],
  ...over,
});

const renderPage = () =>
  render(
    // `env="test"` is Mantine's own switch for this: it turns off transitions and portals, so a
    // menu or a modal is in the DOM the moment it is opened instead of after a CSS transition that
    // jsdom never runs.
    <MantineProvider env="test">
      <AdminBillingPage />
    </MantineProvider>,
  );

/** The page's area tabs carry a row count, so they are matched by prefix rather than exactly. */
const areaTab = (name: string) => screen.getByRole('tab', { name: new RegExp(`^${name}`) });

/** Everything the page shows above the area tabs sits before them in document order. */
function isBefore(node: Element, other: Element) {
  return (node.compareDocumentPosition(other) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0;
}

beforeEach(() => {
  global.matchMedia = ((q: string) => ({
    matches: false,
    media: q,
    onchange: null,
    addListener() {},
    removeListener() {},
    addEventListener() {},
    removeEventListener() {},
    dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;

  auth.isPlatformAdmin = true;
  overviewHook.mockReturnValue({ data: overview(), error: undefined, isLoading: false, isValidating: false } as never);
  settingsHook.mockReturnValue({ data: SETTINGS, isLoading: false, mutate } as never);
  multipliersHook.mockReturnValue({ data: [], isLoading: false, mutate } as never);
  coverageHook.mockReturnValue({ data: [], isLoading: false, isValidating: false } as never);
  consolidationHook.mockReturnValue({ data: { groups: [] }, isLoading: false } as never);
  updateSettingsMock.mockReset();
  updateSettingsMock.mockResolvedValue({ ...SETTINGS, markup: 1.5 });
  mutate.mockReset();
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('AdminBillingPage gate', () => {
  it('refuses to render any figure to a non-platform-admin and asks the API for nothing', () => {
    auth.isPlatformAdmin = false;

    renderPage();

    expect(screen.getByText('Platform administrators only')).toBeTruthy();
    expect(screen.queryByText('Committed / month')).toBeNull();
    expect(overviewHook).toHaveBeenCalledWith(expect.anything(), false);
    expect(settingsHook).toHaveBeenCalledWith(false);
    expect(multipliersHook).toHaveBeenCalledWith(false);
    expect(coverageHook).toHaveBeenCalledWith(false);
    expect(consolidationHook).toHaveBeenCalledWith(false);
  });

  it('loads everything for a platform admin', () => {
    renderPage();

    expect(screen.queryByText('Platform administrators only')).toBeNull();
    expect(settingsHook).toHaveBeenCalledWith(true);
    expect(coverageHook).toHaveBeenCalledWith(true);
    expect(overviewHook).toHaveBeenCalledWith(expect.objectContaining({ from: expect.any(String), to: expect.any(String) }), true);
  });
});

describe('AdminBillingPage pricing honesty', () => {
  it('has no rate cards any more: no Rate cards area and no rate-card wording', () => {
    renderPage();

    expect(screen.queryByRole('tab', { name: /^Rate cards/ })).toBeNull();
    expect(screen.queryByText(/rate card/i)).toBeNull();
    expect(areaTab('Pricing')).toBeTruthy();
  });

  it('explains collected versus run rate and no longer claims payments are unrecorded', () => {
    renderPage();

    expect(screen.queryByText(/never stores what a customer/)).toBeNull();
    expect(screen.getByText(/actually charged in the window/)).toBeTruthy();
    expect(screen.getByText(/Internal grants and trials are never revenue/)).toBeTruthy();
  });

  it('surfaces spend the totals cannot contain instead of hiding an unpriced row', () => {
    overviewHook.mockReturnValue({
      data: overview({
        cost: { ...overview().cost, unpricedPlatformEvents: 2, unattributedEvents: 1 },
      }),
      error: undefined,
      isLoading: false,
      isValidating: false,
    } as never);

    renderPage();

    expect(screen.getByText(/had no price and\s+were charged/)).toBeTruthy();
    expect(screen.getByText(/ran outside any usage scope/)).toBeTruthy();
  });

  it('warns above the tabs when a capability has no price', () => {
    coverageHook.mockReturnValue({ data: [GAP], isLoading: false, isValidating: false } as never);
    renderPage();

    const alert = screen.getByText('Some capabilities have no price');
    const tabs = areaTab('Overview').closest('[role="tablist"]');
    expect(isBefore(alert, tabs as Element)).toBe(true);
    expect(areaTab('Pricing').textContent).toContain('(1)');
  });
});

describe('AdminBillingPage figures', () => {
  it('shows collected, run rate after discounts, COGS and the USD margin', () => {
    renderPage();

    expect(screen.getByText('Collected in window (USD)')).toBeTruthy();
    expect(screen.getByText('Run rate after discounts / month')).toBeTruthy();
    expect(screen.getAllByText('$29.00').length).toBeGreaterThan(0);
    expect(screen.getByText('$4.50')).toBeTruthy();
    expect(screen.getByText('$24.50')).toBeTruthy();
    expect(screen.getByText(/1 paying · 0 trialing · 2 internal trial/)).toBeTruthy();
  });

  it('shows 0 collected and a 0 run rate for a fully discounted subscription', () => {
    overviewHook.mockReturnValue({
      data: overview({
        revenue: {
          ...overview().revenue,
          listPriceMonthlyUsd: 79,
          committedMonthlyUsd: 0,
          windowCommittedUsd: 0,
          discountedSubscriptions: 1,
          fullyDiscountedSubscriptions: 1,
          collected: [usdRow({ payments: 1, zeroAmountPayments: 1, grossMinor: 0, subscriptionGrossMinor: 0, netMinor: 0, discountMinor: 7900 })],
          collectedNetUsd: 0,
        },
        plans: [],
      }),
      error: undefined,
      isLoading: false,
      isValidating: false,
    } as never);

    renderPage();

    expect(screen.getByText(/1 payment, 1 charged 0 \(fully discounted or covered by credit\)/)).toBeTruthy();
    expect(screen.getByText(/list price \$79\.00 · 1 discounted · 1 fully discounted/)).toBeTruthy();
    expect(screen.getAllByText('$0.00').length).toBeGreaterThanOrEqual(2);
  });

  it('shows the active provider and labels Paddle test payments as not revenue', () => {
    overviewHook.mockReturnValue({
      data: overview({
        revenue: {
          ...overview().revenue,
          excluded: [
            { provider: 'paddle', payingSubscriptions: 1, collected: [usdRow({ payments: 4, netMinor: 11600 })] },
          ],
        },
      }),
      error: undefined,
      isLoading: false,
      isValidating: false,
    } as never);

    renderPage();

    expect(screen.getByText('Figures below count Stripe only.')).toBeTruthy();
    expect(screen.getByText('Not counted as revenue')).toBeTruthy();
    expect(screen.getByText(/4 payments \(USD \$116\.00 net\), 1 paying subscription: not counted/)).toBeTruthy();
    expect(screen.getByText('Collected in window (USD)')).toBeTruthy();
  });

  it('shows no excluded block when there is none', () => {
    renderPage();

    expect(screen.queryByText('Not counted as revenue')).toBeNull();
  });

  it('keeps currencies apart and flags the margin when payments are not USD', () => {
    overviewHook.mockReturnValue({
      data: overview({
        revenue: {
          ...overview().revenue,
          collected: [
            usdRow(),
            usdRow({ currencyCode: 'EUR', netMinor: 4500, grossMinor: 4500 }),
            usdRow({ currencyCode: 'JPY', minorUnitExponent: 0, netMinor: 3000, grossMinor: 3000 }),
          ],
          otherCurrencyPayments: 2,
        },
      }),
      error: undefined,
      isLoading: false,
      isValidating: false,
    } as never);

    renderPage();

    expect(screen.getByText('Collected in window (EUR)')).toBeTruthy();
    expect(screen.getByText('€45.00')).toBeTruthy();
    expect(screen.getByText('¥3,000')).toBeTruthy();
    expect(screen.getByText('USD ONLY')).toBeTruthy();
    expect(screen.getByText(/2 payment\(s\) in other currencies/)).toBeTruthy();
  });

  it('lists margin per plan including the plan name and percentage', () => {
    renderPage();

    expect(screen.getByText('Creator')).toBeTruthy();
    expect(screen.getByText('86.2%')).toBeTruthy();
  });

  it('shows the cost slice tabs and the top-organizations table', () => {
    renderPage();

    expect(screen.getByRole('tab', { name: 'Provider' })).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Template' })).toBeTruthy();
    expect(screen.getByText('Acme')).toBeTruthy();
  });
});

describe('AdminBillingPage information hierarchy', () => {
  it('reads the caveats and the headline figures before the area tabs', () => {
    renderPage();

    // The page has a second tablist for the cost slices inside the overview, so this is the area
    // tab strip specifically, not "the first tablist on the page".
    const tabs = areaTab('Pricing').closest('[role="tablist"]');
    expect(tabs).not.toBeNull();
    const ledgerAlert = screen.getByText(/actually charged in the window/);
    // The tile renders first; the plan table's own column header is the later match.
    const tile = screen.getByText('Run rate after discounts / month');

    expect(isBefore(ledgerAlert, tabs as Element)).toBe(true);
    expect(isBefore(tile, tabs as Element)).toBe(true);
  });

  it('puts the pricing rules one click away instead of below the figures', async () => {
    const user = userEvent.setup();
    renderPage();

    expect(areaTab('Overview').getAttribute('aria-selected')).toBe('true');
    expect(screen.getByText('Creator')).toBeTruthy();
    expect(screen.queryByLabelText('Markup')).toBeNull();

    await user.click(areaTab('Pricing'));
    expect(await screen.findByLabelText('Markup')).toBeTruthy();
    // Leaving the overview leaves its tables behind.
    expect(screen.queryByText('Creator')).toBeNull();
  });

  it('reveals each area from its own tab, keyboard-reachable', async () => {
    const user = userEvent.setup();
    multipliersHook.mockReturnValue({ data: [rateMultiplier()], isLoading: false, mutate } as never);
    renderPage();

    expect(screen.getByRole('tab', { name: /^Pricing/ })).toBeTruthy();
    expect(screen.getByRole('tab', { name: /^Multipliers/ })).toBeTruthy();

    await user.click(areaTab('Multipliers'));
    expect(await screen.findByText('Platform')).toBeTruthy();
    expect(screen.getByText('all kinds (mode default)')).toBeTruthy();

    await user.click(areaTab('Pricing'));
    expect(await screen.findByLabelText('Markup')).toBeTruthy();

    // The areas are tabs, so they are reachable by keyboard as well as by pointer.
    areaTab('Overview').focus();
    expect(document.activeElement).toBe(areaTab('Overview'));
  });
});

describe('AdminBillingPage pricing settings', () => {
  it('shows the markup, the read-only credit value and the formula in plain words', async () => {
    const user = userEvent.setup();
    renderPage();
    await user.click(areaTab('Pricing'));

    expect(((await screen.findByLabelText('Markup')) as HTMLInputElement).value).toBe('1.2');
    const creditValue = screen.getByLabelText('Credit value') as HTMLInputElement;
    expect(creditValue.readOnly).toBe(true);
    expect(creditValue.value).toBe('$0.006 per credit');
    expect(screen.getByText('Credits = cost × markup ÷ credit value, unless a price sets its own credits.')).toBeTruthy();
    // $1 × 1.2 ÷ $0.006 = 200 credits.
    expect(screen.getByText(/\$1\.00 of vendor cost is charged 200 credits/)).toBeTruthy();
  });

  it('saves an edited markup with PUT /api/v1/pricing/settings and refreshes', async () => {
    const user = userEvent.setup();
    renderPage();
    await user.click(areaTab('Pricing'));

    const save = screen.getByRole('button', { name: 'Save markup' }) as HTMLButtonElement;
    // Nothing to save until the value changes.
    expect(save.disabled).toBe(true);
    fireEvent.change(screen.getByLabelText('Markup'), { target: { value: '1.5' } });
    expect(screen.getByText(/charged 250 credits/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Save markup' }));

    await waitFor(() => expect(updateSettingsMock).toHaveBeenCalledWith({ markup: 1.5 }));
    await waitFor(() => expect(mutate).toHaveBeenCalled());
  });

  it('refuses a markup that is not a positive number', async () => {
    const user = userEvent.setup();
    renderPage();
    await user.click(areaTab('Pricing'));

    fireEvent.change(screen.getByLabelText('Markup'), { target: { value: '-1' } });
    expect(screen.getByText('A positive number.')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Save markup' }) as HTMLButtonElement).disabled).toBe(true);
  });
});

describe('AdminBillingPage price coverage', () => {
  it('says every capability has a price when there is no gap', async () => {
    const user = userEvent.setup();
    renderPage();
    await user.click(areaTab('Pricing'));

    expect(await screen.findByText('Every capability has a price')).toBeTruthy();
  });

  it('lists each capability with no price, its missing units and a link to its provider', async () => {
    const user = userEvent.setup();
    coverageHook.mockReturnValue({
      data: [GAP, { ...GAP, capabilityId: 'cap-2', modelDisplayName: 'Speech', capability: 'SpeechSynthesis', missingUnits: ['TtsInputBytes'] }],
      isLoading: false,
      isValidating: false,
    } as never);
    renderPage();
    await user.click(areaTab('Pricing'));

    expect(await screen.findByText('2 capabilities have no price')).toBeTruthy();
    const row = screen.getByRole('row', { name: /Transcribe Pro/ });
    expect(within(row).getByText('Transcription')).toBeTruthy();
    expect(within(row).getByText('AsrAudioSeconds')).toBeTruthy();
    expect(within(row).getByRole('link', { name: 'Fish Audio' }).getAttribute('href')).toBe('/admin/inference-providers/prov-fish');
  });
});

describe('AdminBillingPage consolidation report', () => {
  it('shows nothing when the upgrade merged nothing', async () => {
    const user = userEvent.setup();
    renderPage();
    await user.click(areaTab('Pricing'));
    await screen.findByLabelText('Markup');

    expect(screen.queryByText('Providers merged by the catalog upgrade')).toBeNull();
  });

  it('lists each surviving provider read-only when providers were merged', async () => {
    const user = userEvent.setup();
    const report: ConsolidationReport = {
      groups: [{ survivorId: 'p-az', survivorName: 'Azure production', mergedProviderIds: ['a', 'b'], mergedModelCount: 3 }],
    };
    consolidationHook.mockReturnValue({ data: report, isLoading: false } as never);
    renderPage();
    await user.click(areaTab('Pricing'));

    expect(await screen.findByText('Providers merged by the catalog upgrade')).toBeTruthy();
    const row = screen.getByRole('row', { name: /Azure production/ });
    expect(within(row).getByText('2')).toBeTruthy();
    expect(within(row).getByText('3')).toBeTruthy();
    expect(within(row).getByRole('link', { name: 'Azure production' }).getAttribute('href')).toBe('/admin/inference-providers/p-az');
    // Read-only: no control acts on a merge.
    expect(within(row).queryByRole('button')).toBeNull();
  });
});
