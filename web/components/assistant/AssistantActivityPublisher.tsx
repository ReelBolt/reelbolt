'use client';

import { useEffect, useRef, useState } from 'react';
import { useAuiState } from '@assistant-ui/react';
import { useAssistant } from './AssistantShellProvider';

/** How long a thread must sit visibly read before the server is told. */
const MARK_READ_DEBOUNCE_MS = 800;

function useTabVisible(): boolean {
  const [visible, setVisible] = useState(() => typeof document === 'undefined' || !document.hidden);
  useEffect(() => {
    const update = () => setVisible(!document.hidden);
    document.addEventListener('visibilitychange', update);
    return () => document.removeEventListener('visibilitychange', update);
  }, []);
  return visible;
}

/**
 * Lives inside the runtime provider (so it works while the drawer is shut). Tells the shell which
 * thread this tab is streaming itself - those replies are not "background" - and marks a thread read
 * once its newest assistant message is actually on screen. Renders nothing; a failed mark is silent.
 */
export function AssistantActivityPublisher(): null {
  const { threadId, opened, activity, setLocalStreamingThreadId, markThreadRead } = useAssistant();
  const running = useAuiState((s) => s.thread.isRunning);
  const lastIsAssistant = useAuiState((s) => {
    const last = s.thread.messages[s.thread.messages.length - 1];
    return last?.role === 'assistant';
  });
  // The newest stored position among the messages on screen: a reply is "seen" only once it is here.
  const shownSequence = useAuiState((s) => {
    let max = 0;
    for (const m of s.thread.messages) {
      const n = m.metadata?.custom?.sequence;
      if (typeof n === 'number' && n > max) max = n;
    }
    return max;
  });
  const visible = useTabVisible();
  const wasRunning = useRef(false);

  useEffect(() => {
    setLocalStreamingThreadId(running ? threadId : null);
  }, [running, threadId, setLocalStreamingThreadId]);

  // Unread only counts once the reply itself is displayed - never mark what has not been shown.
  const hasUnread =
    threadId !== null &&
    activity.unread.some((u) => u.threadId === threadId) &&
    activity.unread.some((u) => u.threadId === threadId && u.sequence <= shownSequence);
  const onScreen = threadId !== null && opened && visible && !running && lastIsAssistant;

  // Unread replies for the thread on screen: read once they have been showing for a moment.
  useEffect(() => {
    if (!onScreen || !hasUnread || threadId === null) return;
    const timer = setTimeout(() => markThreadRead(threadId, shownSequence), MARK_READ_DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [onScreen, hasUnread, threadId, shownSequence, markThreadRead]);

  // A reply streamed here while the person was looking at it is read the moment it ends.
  useEffect(() => {
    const finished = wasRunning.current && !running;
    wasRunning.current = running;
    if (!finished || !onScreen || threadId === null) return;
    const timer = setTimeout(() => markThreadRead(threadId), MARK_READ_DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [running, onScreen, threadId, markThreadRead]);

  return null;
}
