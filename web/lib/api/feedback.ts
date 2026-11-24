import { apiFetch } from './client';
import type { FeedbackPayload } from '@/lib/feedback/context';

/**
 * The in-product bug / feedback channel (T-019): `POST /api/v1/feedback`, the alpha's primary signal
 * channel. The body is the payload `assembleFeedbackPayload` built — the message the user typed and
 * exactly the context they chose to attach. Nothing else is sent.
 *
 * Caller-scoped, not project-scoped: a report is about the product, and a project or execution is
 * optional context the user attaches, not the route's authority. The API answers `404` for a project
 * or execution the caller cannot see (never `403`) and `429` once the per-user rate limit is hit.
 */
export const FEEDBACK_URL = '/api/v1/feedback';

export interface FeedbackCreated {
  id: string;
  createdAt: string;
}

export function submitFeedback(payload: FeedbackPayload): Promise<FeedbackCreated> {
  return apiFetch<FeedbackCreated>(FEEDBACK_URL, {
    method: 'POST',
    body: JSON.stringify(payload)
  });
}
