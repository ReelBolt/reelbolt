import { apiFetch } from './client';

// Relative path only: the JWT is an httpOnly cookie that nginx turns into an Authorization header.
export const VIDEO_SPEND_LIMIT_PATH = '/api/v1/video-spend-limit';

/**
 * The caller's OWN daily video-generation limit and alert level (docs/design/video-spend-limit.md), in credits.
 * `dailyLimitCredits` null means no daily limit beyond the credit balance and each step's own limit.
 */
export interface VideoSpendLimit {
  dailyLimitCredits: number | null;
  alertThresholdCredits: number | null;
  /** What the caller's runs spent on video today (UTC day). */
  usedTodayCredits: number;
  /** The room left under the limit; null when there is no limit. */
  remainingTodayCredits: number | null;
  alertReached: boolean;
  limitReached: boolean;
  /** The next 00:00 UTC, when the day's use starts over. */
  resetsAt: string;
}

export interface VideoSpendLimitBody {
  dailyLimitCredits: number | null;
  alertThresholdCredits: number | null;
}

/** Replaces both fields; null clears. Refused with `{ error }` for a negative number or an alert above the limit. */
export async function saveVideoSpendLimit(body: VideoSpendLimitBody): Promise<VideoSpendLimit> {
  return apiFetch<VideoSpendLimit>(VIDEO_SPEND_LIMIT_PATH, { method: 'PUT', body: JSON.stringify(body) });
}
