import { apiFetch } from './client';
import type { InferenceProviderCapability, InferenceProviderKind } from '../types/inference-provider';
import type {
  CreateRateMultiplierRequest,
  PriceBookEntry,
  PriceCoverageGap,
  PricingSettings,
  RateMultiplier,
  UpdatePricingSettingsRequest,
} from '../types/pricing';

/**
 * Platform pricing: the global markup, the billing-mode multipliers, the coverage report and the
 * price book. Every endpoint is platform-admin only (403 otherwise). Relative paths: the JWT is an
 * httpOnly cookie that nginx turns into an Authorization header.
 */
export const PRICING_SETTINGS_PATH = '/api/v1/pricing/settings';
export const PRICE_COVERAGE_PATH = '/api/v1/pricing/coverage';
export const RATE_MULTIPLIERS_PATH = '/api/v1/pricing/multipliers';
export const PRICE_BOOK_PATH = '/api/v1/pricing/price-book';

export function getPricingSettings(): Promise<PricingSettings> {
  return apiFetch<PricingSettings>(PRICING_SETTINGS_PATH);
}

/** Changes the markup applied to every price that does not set its own credits. */
export function updatePricingSettings(body: UpdatePricingSettingsRequest): Promise<PricingSettings> {
  return apiFetch<PricingSettings>(PRICING_SETTINGS_PATH, { method: 'PUT', body: JSON.stringify(body) });
}

/** Every enabled platform capability that can be metered in a unit it has no open price for. */
export function getPriceCoverage(): Promise<PriceCoverageGap[]> {
  return apiFetch<PriceCoverageGap[]>(PRICE_COVERAGE_PATH);
}

export function listRateMultipliers(): Promise<RateMultiplier[]> {
  return apiFetch<RateMultiplier[]>(RATE_MULTIPLIERS_PATH);
}

/** Writes a new multiplier row, closing the open one it supersedes. */
export function createRateMultiplier(body: CreateRateMultiplierRequest): Promise<RateMultiplier> {
  return apiFetch<RateMultiplier>(RATE_MULTIPLIERS_PATH, { method: 'POST', body: JSON.stringify(body) });
}

/** The price-book URL for one model capability, so the SWR key and the request never drift apart. */
export function priceBookUrl(
  kind: InferenceProviderKind,
  capability: InferenceProviderCapability,
  model: string,
): string {
  const query = new URLSearchParams({ kind, capability, model });
  return `${PRICE_BOOK_PATH}?${query.toString()}`;
}

/** Suggested prices for a model capability, used only to pre-fill the add-price form. */
export function getPriceBook(
  kind: InferenceProviderKind,
  capability: InferenceProviderCapability,
  model: string,
): Promise<PriceBookEntry[]> {
  return apiFetch<PriceBookEntry[]>(priceBookUrl(kind, capability, model));
}
