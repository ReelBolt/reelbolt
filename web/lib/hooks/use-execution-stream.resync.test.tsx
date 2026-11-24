// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { useExecutionStream } from './use-execution-stream';
import { useResyncTick } from './use-resync-tick';

vi.mock('@/i18n', () => ({ useT: () => (key: string) => key }));
vi.mock('./use-auth', () => ({ useAuth: () => ({ user: { org: 'org-1' }, activeOrgId: 'org-1' }) }));

class FakeEventSource {
  static instances: FakeEventSource[] = [];
  onerror: (() => void) | null = null;
  private listeners = new Map<string, () => void>();
  constructor(public url: string) {
    FakeEventSource.instances.push(this);
  }
  addEventListener(type: string, listener: () => void) {
    this.listeners.set(type, listener);
  }
  close() {}
  emit(type: string) {
    this.listeners.get(type)?.();
  }
  static latest() {
    return FakeEventSource.instances[FakeEventSource.instances.length - 1];
  }
}

function useBoth() {
  const stream = useExecutionStream({ projectId: 'p', workflowId: 'w', executionId: 'e', enabled: true });
  const tick = useResyncTick(stream.connectionState, false);
  return { state: stream.connectionState, tick };
}

describe('a stream the server closed', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    FakeEventSource.instances = [];
    vi.stubGlobal('EventSource', FakeEventSource);
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('makes the page resync once it is connected again, even after failed retries', () => {
    const { result } = renderHook(() => useBoth());
    act(() => FakeEventSource.latest().emit('connected'));
    expect(result.current).toEqual({ state: 'connected', tick: 0 });

    // The server ends the stream, and the first retry fails too.
    act(() => FakeEventSource.latest().onerror?.());
    act(() => { vi.advanceTimersByTime(60_000); });
    expect(result.current.state).toBe('reconnecting');
    act(() => FakeEventSource.latest().onerror?.());
    act(() => { vi.advanceTimersByTime(60_000); });
    expect(result.current.state).toBe('reconnecting');

    act(() => FakeEventSource.latest().emit('connected'));
    expect(result.current).toEqual({ state: 'connected', tick: 1 });
  });
});
