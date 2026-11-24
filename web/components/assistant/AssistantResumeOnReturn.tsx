'use client';

import { useEffect, useRef } from 'react';
import { useAuiState } from '@assistant-ui/react';
import { respondToApproval } from '@/lib/api/assistant';
import { needsResume, pendingCardsLeftByStop, RESUME_MIN_INTERVAL_MS } from '@/lib/assistant/resume';

interface Props {
  /** Reads the thread and the turn's state again; re-attaches to a turn the server still owns. */
  onReload: () => void;
}

/**
 * Brings a conversation back after a phone screen change.
 *
 * Switching apps or locking the phone drops the chat connection, yet the server keeps the turn running
 * and keeps an open permission card answerable. This renders nothing: when the page becomes visible again
 * (or the network returns, or the reply stream ends broken or with a card still waiting), it asks
 * `needsResume` and, if so, reloads the conversation — which re-attaches to the live turn, replays the card
 * and shows the finished reply. It never starts a turn and never sends the person's message again.
 */
export function AssistantResumeOnReturn({ onReload }: Props): null {
  const messages = useAuiState((s) => s.thread.messages);
  const isRunning = useAuiState((s) => s.thread.isRunning);
  const latest = useRef({ messages, isRunning, onReload });
  const hiddenAt = useRef<number | null>(null);
  const lastResumeAt = useRef(0);

  useEffect(() => {
    latest.current = { messages, isRunning, onReload };
  }, [messages, isRunning, onReload]);

  useEffect(() => {
    const look = (hiddenMs: number) => {
      const now = Date.now();
      if (now - lastResumeAt.current < RESUME_MIN_INTERVAL_MS) return;
      const { messages: current, isRunning: running, onReload: reload } = latest.current;
      if (!needsResume({ messages: current, isRunning: running, hiddenMs })) return;
      lastResumeAt.current = now;
      reload();
    };

    const onVisibility = () => {
      if (document.hidden) {
        hiddenAt.current = Date.now();
        return;
      }
      const hiddenMs = hiddenAt.current === null ? 0 : Date.now() - hiddenAt.current;
      hiddenAt.current = null;
      look(hiddenMs);
    };
    const onReturn = () => look(0);

    document.addEventListener('visibilitychange', onVisibility);
    window.addEventListener('pageshow', onReturn);
    window.addEventListener('online', onReturn);
    return () => {
      document.removeEventListener('visibilitychange', onVisibility);
      window.removeEventListener('pageshow', onReturn);
      window.removeEventListener('online', onReturn);
    };
  }, []);

  // Stop only aborts this screen's stream; the server keeps the card open (and the turn with it). Say "don't
  // allow" for each card the person left open, once, so the turn ends instead of holding its slot.
  const declined = useRef(new Set<string>());
  const stoppedCards = pendingCardsLeftByStop(messages).join(',');
  useEffect(() => {
    for (const id of stoppedCards === '' ? [] : stoppedCards.split(',')) {
      if (declined.current.has(id)) continue;
      declined.current.add(id);
      // A 404/409 means the card is already gone (answered or expired): nothing left to do.
      respondToApproval(id, 'deny').catch(() => undefined);
    }
  }, [stoppedCards]);

  // The stream ended broken, or ended with a card still waiting, while the screen is in front: look once, shortly after.
  const lastAssistant = [...(messages ?? [])].reverse().find((message) => message?.role === 'assistant');
  const brokenOrWaiting = !isRunning && needsResume({ messages, isRunning, hiddenMs: 0 });
  const signature = brokenOrWaiting ? (lastAssistant?.id ?? 'last') : null;
  useEffect(() => {
    if (signature === null || document.hidden) return;
    const timer = setTimeout(() => {
      const now = Date.now();
      if (now - lastResumeAt.current < RESUME_MIN_INTERVAL_MS) return;
      const { messages: current, isRunning: running, onReload: reload } = latest.current;
      if (!needsResume({ messages: current, isRunning: running, hiddenMs: 0 })) return;
      lastResumeAt.current = now;
      reload();
    }, 1000);
    return () => clearTimeout(timer);
  }, [signature]);

  return null;
}
