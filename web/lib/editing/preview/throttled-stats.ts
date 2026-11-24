/**
 * Leading + trailing throttle for the playback stats the status bar shows.
 *
 * The session notifies on EVERY presented frame (30-60 per second); putting each snapshot into React
 * state re-rendered the whole editor workspace once more per frame (profiled: 2 commits per frame, one
 * of them for a counter that is read at human speed). The counter stays exact — it is the session's own
 * ledger — only the paint of it is limited to one update per `intervalMs`, and the last value always lands.
 */
export interface StatsThrottle<T> {
  push(value: T): void;
  /** Delivers the pending value now (pause, unmount of the playing state). */
  flush(): void;
  cancel(): void;
}

export function createStatsThrottle<T>(
  deliver: (value: T) => void,
  intervalMs = 250,
  clock: { now(): number; setTimeout(cb: () => void, ms: number): unknown; clearTimeout(handle: unknown): void } = {
    now: () => performance.now(),
    setTimeout: (cb, ms) => setTimeout(cb, ms),
    clearTimeout: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
  },
): StatsThrottle<T> {
  let last = Number.NEGATIVE_INFINITY;
  let pending: { value: T } | null = null;
  let timer: unknown = null;

  const send = () => {
    if (timer !== null) clock.clearTimeout(timer);
    timer = null;
    if (!pending) return;
    const { value } = pending;
    pending = null;
    last = clock.now();
    deliver(value);
  };

  return {
    push(value) {
      pending = { value };
      const wait = last + intervalMs - clock.now();
      if (wait <= 0) send();
      else if (timer === null) timer = clock.setTimeout(send, wait);
    },
    flush: send,
    cancel() {
      if (timer !== null) clock.clearTimeout(timer);
      timer = null;
      pending = null;
    },
  };
}

/** A one-value external store (for `useSyncExternalStore`), so a leaf component can follow a value the shell does not render. */
export interface ValueStore<T> {
  get(): T;
  set(value: T): void;
  subscribe(listener: () => void): () => void;
}

export function createValueStore<T>(initial: T): ValueStore<T> {
  let value = initial;
  const listeners = new Set<() => void>();
  return {
    get: () => value,
    set(next) {
      if (Object.is(next, value)) return;
      value = next;
      for (const listener of [...listeners]) listener();
    },
    subscribe(listener) {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
  };
}
