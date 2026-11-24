import { apiFetch } from './client';
import type {
  ModerationAcknowledgeResponse,
  ModerationReviewQueue,
  ModerationSettings,
} from '../types/moderation';

/**
 * Relative paths only: the JWT is an httpOnly cookie that nginx turns into an
 * Authorization header. Platform-admin only on the backend.
 */
export const MODERATION_REVIEW_QUEUE_PATH = '/api/v1/moderation/review-queue';
export const MODERATION_SETTINGS_PATH = '/api/v1/moderation/settings';

/** The page size the review page asks for; the API caps it. */
export const MODERATION_REVIEW_QUEUE_DEFAULT_LIMIT = 100;

export interface ModerationReviewQueueParams {
  state?: string;
  limit?: number;
}

/** The order the params are appended in. Asserted by the tests, so it stays stable. */
const PARAM_ORDER = ['state', 'limit'] as const;

/**
 * Builds the (relative) review-queue URL from only the supplied params.
 *
 * `=== undefined`/`null` rather than a falsy check: `limit: 0` is a quantity the
 * caller chose, and a falsy check would silently replace it with the server's
 * default. An empty or whitespace-only `state` counts as "not supplied".
 */
export function getModerationReviewQueueUrl(params: ModerationReviewQueueParams = {}): string {
  const parts: string[] = [];
  for (const key of PARAM_ORDER) {
    const value = params[key];
    if (value === undefined || value === null) continue;
    if (typeof value === 'string' && value.trim() === '') continue;
    parts.push(`${key}=${encodeURIComponent(String(value))}`);
  }
  return parts.length > 0
    ? `${MODERATION_REVIEW_QUEUE_PATH}?${parts.join('&')}`
    : MODERATION_REVIEW_QUEUE_PATH;
}

/** Reads pending mid-band decisions. */
export function fetchModerationReviewQueue(
  params: ModerationReviewQueueParams = {},
): Promise<ModerationReviewQueue> {
  return apiFetch<ModerationReviewQueue>(getModerationReviewQueueUrl(params));
}

export function getModerationAcknowledgeUrl(id: string): string {
  return `${MODERATION_REVIEW_QUEUE_PATH}/${encodeURIComponent(id)}/acknowledge`;
}

/**
 * Records that an operator has seen one queue item. Idempotent and review-only:
 * nothing the user uploaded or generated is changed.
 */
export function acknowledgeModerationItem(id: string): Promise<ModerationAcknowledgeResponse> {
  return apiFetch<ModerationAcknowledgeResponse>(getModerationAcknowledgeUrl(id), {
    method: 'POST',
  });
}

/** Reads the effective moderation settings. Read-only in the UI. */
export function fetchModerationSettings(): Promise<ModerationSettings> {
  return apiFetch<ModerationSettings>(MODERATION_SETTINGS_PATH);
}
