import { test, expect, type Page } from '@playwright/test';
import { psql } from '../../fixtures/stack';
import { PHASE, PAGE, API, BODY, MODE_DEFAULT, fake, fakeMode, fakeReset, fakeCalls, sendNowCount, watch, shot, log, noPageOverflow, open, subjectBox, htmlBox, reason, toasts, rowOf, saveDraft, seedConsents, apiReady, apiSend, sendVia, waitDb, dbRow, type Watch } from './helpers';

/**
 * Phase C (list send on): B1-B9 of the Brevo campaigns QA brief, all against the real API and the fake Brevo,
 * except where a title says MOCK. Rows are made through the API (fast, same code path as the page's own calls)
 * and the behaviour under test is driven in the browser.
 */
const FOOTER_OK = /^(?!.*_[a-z]).*$/s; // no raw snake_case code in a user-facing string
const RAW = /\b[a-z]+_[a-z_]+\b|Internal Server Error|\bHTTP \d{3}\b/;

if (PHASE === 'C') {
  test.describe('phase C flows', () => {
    let w: Watch;
    test.beforeEach(async ({ page }) => {
      w = await watch(page);
      psql('delete from marketing_campaigns');
      await fakeReset();
      await fakeMode(MODE_DEFAULT);
    });
    test.afterEach(async ({}, info) => {
      log(info.project.name, info.title, { status: info.status, console: w.console, failedRequests: w.failed, http4xx5xx: w.http, brevoHits: w.brevoHits });
      expect(w.brevoHits, 'no request may reach a brevo host from the browser').toEqual([]);
      await fakeMode(MODE_DEFAULT);
    });
    test.beforeAll(() => seedConsents());

    test('B1 Refresh results sends a request and shows the result', async ({ page }, info) => {
      const pn = info.project.name;
      await open(page);
      const { id } = await sendVia(page, `QA b1 ${pn}`, /^sent/);
      await page.reload();
      const row = rowOf(page, `QA b1 ${pn}`);
      const before = (await fakeCalls()).filter((c) => c.method === 'GET' && c.path.includes(`emailCampaigns/`)).length;
      await fakeMode({ stats: { sent: 4, delivered: 3, uniqueViews: 2, uniqueClicks: 1, hardBounces: 1, softBounces: 1, complaints: 0, unsubscriptions: 0 } });
      const req = page.waitForRequest((r) => r.url().endsWith(`${API}/${id}/refresh-results`) && r.method() === 'POST', { timeout: 5000 });
      const resp = page.waitForResponse((r) => r.url().endsWith('/refresh-results'));
      await row.getByRole('button', { name: 'Refresh results' }).click();
      await req; const r = await resp;
      const after = (await fakeCalls()).filter((c) => c.method === 'GET' && c.path.includes(`emailCampaigns/`)).length;
      log(pn, 'b1-wire', { status: r.status(), fakeGetsBefore: before, fakeGetsAfter: after });
      expect(r.status()).toBe(200);
      expect(after).toBeGreaterThan(before);
      await expect(row.getByRole('cell', { name: '3', exact: true }).first()).toBeVisible(); // delivered 3
      await expect(row.getByRole('cell', { name: '2', exact: true }).first()).toBeVisible(); // bounces 1+1 and opens 2
      log(pn, 'b1-cells', { cells: await row.locator('td').allInnerTexts() });
      await shot(page, pn, 'b1-refreshed');
    });

    test('B2 real 502 on test send and on refresh: plain text only', async ({ page }, info) => {
      const pn = info.project.name;
      await open(page);
      await saveDraft(page, `QA b2 ${pn}`);
      await fakeMode({ sendTest: '500' });
      const resp = page.waitForResponse((r) => r.url().endsWith('/test-send'));
      await page.getByRole('button', { name: 'Send test' }).click();
      const r1 = await resp;
      expect(r1.status()).toBe(502);
      log(pn, 'b2-test-wire', { status: r1.status(), body: await r1.text() });
      const t1 = toasts(page).filter({ hasText: 'Not done' }).first();
      await expect(t1).toBeVisible();
      const txt1 = await t1.innerText();
      log(pn, 'b2-test-toast', { txt1 });
      expect(txt1).toContain('Brevo refused the request. Nothing was sent.');
      expect(txt1).not.toMatch(RAW);
      await shot(page, pn, 'b2-test-502');
      await fakeMode({ sendTest: 'ok' });
      // refresh failing at the fake
      const { id } = await sendVia(page, `QA b2r ${pn}`, /^sent/);
      await page.reload();
      await fakeMode({ getCamp: '500' });
      const resp2 = page.waitForResponse((r) => r.url().endsWith('/refresh-results'));
      await rowOf(page, `QA b2r ${pn}`).getByRole('button', { name: 'Refresh results' }).click();
      const r2 = await resp2;
      log(pn, 'b2-refresh-wire', { status: r2.status(), body: await r2.text() });
      expect(r2.status()).toBe(502);
      const t2 = toasts(page).filter({ hasText: 'Not done' }).first();
      await expect(t2).toBeVisible();
      const txt2 = await t2.innerText();
      log(pn, 'b2-refresh-toast', { txt2, id });
      expect(txt2).toContain('Brevo could not return results. Try again shortly.');
      expect(txt2).not.toMatch(RAW);
      await shot(page, pn, 'b2-refresh-502');
    });

    // each real failure the fake can cause; the UI text for every code must be plain
    const FAILS: { name: string; mode: object; end: RegExp }[] = [
      { name: 'import 500', mode: { importFail: true }, end: /^failed/ },
      { name: 'create 500', mode: { createFail: '500' }, end: /^failed/ },
      { name: 'create bad body', mode: { createFail: 'badbody' }, end: /^failed/ },
      { name: 'sendNow 400', mode: { sendNow: '400' }, end: /^(failed|sendunknown)/ },
      { name: 'process failed', mode: { processStatus: 'failed' }, end: /^failed/ },
    ];
    test('B3 failed rows show the failure as plain text, for each failure the fake can cause', async ({ page }, info) => {
      const pn = info.project.name;
      await open(page);
      for (const f of FAILS) {
        psql('delete from marketing_campaigns');
        await fakeMode(MODE_DEFAULT);
        const subj = `QA b3 ${f.name} ${pn}`;
        const id = await apiReady(page, subj);
        await fakeMode(f.mode);
        const s = await apiSend(page, id);
        expect(s.status, s.body).toBe(202);
        const end = await waitDb(id, f.end);
        await page.reload();
        const row = rowOf(page, subj);
        await expect(row.locator('.mantine-Badge-root')).toBeVisible();
        const badge = ((await row.locator('.mantine-Badge-root').textContent()) ?? '').trim();
        const reasonEl = row.getByTestId('failure-reason');
        const text = (await reasonEl.count()) ? (await reasonEl.innerText()) : '';
        log(pn, `b3-${f.name}`, { db: end, badge, text });
        if (end.toLowerCase().startsWith('failed')) { expect(text, `${f.name} reason text`).not.toBe(''); expect(text).not.toMatch(RAW); }
        else expect(badge).toBe('Send unknown');
        await shot(page, pn, `b3-${f.name.replace(/\s/g, '-')}`);
      }
    });

    test('B4 Duplicate as draft from Failed and Sent rows', async ({ page }, info) => {
      const pn = info.project.name;
      await open(page);
      const html = '<h2>QA dup</h2><p>Original body</p>';
      await fakeMode({ importFail: false });
      const sent = await sendVia(page, `QA dup sent ${pn}`, /^sent/, html);
      const failed = await sendVia(page, `QA dup failed ${pn}`, /^failed/, html, { importFail: true });
      await fakeMode(MODE_DEFAULT);
      for (const [label, src] of [['Sent', sent.id], ['Failed', failed.id]] as const) {
        await page.reload();
        const subj = label === 'Sent' ? `QA dup sent ${pn}` : `QA dup failed ${pn}`;
        const before = Number(psql(`select count(*) from marketing_campaigns where status='Draft'`));
        const resp = page.waitForResponse((r) => r.url().endsWith(`${src}/duplicate`));
        await rowOf(page, subj).getByRole('button', { name: 'Duplicate as draft' }).click();
        const r = await resp;
        expect(r.status()).toBe(200);
        const body = await r.json();
        await expect(page.getByText('Draft created').first()).toBeVisible();
        await expect(page.getByRole('heading', { name: 'Edit draft' })).toBeVisible();
        await expect(subjectBox(page)).toHaveValue(subj);
        await expect(htmlBox(page)).toHaveValue(html);
        await expect(reason(page)).toHaveText('Send a test of the current content first.');
        await expect(page.getByTestId('send-button')).toBeDisabled();
        const d = psql(`select status||'|'||coalesce(test_sent_at::text,'')||'|'||coalesce(brevo_campaign_id::text,'')||'|'||coalesce(failure_code,'') from marketing_campaigns where id='${body.id}'`).trim();
        log(pn, `b4-dup-${label}`, { newId: body.id, db: d, draftsBefore: before, bodyStatus: body.status, testCurrent: body.testCurrent, testSentAt: body.testSentAt });
        expect(body.id).not.toBe(src);
        expect(body.status).toBe('draft');
        expect(body.testSentAt).toBeNull();
        expect(body.testCurrent).toBe(false);
        expect(d).toBe('Draft|||');
        expect(body.html).toBe(html);
        // it must test-send again before it can go
        await page.getByRole('button', { name: 'Send test' }).click();
        await expect(page.getByText('Test sent').first()).toBeVisible();
        await expect(reason(page)).toHaveText('Run a dry run on the current content first.');
        await shot(page, pn, `b4-dup-${label}`);
        psql(`delete from marketing_campaigns where id='${body.id}'`);
      }
    });

    test('B5 SendUnknown: warning, refresh, resolve guard, outcomes, send gate', async ({ page }, info) => {
      test.setTimeout(240_000);
      const pn = info.project.name;
      await fakeMode({ sendNow: '500' });
      await open(page);
      const u1 = await sendVia(page, `QA b5 one ${pn}`, /^sendunknown/);
      // Brevo reports the campaign as suspended: Refresh must leave the row SendUnknown (a "sent" report would settle it, see B7)
      await fakeMode({ sendNow: 'ok', campStatus: 'suspended' });
      expect(await sendNowCount()).toBe(1);
      await page.reload();
      const row = rowOf(page, `QA b5 one ${pn}`);
      const warn = page.getByTestId('send-unknown-warning');
      await expect(warn).toBeVisible();
      // placement: below the table, not inside the Status cell or any table cell
      expect(await warn.locator('xpath=ancestor::td').count()).toBe(0);
      const tb = (await page.locator('table').first().boundingBox())!, wb = (await warn.boundingBox())!;
      log(pn, 'b5-placement', { tableBottom: tb.y + tb.height, warnTop: wb.y });
      expect(wb.y).toBeGreaterThanOrEqual(tb.y + tb.height - 1);
      expect((await row.locator('td').nth(1).innerText())).not.toMatch(/may or may not/);
      for (const b of ['Edit', 'Refresh results', 'Duplicate as draft']) await expect(row.getByRole('button', { name: b })).toHaveCount(0);
      await shot(page, pn, 'b5-warning');
      // refresh from the warning
      const rr = page.waitForResponse((r) => r.url().endsWith('/refresh-results'));
      await warn.getByRole('button', { name: 'Refresh results' }).click();
      const r1 = await rr;
      expect(r1.status()).toBe(200);
      expect(dbRow(u1.id).toLowerCase()).toMatch(/^sendunknown/);
      log(pn, 'b5-refresh', { status: r1.status(), body: (await r1.text()).slice(0, 300), toasts: await toasts(page).allInnerTexts() });
      // confirm guard: opening and cancelling does nothing
      let resolveCalls = 0;
      page.on('request', (r) => { if (/\/resolve$/.test(r.url())) resolveCalls++; });
      await warn.getByRole('button', { name: 'I checked Brevo: it was sent' }).click();
      const modal = page.getByRole('dialog');
      await expect(modal).toContainText('Mark this campaign as sent');
      await shot(page, pn, 'b5-modal-sent');
      await page.keyboard.press('Escape');
      await expect(modal).toHaveCount(0);
      expect(resolveCalls).toBe(0);
      expect(dbRow(u1.id).toLowerCase()).toMatch(/^sendunknown/);
      // another send is refused while unresolved
      const other = await apiReady(page, `QA b5 other ${pn}`);
      const refused = await apiSend(page, other);
      log(pn, 'b5-send-while-unresolved', refused);
      expect(refused.status).toBe(409);
      expect(refused.body).toContain('unresolved_send');
      expect(await sendNowCount()).toBe(1);
      // "was sent" -> Sent
      await fakeMode({ campStatus: 'sent' });
      await page.reload();
      await warn.getByRole('button', { name: 'I checked Brevo: it was sent' }).click();
      const rq = page.waitForRequest((r) => /\/resolve$/.test(r.url()));
      const rs = page.waitForResponse((r) => /\/resolve$/.test(r.url()));
      await modal.getByRole('button', { name: 'Yes, it was sent' }).click();
      const req = await rq; const res = await rs;
      expect(JSON.parse(req.postData()!)).toEqual({ outcome: 'sent' });
      expect(res.status()).toBe(200);
      await expect(modal).toHaveCount(0);
      await expect(rowOf(page, `QA b5 one ${pn}`).locator('.mantine-Badge-root')).toHaveText('Sent');
      await expect(page.getByTestId('send-unknown-warning')).toHaveCount(0);
      expect(dbRow(u1.id).toLowerCase()).toMatch(/^sent/);
      expect(await sendNowCount()).toBe(1);
      // the gate lifts: the other send is now allowed (this is the only sendNow after the first)
      const allowed = await apiSend(page, other);
      log(pn, 'b5-send-after-resolve', allowed);
      expect(allowed.status).toBe(202);
      await waitDb(other, /^sent/);
      expect(await sendNowCount()).toBe(2);
      // "was not sent" -> Failed not_sent
      psql('delete from marketing_campaigns');
      await fakeMode({ sendNow: '500' });
      const u2 = await sendVia(page, `QA b5 two ${pn}`, /^sendunknown/);
      await fakeMode({ sendNow: 'ok', campStatus: 'suspended' });
      const n2 = await sendNowCount();
      await page.reload();
      await page.getByTestId('send-unknown-warning').getByRole('button', { name: 'I checked Brevo: it was not sent' }).click();
      await expect(modal).toContainText('Mark this campaign as not sent');
      const rs2 = page.waitForResponse((r) => /\/resolve$/.test(r.url()));
      const rq2 = page.waitForRequest((r) => /\/resolve$/.test(r.url()));
      await modal.getByRole('button', { name: 'Yes, it was not sent' }).click();
      expect(JSON.parse((await rq2).postData()!)).toEqual({ outcome: 'notSent' });
      expect((await rs2).status()).toBe(200);
      const row2 = rowOf(page, `QA b5 two ${pn}`);
      await expect(row2.locator('.mantine-Badge-root')).toHaveText('Failed');
      await expect(row2.getByTestId('failure-reason')).toHaveText('This campaign has not been sent, so it has no results.');
      expect(dbRow(u2.id)).toMatch(/^Failed\|not_sent/);
      expect(await sendNowCount()).toBe(n2);
      await shot(page, pn, 'b5-not-sent');
    });

    test('B6 preview: footer in the sandboxed iframe, script inert, unsaved note', async ({ page }, info) => {
      const pn = info.project.name;
      await open(page);
      const evil = '<p id="a">hello</p><script>document.getElementById("a").textContent="SCRIPT-RAN";try{parent.document.title="PWNED"}catch(e){}</script>';
      await saveDraft(page, `QA b6 ${pn}`, evil);
      await page.reload();
      await page.getByRole('button', { name: 'Edit' }).click();
      await expect(htmlBox(page)).toHaveValue(evil);
      const frame = page.getByTestId('campaign-preview');
      expect(await frame.getAttribute('sandbox')).toBe('');
      expect(await frame.evaluate((e) => e.hasAttribute('sandbox'))).toBe(true);
      const doc = await frame.getAttribute('srcdoc');
      log(pn, 'b6-srcdoc', { len: doc?.length, hasFooterName: /QA Example Ltd/.test(doc ?? ''), hasAddress: /1 Example Street/.test(doc ?? ''), hasVat: /QA0000000/.test(doc ?? '') });
      // FINDING if these fail: CampaignsPanel overlays the list row (no previewHtml) on the opened campaign, so the footer preview is never used.
      expect.soft(doc, 'saved preview must carry the footer (previewHtml from GET {id})').toContain('QA Example Ltd');
      expect.soft(doc).toContain('1 Example Street, Testville');
      await expect(page.locator('[data-testid=preview-error]')).toHaveCount(0);
      await expect(page.getByText('Showing your unsaved HTML')).toHaveCount(0);
      await expect(page.frameLocator('[data-testid=campaign-preview]').locator('#a')).toHaveText('hello');
      await page.waitForTimeout(500);
      expect(await page.title()).not.toBe('PWNED');
      await shot(page, pn, 'b6-preview-footer');
      // unsaved edit: local HTML and the note, no footer
      await htmlBox(page).fill('<p>LOCAL EDIT</p>');
      await expect(page.getByText('Showing your unsaved HTML. The footer is added when you save.')).toBeVisible();
      const doc2 = await frame.getAttribute('srcdoc');
      expect(doc2).toBe('<p>LOCAL EDIT</p>');
      expect(doc2).not.toContain('QA Example Ltd');
      await shot(page, pn, 'b6-preview-unsaved');
      // MOCK of the single-campaign GET: previewError is shown as a plain sentence
      await page.route(`**${API}/*`, async (route) => {
        if (route.request().method() !== 'GET') return route.fallback();
        const res = await route.fetch(); const j = await res.json();
        return route.fulfill({ response: res, json: { ...j, previewHtml: null, previewError: 'footer_address_missing' } });
      });
      await page.reload();
      await page.getByRole('button', { name: 'Edit' }).click();
      const pe = page.getByTestId('preview-error');
      await expect.soft(pe, 'previewError from GET {id} must be shown').toHaveText('The footer cannot be built: the postal address is not configured on the server.', { timeout: 4000 });
      await shot(page, pn, 'b6-preview-error-mock');
    });

    const cases: { name: string; mode: object; expect: RegExp; badge: string }[] = [
        // a draft in Brevo never settles on Refresh: only `resolve` (not sent) moves the row to Failed not_sent
        { name: 'draft', mode: { campStatus: 'draft' }, expect: /^sendunknown/, badge: 'Send unknown' },
        { name: 'suspended', mode: { campStatus: 'suspended' }, expect: /^sendunknown/, badge: 'Send unknown' },
        { name: 'cancelled', mode: { campStatus: 'cancelled' }, expect: /^sendunknown/, badge: 'Send unknown' },
        { name: 'archive', mode: { campStatus: 'archive' }, expect: /^sendunknown/, badge: 'Send unknown' },
        { name: 'not-found', mode: { getCamp: '404' }, expect: /^sendunknown/, badge: 'Send unknown' },
      ];
    for (const c of cases) {
      test(`B7 Refresh settles on the real API: Brevo says ${c.name}`, async ({ page }, info) => {
        test.setTimeout(240_000);
        const pn = info.project.name;
        await open(page);
        psql('delete from marketing_campaigns');
        await fakeMode({ ...MODE_DEFAULT, campStatus: 'in_process' });
        const subj = `QA b7 ${c.name} ${pn}`;
        const { id, end } = await sendVia(page, subj, /^(sending|sent)/);
        await fakeMode(c.mode);
        await page.reload();
        const row = rowOf(page, subj);
        log(pn, `b7-${c.name}-before`, { end, ui: await row.locator('.mantine-Badge-root').textContent() });
        if (!/^sending/.test(end.toLowerCase())) { log(pn, `b7-${c.name}-skip`, { end }); expect.soft(end.toLowerCase(), 'row must be Sending before refresh').toMatch(/^sending/); return; }
        const rr = page.waitForResponse((r) => r.url().endsWith('/refresh-results'));
        await row.getByRole('button', { name: 'Refresh results' }).click();
        const r = await rr;
        const rbody = (await r.text()).slice(0, 200);
        const db = await waitDb(id, c.expect, 20_000).catch(() => dbRow(id));
        await page.reload();
        const badge = ((await rowOf(page, subj).locator('.mantine-Badge-root').textContent()) ?? '').trim();
        const fr = rowOf(page, subj).getByTestId('failure-reason');
        const text = (await fr.count()) ? await fr.innerText() : '';
        log(pn, `b7-${c.name}`, { refreshStatus: r.status(), body: rbody, db, badge, text });
        expect(db.toLowerCase()).toMatch(c.expect);
        expect(badge).toBe(c.badge);
        await shot(page, pn, `b7-${c.name}`);
      });
    }

    test('B8 layout: badges, warning outside the Status cell, history on phone, toasts vs modal title, no overflow', async ({ page }, info) => {
      const pn = info.project.name;
      await open(page);
      await sendVia(page, `QA b8 sent ${pn}`, /^sent/);
      await sendVia(page, `QA b8 failed ${pn}`, /^failed/, BODY, { importFail: true });
      await fakeMode({ importFail: false });
      await sendVia(page, `QA b8 unknown ${pn}`, /^sendunknown/, BODY, { sendNow: '500' });
      await fakeMode(MODE_DEFAULT);
      await saveDraftApi(page, `QA b8 draft ${pn}`);
      await page.reload();
      await expect(page.locator('table .mantine-Badge-root')).toHaveCount(4);
      const words = await page.locator('table .mantine-Badge-root').allTextContents();
      log(pn, 'b8-badge-words', { words });
      expect(words.map((x) => x.trim()).sort()).toEqual(['Draft', 'Failed', 'Send unknown', 'Sent']);
      const o = await noPageOverflow(page);
      log(pn, 'b8-overflow', o);
      expect(o.doc).toBeLessThanOrEqual(o.win);
      await expect(page.getByTestId('send-unknown-warning')).toBeVisible();
      expect(await page.getByTestId('send-unknown-warning').locator('xpath=ancestor::td').count()).toBe(0);
      const clipped = await page.evaluate(() => [...document.querySelectorAll('table th, table td')].filter((e) => e.scrollWidth > e.clientWidth + 1).map((e) => e.textContent));
      expect(clipped, 'clipped cells').toEqual([]);
      await shot(page, pn, 'b8-history');
      if (pn === 'phone') {
        const m = await page.locator('table').first().evaluate((t) => { let e: HTMLElement | null = t.parentElement; while (e && !/(auto|scroll)/.test(getComputedStyle(e).overflowX)) e = e.parentElement; return e ? { sw: e.scrollWidth, cw: e.clientWidth } : { sw: 0, cw: 0 }; });
        log(pn, 'b8-phone-scroll', m);
        expect(m.sw).toBeGreaterThan(m.cw);
        const btn = page.getByTestId('send-unknown-warning').getByRole('button', { name: 'I checked Brevo: it was not sent' });
        const bb = (await btn.boundingBox())!; const vp = page.viewportSize()!;
        expect(bb.x).toBeGreaterThanOrEqual(0); expect(bb.x + bb.width).toBeLessThanOrEqual(vp.width + 1);
        // history usable: scroll the table sideways and reach the last column's buttons
        await rowOf(page, `QA b8 sent ${pn}`).getByRole('button', { name: 'Duplicate as draft' }).scrollIntoViewIfNeeded();
        await expect(rowOf(page, `QA b8 sent ${pn}`).getByRole('button', { name: 'Duplicate as draft' })).toBeVisible();
        await shot(page, pn, 'b8-history-scrolled');
      }
      // toasts do not cover the modal title: open the resolve modal, make the resolve answer 500 (MOCK) so a toast shows
      await page.route('**/resolve', (r) => r.fulfill({ status: 500, body: '' }));
      await page.getByTestId('send-unknown-warning').getByRole('button', { name: 'I checked Brevo: it was sent' }).click();
      const modal = page.getByRole('dialog');
      await modal.getByRole('button', { name: 'Yes, it was sent' }).click();
      const t = toasts(page).filter({ hasText: 'Not done' }).first();
      await expect(t).toBeVisible();
      await page.waitForTimeout(1000); // let the toast finish sliding in before measuring
      const title = (await modal.locator('.mantine-Modal-title').boundingBox())!, tb = (await t.boundingBox())!;
      const overlap = !(tb.x + tb.width <= title.x || title.x + title.width <= tb.x || tb.y + tb.height <= title.y || title.y + title.height <= tb.y);
      log(pn, 'b8-toast-vs-title', { title, toast: tb, overlap, toastText: await t.innerText() });
      expect(overlap, 'toast overlaps the modal title').toBe(false);
      await page.screenshot({ path: `${process.env.QA_OUT ?? '/tmp'}/shots/C-${pn}-b8-toast-modal-viewport.png` }); // viewport, not fullPage: fixed toasts and the modal are in view
    });

    test('B9 list error retry (MOCK 502 on the list), recovery, trailing-space subject not dirty', async ({ page }, info) => {
      const pn = info.project.name;
      await loginAsOnly(page);
      await sendVia(page, `QA b9 ${pn}`, /^sent/);
      let fail = true;
      await page.route(`**${API}?limit=50`, (r) => (fail ? r.fulfill({ status: 502, body: 'bad gateway' }) : r.fallback()));
      await page.goto(PAGE);
      const err = page.getByTestId('campaigns-list-error');
      await expect(err).toBeVisible();
      const et = await err.innerText();
      log(pn, 'b9-error-text', { et });
      expect(et).not.toMatch(RAW);
      await shot(page, pn, 'b9-list-error');
      fail = false;
      await err.getByRole('button', { name: 'Retry' }).click();
      await expect(err).toHaveCount(0);
      await expect(rowOf(page, `QA b9 ${pn}`).first()).toBeVisible();
      // trailing space
      const subj = `QA b9 trim ${pn}`;
      await subjectBox(page).fill(`${subj}   `);
      await htmlBox(page).fill(BODY);
      await page.getByRole('button', { name: 'Save draft' }).click();
      await expect(page.getByText('Draft saved').first()).toBeVisible();
      await expect(page.getByRole('button', { name: 'Save draft' })).toBeDisabled(); // not dirty after the save
      await expect(page.getByRole('button', { name: 'Dry run' })).toBeEnabled();
      await expect(reason(page)).toHaveText('Send a test of the current content first.');
      await page.reload();
      await expect(rowOf(page, subj).first()).toBeVisible();
    });
  });
}

/** Phase F: campaigns on, footer settings missing. The real previewError (footer_controller_name_missing) must reach the page as a plain sentence. */
if (PHASE === 'F') {
  test('B6 real previewError: footer cannot be built -> plain sentence in the preview', async ({ page }, info) => {
    const w = await watch(page);
    const pn = info.project.name;
    psql('delete from marketing_campaigns');
    await open(page);
    const created = page.waitForResponse((r) => r.url().endsWith(API) && r.request().method() === 'POST');
    await saveDraft(page, `QA f ${pn}`);
    const body = await (await created).json();
    log(pn, 'f-api-previewError', { previewError: body.previewError, previewHtml: body.previewHtml });
    expect(body.previewError).toBe('footer_controller_name_missing');
    await page.reload();
    await page.getByRole('button', { name: 'Edit' }).click();
    const pe = page.getByTestId('preview-error');
    const shown = await pe.count();
    log(pn, 'f-preview-error-shown', { shown, text: shown ? await pe.innerText() : null });
    await shot(page, pn, 'f-preview-error');
    await expect(pe, 'previewError must show as a plain sentence').toHaveText('The footer cannot be built: the sender name is not configured on the server.', { timeout: 5000 });
    log(pn, 'f-console', { console: w.console, http: w.http });
  });
}

async function loginAsOnly(page: Page) {
  const { loginAs } = await import('../../fixtures/stack');
  await loginAs(page, 'admin');
}
async function saveDraftApi(page: Page, subject: string) {
  const r = await page.request.post(API, { data: { subject, html: BODY } });
  expect(r.status()).toBe(200);
}
void fake; void FOOTER_OK; void htmlBox; void saveDraft; void sendNowCount;
