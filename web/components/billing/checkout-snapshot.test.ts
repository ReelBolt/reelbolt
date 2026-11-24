// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { BillingSummary } from '@/lib/types/billing';
import { clearCheckoutSnapshot, purchaseVisible, readCheckoutSnapshot, saveCheckoutSnapshot, type CheckoutSnapshot } from './checkout-snapshot';

const summary = (planKey: string, balance: number): BillingSummary =>
  ({
    subscription: { planKey, planName: planKey, status: 'Active', seats: 1, periodEnd: planKey === 'trial' ? null : '2027-01-01T00:00:00Z' },
    plans: planKey === 'trial' ? [] : [{ key: planKey, current: true }],
    credits: { balance, reserved: 0, available: balance },
  }) as unknown as BillingSummary;

const before = (target: CheckoutSnapshot['target']): CheckoutSnapshot => ({ planKey: 'trial', balance: 100, livePaid: false, at: 0, target });

describe('purchaseVisible', () => {
  it('matches the intended plan, not just any change', () => {
    expect(purchaseVisible(before({ kind: 'plan', planKey: 'pro' }), summary('creator', 1100))).toBe(false);
    expect(purchaseVisible(before({ kind: 'plan', planKey: 'creator' }), summary('creator', 1100))).toBe(true);
  });
  it('needs the whole pack for a top-up', () => {
    expect(purchaseVisible(before({ kind: 'topUp', credits: 500 }), summary('creator', 300))).toBe(false);
    expect(purchaseVisible(before({ kind: 'topUp', credits: 500 }), summary('creator', 600))).toBe(true);
  });
  it('without a target, any change counts and no change does not', () => {
    expect(purchaseVisible(before(null), summary('trial', 100))).toBe(false);
    expect(purchaseVisible(before(null), summary('creator', 100))).toBe(true);
  });
});

describe('snapshot storage', () => {
  afterEach(() => { vi.restoreAllMocks(); window.sessionStorage.clear(); });

  it('returns a fresh snapshot and drops one older than 2 hours', () => {
    saveCheckoutSnapshot({ planKey: 'trial', balance: 100, livePaid: false, at: 1_000, target: null });
    expect(readCheckoutSnapshot(1_000 + 60_000)?.balance).toBe(100);
    expect(readCheckoutSnapshot(1_000 + 2 * 60 * 60 * 1000 + 1)).toBeNull();
  });

  it('never throws when sessionStorage does', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('full'); });
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked'); });
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => { throw new Error('blocked'); });
    expect(() => saveCheckoutSnapshot({ planKey: null, balance: 0, livePaid: false, at: 1, target: null })).not.toThrow();
    expect(readCheckoutSnapshot()).toBeNull();
    expect(() => clearCheckoutSnapshot()).not.toThrow();
  });

  it('treats corrupt content as no snapshot', () => {
    window.sessionStorage.setItem('reelbolt.checkout-snapshot', '{nope');
    expect(readCheckoutSnapshot()).toBeNull();
  });
});
