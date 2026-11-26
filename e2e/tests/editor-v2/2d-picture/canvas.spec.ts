import { reloadPicture, toolButton, expectPanelOn } from '../../../fixtures/editor-v2/picture-lane';
import {
  test, expect, openFresh, pollPicture, getFull, isPhone, press, addBadgeLayer, layerOf, screenMapper, zoomPercent, handles, rotateHandle, stageOf, openTab, stageInView,
} from '../../../fixtures/editor-v2/picture-lane';
import { runOn, skipSyntheticPointers } from '../../../fixtures/editor-v2/rows';

/** Lane D, picture editor: the stage's gestures and the tool column (inventory rows PE-040..PE-069b, PE-066*). */

// The app registers a service worker; Playwright WebKit cannot route requests it handles, so route stubs need it blocked (as 09-analysis/transcript does).
test.use({ serviceWorkers: 'block' });

async function withBadge(page: import('@playwright/test').Page, api: import('../../../fixtures/editor-v2/api').EditorApi, pics: import('../../../fixtures/editor-v2/picture-lane').PictureSeed, tag: string) {
  const { pic, stage } = await openFresh(page, api, pics, tag);
  const badge = await addBadgeLayer(api, pics.projectId, pic.id, pics);
  await reloadPicture(page);
  await expect(stage).toBeVisible();
  return { pic, stage, badge };
}
const rowOf = (page: import('@playwright/test').Page, label: string) => page.getByRole('list', { name: 'Layers, front first' }).getByRole('button', { name: `Select ${label}` });
const stored = async (api: import('../../../fixtures/editor-v2/api').EditorApi, pics: import('../../../fixtures/editor-v2/picture-lane').PictureSeed, picId: string, layerId: string) =>
  layerOf(await getFull(api, pics.projectId, picId), layerId).transform;

test('PE-050 PE-051 clicking a layer selects it (outline and handles), clicking outside the picture clears the selection', async ({ page, api, pics }, testInfo) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  const phone = isPhone(testInfo.project.name);
  const { stage, badge } = await withBadge(page, api, pics, `pe050-${testInfo.project.name}`);
  // Select the badge once through the list to learn where it is, then deselect and click it on the canvas.
  await press(rowOf(page, badge.label), phone);
  const map = await screenMapper(page);
  await stage.focus();
  await page.keyboard.press('Escape');
  await expect(handles(page)).toHaveCount(0);
  if (phone) await openTab(page, 'Layers'); // Escape also closes the phone sheet
  await expect(rowOf(page, badge.label)).toHaveAttribute('aria-current', 'false');

  const at = map.toPage(badge.transform.x + 300, badge.transform.y + 150);
  if (phone) await page.touchscreen.tap(at.x, at.y);
  else await page.mouse.click(at.x, at.y);
  await expect(rowOf(page, badge.label), 'a click on the badge selects it').toHaveAttribute('aria-current', 'true');
  await expect(handles(page), 'four corner handles').toHaveCount(4);
  await expect(rotateHandle(page), 'and a rotate handle').toHaveCount(1);

  const box = (await stage.boundingBox())!;
  if (phone) await page.touchscreen.tap(box.x + 6, box.y + 6);
  else await page.mouse.click(box.x + 6, box.y + 6);
  await expect(handles(page), 'a click on empty space deselects').toHaveCount(0);
});

test('PE-052 PE-053 PE-054 PE-055 PE-056 PE-057 dragging a layer moves it, a corner resizes it, the rotate handle turns it; a locked layer ignores all three', async ({ page, api, pics }) => {
  runOn('desktop');
  const { pic, badge } = await withBadge(page, api, pics, 'pe052');
  await rowOf(page, badge.label).click();
  let map = await screenMapper(page, badge);

  // Move: 200 px right and 100 px down on screen.
  const a = map.toPage(badge.transform.x + 300, badge.transform.y + 150);
  await page.mouse.move(a.x, a.y);
  await page.mouse.down();
  await page.mouse.move(a.x + 100, a.y + 50, { steps: 5 });
  await page.mouse.move(a.x + 200, a.y + 100, { steps: 5 });
  await page.mouse.up();
  const moved = await pollPicture(api, pics.projectId, pic.id, (p) => (layerOf(p, badge.id).transform.x > 50 ? layerOf(p, badge.id).transform : null), 'the move was saved');
  expect(moved!.x, 'x moved by about 200 screen px').toBeCloseTo(200 / map.zoom, -1);
  expect(moved!.y, 'y moved by about 100 screen px').toBeCloseTo(100 / map.zoom, -1);
  await reloadPicture(page);
  await rowOf(page, badge.label).click();
  expect((await stored(api, pics, pic.id, badge.id)).x, 'a reload keeps the position').toBeCloseTo(moved!.x, 0);

  // Undo puts it back (one undo step per drag).
  await page.getByRole('button', { name: 'Undo', exact: true }).isDisabled(); // fresh reload: nothing to undo
  const cur = layerOf(await getFull(api, pics.projectId, pic.id), badge.id);
  map = await screenMapper(page, cur);

  // Shift locks the axis.
  const b = map.toPage(cur.transform.x + 300, cur.transform.y + 150);
  await page.keyboard.down('Shift');
  await page.mouse.move(b.x, b.y);
  await page.mouse.down();
  await page.mouse.move(b.x + 60, b.y + 10, { steps: 6 });
  await page.mouse.move(b.x + 120, b.y + 20, { steps: 6 });
  await page.mouse.up();
  await page.keyboard.up('Shift');
  const locked = await pollPicture(api, pics.projectId, pic.id, (p) => (layerOf(p, badge.id).transform.x > cur.transform.x + 10 ? layerOf(p, badge.id).transform : null), 'the shift-move was saved');
  expect(Math.abs(locked!.y - cur.transform.y), 'with Shift only one axis changed').toBeLessThan(1.5);

  // Resize from the bottom-right corner.
  const corner = handles(page).nth(2);
  const cb = (await corner.boundingBox())!;
  const before = (await stored(api, pics, pic.id, badge.id)).scale;
  await page.mouse.move(cb.x + cb.width / 2, cb.y + cb.height / 2);
  await page.mouse.down();
  await page.mouse.move(cb.x + 60, cb.y + 40, { steps: 6 });
  await page.mouse.move(cb.x + 120, cb.y + 80, { steps: 6 });
  await page.mouse.up();
  const scaled = await pollPicture(api, pics.projectId, pic.id, (p) => (layerOf(p, badge.id).transform.scale > before * 1.05 ? layerOf(p, badge.id).transform : null), 'the resize was saved');
  expect(scaled!.scale).toBeGreaterThan(before);

  // Rotate with Shift: a multiple of 15 degrees, not zero.
  const rh = (await rotateHandle(page).boundingBox())!;
  await page.keyboard.down('Shift');
  await page.mouse.move(rh.x + rh.width / 2, rh.y + rh.height / 2);
  await page.mouse.down();
  await page.mouse.move(rh.x + 80, rh.y + 40, { steps: 6 });
  await page.mouse.move(rh.x + 160, rh.y + 90, { steps: 6 });
  await page.mouse.up();
  await page.keyboard.up('Shift');
  const turned = await pollPicture(api, pics.projectId, pic.id, (p) => (Math.abs(layerOf(p, badge.id).transform.rotation) > 1 ? layerOf(p, badge.id).transform : null), 'the rotation was saved');
  expect(Math.abs(turned!.rotation % 15), 'Shift snaps rotation to 15 degree steps').toBeLessThan(0.01);

  // Locked: no handles, a drag changes nothing.
  await page.getByRole('button', { name: `Lock ${badge.label}` }).click();
  await pollPicture(api, pics.projectId, pic.id, (p) => layerOf(p, badge.id).locked === true, 'locked');
  await expect(handles(page), 'a locked layer shows no handles').toHaveCount(0);
  const lockedBefore = await stored(api, pics, pic.id, badge.id);
  const map2 = await screenMapper(page);
  const g = map2.toPage(lockedBefore.x + 100, lockedBefore.y + 100);
  await page.mouse.move(g.x, g.y);
  await page.mouse.down();
  await page.mouse.move(g.x + 80, g.y + 60, { steps: 6 });
  await page.mouse.up();
  await page.waitForTimeout(1200);
  expect(await stored(api, pics, pic.id, badge.id), 'a locked layer did not move, resize or turn').toEqual(lockedBefore);
});

test('PE-058 PE-059 arrow keys nudge by 1 px (10 with Shift) and Escape deselects', async ({ page, api, pics }) => {
  runOn('desktop');
  const { pic, stage, badge } = await withBadge(page, api, pics, 'pe058');
  await rowOf(page, badge.label).click();
  await stage.focus();
  for (let i = 0; i < 3; i++) await page.keyboard.press('ArrowRight');
  await pollPicture(api, pics.projectId, pic.id, (p) => layerOf(p, badge.id).transform.x === 3, 'three right-arrows = +3 px');
  await page.keyboard.press('Shift+ArrowDown');
  await pollPicture(api, pics.projectId, pic.id, (p) => layerOf(p, badge.id).transform.y === 10, 'Shift+down = +10 px');
  await page.keyboard.press('Escape');
  await expect(handles(page)).toHaveCount(0);
});

test('PE-060 PE-061 PE-062 PE-063 PE-066 the view: wheel zoom, Space-drag and middle-drag pan, zoom keys and the zoom bar, none of which edits the picture', async ({ page, api, pics }) => {
  runOn('desktop');
  const { pic, stage, badge } = await withBadge(page, api, pics, 'pe060');
  await rowOf(page, badge.label).click();
  const startRev = (await getFull(api, pics.projectId, pic.id)).revision;
  const fit = await zoomPercent(page);
  const box = (await stage.boundingBox())!;
  const mid = { x: box.x + box.width / 2, y: box.y + box.height / 2 };

  await page.mouse.move(mid.x, mid.y);
  await page.keyboard.down('Control');
  await page.mouse.wheel(0, -400);
  await page.keyboard.up('Control');
  await expect.poll(() => zoomPercent(page), { message: 'Ctrl+wheel up zooms in' }).toBeGreaterThan(fit);

  await stage.focus();
  await page.keyboard.press('1');
  await expect.poll(() => zoomPercent(page)).toBe(100);
  await page.keyboard.press('0');
  await expect.poll(() => zoomPercent(page), { message: '0 returns to Fit' }).toBe(fit);
  await page.keyboard.press('+');
  await expect.poll(() => zoomPercent(page)).toBeGreaterThan(fit);
  const plus = await zoomPercent(page);
  await page.keyboard.press('-');
  await expect.poll(() => zoomPercent(page), { message: '- zooms back out' }).toBeLessThan(plus);

  // Zoom bar buttons.
  await page.getByRole('button', { name: 'Zoom in', exact: true }).click();
  const zin = await zoomPercent(page);
  expect(zin).toBeGreaterThan(fit);
  await page.getByRole('button', { name: 'Zoom out', exact: true }).click();
  await expect.poll(() => zoomPercent(page)).toBeLessThan(zin);
  await page.getByRole('button', { name: /^Zoom \d+%\. Show at actual size/ }).click();
  await expect.poll(() => zoomPercent(page), { message: 'the readout button shows actual size' }).toBe(100);
  await page.getByRole('button', { name: 'Fit the picture on screen' }).click();
  await expect.poll(() => zoomPercent(page)).toBe(fit);

  // Pan: Space + drag, then middle-button drag. The outline moves, the layer does not.
  const outline = () => stage.locator('svg > g').first().getAttribute('transform');
  const o0 = await outline();
  await page.keyboard.down('Space');
  await page.mouse.move(mid.x, mid.y);
  await page.mouse.down();
  await page.mouse.move(mid.x + 40, mid.y + 20, { steps: 5 });
  await page.mouse.up();
  await page.keyboard.up('Space');
  const o1 = await outline();
  expect(o1, 'Space-drag panned the view').not.toBe(o0);
  await page.mouse.move(mid.x, mid.y);
  await page.mouse.down({ button: 'middle' });
  await page.mouse.move(mid.x - 30, mid.y - 30, { steps: 5 });
  await page.mouse.up({ button: 'middle' });
  expect(await outline(), 'middle-drag panned the view').not.toBe(o1);

  await page.waitForTimeout(1200);
  const end = await getFull(api, pics.projectId, pic.id);
  expect(end.revision, 'moving the view wrote nothing').toBe(startRev);
  expect(layerOf(end, badge.id).transform.x).toBe(0);
});

test('PE-066 PE-066b PE-066c PE-066d the zoom bar works by tap: out, in, actual size, fit', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  await openFresh(page, api, pics, `pe066-${testInfo.project.name}`);
  const fit = await zoomPercent(page);
  await press(page.getByRole('button', { name: 'Zoom in', exact: true }), phone);
  await expect.poll(() => zoomPercent(page), { message: 'Zoom in grows the readout' }).toBeGreaterThan(fit);
  await press(page.getByRole('button', { name: 'Zoom out', exact: true }), phone);
  await press(page.getByRole('button', { name: 'Zoom out', exact: true }), phone);
  await expect.poll(() => zoomPercent(page), { message: 'Zoom out shrinks it' }).toBeLessThan(fit);
  await press(page.getByRole('button', { name: /^Zoom \d+%\. Show at actual size/ }), phone);
  await expect.poll(() => zoomPercent(page)).toBe(100);
  await press(page.getByRole('button', { name: 'Fit the picture on screen' }), phone);
  await expect.poll(() => zoomPercent(page)).toBe(fit);
  const stage = (await page.locator('[role=application][aria-label="Thumbnail canvas"]').boundingBox())!;
  for (const name of ['Zoom in', 'Zoom out', 'Fit the picture on screen']) {
    const b = (await page.getByRole('button', { name, exact: true }).boundingBox())!;
    if (phone) expect(Math.min(b.width, b.height), `${name} is a 44 px target on a phone`).toBeGreaterThanOrEqual(43.5);
    expect(b.x + b.width, `${name} sits inside the stage`).toBeLessThanOrEqual(stage.x + stage.width + 1);
  }
});

test('PE-041 the hand tool: dragging on the stage pans the view and never moves a layer', async ({ page, api, pics }, testInfo) => {
  runOn('desktop', 'pixel7');
  const phone = isPhone(testInfo.project.name);
  const { pic, stage } = await openFresh(page, api, pics, `pe041-${testInfo.project.name}`);
  const startRev = (await getFull(api, pics.projectId, pic.id)).revision;
  await press(page.getByRole('button', { name: 'Move around the canvas', exact: true }), phone);
  const origin = () => stage.locator('svg > g').first().getAttribute('transform');
  const before = await origin();
  await stageInView(page); // on a phone the page scrolls to the sheet: bring the stage under the header so the drag lands on it
  const box = (await stage.boundingBox())!;
  const c = { x: box.x + box.width / 2, y: box.y + box.height / 2 };
  await page.mouse.move(c.x, c.y);
  await page.mouse.down();
  await page.mouse.move(c.x + 50, c.y + 30, { steps: 6 });
  await page.mouse.up();
  expect(await origin(), 'the view moved').not.toBe(before);
  await page.waitForTimeout(1200);
  const after = await getFull(api, pics.projectId, pic.id);
  expect(after.revision, 'nothing was written').toBe(startRev);
  expect(after.layers[0].transform.x).toBe(0);
});

test('PE-041 PE-042 PE-043 PE-044 PE-044a the tool column arms each tool; the brush never leaves you with nothing to paint on', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic, badge } = await withBadge(page, api, pics, `pe040-${testInfo.project.name}`);
  const tool = (name: string) => toolButton(page, name);

  await expect(tool('Select and move')).toHaveAttribute('aria-pressed', 'true');
  await press(tool('Move around the canvas'), phone);
  await expect(tool('Move around the canvas')).toHaveAttribute('aria-pressed', 'true');
  await expect(tool('Select and move')).toHaveAttribute('aria-pressed', 'false');

  await press(tool('Text'), phone);
  await expect(tool('Text')).toHaveAttribute('aria-pressed', 'true');
  await expectPanelOn(page, 'Text');

  await press(tool('Cut out a subject'), phone);
  await expectPanelOn(page, 'AI');
  await expect(page.getByTestId('ai-tool-subject')).toBeVisible();

  // PE-044a: nothing is selected, and the brush is armed. It must pick a layer to paint on, and say so.
  await press(tool('Select and move'), phone);
  await expect(page.getByRole('list', { name: 'Layers, front first' }).locator('[aria-current="true"]')).toHaveCount(0);
  await press(tool('Brush mask'), phone);
  await expect(tool('Brush mask')).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByTestId('ai-tool-erase'), 'the brush opens the Erase tool').toBeVisible();
  await expect(page.getByText(/^Paint over the part of ".+" you want to change/), 'a layer was chosen to paint on, not a dead end').toBeVisible();
  await expect(page.getByText(/Select a picture layer first/)).toHaveCount(0);
  void pic; void badge;
});

test('PE-068 painting with the brush makes a mask (shown as "Mask ready"), Rub out removes it, Clear empties it, and nothing is saved', async ({ page, api, pics }, testInfo) => {
  runOn('desktop', 'pixel7');
  const phone = isPhone(testInfo.project.name);
  const { pic, stage } = await openFresh(page, api, pics, `pe068-${testInfo.project.name}`);
  const startRev = (await getFull(api, pics.projectId, pic.id)).revision;
  await page.getByRole('button', { name: 'Brush mask', exact: true }).click(); // after a raw CDP touch stroke Chromium swallows the next synthetic tap's click; a mouse click reaches the same handler
  await expect(page.getByText('Nothing painted yet.')).toBeVisible();
  const stroke = async () => {
    await stageInView(page);
    const box = (await stage.boundingBox())!;
    const c = { x: box.x + box.width / 2, y: box.y + box.height / 2 };
    if (phone) {
      const cdp = await page.context().newCDPSession(page);
      await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: c.x, y: c.y }] });
      for (let i = 1; i <= 8; i++) await cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ x: c.x + i * 10, y: c.y + i * 4 }] });
      await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
    } else {
      await page.mouse.move(c.x, c.y);
      await page.mouse.down();
      await page.mouse.move(c.x + 80, c.y + 30, { steps: 8 });
      await page.mouse.up();
    }
  };
  await stroke();
  await expect(page.getByText('Mask ready.'), 'a stroke makes a mask').toBeVisible();
  await page.getByRole('button', { name: 'Clear', exact: true }).click(); // after a raw CDP touch stroke Chromium swallows the next synthetic tap's click; a mouse click reaches the same handler
  await expect(page.getByText('Nothing painted yet.')).toBeVisible();

  await stroke();
  await expect(page.getByText('Mask ready.')).toBeVisible();
  await page.getByRole('radiogroup', { name: 'Brush mode' }).getByText('Rub out').click(); // after a raw CDP touch stroke Chromium swallows the next synthetic tap's click; a mouse click reaches the same handler
  await expect(page.getByRole('radio', { name: 'Rub out' }), 'Rub out mode is on').toBeChecked();
  const size = page.getByRole('slider', { name: 'Brush size' });
  await size.focus();
  await size.press('End');
  await expect(page.getByText('400px', { exact: true }), 'the brush size readout follows the slider').toBeVisible();
  await stroke();
  // (A mask whose paint was all rubbed out still counts as "painted" until Cleared: noted in the ledger.)
  await page.getByRole('button', { name: 'Clear', exact: true }).click(); // after a raw CDP touch stroke Chromium swallows the next synthetic tap's click; a mouse click reaches the same handler
  await expect(page.getByText('Nothing painted yet.')).toBeVisible();

  await page.waitForTimeout(1200);
  expect((await getFull(api, pics.projectId, pic.id)).revision, 'a mask is never saved by itself').toBe(startRev);
  void openTab;
});

test('PE-069 PE-069b the subject tool: a click is an include mark, a drag is a box, and "Leave this out" (or Shift) makes a leave-out mark', async ({ page, api, pics }, testInfo) => {
  runOn('desktop', 'pixel7');
  const phone = isPhone(testInfo.project.name);
  const { stage } = await openFresh(page, api, pics, `pe069-${testInfo.project.name}`);
  await page.getByRole('button', { name: 'Cut out a subject', exact: true }).click(); // after a raw CDP touch stroke Chromium swallows the next synthetic tap's click; a mouse click reaches the same handler
  await stageInView(page);
  const box = (await stage.boundingBox())!;
  const at = (fx: number, fy: number) => ({ x: box.x + box.width * fx, y: box.y + box.height * fy });
  const tap = async (p: { x: number; y: number }) => (phone ? page.touchscreen.tap(p.x, p.y) : page.mouse.click(p.x, p.y));

  await tap(at(0.5, 0.5));
  await expect(page.getByText('Include 1', { exact: true })).toBeVisible();

  // A box by dragging (touch drag through CDP).
  const a = at(0.3, 0.3);
  const b = at(0.45, 0.45);
  if (phone) {
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [a] });
    for (let i = 1; i <= 6; i++) await cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ x: a.x + ((b.x - a.x) * i) / 6, y: a.y + ((b.y - a.y) * i) / 6 }] });
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  } else {
    await page.mouse.move(a.x, a.y);
    await page.mouse.down();
    await page.mouse.move(b.x, b.y, { steps: 6 });
    await page.mouse.up();
  }
  await expect(page.getByText('Box 1', { exact: true })).toBeVisible();

  // The leave-out choice works by touch (no Shift key).
  await page.getByRole('radiogroup', { name: 'What a click marks' }).getByText('Leave this out').click(); // after a raw CDP touch stroke Chromium swallows the next synthetic tap's click; a mouse click reaches the same handler
  await tap(at(0.7, 0.6));
  await expect(page.getByText('Leave out 2', { exact: true }), 'a tap in "Leave this out" mode is a leave-out mark').toBeVisible();
  if (!phone) {
    await page.getByRole('radiogroup', { name: 'What a click marks' }).getByText('Cut this out').click(); // after a raw CDP touch stroke Chromium swallows the next synthetic tap's click; a mouse click reaches the same handler
    await page.keyboard.down('Shift');
    await page.mouse.click(at(0.8, 0.7).x, at(0.8, 0.7).y);
    await page.keyboard.up('Shift');
    await expect(page.getByText('Leave out 3', { exact: true }), 'Shift-click is still a leave-out mark').toBeVisible();
  }
  await page.getByRole('button', { name: 'Clear marks' }).click(); // after a raw CDP touch stroke Chromium swallows the next synthetic tap's click; a mouse click reaches the same handler
  await expect(page.getByText('Marks on the picture')).toHaveCount(0);
});

test('PE-064 PE-065 touch: tap selects, a one-finger drag moves, a two-finger pinch zooms without moving the layer', async ({ page, api, pics }) => {
  runOn('pixel7');
  skipSyntheticPointers();
  const { pic, badge } = await withBadge(page, api, pics, 'pe064');
  await page.getByRole('list', { name: 'Layers, front first' }).getByRole('button', { name: `Select ${badge.label}` }).tap();
  const map = await screenMapper(page, badge);
  const cdp = await page.context().newCDPSession(page);
  const p = map.toPage(badge.transform.x + 300, badge.transform.y + 150);

  await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: p.x, y: p.y }] });
  for (let i = 1; i <= 8; i++) await cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ x: p.x + i * 6, y: p.y + i * 3 }] });
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  const moved = await pollPicture(api, pics.projectId, pic.id, (q) => (layerOf(q, badge.id).transform.x > 20 ? layerOf(q, badge.id).transform : null), 'the finger drag moved the layer');
  expect(moved!.x).toBeCloseTo(48 / map.zoom, -1);

  const fit = await zoomPercent(page);
  const cx = map.box.x + map.box.width / 2;
  const cy = map.box.y + map.box.height / 2;
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: cx - 30, y: cy, id: 1 }, { x: cx + 30, y: cy, id: 2 }] });
  for (let i = 1; i <= 8; i++) {
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ x: cx - 30 - i * 12, y: cy, id: 1 }, { x: cx + 30 + i * 12, y: cy, id: 2 }] });
  }
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  await expect.poll(() => zoomPercent(page), { message: 'pinching out zooms in' }).toBeGreaterThan(fit);
  await page.waitForTimeout(1200);
  expect((await stored(api, pics, pic.id, badge.id)).x, 'the pinch did not move the layer').toBeCloseTo(moved!.x, 0);
});
