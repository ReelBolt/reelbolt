import { ApiError } from '@/lib/api/client';
import { normalizeReconcileOutcome } from '@/lib/api/billing-admin';
import { sentenceStart } from '@/components/billing/subscription';
import type { BillingResyncResult, ReconcileOutcome } from '@/lib/types/billing-admin';

/**
 * Plain words for the reconcile attention list and the resync answer. Admin only, so "webhook" and
 * the provider name are fine here; codes never reach the screen when a sentence exists for them, and an
 * unknown code from a newer backend is shown raw rather than dropped.
 */

/** What copy says when the caller has no provider name: neutral, never a hard-coded merchant. */
const NEUTRAL = 'your payment provider';

const REASON_LABELS = (provider: string): Record<string, string> => ({
  change_pending: 'Plan change not confirmed',
  renewal_overdue: 'Renewal not confirmed',
  cancel_overdue: 'Cancellation not confirmed',
  checkout_unconfirmed: 'Paid checkout not confirmed',
  reconcile_failing: `${sentenceStart(provider)} could not be read`,
});

export function reasonLabel(reason: string, provider = NEUTRAL): string {
  return REASON_LABELS(provider)[reason] ?? reason;
}

const OUTCOME_LABELS = (provider: string): Record<ReconcileOutcome, string> => ({
  Healed: `Updated from ${provider}`,
  InSync: 'Already up to date',
  NothingToReconcile: `No ${provider} subscription to read`,
  TooRecent: 'Too recent; try again shortly',
  Stale: `${sentenceStart(provider)}'s state could not be applied; check the logs`,
  ProviderUnavailable: `${sentenceStart(provider)} could not be reached; nothing changed`,
  Busy: 'Another billing change was in progress; nothing changed',
});

/** Mantine color per outcome: green only when something was applied, yellow when it needs a look. */
const OUTCOME_COLORS: Record<ReconcileOutcome, string> = {
  Healed: 'green',
  InSync: 'blue',
  NothingToReconcile: 'blue',
  TooRecent: 'yellow',
  Stale: 'yellow',
  ProviderUnavailable: 'red',
  Busy: 'yellow',
};

export function outcomeLabel(raw: string | null, provider = NEUTRAL): string {
  if (!raw) return 'Not checked yet';
  const outcome = normalizeReconcileOutcome(raw);
  return outcome ? OUTCOME_LABELS(provider)[outcome] : raw;
}

export function outcomeColor(raw: string | null): string {
  const outcome = normalizeReconcileOutcome(raw);
  return outcome ? OUTCOME_COLORS[outcome] : 'gray';
}

const DRIFT_LABELS = (provider: string): Record<string, string> => ({
  plan: 'plan',
  seats: 'seats',
  period: 'billing period',
  status: 'status',
  cancel_at_period_end: 'scheduled cancellation',
  subscription_missing: `subscription missing in ${provider}`,
});

function plural(count: number, one: string, many: string): string {
  return `${count} ${count === 1 ? one : many}`;
}

/** The notification body for a 200 resync: what differed and what was applied. */
export function describeResyncResult(result: BillingResyncResult, workspace: string, provider = NEUTRAL): string {
  const parts: string[] = [];
  const drift = Array.isArray(result.drift) ? result.drift : [];
  if (drift.length > 0) {
    parts.push(`Differed: ${drift.map((field) => DRIFT_LABELS(provider)[field] ?? field).join(', ')}.`);
  }
  const events = result.subscriptionEventsApplied ?? 0;
  const transactions = result.transactionsApplied ?? 0;
  if (events > 0 || transactions > 0) {
    parts.push(
      `Applied ${plural(events, 'subscription event', 'subscription events')} and ${plural(transactions, 'transaction', 'transactions')}.`,
    );
  }
  return parts.length > 0 ? `${workspace}: ${parts.join(' ')}` : `${workspace}: nothing was changed.`;
}

/**
 * The sentence the server wrote, if any: `message`, or an `error` that is prose rather than a code
 * like `change_in_progress`. A bare status text ("Bad Gateway" from a proxy) is never used.
 */
function serverSentence(body: unknown): string | null {
  if (typeof body !== 'object' || body === null) return null;
  const { message, error } = body as Record<string, unknown>;
  if (typeof message === 'string' && message.trim()) return message.trim();
  if (typeof error === 'string' && /\s/.test(error.trim())) return error.trim();
  return null;
}

/** The server's own sentence when it sent one, otherwise a plain fallback for the status. */
export function describeResyncError(err: unknown, provider = NEUTRAL): string {
  if (!(err instanceof ApiError)) {
    return 'The resync request did not reach the server. Check your connection and try again.';
  }
  const sentence = serverSentence(err.body);
  if (sentence) return sentence;
  if (err.status === 400 && err.errors.length > 0) return err.errors.join(' ');
  if (err.status === 409 || err.code === 'change_in_progress') {
    return 'Another billing change for this workspace is in progress, so nothing was applied. Try again in a minute.';
  }
  if (err.status === 502 || err.code === 'billing_provider_error') {
    return `${sentenceStart(provider)} could not be reached, so nothing changed. Try again in a few minutes.`;
  }
  if (err.status === 404) {
    return 'No workspace has that ID, or billing is turned off on this deployment. Check the ID and try again.';
  }
  if (err.status === 400) {
    return 'The server refused the request. Keep the reason under 500 characters and try again.';
  }
  if (err.status === 403) {
    return 'Only platform administrators can resync a workspace.';
  }
  return `The resync failed (HTTP ${err.status}). Try again, and check the server logs if it keeps failing.`;
}

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function isGuid(value: string): boolean {
  return GUID.test(value.trim());
}

/** The exact instant for a tooltip, in the reader's locale and time zone. */
export function formatExactTime(iso: string | null): string {
  if (!iso) return '';
  const at = new Date(iso);
  if (Number.isNaN(at.getTime())) return iso;
  return at.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'long' });
}

/** `stuckAfterSeconds` as the shortest honest phrase: "15 minutes", "2 hours", "90 seconds". */
export function formatStuckAfter(seconds: number): string {
  if (seconds % 3600 === 0 && seconds >= 3600) return plural(seconds / 3600, 'hour', 'hours');
  if (seconds % 60 === 0 && seconds >= 60) return plural(seconds / 60, 'minute', 'minutes');
  return plural(seconds, 'second', 'seconds');
}
