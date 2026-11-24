// @vitest-environment jsdom

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { initializePaddle, type Paddle } from '@paddle/paddle-js';
import { loadPaddle, openOverlayCheckout, requestPricePreview } from './sdk';

// The package's loader is what is stubbed: it injects a <script> from cdn.paddle.com, which no unit
// test can do. Everything this app decides around it stays real.
vi.mock('@paddle/paddle-js', () => ({ initializePaddle: vi.fn() }));

const initializeMock = vi.mocked(initializePaddle);

/** A stand-in for the loaded Paddle instance, recording what the page asks it to do. */
function fakePaddle() {
  const open = vi.fn();
  const preview = vi.fn();
  return { paddle: { Checkout: { open }, PricePreview: preview } as unknown as Paddle, open, preview };
}

beforeEach(() => {
  vi.clearAllMocks();
  initializeMock.mockReset();
});

describe('loadPaddle', () => {
  it('initializes once with the token and environment the API served', async () => {
    const { paddle } = fakePaddle();
    initializeMock.mockResolvedValue(paddle);

    await expect(loadPaddle({ environment: 'sandbox', clientToken: 'test_token_a', customerId: null })).resolves.toBe(paddle);

    expect(initializeMock).toHaveBeenCalledWith({ version: 'v1', environment: 'sandbox', token: 'test_token_a', eventCallback: expect.any(Function) });
  });

  it('drops the saved checkout baseline when the overlay closes without a payment, not after one', async () => {
    const { paddle } = fakePaddle();
    initializeMock.mockResolvedValue(paddle);
    await loadPaddle({ environment: 'sandbox', clientToken: 'test_token_cb', customerId: null });
    const callback = initializeMock.mock.calls.at(-1)![0]!.eventCallback as (e: { name: string }) => void;
    const key = 'reelbolt.checkout-snapshot';

    window.sessionStorage.setItem(key, '{}');
    callback({ name: 'checkout.closed' });
    expect(window.sessionStorage.getItem(key)).toBeNull();

    window.sessionStorage.setItem(key, '{}');
    callback({ name: 'checkout.completed' });
    callback({ name: 'checkout.closed' });
    expect(window.sessionStorage.getItem(key)).toBe('{}');
  });

  it('loads once per (token, environment) pair, so a re-render does not re-inject Paddle.js', async () => {
    const { paddle } = fakePaddle();
    initializeMock.mockResolvedValue(paddle);
    const config = { environment: 'production' as const, clientToken: 'test_token_b', customerId: null };

    await loadPaddle(config);
    await loadPaddle(config);

    expect(initializeMock).toHaveBeenCalledTimes(1);
  });

  it('resolves null when no token is configured, without touching Paddle.js at all', async () => {
    await expect(loadPaddle({ environment: 'sandbox', clientToken: '', customerId: null })).resolves.toBeNull();
    await expect(loadPaddle({ environment: 'sandbox', clientToken: null, customerId: null })).resolves.toBeNull();
    expect(initializeMock).not.toHaveBeenCalled();
  });

  it('resolves null when Paddle.js cannot be loaded, so the caller falls back to the hosted page', async () => {
    initializeMock.mockRejectedValue(new Error('Failed to load Paddle.js - v1'));

    await expect(loadPaddle({ environment: 'sandbox', clientToken: 'test_token_c', customerId: null })).resolves.toBeNull();
  });

  it('identifies the buyer with pwCustomer when the config carries a Paddle customer id', async () => {
    const { paddle } = fakePaddle();
    initializeMock.mockResolvedValue(paddle);

    await loadPaddle({ environment: 'production', clientToken: 'test_token_pw_a', customerId: 'ctm_01hbuyer' });

    expect(initializeMock).toHaveBeenCalledWith({
      version: 'v1',
      environment: 'production',
      token: 'test_token_pw_a',
      eventCallback: expect.any(Function),
      pwCustomer: { id: 'ctm_01hbuyer' },
    });
  });

  it('trims the customer id before handing it to Paddle.js', async () => {
    const { paddle } = fakePaddle();
    initializeMock.mockResolvedValue(paddle);

    await loadPaddle({ environment: 'production', clientToken: 'test_token_pw_trim', customerId: '  ctm_01hbuyer  ' });

    expect(initializeMock).toHaveBeenCalledWith(expect.objectContaining({ pwCustomer: { id: 'ctm_01hbuyer' } }));
  });

  it('omits pwCustomer entirely when the organization has no customer id, rather than sending it empty', async () => {
    const { paddle } = fakePaddle();
    initializeMock.mockResolvedValue(paddle);

    await loadPaddle({ environment: 'sandbox', clientToken: 'test_token_pw_b', customerId: null });

    expect(initializeMock).toHaveBeenCalledTimes(1);
    expect(initializeMock.mock.calls[0][0]).not.toHaveProperty('pwCustomer');
  });

  it('omits pwCustomer for a blank or whitespace-only customer id', async () => {
    const { paddle } = fakePaddle();
    initializeMock.mockResolvedValue(paddle);

    await loadPaddle({ environment: 'sandbox', clientToken: 'test_token_pw_c', customerId: '   ' });

    expect(initializeMock).toHaveBeenCalledTimes(1);
    expect(initializeMock.mock.calls[0][0]).not.toHaveProperty('pwCustomer');
  });

  it('does not reuse a Paddle instance bound to a different buyer when the customer id changes', async () => {
    const { paddle } = fakePaddle();
    initializeMock.mockResolvedValue(paddle);

    await loadPaddle({ environment: 'production', clientToken: 'test_token_pw_d', customerId: 'ctm_first' });
    await loadPaddle({ environment: 'production', clientToken: 'test_token_pw_d', customerId: 'ctm_second' });

    expect(initializeMock).toHaveBeenCalledTimes(2);
    expect(initializeMock).toHaveBeenLastCalledWith(expect.objectContaining({ pwCustomer: { id: 'ctm_second' } }));
  });
});

describe('requestPricePreview', () => {
  it("returns Paddle's formatted totals, keyed by price id", async () => {
    const { paddle, preview } = fakePaddle();
    preview.mockResolvedValue({
      data: { details: { lineItems: [{ price: { id: 'pri_creator' }, formattedTotals: { total: 'GBP 27.60' } }] } },
    });

    const totals = await requestPricePreview(paddle, [{ priceId: 'pri_creator', quantity: 1 }]);

    expect(preview).toHaveBeenCalledWith({ items: [{ priceId: 'pri_creator', quantity: 1 }] });
    expect(totals?.get('pri_creator')).toBe('GBP 27.60');
  });

  it('answers null - never a made-up price - when the call fails', async () => {
    const { paddle, preview } = fakePaddle();
    preview.mockRejectedValue(new Error('network'));

    await expect(requestPricePreview(paddle, [{ priceId: 'pri_creator', quantity: 1 }])).resolves.toBeNull();
  });

  it('does not call Paddle at all when there is nothing to price', async () => {
    const { paddle, preview } = fakePaddle();

    const totals = await requestPricePreview(paddle, []);

    expect(preview).not.toHaveBeenCalled();
    expect(totals?.size).toBe(0);
  });
});

describe('openOverlayCheckout', () => {
  it('opens the transaction the server created, as a one-page overlay, with no browser-supplied data', () => {
    const { paddle, open } = fakePaddle();

    openOverlayCheckout({
      paddle,
      transactionId: 'txn_01H',
      successUrl: 'https://app.reelbolt.ai/app/checkout',
    });

    expect(open).toHaveBeenCalledTimes(1);
    // Exactly this, and nothing else. In particular no customData: the organization a payment is
    // attributed to lives in custom_data.organization_id, which the server attaches to the
    // transaction it created from the caller's own session. A value sent from here would be
    // tamperable, so this client never sends one.
    expect(open).toHaveBeenCalledWith({
      transactionId: 'txn_01H',
      settings: {
        displayMode: 'overlay',
        variant: 'one-page',
        successUrl: 'https://app.reelbolt.ai/app/checkout',
      },
    });
    expect(open.mock.calls[0][0]).not.toHaveProperty('customData');
    expect(open.mock.calls[0][0]).not.toHaveProperty('items');
  });

  it('prefills the signed-in address, which is a top-level customer field in this package\'s types', () => {
    const { paddle, open } = fakePaddle();

    openOverlayCheckout({
      paddle,
      transactionId: 'txn_02',
      successUrl: 'https://app.reelbolt.ai/app/checkout',
      email: '  buyer@example.com  ',
    });

    expect(open).toHaveBeenCalledWith(expect.objectContaining({
      transactionId: 'txn_02',
      customer: { email: 'buyer@example.com' },
    }));
  });

  it('omits the customer entirely when there is no address, rather than sending it empty', () => {
    const { paddle, open } = fakePaddle();

    openOverlayCheckout({ paddle, transactionId: 'txn_03', successUrl: 'https://x/app/checkout', email: '   ' });

    expect(open.mock.calls[0][0]).not.toHaveProperty('customer');
  });

  it('locks the discount in place when the checkout carries one', () => {
    const { paddle, open } = fakePaddle();

    openOverlayCheckout({ paddle, transactionId: 'txn_04', successUrl: 'https://x/app/checkout', discounted: true });

    expect(open).toHaveBeenCalledWith({
      transactionId: 'txn_04',
      settings: {
        displayMode: 'overlay',
        variant: 'one-page',
        successUrl: 'https://x/app/checkout',
        allowDiscountRemoval: false,
        showAddDiscounts: false,
      },
    });
  });

  it('leaves the discount settings out when there is no discount', () => {
    const { paddle, open } = fakePaddle();

    openOverlayCheckout({ paddle, transactionId: 'txn_05', successUrl: 'https://x/app/checkout', discounted: false });

    const settings = open.mock.calls[0][0].settings;
    expect(settings).not.toHaveProperty('allowDiscountRemoval');
    expect(settings).not.toHaveProperty('showAddDiscounts');
  });
});
