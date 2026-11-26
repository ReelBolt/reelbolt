import type { Locator } from '@playwright/test';
import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { createPicture, getPicture, openPanel, openPicture } from '../../../fixtures/editor-v2/picture';
import { knownFailure } from '../expected-failures';

/**
 * T8 (TH-DOC-10, remediation-plan.md W0.8): an edit followed within 0.7 s by an in-app navigation is kept.
 * The audit (static T8, d6 trace-doc3, m4): the autosave timer was cleared on unmount without a flush, so
 * hiding a layer and tapping the "Pictures" breadcrumb straight away silently dropped the edit.
 *
 * The saved layer alone does not prove the unmount flush: if leaving took longer than the autosave delay,
 * the ordinary debounced save would have kept the edit anyway. So the row also pins the request itself,
 * timed on the browser's clock: the layer's write goes out as a `keepalive` request (the leave path; the
 * debounced save is a plain one) and starts before FLUSH_DELAY_MS has passed since the hide tap, i.e.
 * before the debounce could have fired.
 */

/** web/components/editor/thumbnail/useEditorDocument.ts FLUSH_DELAY_MS (the autosave debounce). */
const FLUSH_DELAY_MS = 700;

/** Logs every PATCH the page sends (browser clock, keepalive flag), and the instants of armed taps. */
const PROBE = `(() => {
  const log = (window.__t8 = { taps: {}, writes: [] });
  const send = window.fetch;
  window.fetch = function (input, init) {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    const method = (init && init.method) || (typeof input === 'object' && 'method' in input ? input.method : 'GET');
    if (String(method).toUpperCase() === 'PATCH') log.writes.push({ at: performance.now(), url, keepalive: !!(init && init.keepalive) });
    return send.apply(this, arguments);
  };
})();`;

type ProbeLog = { taps: Record<string, number>; writes: Array<{ at: number; url: string; keepalive: boolean }> };

/** Records, on the browser's clock, the instant `target` receives its click (capture phase, before the app). */
async function armTap(target: Locator, name: string): Promise<void> {
  await target.evaluate((el, key) => {
    el.addEventListener('click', () => {
      const log = (window as unknown as { __t8: ProbeLog }).__t8;
      log.taps[key] ??= performance.now();
    }, { capture: true, once: true });
  }, name);
}

test('T8 TH-DOC-10 an edit followed by an in-app navigation within 0.7 s is kept', async ({ page, api, seed }, testInfo) => {
  runOn('desktop', 'pixel7');
  knownFailure('T8');
  await page.addInitScript(PROBE);
  const pic = await createPicture(api, seed.projectId, seed.files.still, `t8-${testInfo.project.name}-${Date.now()}`);
  const layer = pic.layers[0];
  expect(layer.visible ?? true, 'the photo layer starts visible').toBe(true);
  await openPicture(page, seed.projectId, pic.id);
  await openPanel(page, 'Layers');

  const hide = page.getByRole('button', { name: `Hide ${layer.label}`, exact: true });
  // On a phone the breadcrumb folds into one "Back to Pictures" link.
  const crumb = page.getByRole('link', { name: 'Pictures', exact: true }).or(page.getByRole('link', { name: 'Back to Pictures', exact: true })).first();
  await expect(hide).toBeVisible();
  await expect(crumb, 'the "Pictures" breadcrumb leads back in-app').toBeVisible();
  await armTap(hide, 'hide');
  await armTap(crumb, 'crumb');
  await hide.click();
  await crumb.click();
  await expect(page).not.toHaveURL(/\/editor\/thumbnails\//, { timeout: 15_000 });

  // The browser kept the same document (in-app navigation), so the probe's log is still there.
  const log = await page.evaluate(() => (window as unknown as { __t8: ProbeLog }).__t8);
  expect(log.taps.hide, 'the hide tap was timed').toBeDefined();
  expect(log.taps.crumb, 'the breadcrumb tap was timed').toBeDefined();
  expect(log.taps.crumb - log.taps.hide, 'the navigation started inside the autosave delay (browser clock, ms)').toBeLessThan(FLUSH_DELAY_MS);
  const layerWrites = log.writes.filter((w) => w.url.includes(`/layers/${layer.id}`) && w.at >= log.taps.hide);
  expect(layerWrites.length, 'leaving the editor sent the pending layer edit').toBeGreaterThan(0);
  const first = layerWrites[0];
  expect(first.keepalive, 'the layer edit went out through the leave flush (keepalive), not the debounced save').toBe(true);
  expect(first.at - log.taps.hide, 'the leave flush sent the edit before the autosave delay could fire (browser clock, ms)').toBeLessThan(FLUSH_DELAY_MS);

  await expect.poll(async () => {
    const now = await getPicture(api, seed.projectId, pic.id);
    return now.layers.find((l) => l.id === layer.id)?.visible ?? true;
  }, { message: 'the hidden layer is saved although the editor was left at once', timeout: 10_000 }).toBe(false);
});
