/**
 * Links into Plan & billing from elsewhere (F-07, F-08).
 *
 * F-07: "See plans and credits" (a BYO provider refused by the plan) lands on Plan & billing.
 * F-08: the assistant attach control's "See plans and storage" link is now a Next `Link`, so the
 * dashboard's `/app` basePath applies. That link renders only inside a PROJECT's assistant after a
 * storage refusal, and this stack cannot create a project (POST /api/v1/projects answers 500 —
 * `workflow_definitions` does not exist without the `e2e-engine` profile, which stays off here for
 * disk space). So the exact link is not reachable on this stack; what is checked instead is the
 * class of bug F-08 was: no in-app anchor on the pages this stack renders points at a same-origin
 * path outside `/app` (a raw `href="/settings/…"` would), and every such anchor resolves to a page.
 * Read-only on seeded users plus one refused (402) provider create, which leaves nothing behind.
 *
 *   cd e2e && npx playwright test tests/billing/links.spec.ts --project=desktop
 */
import { test, expect, type Page } from '@playwright/test';
import { loginAs } from '../../fixtures/stack';

test.use({ trace: 'retain-on-failure' });

test.beforeEach(async ({}, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'flows run on the desktop project');
});

test('F-07 "See plans and credits" (BYO provider refused by plan) opens Plan & billing', async ({ page }) => {
  await loginAs(page, 'trial');
  await page.goto('/app/org/inference-providers');
  await page.getByRole('button', { name: 'New provider' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('textbox', { name: 'Name', exact: true }).fill('E2E BYO');
  await dialog.getByRole('textbox', { name: 'Endpoint' }).fill('https://my-resource.openai.azure.com');
  await dialog.getByRole('combobox', { name: 'Deployment name' }).fill('gpt-4o-mini');
  await dialog.getByRole('textbox', { name: 'API Key' }).fill('sk-e2e-not-a-key');
  const resp = page.waitForResponse((r) => r.url().includes('/api/v1/org/inference-providers') && r.request().method() === 'POST');
  await dialog.getByRole('button', { name: 'Create', exact: true }).click();
  expect((await resp).status()).toBe(402);
  const link = dialog.getByRole('link', { name: 'See plans and credits' });
  await expect(link).toHaveAttribute('href', '/app/settings/billing');
  await link.click();
  await expect(page).toHaveURL(/\/app\/settings\/billing$/);
  await expect(page.getByRole('heading', { name: 'Plan and billing' })).toBeVisible();
  await expect(page.getByTestId('plans-card')).toBeVisible();
});

/**
 * The marketing site serves these root paths on purpose (the legal links are root-relative in
 * web/lib/auth/legal.ts:55-60). They are not in-app links, so the basePath check skips them; the
 * test checks instead that they resolve when the site is up.
 */
const SITE_SERVED_PREFIX = '/legal/';

/** Same-origin anchors in the page whose path is not under /app (what a raw anchor without basePath produces). */
async function outsideAppPaths(page: Page): Promise<string[]> {
  return page.evaluate(() =>
    [...document.querySelectorAll('a[href]')]
      .map((a) => new URL((a as HTMLAnchorElement).href, location.href))
      .filter((u) => u.origin === location.origin && !u.pathname.startsWith('/app') && !u.pathname.startsWith('/api/'))
      .map((u) => u.pathname));
}

/** The outside-/app anchors that are not served by the marketing site. */
async function anchorsOutsideApp(page: Page): Promise<string[]> {
  return (await outsideAppPaths(page)).filter((p) => !p.startsWith(SITE_SERVED_PREFIX));
}

/**
 * Is the marketing site served on this stack origin? Decided by probing, not by E2E_PROFILES: env.sh does
 * not export that variable, so a process-env check always reported "no site" (QA-SQF-07). With the e2e-site
 * profile GET / answers the site's HTML with 200; without it nginx answers 502/504.
 */
async function probeSiteUp(page: Page): Promise<{ up: boolean; detail: string }> {
  const r = await page.request.get('/', { maxRedirects: 0 });
  const type = r.headers()['content-type'] ?? '';
  return { up: r.status() === 200 && type.includes('text/html'), detail: `GET / -> ${r.status()} ${type}` };
}

test('F-08 class: in-app anchors on the billing pages and their neighbours keep the /app basePath', async ({ page }) => {
  test.setTimeout(60_000);
  const seen = new Set<string>();
  const siteServed = new Set<string>();
  // The signup form (signed out) carries the /legal/terms and /legal/privacy links; the billing pages no
  // longer do now that the pre-checkout marketing opt-in is gone.
  await page.context().clearCookies();
  await page.goto('/app/signup');
  await page.waitForLoadState('networkidle');
  for (const x of await outsideAppPaths(page)) if (x.startsWith(SITE_SERVED_PREFIX)) siteServed.add(x);
  for (const label of ['creator', 'member'] as const) {
    await loginAs(page, label);
    for (const p of ['/app/dashboard', '/app/settings/billing', '/app/settings/usage', '/app/org/inference-providers']) {
      await page.goto(p);
      await page.waitForLoadState('networkidle');
      const outside = await outsideAppPaths(page);
      expect(outside.filter((x) => !x.startsWith(SITE_SERVED_PREFIX)), `${label} ${p}: anchors that leave /app`).toEqual([]);
      for (const x of outside) if (x.startsWith(SITE_SERVED_PREFIX)) siteServed.add(x);
      for (const href of await page.evaluate(() => [...document.querySelectorAll('main a[href^="/app/"]')].map((a) => a.getAttribute('href')!))) seen.add(href.split('#')[0]);
    }
  }
  // Every in-app link those pages offer resolves to a page (not the 502 a missing basePath produced).
  for (const href of seen) {
    const r = await page.request.get(href);
    expect(r.status(), href).toBeLessThan(400);
  }
  // Site-served links (/legal/...) resolve when the marketing site is up; without the e2e-site profile there is nothing to ask.
  const site = await probeSiteUp(page);
  if (!site.up) {
    const why = `site-served links ${[...siteServed].join(', ')} not checked: marketing site not served on this origin (${site.detail})`;
    console.log(`SKIP: ${why}`);
    test.info().annotations.push({ type: 'skip', description: why });
  } else {
    console.log(`SITE-SERVED checking ${siteServed.size} link(s) (${site.detail}): ${[...siteServed].join(', ') || 'none found'}`);
    // With the site up, an empty set means the check asserted nothing; fail rather than pass vacuously.
    expect(siteServed.size, 'site-served /legal/ links rendered on the signup page').toBeGreaterThan(0);
    for (const path of siteServed) {
      const r = await page.request.get(path);
      console.log(`SITE-SERVED ${path} -> ${r.status()}`);
      expect(r.status(), `site-served link ${path}`).toBeLessThan(400);
    }
  }
});
