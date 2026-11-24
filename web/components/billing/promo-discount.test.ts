import { describe, it, expect } from 'vitest';
import type { PromoDiscount } from '@/lib/types/billing';
import { promoEndsSentence, promoEndsWith } from './promo-discount';

const promo = (over: Partial<PromoDiscount> = {}): PromoDiscount => ({
  discountType: 'Percentage',
  amount: '20',
  currencyCode: null,
  targetPriceIds: ['pri_creator', 'pri_pro'],
  ...over,
});

describe('promoEndsWith', () => {
  it('keeps the promo on a targeted price and ends it on any other', () => {
    expect(promoEndsWith({ priceId: 'pri_pro' }, promo())).toBe(false);
    expect(promoEndsWith({ priceId: 'pri_team' }, promo())).toBe(true);
  });

  it('ends nothing when there is no promo', () => {
    expect(promoEndsWith({ priceId: 'pri_team' }, null)).toBe(false);
    expect(promoEndsWith({ priceId: 'pri_team' }, undefined)).toBe(false);
  });
});

describe('promoEndsSentence', () => {
  it('prints a flat amount in its own currency from minor units', () => {
    expect(promoEndsSentence(promo({ discountType: 'Flat', amount: '500', currencyCode: 'EUR' }), false, 'Creator'))
      .toBe('Your €5.00 promo ends with this change.');
    expect(promoEndsSentence(promo({ discountType: 'Flat', amount: '500', currencyCode: 'JPY' }), false, 'Creator'))
      .toBe('Your ¥500 promo ends with this change.');
  });

  it('still warns when the amount cannot be printed', () => {
    expect(promoEndsSentence(promo({ discountType: 'Flat', amount: 'x', currencyCode: 'USD' }), true, 'Creator'))
      .toBe('Your promo ends with this change. Keeping Creator later does not bring it back.');
  });
});
