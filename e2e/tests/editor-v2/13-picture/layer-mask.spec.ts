import * as fs from 'fs';
import {
  test, expect, openFresh, press, decodeImage, saveDownload, screenMapper, savedBadge, getFull, stageOf, QUADRANT, near,
} from '../../../fixtures/editor-v2/picture-lane';
import { picturesUrl } from '../../../fixtures/editor-v2/picture';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * T-009 (Wave 4, layer masks): paint a non-destructive mask on a picture layer, see it in the export, invert it,
 * delete it, and find it again after a reload and on a duplicate. Desktop (the layer menu is a mask button on each row).
 * Phone (pixel7): no row. The mask controls live in the desktop layer row menu (`layer-mask-menu-*`); picture-lane has
 * phone helpers (`isPhone`, `press`) but no phone layer-panel helper that reaches that menu, so a phone row is skipped
 * here until the phone layer sheet exposes it.
 * PENDING RUN: written during the build-only window; not executed yet.
 */
test.use({ serviceWorkers: 'block' });

type MaskedLayer = { id: string; label: string; kind: string; hasMatte?: boolean; matteUrl?: string | null };
const layersOf = async (api: Parameters<typeof getFull>[0], projectId: string, id: string): Promise<MaskedLayer[]> =>
  (await getFull(api, projectId, id)).layers as unknown as MaskedLayer[];

async function addMaskAndPaint(page: import('@playwright/test').Page, layer: MaskedLayer) {
  await page.getByTestId(`layer-mask-menu-${layer.id}`).click();
  await page.getByTestId(`layer-mask-add-${layer.id}`).click();
  const bar = page.getByTestId('layer-mask-bar');
  await expect(bar).toBeVisible();
  await bar.getByRole('radio', { name: 'Hide' }).check({ force: true });
  const map = await screenMapper(page);
  // A band across the top-left quadrant of the 1920 x 1080 photo, several rows so the round brush covers it.
  for (let y = 120; y <= 280; y += 20) {
    const a = map.toPage(100, y);
    const b = map.toPage(520, y);
    await page.mouse.move(a.x, a.y);
    await page.mouse.down();
    await page.mouse.move((a.x + b.x) / 2, a.y, { steps: 6 });
    await page.mouse.move(b.x, b.y, { steps: 6 });
    await page.mouse.up();
  }
  await bar.getByTestId('layer-mask-done').click();
  await expect(bar).toBeHidden({ timeout: 30_000 });
}

async function exportPng(page: import('@playwright/test').Page, tag: string) {
  await press(page.getByRole('button', { name: 'Export', exact: true }), false);
  const dialog = page.getByRole('dialog', { name: 'Export your picture' });
  await expect(dialog).toBeVisible();
  const download = page.waitForEvent('download');
  await press(dialog.getByRole('button', { name: 'Download' }), false);
  const file = await saveDownload(await download, tag);
  expect(fs.statSync(file).size, 'the export has bytes').toBeGreaterThan(1000);
  return decodeImage(file);
}

test('PE mask: painting Hide stores a matte and the export has transparent pixels there', async ({ page, api, pics }, testInfo) => {
  runOn('desktop');
  const { pic } = await openFresh(page, api, pics, `mask-paint-${testInfo.project.name}`);
  const layer = (await layersOf(api, pics.projectId, pic.id))[0];
  await expect(stageOf(page)).toBeVisible();
  await addMaskAndPaint(page, layer);

  const stored = (await layersOf(api, pics.projectId, pic.id))[0];
  expect(stored.hasMatte, 'the stored layer has a mask').toBe(true);
  const matte = await api.request.get(`/api/v1/projects/${pics.projectId}/image-documents/${pic.id}/layers/${layer.id}/matte`);
  expect(matte.status()).toBe(200);
  expect((await matte.body()).length, 'the mask has bytes').toBeGreaterThan(0);

  await savedBadge(page);
  const img = await exportPng(page, 'mask-paint');
  expect(img.at(300, 200)[3], 'alpha is 0 inside the painted band').toBe(0);
  expect(img.at(1800, 1000)[3], 'alpha is 255 elsewhere').toBe(255);
  expect(near(img.at(1800, 1000), QUADRANT.br), 'and the pixels there are the photo').toBe(true);
  expect(img.at(100, 1000)[3], 'the bottom-left is untouched').toBe(255);
});

test('PE mask: invert and delete mask', async ({ page, api, pics }, testInfo) => {
  runOn('desktop');
  const { pic } = await openFresh(page, api, pics, `mask-invert-${testInfo.project.name}`);
  const layer = (await layersOf(api, pics.projectId, pic.id))[0];
  await addMaskAndPaint(page, layer);
  await savedBadge(page);
  const painted = await exportPng(page, 'mask-before-invert');
  expect(painted.at(300, 200)[3], 'before Invert: alpha 0 inside the painted band').toBe(0);
  expect(painted.at(1800, 1000)[3], 'before Invert: alpha 255 elsewhere').toBe(255);
  await page.keyboard.press('Escape');

  const matteUrl = `/api/v1/projects/${pics.projectId}/image-documents/${pic.id}/layers/${layer.id}/matte`;
  const beforeBytes = await (await api.request.get(matteUrl)).body();
  await page.getByTestId(`layer-mask-menu-${layer.id}`).click();
  await page.getByTestId(`layer-mask-invert-${layer.id}`).click();
  await expect.poll(async () => {
    const res = await api.request.get(matteUrl);
    return Buffer.compare(await res.body(), beforeBytes) !== 0;
  }, { message: 'the inverted mask is stored (its bytes changed)' }).toBe(true);
  await savedBadge(page);
  const inverted = await exportPng(page, 'mask-inverted');
  expect(inverted.at(300, 200)[3], 'the painted band is visible again').toBe(255);
  expect(inverted.at(1800, 1000)[3], 'the rest is hidden').toBe(0);
  await page.keyboard.press('Escape');

  await page.getByTestId(`layer-mask-menu-${layer.id}`).click();
  await page.getByTestId(`layer-mask-delete-${layer.id}`).click();
  await expect.poll(async () => (await layersOf(api, pics.projectId, pic.id))[0].hasMatte, { message: 'the mask is gone on the server' }).toBeFalsy();
  const res = await api.request.get(`/api/v1/projects/${pics.projectId}/image-documents/${pic.id}/layers/${layer.id}/matte`, { failOnStatusCode: false });
  expect(res.status(), 'the mask route has nothing to serve').toBe(404);
  await savedBadge(page);
  const back = await exportPng(page, 'mask-deleted');
  expect(back.at(300, 200)[3], 'the original pixels were never changed').toBe(255);
});

test('PE mask survives reload and duplicate', async ({ page, api, pics }, testInfo) => {
  runOn('desktop');
  const { pic } = await openFresh(page, api, pics, `mask-dup-${testInfo.project.name}`);
  const layer = (await layersOf(api, pics.projectId, pic.id))[0];
  await addMaskAndPaint(page, layer);

  await page.reload();
  await expect(page.getByTestId(`layer-mask-chip-${layer.id}`), 'the mask chip is still on the row after a reload').toBeVisible({ timeout: 45_000 });

  const copy = await api.json<{ id: string }>('POST', `${picturesUrl(pics.projectId, pic.id)}/duplicate`, undefined, [200, 201]);
  const copied = (await layersOf(api, pics.projectId, copy.id))[0];
  expect(copied.hasMatte, 'the copy carries the mask').toBe(true);
  const res = await api.request.get(`/api/v1/projects/${pics.projectId}/image-documents/${copy.id}/layers/${copied.id}/matte`);
  expect(res.status()).toBe(200);
  expect((await res.body()).length).toBeGreaterThan(0);
});
