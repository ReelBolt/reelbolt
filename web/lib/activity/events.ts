'use client';

import { useEffect, useRef, useSyncExternalStore } from 'react';
import { useAuth } from '@/lib/hooks/use-auth';

/**
 * ONE shared, ref-counted EventSource for the Go API's global stream. The API caps that stream at
 * three connections per user, so every consumer (the activity provider, the dashboard, the
 * workflow monitor, the media derivative progress) subscribes here instead of opening its own.
 *
 * The stream opens when the first subscriber arrives and closes when the last one leaves. It
 * carries the cookie it was opened with, so a workspace switch must re-open it: the hook below
 * does that (`resetWorkflowEvents`), keeping the A15-UI reconnect rule.
 */
export const WORKFLOW_EVENTS_URL = '/api/v1/workflows/events';

/** A parsed stream frame. */
export interface WorkflowFrame {
  type: string;
  executionId?: string;
  projectId?: string;
  timestamp: string;
  data: Record<string, unknown>;
}

export type WorkflowEventHandler = (type: string, frame: WorkflowFrame, raw: string) => void;
export type WorkflowEventsConnection = 'connecting' | 'connected' | 'reconnecting';

interface Subscriber {
  types: ReadonlySet<string>;
  handler: WorkflowEventHandler;
}

const subscribers = new Set<Subscriber>();
const stateListeners = new Set<() => void>();
let source: EventSource | null = null;
let registered = new Set<string>();
let connection: WorkflowEventsConnection = 'connecting';
let currentOrg: string | null | undefined;

function setConnection(next: WorkflowEventsConnection): void {
  if (connection === next) return;
  connection = next;
  stateListeners.forEach((l) => l());
}

/** Parses a frame; anything that is not a JSON object is ignored. */
export function parseWorkflowFrame(type: string, raw: string): WorkflowFrame | null {
  let value: unknown;
  try {
    value = JSON.parse(raw);
  } catch {
    return null;
  }
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
  const v = value as Record<string, unknown>;
  const data = v.data && typeof v.data === 'object' && !Array.isArray(v.data) ? (v.data as Record<string, unknown>) : {};
  return {
    type: typeof v.type === 'string' && v.type ? v.type : type,
    executionId: typeof v.executionId === 'string' ? v.executionId : undefined,
    projectId: typeof v.projectId === 'string' ? v.projectId : undefined,
    timestamp: typeof v.timestamp === 'string' ? v.timestamp : new Date().toISOString(),
    data,
  };
}

function dispatch(type: string, ev: Event): void {
  const raw = (ev as MessageEvent<string>).data;
  if (typeof raw !== 'string') return;
  const frame = parseWorkflowFrame(type, raw);
  if (!frame) return;
  setConnection('connected');
  for (const sub of [...subscribers]) if (sub.types.has(type)) sub.handler(type, frame, raw);
}

function register(type: string): void {
  if (!source || registered.has(type)) return;
  registered.add(type);
  source.addEventListener(type, (ev) => dispatch(type, ev));
}

function open(): void {
  if (source || typeof EventSource === 'undefined') return;
  source = new EventSource(WORKFLOW_EVENTS_URL);
  registered = new Set();
  source.addEventListener('connected', () => setConnection('connected'));
  source.onerror = () => setConnection('reconnecting');
  for (const sub of subscribers) sub.types.forEach(register);
}

function close(): void {
  if (!source) return;
  source.close();
  source = null;
  registered = new Set();
}

/** Subscribes to the named event types. Returns the unsubscribe function. */
export function subscribeWorkflowEvents(types: string[], handler: WorkflowEventHandler): () => void {
  const sub: Subscriber = { types: new Set(types), handler };
  subscribers.add(sub);
  open();
  sub.types.forEach(register);
  return () => {
    if (!subscribers.delete(sub)) return;
    if (subscribers.size === 0) {
      close();
      setConnection('connecting');
    }
  };
}

/** Closes the stream and, when anyone is still listening, opens a fresh one (new session cookie). */
export function resetWorkflowEvents(): void {
  close();
  setConnection('connecting');
  if (subscribers.size > 0) open();
}

/** Re-opens the stream when the active workspace differs from the one it was opened for. */
function syncWorkflowEventsOrg(orgId: string | null | undefined): void {
  if (currentOrg === orgId) return;
  const first = currentOrg === undefined;
  currentOrg = orgId;
  if (!first && source) resetWorkflowEvents();
}

export function getWorkflowEventsConnection(): WorkflowEventsConnection {
  return connection;
}

function subscribeConnection(listener: () => void): () => void {
  stateListeners.add(listener);
  return () => {
    stateListeners.delete(listener);
  };
}

/** The stream's connection state, for consumers that show it. */
export function useWorkflowEventsConnection(): WorkflowEventsConnection {
  return useSyncExternalStore(subscribeConnection, getWorkflowEventsConnection, () => 'connecting' as const);
}

/**
 * Subscribes for the component's lifetime. `handler` may change between renders without
 * re-subscribing; a change of `types` content or of the active workspace re-subscribes.
 */
export function useWorkflowEvents(types: string[], handler: WorkflowEventHandler): void {
  const { activeOrgId } = useAuth();
  const handlerRef = useRef(handler);
  useEffect(() => {
    handlerRef.current = handler;
  });
  const typesKey = types.join('|');
  useEffect(() => {
    syncWorkflowEventsOrg(activeOrgId);
    const list = typesKey ? typesKey.split('|') : [];
    return subscribeWorkflowEvents(list, (type, frame, raw) => handlerRef.current(type, frame, raw));
  }, [activeOrgId, typesKey]);
}
