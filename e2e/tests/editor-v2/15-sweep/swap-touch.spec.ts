import { test, expect, openFresh, openTab } from '../../../fixtures/editor-v2/picture-lane';

/** T-012 design review 1: the Canvas tab's swap button is a 44 px target on a phone. */
test.use({ serviceWorkers: 'block' });

test('DR1 phone: the swap width and height button is at least 44 px', async ({ page, api, pics }, testInfo) => {
  test.skip(testInfo.project.name !== 'pixel7', 'phone-only measurement');
  await openFresh(page, api, pics, `sweep-${testInfo.project.name}`);
  await openTab(page, 'Canvas');
  const swap = page.getByRole('button', { name: 'Swap width and height' });
  await expect(swap).toBeVisible();
  const box = await swap.boundingBox();
  expect(box?.height ?? 0).toBeGreaterThanOrEqual(44);
  expect(box?.width ?? 0).toBeGreaterThanOrEqual(44);
});
