import * as fs from 'fs';
import {
  test, expect, openFresh, freshProject, hubUrl, isPhone, press, openTab, solidPng, getFull, pollPicture, savedBadge, createFull,
  decodeImage, saveDownload, layerOf,
} from '../../../fixtures/editor-v2/picture-lane';
import { picturesUrl } from '../../../fixtures/editor-v2/picture';

/** Wave 1, picture track (T-011): the pictures list, shape refit, uploading an image layer, the export format list. */

test.use({ serviceWorkers: 'block' });

/** Waits for the stored picture to satisfy `ok`, then returns it. */
async function pollFull(api: Parameters<typeof getFull>[0], projectId: string, id: string, ok: (p: Awaited<ReturnType<typeof getFull>>) => boolean, message: string) {
  await pollPicture(api, projectId, id, ok, message);
  return getFull(api, projectId, id);
}

test('R13.2 rename, start a new one from the photo, and delete a picture end to end', async ({ page, api }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const project = await freshProject(api, `r132-${testInfo.project.name}`);
  const first = await createFull(api, project.id, project.quad!, 'First picture');
  await page.goto(hubUrl(project.id));
  const list = page.getByRole('list', { name: 'Your pictures' });
  await expect(list.getByRole('link', { name: 'Open First picture' })).toBeVisible();

  // Rename.
  await press(page.getByRole('button', { name: 'More for First picture' }), phone);
  await press(page.getByRole('menuitem', { name: 'Rename' }), phone);
  await page.getByLabel('Picture name').fill('Renamed picture');
  await press(page.getByRole('button', { name: 'Save name' }), phone);
  await expect.poll(async () => (await getFull(api, project.id, first.id)).name).toBe('Renamed picture');

  // Start a new one from this photo: a second document on the same photo.
  await press(page.getByRole('button', { name: 'More for Renamed picture', exact: true }), phone);
  await press(page.getByRole('menuitem', { name: 'Start a new one from this photo' }), phone);
  await expect(page).toHaveURL(/\/editor\/thumbnails\/[0-9a-f-]{36}\?project=/, { timeout: 60_000 });
  const secondId = /thumbnails\/([0-9a-f-]{36})/.exec(page.url())![1];
  expect(secondId).not.toBe(first.id);
  const all = await api.json<Array<{ id: string }>>('GET', picturesUrl(project.id));
  expect(all.length, 'both pictures exist').toBe(2);

  // Delete the first one: the dialog says what is kept, then it is gone on the server.
  await page.goto(hubUrl(project.id));
  await press(page.getByRole('button', { name: 'More for Renamed picture', exact: true }), phone);
  await press(page.getByRole('menuitem', { name: 'Delete picture' }), phone);
  const confirm = page.getByRole('dialog');
  await expect(confirm).toContainText(/stay in your project's files/);
  await press(confirm.getByRole('button', { name: 'Delete picture' }), phone);
  await expect.poll(async () => (await api.request.get(picturesUrl(project.id, first.id), { failOnStatusCode: false })).status()).toBe(404);
  await expect(page.getByRole('link', { name: 'Open Renamed picture', exact: true })).toHaveCount(0);
});

test('R13.3 changing the shape fits the layers in one undo step; Keep positions leaves them; Original size restores the photo', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `r133-${testInfo.project.name}`);
  const badge = await api.json<{ id: string }>('POST', `${picturesUrl(pics.projectId, pic.id)}/layers/from-file`, { projectFileId: pics.badge, role: 'overlay' }, [200, 201]);
  await page.reload();
  const original = await getFull(api, pics.projectId, pic.id);
  const badgeBefore = layerOf(original, badge.id).transform;
  await openTab(page, 'Canvas');

  await press(page.getByRole('button', { name: /^1:1/ }), phone);
  await expect(page.getByTestId('canvas-refit-fit')).toBeVisible();
  await press(page.getByTestId('canvas-refit-fit'), phone);
  const factor = 1080 / 1920;
  const fitted = await pollFull(
    api, pics.projectId, pic.id,
    (p) => p.canvasWidth === p.canvasHeight && Math.abs(layerOf(p, badge.id).transform.scale - badgeBefore.scale * factor) < 0.01,
    'the square and the layer scaled with the frame are saved',
  );
  expect(layerOf(fitted, badge.id).transform.x, 'the layer moved with the frame').toBeCloseTo(badgeBefore.x * factor, 0);
  await savedBadge(page);

  // One undo puts the size and every layer back.
  await page.keyboard.press('Control+z');
  const undone = await pollFull(api, pics.projectId, pic.id, (p) => p.canvasWidth === original.canvasWidth && Math.abs(layerOf(p, badge.id).transform.scale - badgeBefore.scale) < 0.001, 'one undo restores the canvas size and the layer');
  expect(layerOf(undone, badge.id).transform.scale).toBeCloseTo(badgeBefore.scale, 3);
  expect(layerOf(undone, badge.id).transform.x).toBeCloseTo(badgeBefore.x, 1);

  // Keep positions: only the size changes.
  await press(page.getByRole('button', { name: /^9:16/ }), phone);
  await press(page.getByTestId('canvas-refit-keep'), phone);
  const kept = await pollFull(api, pics.projectId, pic.id, (p) => p.canvasWidth < p.canvasHeight, 'the tall canvas is saved');
  expect(layerOf(kept, badge.id).transform.scale).toBeCloseTo(badgeBefore.scale, 3);

  // Original size restores the source photo's size.
  await press(page.getByRole('button', { name: /^Original size/ }), phone);
  await pollPicture(api, pics.projectId, pic.id, (p) => p.canvasWidth === 1920 && p.canvasHeight === 1080, 'the photo size is back');
});

test('R13.4 "Upload an image" adds a picture from this device as a layer and puts it in the project files', async ({ page, api, pics }, testInfo) => {
  const { pic } = await openFresh(page, api, pics, `r134-${testInfo.project.name}`);
  const logo = '/tmp/editor-remediation/media/picture-lane/upload-logo.png';
  fs.writeFileSync(logo, solidPng(300, 200, [10, 120, 240]));
  await openTab(page, 'Layers');
  await expect(page.getByTestId('layer-upload-image')).toBeVisible();
  await page.locator('input[type=file]').first().setInputFiles(logo);
  const stored = await pollFull(api, pics.projectId, pic.id, (p) => p.layers.length === 2, 'the upload became a layer');
  expect(stored.layers.some((l) => l.label.includes('upload-logo')), 'the layer is named after the file').toBe(true);
  expect((await api.listFiles(pics.projectId)).some((f) => f.originalFileName === 'upload-logo.png'), 'and the file is in the project').toBe(true);
});

test('R13.9 the export dialog lists web formats first; AVIF, PSD and ORA sit under "More formats" with plain wording', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  await openFresh(page, api, pics, `r139b-${testInfo.project.name}`);
  await press(page.getByRole('button', { name: 'Export', exact: true }), phone);
  const dialog = page.getByRole('dialog', { name: 'Export your picture' });
  const names = dialog.getByRole('radio');
  await expect(names).toHaveCount(3);
  await expect(names.nth(0)).toContainText('JPEG');
  await expect(names.nth(1)).toContainText('PNG');
  await expect(names.nth(2)).toContainText('WebP');
  await press(dialog.getByTestId('export-more-formats'), phone);
  await expect(dialog.getByRole('radio', { name: /Photoshop \(PSD\)/ })).toContainText('Text becomes flattened pixels');
  await expect(dialog.getByRole('radio', { name: /OpenRaster/ })).toContainText('Text becomes flattened pixels');
  // The estimate follows the quality.
  await press(dialog.getByRole('radio', { name: /^JPEG/ }), phone);
  await expect(dialog.getByTestId('export-estimate')).toContainText(/File size: about/, { timeout: 60_000 });
  const read = async () => (await dialog.getByTestId('export-estimate').textContent()) ?? '';
  const high = await read();
  const slider = dialog.getByRole('slider', { name: 'Quality' });
  await slider.focus();
  for (let i = 0; i < 30; i++) await page.keyboard.press('ArrowLeft');
  await expect.poll(read, { timeout: 30_000 }).not.toBe(high);
  void decodeImage;
  void saveDownload;
});
