import { reloadPicture, layerAction, openPanel } from '../../../fixtures/editor-v2/picture-lane';
import {
  test, expect, openFresh, savedBadge, getFull, pollPicture, isPhone, press, addBadgeLayer, addTextLayer, layerOf, openTab, createFull,
} from '../../../fixtures/editor-v2/picture-lane';
import { openPicture } from '../../../fixtures/editor-v2/picture';
import { runOn } from '../../../fixtures/editor-v2/rows';

/** Lane D, picture editor: the Layers tab (inventory rows PE-080..PE-097). */

// The app registers a service worker; Playwright WebKit cannot route requests it handles, so route stubs need it blocked (as 09-analysis/transcript does).
test.use({ serviceWorkers: 'block' });

const list = (page: import('@playwright/test').Page) => page.getByRole('list', { name: 'Layers, front first' });

test('PE-081 PE-082 PE-083 hide, lock and select a layer: the server follows and a reload shows it', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe081-${testInfo.project.name}`);
  const layer = await addBadgeLayer(api, pics.projectId, pic.id, pics);
  await reloadPicture(page);
  await expect(list(page).getByRole('button', { name: /^Select / })).toHaveCount(2);

  // Select: the row is marked current and the layer's own controls appear.
  const badgeSelect = list(page).getByRole('button', { name: `Select ${layer.label}` });
  await press(badgeSelect, phone);
  await expect(badgeSelect).toHaveAttribute('aria-current', 'true');
  await expect(page.getByLabel('Name', { exact: true })).toHaveValue(layer.label);

  // Hide, then show.
  await press(page.getByRole('button', { name: `Hide ${layer.label}` }), phone);
  await pollPicture(api, pics.projectId, pic.id, (p) => layerOf(p, layer.id).visible === false, 'hidden on the server');
  await press(page.getByRole('button', { name: `Show ${layer.label}` }), phone);
  await pollPicture(api, pics.projectId, pic.id, (p) => layerOf(p, layer.id).visible === true, 'shown on the server');

  // Lock: the server, the flip buttons and the explanation agree; reload keeps it.
  await press(page.getByRole('button', { name: `Lock ${layer.label}` }), phone);
  await pollPicture(api, pics.projectId, pic.id, (p) => layerOf(p, layer.id).locked === true, 'locked on the server');
  await expect(page.getByRole('button', { name: 'Flip horizontally' })).toBeDisabled();
  await expect(page.getByText('Locked: unlock to move, resize or flip.')).toBeVisible();
  await reloadPicture(page);
  await expect(page.getByRole('button', { name: `Unlock ${layer.label}` }), 'the lock survives a reload').toBeVisible();
  await press(page.getByRole('button', { name: `Unlock ${layer.label}` }), phone);
  await pollPicture(api, pics.projectId, pic.id, (p) => !layerOf(p, layer.id).locked, 'unlocked on the server');
});

test('PE-083 a layer\'s name is readable and a 44 px target on a phone, with its buttons wrapped beneath', async ({ page, api, pics }, testInfo) => {
  runOn('pixel7', 'iphone14webkit');
  const { pic } = await openFresh(page, api, pics, `pe083p-${testInfo.project.name}`);
  const badge = await addBadgeLayer(api, pics.projectId, pic.id, pics);
  await reloadPicture(page);
  const name = list(page).getByRole('button', { name: `Select ${badge.label}` });
  const box = (await name.boundingBox())!;
  expect(box.width, `the name button is wide enough to read (${box.width} px)`).toBeGreaterThanOrEqual(80);
  expect(box.height).toBeGreaterThanOrEqual(43.5);
  await expect(name.getByText(badge.label), 'the name text is visible, not squeezed to nothing').toBeVisible();
  const row = (await list(page).getByRole('listitem').first().boundingBox())!;
  // On a phone the row keeps Hide and a "..." menu (Move and Delete live in that menu): both stay inside the row and are 44 px targets.
  const buttons = { Hide: page.getByRole('button', { name: `Hide ${badge.label}` }), 'row menu': page.getByTestId(`layer-menu-${badge.id}`) };
  for (const [label, locator] of Object.entries(buttons)) {
    const b = (await locator.boundingBox())!;
    expect(b.x + b.width, `${label} stays inside the row`).toBeLessThanOrEqual(row.x + row.width + 1);
    expect(Math.min(b.width, b.height)).toBeGreaterThanOrEqual(43.5);
  }
  await layerAction(page, badge, 'forward', true);
  for (const action of ['forward', 'back', 'delete'] as const) {
    const item = page.getByTestId(action === 'delete' ? `layer-menu-delete-${badge.id}` : `layer-menu-${action}-${badge.id}`);
    expect((await item.boundingBox())!.height, `the menu item ${action} is a 44 px target`).toBeGreaterThanOrEqual(43.5);
  }
  await page.keyboard.press('Escape');
  await openPanel(page, 'Layers'); // Escape may close the sheet under the menu too
  await name.tap();
  await expect(name).toHaveAttribute('aria-current', 'true');
});

test('PE-084 PE-085 PE-080 move a layer forward and back with the buttons (the touch route), and drag it on desktop', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe084-${testInfo.project.name}`);
  const badge = await addBadgeLayer(api, pics.projectId, pic.id, pics);
  await reloadPicture(page);
  const order = async () => (await getFull(api, pics.projectId, pic.id)).layers.sort((a, b) => a.zOrder - b.zOrder).map((l) => l.id);
  const original = pic.layers[0].id;
  expect(await order(), 'the badge starts in front').toEqual([original, badge.id]);

  const front = () => list(page).getByRole('listitem').first();
  await expect(front()).toContainText(badge.label);
  const forwardOpen = await layerAction(page, badge, 'forward', true);
  await expect(forwardOpen, 'the front layer cannot go further forward').toBeDisabled();
  if (phone) {
    await page.keyboard.press('Escape'); // closes the row menu (and, on some builds, the sheet under it)
    await openPanel(page, 'Layers');
  }
  await layerAction(page, badge, 'back');
  await expect.poll(order, { message: 'back moved the badge behind the photo on the server' }).toEqual([badge.id, original]);
  await expect(front()).not.toContainText(badge.label);
  await layerAction(page, badge, 'forward');
  await expect.poll(order, { message: 'forward put it in front again' }).toEqual([original, badge.id]);

  if (!phone) {
    // PE-080 desktop: drag the front row onto the back row.
    await front().dragTo(list(page).getByRole('listitem').last());
    await expect.poll(order, { message: 'drag and drop reordered the layers on the server' }).toEqual([badge.id, original]);
  }
});

test('PE-086 PE-087 deleting a layer asks first, removes it for good and clears undo; the original can go too', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe086-${testInfo.project.name}`);
  const badge = await addBadgeLayer(api, pics.projectId, pic.id, pics);
  await reloadPicture(page);

  await layerAction(page, badge, 'delete');
  const dialog = page.getByRole('dialog', { name: 'Delete this layer?' });
  await expect(dialog).toContainText('undo history is cleared');
  await press(dialog.getByRole('button', { name: 'Cancel' }), phone);
  await expect(dialog).toBeHidden();
  expect((await getFull(api, pics.projectId, pic.id)).layers.length, 'cancel keeps the layer').toBe(2);

  await press(page.getByRole('button', { name: 'Hide ' + pic.layers[0].label }), phone); // something to undo
  await expect(page.getByRole('button', { name: 'Undo', exact: true })).toBeEnabled();
  await layerAction(page, badge, 'delete');
  await press(page.getByRole('dialog', { name: 'Delete this layer?' }).getByRole('button', { name: 'Delete' }), phone);
  await expect(list(page).getByRole('button', { name: `Select ${badge.label}` })).toHaveCount(0);
  await pollPicture(api, pics.projectId, pic.id, (p) => p.layers.every((l) => l.id !== badge.id), 'the layer is gone on the server');
  await expect(page.getByRole('button', { name: 'Undo', exact: true }), 'deleting clears the undo history').toBeDisabled();

  // The original photo layer can be deleted as well; the picture still opens and lists no layers.
  await layerAction(page, pic.layers[0], 'delete');
  await press(page.getByRole('dialog', { name: 'Delete this layer?' }).getByRole('button', { name: 'Delete' }), phone);
  await pollPicture(api, pics.projectId, pic.id, (p) => p.layers.length === 0, 'the original layer is gone too');
  await openPicture(page, pics.projectId, pic.id);
  await openPanel(page, 'Layers');
  await expect(page.getByText(/no layers yet/i)).toBeVisible();
});

test('PE-089 PE-090 PE-091 PE-092 PE-093 PE-094 name, opacity, blend, role and flips are saved and survive a reload', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe089-${testInfo.project.name}`);
  const badge = await addBadgeLayer(api, pics.projectId, pic.id, pics);
  await reloadPicture(page);
  await press(list(page).getByRole('button', { name: `Select ${badge.label}` }), phone);

  const name = page.getByLabel('Name', { exact: true });
  await name.fill('Sticker');
  await name.press('Enter');
  await pollPicture(api, pics.projectId, pic.id, (p) => layerOf(p, badge.id).label === 'Sticker', 'renamed on the server');
  await expect(list(page).getByRole('button', { name: 'Select Sticker' })).toBeVisible();

  // Opacity: the slider takes keys (Home goes to 0, then PageUp style steps are not needed: End = 100, ArrowLeft x50).
  const slider = page.getByRole('slider', { name: 'Opacity' });
  await slider.focus();
  await slider.press('End');
  for (let i = 0; i < 50; i++) await slider.press('ArrowLeft');
  await pollPicture(api, pics.projectId, pic.id, (p) => Math.abs(layerOf(p, badge.id).opacity - 0.5) < 0.011, 'opacity 50% on the server');

  // Blend and role are Mantine selects.
  await page.getByRole('textbox', { name: 'Blend' }).or(page.getByRole('combobox', { name: 'Blend' })).first().click();
  await page.getByRole('option', { name: 'Multiply' }).click();
  await pollPicture(api, pics.projectId, pic.id, (p) => layerOf(p, badge.id).blendMode === 'multiply', 'blend multiply on the server');
  await page.getByRole('textbox', { name: 'Role' }).or(page.getByRole('combobox', { name: 'Role' })).first().click();
  await page.getByRole('option', { name: 'Subject' }).click();
  await pollPicture(api, pics.projectId, pic.id, (p) => layerOf(p, badge.id).role === 'subject', 'role subject on the server');

  await press(page.getByRole('button', { name: 'Flip horizontally' }), phone);
  await press(page.getByRole('button', { name: 'Flip vertically' }), phone);
  await pollPicture(api, pics.projectId, pic.id, (p) => layerOf(p, badge.id).transform.flipX === true && layerOf(p, badge.id).transform.flipY === true, 'both flips on the server');

  await savedBadge(page);
  await reloadPicture(page);
  await press(list(page).getByRole('button', { name: 'Select Sticker' }), phone);
  await expect(page.getByLabel('Name', { exact: true })).toHaveValue('Sticker');
  await expect(page.getByRole('button', { name: 'Flip horizontally' })).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('button', { name: 'Flip vertically' })).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByText('50%', { exact: true })).toBeVisible();
});

test('PE-095 PE-096 PE-097 an image from the project is added as a layer, fitted inside the canvas and selected', async ({ page, api, pics }, testInfo) => {
  const phone = isPhone(testInfo.project.name);
  const { pic } = await openFresh(page, api, pics, `pe095-${testInfo.project.name}`);
  const add = page.getByRole('button', { name: 'Add to the picture' });
  await expect(add).toBeDisabled();

  if (phone) {
    const group = page.getByRole('group', { name: 'Add an image from this project' });
    await expect(group.getByRole('button', { name: 'notes.txt' }), 'a text file is not offered').toHaveCount(0);
    await press(group.getByRole('button', { name: 'badge.png' }).first(), true);
    await expect(group.getByRole('button', { name: 'badge.png' }).first()).toHaveAttribute('aria-pressed', 'true');
  } else {
    const select = page.getByRole('combobox', { name: 'Add an image from this project' });
    await select.click();
    await expect(page.getByRole('option', { name: 'notes.txt' })).toHaveCount(0);
    await page.getByRole('option', { name: 'badge.png' }).first().click();
  }
  await press(add, phone);
  const after = await pollPicture(api, pics.projectId, pic.id, (p) => (p.layers.length === 2 ? p : null), 'the image became a layer');
  const placed = after!.layers.find((l) => l.id !== pic.layers[0].id)!;
  expect(placed.transform.scale, 'a 640 px image fits inside a 1920 px canvas').toBeLessThanOrEqual(1);
  await expect(list(page).getByRole('button', { name: `Select ${placed.label}` })).toHaveAttribute('aria-current', 'true');

  // One undo step hides it (undo never deletes).
  await press(page.getByRole('button', { name: 'Undo', exact: true }), phone);
  await pollPicture(api, pics.projectId, pic.id, (p) => layerOf(p, placed.id).visible === false, 'undo hid the added layer');
  void addTextLayer; void openTab; void createFull; void runOn;
});
