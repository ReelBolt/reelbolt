// @vitest-environment jsdom
// QA probes (T-030, background-activity): tray mirroring and "apply to what is left" against the owner addendum.
import React, { useEffect } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { ActivityProvider, useActivity, type ActivityContextValue } from '@/lib/activity/ActivityProvider';
import { signInAs } from '@/lib/activity/__tests__/helpers';
import { getResultsStore, resetResultsStores, setActiveTimeline } from '@/lib/editing/background/results-store';
import type { EditorStore } from '@/lib/editing/store';
import { useBackgroundActivityBridge } from '../useBackgroundActivityBridge';
import { PendingResults } from '../PendingResults';
import { installDomStubs, launchOn, makeStore, STEP } from './helpers';

vi.mock('next/navigation', () => ({ usePathname: () => '/editor/t', useRouter: () => ({ push: vi.fn() }) }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
const server = vi.hoisted(() => ({ resolve: vi.fn() }));
vi.mock('@/lib/editing/background/object-effect', async (importOriginal) => ({ ...(await importOriginal<typeof import('@/lib/editing/background/object-effect')>()), resolveObjectOnServer: server.resolve }));

let ctx: ActivityContextValue;
function Capture() {
  const value = useActivity();
  useEffect(() => {
    ctx = value;
  });
  return null;
}
function Bridge({ store }: { store: EditorStore }) {
  useBackgroundActivityBridge({ store, projectId: 'p', timelineId: 't' });
  return null;
}
/** The app layout keeps ActivityProvider (and the tray) mounted; the editor (and its bridge) comes and goes. */
const tree = (store: EditorStore, editorOpen: boolean) => (
  <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
    <ActivityProvider>
      <Capture />
      {editorOpen && <Bridge store={store} />}
    </ActivityProvider>
  </SWRConfig>
);
const intent = { prompt: 'face', effect: 'blur', strength: 0.7, invert: false, featherPx: 0, analysisStepResultId: STEP, trackIds: ['g1'] };

beforeEach(() => {
  installDomStubs();
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

describe('QA probes - tray mirroring and needs-your-OK', () => {
  it('rule 4: Apply in the tray after leaving the editor does not mark the result applied on a closed editor', async () => {
    const store = makeStore();
    launchOn(store, { state: 'needsReview', intent, review: { applicable: ['x'], reasons: [], lostCount: 0 } });
    const view = render(tree(store, true));
    await waitFor(() => expect(ctx.jobs.map((j) => j.state)).toEqual(['needsReview']));
    view.rerender(tree(store, false)); // the user left the editor; its store and sync client are gone
    await act(async () => ctx.resolveTrackedJob(ctx.jobs[0].id, 'applied'));
    // Nothing can go through the op/sync path now: the paid result must still be waiting, not "applied" into a dead store.
    expect(getResultsStore('t').getEntries()[0].state).toBe('needsReview');
    expect(server.resolve).not.toHaveBeenCalled();
  });

  it('rule 3: leaving and re-opening the editor shows ONE needs-your-OK per result in the tray', async () => {
    launchOn(makeStore(), { state: 'needsReview', intent, review: { applicable: ['x'], reasons: [], lostCount: 0 } });
    const view = render(tree(makeStore(), true));
    await waitFor(() => expect(ctx.jobs.filter((j) => j.state === 'needsReview')).toHaveLength(1));
    view.rerender(tree(makeStore(), false));
    view.rerender(tree(makeStore(), true)); // a fresh page store, as the editor route creates
    await waitFor(() => expect(ctx.jobs.length).toBeGreaterThan(0));
    await act(async () => undefined);
    expect(ctx.jobs.filter((j) => j.state === 'needsReview')).toHaveLength(1);
  });

  it('rule 3: "Apply to what is left" follows the CURRENT document (the deleted clip was restored by undo)', async () => {
    const store = makeStore(); // clip x is present again
    launchOn(store, { state: 'needsReview', intent, review: { applicable: [], reasons: ['targetGone'], lostCount: 1 } });
    render(
      <MantineProvider>
        <PendingResults store={store} projectId="p" />
      </MantineProvider>,
    );
    fireEvent.click(screen.getByTestId('pending-results-chip'));
    const apply = await screen.findByTestId('pending-apply-r1');
    expect((apply as HTMLButtonElement).disabled).toBe(false);
  });
});
