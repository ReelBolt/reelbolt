// @vitest-environment jsdom

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { usePaddlePrices } from './use-paddle-prices';
import { loadPaddle, requestPricePreview } from '../paddle/sdk';
import type { BillingPlan, PaddleClientConfig } from '../types/billing';
import type { Paddle } from '@paddle/paddle-js';

// The two calls that leave the browser are stubbed; everything the hook decides around them (when
// to ask, and what to show when there is no answer) stays real.
vi.mock('../paddle/sdk', () => ({ loadPaddle: vi.fn(), requestPricePreview: vi.fn() }));

const loadMock = vi.mocked(loadPaddle);
const previewMock = vi.mocked(requestPricePreview);

const CONFIG: PaddleClientConfig = { environment: 'sandbox', clientToken: 'test_token', customerId: null };
const PADDLE = {} as unknown as Paddle;

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

beforeEach(() => {
  vi.clearAllMocks();
  loadMock.mockResolvedValue(PADDLE);
  previewMock.mockResolvedValue(new Map([['pri_creator', 'EUR 31.90']]));
});

describe('usePaddlePrices', () => {
  it("reports Paddle's own price for each plan on sale", async () => {
    const { result } = renderHook(() => usePaddlePrices(CONFIG, [plan()]));

    await waitFor(() => expect(result.current.pending).toBe(false));
    expect(result.current.totals.get('pri_creator')).toBe('EUR 31.90');
    expect(previewMock).toHaveBeenCalledWith(PADDLE, [{ priceId: 'pri_creator', quantity: 1 }]);
  });

  it('asks for nothing when the deployment has no embedded checkout', async () => {
    const { result } = renderHook(() => usePaddlePrices(null, [plan()]));

    await waitFor(() => expect(result.current.pending).toBe(false));
    expect(result.current.totals.size).toBe(0);
    expect(loadMock).not.toHaveBeenCalled();
    expect(previewMock).not.toHaveBeenCalled();
  });

  it('leaves the totals empty when Paddle.js could not be loaded', async () => {
    loadMock.mockResolvedValue(null);

    const { result } = renderHook(() => usePaddlePrices(CONFIG, [plan()]));

    await waitFor(() => expect(result.current.pending).toBe(false));
    expect(result.current.totals.size).toBe(0);
    expect(previewMock).not.toHaveBeenCalled();
  });

  it('leaves the totals empty when the preview itself fails, rather than showing a number', async () => {
    previewMock.mockResolvedValue(null);

    const { result } = renderHook(() => usePaddlePrices(CONFIG, [plan()]));

    await waitFor(() => expect(result.current.pending).toBe(false));
    expect(result.current.totals.size).toBe(0);
  });

  it('asks once, not once per render', async () => {
    const plans = [plan()];
    const { result, rerender } = renderHook(() => usePaddlePrices(CONFIG, plans));

    await waitFor(() => expect(result.current.pending).toBe(false));
    rerender();
    rerender();

    expect(loadMock).toHaveBeenCalledTimes(1);
    expect(previewMock).toHaveBeenCalledTimes(1);
  });
});
