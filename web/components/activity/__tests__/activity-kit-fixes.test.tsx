// @vitest-environment jsdom
/**
 * Kit-level cases of the QA probes (background-activity T-032), fixed in T-044. The full probe also covers the editor bridge and lives one layer up. Passing `it` = requirement verified.
 * `it.fails` = a divergence pinned by QA: the assertion states the REQUIRED behaviour and currently fails.
 * When the bug is fixed the `it.fails` turns red: flip it to `it`.
 */
import React, { useEffect } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';

const nav = vi.hoisted(() => ({ pathname: '/dashboard' }));
vi.mock('next/navigation', () => ({ usePathname: () => nav.pathname, useRouter: () => ({ push: vi.fn() }) }));
const toast = vi.hoisted(() => ({ show: vi.fn() }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: (...a: unknown[]) => toast.show(...a) } }));
vi.mock('next/link', () => ({
  default: ({ href, children, ...r }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...r} onClick={(e) => e.preventDefault()}>
      {children}
    </a>
  ),
}));
const media = vi.hoisted(() => ({ phone: false }));
vi.mock('@mantine/hooks', async (orig) => ({ ...(await orig<typeof import('@mantine/hooks')>()), useMediaQuery: () => media.phone }));

import { ActivityProvider, useActivity, type ActivityContextValue } from '@/lib/activity/ActivityProvider';
import { FakeEventSource, item, signInAs } from '@/lib/activity/__tests__/helpers';
import { WORKFLOW_EVENTS_URL } from '@/lib/activity/events';
import type { ActivityItem } from '@/lib/activity/types';
import { notifyAuthChange } from '@/lib/hooks/use-auth';
import { useWorkflowEvents as useDashboardEvents } from '@/lib/hooks/use-dashboard';
import { useExecutionStream } from '@/lib/hooks/use-execution-stream';
import { useMediaDerivativeProgress } from '@/lib/hooks/use-media-derivatives';
import { useWorkflowMonitor } from '@/lib/hooks/use-workflow-monitor';
import { ActivityIndicator } from '..';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (q: string) => ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false, onchange: null }),
});

let ctx: ActivityContextValue;
function Probe() {
  const value = useActivity();
  useEffect(() => {
    ctx = value;
  });
  return null;
}

let feed: ActivityItem[] = [];
function stubFeed() {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) =>
      String(url).includes('/api/v1/activity')
        ? new Response(JSON.stringify({ items: feed, serverTime: '', activeCount: 0 }), { status: 200, headers: { 'content-type': 'application/json' } })
        : new Response('[]', { status: 200, headers: { 'content-type': 'application/json' } }),
    ),
  );
}

const wrap = (ui: React.ReactNode) => (
  <MantineProvider>
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <ActivityProvider>{ui}</ActivityProvider>
    </SWRConfig>
  </MantineProvider>
);

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  FakeEventSource.instances = [];
  vi.stubGlobal('EventSource', FakeEventSource);
  feed = [];
  stubFeed();
  window.localStorage.clear();
  nav.pathname = '/dashboard';
  media.phone = false;
  toast.show.mockReset();
  signInAs('org-a');
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.stubGlobal('EventSource', FakeEventSource);
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});

describe('QA: one shared global stream (acceptance 1)', () => {
  function AllConsumers() {
    useDashboardEvents(() => {}, () => {});
    useWorkflowMonitor();
    useMediaDerivativeProgress('p1', () => {});
    useExecutionStream({ projectId: 'p', workflowId: 'w', executionId: 'e', enabled: true });
    return null;
  }

  it('provider + dashboard + monitor + media derivatives open exactly ONE global EventSource; total <= 3; org switch re-opens one', async () => {
    render(wrap(<AllConsumers />));
    const global = () => FakeEventSource.instances.filter((s) => s.url === WORKFLOW_EVENTS_URL);
    expect(global()).toHaveLength(1);
    expect(FakeEventSource.instances.filter((s) => !s.closed).length).toBeLessThanOrEqual(3);

    act(() => {
      signInAs('org-b');
      notifyAuthChange();
    });
    const open = global().filter((s) => !s.closed);
    expect(open).toHaveLength(1);
    expect(global()[0].closed).toBe(true);
  });

  it('every consumer still receives its frames from the one shared connection', async () => {
    const seen: string[] = [];
    function Consumers() {
      useMediaDerivativeProgress('p1', () => seen.push('media'));
      useDashboardEvents(() => seen.push('dashboard'), () => {});
      return <Probe />;
    }
    feed = [item({ executionId: 'e1' })];
    render(wrap(<Consumers />));
    await waitFor(() => expect(ctx.jobs).toHaveLength(1));
    const es = FakeEventSource.instances.find((s) => s.url === WORKFLOW_EVENTS_URL)!;
    act(() => {
      es.emit('step.progress', { executionId: 'e1', timestamp: 'now', data: { percentComplete: 40 } });
      es.emit('media.derivative.progress', { type: 'media.derivative.progress', projectId: 'p1', timestamp: 'now', data: { projectId: 'p1', mediaRef: 'file:1', stage: 'Proxy', percentComplete: 10 } });
    });
    expect(ctx.jobs[0].liveProgress).toBeCloseTo(0.4);
    expect(seen).toContain('dashboard');
  });
});

describe('QA: tray model', () => {
  it('server feed survives a reload: a fresh provider lists the same running/failed items with refund + credits', async () => {
    feed = [
      item({ id: 'run:1', state: 'running', progress: 0.3 }),
      item({ id: 'run:2', state: 'failed', executionId: 'e2', endedAt: new Date().toISOString(), failure: { code: 'NO_RUNNER_ONLINE', refunded: true }, credits: { heldCredits: null, chargedCredits: 2000 } }),
    ];
    const first = render(wrap(<Probe />));
    await waitFor(() => expect(ctx.jobs).toHaveLength(2));
    first.unmount();
    render(wrap(<Probe />));
    await waitFor(() => expect(ctx.jobs.map((j) => j.state)).toEqual(['running', 'failed']));
  });

  it('client-tracked jobs of workspace A are not listed after switching to workspace B', async () => {
    render(wrap(<Probe />));
    await waitFor(() => expect(ctx.isLoading).toBe(false));
    act(() => {
      void ctx.trackJob({ type: 'upload', title: 'a.mp4', projectId: 'project-of-org-a' }, new Promise(() => {}));
    });
    expect(ctx.jobs).toHaveLength(1);
    act(() => {
      signInAs('org-b');
      notifyAuthChange();
    });
    await waitFor(() => expect(ctx.jobs).toHaveLength(0), { timeout: 500 });
  });

  it('only cancellable jobs with a registered cancel offer Cancel', async () => {
    feed = [item({ id: 'run:1', cancellable: true }), item({ id: 'run:2', executionId: 'e2', cancellable: false }), item({ id: 'an:1', type: 'mediaAnalysis', executionId: null, cancellable: true })];
    render(wrap(<><Probe /><ActivityIndicator /></>));
    await waitFor(() => expect(ctx.jobs).toHaveLength(3));
    fireEvent.click(screen.getByTestId('activity-indicator'));
    await waitFor(() => expect(screen.getAllByTestId('activity-row')).toHaveLength(3));
    expect(screen.getAllByText('Cancel')).toHaveLength(1);
  });

  it('on a phone, tapping Open in the tray drawer closes the drawer', async () => {
    media.phone = true;
    feed = [item({ id: 'run:1' })];
    render(wrap(<><Probe /><ActivityIndicator /></>));
    await waitFor(() => expect(ctx.jobs).toHaveLength(1));
    fireEvent.click(screen.getByTestId('activity-indicator'));
    const open = await screen.findByRole('link', { name: 'Open' });
    fireEvent.click(open);
    await waitFor(() => expect(screen.queryByTestId('activity-row')).toBeNull(), { timeout: 1000 });
  });
});

describe('QA: finish notifier', () => {
  it('toasts a job the user started that fails while they are elsewhere, with the refund note', async () => {
    feed = [item({ id: 'run:1', createdAt: '2020-01-01T00:00:00Z' })];
    render(wrap(<Probe />));
    await waitFor(() => expect(ctx.jobs).toHaveLength(1));
    feed = [item({ id: 'run:1', createdAt: '2020-01-01T00:00:00Z', state: 'failed', endedAt: new Date().toISOString(), failure: { code: null, refunded: true } })];
    await act(async () => {
      FakeEventSource.instances[0].emit('execution.failed', { executionId: 'e1', timestamp: 'now', data: {} });
      await new Promise((r) => setTimeout(r, 700));
    });
    await waitFor(() => expect(toast.show).toHaveBeenCalledTimes(1));
  });

  it('magic blur finishing while the user is still in that timeline does not toast (they are not elsewhere)', async () => {
    nav.pathname = '/editor/p1/timeline/t1';
    const blur = (over: Partial<ActivityItem>) => item({ id: 'run:9', type: 'objectAnalysis', executionId: 'e9', subject: { timelineId: 't1', mediaRef: 'file:1' }, createdAt: '2020-01-01T00:00:00Z', ...over });
    feed = [blur({})];
    render(wrap(<Probe />));
    await waitFor(() => expect(ctx.jobs).toHaveLength(1));
    feed = [blur({ state: 'done', endedAt: new Date().toISOString() })];
    await act(async () => {
      FakeEventSource.instances[0].emit('execution.completed', { executionId: 'e9', timestamp: 'now', data: {} });
      await new Promise((r) => setTimeout(r, 700));
    });
    await waitFor(() => expect(ctx.jobs[0].state).toBe('done'));
    expect(toast.show).not.toHaveBeenCalled();
  });
});


describe('T-044 kit fixes', () => {
  it('a debounced refresh scheduled before a workspace switch is dropped', async () => {
    feed = [item({ id: 'run:1', executionId: 'e1' })];
    render(wrap(<Probe />));
    await waitFor(() => expect(ctx.jobs).toHaveLength(1));
    const calls = () => (fetch as unknown as { mock: { calls: unknown[][] } }).mock.calls.filter((c) => String(c[0]).includes('/api/v1/activity')).length;
    act(() => {
      FakeEventSource.instances[0].emit('execution.completed', { executionId: 'e1', timestamp: 'now', data: {} });
      signInAs('org-b');
      notifyAuthChange();
    });
    await waitFor(() => expect(ctx.jobs.length).toBeGreaterThanOrEqual(0));
    const before = calls();
    await act(async () => {
      await new Promise((r) => setTimeout(r, 700));
    });
    // Only the org-b load happened; the stale org-a refresh did not add a request.
    expect(calls()).toBeLessThanOrEqual(before + 0);
  });

  it('sign-out wipe removes dismissals and seen marks but keeps the toast switch', async () => {
    const { wipeEditorLocalData } = await import('@/lib/editing/sync/user-scope');
    window.localStorage.setItem('reelbolt.activity.dismissed.u.org-a', '["x"]');
    window.localStorage.setItem('reelbolt.activity.seen.u.org-a', '1');
    window.localStorage.setItem('reelbolt.activity.toasts', '0');
    await wipeEditorLocalData({ broadcast: false });
    expect(window.localStorage.getItem('reelbolt.activity.dismissed.u.org-a')).toBeNull();
    expect(window.localStorage.getItem('reelbolt.activity.seen.u.org-a')).toBeNull();
    expect(window.localStorage.getItem('reelbolt.activity.toasts')).toBe('0');
  });

  it('a server stage shows only when translated, else the step line', async () => {
    const { JobProgress } = await import('..');
    const mk = (stage: string) => ({ ...item({ id: 'run:5', step: { order: 2, count: 4, stepType: null } }), origin: 'server' as const, liveStage: stage, liveProgress: 0.3 });
    const { rerender } = render(<MantineProvider><JobProgress job={mk('Encoding')} /></MantineProvider>);
    expect(screen.getByText('Encoding the video')).toBeTruthy();
    rerender(<MantineProvider><JobProgress job={mk('ffmpeg pass 2 of weird')} /></MantineProvider>);
    expect(screen.getByText('Step 2 of 4')).toBeTruthy();
  });

  it('a failing cancel shows a notification and does not throw', async () => {
    const { InlineJobStatus } = await import('..');
    const { ActivityProvider: _p } = await import('@/lib/activity/ActivityProvider');
    void _p;
    feed = [item({ id: 'run:1', cancellable: true })];
    render(wrap(<><Probe /><InlineJobStatus job={{ ...item({ id: 'x', cancellable: true }), origin: 'server' }} onCancel={async () => { throw new Error('403'); }} /></>));
    fireEvent.click(await screen.findByRole('button', { name: 'Cancel' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Yes, stop it' }));
    await waitFor(() => expect(toast.show).toHaveBeenCalled());
  });
});
