// @vitest-environment jsdom
// QA probes (T-030, background-activity): Owner addendum rules 2-4 on the web side. A failing case here is a finding.
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { doc, media, track, TRACKS } from '../../__tests__/fixtures';
import { createEditorStore } from '../../store';
import type { EditProposalResponse } from '../../sync/api';
import type { MediaClip } from '../../types';
import { planProposal } from '../../proposals/apply-proposal';
import { clipAssetKey } from '../anchor';
import { entryFromServerItem } from '../object-effect';
import { applyWhatFits, landProposal, proposalLaunchEntry, reviewKind } from '../proposal';
import { getResultsStore, resetResultsStores } from '../results-store';

vi.mock('../../engine', async (importOriginal) => {
  const engine = await importOriginal<typeof import('../../engine')>();
  return { ...engine, assertInvariants: (d: Parameters<typeof engine.assertInvariants>[0]) => engine.assertInvariants(d, { ids: 'opaque' }) };
});

const generated = (id: string, recordIn: number, recordOut: number, sourceIn = 0): MediaClip => ({ ...media(id, recordIn, recordOut, sourceIn), provenance: { origin: 'generated', editedByUser: false, agentName: 'VideoStoryEditor' } });
const clips = (store: ReturnType<typeof createEditorStore>): MediaClip[] => store.getState().doc.tracks.flatMap((t) => t.items) as MediaClip[];

beforeEach(() => {
  window.localStorage.clear();
  resetResultsStores();
});

describe('QA probes - addendum web side', () => {
  it('rule 3/4: an apply the edit guard holds back is not recorded as applied (the paid proposal stays waiting)', async () => {
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [generated('g1', 0, 60), generated('g2', 60, 120)])]), revision: 1 });
    const results = getResultsStore('t');
    results.launch(proposalLaunchEntry(store.getState().doc, { agent: 'recut', brief: null, range: null }, 'p1', '2026-01-01T00:00:00Z'));
    const trim = { label: 'Trim g1', ops: [{ type: 'trim', itemId: 'g1', edge: 'out', mode: 'ripple', deltaFrames: -15 }], touchedItemIds: ['g1'], touchesUserOwned: false, reason: null };
    const response: EditProposalResponse = { proposalId: 'pr', agent: 'recut', summary: 's', rejected: [], groups: [trim as never] };
    landProposal(results, 'p1', response);
    // What useBusyEditGuard does while magic blur runs on g1: hold the batch and open the modal (the user may press Back).
    store.setEditGuard(() => false);
    const before = store.getState().cursor;
    const outcome = await applyWhatFits({ results, editor: store, entry: results.getEntries()[0], label: 'AI' });
    expect(store.getState().cursor).toBe(before); // nothing was applied
    expect(outcome).not.toBe('applied');
    expect(results.getEntries()[0].state).toBe('needsReview');
  });

  it('rule 2: an assistant-launched magic blur adopted after a split covers every piece that still covers the range', () => {
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [media('x', 0, 60, 0)])]), revision: 1 });
    // The assistant launched on x; the user split x before the editor adopted the result.
    store.dispatch({ type: 'blade', itemId: 'x', atFrame: 30, newItemId: 'x2' } as never);
    const mediaRef = clipAssetKey(clips(store)[0]) ?? '';
    const entry = entryFromServerItem(store.getState().doc, {
      requestId: 'r1', executionId: 'e1', mediaRef, clipIds: ['x'], prompt: 'face', effect: 'blur', strength: 0.7, invert: false, featherPx: 0,
      coverageEndSec: null, analysisStepResultId: '00000000-0000-4000-8000-0000000000aa', trackIds: ['g1'], createdAt: '2026-01-01T00:00:00Z',
    });
    expect(entry.review?.applicable.sort()).toEqual(['x', 'x2']);
  });

  it('rule 2: a proposal cut lands at the same SOURCE time after the clip was moved, or asks for the OK', () => {
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [generated('g1', 0, 60)])]), revision: 1 });
    const blade = { label: 'Cut g1', ops: [{ type: 'blade', itemId: 'g1', atFrame: 30, newItemId: 'g1b' }], touchedItemIds: ['g1'], touchesUserOwned: false, reason: null };
    const response: EditProposalResponse = { proposalId: 'pr', agent: 'recut', summary: 's', rejected: [], groups: [blade as never] };
    // Asked for g1 at 0..60 (cut at source frame 30); meanwhile the user moved g1 to start at frame 10.
    store.dispatch({ type: 'move', itemId: 'g1', toTrackId: TRACKS.v1, toRecordIn: 10 } as never);
    const now = store.getState().doc;
    const kind = reviewKind(now, response);
    if (kind === 'ready') {
      const plan = planProposal(now, response, new Set([0]));
      const right = plan.doc.tracks.flatMap((t) => t.items).find((i) => i.id === 'g1b') as MediaClip | undefined;
      expect(right?.sourceIn).toBe(30);
    } else {
      expect(kind).not.toBe('ready');
    }
  });
});
