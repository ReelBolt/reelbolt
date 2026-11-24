// @vitest-environment jsdom
/** QA probes (T-051, PR #964): PendingResults + bridge audioCleanup arms. */
import React, { useEffect } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import { AppRouterContext } from 'next/dist/shared/lib/app-router-context.shared-runtime';
import { ActivityProvider, useActivity, type ActivityContextValue } from '@/lib/activity/ActivityProvider';
import { signInAs } from '@/lib/activity/__tests__/helpers';
import { createEditorStore, type EditorStore } from '@/lib/editing/store';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import type { MediaClip } from '@/lib/editing/types';
import { entryForCleanup, landCleanupResult } from '@/lib/editing/background/audio-cleanup';
import { getResultsStore, resetResultsStores, setActiveTimeline } from '@/lib/editing/background/results-store';
import { PendingResults } from '../PendingResults';
import { useBackgroundActivityBridge } from '../useBackgroundActivityBridge';
import { installDomStubs } from './helpers';

const nav = vi.hoisted(() => ({ push: vi.fn() }));
vi.mock('next/navigation', () => ({ usePathname: () => '/x', useRouter: () => nav }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));

const OUT = '00000000-0000-4000-8000-0000000000bb';
const make = (extra: Partial<MediaClip> = {}) => createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [media('x', 0, 60, 0, extra)])]), revision: 1 });
const clipX = (s: EditorStore) => s.getState().doc.tracks.flatMap((t) => t.items).find((i) => i.id === 'x') as MediaClip;
function landReady(s: EditorStore) {
  const results = getResultsStore('t');
  const entry = entryForCleanup({ doc: s.getState().doc, clipId: 'x', executionId: 'e1', outputKind: 'VideoWithCleanAudio', now: () => 'now' });
  if (!entry) throw new Error('no entry');
  results.launch(entry);
  landCleanupResult({ results, editor: s, entry, job: { executionId: 'e1', state: 'ready', target: 'Hum', applied: true, outputFileId: OUT } });
  return results;
}

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
  <AppRouterContext.Provider value={nav as unknown as React.ContextType<typeof AppRouterContext>}>
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <ActivityProvider>
        <MantineProvider>
          <Capture />
          {open && <Bridge store={store} />}
          {open && <PendingResults store={store} projectId="p" />}
        </MantineProvider>
      </ActivityProvider>
    </SWRConfig>
  </AppRouterContext.Provider>
);

beforeEach(() => {
  installDomStubs();
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

describe('QA T-051: bridge audioCleanup arm', () => {
  it('ready cleanup mirrors as an audioCleanup tray row; tray Apply = one undo step; reopen keeps one row', async () => {
    const s = make();
    landReady(s);
    const view = render(tree(s, true));
    await waitFor(() => expect(ctx.jobs.map((j) => j.state)).toEqual(['needsReview']));
    expect(ctx.jobs[0].type).toBe('audioCleanup');
    expect(ctx.jobs[0].review?.summaryKey).toBe('editor.audioCleanupReady');
    view.rerender(tree(s, false));
    view.rerender(tree(s, true));
    await act(async () => undefined);
    expect(ctx.allJobs.filter((j) => j.type === 'audioCleanup')).toHaveLength(1);
    const cursor = s.getState().cursor;
    await act(async () => ctx.resolveTrackedJob(ctx.jobs[0].id, 'applied'));
    expect(s.getState().cursor).toBe(cursor + 1);
    expect(clipX(s).assetRef.projectFileId).toBe(OUT);
    expect(getResultsStore('t').getEntries()[0].state).toBe('applied');
  });
  it('a held batch from the tray keeps the row waiting and the entry needsReview', async () => {
    const s = make();
    landReady(s);
    render(tree(s, true));
    await waitFor(() => expect(ctx.jobs.map((j) => j.state)).toEqual(['needsReview']));
    s.setEditGuard(() => false);
    await act(async () => {
      await ctx.resolveTrackedJob(ctx.jobs[0].id, 'applied').catch(() => undefined);
    });
    expect(getResultsStore('t').getEntries()[0].state).toBe('needsReview');
    expect(ctx.jobs[0].state).toBe('needsReview');
    expect(clipX(s).assetRef.projectFileId).not.toBe(OUT);
  });
  it('Apply from PendingResults resolves the tray row too', async () => {
    const s = make();
    landReady(s);
    render(tree(s, true));
    await waitFor(() => expect(ctx.jobs.map((j) => j.state)).toEqual(['needsReview']));
    fireEvent.click(screen.getByTestId('pending-results-chip'));
    fireEvent.click(await screen.findByTestId('pending-apply-cleanup:e1'));
    await waitFor(() => expect(ctx.allJobs.find((j) => j.type === 'audioCleanup')?.state).toBe('done'));
  });
});

describe('QA T-051: PendingResults audioCleanup arm', () => {
  it('a clip at the region-effect limit can still take the cleaned audio (limit is a magic-blur rule)', async () => {
    const s = make({ regionEffects: [{}, {}, {}, {}] as never });
    landReady(s);
    render(tree(s, true));
    fireEvent.click(await screen.findByTestId('pending-results-chip'));
    const apply = (await screen.findByTestId('pending-apply-cleanup:e1')) as HTMLButtonElement;
    expect(screen.queryByTestId('pending-reason-limitReached')).toBeNull();
    expect(apply.disabled).toBe(false);
  });
});
