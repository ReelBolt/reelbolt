import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { bladeAt, journalKey, stubPatches } from './sync-helpers';

/**
 * Wave 2 T-023 (Wave 1 follow-ups): a sign-out in one tab stops every other tab journalling and leaves no
 * journal on the device; a journal entry older than 30 days is never offered.
 * The "other tab signs out" step mirrors what `wipeEditorLocalData` does (the unit tests cover the helper itself):
 * record the sign-out, broadcast it, delete the journal keys and the database.
 */

const DAY = 24 * 60 * 60 * 1000;

test('T-023 sign-out in tab A stops tab B journalling and leaves no journal on the device', async ({ freshTimeline, openEditor, page, context, seed }) => {
  runOn('desktop');
  const idB = await freshTimeline('seeded');
  const edB = await openEditor(idB);
  const idA = await freshTimeline('seeded');
  const tabA = await context.newPage();
  await tabA.goto(`/app/editor/${seed.projectId}/timeline/${idA}`);
  await expect(tabA.getByTestId('timeline-canvas')).toBeVisible({ timeout: 45_000 });

  await page.bringToFront();
  await stubPatches(page, idB, ['network-failure'], { repeatLast: true });
  await bladeAt(edB, 75);
  await expect.poll(() => page.evaluate((k) => localStorage.getItem(k), journalKey(idB)), { message: "B's edit is journalled" }).not.toBeNull();

  // A copied clip sits on the device under the user's clipboard key; the other tab's sign-out wipe must remove it too.
  await page.evaluate(() => localStorage.setItem('reelbolt:editor:clipboard:e2e@example.com', '{"app":"reelforge-edit-clips"}'));

  await tabA.evaluate(async () => {
    localStorage.setItem('reelbolt:editor:signed-out-at', String(Date.now()));
    for (const k of Object.keys(localStorage)) if (k.startsWith('reelbolt:editor:journal:') || k.startsWith('reelbolt:editor:offline-queue:')) localStorage.removeItem(k);
    const channel = new BroadcastChannel('reelbolt:editor:signout');
    channel.postMessage({ t: 'signout' });
    channel.close();
    await new Promise<void>((resolve) => {
      const req = indexedDB.deleteDatabase('reelbolt-editor');
      req.onsuccess = () => resolve();
      req.onerror = () => resolve();
      req.onblocked = () => resolve();
    });
  });

  const journalState = () =>
    page.evaluate(async () => ({
      keys: Object.keys(localStorage).filter((k) => k.startsWith('reelbolt:editor:journal:') || k.startsWith('reelbolt:editor:offline-queue:') || k.startsWith('reelbolt:editor:clipboard:')),
      dbs: (await indexedDB.databases()).map((d) => d.name).filter((n) => n === 'reelbolt-editor'),
    }));
  await expect.poll(journalState, { message: 'B holds no journal after the sign-out (its connection was released)' }).toEqual({ keys: [], dbs: [] });

  // B is locked behind the sign-in prompt and its pending edit is not written back: nothing reappears on the device.
  await page.bringToFront();
  await expect(edB.chip).not.toHaveText('Saved');
  await page.evaluate(() => { window.dispatchEvent(new Event('pagehide')); document.dispatchEvent(new Event('visibilitychange')); });
  await page.waitForTimeout(1200); // a wrongly scheduled journal write (250 ms) or hide-time write would have landed by now
  expect(await journalState(), 'B refused its next journal write').toEqual({ keys: [], dbs: [] });
});

test.describe('T-023 journal expiry', () => {
  for (const [title, ageMs, offered] of [['an entry older than 30 days is not offered', 31 * DAY, false], ['a recent entry is still offered', DAY, true]] as const) {
    test(`T-023 ${title}`, async ({ freshTimeline, openEditor, page, context, seed }) => {
      runOn('desktop');
      const id = await freshTimeline('seeded');
      const probe = await openEditor(id);
      const server = await probe.serverTimeline();
      const base = server.document;
      const local = structuredClone(base) as typeof base;
      local.tracks[0].items[0].name = 'Recovered edit';
      const entry = {
        kind: 'entry',
        entry: {
          version: 1, projectId: seed.projectId, timelineId: id, baseRevision: server.revision, baseDoc: base, localDoc: local,
          ops: [{ type: 'setProperty', itemId: local.tracks[0].items[0].id, key: 'name', value: 'Recovered edit' }],
          savedAt: Date.now() - ageMs, tabId: 'old-tab', userId: journalKey(id).split(':')[3],
        },
      };
      await page.close();
      const fresh = await context.newPage();
      await fresh.addInitScript(([k, v]) => { if (!localStorage.getItem(k)) localStorage.setItem(k, v); }, [journalKey(id), JSON.stringify(entry)] as const);
      await fresh.goto(`/app/editor/${seed.projectId}/timeline/${id}`);
      await expect(fresh.getByTestId('timeline-canvas')).toBeVisible({ timeout: 45_000 });
      if (offered) await expect(fresh.getByTestId('recovery-banner')).toBeVisible({ timeout: 15_000 });
      else {
        await expect.poll(() => fresh.evaluate((k) => localStorage.getItem(k), journalKey(id)), { message: 'the expired entry was evicted' }).toBeNull();
        await expect(fresh.getByTestId('recovery-banner')).toHaveCount(0);
      }
    });
  }
});
