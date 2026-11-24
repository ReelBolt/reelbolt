// @vitest-environment jsdom
/* eslint @typescript-eslint/no-unused-vars: ["warn", { "argsIgnorePattern": "^_" }] */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.stubGlobal(
  'ResizeObserver',
  class ResizeObserver {
    constructor(_callback: ResizeObserverCallback) {}
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);
Element.prototype.scrollIntoView = vi.fn();

vi.mock('@/lib/api/promotions', async (orig) => ({
  ...(await orig<typeof import('@/lib/api/promotions')>()),
  createPromotion: vi.fn(),
  fetchFounderPricePreset: vi.fn(),
}));

import { createPromotion, fetchFounderPricePreset, FOUNDER_PRICE_FALLBACK } from '@/lib/api/promotions';
import {
  CreatePromotionModal,
  buildCreatePromotionRequest,
  emptyPromotionForm,
  isValidPercentage,
  validatePromotionForm,
} from './CreatePromotionModal';

const createMock = vi.mocked(createPromotion);

function renderModal() {
  const onCreated = vi.fn();
  const onClose = vi.fn();
  render(
    <MantineProvider>
      <CreatePromotionModal
        opened
        onClose={onClose}
        onCreated={onCreated}
        planOptions={[{ value: 'creator', label: 'Creator' }]}
        packOptions={[500, 1000]}
      />
    </MantineProvider>,
  );
  return { onCreated, onClose };
}

function chooseKind(label: string) {
  fireEvent.click(screen.getByLabelText(label));
}

describe('CreatePromotionModal', () => {
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

  it('shows the credit fields for a credit code, and no discount fields', () => {
    renderModal();
    expect(screen.getByLabelText(/^Credits\s*\*?$/)).toBeTruthy();
    expect(screen.getByLabelText(/Credits expire after/)).toBeTruthy();
    expect(screen.queryByLabelText(/Percent off/)).toBeNull();
    expect(screen.queryByLabelText(/Bonus/)).toBeNull();
  });

  it('shows amount, duration and plans for a plan discount', () => {
    renderModal();
    chooseKind('Plan discount');
    expect(screen.getByLabelText(/Percent off/)).toBeTruthy();
    expect(screen.getByLabelText('Discount duration')).toBeTruthy();
    expect(screen.getByText('Plans')).toBeTruthy();
    expect(screen.queryByText('Credit packs')).toBeNull();
    expect(screen.queryByLabelText(/Credits expire after/)).toBeNull();

    fireEvent.click(screen.getByLabelText('Several months'));
    expect(screen.getByLabelText(/^Months/)).toBeTruthy();
  });

  it('shows amount and packs, but no duration, for a pack discount', () => {
    renderModal();
    chooseKind('Pack discount');
    expect(screen.getByLabelText(/Percent off/)).toBeTruthy();
    expect(screen.getByText('Credit packs')).toBeTruthy();
    expect(screen.queryByLabelText('Discount duration')).toBeNull();
  });

  it('shows the bonus percent and packs for a pack bonus', () => {
    renderModal();
    chooseKind('Pack bonus');
    expect(screen.getByLabelText(/Bonus/)).toBeTruthy();
    expect(screen.getByText('Credit packs')).toBeTruthy();
    expect(screen.queryByLabelText(/Percent off/)).toBeNull();
  });

  it('refuses a credit code with no code, and sends nothing', async () => {
    renderModal();
    fireEvent.change(screen.getByLabelText(/^Name/), { target: { value: 'Welcome' } });
    fireEvent.change(screen.getByLabelText(/^Credits\s*\*?$/), { target: { value: '500' } });
    fireEvent.click(screen.getByTestId('create-promotion-submit'));
    expect(await screen.findByText('A credit code needs a code.')).toBeTruthy();
    expect(createMock).not.toHaveBeenCalled();
  });

  it('refuses a percentage outside 0.01 to 100', async () => {
    renderModal();
    chooseKind('Plan discount');
    fireEvent.change(screen.getByLabelText(/^Name/), { target: { value: 'Spring' } });
    fireEvent.change(screen.getByLabelText(/Percent off/), { target: { value: '150' } });
    fireEvent.click(screen.getByTestId('create-promotion-submit'));
    expect(await screen.findByText('A percentage between 0.01 and 100.')).toBeTruthy();
    expect(createMock).not.toHaveBeenCalled();
  });

  it('creates a valid credit code with the API shape', async () => {
    createMock.mockResolvedValue({ id: 'p1', name: 'Welcome' } as Awaited<ReturnType<typeof createPromotion>>);
    const { onCreated } = renderModal();
    fireEvent.change(screen.getByLabelText(/^Name/), { target: { value: 'Welcome' } });
    fireEvent.change(screen.getByLabelText(/^Code/), { target: { value: 'welcome50' } });
    fireEvent.change(screen.getByLabelText(/^Credits\s*\*?$/), { target: { value: '500' } });
    fireEvent.click(screen.getByTestId('create-promotion-submit'));
    await waitFor(() => expect(createMock).toHaveBeenCalledTimes(1));
    expect(createMock).toHaveBeenCalledWith({
      kind: 'creditCode',
      name: 'Welcome',
      code: 'WELCOME50',
      maxRedemptionsPerOrg: 1,
      credits: 500,
      creditExpiryDays: 90,
    });
    await waitFor(() => expect(onCreated).toHaveBeenCalled());
  });
});

describe('validatePromotionForm', () => {
  it('bounds a percentage at 0.01 and 100', () => {
    expect(isValidPercentage('0.01')).toBe(true);
    expect(isValidPercentage('100')).toBe(true);
    expect(isValidPercentage('20.5')).toBe(true);
    expect(isValidPercentage('0')).toBe(false);
    expect(isValidPercentage('0.001')).toBe(false);
    expect(isValidPercentage('100.01')).toBe(false);
    expect(isValidPercentage('-5')).toBe(false);
    expect(isValidPercentage('abc')).toBe(false);
  });

  it('requires a code only for a credit code', () => {
    const credit = { ...emptyPromotionForm('creditCode'), name: 'x', credits: 10 as const };
    expect(validatePromotionForm(credit).code).toBe('A credit code needs a code.');
    const bonus = { ...emptyPromotionForm('packBonus'), name: 'x', bonusPercent: 10 as const };
    expect(validatePromotionForm(bonus)).toEqual({});
  });

  it('rejects a malformed code', () => {
    const values = { ...emptyPromotionForm('creditCode'), name: 'x', credits: 10 as const, code: 'AB' };
    expect(validatePromotionForm(values).code).toMatch(/4 to 32/);
  });

  it('needs months for a months-long plan discount, and whole cents for a flat one', () => {
    const months = {
      ...emptyPromotionForm('planDiscount'), name: 'x', discountAmount: '10', duration: 'months' as const,
    };
    expect(validatePromotionForm(months).durationMonths).toBeDefined();
    const flat = { ...emptyPromotionForm('packDiscount'), name: 'x', discountType: 'flat' as const, discountAmount: '4.50' };
    expect(validatePromotionForm(flat).discountAmount).toBe('Whole US cents, at least 1.');
  });

  it('sends only the fields of the chosen kind, with USD for a flat amount', () => {
    const req = buildCreatePromotionRequest({
      ...emptyPromotionForm('planDiscount'),
      name: 'Spring',
      discountType: 'flat',
      discountAmount: '500',
      duration: 'months',
      durationMonths: 3,
      planKeys: ['creator'],
      credits: 99,
    });
    expect(req).toEqual({
      kind: 'planDiscount',
      name: 'Spring',
      maxRedemptionsPerOrg: 1,
      discountType: 'flat',
      discountAmount: '500',
      currencyCode: 'USD',
      duration: 'months',
      durationMonths: 3,
      planKeys: ['creator'],
    });
  });

  it('prefills the form from the Founder price preset', async () => {
    vi.mocked(fetchFounderPricePreset).mockResolvedValue(FOUNDER_PRICE_FALLBACK);
    renderModal();
    fireEvent.click(screen.getByTestId('founder-price-preset'));
    await waitFor(() => expect((screen.getByLabelText(/^Name/) as HTMLInputElement).value).toBe('Founder price'));
    expect((screen.getByLabelText(/Percent off/) as HTMLInputElement).value).toBe('50');
  });
});
