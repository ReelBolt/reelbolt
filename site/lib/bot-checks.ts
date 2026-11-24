// Cheap bot checks shared by the contact handlers (app/api/contact/route.ts and
// functions/api/contact.ts). A request that fails one is answered exactly like a success, so a
// bot learns nothing, and nothing is sent or forwarded. No React/Next imports: both runtimes use it.

// The form records `startedAt` (ms since the epoch) when it mounts. A person needs a few seconds
// to fill it in; a script posting straight away does not.
export const MIN_FILL_TIME_MS = 3000;
// Allowance for a visitor's clock running ahead of the server's. Further ahead than this is forged.
export const MAX_CLOCK_SKEW_MS = 60_000;

/**
 * True when the submission should be treated like a filled honeypot: `startedAt` is missing or not
 * a finite number, it is less than MIN_FILL_TIME_MS before `now`, or it is more than
 * MAX_CLOCK_SKEW_MS ahead of `now`. A timestamp up to MAX_CLOCK_SKEW_MS ahead is accepted as a
 * visitor clock running fast, so a real enquiry from a skewed clock is not silently dropped.
 */
export function isTooFastSubmission(startedAt: unknown, now: number): boolean {
  if (typeof startedAt !== 'number' || !Number.isFinite(startedAt)) return true;
  const elapsed = now - startedAt;
  if (elapsed < 0) return -elapsed > MAX_CLOCK_SKEW_MS;
  return elapsed < MIN_FILL_TIME_MS;
}

/** True when the honeypot field was filled in. */
export function isHoneypotFilled(website: unknown): boolean {
  return typeof website === 'string' && website.trim().length > 0;
}

/** True when a contact payload should get a success-looking answer and nothing else. */
export function isLikelyBot(payload: { website?: unknown; startedAt?: unknown }, now: number): boolean {
  return isHoneypotFilled(payload.website) || isTooFastSubmission(payload.startedAt, now);
}
