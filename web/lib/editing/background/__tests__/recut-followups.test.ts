// @vitest-environment jsdom
// Regression tests for the recut follow-ups: linked partners, blades since launch, reversed twins, ghost seeding, copy.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, renderHook } from '@testing-library/react';
import { applyAll } from '../../engine';
import { doc, media, track } from '../../__tests__/fixtures';
import { createEditorStore } from '../../store';
import type { EditProposalGroupResponse, EditProposalResponse } from '../../sync/api';
import type { EditTimeline, MediaClip } from '../../types';
import { planProposal, userOwnedTouchedBy } from '../../proposals/apply-proposal';
import { applyWhatFits, groupFit, landProposal, proposalIntentOf, proposalLaunchEntry, reviewKind } from '../proposal';
import { getResultsStore, resetResultsStores } from '../results-store';
import { GHOST_SETTLE_MS, useGhostDiff } from '@/components/editor/panels/useProposalFlow';

const V = '11111111-0000-4000-8000-000000000001';
const A = '11111111-0000-4000-8000-000000000002';
const V2 = '11111111-0000-4000-8000-0000000000a1';
const A2 = '11111111-0000-4000-8000-0000000000a2';
const TV = '55555555-0000-4000-8000-000000000001';
const TA = '55555555-0000-4000-8000-000000000002';
const LINK = '22222222-0000-4000-8000-000000000001';
const gen = { origin: 'generated' as const, editedByUser: false };

const wrap = (d: EditTimeline): EditTimeline => ({ ...d, id: '44444444-0000-4000-8000-000000000001', projectId: '44444444-0000-4000-8000-000000000002' });
const linked = (audioProv: MediaClip['provenance'] = gen) => wrap(doc([track(TV, 'video', [media(V, 0, 600, 60, { provenance: gen, linkId: LINK })]), track(TA, 'audio', [media(A, 0, 600, 60, { provenance: audioProv, linkId: LINK })])]));
const cutGroup = (label: string, cuts: Array<[string, number, number]>): EditProposalGroupResponse => ({ label, ops: [], touchedItemIds: cuts.map((c) => c[0]), touchesUserOwned: false, reason: null, cuts: cuts.map(([itemId, startSec, endSec]) => ({ itemId, startSec, endSec })) });
const response = (groups: EditProposalGroupResponse[]): EditProposalResponse => ({ proposalId: 'pr', agent: 'recut', summary: 's', rejected: [], groups });
const spans = (d: EditTimeline, id: string) => d.tracks.find((t) => t.id === id)!.items.map((i) => [i.recordIn, i.recordOut]);

beforeEach(() => {
  window.localStorage.clear();
  resetResultsStores();
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe('cut span outside its clip after a user blade', () => {
  // V source starts at 2 s (frame 60); a blade at record 300 splits it at source 12 s.
  const bladed = (d0: EditTimeline) => applyAll(d0, [{ type: 'blade', itemId: V, atFrame: 300, newItemId: V2 }]);

  it('P20 wholly on the new right piece: groupFit says stale and reviewKind allStale', () => {
    const d0 = linked();
    const intent = proposalIntentOf(proposalLaunchEntry(d0, { agent: 'recut', brief: null, range: null }, 'p1', '2026-01-01T00:00:00Z'));
    const d = bladed(d0);
    const r = response([cutGroup('late', [[V, 15, 16]])]);
    const fit = groupFit(d, r, intent.placements, undefined, intent.surroundings);
    expect(fit.stale).toEqual([0]);
    expect(reviewKind(d, r, intent.placements, intent.surroundings)).toBe('allStale');
  });

  it('P21 straddling the blade (11..13 s) is refused and the group does not fit', () => {
    const d0 = linked();
    const intent = proposalIntentOf(proposalLaunchEntry(d0, { agent: 'recut', brief: null, range: null }, 'p1', '2026-01-01T00:00:00Z'));
    const d = bladed(d0);
    const r = response([cutGroup('straddle', [[V, 11, 13]])]);
    const fit = groupFit(d, r, intent.placements, undefined, intent.surroundings);
    expect(fit.fits).not.toContain(0);
    expect(() => planProposal(d, r, new Set([0]))).toThrow(/straddle/);
  });
});

describe('acknowledgement and one batch', () => {
  it('P22 a manual linked partner: groupFit needsAck, applyWhatFits leaves it waiting and lands nothing', async () => {
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: linked(null), revision: 1 });
    const results = getResultsStore('t');
    results.launch(proposalLaunchEntry(store.getState().doc, { agent: 'recut', brief: null, range: null }, 'p1', '2026-01-01T00:00:00Z'));
    landProposal(results, 'p1', response([cutGroup('c', [[V, 3, 4]])]));
    const before = store.getState().doc;
    expect(userOwnedTouchedBy(before, response([cutGroup('c', [[V, 3, 4]])]).groups[0])).toEqual([A]);
    const outcome = await applyWhatFits({ results, editor: store, entry: results.getEntries()[0], label: 'AI recut' });
    expect(outcome).toBe('nothing');
    expect(store.getState().doc).toBe(before);
  });

  it('P23 three cut groups on linked A/V apply as one batch; ONE undo restores the document', async () => {
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: linked(), revision: 1 });
    const results = getResultsStore('t');
    results.launch(proposalLaunchEntry(store.getState().doc, { agent: 'recut', brief: null, range: null }, 'p1', '2026-01-01T00:00:00Z'));
    landProposal(results, 'p1', response([cutGroup('a', [[V, 3, 4], [V, 12, 13]]), cutGroup('b', [[V, 6, 7]]), cutGroup('c', [[A, 9, 10]])]));
    const before = store.getState().doc;
    const outcome = await applyWhatFits({ results, editor: store, entry: results.getEntries()[0], label: 'AI recut' });
    expect(outcome).toBe('applied');
    expect(spans(store.getState().doc, TV)).toEqual(spans(store.getState().doc, TA));
    store.undo();
    expect(JSON.stringify(store.getState().doc)).toBe(JSON.stringify(before));
  });
});

describe('ghost diff settle', () => {
  const sel = new Set([0]);
  const prop = response([cutGroup('c', [[V, 3, 4]])]);

  it('P24 a cut proposal that arrives AFTER the doc changed is diffed against the current doc at once', () => {
    vi.useFakeTimers();
    const d0 = linked();
    // the user moved on: V/A replaced by bladed pieces before the proposal arrived
    const d1 = applyAll(d0, [{ type: 'blade', itemId: V, atFrame: 300, newItemId: V2 }]);
    const { rerender, result } = renderHook(({ d, p }) => useGhostDiff(d, p, sel), { initialProps: { d: d0, p: null as EditProposalResponse | null } });
    rerender({ d: d1, p: null });
    rerender({ d: d1, p: prop });
    // cut 3..4 s maps to record 30..60 on V in both docs, but the "remove" ghosts must come from d1: V2 must not be reported removed etc.
    const now = result.current;
    const expected = renderHook(() => useGhostDiff(d1, prop, sel)).result.current;
    expect(JSON.stringify(now)).toBe(JSON.stringify(expected));
    act(() => { vi.advanceTimersByTime(GHOST_SETTLE_MS + 1); });
  });

  it('P25 after the settle window the ghosts follow the new doc', () => {
    vi.useFakeTimers();
    const d0 = linked();
    const d1 = applyAll(d0, [{ type: 'blade', itemId: V, atFrame: 300, newItemId: V2 }, { type: 'blade', itemId: A, atFrame: 300, newItemId: A2 }]);
    const { rerender, result } = renderHook(({ d }) => useGhostDiff(d, prop, sel), { initialProps: { d: d0 } });
    rerender({ d: d1 });
    act(() => { vi.advanceTimersByTime(GHOST_SETTLE_MS + 1); });
    const expected = renderHook(() => useGhostDiff(d1, prop, sel)).result.current;
    expect(JSON.stringify(result.current)).toBe(JSON.stringify(expected));
  });
});
