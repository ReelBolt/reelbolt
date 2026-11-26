import { reloadPicture } from '../../../fixtures/editor-v2/picture-lane';
import {
  test, expect, openFresh, getFull, pollPicture, press, layerOf, savedBadge, decodeImage, saveDownload, addBadgeLayer, hubUrl,
} from '../../../fixtures/editor-v2/picture-lane';
import type { FullPicture } from '../../../fixtures/editor-v2/picture-lane';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * T-035: Crop is a non-destructive field on the picture layer (fractions of the stored picture), edited in place.
 * Real outcomes: the stored document (same layer id, same pixels, a crop field), the exported pixels, one undo step.
 */

test.use({ serviceWorkers: 'block' });

type Cropped = { crop?: { x: number; y: number; w: number; h: number } | null; imageUrl: string; width?: number | null; height?: number | null };
const cropOf = (p: FullPicture, id: string) => (layerOf(p, id) as unknown as Cropped).crop ?? null;

const list = (page: import('@playwright/test').Page) => page.getByRole('list', { name: 'Layers, front first' });

async function openCrop(page: import('@playwright/test').Page, label: string, tap: boolean) {
  await press(list(page).getByRole('button', { name: `Select ${label}` }), tap);
  await press(page.getByTestId('crop-open'), tap);
  return page.getByTestId('crop-dialog');
}

test('T35.1 crop edits the layer in place: same layer, same stored pixels, a crop field, exported pixels match, widening and undo work', async ({ page, api, pics }, testInfo) => {
  runOn('desktop');
  const { pic } = await openFresh(page, api, pics, `t35crop-${testInfo.project.name}`);
  const source = pic.layers[0];
  const urlBefore = (source as unknown as Cropped).imageUrl;
  const dialog = await openCrop(page, source.label, false);
  await dialog.getByTestId('crop-w').fill('960');
  await dialog.getByTestId('crop-w').press('Enter');
  await dialog.getByTestId('crop-apply').click();

  const stored = await pollPicture(api, pics.projectId, pic.id, (p) => cropOf(p, source.id), 'a crop is stored on the same layer');
  expect(stored!.x).toBeCloseTo(0, 5);
  expect(stored!.w).toBeCloseTo(0.5, 3);
  expect(stored!.h).toBeCloseTo(1, 5);
  const after = await getFull(api, pics.projectId, pic.id);
  expect(after.layers.map((l) => l.id), 'no layer was added or replaced').toEqual(pic.layers.map((l) => l.id));
  expect((layerOf(after, source.id) as unknown as Cropped).imageUrl, 'the stored picture is untouched').toBe(urlBefore);
  const bytes = await (await api.request.get((layerOf(after, source.id) as unknown as Cropped).imageUrl)).body();
  expect([bytes.readUInt32BE(16), bytes.readUInt32BE(20)], 'the original pixels are still 1920 x 1080').toEqual([1920, 1080]);

  // Exported pixels: the left half (red over blue) is there, the green right half is not.
  await savedBadge(page);
  await press(page.getByRole('button', { name: 'Export', exact: true }), false);
  const exportDialog = page.getByRole('dialog', { name: 'Export your picture' });
  const download = page.waitForEvent('download', { timeout: 30_000 });
  await press(exportDialog.getByRole('button', { name: 'Download' }), false);
  const img = decodeImage(await saveDownload(await download, 't35crop'));
  expect(img.width).toBe(1920);
  const [r1, g1, b1] = img.at(480, 270);
  expect([r1 > 150, g1 < 90, b1 < 90], 'top-left stays red').toEqual([true, true, true]);
  const [r2, g2, b2] = img.at(480, 810);
  expect([r2 < 90, g2 < 90, b2 > 150], 'bottom-left stays blue').toEqual([true, true, true]);
  const right = img.at(1440, 270);
  expect(right[1] > 150 && right[3] > 0, 'the cropped-away green half is not exported').toBe(false);
  await page.keyboard.press('Escape');

  // The crop survives a reload and can be widened again (the original was kept).
  await reloadPicture(page);
  const again = await openCrop(page, source.label, false);
  await expect(again.getByTestId('crop-w')).toHaveValue('960');
  await again.getByTestId('crop-w').fill('1920');
  await again.getByTestId('crop-w').press('Enter');
  await expect(again.getByTestId('crop-apply')).toHaveText('Remove crop');
  await again.getByTestId('crop-apply').click();
  await pollPicture(api, pics.projectId, pic.id, (p) => (cropOf(p, source.id) === null ? true : null), 'removing the crop clears the field');
  const widened = layerOf(await getFull(api, pics.projectId, pic.id), source.id).transform;
  expect(widened.x, 'the picture is back where it started').toBeCloseTo(layerOf(pic, source.id).transform.x, 1);

  // Undo is one step: it brings the crop back.
  await page.getByRole('button', { name: 'Undo' }).click();
  await pollPicture(api, pics.projectId, pic.id, (p) => cropOf(p, source.id), 'one undo restores the crop');
});

test('T35.2 a cropped layer merged with another is baked into the merged layer; undo brings both layers back', async ({ page, api, pics }, testInfo) => {
  runOn('desktop');
  const { pic } = await openFresh(page, api, pics, `t35merge-${testInfo.project.name}`);
  const badge = await addBadgeLayer(api, pics.projectId, pic.id, pics);
  await reloadPicture(page);
  const source = pic.layers[0];
  const dialog = await openCrop(page, source.label, false);
  await dialog.getByTestId('crop-w').fill('960');
  await dialog.getByTestId('crop-w').press('Enter');
  await dialog.getByTestId('crop-apply').click();
  await savedBadge(page);
  await page.getByTestId('arrange-pick-switch').click({ force: true });
  await page.getByTestId(`layer-pick-${badge.id}`).check({ force: true });
  await page.getByTestId(`layer-pick-${source.id}`).check({ force: true });
  await page.getByTestId('merge-layers').click();
  const merged = await pollPicture(api, pics.projectId, pic.id, (p) => p.layers.find((l) => l.label === 'Merged layer' && l.visible !== false) ?? null, 'one merged layer is stored');
  expect(await cropOf(await getFull(api, pics.projectId, pic.id), merged!.id), 'the merged layer is a flat picture').toBeNull();
  await expect(list(page).getByRole('button', { name: /^Select / })).toHaveCount(1);
  await page.getByRole('button', { name: 'Undo' }).click();
  await expect(list(page).getByRole('button', { name: /^Select / })).toHaveCount(2);
  await pollPicture(api, pics.projectId, pic.id, (p) => (cropOf(p, source.id) ? true : null), 'the original keeps its crop after undoing the merge');
  await page.goto(hubUrl(pics.projectId));
});

test.describe('T35 crop on a tablet (1024x768, coarse pointer)', () => {
  test.use({ viewport: { width: 1024, height: 768 }, hasTouch: true });

  test('T35.3 tablet: the crop handles are at least 44 px and a tapped crop is stored', async ({ page, api, pics }, testInfo) => {
    runOn('desktop');
    const { pic } = await openFresh(page, api, pics, `t35tab-${testInfo.project.name}`);
    expect(await page.evaluate(() => window.matchMedia('(pointer: coarse)').matches), 'the page sees a coarse pointer').toBe(true);
    const source = pic.layers[0];
    await list(page).getByRole('button', { name: `Select ${source.label}` }).tap();
    await page.getByTestId('crop-open').tap();
    const dialog = page.getByTestId('crop-dialog');
    for (const id of ['nw', 'n', 'ne', 'e', 'se', 's', 'sw', 'w']) {
      const box = (await dialog.getByTestId(`crop-handle-${id}`).boundingBox())!;
      expect(Math.min(box.width, box.height), `handle ${id}`).toBeGreaterThanOrEqual(44);
    }
    await dialog.getByTestId('crop-h').fill('540');
    await dialog.getByTestId('crop-h').press('Enter');
    await dialog.getByTestId('crop-apply').tap();
    const stored = await pollPicture(api, pics.projectId, pic.id, (p) => cropOf(p, source.id), 'tablet: the crop is stored');
    expect(stored!.h).toBeCloseTo(0.5, 3);
  });
});
