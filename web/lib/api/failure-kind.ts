import { isNetworkFailure } from './network-error';

export type FailureKind =
  | 'network'
  | 'notFound'
  | 'forbidden'
  | 'unauthorized'
  | 'paymentRequired'
  | 'rateLimited'
  | 'server'
  | 'other';

/** Sorts a failed call into the few cases a screen words differently. Reads `status` structurally (no import of ApiError). */
export function classifyFailure(err: unknown): FailureKind {
  if (isNetworkFailure(err)) return 'network';
  const status =
    typeof err === 'object' && err !== null && typeof (err as { status?: unknown }).status === 'number'
      ? (err as { status: number }).status
      : null;
  if (status === null) return 'other';
  if (status === 404 || status === 410) return 'notFound';
  if (status === 403) return 'forbidden';
  if (status === 401) return 'unauthorized';
  if (status === 402) return 'paymentRequired';
  if (status === 429) return 'rateLimited';
  if (status >= 500) return 'server';
  return 'other';
}
