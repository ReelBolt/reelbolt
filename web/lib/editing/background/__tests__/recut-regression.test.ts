// @vitest-environment jsdom
// QA probe (recut-follows-request T-005). Not committed.
import { beforeEach, describe, expect, it } from 'vitest';
import { applyAll } from '../../engine';
import { doc, media, track } from '../../__tests__/fixtures';
import { createEditorStore } from '../../store';
import type { EditProposalGroupResponse, EditProposalResponse } from '../../sync/api';
import type { EditTimeline, MediaClip } from '../../types';
import { applyWhatFits, groupFit, landProposal, proposalLaunchEntry, proposalIntentOf, reviewKind } from '../proposal';
import { getResultsStore, resetResultsStores } from '../results-store';

const V = '11111111-0000-4000-8000-000000000001';
const A = '11111111-0000-4000-8000-000000000002';
const TV = '55555555-0000-4000-8000-000000000001';
const TA = '55555555-0000-4000-8000-000000000002';
const LINK = '22222222-0000-4000-8000-000000000001';
const gen = { origin: 'generated' as const, editedByUser: false };

const base = (audioProv: MediaClip['provenance'] = gen): EditTimeline => ({
  ...doc([track(TV, 'video', [media(V, 0, 600, 60, { provenance: gen, linkId: LINK })]), track(TA, 'audio', [media(A, 0, 600, 60, { provenance: audioProv, linkId: LINK })])]),
  id: '44444444-0000-4000-8000-000000000001',
  projectId: '44444444-0000-4000-8000-000000000002',
});
const cutGroup = (label: string, startSec: number, endSec: number, itemId = V): EditProposalGroupResponse => ({ label, ops: [], touchedItemIds: [itemId], touchesUserOwned: false, reason: null, cuts: [{ itemId, startSec, endSec }] });
const trimA = (): EditProposalGroupResponse => ({ label: 'Trim end of A', ops: [{ type: 'trim', itemId: A, edge: 'out', mode: 'ripple', deltaFrames: -15 }] as never, touchedItemIds: [A], touchesUserOwned: false, reason: null });
const response = (groups: EditProposalGroupResponse[]): EditProposalResponse => ({ proposalId: 'pr', agent: 'recut', summary: 's', rejected: [], groups });
const spans = (d: EditTimeline, id: string) => d.tracks.find((t) => t.id === id)!.items.map((i) => [i.recordIn, i.recordOut]);

beforeEach(() => {
  window.localStorage.clear();
  resetResultsStores();
});

describe('QA probe: groupFit with cut groups (empty ops)', () => {
  it('a cut group fits (not stale, not changed) without placements crash', () => {
    const d = base();
    const entry = proposalLaunchEntry(d, { agent: 'recut', brief: 'cut out all spoken words', range: null }, 'p1', '2026-01-01T00:00:00Z');
    const intent = proposalIntentOf(entry);
    const fit = groupFit(d, response([cutGroup('c1', 3, 4), cutGroup('c2', 6, 7)]), intent.placements, undefined, intent.surroundings);
    expect(fit).toEqual({ fits: [0, 1], stale: [], changed: [], needsAck: [] });
    expect(reviewKind(d, response([cutGroup('c1', 3, 4)]), intent.placements, intent.surroundings)).toBe('ready');
  });

  it('a cut group over a manual linked partner is needsAck', () => {
    const d = base(null);
    const fit = groupFit(d, response([cutGroup('c1', 3, 4)]), undefined);
    // placements undefined => movedSinceLaunch is true => changed. With placements:
    const entry = proposalLaunchEntry(d, { agent: 'recut', brief: null, range: null }, 'p1', '2026-01-01T00:00:00Z');
    const intent = proposalIntentOf(entry);
    const fit2 = groupFit(d, response([cutGroup('c1', 3, 4)]), intent.placements, undefined, intent.surroundings);
    expect(fit.changed).toEqual([0]);
    expect(fit2.needsAck).toEqual([0]);
  });

  it('a cut group whose clip is gone is stale', () => {
    const d = base();
    const entry = proposalLaunchEntry(d, { agent: 'recut', brief: null, range: null }, 'p1', '2026-01-01T00:00:00Z');
    const intent = proposalIntentOf(entry);
    const fit = groupFit(d, response([cutGroup('gone', 3, 4, '33333333-0000-4000-8000-000000000009')]), intent.placements, undefined, intent.surroundings);
    expect(fit.stale).toEqual([0]);
  });

  it('PROBE a cut group whose span left its clip after a user blade is not reported as fitting', () => {
    const d0: EditTimeline = { ...base(), tracks: [track(TV, 'video', [media(V, 0, 600, 60, { provenance: gen })])] };
    const entry = proposalLaunchEntry(d0, { agent: 'recut', brief: null, range: null }, 'p1', '2026-01-01T00:00:00Z');
    const intent = proposalIntentOf(entry);
    // user blades V/A at frame 300 after launch: V keeps [0,300); the cut at source 15..16 s (record 390..420) now sits in the new right piece
    const d = applyAll(d0, [
      { type: 'blade', itemId: V, atFrame: 300, newItemId: '11111111-0000-4000-8000-0000000000a1' },
    ]);
    const fit = groupFit(d, response([cutGroup('c late', 15, 16)]), intent.placements, undefined, intent.surroundings);
    expect(fit.fits).toEqual([]);
  });

  it('applyWhatFits lands cut groups + an op group as ONE batch (one undo step)', async () => {
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: base(), revision: 1 });
    const results = getResultsStore('t');
    results.launch(proposalLaunchEntry(store.getState().doc, { agent: 'recut', brief: null, range: null }, 'p1', '2026-01-01T00:00:00Z'));
    landProposal(results, 'p1', response([cutGroup('c1', 3, 4), cutGroup('c2', 6, 7), trimA()]));
    const before = store.getState().doc;
    // A is V's linked partner, which the ripple cut blades: A's trim would land on the wrong piece, so it is stale.
    const fit = groupFit(before, results.getEntries()[0] && response([cutGroup('c1', 3, 4), cutGroup('c2', 6, 7), trimA()]), proposalIntentOf(results.getEntries()[0]).placements, undefined, proposalIntentOf(results.getEntries()[0]).surroundings);
    expect(fit.fits).toEqual([0, 1]);
    expect(fit.stale).toEqual([2]);
    const outcome = await applyWhatFits({ results, editor: store, entry: results.getEntries()[0], label: 'AI recut' });
    expect(outcome).toBe('applied');
    expect(spans(store.getState().doc, TV)).toEqual([[0, 30], [30, 90], [90, 540]]);
    store.undo();
    expect(JSON.stringify(store.getState().doc)).toBe(JSON.stringify(before));
  });
});
