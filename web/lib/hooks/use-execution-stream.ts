'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import { useT } from '@/i18n';
import { useAuth } from './use-auth';

export type ExecutionStreamConnectionState = 'connecting' | 'connected' | 'reconnecting' | 'closed';

export type ExecutionStreamEventType =
  | 'step.started'
  | 'step.completed'
  | 'step.progress'
  | 'step.tool-called'
  | 'step.reasoning'
  | 'step.chat-turn'
  | 'execution.running'
  | 'execution.completed'
  | 'execution.failed';

export interface ExecutionStreamEvent {
  id: string;
  type: ExecutionStreamEventType;
  executionId: string;
  timestamp: string;
  payload: Record<string, unknown>;
}

interface IncomingExecutionEvent {
  type?: string;
  executionId?: string;
  timestamp?: string;
  data?: unknown;
}

const STREAM_EVENT_TYPES: ExecutionStreamEventType[] = [
  'step.started',
  'step.completed',
  'step.progress',
  'step.tool-called',
  'step.reasoning',
  'step.chat-turn',
  'execution.running',
  'execution.completed',
  'execution.failed',
];

function getNumber(payload: Record<string, unknown>, ...keys: string[]): number {
  for (const key of keys) {
    const value = payload[key];
    if (typeof value === 'number' && Number.isFinite(value)) {
      return value;
    }
  }

  return 0;
}

function getString(payload: Record<string, unknown>, ...keys: string[]): string {
  for (const key of keys) {
    const value = payload[key];
    if (typeof value === 'string' && value.length > 0) {
      return value;
    }
  }

  return '';
}

function normalizePayload(data: unknown): Record<string, unknown> {
  if (typeof data === 'object' && data !== null) {
    return data as Record<string, unknown>;
  }

  return {};
}

function normalizeEvent(type: ExecutionStreamEvent['type'], raw: IncomingExecutionEvent): ExecutionStreamEvent {
  const payload = normalizePayload(raw.data);
  const normalizedExecutionId = raw.executionId
    ?? getString(payload, 'executionId', 'ExecutionId')
    ?? 'unknown';

  return {
    id: `${Date.now()}-${Math.random().toString(16).slice(2)}`,
    type,
    executionId: normalizedExecutionId,
    timestamp: raw.timestamp ?? new Date().toISOString(),
    payload,
  };
}

interface UseExecutionStreamOptions {
  projectId: string;
  workflowId: string;
  executionId: string;
  enabled: boolean;
  /** Change it to close and reopen the stream (a phone tab that came back to the foreground). */
  reconnectKey?: number;
}

const EVENT_LIMIT = 150;
const RECONNECT_BASE_MS = 1000;
const RECONNECT_MAX_MS = 8000;

export function useExecutionStream({ projectId, workflowId, executionId, enabled, reconnectKey = 0 }: UseExecutionStreamOptions) {
  // The session travels in the cookie at connect time and every event feed is org-scoped, so a
  // workspace switch has to reopen the stream under the new session — an already-open EventSource
  // keeps the token it connected with. Keying the effect on the active org does that (A15-UI).
  const { activeOrgId } = useAuth();
  const [connectionState, setConnectionState] = useState<ExecutionStreamConnectionState>('connecting');
  const [lastError, setLastError] = useState<string | null>(null);
  const [events, setEvents] = useState<ExecutionStreamEvent[]>([]);
  const [lastEventAt, setLastEventAt] = useState<string | null>(null);
  const attemptRef = useRef(0);
  const t = useT();
  // Read through a ref so a language change never reconnects the stream.
  const tRef = useRef(t);
  useEffect(() => {
    tRef.current = t;
  }, [t]);

  useEffect(() => {
    if (!enabled) {
      // Nothing to connect or tear down; the returned `connectionState` below already reports
      // 'closed' while disabled, so there is nothing to sync here.
      return;
    }

    let source: EventSource | null = null;
    let reconnectTimeout: ReturnType<typeof setTimeout> | null = null;
    let cancelled = false;

    const connect = () => {
      if (cancelled) {
        return;
      }

      // A retry stays 'reconnecting': useResyncTick refetches on reconnecting -> connected, and that
      // refetch is how a page recovers an event published while its stream was down.
      setConnectionState((current) => (current === 'connected' || current === 'reconnecting' ? 'reconnecting' : 'connecting'));

      source = new EventSource(`/api/v1/workflows/executions/${executionId}/events`);

      source.addEventListener('connected', () => {
        attemptRef.current = 0;
        setConnectionState('connected');
        setLastError(null);
      });

      const onEvent = (eventType: ExecutionStreamEvent['type']) => (event: Event) => {
        try {
          const parsed = JSON.parse((event as MessageEvent<string>).data) as IncomingExecutionEvent;
          const normalized = normalizeEvent(eventType, parsed);

          setEvents((current) => [normalized, ...current].slice(0, EVENT_LIMIT));
          setLastEventAt(normalized.timestamp);
          setLastError(null);
        } catch {
          setLastError(tRef.current('libLabels.hooks.streamParse', { eventType }));
        }
      };

      STREAM_EVENT_TYPES.forEach((eventType) => {
        source?.addEventListener(eventType, onEvent(eventType));
      });

      source.onerror = () => {
        if (cancelled) {
          return;
        }

        setConnectionState('reconnecting');
        setLastError(tRef.current('libLabels.hooks.streamInterrupted'));

        if (source) {
          source.close();
          source = null;
        }

        const nextAttempt = attemptRef.current + 1;
        attemptRef.current = nextAttempt;
        const nextDelay = Math.min(RECONNECT_BASE_MS * 2 ** (nextAttempt - 1), RECONNECT_MAX_MS);
        reconnectTimeout = setTimeout(connect, nextDelay);
      };
    };

    connect();

    return () => {
      cancelled = true;
      if (reconnectTimeout) {
        clearTimeout(reconnectTimeout);
      }
      if (source) {
        source.close();
      }
      setConnectionState('closed');
    };
  }, [projectId, workflowId, executionId, enabled, activeOrgId, reconnectKey]);

  const metrics = useMemo(() => {
    const stepEvents = events.filter((event) => event.type === 'step.completed');

    const totalTokens = stepEvents.reduce((sum, event) => {
      return sum + getNumber(event.payload, 'tokensUsed', 'TokensUsed');
    }, 0);

    const totalInputTokens = stepEvents.reduce((sum, event) => {
      return sum + getNumber(event.payload, 'inputTokens', 'InputTokens');
    }, 0);

    const totalOutputTokens = stepEvents.reduce((sum, event) => {
      return sum + getNumber(event.payload, 'outputTokens', 'OutputTokens');
    }, 0);

    const averageDurationMs =
      stepEvents.length > 0
        ? Math.round(
            stepEvents.reduce((sum, event) => {
              return sum + getNumber(event.payload, 'durationMs', 'DurationMs');
            }, 0) / stepEvents.length,
          )
        : 0;

    return {
      totalEvents: events.length,
      stepEventCount: stepEvents.length,
      totalTokens,
      totalInputTokens,
      totalOutputTokens,
      averageDurationMs,
    };
  }, [events]);

  return {
    events,
    connectionState: enabled ? connectionState : 'closed',
    lastError,
    lastEventAt,
    metrics,
  };
}
