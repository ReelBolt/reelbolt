import { desktopLayoutOnly } from '../../../fixtures/editor-v2/rows';
import { test, expect } from '../../../fixtures/editor-v2/lane-b-test';
import type { Item } from '../../../fixtures/editor-v2/api';
import { allSentOps, uuid, selectAt } from '../../../fixtures/editor-v2/lane-b';

/**
 * Lane B: the right panel tabs after the Inspector - Assist, Settings, Versions, Compare
 * (inventory timeline.md 2.J, TL-270..278, TL-290..293, TL-300..301, TL-310..312).
 *
 * Assist (S19): the proposal endpoint is stubbed with page.route - no model, no provider, no credits.
 */

// WebKit sends the page's API requests through the dashboard's service worker, where page.route never sees them.
test.use({ serviceWorkers: 'block' });

const titleItem = (id: string, text: string, recordIn: number, recordOut: number): Item => ({
  type: 'titleClip', id, name: text, recordIn, recordOut, text, fontSize: 32, fontFamily: 'Inter', color: '#ffffff', enabled: true, provenance: null,
});

/** Right-panel tabs live in the phone's Inspector pane (the left ones in Panels). */
async function openRightTab(ed: import('../../../fixtures/editor-v2').EditorPage, tab: string | RegExp): Promise<void> {
  await ed.openInspector();
  const loc = typeof tab === 'string' ? ed.page.getByTestId(tab) : ed.page.getByRole('tab', { name: tab });
  await ed.tap(loc.first());
}

// The client asks as a background job (`?background=true`), so the query is part of the URL.
const PROPOSALS = /\/edit-timelines\/[^/]+\/proposals(\?.*)?$/;

test('TL-270 TL-271 TL-272 TL-274 TL-276 Assist: ask (stubbed), review, accept one group as one undo step', async ({ freshTimeline, openEditor }) => {
  const id = await freshTimeline('seeded');
  const lane = uuid();
  const [tA, tB] = [uuid(), uuid()];
  let requestBody: { agent?: string; brief?: string | null } = {};
  const ed = await openEditor(id);
  await ed.page.route(PROPOSALS, async (route) => {
    if (route.request().method() !== 'POST') return route.continue();
    requestBody = route.request().postDataJSON() as typeof requestBody;
    return route.fulfill({
      status: 200,
      contentType: 'application/json',
      json: {
        proposalId: `lane-b-${Date.now()}`,
        agent: requestBody.agent ?? 'recut',
        summary: 'Two titles on a new graphics lane.',
        rejected: [],
        groups: [
          { label: 'Title A', reason: 'Opens the film', touchesUserOwned: false, touchedItemIds: [tA], ops: [{ type: 'addTrack', index: 1, track: { id: lane, kind: 'video', name: 'Graphics', height: 64, locked: false, enabled: true, mute: false, solo: false, items: [] } }, { type: 'placeOnTop', trackId: lane, item: titleItem(tA, 'Hello', 0, 30) }] },
          { label: 'Title B', reason: null, touchesUserOwned: false, touchedItemIds: [tB], ops: [{ type: 'placeOnTop', trackId: lane, item: titleItem(tB, 'Bye', 90, 110) }] },
        ],
      },
    });
  });
  await openRightTab(ed, 'tab-assist');
  await ed.tap(ed.testId('proposal-agent'));
  const options = await ed.page.getByRole('option').allInnerTexts();
  expect(options.length, 'five agents are offered').toBe(5);
  await ed.tap(ed.page.getByRole('option').nth(1));
  await ed.testId('proposal-brief').fill('tighten the intro');
  const patchesBefore = ed.patches.length;
  await ed.tap(ed.testId('proposal-ask'));
  await expect(ed.testId('proposal-group-1')).toContainText('Title B');
  expect(requestBody.brief, 'the brief travels with the request').toBe('tighten the intro');
  expect(requestBody.agent, 'the chosen agent travels with the request').not.toBe('recut');
  await ed.page.waitForTimeout(1500);
  expect(ed.patches.length, 'asking writes NOTHING').toBe(patchesBefore);

  // Both groups start selected; deselect Title B, accept Title A only (it brings the lane).
  await ed.tap(ed.testId('proposal-group-check-1'));
  await expect(ed.testId('proposal-accept-selected')).toContainText('(1)');
  await ed.tap(ed.testId('proposal-accept-selected'));
  const tl = await ed.expectServer((t) => t.document.tracks.some((tr) => tr.id === lane && tr.items.some((i) => i.id === tA)), 'Title A is stored on the new lane');
  expect(tl.document.tracks.find((t) => t.id === lane)!.items.map((i) => i.id), 'Title B was not accepted').toEqual([tA]);
  // The remaining group is still on offer.
  await expect(ed.testId('proposal-group-0')).toContainText('Title B');
  // One accept = one undo step.
  await ed.openPanelTab(/history/i);
  await expect(ed.testId('history-entry-1')).toContainText('proposal');
  await expect(ed.testId('history-entry-2')).toHaveCount(0);
});

test('TL-273 TL-277 Assist: the ghost diff toggles, and Reject all clears the proposal without any PATCH', async ({ freshTimeline, openEditor }) => {
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  const tA = uuid();
  const trackId = (await ed.serverTimeline()).document.tracks[0].id;
  await ed.page.route(PROPOSALS, (route) =>
    route.request().method() === 'POST'
      ? route.fulfill({ status: 200, contentType: 'application/json', json: { proposalId: 'lane-b-reject', agent: 'recut', summary: 'One title.', rejected: [], groups: [{ label: 'Title', reason: null, touchesUserOwned: false, touchedItemIds: [tA], ops: [{ type: 'placeOnTop', trackId, item: titleItem(tA, 'Hi', 0, 30) }] }] } })
      : route.continue(),
  );
  await openRightTab(ed, 'tab-assist');
  await ed.tap(ed.testId('proposal-ask'));
  await expect(ed.testId('proposal-group-0')).toBeVisible();
  const ghost = ed.testId('proposal-ghost-toggle');
  await expect(ghost).toBeChecked();
  // Keyboard, not a forced pointer click: a forced click lands on the coordinates read before the panel settles, and on the
  // loaded CI desktop run it never toggled the box (run 45796910736); Space on the focused checkbox cannot miss.
  await ghost.focus();
  await ghost.press('Space');
  await expect(ghost).not.toBeChecked();
  const patches = ed.patches.length;
  await ed.tap(ed.testId('proposal-reject'));
  await expect(ed.testId('proposal-group-0')).toHaveCount(0);
  await ed.page.waitForTimeout(1500);
  expect(ed.patches.length, 'rejecting writes nothing').toBe(patches);
});

test('TL-275 Assist: a group that changes a clip you made needs its acknowledgement', async ({ freshTimeline, openEditor }) => {
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  const doc = (await ed.serverTimeline()).document;
  const clip = doc.tracks.find((t) => t.kind === 'video')!.items[0];
  await ed.page.route(PROPOSALS, (route) =>
    route.request().method() === 'POST'
      ? route.fulfill({
          status: 200,
          contentType: 'application/json',
          json: { proposalId: 'lane-b-ack', agent: 'recut', summary: 'Rename your clip.', rejected: [], groups: [{ label: 'Rename', reason: null, touchesUserOwned: true, touchedItemIds: [clip.id], ops: [{ type: 'setProperty', itemId: clip.id, key: 'name', value: 'Renamed by assist' }] }] },
        })
      : route.continue(),
  );
  await openRightTab(ed, 'tab-assist');
  await ed.tap(ed.testId('proposal-ask'));
  await expect(ed.testId(`proposal-user-owned-0`)).toBeVisible();
  await ed.tap(ed.testId('proposal-accept-selected'));
  await expect(ed.testId('proposal-error'), 'accepting without the acknowledgement is refused in words').toBeVisible();
  expect((await ed.serverTimeline()).document.tracks[0].items[0].name, 'nothing was written').toBe('clipA.mp4');
  await ed.testId(`proposal-ack-${clip.id}`).click({ force: true });
  await ed.tap(ed.testId('proposal-accept-selected'));
  await ed.expectServer((t) => t.document.tracks[0].items[0].name === 'Renamed by assist', 'with the acknowledgement the change applies');
});

test('S19 TL-272 Assist without a chat provider says so in words (503) and writes nothing', async ({ freshTimeline, openEditor }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.page.route(PROPOSALS, (route) => (route.request().method() === 'POST' ? route.fulfill({ status: 503, contentType: 'application/json', json: { message: 'no chat provider' } }) : route.continue()));
  await openRightTab(ed, 'tab-assist');
  await ed.tap(ed.testId('proposal-ask'));
  await expect(ed.testId('proposal-error')).toContainText(/No chat provider is configured/i);
  await ed.page.waitForTimeout(1500);
  expect(allSentOps(ed), 'nothing was written').toEqual([]);
});

test('TL-290 the frame rate is locked, with the reason shown, once the timeline has clips', async ({ freshTimeline, openEditor }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  await openRightTab(ed, /settings/i);
  await expect(ed.testId('settings-rate-locked')).toHaveText("The frame rate can't be changed once there are clips on the timeline. Start a new timeline at the frame rate you want.");
  await expect(ed.testId('settings-fps')).toBeDisabled();
  await expect(ed.testId('settings-rate-num')).toBeDisabled();
  await expect(ed.testId('settings-rate-den')).toBeDisabled();
});

test('TL-290 TL-291 TL-292 TL-293 Settings: edits stay a draft until Apply; Apply is one change; Revert drops the draft', async ({ freshTimeline, openEditor }) => {
  // A blank (empty) timeline: the frame rate is only editable while there are no clips (see the lock test below).
  const ed = await openEditor(await freshTimeline('blank'));
  await openRightTab(ed, /settings/i);
  const width = ed.testId('settings-width');
  const height = ed.testId('settings-height');
  await expect(width).toHaveValue(/1[ ,.]?920/);
  await width.fill('1280');
  await height.fill('720');
  const patches = ed.patches.length;
  await ed.page.waitForTimeout(1200);
  expect(ed.patches.length, 'a draft sends nothing').toBe(patches);
  await expect(ed.testId('settings-apply')).toBeEnabled();

  // Revert: back to the document's values, no PATCH.
  await ed.tap(ed.page.getByRole('button', { name: 'Revert' }));
  await expect(width).toHaveValue(/1[ ,.]?920/);
  await expect(ed.testId('settings-apply')).toBeDisabled();

  // Apply a real change: size, frame rate and aspect policy in ONE op.
  await width.fill('1280');
  await height.fill('720');
  await ed.testId('settings-rate-num').fill('25');
  await ed.tap(ed.testId('settings-aspect-policy'));
  await ed.tap(ed.page.getByRole('option', { name: 'Stretch' }));
  await ed.tap(ed.testId('settings-apply'));
  const tl = await ed.expectServer((t) => t.document.settings.width === 1280 && t.document.settings.height === 720, 'the new size is stored');
  expect(tl.document.settings.rate).toEqual({ num: 25, den: 1 });
  expect(tl.document.settings.aspectPolicy).toBe('stretch');
  await expect.poll(() => allSentOps(ed).filter((o) => o === 'setTimelineSettings').length, { message: 'one op for the whole change' }).toBe(1);
  await expect(ed.viewer, 'the monitor follows the new aspect ratio').toBeVisible();
  const box = await ed.viewer.boundingBox();
  expect(Math.abs(box!.width / box!.height - 1280 / 720)).toBeLessThan(0.05);

  // One undo restores everything.
  await ed.openPanelTab(/history/i);
  await ed.tap(ed.testId('history-undo'));
  await ed.expectServer((t) => t.document.settings.width === 1920 && t.document.settings.rate.num === 30, 'Undo restores the size and rate');
});

test('S23 TL-300 TL-301 Versions: Restore asks first; confirming restores the stored revision as a NEW revision', async ({ freshTimeline, openEditor }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  const original = (await ed.serverTimeline()).document.tracks[0].items[0].name;
  await selectAt(ed, 30);
  await ed.openInspector();
  await ed.testId('inspector-name').fill('Edited once');
  await ed.testId('inspector-name').blur();
  const edited = await ed.expectServer((t) => t.document.tracks[0].items[0].name === 'Edited once', 'the edit is stored');

  await openRightTab(ed, /versions/i);
  await ed.tap(ed.page.getByRole('button', { name: 'Refresh' }));
  const restore1 = ed.testId('revision-restore-1');
  await expect(restore1, 'revision 1 is restorable').toBeEnabled();

  // First click only asks.
  await ed.tap(restore1);
  await expect(ed.testId('revision-restore-confirm-1')).toBeVisible();
  await ed.page.waitForTimeout(1200);
  expect((await ed.serverTimeline()).document.tracks[0].items[0].name, 'asking changed nothing').toBe('Edited once');
  // Keep current: the question goes away.
  await ed.tap(ed.testId('revision-restore-cancel-1'));
  await expect(ed.testId('revision-restore-confirm-1')).toHaveCount(0);
  expect((await ed.serverTimeline()).revision).toBe(edited.revision);

  // Confirm: the stored document of revision 1 comes back, as a new revision (history is append-only).
  await ed.tap(restore1);
  await ed.tap(ed.testId('revision-restore-confirm-1'));
  const restored = await ed.expectServer((t) => t.document.tracks[0].items[0].name === original, 'the original document is back');
  expect(restored.revision, 'restoring creates a new revision').toBeGreaterThan(edited.revision);
  await ed.reload();
  expect((await ed.serverTimeline()).document.tracks[0].items[0].name).toBe(original);
});

test('TL-310 TL-311 TL-312 Compare: against another timeline lists the difference and jumps to it', async ({ freshTimeline, openEditor, seed }) => {
  desktopLayoutOnly('the Compare tab is hidden on a phone (PHONE_HIDDEN_TOOL_TABS in phone/captions-logic.ts; docs/editor.md "Not on a phone": compare)');
  const ed = await openEditor(await freshTimeline('seeded'));
  // Make a known difference: move nothing, rename one clip.
  await selectAt(ed, 30);
  await ed.openInspector();
  await ed.testId('inspector-name').fill('Changed name');
  await ed.testId('inspector-name').blur();
  await ed.expectServer((t) => t.document.tracks[0].items[0].name === 'Changed name', 'the edit is stored');

  await ed.seek(200);
  await openRightTab(ed, /compare/i);
  await ed.tap(ed.testId('compare-other'));
  await ed.tap(ed.page.getByRole('option', { name: /remediation-seeded/ }).first());
  await ed.tap(ed.testId('compare-run'));
  const summary = ed.testId('compare-summary');
  await expect(summary).toContainText(/remediation-seeded/);
  await expect(summary, 'the summary names a change').not.toContainText('No differences');
  const row = ed.page.locator('[data-testid^="compare-item-"]', { hasText: 'Changed name' }).first();
  await expect(row).toBeVisible();
  await expect(ed.testId('status-timecode')).toHaveText('00:00:06:20');
  await ed.tap(row.getByRole('button').first());
  await expect(ed.testId('status-timecode'), 'the timecode button moves the playhead to the clip').toHaveText('00:00:00:00');
  void seed;
});

test('TL-278 Assist: a group\'s timecode button seeks to the clip the group touches', async ({ freshTimeline, openEditor }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  const clipB = (await ed.serverTimeline()).document.tracks[0].items[1]; // clipB starts at frame 150 = 5 s
  await ed.page.route(PROPOSALS, (route) =>
    route.request().method() === 'POST'
      ? route.fulfill({
          status: 200,
          contentType: 'application/json',
          json: { proposalId: 'lane-b-seek', agent: 'recut', summary: 'Rename clipB.', rejected: [], groups: [{ label: 'Rename', reason: null, touchesUserOwned: false, touchedItemIds: [clipB.id], ops: [{ type: 'setProperty', itemId: clipB.id, key: 'name', value: 'x' }] }] },
        })
      : route.continue(),
  );
  await openRightTab(ed, 'tab-assist');
  await ed.tap(ed.testId('proposal-ask'));
  const seekButton = ed.testId('proposal-group-0').getByRole('button', { name: /^\d\d:\d\d:\d\d:\d\d$/ });
  await expect(seekButton).toHaveText('00:00:05:00');
  await expect(ed.testId('status-timecode')).toHaveText('00:00:00:00');
  await ed.tap(seekButton);
  await expect(ed.testId('status-timecode'), 'the playhead moves to the touched clip').toHaveText('00:00:05:00');
});
