// @vitest-environment jsdom
import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createEditorStore, type EditorStore } from '@/lib/editing/store';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import type { MediaClip } from '@/lib/editing/types';
import type { EditProposalResponse } from '@/lib/editing/sync/api';
import { clearProposalReview, landProposal, proposalLaunchEntry, useProposalReviewRequest } from '@/lib/editing/background/proposal';
import { getResultsStore, resetResultsStores, setActiveTimeline } from '@/lib/editing/background/results-store';
import { ProposalPanel } from '@/components/editor/panels/ProposalPanel';
import { PendingResults } from '../PendingResults';
import { useBusyEditGuard } from '../useBusyEditGuard';
import { installDomStubs } from './helpers';

vi.mock('@/lib/editing/engine', async (importOriginal) => {
  const engine = await importOriginal<typeof import('@/lib/editing/engine')>();
  return { ...engine, assertInvariants: (d: Parameters<typeof engine.assertInvariants>[0]) => engine.assertInvariants(d, { ids: 'opaque' }) };
});
const versionCalls = vi.hoisted(() => vi.fn().mockResolvedValue({}));
vi.mock('@/lib/editing/sync/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/sync/api')>()),
  createEditTimelineVersion: (...args: unknown[]) => versionCalls(...args),
}));

const generated = (id: string, recordIn: number, recordOut: number): MediaClip => ({ ...media(id, recordIn, recordOut, 0), provenance: { origin: 'generated', editedByUser: false, agentName: 'VideoStoryEditor' } });
const makeStore = (): EditorStore => createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [generated('g1', 0, 60), generated('g2', 60, 120)])]), revision: 1 });
const trimGroup = (itemId: string) => ({ label: `Trim ${itemId}`, ops: [{ type: 'trim', itemId, edge: 'out', mode: 'ripple', deltaFrames: -15 }], touchedItemIds: [itemId], touchesUserOwned: false, reason: null }) as never;
const response: EditProposalResponse = { proposalId: 'pr', agent: 'recut', summary: 's', rejected: [], groups: [trimGroup('g1'), trimGroup('g2')] };
const noQuote = async () => {
  throw new Error('no quote');
};
const noHelpers = async () => ({ agents: {}, analysisAvailable: false });

function Probe({ onRequest }: { onRequest: (id: string | null) => void }) {
  onRequest(useProposalReviewRequest());
  return null;
}
function Guard({ store }: { store: EditorStore }) {
  return <>{useBusyEditGuard({ store, projectId: 'p', timelineId: 't', onRefusal: () => undefined })}</>;
}
const wrap = (ui: React.ReactNode) => <MantineProvider>{ui}</MantineProvider>;

beforeEach(() => {
  installDomStubs();
  window.localStorage.clear();
  resetResultsStores();
  clearProposalReview();
  setActiveTimeline('t');
  versionCalls.mockClear();
});
afterEach(cleanup);

describe('AI proposals follow the user\'s edits', () => {
  it('asks before a trim of a clip an in-flight proposal covers, with the suggestions wording', async () => {
    const store = makeStore();
    getResultsStore('t').launch(proposalLaunchEntry(store.getState().doc, { agent: 'recut', brief: null, range: { inFrame: 0, outFrame: 40 } }, 'p1', '2026-01-01T00:00:00Z'));
    render(wrap(<Guard store={store} />));
    const before = store.getState().cursor;
    act(() => store.dispatch({ type: 'trim', itemId: 'g1', edge: 'out', newFrame: 30, mode: 'ripple' } as never));
    expect(await screen.findByTestId('busy-edit-modal')).toBeTruthy();
    expect(screen.getByText(/AI suggestions are being prepared for this part/)).toBeTruthy();
    expect(store.getState().cursor).toBe(before);
    // the other clip is not covered by the range
    fireEvent.click(screen.getByTestId('busy-back'));
    act(() => store.dispatch({ type: 'trim', itemId: 'g2', edge: 'out', deltaFrames: -10, mode: 'ripple' } as never));
    expect(store.getState().cursor).toBe(before + 1);
  });

  it('keeps an answer that lands after the panel is gone, and Review loads it back into the panel', async () => {
    const store = makeStore();
    let resolveAnswer: (r: EditProposalResponse) => void = () => undefined;
    const request = () => new Promise<EditProposalResponse>((resolve) => (resolveAnswer = resolve));
    const panel = (
      <ProposalPanel doc={store.getState().doc} projectId="p" timelineId="t" readOnly={false} inOut={{ inFrame: null, outFrame: null }} onGhosts={() => undefined} onApply={(ops, label) => store.dispatchBatch(ops, label)} onSeek={() => undefined} request={request} estimate={noQuote} availability={noHelpers as never} />
    );
    const view = render(wrap(panel));
    fireEvent.click(screen.getByTestId('proposal-ask'));
    await waitFor(() => expect(getResultsStore('t').busyClipIds(store.getState().doc).size).toBe(2));
    view.unmount();
    await act(async () => resolveAnswer(response));
    const entry = getResultsStore('t').getEntries()[0];
    expect(entry.state).toBe('needsReview');
    expect((entry.intent as { response?: EditProposalResponse }).response?.groups).toHaveLength(2);

    let requested: string | null = null;
    render(wrap(<PendingResults store={store} projectId="p" />));
    fireEvent.click(screen.getByTestId('pending-results-chip'));
    expect(await screen.findByText('Suggestions ready')).toBeTruthy();
    render(wrap(<Probe onRequest={(id) => (requested = id)} />));
    fireEvent.click(screen.getByTestId(`pending-review-${entry.id}`));
    await waitFor(() => expect(requested).toBe(entry.id));
    cleanup();
    render(wrap(<ProposalPanel doc={store.getState().doc} projectId="p" timelineId="t" readOnly={false} inOut={{ inFrame: null, outFrame: null }} onGhosts={() => undefined} onApply={() => undefined} onSeek={() => undefined} request={request} estimate={noQuote} availability={noHelpers as never} />));
    expect(await screen.findByTestId('proposal-group-1')).toBeTruthy();
  });

  it('partly stale: "Apply what still fits" applies the valid group in one undo step, with the version taken first', async () => {
    const store = makeStore();
    const results = getResultsStore('t');
    results.launch(proposalLaunchEntry(store.getState().doc, { agent: 'recut', brief: null, range: null }, 'p1', '2026-01-01T00:00:00Z'));
    landProposal(results, 'p1', response);
    act(() => store.dispatch({ type: 'lift', itemId: 'g2' } as never));
    render(wrap(<PendingResults store={store} projectId="p" />));
    fireEvent.click(screen.getByTestId('pending-results-chip'));
    expect(await screen.findByText('Some suggestions no longer fit your edit')).toBeTruthy();
    const before = store.getState().cursor;
    fireEvent.click(screen.getByTestId('pending-apply-fits-p1'));
    await waitFor(() => expect(store.getState().cursor).toBe(before + 1));
    expect(versionCalls).toHaveBeenCalledWith('p', 't', { cause: 'beforeProposal' });
    expect(results.getEntries()[0].state).toBe('applied');
  });

  it('fully stale: only Discard is offered', async () => {
    const store = makeStore();
    const results = getResultsStore('t');
    results.launch(proposalLaunchEntry(store.getState().doc, { agent: 'recut', brief: null, range: null }, 'p1', '2026-01-01T00:00:00Z'));
    landProposal(results, 'p1', response);
    act(() => {
      store.dispatch({ type: 'lift', itemId: 'g1' } as never);
      store.dispatch({ type: 'lift', itemId: 'g2' } as never);
    });
    render(wrap(<PendingResults store={store} projectId="p" />));
    fireEvent.click(screen.getByTestId('pending-results-chip'));
    expect(await screen.findByText('These suggestions no longer fit your edit')).toBeTruthy();
    expect(screen.queryByTestId('pending-apply-fits-p1')).toBeNull();
    expect(screen.queryByTestId('pending-review-p1')).toBeNull();
    fireEvent.click(screen.getByTestId('pending-discard-p1'));
    expect(results.getEntries()[0].state).toBe('discarded');
  });
});
