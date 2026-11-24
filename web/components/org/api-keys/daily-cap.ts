import { ApiError } from '@/lib/api/client';

/** The daily limit a key gets unless someone changes it on purpose (mirrors the server default). */
export const DEFAULT_DAILY_CAP_CREDITS = 200;

/** True when the server refused the caps because the raise or removal was not confirmed. */
export function isCapConfirmationRequired(err: unknown): boolean {
  return err instanceof ApiError && err.code === 'cap_confirmation_required';
}

/** True when the workspace plan can read through the API but not run apps or upload. */
export function isReadOnlyPlan(err: unknown): boolean {
  return err instanceof ApiError && err.code === 'API_PLAN_READ_ONLY';
}

/**
 * Whether a daily-limit choice needs an explicit "I understand" step: removing the limit, or
 * setting one above the default. Keeping, lowering or staying at or below max(default, current cap) never asks.
 */
export function dailyCapNeedsConfirmation(
  choice: { noDailyCap: boolean; daily: number | ''; current?: number | null; currentWaived?: boolean },
  defaultCredits: number = DEFAULT_DAILY_CAP_CREDITS,
): boolean {
  if (choice.noDailyCap) return !choice.currentWaived;
  if (choice.daily === '') return false;
  return choice.daily > Math.max(defaultCredits, choice.current ?? 0);
}

/** Plain-language message for the refusals the key screens know about; null for anything else. */
export function mapApiKeyError(err: unknown, t: (key: string) => string): string | null {
  if (isCapConfirmationRequired(err)) return t('capGuard.confirmRequired');
  if (isReadOnlyPlan(err)) return t('capGuard.readOnlyPlan');
  return null;
}
