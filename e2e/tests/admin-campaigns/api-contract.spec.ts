import { test, expect, type APIRequestContext, type Browser } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { psql, loginAs } from '../../fixtures/stack';
import { PHASE, API, BODY, log, open } from './helpers';

/**
 * The resolve and duplicate routes on the real API with an admin session (phase E, run on the list-send-on stack; phase A for the
 * campaigns-disabled 404). Rows are made with the API and then moved to the state under test in SQL, so no Brevo call is needed.
 * Refusal codes are compared with the MESSAGES table the page uses (web/lib/api/marketingCampaigns.ts).
 */
const SRC = fs.readFileSync(path.resolve(__dirname, '../../../web/lib/api/marketingCampaigns.ts'), 'utf8');
const MESSAGE_KEYS = new Set([...SRC.slice(SRC.indexOf('const MESSAGES'), SRC.indexOf('const GENERIC')).matchAll(/^\s+(\w+):/gm)].map((m) => m[1]));
const STATUSES = ['draft', 'queued', 'syncing', 'sending', 'sent', 'failed', 'sendUnknown'];
const CAMPAIGN_KEYS = ['id', 'subject', 'status', 'createdAt', 'updatedAt', 'testSentAt', 'testCurrent', 'confirmedRecipientCount', 'finalRecipientCount', 'dispatchStartedAt', 'failureCode', 'stats', 'statsAt'];
const ZERO = '00000000-0000-4000-8000-000000000000';

async function row(req: APIRequestContext, status: string, failure: string | null = null) {
  const r = await req.post(API, { data: { subject: `QA contract ${status}`, html: BODY } });
  expect(r.status()).toBe(200);
  const id = (await r.json()).id as string;
  if (status !== 'Draft') {
    psql(`update marketing_campaigns set status='${status}', failure_code=${failure ? `'${failure}'` : 'null'}, dispatch_started_at=now(), confirmed_recipient_count=4 where id='${id}'`);
  }
  return id;
}
async function call(req: APIRequestContext, method: 'post', url: string, data?: unknown) {
  const r = await req.post(url, data === undefined ? {} : { data });
  const text = await r.text();
  let json: any = null; try { json = JSON.parse(text); } catch { /* not json */ }
  return { status: r.status(), text, json };
}
function shapeOk(j: any) {
  for (const k of CAMPAIGN_KEYS) expect(j, `key ${k}`).toHaveProperty(k);
  expect(STATUSES).toContain(j.status);
}
function codeKnown(label: string, j: any) {
  const code = j?.error;
  const known = typeof code === 'string' && MESSAGE_KEYS.has(code);
  log('api', `${label}-code-check`, { code, inMessages: known });
  expect(known, `${label}: refusal code ${code} must have a page message`).toBe(true);
}
async function asUser(browser: Browser, who: 'creator' | 'admin') {
  const ctx = await browser.newContext({ baseURL: process.env.BASE_URL ?? 'http://localhost:8731' });
  const page = await ctx.newPage();
  await loginAs(page, who);
  return { ctx, req: ctx.request };
}

if (PHASE === 'E') {
  test.describe('phase E: resolve and duplicate on the real API', () => {
    test.beforeAll(() => psql('delete from marketing_campaigns'));

    test('resolve: sent, notSent, bad outcome, wrong row state, unknown id, non-admin', async ({ browser }, info) => {
      test.skip(info.project.name !== 'desktop', 'API-only; one project');
      const { ctx, req } = await asUser(browser, 'admin');
      const rec = (n: string, r: unknown) => log('api', n, r as object);

      const a = await row(req, 'SendUnknown', 'send_unknown');
      const rs = await call(req, 'post', `${API}/${a}/resolve`, { outcome: 'sent' });
      rec('resolve-sent', { status: rs.status, body: rs.text.slice(0, 400) });
      expect(rs.status).toBe(200); shapeOk(rs.json); expect(rs.json.status).toBe('sent');
      expect(psql(`select status from marketing_campaigns where id='${a}'`).trim()).toBe('Sent');
      const again = await call(req, 'post', `${API}/${a}/resolve`, { outcome: 'sent' });
      rec('resolve-twice', { status: again.status, body: again.text });
      expect(again.status).toBe(409); expect(again.json.error).toBe('not_send_unknown'); codeKnown('resolve-twice', again.json);

      const b = await row(req, 'SendUnknown', 'send_unknown');
      const rn = await call(req, 'post', `${API}/${b}/resolve`, { outcome: 'notSent' });
      rec('resolve-notSent', { status: rn.status, body: rn.text.slice(0, 400) });
      expect(rn.status).toBe(200); shapeOk(rn.json); expect(rn.json.status).toBe('failed'); expect(rn.json.failureCode).toBe('not_sent');

      const c = await row(req, 'SendUnknown', 'send_unknown');
      for (const [label, data] of [['maybe', { outcome: 'maybe' }], ['empty-object', {}], ['null-outcome', { outcome: null }], ['number', { outcome: 1 }], ['wrong-case', { outcome: 'SENT' }]] as const) {
        const r = await call(req, 'post', `${API}/${c}/resolve`, data);
        rec(`resolve-bad-${label}`, { status: r.status, body: r.text });
        expect(r.status, label).toBe(400);
        if (r.json?.error) { expect(r.json.error).toBe('outcome_invalid'); codeKnown(`resolve-bad-${label}`, r.json); }
        else rec(`resolve-bad-${label}-no-code`, { note: 'binding error without an error code: the page falls back to the generic sentence' });
      }
      const noBody = await req.post(`${API}/${c}/resolve`);
      rec('resolve-no-body', { status: noBody.status(), body: (await noBody.text()).slice(0, 300) });
      expect([400, 415, 422]).toContain(noBody.status());
      void 0;
      expect(psql(`select status from marketing_campaigns where id='${c}'`).trim()).toBe('SendUnknown'); // none of the bad calls changed it

      for (const st of ['Draft', 'Failed', 'Sent', 'Sending', 'Queued', 'Syncing']) {
        const id = await row(req, st, st === 'Failed' ? 'brevo_http_500' : null);
        const r = await call(req, 'post', `${API}/${id}/resolve`, { outcome: 'sent' });
        rec(`resolve-from-${st}`, { status: r.status, body: r.text });
        expect(r.status, st).toBe(409); expect(r.json.error).toBe('not_send_unknown'); codeKnown(`resolve-from-${st}`, r.json);
        expect(psql(`select status from marketing_campaigns where id='${id}'`).trim()).toBe(st);
        if (['Queued', 'Syncing', 'Sending'].includes(st)) psql(`delete from marketing_campaigns where id='${id}'`); // one active row at a time (ix_marketing_campaigns_one_active)
      }
      const unk = await call(req, 'post', `${API}/${ZERO}/resolve`, { outcome: 'sent' });
      rec('resolve-unknown-id', { status: unk.status, body: unk.text.slice(0, 200) });
      expect(unk.status).toBe(404);

      const other = await asUser(browser, 'creator');
      const na = await call(other.req, 'post', `${API}/${c}/resolve`, { outcome: 'sent' });
      rec('resolve-non-admin', { status: na.status, body: na.text.slice(0, 200) });
      expect(na.status).toBe(403);
      expect(psql(`select status from marketing_campaigns where id='${c}'`).trim()).toBe('SendUnknown');
      const anon = await (await browser.newContext({ baseURL: process.env.BASE_URL ?? 'http://localhost:8731' })).request.post(`${API}/${c}/resolve`, { data: { outcome: 'sent' } });
      rec('resolve-anonymous', { status: anon.status() });
      expect([401, 403]).toContain(anon.status());
      await other.ctx.close(); await ctx.close();
    });

    test('duplicate: Draft, Failed, Sent, other states, unknown id, non-admin', async ({ browser }, info) => {
      test.skip(info.project.name !== 'desktop', 'API-only; one project');
      const { ctx, req } = await asUser(browser, 'admin');
      const rec = (n: string, r: unknown) => log('api', n, r as object);
      const bad = '<p>dup source</p>';
      for (const [st, failure] of [['Draft', null], ['Failed', 'brevo_http_500'], ['Sent', null], ['Queued', null], ['Syncing', null], ['Sending', null], ['SendUnknown', 'send_unknown']] as const) {
        const r0 = await req.post(API, { data: { subject: `QA dup src ${st}`, html: bad } });
        const id = (await r0.json()).id as string;
        if (st !== 'Draft') psql(`update marketing_campaigns set status='${st}', failure_code=${failure ? `'${failure}'` : 'null'}, test_sent_at=now(), test_content_hash='abc', brevo_campaign_id=123, dispatch_started_at=now(), confirmed_recipient_count=4 where id='${id}'`);
        else psql(`update marketing_campaigns set test_sent_at=now(), test_content_hash='abc' where id='${id}'`);
        const r = await call(req, 'post', `${API}/${id}/duplicate`);
        rec(`duplicate-from-${st}`, { status: r.status, body: r.text.slice(0, 500) });
        if (r.status === 200) {
          shapeOk(r.json);
          expect(r.json.id).not.toBe(id);
          expect(r.json.status).toBe('draft'); expect(r.json.html).toBe(bad); expect(r.json.subject).toBe(`QA dup src ${st}`);
          expect(r.json.testSentAt).toBeNull(); expect(r.json.testCurrent).toBe(false); expect(r.json.failureCode).toBeNull();
          expect(psql(`select coalesce(test_sent_at::text,'')||coalesce(test_content_hash,'')||coalesce(brevo_campaign_id::text,'')||coalesce(dispatch_started_at::text,'') from marketing_campaigns where id='${r.json.id}'`).trim()).toBe('');
          expect(psql(`select status from marketing_campaigns where id='${id}'`).trim()).toBe(st); // the source is untouched
        } else {
          expect([404, 409, 422]).toContain(r.status);
          codeKnown(`duplicate-from-${st}`, r.json);
        }
        if (['Queued', 'Syncing', 'Sending'].includes(st)) psql(`delete from marketing_campaigns where id='${id}'`);
      }
      const unk = await call(req, 'post', `${API}/${ZERO}/duplicate`);
      rec('duplicate-unknown-id', { status: unk.status, body: unk.text.slice(0, 200) });
      expect(unk.status).toBe(404);
      const other = await asUser(browser, 'creator');
      const id = psql(`select id from marketing_campaigns limit 1`).trim();
      const before = psql('select count(*) from marketing_campaigns').trim();
      const na = await call(other.req, 'post', `${API}/${id}/duplicate`);
      rec('duplicate-non-admin', { status: na.status });
      expect(na.status).toBe(403);
      expect(psql('select count(*) from marketing_campaigns').trim()).toBe(before);
      await other.ctx.close(); await ctx.close();
    });
  });
}

if (PHASE === 'A') {
  test.describe('phase A: campaigns disabled', () => {
    test('resolve and duplicate answer 404 for an admin, 403 for a non-admin', async ({ browser }, info) => {
      test.skip(info.project.name !== 'desktop', 'API-only; one project');
      const { ctx, req } = await asUser(browser, 'admin');
      const res = await call(req, 'post', `${API}/${ZERO}/resolve`, { outcome: 'sent' });
      const dup = await call(req, 'post', `${API}/${ZERO}/duplicate`);
      log('api', 'disabled-admin', { resolve: res.status, duplicate: dup.status });
      expect(res.status).toBe(404); expect(dup.status).toBe(404);
      const other = await asUser(browser, 'creator');
      const r2 = await call(other.req, 'post', `${API}/${ZERO}/resolve`, { outcome: 'sent' });
      const d2 = await call(other.req, 'post', `${API}/${ZERO}/duplicate`);
      log('api', 'disabled-non-admin', { resolve: r2.status, duplicate: d2.status });
      expect(r2.status).toBe(403); expect(d2.status).toBe(403);
      await other.ctx.close(); await ctx.close();
    });
  });
}
void open;
