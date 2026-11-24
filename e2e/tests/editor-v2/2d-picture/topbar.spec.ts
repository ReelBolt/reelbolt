import { test, expect, openFresh, savedBadge, getFull, pollPicture, isPhone, press, stageOf, layerRow } from '../../../fixtures/editor-v2/picture-lane';
import { picturesUrl } from '../../../fixtures/editor-v2/picture';
import { runOn } from '../../../fixtures/editor-v2/rows';

/** Lane D, picture editor: top bar, save pipeline and shortcuts (inventory rows PE-020..PE-031). */

// The app registers a service worker; Playwright WebKit cannot route requests it handles, so route stubs need it blocked (as 09-analysis/transcript does).
test.use({ serviceWorkers: 'block' });

test('PE-020 PE-021 Undo and Redo revert and restore an edit on the page and on the server', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe020-${testInfo.project.name}`);
  const layerId = pic.layers[0].id;
  const undo = page.getByRole('button', { name: 'Undo', exact: true });
  const redo = page.getByRole('button', { name: 'Redo', exact: true });
  await expect(undo, 'nothing to undo on a fresh picture').toBeDisabled();
  await expect(redo).toBeDisabled();

  await press(page.getByRole('button', { name: /^Hide / }), phone);
  await expect(undo).toBeEnabled();
  await pollPicture(api, pics.projectId, pic.id, (p) => p.layers.find((l) => l.id === layerId)?.visible === false, 'the layer is hidden on the server');

  await press(undo, phone);
  await expect(page.getByRole('button', { name: /^Hide / }), 'undo shows the layer again').toBeVisible();
  await pollPicture(api, pics.projectId, pic.id, (p) => p.layers.find((l) => l.id === layerId)?.visible === true, 'undo re-saved the layer as visible');
  await expect(redo).toBeEnabled();

  await press(redo, phone);
  await pollPicture(api, pics.projectId, pic.id, (p) => p.layers.find((l) => l.id === layerId)?.visible === false, 'redo hid it on the server again');
  await expect(page.getByRole('button', { name: /^Show / })).toBeVisible();
});

test('PE-020p PE-025 the top bar and the tool row are 44 px targets on a phone, and Export opens the dialog', async ({ page, api, pics }, testInfo) => {
  runOn('pixel7', 'iphone14webkit');
  const phone = isPhone(testInfo.project.name);
  await openFresh(page, api, pics, `pe020p-${testInfo.project.name}`);
  const names = ['Undo', 'Redo', 'Export', 'Select and move', 'Move around the canvas', 'Text', 'Cut out a subject', 'Brush mask'];
  for (const name of names) {
    const box = await page.getByRole('button', { name, exact: true }).first().boundingBox();
    expect(box, `${name} is laid out`).toBeTruthy();
    expect(Math.min(box!.width, box!.height), `${name} is at least 44 px (${box!.width} x ${box!.height})`).toBeGreaterThanOrEqual(43.5);
  }
  // The flip buttons appear with a layer selected.
  await press(page.getByRole('list', { name: 'Layers, front first' }).getByRole('button', { name: /^Select / }).first(), phone);
  for (const name of ['Flip horizontally', 'Flip vertically']) {
    const box = await page.getByRole('button', { name }).boundingBox();
    expect(Math.min(box!.width, box!.height), `${name} is at least 44 px`).toBeGreaterThanOrEqual(43.5);
  }
  await press(page.getByRole('button', { name: 'Export', exact: true }), phone);
  await expect(page.getByRole('dialog', { name: 'Export your picture' })).toBeVisible();
});

test('PE-022 Ctrl+Z, Ctrl+Shift+Z and Ctrl+Y undo and redo, but not while typing in a field', async ({ page, api, pics }) => {
  runOn('desktop');
  const { pic } = await openFresh(page, api, pics, 'pe022');
  const layerId = pic.layers[0].id;
  const visible = (v: boolean) => pollPicture(api, pics.projectId, pic.id, (p) => p.layers.find((l) => l.id === layerId)?.visible === v, `layer visible=${v}`);

  await page.getByRole('list', { name: 'Layers, front first' }).getByRole('button', { name: /^Select / }).first().click();
  await page.getByRole('button', { name: /^Hide / }).click();
  await visible(false);
  await savedBadge(page);

  // Typing in the layer's Name field: Ctrl+Z belongs to the field, not to the editor.
  const name = page.getByLabel('Name', { exact: true });
  await name.focus();
  await page.keyboard.press('Control+z');
  await savedBadge(page);
  await page.waitForTimeout(1200);
  expect((await getFull(api, pics.projectId, pic.id)).layers.find((l) => l.id === layerId)!.visible, 'typing context: the editor did not undo').toBe(false);

  // Focus on the canvas: Ctrl+Z undoes, Ctrl+Shift+Z and Ctrl+Y redo.
  await stageOf(page).focus();
  await page.keyboard.press('Control+z');
  await visible(true);
  await page.keyboard.press('Control+Shift+z');
  await visible(false);
  await page.keyboard.press('Control+z');
  await visible(true);
  await page.keyboard.press('Control+y');
  await visible(false);
});

test('PE-023 PE-027 PE-031 the save badge follows an edit; a failed save is shown and retried; edits are batched', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe023-${testInfo.project.name}`);
  const layerId = pic.layers[0].id;
  const layerUrl = new RegExp(`/image-documents/${pic.id}(/layers/[^/]+)?$`);

  // Several quick edits: one saved state equal to the last, in fewer requests than edits.
  const patches: string[] = [];
  page.on('request', (r) => { if (r.method() === 'PATCH' && layerUrl.test(new URL(r.url()).pathname)) patches.push(r.url()); });
  const hide = () => press(page.getByRole('button', { name: /^Hide / }), phone);
  const show = () => press(page.getByRole('button', { name: /^Show / }), phone);
  await hide();
  await expect(page.getByRole('status').filter({ hasText: /Unsaved changes|Saving/ }).first(), 'an edit shows as unsaved first').toBeVisible();
  await show();
  await hide();
  await savedBadge(page);
  expect((await getFull(api, pics.projectId, pic.id)).layers.find((l) => l.id === layerId)!.visible, 'the last edit won').toBe(false);
  expect(patches.length, `three edits went out in ${patches.length} request(s), batched`).toBeLessThan(3);

  // A save that fails: the badge says so, the alert offers Try again, and the retry saves it.
  await page.route(layerUrl, (route) => (route.request().method() === 'PATCH' ? route.abort('failed') : route.continue()));
  await show();
  await expect(page.getByRole('status').filter({ hasText: 'Not saved' }).first()).toBeVisible({ timeout: 20_000 });
  await expect(page.getByText('Your changes could not be saved')).toBeVisible();
  await page.unroute(layerUrl);
  await press(page.getByRole('button', { name: 'Try again' }), phone);
  await savedBadge(page);
  await pollPicture(api, pics.projectId, pic.id, (p) => p.layers.find((l) => l.id === layerId)?.visible === true, 'the retried edit reached the server');
  await expect(page.getByText('Your changes could not be saved')).toHaveCount(0);
});

test('PE-026 a change made somewhere else shows a notice, reloads the picture, and the notice can be closed', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe026-${testInfo.project.name}`);
  // Someone else changes the document (revision advances) while this editor holds the old one.
  await api.json('PATCH', picturesUrl(pics.projectId, pic.id), { name: `elsewhere-${Date.now()}` });
  await press(page.getByRole('button', { name: /^Hide / }), phone);
  const notice = page.getByText(/changed somewhere else/i);
  await expect(notice, 'a conflicting save says the picture was changed elsewhere').toBeVisible({ timeout: 20_000 });
  await press(notice.locator('xpath=ancestor::*[@role="alert"][1]').getByRole('button').first(), phone);
  await expect(notice).toHaveCount(0);
  await savedBadge(page);
});

test('PE-029 leaving with unsaved edits raises the browser prompt', async ({ page, api, pics }) => {
  runOn('desktop');
  const { pic } = await openFresh(page, api, pics, 'pe029');
  // Make the save fail so the edit stays unsaved.
  await page.route(new RegExp(`/image-documents/${pic.id}(/layers/[^/]+)?$`), (route) => (route.request().method() === 'PATCH' ? route.abort('failed') : route.continue()));
  await page.getByRole('button', { name: /^Hide / }).click();
  await expect(page.getByRole('status').filter({ hasText: /Unsaved|Saving|Not saved/ }).first()).toBeVisible();
  const prompted = new Promise<string>((resolve) => page.once('dialog', (d) => { resolve(d.type()); void d.dismiss(); }));
  await page.evaluate(() => window.dispatchEvent(new Event('beforeunload', { cancelable: true })));
  const flagged = await page.evaluate(() => {
    const e = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(e);
    return e.defaultPrevented;
  });
  expect(flagged, 'the editor asks the browser to confirm leaving').toBe(true);
  void prompted;
});

test('PE-030 an edit followed at once by leaving the page is kept', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe030-${testInfo.project.name}`);
  const layerId = pic.layers[0].id;
  await press(page.getByRole('button', { name: /^Hide / }), phone);
  await page.goto('/app/editor');
  await pollPicture(api, pics.projectId, pic.id, (p) => p.layers.find((l) => l.id === layerId)?.visible === false, 'the edit was flushed on leave');
  void stageOf; void layerRow;
});
