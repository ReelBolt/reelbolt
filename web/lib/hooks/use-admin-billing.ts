'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import { getBillingAdminOverviewUrl } from '../api/billing-admin';
import {
  getPriceCoverage,
  getPricingSettings,
  listRateMultipliers,
  PRICE_COVERAGE_PATH,
  PRICING_SETTINGS_PATH,
  RATE_MULTIPLIERS_PATH,
} from '../api/pricing';
import type { BillingAdminOverview, BillingAdminOverviewParams } from '../types/billing-admin';

/**
 * The reads the admin billing view needs.
 *
 * Each takes `enabled` so a non-admin page render issues no request at all: the API answers 403,
 * and firing a request whose answer is known to be a refusal only produces a console error. A null
 * SWR key is the documented way to pause a subscription.
 *
 * The overview's SWR key is the fully-built URL, so changing the window produces a different key and
 * re-queries rather than serving the previous window's cached figures.
 */

/** The global markup and the credit value it is divided by. */
export function usePricingSettings(enabled = true) {
  return useSWR(enabled ? PRICING_SETTINGS_PATH : null, () => getPricingSettings());
}

export function useRateMultipliers(enabled = true) {
  return useSWR(enabled ? RATE_MULTIPLIERS_PATH : null, () => listRateMultipliers());
}

/**
 * The forward-looking coverage report: which enabled platform capabilities can be metered in a unit
 * that has no open price. Computed on the server against every price row.
 */
export function usePriceCoverage(enabled = true) {
  return useSWR(enabled ? PRICE_COVERAGE_PATH : null, () => getPriceCoverage());
}

export function useBillingAdminOverview(params: BillingAdminOverviewParams = {}, enabled = true) {
  return useSWR<BillingAdminOverview>(
    enabled ? getBillingAdminOverviewUrl(params) : null,
    fetcher,
  );
}
