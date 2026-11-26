import { test, expect, openFresh, openPanel } from '../../../fixtures/editor-v2/picture-lane';
import { runOn } from '../../../fixtures/editor-v2/rows';

/** A dialog opened from the phone's picture sheet covers the sheet (EDITOR_MODAL_Z above the sheet's z 250). */
test.use({ serviceWorkers: 'block' });

test('MODAL-03 phone: the AI tool picker covers the open bottom sheet', async ({ page, api, pics }, testInfo) => {
  runOn('pixel7');
  await openFresh(page, api, pics, `modal03-${testInfo.project.name}`);
  await openPanel(page, 'AI');
  await page.getByRole('button', { name: /Choose an AI tool/ }).tap();
  const dialog = page.getByRole('dialog', { name: 'Choose an AI tool' });
  await expect(dialog).toBeVisible();
  const covered = await page.evaluate(() => {
    const sheet = document.querySelector('[data-testid="picture-sheet"]') as HTMLElement;
    const r = sheet.getBoundingClientRect();
    const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    return !!hit?.closest('[role="dialog"]');
  });
  expect(covered, 'at the sheet centre the dialog is what a finger meets').toBe(true);
});
