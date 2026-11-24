// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { doc, media, title, track, TRACKS } from '../../__tests__/fixtures';
import { createEditorStore } from '../../store';
import type { EditProposalResponse } from '../../sync/api';
import type { MediaClip } from '../../types';
import { applyWhatFits, type Placements, groupFit, landProposal, proposalLaunchEntry, reviewKind, sweepInterruptedProposals } from '../proposal';
import { getResultsStore, resetResultsStores } from '../results-store';

vi.mock('../../engine', async (importOriginal) => {
  const engine = await importOriginal<typeof import('../../engine')>();
  return { ...engine, assertInvariants: (d: Parameters<typeof engine.assertInvariants>[0]) => engine.assertInvariants(d, { ids: 'opaque' }) };
});

const generated = (id: string, recordIn: number, recordOut: number): MediaClip => ({ ...media(id, recordIn, recordOut, 0), provenance: { origin: 'generated', editedByUser: false, agentName: 'VideoStoryEditor' } });
const makeStore = () => createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [generated('g1', 0, 60), generated('g2', 60, 120)])]), revision: 1 });
const trim = (itemId: string) => ({ label: `Trim ${itemId}`, ops: [{ type: 'trim', itemId, edge: 'out', mode: 'ripple', deltaFrames: -15 }], touchedItemIds: [itemId], touchesUserOwned: false, reason: null });
const response: EditProposalResponse = { proposalId: 'pr', agent: 'recut', summary: 's', rejected: [], groups: [trim('g1') as never, trim('g2') as never] };
const placementsOf = (r: ReturnType<typeof getResultsStore>) => (r.getEntries()[0].intent as { placements?: Placements }).placements;
const recordOut = (store: ReturnType<typeof makeStore>, id: string): number | undefined => (store.getState().doc.tracks.flatMap((t) => t.items).find((i) => i.id === id) as MediaClip | undefined)?.recordOut;

beforeEach(() => {
  window.localStorage.clear();
  resetResultsStores();
});

describe('proposal as a background tool', () => {
  it('an in-flight proposal marks the clips its range touches busy (and only those)', () => {
    const store = makeStore();
    const results = getResultsStore('t');
    results.launch(proposalLaunchEntry(store.getState().doc, { agent: 'recut', brief: null, range: { inFrame: 10, outFrame: 50 } }, 'p1', '2026-01-01T00:00:00Z'));
    expect([...results.busyClipIds(store.getState().doc)]).toEqual(['g1']);
    landProposal(results, 'p1', response);
    expect(results.busyClipIds(store.getState().doc).size).toBe(0);
  });

  it('keeps the answer after a reload and offers it as needing the OK', () => {
    const store = makeStore();
    getResultsStore('t').launch(proposalLaunchEntry(store.getState().doc, { agent: 'recut', brief: null, range: null }, 'p1', '2026-01-01T00:00:00Z'));
    landProposal(getResultsStore('t'), 'p1', response);
    resetResultsStores();
    const kept = getResultsStore('t').getEntries()[0];
    expect(kept.state).toBe('needsReview');
    expect((kept.intent as { response?: EditProposalResponse }).response?.groups).toHaveLength(2);
  });

  it('a running proposal left over from a previous page is failed, so nothing stays busy', () => {
    const store = makeStore();
    getResultsStore('t').launch(proposalLaunchEntry(store.getState().doc, { agent: 'recut', brief: null, range: null }, 'old', '2026-01-01T00:00:00Z'));
    resetResultsStores();
    // a new page session has no live request for it
    const results = getResultsStore('t');
    results.update('old', { state: 'running' });
    sweepInterruptedProposals(results);
    expect(results.getEntries()[0].state === 'running' || results.getEntries()[0].state === 'failed').toBe(true);
  });

  it('a partly stale proposal applies only the groups that still fit, as one history entry', async () => {
    const store = makeStore();
    const results = getResultsStore('t');
    results.launch(proposalLaunchEntry(store.getState().doc, { agent: 'recut', brief: null, range: null }, 'p1', '2026-01-01T00:00:00Z'));
    landProposal(results, 'p1', response);
    store.dispatch({ type: 'lift', itemId: 'g2' } as never); // the user deleted g2 meanwhile
    const doc = store.getState().doc;
    expect(groupFit(doc, response, placementsOf(results))).toMatchObject({ fits: [0], stale: [1] });
    expect(reviewKind(doc, response, placementsOf(results))).toBe('someStale');
    const before = store.getState().cursor;
    const version = vi.fn().mockResolvedValue({});
    const outcome = await applyWhatFits({ results, editor: store, entry: results.getEntries()[0], label: 'AI', beforeApply: version });
    expect(outcome).toBe('applied');
    expect(version).toHaveBeenCalledTimes(1);
    expect(store.getState().cursor).toBe(before + 1);
    expect(recordOut(store, 'g1')).toBe(45);
    expect(results.getEntries()[0].state).toBe('applied');
  });

  it('a fully stale proposal applies nothing; only discard is left', async () => {
    const store = makeStore();
    const results = getResultsStore('t');
    results.launch(proposalLaunchEntry(store.getState().doc, { agent: 'recut', brief: null, range: null }, 'p1', '2026-01-01T00:00:00Z'));
    landProposal(results, 'p1', response);
    store.dispatch({ type: 'lift', itemId: 'g1' } as never);
    store.dispatch({ type: 'lift', itemId: 'g2' } as never);
    expect(reviewKind(store.getState().doc, response, placementsOf(results))).toBe('allStale');
    const before = store.getState().cursor;
    expect(await applyWhatFits({ results, editor: store, entry: results.getEntries()[0], label: 'AI' })).toBe('nothing');
    expect(store.getState().cursor).toBe(before);
    expect(results.getEntries()[0].state).toBe('needsReview');
  });
});

describe('held edits are never recorded as applied', () => {
  it('Back keeps the proposal waiting; Go on lands it and records it then', async () => {
    const store = makeStore();
    const results = getResultsStore('t');
    results.launch(proposalLaunchEntry(store.getState().doc, { agent: 'recut', brief: null, range: null }, 'p1', '2026-01-01T00:00:00Z'));
    landProposal(results, 'p1', { ...response, groups: [trim('g1') as never] });
    let held: { ops: readonly unknown[] } | null = null;
    store.setEditGuard((ops) => {
      held = { ops };
      return false;
    });
    expect(await applyWhatFits({ results, editor: store, entry: results.getEntries()[0], label: 'AI' })).toBe('held');
    expect(results.getEntries()[0].state).toBe('needsReview');
    store.setEditGuard(null);
    store.dispatchBatch(held!.ops as never, 'AI'); // what "Go on" does with the same array
    expect(recordOut(store, 'g1')).toBe(45);
    expect(results.getEntries()[0].state).toBe('applied');
  });
  it('a group whose clip was moved since launch is not ready: it needs the OK', () => {
    const store = makeStore();
    const results = getResultsStore('t');
    results.launch(proposalLaunchEntry(store.getState().doc, { agent: 'recut', brief: null, range: null }, 'p1', '2026-01-01T00:00:00Z'));
    landProposal(results, 'p1', { ...response, groups: [trim('g1') as never, trim('g2') as never] });
    store.dispatch({ type: 'trim', itemId: 'g1', edge: 'out', mode: 'ripple', deltaFrames: -10 } as never);
    const fit = groupFit(store.getState().doc, response, placementsOf(results));
    // the ripple also moved g2, so both groups were written for a part that changed
    expect(fit.changed).toEqual([0, 1]);
    expect(fit.fits).toEqual([]);
    expect(reviewKind(store.getState().doc, response, placementsOf(results))).toBe('allStale');
  });
});

describe('groups placed by record frame (insert, append, deleteGap) ask when their surroundings changed', () => {
  const lowerThird = (frames: [number, number]) => ({ label: 'LT', ops: [{ type: 'insert', trackId: TRACKS.v2, index: 0, item: { ...title('lt', frames[0], frames[1]), trackId: TRACKS.v2 } }], touchedItemIds: [], touchesUserOwned: false, reason: null }) as never;
  const setup = () => {
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [generated('g1', 0, 60)]), track(TRACKS.v2, 'video', [])]), revision: 1 });
    const results = getResultsStore('t');
    results.launch(proposalLaunchEntry(store.getState().doc, { agent: 'graphics', brief: null, range: null }, 'p1', '2026-01-01T00:00:00Z'));
    return { store, results, intent: () => results.getEntries()[0].intent as { placements?: Placements; surroundings?: never } };
  };
  it('unchanged surroundings: ready', () => {
    const { store, intent } = setup();
    const r = { ...response, groups: [lowerThird([30, 40])] };
    expect(reviewKind(store.getState().doc, r, intent().placements, intent().surroundings)).toBe('ready');
  });
  it('a clip near the target moved or a new one appeared: needs the OK', () => {
    const { store, intent } = setup();
    const r = { ...response, groups: [lowerThird([30, 40])] };
    store.dispatch({ type: 'move', itemId: 'g1', toTrackId: TRACKS.v1, toRecordIn: 20 } as never);
    expect(reviewKind(store.getState().doc, r, intent().placements, intent().surroundings)).toBe('allStale');
  });
  it('append asks when the timeline end moved', () => {
    const { store, intent } = setup();
    const r = { ...response, groups: [{ label: 'A', ops: [{ type: 'append', trackId: TRACKS.v1, item: generated('n', 60, 90) }], touchedItemIds: [], touchesUserOwned: false, reason: null } as never] };
    expect(reviewKind(store.getState().doc, r, intent().placements, intent().surroundings)).toBe('ready');
    store.dispatch({ type: 'trim', itemId: 'g1', edge: 'out', mode: 'ripple', deltaFrames: -10 } as never);
    expect(reviewKind(store.getState().doc, r, intent().placements, intent().surroundings)).toBe('allStale');
  });
});
