import { expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import { loginAs, psql } from '../../fixtures/stack';

/**
 * Shared QA helpers for /app/admin/campaigns against an isolated stack and a local fake Brevo (see e2e/brevo-mock/server.mjs).
 * The fake's address comes from env FAKE (default http://127.0.0.1:19741); the phase from QA_PHASE; artifacts go to QA_OUT.
 * Results are appended to results-<phase>.jsonl so one phase never overwrites another.
 */
export const PHASE = process.env.QA_PHASE ?? 'B';
export const FAKE = process.env.FAKE ?? 'http://127.0.0.1:19741';
export const OUT = process.env.QA_OUT ?? `${process.cwd()}/test-results/admin-campaigns-qa`;
export const TEST_RCPT = 'qa-test@example.invalid';
export const PAGE = '/app/admin/campaigns';
export const API = '/api/v1/marketing/admin/campaigns';
export const BODY = '<h1>QA hello</h1><p>Plain body for the QA run.</p>';
fs.mkdirSync(`${OUT}/shots`, { recursive: true });

export const fake = async (path: string, init?: RequestInit) => (await fetch(`${FAKE}${path}`, init)).json() as Promise<any>;
export const fakeMode = (m: object) => fake('/__qa/mode', { method: 'POST', body: JSON.stringify(m), headers: { 'content-type': 'application/json' } });
export const fakeReset = () => fake('/__qa/reset', { method: 'POST' });
export const fakeCalls = () => fake('/__qa/calls') as Promise<any[]>;
export const sendNowCount = async () => (await fakeCalls()).filter((c) => c.path.endsWith('/sendNow')).length;
export const MODE_DEFAULT = { importFail: false, sendNow: 'ok', campStatus: 'sent', stats: null, sendDelayMs: 0, sendTest: 'ok', getCamp: 'ok', createFail: null, processStatus: 'completed', sendHold: false, importDelayMs: 0 };

export type Watch = { console: string[]; failed: string[]; http: string[]; brevoHits: string[] };
export async function watch(page: Page): Promise<Watch> {
  const w: Watch = { console: [], failed: [], http: [], brevoHits: [] };
  page.on('console', (m) => { if (m.type() === 'error') w.console.push(m.text().slice(0, 300)); });
  page.on('pageerror', (e) => w.console.push(`pageerror: ${e.message.slice(0, 300)}`));
  page.on('requestfailed', (r) => { if (!/brevo/i.test(r.url())) w.failed.push(`${r.method()} ${r.url()} ${r.failure()?.errorText}`); });
  page.on('response', (r) => { if (r.status() >= 400) w.http.push(`${r.status()} ${r.request().method()} ${new URL(r.url()).pathname}`); });
  await page.context().route((u) => /brevo/i.test(u.hostname), (route) => { w.brevoHits.push(route.request().url()); return route.abort(); });
  return w;
}
export const shot = async (page: Page, project: string, name: string) => {
  const t0 = Date.now();
  try { await page.screenshot({ path: `${OUT}/shots/${PHASE}-${project}-${name}.png`, fullPage: true, timeout: 20_000 }); }
  catch (e) { fs.appendFileSync(`${OUT}/results-${PHASE}.jsonl`, JSON.stringify({ project, phase: PHASE, name: `shot-failed-${name}`, ms: Date.now() - t0, err: String(e).slice(0, 200) }) + '\n'); }
};
export const log = (project: string, name: string, data: unknown) =>
  fs.appendFileSync(`${OUT}/results-${PHASE}.jsonl`, JSON.stringify({ project, phase: PHASE, name, ...(data as object) }) + '\n');
export const noPageOverflow = (page: Page) => page.evaluate(() => ({ doc: document.documentElement.scrollWidth, win: window.innerWidth }));

export async function open(page: Page) {
  await loginAs(page, 'admin');
  await page.goto(PAGE);
}
export const subjectBox = (page: Page) => page.getByLabel('Subject');
export const htmlBox = (page: Page) => page.getByLabel('HTML', { exact: true });
export const reason = (page: Page) => page.getByTestId('send-reason');
export const toasts = (page: Page) => page.locator('.mantine-Notification-root');
/** A campaign's history row plus the row below it that holds its actions. Use `.first()` for the row alone. */
export const rowOf = (page: Page, subject: string) =>
  page.getByRole('row').filter({ hasText: subject }).locator('xpath=self::tr | following-sibling::tr[1]');

export async function saveDraft(page: Page, subject: string, html = BODY) {
  await subjectBox(page).fill(subject);
  await htmlBox(page).fill(html);
  await page.getByRole('button', { name: 'Save draft' }).click();
  await expect(page.getByText('Draft saved').first()).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Edit draft' })).toBeVisible();
}

export function seedConsents() {
  psql(`delete from marketing_consents where email like '%@example.invalid'`);
  for (const n of [1, 2, 3, 4]) {
    psql(`insert into marketing_consents (id,email,status,wording_version,source,confirmed_at,consented_at,updated_at) values (gen_random_uuid(),'qa-person${n}@example.invalid','OptedIn','qa-v1','SiteFooter',now(),now(),now())`);
  }
}

/** Real-API setup without the UI: create, test-send and dry-run a draft; returns its id. */
export async function apiReady(page: Page, subject: string, html = BODY): Promise<string> {
  const c = await page.request.post(API, { data: { subject, html } });
  expect(c.status(), await c.text()).toBe(200);
  const id = (await c.json()).id as string;
  expect((await page.request.post(`${API}/${id}/test-send`)).status()).toBe(200);
  const d = await page.request.post(`${API}/${id}/dry-run`);
  expect(d.status()).toBe(200);
  return id;
}
export async function apiSend(page: Page, id: string, count = 4) {
  const r = await page.request.post(`${API}/${id}/send`, { data: { confirmRecipientCount: count } });
  return { status: r.status(), body: await r.text() };
}
export const dbRow = (id: string) => psql(`select status||'|'||coalesce(failure_code,'') from marketing_campaigns where id='${id}'`).trim();
export async function waitDb(id: string, re: RegExp, ms = 120_000) {
  const t0 = Date.now();
  let last = '';
  while (Date.now() - t0 < ms) {
    last = dbRow(id);
    if (re.test(last.toLowerCase())) return last;
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error(`campaign ${id} did not reach ${re}; last ${last}`);
}
/** Create, test, dry-run and send through the API; waits for the end state in the DB. */
export async function sendVia(page: Page, subject: string, end: RegExp, html = BODY, modeAfterReady?: object) {
  const id = await apiReady(page, subject, html);
  if (modeAfterReady) await fakeMode(modeAfterReady);
  const s = await apiSend(page, id);
  expect(s.status, s.body).toBe(202);
  return { id, end: await waitDb(id, end) };
}
