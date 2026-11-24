import fs from 'node:fs';
import path from 'node:path';
import type { Worker as PwWorker } from '@playwright/test';
import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Prod regression (initiative lut-1d-support): the owner's Samsung Log-to-Linear 1D .cube (LUT_1D_SIZE 4096, CRLF,
 * values -0.05..9.9994, uploaded as application/octet-stream) is picked as the viewing LUT in a PRODUCTION web build
 * and must turn on, not fall back to "could not open that LUT file".
 */
/**
 * A file of the same shape as the owner's (the vendor file itself is not committed): TITLE, LUT_1D_SIZE 4096, a blank
 * line, CRLF line ends, one log-to-linear curve on all three channels from about -0.05 up to about 9.9.
 */
function samsungShapedLut(): string {
  const dir = '/tmp/editor-remediation/media/c-fixtures';
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, 'samsung-shaped-1d.cube');
  const rows = ['TITLE "Samsung Log to Linear 1DLUT v1.0"', 'LUT_1D_SIZE 4096', ''];
  for (let i = 0; i < 4096; i += 1) {
    const v = (10 ** ((i / 4095) * 2.3) * 0.050003 - 0.1000028).toFixed(10);
    rows.push(`${v} ${v} ${v}`);
  }
  fs.writeFileSync(file, `${rows.join('\r\n')}\r\n`);
  return file;
}
const CEO_LUT = samsungShapedLut();

test("a 1D viewing LUT shaped like the owner's Samsung file turns on in the program monitor", async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const errors: string[] = [];
  const name = `samsung-1d-${Date.now()}.cube`;
  const lutId = await api.upload(seed.projectId, CEO_LUT, 'application/octet-stream', name);
  const ed = await openEditor(await freshTimeline('seeded'));
  ed.page.on('console', (m) => { if (m.type() === 'error' || m.type() === 'warning') errors.push(`${m.type()}: ${m.text()}`); });
  ed.page.on('pageerror', (e) => errors.push(`pageerror: ${e.message}`));
  ed.page.on('response', (r) => { if (r.url().includes(`/files/${lutId}/content`) || r.url().includes('cube-lut-worker') || /\.worker|worker/.test(r.url())) errors.push(`response ${r.status()} ${r.url()}`); });

  await ed.tap(ed.testId('viewing-lut-button'));
  await ed.tap(ed.testId(`viewing-lut-file-${lutId}`));

  // Well under the old 10 s parse timeout, which is how every pick used to end ("could not be read").
  await expect(ed.testId('viewing-lut-badge'), `the viewing LUT is on (log: ${errors.join(' | ')})`).toBeVisible({ timeout: 8_000 });
  await expect(ed.testId('viewing-lut-problem')).toHaveCount(0);
});

/** Which module a worker is running, from its live `onmessage` (minified source carries these markers). */
async function moduleOf(worker: PwWorker): Promise<'decode' | 'cube-lut' | 'other'> {
  const source = await worker.evaluate(() => String((self as unknown as { onmessage: unknown }).onmessage));
  if (source.includes('scaleToWidth')) return 'decode';
  if (source.includes('lut:')) return 'cube-lut';
  return 'other';
}

test('every decode worker of the program monitor boots the decode module (production bundle)', async ({ page, freshTimeline, openEditor }) => {
  runOn('desktop');
  const workers: PwWorker[] = [];
  page.on('worker', (w) => workers.push(w));
  const ed = await openEditor(await freshTimeline('seeded'));
  await expect.poll(() => workers.length, { message: 'the monitor starts its decode workers' }).toBeGreaterThan(0);
  await ed.page.waitForTimeout(1_500);
  for (const w of workers) expect(await moduleOf(w), w.url()).toBe('decode');
});

/**
 * Root cause, filed as issue #1006: in the Turbopack production build the cube-lut worker boots the DECODE worker's
 * module, so the parser never runs (the loader's ready handshake now parses on the main thread instead). The page
 * records every `{ ready: true }` a worker posts; this test is expected to fail until the cube-lut worker sends one.
 * The day #1006 lands it passes, Playwright reports the `test.fail` red, and the marker is removed.
 */
test('the viewing LUT worker boots its own parser module (production bundle)', async ({ page, freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.fail(true, 'Turbopack prod build boots the decode worker module in the cube-lut worker (issue #1006)');
  await page.addInitScript(() => {
    const NativeWorker = window.Worker;
    const w = window as unknown as { __lutReady: number };
    w.__lutReady = 0;
    window.Worker = class extends NativeWorker {
      constructor(url: string | URL, options?: WorkerOptions) {
        super(url, options);
        this.addEventListener('message', (event: MessageEvent) => {
          const data = event.data as { ready?: unknown } | null;
          if (data && typeof data === 'object' && data.ready === true) w.__lutReady += 1;
        });
      }
    } as typeof Worker;
  });
  const lutId = await api.upload(seed.projectId, CEO_LUT, 'application/octet-stream', `samsung-1d-${Date.now()}.cube`);
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.tap(ed.testId('viewing-lut-button'));
  await ed.tap(ed.testId(`viewing-lut-file-${lutId}`));
  await expect(ed.testId('viewing-lut-badge')).toBeVisible({ timeout: 8_000 });
  expect(await ed.page.evaluate(() => (window as unknown as { __lutReady: number }).__lutReady), 'the cube-lut worker said it was ready').toBeGreaterThan(0);
});
