import type { AssistantActivity } from '../types/assistant';

/** Poll cadence while something is happening (or the panel was just open), and otherwise. */
export const ACTIVITY_FAST_MS = 5000;
/** Only waiting on a run to finish (nothing writing): a slower check is enough. */
export const ACTIVITY_WATCH_MS = 10000;
export const ACTIVITY_SLOW_MS = 30000;
/** The panel counts as "recently open" for this long after it closes. */
export const ACTIVITY_RECENT_OPEN_MS = 60000;
/** The longest the poll backs off to after repeated failures. */
export const ACTIVITY_MAX_BACKOFF_MS = 300000;

export const NO_ACTIVITY: AssistantActivity = { writing: [], unread: [], pendingWatches: 0 };

/** What a background change looks like to the toast and the browser notification. */
export interface AssistantActivityEvent {
  /** Unique and increasing, so a consumer can remember the last one it handled. */
  id: number;
  kind: 'writing' | 'replied' | 'approval';
  threadId: string;
  title: string;
  /** The assistant picked the thread up by itself because a run finished. */
  wake: boolean;
  /** The reply's position in the thread, for `replied`. */
  sequence?: number;
}

/** Wait before the next poll. Errors back off exponentially from the slow cadence. */
export function nextActivityDelay(input: {
  activity: AssistantActivity;
  openedRecently: boolean;
  failures: number;
}): number {
  if (input.failures > 0) {
    return Math.min(ACTIVITY_SLOW_MS * 2 ** (input.failures - 1), ACTIVITY_MAX_BACKOFF_MS);
  }
  if (input.activity.writing.length > 0 || input.openedRecently) return ACTIVITY_FAST_MS;
  return input.activity.pendingWatches > 0 ? ACTIVITY_WATCH_MS : ACTIVITY_SLOW_MS;
}

/**
 * What is new in `next` compared with `prev`. A thread this tab is streaming itself raises nothing,
 * and a (thread, sequence) already reported never raises twice.
 */
export function diffActivity(
  prev: AssistantActivity,
  next: AssistantActivity,
  localThreadId: string | null,
  seen: Set<string>,
): Array<Omit<AssistantActivityEvent, 'id'>> {
  const events: Array<Omit<AssistantActivityEvent, 'id'>> = [];
  for (const item of next.writing) {
    if (item.threadId === localThreadId) continue;
    const before = prev.writing.find((w) => w.threadId === item.threadId && w.startedAt === item.startedAt);
    const wake = item.kind === 'run_wake';
    if (!before) {
      const key = `w:${item.threadId}:${item.startedAt}`;
      if (!seen.has(key)) {
        seen.add(key);
        events.push({ kind: 'writing', threadId: item.threadId, title: item.title, wake });
      }
    }
    if (item.state === 'awaiting_approval' && before?.state !== 'awaiting_approval') {
      const key = `a:${item.threadId}:${item.startedAt}`;
      if (!seen.has(key)) {
        seen.add(key);
        events.push({ kind: 'approval', threadId: item.threadId, title: item.title, wake });
      }
    }
  }
  for (const item of next.unread) {
    if (item.threadId === localThreadId) continue;
    const key = `r:${item.threadId}:${item.sequence}`;
    if (seen.has(key)) continue;
    seen.add(key);
    events.push({
      kind: 'replied',
      threadId: item.threadId,
      title: item.title,
      wake: item.kind === 'run_wake',
      sequence: item.sequence,
    });
  }
  return events;
}
