// @vitest-environment jsdom

/**
 * The dashboard-wide low-balance bar (B10c-UI). The cases that matter most are the ones where it
 * must NOT appear: a deployment without billing, a summary that failed to load, and a workspace
 * whose plan allowance is not on the wire. A warning nobody can act on is worse than none.
 */
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { LowBalanceBanner } from './LowBalanceBanner';
import { useBillingSummary } from '@/lib/hooks/use-billing';
import { ApiError } from '@/lib/api/client';
import type { BillingPlan, BillingSummary } from '@/lib/types/billing';

vi.mock('@/lib/hooks/use-billing', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/hooks/use-billing')>();
  return { ...actual, useBillingSummary: vi.fn() };
});

const summaryMock = vi.mocked(useBillingSummary);
type SummaryResult = ReturnType<typeof useBillingSummary>;

const result = (over: Partial<SummaryResult>) =>
  ({ data: undefined, error: undefined, isLoading: false, mutate: vi.fn(), ...over }) as unknown as SummaryResult;

const PLAN_PRO: BillingPlan = {
  key: 'pro', name: 'Pro', kind: 'Subscription', priceMonthlyUsd: 79,
  minSeats: null, includedCredits: 3500, priceId: 'pri_pro', current: true,
};

const summary = (available: number, over: Partial<BillingSummary> = {}): BillingSummary => ({
  billingProvider: 'paddle',
  paddle: null,
  canManageBilling: false,
  subscription: {
    planKey: 'pro', planName: 'Pro', status: 'Active', seats: 1,
    periodStart: '2026-09-10T00:00:00Z', periodEnd: '2026-10-10T00:00:00Z',
    cancelAtPeriodEnd: false, hasBillingCustomer: true,
  },
  credits: { balance: available, reserved: 0, available },
  topUp: { available: true, packs: [500] },
  plans: [PLAN_PRO],
  ...over,
});

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query) => ({
      matches: false, media: query, onchange: null,
      addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
});

afterEach(() => {
  cleanup();
  summaryMock.mockReset();
});

const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);

describe('LowBalanceBanner', () => {
  it('stays quiet while the balance covers the plan', () => {
    summaryMock.mockReturnValue(result({ data: summary(900) }));
    wrap(<LowBalanceBanner />);

    expect(screen.queryByTestId('low-balance-banner')).toBeNull();
  });

  it('warns below 15% of what the plan grants, and points at the top-up page', () => {
    summaryMock.mockReturnValue(result({ data: summary(300) }));
    wrap(<LowBalanceBanner />);

    expect(screen.getByText(/Your credits are running low/)).toBeTruthy();
    expect(screen.getByText(/300 of the 3,500 credits/)).toBeTruthy();
    expect(screen.getByText(/about 9%/)).toBeTruthy();
    expect(screen.getByRole('link', { name: /Top up credits/ }).getAttribute('href')).toBe('/settings/billing');
  });

  it('stays quiet on a deployment without billing, whose summary answers 404', () => {
    summaryMock.mockReturnValue(result({ error: new ApiError('Not Found', 404) }));
    wrap(<LowBalanceBanner />);

    expect(screen.queryByTestId('low-balance-banner')).toBeNull();
  });

  it('stays quiet when the summary failed for any other reason', () => {
    summaryMock.mockReturnValue(result({ error: new Error('offline') }));
    wrap(<LowBalanceBanner />);

    expect(screen.queryByTestId('low-balance-banner')).toBeNull();
  });

  it('stays quiet when the plan allowance is not on the wire, rather than inventing a threshold', () => {
    // The summary lists only the plans it can sell, so a Trial workspace has no allowance at all.
    summaryMock.mockReturnValue(result({ data: summary(3, { plans: [] }) }));
    wrap(<LowBalanceBanner />);

    expect(screen.queryByTestId('low-balance-banner')).toBeNull();
  });

  it('counts credits held by runs in flight as spent, because the next run cannot use them', () => {
    summaryMock.mockReturnValue(
      result({ data: summary(300, { credits: { balance: 3500, reserved: 3200, available: 300 } }) }),
    );
    wrap(<LowBalanceBanner />);

    expect(screen.getByText(/300 of the 3,500 credits/)).toBeTruthy();
  });
});
