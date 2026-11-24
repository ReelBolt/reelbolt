import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { box, createPicture, inside, openPicture, overlaps } from '../../../fixtures/editor-v2/picture';
import { knownFailure } from '../expected-failures';

/**
 * m4 N2 (TH-LAYER-11 / T15, remediation-plan.md W0.8): at Fit, the selected full-frame layer's handles can
 * be reached: no corner handle under the zoom bar, the rotate handle inside the stage. The audit (m4,
 * iPhone 390x664): the bottom handles were drawn under the zoom controls and the rotate handle was clipped
 * by the stage's top edge. Checked on desktop and Pixel 7 from the drawn handles' positions.
 */
test('m4-N2 TH-LAYER-11 at Fit the full-frame layer handles sit inside the stage and clear of the zoom bar', async ({ page, api, seed }, testInfo) => {
  runOn('desktop', 'pixel7');
  knownFailure('m4-N2');
  const pic = await createPicture(api, seed.projectId, seed.files.still, `m4n2-${testInfo.project.name}-${Date.now()}`);
  const stage = await openPicture(page, seed.projectId, pic.id);
  const layer = pic.layers[0];

  const select = page.getByRole('button', { name: `Select ${layer.label}`, exact: true });
  await select.scrollIntoViewIfNeeded();
  await select.click();
  const fit = stage.getByRole('button', { name: 'Fit the picture on screen' });
  await fit.click();
  await stage.scrollIntoViewIfNeeded();

  const corners = stage.locator('svg rect[fill="#ffffff"]');
  const rotate = stage.locator('svg circle[fill="#101218"]');
  await expect(corners, 'the selected layer shows its four corner handles').toHaveCount(4);
  await expect(rotate, 'and its rotate handle').toHaveCount(1);

  const s = await box(stage, 'the stage');
  const zoomBar = await box(stage.locator('div:has(> button[aria-label="Fit the picture on screen"])').first(), 'the zoom bar');
  for (let i = 0; i < 4; i++) {
    const h = await box(corners.nth(i), `corner handle ${i}`);
    expect(inside(h, s), `corner handle ${i} ${JSON.stringify(h)} is inside the stage ${JSON.stringify(s)}`).toBe(true);
    expect(overlaps(h, zoomBar), `corner handle ${i} ${JSON.stringify(h)} is clear of the zoom bar ${JSON.stringify(zoomBar)}`).toBe(false);
  }
  const r = await box(rotate, 'the rotate handle');
  expect(inside(r, s), `the rotate handle ${JSON.stringify(r)} is not clipped by the stage ${JSON.stringify(s)}`).toBe(true);
});
