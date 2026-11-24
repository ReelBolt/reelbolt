// @vitest-environment jsdom
import { act, cleanup, render } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { notifyAuthChange } from '@/lib/hooks/use-auth';
import { getWorkflowEventsConnection, resetWorkflowEvents, subscribeWorkflowEvents, useWorkflowEvents } from '../events';
import { FakeEventSource, signInAs } from './helpers';

beforeEach(() => {
  FakeEventSource.instances = [];
  vi.stubGlobal('EventSource', FakeEventSource);
  signInAs();
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('shared workflow event stream', () => {
  it('opens one EventSource for many subscribers and closes it when the last leaves', () => {
    const a = subscribeWorkflowEvents(['step.progress'], () => {});
    const b = subscribeWorkflowEvents(['execution.running'], () => {});
    expect(FakeEventSource.instances).toHaveLength(1);
    a();
    expect(FakeEventSource.instances[0].closed).toBe(false);
    b();
    expect(FakeEventSource.instances[0].closed).toBe(true);
  });

  it('routes frames by type, parses them and ignores malformed ones', () => {
    const got: string[] = [];
    const other: string[] = [];
    const off1 = subscribeWorkflowEvents(['step.progress'], (type, frame) => got.push(`${type}:${frame.executionId}:${frame.data.percentComplete}`));
    const off2 = subscribeWorkflowEvents(['execution.running'], (type) => other.push(type));
    const source = FakeEventSource.instances[0];
    source.emit('step.progress', { executionId: 'e1', timestamp: 'now', data: { percentComplete: 40 } });
    source.emit('step.progress', 'not json');
    source.emit('execution.running', { executionId: 'e1', timestamp: 'now', data: {} });
    expect(got).toEqual(['step.progress:e1:40']);
    expect(other).toEqual(['execution.running']);
    off1();
    off2();
  });

  it('reports the connection state', () => {
    const off = subscribeWorkflowEvents(['step.progress'], () => {});
    const source = FakeEventSource.instances[0];
    act(() => source.emit('connected', {}));
    expect(getWorkflowEventsConnection()).toBe('connected');
    act(() => source.onerror?.(new Event('error')));
    expect(getWorkflowEventsConnection()).toBe('reconnecting');
    off();
  });

  it('resetWorkflowEvents re-opens the stream while someone listens', () => {
    const off = subscribeWorkflowEvents(['step.progress'], () => {});
    resetWorkflowEvents();
    expect(FakeEventSource.instances).toHaveLength(2);
    expect(FakeEventSource.instances[0].closed).toBe(true);
    expect(FakeEventSource.instances[1].closed).toBe(false);
    off();
  });

  it('re-opens once when the active workspace changes, however many hooks listen', () => {
    function Probe() {
      useWorkflowEvents(['step.progress'], () => {});
      return null;
    }
    render(
      <>
        <Probe />
        <Probe />
      </>,
    );
    expect(FakeEventSource.instances).toHaveLength(1);
    act(() => {
      signInAs('org-acme');
      notifyAuthChange();
    });
    expect(FakeEventSource.instances[0].closed).toBe(true);
    expect(FakeEventSource.instances.filter((s) => !s.closed)).toHaveLength(1);
    expect(FakeEventSource.instances.length).toBeLessThanOrEqual(2);
  });
});
