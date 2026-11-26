import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { lightApi, withTempProject } from '../../../fixtures/editor-v2/ws';
import type { Page } from '@playwright/test';
import { probe, expectReachable, press } from '../../../fixtures/editor-v2/popover-probe';

/**
 * QA blast radius of the app-wide Popover default in web/app/theme.ts (flip/shift padding { bottom: 96 }).
 * Outside the editor there is no bottom chrome, so the only acceptable effect is "a dropdown may open upward":
 * every option must stay inside the viewport, hit-test to itself and be pickable, on desktop and pixel7.
 */

test.beforeEach(async ({ page }) => {
  runOn('desktop', 'pixel7');
  // A fresh user gets the first-run onboarding tour, whose full-page layer sits over the header and navbar (by design) and
  // swallows every hit-test. These tests are about the dropdowns, so the tour is already skipped (same stub as the assistant suite).
  await page.route(/\/api\/v1\/onboarding(\?.*)?$/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ tour: { status: 'skipped', version: 1, updatedAt: null }, checklist: { dismissed: true, items: [] } }),
    }),
  );
});

test('POP-01 header Account menu: items reachable', async ({ page }) => {
  await page.goto('/app/dashboard');
  await press(page, page.getByRole('button', { name: /^Account menu/ }));
  expectReachable(await probe(page, page.getByRole('menu').getByRole('menuitem')), 'account menu');
  await page.keyboard.press('Escape');
  await expect(page.getByRole('menu')).toBeHidden();
});

test('POP-02 navbar workspace switcher: items reachable', async ({ page }) => {
  await page.goto('/app/dashboard');
  const switcher = page.getByRole('button', { name: 'Switch workspace' });
  if (test.info().project.name === 'pixel7') await press(page, page.getByRole('button', { name: 'Toggle navigation' }));
  await press(page, switcher);
  const items = page.getByRole('menu').getByRole('menuitem');
  expectReachable(await probe(page, items), 'workspace switcher');
  await page.keyboard.press('Escape');
});

test('POP-03 projects list sort Select: options reachable and pickable, also near the viewport bottom', async ({ page }) => {
  // The sort control only renders once a project exists.
  await withTempProject(lightApi(page), 'pop-blast-sort', async () => {
    await page.goto('/app/projects');
    const sort = page
      .getByRole('textbox', { name: 'Sort projects' })
      .or(page.getByRole('combobox', { name: 'Sort projects' }))
      .first();
    await expect(sort).toBeVisible();
    await press(page, sort);
    expectReachable(await probe(page, page.getByRole('listbox').getByRole('option')), 'sort select');
    await press(page, page.getByRole('option', { name: 'Name' }));
    await expect(sort).toHaveValue('Name');

    // Squeeze: the trigger sits 200 px above the viewport bottom (the dropdown fits below unpadded, not with the 96 px reserve).
    const box = (await sort.boundingBox())!;
    const size = page.viewportSize()!;
    await page.setViewportSize({
      width: size.width,
      height: Math.round(box.y + box.height + 200),
    });
    await press(page, sort);
    const p = await probe(page, page.getByRole('listbox').getByRole('option'));
    expectReachable(p, 'sort select near bottom');
    test.info().annotations.push({
      type: 'placement',
      description: p.rows[0].bottom <= box.y ? 'above' : 'below',
    });
    await press(page, page.getByRole('option', { name: 'Newest created' }));
    await expect(sort).toHaveValue('Newest created');
  });
});

test('POP-04 project page actions Menu: items reachable and pickable', async ({ page }) => {
  const api = lightApi(page);
  await withTempProject(api, 'pop-blast', async (project) => {
    await page.goto(`/app/projects/${project.id}`);
    await press(page, page.getByRole('button', { name: 'Project actions' }));
    expectReachable(await probe(page, page.getByRole('menu').getByRole('menuitem')), 'project actions');
    await press(page, page.getByRole('menuitem', { name: 'Edit name and description' }));
    await expect(page.getByRole('dialog')).toBeVisible();
    await page.keyboard.press('Escape');
  });
});

test('POP-05 workflow step config Select in the step settings dialog: options reachable and pickable', async ({ page }) => {
  const api = lightApi(page);
  await withTempProject(api, 'pop-blast-wf', async (project) => {
    await page.goto(`/app/projects/${project.id}/workflows/new`);
    await expect(page.locator('input[placeholder="My Workflow"]')).toBeVisible();
    // A press before hydration is dropped: retry until the step (phone) or the step-type dialog (desktop) is really there.
    await expect(async () => {
      await press(page, page.getByRole('button', { name: 'Add Step' }).first());
      await expect(
        test.info().project.name === 'pixel7' ? page.getByRole('combobox', { name: 'Step Type' }) : page.getByRole('dialog'),
      ).not.toHaveCount(0, { timeout: 2000 });
    }).toPass();
    if (test.info().project.name === 'pixel7') {
      // Phone builder: Add Step appends a list row whose Step Type is itself a Select.
      const stepType = page.getByRole('combobox', { name: 'Step Type' }).last();
      await press(page, stepType);
      expectReachable(await probe(page, page.getByRole('listbox').getByRole('option')), 'step type select');
      await press(page, page.getByRole('option', { name: /Grab a frame/ }).first());
      await expect(stepType).toHaveValue(/Grab a frame/);
    } else {
      await press(
        page,
        page
          .getByRole('dialog')
          .getByRole('button', { name: /Grab a frame/ })
          .first(),
      );
    }
    const field = page
      .getByRole('textbox', { name: 'Take the frame from' })
      .or(page.getByRole('combobox', { name: 'Take the frame from' }))
      .first();
    const open = page.getByRole('button', { name: /^Step 1, Grab a frame/ }).or(page.getByRole('button', { name: 'Open settings for step 1' })).first();
    // Wait for the step to render either the field itself or the button that opens its settings, then act (no racy isVisible).
    await expect(field.or(open)).toBeVisible();
    if (!(await field.isVisible())) await press(page, open);
    await expect(field).toBeVisible();
    await field.scrollIntoViewIfNeeded();
    await page.waitForTimeout(800); // the settings dialog is still animating in: a dropdown opened now closes with it
    await press(page, field);
    const opts = page.getByRole('listbox').getByRole('option');
    // The dropdown mounts a beat after the press (the dialog is still animating in): probe once it lists options.
    await expect(opts.first()).toBeVisible();
    expectReachable(await probe(page, opts), 'step config select');
    const last = (await opts.last().textContent())!.trim();
    await press(page, opts.last());
    await expect(field).toHaveValue(last);
  });
});

test('POP-06 admin calibration Provider Select: options reachable and pickable', async ({ page }) => {
  await page.goto('/app/admin/calibration');
  const provider = page
    .getByRole('textbox', { name: 'Provider' })
    .or(page.getByRole('combobox', { name: 'Provider' }))
    .first();
  await expect(provider).toBeVisible();
  await provider.scrollIntoViewIfNeeded();
  await press(page, provider);
  const opts = page.getByRole('listbox').getByRole('option');
  expectReachable(await probe(page, opts), 'calibration provider');
  const first = (await opts.first().textContent())!.trim();
  await press(page, opts.first());
  await expect(provider).toHaveValue(first);
});
