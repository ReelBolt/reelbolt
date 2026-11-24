import type { Page } from '@playwright/test';
import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Video enhance (T-006): the Enhance section of the clip inspector against stubbed routes (page.route), so the rows
 * need no video engine. Desktop only: the phone sheet shares the same component.
 * Rows: a Fast run shows Free and Apply switches every clip on the source in one change, Use original puts it back;
 * the first AI run asks for the project consent (PUT) before anything starts; a 402 plan refusal shows the plan message.
 */
type Clip = { assetRef?: { kind: string; projectFileId?: string | null } };
const json = (body: unknown, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(body) });

/** `outputFileId` must be a REAL file of the test project: Apply writes it into the timeline and the server checks it. */
async function stubEnhance(page: Page, outputFileId: string, opts: { hosted?: boolean; startStatus?: number } = {}) {
  const starts: unknown[] = [];
  const consent: string[] = [];
  let consented = false;
  await page.route(/\/video-enhance\/consent$/, (route) => {
    consent.push(route.request().method());
    if (route.request().method() === 'PUT') consented = true;
    return route.fulfill(json({ consented, consentedAt: consented ? new Date().toISOString() : null }));
  });
  await page.route(/\/media\/[^/]+\/enhance\/estimate$/, (route) =>
    route.fulfill(json({ billingEnabled: true, hosted: !!opts.hosted, credits: opts.hosted ? 3 : 0, seconds: 5, balanceCredits: 50, affordable: true, unavailableReason: null, consented, maxOutputHeight: 1080, outputWidth: 1920, outputHeight: 1080, outputFps: 30, estimatedBytes: 9_000_000, estimatedProcessingSeconds: 30 })),
  );
  await page.route(/\/media\/[^/]+\/enhance$/, (route) => {
    starts.push(JSON.parse(route.request().postData() ?? '{}'));
    if (opts.startStatus === 402) return route.fulfill(json({ code: 'PLAN_NOT_ENTITLED', message: 'Not in plan', feature: 'video_enhance' }, 402));
    return route.fulfill(json({ executionId: 'x1', op: 'Upscale', engine: opts.hosted ? 'Hosted' : 'Local', hosted: !!opts.hosted, state: 'queued', credits: 0 }, 202));
  });
  await page.route(/\/media\/[^/]+\/enhance\/x1$/, (route) =>
    route.fulfill(json({ executionId: 'x1', state: 'ready', op: 'Upscale', engine: 'Local', outputFileId, sourceMediaRef: 'file:src', warnings: [], progress: 100 })),
  );
  return { starts, consent };
}

/** How many clips play `fileId` (the seeded timeline already plays every seeded file once). */
const playing = (t: { document: { tracks: Array<{ items: unknown[] }> } }, fileId: string) => t.document.tracks.flatMap((tr) => tr.items).filter((i) => (i as Clip).assetRef?.projectFileId === fileId).length;

async function pickUpscale(ed: EditorPage) {
  await ed.selectClipAt(30, 0);
  await ed.openInspector();
  await ed.testId('enhance-choice').click();
  await ed.page.getByText('Sharper picture', { exact: true }).click();
}

test('enhance: a Fast run shows Free, compares, and Apply switches the clip to the new file in one change', async ({ page, freshTimeline, openEditor, seed }) => {
  runOn('desktop');
  const { starts } = await stubEnhance(page, seed.files.vertical);
  const ed = await openEditor(await freshTimeline('seeded'));
  await pickUpscale(ed);
  await expect(ed.testId('enhance-estimate')).toContainText(/free/i);
  await ed.testId('enhance-start').click();
  await expect(ed.testId('enhance-compare')).toBeVisible();
  expect(starts[0]).toMatchObject({ op: 'Upscale', engine: 'Local' });
  await ed.testId('enhance-apply').click();
  await ed.expectServer((t) => playing(t, seed.files.vertical) === 2, 'the clip now plays the enhanced file (and the seeded one still does)', 40_000);
  await ed.testId('enhance-use-original').click();
  await ed.expectServer((t) => playing(t, seed.files.vertical) === 1, 'Use original put the source back', 40_000);
});

test('enhance: the first AI run asks for the project consent before anything starts', async ({ page, freshTimeline, openEditor, seed }) => {
  runOn('desktop');
  const { starts, consent } = await stubEnhance(page, seed.files.vertical, { hosted: true });
  const ed = await openEditor(await freshTimeline('seeded'));
  await pickUpscale(ed);
  await ed.testId('enhance-engine').getByText('AI (uses credits)').click();
  await expect(ed.testId('enhance-estimate')).toContainText(/about 3 credits/i);
  await ed.testId('enhance-start').click();
  // The Modal root is a zero-size wrapper: assert on the dialog's own content (role and title).
  await expect(ed.page.getByRole('dialog', { name: 'Send this video to an outside service?' })).toBeVisible();
  expect(starts, 'nothing was sent before the answer').toHaveLength(0);
  await ed.testId('enhance-consent-check').click();
  await ed.testId('enhance-consent-confirm').click();
  await expect.poll(() => starts.length).toBe(1);
  expect(consent).toContain('PUT');
  expect(starts[0]).toMatchObject({ engine: 'Hosted' });
});

test('enhance: a 402 plan refusal shows the plan message', async ({ page, freshTimeline, openEditor, seed }) => {
  runOn('desktop');
  await stubEnhance(page, seed.files.vertical, { startStatus: 402 });
  const ed = await openEditor(await freshTimeline('seeded'));
  await pickUpscale(ed);
  await ed.testId('enhance-start').click();
  await expect(ed.testId('enhance-see-plans')).toBeVisible();
});
