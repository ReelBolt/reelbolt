import { reloadPicture } from '../../../fixtures/editor-v2/picture-lane';
import * as fs from 'fs';
import {
  test, expect, openFresh, isPhone, press, addBadgeLayer, decodeImage, near, saveDownload, QUADRANT, savedBadge, getFull,
} from '../../../fixtures/editor-v2/picture-lane';
import { picturesUrl } from '../../../fixtures/editor-v2/picture';

/**
 * Lane D, picture editor: the Export dialog (inventory rows PE-190..PE-197). Every download is a real file decoded by the
 * host ffmpeg: size > 0, the right pixel size, the right colours. "Save to project" needs the browser to reach storage
 * (presigned PUT): see e2e/scripts/editor-v2-picture-storage.sh.
 */

// The app registers a service worker; Playwright WebKit cannot route requests it handles, so route stubs need it blocked (as 09-analysis/transcript does).
test.use({ serviceWorkers: 'block' });

const open = async (page: import('@playwright/test').Page, phone: boolean) => {
  await press(page.getByRole('button', { name: 'Export', exact: true }), phone);
  const dialog = page.getByRole('dialog', { name: 'Export your picture' });
  await expect(dialog).toBeVisible();
  return dialog;
};

test('PE-190 PE-191 PE-193 PE-194 Download gives a real PNG of the right size and colours, named from the File name field', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe194-${testInfo.project.name}`);
  const badge = await addBadgeLayer(api, pics.projectId, pic.id, pics);
  await reloadPicture(page);
  await expect(page.getByRole('button', { name: `Hide ${badge.label}` })).toBeVisible();

  const dialog = await open(page, phone);
  await expect(dialog.getByRole('radio', { name: /^PNG/ })).toHaveAttribute('aria-checked', 'true');
  await dialog.getByLabel('File name').fill('my-thumb');
  await expect(dialog.getByText('Saved as my-thumb.png, 1920 × 1080 pixels')).toBeVisible();

  const download = page.waitForEvent('download');
  await press(dialog.getByRole('button', { name: 'Download' }), phone);
  const dl = await download;
  expect(dl.suggestedFilename()).toBe('my-thumb.png');
  const file = await saveDownload(dl, 'pe194');
  expect(fs.statSync(file).size, 'the file has bytes').toBeGreaterThan(1000);
  expect([...fs.readFileSync(file).subarray(0, 8)], 'it is a PNG').toEqual([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const img = decodeImage(file);
  expect([img.width, img.height], 'the pixel size is the canvas').toEqual([1920, 1080]);
  expect(near(img.at(1800, 100), QUADRANT.tr), 'top-right quadrant is green').toBe(true);
  expect(near(img.at(100, 1000), QUADRANT.bl), 'bottom-left is blue').toBe(true);
  expect(near(img.at(1800, 1000), QUADRANT.br), 'bottom-right is yellow').toBe(true);
  expect(near(img.at(100, 100), [255, 0, 255]), 'the badge layer covers the top-left corner').toBe(true);
  await expect(dialog.getByText(/Downloaded my-thumb\.png \(1920 × 1080/)).toBeVisible();

  // A hidden layer is not in the pixels.
  await press(dialog.getByRole('button', { name: 'Close' }), phone);
  await press(page.getByRole('button', { name: `Hide ${badge.label}` }), phone);
  await savedBadge(page);
  const dialog2 = await open(page, phone);
  const second = page.waitForEvent('download');
  await press(dialog2.getByRole('button', { name: 'Download' }), phone);
  const hidden = decodeImage(await saveDownload(await second, 'pe194-hidden'));
  expect(near(hidden.at(100, 100), QUADRANT.tl), 'with the badge hidden the corner is the photo (red)').toBe(true);
});

test('PE-191 PE-192 PE-194 JPEG, WebP, Photoshop and OpenRaster files download and are what they say; quality changes the size', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe191-${testInfo.project.name}`);
  await addBadgeLayer(api, pics.projectId, pic.id, pics);
  await reloadPicture(page);
  const dialog = await open(page, phone);
  const get = async (format: RegExp, tag: string) => {
    await press(dialog.getByRole('radio', { name: format }), phone);
    const d = page.waitForEvent('download');
    await press(dialog.getByRole('button', { name: 'Download' }), phone);
    return saveDownload(await d, tag);
  };

  const jpegHigh = await get(/^JPEG/, 'jpeg-hi');
  const jpegImg = decodeImage(jpegHigh);
  expect([jpegImg.width, jpegImg.height]).toEqual([1920, 1080]);
  expect(near(jpegImg.at(1800, 100), QUADRANT.tr, 40)).toBe(true);
  // Lower the quality: a smaller file.
  const quality = dialog.getByRole('slider', { name: 'Quality' });
  await quality.focus();
  await quality.press('Home');
  const jpegLow = await get(/^JPEG/, 'jpeg-lo');
  expect(fs.statSync(jpegLow).size, 'lower quality, fewer bytes').toBeLessThan(fs.statSync(jpegHigh).size);

  const webp = decodeImage(await get(/^WebP/, 'webp'));
  expect([webp.width, webp.height]).toEqual([1920, 1080]);

  // PSD, ORA and AVIF sit under "More formats" (R13.9).
  await press(dialog.getByTestId('export-more-formats'), phone);
  const psd = fs.readFileSync(await get(/^Photoshop/, 'psd'));
  expect(psd.subarray(0, 4).toString('ascii'), 'a Photoshop file starts with 8BPS').toBe('8BPS');
  expect(psd.readUInt32BE(14), 'its height is the canvas height').toBe(1080);

  const ora = fs.readFileSync(await get(/^OpenRaster/, 'ora'));
  expect(ora.subarray(0, 2).toString('ascii'), 'an OpenRaster file is a zip').toBe('PK');
  expect(ora.subarray(30, 38).toString('ascii'), 'whose first entry is "mimetype"').toBe('mimetype');

  const avif = dialog.getByRole('radio', { name: /^AVIF/ });
  if (testInfo.project.name === 'iphone14webkit') await expect(avif, 'WebKit cannot make AVIF: disabled with a reason').toHaveAttribute('aria-disabled', 'true');
  await expect(dialog.getByText(/Saved as .*\.ora/)).toBeVisible();
});

test('PE-195 Save to project stores the picture as a project file that decodes to the canvas size', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe195-${testInfo.project.name}`);
  const dialog = await open(page, phone);
  const name = `saved-${testInfo.project.name}-${Date.now()}`;
  await dialog.getByLabel('File name').fill(name);
  await press(dialog.getByRole('button', { name: 'Save to project' }), phone);
  await expect(dialog.getByText(new RegExp(`Saved as ${name}\\.png in your project's files \\(1920 × 1080\\)`)), 'the green confirmation line').toBeVisible({ timeout: 30_000 });

  const exports = await api.json<Array<{ id: string; projectFileId: string | null; width: number; height: number }>>('GET', `${picturesUrl(pics.projectId, pic.id)}/exports`);
  expect(exports.length, 'one export row').toBe(1);
  const fileId = exports[0].projectFileId!;
  expect(exports[0]).toMatchObject({ width: 1920, height: 1080 });
  const files = await api.listFiles(pics.projectId);
  expect(files.some((f) => f.id === fileId && f.originalFileName === `${name}.png`), 'the file is in the project Files').toBe(true);

  const res = await api.request.get(`/api/v1/projects/${pics.projectId}/files/${fileId}/media`);
  expect(res.status()).toBe(200);
  const bytes = await res.body();
  expect(bytes.length, 'the stored object has bytes').toBeGreaterThan(1000);
  const stored = await saveDownload({ suggestedFilename: () => 'stored.png', saveAs: async (p: string) => fs.writeFileSync(p, bytes) }, 'pe195');
  const img = decodeImage(stored);
  expect([img.width, img.height], 'the stored file decodes to the canvas size').toEqual([1920, 1080]);
  expect(near(img.at(100, 100), QUADRANT.tl)).toBe(true);
  void getFull;
});

test('PE-195b when storage cannot be reached the dialog says so in plain words, and nothing is half-saved', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe195b-${testInfo.project.name}`);
  // The browser cannot reach the presigned URL (a storage host only the server network can name, or a missing CORS rule).
  await page.route((url) => url.searchParams.has('X-Amz-Signature') || url.searchParams.has('X-Amz-Credential'), (route) => route.abort('failed'));
  const dialog = await open(page, phone);
  await press(dialog.getByRole('button', { name: 'Save to project' }), phone);
  const message = dialog.getByText(/could not reach storage/i);
  await expect(message).toBeVisible({ timeout: 30_000 });
  await expect(message, 'the message points the operator at the storage address or CORS').toContainText(/CORS|storage address/i);
  const exports = await api.json<unknown[]>('GET', `${picturesUrl(pics.projectId, pic.id)}/exports`);
  expect(exports, 'no export was registered').toEqual([]);
  await expect(dialog.getByRole('button', { name: 'Download' }), 'Download still works as the fallback').toBeEnabled();
});

test('PE-196 a layer image that cannot load blocks the export, names the layer, and Try again recovers', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe196-${testInfo.project.name}`);
  const layer = pic.layers[0];
  let fail = true;
  await page.route(new RegExp(`/layers/${layer.id}/image`), (route) => (fail ? route.abort('failed') : route.continue()));
  await reloadPicture(page);
  await expect(page.getByRole('button', { name: /^Select / }).first()).toBeVisible();
  const dialog = await open(page, phone);
  await expect(dialog.getByText(new RegExp(`"${layer.label}" could not be loaded`)), 'the blocker names the layer').toBeVisible({ timeout: 30_000 });
  await expect(dialog.getByRole('button', { name: 'Download' })).toBeDisabled();
  fail = false;
  await press(dialog.getByRole('button', { name: 'Try again' }), phone);
  await expect(dialog.getByRole('button', { name: 'Download' }), 'after the layer loads, export is allowed').toBeEnabled({ timeout: 30_000 });
});

test('PE-196b a layer whose stored image is gone is left out of the export, with a notice', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe196b-${testInfo.project.name}`);
  const badge = await addBadgeLayer(api, pics.projectId, pic.id, pics);
  await page.route(new RegExp(`/layers/${badge.id}/image`), (route) =>
    route.fulfill({ status: 404, contentType: 'application/json', body: JSON.stringify({ code: 'LAYER_IMAGE_MISSING', message: 'gone' }) }));
  await reloadPicture(page);
  await expect(page.getByText('Image missing')).toBeVisible({ timeout: 30_000 });
  const dialog = await open(page, phone);
  await expect(dialog.getByText(new RegExp(`The image of "${badge.label}" is gone, so the export leaves it out`))).toBeVisible();
  const d = page.waitForEvent('download');
  await press(dialog.getByRole('button', { name: 'Download' }), phone);
  const img = decodeImage(await saveDownload(await d, 'pe196b'));
  expect(near(img.at(100, 100), QUADRANT.tl), 'the exported corner is the photo, not the missing badge').toBe(true);
});
