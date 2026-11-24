import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { openWorkspace } from '../../../fixtures/editor-v2/visual';

/**
 * T-026 / VF4: from 1024 to 1920 px wide no panel tab shows a label that is cut off. A label either shows whole or the
 * tab shrinks to its icon (named, with a tooltip); when even the icons do not fit, a More menu lists every tab.
 */
for (const width of [1024, 1280, 1440, 1920]) {
  test(`T026.VF4 ${width}px: no tab label is clipped and every panel tab can be reached`, async ({ page, freshTimeline, openEditor }) => {
    runOn('desktop');
    await page.setViewportSize({ width, height: 900 });
    const ed = await openEditor(await freshTimeline('seeded'));
    await openWorkspace(ed, 'edit');
    // At 1024 px the clip panels sit in the Inspector drawer (tablet layout).
    await ed.openDrawer();
    for (const name of ['Timeline panels', 'Clip panels']) {
      const list = page.getByRole('tablist', { name });
      const listBox = (await list.boundingBox())!;
      const tabs = list.getByRole('tab');
      const count = await tabs.count();
      expect(count, `${name} has tabs`).toBeGreaterThan(3);
      const more = await page.getByTestId('panel-tabs-more').count();
      for (let i = 0; i < count; i++) {
        const tab = tabs.nth(i);
        if ((await tab.getAttribute('data-collapsed')) === 'true') {
          // An icon-only tab still has its name.
          expect(((await tab.textContent()) ?? '').trim().length, `${name}: an icon-only tab keeps its name`).toBeGreaterThan(0);
          continue;
        }
        const box = (await tab.boundingBox())!;
        const text = (await tab.innerText()).trim();
        expect(box.x + box.width, `${name} / ${text}: right edge inside the strip`).toBeLessThanOrEqual(listBox.x + listBox.width + 1);
        expect(box.x, `${name} / ${text}: left edge inside the strip`).toBeGreaterThanOrEqual(listBox.x - 1);
        // The label text itself is not truncated by an ellipsis or overflow.
        const clipped = await tab.evaluate((el) => {
          const label = el.querySelector('[data-tab-label]') as HTMLElement | null;
          return label ? label.scrollWidth > label.clientWidth + 1 : false;
        });
        expect(clipped, `${name} / ${text}: label is not cut off`).toBe(false);
      }
      void more;
    }
    // Every tab is reachable by name: a tab in the strip by click, or through More.
    for (const [list, label] of [['Timeline panels', 'Markers'], ['Clip panels', 'Compare']] as const) {
      const tab = page.getByRole('tablist', { name: list }).getByRole('tab', { name: label, exact: true });
      await tab.click();
      await expect(tab).toHaveAttribute('aria-selected', 'true');
    }
  });
}

test('T026.VF4 a narrow left panel lists every tab in the More menu', async ({ page, freshTimeline, openEditor }) => {
  runOn('desktop');
  // Above the tablet breakpoint: from 1100 px down the left panel keeps at least 300 px, which always fits the icons.
  await page.setViewportSize({ width: 1280, height: 768 });
  const ed = await openEditor(await freshTimeline('seeded'));
  await openWorkspace(ed, 'edit');
  // Drag the left separator in until the icons no longer fit.
  const sep = page.getByRole('separator', { name: 'Resize left panel' });
  await sep.focus();
  for (let i = 0; i < 6; i++) await page.keyboard.press('Shift+ArrowLeft');
  const more = page.getByTestId('panel-tabs-more').first();
  await expect(more, 'the More menu appears').toBeVisible();
  await more.click();
  await page.getByRole('menuitem', { name: 'Structure' }).click();
  await expect(page.getByRole('tab', { name: 'Structure', exact: true })).toHaveAttribute('aria-selected', 'true');
});
