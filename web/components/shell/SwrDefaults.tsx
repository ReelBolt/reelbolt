'use client';

import { SWRConfig } from 'swr';

/**
 * How long a window focus is ignored after the last one triggered a refetch. SWR's default is
 * 5 seconds, and a focus refetches EVERY query mounted on the page — eight on a run's page. A
 * browser that fires focus events steadily turned that into the whole page re-downloaded every
 * 5 seconds for an hour (~120 MB a minute on the test deployment). Thirty seconds still makes
 * coming back to a tab feel current; live progress comes from the SSE streams and the hooks' own
 * `refreshInterval`, neither of which this throttles.
 */
export const FOCUS_THROTTLE_MS = 30_000;

export function SwrDefaults({ children }: { children: React.ReactNode }) {
  return <SWRConfig value={{ focusThrottleInterval: FOCUS_THROTTLE_MS }}>{children}</SWRConfig>;
}
