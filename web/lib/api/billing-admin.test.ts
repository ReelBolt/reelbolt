import { beforeEach, describe, expect, it, vi } from 'vitest';

// `apiFetch` is stubbed so the assertions are on the URLs and bodies the client builds, not on
// fetch semantics (client.test.ts already covers those). There is no MSW in this repo.
vi.mock('./client', async () => {
  const actual = await vi.importActual<typeof import('./client')>('./client');
  return { ...actual, apiFetch: vi.fn() };
});

import { apiFetch } from './client';
import { getBillingAdminOverview } from './billing-admin';
import { createRateMultiplier, updatePricingSettings } from './pricing';

const apiFetchMock = vi.mocked(apiFetch);

function calledUrl(call = 0): string {
  return apiFetchMock.mock.calls[call][0] as string;
}

beforeEach(() => {
  apiFetchMock.mockReset();
  apiFetchMock.mockResolvedValue({} as never);
});

describe('getBillingAdminOverview URL construction', () => {
  it('sends no querystring when no params are supplied, so the server default window applies', async () => {
    await getBillingAdminOverview();

    expect(calledUrl()).toBe('/api/v1/billing/admin/overview');
  });

  it('appends both bounds in the order from, to', async () => {
    await getBillingAdminOverview({ from: '2026-01-01T00:00:00.000Z', to: '2026-01-31T00:00:00.000Z' });

    expect(calledUrl()).toBe(
      '/api/v1/billing/admin/overview?from=2026-01-01T00%3A00%3A00.000Z&to=2026-01-31T00%3A00%3A00.000Z',
    );
  });

  it('drops an empty-string bound rather than sending a meaningless one', async () => {
    await getBillingAdminOverview({ from: '', to: '2026-01-31T00:00:00.000Z' });

    expect(calledUrl()).toBe('/api/v1/billing/admin/overview?to=2026-01-31T00%3A00%3A00.000Z');
  });

  it('is not under /api/v1/admin, which nginx routes to the Go API', async () => {
    await getBillingAdminOverview();

    expect(calledUrl().startsWith('/api/v1/admin/')).toBe(false);
  });
});

// Rate cards were removed with the model catalog: prices now live on each model capability (see
// inference-providers.test.ts). The multiplier write moved under /api/v1/pricing.
describe('pricing writes', () => {
  it('PUTs the markup to /api/v1/pricing/settings', async () => {
    await updatePricingSettings({ markup: 1.35 });

    expect(calledUrl()).toBe('/api/v1/pricing/settings');
    const init = apiFetchMock.mock.calls[0][1] as RequestInit;
    expect(init.method).toBe('PUT');
    expect(JSON.parse(String(init.body))).toEqual({ markup: 1.35 });
  });

  it('POSTs a multiplier to /api/v1/pricing/multipliers, with a null usage kind for the mode default', async () => {
    await createRateMultiplier({ billingMode: 'Byo', usageKind: null, multiplier: 0, effectiveFrom: null });

    expect(calledUrl()).toBe('/api/v1/pricing/multipliers');
    const init = apiFetchMock.mock.calls[0][1] as RequestInit;
    expect(init.method).toBe('POST');
    expect(JSON.parse(String(init.body))).toEqual({
      billingMode: 'Byo',
      usageKind: null,
      multiplier: 0,
      effectiveFrom: null,
    });
  });
});
