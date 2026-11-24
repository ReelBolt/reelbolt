import { test, expect } from '../../../fixtures/editor-v2';
import type { Item } from '../../../fixtures/editor-v2/api';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { knownFailure } from '../expected-failures';

/**
 * 11 basics: partial accept of an AI proposal (remediation-plan.md W0.6 "Partial accept of a proposal reuses the
 * created lane", stubbed proposal e2e; report.md §5 UI2-11, verdict row P8-PROP-12; T-031 item 7).
 *
 * Only the proposal request is stubbed (no paid model runs); the editor, the engine and the saves are real.
 */

const title = (id: string, recordIn: number, recordOut: number, text: string): Item => ({
  type: 'titleClip', id, name: text, recordIn, recordOut, text, fontSize: 32, fontFamily: 'Inter', color: '#ffffff', enabled: true, provenance: null,
});

/** UI2-11 (desktop d5): accept the later group first (it pulls the new lane in), then the lane group: one lane. */
test('UI2-11 a partial proposal accept then the remaining group reuses the new lane', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('UI2-11');
  const id = await freshTimeline('seeded');
  const lane = crypto.randomUUID();
  const [tA, tB] = [crypto.randomUUID(), crypto.randomUUID()];
  const proposal = {
    proposalId: `rem-ui211-${Date.now()}`,
    agent: 'recut',
    summary: 'Two titles on a new graphics lane.',
    rejected: [],
    groups: [
      {
        label: 'Title A', reason: null, touchesUserOwned: false, touchedItemIds: [tA],
        ops: [
          { type: 'addTrack', index: 1, track: { id: lane, kind: 'video', name: 'Graphics', height: 64, locked: false, enabled: true, mute: false, solo: false, items: [] } },
          { type: 'placeOnTop', trackId: lane, item: title(tA, 0, 30, 'Hello') },
        ],
      },
      { label: 'Title B', reason: null, touchesUserOwned: false, touchedItemIds: [tB], ops: [{ type: 'placeOnTop', trackId: lane, item: title(tB, 90, 110, 'Bye') }] },
    ],
  };
  const ed = await openEditor(id);
  await ed.page.route(/\/edit-timelines\/[^/]+\/proposals$/, (route) =>
    route.request().method() === 'POST' ? route.fulfill({ status: 200, contentType: 'application/json', json: proposal }) : route.continue(),
  );
  await ed.openPanelTab('tab-assist');
  await ed.tap(ed.testId('proposal-ask'));
  await expect(ed.testId('proposal-group-1')).toContainText('Title B');

  // 1. Only "Title B": its group needs the lane "Title A" carries, which is pulled in.
  await ed.tap(ed.testId('proposal-group-check-0'));
  await ed.tap(ed.testId('proposal-accept-selected'));
  await ed.expectServer((tl) => tl.document.tracks.some((t) => t.id === lane && t.items.some((i) => i.id === tB)), 'Title B is stored on the new lane');

  // 2. The remaining group ("Title A", now listed first) goes on the same lane, without adding it again.
  await expect(ed.testId('proposal-group-0')).toContainText('Title A');
  await ed.tap(ed.testId('proposal-group-check-0'));
  await ed.tap(ed.testId('proposal-accept-selected'));
  await expect(ed.testId('proposal-error'), 'the second accept is not refused').toHaveCount(0);
  const tl = await ed.expectServer((t) => t.document.tracks.some((tr) => tr.id === lane && tr.items.some((i) => i.id === tA)), 'Title A is stored on the same lane');
  expect(tl.document.tracks.filter((t) => t.id === lane), 'one Graphics lane').toHaveLength(1);
  expect(tl.document.tracks.find((t) => t.id === lane)!.items.map((i) => i.id).sort(), 'both titles on it').toEqual([tA, tB].sort());
  await expect(ed.chip).not.toContainText(/rejected/i);
});
