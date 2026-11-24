import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { box, createPicture, inside, openPicture } from '../../../fixtures/editor-v2/picture';
import { knownFailure } from '../expected-failures';

/**
 * d6 N1 (TH-CANVAS, remediation-plan.md W0.8): the picture stage fits the window. The audit (d6): at
 * 1440x900 the stage's bottom edge sat at y=912 and the page scrolled (scrollHeight 949), the zoom bar at
 * the fold. Desktop: the whole editor fits 1440x900 with no page scroll. Phone (single column, the panel
 * is below by design): the stage and its zoom bar are fully on screen on first open, no sideways scroll.
 */
test('d6-N1 TH-CANVAS the picture stage and its zoom bar fit the window on first open', async ({ page, api, seed }, testInfo) => {
  runOn('desktop', 'pixel7');
  knownFailure('d6-N1');
  const desktop = testInfo.project.name === 'desktop';
  if (desktop) await page.setViewportSize({ width: 1440, height: 900 });
  const pic = await createPicture(api, seed.projectId, seed.files.still, `d6n1-${testInfo.project.name}-${Date.now()}`);
  const stage = await openPicture(page, seed.projectId, pic.id);
  await page.waitForTimeout(500); // the stage height is measured after the first layout

  const view = await page.evaluate(() => ({
    w: window.innerWidth,
    h: window.innerHeight,
    scrollH: document.documentElement.scrollHeight,
    scrollW: document.documentElement.scrollWidth,
    clientW: document.documentElement.clientWidth,
    scrollY: window.scrollY,
  }));
  const viewport = { x: 0, y: 0, width: view.w, height: view.h };
  const s = await box(stage, 'the stage');
  const zoomBar = await box(stage.locator('div:has(> button[aria-label="Fit the picture on screen"])').first(), 'the zoom bar');
  expect(view.scrollY, 'the editor opens at the top of the page').toBe(0);
  expect(inside(s, viewport), `the stage ${JSON.stringify(s)} is inside the ${view.w}x${view.h} window`).toBe(true);
  expect(inside(zoomBar, viewport), `the zoom bar ${JSON.stringify(zoomBar)} is inside the window`).toBe(true);
  expect(view.scrollW, 'no sideways scroll').toBeLessThanOrEqual(view.clientW);
  if (desktop) expect(view.scrollH, 'at 1440x900 the page does not scroll').toBeLessThanOrEqual(view.h);
});
