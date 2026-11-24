import { mkdirSync } from 'node:fs';
import { test, type Page } from '@playwright/test';
import { JOURNEY, block, setupJourney, type Failure } from './fixtures';

// Before/after screenshots. Run the same spec against the BASE build (ES_SHOTS=before) and the
// head build (ES_SHOTS=after); files are `<page>-<state>-<width>.png` under ES_SHOTS_DIR/<before|after>.
const which = process.env.ES_SHOTS === 'before' ? 'before' : 'after';
const root = process.env.ES_SHOTS_DIR ?? '/tmp/error-states-evidence';
const dir = `${root}/${which}`;
const WIDTHS = [390, 820, 1440];
const PAGES = ['dashboard', 'project', 'run', 'detail'];
const STATES: { state: string; mode: Failure }[] = [
  { state: '500', mode: 'json500' },
  { state: 'network', mode: 'network' },
  { state: 'slow', mode: 'slow' },
  { state: '404', mode: 'e404' },
];

test.skip(process.env.ES_SHOTS === undefined, 'set ES_SHOTS=before|after');

async function settle(page: Page, state: string) {
  // The slow notice shows ~10 s after hydration; the others settle once the request has failed.
  await page.waitForTimeout(state === 'slow' ? 12_000 : 2_500);
}

for (const name of PAGES) {
  const p = JOURNEY.find((j) => j.name === name)!;
  for (const { state, mode } of STATES) {
    for (const width of WIDTHS) {
      test(`${name}-${state}-${width}`, async ({ page, baseURL }) => {
        mkdirSync(dir, { recursive: true });
        await page.setViewportSize({ width, height: width < 500 ? 844 : 900 });
        await setupJourney(page, baseURL!);
        await block(page, p.target, mode);
        await page.goto(p.path);
        await settle(page, state);
        await page.screenshot({ path: `${dir}/${name}-${state}-${width}.png`, fullPage: false });
      });
    }
  }
}

for (const width of WIDTHS) {
  test(`dashboard-offline-${width}`, async ({ page, context, baseURL }) => {
    mkdirSync(dir, { recursive: true });
    await page.setViewportSize({ width, height: width < 500 ? 844 : 900 });
    await setupJourney(page, baseURL!);
    await page.goto('/app/dashboard');
    await page.waitForTimeout(2_500);
    await context.setOffline(true);
    await page.waitForTimeout(1_500);
    await page.screenshot({ path: `${dir}/dashboard-offline-${width}.png`, fullPage: false });
  });
}
