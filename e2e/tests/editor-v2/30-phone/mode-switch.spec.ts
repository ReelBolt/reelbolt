import { test, expect } from '../../../fixtures/editor-v2/lane-a-test';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * A phone stuck on the old "full view" choice (localStorage `reelbolt:editor:full-view` = '1', what the owner's phone held)
 * used to load the tablet layout at phone width: the page overflowed sideways, the picture collapsed to 0x0 and nothing led
 * back. A phone now always gets the phone surface, "Open full view" is no longer offered there, and the stale value is cleared.
 */
test.use({ serviceWorkers: 'block' });
test.beforeEach(() => { runOn('pixel7', 'iphone14webkit'); });

const KEY = 'reelbolt:editor:full-view';

for (const scheme of ['dark', 'light'] as const) {
  test(`a phone stuck on full view returns to the phone view with no action, ${scheme}, and stays there after reload`, async ({ freshTimeline, openEditor }) => {
    const ed = await openEditor(await freshTimeline('seeded'));
    await ed.page.emulateMedia({ colorScheme: scheme });
    await expect(ed.testId('phone-header')).toBeVisible();
    await expect(ed.page.getByText(/Open full view/), 'full view is not offered on a phone').toHaveCount(0);

    // The stuck state: the stored choice from the old button.
    await ed.page.evaluate((k) => window.localStorage.setItem(k, '1'), KEY);
    await ed.reload();

    await expect(ed.testId('phone-header'), 'back on the phone surface without any tap').toBeVisible();
    await expect(ed.page.getByTestId('workspace-edit')).toHaveCount(0);
    expect(await ed.page.evaluate((k) => window.localStorage.getItem(k), KEY), 'the stored choice is cleared').toBeNull();

    for (const size of [{ width: 390, height: 844 }, { width: 844, height: 390 }]) {
      await ed.page.setViewportSize(size);
      await expect(ed.testId('phone-header')).toBeVisible();
      const over = await ed.page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
      expect(over, `no sideways scroll at ${size.width}x${size.height}`).toBeLessThanOrEqual(0);
      const viewer = await ed.testId('phone-viewer').boundingBox();
      expect(viewer && viewer.width > 100 && viewer.height > 50, 'the picture is visible').toBeTruthy();
      await expect(ed.testId('phone-dock')).toBeVisible();
    }

    await ed.reload();
    await expect(ed.testId('phone-header'), 'still the phone view after another reload').toBeVisible();
    expect(await ed.page.evaluate((k) => window.localStorage.getItem(k), KEY)).toBeNull();
  });
}
