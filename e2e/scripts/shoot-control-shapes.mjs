#!/usr/bin/env node
// Screenshot the control-shape preview (proposal phase) for docs/design/control-shapes.md.
//
//   cd e2e && node scripts/shoot-control-shapes.mjs [baseUrl]
//
// The preview is the dev-only route `web/app/control-shapes/page.tsx` (404 in a production build),
// so this expects a dev server for it: `cd web && npx next dev -p 4711 --turbopack`.
// Writes PNGs straight into docs/design/control-shapes/, next to the document that references them.
// Playwright resolves from e2e/node_modules (this directory's own npm project).
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const here = path.dirname(fileURLToPath(import.meta.url));
const outDir = path.resolve(here, '../../docs/design/control-shapes');
const base = (process.argv[2] ?? 'http://localhost:4711').replace(/\/$/, '');
const url = `${base}/app/control-shapes`;

const variants = ['a', 'b', 'c'];
const schemes = ['light', 'dark'];

const browser = await chromium.launch();
await mkdir(outDir, { recursive: true });

// `proxy.ts` sends every non-public route to /login without the two session cookies. The preview is a
// design surface, not a product screen, so the script signs in as a local stub user: the cookies
// only have to parse (`lib/auth/user-cookie.ts`); nothing behind them is fetched.
const previewUser = { id: 'preview', email: 'preview@localhost', firstName: 'Preview', lastName: 'User', platformAdmin: true };
const cookies = [
  { name: 'reelbolt_user', value: encodeURIComponent(JSON.stringify(previewUser)), url: base },
  { name: 'reelbolt_token', value: 'preview', url: base, httpOnly: true },
];

let shot = 0;
async function capture(contextOptions, suffix) {
  const context = await browser.newContext(contextOptions);
  await context.addCookies(cookies);

  // Mantine follows prefers-color-scheme by default, so the context's colour scheme is the theme.
  // A FRESH PAGE per scheme: focus, hover and the value tooltip survive an emulateMedia() switch,
  // and a resting shot that carried another state is worse than no shot (design review, 2026-10-30).
  for (const scheme of schemes) {
    const page = await context.newPage();
    await page.emulateMedia({ colorScheme: scheme });
    await page.goto(url, { waitUntil: 'networkidle' });
    await page.waitForSelector('[data-control-variant="a"]', { timeout: 30_000 });
    await page.mouse.move(0, 0);
    await page.waitForTimeout(250); // let Mantine's scheme attribute and transitions settle
    if (suffix === 'desktop') {
      // Resting state first, and only then the states that alter the control.
      for (const v of variants) {
        const section = page.locator(`[data-control-variant="${v}"]`);
        await section.screenshot({ path: path.join(outDir, `${v}-${scheme}.png`) });
        shot += 1;
      }
      // The applied shape: no variant override, so this section is what every product slider and
      // switch inherits (global.css + theme.ts). Its dense row carries a size="xs" slider.
      await page
        .locator('[data-testid="applied-section"]')
        .screenshot({ path: path.join(outDir, `applied-${scheme}.png`) });
      shot += 1;
      // The active-thumb state: variant B's glow only exists while the thumb is hovered, dragged or
      // keyboard-focused, which a resting screenshot cannot show. The value tooltip is hidden for
      // this one shot: it belongs to Mantine's hover behaviour, not to the glow being shown, and it
      // otherwise floats over the row above.
      const sectionB = page.locator('[data-control-variant="b"]');
      const hideTooltip = await page.addStyleTag({
        content: '.mantine-Slider-label{display:none!important}',
      });
      await sectionB.locator('.mantine-Slider-thumb').first().hover();
      await page.waitForTimeout(250);
      await sectionB.screenshot({ path: path.join(outDir, `b-${scheme}-active.png`) });
      shot += 1;
      await hideTooltip.evaluate((el) => el.remove());

      // Tab (not .focus()) so the browser treats it as keyboard focus and paints :focus-visible,
      // and land on the 50 % thumb in both themes so the two focus shots show the same control.
      // Park the pointer first, so the ring is shown on its own.
      await page.mouse.move(0, 0);
      await page.evaluate(() => document.activeElement?.blur());
      for (let i = 0; i < 40; i += 1) {
        await page.keyboard.press('Tab');
        const onFifty = await page.evaluate(() => {
          const el = document.activeElement;
          return (
            el?.getAttribute('role') === 'slider' &&
            el.getAttribute('aria-valuenow') === '50' &&
            !!el.closest('[data-control-variant="b"]')
          );
        });
        if (onFifty) break;
      }
      await page.waitForTimeout(250);
      await sectionB.screenshot({ path: path.join(outDir, `b-${scheme}-focus.png`) });
      shot += 1;
    }
    await page.screenshot({ path: path.join(outDir, `${suffix}-${scheme}.png`), fullPage: true });
    shot += 1;
    if (suffix === 'phone') {
      // The applied section carries `.touch-targets`, so this shot proves the switch keeps the B
      // shape inside the launch-reliability 44 px containers on a phone (see the app rules).
      await page
        .locator('[data-testid="applied-section"]')
        .screenshot({ path: path.join(outDir, `applied-phone-${scheme}.png`) });
      shot += 1;
    }
    await page.close();
  }
  await context.close();
}

await capture({ viewport: { width: 1180, height: 900 }, deviceScaleFactor: 2 }, 'desktop');
// The phone surface must keep working: coarse pointer, narrow viewport, 44 px targets.
await capture({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true }, 'phone');

await browser.close();
console.log(`wrote ${shot} screenshots to ${outDir}`);
