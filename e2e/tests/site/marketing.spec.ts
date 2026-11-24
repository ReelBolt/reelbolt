/**
 * The refreshed ReelBolt marketing site on the isolated stack (saas-marketing-refresh, T-010a):
 *
 *   source e2e/.stack/<project>/env.sh
 *   npx playwright test tests/site/marketing.spec.ts --project=desktop
 *
 * Catalog facts, banned vocabulary, the header CTA switch, the promotions banner and pricing badge
 * (driven by a real automatic promotion created through the admin API), the checkout preview, the
 * footer newsletter, and the blog (comparison article, RSS, llms.txt, robots.txt).
 */
import { expect, test, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { execFileSync } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import { loadStack, loginAs, Throwaways } from '../../fixtures/stack';

// ------------------------------------------------------------------ helpers

interface CatalogPlan {
  id: string;
  name: string;
  entitlements?: { maxOutputHeight?: number | null };
}
interface Catalog {
  plans: CatalogPlan[];
  topUp: { packs: { credits: number; price: number }[] };
}

/** plans.catalog.json lives in the site package, next to this e2e package (repo root / site). */
function readCatalog(): Catalog {
  const file = path.resolve(__dirname, '../../../site/lib/pricing/plans.catalog.json');
  return JSON.parse(fs.readFileSync(file, 'utf8')) as Catalog;
}
const catalog = readCatalog();

function randomSuffix(length = 6): string {
  const alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
  let out = '';
  for (let i = 0; i < length; i++) out += alphabet[Math.floor(Math.random() * alphabet.length)];
  return out;
}

/** NEXT_PUBLIC_SIGNUP_ENABLED as the site container sees it, or null when docker cannot tell us. */
function signupFlagFromContainer(): string | null {
  const project = process.env.E2E_PROJECT;
  if (!project) return null;
  try {
    return execFileSync(
      'docker',
      ['compose', '-p', project, 'exec', '-T', 'site', 'printenv', 'NEXT_PUBLIC_SIGNUP_ENABLED'],
      { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], timeout: 30_000 },
    ).trim();
  } catch {
    return null;
  }
}

/** The plan card on /pricing: the smallest flex column holding the plan's H3. */
function planCard(page: Page, name: string) {
  return page
    .locator('div[class*="h-full"][class*="flex-col"]')
    .filter({ has: page.getByRole('heading', { level: 3, name, exact: true }) })
    .last();
}

/** The credits section of /pricing (the one with the top-up packs). */
function creditsSection(page: Page) {
  return page.locator('section').filter({ has: page.getByRole('heading', { level: 2, name: 'How credits work' }) });
}

/** Visible text of a page, minus the consent and legal links (where a processor name may appear). */
async function visibleTextOutsideLegal(page: Page): Promise<string> {
  return page.evaluate(() => {
    const clone = document.body.cloneNode(true) as HTMLElement;
    clone.querySelectorAll('script,style,noscript').forEach((n) => n.remove());
    clone.querySelectorAll('a[href]').forEach((a) => {
      if (/\/(legal|privacy|terms)|consent/i.test(a.getAttribute('href') ?? '')) a.remove();
    });
    return clone.innerText;
  });
}

const BANNED: { label: string; re: RegExp }[] = [
  { label: 'ffmpeg', re: /ffmpeg/i },
  { label: 'remotion', re: /remotion/i },
  { label: 'docker', re: /docker/i },
  { label: 'postgres', re: /postgres/i },
  { label: 'rabbitmq', re: /rabbitmq/i },
  { label: 'qdrant', re: /qdrant/i },
  { label: 'workflow engine', re: /workflow engine/i },
  { label: 'minimax', re: /minimax/i },
  { label: 'higgsfield', re: /higgsfield/i },
  { label: 'fal.ai', re: /fal\.ai/i },
  { label: 'fish audio', re: /fish audio/i },
  { label: 'comfyui', re: /comfyui/i },
  { label: 'typesafe', re: /typesafe/i },
];
// Paddle is a processor name: allowed only inside the consent and legal links.
const PADDLE = /paddle/i;

// ------------------------------------------------------------------ catalog facts

test.describe('catalog facts on /pricing', () => {
  test('plan names, 4K entitlements and top-up packs match plans.catalog.json', async ({ page }) => {
    await page.goto('/pricing', { waitUntil: 'networkidle' });

    for (const plan of catalog.plans) {
      await expect(page.getByRole('heading', { level: 3, name: plan.name, exact: true }), `plan ${plan.name}`).toBeVisible();
    }

    for (const plan of catalog.plans) {
      const height = plan.entitlements?.maxOutputHeight;
      if (plan.id !== 'pro' && plan.id !== 'team' && plan.id !== 'creator') continue;
      const card = planCard(page, plan.name);
      if (height === 2160) {
        await expect(card, `${plan.name} card shows 4K`).toContainText('4K');
      }
      if (height === 1080) {
        await expect(card, `${plan.name} card does not show 4K`).not.toContainText('4K');
      }
    }
    // Pro and Team are 2160 in the catalog, Creator is 1080: pin the catalog's own values.
    expect(catalog.plans.find((p) => p.id === 'pro')?.entitlements?.maxOutputHeight).toBe(2160);
    expect(catalog.plans.find((p) => p.id === 'team')?.entitlements?.maxOutputHeight).toBe(2160);
    await expect(planCard(page, 'Creator')).not.toContainText('4K');

    const credits = creditsSection(page);
    await expect(credits).toBeVisible();
    for (const pack of catalog.topUp.packs) {
      const formatted = pack.credits.toLocaleString('en-US');
      await expect(credits, `top-up pack ${formatted} credits`).toContainText(`${formatted} credits`);
    }
  });
});

// ------------------------------------------------------------------ banned words

test.describe('banned vocabulary', () => {
  test('marketing pages and every linked blog post are free of vendor and infrastructure names', async ({ page }) => {
    const { baseUrl } = loadStack();
    await page.goto('/blog', { waitUntil: 'networkidle' });
    const slugs = await page.$$eval('a[href^="/blog/"]', (as) =>
      [...new Set(as.map((a) => (a.getAttribute('href') ?? '').split(/[?#]/)[0]))].filter((h) => !h.startsWith('/blog/tag/')),
    );
    expect(slugs.length, 'blog posts linked from /blog').toBeGreaterThan(0);

    const paths = ['/', '/features', '/about', '/pricing', '/blog', ...slugs];
    const failures: string[] = [];
    for (const p of paths) {
      const res = await page.goto(p, { waitUntil: 'networkidle' });
      expect(res?.status(), `GET ${p}`).toBeLessThan(400);
      const html = (await page.content()).toLowerCase();
      const text = (await page.evaluate(() => document.body.innerText)).toLowerCase();
      for (const { label, re } of BANNED) {
        if (re.test(html) || re.test(text)) failures.push(`${p}: "${label}" present`);
      }
      const htmlNoLegal = (await page.content()).replace(/<a\b[^>]*href="[^"]*(?:\/legal|\/privacy|\/terms|consent)[^"]*"[^>]*>[\s\S]*?<\/a>/gi, '');
      if (PADDLE.test(htmlNoLegal) || PADDLE.test(await visibleTextOutsideLegal(page))) {
        failures.push(`${p}: "Paddle" outside the consent/legal links`);
      }
    }
    expect(failures, `banned words found (checked ${paths.length} pages: ${baseUrl})`).toEqual([]);
  });
});


/**
 * The home page streams its launch film from the production CDN (`cdn.reelbolt.ai`). On a machine with
 * no route to it the request never finishes, so `waitUntil: 'networkidle'` never settles on the desktop
 * layout (the phone layout does not load the film). Nothing here asserts on that asset, so every
 * context aborts requests to it.
 */
async function blockExternalMedia(context: BrowserContext): Promise<void> {
  await context.route(/^https:\/\/cdn\.reelbolt\.ai\//, (route) => route.abort());
}

async function newSiteContext(browser: Browser, options: Parameters<Browser['newContext']>[0]): Promise<BrowserContext> {
  const context = await browser.newContext(options);
  await blockExternalMedia(context);
  return context;
}

test.beforeEach(async ({ context }) => {
  await blockExternalMedia(context);
});

// ------------------------------------------------------------------ CTA switch

test.describe('header primary CTA', () => {
  test('follows NEXT_PUBLIC_SIGNUP_ENABLED', async ({ page }) => {
    await page.goto('/', { waitUntil: 'networkidle' });
    const flag = signupFlagFromContainer();
    const header = page.locator('header');
    // Below the md breakpoint the header's primary CTA lives inside the menu, behind its toggle.
    const openMenu = header.getByRole('button', { name: 'Open menu' });
    if (await openMenu.isVisible()) await openMenu.click();
    if (flag !== null) {
      expect(flag, 'site container NEXT_PUBLIC_SIGNUP_ENABLED').toBe('true');
    }
    const enabled = flag === null ? (await header.getByRole('link', { name: 'Start free', exact: true }).count()) > 0 : flag === 'true';
    if (enabled) {
      const cta = header.getByRole('link', { name: 'Start free', exact: true });
      await expect(cta).toHaveAttribute('href', /^\/app\/signup/);
      await expect(header.getByRole('link', { name: 'Talk to us', exact: true })).toHaveCount(0);
    } else {
      const cta = header.getByRole('link', { name: 'Talk to us', exact: true });
      await expect(cta).toHaveAttribute('href', '/contact');
      await expect(header.getByRole('link', { name: 'Start free', exact: true })).toHaveCount(0);
    }
  });
});

// ------------------------------------------------------------------ promotions end to end

test.describe('promotions on the site', () => {
  test.describe.configure({ mode: 'serial' });

  const users = new Throwaways('site');
  const suffix = randomSuffix();
  const headline = `E2E launch offer ${suffix}`;
  let promotionId: string | undefined;
  let adminCtx: BrowserContext | undefined;
  let proPreviewTotal = '';

  async function adminRequestContext(browser: Browser): Promise<BrowserContext> {
    if (!adminCtx) {
      adminCtx = await newSiteContext(browser, { baseURL: loadStack().baseUrl });
      await loginAs(adminCtx, 'admin');
    }
    return adminCtx;
  }

  test.afterAll(async () => {
    const { baseUrl } = loadStack();
    if (promotionId && adminCtx) {
      // End the promotion (idempotent), so no stray automatic discount outlives the run.
      await adminCtx.request.patch(`${baseUrl}/api/v1/billing/admin/promotions/${promotionId}`, { data: { status: 'archived' } });
    }
    await adminCtx?.close();
    await users.cleanup();
  });

  test('admin creates an automatic 20% Pro promotion ending in an hour', async ({ browser }) => {
    const { baseUrl } = loadStack();
    const ctx = await adminRequestContext(browser);
    const now = Date.now();
    const res = await ctx.request.post(`${baseUrl}/api/v1/billing/admin/promotions`, {
      data: {
        kind: 'planDiscount',
        name: `E2E Pro 20% ${suffix}`,
        publicHeadline: headline,
        discountType: 'percentage',
        discountAmount: '20',
        duration: 'once',
        planKeys: ['pro'],
        startsAt: new Date(now - 60_000).toISOString(),
        endsAt: new Date(now + 60 * 60_000).toISOString(),
      },
    });
    expect(res.status(), await res.text()).toBeLessThan(300);
    const body = await res.json();
    promotionId = body.id;
    expect(promotionId, 'created promotion id').toBeTruthy();

    const list = await ctx.request.get(`${baseUrl}/api/v1/billing/admin/promotions`);
    expect(list.status()).toBe(200);
    const items: any[] = (await list.json()).items ?? [];
    const found = items.find((p) => p.id === promotionId);
    expect(found, 'promotion in the admin list').toBeTruthy();
    expect(found.kind).toMatch(/planDiscount/i);
    expect(found.code ?? null).toBeNull();
    expect(found.status).toMatch(/active/i);
    expect(found.publicHeadline).toBe(headline);
  });

  test('banner and Pro badge show on / and /pricing; dismissal sticks on reload', async ({ browser }) => {
    const { baseUrl } = loadStack();
    const anon = await newSiteContext(browser, { baseURL: baseUrl });
    const page = await anon.newPage();
    try {
      await page.goto('/', { waitUntil: 'networkidle' });
      const region = page.getByRole('region', { name: 'Current offer' });
      await expect(region).toBeVisible();
      await expect(region).toContainText(headline);
      await expect(region.getByRole('link', { name: 'See pricing' })).toHaveAttribute('href', '/pricing');

      await page.goto('/pricing', { waitUntil: 'networkidle' });
      await expect(page.getByRole('region', { name: 'Current offer' })).toBeVisible();
      await expect(planCard(page, 'Pro')).toContainText(headline);
      await expect(planCard(page, 'Creator')).not.toContainText(headline);
      await expect(planCard(page, 'Team')).not.toContainText(headline);

      await page.getByRole('button', { name: 'Dismiss this offer' }).click();
      await expect(page.getByRole('region', { name: 'Current offer' })).toHaveCount(0);
      await page.reload({ waitUntil: 'networkidle' });
      await expect(page.getByRole('region', { name: 'Current offer' })).toHaveCount(0);
    } finally {
      await anon.close();
    }
  });

  test('a Pro-eligible owner sees the promotion in the checkout preview', async ({ browser }) => {
    const { baseUrl } = loadStack();
    const t = await users.create('trial');
    const ctx = await newSiteContext(browser, { baseURL: baseUrl });
    const owner = await ctx.newPage();
    try {
      await users.login(owner, t);
      const res = await owner.request.post(`${baseUrl}/api/v1/billing/checkout/preview`, { data: { plan: 'pro' } });
      expect(res.status(), await res.text()).toBe(200);
      const preview = await res.json();
      expect(preview.discount, 'preview discount').toBeTruthy();
      expect(preview.promotion?.headline, 'preview promotion headline').toBe(headline);
      proPreviewTotal = String(preview.total);
      expect(proPreviewTotal).toBeTruthy();

      await owner.goto('/app/settings/billing');
      await expect(owner.getByRole('heading', { name: 'Plan and billing' })).toBeVisible();
      const proCard = owner.getByTestId('plan-pro');
      await expect(proCard.getByTestId('promo-preview-total')).toContainText(proPreviewTotal);
    } finally {
      await ctx.close();
    }
  });

  test('ending the promotion removes the banner on a fresh context', async ({ browser }) => {
    const { baseUrl } = loadStack();
    const ctx = await adminRequestContext(browser);
    const res = await ctx.request.patch(`${baseUrl}/api/v1/billing/admin/promotions/${promotionId}`, { data: { status: 'archived' } });
    expect(res.status(), await res.text()).toBeLessThan(300);

    const fresh = await newSiteContext(browser, { baseURL: baseUrl });
    const page = await fresh.newPage();
    try {
      await page.goto('/', { waitUntil: 'networkidle' });
      await expect(page.getByRole('region', { name: 'Current offer' })).toHaveCount(0);
      await page.goto('/pricing', { waitUntil: 'networkidle' });
      await expect(planCard(page, 'Pro')).not.toContainText(headline);
    } finally {
      await fresh.close();
    }
  });
});

// ------------------------------------------------------------------ footer newsletter

test.describe('footer newsletter', () => {
  test('consent gates the submit; a valid address gets the check-your-inbox state', async ({ page }) => {
    const { baseUrl } = loadStack();
    let subscribeRequests = 0;
    page.on('request', (r) => {
      if (r.url().includes('/api/subscribe') && r.method() === 'POST') subscribeRequests++;
    });
    await page.goto('/', { waitUntil: 'networkidle' });
    const loadedAt = Date.now();

    const form = page.locator('footer form[aria-labelledby="footer-newsletter"]');
    const consent = page.locator('#footer-newsletter-consent');
    await expect(consent).not.toBeChecked();

    // Unticked: the submit is blocked with the consent message, and no request leaves the page.
    await page.locator('#footer-newsletter-email').fill(`e2e+${randomSuffix()}@example.test`);
    await form.getByRole('button', { name: 'Send confirmation link' }).click();
    await expect(form).toContainText(/Tick the box/);
    expect(subscribeRequests, 'POST /api/subscribe with consent unticked').toBe(0);

    // The bot timing check: more than 3 s after the page loaded.
    const wait = 3500 - (Date.now() - loadedAt);
    if (wait > 0) await page.waitForTimeout(wait);

    await consent.check();
    const address = `e2e+${randomSuffix()}@example.test`;
    await page.locator('#footer-newsletter-email').fill(address);
    await form.getByRole('button', { name: 'Send confirmation link' }).click();
    // After a successful submit the form is replaced by a role=status line in the same footer block.
    const sent = page.locator('footer [role="status"]').filter({ hasText: 'Check your inbox' });
    await expect(sent, 'check-your-inbox state').toBeVisible();
    const sentText = (await sent.innerText()).toLowerCase();
    expect(sentText).not.toContain('subscribed');
    expect((await page.locator('footer').innerText()).toLowerCase(), 'footer text').not.toContain('subscribed');
    expect(baseUrl).toBeTruthy();
  });
});

// ------------------------------------------------------------------ blog

test.describe('blog', () => {
  const ARTICLE = '/blog/reelbolt-vs-descript';

  test('/blog lists the posts', async ({ page }) => {
    await page.goto('/blog', { waitUntil: 'networkidle' });
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    const posts = page.locator('a[href^="/blog/"]:not([href^="/blog/tag/"])');
    expect(await posts.count()).toBeGreaterThan(0);
    await expect(page.locator('a[href="/blog/reelbolt-vs-descript"]').first()).toBeVisible();
  });

  test('comparison article: h1, scrollable table, nofollow sources, Article and BreadcrumbList JSON-LD', async ({ page }) => {
    await page.goto(ARTICLE, { waitUntil: 'networkidle' });
    await expect(page.getByRole('heading', { level: 1 })).toContainText('ReelBolt vs Descript');

    const table = page.locator('table').first();
    // Below `sm` a narrow table is replaced by a stacked definition list; the table stays in the DOM, hidden.
    if (await table.isVisible()) {
      await expect(table).toBeVisible();
    } else {
      await expect(page.locator('main dl').first(), 'stacked comparison list').toBeVisible();
    }
    // The table sits in a labelled, keyboard-focusable region. Narrow tables fit without a scroller
    // (and stack as a definition list below `sm`); wide ones scroll inside that region.
    const region = page.locator('[role="region"][tabindex="0"]', { has: table }).first();
    await expect(region, 'table sits in a labelled, focusable region').toHaveCount(1);
    expect(await region.getAttribute('aria-label')).toBeTruthy();

    const external = page.locator('main a[href^="http"]');
    const externalCount = await external.count();
    expect(externalCount, 'external source links').toBeGreaterThan(0);
    for (let i = 0; i < externalCount; i++) {
      await expect(external.nth(i), `external link ${i} rel`).toHaveAttribute('rel', /nofollow/);
    }

    const types = await page.$$eval('script[type="application/ld+json"]', (scripts) => {
      const out: string[] = [];
      const walk = (node: any) => {
        if (!node || typeof node !== 'object') return;
        if (Array.isArray(node)) return node.forEach(walk);
        if (node['@type']) out.push(...[node['@type']].flat());
        if (node['@graph']) walk(node['@graph']);
      };
      for (const s of scripts) {
        try {
          walk(JSON.parse(s.textContent ?? 'null'));
        } catch {
          /* ignore malformed */
        }
      }
      return out;
    });
    expect(types).toEqual(expect.arrayContaining(['Article', 'BreadcrumbList']));
  });

  test('no horizontal overflow at 360 px', async ({ browser }) => {
    const { baseUrl } = loadStack();
    const ctx = await newSiteContext(browser, { baseURL: baseUrl, viewport: { width: 360, height: 740 } });
    const page = await ctx.newPage();
    try {
      await page.goto(ARTICLE, { waitUntil: 'networkidle' });
      const sw = await page.evaluate(() => document.documentElement.scrollWidth);
      expect(sw, 'scrollWidth at 360 px').toBeLessThanOrEqual(360);
    } finally {
      await ctx.close();
    }
  });

  test('rss.xml parses and lists the article URLs', async ({ page }) => {
    const res = await page.request.get(`${loadStack().baseUrl}/blog/rss.xml`);
    expect(res.status()).toBe(200);
    const xml = await res.text();
    await page.goto('/blog', { waitUntil: 'load' });
    const parsed = await page.evaluate((text) => {
      const doc = new DOMParser().parseFromString(text, 'application/xml');
      return {
        error: doc.querySelector('parsererror')?.textContent ?? null,
        links: [...doc.querySelectorAll('item > link')].map((l) => l.textContent ?? ''),
      };
    }, xml);
    expect(parsed.error, 'RSS is well-formed XML').toBeNull();
    expect(parsed.links.length).toBeGreaterThan(0);
    expect(parsed.links.some((l) => l.endsWith(ARTICLE)), 'RSS lists the comparison article').toBe(true);
  });

  test('llms.txt answers 200; robots.txt names GPTBot and ClaudeBot', async ({ page }) => {
    const base = loadStack().baseUrl;
    const llms = await page.request.get(`${base}/llms.txt`);
    expect(llms.status()).toBe(200);
    const robots = await page.request.get(`${base}/robots.txt`);
    expect(robots.status()).toBe(200);
    const body = await robots.text();
    expect(body).toMatch(/User-Agent:\s*GPTBot/);
    expect(body).toMatch(/User-Agent:\s*ClaudeBot/);
  });
});
