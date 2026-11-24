import { describe, expect, it } from 'vitest';
import type { BillingPlan } from '@/lib/types/billing';
import { startPlanCheckout, changeSubscriptionPlan } from '@/lib/api/billing';
import { previewItems, priceIdFor } from '@/lib/paddle/pricing';
import { annualPerMonth, effectiveInterval, monthsFree, sellsAnnual } from './plan-interval';
import { vi } from 'vitest';

const plan = (over: Partial<BillingPlan> = {}): BillingPlan => ({
  key: 'pro', name: 'Pro', kind: 'Subscription', priceMonthlyUsd: 79, minSeats: null, includedCredits: 3500,
  priceId: 'pri_m', priceAnnualUsd: 790, annualPriceId: 'pri_y', current: false, ...over,
});

describe('plan-interval', () => {
  it('derives months free only when exactly whole', () => {
    expect(monthsFree(plan())).toBe(2);
    expect(monthsFree(plan({ priceAnnualUsd: 800 }))).toBeNull();
    expect(monthsFree(plan({ priceAnnualUsd: 948 }))).toBeNull();
    expect(monthsFree(plan({ priceAnnualUsd: null }))).toBeNull();
  });
  it('spreads the yearly price over twelve months', () => {
    expect(annualPerMonth(plan({ priceAnnualUsd: 120 }))).toBe(10);
    expect(annualPerMonth(plan({ priceAnnualUsd: null }))).toBeNull();
  });
  it('sells yearly only with a price and a provider price id', () => {
    expect(sellsAnnual(plan())).toBe(true);
    expect(sellsAnnual(plan({ annualPriceId: null }))).toBe(false);
    expect(sellsAnnual(plan({ priceAnnualUsd: undefined }))).toBe(false);
  });
  it('keeps a yearly subscriber on yearly', () => {
    expect(effectiveInterval('monthly', { interval: 'annual' })).toBe('annual');
    expect(effectiveInterval(null, { interval: 'monthly' })).toBe('monthly');
    expect(effectiveInterval('annual', null)).toBe('annual');
  });
  it('picks the price id per interval for Paddle previews', () => {
    expect(priceIdFor(plan(), 'annual')).toBe('pri_y');
    expect(previewItems([plan(), plan({ key: 'x', annualPriceId: null })], 'annual')).toEqual([{ priceId: 'pri_y', quantity: 1 }]);
    expect(previewItems([plan()])).toEqual([{ priceId: 'pri_m', quantity: 1 }]);
  });
});

vi.mock('@/lib/api/client', async (orig) => ({ ...(await orig<typeof import('@/lib/api/client')>()), apiFetch: vi.fn().mockResolvedValue({}) }));

describe('request bodies', () => {
  it('adds interval only for yearly', async () => {
    const { apiFetch } = await import('@/lib/api/client');
    const fetchMock = vi.mocked(apiFetch);
    await startPlanCheckout('pro', undefined, undefined, 'annual');
    expect(JSON.parse(fetchMock.mock.calls.at(-1)?.[1]?.body as string)).toEqual({ plan: 'pro', interval: 'annual' });
    await startPlanCheckout('pro', 2);
    expect(JSON.parse(fetchMock.mock.calls.at(-1)?.[1]?.body as string)).toEqual({ plan: 'pro', seats: 2 });
    await changeSubscriptionPlan('pro', 2, 'annual');
    expect(JSON.parse(fetchMock.mock.calls.at(-1)?.[1]?.body as string)).toEqual({ plan: 'pro', seats: 2, interval: 'annual' });
    await changeSubscriptionPlan('pro');
    expect(JSON.parse(fetchMock.mock.calls.at(-1)?.[1]?.body as string)).toEqual({ plan: 'pro' });
  });
});
