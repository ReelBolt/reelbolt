/**
 * Marketing consent on the isolated stack (spec §E.5, flow 3):
 *
 *   source e2e/.stack/<project>/env.sh
 *   npx playwright test tests/billing/marketing-consent.spec.ts --project=desktop
 *
 * The seed verifies every user's email, so a settings opt-in is confirmed at once and the user is in
 * the admin export.
 */
import { expect, request as playwrightRequest, test, type APIRequestContext, type Page } from '@playwright/test';
import { loadStack, loginAs, user, type SeededLabel } from '../../fixtures/stack';

const LABEL: SeededLabel = 'creator';

async function consentOf(request: APIRequestContext): Promise<any> {
  const res = await request.get(`${loadStack().baseUrl}/api/v1/marketing/consent`);
  expect(res.status(), 'GET /api/v1/marketing/consent').toBe(200);
  return res.json();
}

/** Minimal RFC 4180 parse: quoted cells, doubled quotes. */
function parseCsv(text: string): string[][] {
  const rows: string[][] = [];
  let row: string[] = [];
  let cell = '';
  let quoted = false;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (quoted) {
      if (ch === '"' && text[i + 1] === '"') { cell += '"'; i++; }
      else if (ch === '"') quoted = false;
      else cell += ch;
    } else if (ch === '"') quoted = true;
    else if (ch === ',') { row.push(cell); cell = ''; }
    else if (ch === '\n' || ch === '\r') {
      if (ch === '\r' && text[i + 1] === '\n') i++;
      row.push(cell); rows.push(row); row = []; cell = '';
    } else cell += ch;
  }
  if (cell !== '' || row.length > 0) { row.push(cell); rows.push(row); }
  return rows.filter((r) => r.length > 1 || r[0] !== '');
}

async function settingsSwitch(page: Page) {
  await page.goto('/app/settings');
  const card = page.getByTestId('marketing-emails-card');
  await expect(card.getByTestId('marketing-emails-status')).toBeVisible();
  return card.getByTestId('marketing-emails-switch');
}

test.describe('marketing consent', () => {
  test('consent toggle persists; export contains the user; unsubscribe link withdraws', async ({ browser }) => {
    const { baseUrl } = loadStack();
    const email = user(LABEL).email;

    const userContext = await browser.newContext({ baseURL: baseUrl });
    const page = await userContext.newPage();
    await loginAs(page, LABEL);

    // --- Turn marketing emails on in settings; it survives a reload.
    let sw = await settingsSwitch(page);
    if (await sw.isChecked()) {
      // Left on by an earlier run: start from off.
      await sw.click({ force: true });
      await expect(sw).not.toBeChecked();
    }
    await sw.click({ force: true });
    await expect(sw).toBeChecked();
    await page.reload();
    sw = await settingsSwitch(page);
    await expect(sw).toBeChecked();
    const consent = await consentOf(page.request);
    expect(consent.status).toBe('optedIn');
    expect(consent.confirmedAt, 'confirmed at once: the seeded email is verified').toBeTruthy();

    // --- Admin export holds the user, with an unsubscribe link.
    const adminContext = await browser.newContext({ baseURL: baseUrl });
    const admin = await adminContext.newPage();
    await loginAs(admin, 'admin');
    const exported = await admin.request.get(`${baseUrl}/api/v1/marketing/admin/consents/export.csv`);
    expect(exported.status()).toBe(200);
    expect(exported.headers()['content-type']).toContain('text/csv');
    expect(exported.headers()['cache-control']).toContain('no-store');
    expect(exported.headers()['content-disposition']).toContain('attachment');
    const rows = parseCsv(await exported.text());
    expect(rows[0]).toEqual(['email', 'user_id', 'consented_at', 'confirmed_at', 'wording_version', 'source', 'unsubscribe_url']);
    const row = rows.find((r) => r[0].toLowerCase() === email.toLowerCase());
    expect(row, `${email} in the export`).toBeTruthy();
    expect(row![3], 'confirmed_at').not.toBe('');
    expect(row![5].toLowerCase()).toBe('settings');
    const exportedUrl = new URL(row![6]);
    expect(exportedUrl.pathname).toBe('/app/unsubscribe');
    const token = exportedUrl.searchParams.get('token');
    expect(token).toMatch(/^u1\./);
    expect(row![6]).not.toContain(email);
    await adminContext.close();

    // --- The link, opened as from a mail client (no session). Loading it changes nothing.
    const unsubscribeUrl = `${baseUrl}${exportedUrl.pathname}${exportedUrl.search}`;
    const anonymous = await browser.newContext({ baseURL: baseUrl });
    const linkPage = await anonymous.newPage();
    await linkPage.goto(unsubscribeUrl);
    const button = linkPage.getByTestId('unsubscribe-button');
    await expect(button).toBeVisible();
    await linkPage.waitForLoadState('networkidle');
    expect((await consentOf(page.request)).status, 'a GET of the unsubscribe page alone keeps the consent').toBe('optedIn');

    await button.click();
    await expect(linkPage.getByTestId('unsubscribe-done')).toBeVisible();
    await anonymous.close();

    expect((await consentOf(page.request)).status).toBe('withdrawn');
    sw = await settingsSwitch(page);
    await expect(sw).not.toBeChecked();

    // --- RFC 8058 one-click POST: ?token= and a urlencoded List-Unsubscribe=One-Click body, no cookie.
    const bare = await playwrightRequest.newContext();
    const oneClick = await bare.post(`${baseUrl}/api/v1/marketing/unsubscribe?token=${encodeURIComponent(token!)}`, {
      form: { 'List-Unsubscribe': 'One-Click' },
      maxRedirects: 0,
    });
    expect(oneClick.status()).toBe(200);
    expect(await oneClick.json()).toEqual({ status: 'unsubscribed' });
    const bad = await bare.post(`${baseUrl}/api/v1/marketing/unsubscribe?token=u1.k1.bogus.bogus`, {
      form: { 'List-Unsubscribe': 'One-Click' },
      maxRedirects: 0,
    });
    expect(bad.status()).toBe(400);
    await bare.dispose();

    expect((await consentOf(page.request)).status).toBe('withdrawn');
    await userContext.close();
  });
});
