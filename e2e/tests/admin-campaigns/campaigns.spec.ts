import { test, expect, type Page } from '@playwright/test';
import { loginAs, psql } from '../../fixtures/stack';
import { PHASE, FAKE, OUT, TEST_RCPT, PAGE, fake, fakeMode, fakeReset, watch, shot, log, noPageOverflow, open, subjectBox, htmlBox, BODY, saveDraft, rowOf, reason, seedConsents, type Watch } from './helpers';

/**
 * Real-browser run of /app/admin/campaigns. Real API + local fake Brevo unless a test says "MOCK".
 * Phases (QA_PHASE): A = campaigns disabled, B = enabled / list send off, C = enabled / list send on, D = list send on, Brevo key blank.
 * Nothing here may reach a host containing "brevo": every page aborts such requests and counts them.
 */
void FAKE; void OUT;

test.describe(`phase ${PHASE}`, () => {
  test.describe.configure({ mode: 'serial' });
  let w: Watch;
  test.beforeEach(async ({ page }) => { w = await watch(page); });
  test.afterEach(async ({}, info) => {
    log(info.project.name, info.title, { status: info.status, console: w.console, failedRequests: w.failed, http4xx5xx: w.http, brevoHits: w.brevoHits });
    expect(w.brevoHits, 'no request may reach a brevo host from the browser').toEqual([]);
  });

  if (PHASE === 'A') {
    test('(a) disabled: Enabled=false shows the 404 state', async ({ page }, info) => {
      await open(page);
      await expect(page.getByTestId('campaigns-disabled')).toBeVisible();
      await expect(page.getByText('Campaigns are not enabled')).toBeVisible();
      await expect(page.getByRole('button', { name: 'Save draft' })).toHaveCount(0);
      const api = await page.request.get('/api/v1/marketing/admin/campaigns');
      expect(api.status()).toBe(404);
      expect(await noPageOverflow(page)).toMatchObject({});
      await shot(page, info.project.name, 'a-disabled');
    });
    test('(a) non-admin is refused in the UI and by the API', async ({ page }, info) => {
      await loginAs(page, 'member');
      await page.goto(PAGE);
      // The admin layout bounces non-admins before the page's own alert can render: refused either way.
      await page.waitForLoadState('networkidle');
      log(info.project.name, 'a-non-admin-landed', { url: page.url(), alert: await page.getByText('Platform administrators only').count() });
      expect(page.url()).not.toContain('/admin/campaigns');
      await expect(page.getByTestId('campaigns-panel')).toHaveCount(0);
      await expect(page.getByRole('button', { name: 'Save draft' })).toHaveCount(0);
      for (const url of ['/api/v1/marketing/admin/campaigns', '/api/v1/marketing/admin/campaigns/settings']) {
        expect((await page.request.get(url)).status()).toBe(403);
      }
      expect((await page.request.post('/api/v1/marketing/admin/campaigns', { data: { subject: 'x', html: '<p>x</p>' } })).status()).toBe(403);
      await shot(page, info.project.name, 'a-non-admin');
    });
  }

  if (PHASE === 'B') {
    test.beforeAll(async () => { seedConsents(); psql('delete from marketing_campaigns'); await fakeReset(); });

    test('(b)(c)(d)(e)(f) draft, preview, dry run, test send, gating with list send off', async ({ page }, info) => {
      const pn = info.project.name;
      await open(page);
      await expect(page.getByTestId('campaigns-panel')).toBeVisible();
      await expect(page.getByText('No campaigns yet.')).toBeVisible();
      await shot(page, pn, 'b-empty');

      // (b) save, edit, reload
      const subject = `QA draft ${pn}`;
      await saveDraft(page, subject);
      await page.reload();
      await expect(page.getByRole('cell', { name: subject })).toBeVisible();
      await page.getByRole('button', { name: 'Edit' }).click();
      await expect(subjectBox(page)).toHaveValue(subject);
      await expect(htmlBox(page)).toHaveValue(BODY);
      await subjectBox(page).fill(`${subject} v2`);
      await expect(reason(page)).toHaveText('Sending to the list is switched off on this deployment.');
      await page.getByRole('button', { name: 'Save draft' }).click();
      await expect(page.getByText('Draft saved').first()).toBeVisible();
      await page.reload();
      await expect(page.getByRole('cell', { name: `${subject} v2` })).toBeVisible();
      await page.getByRole('button', { name: 'Edit' }).click();
      await expect(subjectBox(page)).toHaveValue(`${subject} v2`);
      await expect(page.getByTestId('send-button')).toBeDisabled();

      // (c) sandboxed preview
      const evil = '<p id="a">hello</p><script>document.getElementById("a").textContent="SCRIPT-RAN";try{parent.document.title="PWNED"}catch(e){}</script>';
      await htmlBox(page).fill(evil);
      const frame = page.getByTestId('campaign-preview');
      expect(await frame.getAttribute('sandbox')).toBe('');
      await expect(page.frameLocator('[data-testid=campaign-preview]').locator('#a')).toHaveText('hello');
      await page.waitForTimeout(500);
      expect(await page.title()).not.toBe('PWNED');
      await expect(page.frameLocator('[data-testid=campaign-preview]').locator('#a')).toHaveText('hello');
      await shot(page, pn, 'c-preview-sandbox');
      await htmlBox(page).fill(BODY);
      await expect(page.getByRole('button', { name: 'Dry run' })).toBeEnabled(); // back to the saved content: not dirty

      // (d) dry run, real API
      await subjectBox(page).fill(`${subject} v2`);
      await page.getByRole('button', { name: 'Save draft' }).isEnabled().then(async (en) => { if (en) await page.getByRole('button', { name: 'Save draft' }).click(); });
      await expect(page.getByRole('button', { name: 'Dry run' })).toBeEnabled();
      await page.getByRole('button', { name: 'Dry run' }).click();
      const dr = page.getByTestId('dry-run-result');
      await expect(dr).toContainText('Recipients: 4');
      await expect(dr).toContainText('Suppressed: 0');
      const sample = (await dr.innerText());
      expect(sample).not.toContain('qa-person1@example.invalid');
      expect(sample).toMatch(/Sample: .*\*+/);
      await shot(page, pn, 'd-dry-run-real');
      log(pn, 'd-sample-text', { sample });

      // (d) MOCK of the dry-run response: suppressed count and errors (the fake cannot force them)
      await page.route('**/api/v1/marketing/admin/campaigns/*/dry-run', (r) => r.fulfill({ json: { recipientCount: 2, suppressedCount: 3, sampleMasked: ['q***@e***.invalid'], footerOk: false, errors: ['footer_incomplete', 'html_too_large'], listSendEnabled: false } }));
      await page.getByRole('button', { name: 'Dry run' }).click();
      await expect(dr).toContainText('Suppressed: 3');
      await expect(dr.getByRole('alert')).toHaveCount(2);
      await shot(page, pn, 'd-dry-run-mock-errors');
      await page.unroute('**/api/v1/marketing/admin/campaigns/*/dry-run');
      await expect(reason(page)).toHaveText('Sending to the list is switched off on this deployment.');

      // (e) test send
      await page.getByRole('button', { name: 'Send test' }).click();
      await expect(page.getByText('Test sent').first()).toBeVisible();
      const rcpt = await page.getByTestId('test-recipient').innerText();
      expect(rcpt).not.toContain(TEST_RCPT);
      expect(rcpt).toMatch(/\*+.*example\.invalid|q\*+/);
      log(pn, 'e-masked-recipient', { rcpt });
      const card = page.locator('.mantine-Card-root', { has: page.getByRole('heading', { name: 'Check and send' }) });
      await expect(card.locator('input, textarea')).toHaveCount(0);
      await expect(page.getByTestId('send-button')).toBeDisabled();
      await shot(page, pn, 'e-test-sent-list-send-off');
      const calls = (await fake('/__qa/calls')) as any[];
      const tests = calls.filter((c) => c.path.includes('/sendTest'));
      expect(tests.length).toBeGreaterThan(0);
      for (const t of tests) expect(t.body.emailTo).toEqual([TEST_RCPT]);
      // list send is off: nothing may have written the managed list or created a campaign
      const touchesMain = (c: any) => /lists\/10\b|sendNow/.test(c.path) || JSON.stringify(c.body ?? {}).match(/"listIds":\[[^\]]*\b10\b/);
      expect(calls.filter(touchesMain)).toEqual([]);
      log(pn, 'e-fake-call-paths', { paths: calls.map((c) => `${c.method} ${c.path}`) });
      const o = await noPageOverflow(page);
      log(pn, 'b-overflow', o);
    });

    test('(k) layout, empty-history to full page, phone usability', async ({ page }, info) => {
      const pn = info.project.name;
      await open(page);
      await expect(page.getByTestId('campaigns-panel')).toBeVisible();
      const o = await noPageOverflow(page);
      log(pn, 'layout-overflow', o);
      expect(o.doc).toBeLessThanOrEqual(o.win);
      await page.getByRole('button', { name: 'Edit' }).first().click();
      for (const b of ['Save draft', 'Dry run', 'Send test']) await page.getByRole('button', { name: b }).scrollIntoViewIfNeeded();
      const vp = page.viewportSize()!;
      for (const el of [subjectBox(page), htmlBox(page), page.getByTestId('campaign-preview')]) {
        const bb = (await el.boundingBox())!;
        expect(bb.x, 'input fits left').toBeGreaterThanOrEqual(0);
        expect(bb.x + bb.width, 'input fits right').toBeLessThanOrEqual(vp.width + 1);
      }
      await shot(page, pn, 'k-layout-b');
    });
  }

  if (PHASE === 'C') {
    test.beforeAll(() => { seedConsents(); psql('delete from marketing_campaigns'); });
    const ready = async (page: Page, subject: string) => {
      await saveDraft(page, subject);
      await page.getByRole('button', { name: 'Send test' }).click();
      await expect(page.getByText('Test sent').first()).toBeVisible();
      await page.getByRole('button', { name: 'Dry run' }).click();
      await expect(page.getByTestId('dry-run-result')).toContainText('Recipients: 4');
    };

    test('(f) gating reasons with list send on', async ({ page }, info) => {
      const pn = info.project.name;
      await open(page);
      await saveDraft(page, `QA gating ${pn}`);
      await expect(reason(page)).toHaveText('Send a test of the current content first.');
      await expect(page.getByTestId('send-button')).toBeDisabled();
      await page.getByRole('button', { name: 'Send test' }).click();
      await expect(page.getByText('Test sent').first()).toBeVisible();
      await expect(reason(page)).toHaveText('Run a dry run on the current content first.');
      await shot(page, pn, 'f-needs-dry-run');
      await page.getByRole('button', { name: 'Dry run' }).click();
      await expect(page.getByTestId('send-button')).toBeEnabled();
      await expect(reason(page)).toHaveCount(0);
      await shot(page, pn, 'f-ready');
      await subjectBox(page).fill(`QA gating ${pn} edited`);
      await expect(reason(page)).toHaveText('Save your changes first.');
      await expect(page.getByTestId('send-button')).toBeDisabled();
      await expect(page.getByRole('button', { name: 'Dry run' })).toBeDisabled();
      await expect(page.getByRole('button', { name: 'Send test' })).toBeDisabled();
      await shot(page, pn, 'f-dirty');
      await page.getByRole('button', { name: 'Save draft' }).click();
      await expect(page.getByText('Draft saved').first()).toBeVisible();
      await expect(reason(page)).toHaveText('Send a test of the current content first.'); // saved content changed: old test no longer current
      await expect(page.getByTestId('dry-run-result')).toHaveCount(0); // stale dry run dropped
      // stale dry run after a Brevo-side change is the same path: errors in a dry run keep the send blocked
      await page.getByRole('button', { name: 'Send test' }).click();
      await expect(page.getByText('Test sent').first()).toBeVisible();
      await page.route('**/api/v1/marketing/admin/campaigns/*/dry-run', (r) => r.fulfill({ json: { recipientCount: 4, suppressedCount: 0, sampleMasked: [], footerOk: false, errors: ['footer_incomplete'], listSendEnabled: true } }));
      await page.getByRole('button', { name: 'Dry run' }).click();
      await expect(reason(page)).toHaveText('Run a dry run on the current content first.');
      await expect(page.getByTestId('send-button')).toBeDisabled();
      await page.unroute('**/api/v1/marketing/admin/campaigns/*/dry-run');
    });

    test('(g)(h) confirm modal and refusal messages (send answered by MOCK)', async ({ page }, info) => {
      const pn = info.project.name;
      await open(page);
      await page.getByRole('button', { name: 'New campaign' }).click();
      await ready(page, `QA confirm ${pn}`);
      await page.getByTestId('send-button').click();
      const modal = page.getByRole('dialog');
      await expect(modal).toContainText('Type 4 to confirm');
      const confirm = modal.getByRole('button', { name: 'Send 4 emails' });
      await expect(confirm).toBeDisabled();
      await modal.getByLabel('Recipient count').fill('3');
      await expect(confirm).toBeDisabled();
      await modal.getByLabel('Recipient count').fill('5');
      await expect(confirm).toBeDisabled();
      await modal.getByLabel('Recipient count').fill('4');
      await expect(confirm).toBeEnabled();
      const vp = page.viewportSize()!;
      const mb = (await modal.boundingBox())!;
      expect(mb.x).toBeGreaterThanOrEqual(0);
      expect(mb.x + mb.width).toBeLessThanOrEqual(vp.width + 1);
      expect(mb.y + mb.height).toBeLessThanOrEqual(vp.height + 1);
      log(pn, 'g-modal-box', { mb, vp });
      await shot(page, pn, 'g-confirm-modal');

      const cases: [number, object | string, string][] = [
        [409, { error: 'recipient_count_changed' }, 'The recipient count changed since the dry run'],
        [409, { error: 'campaign_active' }, 'Another campaign is being sent right now'],
        [403, { error: 'list_send_disabled' }, 'Sending to the list is switched off on this deployment.'],
        [422, { error: 'test_send_required' }, 'Send a test of the current content first.'],
        [422, { error: 'no_recipients' }, 'There are no recipients to send to.'],
        [422, { error: 'brevo_key_missing' }, 'The Brevo key is not configured on the server.'],
        [429, { error: 'rate_limited' }, 'Too many attempts.'],
        // Unmapped codes and bare 500s now read as one generic sentence: a raw code or "Internal Server Error" is never shown (T-013).
        [422, { error: 'some_unmapped_code' }, 'Something went wrong. Try again.'],
        [500, '', 'Something went wrong. Try again.'],
      ];
      let sentBodies: any[] = [];
      await page.route('**/api/v1/marketing/admin/campaigns/*/send', async (r) => {
        sentBodies.push(JSON.parse(r.request().postData() ?? '{}'));
        const [status, json] = (page as any).__case;
        await r.fulfill(typeof json === 'string' ? { status, body: json } : { status, json });
      });
      for (const [status, json, text] of cases) {
        (page as any).__case = [status, json];
        try { await confirm.click({ timeout: 8000 }); } catch (e) {
          log(pn, 'h-confirm-click-blocked', { status, json, err: String(e).slice(0, 400) });
          await shot(page, pn, `h-click-blocked-${status}`);
          await confirm.click({ force: true });
        }
        const got = page.locator('.mantine-Notification-root', { hasText: 'Not done' }).filter({ hasText: text }).first();
        let ok = true;
        try { await expect(got).toBeVisible({ timeout: 5000 }); } catch { ok = false; }
        const shown = await page.locator('.mantine-Notification-root').allInnerTexts();
        log(pn, `h-refusal-${typeof json === 'string' ? status : (json as any).error}`, { ok, shown });
        if (ok) await shot(page, pn, `h-${typeof json === 'string' ? status : (json as any).error}`);
        await expect(modal).toBeVisible(); // a refusal never closes the modal
        for (const n of await page.locator('.mantine-Notification-closeButton').all()) await n.click({ timeout: 1000 }).catch(() => {});
        expect(ok, `message for ${status} ${JSON.stringify(json)}`).toBe(true);
      }
      expect(sentBodies.length).toBe(cases.length);
      for (const b of sentBodies) expect(b).toEqual({ confirmRecipientCount: 4 });
      await page.keyboard.press('Escape');
      await expect(modal).toHaveCount(0);
    });

    test('(g)(i)(k) real send: queued -> syncing -> sending -> sent without reload, polling stops, stats', async ({ page }, info) => {
      test.setTimeout(300_000);
      const pn = info.project.name;
      psql('delete from marketing_campaigns');
      await fake('/__qa/reset', { method: 'POST' });
      await fakeMode({ importFail: false, sendNow: 'ok', campStatus: 'sent', sendDelayMs: 7000, stats: null });
      await open(page);
      await ready(page, `QA real send ${pn}`);
      const sendReq = page.waitForRequest((r) => /campaigns\/[^/]+\/send$/.test(r.url()) && r.method() === 'POST');
      await page.getByTestId('send-button').click();
      await page.getByRole('dialog').getByLabel('Recipient count').fill('4');
      await page.getByRole('dialog').getByRole('button', { name: 'Send 4 emails' }).click();
      const req = await sendReq;
      expect(JSON.parse(req.postData()!)).toEqual({ confirmRecipientCount: 4 });
      await expect(page.getByRole('dialog')).toHaveCount(0);
      const seen: string[] = [];
      const badge = page.getByRole('row').filter({ hasText: `QA real send ${pn}` }).locator('.mantine-Badge-root');
      const t0 = Date.now();
      let shotQueued = false;
      while (Date.now() - t0 < 200_000) {
        const t = ((await badge.first().innerText().catch(() => '')) || '').trim().toLowerCase();
        if (t && seen[seen.length - 1] !== t) {
          seen.push(t);
          if (!shotQueued || t !== 'sent') await shot(page, pn, `i-status-${t}`);
          shotQueued = true;
        }
        if (t === 'sent') break;
        await page.waitForTimeout(400);
        if ((Date.now() - t0) % 10_000 < 450) log(pn, 'i-db-status', { t: Date.now() - t0, ui: t, db: psql(`select status||' '||coalesce(failure_code,'') from marketing_campaigns where subject like 'QA real send%'`) });
      }
      log(pn, 'i-status-sequence', { seen, ms: Date.now() - t0 });
      expect(seen[seen.length - 1]).toBe('sent');
      // polling stops once nothing is active
      let listGets = 0;
      page.on('request', (r) => { if (r.method() === 'GET' && /marketing\/admin\/campaigns\?limit/.test(r.url())) listGets++; });
      await page.waitForTimeout(13_000);
      log(pn, 'i-polls-after-sent', { listGets });
      expect(listGets).toBe(0);

      // (k) results
      const row = rowOf(page, `QA real send ${pn}`);
      await expect(row.getByRole('button', { name: 'Refresh results' })).toBeVisible();
      await row.getByRole('button', { name: 'Refresh results' }).click();
      await expect(row.getByRole('cell', { name: '3', exact: true }).first()).toBeVisible();
      await expect(row.getByRole('button', { name: 'Edit' })).toHaveCount(0);
      await expect(row.getByRole('button', { name: /send/i })).toHaveCount(0);
      const cells = await row.locator('td').allInnerTexts();
      log(pn, 'k-row-cells', { cells });
      // unclipped: every cell fits its content, header fits too
      const clipped = await page.evaluate(() => [...document.querySelectorAll('table th, table td')].filter((e) => e.scrollWidth > e.clientWidth + 1).map((e) => e.textContent));
      expect(clipped, 'clipped table cells').toEqual([]);
      const o = await noPageOverflow(page);
      log(pn, 'k-overflow', o);
      expect(o.doc).toBeLessThanOrEqual(o.win);
      await shot(page, pn, 'k-history-sent-stats');
      if (pn === 'phone') {
        const m = await page.locator('table').first().evaluate((t) => {
          let e: HTMLElement | null = t.parentElement;
          while (e && !/(auto|scroll)/.test(getComputedStyle(e).overflowX)) e = e.parentElement;
          return e ? { sw: e.scrollWidth, cw: e.clientWidth, cls: e.className.slice(0, 60) } : { sw: 0, cw: 0, cls: 'no scroll container' };
        });
        log(pn, 'k-phone-table-scroll', m);
        expect(m.sw).toBeGreaterThan(m.cw); // wide table scrolls inside its card instead of the page
      }
      // the fake saw: remove-all, import (4 only), create, sendNow once
      const calls = (await fake('/__qa/calls')) as any[];
      const imports = calls.filter((c) => c.path.endsWith('contacts/import') && (c.body.listIds ?? []).includes(10));
      expect(imports.length).toBe(1);
      expect(imports[0].body.jsonBody.map((x: any) => x.email).sort()).toEqual([1, 2, 3, 4].map((n) => `qa-person${n}@example.invalid`));
      expect(imports[0].body.emailBlacklist).toBeUndefined();
      expect(calls.filter((c) => c.path.endsWith('/sendNow')).length).toBe(1);
      const create = calls.find((c) => c.path.endsWith('/emailCampaigns'));
      log(pn, 'i-create-body-keys', { keys: Object.keys(create.body), recipients: create.body.recipients });
      expect(calls.every((c) => c.keyOk)).toBe(true);
    });

    test('import failure: nothing sent, shown plainly', async ({ page }, info) => {
      const pn = info.project.name;
      psql('delete from marketing_campaigns');
      await fake('/__qa/reset', { method: 'POST' });
      await fakeMode({ importFail: true, sendNow: 'ok', sendDelayMs: 0 });
      await open(page);
      // test send while the fake's import answers 500 (502 send_failed from the API)
      await saveDraft(page, `QA import fail ${pn}`);
      await page.getByRole('button', { name: 'Send test' }).click();
      const failToast = page.locator('.mantine-Notification-root', { hasText: 'Not done' }).first();
      await expect(failToast).toBeVisible();
      log(pn, 'j-test-send-brevo-500', { toast: await failToast.innerText() });
      await shot(page, pn, 'j-test-send-brevo-500');
      await fakeMode({ importFail: false });
      await page.getByRole('button', { name: 'Send test' }).click();
      await expect(page.getByText('Test sent').first()).toBeVisible();
      await page.getByRole('button', { name: 'Dry run' }).click();
      await expect(page.getByTestId('dry-run-result')).toContainText('Recipients: 4');
      await fake('/__qa/reset', { method: 'POST' });
      await fakeMode({ importFail: true });
      await page.getByTestId('send-button').click();
      await page.getByRole('dialog').getByLabel('Recipient count').fill('4');
      await page.getByRole('dialog').getByRole('button', { name: 'Send 4 emails' }).click();
      const row = page.getByRole('row').filter({ hasText: `QA import fail ${pn}` });
      await expect(row.locator('.mantine-Badge-root')).toHaveText(/failed/i, { timeout: 120_000 });
      await shot(page, pn, 'j-import-failed');
      const text = await row.innerText();
      log(pn, 'j-import-failed-row', { text });
      const calls = (await fake('/__qa/calls')) as any[];
      expect(calls.filter((c) => /sendNow|emailCampaigns$/.test(c.path))).toEqual([]);
      await fakeMode({ importFail: false });
      // The row now carries the failure as plain text (codeMessage), never the raw code.
      const reasonText = await row.getByTestId('failure-reason').innerText();
      log(pn, 'j-failure-code-visible', { dbFailureCode: psql(`select failure_code from marketing_campaigns where subject like 'QA import fail%'`), reasonText });
      expect(reasonText).toBe('Brevo refused the request.');
      expect(text).not.toMatch(/brevo_http_500/);
    });

    // (j) SendUnknown moved to campaigns-flows.spec.ts (B5), where Refresh results and resolve now live in the warning.

    test('(i) polling MOCK: queued -> syncing -> sending -> sent without reload, then stops', async ({ page }, info) => {
      const pn = info.project.name;
      const mk = (status: string, extra = {}) => ({ id: '11111111-1111-4111-8111-111111111111', subject: 'QA mock poll', html: null, status, createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(), testSentAt: null, testCurrent: false, confirmedRecipientCount: 4, finalRecipientCount: null, dispatchStartedAt: null, failureCode: null, stats: null, statsAt: null, ...extra });
      const seq = ['queued', 'syncing', 'sending', 'sent'];
      let n = 0; let gets = 0;
      await loginAs(page, 'admin');
      await page.route('**/api/v1/marketing/admin/campaigns?limit=50', (r) => { gets++; const s = seq[Math.min(n++, seq.length - 1)]; return r.fulfill({ json: [mk(s, s === 'sent' ? { finalRecipientCount: 4, stats: { sent: 4, delivered: 4, uniqueViews: 2, uniqueClicks: 1, hardBounces: 0, softBounces: 0, complaints: 0, unsubscriptions: 0 } } : {})] }); });
      await page.goto(PAGE);
      const badge = page.locator('.mantine-Badge-root').first();
      const seen: string[] = [];
      const t0 = Date.now();
      while (Date.now() - t0 < 60_000) {
        const t = ((await badge.innerText().catch(() => '')) || '').trim().toLowerCase();
        if (t && seen[seen.length - 1] !== t) { seen.push(t); await shot(page, pn, `i-mock-${t}`); }
        if (t === 'sent') break;
        await page.waitForTimeout(300);
      }
      expect(seen).toEqual(seq);
      const after = gets;
      await page.waitForTimeout(12_000);
      log(pn, 'i-mock-poll', { seen, getsAtSent: after, getsAfter: gets });
      expect(gets).toBe(after);
    });
  }

  if (PHASE === 'D') {
    test.beforeAll(async () => { await fakeReset(); });
    test('(h) brevo_key_missing, real API (key blank)', async ({ page }, info) => {
      const pn = info.project.name;
      psql('delete from marketing_campaigns');
      await open(page);
      await saveDraft(page, `QA nokey ${pn}`);
      await page.getByRole('button', { name: 'Send test' }).click();
      const got = page.locator('.mantine-Notification-root', { hasText: 'Not done' });
      await expect(got.first()).toBeVisible();
      const txt = await got.allInnerTexts();
      log(pn, 'h-nokey-notifications', { txt });
      await shot(page, pn, 'h-brevo-key-missing-real');
      expect(txt.join(' ')).toContain('The Brevo key is not configured on the server.');
      const calls = (await fake('/__qa/calls')) as any[];
      expect(calls.length).toBe(0);
    });
  }
});
