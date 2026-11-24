import { afterEach, describe, expect, it, vi } from 'vitest';
import type { BillingPlan, BillingSummary } from '@/lib/types/billing';
import {
  cancelConfirmed,
  creditsFromMillis,
  hasLivePaidPlan,
  pickedPlanCheckoutHref,
  planChangeConfirmed,
  planDirection,
  pollSummary,
  providerDisplayName,
  merchantLabel,
  providerTestIdSuffix,
  isNonPaddleProvider,
  resumeConfirmed,
} from './subscription';

const plan = (over: Partial<BillingPlan> = {}): BillingPlan => ({
  key: 'creator', name: 'Creator', kind: 'Subscription', priceMonthlyUsd: 29, minSeats: null,
  includedCredits: 1000, priceId: 'pri_creator', current: false, ...over,
});

const CREATOR = plan();
const PRO = plan({ key: 'pro', name: 'Pro', priceMonthlyUsd: 79, priceId: 'pri_pro' });
const TEAM = plan({ key: 'team', name: 'Team', kind: 'PerSeat', priceMonthlyUsd: 49, minSeats: 3, priceId: 'pri_team' });

const summary = (sub: Partial<NonNullable<BillingSummary['subscription']>> | null, plans: BillingPlan[] = [CREATOR, PRO, TEAM]): BillingSummary => ({
  billingProvider: 'paddle',
  paddle: null,
  canManageBilling: true,
  subscription: sub === null ? null : {
    planKey: 'pro', planName: 'Pro', status: 'Active', seats: 1,
    periodStart: '2026-09-10T00:00:00Z', periodEnd: '2026-10-10T00:00:00Z',
    cancelAtPeriodEnd: false, cancelEffectiveAt: null, scheduledChange: null, hasBillingCustomer: true,
    ...sub,
  },
  credits: { balance: 0, reserved: 0, available: 0 },
  topUp: { available: false, packs: [] },
  plans,
});

const onPro = [CREATOR, { ...PRO, current: true }, TEAM];

describe('hasLivePaidPlan', () => {
  it('is true for a live plan on sale, whatever its live status', () => {
    for (const status of ['Active', 'Trialing', 'PastDue', 'Paused']) {
      expect(hasLivePaidPlan(summary({ status }, onPro))).toBe(true);
    }
  });

  it('is false for the free plan even when the workspace kept its Paddle customer (canceled, abandoned checkout)', () => {
    const trial = { planKey: 'trial', planName: 'Trial', periodStart: null, periodEnd: null, hasBillingCustomer: true };
    expect(hasLivePaidPlan(summary(trial))).toBe(false);
    expect(hasLivePaidPlan(summary({ ...trial, hasBillingCustomer: false }))).toBe(false);
  });

  it('is false for a canceled status and for no subscription at all', () => {
    expect(hasLivePaidPlan(summary({ status: 'Canceled' }, onPro))).toBe(false);
    expect(hasLivePaidPlan(summary(null))).toBe(false);
  });

  it('counts a plan not on sale as live when it has a billing period (an arranged plan)', () => {
    expect(hasLivePaidPlan(summary({ planKey: 'enterprise', planName: 'Enterprise' }))).toBe(true);
    expect(hasLivePaidPlan(summary({ planKey: 'enterprise', planName: 'Enterprise', periodEnd: null }))).toBe(false);
  });
});

describe('planDirection', () => {
  it('compares monthly price × seats like the server', () => {
    expect(planDirection(PRO, 1, CREATOR, 1)).toBe('downgrade');
    expect(planDirection(CREATOR, 1, PRO, 1)).toBe('upgrade');
    // Team at 3 seats is $147, above Pro's $79; Pro is a downgrade from it.
    expect(planDirection(PRO, 1, TEAM, 3)).toBe('upgrade');
    expect(planDirection(TEAM, 3, PRO, 1)).toBe('downgrade');
  });

  it('treats an equal price as an upgrade, and seats on a per-seat plan', () => {
    expect(planDirection(PRO, 1, { ...CREATOR, priceMonthlyUsd: 79 }, 1)).toBe('upgrade');
    expect(planDirection(TEAM, 5, TEAM, 4)).toBe('downgrade');
    expect(planDirection(TEAM, 4, TEAM, 5)).toBe('upgrade');
  });
});

describe('providerDisplayName', () => {
  it('names Paddle properly and capitalises an unknown key', () => {
    expect(providerDisplayName('paddle')).toBe('Paddle');
    expect(providerDisplayName('PADDLE')).toBe('Paddle');
    expect(providerDisplayName('stripe')).toBe('Stripe');
    expect(providerDisplayName(null)).toBeNull();
    expect(providerDisplayName('  ')).toBeNull();
  });
});

describe('creditsFromMillis', () => {
  it('formats the server millicredits as credits', () => {
    expect(creditsFromMillis(1_234_000)).toBe((1234).toLocaleString(undefined, { maximumFractionDigits: 0 }));
  });
});

describe('confirmation checks', () => {
  it('cancel / resume follow cancelAtPeriodEnd', () => {
    expect(cancelConfirmed(summary({ cancelAtPeriodEnd: true }))).toBe(true);
    expect(cancelConfirmed(summary({}))).toBe(false);
    expect(resumeConfirmed(summary({}))).toBe(true);
    expect(resumeConfirmed(summary({ cancelAtPeriodEnd: true }))).toBe(false);
  });

  it('an upgrade waits for the plan and seats; a downgrade for the scheduled change; keep for it to clear', () => {
    const up = planChangeConfirmed('team', { direction: 'upgrade', seats: 4 });
    expect(up(summary({}))).toBe(false);
    expect(up(summary({ planKey: 'team', seats: 3 }))).toBe(false);
    expect(up(summary({ planKey: 'team', seats: 4 }))).toBe(true);

    const down = planChangeConfirmed('creator', { direction: 'downgrade', seats: 1 });
    expect(down(summary({}))).toBe(false);
    expect(down(summary({ scheduledChange: { planKey: 'creator', planName: 'Creator', seats: 1, effectiveAt: '2026-10-10T00:00:00Z' } }))).toBe(true);

    const keep = planChangeConfirmed('pro', { direction: 'keep', seats: 1 });
    expect(keep(summary({ scheduledChange: { planKey: 'creator', planName: 'Creator', seats: 1, effectiveAt: '2026-10-10T00:00:00Z' } }))).toBe(false);
    expect(keep(summary({}))).toBe(true);
  });
});

describe('pollSummary', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it('refreshes once a second until the check passes', async () => {
    vi.useFakeTimers();
    const answers = [summary({}), summary({}), summary({ cancelAtPeriodEnd: true })];
    const refresh = vi.fn(async () => answers.shift());

    const done = pollSummary(refresh, cancelConfirmed);
    await vi.advanceTimersByTimeAsync(2000);

    await expect(done).resolves.toMatchObject({ subscription: { cancelAtPeriodEnd: true } });
    expect(refresh).toHaveBeenCalledTimes(3);
  });

  it('gives up after 20 seconds with null, never a success', async () => {
    vi.useFakeTimers();
    const refresh = vi.fn(async () => summary({}));

    const done = pollSummary(refresh, cancelConfirmed);
    await vi.advanceTimersByTimeAsync(25_000);

    await expect(done).resolves.toBeNull();
    expect(refresh.mock.calls.length).toBeGreaterThanOrEqual(19);
    expect(refresh.mock.calls.length).toBeLessThanOrEqual(21);
  });

  it('treats a failed refresh as a miss, and stops when cancelled', async () => {
    vi.useFakeTimers();
    let cancelled = false;
    const refresh = vi.fn(async () => {
      throw new Error('offline');
    });

    const done = pollSummary(refresh, cancelConfirmed, { isCancelled: () => cancelled });
    await vi.advanceTimersByTimeAsync(1000);
    cancelled = true;
    await vi.advanceTimersByTimeAsync(1000);

    await expect(done).resolves.toBeNull();
    expect(refresh.mock.calls.length).toBeLessThanOrEqual(3);
  });
});

describe('provider names (Stripe)', () => {
  it('prefers the API display name, knows Stripe, and falls back neutrally', () => {
    expect(providerDisplayName('stripe')).toBe('Stripe');
    expect(providerDisplayName('stripe', 'Stripe Payments')).toBe('Stripe Payments');
    expect(merchantLabel({ billingProvider: null })).toBe('your payment provider');
    expect(merchantLabel({ billingProvider: 'paddle' })).toBe('Paddle');
    expect(providerTestIdSuffix({ billingProvider: 'stripe' })).toBe('stripe');
    expect(isNonPaddleProvider({ billingProvider: 'stripe' })).toBe(true);
    expect(isNonPaddleProvider({ billingProvider: 'paddle' })).toBe(false);
  });
});

describe('pickedPlanCheckoutHref', () => {
  const free = summary(null, [CREATOR, PRO, TEAM]);
  const HREF = '/settings/billing?plan=pro&start=1#plans';

  it('links a plan on sale to its checkout start, with no price in it', () => {
    expect(pickedPlanCheckoutHref('pro', free, true)).toBe(HREF);
    expect(pickedPlanCheckoutHref('pro', free, undefined)).toBe(HREF);
    expect(pickedPlanCheckoutHref('pro', summary({ planKey: 'trial', status: 'Active', periodEnd: null }, [CREATOR, PRO, TEAM]), true)).toBe(HREF);
  });

  it('answers null for an absent, unknown or already held plan', () => {
    expect(pickedPlanCheckoutHref(undefined, free, true)).toBeNull();
    expect(pickedPlanCheckoutHref('', free, true)).toBeNull();
    expect(pickedPlanCheckoutHref('nope', free, true)).toBeNull();
    expect(pickedPlanCheckoutHref('pro', summary(null, [CREATOR, { ...PRO, current: true }, TEAM]), true)).toBeNull();
  });

  it('answers null for a per-seat plan and the trial key', () => {
    expect(pickedPlanCheckoutHref('team', free, true)).toBeNull();
    expect(pickedPlanCheckoutHref('trial', summary(null, [plan({ key: 'trial' }), PRO]), true)).toBeNull();
  });

  it('answers null on a live paid plan (that changes through change-plan)', () => {
    expect(pickedPlanCheckoutHref('creator', summary({ status: 'Active' }, onPro), true)).toBeNull();
  });

  it('answers null for a member who cannot manage billing, an unverified address, or no summary', () => {
    expect(pickedPlanCheckoutHref('pro', { ...free, canManageBilling: false }, true)).toBeNull();
    expect(pickedPlanCheckoutHref('pro', free, false)).toBeNull();
    expect(pickedPlanCheckoutHref('pro', undefined, true)).toBeNull();
  });

  it('never builds a link from a stored value that is not a plan on sale', () => {
    expect(pickedPlanCheckoutHref('../evil', free, true)).toBeNull();
    expect(pickedPlanCheckoutHref('https://x', free, true)).toBeNull();
  });

  it('encodes the summary\'s own key', () => {
    const odd = plan({ key: 'a b&c', name: 'Odd' });
    expect(pickedPlanCheckoutHref('a b&c', summary(null, [odd]), true)).toBe('/settings/billing?plan=a%20b%26c&start=1#plans');
  });
});
