import { expect, test, type Page, type Route } from '@playwright/test';

/**
 * T-015 banner-turnstile. Route-stubbed: fabricated unverified session cookies, every /api call answered
 * here, and Cloudflare's Turnstile replaced by an in-page fake that "solves" itself.
 * Needs the web server started with TURNSTILE_SITE_KEY set (the no-key behaviour is a unit test).
 * Writes are stubbed by an in-page fetch wrapper because WebKit does not route POST through page.route.
 */
const BASE = (process.env.LOCAL_STAGING_URL ?? 'http://localhost:28080').replace(/\/+$/, '');

interface Call {
  method: string;
  path: string;
  body: Record<string, unknown>;
}
interface Seen {
  calls: () => Promise<Call[]>;
  renders: () => Promise<string[]>;
}

function json(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

async function stub(page: Page, resendStatus: number): Promise<Seen> {
  await page.addInitScript((status: number) => {
    const w = window as unknown as {
      __calls: Call[];
      __renders: string[];
      turnstile: unknown;
    };
    w.__calls = [];
    w.__renders = [];
    let n = 0;
    // Fake Turnstile API: each render yields a distinct token shortly after mounting.
    w.turnstile = {
      render: (_el: HTMLElement, o: { sitekey: string; callback: (t: string) => void }) => {
        n += 1;
        w.__renders.push(o.sitekey);
        const token = `tok-${n}`;
        setTimeout(() => o.callback(token), 50);
        return `w${n}`;
      },
      remove: () => {},
    };
    const real = window.fetch.bind(window);
    window.fetch = async (input, init) => {
      const method = (init?.method ?? (input instanceof Request ? input.method : 'GET')).toUpperCase();
      const url = new URL(typeof input === 'string' ? input : input instanceof Request ? input.url : String(input), location.href);
      if (method === 'GET' || !url.pathname.startsWith('/api/')) return real(input, init);
      let body: Record<string, unknown> = {};
      try {
        body = JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>;
      } catch {
        // leave empty
      }
      w.__calls.push({ method, path: url.pathname, body });
      const reply = (s: number, payload: unknown) =>
        new Response(JSON.stringify(payload), { status: s, headers: { 'Content-Type': 'application/json' } });
      if (method === 'POST' && url.pathname.endsWith('/auth/resend-verification')) {
        return status === 202 ? reply(202, { message: 'ok' }) : reply(status, { error: 'turnstile verification failed' });
      }
      return reply(404, { error: 'not stubbed' });
    };
  }, resendStatus);
  const user = {
    email: 'ada@example.com',
    isPlatformAdmin: false,
    mustChangePassword: false,
    emailVerified: false,
    org: '11111111-1111-1111-1111-111111111111',
    orgName: 'ada@example.com',
    orgKind: 'Personal',
    orgRole: 'Owner',
    displayName: 'Ada',
    termsVersion: '2026-10',
    privacyVersion: '2026-10',
  };
  const host = new URL(BASE).hostname;
  await page.context().addCookies([
    { name: 'reelbolt_user', value: encodeURIComponent(JSON.stringify(user)), domain: host, path: '/' },
    { name: 'reelbolt_token', value: 'stub', domain: host, path: '/' },
  ]);
  await page.route('**/api/**', async (route) => {
    const p = new URL(route.request().url()).pathname.replace(/^\/app/, '');
    return p.endsWith('s') ? json(route, []) : json(route, { error: 'not stubbed' }, 404);
  });
  return {
    calls: () => page.evaluate(() => (window as unknown as { __calls?: Call[] }).__calls ?? []),
    renders: () => page.evaluate(() => (window as unknown as { __renders?: string[] }).__renders ?? []),
  };
}

const resendCalls = async (seen: Seen) => (await seen.calls()).filter((c) => c.path.endsWith('/auth/resend-verification'));

test.describe('banner resend carries a Turnstile token', () => {
  test('Resend is gated on the challenge, sends turnstileToken, and re-arms with a fresh widget', async ({ page }) => {
    const seen = await stub(page, 202);
    await page.goto('/app/dashboard');
    await page.waitForLoadState('networkidle');
    const banner = page.getByRole('status').filter({ hasText: 'Confirm your email address' });
    await expect(banner).toBeVisible();
    const resend = banner.getByRole('button', { name: 'Resend the email' });
    await expect.poll(async () => (await seen.renders()).length).toBe(1);
    await expect(resend).toBeEnabled();
    await resend.click();
    await expect(banner.getByText('Sent. Check your inbox.')).toBeVisible();
    const first = await resendCalls(seen);
    expect(first).toHaveLength(1);
    expect(first[0].body).toEqual({ email: 'ada@example.com', turnstileToken: 'tok-1' });
    // A fresh widget was mounted, and the second send uses its token.
    await expect.poll(async () => (await seen.renders()).length).toBe(2);
    const again = banner.getByRole('button', { name: 'Send it again' });
    await expect(again).toBeEnabled();
    await again.click();
    await expect.poll(async () => (await resendCalls(seen)).length).toBe(2);
    expect((await resendCalls(seen))[1].body).toEqual({ email: 'ada@example.com', turnstileToken: 'tok-2' });
  });

  test('a refused challenge shows the failure message and re-arms', async ({ page }) => {
    const seen = await stub(page, 400);
    await page.goto('/app/dashboard');
    await page.waitForLoadState('networkidle');
    const banner = page.getByRole('status').filter({ hasText: 'Confirm your email address' });
    const resend = banner.getByRole('button', { name: 'Resend the email' });
    await expect(resend).toBeEnabled();
    await resend.click();
    await expect(banner.getByText('We could not send it just now')).toBeVisible();
    await expect.poll(async () => (await seen.renders()).length).toBe(2);
    await expect(resend).toBeEnabled();
  });
});
