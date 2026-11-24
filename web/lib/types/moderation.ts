/**
 * The content-moderation admin API, as the browser reads it.
 *
 * All three payloads are camelCase mirrors of the backend DTOs: the review queue
 * (`GET /api/v1/moderation/review-queue`), one acknowledgement
 * (`POST /api/v1/moderation/review-queue/{id}/acknowledge`) and the read-only
 * settings (`GET /api/v1/moderation/settings`).
 *
 * The queue is REVIEW-ONLY. There is deliberately no block, dismiss or delete
 * request type here: acknowledging records that an operator has seen a mid-band
 * decision and changes no user content.
 */

/**
 * One pending mid-band decision: the provider scored it between the review and
 * block thresholds, so it neither passed silently nor blocked anybody.
 */
export interface ModerationReviewQueueItem {
  id: string;
  /** Where the content came from, e.g. `project_upload` or `agent_output`. */
  surface: string;
  /** What the caller was trying to do when the decision was written. */
  action: string;
  /** The kind of content sampled, e.g. `image` or `text`. */
  contentKind: string;
  /** The highest-scoring category, or null when the provider returned none. */
  topCategory: string | null;
  /** The highest category score (0..1), or null when the provider returned none. */
  topScore: number | null;
  /** A stable machine-readable reason for the decision. */
  reasonCode: string | null;
  /** `pending` until an operator acknowledges it; acknowledgement does not hide the row's history. */
  reviewState: string;
  projectId: string | null;
  projectName: string | null;
  /** ISO-8601 instant the decision was recorded. */
  createdAt: string;
  /** ISO-8601 instant after which the row is no longer actionable, or null. */
  expiresAt: string | null;
}

/** `GET /api/v1/moderation/review-queue?state=pending&limit=100`. */
export interface ModerationReviewQueue {
  items: ModerationReviewQueueItem[];
  /** The total matching the filter, which may exceed the returned page (`limit`). */
  total: number;
  /** The page size the server actually applied. */
  limit: number;
}

/** `POST /api/v1/moderation/review-queue/{id}/acknowledge`. Idempotent. */
export interface ModerationAcknowledgeResponse {
  id: string;
  reviewState: string;
}

/**
 * `GET /api/v1/moderation/settings` — read-only in this UI.
 *
 * `model` is null when moderation has no resolved provider, which is a valid
 * configuration (moderation is off) rather than an error.
 */
export interface ModerationSettings {
  enabled: boolean;
  model: string | null;
  /** A score at or above this is blocked; below `reviewThreshold` passes silently. */
  blockThreshold: number;
  /** A score at or above this (and below `blockThreshold`) goes to the review queue. */
  reviewThreshold: number;
  hardBlockCategories: string[];
  timeoutSeconds: number;
  auditRetentionDays: number;
  /** True when an unavailable provider refuses publication rather than letting it through. */
  failClosedAtPublish: boolean;
}
