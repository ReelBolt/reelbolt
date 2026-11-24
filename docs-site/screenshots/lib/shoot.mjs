// One shot: a fresh browser context (light theme, frozen clock), the real page, the faked API.
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { cookieUser, NOW } from '../fixtures/data.mjs';
import { respond } from '../fixtures/respond.mjs';
import { chatScenario } from '../fixtures/chat.mjs';
import { VIEWPORTS } from './manifest.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const mediaDir = resolve(here, '../fixtures/media');
const VIOLET = '#6d28d9'; // --rb-accent-strong, the brand violet on a light surface
const FULL_PAGE_CAP = 2400;

// Quiet the page: no animation, no caret, no Next dev badge. The page itself is untouched.
const QUIET_CSS = `
  *, *::before, *::after { animation: none !important; transition: none !important; caret-color: transparent !important; scroll-behavior: auto !important; }
  nextjs-portal, [data-nextjs-toast], [data-nextjs-dev-tools-button], #__next-build-watcher, [data-next-badge-root] { display: none !important; }
`;

export const unmatched = new Set();

async function installApi(ctx, shot) {
  // Nothing leaves the machine: every request to a host other than this one is refused (the editor's Google Fonts
  // request is the one the app makes today; it also raced the first canvas draw, so text looked different run to run).
  // Registered first so the `/api` route below, registered later, wins for the app's own API.
  await ctx.route(/.*/, (route) => {
    const { hostname } = new URL(route.request().url());
    return hostname === '127.0.0.1' || hostname === 'localhost' ? route.fallback() : route.abort();
  });
  const held = new Set();
  await ctx.route('**/api/**', async (route) => {
    const req = route.request();
    const url = new URL(req.url());
    if (url.pathname.startsWith('/app/_next')) return route.continue(); // the dev server's own assets
    const result = respond(req.method(), url.pathname, url.search, { scenario: shot.scenario, body: req.postData() });
    if (!result) {
      unmatched.add(`${req.method()} ${url.pathname}${url.search}`);
      return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' });
    }
    if (result.sse) {
      if (held.has(url.pathname)) return; // a reconnect: keep it pending, i.e. a quiet open stream
      held.add(url.pathname);
      const body = result.sse.map((e) => `event: ${e.event}\ndata: ${JSON.stringify(e.data)}\n\n`).join('');
      return route.fulfill({ status: 200, headers: { 'content-type': 'text/event-stream', 'cache-control': 'no-store' }, body });
    }
    if (result.image) {
      return route.fulfill({ status: 200, headers: { 'content-type': 'image/png' }, body: readFileSync(resolve(mediaDir, result.image)) });
    }
    if (result.media !== undefined) {
      const buf = readFileSync(resolve(mediaDir, `clip-${result.media}.mp4`));
      const range = /bytes=(\d*)-(\d*)/.exec(req.headers()['range'] ?? '');
      if (range) {
        const start = range[1] ? Number(range[1]) : 0;
        const end = Math.min(range[2] ? Number(range[2]) : buf.length - 1, buf.length - 1);
        return route.fulfill({
          status: 206,
          headers: { 'content-type': 'video/mp4', 'accept-ranges': 'bytes', 'content-range': `bytes ${start}-${end}/${buf.length}` },
          body: buf.subarray(start, end + 1),
        });
      }
      return route.fulfill({ status: 200, headers: { 'content-type': 'video/mp4', 'accept-ranges': 'bytes' }, body: buf });
    }
    return route.fulfill({ status: result.status, contentType: 'application/json', body: JSON.stringify(result.json) });
  });
}

async function newContext(browser, base, shot) {
  const vp = VIEWPORTS[shot.viewport];
  const ctx = await browser.newContext({
    viewport: { ...vp.viewport, height: shot.height ?? vp.viewport.height }, deviceScaleFactor: vp.deviceScaleFactor, isMobile: vp.isMobile, hasTouch: vp.hasTouch,
    colorScheme: 'light', reducedMotion: 'reduce', locale: 'en-US', timezoneId: 'UTC',
  });
  // A signed-out page (sign-up) takes no session cookies.
  if (shot.auth !== false) {
    await ctx.addCookies([
      { name: 'reelbolt_user', value: encodeURIComponent(JSON.stringify(cookieUser)), url: base },
      { name: 'reelbolt_token', value: 'harness-fixture-token', url: base },
    ]);
  }
  await ctx.addInitScript(({ css, alpha }) => {
    const add = () => { const s = document.createElement('style'); s.textContent = css; (document.head ?? document.documentElement).appendChild(s); };
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', add); else add();
    if (!alpha) {
      // The alpha banner is dismissed per product version (`reelbolt.alpha-banner.dismissed:<version>`).
      const orig = Storage.prototype.getItem;
      Storage.prototype.getItem = function (k) { return String(k).startsWith('reelbolt.alpha-banner.dismissed:') ? '1' : orig.call(this, k); };
    }
  }, { css: QUIET_CSS, alpha: shot.alpha === true });
  // A preference the person set earlier on this device (e.g. the editor's preview quality), as the app stores it.
  if (shot.storage) {
    await ctx.addInitScript((entries) => { for (const [k, v] of Object.entries(entries)) localStorage.setItem(k, v); }, shot.storage);
  }
  if (shot.chat) await installChat(ctx, shot.chat);
  await installApi(ctx, shot);
  return ctx;
}

/**
 * The assistant's reply stream stays open for a pending approval card, which a fulfilled route cannot do,
 * so the page's own fetch answers the chat call with a stream the harness controls.
 */
async function installChat(ctx, name) {
  const { frames, hold } = chatScenario(name);
  await ctx.addInitScript(({ frames, hold }) => {
    const real = window.fetch.bind(window);
    window.fetch = (input, init) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
      const method = (init && init.method) || (typeof input !== 'string' && !(input instanceof URL) && input.method) || 'GET';
      if (/\/api\/v1\/assistant\/chat(\?|$)/.test(url) && method.toUpperCase() === 'POST') {
        const enc = new TextEncoder();
        const line = (o) => enc.encode(`data: ${JSON.stringify(o)}\n\n`);
        const body = new ReadableStream({
          start(c) {
            c.enqueue(line({ type: 'start', messageId: 'msg_1' }));
            for (const f of frames) c.enqueue(line(f));
            if (!hold) {
              c.enqueue(line({ type: 'finish', finishReason: 'stop', usage: { inputTokens: 0, outputTokens: 0 } }));
              c.enqueue(enc.encode('data: [DONE]\n\n'));
              c.close();
            }
          },
        });
        return Promise.resolve(new Response(body, { status: 200, headers: { 'content-type': 'text/event-stream', 'x-vercel-ai-ui-message-stream': 'v1' } }));
      }
      return real(input, init);
    };
  }, { frames, hold });
}

const first = (page, sel) => page.locator(sel).first();

async function settle(page) {
  await page.evaluate(() => document.fonts.ready);
  await page.waitForLoadState('networkidle', { timeout: 30_000 }).catch(() => {});
  await page.waitForFunction(() => !document.querySelector('.mantine-Skeleton-root, .mantine-Loader-root, [aria-busy="true"]'), null, { timeout: 10_000 }).catch(() => {});
  // Video thumbnails: the page seeks them once their metadata arrives, and which frame is on screen (and whether
  // the layout around it has moved) depends on timing. Wait for metadata, then park every video on the same
  // frame and make sure it has been painted, repeating while the page's own seek is still landing.
  await page.waitForFunction(() => [...document.querySelectorAll('video')].every((v) => v.readyState >= 1 || v.error), null, { timeout: 20_000 }).catch(() => {});
  for (let pass = 0; pass < 3; pass++) {
    await page.evaluate(() => Promise.all([...document.querySelectorAll('video')].filter((v) => v.readyState >= 1).map((v) => new Promise((done) => {
      v.pause();
      const at = Math.min(0.5, (v.duration || 1) / 2);
      const finish = () => { if (v.readyState >= 2 && !v.seeking) done(); else { v.addEventListener('canplay', () => done(), { once: true }); setTimeout(done, 3000); } };
      if (Math.abs(v.currentTime - at) < 0.01 && !v.seeking) return finish();
      v.addEventListener('seeked', finish, { once: true });
      setTimeout(done, 4000);
      v.currentTime = at;
    }))));
    await page.waitForTimeout(250);
  }
  await page.waitForTimeout(500);
}

async function runStep(page, step) {
  if ('clickAt' in step) await page.mouse.click(step.clickAt[0], step.clickAt[1]);
  else if ('click' in step) await first(page, step.click).click();
  else if ('fill' in step) await first(page, step.fill).fill(step.value);
  else if ('hover' in step) await first(page, step.hover).hover();
  else if ('press' in step) await (step.on ? first(page, step.on).press(step.press) : page.keyboard.press(step.press));
  else if ('scrollTo' in step) {
    // The element's top just under the fixed header.
    await first(page, step.scrollTo).evaluate((el) => { el.scrollIntoView({ block: 'start' }); window.scrollBy(0, -80); });
  } else if ('scroll' in step) {
    if (typeof step.scroll === 'string') await first(page, step.scroll).scrollIntoViewIfNeeded();
    else await page.evaluate((y) => window.scrollTo(0, y), step.scroll.y ?? 0);
  } else if ('waitFor' in step) await first(page, step.waitFor).waitFor({ timeout: 60_000 });
  else if ('wait' in step) await page.waitForTimeout(step.wait);
  await settle(page);
}

async function drawHighlight(page, target) {
  // A selector, or a plain rectangle {x,y,width,height} in viewport pixels (for things drawn on a canvas).
  const box = typeof target === 'string' ? await first(page, target).boundingBox() : target;
  if (!box) throw new Error(`highlight "${target}" is not visible`);
  await page.evaluate(({ b, color }) => {
    const el = document.createElement('div');
    el.setAttribute('data-shot-highlight', '');
    Object.assign(el.style, {
      position: 'fixed', left: `${b.x - 6}px`, top: `${b.y - 6}px`, width: `${b.width + 12}px`, height: `${b.height + 12}px`,
      border: `3px solid ${color}`, borderRadius: '12px', boxShadow: `0 0 0 6px ${color}26`, pointerEvents: 'none', zIndex: '2147483647', boxSizing: 'border-box',
    });
    document.body.appendChild(el);
  }, { b: box, color: VIOLET });
}

/** Takes screenshots until two in a row are byte-identical, so a transition still finishing cannot leak into the image. */
async function stable(page, options) {
  let previous = await page.screenshot(options);
  for (let i = 0; i < 6; i++) {
    await page.waitForTimeout(250);
    const next = await page.screenshot(options);
    if (next.equals(previous)) return next;
    previous = next;
  }
  return previous;
}

/** Returns the PNG buffer of one manifest shot. */
export async function shoot(browser, base, shot, log) {
  const ctx = await newContext(browser, base, shot);
  const page = await ctx.newPage();
  try {
    if (process.env.SCREENSHOT_VERBOSE) page.on('console', (m) => { if (['warning', 'error'].includes(m.type())) log(`  [console.${m.type()}] ${shot.id}: ${m.text().slice(0, 400)}`); });
    page.on('pageerror', (e) => log(`  [pageerror] ${shot.id}: ${e.message.split('\n')[0]}`));
    await page.clock.setFixedTime(NOW);
    await page.goto(`${base}${shot.route}`, { waitUntil: 'load', timeout: 120_000 });
    for (const sel of [].concat(shot.waitFor ?? [])) await first(page, sel).waitFor({ timeout: 90_000 });
    await settle(page);
    for (const step of shot.steps ?? []) await runStep(page, step);
    for (const sel of [].concat(shot.highlight ?? [])) await drawHighlight(page, sel);

    const clip = shot.clip;
    if (typeof clip === 'string') {
      const box = await first(page, clip).boundingBox();
      if (!box) throw new Error(`clip "${clip}" is not visible`);
      return await stable(page, { clip: box });
    }
    if (clip && typeof clip === 'object') return await stable(page, { clip });
    if (shot.fullPage) {
      // Grow the viewport to the page (capped) so fixed chrome spans the whole image.
      const h = await page.evaluate(() => Math.max(document.documentElement.scrollHeight, document.body.scrollHeight));
      const vp = { ...VIEWPORTS[shot.viewport].viewport, height: shot.height ?? VIEWPORTS[shot.viewport].viewport.height };
      await page.setViewportSize({ width: vp.width, height: Math.max(vp.height, Math.min(h, FULL_PAGE_CAP)) });
      await page.evaluate(() => window.scrollTo(0, 0));
      await page.waitForTimeout(400);
    }
    return await stable(page);
  } finally {
    await ctx.close();
  }
}
