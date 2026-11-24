'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { getAssistantActivity, markThreadRead } from '@/lib/api/assistant';
import {
  ACTIVITY_RECENT_OPEN_MS,
  NO_ACTIVITY,
  diffActivity,
  nextActivityDelay,
} from '@/lib/assistant/activity';
import type { AssistantActivityEvent } from '@/lib/assistant/activity';
import type { AssistantActivity } from '@/lib/types/assistant';

const MAX_EVENTS = 20;
/** Survives sign-out/in, so a consumer's "last handled" marker never outruns new events. */
let eventCounter = 0;

/**
 * Polls what the assistant is doing across all threads, whether or not the panel is open. Quick
 * (5 s) while something is writing, a run is being waited on, or the panel was open in the last
 * minute; slow (30 s) otherwise; paused while the tab is hidden, with one poll when it returns or
 * the browser comes back online; exponential back-off after errors. Never runs when `enabled` is
 * false (signed out).
 */
export function useAssistantActivity(input: { enabled: boolean; opened: boolean; localThreadId: string | null }) {
  const { enabled, opened, localThreadId } = input;
  const [activity, setActivity] = useState<AssistantActivity>(NO_ACTIVITY);
  const [events, setEvents] = useState<readonly AssistantActivityEvent[]>([]);
  const live = useRef({ opened, localThreadId, closedAt: 0 });
  const pollNow = useRef<(() => void) | null>(null);

  useEffect(() => {
    const wasOpened = live.current.opened;
    live.current.opened = opened;
    live.current.localThreadId = localThreadId;
    if (wasOpened && !opened) live.current.closedAt = Date.now();
  }, [opened, localThreadId]);

  // Opening the panel is a reason to look right now.
  useEffect(() => {
    if (opened) pollNow.current?.();
  }, [opened]);

  useEffect(() => {
    if (!enabled) return;
    let stopped = false;
    let inFlight = false;
    let failures = 0;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let previous: AssistantActivity | null = null;
    const seen = new Set<string>();
    let current: AssistantActivity = NO_ACTIVITY;

    const schedule = () => {
      if (timer !== undefined) clearTimeout(timer);
      timer = undefined;
      if (stopped || document.hidden) return;
      const openedRecently =
        live.current.opened || Date.now() - live.current.closedAt < ACTIVITY_RECENT_OPEN_MS;
      timer = setTimeout(() => void poll(), nextActivityDelay({ activity: current, openedRecently, failures }));
    };

    const poll = async () => {
      if (stopped || inFlight || document.hidden) return;
      if (timer !== undefined) clearTimeout(timer);
      inFlight = true;
      try {
        const next = await getAssistantActivity();
        if (stopped) return;
        failures = 0;
        // The first answer is only a baseline: what was already unread is the badge's job.
        if (previous === null) {
          for (const item of next.unread) seen.add(`r:${item.threadId}:${item.sequence}`);
          for (const item of next.writing) {
            seen.add(`w:${item.threadId}:${item.startedAt}`);
            if (item.state === 'awaiting_approval') seen.add(`a:${item.threadId}:${item.startedAt}`);
          }
        } else {
          const fresh = diffActivity(previous, next, live.current.localThreadId, seen);
          if (fresh.length > 0) {
            setEvents((list) =>
              [...list, ...fresh.map((e) => ({ ...e, id: ++eventCounter }))].slice(-MAX_EVENTS),
            );
          }
        }
        previous = next;
        current = next;
        setActivity(next);
      } catch {
        failures += 1;
      } finally {
        inFlight = false;
        schedule();
      }
    };

    pollNow.current = () => void poll();
    const onVisibility = () => {
      if (!document.hidden) void poll();
    };
    const onOnline = () => void poll();
    document.addEventListener('visibilitychange', onVisibility);
    window.addEventListener('online', onOnline);
    void poll();

    return () => {
      stopped = true;
      pollNow.current = null;
      if (timer !== undefined) clearTimeout(timer);
      document.removeEventListener('visibilitychange', onVisibility);
      window.removeEventListener('online', onOnline);
    };
  }, [enabled]);

  /** Best effort: a failure changes nothing and says nothing. */
  const markRead = useCallback((threadId: string, throughSequence?: number) => {
    void markThreadRead(threadId, throughSequence)
      .then(() => {
        setActivity((a) => ({
          ...a,
          unread: a.unread.filter(
            (u) => u.threadId !== threadId || (throughSequence !== undefined && u.sequence > throughSequence),
          ),
        }));
      })
      .catch(() => undefined);
  }, []);

  return { activity: enabled ? activity : NO_ACTIVITY, events: enabled ? events : [], markRead };
}
