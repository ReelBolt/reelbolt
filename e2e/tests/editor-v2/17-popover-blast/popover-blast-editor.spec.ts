import { test, expect, openFresh, addBadgeLayer, addTextLayer, openTab, isPhone, textMore } from '../../../fixtures/editor-v2/picture-lane';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { lightApi, withTempProject } from '../../../fixtures/editor-v2/ws';
import { probe, expectReachable, press } from '../../../fixtures/editor-v2/popover-probe';
import type { Locator, Page } from '@playwright/test';

/**
 * Dropdowns inside the editor (picture editor panels, the New timeline modal): every option inside the viewport,
 * hit-tests to itself, pickable. The editor reserves the 96 px bottom chrome strip (EditorPopoverTheme).
 */
test.use({ serviceWorkers: 'block' });
test.beforeEach(() => runOn('desktop', 'pixel7', 'iphone14webkit'));

async function pickFirstOtherOption(page: Page, field: Locator, what: string): Promise<void> {
  await field.scrollIntoViewIfNeeded();
  await press(page, field);
  const opts = page.getByRole('listbox').getByRole('option');
  expectReachable(await probe(page, opts), what);
  const before = await field.inputValue();
  const target = opts.filter({ hasNotText: before }).first();
  const label = (await target.textContent())!.trim();
  await press(page, target);
  await expect(field).toHaveValue(label);
}

const field = (page: Page, name: string): Locator =>
  page.getByRole('textbox', { name, exact: true }).or(page.getByRole('combobox', { name, exact: true })).first();

test('POP-07 picture editor layer Blend and Role dropdowns: reachable and the choice sticks', async ({ page, api, pics }, testInfo) => {
  const { pic } = await openFresh(page, api, pics, `pop07-${testInfo.project.name}`);
  const layer = await addBadgeLayer(api, pics.projectId, pic.id, pics);
  await page.reload();
  await openTab(page, 'Layers');
  await press(page, page.getByRole('button', { name: `Select ${layer.label}` }));
  await pickFirstOtherOption(page, field(page, 'Blend'), 'picture Blend');
  await pickFirstOtherOption(page, field(page, 'Role'), 'picture Role');
});

test('POP-08 picture editor Text tab Font and Weight dropdowns: reachable and pickable', async ({ page, api, pics }, testInfo) => {
  const { pic } = await openFresh(page, api, pics, `pop08-${testInfo.project.name}`);
  const text = await addTextLayer(api, pics.projectId, pic.id, 'Blast');
  await page.reload();
  await openTab(page, 'Layers');
  await press(page, page.getByRole('button', { name: `Select ${text.label}` }));
  await openTab(page, 'Text');
  await textMore(page); // the phone keeps Weight under the Text sheet's More
  await expect(field(page, 'Font')).toBeVisible();
  await pickFirstOtherOption(page, field(page, 'Font'), 'text Font');
  await pickFirstOtherOption(page, field(page, 'Weight'), 'text Weight');
});

test('POP-09 editor New timeline modal Selects: options reachable and pickable inside the dialog', async ({ page }, testInfo) => {
  const api = lightApi(page);
  await withTempProject(api, `pop09-${testInfo.project.name}`, async (project) => {
    await page.goto(`/app/editor?project=${project.id}`);
    await expect(page.getByTestId('timelines-empty')).toBeVisible({ timeout: 30_000 });
    await press(page, page.getByTestId('timeline-create').first());
    const dialog = page.getByRole('dialog', { name: 'New timeline' });
    await expect(dialog).toBeVisible();
    for (const name of ['Format', 'Frame rate']) {
      const f = dialog.getByRole('textbox', { name, exact: true }).or(dialog.getByRole('combobox', { name, exact: true })).first();
      if (!(await f.isVisible().catch(() => false))) continue;
      await pickFirstOtherOption(page, f, `new timeline ${name}`);
    }
  });
});

test('POP-10 New timeline modal Select squeezed near the viewport bottom: still reachable', async ({ page }, testInfo) => {
  const api = lightApi(page);
  await withTempProject(api, `pop10-${testInfo.project.name}`, async (project) => {
    await page.goto(`/app/editor?project=${project.id}`);
    await expect(page.getByTestId('timelines-empty')).toBeVisible({ timeout: 30_000 });
    await press(page, page.getByTestId('timeline-create').first());
    const dialog = page.getByRole('dialog', { name: 'New timeline' });
    await expect(dialog).toBeVisible();
    const f = dialog.getByRole('textbox', { name: 'Format', exact: true }).or(dialog.getByRole('combobox', { name: 'Format', exact: true })).first();
    await f.scrollIntoViewIfNeeded();
    const box = (await f.boundingBox())!;
    const size = page.viewportSize()!;
    await page.setViewportSize({ width: size.width, height: Math.round(Math.max(box.y + box.height + 140, 420)) });
    await pickFirstOtherOption(page, f, isPhone(testInfo.project.name) ? 'phone near bottom' : 'near bottom');
  });
});
