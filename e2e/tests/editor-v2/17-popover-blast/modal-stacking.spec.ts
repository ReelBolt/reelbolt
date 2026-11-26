import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { probe, expectReachable } from '../../../fixtures/editor-v2/popover-probe';

/**
 * Dialogs opened from the editor sit between the phone bottom sheet (z 250) and Mantine's dropdowns (300) and toasts (400)
 * (EDITOR_MODAL_Z): a dialog covers the sheet, and its own Select options and a toast raised over it stay on top of it.
 */
test.use({ serviceWorkers: 'block' });

test('MODAL-01 the track Select inside Add title opens above the dialog and its options can be reached', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.openAddMenu();
  await ed.tap(ed.testId('add-title-options'));
  const dialog = ed.page.getByTestId('add-title-dialog');
  await expect(dialog).toBeVisible();
  await ed.page.getByRole('textbox', { name: 'Track' }).or(ed.page.getByRole('combobox', { name: 'Track' })).first().click();
  expectReachable(await probe(ed.page, ed.page.getByRole('listbox').getByRole('option')), 'the Add title track options');
});

test('MODAL-02 the dialog layer sits between the phone sheet (250) and Mantine dropdowns (300)', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.openAddMenu();
  await ed.tap(ed.testId('add-title-options'));
  await expect(ed.page.getByTestId('add-title-dialog')).toBeVisible();
  // The first ancestor of the dialog with a numeric z-index is the layer Mantine stacked it on.
  const z = await ed.page.getByTestId('add-title-dialog').evaluate((el) => {
    for (let n: Element | null = el; n; n = n.parentElement) {
      const v = Number(getComputedStyle(n).zIndex);
      if (Number.isFinite(v)) return v;
    }
    return Number.NaN;
  });
  expect(z, 'above the phone bottom sheet (250)').toBeGreaterThan(250);
  expect(z, 'below Mantine dropdowns and popovers (300), and so below toasts (400)').toBeLessThan(300);
});
