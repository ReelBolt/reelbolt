import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { clipsTimeline, itemNamed, toastWith } from '../../../fixtures/editor-v2/lane-a';
import { runOn, skipSyntheticPointers } from '../../../fixtures/editor-v2/rows';

/**
 * R03 deferred ops (Wave 4, T-008): Slide, Join, Fit to the gap and Replace each have a labelled control on
 * desktop; every test reads the STORED document after a reload and checks one Undo. Phone shows none of them.
 * R11.2: the helper availability and grounding line, with the proposal endpoints stubbed (no provider, no credits).
 * Status: written, pending run (build-only window).
 */
test.use({ serviceWorkers: 'block' });

type Rated = { playbackRate?: number; sourceIn?: number; assetRef?: { projectFileId?: string } };

async function openClipActions(ed: EditorPage): Promise<void> {
  await ed.tap(ed.testId('clip-actions-open'));
  await expect(ed.testId('clip-actions-menu')).toBeVisible();
}

test('R03 slide moves neighbour edges and stores them', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  skipSyntheticPointers();
  const ed = await openEditor(
    await clipsTimeline(freshTimeline, api, seed, [
      { name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 60 },
      { name: 'clipB.mp4', file: 'clipB', recordIn: 60, recordOut: 120, extra: { sourceIn: 30 } },
      { name: 'clipC.mp4', file: 'clipA', recordIn: 120, recordOut: 180, extra: { sourceIn: 30 } },
    ]),
  );
  await ed.selectClipAt(90);
  await ed.tap(ed.testId('trim-mode'));
  await expect(ed.testId('trim-mode-slide'), 'Slide is listed and explained').toContainText(/neighbour/i);
  await ed.tap(ed.testId('trim-mode-slide'));
  const handle = ed.testId('trim-handle-out');
  const box = (await handle.boundingBox())!;
  const x = box.x + box.width / 2;
  const y = box.y + box.height / 2;
  await ed.mouseDrag(x, y, x + 30, y, 16);
  await ed.expectServer((t) => (itemNamed(t, 'clipB.mp4')?.recordIn ?? 60) > 70, 'clipB slid later');
  const tl = await ed.expectServer((t) => (itemNamed(t, 'clipB.mp4')?.recordIn ?? 60) > 70, 'the slide is stored');
  const b = itemNamed(tl, 'clipB.mp4')!;
  expect(b.recordOut - b.recordIn, 'clipB kept its length').toBe(60);
  expect(itemNamed(tl, 'clipC.mp4')!.recordIn, 'clipC absorbed the move').toBe(b.recordOut);
  // Undo history is in memory only (a reload starts empty), so Undo runs BEFORE the reload.
  await ed.tap(ed.testId('undo'));
  await ed.expectServer((t) => itemNamed(t, 'clipB.mp4')?.recordIn === 60 && itemNamed(t, 'clipC.mp4')?.recordIn === 120, 'one Undo restores both neighbours');
  await ed.reload();
  await ed.expectServer((t) => itemNamed(t, 'clipB.mp4')?.recordIn === 60 && itemNamed(t, 'clipC.mp4')?.recordIn === 120, 'the restored layout survives reload');
});

test('R03 join merges two contiguous clips into one', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(
    await clipsTimeline(freshTimeline, api, seed, [
      { name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 30 },
      { name: 'clipA.mp4', file: 'clipA', recordIn: 30, recordOut: 60, extra: { sourceIn: 30 } },
      { name: 'clipB.mp4', file: 'clipB', recordIn: 90, recordOut: 150 },
    ]),
  );
  await ed.selectClipAt(45);
  await openClipActions(ed);
  await expect(ed.testId('clip-action-join'), 'the second clip has a gap after it, so Join says why').toBeDisabled();
  await expect(ed.testId('clip-action-join-reason')).toContainText(/gap/i);
  await ed.page.keyboard.press('Escape');
  await ed.selectClipAt(15);
  await openClipActions(ed);
  await ed.tap(ed.testId('clip-action-join'));
  await ed.expectServer((t) => t.document.tracks[0].items.length === 2, 'the two halves became one clip');
  const tl = await ed.serverTimeline();
  expect([tl.document.tracks[0].items[0].recordIn, tl.document.tracks[0].items[0].recordOut]).toEqual([0, 60]);
  await ed.tap(ed.testId('undo'));
  await ed.expectServer((t) => t.document.tracks[0].items.length === 3, 'one Undo splits them again');
  await ed.reload();
  await ed.expectServer((t) => t.document.tracks[0].items.length === 3, 'the split survives reload');
});

test('R03 fit to fill retimes a clip to the gap', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(
    await clipsTimeline(freshTimeline, api, seed, [
      { name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 60 },
      { name: 'clipB.mp4', file: 'clipB', recordIn: 90, recordOut: 150 },
    ]),
  );
  await ed.selectClipAt(30);
  await openClipActions(ed);
  await ed.tap(ed.testId('clip-action-fit-to-fill'));
  await expect(toastWith(ed, /Speed becomes 67%/), 'the confirm line names the new speed').toBeVisible();
  const tl = await ed.expectServer((t) => itemNamed(t, 'clipA.mp4')?.recordOut === 90, 'clipA now ends where clipB starts');
  expect((itemNamed(tl, 'clipA.mp4') as unknown as Rated).playbackRate).toBeCloseTo(2 / 3, 2);
  await ed.tap(ed.testId('undo'));
  const back = await ed.expectServer((t) => itemNamed(t, 'clipA.mp4')?.recordOut === 60, 'one Undo restores the span');
  expect((itemNamed(back, 'clipA.mp4') as unknown as Rated).playbackRate).toBe(1);
  await ed.reload();
  const after = await ed.expectServer((t) => itemNamed(t, 'clipA.mp4')?.recordOut === 60, 'the restored span survives reload');
  expect((itemNamed(after, 'clipA.mp4') as unknown as Rated).playbackRate).toBe(1);
});

test('R03 replace swaps the source and keeps the span', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, [{ name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 60 }]));
  const before = (itemNamed(await ed.serverTimeline(), 'clipA.mp4') as unknown as Rated).assetRef?.projectFileId;
  await ed.selectClipAt(30);
  await openClipActions(ed);
  await ed.tap(ed.testId('clip-action-replace'));
  await ed.tap(ed.testId('replace-source'));
  await ed.page.getByRole('option', { name: /clipB/i }).first().click();
  await ed.tap(ed.testId('replace-confirm'));
  const tl = await ed.expectServer((t) => (itemNamed(t, 'clipA.mp4') as unknown as Rated | undefined)?.assetRef?.projectFileId !== before, 'the clip plays the other file');
  const clip = itemNamed(tl, 'clipA.mp4')!;
  expect([clip.recordIn, clip.recordOut], 'the span is untouched').toEqual([0, 60]);
  await ed.tap(ed.testId('undo'));
  await ed.expectServer((t) => (itemNamed(t, 'clipA.mp4') as unknown as Rated | undefined)?.assetRef?.projectFileId === before, 'one Undo brings the first file back');
  await ed.reload();
  await ed.expectServer((t) => (itemNamed(t, 'clipA.mp4') as unknown as Rated | undefined)?.assetRef?.projectFileId === before, 'the first file is still stored after reload');
});

test('R03 phone shows no join, fit or replace entries', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('pixel7');
  const ed = await openEditor(
    await clipsTimeline(freshTimeline, api, seed, [
      { name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 60 },
      { name: 'clipB.mp4', file: 'clipB', recordIn: 90, recordOut: 150 },
    ]),
  );
  await ed.selectClipAt(30);
  await ed.tap(ed.testId('phone-action-more'));
  await ed.tap(ed.testId('phone-more-clip'));
  await expect(ed.testId('clip-sheet'), 'the phone clip sheet is open').toBeVisible();
  for (const id of ['join', 'fit-to-fill', 'replace']) {
    await expect(ed.testId(`clip-action-${id}`)).toHaveCount(0);
    await expect(ed.testId(`ctx-${id}`)).toHaveCount(0);
  }
});

const AVAILABILITY = /\/edit-timelines\/[^/]+\/proposals\/availability/;
const ESTIMATE = /\/edit-timelines\/[^/]+\/proposals\/estimate$/;
const PROPOSALS = /\/edit-timelines\/[^/]+\/proposals$/;

test('R11.2 helper availability and grounding line', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('seeded'));
  const grounding = { level: 'namesOnly', clipsWithFacts: 0, clipsTotal: 2, note: 'Cutting by length only: analyse this footage first for a smarter cut.' };
  await ed.page.route(AVAILABILITY, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      json: {
        analysisAvailable: true,
        agents: {
          recut: { available: true, grounding, reason: null },
          grade: { available: false, grounding: null, reason: 'Analyse this footage first so the colourist can see its colours and light.' },
          review: { available: false, grounding: null, reason: 'Analyse this footage first so the reviewer has the words and shots to go on.' },
        },
      },
    }),
  );
  await ed.page.route(ESTIMATE, (route) => route.fulfill({ status: 200, contentType: 'application/json', json: { billingEnabled: false, affordable: true, used: 0, limit: 20 } }));
  await ed.openPanelTab('tab-assist');
  await expect(ed.testId('proposal-unavailable-grade')).toContainText('colourist');
  await expect(ed.testId('proposal-unavailable-review')).toContainText('reviewer');
  await expect(ed.testId('proposal-grounding')).toContainText('Cutting by length only');
  await expect(ed.testId('analyse-first'), 'Analyse first is offered').toBeVisible();

  await ed.page.route(PROPOSALS, (route) =>
    route.request().method() === 'POST'
      ? route.fulfill({
          status: 200,
          contentType: 'application/json',
          json: { proposalId: 'p1', agent: 'recut', summary: 'Nothing to tighten.', groups: [], rejected: [], grounding: { level: 'transcriptAndShots', clipsWithFacts: 5, clipsTotal: 6, note: 'x' }, analysisAvailable: false },
        })
      : route.continue(),
  );
  await ed.tap(ed.testId('proposal-ask'));
  await expect(ed.testId('proposal-grounding')).toHaveText('Used the spoken words and shots of 5 of 6 clips');

  await ed.page.unroute(PROPOSALS);
  await ed.page.route(PROPOSALS, (route) =>
    route.request().method() === 'POST'
      ? route.fulfill({ status: 409, contentType: 'application/json', json: { code: 'PROPOSAL_NEEDS_ANALYSIS', message: 'Analyse this footage first. You were not charged.' } })
      : route.continue(),
  );
  await ed.tap(ed.testId('proposal-ask'));
  await expect(ed.testId('proposal-error')).toHaveText('Analyse this footage first. You were not charged.');
});
