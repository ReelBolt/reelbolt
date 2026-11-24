import { describe, expect, it } from 'vitest';
import {
  creditsText,
  estimateLine,
  isUnaffordable,
  noProviderText,
  parseChargedHeader,
  unaffordableText,
  usedLine,
} from './credit-estimate';

describe('credit estimate wording', () => {
  it('says about N credits only when billing is on', () => {
    expect(estimateLine({ billingEnabled: true, credits: 4.5, affordable: true })).toBe('about 4.5 credits');
    expect(estimateLine({ billingEnabled: true, credits: 1, affordable: true })).toBe('about 1 credit');
    expect(estimateLine({ billingEnabled: false, credits: 0, affordable: true })).toBeNull();
    expect(estimateLine(undefined)).toBeNull();
  });
  it('trims long decimals', () => {
    expect(creditsText(2.3333)).toBe('2.33 credits');
  });
  it('explains an unaffordable run', () => {
    const e = { billingEnabled: true, credits: 4.5, balanceCredits: 1, affordable: false };
    expect(isUnaffordable(e)).toBe(true);
    expect(unaffordableText(e)).toContain('Add credits');
    expect(unaffordableText({ ...e, affordable: true })).toBeNull();
  });
  it('reports what a run used', () => {
    expect(usedLine(4.5)).toBe('Used 4.5 credits');
    expect(usedLine(0)).toBeNull();
    expect(parseChargedHeader('4.5')).toBe(4.5);
    expect(parseChargedHeader(null)).toBeNull();
  });
  it('is role aware when no provider exists', () => {
    expect(noProviderText(false)).toBe('Ask your workspace admin to set up picture tools.');
    expect(noProviderText(true)).not.toContain('Ask your workspace admin');
  });
});
