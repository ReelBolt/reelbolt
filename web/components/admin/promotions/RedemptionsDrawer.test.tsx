// @vitest-environment jsdom

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, cleanup, within, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { Promotion, PromotionRedemption } from '@/lib/api/promotions';

vi.stubGlobal(
  'ResizeObserver',
  class ResizeObserver {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);

vi.mock('@/lib/hooks/use-promotions', () => ({
  usePromotionRedemptions: vi.fn(),
}));

import { usePromotionRedemptions } from '@/lib/hooks/use-promotions';
import { RedemptionsDrawer, notGrantedReasonLabel } from './RedemptionsDrawer';

const hookMock = vi.mocked(usePromotionRedemptions);

const PROMOTION = { id: 'p1', name: 'Spring bonus' } as Promotion;

function row(overrides: Partial<PromotionRedemption>): PromotionRedemption {
  return {
    id: 'r1',
    kind: 'bonusGranted',
    organizationId: 'org-1',
    organizationName: 'Acme',
    userId: null,
    providerTransactionId: 'txn_1',
    credits: 100,
    discountTotal: null,
    currencyCode: null,
    overshoot: false,
    outcome: 'granted',
    notGrantedReason: null,
    discountRemovedAt: null,
    createdAt: '2026-10-01T10:00:00Z',
    ...overrides,
  };
}

function renderWith(rows: PromotionRedemption[]) {
  hookMock.mockReturnValue({ data: rows, error: undefined, isLoading: false } as unknown as ReturnType<
    typeof usePromotionRedemptions
  >);
  render(
    <MantineProvider env="test">
      <RedemptionsDrawer promotion={PROMOTION} onClose={() => {}} />
    </MantineProvider>,
  );
  const table = screen.getByTestId('promotion-redemptions-table');
  return within(table).getAllByRole('row').slice(1);
}

describe('RedemptionsDrawer', () => {
  beforeEach(() => {
    global.matchMedia = ((q: string) => ({
      matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
      addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
    })) as unknown as typeof matchMedia;
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it('shows a granted bonus as granted', () => {
    const [tr] = renderWith([row({})]);
    expect(tr.textContent).toContain('Bonus granted');
    expect(tr.textContent).not.toContain('Not given');
  });

  it('never shows a not-granted bonus as granted, and says why', () => {
    const [tr] = renderWith([row({ outcome: 'notGranted', notGrantedReason: 'userCapReached' })]);
    expect(tr.textContent).not.toMatch(/granted/i);
    expect(tr.textContent).toContain('Not given');
    expect(tr.textContent).toContain(notGrantedReasonLabel('userCapReached'));
  });

  it('falls back to a plain "Not given" for an unknown reason', () => {
    const [tr] = renderWith([row({ outcome: 'notGranted', notGrantedReason: 'somethingNew' })]);
    expect(tr.textContent).toContain('Not given');
    expect(tr.textContent).not.toMatch(/granted|somethingNew/i);
  });

  it('marks an overshoot row with an "Over limit" badge and explains it', async () => {
    const [plain, over] = renderWith([row({ id: 'a' }), row({ id: 'b', overshoot: true })]);
    expect(plain.textContent).not.toContain('Over limit');
    const badge = within(over).getByText('Over limit');
    // floating-ui's hover opens on mousemove under jsdom (there is no real pointerenter).
    fireEvent.mouseMove(badge);
    await waitFor(() =>
      expect(screen.getByText('Given past the limit because the checkout was reserved before it filled')).toBeTruthy(),
    );
  });

  it('marks a discount a plan change ended, with the date it ended', () => {
    const removedAt = '2026-10-05T14:30:00Z';
    const [kept, ended] = renderWith([
      row({ id: 'a', kind: 'discountApplied', credits: null, discountTotal: '580', currencyCode: 'USD' }),
      row({ id: 'b', kind: 'discountApplied', credits: null, discountTotal: '580', currencyCode: 'USD', discountRemovedAt: removedAt }),
    ]);
    expect(kept.textContent).not.toContain('Ended on plan change');
    expect(ended.textContent).toContain('Discount applied');
    expect(ended.textContent).toContain('Ended on plan change');
    expect(ended.textContent).toContain(new Date(removedAt).toLocaleDateString(undefined, { dateStyle: 'medium' }));
  });
});

describe('notGrantedReasonLabel', () => {
  it('has a distinct plain-English label for every API reason', () => {
    const reasons = [
      'superseded',
      'expired',
      'reservedOverCap',
      'notReserved',
      'overshootBoundReached',
      'userCapReached',
      'orgCapReached',
      'checkoutMismatch',
    ];
    const labels = reasons.map(notGrantedReasonLabel);
    for (const label of labels) expect(label).toBeTruthy();
    expect(new Set(labels).size).toBe(reasons.length);
  });

  it('gives nothing for a missing or unknown reason', () => {
    expect(notGrantedReasonLabel(null)).toBeNull();
    expect(notGrantedReasonLabel('somethingNew')).toBeNull();
  });
});
