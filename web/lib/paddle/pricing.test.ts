// @vitest-environment jsdom

import { describe, it, expect } from 'vitest';
import {
  checkoutSuccessUrl,
  paddleClientOf,
  previewItems,
  totalsByPriceId,
} from './pricing';
import type { BillingPlan, BillingSummary } from '../types/billing';
import type { PricePreviewResponse } from '@paddle/paddle-js';

const plan = (over: Partial<BillingPlan> = {}): BillingPlan => ({
  key: 'creator',
  name: 'Creator',
  kind: 'Subscription',
  priceMonthlyUsd: 29,
  minSeats: null,
  includedCredits: 1000,
  priceId: 'pri_creator',
  current: false,
  ...over,
});

describe('checkoutSuccessUrl', () => {
  it("is the app's own checkout page, under the dashboard's base path", () => {
    expect(checkoutSuccessUrl('https://app.reelbolt.ai')).toBe('https://app.reelbolt.ai/app/checkout');
  });

  it('tolerates a trailing slash on the origin rather than doubling it', () => {
    expect(checkoutSuccessUrl('http://localhost:3000/')).toBe('http://localhost:3000/app/checkout');
  });

  it('is absolute, because Paddle navigates the browser from its own origin', () => {
    // A relative '/checkout' would resolve against Paddle's domain and 404.
    expect(new URL(checkoutSuccessUrl('https://example.test')).host).toBe('example.test');
  });
});

describe('paddleClientOf', () => {
  const summary = (over: Partial<BillingSummary> = {}): BillingSummary => ({
    billingProvider: 'paddle',
    paddle: { environment: 'sandbox', clientToken: 'test_87d07575', customerId: null },
    canManageBilling: true,
    subscription: null,
    credits: { balance: 0, reserved: 0, available: 0 },
    topUp: { available: false, packs: [] },
    plans: [],
    ...over,
  });

  it('returns the settings the API served', () => {
    expect(paddleClientOf(summary())?.clientToken).toBe('test_87d07575');
  });

  it('is null when another provider is configured, when nothing is served, and before the summary arrives', () => {
    expect(paddleClientOf(summary({ billingProvider: 'stripe', paddle: null }))).toBeNull();
    expect(paddleClientOf(summary({ paddle: null }))).toBeNull();
    expect(paddleClientOf(undefined)).toBeNull();
  });

  it('is null for a token the API sent empty, so no checkout is initialized with a blank token', () => {
    expect(paddleClientOf(summary({ paddle: { environment: 'production', clientToken: '   ', customerId: null } }))).toBeNull();
  });

  it('returns the config when the organization has no Paddle customer id, so a first-time buyer still gets the embedded checkout', () => {
    const config = paddleClientOf(summary({
      paddle: { environment: 'sandbox', clientToken: 'test_87d07575', customerId: null },
    }));

    expect(config?.clientToken).toBe('test_87d07575');
    expect(config?.customerId).toBeNull();
  });

  it('passes the served Paddle customer id through, so Retain can identify the buyer', () => {
    const config = paddleClientOf(summary({
      paddle: { environment: 'production', clientToken: 'live_abc', customerId: 'ctm_01hbuyer' },
    }));

    expect(config?.customerId).toBe('ctm_01hbuyer');
  });
});

describe('previewItems', () => {
  it('asks for one of each plan, keyed by the price id the checkout will use', () => {
    expect(previewItems([plan(), plan({ key: 'team', priceId: 'pri_team' })])).toEqual([
      { priceId: 'pri_creator', quantity: 1 },
      { priceId: 'pri_team', quantity: 1 },
    ]);
  });

  it('never passes a missing or blank price id to Paddle', () => {
    expect(previewItems([plan({ priceId: '' }), plan({ key: 'x', priceId: '   ' })])).toEqual([]);
  });
});

describe('totalsByPriceId', () => {
  const preview = (lineItems: unknown[]): PricePreviewResponse =>
    ({ data: { details: { lineItems } }, meta: { requestId: 'r' } }) as unknown as PricePreviewResponse;

  it("keys Paddle's own formatted total by the price it belongs to", () => {
    const totals = totalsByPriceId(preview([
      { price: { id: 'pri_creator' }, formattedTotals: { subtotal: 'EUR 29.00', total: 'EUR 31.90' } },
      { price: { id: 'pri_team' }, formattedTotals: { subtotal: 'EUR 49.00', total: 'EUR 53.90' } },
    ]));

    expect(totals.get('pri_creator')).toBe('EUR 31.90');
    expect(totals.get('pri_team')).toBe('EUR 53.90');
  });

  it('drops a line without a price id or without a formatted total rather than inventing one', () => {
    expect(totalsByPriceId(preview([
      { price: {}, formattedTotals: { total: 'EUR 10.00' } },
      { price: { id: 'pri_x' }, formattedTotals: {} },
      { price: { id: 'pri_y' }, formattedTotals: { total: '   ' } },
    ])).size).toBe(0);
  });

  it('is empty for a missing or malformed answer, never a zero price', () => {
    expect(totalsByPriceId(null).size).toBe(0);
    expect(totalsByPriceId(undefined).size).toBe(0);
    expect(totalsByPriceId({} as PricePreviewResponse).size).toBe(0);
  });
});
