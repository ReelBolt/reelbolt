import { expect, test } from '@playwright/test';
import * as fs from 'fs';
import { loginAs } from '../../fixtures/stack';

const SHOTS = process.env.RUN_PAGE_SHOTS;
if (SHOTS) fs.mkdirSync(SHOTS, { recursive: true });

const LABELS = ['1K (~1024 px) 8,8 cr', '2K (~2048 px) 12,6 cr', '4K (~4096 px) 17 cr'];
const VIEWPORTS = [{ name: 'phone', width: 390, height: 844 }, { name: 'tablet', width: 820, height: 1180 }, { name: 'desktop', width: 1440, height: 900 }];

for (const vp of VIEWPORTS) {
  for (const locale of ['en', 'it'] as const) {
    test(`run page choice input with long labels fits ${vp.width}px, 44px targets (${vp.name}, ${locale})`, async ({ page }, info) => {
      const { baseURL } = info.project.use;
      await page.setViewportSize({ width: vp.width, height: vp.height });
      await loginAs(page, 'creator');
      const api = page.context().request;
      const created = await api.post(`${baseURL}/api/v1/projects`, { data: { name: `runphone-${vp.name}-${locale}-${Date.now()}`, description: 'scratch' } });
      expect(created.ok()).toBeTruthy();
      const projectId = ((await created.json()) as { id: string }).id;
      try {
        const agents = (await (await api.get(`${baseURL}/api/v1/agents`)).json()) as Array<{ id: string }>;
        expect(agents.length).toBeGreaterThan(0);
        const wf = await api.post(`${baseURL}/api/v1/projects/${projectId}/workflows`, {
          data: { name: 'Resolution app', steps: [{ agentDefinitionId: agents[0]!.id, stepOrder: 1, label: 'Make it', stepType: 'Agent' }] },
        });
        expect(wf.ok()).toBeTruthy();
        const wid = ((await wf.json()) as { id: string }).id;

        // The workflow is real; its app page is served with a choice input whose labels are the long ones
        // (a stored manifest must bind to step settings, which is not what this test is about).
        await page.route(new RegExp(`/api/v1/workflows/${wid}/app$|/workflows/${wid}/app$`), async (route) => {
          const res = await route.fetch();
          const body = (await res.json()) as { manifest: { inputs: unknown[] } };
          body.manifest.inputs = [{
            kind: 'input', id: 'resolution', type: 'choice', label: 'Resolution', default: '1K',
            options: LABELS.map((label, i) => ({ value: ['1K', '2K', '4K'][i], label })),
          }];
          await route.fulfill({ response: res, json: body });
        });

        await page.context().addCookies([{ name: 'rb_locale', value: locale, url: baseURL! }]);
        await page.goto(`/app/projects/${projectId}/workflows/${wid}`);
        const group = page.getByRole('radiogroup', { name: 'Resolution' });
        await expect(group).toBeVisible({ timeout: 30_000 });
        await page.waitForLoadState('networkidle');
        if (SHOTS) await page.screenshot({ path: `${SHOTS}/${vp.name}-${locale}.png` }); // before asserting, so a failing run still leaves its picture

        const m = await group.evaluate((el) => ({ sw: el.scrollWidth, cw: el.clientWidth }));
        expect(m.sw, 'segmented control scrolls sideways').toBeLessThanOrEqual(m.cw);

        const options = group.getByRole('radio');
        expect(await options.count()).toBe(3);
        for (let i = 0; i < 3; i++) {
          await options.nth(i).scrollIntoViewIfNeeded();
          const label = group.locator('label').nth(i);
          const box = (await label.boundingBox())!;
          expect(box.x, `option ${i} left edge`).toBeGreaterThanOrEqual(-0.5);
          expect(box.x + box.width, `option ${i} right edge inside the ${vp.width}px viewport`).toBeLessThanOrEqual(vp.width + 0.5);
          expect(box.height, `option ${i} touch target`).toBeGreaterThanOrEqual(44);
        }
      } finally {
        await api.delete(`${baseURL}/api/v1/projects/${projectId}`);
      }
    });
  }
}
