// @vitest-environment jsdom
import React, { useEffect } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, waitFor } from '@testing-library/react';
import { SWRConfig } from 'swr';
import { ActivityProvider, useActivity, type ActivityContextValue } from '@/lib/activity/ActivityProvider';
import { signInAs } from '@/lib/activity/__tests__/helpers';
import { getResultsStore, resetResultsStores, setActiveTimeline } from '@/lib/editing/background/results-store';
import type { EditorStore } from '@/lib/editing/store';
import { useBackgroundActivityBridge } from '../useBackgroundActivityBridge';
import { clipOf, launchOn, makeStore, STEP } from './helpers';

vi.mock('next/navigation', () => ({ usePathname: () => '/editor/t', useRouter: () => ({ push: vi.fn() }) }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
const server = vi.hoisted(() => ({ resolve: vi.fn() }));
vi.mock('@/lib/editing/background/object-effect', async (importOriginal) => ({ ...(await importOriginal<typeof import('@/lib/editing/background/object-effect')>()), resolveObjectOnServer: server.resolve }));

let ctx: ActivityContextValue;
function Mount({ store }: { store: EditorStore }) {
  useBackgroundActivityBridge({ store, projectId: 'p', timelineId: 't' });
  const value = useActivity();
  useEffect(() => {
    ctx = value;
  });
  return null;
}
const mount = (store: EditorStore) =>
  render(
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <ActivityProvider>
        <Mount store={store} />
      </ActivityProvider>
    </SWRConfig>,
  );
const intent = { prompt: 'face', effect: 'blur', strength: 0.7, invert: false, featherPx: 0, analysisStepResultId: STEP, trackIds: ['g1'] };
const review = { applicable: ['x'], reasons: [] as Array<'targetGone'>, lostCount: 0 };

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ items: [], serverTime: '', activeCount: 0 }), { status: 200 })));
  window.localStorage.clear();
  signInAs();
  resetResultsStores();
  setActiveTimeline('t');
  server.resolve.mockReset().mockResolvedValue(undefined);
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('background activity bridge', () => {
  it('mirrors a running entry, then needs-your-OK, and Apply in the tray applies it to what is left', async () => {
    const store = makeStore();
    launchOn(store, { intent });
    mount(store);
    await waitFor(() => expect(ctx.jobs.map((j) => j.state)).toEqual(['running']));
    act(() => getResultsStore('t').update('r1', { state: 'needsReview', review }));
    await waitFor(() => expect(ctx.jobs.map((j) => j.state)).toEqual(['needsReview']));
    await act(async () => ctx.resolveTrackedJob(ctx.jobs[0].id, 'applied'));
    expect(getResultsStore('t').getEntries()[0].state).toBe('applied');
    expect(clipOf(store, 'x').regionEffects).toHaveLength(1);
    expect(ctx.jobs[0].state).toBe('done');
    expect(server.resolve).toHaveBeenCalledWith('p', expect.any(String), 'r1', 'applied', 1);
  });
  it('Discard in the tray discards the entry; a failed entry ends its job as failed', async () => {
    const store = makeStore();
    launchOn(store, { state: 'needsReview', intent, review });
    launchOn(store, { id: 'r2', requestId: 'r2', executionId: 'e2' });
    mount(store);
    await waitFor(() => expect(ctx.jobs).toHaveLength(2));
    act(() => getResultsStore('t').update('r2', { state: 'failed', failure: { code: null, message: 'no' } }));
    await waitFor(() => expect(ctx.allJobs.map((j) => j.state).sort()).toEqual(['failed', 'needsReview']));
    const reviewJob = ctx.jobs.find((j) => j.state === 'needsReview');
    await act(async () => ctx.resolveTrackedJob(reviewJob?.id ?? '', 'discarded'));
    expect(getResultsStore('t').getEntries()[0].state).toBe('discarded');
    expect(clipOf(store, 'x').regionEffects ?? null).toBeNull();
  });
  it('an entry resolved in the editor ends its job without running the tray callbacks twice', async () => {
    const store = makeStore();
    launchOn(store, { state: 'needsReview', intent, review });
    mount(store);
    await waitFor(() => expect(ctx.jobs.map((j) => j.state)).toEqual(['needsReview']));
    act(() => getResultsStore('t').resolve('r1', 'discarded'));
    await waitFor(() => expect(ctx.jobs.map((j) => j.state)).toEqual(['cancelled']));
    expect(server.resolve).not.toHaveBeenCalled();
  });
  it('Apply that cannot apply keeps the tray job in needsReview (unknown tool, no object calls)', async () => {
    const store = makeStore();
    launchOn(store, { tool: 'mystery', state: 'needsReview', intent, review });
    mount(store);
    await waitFor(() => expect(ctx.jobs.map((j) => [j.type, j.state])).toEqual([['generic', 'needsReview']]));
    await act(async () => ctx.resolveTrackedJob(ctx.jobs[0].id, 'applied'));
    expect(ctx.jobs[0].state).toBe('needsReview');
    expect(getResultsStore('t').getEntries()[0].state).toBe('needsReview');
    expect(server.resolve).not.toHaveBeenCalled();
  });
});
