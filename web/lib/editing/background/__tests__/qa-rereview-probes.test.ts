// @vitest-environment jsdom
// QA re-review probes (T-050, background-activity): Owner addendum rule 2 against the placements check. A failing case is a finding.
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { doc, media, title, track, TRACKS } from '../../__tests__/fixtures';
import { createEditorStore } from '../../store';
import type { EditProposalResponse } from '../../sync/api';
import type { MediaClip } from '../../types';
import { planProposal } from '../../proposals/apply-proposal';
import { landProposal, proposalLaunchEntry, proposalIntentOf, reviewKind } from '../proposal';
import { getResultsStore, resetResultsStores } from '../results-store';

vi.mock('../../engine', async (importOriginal) => {
  const engine = await importOriginal<typeof import('../../engine')>();
  return { ...engine, assertInvariants: (d: Parameters<typeof engine.assertInvariants>[0]) => engine.assertInvariants(d, { ids: 'opaque' }) };
});

const generated = (id: string, recordIn: number, recordOut: number, sourceIn = 0): MediaClip => ({ ...media(id, recordIn, recordOut, sourceIn), provenance: { origin: 'generated', editedByUser: false, agentName: 'VideoStoryEditor' } });

beforeEach(() => {
  window.localStorage.clear();
  resetResultsStores();
});

describe('QA re-review probes - rule 2, proposals', () => {
  it('a graphic placed over a clip (insert, names no clip) is not silently placed at the launch record frame after the clip moved', () => {
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [generated('g1', 0, 60)]), track(TRACKS.v2, 'video', [])]), revision: 1 });
    const results = getResultsStore('t');
    results.launch(proposalLaunchEntry(store.getState().doc, { agent: 'recut', brief: null, range: null }, 'p1', '2026-01-01T00:00:00Z'));
    // A lower third written for g1's source frames 30..40 (record 30..40 at launch).
    const add = { label: 'Lower third', ops: [{ type: 'insert', trackId: TRACKS.v2, index: 0, item: { ...title('lt', 30, 40), trackId: TRACKS.v2 } }], touchedItemIds: [], touchesUserOwned: false, reason: null };
    const response: EditProposalResponse = { proposalId: 'pr', agent: 'recut', summary: 's', rejected: [], groups: [add as never] };
    landProposal(results, 'p1', response);
    // Meanwhile the user moved g1 20 frames later: source 30 now sits at record 50.
    store.dispatch({ type: 'move', itemId: 'g1', toTrackId: TRACKS.v1, toRecordIn: 20 } as never);
    const now = store.getState().doc;
    const kind = reviewKind(now, response, proposalIntentOf(results.getEntries()[0]).placements);
    if (kind === 'ready') {
      const plan = planProposal(now, response, new Set([0]));
      const lt = plan.doc.tracks.flatMap((t) => t.items).find((i) => i.id === 'lt');
      expect(lt?.recordIn).toBe(50);
    } else {
      expect(kind).not.toBe('ready');
    }
  });
});
