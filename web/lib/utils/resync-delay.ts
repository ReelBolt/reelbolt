export const RESYNC_DEBOUNCE_MS = 1500;
export const RESYNC_MAX_WAIT_MS = 5000;

/** How long to wait before the next coalesced read: a 1.5 s trailing debounce, but never past 5 s after the first queued trigger. */
export function resyncDelay(now: number, firstQueuedAt: number): number {
  return Math.max(0, Math.min(RESYNC_DEBOUNCE_MS, firstQueuedAt + RESYNC_MAX_WAIT_MS - now));
}
