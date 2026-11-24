// Framework-free, in-memory, per-key rate limiter for the contact handlers (app/api/contact/route.ts
// and functions/api/contact.ts). Best effort only: it resets on restart and is per instance / per
// isolate. Same shape as the limiter in app/api/subscribe/route.ts, including the pruning that
// stops a stream of distinct client addresses from growing the map without bound.
// No React/Next imports: both runtimes use it and `node --test` imports it directly.

export interface RateLimiterOptions {
  windowMs: number;
  maxRequests: number;
  /** Once the map holds more keys than this, keys with no request inside the window are dropped. */
  pruneThreshold?: number;
}

export interface RateLimiter {
  /** Records a request for `key` and says whether it is over the limit. */
  isRateLimited(key: string, now?: number): boolean;
  /** Number of keys currently tracked (for tests). */
  size(): number;
}

export const DEFAULT_PRUNE_THRESHOLD = 1000;

export function createRateLimiter(options: RateLimiterOptions): RateLimiter {
  const { windowMs, maxRequests, pruneThreshold = DEFAULT_PRUNE_THRESHOLD } = options;
  const requestLog = new Map<string, number[]>();

  function prune(now: number): void {
    if (requestLog.size <= pruneThreshold) return;
    const windowStart = now - windowMs;
    for (const [key, timestamps] of requestLog) {
      const newest = timestamps[timestamps.length - 1];
      if (newest === undefined || newest <= windowStart) requestLog.delete(key);
    }
  }

  return {
    isRateLimited(key: string, now: number = Date.now()): boolean {
      prune(now);
      const windowStart = now - windowMs;
      const timestamps = (requestLog.get(key) ?? []).filter((ts) => ts > windowStart);
      if (timestamps.length >= maxRequests) {
        requestLog.set(key, timestamps);
        return true;
      }
      timestamps.push(now);
      requestLog.set(key, timestamps);
      return false;
    },
    size: () => requestLog.size,
  };
}
