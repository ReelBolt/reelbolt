import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { bladeAt, hasCutAt, stubPatches } from './sync-helpers';

/**
 * Wave 1 T-014 (security): after the browser signed in as another user, a tab that still holds user A's pending
 * edits must NEVER send them under B's identity, on any path (autosave, retry, reconnect, keepalive on unload,
 * journal restore), and B must not be offered A's journal.
 *
 * The identity the editor sees is the readable `reelbolt_user` cookie. "Sign in as user B" is staged by writing
 * that cookie in the page (what the app's login response does); the server session stays A's, so the assertions
 * are on what the BROWSER sends (page.on('request')) and on the stored revision, which must not move.
 */

const asUser = (email: string) => encodeURIComponent(JSON.stringify({ email, name: 'User B' }));

test('T-014 pending edits of user A are never PATCHed under user B, and B is not offered A\'s journal', async ({ freshTimeline, openEditor, page, context, seed }) => {
  runOn('desktop');
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  const before = await ed.serverTimeline();
  const sent: string[] = [];
  page.on('request', (req) => {
    if (req.method() !== 'GET' && req.url().includes(`/edit-timelines/${id}`)) sent.push(`${req.method()} ${req.url()}`);
  });

  // A edits; the save cannot reach the server, so the edit stays pending (and journalled for A).
  const stub = await stubPatches(page, id, ['network-failure'], { repeatLast: true });
  await bladeAt(ed, 75);
  await ed.seek(120); // park the playhead for B's edit now: once the account change is noticed the page may lock and reset it
  await expect.poll(() => page.evaluate(() => Object.keys(localStorage).filter((k) => k.startsWith('reelbolt:editor:journal:'))), { message: "A's edit is journalled on this device" }).toHaveLength(1);
  const journalKeys = await page.evaluate(() => Object.keys(localStorage).filter((k) => k.startsWith('reelbolt:editor:journal:')));
  expect(journalKeys[0], 'the journal key carries the user').toMatch(/^reelbolt:editor:journal:[^:]+:/);

  // The browser now says "user B" (signed in elsewhere), and the network works again.
  await context.addCookies([{ name: 'reelbolt_user', value: asUser('user-b@example.test'), url: new URL(page.url()).origin }]);
  await page.evaluate((v) => { document.cookie = `reelbolt_user=${v}; path=/`; }, asUser('user-b@example.test'));
  await stub.heal();
  sent.length = 0;

  // Every trigger that used to send: a new edit (autosave), the page hiding, and leaving the page.
  await page.evaluate(() => {
    const visible = [...document.querySelectorAll<HTMLElement>('[data-testid="blade-at-playhead"], [data-testid="phone-action-split"]')].find((e) => e.offsetParent !== null);
    if (visible) visible.click();
  });
  await page.evaluate(() => {
    window.dispatchEvent(new Event('beforeunload'));
    window.dispatchEvent(new Event('pagehide'));
    window.dispatchEvent(new Event('online'));
    document.dispatchEvent(new Event('visibilitychange'));
  });
  await expect(page.getByTestId('save-state-chip')).not.toHaveText('Saved');
  await expect.poll(() => page.getByText(/signed in as a different account/i).count(), { message: 'the different-account dialog is shown' }).toBeGreaterThan(0);
  await page.waitForTimeout(1500); // a wrongly scheduled autosave (debounce 0.5 s) would have fired by now
  expect(sent, "no PATCH was sent for A's edits under B's session").toEqual([]);
  const after = await ed.serverTimeline();
  expect(after.revision, 'the stored revision did not move').toBe(before.revision);
  expect(hasCutAt(after, 75)).toBe(false);

  // B opens the timeline in a new tab: A's journal is not offered.
  const reopened = await context.newPage();
  await reopened.goto(`/app/editor/${seed.projectId}/timeline/${id}`);
  await expect(reopened.getByTestId('timeline-canvas')).toBeVisible({ timeout: 45_000 });
  await expect(reopened.getByTestId('recovery-banner')).toHaveCount(0);
  expect((await ed.serverTimeline()).revision, 'opening as B wrote nothing').toBe(before.revision);
});
