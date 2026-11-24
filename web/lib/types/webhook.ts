/** Dashboard webhook endpoints (`/api/v1/org/webhooks`), the shape the Inference API serves. */
export const WEBHOOK_EVENTS = [
  'run.succeeded',
  'run.failed',
  'run.cancelled',
  'run.step_completed',
  'run_request.started',
  'run_request.refused',
] as const;

export type WebhookEvent = (typeof WEBHOOK_EVENTS)[number];

export interface WebhookEndpoint {
  id: string;
  url: string;
  description: string | null;
  events: string[];
  /** null means "all projects". */
  projectIds: string[] | null;
  enabled: boolean;
  disabledReason: string | null;
  createdAt: string;
  lastSuccessAt: string | null;
  failingSince: string | null;
}

/**
 * The create response only: the endpoint plus its `whsec_...` secret, shown once and never cached.
 * The list type deliberately has no `secret`, so a list record can never carry one by accident.
 */
export type WebhookCreated = WebhookEndpoint & { secret: string };

export type WebhookDeliveryStatus = 'pending' | 'delivered' | 'failed' | 'abandoned';

export interface WebhookDelivery {
  id: string;
  eventType: string;
  status: WebhookDeliveryStatus;
  attempts: number;
  nextAttemptAt: string | null;
  lastStatusCode: number | null;
  lastError: string | null;
  createdAt: string;
  deliveredAt: string | null;
}

export interface WebhookDeliveryPage {
  data: WebhookDelivery[];
  nextCursor: string | null;
}

export interface CreateWebhookRequest {
  url: string;
  description?: string;
  events: string[];
  projectIds?: string[];
}

export interface UpdateWebhookRequest {
  url?: string;
  description?: string;
  events?: string[];
  /** An empty array means all projects. */
  projectIds?: string[];
  enabled?: boolean;
}
