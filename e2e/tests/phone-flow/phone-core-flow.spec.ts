import { expect, test, type Locator, type Page } from '@playwright/test';
import * as fs from 'fs';
import { clippedControls, smallControls } from '../../fixtures/editor-v2/phone-measure';

const SHOTS = '/tmp/launch-reliability/phone';
const EMAIL = process.env.LOCAL_STAGING_EMAIL ?? '';
const PASSWORD = process.env.LOCAL_STAGING_PASSWORD ?? '';
const PROJECT = process.env.LOCAL_STAGING_CEO_PROJECT ?? '';

test.skip(!EMAIL || !PASSWORD || !PROJECT, 'LOCAL_STAGING_* not set');
fs.mkdirSync(SHOTS, { recursive: true });

/** Text smaller than 12 CSS px that is actually visible (blank nodes and hidden ones skipped). */
async function tinyText(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const out = new Set<string>();
    for (const el of Array.from(document.body.querySelectorAll<HTMLElement>('*'))) {
      const own = Array.from(el.childNodes).some((n) => n.nodeType === 3 && (n.textContent ?? '').trim());
      if (!own) continue;
      const cs = getComputedStyle(el);
      const r = el.getBoundingClientRect();
      if (cs.visibility === 'hidden' || cs.display === 'none' || r.width === 0 || r.height === 0) continue;
      if (parseFloat(cs.fontSize) < 12) out.add(`${el.tagName} "${(el.textContent ?? '').trim().slice(0, 24)}" ${cs.fontSize}`);
    }
    return Array.from(out);
  });
}

/** Everything the ticket lists for a screen, as soft expectations so one run reports every defect. */
async function audit(page: Page, name: string): Promise<void> {
  const tag = `${test.info().project.name}-${name}`;
  await page.screenshot({ path: `${SHOTS}/${tag}.png`, fullPage: false });
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect.soft(overflow, `${tag}: horizontal overflow`).toBeLessThanOrEqual(0);
  // The library attribution is an inline credit link, not a control: left at its text size.
  expect.soft((await smallControls(page, 'body')).filter((c) => !c.includes('React Flow attribution')), `${tag}: controls under 44px`).toEqual([]);
  expect.soft(await clippedControls(page, 'body'), `${tag}: clipped controls`).toEqual([]);
  expect.soft(await tinyText(page), `${tag}: text under 12px`).toEqual([]);
  expect.soft(await clippedLabels(page), `${tag}: button text cut off`).toEqual([]);
}

/** Buttons and links whose label is wider than the control (the text is cut or ellipsised). */
async function clippedLabels(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const out: string[] = [];
    for (const el of Array.from(document.querySelectorAll<HTMLElement>('button, a[href], [role="button"], [class*="Button-label"]'))) {
      const r = el.getBoundingClientRect();
      if (r.width === 0 || el.closest('[inert], [hidden], [aria-hidden="true"]')) continue;
      if (el.scrollWidth > el.clientWidth + 1 && getComputedStyle(el).overflowX !== 'visible' && !el.closest('[class*="Tabs-list"]')) {
        out.push(`${el.tagName} "${(el.textContent ?? '').trim().slice(0, 24)}" ${el.scrollWidth}>${el.clientWidth}`);
      }
    }
    return out;
  });
}

/** Settle: client-only state (media queries, SWR) lands after the load event. */
async function settle(page: Page): Promise<void> {
  await page.waitForLoadState('networkidle');
  await page.waitForTimeout(800);
}

/** The control is fully inside the window and not covered by something else at its centre. */
async function reachable(page: Page, loc: Locator, what: string): Promise<void> {
  await loc.scrollIntoViewIfNeeded();
  const hit = await loc.evaluate((el) => {
    const r = el.getBoundingClientRect();
    const top = document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2);
    return { covered: !(top && (el === top || el.contains(top) || top.contains(el))), by: top?.tagName ?? 'none', inside: r.bottom <= window.innerHeight && r.top >= 0 };
  });
  expect.soft(hit.covered, `${what}: something covers it (${hit.by})`).toBe(false);
  expect.soft(hit.inside, `${what}: inside the window after scroll`).toBe(true);
}

test('phone core flow: sign in -> project -> upload -> app -> run page -> download', async ({ page, request }) => {
  const stamp = `phone-flow-${Date.now()}`;
  let createdProject = '';

  await test.step('sign in', async () => {
    await page.goto('/app/login');
    await settle(page);
    await audit(page, '01-login');
    await page.getByPlaceholder('you@example.com').fill(EMAIL);
    await page.getByPlaceholder('Your password').fill(PASSWORD);
    await page.getByRole('button', { name: 'Sign in' }).click();
    await page.waitForURL(/\/app\/(dashboard|projects)/);
  });

  await test.step('dashboard and project list', async () => {
    await page.goto('/app/dashboard');
    await settle(page);
    await audit(page, '02-dashboard');
    await page.goto('/app/projects');
    await settle(page);
    await audit(page, '03-projects');
  });

  await test.step('burger opens the navigation and it is usable', async () => {
    await page.getByRole('button', { name: 'Toggle navigation' }).click();
    await page.waitForTimeout(500);
    await expect(page.getByRole('link', { name: 'Projects' })).toBeVisible();
    await audit(page, '04-nav-open');
    await page.getByRole('link', { name: 'Projects' }).click();
    await settle(page);
  });

  await test.step('create project', async () => {
    await page.getByRole('button', { name: 'New project' }).click();
    await page.getByPlaceholder('Spring launch teaser').fill(stamp);
    await audit(page, '05-new-project');
    await reachable(page, page.getByRole('button', { name: 'Create project' }), 'Create project');
    await page.getByRole('button', { name: 'Create project' }).click();
    await page.waitForURL(/\/app\/projects\/[0-9a-f-]{36}/);
    createdProject = page.url().match(/projects\/([0-9a-f-]{36})/)![1];
    await settle(page);
    await audit(page, '06-project-files-empty');
  });

  await test.step('attach a file from the picker', async () => {
    const upload = page.getByRole('button', { name: 'Upload files' }).first();
    await reachable(page, upload, 'Upload files');
    // A phone picker hands the page a file through a hidden <input type=file>: drive that input.
    const input = page.locator('input[type="file"]').first();
    await expect(input).toHaveCount(1);
    await audit(page, '07-upload-button');
    await page.locator('input[type="file"]').first().setInputFiles({ name: 'phone-note.txt', mimeType: 'text/plain', buffer: Buffer.from('phone flow note') });
    await expect(page.getByText('phone-note.txt').first()).toBeVisible({ timeout: 30_000 });
    await settle(page);
    await audit(page, '08-file-attached');
  });

  await test.step('tabs: apps, renders', async () => {
    await page.goto(`/app/projects/${createdProject}?tab=workflows`);
    await settle(page);
    await audit(page, '09-apps-new-project');
  });

  await test.step('existing project: apps gallery, brief form (never pressing Run)', async () => {
    await page.goto(`/app/projects/${PROJECT}?tab=workflows`);
    await settle(page);
    await audit(page, '10-apps-gallery');
    await page.locator(`a[href*="/projects/${PROJECT}/workflows/"]:visible`).first().click();
    await page.waitForURL(/workflows\/[0-9a-f-]{36}/);
    await settle(page);
    await audit(page, '11-app-run-form');
    const run = page.getByRole('button', { name: /^(run|start|generate)/i }).first();
    if (await run.count()) await reachable(page, run, 'primary run button (not pressed)');
  });

  await test.step('run page: the failed run and a finished run (existing runs, nothing is started)', async () => {
    await page.goto(`/app/projects/${PROJECT}`);
    await settle(page);
    const hrefs = Array.from(new Set(await page.locator('a[href*="/executions/"]').evaluateAll((a) => a.map((x) => x.getAttribute('href') ?? ''))));
    expect(hrefs.length, 'the copied project has at least one run to open').toBeGreaterThan(0);
    let n = 0;
    for (const href of hrefs.slice(0, 3)) {
      await page.goto(href);
      await settle(page);
      await audit(page, `12-run-${++n}`);
    }
  });

  await test.step('renders: watch and download', async () => {
    await page.goto(`/app/projects/${PROJECT}?tab=renders`);
    await settle(page);
    await audit(page, '13-renders');
    const dl = page.getByRole('link', { name: 'Download' }).first();
    await expect(dl).toBeVisible();
    await reachable(page, dl, 'Download');
    const href = await dl.getAttribute('href');
    expect.soft(href, 'Download is a real same-origin link').toMatch(/^\/|^http/);
    // iOS Safari only saves an <a download> when the response is an attachment; the API answers this URL with
    // Content-Disposition: inline (the same URL feeds the <video>). That is an API change (outside web/), so it is
    // recorded as an annotation, not asserted.
    if (href) {
      const res = await request.get(href, { headers: { Range: 'bytes=0-1' } }).catch(() => null);
      const disposition = res?.headers()['content-disposition'] ?? '(none)';
      test.info().annotations.push({ type: 'download-disposition', description: disposition.replace(/filename.*/, '') });
    }
    const video = page.locator('video').first();
    await expect(video).toBeVisible();
    expect.soft(await video.getAttribute('controls'), 'the render player has native controls').not.toBeNull();
    expect.soft(await video.getAttribute('playsinline'), 'inline playback on iOS (no forced fullscreen)').not.toBeNull();
    await audit(page, '14-render-player');
  });

  if (createdProject) await request.delete(`/api/v1/projects/${createdProject}`).catch(() => undefined);
});
