import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { box, createPicture, getPicture, inside, openPicture, overlaps, picturesUrl } from '../../../fixtures/editor-v2/picture';
import type { FullPicture } from '../../../fixtures/editor-v2/picture-lane';

/**
 * Wave 3 / T-010: the picture editor on a phone (RM.14: R13.6 shell, R13.7 layer rows and nudge/align, VF10 desktop
 * fit). Outcomes are read back from the SAVED picture through the API, never from the DOM alone. The phone projects
 * run the shell; the pinch uses real CDP touch on pixel7 only (iphone14webkit has synthetic pointers).
 */
test.use({ serviceWorkers: 'block' });

const MIN_TARGET = 44;
const phoneOnly = () => runOn('pixel7', 'iphone14webkit');

async function openLayers(page: import('@playwright/test').Page) {
  await page.getByTestId('picture-dock-layers').tap();
  await expect(page.getByTestId('picture-sheet')).toBeVisible();
}

test.describe('RM.14 picture phone shell', () => {
  test('RM.14 R13.6 the stage stays visible and reactive while a sheet is open', async ({ page, api, seed }, testInfo) => {
    phoneOnly();
    const pic = await createPicture(api, seed.projectId, seed.files.still, `shell-${testInfo.project.name}-${Date.now()}`);
    await openPicture(page, seed.projectId, pic.id);
    const stage = page.getByTestId('picture-stage');
    await expect(stage).toBeVisible();
    await openLayers(page);
    const s = await box(stage, 'the stage');
    const sheet = await box(page.getByTestId('picture-sheet'), 'the sheet');
    expect(s.height, 'the stage keeps room to see and aim at the picture').toBeGreaterThanOrEqual(150);
    expect(s.y + s.height, 'the stage ends above the sheet').toBeLessThanOrEqual(sheet.y + 1);
    // Reactive: selecting a layer from the canvas still works with the sheet open.
    const canvas = page.getByRole('img', { name: 'Your picture' });
    await canvas.tap();
    await expect(page.getByRole('button', { name: /^Select .+/ }).first()).toBeVisible();
  });

  test('RM.14 R13.6 the back gesture closes the sheet and stays in the editor', async ({ page, api, seed }, testInfo) => {
    phoneOnly();
    const pic = await createPicture(api, seed.projectId, seed.files.still, `back-${testInfo.project.name}-${Date.now()}`);
    await openPicture(page, seed.projectId, pic.id);
    await openLayers(page);
    await page.goBack();
    await expect(page.getByTestId('picture-sheet')).toBeHidden();
    expect(page.url()).toContain(`/editor/thumbnails/${pic.id}`);
  });

  test('RM.14 R13.7 every layer row action is at least 44 px and Delete is 8 px clear of the other actions', async ({ page, api, seed }, testInfo) => {
    phoneOnly();
    const pic = await createPicture(api, seed.projectId, seed.files.still, `rows-${testInfo.project.name}-${Date.now()}`);
    await openPicture(page, seed.projectId, pic.id);
    await openLayers(page);
    const row = page.getByTestId(`layer-row-${pic.layers[0].id}`);
    const buttons = row.getByRole('button');
    const count = await buttons.count();
    expect(count, 'eye, lock, name and the overflow menu').toBeGreaterThanOrEqual(4);
    for (let i = 0; i < count; i++) {
      const b = await box(buttons.nth(i), `row button ${i}`);
      expect(b.width, `row button ${i} width`).toBeGreaterThanOrEqual(MIN_TARGET - 0.5);
      expect(b.height, `row button ${i} height`).toBeGreaterThanOrEqual(MIN_TARGET - 0.5);
    }
    // No Delete or arrow in the row itself: they live in the menu, with a divider between them.
    await expect(row.getByRole('button', { name: /^Delete / })).toHaveCount(0);
    await page.getByTestId(`layer-menu-${pic.layers[0].id}`).tap();
    const del = await box(page.getByTestId(`layer-menu-delete-${pic.layers[0].id}`), 'Delete');
    const back = await box(page.getByTestId(`layer-menu-backmost-${pic.layers[0].id}`), 'Send to back');
    expect(del.y - (back.y + back.height), 'Delete is clear of the arrows').toBeGreaterThanOrEqual(8);
    expect(del.height).toBeGreaterThanOrEqual(MIN_TARGET - 0.5);
  });

  test('RM.14 R13.7 the overflow menu sends a layer to the back and the saved picture stores it', async ({ page, api, seed }, testInfo) => {
    phoneOnly();
    const pic = await createPicture(api, seed.projectId, seed.files.still, `menu-${testInfo.project.name}-${Date.now()}`);
    await openPicture(page, seed.projectId, pic.id);
    await page.getByTestId('picture-dock-text').tap();
    await page.getByRole('button', { name: 'Add text' }).tap();
    await expect.poll(async () => (await getPicture(api, seed.projectId, pic.id)).layers.length, { message: 'the text layer is saved' }).toBe(2);
    await page.getByTestId('picture-dock-layers').tap();
    const text = (await getPicture(api, seed.projectId, pic.id)).layers.find((l) => l.kind.toLowerCase() === 'text');
    expect(text).toBeTruthy();
    await page.getByTestId(`layer-menu-${text!.id}`).tap();
    await page.getByTestId(`layer-menu-backmost-${text!.id}`).tap();
    await expect.poll(async () => {
      const layers = (await getPicture(api, seed.projectId, pic.id)).layers;
      return layers.find((l) => l.id === text!.id)!.zOrder < layers.find((l) => l.id !== text!.id)!.zOrder;
    }, { message: 'the text layer is now behind the photo' }).toBe(true);
  });

  test('RM.14 R13.7 the nudge pad (1 px, then 10 px) and align are stored in the picture', async ({ page, api, seed }, testInfo) => {
    phoneOnly();
    const pic = await createPicture(api, seed.projectId, seed.files.still, `nudge-${testInfo.project.name}-${Date.now()}`);
    await openPicture(page, seed.projectId, pic.id);
    await openLayers(page);
    const id = pic.layers[0].id;
    await page.getByRole('button', { name: `Select ${pic.layers[0].label}`, exact: true }).tap();
    const read = async () => (await api.json<FullPicture>('GET', picturesUrl(seed.projectId, pic.id))).layers.find((l) => l.id === id)!.transform;
    const start = await read();
    await page.getByTestId('nudge-ArrowRight').scrollIntoViewIfNeeded();
    await page.getByTestId('nudge-ArrowRight').tap();
    await expect.poll(read, { message: 'one pixel to the right' }).toMatchObject({ x: start.x + 1 });
    await page.locator('label', { hasText: '10 px' }).first().tap();
    await page.getByTestId('nudge-ArrowDown').tap();
    await expect.poll(read, { message: 'ten pixels down' }).toMatchObject({ y: start.y + 10 });
    await page.getByTestId('align-left').tap();
    await expect.poll(async () => (await read()).x, { message: 'align left puts the photo back at the left edge' }).toBeLessThanOrEqual(start.x + 0.5);
  });

  test('RM.14 T7 a two-finger pinch on the stage with the Text tool never adds a text layer', async ({ page, api, seed }, testInfo) => {
    runOn('pixel7');
    const pic = await createPicture(api, seed.projectId, seed.files.still, `pinch-${testInfo.project.name}-${Date.now()}`);
    await openPicture(page, seed.projectId, pic.id);
    await page.getByRole('toolbar', { name: 'Tools' }).getByRole('button', { name: 'Text', exact: true }).tap();
    await page.getByTestId('picture-sheet-close').tap();
    const b = await box(page.getByRole('img', { name: 'Your picture' }), 'the picture');
    const cx = b.x + b.width / 2;
    const cy = b.y + b.height / 2;
    const writes: string[] = [];
    page.on('request', (r) => { if (/^(PUT|PATCH|POST)$/.test(r.method()) && r.url().includes(pic.id)) writes.push(`${r.method()} ${new URL(r.url()).pathname}`); });
    const cdp = await page.context().newCDPSession(page);
    const send = (type: 'touchStart' | 'touchMove' | 'touchEnd', pts: { id: number; x: number; y: number }[]) => cdp.send('Input.dispatchTouchEvent', { type, touchPoints: pts });
    await send('touchStart', [{ id: 1, x: cx - 20, y: cy }]);
    await page.waitForTimeout(20);
    await send('touchStart', [{ id: 1, x: cx - 20, y: cy }, { id: 2, x: cx + 20, y: cy }]);
    for (let i = 1; i <= 10; i++) {
      await send('touchMove', [{ id: 1, x: cx - 20 - i * 5, y: cy }, { id: 2, x: cx + 20 + i * 5, y: cy }]);
      await page.waitForTimeout(16);
    }
    await send('touchEnd', []);
    // The picture autosaves 700 ms after an edit (FLUSH_DELAY_MS), so wait longer than that before reading anything.
    await page.waitForTimeout(1200);
    expect(writes, 'no save request was sent after the pinch (an added layer would have been saved)').toEqual([]);
    expect((await getPicture(api, seed.projectId, pic.id)).layers, 'the pinch added no layer').toHaveLength(pic.layers.length);
  });
});

test.describe('RM.14 VF10 desktop fit', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test('RM.14 VF10 at 1440x900 the stage fits the window and the handles are clear of the zoom bar', async ({ page, api, seed }, testInfo) => {
    runOn('desktop');
    const pic = await createPicture(api, seed.projectId, seed.files.still, `vf10-${testInfo.project.name}-${Date.now()}`);
    const stage = await openPicture(page, seed.projectId, pic.id);
    await expect(page.getByTestId('picture-dock')).toHaveCount(0);
    await expect(page.getByTestId('picture-export')).toBeVisible();
    const frame = await box(page.getByTestId('picture-stage'), 'the stage');
    expect(frame.y + frame.height, 'the stage ends inside the 900 px window').toBeLessThanOrEqual(900);
    await page.getByRole('button', { name: `Select ${pic.layers[0].label}`, exact: true }).click();
    await stage.getByRole('button', { name: 'Fit the picture on screen' }).click();
    const zoomBar = await box(stage.locator('div:has(> button[aria-label="Fit the picture on screen"])').first(), 'the zoom bar');
    const corners = stage.locator('svg rect[fill="#ffffff"]');
    await expect(corners).toHaveCount(4);
    for (let i = 0; i < 4; i++) {
      const h = await box(corners.nth(i), `corner handle ${i}`);
      expect(inside(h, frame), `corner handle ${i} is inside the stage`).toBe(true);
      expect(overlaps(h, zoomBar), `corner handle ${i} is clear of the zoom bar`).toBe(false);
    }
  });
});
