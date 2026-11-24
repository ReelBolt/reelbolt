/**
 * W0.6 (UI2-11): a partial accept of an AI proposal, then accepting another group that places on the
 * same new lane, reuses the lane the first accept created instead of adding it a second time
 * ("track id already in use").
 */

import { describe, expect, it, vi } from 'vitest';
import { assertInvariants } from '../engine';
import { doc, media, title, track, TRACKS } from './fixtures';
import { ghostDiff, planProposal, programFor } from '../proposals/apply-proposal';
import type { EditProposalResponse } from '../sync/api';

// These fixtures name things `gfx`/`timeline-1`, and the production code under test checks documents
// with the strict engine invariants. Only the Guid id shape is relaxed here (it is pinned in
// `engine/__tests__/apply.contract.test.ts`); every structural invariant still runs. The loose cast keeps
// this compiling before the engine's options argument exists (the extra argument is then ignored).
vi.mock('../engine', async (importOriginal) => {
  const engine = await importOriginal<typeof import('../engine')>();
  const strict = engine.assertInvariants as (...args: unknown[]) => unknown;
  return { ...engine, assertInvariants: (doc: Parameters<typeof engine.assertInvariants>[0]) => strict(doc, { ids: 'opaque' }) };
});

const start = () => doc([track(TRACKS.v1, 'video', [media('a', 0, 150)])]);
const gfx = { id: 'gfx', kind: 'video' as const, name: 'Graphics', height: 64, locked: false, enabled: true, mute: false, solo: false, items: [] };
const proposal: Pick<EditProposalResponse, 'groups'> = {
  groups: [
    { label: 'Title A', ops: [{ type: 'addTrack', index: 1, track: gfx }, { type: 'placeOnTop', trackId: 'gfx', item: title('tA', 0, 30, 'Hello') }], touchedItemIds: ['tA'], touchesUserOwned: false, reason: null },
    { label: 'Title B', ops: [{ type: 'placeOnTop', trackId: 'gfx', item: title('tB', 90, 110, 'Bye') }], touchedItemIds: ['tB'], touchesUserOwned: false, reason: null },
  ],
};

describe('W0.6 partial accept reuses its lane (UI2-11)', () => {
  it('accepting the later group first, then the lane group, keeps one Graphics track', () => {
    const first = planProposal(start(), proposal, new Set([1]));
    expect(first.doc.tracks.map((t) => t.id)).toEqual([TRACKS.v1, 'gfx']);
    const second = planProposal(first.doc, proposal, new Set([0]));
    assertInvariants(second.doc);
    expect(second.ops.map((o) => o.type)).toEqual(['placeOnTop']);
    expect(second.doc.tracks.map((t) => t.id)).toEqual([TRACKS.v1, 'gfx']);
    expect(second.doc.tracks[1].items.map((i) => i.id)).toEqual(['tA', 'tB']);
  });

  it('accepting the lane group first, then the later group, does not pull the lane in again', () => {
    const first = planProposal(start(), proposal, new Set([0]));
    expect(programFor(proposal, new Set([1]), first.doc).map((o) => o.type)).toEqual(['placeOnTop']);
    const second = planProposal(first.doc, proposal, new Set([1]));
    expect(second.doc.tracks.filter((t) => t.id === 'gfx')).toHaveLength(1);
  });

  it('the ghost diff of the remaining group is available after a partial accept', () => {
    const first = planProposal(start(), proposal, new Set([1]));
    const { failed, ghosts, newTracks } = ghostDiff(first.doc, proposal, new Set([0]));
    expect(failed).toEqual([]);
    expect(newTracks).toEqual([]);
    expect(ghosts.find((g) => g.itemId === 'tA')).toMatchObject({ kind: 'add', trackId: 'gfx' });
  });
});
