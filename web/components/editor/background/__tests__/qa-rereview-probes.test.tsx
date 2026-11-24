// @vitest-environment jsdom
// QA re-review probes (T-050, background-activity, delta 35c3439c4..403910d68). A failing case here is a finding.
import React, { useEffect } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, waitFor } from '@testing-library/react';
import { SWRConfig } from 'swr';
import { ActivityProvider, useActivity, type ActivityContextValue } from '@/lib/activity/ActivityProvider';
import { signInAs } from '@/lib/activity/__tests__/helpers';
import { getResultsStore, resetResultsStores, setActiveTimeline } from '@/lib/editing/background/results-store';
import { clearProposalReview, landProposal, peekProposalReview, proposalLaunchEntry } from '@/lib/editing/background/proposal';
import type { EditProposalResponse } from '@/lib/editing/sync/api';
import type { EditorStore } from '@/lib/editing/store';
import { useBackgroundActivityBridge } from '../useBackgroundActivityBridge';
import { installDomStubs, makeStore } from './helpers';

vi.mock('next/navigation', () => ({ usePathname: () => '/editor/t', useRouter: () => ({ push: vi.fn() }) }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));
vi.mock('@/lib/editing/sync/api', async (importOriginal) => ({ ...(await importOriginal<typeof import('@/lib/editing/sync/api')>()), createEditTimelineVersion: vi.fn(async () => ({})) }));

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
const tree = (store: EditorStore) => (
  <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
    <ActivityProvider>
      <Capture />
      <Bridge store={store} />
    </ActivityProvider>
  </SWRConfig>
);

beforeEach(() => {
  installDomStubs();
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ items: [], serverTime: '', activeCount: 0 }), { status: 200 })));
  window.localStorage.clear();
  signInAs();
  resetResultsStores();
  setActiveTimeline('t');
  clearProposalReview();
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('QA re-review probes - tray Apply on a proposal', () => {
  it('tray Apply on a proposal nothing changed since launch opens Review (as the in-editor list does), it is not a dead button', async () => {
    const store = makeStore();
    const results = getResultsStore('t');
    results.launch(proposalLaunchEntry(store.getState().doc, { agent: 'recut', brief: null, range: null }, 'p1', '2026-01-01T00:00:00Z'));
    const trim = { label: 'Trim x', ops: [{ type: 'trim', itemId: 'x', edge: 'out', mode: 'ripple', deltaFrames: -15 }], touchedItemIds: ['x'], touchesUserOwned: true, reason: null };
    const response: EditProposalResponse = { proposalId: 'pr', agent: 'recut', summary: 's', rejected: [], groups: [trim as never] };
    landProposal(results, 'p1', response);
    render(tree(store));
    await waitFor(() => expect(ctx.jobs.filter((j) => j.state === 'needsReview')).toHaveLength(1));
    await act(async () => ctx.resolveTrackedJob(ctx.jobs.find((j) => j.state === 'needsReview')!.id, 'applied').catch(() => undefined));
    // The doc is untouched since launch: reviewKind is 'ready' with the launch placements, so Apply must open Review.
    expect(peekProposalReview()).toBe('p1');
  });
});
