import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { createPicture, getPicture, openPicture } from '../../../fixtures/editor-v2/picture';
import { knownFailure } from '../expected-failures';

/**
 * m4 N1 (TH-LAYER-14, remediation-plan.md W0.8): a second image can be added to a picture by touch.
 * The audit (m4, Pixel 7 / Android Chrome, 4 repros): tapping "Add an image from this project" made the
 * page jump to the top and the dropdown stayed `display:none`, so no option could be tapped. On the phone
 * the spec taps; on desktop it clicks the same control. The page must not jump, and the layer is added.
 */
test('m4-N1 TH-LAYER-14 an image from the project can be added to the picture by tap without the page jumping', async ({ page, api, seed }, testInfo) => {
  runOn('desktop', 'pixel7');
  knownFailure('m4-N1');
  const phone = testInfo.project.name !== 'desktop';
  const pic = await createPicture(api, seed.projectId, seed.files.still, `m4n1-${testInfo.project.name}-${Date.now()}`);
  await openPicture(page, seed.projectId, pic.id);

  const add = page.getByRole('button', { name: 'Add to the picture' });
  await add.scrollIntoViewIfNeeded();
  await expect(add).toBeDisabled();
  const scrollBefore = await page.evaluate(() => window.scrollY);

  if (phone) {
    const list = page.getByRole('group', { name: 'Add an image from this project' });
    const option = list.getByRole('button').first();
    await expect(option, 'the project images are listed as targets a finger can tap').toBeVisible();
    await option.tap();
    await expect(option).toHaveAttribute('aria-pressed', 'true');
  } else {
    const select = page.getByRole('combobox', { name: 'Add an image from this project' });
    await select.click();
    const option = page.getByRole('option').first();
    await expect(option, 'the dropdown opens with the project images').toBeVisible();
    await option.click();
  }
  const scrollAfter = await page.evaluate(() => window.scrollY);
  if (phone) expect(scrollBefore, 'on the phone the control sits below the stage, so the page is scrolled to it').toBeGreaterThan(50);
  if (scrollBefore > 50) expect(scrollAfter, `the page stays where it was (scrollY ${scrollBefore} -> ${scrollAfter})`).toBeGreaterThan(scrollBefore / 2);

  await expect(add).toBeEnabled();
  if (phone) await add.tap();
  else await add.click();
  await expect.poll(async () => (await getPicture(api, seed.projectId, pic.id)).layers.length, { message: 'the image is added as a new layer' })
    .toBe(pic.layers.length + 1);
  await expect(page.getByRole('list', { name: 'Layers, front first' }).getByRole('button', { name: /^Select / })).toHaveCount(pic.layers.length + 1);
});
