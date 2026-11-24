import {
  test, expect, openFresh, savedBadge, pollPicture, isPhone, press, addTextLayer, layerOf, openTab, stageOf, stageInView, decodeImage, saveDownload,
} from '../../../fixtures/editor-v2/picture-lane';

/** Lane D, picture editor: the Text tab (inventory rows PE-100..PE-114, PE-123). */

// The app registers a service worker; Playwright WebKit cannot route requests it handles, so route stubs need it blocked (as 09-analysis/transcript does).
test.use({ serviceWorkers: 'block' });

test('PE-100 PE-114 the Text tab explains itself with no text layer, and "Add text" adds one that is selected', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe100-${testInfo.project.name}`);
  await openTab(page, 'Text');
  await expect(page.getByText(/select a text layer to change its words/i), 'empty state: instructions, no fields').toBeVisible();
  await expect(page.getByLabel('Words')).toHaveCount(0);

  await press(page.getByRole('button', { name: 'Add text' }), phone);
  const after = await pollPicture(api, pics.projectId, pic.id, (p) => (p.layers.some((l) => l.kind.toLowerCase() === 'text') ? p : null), 'a text layer exists on the server');
  const text = after!.layers.find((l) => l.kind.toLowerCase() === 'text')!;
  expect(text.content?.text, 'default content').toBeTruthy();
  await expect(page.getByLabel('Words'), 'the new layer is selected, so its fields show').toBeVisible();
  await expect(page.getByLabel('Words')).toHaveValue(text.content!.text);
});

test('PE-101 PE-102 PE-103 PE-104 PE-105 PE-106 PE-107 PE-108 PE-109 words, font, weight, size, colour, alignment, spacing and capitals are saved', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe101-${testInfo.project.name}`);
  const text = await addTextLayer(api, pics.projectId, pic.id, 'Hello world');
  await page.reload();
  await press(page.getByRole('button', { name: `Select ${text.label}` }), phone);
  await openTab(page, 'Text');
  const content = async () => layerOf(await (await import('../../../fixtures/editor-v2/picture-lane')).getFull(api, pics.projectId, pic.id), text.id).content!;

  await page.getByLabel('Words').fill('Big news today');
  await expect.poll(async () => (await content()).text).toBe('Big news today');

  await page.getByRole('textbox', { name: 'Font' }).or(page.getByRole('combobox', { name: 'Font' })).first().click();
  await page.getByRole('option', { name: 'Oswald' }).click();
  await expect.poll(async () => (await content()).fontFamily, { message: 'font family saved' }).toBe('Oswald');

  await page.getByRole('textbox', { name: 'Weight' }).or(page.getByRole('combobox', { name: 'Weight' })).first().click();
  const options = await page.getByRole('option').allInnerTexts();
  const pick = options.find((o) => o.trim() !== '700') ?? options[0];
  await page.getByRole('option', { name: pick.trim(), exact: true }).click();
  await expect.poll(async () => String((await content()).fontWeight), { message: 'weight saved' }).toBe(pick.trim());

  await page.getByRole('textbox', { name: 'Size' }).fill('96');
  await page.getByRole('textbox', { name: 'Colour', exact: true }).fill('#ff0000');
  await page.getByRole('textbox', { name: 'Colour', exact: true }).blur();
  await page.getByLabel('Line height').fill('1.4');
  await page.getByLabel('Letter spacing').fill('3');
  await expect.poll(async () => (await content()).fontSize, { message: 'size saved' }).toBe(96);
  await expect.poll(async () => (await content()).color.toLowerCase(), { message: 'colour saved' }).toBe('#ff0000');
  await expect.poll(async () => (await content()).lineHeight).toBeCloseTo(1.4, 2);
  await expect.poll(async () => (await content()).letterSpacing).toBe(3);

  await press(page.getByRole('radiogroup', { name: 'Alignment' }).getByText(/centre|center/i), phone);
  await expect.poll(async () => (await content()).align, { message: 'alignment saved' }).toBe('center');
  await press(page.getByRole('checkbox', { name: 'ALL CAPITALS' }).or(page.getByLabel('ALL CAPITALS')).first(), phone);
  await expect.poll(async () => (await content()).uppercase).toBe(true);

  await savedBadge(page);
  await page.reload();
  await press(page.getByRole('button', { name: `Select ${text.label}` }), phone);
  await openTab(page, 'Text');
  await expect(page.getByLabel('Words'), 'a reload shows the saved words').toHaveValue('Big news today');
});

test('PE-110 PE-111 PE-112 wrap width, outline and shadow switch on with their fields, are saved, and switch off to null', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe110-${testInfo.project.name}`);
  const text = await addTextLayer(api, pics.projectId, pic.id, 'Wrap me please');
  await page.reload();
  await press(page.getByRole('button', { name: `Select ${text.label}` }), phone);
  await openTab(page, 'Text');
  const content = async () => layerOf(await (await import('../../../fixtures/editor-v2/picture-lane')).getFull(api, pics.projectId, pic.id), text.id).content!;
  const toggle = (label: string) => press(page.getByLabel(label, { exact: true }).first(), phone);

  await toggle('Wrap lines at a width');
  await expect.poll(async () => (await content()).maxWidth, { message: 'wrap width defaults to 900' }).toBe(900);
  await page.getByLabel('Wrap width (px)').fill('640');
  await expect.poll(async () => (await content()).maxWidth).toBe(640);

  await toggle('Outline');
  await expect.poll(async () => (await content()).stroke).not.toBeNull();
  await page.getByLabel('Outline width').fill('10');
  await expect.poll(async () => (await content()).stroke?.width).toBe(10);

  await toggle('Shadow');
  await expect.poll(async () => (await content()).shadow).not.toBeNull();
  await page.getByLabel('Blur', { exact: true }).fill('30');
  await page.getByLabel('Down', { exact: true }).fill('12');
  await expect.poll(async () => (await content()).shadow?.blur).toBe(30);
  await expect.poll(async () => (await content()).shadow?.offsetY).toBe(12);

  await toggle('Wrap lines at a width');
  await toggle('Outline');
  await toggle('Shadow');
  await expect.poll(async () => JSON.stringify([(await content()).maxWidth, (await content()).stroke, (await content()).shadow]), { message: 'switching off stores null' }).toBe('[null,null,null]');
});

test('PE-113 an out-of-range size is explained and never sent', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe113-${testInfo.project.name}`);
  const text = await addTextLayer(api, pics.projectId, pic.id, 'Limits');
  await page.reload();
  await press(page.getByRole('button', { name: `Select ${text.label}` }), phone);
  await openTab(page, 'Text');
  await page.getByRole('textbox', { name: 'Size' }).fill('5');
  await expect(page.getByText(/size/i).filter({ hasText: /8|600|between|at least/i }).first(), 'an alert names the limit').toBeVisible();
  await page.waitForTimeout(1500);
  expect(layerOf(await (await import('../../../fixtures/editor-v2/picture-lane')).getFull(api, pics.projectId, pic.id), text.id).content!.fontSize, 'the bad value was not saved').toBe(120);
});

test('PE-067 the Text tool places a text layer where the stage is clicked, as one undo step', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic, stage } = await openFresh(page, api, pics, `pe067-${testInfo.project.name}`);
  await press(page.getByRole('button', { name: 'Text', exact: true }), phone);
  await expect(page.getByRole('tab', { name: 'Text', exact: true })).toHaveAttribute('aria-selected', 'true');
  await stageInView(page);
  const box = (await stage.boundingBox())!;
  if (phone) await page.touchscreen.tap(box.x + box.width * 0.5, box.y + box.height * 0.5);
  else await page.mouse.click(box.x + box.width * 0.5, box.y + box.height * 0.5);
  const after = await pollPicture(api, pics.projectId, pic.id, (p) => (p.layers.some((l) => l.kind.toLowerCase() === 'text') ? p : null), 'the text layer was placed');
  const text = after!.layers.find((l) => l.kind.toLowerCase() === 'text')!;
  expect(text.transform.x, 'placed near the middle of a 1920 canvas').toBeGreaterThan(300);
  expect(text.transform.x).toBeLessThan(1700);
  await openTab(page, 'Layers');
  await expect(page.getByRole('list', { name: 'Layers, front first' }).getByRole('button', { name: 'Select Text' }).first()).toHaveAttribute('aria-current', 'true');
  await press(page.getByRole('button', { name: 'Undo', exact: true }), phone);
  await pollPicture(api, pics.projectId, pic.id, (p) => layerOf(p, text.id).visible === false, 'undo hides the placed text');
  void stageOf;
});

test('PE-123 text with the web fonts unreachable still draws, and the picture still exports', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  // Google Fonts is an outside dependency: block it (hang the stylesheet like a black-holed network) and prove nothing waits on it.
  await page.route(/fonts\.(googleapis|gstatic)\.com/, () => { /* never answered */ });
  const { pic } = await openFresh(page, api, pics, `pe123-${testInfo.project.name}`);
  const text = await addTextLayer(api, pics.projectId, pic.id, 'OFFLINE');
  await page.reload();
  await expect(page.getByRole('button', { name: `Select ${text.label}` })).toBeVisible();
  await press(page.getByRole('button', { name: 'Export', exact: true }), phone);
  const dialog = page.getByRole('dialog', { name: 'Export your picture' });
  const download = page.waitForEvent('download', { timeout: 30_000 });
  await press(dialog.getByRole('button', { name: 'Download' }), phone);
  const file = await saveDownload(await download, 'pe123');
  const img = decodeImage(file);
  expect(img.width).toBe(1920);
  expect(img.height).toBe(1080);
  expect(layerOf(await (await import('../../../fixtures/editor-v2/picture-lane')).getFull(api, pics.projectId, pic.id), text.id).content!.text, 'the content is intact').toBe('OFFLINE');
});
