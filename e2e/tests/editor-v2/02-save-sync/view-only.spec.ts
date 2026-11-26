import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { journalKey } from './sync-helpers';

/**
 * Wave 1 T-004, R02.5 view-only open.
 *
 * A viewer-role member cannot be created yet: the server has no role that may read a project but not edit it
 * (T-003 adds `canEdit` to GET {id}; today every role that can read can edit). So the GET is STUBBED with
 * `canEdit: false` through page.route, exactly the answer T-003 will give a viewer. When T-003 has merged this
 * spec can drop the stub and sign in as a real viewer.
 */

test('R02.5 a view-only timeline: banner, frozen controls, no PATCH, nothing journalled', async ({ freshTimeline, page, seed }, testInfo) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  const id = await freshTimeline('seeded');
  await page.route(`**/edit-timelines/${id}`, async (route) => {
    if (route.request().method() !== 'GET') return route.fallback();
    const res = await route.fetch();
    const body = (await res.json()) as Record<string, unknown>;
    await route.fulfill({ response: res, json: { ...body, canEdit: false } });
  });
  const requests: string[] = [];
  page.on('request', (req) => { if (req.method() !== 'GET' && req.url().includes(`/edit-timelines/${id}`)) requests.push(`${req.method()} ${req.url()}`); });
  const before = await seed.api.getTimeline(seed.projectId, id);

  await page.goto(`/app/editor/${seed.projectId}/timeline/${id}`);
  await expect(page.getByTestId('timeline-canvas')).toBeVisible({ timeout: 45_000 });
  await expect(page.getByTestId('view-only-banner')).toContainText('You can look but not change this timeline.');
  // The save chip says "View only" on every device (the phone header's chip too); the desktop status bar adds its read-only chip.
  await expect(page.getByTestId('save-state-chip')).toHaveText('View only');
  if (testInfo.project.name === 'desktop') await expect(page.getByTestId('read-only-chip').first()).toBeVisible();

  // The edit controls are inert: disabled buttons, refused keys.
  // (the toolbar scissors on desktop and tablet; the phone has the action bar's Add media, Add text and Select instead)
  for (const control of ['undo', 'redo', 'blade-at-playhead', 'phone-action-add-media', 'phone-action-add-text']) {
    const visible = page.locator(`[data-testid="${control}"]:visible`).first();
    if (await visible.count()) await expect(visible).toBeDisabled();
  }
  const canvas = page.getByTestId('timeline-canvas');
  const box = (await canvas.boundingBox())!;
  if (testInfo.project.name === 'desktop') {
    await page.mouse.click(box.x + box.width * 0.2, box.y + 60);
    for (const key of ['Delete', 'b', 'x', 'Control+z', 'Backspace']) await page.keyboard.press(key);
  } else {
    await page.touchscreen.tap(box.x + box.width * 0.2, box.y + 60);
  }
  // Give a (wrongly) scheduled autosave and journal time to fire: the debounce is half a second.
  await page.waitForTimeout(1500);

  expect(requests, 'no PATCH, DELETE or POST was ever sent for this timeline').toEqual([]);
  expect(await page.evaluate((k) => localStorage.getItem(k), journalKey(id)), 'nothing was journalled').toBeNull();
  const after = await seed.api.getTimeline(seed.projectId, id);
  expect(after.revision).toBe(before.revision);
  expect(JSON.stringify(after.document)).toBe(JSON.stringify(before.document));
});
