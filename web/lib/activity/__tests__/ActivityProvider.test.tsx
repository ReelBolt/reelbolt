// @vitest-environment jsdom
import { act, cleanup, render, waitFor } from '@testing-library/react';
import { SWRConfig } from 'swr';
import { useEffect, type ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('next/navigation', () => ({ usePathname: () => '/dashboard', useRouter: () => ({ push: vi.fn() }) }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
vi.mock('@/i18n', () => ({ useT: () => Object.assign((k: string) => k, { has: () => true }) }));

import { ActivityProvider, useActivity, type ActivityContextValue } from '../ActivityProvider';
import { FakeEventSource, item, signInAs } from './helpers';

let ctx: ActivityContextValue;
function Probe() {
  const value = useActivity();
  useEffect(() => {
    ctx = value;
  });
  return null;
}
const wrap = (children: ReactNode) => (
  <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
    <ActivityProvider>{children}</ActivityProvider>
  </SWRConfig>
);

function mockFetch(handler: () => Response) {
  vi.stubGlobal('fetch', vi.fn(async () => handler()));
}
const ok = (items: unknown[]) => () =>
  new Response(JSON.stringify({ items, serverTime: new Date().toISOString(), activeCount: items.length }), { status: 200 });

beforeEach(() => {
  FakeEventSource.instances = [];
  vi.stubGlobal('EventSource', FakeEventSource);
  window.localStorage.clear();
  signInAs();
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('ActivityProvider', () => {
  it('lists server jobs (running first) and merges live step progress', async () => {
    const done = item({ id: 'run:2', state: 'done', executionId: 'e2', endedAt: new Date().toISOString() });
    mockFetch(ok([done, item()]));
    render(wrap(<Probe />));
    await waitFor(() => expect(ctx.jobs).toHaveLength(2));
    expect(ctx.jobs.map((j) => j.id)).toEqual(['run:1', 'run:2']);
    expect(ctx.activeCount).toBe(1);
    act(() => {
      FakeEventSource.instances[0].emit('step.progress', {
        executionId: 'e1',
        timestamp: 'now',
        data: { percentComplete: 55, stage: 'Encoding' },
      });
    });
    expect(ctx.jobs[0].liveProgress).toBeCloseTo(0.55);
    expect(ctx.jobs[0].liveStage).toBe('Encoding');
  });

  it('turns a 404 into an empty list', async () => {
    mockFetch(() => new Response('nope', { status: 404 }));
    render(wrap(<Probe />));
    await waitFor(() => expect(ctx.isLoading).toBe(false));
    expect(ctx.jobs).toEqual([]);
    expect(ctx.activeCount).toBe(0);
  });

  it('tracks a client job from running to done, and to failed', async () => {
    mockFetch(ok([]));
    render(wrap(<Probe />));
    let resolve!: (v: string) => void;
    let promise!: Promise<string>;
    act(() => {
      promise = ctx.trackJob<string>({ type: 'proposal', title: 'Ideas', creditsEstimate: 3 }, new Promise<string>((r) => (resolve = r)));
    });
    expect(ctx.jobs[0]).toMatchObject({ origin: 'client', state: 'running', type: 'proposal' });
    expect(ctx.activeCount).toBe(1);
    await act(async () => {
      resolve('ok');
      await promise;
    });
    expect(ctx.jobs[0].state).toBe('done');

    let failing!: Promise<unknown>;
    act(() => {
      failing = ctx.trackJob({ type: 'pictureOperation' }, () => Promise.reject(new Error('x')));
    });
    await act(async () => {
      await failing.catch(() => undefined);
    });
    expect(ctx.jobs.find((j) => j.type === 'pictureOperation')?.state).toBe('failed');
  });

  it('parks a needsReview job until resolved, and refuses to dismiss it', async () => {
    mockFetch(ok([]));
    render(wrap(<Probe />));
    const onApply = vi.fn();
    let id = '';
    act(() => {
      void ctx.trackJob({ type: 'proposal' }, (_s, jobId) => {
        id = jobId;
        return new Promise<void>(() => {});
      });
    });
    act(() => ctx.markNeedsReview(id, { summaryKey: 'x', onApply }));
    expect(ctx.jobs[0].state).toBe('needsReview');
    act(() => ctx.dismiss(id));
    expect(ctx.jobs).toHaveLength(1);
    await act(async () => ctx.resolveTrackedJob(id, 'applied'));
    expect(onApply).toHaveBeenCalled();
    expect(ctx.jobs[0].state).toBe('done');
  });

  it('dismisses finished jobs persistently and refuses to dismiss active ones', async () => {
    mockFetch(ok([item(), item({ id: 'run:2', state: 'failed', executionId: 'e2', endedAt: new Date().toISOString() })]));
    const first = render(wrap(<Probe />));
    await waitFor(() => expect(ctx.jobs).toHaveLength(2));
    act(() => ctx.dismiss('run:1'));
    expect(ctx.jobs).toHaveLength(2);
    act(() => ctx.dismiss('run:2'));
    expect(ctx.jobs.map((j) => j.id)).toEqual(['run:1']);
    expect(JSON.parse(window.localStorage.getItem(Object.keys(window.localStorage).find((k) => k.startsWith('reelbolt.activity.dismissed.') && k.endsWith('.org-personal')) ?? '') ?? '[]')).toEqual(['run:2']);
    first.unmount();
    render(wrap(<Probe />));
    await waitFor(() => expect(ctx.allJobs).toHaveLength(2));
    expect(ctx.jobs.map((j) => j.id)).toEqual(['run:1']);
  });
  it('lists one run once: the server item wins while running, the client job wins while it needs review', async () => {
    mockFetch(ok([item({ id: 'run:1', executionId: 'ex1' })]));
    render(wrap(<Probe />));
    await waitFor(() => expect(ctx.jobs).toHaveLength(1));
    let id = '';
    act(() => {
      void ctx.trackJob({ type: 'objectAnalysis', executionId: 'ex1' }, (_s, jobId) => {
        id = jobId;
        return new Promise<void>(() => undefined);
      });
    });
    expect(ctx.jobs.map((j) => j.id)).toEqual(['run:1']);
    act(() => ctx.markNeedsReview(id, { summaryKey: 'editor.needsReview' }));
    expect(ctx.jobs.map((j) => [j.id, j.state])).toEqual([[id, 'needsReview']]);
  });
});
