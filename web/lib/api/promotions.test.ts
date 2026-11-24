import { describe, it, expect, vi, beforeEach } from 'vitest';

vi.mock('./client', async () => {
  const actual = await vi.importActual<typeof import('./client')>('./client');
  return { ...actual, apiFetch: vi.fn() };
});

import { ApiError, apiFetch } from './client';
import {
  ACTIVE_PROMOTIONS_PATH,
  ADMIN_PROMOTIONS_PATH,
  CHECKOUT_PREVIEW_PATH,
  REDEEM_PROMOTION_PATH,
  adminOrgSearchPath,
  getActivePromotions,
  grantOrgCredits,
  listOrgCreditGrants,
  normalizePromoCode,
  orgCreditGrantsPath,
  previewCheckout,
  promoCodeMessage,
  promotionAdminMessage,
  promotionCoversPack,
  promotionCoversPlan,
  redeemPromoCode,
  updatePromotion,
} from './promotions';

const apiFetchMock = vi.mocked(apiFetch);

beforeEach(() => {
  apiFetchMock.mockReset();
  apiFetchMock.mockResolvedValue({ items: [] });
});

/** A refusal shaped the way the billing endpoints send them: `{ error, message? }`. */
function refusal(status: number, error: string, message?: string): ApiError {
  const body = message ? { error, message } : { error };
  return new ApiError(message ?? error, status, [], body);
}

describe('promoCodeMessage', () => {
  it('maps promo_code_invalid to a plain sentence', () => {
    expect(promoCodeMessage(refusal(400, 'promo_code_invalid'))).toBe("That code isn't valid.");
  });

  it('maps the code even when the server also sent a message of its own', () => {
    expect(promoCodeMessage(refusal(400, 'promo_code_invalid', 'Invalid promotion code.'))).toBe(
      "That code isn't valid.",
    );
  });

  it('maps any 429 to "try again later"', () => {
    expect(promoCodeMessage(refusal(429, 'promo_rate_limited'))).toBe('Too many attempts. Try again later.');
    expect(promoCodeMessage(new ApiError('Too Many Requests', 429))).toBe('Too many attempts. Try again later.');
  });

  it('maps an already-used code and a fully-claimed code', () => {
    expect(promoCodeMessage(refusal(409, 'promo_already_redeemed'))).toBe(
      'This workspace has already used that code.',
    );
  });

  it('falls back to the billing messages for anything else', () => {
    expect(promoCodeMessage(refusal(503, 'billing_not_configured'))).toMatch(/No payment provider/);
    expect(promoCodeMessage(new Error('offline'))).toBe('offline');
  });

  it('never names an internal system in customer copy', () => {
    for (const code of ['promo_code_invalid', 'promo_already_redeemed', 'billing_provider_error']) {
      expect(promoCodeMessage(refusal(400, code))).not.toMatch(/paddle|ledger|millis/i);
    }
  });
});

describe('promotionAdminMessage', () => {
  it('explains the admin refusals', () => {
    expect(promotionAdminMessage(refusal(409, 'promo_code_taken'))).toMatch(/already uses that code/);
    expect(promotionAdminMessage(refusal(409, 'automatic_promotion_overlap'))).toMatch(/overlapping/);
    expect(promotionAdminMessage(refusal(502, 'billing_provider_error'))).toMatch(/payment provider rejected/);
  });

  it('shows the validation errors of a 400', () => {
    const error = new ApiError('One or more validation errors occurred.', 400, ['Reason is required.']);
    expect(promotionAdminMessage(error)).toBe('Reason is required.');
  });
});

describe('requests', () => {
  it('normalizes a redeemed code before sending it', async () => {
    apiFetchMock.mockResolvedValue({ credits: 500, expiresAt: '2027-01-01T00:00:00Z', headline: null });
    await redeemPromoCode('  welcome 50 ');
    expect(apiFetchMock.mock.calls[0]).toEqual([
      REDEEM_PROMOTION_PATH,
      { method: 'POST', body: JSON.stringify({ code: 'WELCOME50' }) },
    ]);
    expect(normalizePromoCode('abc d')).toBe('ABCD');
  });

  it('posts the preview body as given', async () => {
    await previewCheckout({ plan: 'creator', promoCode: 'SPRING20' });
    expect(apiFetchMock.mock.calls[0]).toEqual([
      CHECKOUT_PREVIEW_PATH,
      { method: 'POST', body: JSON.stringify({ plan: 'creator', promoCode: 'SPRING20' }) },
    ]);
  });

  it('patches only the editable fields', async () => {
    await updatePromotion('p1', { status: 'paused' });
    expect(apiFetchMock.mock.calls[0]).toEqual([
      `${ADMIN_PROMOTIONS_PATH}/p1`,
      { method: 'PATCH', body: JSON.stringify({ status: 'paused' }) },
    ]);
  });

  it('posts a credit grant with its request id', async () => {
    const body = { requestId: 'r-1', credits: 100, expiresAt: '2027-01-01T00:00:00.000Z', reason: 'Goodwill' };
    await grantOrgCredits('org 1', body);
    expect(apiFetchMock.mock.calls[0]).toEqual([
      orgCreditGrantsPath('org 1'),
      { method: 'POST', body: JSON.stringify(body) },
    ]);
    expect(orgCreditGrantsPath('org 1')).toBe('/api/v1/billing/admin/orgs/org%201/credit-grants');
  });

  it('reads a grants list from either { items } or a bare array', async () => {
    apiFetchMock.mockResolvedValueOnce({ items: [{ id: 'g1' }] });
    expect(await listOrgCreditGrants('o')).toEqual([{ id: 'g1' }]);
    apiFetchMock.mockResolvedValueOnce([{ id: 'g2' }]);
    expect(await listOrgCreditGrants('o')).toEqual([{ id: 'g2' }]);
  });

  it('builds the org search URL', () => {
    expect(adminOrgSearchPath(' acme co ')).toBe('/api/v1/billing/admin/orgs?search=acme%20co');
    expect(adminOrgSearchPath('')).toBe('/api/v1/billing/admin/orgs');
  });

  it('treats a 404 from the active promotions as "none running"', async () => {
    apiFetchMock.mockRejectedValueOnce(new ApiError('Not Found', 404));
    expect(await getActivePromotions()).toEqual([]);
    expect(apiFetchMock.mock.calls[0][0]).toBe(ACTIVE_PROMOTIONS_PATH);
  });
});

describe('promotion targets', () => {
  it('an empty target list covers every plan or pack', () => {
    expect(promotionCoversPlan({ kind: 'planDiscount', headline: null, endsAt: null }, 'creator')).toBe(true);
    expect(promotionCoversPack({ kind: 'packBonus', headline: null, endsAt: null, packCredits: [] }, 500)).toBe(true);
  });

  it('a listed target covers only what it lists, and kinds do not cross', () => {
    const plan = { kind: 'planDiscount', headline: null, endsAt: null, planKeys: ['team'] };
    expect(promotionCoversPlan(plan, 'team')).toBe(true);
    expect(promotionCoversPlan(plan, 'creator')).toBe(false);
    expect(promotionCoversPack(plan, 500)).toBe(false);
    const pack = { kind: 'packDiscount', headline: null, endsAt: null, packCredits: [1000] };
    expect(promotionCoversPack(pack, 1000)).toBe(true);
    expect(promotionCoversPack(pack, 500)).toBe(false);
  });
});
