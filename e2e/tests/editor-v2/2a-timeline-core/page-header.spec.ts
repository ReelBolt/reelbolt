import { test, expect } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline, countItems, itemNamed } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Lane A, sections 2.A (page states) and 2.B (header). Real outcomes: the right state for each server
 * answer, the stored document / preferences after a header control, the URL after a link.
 */

const GUID = '00000000-0000-4000-8000-0000000000aa';

// A service worker would answer the stubbed PATCH on a phone before page.route sees it.
test.use({ serviceWorkers: 'block' });

test('TL-001 TL-004 loading, then a failed load offers Try again (stub: GET 500, then the real server)', async ({ page, seed, openEditor, freshTimeline }) => {
  const id = await freshTimeline('blank');
  const url = `/app/editor/${seed.projectId}/timeline/${id}`;
  let fail = true;
  let release!: () => void;
  const gate = new Promise<void>((r) => (release = r));
  await page.route(new RegExp(`/edit-timelines/${id}$`), async (route) => {
    if (route.request().method() !== 'GET') return route.continue();
    await gate;
    if (fail) return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: 'stubbed failure' }) });
    return route.continue();
  });
  await page.goto(url);
  await expect(page.getByTestId('timeline-loading'), 'the loading state shows while the GET is pending').toBeVisible();
  release();
  await expect(page.getByTestId('timeline-load-error'), 'a 500 shows the load error card').toBeVisible();
  fail = false;
  await page.getByRole('button', { name: /try again/i }).click();
  await expect(page.getByTestId('editor-workspace'), 'Try again loads the timeline').toBeVisible({ timeout: 45_000 });
  void openEditor;
});

test('TL-002 TL-005 an unknown timeline says "not found" and All timelines goes to the hub', async ({ page, seed }) => {
  await page.goto(`/app/editor/${seed.projectId}/timeline/${GUID}`);
  await expect(page.getByTestId('timeline-not-found')).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('timeline-not-found')).toContainText(/not found/i);
  await page.getByTestId('timeline-not-found').getByRole('link', { name: /all timelines/i }).or(page.getByRole('link', { name: /all timelines/i })).first().click();
  await expect(page).toHaveURL(new RegExp(`/app/editor\\?project=${seed.projectId}`));
});

test('TL-003 a 403 says "no access", not "not found" (stub: GET 403)', async ({ page, seed, freshTimeline }) => {
  const id = await freshTimeline('blank');
  await page.route(new RegExp(`/edit-timelines/${id}$`), (route) =>
    route.request().method() === 'GET' ? route.fulfill({ status: 403, contentType: 'application/json', body: JSON.stringify({ message: 'forbidden' }) }) : route.continue(),
  );
  await page.goto(`/app/editor/${seed.projectId}/timeline/${id}`);
  await expect(page.getByTestId('timeline-forbidden')).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('timeline-forbidden')).toContainText(/access/i);
  await expect(page.getByTestId('timeline-not-found')).toHaveCount(0);
});

test('TL-010 TL-011 TL-012 the header: name and revision follow an edit, Back goes to the hub, the Early preview label explains itself', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  const back = ed.isPhone ? ed.testId('phone-back') : ed.testId('back-to-timelines');
  const header = ed.page.locator('header').filter({ has: back });
  await expect(header).toContainText('rem-');
  const rev0 = (await ed.serverTimeline()).revision;
  // The phone header shows the name and the save state, not the revision number.
  if (!ed.isPhone) await expect(header).toContainText(`Version ${rev0}`);

  // Edit (lift via the Clip menu), then the header revision equals the stored revision.
  await ed.selectClipAt(30);
  await ed.clipAction('lift');
  const tl = await ed.expectServer((t) => countItems(t) === 1, 'the edit is stored');
  expect(tl.revision).toBeGreaterThan(rev0);
  if (!ed.isPhone) await expect(header).toContainText(`Version ${tl.revision}`);

  // The label is on screen and opens its explanation (click, and Enter from the keyboard).
  const label = ed.testId('early-preview-label');
  await expect(label).toBeVisible();
  await label.click();
  await expect(ed.page.getByText(/early preview/i).nth(1).or(ed.page.getByRole('dialog'))).toBeVisible();
  await ed.page.keyboard.press('Escape');

  await ed.tap(back);
  await expect(ed.page).toHaveURL(new RegExp(`/app/editor\\?project=${seed.projectId}`));
});

test('TL-013..019 TL-147 every workspace switch changes the view only (no PATCH, same revision) and shows its own tools', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet'); // the phone has no workspace bar (dock sheets instead, no Cut or Compose): x1-mobile-model verdict; the dock is covered in 30-phone
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  const rev = (await ed.serverTimeline()).revision;
  const shows: Record<string, string> = {
    media: 'media-pool',
    cut: 'cut-workspace',
    edit: 'media-pool', // R05.8: the Edit workspace opens on the media pool (a phone keeps the timeline)
    compose: 'compose-workspace',
    color: 'color-workspace',
    audio: 'mixer-panel',
    deliver: 'deliver-workspace',
  };
  for (const [name, testId] of Object.entries(shows)) {
    await ed.workspace(name as 'media');
    await expect(ed.page.getByTestId('editor-workspace')).toHaveAttribute('data-workspace', name);
    // On a phone the Edit workspace shows the timeline (its panels sit behind the Panels switch).
    const tool = ed.isPhone && name === 'edit' ? 'timeline-canvas' : testId;
    await expect(ed.page.getByTestId(tool).first(), `${name}: its own tools are on screen`).toBeVisible({ timeout: 15_000 });
  }
  expect(ed.patches, 'switching workspaces writes nothing').toEqual([]);
  expect((await ed.serverTimeline()).revision, 'and the revision did not move').toBe(rev);
});

test('TL-020 TL-029 the Review page link opens the timeline\'s review page', async ({ freshTimeline, openEditor, api, seed }) => {
  const id = await clipsTimeline(freshTimeline, api, seed, PAIR);
  const ed = await openEditor(id);
  if (ed.isPhone) {
    await ed.tap(ed.testId('phone-more'));
    await ed.tap(ed.page.getByTestId('phone-more-review-page'));
  } else {
    await ed.tap(ed.testId('open-review-page'));
  }
  await expect(ed.page).toHaveURL(new RegExp(`/app/editor/${seed.projectId}/review/${id}`));
});

test('TL-026 TL-027 TL-028 Export (button on desktop, More menu on a phone) opens Deliver without touching the document', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  if (ed.isPhone) {
    await ed.tap(ed.testId('phone-export')); // the header's Export button opens the Deliver sheet
    await expect(ed.page.getByTestId('phone-sheet')).toBeVisible();
  } else {
    await ed.tap(ed.testId('open-deliver'));
    await expect(ed.page.getByTestId('editor-workspace')).toHaveAttribute('data-workspace', 'deliver');
    await expect(ed.page.getByTestId('deliver-workspace').first()).toBeVisible();
  }
  expect(ed.patches).toEqual([]);
});

test('TL-025 the Keyboard shortcuts button opens the cheatsheet listing every action', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await ed.tap(ed.testId('open-keymap'));
  const dialog = ed.page.getByRole('dialog', { name: 'Keyboard shortcuts' });
  await expect(dialog).toBeVisible();
  // T-007: the read-only cheatsheet lists all 77 actions; Edit shortcuts opens the editable keymap.
  await expect(dialog.locator('[data-testid^="shortcuts-row-"]')).toHaveCount(77);
  await expect(dialog.getByTestId('shortcuts-row-deleteSelection')).toContainText('Delete');
  await expect(dialog.getByTestId('shortcuts-row-liftSelection')).toContainText('Shift');
  await ed.tap(dialog.getByTestId('shortcuts-edit'));
  await expect(ed.page.getByRole('dialog', { name: 'Keyboard shortcuts' })).toBeVisible();
});

test.describe('layouts (desktop)', () => {
  test('TL-021..024 save a layout, change the panels, apply it back; deleting asks first', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop');
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    const name = `lane-a-${Date.now() % 100000}`;
    const left = ed.testId('panel-left');
    const width0 = (await left.boundingBox())!.width;

    // Save the layout as it is now.
    await ed.tap(ed.testId('layout-menu'));
    await ed.testId('layout-name').fill(name);
    await ed.tap(ed.testId('layout-save'));
    await expect.poll(async () => {
      const prefs = await api.json<{ layouts?: Array<{ name: string }> } | undefined>('GET', '/api/v1/editor/preferences', undefined, [200, 204, 404]);
      return (prefs?.layouts ?? []).some((l) => l.name === name);
    }, { message: 'the preferences hold the saved layout' }).toBe(true);
    await ed.page.keyboard.press('Escape');

    // Make the left panel wider with the keyboard on its separator, then apply the saved layout.
    const sep = ed.page.getByRole('separator', { name: 'Resize left panel' });
    await sep.focus();
    for (let i = 0; i < 4; i++) await ed.page.keyboard.press('ArrowRight');
    await expect.poll(async () => (await left.boundingBox())!.width).toBeGreaterThan(width0 + 40);
    await ed.tap(ed.testId('layout-menu'));
    await ed.tap(ed.testId(`layout-apply-${name}`));
    await ed.page.keyboard.press('Escape');
    await expect.poll(async () => Math.round((await left.boundingBox())!.width), { message: 'Apply puts the left panel back' }).toBeCloseTo(width0, -1);

    // Delete: Keep leaves it, Delete removes it (after the question).
    await expect(ed.testId(`layout-apply-${name}`)).toBeHidden();
    await ed.tap(ed.testId('layout-menu'));
    await ed.tap(ed.testId(`layout-delete-${name}`));
    await expect(ed.testId(`layout-delete-confirm-row-${name}`)).toBeVisible();
    await ed.tap(ed.testId(`layout-delete-keep-${name}`));
    await expect(ed.testId(`layout-apply-${name}`), 'Keep leaves the layout').toBeVisible();
    await ed.tap(ed.testId(`layout-delete-${name}`));
    await ed.tap(ed.testId(`layout-delete-confirm-${name}`));
    await expect.poll(async () => {
      const prefs = await api.json<{ layouts?: Array<{ name: string }> } | undefined>('GET', '/api/v1/editor/preferences', undefined, [200, 204, 404]);
      return (prefs?.layouts ?? []).some((l) => l.name === name);
    }, { message: 'the layout is gone from the preferences' }).toBe(false);
  });
});

test('TL-030 TL-037 a revision bumped elsewhere makes the page read-only, with Reload latest to recover', async ({ freshTimeline, openEditor, api, seed }) => {
  const id = await clipsTimeline(freshTimeline, api, seed, PAIR);
  const ed = await openEditor(id);
  // Another writer advances the revision.
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.name = `${doc.name}-elsewhere`;
  });
  // This tab edits: the PATCH is refused with 409 and the page freezes.
  await ed.selectClipAt(30);
  await ed.clipAction('lift');
  // The phone shows the conflict as its own bar (conflict-dialog) instead of the status-bar chip and the banner.
  if (!ed.isPhone) {
    await expect(ed.testId('read-only-chip')).toBeVisible({ timeout: 30_000 });
    await expect(ed.testId('read-only-banner')).toBeVisible();
  }
  const serverAfterConflict = await ed.serverTimeline();
  expect(countItems(serverAfterConflict), 'the stale edit was NOT written').toBe(2);
  // The conflict dialog opens on its own (R02.1); "Use the latest" saves a recovery file, then loads what the server has.
  await expect(ed.testId('conflict-dialog')).toBeVisible({ timeout: 30_000 });
  await ed.tap(ed.testId('conflict-use-latest'));
  await expect(ed.testId('read-only-banner')).toHaveCount(0, { timeout: 30_000 });
  await expect(ed.testId('read-only-chip')).toHaveCount(0);
  // The stale edit was journalled on this device; the page may offer it back. Not wanted here: discard it.
  // The offer arrives a beat after the reload (on a phone it pushes the lanes down): wait for it rather than sampling once.
  if (await ed.testId('recovery-banner').waitFor({ state: 'visible', timeout: 4000 }).then(() => true, () => false)) await ed.tap(ed.testId('recovery-discard'));
  // Editing works again from the latest revision.
  await ed.showTimeline();
  await ed.selectClipAt(30);
  await ed.clipAction('lift');
  await ed.expectServer((t) => countItems(t) === 1, 'after Reload the edit is stored');
});

test('TL-031 offline: the banner shows, edits are kept, and they are stored once the connection returns', async ({ freshTimeline, openEditor, api, seed, context }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await context.setOffline(true);
  await ed.page.evaluate(() => window.dispatchEvent(new Event('offline')));
  await ed.selectClipAt(30);
  await ed.clipAction('lift');
  await expect(ed.testId('offline-banner')).toBeVisible({ timeout: 30_000 });
  expect(countItems(await ed.serverTimeline()), 'nothing reached the server while offline').toBe(2);
  await context.setOffline(false);
  await ed.page.evaluate(() => window.dispatchEvent(new Event('online')));
  await expect(ed.testId('offline-banner')).toHaveCount(0, { timeout: 45_000 });
  await ed.expectServer((t) => countItems(t) === 1, 'the queued edit was flushed after reconnecting', 45_000);
  await ed.reload();
  expect(countItems(await ed.serverTimeline()), 'and survives reload').toBe(1);
});

test('TL-032 TL-033 offline edits that cannot be merged: download them, or discard and reload', async ({ freshTimeline, openEditor, api, seed, context }) => {
  runOn('desktop');
  const id = await clipsTimeline(freshTimeline, api, seed, PAIR);
  const ed = await openEditor(id);
  await context.setOffline(true);
  await ed.page.evaluate(() => window.dispatchEvent(new Event('offline')));
  // Offline: lift clipA (queued locally).
  await ed.selectClipAt(30);
  await ed.clipAction('lift');
  await expect(ed.testId('offline-banner')).toBeVisible({ timeout: 30_000 });
  // Meanwhile somebody else rewrites clipA on the server (the queued edit no longer commutes).
  await api.putDocument(seed.projectId, id, (doc) => {
    const a = doc.tracks[0].items.find((i) => i.name === 'clipA.mp4')!;
    a.recordOut = 45;
    a.name = 'clipA-renamed.mp4';
  });
  await context.setOffline(false);
  await ed.page.evaluate(() => window.dispatchEvent(new Event('online')));
  const refused = ed.testId('offline-refused-banner');
  await expect(refused, 'edits that cannot be rebased are refused with a banner').toBeVisible({ timeout: 45_000 });
  const [download] = await Promise.all([ed.page.waitForEvent('download'), ed.tap(ed.testId('offline-download'))]);
  expect(download.suggestedFilename()).toMatch(/offline-edits\.json$/);
  await ed.tap(ed.testId('offline-discard'));
  await expect(ed.canvas).toBeVisible({ timeout: 45_000 });
  await expect(refused).toHaveCount(0, { timeout: 45_000 });
  const tl = await ed.serverTimeline();
  expect(itemNamed(tl, 'clipA-renamed.mp4'), 'the page reloaded at the server\'s revision (the queued lift was dropped)').toBeTruthy();
});

test('TL-034 TL-035 TL-036 a refused save says why and Retry stores it (stub: PATCH 400, then the real server)', async ({ freshTimeline, openEditor, api, seed, page }) => {
  // Playwright's WebKit does not intercept the sync client's PATCH with page.route (the edit was stored
  // for real in the first run), so the refused-save journey is checked on the two Chromium projects.
  runOn('desktop', 'pixel7');
  const id = await clipsTimeline(freshTimeline, api, seed, PAIR);
  const ed = await openEditor(id);
  let refuse = true;
  await page.route(new RegExp(`/edit-timelines/${id}$`), (route) => {
    if (route.request().method() !== 'PATCH' || !refuse) return route.continue();
    return route.fulfill({ status: 400, contentType: 'application/json', body: JSON.stringify({ message: 'stubbed validation failure', errors: ['stubbed reason'] }) });
  });
  await ed.selectClipAt(30);
  await ed.clipAction('lift');
  await expect(ed.chip, 'the refusal is in (not just a pending save)').toHaveAttribute('data-status', 'rejected', { timeout: 30_000 });
  // The phone header has no reasons popover: its chip carries the state and its Retry button stores the edit.
  if (!ed.isPhone) {
    await expect(ed.testId('save-reasons-toggle')).toBeVisible();
    await ed.tap(ed.testId('save-reasons-toggle'));
    await expect(ed.testId('save-reasons')).toBeVisible();
    expect(await ed.page.getByTestId('save-reason').count()).toBeGreaterThanOrEqual(1);
  }
  expect(countItems(await ed.serverTimeline()), 'nothing stored yet').toBe(2);
  refuse = false;
  await ed.tap(ed.testId(ed.isPhone ? 'phone-retry' : 'save-retry'));
  await ed.expectServer((t) => countItems(t) === 1, 'Retry stored the edit');
  await expect(ed.chip).toHaveText(/^\s*Saved/);
});

test('TL-038 TL-039 TL-041 the status bar shows the dropped-frame counter, the session kind and the playhead timecode on every layout', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet'); // the phone shows the timecode in the strip and the dropped-frame warning only when it happens (phone-layout.spec.ts)
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await expect(ed.testId('dropped-frames')).toHaveText(/^Skipped frames: \d+/);
  await expect(ed.testId('session-kind')).toHaveText(/Live|Simple|No preview/);
  await ed.seek(75);
  await expect(ed.testId('status-timecode')).toHaveText('00:00:02:15');
});
