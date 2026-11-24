// @vitest-environment jsdom
/**
 * A15-UI step 1: "switching ... reconnects SSE". Every event feed in the dashboard carries the
 * session cookie it was opened with, so an already-open EventSource keeps the OLD workspace's
 * token. Each hook therefore keys its effect on the active org id, and a switch — which re-issues
 * the readable cookie — has to close the old streams and open fresh ones.
 *
 * This is the executable form of that contract, for all three feeds at once.
 */
import { act, cleanup, render } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { UserInfo } from '@/lib/types/auth';

vi.mock('@/lib/api/client', async (orig) => ({
  ...(await orig<typeof import('@/lib/api/client')>()),
  fetcher: vi.fn().mockResolvedValue([]),
}));

import { notifyAuthChange } from './use-auth';
import { useExecutionStream } from './use-execution-stream';
import { useWorkflowEvents } from './use-dashboard';
import { useWorkflowMonitor } from './use-workflow-monitor';

class FakeEventSource {
  static instances: FakeEventSource[] = [];
  url: string;
  closed = false;
  listeners = new Map<string, ((event: Event) => void)[]>();
  onerror: ((event: Event) => void) | null = null;

  constructor(url: string) {
    this.url = url;
    FakeEventSource.instances.push(this);
  }

  addEventListener(type: string, handler: (event: Event) => void) {
    const list = this.listeners.get(type) ?? [];
    list.push(handler);
    this.listeners.set(type, list);
  }

  removeEventListener() {}
  close() { this.closed = true; }
}

/** Three feeds, one of each kind — the same three the shell and the run page subscribe to. */
function Harness() {
  useExecutionStream({ projectId: 'p', workflowId: 'w', executionId: 'e', enabled: true });
  useWorkflowEvents(() => {}, () => {});
  useWorkflowMonitor();
  return null;
}

function signInAs(user: Partial<UserInfo>) {
  const full: UserInfo = {
    email: 'tommaso@example.com', isPlatformAdmin: false, mustChangePassword: false,
    org: 'org-personal', orgName: 'Personal', orgKind: 'Personal', orgRole: 'Owner',
    ...user,
  };
  document.cookie = `reelbolt_user=${encodeURIComponent(JSON.stringify(full))}; path=/`;
}

beforeEach(() => {
  FakeEventSource.instances = [];
  vi.stubGlobal('EventSource', FakeEventSource);
});
afterEach(() => {
  cleanup();
  document.cookie = 'reelbolt_user=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
  vi.unstubAllGlobals();
});

describe('SSE reconnection on a workspace switch', () => {
  it('opens one stream per feed, and reopening them is what a re-issued session does', () => {
    signInAs({});
    render(<Harness />);

    // The two global-stream feeds share ONE connection (lib/activity/events.ts); the run's own stream is separate.
    expect(FakeEventSource.instances).toHaveLength(2);
    expect(FakeEventSource.instances.map((s) => s.url).sort()).toEqual([
      '/api/v1/workflows/events',
      '/api/v1/workflows/executions/e/events',
    ]);
    expect(FakeEventSource.instances.some((s) => s.closed)).toBe(false);
  });

  it('closes every stream and reopens it when the active workspace changes', () => {
    signInAs({});
    render(<Harness />);
    const firstRound = [...FakeEventSource.instances];

    // What switch-org does: the re-issued cookie, then the auth store telling the app.
    act(() => {
      signInAs({ org: 'org-acme', orgName: 'Acme Team', orgKind: 'Team', orgRole: 'Admin' });
      notifyAuthChange();
    });

    expect(firstRound.every((s) => s.closed)).toBe(true);
    expect(FakeEventSource.instances).toHaveLength(4);
    const secondRound = FakeEventSource.instances.slice(2);
    expect(secondRound.map((s) => s.url).sort()).toEqual(firstRound.map((s) => s.url).sort());
    expect(secondRound.some((s) => s.closed)).toBe(false);
  });

  it('leaves a re-render with no workspace change alone, so a stream is not churned', () => {
    signInAs({});
    const { rerender } = render(<Harness />);
    act(() => { rerender(<Harness />); });

    expect(FakeEventSource.instances).toHaveLength(2);
    expect(FakeEventSource.instances.some((s) => s.closed)).toBe(false);
  });
});
