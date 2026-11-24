/**
 * Client error reporting (alpha-launch T-018). A browser error is reported to the first-party
 * endpoint — never a third-party SDK — and only when the person has opted in. The payload is built
 * from `allowlist.ts`, so only the named fields are ever read out of the error.
 *
 * The error's **message and stack are not sent**: they can carry user content, and the server does
 * not rely on a client's own redaction. What travels is the error's class name and the redacted
 * route, which is the fingerprint an operator groups by.
 *
 * Reporting is best-effort and never changes what the person sees: the error is still thrown to the
 * app's own handlers exactly as before, and a failure to report it is swallowed.
 */

import { buildClientErrorPayload, type ClientErrorPayload } from './allowlist';
import { sendClientError } from './client';
import { telemetryEnabled } from './config';
import { readTelemetryConsent } from './consent';

/** The extra context a caller may attach; only `route` is allowlisted, and it is redacted. */
export interface ClientErrorContext {
  route?: string;
}

/** The allowlisted half of a thrown value: its class name, or `Error` when there is none. */
export function errorName(error: unknown): string {
  return error instanceof Error && error.name ? error.name : 'Error';
}

/**
 * Reports one client error, if consent has been granted. Returns whether a report was sent.
 *
 * With consent withheld this is a no-op: no request leaves the browser.
 */
export function reportClientError(error: unknown, context: ClientErrorContext = {}): boolean {
  if (typeof window === 'undefined') return false;
  if (!telemetryEnabled()) return false;
  if (readTelemetryConsent() !== 'granted') return false;

  const payload: ClientErrorPayload = buildClientErrorPayload({
    name: errorName(error),
    route: context.route ?? window.location.pathname,
  });

  void sendClientError(payload);
  return true;
}
