// @vitest-environment jsdom
import React, { useEffect } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, waitFor } from '@testing-library/react';
import { SWRConfig } from 'swr';
import { ActivityProvider, useActivity, type ActivityContextValue } from '@/lib/activity/ActivityProvider';
import { signInAs } from '@/lib/activity/__tests__/helpers';
import { jobLabel } from '@/lib/activity/notifier';
import { resetResultsStores, setActiveTimeline } from '@/lib/editing/background/results-store';
import type { EditorStore } from '@/lib/editing/store';
import { AppRouterContext } from 'next/dist/shared/lib/app-router-context.shared-runtime';
import { useBackgroundActivityBridge } from '../useBackgroundActivityBridge';
import { launchOn, makeStore } from './helpers';

const nav = vi.hoisted(() => ({ push: vi.fn() }));
vi.mock('next/navigation', () => ({ usePathname: () => '/x', useRouter: () => nav }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));

let ctx: ActivityContextValue;
function Capture() {
  const v = useActivity();
  useEffect(() => {
    ctx = v;
  });
  return null;
}
function Bridge({ store }: { store: EditorStore }) {
  useBackgroundActivityBridge({ store, projectId: 'p', timelineId: 't' });
  return null;
}
const tree = (store: EditorStore, open: boolean) => (
  // The bridge reads the router from the context (it must not throw where no app router is mounted).
  <AppRouterContext.Provider value={nav as unknown as React.ContextType<typeof AppRouterContext>}>
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <ActivityProvider>
        <Capture />
        {open && <Bridge store={store} />}
      </ActivityProvider>
    </SWRConfig>
  </AppRouterContext.Provider>
);
const intent = { prompt: 'secret face', effect: 'blur', strength: 0.7, invert: false, featherPx: 0 };

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ items: [], serverTime: '', activeCount: 0 }), { status: 200 })));
  window.localStorage.clear();
  signInAs();
  resetResultsStores();
  setActiveTimeline('t');
  nav.push.mockReset();
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('bridge: leaving and reopening the editor', () => {
  it('after leaving, Apply opens the editor instead of applying; reopening keeps ONE row with the same id', async () => {
    const store = makeStore();
    launchOn(store, { state: 'needsReview', intent, review: { applicable: ['x'], reasons: [], lostCount: 0 } });
    const view = render(tree(store, true));
    await waitFor(() => expect(ctx.jobs.map((j) => j.state)).toEqual(['needsReview']));
    const id = ctx.jobs[0].id;
    expect(id).toBe('bg:t:r1');
    view.rerender(tree(store, false));
    await waitFor(() => expect(ctx.jobs[0].review?.summaryKey).toBe('editor.openToDecide'));
    await act(async () => ctx.resolveTrackedJob(id, 'applied'));
    expect(nav.push).toHaveBeenCalledWith('/editor/p/timeline/t');
    expect(ctx.jobs[0].state).toBe('needsReview');
    view.rerender(tree(makeStore(), true));
    await act(async () => undefined);
    expect(ctx.allJobs.map((j) => [j.id, j.state])).toEqual([[id, 'needsReview']]);
    expect(ctx.jobs[0].review?.summaryKey).toBe('editor.needsReview');
  });
  it('the prompt stays in the tray row title and out of the notification label', async () => {
    const store = makeStore();
    launchOn(store, { intent });
    render(tree(store, true));
    await waitFor(() => expect(ctx.jobs).toHaveLength(1));
    expect(ctx.jobs[0].title).toContain('secret face');
    expect(jobLabel(ctx.jobs[0], Object.assign((k: string) => k, { rich: vi.fn(), has: vi.fn(), raw: vi.fn(), msg: vi.fn() }) as unknown as Parameters<typeof jobLabel>[1])).not.toContain('secret face');
  });
});
