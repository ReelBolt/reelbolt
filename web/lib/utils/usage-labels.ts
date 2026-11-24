// Plain-language labels for the metering vocabulary (WP B10b-UI). The backend sends the raw
// `UsageKind`/the raw `UsageBillingMode` member names — the database stores them by name so
// they can never be renamed — and this is where they become something a person can read.
//
// An unknown member is passed through unchanged rather than dropped: the enums are append-only,
// so a value this build has never heard of is a newer backend, not a bug.

import type { T } from '@/i18n/translator';
import { enT } from '@/lib/i18n-default';

/**
 * The group axis' own label for a raw kind, e.g. "LlmInputTokens" -> "Input tokens". Pass the
 * component's `useT()` for the active language; unknown kinds are passed through.
 */
export function usageKindLabel(kind: string, t: T = enT): string {
  const key = `libLabels.usageKinds.${kind}`;
  return t.has(key) ? t(key) : kind;
}

// Who the provider bill lands on. "Platform" means ReelBolt's own provider account paid for it;
// "Byo" means the organization's own key did; "Runner" means the work ran on the organization's
// own machine, so only the platform's coordination cost was charged.

export const USAGE_MODE_ORDER = ['Platform', 'Byo', 'Runner'] as const;

export function usageModeLabel(mode: string, t: T = enT): string {
  const key = `libLabels.usageModes.${mode}`;
  return t.has(key) ? t(key) : mode;
}

/** Locale for number output: the UI language (`useFormat().locale`), English as the default. */
function intlLocale(locale: string | undefined): string | undefined {
  return locale === 'en-XA' ? 'en' : locale;
}

/** A compact count: 1,234 stays exact, 1.2M does not pretend to be exact. */
export function formatUnits(quantity: number, locale?: string): string {
  if (!Number.isFinite(quantity)) return '—';
  const abs = Math.abs(quantity);
  const loc = intlLocale(locale);
  const one = (value: number, suffix: string) =>
    `${value.toLocaleString(loc, { minimumFractionDigits: 1, maximumFractionDigits: 1, useGrouping: false })}${suffix}`;
  if (abs >= 1_000_000_000) return one(quantity / 1_000_000_000, 'B');
  if (abs >= 1_000_000) return one(quantity / 1_000_000, 'M');
  if (abs >= 10_000) return one(quantity / 1_000, 'k');
  return quantity.toLocaleString(loc, { maximumFractionDigits: 2 });
}

/** Credits: the ledger stores millicredits (D8), the API reports whole credits. */
export function formatCredits(credits: number, locale?: string): string {
  if (!Number.isFinite(credits)) return '—';
  const abs = Math.abs(credits);
  const digits = abs > 0 && abs < 1 ? 3 : abs < 100 ? 2 : 0;
  return credits.toLocaleString(intlLocale(locale), { minimumFractionDigits: digits, maximumFractionDigits: digits });
}
