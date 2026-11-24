import { test, expect } from '@playwright/test';
import { psql } from '../../fixtures/stack';
import { PHASE, API, BODY, MODE_DEFAULT, fakeMode, fakeReset, watch, shot, log, open, seedConsents, sendVia, waitDb, dbRow, rowOf } from './helpers';

/** Phase C extras: saved-draft preview, row actions in the viewport, refresh on a Brevo `draft`, the 150-event webhook. */
if (PHASE === 'C') {
  test.describe('phase C extras', () => {
    test.beforeEach(async ({ page }) => {
      await watch(page);
      psql('delete from marketing_campaigns');
      await fakeReset();
      await fakeMode(MODE_DEFAULT);
      seedConsents();
    });

    test('B6 saved draft preview carries the footer in an empty-sandbox iframe', async ({ page }, info) => {
      await open(page);
      const subj = `QA x6 ${info.project.name}`;
      const c = await page.request.post(API, { data: { subject: subj, html: BODY } });
      expect(c.status()).toBe(200);
      await page.reload();
      await rowOf(page, subj).getByRole('button', { name: 'Edit' }).click();
      const frame = page.getByTestId('campaign-preview');
      await expect(frame).toHaveAttribute('srcdoc', /.+/);
      expect(await frame.getAttribute('sandbox')).toBe('');
      const doc = await frame.getAttribute('srcdoc');
      expect(doc).toContain('QA Example Ltd');
      expect(doc).toContain('1 Example Street, Testville');
      expect(doc).toContain('QA0000000');
      await shot(page, info.project.name, 'x6-preview-footer');
    });

    test('row actions are in the viewport at 1440x900 with no sideways scroll', async ({ page }, info) => {
      test.skip(info.project.name !== 'desktop', 'desktop viewport only');
      await page.setViewportSize({ width: 1440, height: 900 });
      await open(page);
      const subj = `QA x7 ${info.project.name}`;
      expect((await page.request.post(API, { data: { subject: subj, html: BODY } })).status()).toBe(200);
      await page.reload();
      const btns = rowOf(page, subj).last().getByRole('button');
      await btns.first().waitFor();
      expect(await btns.count()).toBeGreaterThan(0);
      for (let i = 0; i < (await btns.count()); i++) {
        const b = await btns.nth(i).boundingBox();
        expect(b, `action ${i} has a box`).not.toBeNull();
        expect(b!.x).toBeGreaterThanOrEqual(0);
        expect(b!.x + b!.width).toBeLessThanOrEqual(1440);
      }
      const o = await page.evaluate(() => ({ doc: document.documentElement.scrollWidth, win: window.innerWidth }));
      expect(o.doc).toBeLessThanOrEqual(o.win);
    });

    test('refresh on a Brevo draft leaves SendUnknown; only resolve moves it to Failed not_sent', async ({ page }, info) => {
      test.setTimeout(240_000);
      await open(page);
      const subj = `QA x8 ${info.project.name}`;
      await fakeMode({ ...MODE_DEFAULT, campStatus: 'in_process' });
      const { id, end } = await sendVia(page, subj, /^(sending|sent)/);
      test.skip(!/^sending/.test(end.toLowerCase()), `row ended ${end}, not Sending`);
      await fakeMode({ campStatus: 'draft' });
      const r = await page.request.post(`${API}/${id}/refresh-results`);
      log(info.project.name, 'x8-refresh', { status: r.status() });
      await waitDb(id, /^sendunknown/, 20_000);
      expect(dbRow(id).toLowerCase()).toMatch(/^sendunknown/);
      const res = await page.request.post(`${API}/${id}/resolve`, { data: { outcome: 'notSent' } });
      expect(res.status(), await res.text()).toBe(200);
      expect(dbRow(id).toLowerCase()).toMatch(/^failed\|not_sent/);
    });

    test('webhook: 150 events in one body are all applied, including the tail', async ({ page }) => {
      await open(page);
      const mails = [1, 2, 3, 4].map((n) => `qa-person${n}@example.invalid`);
      const events = [
        ...Array.from({ length: 146 }, (_, i) => ({ event: 'unsubscribe', email: `qa-nobody${i}@example.invalid` })),
        ...mails.map((email) => ({ event: 'unsubscribe', email })),
      ];
      expect(events.length).toBe(150);
      const r = await page.request.post('/api/v1/marketing/webhooks/brevo', {
        data: events,
        headers: { 'X-ReelBolt-Webhook-Token': 'qa-fake-webhook-token' },
      });
      expect(r.status()).toBe(200);
      const left = psql(`select count(*) from marketing_consents where email like 'qa-person%@example.invalid' and status='OptedIn'`).trim();
      expect(left).toBe('0');
    });
  });
}
