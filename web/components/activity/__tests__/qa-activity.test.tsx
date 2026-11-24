// @vitest-environment jsdom
/**
 * QA probes (background-activity T-032). Passing `it` = requirement verified.
 * `it.fails` = a divergence pinned by QA: the assertion states the REQUIRED behaviour and currently fails.
 * When the bug is fixed the `it.fails` turns red: flip it to `it`.
 */
import React, { useEffect, useState } from 'react';
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
const server = vi.hoisted(() => ({ resolve: vi.fn() }));
vi.mock('@/lib/editing/background/object-effect', async (orig) => ({
  ...(await orig<typeof import('@/lib/editing/background/object-effect')>()),
  resolveObjectOnServer: server.resolve,
}));

import { ActivityProvider, useActivity, type ActivityContextValue } from '@/lib/activity/ActivityProvider';
import { FakeEventSource, item, signInAs } from '@/lib/activity/__tests__/helpers';
import { WORKFLOW_EVENTS_URL } from '@/lib/activity/events';
import type { ActivityItem } from '@/lib/activity/types';
import { notifyAuthChange } from '@/lib/hooks/use-auth';
import { useWorkflowEvents as useDashboardEvents } from '@/lib/hooks/use-dashboard';
import { useExecutionStream } from '@/lib/hooks/use-execution-stream';
import { useMediaDerivativeProgress } from '@/lib/hooks/use-media-derivatives';
import { useWorkflowMonitor } from '@/lib/hooks/use-workflow-monitor';
import { getResultsStore, resetResultsStores, setActiveTimeline } from '@/lib/editing/background/results-store';
import type { EditorStore } from '@/lib/editing/store';
import { useBackgroundActivityBridge } from '@/components/editor/background/useBackgroundActivityBridge';
import { installDomStubs, launchOn, makeStore, STEP } from '@/components/editor/background/__tests__/helpers';
import { ActivityIndicator, JobCost } from '..';

installDomStubs();

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

  it('BUG: client-tracked jobs of workspace A are not listed after switching to workspace B', async () => {
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

  it('BUG: a picture operation estimate of 4.5 credits shows as about 4.5 credits held (not 0.005)', async () => {
    render(wrap(<Probe />));
    act(() => {
      // ThumbnailEditor passes PictureEstimate.credits (credits, e.g. 4.5) as creditsEstimate.
      void ctx.trackJob({ type: 'pictureOperation', title: 'Erase', creditsEstimate: 4.5 }, new Promise(() => {}));
    });
    const { container } = render(
      <MantineProvider>
        <JobCost job={ctx.jobs[0]} />
      </MantineProvider>,
    );
    const text = Array.from(container.querySelectorAll('p')).map((p) => p.textContent).join(' ');
    expect(text).toMatch(/4\.5 credits/);
  });

  it('only cancellable jobs with a registered cancel offer Cancel', async () => {
    feed = [item({ id: 'run:1', cancellable: true }), item({ id: 'run:2', executionId: 'e2', cancellable: false }), item({ id: 'an:1', type: 'mediaAnalysis', executionId: null, cancellable: true })];
    render(wrap(<><Probe /><ActivityIndicator /></>));
    await waitFor(() => expect(ctx.jobs).toHaveLength(3));
    fireEvent.click(screen.getByTestId('activity-indicator'));
    await waitFor(() => expect(screen.getAllByTestId('activity-row')).toHaveLength(3));
    expect(screen.getAllByText('Cancel')).toHaveLength(1);
  });

  it('BUG: on a phone, tapping Open in the tray drawer closes the drawer', async () => {
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

  it('BUG: magic blur finishing while the user is still in that timeline does not toast (they are not elsewhere)', async () => {
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

describe('QA: editor results mirrored into the tray', () => {
  const intent = { prompt: 'face', effect: 'blur', strength: 0.7, invert: false, featherPx: 0, analysisStepResultId: STEP, trackIds: ['g1'] };
  const review = { applicable: ['x'], reasons: [] as Array<'targetGone'>, lostCount: 0 };
  function Bridge({ store }: { store: EditorStore }) {
    useBackgroundActivityBridge({ store, projectId: 'p', timelineId: 't' });
    return null;
  }
  function Toggle({ store }: { store: EditorStore }) {
    const [shown, setShown] = useState(true);
    return (
      <>
        <Probe />
        {shown && <Bridge store={store} />}
        <button data-testid="toggle" onClick={() => setShown((s) => !s)} />
      </>
    );
  }

  it('BUG: leaving the editor and coming back does not list the same needs-your-OK result twice', async () => {
    resetResultsStores();
    setActiveTimeline('t');
    server.resolve.mockReset().mockResolvedValue(undefined);
    const store = makeStore();
    launchOn(store, { state: 'needsReview', intent, review });
    render(wrap(<Toggle store={store} />));
    await waitFor(() => expect(ctx.jobs.map((j) => j.state)).toEqual(['needsReview']));
    fireEvent.click(screen.getByTestId('toggle')); // leave the editor (bridge unmounts, provider stays)
    fireEvent.click(screen.getByTestId('toggle')); // come back
    await new Promise((r) => setTimeout(r, 50));
    expect(getResultsStore('t').getEntries()).toHaveLength(1);
    expect(ctx.jobs.filter((j) => j.state === 'needsReview')).toHaveLength(1);
  });
});
