import {
  test, expect, openFresh, savedBadge, pollPicture, isPhone, press, openTab, decodeImage, saveDownload, near, getFull,
} from '../../../fixtures/editor-v2/picture-lane';

/** Lane D, picture editor: the Canvas tab, size and background (inventory rows PE-024, PE-130..PE-137). */

// The app registers a service worker; Playwright WebKit cannot route requests it handles, so route stubs need it blocked (as 09-analysis/transcript does).
test.use({ serviceWorkers: 'block' });

/** A new shape asks what to do with the layers (R13.3); these older rows only care about the size, so they keep positions. */
const keepPositions = async (page: import('@playwright/test').Page, phone: boolean) => {
  const keep = page.getByTestId('canvas-refit-keep');
  await keep.waitFor({ state: 'visible', timeout: 2000 }).then(() => press(keep, phone), () => undefined);
};

const readout = (page: import('@playwright/test').Page, w: number, h: number) => page.getByText(`${w} × ${h}`, { exact: true }).first();

test('PE-024 PE-130 PE-131 PE-132 shape presets, size tiers and "Original size" change the canvas, the readout and the server', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe130-${testInfo.project.name}`);
  await expect(readout(page, 1920, 1080), 'PE-024: the readout shows the canvas size').toBeVisible();
  await openTab(page, 'Canvas');

  await press(page.getByRole('button', { name: /^9:16/ }), phone);
  await keepPositions(page, phone);
  await pollPicture(api, pics.projectId, pic.id, (p) => p.canvasWidth === 1080 && p.canvasHeight === 1920, '9:16 at 1080p is 1080x1920');
  await expect(readout(page, 1080, 1920)).toBeVisible();

  await press(page.getByRole('button', { name: /^16:9/ }), phone);
  await keepPositions(page, phone);
  await pollPicture(api, pics.projectId, pic.id, (p) => p.canvasWidth === 1920 && p.canvasHeight === 1080, '16:9 again is 1920x1080');

  const tier = page.getByRole('radiogroup', { name: 'Size' }).or(page.locator('[aria-labelledby="canvas-size-label"]')).first();
  await press(tier.getByText('4K', { exact: true }), phone);
  await pollPicture(api, pics.projectId, pic.id, (p) => p.canvasWidth === 3840 && p.canvasHeight === 2160, '4K 16:9 is 3840x2160');
  await expect(readout(page, 3840, 2160)).toBeVisible();

  await press(page.getByRole('button', { name: /^Original size/ }), phone);
  await pollPicture(api, pics.projectId, pic.id, (p) => p.canvasWidth === 1920 && p.canvasHeight === 1080, 'Original size restores the photo size');
  await page.reload();
  await expect(readout(page, 1920, 1080), 'the size survives a reload').toBeVisible();
});

test('PE-133 PE-134 PE-137 exact width and height, swap, and the large-size warning', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe133-${testInfo.project.name}`);
  await openTab(page, 'Canvas');
  const use = page.getByRole('button', { name: /^Use / });
  await expect(use, 'nothing changed yet, nothing to apply').toBeDisabled();

  await page.getByLabel('Width', { exact: true }).fill('1000');
  await page.getByLabel('Height', { exact: true }).fill('500');
  await press(page.getByRole('button', { name: 'Use 1000 × 500' }), phone);
  await keepPositions(page, phone);
  await pollPicture(api, pics.projectId, pic.id, (p) => p.canvasWidth === 1000 && p.canvasHeight === 500, '1000x500 on the server');
  await expect(readout(page, 1000, 500)).toBeVisible();

  await press(page.getByRole('button', { name: 'Swap width and height' }), phone);
  await press(page.getByRole('button', { name: 'Use 500 × 1000' }), phone);
  await keepPositions(page, phone);
  await pollPicture(api, pics.projectId, pic.id, (p) => p.canvasWidth === 500 && p.canvasHeight === 1000, 'swapped on the server');

  await page.getByLabel('Width', { exact: true }).fill('4100');
  await page.getByLabel('Height', { exact: true }).fill('4100');
  await press(page.getByRole('button', { name: 'Use 4100 × 4100' }), phone);
  await keepPositions(page, phone);
  await expect(page.getByText(/may not open on some phones/i)).toBeVisible();
  await pollPicture(api, pics.projectId, pic.id, (p) => p.canvasWidth === 4100, 'large canvas saved');
});

test('PE-135 PE-136 the background: transparent shows no colour in a PNG export, a colour fills it', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe135-${testInfo.project.name}`);
  await openTab(page, 'Canvas');
  // A canvas wider than the photo leaves a strip the photo does not cover.
  await page.getByLabel('Width', { exact: true }).fill('2400');
  await page.getByLabel('Height', { exact: true }).fill('1080');
  await press(page.getByRole('button', { name: 'Use 2400 × 1080' }), phone);
  await keepPositions(page, phone);
  await pollPicture(api, pics.projectId, pic.id, (p) => p.canvasWidth === 2400, 'canvas widened');

  const exportPng = async (tag: string) => {
    await press(page.getByRole('button', { name: 'Export', exact: true }), phone);
    const dialog = page.getByRole('dialog', { name: 'Export your picture' });
    const d = page.waitForEvent('download');
    await press(dialog.getByRole('button', { name: 'Download' }), phone);
    const img = decodeImage(await saveDownload(await d, tag));
    await press(dialog.getByRole('button', { name: 'Close' }), phone);
    return img;
  };

  expect((await getFull(api, pics.projectId, pic.id)).backgroundColor, 'transparent by default').toBeNull();
  const clear = await exportPng('pe135-clear');
  expect(clear.at(2300, 500)[3], 'outside the photo the PNG is see-through').toBe(0);

  await press(page.getByLabel('Transparent', { exact: true }), phone);
  await pollPicture(api, pics.projectId, pic.id, (p) => p.backgroundColor !== null, 'a background colour is set');
  const colour = page.getByRole('textbox', { name: 'Colour behind the layers' });
  await colour.fill('#ff0000');
  await colour.blur();
  await pollPicture(api, pics.projectId, pic.id, (p) => (p.backgroundColor ?? '').toLowerCase() === '#ff0000', 'red saved');
  await savedBadge(page);
  const red = await exportPng('pe135-red');
  const px = red.at(2300, 500);
  expect(px[3], 'now opaque').toBe(255);
  expect(near(px, [255, 0, 0]), 'and red').toBe(true);

  await press(page.getByLabel('Transparent', { exact: true }), phone);
  await pollPicture(api, pics.projectId, pic.id, (p) => p.backgroundColor === null, 'transparent again clears the colour on the server');
});
