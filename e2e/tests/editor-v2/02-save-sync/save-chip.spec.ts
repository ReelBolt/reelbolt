import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { bladeAt, expectTapTarget, hasCutAt, otherWriterRenames, stubPatches } from './sync-helpers';

/**
 * Wave 1 T-004, R02.3 the save chip says each failure in plain words, one sentence per class, no revision
 * counters, with a phone-sized Retry where retrying can help. Failures are route stubs of the PATCH.
 */

test('R02.3 saved: "Saved", never a revision number', async ({ freshTimeline, openEditor }) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  const ed = await openEditor(await freshTimeline('seeded'));
  await expect(ed.chip).toHaveText('Saved');
  await bladeAt(ed, 75);
  await ed.expectServer((tl) => hasCutAt(tl, 75), 'the split is stored');
  await expect(ed.chip).toHaveText('Saved');
  await expect(ed.chip).not.toContainText(/rev|\d/);
});

test('R02.3 too large (413): the sentence and Retry; Retry saves once the cause is gone', async ({ freshTimeline, openEditor, page }, testInfo) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  await stubPatches(page, id, [{ status: 413, body: { message: 'the serialized document exceeds the limit' } }]);
  await bladeAt(ed, 75);
  await expect(ed.chip).toContainText('Not saved. This timeline is too large to save.');
  const retry = page.getByTestId('save-retry');
  await expect(retry).toBeVisible();
  if (testInfo.project.name !== 'desktop') await expectTapTarget(retry, 'Retry');
  await retry.click();
  await ed.expectServer((tl) => hasCutAt(tl, 75), 'Retry stores the edit');
  await expect(ed.chip).toHaveText('Saved');
});

test('R02.3 no permission (403) and timeline deleted (404): their own sentences, no Retry', async ({ freshTimeline, openEditor, page }) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  await stubPatches(page, id, [{ status: 403, body: { message: 'forbidden' } }, { status: 404 }]);
  await bladeAt(ed, 75);
  await expect(ed.chip).toHaveText('Not saved. You no longer have permission to change this timeline.');
  await expect(page.getByTestId('save-retry')).toHaveCount(0);
  // The next edit meets a deleted timeline.
  await bladeAt(ed, 100);
  await expect(ed.chip).toHaveText('Not saved. This timeline was deleted.');
  await expect(page.getByTestId('save-retry')).toHaveCount(0);
});

test('R02.3 the server refuses the document (400): the sentence carries the plain reason', async ({ freshTimeline, openEditor, page }, testInfo) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  await stubPatches(page, id, [{ status: 400, body: [{ path: 'tracks[0].items[1].recordIn', message: 'overlaps the previous clip' }] }]);
  await bladeAt(ed, 75);
  await expect(ed.chip).toContainText('Not saved. The server refused this change.');
  await expect(ed.chip).toContainText('Track 1, clip 2: overlaps the previous clip.');
  await expect(ed.chip).not.toContainText(/tracks\[|recordIn|[0-9a-f]{8}-/);
  const retry = page.getByTestId('save-retry');
  if (testInfo.project.name !== 'desktop') await expectTapTarget(retry, 'Retry');
  await retry.click();
  await ed.expectServer((tl) => hasCutAt(tl, 75), 'Retry stores the edit once the stub is gone');
});

test('R02.3 server error (5xx): "Trying again" while it retries, then the sentence and Retry', async ({ freshTimeline, openEditor, page }, testInfo) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  test.setTimeout(150_000);
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  const stub = await stubPatches(page, id, [{ status: 503, body: { message: 'unavailable' } }], { repeatLast: true });
  await bladeAt(ed, 75);
  await expect(ed.chip).toContainText('Can’t reach the server. Trying again…');
  await expect(ed.chip).toHaveText('Not saved. The server had a problem.', { timeout: 90_000 });
  const retry = page.getByTestId('save-retry');
  if (testInfo.project.name !== 'desktop') await expectTapTarget(retry, 'Retry');
  await stub.heal();
  await retry.click();
  await ed.expectServer((tl) => hasCutAt(tl, 75), 'Retry stores the edit once the server is back');
  await expect(ed.chip).toHaveText('Saved');
});

test('R02.3 offline: the sentence says it will save when back, and it does', async ({ freshTimeline, openEditor, page, context }) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  await context.setOffline(true);
  await bladeAt(ed, 75);
  await expect(ed.chip).toContainText('You are offline.');
  await expect(ed.chip).toContainText('will save when you are back');
  await context.setOffline(false);
  await ed.expectServer((tl) => hasCutAt(tl, 75), 'the edit made offline is stored after reconnecting', 40_000);
  await expect(ed.chip).toHaveText('Saved');
  void page;
});

test('R02.3 conflict: the sentence, with no revision numbers', async ({ freshTimeline, openEditor }) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  const ed = await openEditor(await freshTimeline('seeded'));
  await otherWriterRenames(ed, 0, 'Theirs');
  await bladeAt(ed, 75);
  await expect(ed.chip).toHaveText('Not saved. This timeline was changed somewhere else.');
  await expect(ed.chip).not.toContainText(/\d/);
});
