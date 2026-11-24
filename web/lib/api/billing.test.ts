import { describe, it, expect, vi, beforeEach } from 'vitest';

// `apiFetch` is stubbed so the assertion is on the URL the client builds, not on fetch semantics
// (client.test.ts already covers those). There is no MSW in this repo.
vi.mock('./client', async () => {
  const actual = await vi.importActual<typeof import('./client')>('./client');
  return { ...actual, apiFetch: vi.fn() };
});

import { ApiError, apiFetch } from './client';
import {
  BILLING_CHECKOUT_PATH,
  BILLING_PORTAL_PATH,
  BILLING_SEATS_PATH,
  BILLING_SUMMARY_PATH,
  BILLING_USAGE_PATH,
  BILLING_CANCEL_PATH,
  BILLING_CHANGE_PLAN_PATH,
  BILLING_RESUME_PATH,
  billingActionMessage,
  cancelSubscription,
  changeBillingSeats,
  changeSubscriptionPlan,
  resumeSubscription,
  getBillingSummary,
  getBillingUsage,
  getBillingUsageUrl,
  getWorkflowCostEstimate,
  insufficientCredits,
  isBillingUnavailable,
  isSafeRedirectUrl,
  openBillingPortal,
  startPlanCheckout,
  startTopUpCheckout,
  workflowEstimatePath,
} from './billing';

const apiFetchMock = vi.mocked(apiFetch);

beforeEach(() => {
  apiFetchMock.mockReset();
  apiFetchMock.mockResolvedValue({} as never);
});

describe('getBillingUsageUrl', () => {
  it('sends no params at all when the caller supplies none, letting the backend default', () => {
    expect(getBillingUsageUrl()).toBe(BILLING_USAGE_PATH);
    expect(getBillingUsageUrl({})).toBe(BILLING_USAGE_PATH);
  });

  it('appends only the supplied params, in a stable order', () => {
    expect(getBillingUsageUrl({ groupBy: 'project', from: '2026-10-01T00:00:00.000Z' })).toBe(
      `${BILLING_USAGE_PATH}?from=2026-10-01T00%3A00%3A00.000Z&groupBy=project`,
    );
  });

  it('treats an empty or whitespace value as not supplied rather than sending a meaningless filter', () => {
    expect(getBillingUsageUrl({ groupBy: 'day', from: '   ' })).toBe(`${BILLING_USAGE_PATH}?groupBy=day`);
  });

  it('passes an execution filter through, which is how the run page asks for one run', () => {
    expect(getBillingUsageUrl({ executionId: 'ex1' })).toBe(`${BILLING_USAGE_PATH}?executionId=ex1`);
  });

  it('fetches the usage URL it built, and the summary from its own path', async () => {
    await getBillingUsage({ groupBy: 'kind' });
    expect(apiFetchMock.mock.calls[0][0]).toBe(`${BILLING_USAGE_PATH}?groupBy=kind`);

    apiFetchMock.mockClear();
    await getBillingSummary();
    expect(apiFetchMock.mock.calls[0][0]).toBe(BILLING_SUMMARY_PATH);
  });
});

describe('isBillingUnavailable', () => {
  it('is true only for the 404 a deployment without billing answers with', () => {
    expect(isBillingUnavailable(new ApiError('Not Found', 404))).toBe(true);
    expect(isBillingUnavailable(new ApiError('Forbidden', 403))).toBe(false);
    expect(isBillingUnavailable(new Error('offline'))).toBe(false);
    expect(isBillingUnavailable(undefined)).toBe(false);
  });
});

describe('purchases', () => {
  it('posts exactly one of plan or topUpCredits, the only two shapes the endpoint accepts', async () => {
    await startPlanCheckout('creator');
    expect(apiFetchMock.mock.calls[0]).toEqual([
      BILLING_CHECKOUT_PATH,
      { method: 'POST', body: JSON.stringify({ plan: 'creator' }) },
    ]);

    apiFetchMock.mockClear();
    await startTopUpCheckout(1000);
    expect(apiFetchMock.mock.calls[0]).toEqual([
      BILLING_CHECKOUT_PATH,
      { method: 'POST', body: JSON.stringify({ topUpCredits: 1000 }) },
    ]);
  });

  it('sends the seat count only when the caller supplied one, so a per-seat plan still defaults to its minimum', async () => {
    await startPlanCheckout('team', 7);
    expect(apiFetchMock.mock.calls[0][1]).toMatchObject({ body: JSON.stringify({ plan: 'team', seats: 7 }) });

    apiFetchMock.mockClear();
    await startPlanCheckout('team', undefined);
    expect(apiFetchMock.mock.calls[0][1]).toMatchObject({ body: JSON.stringify({ plan: 'team' }) });
  });

  it('passes an applied promo code to the checkout, and only when there is one', async () => {
    await startPlanCheckout('team', 7, 'SPRING20');
    expect(apiFetchMock.mock.calls[0][1]).toMatchObject({
      body: JSON.stringify({ plan: 'team', seats: 7, promoCode: 'SPRING20' }),
    });

    apiFetchMock.mockClear();
    await startTopUpCheckout(1000, 'PACK10');
    expect(apiFetchMock.mock.calls[0][1]).toMatchObject({
      body: JSON.stringify({ topUpCredits: 1000, promoCode: 'PACK10' }),
    });

    apiFetchMock.mockClear();
    await startPlanCheckout('creator', undefined, '');
    expect(apiFetchMock.mock.calls[0][1]).toMatchObject({ body: JSON.stringify({ plan: 'creator' }) });
  });

  it('posts cancel, resume and change plan to their own routes', async () => {
    await cancelSubscription();
    expect(apiFetchMock.mock.calls[0]).toEqual([BILLING_CANCEL_PATH, { method: 'POST' }]);
    await resumeSubscription();
    expect(apiFetchMock.mock.calls[1]).toEqual([BILLING_RESUME_PATH, { method: 'POST' }]);
    await changeSubscriptionPlan('creator');
    expect(apiFetchMock.mock.calls[2]).toEqual([BILLING_CHANGE_PLAN_PATH, { method: 'POST', body: JSON.stringify({ plan: 'creator' }) }]);
    await changeSubscriptionPlan('team', 4);
    expect(apiFetchMock.mock.calls[3][1]).toMatchObject({ body: JSON.stringify({ plan: 'team', seats: 4 }) });
  });

  it('posts the portal and seat actions with no body of their own', async () => {
    await openBillingPortal();
    expect(apiFetchMock.mock.calls[0]).toEqual([BILLING_PORTAL_PATH, { method: 'POST' }]);

    apiFetchMock.mockClear();
    await changeBillingSeats(5);
    expect(apiFetchMock.mock.calls[0]).toEqual([
      BILLING_SEATS_PATH,
      { method: 'POST', body: JSON.stringify({ seats: 5 }) },
    ]);
  });
});

describe('isSafeRedirectUrl', () => {
  it('follows only http and https, whatever else the provider hands back', () => {
    expect(isSafeRedirectUrl('https://checkout.paddle.test/_abc')).toBe(true);
    expect(isSafeRedirectUrl('http://localhost:8080/checkout')).toBe(true);

    // A provider link is still untrusted input: following any of these would run it in our origin.
    expect(isSafeRedirectUrl('javascript:alert(1)')).toBe(false);
    expect(isSafeRedirectUrl('data:text/html,<script>1</script>')).toBe(false);
    expect(isSafeRedirectUrl('//evil.test/checkout')).toBe(false);
    expect(isSafeRedirectUrl('/app/settings/billing')).toBe(false);
    expect(isSafeRedirectUrl('')).toBe(false);
    expect(isSafeRedirectUrl(undefined)).toBe(false);
    expect(isSafeRedirectUrl(42)).toBe(false);
  });
});

describe('billingActionMessage', () => {
  it('explains a machine refusal code, which the API client would otherwise surface raw', () => {
    // The endpoint sends these with no `message`, so `ApiError.message` IS the code.
    expect(billingActionMessage(new ApiError('already_subscribed', 409)))
      .toBe('This workspace already has a plan. Change it from Plan & billing.');
    expect(billingActionMessage(new ApiError('topup_requires_paid_plan', 403)))
      .toContain('paid plans only');
    expect(billingActionMessage(new ApiError('below_min_seats', 400)))
      .toContain('fewer seats');
  });

  it('reads the code from the body even when the server also sent a sentence', () => {
    const refusal = (body: Record<string, unknown>) =>
      new ApiError(typeof body.message === 'string' ? body.message : String(body.error), 400, [], body);

    expect(billingActionMessage(refusal({ error: 'already_subscribed', message: 'Server words.' })))
      .toContain('Change it from Plan & billing');
    expect(billingActionMessage(refusal({ error: 'below_min_seats', minSeats: 3 })))
      .toBe('That plan needs at least 3 seats.');
    expect(billingActionMessage(refusal({ error: 'too_many_seats', maxSeats: 200, message: 'x' })))
      .toBe('Self-serve plans go up to 200 seats. Talk to us for more.');
    expect(billingActionMessage(refusal({ error: 'no_subscription', message: 'x' }))).toContain('Choose a plan');
    expect(billingActionMessage(refusal({ error: 'cancel_scheduled', message: 'x' }))).toContain('Keep my plan first');
    expect(billingActionMessage(refusal({ error: 'no_change', message: 'x' }))).toContain('already on that plan');
    // These two carry our own fixed sentence, which is shown as it came.
    expect(billingActionMessage(refusal({ error: 'billing_provider_refused', providerCode: 'p', message: 'Fixed text.' })))
      .toBe('Fixed text.');
    expect(billingActionMessage(refusal({ error: 'subscription_pending', message: 'A new plan is being confirmed.' })))
      .toBe('A new plan is being confirmed.');
    expect(billingActionMessage(refusal({ error: 'change_pending', message: 'Still confirming.' })))
      .toBe('Still confirming.');
    expect(billingActionMessage(refusal({ error: 'downgrade_scheduled', message: 'Keep first.' })))
      .toBe('Keep first.');
    // Without a sentence from the server, the code still gets the same explanation, never the raw code.
    expect(billingActionMessage(new ApiError('change_pending', 409)))
      .toBe('Your last change is still being confirmed. Refresh this page in a minute, then try again.');
    expect(billingActionMessage(new ApiError('downgrade_scheduled', 409)))
      .toBe('Keep your current plan first, then change it.');
  });

  it('passes through anything it does not recognise rather than inventing a message', () => {
    expect(billingActionMessage(new ApiError('Something specific went wrong', 500)))
      .toBe('Something specific went wrong');
    expect(billingActionMessage(new Error('offline'))).toBe('offline');
    expect(billingActionMessage(undefined)).toContain('Nothing was charged');
  });
});

describe('getWorkflowCostEstimate', () => {
  it('asks the workflow route B7 published the estimate on', async () => {
    await getWorkflowCostEstimate('p1', 'w2');
    expect(apiFetchMock.mock.calls[0][0]).toBe(workflowEstimatePath('p1', 'w2'));
    expect(workflowEstimatePath('p1', 'w2')).toBe('/api/v1/projects/p1/workflows/w2/estimate');
    expect(workflowEstimatePath('p1', 'w2', undefined, ['a', 'b'])).toBe('/api/v1/projects/p1/workflows/w2/estimate?videoFileIds=a&videoFileIds=b');
    expect(workflowEstimatePath('p1', 'w2', '2160p')).toBe('/api/v1/projects/p1/workflows/w2/estimate?outputResolution=2160p');
  });
});

describe('insufficientCredits', () => {
  /** The exact 402 body the execute preflight answers with (B7). */
  const refusal = (over: Record<string, unknown> = {}) =>
    new ApiError('This run needs about 40 credits…', 402, [], {
      code: 'INSUFFICIENT_CREDITS',
      message: 'This run needs about 40 credits and the workspace has 12 available.',
      required: 40,
      balance: 12,
      low: 30,
      high: 52,
      hasHistory: true,
      missing: 28,
      typical: 22,
      fixedMax: 6,
      ...over,
    });

  it('reads the shortfall out of the refusal body', () => {
    expect(insufficientCredits(refusal())).toEqual({
      required: 40,
      balance: 12,
      shortfall: 28,
      low: 30,
      high: 52,
      hasHistory: true,
      message: 'This run needs about 40 credits and the workspace has 12 available.',
      missing: 28,
      typical: 22,
      fixedMax: 6,
    });
  });

  it('carries the server missing, typical and fixedMax figures, and says null when the body has none', () => {
    expect(insufficientCredits(refusal({ missing: undefined, typical: null, fixedMax: 'x' }))).toMatchObject({
      missing: null,
      typical: null,
      fixedMax: null,
    });
  });

  it('carries the server message and says null when the body has none', () => {
    expect(insufficientCredits(refusal({ message: 42 }))?.message).toBeNull();
    expect(insufficientCredits(refusal({ message: '  ' }))?.message).toBeNull();
  });

  it('never reports a negative shortfall, even if the balance moved past the refusal', () => {
    expect(insufficientCredits(refusal({ balance: 50 }))?.shortfall).toBe(0);
  });

  it('is null for anything that is not this refusal', () => {
    // The other 402 on this platform is a plan limit ("bring your own providers"), whose remedy is
    // a different page entirely — keying on the status alone would send people to a top-up.
    expect(insufficientCredits(new ApiError('byo_not_in_plan', 402))).toBeNull();
    expect(insufficientCredits(refusal({ code: 'SOMETHING_ELSE' }))).toBeNull();
    expect(insufficientCredits(new ApiError('Nope', 403, [], { code: 'INSUFFICIENT_CREDITS' }))).toBeNull();
    expect(insufficientCredits(new Error('offline'))).toBeNull();
    expect(insufficientCredits(undefined)).toBeNull();
  });

  it('is null when the body lost the numbers, so the caller keeps the server sentence instead of inventing one', () => {
    expect(insufficientCredits(refusal({ required: undefined }))).toBeNull();
    expect(insufficientCredits(refusal({ balance: 'twelve' }))).toBeNull();
  });

  it('treats missing range ends and a missing hasHistory as unknown, not as zero', () => {
    const parsed = insufficientCredits(refusal({ low: null, high: undefined, hasHistory: undefined }));
    expect(parsed).toMatchObject({ low: null, high: null, hasHistory: false });
  });
});
