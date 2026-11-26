import { reloadPicture } from '../../../fixtures/editor-v2/picture-lane';
import {
  test, expect, openFresh, getFull, pollPicture, isPhone, press, addBadgeLayer, layerOf, hubUrl, createFull, savedBadge,
} from '../../../fixtures/editor-v2/picture-lane';
import { picturesUrl } from '../../../fixtures/editor-v2/picture';
import { runOn } from '../../../fixtures/editor-v2/rows';

/** Wave 2 picture editor: undo ghosts, Delete key, numbers, align, merge, crop, duplicate. Real outcomes via the document API. */

test.use({ serviceWorkers: 'block' });

type Sized = { width?: number | null; height?: number | null };
const sizeOf = (l: object): Sized => l as Sized;

const list = (page: import('@playwright/test').Page) => page.getByRole('list', { name: 'Layers, front first' });
const undo = (page: import('@playwright/test').Page) => page.getByRole('button', { name: 'Undo' });

test('W2-R13.5 undoing an added layer removes it from the list and the server once the redo is dropped; redo brings it back', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `w2ghost-${testInfo.project.name}`);
  const added = await addBadgeLayer(api, pics.projectId, pic.id, pics);
  await reloadPicture(page);
  await expect(list(page).getByRole('button', { name: /^Select / })).toHaveCount(2);
  // Delete (undoable), undo, redo: the layer comes and goes in the list.
  await press(list(page).getByRole('button', { name: `Select ${added.label}` }), phone);
  await page.keyboard.press('Delete');
  await expect(list(page).getByRole('button', { name: /^Select / })).toHaveCount(1);
  await press(undo(page), phone);
  await expect(list(page).getByRole('button', { name: /^Select / })).toHaveCount(2);
  await press(page.getByRole('button', { name: 'Redo' }), phone);
  await expect(list(page).getByRole('button', { name: /^Select / })).toHaveCount(1);
  // Not deleted on the server while undo can still bring it back...
  await savedBadge(page);
  expect((await getFull(api, pics.projectId, pic.id)).layers.some((l) => l.id === added.id)).toBe(true);
  // ...and gone for good after the page is left.
  await page.goto(hubUrl(pics.projectId));
  await pollPicture(api, pics.projectId, pic.id, (p) => !p.layers.some((l) => l.id === added.id), 'the deleted layer is gone from the stored picture');
});

test('W2 the Delete key does nothing while typing in a number box', async ({ page, api, pics }, testInfo) => {
  runOn('desktop');
  const { pic } = await openFresh(page, api, pics, `w2del-${testInfo.project.name}`);
  const layer = await addBadgeLayer(api, pics.projectId, pic.id, pics);
  await reloadPicture(page);
  await list(page).getByRole('button', { name: `Select ${layer.label}` }).click();
  await page.getByTestId('arrange-left').click();
  await page.keyboard.press('Delete');
  await expect(list(page).getByRole('button', { name: /^Select / })).toHaveCount(2);
});

test('W2 numeric Left/Top/Width set the stored layer, survive a reload and undo', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `w2num-${testInfo.project.name}`);
  const layer = await addBadgeLayer(api, pics.projectId, pic.id, pics);
  await reloadPicture(page);
  await press(list(page).getByRole('button', { name: `Select ${layer.label}` }), phone);
  const left = page.getByTestId('arrange-left');
  await left.fill('123');
  await left.press('Enter');
  const top = page.getByTestId('arrange-top');
  await top.fill('45');
  await top.press('Enter');
  const width = page.getByTestId('arrange-width');
  await width.fill('320');
  await width.press('Enter');
  const stored = await pollPicture(api, pics.projectId, pic.id, (p) => {
    const t = layerOf(p, layer.id).transform;
    return t.x === 123 && t.y === 45 && Math.abs(t.scale - 0.5) < 0.01 ? t : null;
  }, 'x 123, y 45 and half the 640 px width are stored');
  expect(stored?.x).toBe(123);
  await reloadPicture(page);
  await press(list(page).getByRole('button', { name: `Select ${layer.label}` }), phone);
  await expect(page.getByTestId('arrange-left')).toHaveValue('123');
  await expect(page.getByTestId('arrange-width')).toHaveValue('320');
  await page.getByTestId('arrange-left').fill('200');
  await page.getByTestId('arrange-left').press('Enter');
  await pollPicture(api, pics.projectId, pic.id, (p) => layerOf(p, layer.id).transform.x === 200, 'x 200 is stored');
  await press(undo(page), phone);
  await pollPicture(api, pics.projectId, pic.id, (p) => layerOf(p, layer.id).transform.x === 123, 'undo puts the position back on the server');
});

test('W2 align right and bottom to the picture lands the layer on the edges; undo reverses', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `w2align-${testInfo.project.name}`);
  const layer = await addBadgeLayer(api, pics.projectId, pic.id, pics);
  await reloadPicture(page);
  await press(list(page).getByRole('button', { name: `Select ${layer.label}` }), phone);
  await press(page.getByTestId('align-right'), phone);
  await press(page.getByTestId('align-bottom'), phone);
  await pollPicture(api, pics.projectId, pic.id, (p) => {
    const l = layerOf(p, layer.id);
    const w = (sizeOf(l).width ?? 640) * l.transform.scale;
    const h = (sizeOf(l).height ?? 360) * l.transform.scale;
    return Math.abs(l.transform.x + w - p.canvasWidth) < 1 && Math.abs(l.transform.y + h - p.canvasHeight) < 1;
  }, 'the layer touches the right and bottom edges');
  await reloadPicture(page);
  await expect(list(page).getByRole('button', { name: /^Select / })).toHaveCount(2);
});

test('W2 merge two layers into one raster layer; undo brings both back', async ({ page, api, pics }, testInfo) => {
  runOn('desktop');
  const { pic } = await openFresh(page, api, pics, `w2merge-${testInfo.project.name}`);
  const layer = await addBadgeLayer(api, pics.projectId, pic.id, pics);
  await reloadPicture(page);
  await list(page).getByRole('button', { name: `Select ${layer.label}` }).click();
  await page.getByTestId('arrange-pick-switch').click({ force: true });
  await page.getByTestId(`layer-pick-${layer.id}`).check({ force: true });
  await page.getByTestId(`layer-pick-${pic.layers[0].id}`).check({ force: true });
  await page.getByTestId('merge-layers').click();
  await pollPicture(api, pics.projectId, pic.id, (p) => p.layers.some((l) => l.label === 'Merged layer'), 'a merged layer is stored');
  await expect(list(page).getByRole('button', { name: /^Select / })).toHaveCount(1);
  await press(undo(page), false);
  await expect(list(page).getByRole('button', { name: /^Select / })).toHaveCount(2);
});

// Crop is covered in 13-picture-editor/crop-in-place.spec.ts (T-035: crop is now a non-destructive field, not a replacement layer).

test('W2 Duplicate in the Pictures list makes a copy with the same layers', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const name = `w2dup-${testInfo.project.name}-${Date.now()}`;
  const pic = await createFull(api, pics.projectId, pics.quad, name);
  await page.goto(hubUrl(pics.projectId));
  await press(page.getByRole('button', { name: `More for ${name}`, exact: true }), phone);
  await press(page.getByRole('menuitem', { name: 'Duplicate' }), phone);
  const copy = await expect.poll(async () => {
    const all = await api.json<{ id: string; name: string | null }[]>('GET', picturesUrl(pics.projectId));
    return all.find((d) => d.id !== pic.id && d.name === `Copy of ${name}`)?.id ?? null;
  }, { timeout: 20_000 }).not.toBeNull().then(async () => {
    const all = await api.json<{ id: string; name: string | null }[]>('GET', picturesUrl(pics.projectId));
    return all.find((d) => d.name === `Copy of ${name}`)!.id;
  });
  const dup = await getFull(api, pics.projectId, copy);
  expect(dup.layers.length, 'the copy has the original layers').toBe(pic.layers.length);
  expect(dup.layers[0].imageUrl, 'its own layer object').not.toBe(pic.layers[0].imageUrl);
  expect((await api.request.delete(picturesUrl(pics.projectId, pic.id))).status()).toBeLessThan(300);
  const still = await api.request.get(dup.layers[0].imageUrl);
  expect(still.ok(), 'the copy survives deleting the original').toBe(true);
});
