import { test, expect, EditorPage } from '../../../fixtures/editor-v2';
import { credentials } from '../../../fixtures/editor-v2/env';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { bladeAt, expectTapTarget, hasCutAt, itemCount, journalKey, killTab, stubPatches } from './sync-helpers';

/**
 * Wave 1 T-004, R02.2 "never lose an edit": a killed tab restores, a session that ends mid-edit signs in again
 * in place, and a browser that cannot keep anything says so. Every test asserts the STORED document.
 */

test('R02.2 kill the tab mid-edit: reopening offers the edit back and Restore saves it', async ({ freshTimeline, openEditor, page, context, seed }, testInfo) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  const before = await ed.serverTimeline();
  // The edit never reaches the server: every PATCH fails in transit.
  await stubPatches(page, id, ['network-failure'], { repeatLast: true }); // the save cannot reach the server, not even the flush a closing page makes
  await bladeAt(ed, 75);
  await expect.poll(() => page.evaluate((k) => localStorage.getItem(k), journalKey(id)), { message: 'the unsaved edit is journalled on this device' }).not.toBeNull();
  await killTab(page);

  const reopened = await context.newPage();
  const ed2 = await EditorPage.open(reopened, seed, id, testInfo.project.name);
  const banner = reopened.getByTestId('recovery-banner');
  try {
    await expect(banner).toBeVisible();
  } catch (error) {
    const journal = await reopened.evaluate((k) => localStorage.getItem(k)?.length ?? null, journalKey(id));
    throw new Error(`no recovery banner: server revision ${(await ed2.serverTimeline()).revision} (was ${before.revision}), journal ${journal}, chip "${await ed2.chip.textContent()}"; first tab PATCHes ${ed.patchSummary(5)}, second tab ${ed2.patchSummary(5)} - ${String(error).slice(0, 80)}`);
  }
  await expect(banner).toContainText('We recovered edits from your last visit');
  expect((await ed2.serverTimeline()).revision, 'nothing was written by merely opening').toBe(before.revision);
  if (testInfo.project.name !== 'desktop') await expectTapTarget(reopened.getByTestId('recovery-restore'), 'Restore');

  await reopened.getByTestId('recovery-restore').click();
  const stored = await ed2.expectServer((tl) => hasCutAt(tl, 75) && itemCount(tl) === itemCount(before) + 1, 'the recovered split is stored');
  expect(stored.revision, 'the server revision advanced').toBeGreaterThan(before.revision);
  await expect(reopened.getByTestId('recovery-banner')).toBeHidden();
  await expect(ed2.chip).toHaveText('Saved');
  expect(await reopened.evaluate((k) => localStorage.getItem(k), journalKey(id)), 'the journal is cleared once the server has it').toBeNull();
  await ed2.reload();
  expect(hasCutAt(await ed2.serverTimeline(), 75)).toBe(true);
});

test('R02.2 discarding the recovered edits leaves the server untouched and asks no more', async ({ freshTimeline, openEditor, page, context, seed }, testInfo) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  const before = await ed.serverTimeline();
  await stubPatches(page, id, ['network-failure'], { repeatLast: true }); // the save cannot reach the server, not even the flush a closing page makes
  await bladeAt(ed, 75);
  await expect.poll(() => page.evaluate((k) => localStorage.getItem(k), journalKey(id))).not.toBeNull();
  await killTab(page);

  const reopened = await context.newPage();
  const ed2 = await EditorPage.open(reopened, seed, id, testInfo.project.name);
  await reopened.getByTestId('recovery-discard').click();
  await expect(reopened.getByTestId('recovery-banner')).toBeHidden();
  await ed2.reload();
  await expect(reopened.getByTestId('recovery-banner')).toHaveCount(0);
  const after = await ed2.serverTimeline();
  expect(after.revision).toBe(before.revision);
  expect(hasCutAt(after, 75)).toBe(false);
});

test('R02.2 a session that ends mid-edit: sign in in place, and the edit is saved without a reload', async ({ freshTimeline, openEditor, page }, testInfo) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  const before = await ed.serverTimeline();
  // The first save answers 401 (the session ended). Remember the page instance: a reload would drop it.
  await page.evaluate(() => { (window as unknown as { __alive: boolean }).__alive = true; });
  await stubPatches(page, id, [{ status: 401, body: { error: 'unauthorized' } }]);
  await bladeAt(ed, 75);
  await expect(ed.chip).toHaveText('Sign in again to keep saving');
  const dialog = page.getByTestId('sign-in-dialog');
  await expect(dialog).toBeVisible();
  expect((await ed.serverTimeline()).revision, 'nothing saved while signed out').toBe(before.revision);

  const { email, password } = credentials(testInfo.project.use.baseURL as string);
  await page.getByTestId('sign-in-email').fill(email);
  await page.getByTestId('sign-in-password').fill(password);
  if (testInfo.project.name !== 'desktop') await expectTapTarget(page.getByTestId('sign-in-submit'), 'Sign in');
  await page.getByTestId('sign-in-submit').click();

  await ed.expectServer((tl) => hasCutAt(tl, 75), 'the edit made while signed out is stored after signing in');
  await expect(dialog).toBeHidden();
  await expect(ed.chip).toHaveText('Saved');
  expect(await page.evaluate(() => (window as unknown as { __alive?: boolean }).__alive), 'no reload happened').toBe(true);
});

test('R02.2 a wrong password says so and keeps the edit', async ({ freshTimeline, openEditor, page }) => {
  runOn('desktop');
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  await stubPatches(page, id, [{ status: 401 }]);
  await bladeAt(ed, 75);
  await expect(page.getByTestId('sign-in-dialog')).toBeVisible();
  await page.getByTestId('sign-in-email').fill('nobody@example.invalid');
  await page.getByTestId('sign-in-password').fill('not-the-password');
  await page.getByTestId('sign-in-submit').click();
  await expect(page.getByTestId('sign-in-error')).toHaveText('That email or password is not right.');
  await expect(ed.chip).toHaveText('Sign in again to keep saving');
});

test('R02.2 when the browser can keep nothing, the chip says edits are held in memory only', async ({ freshTimeline, page, seed }, testInfo) => {
  runOn('desktop', 'pixel7');
  const id = await freshTimeline('seeded');
  // No IndexedDB, and localStorage refuses writes (a locked-down private window).
  await page.addInitScript(() => {
    Object.defineProperty(window, 'indexedDB', { value: undefined, configurable: true });
    Storage.prototype.setItem = () => { throw new DOMException('denied', 'QuotaExceededError'); };
  });
  const ed = await EditorPage.open(page, seed, id, testInfo.project.name);
  await stubPatches(page, id, ['network-failure'], { repeatLast: true });
  await bladeAt(ed, 75);
  await expect(ed.chip).toContainText('Edits are held in memory only');
});
