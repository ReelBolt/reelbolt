import { test, expect } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { psql } from '../../fixtures/stack';
import { PHASE, API, MODE_DEFAULT, fake, fakeMode, fakeReset, fakeCalls, sendNowCount, watch, shot, log, open, seedConsents, apiReady, apiSend, waitDb, dbRow, rowOf, type Watch } from './helpers';

/**
 * Phase R: restart the Inference API mid-send. Needs the list-send-on stack with SyncStaleMinutes=1 (the lease), and
 * QA_COMPOSE = a script that runs `docker compose` for this stack (e2e/README.md: the same -p/--env-file/-f flags as up-isolated.sh).
 * The fake holds the call under test open (sendHold / importDelayMs) so the API is restarted while the send is in flight.
 */
const COMPOSE = process.env.QA_COMPOSE ?? '';
const CONTAINER = process.env.QA_INFERENCE_CONTAINER ?? '';
const compose = (...a: string[]) => execFileSync(COMPOSE, a, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

async function until<T>(what: string, fn: () => Promise<T | false | null | undefined> | T | false | null | undefined, ms = 60_000): Promise<T> {
  const t0 = Date.now();
  for (;;) {
    const v = await fn();
    if (v) return v as T;
    if (Date.now() - t0 > ms) throw new Error(`timed out waiting for ${what}`);
    await sleep(300);
  }
}
let restartedAt = Date.now();
const restartGraceful = () => { restartedAt = Date.now(); return compose('restart', '-t', '30', 'inference'); };
const restartKill = () => { restartedAt = Date.now(); execFileSync('docker', ['kill', '-s', 'KILL', CONTAINER]); compose('up', '-d', '--wait', '--wait-timeout', '240', 'inference'); };
const healthy = () => until('inference healthy', () => execFileSync('docker', ['inspect', '-f', '{{.State.Health.Status}}', CONTAINER], { encoding: 'utf8' }).trim() === 'healthy', 240_000);

if (PHASE === 'R') {
  test.describe('phase R: restart mid-send', () => {
    let w: Watch;
    test.beforeAll(() => { expect(COMPOSE && CONTAINER, 'QA_COMPOSE and QA_INFERENCE_CONTAINER are required').toBeTruthy(); });
    test.beforeEach(async ({ page }, info) => {
      test.skip(info.project.name !== 'desktop', 'server-side behaviour; one project');
      test.setTimeout(480_000);
      w = await watch(page);
      seedConsents();
      psql('delete from marketing_campaigns');
      await fakeReset();
      await fakeMode(MODE_DEFAULT);
    });
    test.afterEach(async ({}, info) => {
      await fakeMode(MODE_DEFAULT);
      if (info.project.name === 'desktop') {
        log('desktop', info.title, { status: info.status, console: w.console, http4xx5xx: w.http, brevoHits: w.brevoHits });
        expect(w.brevoHits).toEqual([]);
      }
    });

    /** After the restart: wait past the lease (1 min) plus recovery ticks for the expected end state; returns seconds taken. */
    async function settle(id: string, re: RegExp, label: string) {
      const t0 = Date.now();
      const end = await until(`${label} -> ${re}`, async () => { const r = dbRow(id); return re.test(r.toLowerCase()) ? r : false; }, 240_000);
      const secs = Math.round((Date.now() - t0) / 1000);
      log('desktop', `${label}-settled`, { end, secondsAfterRestart: secs });
      await sleep(Math.max(10_000, restartedAt + 90_000 - Date.now())); // past the 1-minute lease plus recovery ticks: nothing may change or be resent
      log('desktop', `${label}-after-settle`, { row: dbRow(id), sendNow: await sendNowCount() });
      expect(dbRow(id)).toBe(end);
      return end;
    }
    async function laterSendWorks(page: import('@playwright/test').Page, tag: string, expectedSendNow: number) {
      const id2 = await apiReady(page, `QA ${tag} later`);
      const s = await apiSend(page, id2);
      log('desktop', `${tag}-later-send`, s);
      expect(s.status, s.body).toBe(202);
      await waitDb(id2, /^sent/, 120_000);
      expect(await sendNowCount()).toBe(expectedSendNow);
    }

    for (const [tag, kill] of [['R1 graceful', false], ['R2 sigkill', true]] as const) {
      test(`${tag}: restart while sendNow is held open -> SendUnknown, no second sendNow`, async ({ page }) => {
        await open(page);
        await fakeMode({ sendHold: true });
        const id = await apiReady(page, `QA ${tag}`);
        expect((await apiSend(page, id)).status).toBe(202);
        await until('sendNow reaches the fake', async () => (await sendNowCount()) === 1);
        log('desktop', `${tag}-before-restart`, { row: dbRow(id), sendNow: await sendNowCount() });
        if (kill) restartKill(); else restartGraceful();
        await healthy();
        const end = await settle(id, /^sendunknown/, tag);
        expect(end).toMatch(/^SendUnknown\|send_unknown/);
        expect(await sendNowCount()).toBe(1);
        // the fake now answers the held call (its caller is gone): still no second sendNow, row unchanged
        await fakeMode({ sendHold: false });
        await sleep(25_000);
        log('desktop', `${tag}-after-fake-answers`, { row: dbRow(id), sendNow: await sendNowCount() });
        expect(await sendNowCount()).toBe(1);
        expect(dbRow(id)).toBe(end);
        // the page: SendUnknown warning, and another send is refused until resolved
        await page.reload();
        await expect(page.getByTestId('send-unknown-warning')).toBeVisible();
        await shot(page, 'desktop', `${tag.replace(' ', '-')}-warning`);
        const other = await apiReady(page, `QA ${tag} other`);
        const refused = await apiSend(page, other);
        log('desktop', `${tag}-send-while-unresolved`, refused);
        expect(refused.status).toBe(409);
        const res = await page.request.post(`${API}/${id}/resolve`, { data: { outcome: 'notSent' } });
        expect(res.status()).toBe(200);
        const ok = await apiSend(page, other);
        log('desktop', `${tag}-send-after-resolve`, ok);
        expect(ok.status).toBe(202);
        await waitDb(other, /^sent/, 120_000);
        expect(await sendNowCount()).toBe(2);
      });
    }

    test('R3 restart during list import, before dispatch -> Failed interrupted, no sendNow, no campaign', async ({ page }) => {
      await open(page);
      const id = await apiReady(page, 'QA R3');
      await fakeReset();
      const campsBefore = Object.keys((await fake('/__qa/state')).camps); // earlier tests left campaigns at the fake
      await fakeMode({ importDelayMs: 90_000 });
      expect((await apiSend(page, id)).status).toBe(202);
      await until('import reaches the fake', async () => (await fakeCalls()).some((c) => c.path.endsWith('contacts/import') && JSON.stringify(c.body ?? {}).includes('"listIds":[10]')));
      log('desktop', 'R3-before-restart', { row: dbRow(id), calls: (await fakeCalls()).map((c) => `${c.method} ${c.path}`) });
      restartGraceful();
      await fakeMode({ importDelayMs: 0 });
      await healthy();
      const end = await settle(id, /^failed/, 'R3');
      expect(end).toBe('Failed|interrupted');
      const calls = await fakeCalls();
      log('desktop', 'R3-fake', { paths: calls.map((c) => `${c.method} ${c.path}`), newCamps: Object.keys((await fake('/__qa/state')).camps).filter((k) => !campsBefore.includes(k)) });
      expect(calls.filter((c) => /sendNow|emailCampaigns$/.test(c.path))).toEqual([]);
      expect(Object.keys((await fake('/__qa/state')).camps)).toEqual(campsBefore); // no campaign was created by the interrupted send
      await page.reload();
      await expect(rowOf(page, 'QA R3').getByTestId('failure-reason')).toHaveText('The send was interrupted. Check Brevo before sending again.');
      await laterSendWorks(page, 'R3', 1);
    });

    test('R4 restart right after a row entered Syncing -> not stuck, later send works, page updates without reload', async ({ page }) => {
      await open(page);
      const id = await apiReady(page, 'QA R4');
      await fakeMode({ importDelayMs: 90_000 });
      expect((await apiSend(page, id)).status).toBe(202);
      await page.reload();
      await until('row Syncing', async () => /^syncing/.test(dbRow(id).toLowerCase()), 30_000);
      const badge = rowOf(page, 'QA R4').locator('.mantine-Badge-root');
      log('desktop', 'R4-before-restart', { row: dbRow(id), ui: await badge.textContent() });
      restartGraceful();
      await fakeMode({ importDelayMs: 0 });
      await healthy();
      // watch the page (no reload) while the lease runs out
      const t0 = Date.now(); const seen: string[] = [];
      while (Date.now() - t0 < 240_000) {
        const t = ((await badge.textContent().catch(() => '')) ?? '').trim();
        if (t && seen[seen.length - 1] !== t) seen.push(t);
        if (/failed/i.test(t)) break;
        await sleep(1000);
      }
      log('desktop', 'R4-page-without-reload', { seen, seconds: Math.round((Date.now() - t0) / 1000), row: dbRow(id) });
      await shot(page, 'desktop', 'R4-page');
      const end = await settle(id, /^failed/, 'R4');
      expect(end).toBe('Failed|interrupted');
      expect(seen[seen.length - 1], 'the open page must show the settled row without a reload').toMatch(/failed/i);
      expect(await sendNowCount()).toBe(0);
      await laterSendWorks(page, 'R4', 1);
    });
  });
}
