import { test, expect } from '../../../fixtures/editor-v2';
import { audioTimeline } from '../../../fixtures/editor-v2/audio-lane';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Audio cleanup (T-006): the "Clean voice" panel against stubbed cleanup routes (page.route), so the
 * row needs no audio engine. Desktop only: the phone sheet shares the same panel component.
 * Rows: free flow with estimate "Free" and Apply as one change; first AI run asks for consent;
 * a 402 shows the add-credits refusal.
 */
type Clip = { assetRef?: { kind: string; projectFileId?: string | null } };
const json = (body: unknown, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(body) });

async function stubCleanup(page: import('@playwright/test').Page, cleanId: string, opts: { credits?: number; startStatus?: number } = {}) {
  const starts: unknown[] = [];
  await page.route(/\/media\/[^/]+\/cleanup\/estimate$/, (route) => route.fulfill(json({ billingEnabled: true, hosted: (opts.credits ?? 0) > 0, credits: opts.credits ?? 0, balanceCredits: 50, affordable: true, cached: false })));
  await page.route(/\/media\/[^/]+\/cleanup$/, (route) => {
    starts.push(JSON.parse(route.request().postData() ?? '{}'));
    if (opts.startStatus === 402) return route.fulfill(json({ code: 'INSUFFICIENT_CREDITS', message: 'Not enough credits', missing: 2 }, 402));
    return route.fulfill(json({ executionId: 'x1', target: 'Hum', hosted: false, state: 'queued', credits: null }, 202));
  });
  await page.route(/\/media\/[^/]+\/cleanup\/x1$/, (route) => route.fulfill(json({ executionId: 'x1', state: 'ready', target: 'Hum', applied: true, outputFileId: cleanId })));
  return starts;
}

test('audio cleanup: a free option shows Free, plays before and after, and Apply switches the clip to the cleaned file', async ({ page, freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  // The cleaned file must be a real file of this project: the server refuses an asset reference to one that does not exist.
  const CLEAN = seed.files.clipB;
  const starts = await stubCleanup(page, CLEAN);
  const ed = await openEditor(await audioTimeline(freshTimeline, api, seed, [{ name: 'Voice', clips: [{ name: 'voice-1', recordIn: 0, recordOut: 60 }] }]));
  await ed.workspace('audio');
  await ed.selectClipAt(10, 1);
  await ed.tap(ed.testId('cleanup-choice-hum'));
  await expect(ed.testId('cleanup-estimate')).toContainText(/free/i);
  await ed.tap(ed.testId('cleanup-run'));
  await expect(ed.testId('cleanup-audition')).toBeVisible();
  expect(starts[0], 'a free option never sends the outside-service flag').not.toHaveProperty('allowSourceMediaEgress');
  await ed.tap(ed.testId('cleanup-apply'));
  const tl = await ed.expectServer((t) => t.document.tracks.some((tr) => tr.items.some((i) => (i as unknown as Clip).assetRef?.projectFileId === CLEAN)), 'the clip points at the cleaned file', 40_000);
  expect(tl).toBeTruthy();
  await ed.tap(ed.testId('cleanup-use-original'));
  await ed.expectServer((t) => !t.document.tracks.some((tr) => tr.items.some((i) => (i as unknown as Clip).assetRef?.projectFileId === CLEAN)), 'Use original put the source back', 40_000);
});

test('audio cleanup: the first AI run asks before sending anything', async ({ page, freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const starts = await stubCleanup(page, seed.files.clipB, { credits: 3 });
  const ed = await openEditor(await audioTimeline(freshTimeline, api, seed, [{ name: 'Voice', clips: [{ name: 'voice-1', recordIn: 0, recordOut: 60 }] }]));
  await ed.workspace('audio');
  await ed.selectClipAt(10, 1);
  await ed.tap(ed.testId('cleanup-choice-studio'));
  await expect(ed.testId('cleanup-estimate')).toContainText(/about 3 credits/i);
  await ed.tap(ed.testId('cleanup-run'));
  await expect(ed.testId('cleanup-consent-accept')).toBeVisible();
  expect(starts, 'nothing was sent before the answer').toHaveLength(0);
  await ed.tap(ed.testId('cleanup-consent-accept'));
  await expect.poll(() => starts.length).toBe(1);
  expect(starts[0]).toMatchObject({ allowSourceMediaEgress: true });
});

test('audio cleanup: a 402 shows the add-credits refusal', async ({ page, freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  await stubCleanup(page, seed.files.clipB, { startStatus: 402 });
  const ed = await openEditor(await audioTimeline(freshTimeline, api, seed, [{ name: 'Voice', clips: [{ name: 'voice-1', recordIn: 0, recordOut: 60 }] }]));
  await ed.workspace('audio');
  await ed.selectClipAt(10, 1);
  await ed.tap(ed.testId('cleanup-choice-hum'));
  await ed.tap(ed.testId('cleanup-run'));
  await expect(ed.testId('cleanup-add-credits')).toBeVisible();
});
