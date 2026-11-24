import * as fs from 'fs';
import * as path from 'path';
import {
  test, expect, freshProject, hubUrl, isPhone, press, openTab, makePng, decodeImage, saveDownload, getFull, pollPicture, savedBadge,
  addTextLayer, stageOf,
} from '../../../fixtures/editor-v2/picture-lane';
import { picturesUrl } from '../../../fixtures/editor-v2/picture';

/**
 * Wave 1, picture track (T-011): the marketer's two stories end to end, with REAL files.
 *  - R13.1 / R13.2 / R13.3 / R13.9: a 1080x1350 post from a phone photo, renamed, exported as a JPEG under 2 MB.
 *  - R13.1: a blank 1080x1350 picture, a text layer, an export.
 */

test.use({ serviceWorkers: 'block' });

/** Waits for the stored picture to satisfy `ok`, then returns it. */
async function pollFull(api: Parameters<typeof getFull>[0], projectId: string, id: string, ok: (p: Awaited<ReturnType<typeof getFull>>) => boolean, message: string) {
  await pollPicture(api, projectId, id, ok, message);
  return getFull(api, projectId, id);
}

/** A 2160 x 2700 "phone photo" of 2x2 blocks of random colour: detailed enough that a JPEG of it is large. */
function phonePhoto(file: string): void {
  let seed = 12345;
  const rand = () => {
    seed = (seed * 1664525 + 1013904223) >>> 0;
    return seed >>> 24;
  };
  const cache = new Map<number, [number, number, number]>();
  const png = makePng(2160, 2700, (x, y) => {
    const key = (y >> 1) * 1080 + (x >> 1);
    let v = cache.get(key);
    if (!v) {
      v = [rand(), rand(), rand()];
      cache.set(key, v);
    }
    return v;
  });
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, png);
}

test('R13.1 R13.2 R13.3 R13.9 a marketer makes a 1080x1350 post from a phone photo, renames it and exports a JPEG under 2 MB', async ({ page, api }, testInfo) => {
  test.setTimeout(240_000);
  const phone = isPhone(testInfo.project.name);
  const project = await freshProject(api, `r139-${testInfo.project.name}`, false);
  const photo = path.join('/tmp/editor-remediation/media/picture-lane', 'phone-photo.png');
  phonePhoto(photo);

  // Upload from the Pictures tab: the photo lands in the project's files and the editor opens on it.
  await page.goto(hubUrl(project.id));
  await page.locator('input[type=file]').first().setInputFiles(photo);
  await expect(page).toHaveURL(/\/editor\/thumbnails\/[0-9a-f-]{36}\?project=/, { timeout: 90_000 });
  const docId = /thumbnails\/([0-9a-f-]{36})/.exec(page.url())![1];
  await expect(stageOf(page)).toBeVisible({ timeout: 60_000 });
  expect((await api.listFiles(project.id)).some((f) => f.originalFileName === 'phone-photo.png'), 'the photo is in the project files').toBe(true);

  // The phone photo is 4:5 already; the 1080p size makes it a 1080 x 1350 post (the same shape, so the layer is refitted without asking).
  await openTab(page, 'Canvas');
  await press(page.getByRole('radiogroup', { name: 'Size' }).or(page.locator('[aria-labelledby="canvas-size-label"]')).first().getByText('1080p', { exact: true }), phone);
  const post = await pollFull(api, project.id, docId, (p) => p.canvasWidth === 1080 && p.canvasHeight === 1350, 'the post is 1080 x 1350');
  expect(post.layers.length).toBe(1);
  await expect.poll(async () => (await getFull(api, project.id, docId)).layers[0].transform.scale, { message: 'the photo was scaled into the post' }).toBeCloseTo(0.5, 1);
  await savedBadge(page);

  // Rename it from the hub's row menu.
  await page.goto(hubUrl(project.id));
  await press(page.getByRole('button', { name: /^More for / }).first(), phone);
  await press(page.getByRole('menuitem', { name: 'Rename' }), phone);
  await page.getByLabel('Picture name').fill('Spring sale post');
  await press(page.getByRole('button', { name: 'Save name' }), phone);
  await expect.poll(async () => (await getFull(api, project.id, docId)).name ?? '', { message: 'the new name is stored' }).toBe('Spring sale post');
  await expect(page.getByRole('link', { name: 'Open Spring sale post' })).toBeVisible();

  // The editor page carries the picture's name in its title.
  await page.getByRole('link', { name: 'Open Spring sale post' }).click();
  await expect(stageOf(page)).toBeVisible({ timeout: 60_000 });
  await expect(page.getByRole('heading', { name: 'Spring sale post' }).first()).toBeVisible();

  // Export: JPEG first, a file-size estimate, and "Under 2 MB" picks a quality that fits.
  await press(page.getByRole('button', { name: 'Export', exact: true }), phone);
  const dialog = page.getByRole('dialog', { name: 'Export your picture' });
  await expect(dialog).toBeVisible();
  const radios = dialog.getByRole('radio');
  await expect(radios.first(), 'web formats come first: JPEG leads').toContainText('JPEG');
  await press(dialog.getByRole('radio', { name: /^JPEG/ }), phone);
  await expect(dialog.getByTestId('export-estimate')).toContainText(/File size: about/, { timeout: 60_000 });
  await press(dialog.getByRole('button', { name: 'Under 2 MB' }), phone);
  await expect(dialog.getByTestId('export-fit-note')).toContainText(/Quality set to \d+%/, { timeout: 90_000 });
  await expect(dialog.getByTestId('export-estimate')).toContainText(/File size: about/, { timeout: 60_000 });

  const download = page.waitForEvent('download');
  await press(dialog.getByRole('button', { name: 'Download' }), phone);
  const file = await saveDownload(await download, 'r139');
  const size = fs.statSync(file).size;
  expect(size, 'the JPEG has bytes').toBeGreaterThan(10_000);
  expect(size, 'and fits under 2 MB').toBeLessThanOrEqual(2_000_000);
  expect([...fs.readFileSync(file).subarray(0, 2)], 'it is a JPEG').toEqual([0xff, 0xd8]);
  const img = decodeImage(file);
  expect([img.width, img.height], 'the pixel size is the post').toEqual([1080, 1350]);
});

test('R13.1 a blank 1080x1350 picture with a text layer exports', async ({ page, api }, testInfo) => {
  test.setTimeout(180_000);
  const phone = isPhone(testInfo.project.name);
  const project = await freshProject(api, `r131-${testInfo.project.name}`, false);

  await page.goto(hubUrl(project.id));
  await press(page.getByTestId('picture-shape-post'), phone);
  await page.getByLabel('Name of the new picture').fill('Blank post');
  await press(page.getByTestId('picture-new-create'), phone);
  await expect(page).toHaveURL(/\/editor\/thumbnails\/[0-9a-f-]{36}\?project=/, { timeout: 60_000 });
  const docId = /thumbnails\/([0-9a-f-]{36})/.exec(page.url())![1];
  await expect(stageOf(page)).toBeVisible({ timeout: 60_000 });

  const stored = await getFull(api, project.id, docId);
  expect([stored.canvasWidth, stored.canvasHeight], 'a blank post').toEqual([1080, 1350]);
  expect(stored.layers.length, 'no layers yet').toBe(0);
  expect(stored.name).toBe('Blank post');
  await expect(page.getByRole('heading', { name: 'Blank post' }).first()).toBeVisible();

  // A text layer (white on the transparent canvas), then export a PNG.
  await addTextLayer(api, project.id, docId, 'Spring sale', { x: 100, y: 100 });
  await page.reload();
  await expect(stageOf(page)).toBeVisible({ timeout: 60_000 });
  await press(page.getByRole('button', { name: 'Export', exact: true }), phone);
  const dialog = page.getByRole('dialog', { name: 'Export your picture' });
  await press(dialog.getByRole('radio', { name: /^PNG/ }), phone);
  const download = page.waitForEvent('download');
  await press(dialog.getByRole('button', { name: 'Download' }), phone);
  const file = await saveDownload(await download, 'r131');
  expect(fs.statSync(file).size).toBeGreaterThan(500);
  const img = decodeImage(file);
  expect([img.width, img.height]).toEqual([1080, 1350]);
  // Somewhere in the headline's box there is a white pixel; the rest of the canvas is see-through.
  let white = 0;
  for (let y = 100; y < 260 && white === 0; y += 4) for (let x = 100; x < 900; x += 4) if (img.at(x, y)[3] > 200) white++;
  expect(white, 'the text is in the pixels').toBeGreaterThan(0);
  expect(img.at(1050, 1300)[3], 'the rest is see-through').toBe(0);
  void picturesUrl;
});
